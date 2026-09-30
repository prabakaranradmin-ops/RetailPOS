using Microsoft.Data.Sqlite;
using Pos.Core.Domain;

namespace Pos.Core.Data;

/// <summary>
/// The shelf count and the ledger behind it.
/// </summary>
/// <remarks>
/// <para>
/// Every change goes through <see cref="Move"/>, <see cref="Set"/> or <see cref="ApplySheet"/>,
/// which write the new balance and the reason for it in one transaction. Nothing updates
/// <c>items.stock_qty</c> on its own — a figure that changed with no movement to explain it is
/// exactly the thing the ledger exists to prevent.
/// </para>
/// <para>
/// The same write keeps <c>full_qty</c>, what "full" means for the item: a delivery, a count or a
/// correction that takes the shelf higher than it has been raises it. A sale, a void or a count
/// going down never lowers it, or every item would look full at whatever it was last down to.
/// </para>
/// </remarks>
public sealed class StockRepository : IStockStore
{
    private readonly PosDatabase _database;
    private readonly Func<decimal> _percent;

    /// <param name="lowStockPercent">
    /// The share of full an item without a reorder level warns at, read each time a list is asked
    /// for, so a change on the owner's screen shows on the next one. 10% when not given.
    /// </param>
    public StockRepository(PosDatabase database, Func<decimal>? lowStockPercent = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
        _percent = lowStockPercent ?? (() => LowStock.DefaultPercent);
    }

    /// <summary>The share of full in force right now.</summary>
    public decimal LowStockPercent => _percent();

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

        var balance = WriteIn(connection, transaction, itemId, laneId, reason, reference, next, startCounting: false);

        transaction.Commit();
        return balance;
    }

    /// <summary>One movement, inside a transaction somebody else owns.</summary>
    /// <param name="startCounting">
    /// Whether an item nobody counts yet may be given a count. A sale must not start counting
    /// something — the shop never said it wanted it counted — but a stocktake that writes a figure
    /// down against it has.
    /// </param>
    internal static decimal? WriteIn(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long itemId,
        string laneId,
        StockReason reason,
        string? reference,
        Func<decimal, decimal> next,
        bool startCounting)
    {
        object? value;

        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT stock_qty FROM items WHERE id = $id;";
            read.Parameters.AddWithValue("$id", itemId);
            value = read.ExecuteScalar();
        }

        // No row at all, or an item nobody counts. Neither is an error — most of a first catalogue
        // has no stock figure — and only a count may start one.
        if (value is null || (value is DBNull && !startCounting))
            return null;

        var counted = value is not DBNull;
        var current = counted ? Convert.ToDecimal(value) : 0m;
        var balance = next(current);

        // A delivery, a count or a correction that takes the shelf higher is a restock. A void
        // puts back what a sale took, which is not the shelf being filled.
        var restock = reason is StockReason.Import or StockReason.Adjust or StockReason.Count or StockReason.Purchase
                      && (!counted || balance > current);

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE items
                SET stock_qty = $qty,
                    full_qty = CASE
                      WHEN $restock = 1 AND CAST($qty AS REAL) > 0
                           AND (full_qty IS NULL OR CAST(full_qty AS REAL) < CAST($qty AS REAL))
                        THEN $qty
                      ELSE full_qty
                    END
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$qty", balance);
            update.Parameters.AddWithValue("$restock", restock ? 1 : 0);
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
        var (since, days) = SalesRateSql.Window(connection, DateTimeOffset.Now);
        using var command = connection.CreateCommand();

        // Ordered by how far below the line each item is, not alphabetically. The point of the
        // listing is what to buy first, and a shop with two hundred tracked lines should not have
        // to read all of them to find the three that matter.
        //
        // CAST is needed because the quantities are stored as text to keep them exact; without it
        // SQLite compares '9' against '10' as strings and puts nine below ten.
        command.CommandText = $"""
            WITH sold AS ({SalesRateSql.SoldSince})
            SELECT i.id, i.sku, i.name, i.category, i.stock_qty, i.reorder_level, i.unit_type, i.full_qty,
                   {LowStockSql.WarnAt("i")} AS warn,
                   COALESCE(s.qty, 0)
            FROM items i
            LEFT JOIN sold s ON s.item_id = i.id
            WHERE i.is_active = 1
              AND i.stock_qty IS NOT NULL
              {(lowOnly ? "AND " + LowStockSql.IsLow("i") : "")}
            ORDER BY
              CASE WHEN warn IS NULL THEN 1 ELSE 0 END,
              CAST(i.stock_qty AS REAL) - COALESCE(warn, 0),
              i.name
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100_000));
        command.Parameters.AddWithValue("$pct", LowStockSql.Percent(_percent()));
        command.Parameters.AddWithValue("$since", since);

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
                Unit: (UnitType)reader.GetInt32(6),
                FullLevel: reader.IsDBNull(7) ? null : reader.GetDecimal(7),
                WarnAt: reader.IsDBNull(8) ? null : Math.Round((decimal)reader.GetDouble(8), 3),
                PerDay: Reorder.PerDay(Math.Round((decimal)reader.GetDouble(9), 3), days)));
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

    public IReadOnlyList<StockSheetItem> Sheet()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();

        // By department and then name, the order somebody walks the shelves in, so the sheet can be
        // filled in on one walk round the shop.
        command.CommandText = """
            SELECT id, sku, name, unit_type, stock_qty, full_qty
            FROM items
            WHERE is_active = 1
            ORDER BY COALESCE(category, 'zzz') COLLATE NOCASE, name COLLATE NOCASE;
            """;

        using var reader = command.ExecuteReader();
        var items = new List<StockSheetItem>();

        while (reader.Read())
        {
            items.Add(new StockSheetItem(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                (UnitType)reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetDecimal(4),
                reader.IsDBNull(5) ? null : reader.GetDecimal(5)));
        }

        return items;
    }

    public int ApplySheet(IReadOnlyList<StockSheetChange> changes, string laneId, string? reference = null)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentException.ThrowIfNullOrWhiteSpace(laneId);

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var counted = 0;

        foreach (var change in changes)
        {
            if (change.NewCount is { } count)
            {
                WriteIn(connection, transaction, change.ItemId, laneId, StockReason.Count, reference, _ => count, startCounting: true);
                counted++;
            }

            // After the count, so a full level the owner wrote on the same row is the one that stands.
            if (change.NewFullLevel is { } full)
            {
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE items SET full_qty = $full WHERE id = $id;";
                update.Parameters.AddWithValue("$full", full > 0m ? full : DBNull.Value);
                update.Parameters.AddWithValue("$id", change.ItemId);
                update.ExecuteNonQuery();
            }
        }

        transaction.Commit();
        return counted;
    }
}
