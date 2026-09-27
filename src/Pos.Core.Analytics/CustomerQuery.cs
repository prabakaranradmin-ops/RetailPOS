using System.Globalization;
using Microsoft.Data.Sqlite;
using Pos.Core.Data;
using Pos.Core.Domain;

namespace Pos.Core.Analytics;

/// <summary>One customer as a line in a list: who they are and what they have spent.</summary>
public sealed record CustomerSummary(
    long Id,
    string MobileNo,
    string? Name,
    int LoyaltyBalance,
    int Visits,
    decimal Spent,
    DateTimeOffset? LastVisit,
    decimal Owed = 0m)
{
    /// <summary>What the shop calls them: their name when it knows it, their number when it does not.</summary>
    public string Label => string.IsNullOrWhiteSpace(Name) ? MobileNo : Name;
}

/// <param name="Month">The first day of the month.</param>
public sealed record MonthlySpend(DateOnly Month, int Bills, decimal Spent);

/// <summary>Something a customer buys, and how much of it.</summary>
public sealed record CustomerItem(string Name, decimal Quantity, UnitType Unit, decimal Spent, int Bills);

public sealed record CustomerBill(string InvoiceNo, DateTimeOffset At, int Lines, decimal Amount);

/// <summary>Everything the owner's screen shows about one customer.</summary>
public sealed record CustomerProfile(
    CustomerSummary Customer,
    DateTimeOffset? FirstVisit,
    IReadOnlyList<MonthlySpend> Months,
    IReadOnlyList<CustomerItem> TopItems,
    IReadOnlyList<CustomerBill> RecentBills)
{
    public decimal AverageBasket => Customer.Visits == 0
        ? 0m
        : decimal.Round(Customer.Spent / Customer.Visits, 2, MidpointRounding.ToEven);
}

/// <summary>
/// Reads one customer's history back out of the bills: what they spend, when, and on what.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here is stored for the purpose. Every bill already records who it was for and every line
/// on it, so a customer's history is a question asked of the books rather than a second copy of
/// them that could drift. Voided sales are left out, as they are everywhere else: a bill that was
/// cancelled is not something the customer bought.
/// </para>
/// <para>
/// Money is summed in exact paise with <see cref="PaiseSql"/>, the same arithmetic the dashboard
/// uses, so a customer's total and the shop's total cannot disagree about the same bills.
/// </para>
/// </remarks>
public sealed class CustomerQuery(PosDatabase database)
{
    private readonly PosDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

    /// <summary>What a bill came to: its total plus the round-off, which is what was paid.</summary>
    private static readonly string Paid = $"{PaiseSql.Of("i.grand_total")} + {PaiseSql.Of("i.round_off")}";

    /// <summary>
    /// Customers matching the text by name or number, or the best customers when it is blank.
    /// </summary>
    /// <remarks>
    /// Blank shows who spends most, because that is the list an owner opening this screen wants
    /// first. With text, a number or name that starts with it ranks above one that only contains it.
    /// </remarks>
    /// <param name="onlyOwing">
    /// Just the customers who owe something on credit, most owed first: the list an owner reads at
    /// the end of the month.
    /// </param>
    public IReadOnlyList<CustomerSummary> Find(string? text, int limit = 50, bool onlyOwing = false)
    {
        var term = text?.Trim() ?? string.Empty;
        var escaped = term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT c.id, c.mobile_no, c.name, c.loyalty_balance,
                   COUNT(i.id),
                   COALESCE(SUM({Paid}), 0),
                   MAX(i.created_at),
                   {CreditRepository.OwedPaiseSql("c.id")} AS owed
            FROM customers c
            LEFT JOIN invoices i ON i.customer_id = c.id AND i.voided_at IS NULL
            WHERE $term = ''
               OR c.mobile_no LIKE $contains ESCAPE '\'
               OR c.name LIKE $contains ESCAPE '\'
            GROUP BY c.id
            HAVING $owing = 0 OR owed > 0
            ORDER BY
                CASE WHEN $owing = 1 THEN -owed ELSE 0 END,
                CASE WHEN $term = '' THEN 0
                     WHEN c.mobile_no LIKE $starts ESCAPE '\' THEN 0
                     WHEN c.name LIKE $starts ESCAPE '\' THEN 1
                     ELSE 2 END,
                6 DESC, c.name COLLATE NOCASE, c.mobile_no
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$owing", onlyOwing ? 1 : 0);
        command.Parameters.AddWithValue("$term", term);
        command.Parameters.AddWithValue("$contains", $"%{escaped}%");
        command.Parameters.AddWithValue("$starts", $"{escaped}%");
        command.Parameters.AddWithValue("$limit", Math.Max(1, limit));

        var found = new List<CustomerSummary>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
            found.Add(ReadSummary(reader));

        return found;
    }

    /// <summary>One customer's history, or null if there is no such customer.</summary>
    /// <param name="months">How many months the spending chart covers, ending with this one.</param>
    public CustomerProfile? Profile(long customerId, int months = 12, int topItems = 10, int recentBills = 10)
    {
        using var connection = _database.OpenConnection();

        var summary = ReadOneSummary(connection, customerId);
        if (summary is null)
            return null;

        return new CustomerProfile(
            summary,
            FirstVisit(connection, customerId),
            Months(connection, customerId, Math.Clamp(months, 1, 60)),
            TopItems(connection, customerId, Math.Clamp(topItems, 1, 100)),
            RecentBills(connection, customerId, Math.Clamp(recentBills, 1, 100)));
    }

    private static CustomerSummary? ReadOneSummary(SqliteConnection connection, long customerId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT c.id, c.mobile_no, c.name, c.loyalty_balance,
                   COUNT(i.id),
                   COALESCE(SUM({Paid}), 0),
                   MAX(i.created_at),
                   {CreditRepository.OwedPaiseSql("c.id")}
            FROM customers c
            LEFT JOIN invoices i ON i.customer_id = c.id AND i.voided_at IS NULL
            WHERE c.id = $id
            GROUP BY c.id;
            """;
        command.Parameters.AddWithValue("$id", customerId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSummary(reader) : null;
    }

    private static CustomerSummary ReadSummary(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.GetInt32(3),
        reader.GetInt32(4),
        PaiseSql.Rupees(reader.GetInt64(5)),
        reader.IsDBNull(6) ? null : reader.GetDateTimeOffset(6),
        PaiseSql.Rupees(reader.GetInt64(7)));

    private static DateTimeOffset? FirstVisit(SqliteConnection connection, long customerId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MIN(created_at) FROM invoices WHERE customer_id = $id AND voided_at IS NULL;";
        command.Parameters.AddWithValue("$id", customerId);

        using var reader = command.ExecuteReader();
        return reader.Read() && !reader.IsDBNull(0) ? reader.GetDateTimeOffset(0) : null;
    }

    /// <summary>
    /// Spending by month, dense: a month they did not come in is a zero, not a gap.
    /// </summary>
    /// <remarks>
    /// A regular who stopped coming in three months ago is the thing this chart is for, and it
    /// only shows if the empty months are drawn. Skipping them would put January beside May and
    /// read as a steady customer.
    /// </remarks>
    private static IReadOnlyList<MonthlySpend> Months(SqliteConnection connection, long customerId, int months)
    {
        var thisMonth = DateOnly.FromDateTime(DateTime.Today);
        thisMonth = new DateOnly(thisMonth.Year, thisMonth.Month, 1);
        var first = thisMonth.AddMonths(-(months - 1));

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT substr(i.created_at, 1, 7) AS month, COUNT(*), COALESCE(SUM({Paid}), 0)
            FROM invoices i
            WHERE i.customer_id = $id AND i.voided_at IS NULL AND i.created_at >= $from
            GROUP BY month;
            """;
        command.Parameters.AddWithValue("$id", customerId);
        command.Parameters.AddWithValue("$from", first.ToString("yyyy-MM-01", CultureInfo.InvariantCulture));

        var byMonth = new Dictionary<string, (int Bills, long Paise)>(StringComparer.Ordinal);

        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                byMonth[reader.GetString(0)] = (reader.GetInt32(1), reader.GetInt64(2));
        }

        var series = new List<MonthlySpend>(months);

        for (var month = first; month <= thisMonth; month = month.AddMonths(1))
        {
            var key = month.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            var (bills, paise) = byMonth.TryGetValue(key, out var found) ? found : (0, 0L);
            series.Add(new MonthlySpend(month, bills, PaiseSql.Rupees(paise)));
        }

        return series;
    }

    /// <summary>What they buy, ranked by what it came to.</summary>
    private static IReadOnlyList<CustomerItem> TopItems(SqliteConnection connection, long customerId, int limit)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT l.name_snapshot,
                   COALESCE(SUM(CAST(l.quantity AS REAL)), 0),
                   MAX(l.unit_type),
                   COALESCE({PaiseSql.Sum("l.line_total")}, 0) AS spent,
                   COUNT(DISTINCT l.invoice_id)
            FROM invoice_lines l
            JOIN invoices i ON i.id = l.invoice_id
            WHERE i.customer_id = $id AND i.voided_at IS NULL
            GROUP BY l.name_snapshot
            ORDER BY spent DESC, l.name_snapshot
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$id", customerId);
        command.Parameters.AddWithValue("$limit", limit);

        var items = new List<CustomerItem>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            items.Add(new CustomerItem(
                reader.GetString(0),
                Math.Round((decimal)reader.GetDouble(1), 3),
                (UnitType)reader.GetInt32(2),
                PaiseSql.Rupees(reader.GetInt64(3)),
                reader.GetInt32(4)));
        }

        return items;
    }

    private static IReadOnlyList<CustomerBill> RecentBills(SqliteConnection connection, long customerId, int limit)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT i.invoice_no, i.created_at,
                   (SELECT COUNT(*) FROM invoice_lines l WHERE l.invoice_id = i.id),
                   {Paid}
            FROM invoices i
            WHERE i.customer_id = $id AND i.voided_at IS NULL
            ORDER BY i.created_at DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$id", customerId);
        command.Parameters.AddWithValue("$limit", limit);

        var bills = new List<CustomerBill>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            bills.Add(new CustomerBill(
                reader.GetString(0),
                reader.GetDateTimeOffset(1),
                reader.GetInt32(2),
                PaiseSql.Rupees(reader.GetInt64(3))));
        }

        return bills;
    }
}
