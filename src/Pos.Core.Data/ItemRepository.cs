using Microsoft.Data.Sqlite;
using Pos.Core.Domain;

namespace Pos.Core.Data;

/// <summary>
/// Item master reads for the billing screen. Every method here sits on the critical path between
/// a keystroke and a line appearing in the grid, so the queries are written to hit an index.
/// </summary>
public sealed class ItemRepository : IItemStore
{
    /// <summary>
    /// Ceiling on rows returned to the search list. The cashier picks from a short list; fetching
    /// thousands of matches only to render ten of them is what makes typed search feel slow.
    /// </summary>
    public const int DefaultResultLimit = 50;

    private const string SelectColumns =
        "id, sku, barcode, hsn_code, name, mrp, sell_price, gst_rate, is_tax_inclusive, unit_type, is_active";

    private readonly PosDatabase _database;

    public ItemRepository(PosDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>
    /// Exact barcode lookup — the scanner path. A unique index makes this a single seek, which is
    /// why a scan can bypass the debounce and resolve immediately.
    /// </summary>
    public Item? FindByBarcode(string barcode)
    {
        if (string.IsNullOrWhiteSpace(barcode))
            return null;

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM items WHERE barcode = $barcode AND is_active = 1;";
        command.Parameters.AddWithValue("$barcode", barcode.Trim());

        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public Item? FindBySku(string sku)
    {
        if (string.IsNullOrWhiteSpace(sku))
            return null;

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM items WHERE sku = $sku AND is_active = 1;";
        command.Parameters.AddWithValue("$sku", sku.Trim());

        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    /// <summary>
    /// The single search box behind SRS 2.1. Match priority is exact barcode, then SKU prefix,
    /// then name substring; inactive items never appear and the result count is capped.
    /// </summary>
    /// <remarks>
    /// An exact barcode hit short-circuits and returns alone. That is not just a ranking
    /// preference: a barcode uniquely identifies one item, so once it matches there is nothing
    /// useful to disambiguate, and returning immediately keeps the scanner path off the substring
    /// scan entirely.
    /// </remarks>
    public IReadOnlyList<Item> Search(string query, int limit = DefaultResultLimit)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "Result limit must be positive.");

        var trimmed = query.Trim();

        var exact = FindByBarcode(trimmed);
        if (exact is not null)
            return [exact];

        using var connection = _database.OpenConnection();

        // The two branches are run separately because they need different indexes, and a single
        // OR across both leaves the planner able to serve only one of them. Merging in memory
        // costs nothing at these result counts and keeps each query on its own index.
        var results = new List<Item>(limit);
        var seen = new HashSet<long>();

        foreach (var item in MatchSkuPrefix(connection, trimmed, limit))
        {
            if (seen.Add(item.Id))
                results.Add(item);

            if (results.Count == limit)
                return results;
        }

        foreach (var item in MatchName(connection, trimmed, limit))
        {
            if (seen.Add(item.Id))
                results.Add(item);

            if (results.Count == limit)
                return results;
        }

        return results;
    }

    /// <summary>
    /// SKU prefix match, expressed as a half-open range so it becomes a seek on the SKU index.
    /// </summary>
    /// <remarks>
    /// It is deliberately not written as <c>sku LIKE 'abc%' ESCAPE '\'</c>. Supplying ESCAPE turns
    /// off SQLite's LIKE-prefix optimisation, and the planner then walks the name index fetching
    /// every row to read its SKU — 221ms over a 100k catalogue, against NFR-01's 100ms budget.
    /// The range bounds do the seeking; the LIKE that follows only re-checks exactness, on the
    /// handful of rows the range returned.
    /// </remarks>
    private static List<Item> MatchSkuPrefix(SqliteConnection connection, string query, int limit)
    {
        var upperBound = ExclusiveUpperBound(query);
        var pattern = EscapeLikePattern(query) + "%";

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM items
            WHERE sku >= $lo
              {(upperBound is null ? string.Empty : "AND sku < $hi")}
              AND is_active = 1
              AND sku LIKE $prefix ESCAPE '\'
            ORDER BY sku
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$lo", query);
        command.Parameters.AddWithValue("$prefix", pattern);
        command.Parameters.AddWithValue("$limit", limit);

        if (upperBound is not null)
            command.Parameters.AddWithValue("$hi", upperBound);

        return ReadAll(command);
    }

    /// <summary>
    /// Name substring match. A leading wildcard cannot seek, but the (is_active, name) index
    /// covers both the filter and the sort, so this scans the index rather than the table and only
    /// fetches rows for the few matches that survive the limit.
    /// </summary>
    private static List<Item> MatchName(SqliteConnection connection, string query, int limit)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM items
            WHERE is_active = 1
              AND name LIKE $contains ESCAPE '\'
            ORDER BY name
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$contains", "%" + EscapeLikePattern(query) + "%");
        command.Parameters.AddWithValue("$limit", limit);

        return ReadAll(command);
    }

    private static List<Item> ReadAll(SqliteCommand command)
    {
        var results = new List<Item>();

        using var reader = command.ExecuteReader();
        while (reader.Read())
            results.Add(Map(reader));

        return results;
    }

    /// <summary>
    /// Smallest string that sorts after every string starting with <paramref name="prefix"/>,
    /// giving the range seek its upper bound. Null when no such bound can be formed, in which case
    /// the caller drops the upper bound and relies on the LIKE and the limit — slower, but a SKU
    /// ending in the maximum code point is not something that happens.
    /// </summary>
    private static string? ExclusiveUpperBound(string prefix)
    {
        var last = prefix[^1];

        return last == char.MaxValue ? null : prefix[..^1] + (char)(last + 1);
    }

    public long Add(Item item)
    {
        ArgumentNullException.ThrowIfNull(item);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        PrepareInsert(command);
        BindInsert(command, item);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>
    /// Bulk insert in one transaction. Used for item master import and to build the catalogue the
    /// lookup latency benchmark measures against.
    /// </summary>
    /// <remarks>
    /// Refreshes the planner's statistics afterwards. Search latency depends on it — see
    /// <see cref="PosDatabase.Analyze"/> for what happens without it.
    /// </remarks>
    public void AddRange(IEnumerable<Item> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        using (var connection = _database.OpenConnection())
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            PrepareInsert(command);

            foreach (var item in items)
            {
                BindInsert(command, item);
                command.ExecuteScalar();
            }

            transaction.Commit();
        }

        _database.Analyze();
    }

    /// <summary>
    /// Inserts, or updates the item already holding that SKU, as one transaction. This is what a
    /// re-import runs through, and a re-import is nearly always a price change.
    /// </summary>
    public void UpsertRange(IEnumerable<Item> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        using (var connection = _database.OpenConnection())
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;

            command.CommandText = """
                INSERT INTO items
                  (sku, barcode, hsn_code, name, mrp, sell_price, gst_rate, is_tax_inclusive, unit_type, is_active)
                VALUES
                  ($sku, $barcode, $hsn, $name, $mrp, $sellPrice, $gstRate, $taxInclusive, $unitType, $active)
                ON CONFLICT (sku) DO UPDATE SET
                  barcode = excluded.barcode,
                  hsn_code = excluded.hsn_code,
                  name = excluded.name,
                  mrp = excluded.mrp,
                  sell_price = excluded.sell_price,
                  gst_rate = excluded.gst_rate,
                  is_tax_inclusive = excluded.is_tax_inclusive,
                  unit_type = excluded.unit_type,
                  is_active = excluded.is_active;
                """;

            foreach (var name in new[]
                     {
                         "$sku", "$barcode", "$hsn", "$name", "$mrp",
                         "$sellPrice", "$gstRate", "$taxInclusive", "$unitType", "$active",
                     })
            {
                command.Parameters.Add(new SqliteParameter(name, null));
            }

            foreach (var item in items)
            {
                BindInsert(command, item);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        _database.Analyze();
    }

    public int Count()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM items;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void PrepareInsert(SqliteCommand command)
    {
        command.CommandText = """
            INSERT INTO items
              (sku, barcode, hsn_code, name, mrp, sell_price, gst_rate, is_tax_inclusive, unit_type, is_active)
            VALUES
              ($sku, $barcode, $hsn, $name, $mrp, $sellPrice, $gstRate, $taxInclusive, $unitType, $active);
            SELECT last_insert_rowid();
            """;

        foreach (var name in new[]
                 {
                     "$sku", "$barcode", "$hsn", "$name", "$mrp",
                     "$sellPrice", "$gstRate", "$taxInclusive", "$unitType", "$active",
                 })
        {
            command.Parameters.Add(new SqliteParameter(name, null));
        }
    }

    private static void BindInsert(SqliteCommand command, Item item)
    {
        command.Parameters["$sku"].Value = item.Sku;
        command.Parameters["$barcode"].Value = (object?)item.Barcode ?? DBNull.Value;
        command.Parameters["$hsn"].Value = item.HsnCode;
        command.Parameters["$name"].Value = item.Name;
        command.Parameters["$mrp"].Value = item.Mrp;
        command.Parameters["$sellPrice"].Value = item.SellPrice;
        command.Parameters["$gstRate"].Value = item.GstRate;
        command.Parameters["$taxInclusive"].Value = item.IsTaxInclusive ? 1 : 0;
        command.Parameters["$unitType"].Value = (int)item.UnitType;
        command.Parameters["$active"].Value = item.IsActive ? 1 : 0;
    }

    /// <summary>
    /// Neutralises LIKE wildcards in user input, so an item name containing a literal '%' or '_'
    /// is searchable and a stray '%' does not turn the query into a match-everything scan.
    /// </summary>
    private static string EscapeLikePattern(string value) => value
        .Replace(@"\", @"\\", StringComparison.Ordinal)
        .Replace("%", @"\%", StringComparison.Ordinal)
        .Replace("_", @"\_", StringComparison.Ordinal);

    private static Item Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Sku = reader.GetString(1),
        Barcode = reader.IsDBNull(2) ? null : reader.GetString(2),
        HsnCode = reader.GetString(3),
        Name = reader.GetString(4),
        Mrp = reader.GetDecimal(5),
        SellPrice = reader.GetDecimal(6),
        GstRate = reader.GetDecimal(7),
        IsTaxInclusive = reader.GetInt32(8) != 0,
        UnitType = (UnitType)reader.GetInt32(9),
        IsActive = reader.GetInt32(10) != 0,
    };
}
