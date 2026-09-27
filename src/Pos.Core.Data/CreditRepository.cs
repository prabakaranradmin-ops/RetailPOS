using System.Globalization;
using Microsoft.Data.Sqlite;
using Pos.Core.Domain;

namespace Pos.Core.Data;

/// <summary>
/// Customer credit read from the books: what is owed, what has been paid back, and taking a payment.
/// </summary>
/// <remarks>
/// <para>
/// What a customer owes is never stored. It is the store-credit payments on their bills that were
/// not voided, less their repayments, summed in exact paise every time it is asked. Voiding a credit
/// sale therefore takes it off what they owe with nothing having to remember to, and there is no
/// balance column to drift from the bills it summarises.
/// </para>
/// </remarks>
public sealed class CreditRepository(PosDatabase database) : ICreditStore
{
    private readonly PosDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

    /// <summary>
    /// The one definition of what a customer owes, as a SQL expression in whole paise.
    /// </summary>
    /// <remarks>
    /// Public so the owner's customer list can show a balance beside every name using this exact
    /// arithmetic rather than a second copy of it that could drift. Store credit is tender type
    /// <c>3</c>; inlined rather than bound so the expression can sit inside any query.
    /// </remarks>
    /// <param name="customerId">The SQL that names the customer, such as <c>c.id</c>.</param>
    public static string OwedPaiseSql(string customerId) => $"""
        (COALESCE((SELECT {PaiseSql.Sum("cp_p.amount")}
                   FROM payments cp_p JOIN invoices cp_i ON cp_i.id = cp_p.invoice_id
                   WHERE cp_i.customer_id = {customerId} AND cp_i.voided_at IS NULL
                     AND cp_p.tender_type = {(int)TenderType.StoreCredit}), 0)
         - COALESCE((SELECT {PaiseSql.Sum("cp_r.amount")} FROM credit_payments cp_r
                     WHERE cp_r.customer_id = {customerId}), 0))
        """;

    /// <summary>What a customer owes, in paise. Shared with forgetting, which must not wipe a debt.</summary>
    internal static long OwedPaise(SqliteConnection connection, SqliteTransaction? transaction, long customerId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {OwedPaiseSql("$id")};";
        command.Parameters.AddWithValue("$id", customerId);

        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public decimal Balance(long customerId)
    {
        using var connection = _database.OpenConnection();
        return PaiseSql.Rupees(OwedPaise(connection, null, customerId));
    }

    public IReadOnlyList<CustomerBalance> Owing(int limit = 200)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, mobile_no, name, owed, last_at FROM (
                SELECT c.id, c.mobile_no, c.name,
                       COALESCE(bought.paise, 0) - COALESCE(paid.paise, 0) AS owed,
                       bought.last_at
                FROM customers c
                LEFT JOIN (
                    SELECT i.customer_id, {PaiseSql.Sum("p.amount")} AS paise, MAX(i.created_at) AS last_at
                    FROM payments p JOIN invoices i ON i.id = p.invoice_id
                    WHERE p.tender_type = $credit AND i.voided_at IS NULL AND i.customer_id IS NOT NULL
                    GROUP BY i.customer_id
                ) bought ON bought.customer_id = c.id
                LEFT JOIN (
                    SELECT customer_id, {PaiseSql.Sum("amount")} AS paise
                    FROM credit_payments WHERE customer_id IS NOT NULL
                    GROUP BY customer_id
                ) paid ON paid.customer_id = c.id
            )
            WHERE owed > 0
            ORDER BY owed DESC, name COLLATE NOCASE
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$credit", (int)TenderType.StoreCredit);
        command.Parameters.AddWithValue("$limit", Math.Max(1, limit));

        var owing = new List<CustomerBalance>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            owing.Add(new CustomerBalance(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                PaiseSql.Rupees(reader.GetInt64(3)),
                reader.IsDBNull(4) ? null : reader.GetDateTimeOffset(4)));
        }

        return owing;
    }

    public CreditPayment Collect(
        long customerId,
        decimal amount,
        TenderType tender,
        string laneId,
        DateTimeOffset receivedAt,
        string? cashierName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(laneId);

        // Money only. A debt paid off with more credit is the same debt, and one paid in points is
        // the shop paying itself.
        if (tender is not (TenderType.Cash or TenderType.Card or TenderType.Upi))
            throw new ArgumentException($"A repayment is taken in cash, card or UPI, not {tender}.", nameof(tender));

        if (amount <= 0m)
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "A repayment has to be more than nothing.");

        if (decimal.Round(amount, 2) != amount)
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "An amount cannot be finer than a paisa.");

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);

        using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT COUNT(*) FROM customers WHERE id = $id;";
            exists.Parameters.AddWithValue("$id", customerId);

            if (Convert.ToInt64(exists.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
                throw new InvalidOperationException($"No customer with id {customerId}.");
        }

        // Checked inside the same transaction as the insert, so two lanes taking the same
        // customer's money at once cannot both pass the check and together take too much.
        var owed = PaiseSql.Rupees(OwedPaise(connection, transaction, customerId));

        if (owed <= 0m)
            throw new InvalidOperationException("They owe nothing, so there is nothing to take.");

        // Taking more than is owed would leave the shop owing the customer, which is a different
        // thing - an advance - and not something to create by mistyping an amount at a counter.
        if (amount > owed)
            throw new InvalidOperationException($"They owe {owed:0.00}. Taking {amount:0.00} would be more than that.");

        long id;

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO credit_payments (customer_id, lane_id, received_at, tender_type, amount, cashier_name)
                VALUES ($customer, $lane, $at, $tender, $amount, $cashier);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("$customer", customerId);
            insert.Parameters.AddWithValue("$lane", laneId);
            insert.Parameters.AddWithValue("$at", receivedAt);
            insert.Parameters.AddWithValue("$tender", (int)tender);
            insert.Parameters.AddWithValue("$amount", amount);
            insert.Parameters.AddWithValue("$cashier", (object?)cashierName ?? DBNull.Value);

            id = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        transaction.Commit();

        return new CreditPayment(id, customerId, laneId, receivedAt, tender, amount, cashierName);
    }

    public IReadOnlyList<CreditMovement> History(long customerId, int limit = 50)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();

        // Both sides in one ordered list, oldest first, so the running balance can be walked
        // forward; the newest end is what is returned.
        command.CommandText = $"""
            SELECT at, what, paise FROM (
                SELECT i.created_at AS at, i.invoice_no AS what, {PaiseSql.Sum("p.amount")} AS paise
                FROM payments p JOIN invoices i ON i.id = p.invoice_id
                WHERE i.customer_id = $id AND i.voided_at IS NULL AND p.tender_type = $credit
                GROUP BY i.id
                UNION ALL
                SELECT received_at, 'paid:' || tender_type, -{PaiseSql.Of("amount")}
                FROM credit_payments WHERE customer_id = $id
            )
            ORDER BY at, paise DESC;
            """;
        command.Parameters.AddWithValue("$id", customerId);
        command.Parameters.AddWithValue("$credit", (int)TenderType.StoreCredit);

        var movements = new List<CreditMovement>();
        long running = 0;

        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var paise = reader.GetInt64(2);
                running += paise;

                var what = reader.GetString(1);
                var description = what.StartsWith("paid:", StringComparison.Ordinal)
                    ? $"Paid back, {Tender((TenderType)int.Parse(what[5..], CultureInfo.InvariantCulture))}"
                    : $"Bought on credit, bill {what}";

                movements.Add(new CreditMovement(
                    reader.GetDateTimeOffset(0),
                    description,
                    PaiseSql.Rupees(paise),
                    PaiseSql.Rupees(running)));
            }
        }

        movements.Reverse();
        return movements.Take(Math.Max(1, limit)).ToList();
    }

    private static string Tender(TenderType tender) => tender switch
    {
        TenderType.Cash => "cash",
        TenderType.Card => "card",
        TenderType.Upi => "UPI",
        _ => tender.ToString(),
    };
}
