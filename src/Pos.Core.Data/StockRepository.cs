using Microsoft.Data.Sqlite;
using Pos.Core.Domain;

namespace Pos.Core.Data;

/// <summary>
/// The shelf count and the ledger behind it.
/// </summary>
/// <remarks>
/// Every change goes through <see cref="Move"/> or <see cref="Set"/>, which write the new balance
/// and the reason for it in one transaction. Nothing updates <c>items.stock_qty</c> on its own —
/// a figure that changed with no movement to explain it is exactly the thing the ledger exists to
/// prevent.
/// </remarks>
public sealed class StockRepository : IStockStore
{
    private const string LevelColumns =
        "i.id, i.sku, i.name, i.category, i.stock_qty, i.reorder_level, i.unit_type";

    private readonly PosDatabase _database;

    public StockRepository(PosDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    public decimal? Move(long itemId, decimal delta, StockReason reason, string laneId, string? reference = null)
    {
        if (delta == 0m)
            return Current(itemId);

        return Write(itemId, laneId, reason, reference, current => current + delta);
    }

    public decimal? Set(long itemId, decimal quantity, StockReason reason, string laneId, string? reference = null) =>
        Write(itemId, laneId, reason, reference, _ => quantity);

    /// <summary>
    /// Reads the current figure, computes the new one, and writes both it and its movement.
    /// </summary>
    /// <remarks>
    /// One transaction with the read inside it, so two lanes selling the last packet at the same
    /// moment cannot both read four and both write three. SQLite serialises writers, so the second
    /// waits and then reads what the first wrote.
    /// </remarks>
    private decimal? Write(
        long itemId,
        string laneId,
        StockReason reason,
        string? reference,
        Func<decimal, decimal> next)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(laneId);

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        decimal current;

        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT stock_qty FROM items WHERE id = $id;";
            read.Parameters.AddWithValue("$id", itemId);

            var value = read.ExecuteScalar();

            // No row, or an item nobody counts. Either way there is nothing to move, and this is
            // not an error — most of a first catalogue will have no stock figure at all.
            if (value is null || value is DBNull)
                return null;

            current = Convert.ToDecimal(value);
        }

        var balance = next(current);

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE items SET stock_qty = $qty WHERE id = $id;";
            update.Parameters.AddWithValue("$qty", balance);
            update.Parameters.AddWithValue("$id", itemId);
            update.ExecuteNonQuery();
        }

        using (var log = connection.CreateCommand())
        {
            log.Transaction = transaction;
            log.CommandText = """
                INSERT INTO stock_movements (item_id, moved_at, lane_id, delta, balance_after, reason, reference)
                VALUES ($id, $at, $lane, $delta, $balance, $reason, $reference);
                """;
            log.Parameters.AddWithValue("$id", itemId);
            log.Parameters.AddWithValue("$at", DateTimeOffset.Now);
            log.Parameters.AddWithValue("$lane", laneId);
            log.Parameters.AddWithValue("$delta", balance - current);
            log.Parameters.AddWithValue("$balance", balance);
            log.Parameters.AddWithValue("$reason", reason.ToString());
            log.Parameters.AddWithValue("$reference", (object?)reference ?? DBNull.Value);
            log.ExecuteNonQuery();
        }

        transaction.Commit();
        return balance;
    }

    private decimal? Current(long itemId)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT stock_qty FROM items WHERE id = $id;";
        command.Parameters.AddWithValue("$id", itemId);

        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToDecimal(value);
    }

    public IReadOnlyList<StockLevel> List(int limit = 500) => Levels(lowOnly: false, limit);

    public IReadOnlyList<StockLevel> ListLow(int limit = 500) => Levels(lowOnly: true, limit);

    private List<StockLevel> Levels(bool lowOnly, int limit)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();

        // Ordered by how far below the line each item is, not alphabetically. The point of the
        // listing is what to buy first, and a shop with two hundred tracked lines should not have
        // to read all of them to find the three that matter.
        //
        // CAST is needed because the quantities are stored as text to keep them exact; without it
        // SQLite compares '9' against '10' as strings and puts nine below ten.
        command.CommandText = $"""
            SELECT {LevelColumns}
            FROM items i
            WHERE i.is_active = 1
              AND i.stock_qty IS NOT NULL
              {(lowOnly ? "AND i.reorder_level IS NOT NULL AND CAST(i.stock_qty AS REAL) <= CAST(i.reorder_level AS REAL)" : "")}
            ORDER BY
              CASE WHEN i.reorder_level IS NULL THEN 1 ELSE 0 END,
              CAST(i.stock_qty AS REAL) - CAST(COALESCE(i.reorder_level, '0') AS REAL),
              i.name
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100_000));

        using var reader = command.ExecuteReader();
        var levels = new List<StockLevel>();

        while (reader.Read())
        {
            levels.Add(new StockLevel(
                ItemId: reader.GetInt64(0),
                Sku: reader.GetString(1),
                Name: reader.GetString(2),
                Category: reader.IsDBNull(3) ? null : reader.GetString(3),
                Quantity: reader.GetDecimal(4),
                ReorderLevel: reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                Unit: (UnitType)reader.GetInt32(6)));
        }

        return levels;
    }

    public IReadOnlyList<StockMovement> History(long itemId, int limit = 50)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.id, m.item_id, i.name, m.moved_at, m.lane_id, m.delta, m.balance_after,
                   m.reason, m.reference
            FROM stock_movements m
            JOIN items i ON i.id = m.item_id
            WHERE m.item_id = $id
            ORDER BY m.moved_at DESC, m.id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$id", itemId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 10_000));

        using var reader = command.ExecuteReader();
        var movements = new List<StockMovement>();

        while (reader.Read())
        {
            movements.Add(new StockMovement(
                Id: reader.GetInt64(0),
                ItemId: reader.GetInt64(1),
                ItemName: reader.GetString(2),
                MovedAt: reader.GetDateTimeOffset(3),
                LaneId: reader.GetString(4),
                Delta: reader.GetDecimal(5),
                BalanceAfter: reader.GetDecimal(6),
                Reason: Enum.TryParse<StockReason>(reader.GetString(7), out var reason) ? reason : StockReason.Adjust,
                Reference: reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return movements;
    }
}
