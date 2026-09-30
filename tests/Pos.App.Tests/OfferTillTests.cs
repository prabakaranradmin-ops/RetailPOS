using System.IO;
using System.Text;
using System.Windows.Input;
using Pos.App.ViewModels;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// Offers at the till: worked out as the bill is rung up, said as they apply, put right as it
/// changes, never over a discount given by hand - and loaded from the owner's sheet.
/// </summary>
public class OfferTillTests
{
    private static BillingHarness Till()
    {
        var till = new BillingHarness(
            Catalogue.Item(sku: "SOAP75", barcode: "8901234567937", name: "Bath Soap 75g", price: 20m, gstRate: 18m),
            Catalogue.Item(sku: "DAL001", barcode: "8901234567890", name: "Toor Dal 1kg", price: 189m));

        var soap = till.Items.FindBySku("SOAP75")!;
        till.ViewModel.Offers = [new Offer { Name = "Buy 2 soaps get 1", Kind = OfferKind.BuyGet, Sku = "SOAP75", ItemId = soap.Id, Buy = 2, Get = 1 }];
        return till;
    }

    [Fact]
    public void TheThirdSoapIsFreeAndTheTillSaysSo()
    {
        using var till = Till();

        till.Scan("8901234567937");
        till.Scan("8901234567937");
        Assert.Equal(40m, till.ViewModel.GrandTotal);

        till.Scan("8901234567937");

        Assert.Equal(40m, till.ViewModel.GrandTotal);
        Assert.Contains("Offer: Buy 2 soaps get 1 - 20.00 off Bath Soap 75g", till.ViewModel.StatusMessage);
    }

    [Fact]
    public void TakingASoapOffTakesTheFreeOneBack()
    {
        using var till = Till();
        till.Scan("8901234567937");
        till.Scan("8901234567937");
        till.Scan("8901234567937");

        till.Press(Key.Delete);

        Assert.Equal(40m, till.ViewModel.GrandTotal);
        Assert.All(till.ViewModel.Lines, l => Assert.False(l.HasOffer));
    }

    /// <summary>F4 on an offered line is the cashier's word: the offer leaves it until F4 sets it back to nothing.</summary>
    [Fact]
    public void AHandDiscountReplacesTheOfferUntilItIsTakenOff()
    {
        using var till = Till();
        till.Scan("8901234567937");
        till.Press(Key.F3);
        till.ViewModel.EditBuffer = "3";
        till.Press(Key.Enter);
        Assert.Equal(40m, till.ViewModel.GrandTotal);

        till.Press(Key.F4);
        till.ViewModel.EditBuffer = "5";
        till.Press(Key.Enter);

        Assert.Equal(55m, till.ViewModel.GrandTotal);
        Assert.False(till.ViewModel.Lines[0].HasOffer);

        till.Press(Key.F4);
        till.ViewModel.EditBuffer = "0";
        till.Press(Key.Enter);

        Assert.Equal(40m, till.ViewModel.GrandTotal);
        Assert.True(till.ViewModel.Lines[0].HasOffer);
    }

    /// <summary>A new list from the owner's screen re-prices the bill on the till straight away.</summary>
    [Fact]
    public void NewOffersRepriceTheBillOnScreen()
    {
        using var till = Till();
        till.ViewModel.Offers = [];
        till.Scan("8901234567890");
        till.Scan("8901234567890");
        Assert.Equal(378m, till.ViewModel.GrandTotal);

        till.ViewModel.Offers = [new Offer { Name = "Dal 10%", Kind = OfferKind.Percent, Sku = "DAL001", ItemId = till.Items.FindBySku("DAL001")!.Id, Percent = 10m }];

        Assert.Equal(340.20m, till.ViewModel.GrandTotal);
    }

    [Fact]
    public void TheOfferIsOnThePaidBill()
    {
        using var till = Till();
        till.Scan("8901234567937");
        till.Press(Key.F3);
        till.ViewModel.EditBuffer = "3";
        till.Press(Key.Enter);

        till.Press(Key.F12);
        till.Press(Key.Enter);
        till.Press(Key.Enter);

        var sale = till.Invoices.FindLatest(BillingHarness.LaneId)!;
        Assert.Equal("Buy 2 soaps get 1", sale.Sale.Lines[0].OfferName);
        Assert.Equal(40m, sale.Sale.Totals.AmountPayable);
        Assert.Contains("Offer: Buy 2 soaps get 1", Encoding.ASCII.GetString(till.Printer.LastJob));
    }

    // ---- The owner's sheet ---------------------------------------------------------------------

    [Fact]
    public void TheOwnerLoadsASheetAndTheTillGetsIt()
    {
        using var till = Till();
        using var temp = new TempDatabase();
        temp.Items.UpsertRange([Catalogue.Item(sku: "SOAP75", name: "Bath Soap 75g", price: 20m)]);
        IReadOnlyList<Offer>? handedOn = null;
        var screen = new OffersViewModel(new OfferRepository(temp.Database), temp.Items.Skus, temp.Items.Categories, offers => handedOn = offers);
        var path = Path.Combine(Path.GetTempPath(), $"offers-{Guid.NewGuid():N}.csv");

        try
        {
            Assert.Null(screen.SaveSheet(path));
            Assert.Contains("# Buy 2 soaps get 1", File.ReadAllText(path));

            File.WriteAllText(path, "name,kind,sku,buy,get\nSoap deal,BuyGet,SOAP75,2,1\n");
            var plan = screen.CheckSheet(path);

            Assert.NotNull(plan);
            Assert.Contains("1 new", screen.Question(plan));
            Assert.Null(screen.ApplySheet(plan));

            Assert.Equal("Soap deal", Assert.Single(handedOn!).Name);
            var row = Assert.Single(screen.Rows);
            Assert.Equal("On today", row.State);
            Assert.Equal("Buy 2, get 1 free (SOAP75)", row.What);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AWrongSheetChangesNothingAndSaysWhere()
    {
        using var temp = new TempDatabase();
        var store = new OfferRepository(temp.Database);
        store.ReplaceAll([new Offer { Name = "Keep me", Kind = OfferKind.BillAmount, Amount = 10m, MinBill = 100m }], DateTimeOffset.Now);
        var screen = new OffersViewModel(store, temp.Items.Skus, temp.Items.Categories);
        var path = Path.Combine(Path.GetTempPath(), $"offers-{Guid.NewGuid():N}.csv");

        try
        {
            File.WriteAllText(path, "name,kind,sku,buy,get\nSoap deal,BuyGet,NOPE,2,1\n");

            Assert.Null(screen.CheckSheet(path));
            Assert.True(screen.HasSheetProblems);
            Assert.Contains("Line 2, sku:", screen.SheetProblems[0]);
            Assert.Equal("Keep me", Assert.Single(store.All()).Name);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnOfferOnAMissingItemSaysItGivesNothing()
    {
        using var temp = new TempDatabase();
        var store = new OfferRepository(temp.Database);
        store.ReplaceAll([new Offer { Name = "Gone", Kind = OfferKind.BuyGet, Sku = "GONE", Buy = 2, Get = 1 }], DateTimeOffset.Now);
        var screen = new OffersViewModel(store, temp.Items.Skus, temp.Items.Categories);

        screen.Load();

        Assert.Equal("Gives nothing: no item has SKU GONE", Assert.Single(screen.Rows).State);
    }
}
