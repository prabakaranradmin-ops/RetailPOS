using System.Globalization;
using Microsoft.Data.Sqlite;
using Pos.Core.Domain;

namespace Pos.Core.Data;

/// <summary>
/// Deliveries close to their use-by dates, read from the purchase bills and the shelf count.
/// </summary>
/// <remarks>
/// Only items with at least one dated delivery inside the warning window are read, and for those
/// every delivery that stands, dated or not: an undated delivery still takes its share of the count
/// in <see cref="Expiry.For"/>, or an older dated one would look as if it were still on the shelf.
/// </remarks>
public sealed class ExpiryRepository(PosDatabase database) : IExpiryStore
{
    private readonly PosDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

    public IReadOnlyList<ExpiryWarning> Expiring(DateOnly today) => Read(today, itemId: null);

    public IReadOnlyList<ExpiryWarning> ExpiringFor(long itemId, DateOnly today) => Read(today, itemId);

    private List<ExpiryWarning> Read(DateOnly today, long? itemId)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT i.id, i.sku, i.name, i.unit_type, i.stock_qty,
                   pl.batch_no, pl.expiry_date, p.bill_date, s.name, pl.quantity,
                   (SELECT group_concat(rl.quantity, '|')
                    FROM supplier_return_lines rl JOIN supplier_returns r ON r.id = rl.return_id
                    WHERE r.purchase_id = p.id AND rl.purchase_line_no = pl.line_no)
            FROM purchase_lines pl
            JOIN purchases p ON p.id = pl.purchase_id
            JOIN suppliers s ON s.id = p.supplier_id
            JOIN items i ON i.id = pl.item_id
            WHERE p.voided_at IS NULL AND i.is_active = 1
              {(itemId is null ? "" : "AND i.id = $item")}
              AND pl.item_id IN (
                  SELECT x.item_id FROM purchase_lines x JOIN purchases y ON y.id = x.purchase_id
                  WHERE y.voided_at IS NULL AND x.expiry_date IS NOT NULL AND x.expiry_date <= $until
                  {(itemId is null ? "" : "AND x.item_id = $item")})
            ORDER BY i.id, p.bill_date DESC, p.id DESC, pl.id DESC;
            """;
        command.Parameters.AddWithValue("$until", today.AddDays(Expiry.WarnDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        if (itemId is not null)
            command.Parameters.AddWithValue("$item", itemId.Value);

        var warnings = new List<ExpiryWarning>();
        using var reader = command.ExecuteReader();

        long? current = null;
        (string Sku, string Name, UnitType Unit, decimal? Have)? item = null;
        var deliveries = new List<Expiry.Delivery>();

        void Flush()
        {
            if (current is { } id && item is { } head)
                warnings.AddRange(Expiry.For(id, head.Sku, head.Name, head.Unit, head.Have, deliveries, today));

            deliveries.Clear();
        }

        while (reader.Read())
        {
            var id = reader.GetInt64(0);

            if (id != current)
            {
                Flush();
                current = id;
                item = (reader.GetString(1), reader.GetString(2), (UnitType)reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetDecimal(4));
            }

            // What went back to the supplier from this delivery is not on the shelf to expire. The
            // quantities are added here, exactly, rather than summed by SQLite as floating point.
            var sentBack = reader.IsDBNull(10)
                ? 0m
                : reader.GetString(10).Split('|').Sum(q => decimal.Parse(q, NumberStyles.Number, CultureInfo.InvariantCulture));

            var delivered = reader.GetDecimal(9) - sentBack;

            if (delivered <= 0m)
                continue;

            deliveries.Add(new Expiry.Delivery(
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : DateOnly.ParseExact(reader.GetString(6), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                DateOnly.ParseExact(reader.GetString(7), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                reader.GetString(8),
                delivered));
        }

        Flush();

        return [.. warnings.OrderBy(w => w.Expires).ThenBy(w => w.Name, StringComparer.OrdinalIgnoreCase)];
    }
}
