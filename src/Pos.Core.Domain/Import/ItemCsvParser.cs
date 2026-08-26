using System.Globalization;
using System.Text;
using Pos.Core.Hardware.Scanning;

// Aliased because this class has a "barcode" column constant of its own, which would otherwise
// shadow the type.
using BarcodeRules = Pos.Core.Hardware.Scanning.Barcode;

namespace Pos.Core.Domain.Import;

/// <param name="Line">Line number in the file, counting the header as line 1.</param>
/// <param name="Column">Which column is wrong, or empty when the problem is the whole row.</param>
/// <param name="Problem">What is wrong with it, in words a shopkeeper can act on.</param>
public sealed record ImportProblem(int Line, string Column, string Problem)
{
    public override string ToString() =>
        string.IsNullOrEmpty(Column) ? $"line {Line}: {Problem}" : $"line {Line}, {Column}: {Problem}";
}

/// <summary>A row that parsed cleanly, with the line it came from for error reporting.</summary>
public sealed record ParsedItem(int Line, Item Item);

/// <summary>
/// Reads a catalogue CSV into items, checking every rule before anything reaches the database.
/// </summary>
/// <remarks>
/// Import is the one moment where a single bad cell prices a product wrong for as long as the
/// store sells it, and nobody notices until a customer or an auditor does. So the validation here
/// is deliberately unforgiving: an unknown GST slab, a selling price above MRP, a barcode whose
/// check digit does not add up, or a unit that disagrees with the weighed flag all stop the row.
/// Every problem in the file is reported at once, because a shopkeeper fixing a spreadsheet wants
/// the whole list, not the first line that failed.
/// </remarks>
public static class ItemCsvParser
{
    /// <summary>The slabs GST actually has. Anything else is a typo, not a rate.</summary>
    public static readonly decimal[] ValidGstRates = [0m, 5m, 12m, 18m, 28m];

    private const string Sku = "sku";
    private const string Barcode = "barcode";
    private const string Name = "name";
    private const string Hsn = "hsn_code";
    private const string Unit = "unit";
    private const string Mrp = "mrp";
    private const string SellingPrice = "selling_price";
    private const string GstRate = "gst_rate";
    private const string IsWeighed = "is_weighed";

    /// <summary>
    /// The two optional columns. A catalogue written before they existed imports unchanged, which
    /// is the whole point: a shop should never have to rewrite a working file to take an update.
    /// </summary>
    private const string Category = "category";
    private const string CostPrice = "cost_price";

    private static readonly string[] RequiredColumns =
        [Sku, Barcode, Name, Hsn, Unit, Mrp, SellingPrice, GstRate, IsWeighed];

    public static (IReadOnlyList<ParsedItem> Items, IReadOnlyList<ImportProblem> Problems) Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var items = new List<ParsedItem>();
        var problems = new List<ImportProblem>();

        var rows = ReadRows(reader).ToList();

        if (rows.Count == 0)
        {
            problems.Add(new ImportProblem(1, string.Empty, "The file is empty."));
            return (items, problems);
        }

        var header = MapHeader(rows[0].Fields, problems);

        if (problems.Count > 0)
            return (items, problems);

        // Catching repeats inside the file matters as much as catching them against the database:
        // a spreadsheet with the same barcode on two rows would otherwise import whichever came
        // last and silently lose the other product.
        var seenSkus = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var seenBarcodes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows.Skip(1))
        {
            if (row.Fields.All(string.IsNullOrWhiteSpace))
                continue;

            var before = problems.Count;
            var item = ParseRow(row, header, problems, seenSkus, seenBarcodes);

            if (item is not null && problems.Count == before)
                items.Add(new ParsedItem(row.Line, item));
        }

        return (items, problems);
    }

    private static Dictionary<string, int> MapHeader(string[] fields, List<ImportProblem> problems)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < fields.Length; i++)
        {
            var name = fields[i].Trim().ToLowerInvariant();

            if (name.Length > 0 && !map.ContainsKey(name))
                map[name] = i;
        }

        foreach (var required in RequiredColumns)
        {
            if (!map.ContainsKey(required))
                problems.Add(new ImportProblem(1, required, $"The file has no '{required}' column."));
        }

        return map;
    }

    private static Item? ParseRow(
        Row row,
        Dictionary<string, int> header,
        List<ImportProblem> problems,
        Dictionary<string, int> seenSkus,
        Dictionary<string, int> seenBarcodes)
    {
        var line = row.Line;

        var sku = Field(row, header, Sku);
        var barcode = Field(row, header, Barcode);
        var name = Field(row, header, Name);
        var hsn = Field(row, header, Hsn);

        if (sku.Length == 0)
            problems.Add(new ImportProblem(line, Sku, "An item needs a SKU."));

        if (name.Length == 0)
            problems.Add(new ImportProblem(line, Name, "An item needs a name."));

        if (hsn.Length == 0)
            problems.Add(new ImportProblem(line, Hsn, "A GST invoice has to carry an HSN code for every line."));

        if (sku.Length > 0 && !seenSkus.TryAdd(sku, line))
            problems.Add(new ImportProblem(line, Sku, $"SKU '{sku}' is already used on line {seenSkus[sku]}."));

        if (barcode.Length > 0)
        {
            if (!seenBarcodes.TryAdd(barcode, line))
                problems.Add(new ImportProblem(line, Barcode, $"Barcode '{barcode}' is already used on line {seenBarcodes[barcode]}."));

            // Only codes of an EAN or UPC length carry a check digit. Internal codes and other
            // symbologies are passed through, because refusing them would be worse than not
            // verifying them.
            if (BarcodeRules.Identify(barcode) != Symbology.Unknown && !BarcodeRules.IsValid(barcode))
                problems.Add(new ImportProblem(line, Barcode, $"'{barcode}' has the wrong check digit — a digit is mistyped or transposed."));
        }

        var unit = ParseUnit(Field(row, header, Unit), line, problems);
        var weighed = ParseBoolean(Field(row, header, IsWeighed), IsWeighed, line, problems);
        var mrp = ParseMoney(Field(row, header, Mrp), Mrp, line, problems);
        var sellingPrice = ParseMoney(Field(row, header, SellingPrice), SellingPrice, line, problems);
        var gstRate = ParseGstRate(Field(row, header, GstRate), line, problems);

        // The two ways of saying the same thing have to agree, or one of them is a mistake and
        // there is no way to know which.
        if (unit is not null && weighed is not null && unit.Value.AllowsFractionalQuantity() != weighed.Value)
        {
            problems.Add(new ImportProblem(
                line,
                IsWeighed,
                $"unit '{Field(row, header, Unit)}' and is_weighed '{Field(row, header, IsWeighed)}' contradict each other."));
        }

        // Selling above the printed maximum retail price is not allowed, and a till that does it
        // quietly is a liability rather than a convenience.
        if (mrp is not null && sellingPrice is not null && sellingPrice > mrp)
            problems.Add(new ImportProblem(line, SellingPrice, $"selling price {sellingPrice:0.00} is above the MRP of {mrp:0.00}."));

        // Both optional. A column that is not in the file, or a cell left empty in one that is,
        // means the shop has not said — which is a different thing from saying zero, and is treated
        // as such everywhere downstream.
        var category = Field(row, header, Category);
        var costText = Field(row, header, CostPrice);
        decimal? cost = null;

        if (costText.Length > 0)
        {
            cost = ParseMoney(costText, CostPrice, line, problems);

            // 0 <= cost <= selling <= mrp. A cost above what the item sells for is either a typo or
            // a line the shop is losing money on every time it scans, and both are worth stopping
            // the import over rather than discovering in a margin report months later.
            if (cost is not null && sellingPrice is not null && cost > sellingPrice)
                problems.Add(new ImportProblem(line, CostPrice, $"cost price {cost:0.00} is above the selling price of {sellingPrice:0.00}."));
        }

        if (unit is null || weighed is null || mrp is null || sellingPrice is null || gstRate is null)
            return null;

        if (costText.Length > 0 && cost is null)
            return null;

        return new Item
        {
            Sku = sku,
            Barcode = barcode.Length == 0 ? null : barcode,
            Name = name,
            HsnCode = hsn,
            Mrp = mrp.Value,
            SellPrice = sellingPrice.Value,
            GstRate = gstRate.Value,
            IsTaxInclusive = true,
            UnitType = unit.Value,
            Category = category.Length == 0 ? null : category,
            CostPrice = cost,
            IsActive = true,
        };
    }

    private static string Field(Row row, Dictionary<string, int> header, string column) =>
        header.TryGetValue(column, out var index) && index < row.Fields.Length
            ? row.Fields[index].Trim()
            : string.Empty;

    private static UnitType? ParseUnit(string value, int line, List<ImportProblem> problems)
    {
        var unit = value.ToLowerInvariant() switch
        {
            "pcs" or "pc" or "piece" or "each" or "nos" or "no" => UnitType.Each,
            "kg" or "kgs" or "kilogram" => UnitType.Kilogram,
            "l" or "ltr" or "litre" or "liter" => UnitType.Litre,
            "m" or "mtr" or "metre" or "meter" => UnitType.Metre,
            _ => (UnitType?)null,
        };

        if (unit is null)
            problems.Add(new ImportProblem(line, Unit, $"'{value}' is not a unit. Use Pcs or Kg."));

        return unit;
    }

    private static bool? ParseBoolean(string value, string column, int line, List<ImportProblem> problems)
    {
        var parsed = value.ToLowerInvariant() switch
        {
            "true" or "yes" or "y" or "1" => true,
            "false" or "no" or "n" or "0" => false,
            _ => (bool?)null,
        };

        if (parsed is null)
            problems.Add(new ImportProblem(line, column, $"'{value}' is not yes or no."));

        return parsed;
    }

    private static decimal? ParseMoney(string value, string column, int line, List<ImportProblem> problems)
    {
        // Spreadsheets export thousands separators and currency prefixes without being asked.
        var cleaned = value.Replace(",", string.Empty, StringComparison.Ordinal)
            .Replace("Rs.", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("₹", string.Empty, StringComparison.Ordinal)
            .Trim();

        if (!decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
        {
            problems.Add(new ImportProblem(line, column, $"'{value}' is not an amount."));
            return null;
        }

        if (amount < 0m)
        {
            problems.Add(new ImportProblem(line, column, $"{amount:0.00} is negative."));
            return null;
        }

        return amount;
    }

    private static decimal? ParseGstRate(string value, int line, List<ImportProblem> problems)
    {
        var cleaned = value.TrimEnd('%').Trim();

        if (!decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var rate))
        {
            problems.Add(new ImportProblem(line, GstRate, $"'{value}' is not a GST rate."));
            return null;
        }

        if (!ValidGstRates.Contains(rate))
        {
            problems.Add(new ImportProblem(
                line,
                GstRate,
                $"{rate:0.##}% is not a GST slab. Use one of {string.Join(", ", ValidGstRates.Select(r => $"{r:0.##}"))}."));
            return null;
        }

        return rate;
    }

    // ---- CSV reading -------------------------------------------------------------------------

    private readonly record struct Row(int Line, string[] Fields);

    /// <summary>
    /// Reads the file as CSV, honouring quoted fields so a product name with a comma in it does not
    /// silently shift every column after it.
    /// </summary>
    private static IEnumerable<Row> ReadRows(TextReader reader)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var line = 1;
        var inQuotes = false;
        var any = false;

        while (true)
        {
            var read = reader.Read();

            if (read < 0)
                break;

            var c = (char)read;
            any = true;

            if (inQuotes)
            {
                if (c == '"')
                {
                    // A doubled quote inside a quoted field is a literal quote.
                    if (reader.Peek() == '"')
                    {
                        reader.Read();
                        field.Append('"');
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;

                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    break;

                case '\r':
                    break;

                case '\n':
                    fields.Add(field.ToString());
                    field.Clear();
                    yield return new Row(line, [.. fields]);
                    fields.Clear();
                    line++;
                    any = false;
                    break;

                default:
                    field.Append(c);
                    break;
            }
        }

        if (any || field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            yield return new Row(line, [.. fields]);
        }
    }

    /// <summary>Strips a byte order mark, which Excel writes and nothing expects.</summary>
    public static TextReader OpenText(string path) =>
        new StreamReader(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: true);
}
