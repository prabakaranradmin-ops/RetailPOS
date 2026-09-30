using System.Globalization;
using Pos.Core.Domain;

namespace Pos.Core.Data;

/// <summary>
/// Counted items with something on the shelf that have not sold for a while.
/// </summary>
/// <remarks>
/// When an item came into the shop is the earliest of its first purchase bill and its first price
/// (recorded when it was added, migration 016). An item with neither on record has been there
/// longer than the books go back.
/// </remarks>
public sealed class DeadStockRepository(PosDatabase database) : IDeadStockStore
{
    private readonly PosDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

    public IReadOnlyList<DeadStockItem> NotSelling(DateOnly today, int days = DeadStock.Days)
    {
        if (!DeadStock.IsValidDays(days))
            throw new ArgumentOutOfRangeException(nameof(days), days, "Dead stock is not sold for 14 to 365 days.");

        var since = today.AddDays(-days);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH sold AS (
                SELECT l.item_id, MAX(substr(i.created_at, 1, 10)) AS last_sold
                FROM invoice_lines l JOIN invoices i ON i.id = l.invoice_id
                WHERE i.voided_at IS NULL
                GROUP BY l.item_id),
            arrived AS (
                SELECT item_id, MIN(day) AS first_seen FROM (
                    SELECT pl.item_id, p.bill_date AS day
                    FROM purchase_lines pl JOIN purchases p ON p.id = pl.purchase_id
                    WHERE p.voided_at IS NULL
                    UNION ALL
                    SELECT item_id, substr(changed_at, 1, 10) FROM price_changes)
                GROUP BY item_id)
            SELECT it.id, it.sku, it.name, it.category, it.unit_type, it.stock_qty, it.cost_price,
                   s.last_sold, a.first_seen
            FROM items it
            LEFT JOIN sold s ON s.item_id = it.id
            LEFT JOIN arrived a ON a.item_id = it.id
            WHERE it.is_active = 1
              AND it.stock_qty IS NOT NULL AND CAST(it.stock_qty AS REAL) > 0
              AND (s.last_sold IS NOT NULL AND s.last_sold < $since
                   OR s.last_sold IS NULL AND (a.first_seen IS NULL OR a.first_seen < $since));
            """;
        command.Parameters.AddWithValue("$since", since.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        var items = new List<DeadStockItem>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            DateOnly? lastSold = reader.IsDBNull(7) ? null : DateOnly.ParseExact(reader.GetString(7), "yyyy-MM-dd", CultureInfo.InvariantCulture);

            items.Add(new DeadStockItem(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                (UnitType)reader.GetInt32(4),
                reader.GetDecimal(5),
                lastSold,
                lastSold is { } day ? today.DayNumber - day.DayNumber : null,
                reader.IsDBNull(6) ? null : reader.GetDecimal(6)));
        }

        // The most money first: that is the order to deal with them in. Nothing known about the
        // money goes last, longest unsold first.
        return
        [
            .. items
                .OrderByDescending(i => i.TiedUp ?? -1m)
                .ThenByDescending(i => i.DaysSinceSold ?? int.MaxValue)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
        ];
    }
}
