using System.Globalization;
using Pos.Core.Domain;
using Pos.Core.Domain.Import;

namespace Pos.Core.Data;

/// <summary>
/// Prices in bulk, and the shelf labels they make out of date.
/// </summary>
/// <remarks>
/// A price change is recorded by the database's own triggers (migration 016), whatever made it:
/// the price sheet here, a catalogue re-import, an item added by hand. So the list of labels due
/// cannot miss a change made some other way.
/// </remarks>
public sealed class PriceRepository(PosDatabase database) : IPriceStore
{
    private readonly PosDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

    public IReadOnlyList<PriceSheetItem> PriceSheet()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, sku, name, unit_type, mrp, sell_price, cost_price, gst_rate
            FROM items WHERE is_active = 1
            ORDER BY COALESCE(category, ''), name COLLATE NOCASE;
            """;

        var items = new List<PriceSheetItem>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            items.Add(new PriceSheetItem(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                (UnitType)reader.GetInt32(3),
                reader.GetDecimal(4),
                reader.GetDecimal(5),
                reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                reader.GetDecimal(7)));
        }

        return items;
    }

    public int Apply(IReadOnlyList<PriceChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        if (changes.Count == 0)
            return 0;

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        // Guarded by the price read when the sheet was checked: a price changed since, by another
        // lane or a re-import, is not quietly written over with one worked out against the old one.
        // Compared in whole paise: two floating-point readings of the same text are not promised to agree.
        command.CommandText = $"""
            UPDATE items SET mrp = $mrp, sell_price = $price
            WHERE id = $id AND {PaiseSql.Of("mrp")} = $oldMrp AND {PaiseSql.Of("sell_price")} = $oldPrice;
            """;

        foreach (var name in new[] { "$mrp", "$price", "$id", "$oldMrp", "$oldPrice" })
            command.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter(name, null));

        foreach (var change in changes)
        {
            command.Parameters["$mrp"].Value = change.NewMrp;
            command.Parameters["$price"].Value = change.NewPrice;
            command.Parameters["$id"].Value = change.ItemId;
            command.Parameters["$oldMrp"].Value = (long)(change.OldMrp * 100m);
            command.Parameters["$oldPrice"].Value = (long)(change.OldPrice * 100m);

            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException($"The price of {change.Name} has changed since the sheet was checked. Check it again.");
        }

        transaction.Commit();
        return changes.Count;
    }

    private const string LabelColumns = "i.id, i.sku, i.name, i.barcode, i.unit_type, i.mrp, i.sell_price";

    public IReadOnlyList<ShelfLabel> LabelsDue()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {LabelColumns}, MAX(p.changed_at)
            FROM price_changes p JOIN items i ON i.id = p.item_id
            WHERE p.labelled_at IS NULL AND i.is_active = 1
            GROUP BY i.id
            ORDER BY MIN(p.changed_at), i.name COLLATE NOCASE;
            """;

        return ReadLabels(command, withChangedAt: true);
    }

    public IReadOnlyList<ShelfLabel> AllLabels()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {LabelColumns}
            FROM items i WHERE i.is_active = 1
            ORDER BY COALESCE(i.category, ''), i.name COLLATE NOCASE;
            """;

        return ReadLabels(command, withChangedAt: false);
    }

    private static List<ShelfLabel> ReadLabels(Microsoft.Data.Sqlite.SqliteCommand command, bool withChangedAt)
    {
        var labels = new List<ShelfLabel>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            DateTimeOffset? changedAt = withChangedAt && !reader.IsDBNull(7)
                && DateTimeOffset.TryParse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var at)
                    ? at
                    : null;

            labels.Add(new ShelfLabel(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                (UnitType)reader.GetInt32(4),
                reader.GetDecimal(5),
                reader.GetDecimal(6),
                changedAt));
        }

        return labels;
    }

    public void MarkLabelled(IEnumerable<long> itemIds, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(itemIds);

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE price_changes SET labelled_at = $at WHERE item_id = $id AND labelled_at IS NULL;";
        command.Parameters.AddWithValue("$at", at);
        var id = command.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("$id", null));

        foreach (var itemId in itemIds.Distinct())
        {
            id.Value = itemId;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }
}
