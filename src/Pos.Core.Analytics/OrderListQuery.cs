using System.Globalization;
using Microsoft.Data.Sqlite;
using Pos.Core.Data;
using Pos.Core.Domain;

namespace Pos.Core.Analytics;

/// <summary>What to order, and from whom.</summary>
/// <param name="DaysMeasured">How many days of sales the rates are taken over.</param>
/// <param name="CoverDays">How many days each order is meant to last.</param>
public sealed record OrderList(DateTimeOffset GeneratedAt, int DaysMeasured, int CoverDays, IReadOnlyList<SupplierOrder> Suppliers)
{
    public int Lines => Suppliers.Sum(s => s.Lines.Count);

    public bool IsEmpty => Suppliers.Count == 0;
}

/// <summary>
/// Reads the shelf, the rate each thing sells at and the supplier each was last bought from, and
/// works out what to order from each.
/// </summary>
/// <remarks>
/// <para>
/// Read-only. The supplier for an item is the one on its most recent purchase bill that stands: the
/// wholesaler the shop actually went to last, rather than one the catalogue was told about once.
/// Items never bought on a purchase bill are listed together, so nothing that needs ordering is
/// left off for want of a supplier.
/// </para>
/// <para>
/// Only counted items: without a count there is no shelf to run out.
/// </para>
/// </remarks>
public sealed class OrderListQuery(PosDatabase database, decimal lowStockPercent = LowStock.DefaultPercent)
{
    private readonly PosDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

    public OrderList Gather(int coverDays = Reorder.DefaultCoverDays, DateTimeOffset? now = null)
    {
        if (!Reorder.IsValidCoverDays(coverDays))
            throw new ArgumentOutOfRangeException(nameof(coverDays), coverDays, "An order covers between 1 and 120 days.");

        var at = now ?? DateTimeOffset.Now;

        using var connection = _database.OpenConnection();
        var (since, days) = SalesRateSql.Window(connection, at);

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH sold AS ({SalesRateSql.SoldSince}),
            bought AS (
                SELECT pl.item_id, p.supplier_id, pl.rate, p.bill_date,
                       ROW_NUMBER() OVER (PARTITION BY pl.item_id ORDER BY p.bill_date DESC, p.id DESC, pl.id DESC) AS latest
                FROM purchase_lines pl
                JOIN purchases p ON p.id = pl.purchase_id
                WHERE p.voided_at IS NULL
            )
            SELECT i.id, i.sku, i.name, i.unit_type, i.stock_qty, i.full_qty, i.reorder_level,
                   {LowStockSql.IsLow("i")} AS low,
                   COALESCE(s.qty, 0),
                   b.supplier_id, sup.name, sup.phone, b.rate, b.bill_date
            FROM items i
            LEFT JOIN sold s ON s.item_id = i.id
            LEFT JOIN bought b ON b.item_id = i.id AND b.latest = 1
            LEFT JOIN suppliers sup ON sup.id = b.supplier_id
            WHERE i.is_active = 1 AND i.stock_qty IS NOT NULL;
            """;
        command.Parameters.AddWithValue("$since", since);
        command.Parameters.AddWithValue("$pct", LowStockSql.Percent(lowStockPercent));

        var rows = new List<(long? SupplierId, string Supplier, string? Phone, ReorderLine Line)>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var have = reader.GetDecimal(4);
            decimal? full = reader.IsDBNull(5) ? null : reader.GetDecimal(5);
            decimal? level = reader.IsDBNull(6) ? null : reader.GetDecimal(6);
            var low = !reader.IsDBNull(7) && reader.GetBoolean(7);
            var perDay = Reorder.PerDay(Math.Round((decimal)reader.GetDouble(8), 3), days);

            var order = Reorder.Suggest(have, perDay, coverDays, level, low, full);

            if (order <= 0m)
                continue;

            long? supplierId = reader.IsDBNull(9) ? null : reader.GetInt64(9);

            rows.Add((
                supplierId,
                supplierId is null ? SupplierOrder.NoSupplier : reader.GetString(10),
                supplierId is null || reader.IsDBNull(11) ? null : reader.GetString(11),
                new ReorderLine(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    (UnitType)reader.GetInt32(3),
                    have,
                    perDay,
                    Reorder.DaysLeft(have, perDay),
                    order,
                    reader.IsDBNull(12) ? null : reader.GetDecimal(12),
                    reader.IsDBNull(13) ? null : DateOnly.ParseExact(reader.GetString(13), "yyyy-MM-dd", CultureInfo.InvariantCulture))));
        }

        // The supplier with the most urgent line first, and within each the item that runs out
        // soonest. Something not selling has no days left to sort by, and goes last.
        static decimal Urgency(ReorderLine line) => line.DaysLeft ?? decimal.MaxValue;

        var suppliers = rows
            .GroupBy(r => r.SupplierId)
            .Select(g => new SupplierOrder(
                g.Key,
                g.First().Supplier,
                g.First().Phone,
                [.. g.Select(r => r.Line).OrderBy(Urgency).ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)]))
            .OrderBy(s => s.SupplierId is null ? 1 : 0)
            .ThenBy(s => s.Lines.Min(Urgency))
            .ThenBy(s => s.Supplier, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new OrderList(at, days, coverDays, suppliers);
    }
}
