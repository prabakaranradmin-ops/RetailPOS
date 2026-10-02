using Microsoft.Data.Sqlite;
using Pos.Core.Domain;
using Pos.Core.Domain.Catalogue;

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
        "id, sku, barcode, hsn_code, name, mrp, sell_price, gst_rate, is_tax_inclusive, unit_type, is_active, " +
        "category, cost_price, stock_qty, reorder_level, full_qty, older_mrp, older_price, older_left, name_ta";

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

    /// <summary>Every active item's SKU, for checking a sheet that names items by SKU.</summary>
    public IReadOnlySet<string> Skus() =>
        Strings("SELECT sku FROM items WHERE is_active = 1;");

    /// <summary>Every department an active item is in, for checking a sheet that names departments.</summary>
    public IReadOnlySet<string> Categories() =>
        Strings("SELECT DISTINCT category FROM items WHERE is_active = 1 AND category IS NOT NULL AND TRIM(category) <> '';");

    private HashSet<string> Strings(string sql)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();

        while (reader.Read())
            values.Add(reader.GetString(0).Trim());

        return values;
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

        // Last, how it sounds: paruppu for பருப்பு, jeeragam for seeragam. After every match on the
        // text as typed, so an exact name never sits below one that only sounds like it.
        var sound = SoundKey.Of(trimmed);

        if (sound.Length < SoundKey.MinLength)
            return results;

        foreach (var item in MatchSound(connection, sound, limit))
        {
            if (seen.Add(item.Id))
                results.Add(item);

            if (results.Count == limit)
                return results;
        }

        return results;
    }

    /// <summary>
    /// Items whose English or Tamil name sounds like what was typed, both folded by
    /// <see cref="SoundKey"/>.
    /// </summary>
    /// <remarks>
    /// The matching is done on the sound index alone, which holds both keys beside the active flag,
    /// and only the rows that match are fetched. Asked for directly with an ORDER BY on the name,
    /// the planner may walk the name index instead and fetch every row in the catalogue to read its
    /// keys - the same trap the SKU search avoids.
    /// </remarks>
    private static List<Item> MatchSound(SqliteConnection connection, string sound, int limit)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM items
            WHERE id IN (
                SELECT id FROM items INDEXED BY ix_items_active_sound
                WHERE is_active = 1
                  AND (sound_name LIKE $contains ESCAPE '\' OR sound_ta LIKE $contains ESCAPE '\')
                LIMIT $limit)
            ORDER BY name;
            """;
        command.Parameters.AddWithValue("$contains", "%" + EscapeLikePattern(sound) + "%");
        command.Parameters.AddWithValue("$limit", limit);

        return ReadAll(command);
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

    /// <summary>
    /// Loose items - nothing to scan - most often sold over the last four weeks first, for the
    /// till's quick keys.
    /// </summary>
    /// <remarks>
    /// Chosen from the sales rather than set up by hand, so the keys follow the season: the mangoes
    /// move up in April and down again in July without anybody touching a setting. Counted in bills,
    /// not kilos, because what the keys save is keystrokes per bill.
    /// </remarks>
    public IReadOnlyList<Item> LooseItems(int limit = 24)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {string.Join(", ", SelectColumns.Split(", ").Select(c => "i." + c))}
            FROM items i
            LEFT JOIN (
                SELECT l.item_id, COUNT(*) AS bills
                FROM invoice_lines l JOIN invoices v ON v.id = l.invoice_id
                WHERE v.voided_at IS NULL AND v.created_at >= $since
                GROUP BY l.item_id
            ) s ON s.item_id = i.id
            WHERE i.is_active = 1 AND (i.barcode IS NULL OR TRIM(i.barcode) = '')
            ORDER BY COALESCE(s.bills, 0) DESC, i.name COLLATE NOCASE
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$since", new DateTimeOffset(DateTime.Today.AddDays(-(Reorder.WindowDays - 1))));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));

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
                  (sku, barcode, hsn_code, name, mrp, sell_price, gst_rate, is_tax_inclusive, unit_type, is_active,
                   category, cost_price, stock_qty, reorder_level, full_qty, name_ta, sound_name, sound_ta)
                VALUES
                  ($sku, $barcode, $hsn, $name, $mrp, $sellPrice, $gstRate, $taxInclusive, $unitType, $active,
                   $category, $cost, $stock, $reorder, $fullInitial, $nameTa, $soundName, $soundTa)
                ON CONFLICT (sku) DO UPDATE SET
                  barcode = excluded.barcode,
                  hsn_code = excluded.hsn_code,
                  name = excluded.name,
                  sound_name = excluded.sound_name,

                  -- A file with no Tamil name for an item leaves the one it has, as an empty stock
                  -- cell leaves the count: a price revision is not the shop taking its names back.
                  name_ta = COALESCE(excluded.name_ta, items.name_ta),
                  sound_ta = CASE WHEN excluded.name_ta IS NULL THEN items.sound_ta ELSE excluded.sound_ta END,
                  mrp = excluded.mrp,
                  sell_price = excluded.sell_price,
                  gst_rate = excluded.gst_rate,
                  is_tax_inclusive = excluded.is_tax_inclusive,
                  unit_type = excluded.unit_type,
                  is_active = excluded.is_active,
                  category = excluded.category,
                  cost_price = excluded.cost_price,

                  -- COALESCE, not excluded: an empty stock cell leaves the live count alone.
                  --
                  -- A shop re-imports to change prices far more often than to restate its shelves,
                  -- and the file it re-imports is usually the one it first loaded. Overwriting here
                  -- would silently reset every count to whatever was in a spreadsheet weeks ago,
                  -- and the only sign would be wrong reorder warnings nobody could explain.
                  stock_qty = COALESCE(excluded.stock_qty, items.stock_qty),
                  reorder_level = COALESCE(excluded.reorder_level, items.reorder_level),

                  -- Full is what the file says when it says; otherwise a count in the file that
                  -- takes the shelf higher than it has ever been raises it. A lower count never
                  -- lowers it: a price revision reloading last month's file is not a stocktake.
                  full_qty = CASE
                    WHEN $full IS NOT NULL THEN $full
                    WHEN excluded.stock_qty IS NOT NULL AND CAST(excluded.stock_qty AS REAL) > 0
                         AND (items.full_qty IS NULL OR CAST(items.full_qty AS REAL) < CAST(excluded.stock_qty AS REAL))
                      THEN excluded.stock_qty
                    ELSE items.full_qty
                  END;
                """;

            foreach (var name in new[]
                     {
                         "$sku", "$barcode", "$hsn", "$name", "$mrp",
                         "$sellPrice", "$gstRate", "$taxInclusive", "$unitType", "$active", "$category", "$cost",
                         "$stock", "$reorder", "$full", "$fullInitial", "$nameTa", "$soundName", "$soundTa",
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
              (sku, barcode, hsn_code, name, mrp, sell_price, gst_rate, is_tax_inclusive, unit_type, is_active,
               category, cost_price, stock_qty, reorder_level, full_qty, name_ta, sound_name, sound_ta)
            VALUES
              ($sku, $barcode, $hsn, $name, $mrp, $sellPrice, $gstRate, $taxInclusive, $unitType, $active,
               $category, $cost, $stock, $reorder, $fullInitial, $nameTa, $soundName, $soundTa);
            SELECT last_insert_rowid();
            """;

        foreach (var name in new[]
                 {
                     "$sku", "$barcode", "$hsn", "$name", "$mrp",
                     "$sellPrice", "$gstRate", "$taxInclusive", "$unitType", "$active", "$category", "$cost",
                     "$stock", "$reorder", "$full", "$fullInitial", "$nameTa", "$soundName", "$soundTa",
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
        command.Parameters["$category"].Value = (object?)item.Category ?? DBNull.Value;
        command.Parameters["$cost"].Value = (object?)item.CostPrice ?? DBNull.Value;
        command.Parameters["$stock"].Value = (object?)item.StockQty ?? DBNull.Value;
        command.Parameters["$reorder"].Value = (object?)item.ReorderLevel ?? DBNull.Value;

        // What the file said about full, and what a new item starts with: that, or its first count.
        command.Parameters["$full"].Value = (object?)item.FullLevel ?? DBNull.Value;
        command.Parameters["$fullInitial"].Value = (object?)(item.FullLevel ?? (item.StockQty is > 0m ? item.StockQty : null)) ?? DBNull.Value;

        // How each name sounds, for the search: worked out here, on the one way into the table.
        var nameTa = string.IsNullOrWhiteSpace(item.NameTa) ? null : item.NameTa.Trim();
        command.Parameters["$nameTa"].Value = (object?)nameTa ?? DBNull.Value;
        command.Parameters["$soundName"].Value = SoundKey.Of(item.Name);
        command.Parameters["$soundTa"].Value = nameTa is null ? DBNull.Value : SoundKey.Of(nameTa);
    }

    /// <summary>
    /// Works out how each name sounds for every item that has no key yet: the whole catalogue, the
    /// first time the till starts after the keys were added, and nothing at all after that.
    /// </summary>
    /// <returns>How many items were given keys.</returns>
    public static int FillSoundKeys(SqliteConnection connection)
    {
        var missing = new List<(long Id, string Name, string? NameTa)>();

        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT id, name, name_ta FROM items WHERE sound_name IS NULL OR (name_ta IS NOT NULL AND sound_ta IS NULL);";

            using var reader = read.ExecuteReader();

            while (reader.Read())
                missing.Add((reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        if (missing.Count == 0)
            return 0;

        using var transaction = connection.BeginTransaction(deferred: false);
        using var write = connection.CreateCommand();
        write.Transaction = transaction;
        write.CommandText = "UPDATE items SET sound_name = $name, sound_ta = $ta WHERE id = $id;";
        var name = write.Parameters.Add("$name", SqliteType.Text);
        var ta = write.Parameters.Add("$ta", SqliteType.Text);
        var id = write.Parameters.Add("$id", SqliteType.Integer);

        foreach (var item in missing)
        {
            name.Value = SoundKey.Of(item.Name);
            ta.Value = item.NameTa is null ? DBNull.Value : SoundKey.Of(item.NameTa);
            id.Value = item.Id;
            write.ExecuteNonQuery();
        }

        transaction.Commit();
        return missing.Count;
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
        Category = reader.IsDBNull(11) ? null : reader.GetString(11),
        CostPrice = reader.IsDBNull(12) ? null : reader.GetDecimal(12),

        // Null is "not counted", and stays null rather than becoming a zero that would put an
        // out-of-stock warning on the counter screen for something nobody tracks.
        StockQty = reader.IsDBNull(13) ? null : reader.GetDecimal(13),
        ReorderLevel = reader.IsDBNull(14) ? null : reader.GetDecimal(14),
        FullLevel = reader.IsDBNull(15) ? null : reader.GetDecimal(15),
        OlderMrp = reader.FieldCount > 16 && !reader.IsDBNull(16) ? reader.GetDecimal(16) : null,
        OlderPrice = reader.FieldCount > 17 && !reader.IsDBNull(17) ? reader.GetDecimal(17) : null,
        OlderLeft = reader.FieldCount > 18 && !reader.IsDBNull(18) ? reader.GetDecimal(18) : null,
        NameTa = reader.FieldCount > 19 && !reader.IsDBNull(19) ? reader.GetString(19) : null,
    };

    /// <summary>
    /// Counts packs sold at the older MRP off what is left of them, and forgets the older MRP once
    /// they are gone - after which the till stops asking.
    /// </summary>
    /// <returns>How many older packs are left, or null when there are none now.</returns>
    public decimal? SoldAtOlderMrp(long itemId, decimal mrp, decimal quantity)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);

        decimal? left;

        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT older_mrp, older_left FROM items WHERE id = $id;";
            read.Parameters.AddWithValue("$id", itemId);

            using var reader = read.ExecuteReader();

            if (!reader.Read() || reader.IsDBNull(0) || reader.IsDBNull(1) || reader.GetDecimal(0) != mrp)
                return null;

            left = reader.GetDecimal(1) - quantity;
        }

        using (var write = connection.CreateCommand())
        {
            write.Transaction = transaction;

            if (left > 0m)
            {
                write.CommandText = "UPDATE items SET older_left = $left WHERE id = $id;";
                write.Parameters.AddWithValue("$left", left.Value);
            }
            else
            {
                write.CommandText = "UPDATE items SET older_mrp = NULL, older_price = NULL, older_left = NULL WHERE id = $id;";
                left = null;
            }

            write.Parameters.AddWithValue("$id", itemId);
            write.ExecuteNonQuery();
        }

        transaction.Commit();
        return left;
    }
}
