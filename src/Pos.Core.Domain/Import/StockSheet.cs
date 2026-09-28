using System.Globalization;
using System.Text;

namespace Pos.Core.Domain.Import;

/// <summary>What loading a filled-in stock sheet would do, and everything wrong with it.</summary>
/// <param name="Changes">Rows that change a count or a full level. Nothing has been written.</param>
/// <param name="Blank">Rows with nothing filled in, which are left alone.</param>
/// <param name="Unchanged">Rows whose new count is what the count already says.</param>
public sealed record StockSheetPlan(
    IReadOnlyList<StockSheetChange> Changes,
    IReadOnlyList<ImportProblem> Problems,
    int Blank,
    int Unchanged)
{
    public bool IsClean => Problems.Count == 0;

    public int Counts => Changes.Count(c => c.NewCount is not null);

    public int FullLevels => Changes.Count(c => c.NewFullLevel is not null);
}

/// <summary>
/// The bulk way to change shelf counts: a sheet of the shop's own items to fill in and load back.
/// </summary>
/// <remarks>
/// <para>
/// The catalogue file can set counts too, but only with every one of its nine columns beside each
/// one — prices, HSN, GST — and a delivery is not a price revision. The sheet carries only what a
/// count needs, and loading it changes only counts and full levels. A price cannot be changed by
/// it, however it is edited.
/// </para>
/// <para>
/// It is written from the shop's own catalogue rather than handed over blank, so nobody retypes a
/// SKU. The name, unit and current count are there to count against and are ignored on the way
/// back: only <c>sku</c>, <c>new_count</c> and <c>full_level</c> are read.
/// </para>
/// <para>
/// Loading is all or nothing, like a catalogue: a sheet with one mistake in it changes nothing,
/// and says where the mistake is.
/// </para>
/// </remarks>
public static class StockSheet
{
    public const string SkuColumn = "sku";
    public const string NewCountColumn = "new_count";
    public const string FullLevelColumn = "full_level";

    /// <summary>The sheet as CSV text, one row per active item.</summary>
    public static string Write(IEnumerable<StockSheetItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var csv = new StringBuilder();
        csv.AppendLine($"{SkuColumn},name,unit,have,{FullLevelColumn},{NewCountColumn}");

        foreach (var item in items)
        {
            csv.AppendLine(string.Join(',',
                Field(item.Sku),
                Field(item.Name),
                Field(Units.Of(item.Unit).Code),
                Quantity(item.Have),
                Quantity(item.FullLevel),
                string.Empty));
        }

        return csv.ToString();
    }

    /// <summary>
    /// Reads a filled-in sheet against the catalogue and works out what it would change, without
    /// writing anything.
    /// </summary>
    /// <param name="catalogue">The shop's items, by SKU. A SKU the catalogue does not hold is a problem.</param>
    public static StockSheetPlan Read(TextReader reader, IReadOnlyList<StockSheetItem> catalogue)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(catalogue);

        var problems = new List<ImportProblem>();
        var changes = new List<StockSheetChange>();
        var blank = 0;
        var unchanged = 0;

        var rows = ItemCsvParser.ReadRows(reader).ToList();

        if (rows.Count == 0)
            return new StockSheetPlan(changes, [new ImportProblem(1, string.Empty, "The file is empty.")], 0, 0);

        var header = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < rows[0].Fields.Length; i++)
        {
            var name = rows[0].Fields[i].Trim().ToLowerInvariant();

            if (name.Length > 0)
                header.TryAdd(name, i);
        }

        foreach (var required in new[] { SkuColumn, NewCountColumn })
        {
            if (!header.ContainsKey(required))
                problems.Add(new ImportProblem(1, required, $"The file has no '{required}' column. Save a fresh stock sheet and fill that in."));
        }

        if (problems.Count > 0)
            return new StockSheetPlan(changes, problems, 0, 0);

        var bySku = catalogue.ToDictionary(item => item.Sku, StringComparer.OrdinalIgnoreCase);
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows.Skip(1))
        {
            if (row.Fields.All(string.IsNullOrWhiteSpace))
                continue;

            var sku = Field(row, header, SkuColumn);
            var countText = Field(row, header, NewCountColumn);
            var fullText = Field(row, header, FullLevelColumn);

            if (sku.Length == 0)
            {
                // A row with no SKU and nothing to change is a stray line from a spreadsheet, not
                // a mistake. One with a count and no SKU would put that count nowhere.
                if (countText.Length > 0 || fullText.Length > 0)
                    problems.Add(new ImportProblem(row.Line, SkuColumn, "A count with no SKU beside it cannot be given to any item."));

                continue;
            }

            if (!seen.TryAdd(sku, row.Line))
            {
                problems.Add(new ImportProblem(row.Line, SkuColumn, $"SKU '{sku}' is already on line {seen[sku]}. Which count is right?"));
                continue;
            }

            if (!bySku.TryGetValue(sku, out var item))
            {
                problems.Add(new ImportProblem(row.Line, SkuColumn, $"No item in the catalogue has SKU '{sku}'. If Excel dropped leading zeros, format the column as text."));
                continue;
            }

            if (countText.Length == 0 && fullText.Length == 0)
            {
                blank++;
                continue;
            }

            var before = problems.Count;
            var count = countText.Length == 0 ? null : ParseQuantity(countText, NewCountColumn, row.Line, item, problems);
            var full = fullText.Length == 0 ? null : ParseQuantity(fullText, FullLevelColumn, row.Line, item, problems);

            if (problems.Count != before)
                continue;

            // The sheet goes out with the full level already filled in, so a full level that is
            // what it already was is not a change the owner asked for.
            var newFull = full is { } f && f != item.FullLevel ? full : null;
            var newCount = count is { } c && c != item.Have ? count : null;

            if (newCount is null && newFull is null)
            {
                // No count written, and the full level as it went out: a row left blank. A count
                // written that matches what the count already says is a row checked and found right.
                if (count is null)
                    blank++;
                else
                    unchanged++;

                continue;
            }

            changes.Add(new StockSheetChange(item.ItemId, item.Sku, item.Name, item.Have, newCount, newFull));
        }

        return new StockSheetPlan(problems.Count == 0 ? changes : [], problems, blank, unchanged);
    }

    private static decimal? ParseQuantity(string value, string column, int line, StockSheetItem item, List<ImportProblem> problems)
    {
        var cleaned = value.Replace(",", string.Empty, StringComparison.Ordinal).Trim();

        if (!decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var quantity))
        {
            problems.Add(new ImportProblem(line, column, $"'{value}' is not a quantity."));
            return null;
        }

        if (quantity < 0m)
        {
            problems.Add(new ImportProblem(line, column, $"{quantity:0.###} is negative."));
            return null;
        }

        // Counted the way it is sold: half a comb of bananas is not something on a shelf.
        if (!item.Unit.AllowsFractionalQuantity() && decimal.Truncate(quantity) != quantity)
        {
            problems.Add(new ImportProblem(line, column, $"{item.Name} is counted in whole {Units.Of(item.Unit).Code}, so {quantity:0.###} cannot be right."));
            return null;
        }

        return quantity;
    }

    private static string Field(ItemCsvParser.Row row, Dictionary<string, int> header, string column) =>
        header.TryGetValue(column, out var index) && index < row.Fields.Length
            ? row.Fields[index].Trim()
            : string.Empty;

    private static string Quantity(decimal? value) =>
        value?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>Quoted when it has to be: "Rice, Ponni" stays one field.</summary>
    private static string Field(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
}
