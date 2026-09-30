using System.Globalization;
using System.Text;

namespace Pos.Core.Domain.Import;

/// <summary>One item as the price sheet lists it.</summary>
public sealed record PriceSheetItem(long ItemId, string Sku, string Name, UnitType Unit, decimal Mrp, decimal Price, decimal? Cost, decimal GstRate);

/// <summary>One price a loaded sheet will change.</summary>
public sealed record PriceChange(long ItemId, string Sku, string Name, decimal OldMrp, decimal NewMrp, decimal OldPrice, decimal NewPrice)
{
    public bool MrpChanged => OldMrp != NewMrp;

    public bool PriceChanged => OldPrice != NewPrice;
}

/// <summary>What loading a filled-in price sheet would do, and everything wrong with it.</summary>
/// <param name="Warnings">Not wrong, but worth a second look before it goes on the shelf.</param>
public sealed record PriceSheetPlan(
    IReadOnlyList<PriceChange> Changes,
    IReadOnlyList<ImportProblem> Problems,
    IReadOnlyList<string> Warnings,
    int Blank,
    int Unchanged)
{
    public bool IsClean => Problems.Count == 0;
}

/// <summary>
/// The bulk way to change prices: a sheet of the shop's own items with their prices, to fill in and
/// load back.
/// </summary>
/// <remarks>
/// <para>
/// Like the stock sheet, and for the same reason: the catalogue file changes prices too, but only
/// with every one of its columns beside them, and a price revision is not a new catalogue. Loading
/// this changes only the MRP and the selling price. Excel's own formulas do a percentage rise on a
/// column.
/// </para>
/// <para>
/// All or nothing. A price above its MRP is refused - it cannot be charged - and so is anything that
/// is not a price. A price below what the item costs, or one that moves by more than half, is loaded
/// but named first: those are usually a typo, and are sometimes meant.
/// </para>
/// </remarks>
public static class PriceSheet
{
    public const string SkuColumn = "sku";
    public const string NewMrpColumn = "new_mrp";
    public const string NewPriceColumn = "new_selling_price";

    /// <summary>More than this change either way is named before loading, as a likely typo.</summary>
    public const decimal LargeChangeShare = 0.5m;

    public static string Write(IEnumerable<PriceSheetItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var csv = new StringBuilder();
        csv.AppendLine($"{SkuColumn},name,unit,gst_rate,cost_price,mrp,selling_price,{NewMrpColumn},{NewPriceColumn}");

        foreach (var item in items)
        {
            csv.AppendLine(string.Join(',',
                Field(item.Sku),
                Field(item.Name),
                Field(Units.Of(item.Unit).Code),
                item.GstRate.ToString("0.##", CultureInfo.InvariantCulture),
                item.Cost?.ToString("0.00", CultureInfo.InvariantCulture) ?? string.Empty,
                Money(item.Mrp),
                Money(item.Price),
                string.Empty,
                string.Empty));
        }

        return csv.ToString();
    }

    /// <summary>Reads a filled-in sheet against the catalogue and works out what it would change, writing nothing.</summary>
    public static PriceSheetPlan Read(TextReader reader, IReadOnlyList<PriceSheetItem> catalogue)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(catalogue);

        var problems = new List<ImportProblem>();
        var warnings = new List<string>();
        var changes = new List<PriceChange>();
        var blank = 0;
        var unchanged = 0;

        var rows = ItemCsvParser.ReadRows(reader).ToList();

        if (rows.Count == 0)
            return new PriceSheetPlan(changes, [new ImportProblem(1, string.Empty, "The file is empty.")], warnings, 0, 0);

        var header = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < rows[0].Fields.Length; i++)
        {
            var name = rows[0].Fields[i].Trim().ToLowerInvariant();

            if (name.Length > 0)
                header.TryAdd(name, i);
        }

        foreach (var required in new[] { SkuColumn, NewMrpColumn, NewPriceColumn })
        {
            if (!header.ContainsKey(required))
                problems.Add(new ImportProblem(1, required, $"The file has no '{required}' column. Save a fresh price sheet and fill that in."));
        }

        if (problems.Count > 0)
            return new PriceSheetPlan(changes, problems, warnings, 0, 0);

        var bySku = catalogue.ToDictionary(item => item.Sku, StringComparer.OrdinalIgnoreCase);
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows.Skip(1))
        {
            if (row.Fields.All(string.IsNullOrWhiteSpace))
                continue;

            var sku = Field(row, header, SkuColumn);
            var mrpText = Field(row, header, NewMrpColumn);
            var priceText = Field(row, header, NewPriceColumn);

            if (sku.Length == 0)
            {
                if (mrpText.Length > 0 || priceText.Length > 0)
                    problems.Add(new ImportProblem(row.Line, SkuColumn, "A price with no SKU beside it cannot be given to any item."));

                continue;
            }

            if (!seen.TryAdd(sku, row.Line))
            {
                problems.Add(new ImportProblem(row.Line, SkuColumn, $"SKU '{sku}' is already on line {seen[sku]}. Which price is right?"));
                continue;
            }

            if (!bySku.TryGetValue(sku, out var item))
            {
                problems.Add(new ImportProblem(row.Line, SkuColumn, $"No item in the catalogue has SKU '{sku}'. If Excel dropped leading zeros, format the column as text."));
                continue;
            }

            if (mrpText.Length == 0 && priceText.Length == 0)
            {
                blank++;
                continue;
            }

            var before = problems.Count;
            var mrp = mrpText.Length == 0 ? item.Mrp : ParseMoney(mrpText, NewMrpColumn, row.Line, problems);
            var price = priceText.Length == 0 ? item.Price : ParseMoney(priceText, NewPriceColumn, row.Line, problems);

            if (problems.Count != before)
                continue;

            if (price > mrp)
            {
                problems.Add(new ImportProblem(row.Line, priceText.Length == 0 ? NewMrpColumn : NewPriceColumn,
                    priceText.Length == 0
                        ? $"An MRP of {Money(mrp)} is below the {Money(price)} {item.Name} sells for. Lower the selling price as well."
                        : $"{Money(price)} is above the MRP of {Money(mrp)}. Nothing can be sold above its MRP."));
                continue;
            }

            if (mrp == item.Mrp && price == item.Price)
            {
                unchanged++;
                continue;
            }

            if (item.Cost is { } cost && price < cost)
                warnings.Add($"Line {row.Line}: {item.Name} will sell for {Money(price)}, below the {Money(cost)} it costs.");

            if (item.Price > 0m && Math.Abs(price - item.Price) > item.Price * LargeChangeShare)
                warnings.Add($"Line {row.Line}: {item.Name} goes from {Money(item.Price)} to {Money(price)}. Check it is not a typo.");

            changes.Add(new PriceChange(item.ItemId, item.Sku, item.Name, item.Mrp, mrp, item.Price, price));
        }

        return new PriceSheetPlan(problems.Count == 0 ? changes : [], problems, problems.Count == 0 ? warnings : [], blank, unchanged);
    }

    private static decimal ParseMoney(string value, string column, int line, List<ImportProblem> problems)
    {
        var cleaned = value.Replace(",", string.Empty, StringComparison.Ordinal).Replace("₹", string.Empty, StringComparison.Ordinal).Trim();

        if (!decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
        {
            problems.Add(new ImportProblem(line, column, $"'{value}' is not a price."));
            return 0m;
        }

        if (amount <= 0m)
        {
            problems.Add(new ImportProblem(line, column, $"{amount:0.##} is not a price anybody pays."));
            return 0m;
        }

        if (decimal.Round(amount, 2) != amount)
        {
            problems.Add(new ImportProblem(line, column, $"{amount} is finer than a paisa."));
            return 0m;
        }

        return amount;
    }

    private static string Field(ItemCsvParser.Row row, Dictionary<string, int> header, string column) =>
        header.TryGetValue(column, out var index) && index < row.Fields.Length
            ? row.Fields[index].Trim()
            : string.Empty;

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Field(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
}
