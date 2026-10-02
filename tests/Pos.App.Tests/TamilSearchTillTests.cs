using System.Windows.Input;
using Pos.App.ViewModels;
using Pos.Core.Domain;
using Pos.Core.Domain.Catalogue;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// At the till and on the owner's form: an item found by its Tamil name, and a Tamil name given to
/// an item by hand.
/// </summary>
public class TamilSearchTillTests
{
    private static BillingHarness Till() => new(
        Catalogue.Item(sku: "DAL001", barcode: "8901234567890", name: "Toor Dal 1kg", price: 189m) with { NameTa = "துவரம் பருப்பு" },
        Catalogue.Item(sku: "SUG001", name: "Sugar Loose", price: 45m, unit: UnitType.Kilogram) with { NameTa = "சர்க்கரை" });

    [Theory]
    [InlineData("paruppu")]
    [InlineData("பருப்பு")]
    [InlineData("thuvaram")]
    public void TypedTheWayTheCustomerSaidItTheItemGoesOn(string typed)
    {
        using var till = Till();

        till.TypeAndWait(typed);
        Assert.Equal("Toor Dal 1kg", Assert.Single(till.ViewModel.SearchResults).Name);

        till.Press(Key.Enter);

        Assert.Equal("Toor Dal 1kg", Assert.Single(till.ViewModel.Lines).Name);
    }

    [Fact]
    public void TheResultCarriesItsTamilNameToShowWhyItWasFound()
    {
        using var till = Till();

        till.TypeAndWait("sarkkarai");

        Assert.Equal("சர்க்கரை", Assert.Single(till.ViewModel.SearchResults).NameTa);
    }

    [Fact]
    public void ATamilNameGivenOnTheOwnersFormIsSavedAndFound()
    {
        using var temp = new TempDatabase();
        var items = temp.Items;
        var form = new NewItemViewModel(items, new HsnSuggester(query => items.Search(query)))
        {
            Sku = "MANJ01",
            Name = "Turmeric Powder 100g",
            NameTa = "  மஞ்சள் தூள்  ",
            HsnCode = "0910",
            GstRate = "5",
            Mrp = "38",
        };

        Assert.True(form.Save());

        Assert.Equal("மஞ்சள் தூள்", items.FindBySku("MANJ01")!.NameTa);
        Assert.Equal("MANJ01", Assert.Single(items.Search("manjal")).Sku);
        Assert.Equal(string.Empty, form.NameTa);
    }

    [Fact]
    public void AnItemAddedWithNoTamilNameHasNone()
    {
        using var temp = new TempDatabase();
        var items = temp.Items;
        var form = new NewItemViewModel(items, new HsnSuggester(query => items.Search(query)))
        {
            Sku = "MANJ01",
            Name = "Turmeric Powder 100g",
            HsnCode = "0910",
            GstRate = "5",
            Mrp = "38",
        };

        Assert.True(form.Save());

        Assert.Null(items.FindBySku("MANJ01")!.NameTa);
    }
}
