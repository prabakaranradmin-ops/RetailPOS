using System.Globalization;
using System.Text;

namespace Pos.Core.Domain.Import;

/// <summary>What loading an offers sheet would do, and everything wrong with it.</summary>
/// <param name="Offers">Every offer the sheet lists: loading it replaces the shop's offers with these.</param>
/// <param name="Warnings">Not wrong, but worth a look - a department nothing is in yet.</param>
public sealed record OfferSheetPlan(IReadOnlyList<Offer> Offers, IReadOnlyList<ImportProblem> Problems, IReadOnlyList<string> Warnings)
{
    public bool IsClean => Problems.Count == 0;
}

/// <summary>
/// The shop's offers and schemes as a sheet: one offer a row, filled in with Excel and loaded back.
/// </summary>
/// <remarks>
/// <para>
/// The sheet is the whole list. Loading it replaces every offer with what it lists, so taking a row
/// out ends that offer and the sheet saved from the screen is always the list as it runs. All or
/// nothing: a sheet with anything wrong changes nothing, and says what and where.
/// </para>
/// <para>
/// A row whose name starts with <c>#</c> is a note and is skipped - which is how a fresh sheet carries
/// its examples without loading them.
/// </para>
/// </remarks>
public static class OfferSheet
{
    public static readonly string[] Columns =
        ["name", "kind", "sku", "category", "buy", "get", "percent", "amount", "price", "min_bill", "free_qty", "from", "to", "days"];

    /// <summary>The sheet of the offers as they are - or, with none, of examples to copy.</summary>
    public static string Write(IEnumerable<Offer> offers)
    {
        ArgumentNullException.ThrowIfNull(offers);

        var csv = new StringBuilder();
        csv.AppendLine(string.Join(',', Columns));

        var list = offers.ToList();

        if (list.Count == 0)
        {
            // Examples, one of each kind, as notes: copy a row, take the # off, change it.
            csv.AppendLine("# Buy 2 soaps get 1,BuyGet,SOAP75,,2,1,,,,,,2026-10-01,2026-10-31,");
            csv.AppendLine("# 10% off staples,Percent,,Staples,,,10,,,,,,,");
            csv.AppendLine("# Biscuits 3 for 100,MultiPrice,BISC01,,3,,,,100,,,,,");
            csv.AppendLine("# 50 off a 1000 bill,BillAmount,,,,,,50,,1000,,,,");
            csv.AppendLine("# Wednesday 5% off 500,BillPercent,,,,,5,,,500,,,,Wed");
            csv.AppendLine("# Free sugar over 2000,FreeItem,SUG001,,,,,,,2000,1,,,");
        }

        foreach (var offer in list)
        {
            csv.AppendLine(string.Join(',',
                Field(offer.Name),
                offer.Kind.ToString(),
                Field(offer.Sku ?? string.Empty),
                Field(offer.Category ?? string.Empty),
                offer.Buy > 0 ? offer.Buy.ToString(CultureInfo.InvariantCulture) : string.Empty,
                offer.Get > 0 ? offer.Get.ToString(CultureInfo.InvariantCulture) : string.Empty,
                Figure(offer.Percent),
                Figure(offer.Amount),
                Figure(offer.Price),
                Figure(offer.MinBill),
                Figure(offer.FreeQuantity),
                offer.From?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                offer.To?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                string.Join(' ', offer.Days.Select(d => CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedDayName(d)))));
        }

        return csv.ToString();
    }

    /// <summary>Reads a filled-in sheet and works out the offers it lists, writing nothing.</summary>
    /// <param name="skus">The catalogue's SKUs: an offer on an item the shop does not have is refused.</param>
    /// <param name="categories">The catalogue's departments: an offer on one nothing is in is named.</param>
    public static OfferSheetPlan Read(TextReader reader, IReadOnlySet<string> skus, IReadOnlySet<string> categories)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(skus);
        ArgumentNullException.ThrowIfNull(categories);

        var problems = new List<ImportProblem>();
        var warnings = new List<string>();
        var offers = new List<Offer>();
        var rows = ItemCsvParser.ReadRows(reader).ToList();

        if (rows.Count == 0)
            return new OfferSheetPlan([], [new ImportProblem(1, string.Empty, "The file is empty.")], []);

        var header = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < rows[0].Fields.Length; i++)
        {
            var name = rows[0].Fields[i].Trim().ToLowerInvariant();

            if (name.Length > 0)
                header.TryAdd(name, i);
        }

        foreach (var required in new[] { "name", "kind" })
        {
            if (!header.ContainsKey(required))
                problems.Add(new ImportProblem(1, required, $"The file has no '{required}' column. Save a fresh offers sheet and fill that in."));
        }

        if (problems.Count > 0)
            return new OfferSheetPlan([], problems, []);

        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows.Skip(1))
        {
            if (row.Fields.All(string.IsNullOrWhiteSpace))
                continue;

            string Cell(string column) =>
                header.TryGetValue(column, out var index) && index < row.Fields.Length ? row.Fields[index].Trim() : string.Empty;

            var name = Cell("name");

            if (name.StartsWith('#'))
                continue;

            var before = problems.Count;

            void Wrong(string column, string message) => problems.Add(new ImportProblem(row.Line, column, message));

            if (name.Length > 0 && !seen.TryAdd(name, row.Line))
            {
                Wrong("name", $"'{name}' is already the name on line {seen[name]}. Each offer needs its own name.");
                continue;
            }

            var kindText = Cell("kind").Replace("-", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);

            if (!Enum.TryParse<OfferKind>(kindText, ignoreCase: true, out var kind) || !Enum.IsDefined(kind) || int.TryParse(kindText, out _))
            {
                Wrong("kind", $"'{Cell("kind")}' is not a kind of offer. Use BuyGet, Percent, MultiPrice, BillAmount, BillPercent or FreeItem.");
                continue;
            }

            var sku = Cell("sku");
            var category = Cell("category");

            var offer = new Offer
            {
                Name = name,
                Kind = kind,
                Sku = sku.Length > 0 ? sku : null,
                Category = category.Length > 0 ? category : null,
                Buy = Whole(Cell("buy"), "buy"),
                Get = Whole(Cell("get"), "get"),
                Percent = Number(Cell("percent"), "percent"),
                Amount = Number(Cell("amount"), "amount"),
                Price = Number(Cell("price"), "price"),
                MinBill = Number(Cell("min_bill"), "min_bill"),
                FreeQuantity = Number(Cell("free_qty"), "free_qty"),
                From = Day(Cell("from"), "from"),
                To = Day(Cell("to"), "to"),
                Days = WeekDays(Cell("days")),
            };

            if (problems.Count != before)
                continue;

            if (offer.Problem() is { } problem)
            {
                Wrong(ColumnFor(offer), problem);
                continue;
            }

            if (offer.Sku is { } itemSku && !skus.Contains(itemSku))
            {
                Wrong("sku", $"No item in the catalogue has SKU '{itemSku}'. If Excel dropped leading zeros, format the column as text.");
                continue;
            }

            if (offer.Category is { } department && !categories.Contains(department))
                warnings.Add($"Line {row.Line}: nothing in the catalogue is in '{department}' yet, so '{offer.Name}' will give nothing until something is.");

            offers.Add(offer);

            int Whole(string text, string column)
            {
                if (text.Length == 0)
                    return 0;

                if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                    return value;

                Wrong(column, $"'{text}' is not a whole number.");
                return 0;
            }

            decimal Number(string text, string column)
            {
                if (text.Length == 0)
                    return 0m;

                var cleaned = text.Replace(",", string.Empty, StringComparison.Ordinal).Replace("₹", string.Empty, StringComparison.Ordinal).TrimEnd('%').Trim();

                if (decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value >= 0m)
                    return value;

                Wrong(column, $"'{text}' is not a number.");
                return 0m;
            }

            DateOnly? Day(string text, string column)
            {
                if (text.Length == 0)
                    return null;

                if (DateOnly.TryParseExact(text, ["yyyy-MM-dd", "dd-MM-yyyy", "dd/MM/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                    return day;

                Wrong(column, $"'{text}' is not a date. Write it 2026-10-01 or 01-10-2026.");
                return null;
            }

            IReadOnlyList<DayOfWeek> WeekDays(string text)
            {
                var days = new List<DayOfWeek>();

                foreach (var part in text.Split([' ', ';', '/', '|'], StringSplitOptions.RemoveEmptyEntries))
                {
                    var match = Enum.GetValues<DayOfWeek>().Where(d => d.ToString().StartsWith(part, StringComparison.OrdinalIgnoreCase) && part.Length >= 2).ToList();

                    if (match.Count == 1)
                    {
                        if (!days.Contains(match[0]))
                            days.Add(match[0]);
                    }
                    else
                    {
                        Wrong("days", $"'{part}' is not a day. Write the days as Mon Tue Wed Thu Fri Sat Sun.");
                    }
                }

                return days;
            }
        }

        return new OfferSheetPlan(problems.Count == 0 ? offers : [], problems, problems.Count == 0 ? warnings : []);
    }

    /// <summary>The column a problem with an offer is most likely in, for the message.</summary>
    private static string ColumnFor(Offer offer) => offer.Kind switch
    {
        _ when string.IsNullOrWhiteSpace(offer.Name) => "name",
        _ when offer.From is { } from && offer.To is { } to && to < from => "to",
        OfferKind.BuyGet or OfferKind.MultiPrice or OfferKind.FreeItem when string.IsNullOrWhiteSpace(offer.Sku) => "sku",
        OfferKind.BuyGet => "buy",
        OfferKind.MultiPrice => offer.Buy < 2 ? "buy" : "price",
        OfferKind.Percent when offer.Percent is > 0m and < 100m => "sku",
        OfferKind.Percent or OfferKind.BillPercent => offer.Percent is > 0m and < 100m ? "min_bill" : "percent",
        OfferKind.BillAmount => offer.Amount <= 0m ? "amount" : "min_bill",
        OfferKind.FreeItem => offer.FreeQuantity <= 0m ? "free_qty" : "min_bill",
        _ => "name",
    };

    private static string Figure(decimal value) => value == 0m ? string.Empty : value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Field(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
}
