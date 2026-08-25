using Pos.Core.Domain;
using Pos.Core.Domain.Import;
using Pos.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Pos.Core.Tests;

/// <summary>
/// Catalogue import. This is the one moment where a single bad cell prices a product wrong for as
/// long as the store sells it, so most of these tests are about what the importer refuses.
/// </summary>
public class ItemImportTests(ITestOutputHelper output) : IDisposable
{
    private const string Header = "sku,barcode,name,hsn_code,unit,mrp,selling_price,gst_rate,is_weighed";

    private readonly TempDatabase _temp = new();

    public void Dispose() => _temp.Dispose();

    private static (IReadOnlyList<ParsedItem> Items, IReadOnlyList<ImportProblem> Problems) Parse(string csv) =>
        ItemCsvParser.Parse(new StringReader(csv));

    private ImportResult Import(string csv, bool updateExisting = false, bool dryRun = false) =>
        new ItemImporter(_temp.Items).Import(new StringReader(csv), updateExisting, dryRun);

    private static string Row(
        string sku = "DAL001",
        string barcode = "8901234567890",
        string name = "Toor Dal 1kg",
        string hsn = "0713",
        string unit = "Pcs",
        string mrp = "189.00",
        string sellingPrice = "189.00",
        string gstRate = "5",
        string isWeighed = "false") =>
        $"{sku},{barcode},{name},{hsn},{unit},{mrp},{sellingPrice},{gstRate},{isWeighed}";

    // ---- The happy path -----------------------------------------------------------------------

    [Fact]
    public void AWellFormedFileImports()
    {
        var result = Import($"""
            {Header}
            {Row()}
            {Row(sku: "SUG001", barcode: "8901234567906", name: "Sugar Loose", hsn: "1701", unit: "Kg", mrp: "45", sellingPrice: "45", isWeighed: "true")}
            {Row(sku: "SHM001", barcode: "8901234567913", name: "Shampoo 340ml", hsn: "3305", mrp: "299", sellingPrice: "279", gstRate: "18")}
            """);

        Assert.True(result.IsClean, string.Join("; ", result.Problems));
        Assert.True(result.Committed);
        Assert.Equal(3, result.RowsRead);
        Assert.Equal(3, result.Inserted);
        Assert.Equal(3, _temp.Items.Count());

        var sugar = _temp.Items.FindBySku("SUG001")!;
        Assert.Equal(UnitType.Kilogram, sugar.UnitType);
        Assert.Equal("1701", sugar.HsnCode);
        Assert.Equal(45m, sugar.SellPrice);

        var shampoo = _temp.Items.FindBySku("SHM001")!;
        Assert.Equal(299m, shampoo.Mrp);
        Assert.Equal(279m, shampoo.SellPrice);
        Assert.Equal(18m, shampoo.GstRate);
    }

    [Fact]
    public void ColumnsMayBeInAnyOrderAndAnyCase()
    {
        var result = Import("""
            GST_RATE,SKU,Name,MRP,selling_price,Unit,HSN_Code,is_weighed,Barcode
            5,DAL001,Toor Dal 1kg,189,189,Pcs,0713,false,8901234567890
            """);

        Assert.True(result.IsClean, string.Join("; ", result.Problems));
        Assert.Equal(5m, _temp.Items.FindBySku("DAL001")!.GstRate);
    }

    /// <summary>A product name with a comma in it must not shift every column after it.</summary>
    [Fact]
    public void QuotedFieldsHoldCommas()
    {
        var (items, problems) = Parse($"""
            {Header}
            DAL001,8901234567890,"Toor Dal, Premium, 1kg",0713,Pcs,189,189,5,false
            """);

        Assert.Empty(problems);
        Assert.Equal("Toor Dal, Premium, 1kg", Assert.Single(items).Item.Name);
    }

    [Fact]
    public void QuotesInsideAQuotedFieldAreHandled()
    {
        var (items, _) = Parse($"""
            {Header}
            DAL001,8901234567890,"Toor Dal ""Extra"" 1kg",0713,Pcs,189,189,5,false
            """);

        Assert.Equal("Toor Dal \"Extra\" 1kg", Assert.Single(items).Item.Name);
    }

    /// <summary>Spreadsheets export thousands separators and currency prefixes without being asked.</summary>
    [Theory]
    [InlineData("\"1,299.00\"", 1299.00)]
    [InlineData("Rs.1299", 1299)]
    [InlineData("1299", 1299)]
    public void SpreadsheetFormattingIsToleratedOnAmounts(string mrp, double expected)
    {
        var (items, problems) = Parse($"""
            {Header}
            DAL001,8901234567890,Oil 5L,1512,Pcs,{mrp},1000,5,false
            """);

        Assert.Empty(problems);
        Assert.Equal((decimal)expected, Assert.Single(items).Item.Mrp);
    }

    [Fact]
    public void ATrailingPercentOnTheRateIsTolerated()
    {
        var (items, problems) = Parse($"""
            {Header}
            {Row(gstRate: "18%")}
            """);

        Assert.Empty(problems);
        Assert.Equal(18m, Assert.Single(items).Item.GstRate);
    }

    [Fact]
    public void AnItemWithNoBarcodeIsFine()
    {
        var result = Import($"""
            {Header}
            {Row(sku: "SUG001", barcode: "", name: "Sugar Loose", hsn: "1701", unit: "Kg", isWeighed: "true")}
            """);

        Assert.True(result.IsClean, string.Join("; ", result.Problems));
        Assert.Null(_temp.Items.FindBySku("SUG001")!.Barcode);
    }

    [Fact]
    public void BlankLinesAreSkipped()
    {
        var result = Import($"{Header}\n{Row()}\n\n\n{Row(sku: "A2", barcode: "8901234567906")}\n");

        Assert.True(result.IsClean, string.Join("; ", result.Problems));
        Assert.Equal(2, result.RowsRead);
    }

    // ---- What it refuses ----------------------------------------------------------------------

    /// <summary>
    /// A rate that is not a GST slab is a typo, and a typo here misprices every sale of that item
    /// until somebody notices.
    /// </summary>
    [Theory]
    [InlineData("8")]
    [InlineData("15")]
    [InlineData("3")]
    [InlineData("100")]
    [InlineData("abc")]
    public void ARateThatIsNotAGstSlabIsRefused(string rate)
    {
        var result = Import($"{Header}\n{Row(gstRate: rate)}");

        Assert.False(result.IsClean);
        Assert.False(result.Committed);
        Assert.Contains(result.Problems, p => p.Column == "gst_rate");
        Assert.Equal(0, _temp.Items.Count());
    }

    /// <summary>Selling above the printed maximum retail price is not allowed.</summary>
    [Fact]
    public void ASellingPriceAboveTheMrpIsRefused()
    {
        var result = Import($"{Header}\n{Row(mrp: "189", sellingPrice: "199")}");

        Assert.False(result.IsClean);
        Assert.Contains(result.Problems, p => p.Problem.Contains("above the MRP"));
    }

    /// <summary>
    /// The check digit is what catches a mistyped or transposed barcode. Importing one would mean
    /// a scan that never finds the product, or finds the wrong one.
    /// </summary>
    [Fact]
    public void ABarcodeWithAWrongCheckDigitIsRefused()
    {
        var result = Import($"{Header}\n{Row(barcode: "8901234567894")}");

        Assert.False(result.IsClean);
        Assert.Contains(result.Problems, p => p.Problem.Contains("check digit"));
    }

    /// <summary>An internal code has no check digit to test, and refusing it would be worse.</summary>
    [Fact]
    public void AnInternalCodeThatIsNotAnEanIsAccepted()
    {
        var result = Import($"{Header}\n{Row(barcode: "INTERNAL-4471")}");

        Assert.True(result.IsClean, string.Join("; ", result.Problems));
    }

    [Fact]
    public void ADuplicateSkuInsideTheFileIsRefusedAndNamesBothLines()
    {
        var result = Import($"{Header}\n{Row()}\n{Row(barcode: "8901234567906")}");

        Assert.False(result.IsClean);

        var problem = Assert.Single(result.Problems, p => p.Column == "sku");
        Assert.Contains("line 2", problem.Problem);
        Assert.Equal(3, problem.Line);
    }

    [Fact]
    public void ADuplicateBarcodeInsideTheFileIsRefused()
    {
        var result = Import($"{Header}\n{Row()}\n{Row(sku: "A2")}");

        Assert.False(result.IsClean);
        Assert.Contains(result.Problems, p => p.Column == "barcode");
    }

    /// <summary>
    /// The unit and the weighed flag say the same thing two ways. If they disagree one is wrong and
    /// there is no way to know which.
    /// </summary>
    [Theory]
    [InlineData("Pcs", "true")]
    [InlineData("Kg", "false")]
    public void AUnitThatContradictsTheWeighedFlagIsRefused(string unit, string weighed)
    {
        var result = Import($"{Header}\n{Row(unit: unit, isWeighed: weighed)}");

        Assert.False(result.IsClean);
        Assert.Contains(result.Problems, p => p.Problem.Contains("contradict"));
    }

    [Theory]
    [InlineData("sku", "")]
    [InlineData("name", "")]
    [InlineData("hsn_code", "")]
    public void ARowMissingSomethingItMustHaveIsRefused(string column, string value)
    {
        var csv = column switch
        {
            "sku" => $"{Header}\n{Row(sku: value)}",
            "name" => $"{Header}\n{Row(name: value)}",
            _ => $"{Header}\n{Row(hsn: value)}",
        };

        var result = Import(csv);

        Assert.False(result.IsClean);
        Assert.Contains(result.Problems, p => p.Column == column);
    }

    [Fact]
    public void AMissingColumnIsReportedAgainstTheHeaderAndNothingElseIsParsed()
    {
        var result = Import("""
            sku,name,mrp
            DAL001,Toor Dal,189
            """);

        Assert.False(result.IsClean);
        Assert.All(result.Problems, p => Assert.Equal(1, p.Line));
        Assert.Contains(result.Problems, p => p.Column == "gst_rate");
        Assert.Contains(result.Problems, p => p.Column == "hsn_code");
    }

    [Fact]
    public void AnEmptyFileIsReportedRatherThanSilentlyImportingNothing()
    {
        var result = Import(string.Empty);

        Assert.False(result.IsClean);
        Assert.Contains(result.Problems, p => p.Problem.Contains("empty"));
    }

    [Theory]
    [InlineData("Dozen")]
    [InlineData("")]
    [InlineData("box")]
    public void AnUnknownUnitIsRefused(string unit)
    {
        var result = Import($"{Header}\n{Row(unit: unit)}");

        Assert.False(result.IsClean);
        Assert.Contains(result.Problems, p => p.Column == "unit");
    }

    [Fact]
    public void ANegativePriceIsRefused()
    {
        var result = Import($"{Header}\n{Row(mrp: "-5", sellingPrice: "-5")}");

        Assert.False(result.IsClean);
        Assert.Contains(result.Problems, p => p.Column == "mrp");
    }

    // ---- All or nothing -----------------------------------------------------------------------

    /// <summary>
    /// A partly imported catalogue is worse than a rejected one: the missing items cannot be sold,
    /// and nobody knows which they are.
    /// </summary>
    [Fact]
    public void OneBadRowStopsTheWholeFile()
    {
        var result = Import($"""
            {Header}
            {Row()}
            {Row(sku: "A2", barcode: "8901234567906", gstRate: "7")}
            {Row(sku: "A3", barcode: "8901234567913")}
            """);

        Assert.False(result.Committed);
        Assert.Equal(0, _temp.Items.Count());
    }

    /// <summary>
    /// Every problem in the file is reported at once. A shopkeeper fixing a spreadsheet wants the
    /// whole list, not the first line that failed.
    /// </summary>
    [Fact]
    public void EveryProblemInTheFileIsReportedTogetherAndInLineOrder()
    {
        var result = Import($"""
            {Header}
            {Row(gstRate: "7")}
            {Row(sku: "A2", barcode: "8901234567906", mrp: "100", sellingPrice: "150")}
            {Row(sku: "A3", barcode: "8901234567913", unit: "Dozen")}
            """);

        Assert.Equal(3, result.Problems.Count);
        Assert.Equal([2, 3, 4], result.Problems.Select(p => p.Line).ToArray());

        foreach (var problem in result.Problems)
            output.WriteLine(problem.ToString());
    }

    [Fact]
    public void ADryRunReportsWithoutWriting()
    {
        var result = Import($"{Header}\n{Row()}", dryRun: true);

        Assert.True(result.IsClean);
        Assert.False(result.Committed);
        Assert.Equal(1, result.Inserted);
        Assert.Equal(0, _temp.Items.Count());
    }

    // ---- Re-import ----------------------------------------------------------------------------

    [Fact]
    public void ASkuAlreadyInTheCatalogueIsRefusedUnlessUpdatingIsAskedFor()
    {
        Import($"{Header}\n{Row()}");

        var again = Import($"{Header}\n{Row(sellingPrice: "179")}");

        Assert.False(again.IsClean);
        Assert.Contains(again.Problems, p => p.Problem.Contains("already in the catalogue"));
        Assert.Equal(189m, _temp.Items.FindBySku("DAL001")!.SellPrice);
    }

    /// <summary>A re-import is nearly always a price change.</summary>
    [Fact]
    public void UpdatingChangesThePriceAndKeepsTheItem()
    {
        Import($"{Header}\n{Row()}");

        var again = Import($"{Header}\n{Row(sellingPrice: "179", name: "Toor Dal 1kg Premium")}", updateExisting: true);

        Assert.True(again.IsClean, string.Join("; ", again.Problems));
        Assert.Equal(1, again.Updated);
        Assert.Equal(0, again.Inserted);
        Assert.Equal(1, _temp.Items.Count());

        var item = _temp.Items.FindBySku("DAL001")!;
        Assert.Equal(179m, item.SellPrice);
        Assert.Equal("Toor Dal 1kg Premium", item.Name);
    }

    [Fact]
    public void AnUpdateCanAddNewItemsAtTheSameTime()
    {
        Import($"{Header}\n{Row()}");

        var again = Import($"""
            {Header}
            {Row(sellingPrice: "179")}
            {Row(sku: "RICE01", barcode: "8901234567920", name: "Basmati Rice 5kg", hsn: "1006", mrp: "649", sellingPrice: "649")}
            """, updateExisting: true);

        Assert.True(again.IsClean, string.Join("; ", again.Problems));
        Assert.Equal(1, again.Updated);
        Assert.Equal(1, again.Inserted);
        Assert.Equal(2, _temp.Items.Count());
    }

    /// <summary>
    /// A barcode identifies exactly one product. Handing it to a second SKU would make every scan
    /// of it ambiguous.
    /// </summary>
    [Fact]
    public void ABarcodeAlreadyBelongingToAnotherSkuIsRefused()
    {
        Import($"{Header}\n{Row()}");

        var again = Import($"{Header}\n{Row(sku: "OTHER1")}", updateExisting: true);

        Assert.False(again.IsClean);
        Assert.Contains(again.Problems, p => p.Problem.Contains("already belongs to SKU 'DAL001'"));
    }

    /// <summary>An item keeping its own barcode across a re-import is not a conflict.</summary>
    [Fact]
    public void AnItemKeepingItsOwnBarcodeUpdatesCleanly()
    {
        Import($"{Header}\n{Row()}");

        var again = Import($"{Header}\n{Row(sellingPrice: "179")}", updateExisting: true);

        Assert.True(again.IsClean, string.Join("; ", again.Problems));
    }

    // ---- Scale ---------------------------------------------------------------------------------

    /// <summary>
    /// A real catalogue is thousands of rows, and it has to land in one transaction — a store
    /// cannot be left with two thirds of its products.
    /// </summary>
    [Fact]
    public void AFullSizedCatalogueImportsInOnePiece()
    {
        var csv = new System.Text.StringBuilder(Header).AppendLine();

        for (var i = 1; i <= 5_000; i++)
        {
            var barcode = Pos.Core.Hardware.Scanning.Barcode.WithCheckDigit($"890{i:D9}");
            csv.AppendLine($"SKU{i:D6},{barcode},Item {i},0713,Pcs,{100 + i}.00,{100 + i}.00,{ItemCsvParser.ValidGstRates[i % 5]},false");
        }

        var result = Import(csv.ToString());

        Assert.True(result.IsClean, string.Join("; ", result.Problems.Take(5)));
        Assert.Equal(5_000, result.Inserted);
        Assert.Equal(5_000, _temp.Items.Count());
        Assert.NotNull(_temp.Items.FindBySku("SKU004999"));
    }
}
