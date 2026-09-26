using Pos.App.ViewModels;
using Pos.Core.Data;
using Pos.Core.Domain.Catalogue;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// Adding one product by hand, for the shop that has just started stocking something.
/// </summary>
/// <remarks>
/// The rule worth holding down is that this route is not a softer one. The form composes the single
/// row it was given and hands it to the same <c>ItemImporter</c> a CSV goes through, so a barcode
/// with a bad check digit, a selling price above MRP or a rate that is not a slab is refused here
/// exactly as it would be in a file. A friendlier screen with looser rules would be the way bad
/// data gets into a catalogue.
/// </remarks>
public class NewItemTests : IDisposable
{
    private readonly TempDatabase _temp = new();

    public void Dispose() => _temp.Dispose();

    private ItemRepository Items => new(_temp.Database);

    private NewItemViewModel Form()
    {
        var items = Items;
        return new NewItemViewModel(items, new HsnSuggester(query => items.Search(query)));
    }

    private static NewItemViewModel Filled(
        NewItemViewModel form,
        string sku = "SOAP01",
        string name = "Hamam Soap 100g",
        string barcode = "8901000000019",
        string hsn = "3401",
        string gst = "18",
        string mrp = "38.00",
        string? selling = null)
    {
        form.Sku = sku;
        form.Name = name;
        form.Barcode = barcode;
        form.HsnCode = hsn;
        form.GstRate = gst;
        form.Mrp = mrp;
        form.SellingPrice = selling ?? mrp;

        return form;
    }

    // ---- The ordinary case -----------------------------------------------------------------------

    [Fact]
    public void AnItemTypedInByHandReachesTheCatalogue()
    {
        var form = Filled(Form());

        Assert.True(form.Save());

        var saved = Items.FindBySku("SOAP01")!;

        Assert.Equal("Hamam Soap 100g", saved.Name);
        Assert.Equal("3401", saved.HsnCode);
        Assert.Equal(18m, saved.GstRate);
        Assert.Equal(38.00m, saved.SellPrice);

        // And it is sellable at the counter immediately, by the barcode that was typed.
        Assert.Equal("SOAP01", Items.FindByBarcode("8901000000019")!.Sku);
    }

    [Fact]
    public void TheFormEmptiesItselfForTheNextOneButKeepsTheDepartment()
    {
        var form = Filled(Form());
        form.Category = "Household";

        form.Save();

        Assert.Equal(string.Empty, form.Sku);
        Assert.Equal(string.Empty, form.Name);
        Assert.Equal("Household", form.Category);
    }

    /// <summary>Most grocery lines sell at the printed price, and typing it twice is a way to mistype it.</summary>
    [Fact]
    public void TheSellingPriceFollowsTheMrpUntilItIsSetItself()
    {
        var form = Form();

        form.Mrp = "38.00";
        Assert.Equal("38.00", form.SellingPrice);

        form.SellingPrice = "35.00";
        form.Mrp = "40.00";

        // Once the shop has said what it charges, nothing overwrites it.
        Assert.Equal("35.00", form.SellingPrice);
    }

    [Fact]
    public void ShelfCountsTypedInHereReachTheReorderList()
    {
        var form = Filled(Form());
        form.StockQty = "3";
        form.ReorderLevel = "10";

        Assert.True(form.Save());

        var low = new StockRepository(_temp.Database).ListLow(50);

        Assert.Single(low);
        Assert.True(low[0].IsLow);
    }

    // ---- It is not a softer way in ---------------------------------------------------------------

    [Fact]
    public void ABarcodeWithABadCheckDigitIsRefusedHereToo()
    {
        var form = Filled(Form(), barcode: "8901000000018");

        Assert.False(form.Save());
        Assert.Contains(form.Problems, p => p.Column == "barcode");
        Assert.Equal(0, Items.Count());
    }

    [Fact]
    public void SoIsASellingPriceAboveTheMrp()
    {
        var form = Filled(Form(), mrp: "38.00", selling: "45.00");

        Assert.False(form.Save());
        Assert.Contains(form.Problems, p => p.Column == "selling_price");
    }

    [Fact]
    public void AndARateThatIsNotASlab()
    {
        var form = Filled(Form(), gst: "7");

        Assert.False(form.Save());
        Assert.Contains(form.Problems, p => p.Column == "gst_rate");
    }

    [Fact]
    public void ASkuTheCatalogueAlreadyHasIsRefusedRatherThanWrittenOver()
    {
        Assert.True(Filled(Form()).Save());

        var second = Filled(Form(), name: "Different Soap", barcode: string.Empty);

        Assert.False(second.Save());
        Assert.Contains(second.Problems, p => p.Column == "sku");

        // The first item is exactly as it was.
        Assert.Equal("Hamam Soap 100g", Items.FindBySku("SOAP01")!.Name);
    }

    /// <summary>
    /// A name with a comma in it is ordinary. Unquoted it would shift every column after it, and
    /// the item would import as something else entirely.
    /// </summary>
    [Fact]
    public void ANameWithACommaInItSurvivesIntact()
    {
        var form = Filled(Form(), name: "Toor Dal, Premium, 1kg");

        Assert.True(form.Save());
        Assert.Equal("Toor Dal, Premium, 1kg", Items.FindBySku("SOAP01")!.Name);
    }

    [Fact]
    public void ANameWithQuotesInItSurvivesToo()
    {
        var form = Filled(Form(), name: "Amul \"Taaza\" Milk");

        Assert.True(form.Save());
        Assert.Equal("Amul \"Taaza\" Milk", Items.FindBySku("SOAP01")!.Name);
    }

    [Fact]
    public void NothingIsSavedUntilTheFieldsAnItemCannotExistWithoutAreThere()
    {
        var form = Form();
        form.Name = "Hamam Soap 100g";

        Assert.False(form.CanSave);
        Assert.False(form.Save());
        Assert.Equal(0, Items.Count());

        Filled(form);
        Assert.True(form.CanSave);
    }

    // ---- The HSN suggestions ---------------------------------------------------------------------

    [Fact]
    public void TypingANameOffersCodesAndTakingOneFillsBothFields()
    {
        var form = Form();
        form.Name = "Hamam Soap 100g";

        Assert.NotEmpty(form.HsnSuggestions);

        form.Accept(form.HsnSuggestions[0]);

        Assert.Equal("3401", form.HsnCode);
        Assert.Equal("18", form.GstRate);
    }

    /// <summary>
    /// Once the shop has added one soap, the next one is suggested from that rather than from the
    /// shipped table — which is the whole point of preferring its own catalogue.
    /// </summary>
    [Fact]
    public void TheSecondItemIsSuggestedFromTheFirst()
    {
        var first = Filled(Form(), sku: "SOAP01", name: "Lux Soap 100g", barcode: string.Empty, gst: "12");
        Assert.True(first.Save());

        var second = Form();
        second.Name = "Hamam Soap 100g";

        var top = second.HsnSuggestions[0];

        Assert.True(top.FromOwnCatalogue);
        Assert.Equal(12m, top.GstRate);
        Assert.Contains("Lux Soap 100g", top.Reason);
    }

    [Fact]
    public void AcceptingASuggestionSaysWhereItCameFrom()
    {
        var form = Form();
        form.Name = "Hamam Soap 100g";
        form.Accept(form.HsnSuggestions[0]);

        Assert.Contains("common code", form.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClearingTheNameClearsWhatWasOffered()
    {
        var form = Form();
        form.Name = "Hamam Soap 100g";

        Assert.NotEmpty(form.HsnSuggestions);

        form.Name = string.Empty;

        Assert.Empty(form.HsnSuggestions);
    }

    /// <summary>Saving announces itself so the reorder list and the item count can re-read.</summary>
    [Fact]
    public void AnItemAddedAnnouncesItself()
    {
        var form = Filled(Form());

        var announced = 0;
        form.Added += (_, _) => announced++;

        form.Save();

        Assert.Equal(1, announced);
    }
}
