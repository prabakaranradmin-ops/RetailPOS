using Pos.Core.Domain;

namespace Pos.Core.Data;

/// <summary>Sales of items not in the catalogue, and which of them the owner has dealt with.</summary>
public sealed class OpenItemRepository(PosDatabase database) : IOpenItemStore
{
    private readonly PosDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

    public IReadOnlyList<OpenItemSale> Waiting(int limit = 500)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();

        // Every lane's, since they share the catalogue the owner adds them to. A voided bill's lines
        // are not waiting for anything: the sale did not happen.
        command.CommandText = """
            SELECT l.id, i.invoice_no, i.created_at, l.name_snapshot, l.barcode_snapshot, l.unit_price,
                   l.gst_rate, l.hsn_snapshot, l.quantity, i.cashier_name
            FROM invoice_lines l
            JOIN invoices i ON i.id = l.invoice_id
            WHERE l.item_id = 0
              AND i.voided_at IS NULL AND i.hold_token IS NULL
              AND NOT EXISTS (SELECT 1 FROM open_item_reviews r WHERE r.invoice_line_id = l.id)
            ORDER BY i.created_at DESC, l.id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100_000));

        var sales = new List<OpenItemSale>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            sales.Add(new OpenItemSale(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetDateTimeOffset(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetDecimal(5),
                reader.GetDecimal(6),
                reader.GetString(7),
                reader.GetDecimal(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return sales;
    }

    public void DealtWith(IEnumerable<long> lineIds, DateTimeOffset at, string? addedAs)
    {
        ArgumentNullException.ThrowIfNull(lineIds);

        var ids = lineIds.Distinct().ToList();

        if (ids.Count == 0)
            return;

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        // Only open lines: a catalogue line has nothing to deal with. And only once: the first word
        // on a sale - added as this, or set aside - is the one kept.
        command.CommandText = """
            INSERT OR IGNORE INTO open_item_reviews (invoice_line_id, dealt_at, added_as)
            SELECT id, $at, $sku FROM invoice_lines WHERE id = $id AND item_id = 0;
            """;
        command.Parameters.AddWithValue("$at", at);
        command.Parameters.AddWithValue("$sku", string.IsNullOrWhiteSpace(addedAs) ? DBNull.Value : (object)addedAs.Trim());
        var id = command.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);

        foreach (var lineId in ids)
        {
            id.Value = lineId;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }
}
