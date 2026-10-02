using Pos.App.ViewModels;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Domain.Catalogue;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// The owner's list of what the till sold that is not in the catalogue: read, grouped, added through
/// the ordinary form, or left out.
/// </summary>
public class OpenItemsViewModelTests : IDisposable
{
    private readonly TempDatabase _temp = new();

    public void Dispose() => _temp.Dispose();

    private NewItemViewModel NewItem { get; }

    private OpenItemsViewModel List { get; }

    public OpenItemsViewModelTests()
    {
        var items = _temp.Items;
        NewItem = new NewItemViewModel(items, new HsnSuggester(query => items.Search(query)));
        List = new OpenItemsViewModel(new OpenItemRepository(_temp.Database), NewItem);
    }

    private void Sell(string? cashier, params Item[] items)
    {
        var bill = new InvoiceEngine("33");

        foreach (var item in items)
            bill.AddItem(item);

        var basket = new TenderBasket(bill.Totals.AmountPayable);
        basket.Add(TenderType.Cash, bill.Totals.AmountPayable);

        new CheckoutService(new InvoiceRepository(_temp.Database), new CustomerRepository(_temp.Database), new RecordingDrawerService(),
            cashier: () => cashier).Complete("L1", bill, basket);
    }

    private static Item Coil(decimal price = 45m, string? barcode = "8901234599990") =>
        OpenItem.For("Mosquito coil", price, 18m, hsn: null, barcode);

    private static Item Agarbathi() => OpenItem.For("Agarbathi", 30m, 5m, hsn: "3307");

    [Fact]
    public void NothingWaitingSaysSoAndOffersNothing()
    {
        List.Load();

        Assert.False(List.HasWaiting);
        Assert.False(List.CanAct);
        Assert.Equal(string.Empty, List.Notice);
        Assert.Equal("Nothing sold at the till is waiting to be added.", List.Summary);
    }

    [Fact]
    public void SalesOfTheSameThingAreOneRowNewestFirst()
    {
        Sell("Murugan", Coil());
        Sell("Lakshmi", Agarbathi());
        Sell("Lakshmi", Coil());

        List.Load();

        Assert.Equal(["Mosquito coil", "Agarbathi"], List.Rows.Select(r => r.Name));
        Assert.Equal("₹45.00  ·  18%  ·  no HSN code  ·  sold 2 times", List.Rows[0].Sold);
        Assert.Equal("₹30.00  ·  5%  ·  HSN 3307  ·  sold once", List.Rows[1].Sold);
        Assert.Contains("by Lakshmi, Murugan", List.Rows[0].Detail);
        Assert.Contains("barcode 8901234599990", List.Rows[0].Detail);

        Assert.Equal("2 items sold at the till are not in the catalogue, 3 sales in all. Pick one and add it, so the till finds it next time, or take it off the list if the shop will not stock it.", List.Summary);
        Assert.Equal("2 items sold at the till are not in the catalogue yet. Ctrl+3 to add them.", List.Notice);
        Assert.Same(List.Rows[0], List.Selected);
    }

    [Fact]
    public void OneSaleOfOneThingIsSaidInTheSingular()
    {
        Sell(null, Coil());

        List.Load();

        Assert.Equal("1 item sold at the till is not in the catalogue. Pick one and add it, so the till finds it next time, or take it off the list if the shop will not stock it.", List.Summary);
        Assert.Equal("1 item sold at the till is not in the catalogue yet. Ctrl+3 to add it.", List.Notice);
        Assert.DoesNotContain(" by ", List.Rows[0].Detail);
    }

    [Fact]
    public void AddingFillsTheFormFromTheTill()
    {
        Sell("Murugan", Coil(price: 45.5m));
        List.Load();

        Assert.True(List.StartAdding());

        Assert.Equal("Mosquito coil", NewItem.Name);
        Assert.Equal("8901234599990", NewItem.Barcode);
        Assert.Equal("45.5", NewItem.Mrp);
        Assert.Equal("45.5", NewItem.SellingPrice);
        Assert.Equal("18", NewItem.GstRate);
        Assert.Equal(string.Empty, NewItem.HsnCode);
        Assert.Equal(string.Empty, NewItem.Sku);
        Assert.StartsWith("From the till: Mosquito coil, sold with no HSN code.", NewItem.Status);
        Assert.Equal("Mosquito coil is in the form below. Give it your own SKU, check it, and Add it.", List.Status);
    }

    [Fact]
    public void OnceTheFormHasAddedItItLeavesTheList()
    {
        Sell(null, Coil());
        Sell(null, Coil());
        Sell(null, Agarbathi());
        List.Load();

        // The newest is picked to start with; the coil is picked by hand.
        Assert.Equal("Agarbathi", List.Selected!.Name);
        List.Selected = List.Rows.Single(r => r.Name == "Mosquito coil");

        List.StartAdding();
        NewItem.Sku = "COIL10";
        NewItem.HsnCode = "3808";

        Assert.True(NewItem.Save());

        Assert.Equal("Agarbathi", Assert.Single(List.Rows).Name);
        Assert.Equal("Mosquito coil is in the catalogue now and off this list.", List.Status);
        Assert.Equal("Mosquito coil", _temp.Items.FindByBarcode("8901234599990")!.Name);
        Assert.Equal("COIL10", NewItem.LastAddedSku);
        Assert.Single(new OpenItemRepository(_temp.Database).Waiting());
    }

    [Fact]
    public void AFormThatCannotSaveLeavesItOnTheList()
    {
        Sell(null, Coil());
        List.Load();

        List.StartAdding();
        NewItem.Sku = "COIL10";

        // No HSN code: the form will not add it, so it is still waiting.
        Assert.False(NewItem.Save());
        Assert.Single(List.Rows);
    }

    [Fact]
    public void AFormClearedAndUsedForSomethingElseLeavesItOnTheList()
    {
        Sell(null, Coil());
        List.Load();

        List.StartAdding();
        List.ForgetAdding();
        NewItem.Clear();
        NewItem.Sku = "DAL001";
        NewItem.Name = "Toor Dal 1kg";
        NewItem.HsnCode = "0713";
        NewItem.GstRate = "5";
        NewItem.Mrp = "189";

        Assert.True(NewItem.Save());
        Assert.Single(List.Rows);
    }

    [Fact]
    public void LeavingOneOutTakesItOffTheListAndNothingElse()
    {
        Sell(null, Coil());
        Sell(null, Agarbathi());
        List.Load();
        List.Selected = List.Rows.Single(r => r.Name == "Mosquito coil");

        Assert.True(List.LeaveOut());

        Assert.Equal("Agarbathi", Assert.Single(List.Rows).Name);
        Assert.Equal("Mosquito coil is off the list. Its sales stay on the bills as they were.", List.Status);
        Assert.Null(_temp.Items.FindByBarcode("8901234599990"));
    }

    [Fact]
    public void NothingPickedIsSaid()
    {
        Sell(null, Coil());
        List.Load();
        List.Selected = null;

        Assert.False(List.CanAct);
        Assert.False(List.StartAdding());
        Assert.Equal("Pick one from the list first.", List.Status);
        Assert.False(List.LeaveOut());
        Assert.Single(List.Rows);
    }

    [Fact]
    public void ReadingAgainKeepsTheSameOnePicked()
    {
        Sell(null, Coil());
        Sell(null, Agarbathi());
        List.Load();
        List.Selected = List.Rows.Single(r => r.Name == "Mosquito coil");

        Sell(null, OpenItem.For("Phenyl 1L", 120m, 18m));
        List.Load();

        Assert.Equal("Mosquito coil", List.Selected!.Name);
    }

    [Fact]
    public void ACodeCarriedFromTheTillIsSaidAsSuch()
    {
        Sell(null, Agarbathi());
        List.Load();

        List.StartAdding();

        Assert.Equal("3307", NewItem.HsnCode);
        Assert.Equal("5", NewItem.GstRate);
        Assert.Equal("From the till: Agarbathi. Give it your own SKU, check the MRP and the code, and add it.", NewItem.Status);
    }
}
