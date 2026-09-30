using System.Globalization;
using Microsoft.Data.Sqlite;
using Pos.Core.Domain;

namespace Pos.Core.Data;

/// <summary>
/// <see cref="Reorder"/>'s rate of sale as SQL: what each item sold over the window, less what came
/// back, for the queries that list stock with how long it will last.
/// </summary>
/// <remarks>
/// Every lane's sales in this database, not one lane's: the shelf count is the database's, and
/// every till selling from it empties the same shelf. Needs <c>$since</c> bound as a
/// <see cref="DateTimeOffset"/>, never as a formatted string - the driver writes a space between date
/// and time, and a "T" sorts after it.
/// </remarks>
public static class SalesRateSql
{
    /// <summary>
    /// A table of <c>item_id, qty</c>: sold since <c>$since</c> on bills that stand, less returned since then.
    /// </summary>
    public const string SoldSince = """
        SELECT item_id, SUM(qty) AS qty FROM (
            SELECT l.item_id, CAST(l.quantity AS REAL) AS qty
            FROM invoice_lines l JOIN invoices i ON i.id = l.invoice_id
            WHERE i.voided_at IS NULL AND i.created_at >= $since
            UNION ALL
            SELECT l.item_id, -CAST(l.quantity AS REAL)
            FROM credit_note_lines l JOIN credit_notes n ON n.id = l.credit_note_id
            WHERE n.created_at >= $since
        )
        GROUP BY item_id
        """;

    /// <summary>When the window starts, and how many days it covers, for a query run now.</summary>
    public static (DateTimeOffset Since, int Days) Window(SqliteConnection connection, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.Date);
        var start = today.AddDays(-(Reorder.WindowDays - 1));
        var since = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), now.Offset);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MIN(created_at) FROM invoices WHERE voided_at IS NULL;";
        var first = command.ExecuteScalar();

        DateOnly? firstSale = first is string text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? DateOnly.FromDateTime(at.Date)
            : null;

        return (since, Reorder.DaysMeasured(firstSale, today));
    }
}
