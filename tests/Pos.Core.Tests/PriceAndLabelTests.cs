using System.Text;
using Pos.Core.Analytics;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Domain.Import;
using Pos.Core.Domain.Printing;
using Pos.Core.Hardware.Printing;
using Pos.TestSupport;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// Prices in bulk, and the shelf labels a price change puts out of date: the sheet's rules, the
/// database recording every change, and the labels on thermal paper and on A4.
/// </summary>
public class PriceAndLabelTests : IDisposable
{
    private readonly TempDatabase _temp = new();

    public void Dispose() => _temp.Dispose();

    private PriceRepository Prices => new(_temp.Database);

    private static readonly PriceSheetItem Dal = new(1, "DAL", "Toor Dal 1kg", UnitType.Each, 199m, 189m, 150m, 5m);
    private static readonly PriceSheetItem Sugar = new(2, "SUG", "Sugar Loose", UnitType.Kilogram, 48m, 45m, 38m, 5m);

    private static PriceSheetPlan Read(string csv) =>
        PriceSheet.Read(new StringReader(csv), [Dal, Sugar]);

    private const string Header = "sku,name,unit,gst_rate,cost_price,mrp,selling_price,new_mrp,new_selling_price\n";

    // ---- The sheet -------------------------------------------------------------------------------

    [Fact]
    public void TheSheetListsEveryItemWithItsPricesAndTwoEmptyColumns()
    {
        var lines = PriceSheet.Write([Dal, Sugar]).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(Header.TrimEnd(), lines[0]);
        Assert.Equal("DAL,Toor Dal 1kg,Pcs,5,150.00,199.00,189.00,,", lines[1]);
        Assert.Equal("SUG,Sugar Loose,Kg,5,38.00,48.00,45.00,,", lines[2]);
    }

    [Fact]
    public void ANewPriceIsAChangeAndBlankRowsAreLeftAlone()
    {
        var plan = Read(Header + "DAL,,,,,,,,179\nSUG,,,,,,,,\n");

        Assert.True(plan.IsClean);
        var change = Assert.Single(plan.Changes);
        Assert.Equal(189m, change.OldPrice);
        Assert.Equal(179m, change.NewPrice);
        Assert.Equal(199m, change.NewMrp);
        Assert.False(change.MrpChanged);
        Assert.Equal(1, plan.Blank);
    }

    [Fact]
    public void ThePriceItAlreadyHasIsNotAChange()
    {
        var plan = Read(Header + "DAL,,,,,,,199.00,189\n");

        Assert.Empty(plan.Changes);
        Assert.Equal(1, plan.Unchanged);
    }

    [Fact]
    public void AnMrpAndAPriceCanChangeTogether()
    {
        var change = Assert.Single(Read(Header + "DAL,,,,,,,210,199.50\n").Changes);

        Assert.True(change.MrpChanged);
        Assert.Equal(210m, change.NewMrp);
        Assert.Equal(199.50m, change.NewPrice);
    }

    [Theory]
    [InlineData("DAL,,,,,,,,205", "above the MRP")]
    [InlineData("DAL,,,,,,,180,", "Lower the selling price as well")]
    [InlineData("DAL,,,,,,,,abc", "not a price")]
    [InlineData("DAL,,,,,,,,0", "not a price anybody pays")]
    [InlineData("DAL,,,,,,,,179.005", "finer than a paisa")]
    [InlineData("RICE,,,,,,,,50", "No item in the catalogue")]
    [InlineData(",,,,,,,,50", "no SKU")]
    public void APriceThatCannotBeChargedIsRefused(string row, string says)
    {
        var plan = Read(Header + row + "\n");

        Assert.False(plan.IsClean);
        Assert.Empty(plan.Changes);
        Assert.Contains(plan.Problems, p => p.Problem.Contains(says, StringComparison.Ordinal));
    }

    [Fact]
    public void OneMistakeStopsTheWholeSheet()
    {
        var plan = Read(Header + "DAL,,,,,,,,179\nSUG,,,,,,,,60\n");

        Assert.False(plan.IsClean);
        Assert.Empty(plan.Changes);
    }

    [Fact]
    public void TheSameSkuTwiceIsAQuestion()
    {
        var plan = Read(Header + "DAL,,,,,,,,179\nDAL,,,,,,,,169\n");

        Assert.Contains(plan.Problems, p => p.Problem.Contains("already on line 2", StringComparison.Ordinal));
    }

    [Fact]
    public void ASheetWithoutTheNewColumnsIsRefused()
    {
        var plan = PriceSheet.Read(new StringReader("sku,price\nDAL,179\n"), [Dal]);

        Assert.Contains(plan.Problems, p => p.Column == PriceSheet.NewPriceColumn);
    }

    /// <summary>Loaded, but named first: below cost, and a jump that is probably a missing or extra digit.</summary>
    [Fact]
    public void BelowCostAndBigJumpsAreNamedButAllowed()
    {
        var plan = Read(Header + "DAL,,,,,,,,140\nSUG,,,,,,,,20\n");

        Assert.True(plan.IsClean);
        Assert.Equal(2, plan.Changes.Count);
        Assert.Contains(plan.Warnings, w => w.Contains("below the 150.00 it costs", StringComparison.Ordinal));
        Assert.Contains(plan.Warnings, w => w.Contains("from 45.00 to 20.00", StringComparison.Ordinal));
    }

    // ---- The books -------------------------------------------------------------------------------

    private void Load(params Item[] items) => _temp.Items.AddRange(items);

    [Fact]
    public void ANewItemIsDueALabel()
    {
        Load(Catalogue.Item(sku: "DAL", name: "Toor Dal 1kg", price: 189m));

        var label = Assert.Single(Prices.LabelsDue());
        Assert.Equal("Toor Dal 1kg", label.Name);
        Assert.Equal(189m, label.Price);
    }

    [Fact]
    public void ALabelDoneIsNotDueUntilThePriceChangesAgain()
    {
        Load(Catalogue.Item(sku: "DAL", price: 189m), Catalogue.Item(sku: "SUG", price: 45m));
        Prices.MarkLabelled(Prices.LabelsDue().Select(l => l.ItemId), DateTimeOffset.Now);

        Assert.Empty(Prices.LabelsDue());

        var sheet = Prices.PriceSheet();
        var dal = sheet.Single(i => i.Sku == "DAL");
        Prices.Apply([new PriceChange(dal.ItemId, dal.Sku, dal.Name, dal.Mrp, dal.Mrp, dal.Price, 179m)]);

        var due = Assert.Single(Prices.LabelsDue());
        Assert.Equal("DAL", due.Sku);
        Assert.Equal(179m, due.Price);
        Assert.Equal(179m, _temp.Items.FindBySku("DAL")!.SellPrice);
    }

    /// <summary>A re-import changes prices too, and the database records it whichever way it came.</summary>
    [Fact]
    public void ACatalogueReImportThatChangesAPriceMakesItsLabelDue()
    {
        Load(Catalogue.Item(sku: "DAL", price: 189m), Catalogue.Item(sku: "SUG", price: 45m));
        Prices.MarkLabelled(Prices.LabelsDue().Select(l => l.ItemId), DateTimeOffset.Now);

        _temp.Items.UpsertRange([Catalogue.Item(sku: "DAL", price: 189.00m), Catalogue.Item(sku: "SUG", price: 47m)]);

        Assert.Equal("SUG", Assert.Single(Prices.LabelsDue()).Sku);
    }

    [Fact]
    public void APriceChangedSinceTheSheetWasCheckedIsNotWrittenOver()
    {
        Load(Catalogue.Item(sku: "DAL", price: 189m), Catalogue.Item(sku: "SUG", price: 45m));
        var sheet = Prices.PriceSheet();
        var dal = sheet.Single(i => i.Sku == "DAL");
        var sugar = sheet.Single(i => i.Sku == "SUG");

        _temp.Items.UpsertRange([Catalogue.Item(sku: "SUG", price: 47m)]);

        var error = Assert.Throws<InvalidOperationException>(() => Prices.Apply(
        [
            new PriceChange(dal.ItemId, dal.Sku, dal.Name, dal.Mrp, dal.Mrp, dal.Price, 179m),
            new PriceChange(sugar.ItemId, sugar.Sku, sugar.Name, sugar.Mrp, sugar.Mrp, sugar.Price, 44m),
        ]));

        Assert.Contains("has changed since the sheet was checked", error.Message);
        Assert.Equal(189m, _temp.Items.FindBySku("DAL")!.SellPrice);
        Assert.Equal(47m, _temp.Items.FindBySku("SUG")!.SellPrice);
    }

    [Fact]
    public void EveryItemCanHaveALabelNotOnlyTheChanged()
    {
        Load(Catalogue.Item(sku: "DAL"), Catalogue.Item(sku: "SUG"), Catalogue.Item(sku: "OLD", active: false));
        Prices.MarkLabelled(Prices.LabelsDue().Select(l => l.ItemId), DateTimeOffset.Now);

        Assert.Empty(Prices.LabelsDue());
        Assert.Equal(2, Prices.AllLabels().Count);
    }

    // ---- On paper --------------------------------------------------------------------------------

    private static readonly ShelfLabel Packet = new(1, "DAL", "Toor Dal 1kg", "8901234567890", UnitType.Each, 199m, 189m);
    private static readonly ShelfLabel Loose = new(2, "SUG001", "Sugar Loose", null, UnitType.Kilogram, 48m, 48m);

    [Fact]
    public void AThermalLabelCarriesTheNameThePricesAndACode()
    {
        var text = new ShelfLabelComposer().Compose([Packet, Loose]).ToPlainText();

        Assert.Contains("Toor Dal 1kg", text);
        Assert.Contains("MRP Rs 199.00", text);
        Assert.Contains("You save Rs 10.00", text);
        Assert.Contains("Our price", text);
        Assert.Contains("Rs 189.00 / Pcs", text);
        Assert.Contains("||| 8901234567890 |||", text);

        // Sold loose: no saving to state, and its SKU as the code.
        Assert.Contains("Rs 48.00 / Kg", text);
        Assert.Contains("||| SUG001 |||", text);
        Assert.Equal(2, text.Split('\n').Count(l => l.StartsWith("=====", StringComparison.Ordinal)));
    }

    [Fact]
    public void ATamilLabelSaysItInTamil()
    {
        var text = new ShelfLabelComposer(language: ReceiptLanguage.Tamil).Compose([Packet]).ToPlainText();

        Assert.Contains("எங்கள் விலை", text);
        Assert.Contains("சேமிப்பு", text);
    }

    [Fact]
    public void AnEan13IsSentAsOneAndASkuAsCode128()
    {
        var ean = EscPos.Barcode("8901234567890");
        Assert.Equal([0x1D, (byte)'h', 60, 0x1D, (byte)'w', 2, 0x1D, (byte)'H', 2, 0x1D, (byte)'f', 0, 0x1D, (byte)'k', 67, 13], ean[..16]);
        Assert.Equal("8901234567890", Encoding.ASCII.GetString(ean[16..]));

        var sku = EscPos.Barcode("SUG001");
        Assert.Equal([0x1D, (byte)'k', 73, 8], sku[12..16]);
        Assert.Equal("{BSUG001", Encoding.ASCII.GetString(sku[16..]));

        // An EAN-13 with the wrong check digit is not one: it goes as Code 128, which reads back as typed.
        Assert.Equal(73, EscPos.Barcode("8901234567891")[14]);
    }

    [Theory]
    [InlineData("சர்க்கரை")]
    [InlineData("tab\there")]
    public void ACodeTheSymbologyCannotCarryIsRefused(string code) =>
        Assert.Throws<ArgumentException>(() => new ReceiptBuilder().Barcode(code));

    [Fact]
    public void TheBarcodeReachesThePrinterCentred()
    {
        var bytes = new ReceiptBuilder().Barcode("8901234567890").ToEscPos();
        var text = Encoding.Latin1.GetString(bytes);

        Assert.Contains("\u001ba\u0001", text);
        Assert.Contains("\u001dkC\u000d8901234567890", text);
    }

    /// <summary>
    /// The published example 5901234123457: first digit 5 sets the left half's parity to
    /// odd-even-even-odd-odd-even.
    /// </summary>
    [Fact]
    public void AnEan13IsDrawnModuleForModule()
    {
        var expected = "101"
            + "0001011" + "0100111" + "0110011" + "0010011" + "0111101" + "0011101"
            + "01010"
            + "1100110" + "1101100" + "1000010" + "1011100" + "1001110" + "1000100"
            + "101";

        var bars = Ean13.Bars("5901234123457")!;

        Assert.Equal(95, bars.Length);
        Assert.Equal(expected, new string(bars.Select(b => b ? '1' : '0').ToArray()));
        Assert.Null(Ean13.Bars("5901234123458"));
        Assert.Null(Ean13.Bars("SUG001"));
    }

    [Fact]
    public void TheA4PageDrawsRealBarsForAnEanAndTheSkuOtherwise()
    {
        var page = ShelfLabelPage.Render([Packet, Loose], "Sri Murugan Stores");

        Assert.Contains("2 labels for Sri Murugan Stores", page);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(page, "<svg"));
        Assert.Contains("SUG001", page);
        Assert.Contains("You save Rs 10.00", page);
        Assert.Contains("Rs 189.00 <small>/ Pcs</small>", page);
    }
}
