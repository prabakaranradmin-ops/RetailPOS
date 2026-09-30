using System.Text;
using System.Windows.Input;
using Pos.App.ViewModels;
using Pos.Core.Analytics;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// Customers: finding and naming them at the till, and reading one person's history back on the
/// owner's screen.
/// </summary>
public class CustomerTests : IDisposable
{
    private const string Lane = "L1";
    private const string HomeState = "33";

    private readonly TempDatabase _temp = new();

    public void Dispose() => _temp.Dispose();

    private CustomerRepository Customers => new(_temp.Database);

    private Customer Known(string mobile, string? name = null, int points = 0) =>
        Customers.Add(new Customer { MobileNo = mobile, Name = name, LoyaltyBalance = points });

    private Item Stock(string sku, string name, decimal price, UnitType unit = UnitType.Each)
    {
        var items = new ItemRepository(_temp.Database);
        items.UpsertRange([Catalogue.Item(sku: sku, name: name, price: price, unit: unit) with { CostPrice = price * 0.8m }]);
        return items.FindBySku(sku)!;
    }

    private CheckoutService Checkout() => new(
        new InvoiceRepository(_temp.Database),
        new CustomerRepository(_temp.Database),
        new RecordingDrawerService());

    private SettledInvoice Sell(Customer? customer, params (Item Item, decimal Qty)[] lines)
    {
        var bill = new InvoiceEngine(HomeState);

        foreach (var (item, qty) in lines)
        {
            bill.AddItem(item);

            if (qty != 1m)
                bill.SetQuantity(bill.Lines.Count - 1, qty);
        }

        if (customer is not null)
            bill.SetCustomer(customer);

        var basket = new TenderBasket(bill.Totals.AmountPayable);
        basket.Add(TenderType.Cash, bill.Totals.AmountPayable);

        return Checkout().Complete(Lane, bill, basket).Invoice;
    }

    // ---- Finding them ----------------------------------------------------------------------------

    [Fact]
    public void ACustomerIsFoundByTheStartOfTheirNumber()
    {
        Known("9876543210", "Lakshmi");
        Known("9123456780", "Ravi");

        var found = Customers.Search("98765");

        Assert.Single(found);
        Assert.Equal("Lakshmi", found[0].Name);
    }

    [Fact]
    public void ACustomerIsFoundByPartOfTheirNameInAnyCase()
    {
        Known("9876543210", "Lakshmi Narayanan");

        Assert.Single(Customers.Search("naray"));
        Assert.Single(Customers.Search("LAKS"));
    }

    /// <summary>A cashier typing "Lak" means the start of a name, not a name with "lak" inside it.</summary>
    [Fact]
    public void ANameThatStartsWithTheTextRanksAboveOneThatContainsIt()
    {
        Known("9000000001", "Malak Stores");
        Known("9000000002", "Lakshmi");

        var found = Customers.Search("lak");

        Assert.Equal("Lakshmi", found[0].Name);
    }

    [Fact]
    public void BlankTextFindsNobodyRatherThanEverybody()
    {
        Known("9876543210", "Lakshmi");

        Assert.Empty(Customers.Search("  "));
    }

    /// <summary>A percent sign in a search is a character, not a pattern that matches everyone.</summary>
    [Fact]
    public void WildcardsInTheSearchAreTakenLiterally()
    {
        Known("9876543210", "Lakshmi");

        Assert.Empty(Customers.Search("%"));
        Assert.Empty(Customers.Search("_"));
    }

    [Fact]
    public void ANameCanBeGivenAndCleared()
    {
        var customer = Known("9876543210");

        Customers.Rename(customer.Id, "  Lakshmi  ");
        Assert.Equal("Lakshmi", Customers.FindByMobile("9876543210")!.Name);

        Customers.Rename(customer.Id, " ");
        Assert.Null(Customers.FindByMobile("9876543210")!.Name);
    }

    // ---- Forgetting them -------------------------------------------------------------------------

    /// <summary>
    /// The person goes; the sale stays. An invoice is the shop's record of what it sold and the
    /// tax it charged, and it has to outlive whoever it was issued to.
    /// </summary>
    [Fact]
    public void ForgettingACustomerKeepsTheirBillsButNotWhoTheyWere()
    {
        var dal = Stock("DAL001", "Toor Dal 1kg", 100m);
        var lakshmi = Known("9876543210", "Lakshmi", points: 40);

        var first = Sell(lakshmi, (dal, 1m));
        var second = Sell(lakshmi, (dal, 2m));

        var unlinked = Customers.Forget(lakshmi.Id);

        Assert.Equal(2, unlinked);
        Assert.Null(Customers.FindByMobile("9876543210"));

        var invoices = new InvoiceRepository(_temp.Database);
        Assert.NotNull(invoices.FindByInvoiceNo(first.InvoiceNo));
        Assert.Null(invoices.FindByInvoiceNo(second.InvoiceNo)!.Sale.Customer);
    }

    [Fact]
    public void ForgettingSomebodyWhoIsNotOnFileSaysSo()
    {
        Assert.Equal(-1, Customers.Forget(12345));
    }

    /// <summary>A parked bill would otherwise bring them back the moment it was recalled.</summary>
    [Fact]
    public void ForgettingACustomerAlsoLetsGoOfTheirParkedBill()
    {
        var dal = Stock("DAL001", "Toor Dal 1kg", 100m);
        var lakshmi = Known("9876543210", "Lakshmi");

        var bill = new InvoiceEngine(HomeState);
        bill.AddItem(dal);
        bill.SetCustomer(lakshmi);

        var held = new HeldBillRepository(_temp.Database);
        var token = held.NextToken(Lane);
        held.Park(Lane, token, DateTimeOffset.Now, lakshmi, bill.SnapshotLines());

        Customers.Forget(lakshmi.Id);

        var recalled = held.Recall(Lane, token)!;
        Assert.Null(recalled.Customer);
    }

    // ---- Their history ---------------------------------------------------------------------------

    [Fact]
    public void AProfileCountsVisitsSpendAndTheAverageBasket()
    {
        var dal = Stock("DAL001", "Toor Dal 1kg", 100m);
        var oil = Stock("OIL001", "Groundnut Oil 1L", 200m);
        var lakshmi = Known("9876543210", "Lakshmi");

        Sell(lakshmi, (dal, 1m));
        Sell(lakshmi, (dal, 1m), (oil, 1m));

        var profile = new CustomerQuery(_temp.Database).Profile(lakshmi.Id)!;

        Assert.Equal(2, profile.Customer.Visits);
        Assert.Equal(400.00m, profile.Customer.Spent);
        Assert.Equal(200.00m, profile.AverageBasket);
        Assert.NotNull(profile.Customer.LastVisit);
    }

    [Fact]
    public void WhatTheyBuyIsRankedByWhatItCameTo()
    {
        var dal = Stock("DAL001", "Toor Dal 1kg", 100m);
        var oil = Stock("OIL001", "Groundnut Oil 1L", 200m);
        var lakshmi = Known("9876543210", "Lakshmi");

        Sell(lakshmi, (dal, 3m));
        Sell(lakshmi, (oil, 1m));

        var items = new CustomerQuery(_temp.Database).Profile(lakshmi.Id)!.TopItems;

        Assert.Equal("Toor Dal 1kg", items[0].Name);
        Assert.Equal(300.00m, items[0].Spent);
        Assert.Equal(3m, items[0].Quantity);
        Assert.Equal("Groundnut Oil 1L", items[1].Name);
    }

    /// <summary>
    /// A month they did not come in is a zero, not a gap. A regular who has stopped coming is the
    /// thing this chart is for, and it only shows if the empty months are drawn.
    /// </summary>
    [Fact]
    public void TheMonthlyChartHasEveryMonthIncludingTheEmptyOnes()
    {
        var dal = Stock("DAL001", "Toor Dal 1kg", 100m);
        var lakshmi = Known("9876543210", "Lakshmi");

        Sell(lakshmi, (dal, 1m));

        var months = new CustomerQuery(_temp.Database).Profile(lakshmi.Id, months: 12)!.Months;

        Assert.Equal(12, months.Count);
        Assert.Equal(100.00m, months[^1].Spent);
        Assert.All(months.Take(11), m => Assert.Equal(0m, m.Spent));
    }

    [Fact]
    public void ACancelledSaleIsNotSomethingTheyBought()
    {
        var dal = Stock("DAL001", "Toor Dal 1kg", 100m);
        var lakshmi = Known("9876543210", "Lakshmi");

        Sell(lakshmi, (dal, 1m));
        var mistake = Sell(lakshmi, (dal, 5m));
        Checkout().VoidSale(mistake.InvoiceNo, reason: "rung up twice");

        var profile = new CustomerQuery(_temp.Database).Profile(lakshmi.Id)!;

        Assert.Equal(1, profile.Customer.Visits);
        Assert.Equal(100.00m, profile.Customer.Spent);
        Assert.Single(profile.RecentBills);
    }

    /// <summary>
    /// The same bills, read two ways, have to come to the same figure. Both sum in exact paise with
    /// one shared helper, so they cannot drift apart.
    /// </summary>
    [Fact]
    public void ACustomersSpendAgreesWithTheShopsTakingsToThePaisa()
    {
        var sugar = Stock("SUG001", "Sugar Loose", 45m, UnitType.Kilogram);
        var lakshmi = Known("9876543210", "Lakshmi");

        Sell(lakshmi, (sugar, 1.25m));
        Sell(lakshmi, (sugar, 2.333m));

        var spent = new CustomerQuery(_temp.Database).Profile(lakshmi.Id)!.Customer.Spent;
        var takings = new DashboardQuery(_temp.Database)
            .Gather(Lane, DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddMinutes(1)).Range.NetSales;

        Assert.Equal(takings, spent);
    }

    [Fact]
    public void WithNothingTypedTheBestCustomersComeFirst()
    {
        var dal = Stock("DAL001", "Toor Dal 1kg", 100m);
        var small = Known("9000000001", "Occasional");
        var big = Known("9000000002", "Regular");

        Sell(small, (dal, 1m));
        Sell(big, (dal, 5m));

        var list = new CustomerQuery(_temp.Database).Find(null);

        Assert.Equal("Regular", list[0].Name);
    }

    // ---- At the till -----------------------------------------------------------------------------

    private BillingHarness Till() => new(
        Catalogue.Item(sku: "DAL001", barcode: "8901234567890", name: "Toor Dal 1kg", price: 189m));

    [Fact]
    public void TypingPartOfANameListsMatchesWithoutPickingOne()
    {
        using var till = Till();
        till.AddCustomer("9876543210", name: "Lakshmi");
        till.Scan("8901234567890");

        till.Press(Key.F7);
        till.ViewModel.EditBuffer = "Lak";

        Assert.Single(till.ViewModel.CustomerMatches);

        // Nothing highlighted until an arrow is pressed: a new number sharing digits with
        // somebody else's must not be committed onto their account by a reflexive Enter.
        Assert.Equal(-1, till.ViewModel.SelectedCustomerMatchIndex);
    }

    [Fact]
    public void AMatchIsPickedWithTheArrowAndAttachedWithCommit()
    {
        using var till = Till();
        till.AddCustomer("9876543210", loyaltyBalance: 120, name: "Lakshmi");
        till.Scan("8901234567890");

        till.Press(Key.F7);
        till.ViewModel.EditBuffer = "lak";
        till.Press(Key.Down);
        till.Press(Key.Enter);

        Assert.False(till.ViewModel.IsFindingCustomer);
        Assert.Equal("Lakshmi", till.ViewModel.CustomerLabel);
        Assert.Equal(120, till.ViewModel.LoyaltyBalance);
    }

    /// <summary>Letters are a search. Nobody is ever added under a name typed into the number box.</summary>
    [Fact]
    public void ANameWithNoMatchIsNotTakenAsANumberToAddSomebodyUnder()
    {
        using var till = Till();
        till.Scan("8901234567890");

        till.Press(Key.F7);
        till.ViewModel.EditBuffer = "Priya";
        till.Press(Key.Enter);
        till.Press(Key.Enter);

        Assert.True(till.ViewModel.IsFindingCustomer);
        Assert.False(till.ViewModel.IsNamingCustomer);
        Assert.Contains("mobile number", till.ViewModel.StatusMessage);
        Assert.Empty(till.Customers.Search("Priya"));
    }

    /// <summary>
    /// The confirmation belongs to the number it was given for. It used to be a bare flag, so
    /// correcting a mistyped number after the first commit added the correction unconfirmed.
    /// </summary>
    [Fact]
    public void ChangingTheNumberAfterConfirmingItAsksAgain()
    {
        using var till = Till();
        till.Scan("8901234567890");

        till.Press(Key.F7);
        till.ViewModel.EditBuffer = "9000000001";
        till.Press(Key.Enter);

        till.ViewModel.EditBuffer = "9000000002";
        till.Press(Key.Enter);

        Assert.False(till.ViewModel.IsNamingCustomer);
        Assert.Contains("Enter again to add", till.ViewModel.StatusMessage);
    }

    [Fact]
    public void EscapeAtTheNameAddsNobody()
    {
        using var till = Till();
        till.Scan("8901234567890");

        till.Press(Key.F7);
        till.ViewModel.EditBuffer = "9000000001";
        till.Press(Key.Enter);
        till.Press(Key.Enter);

        Assert.True(till.ViewModel.IsNamingCustomer);

        till.Press(Key.Escape);

        Assert.False(till.ViewModel.HasCustomer);
        Assert.Null(till.Customers.FindByMobile("9000000001"));
    }

    /// <summary>The next time they come in, typing the number brings the name back with the points.</summary>
    [Fact]
    public void ANameTakenOnceIsFilledInNextTime()
    {
        using var till = Till();
        till.Scan("8901234567890");

        till.Press(Key.F7);
        till.ViewModel.EditBuffer = "9000000001";
        till.Press(Key.Enter);
        till.Press(Key.Enter);
        till.ViewModel.EditBuffer = "Lakshmi";
        till.Press(Key.Enter);

        // A later visit: a fresh lookup by number alone.
        till.Press(Key.F7);
        till.ViewModel.EditBuffer = "9000000001";
        till.Press(Key.Enter);

        Assert.Equal("Lakshmi", till.ViewModel.CustomerLabel);
    }

    [Fact]
    public void TheNamePrintsOnTheBill()
    {
        using var till = Till();
        till.AddCustomer("9876543210", name: "Lakshmi");
        till.Scan("8901234567890");

        till.Press(Key.F7);
        till.ViewModel.EditBuffer = "9876543210";
        till.Press(Key.Enter);

        till.Press(Key.F12);
        till.Press(Key.Enter);
        till.Press(Key.Enter);

        var printed = Encoding.Latin1.GetString(till.Printer.LastJob);

        Assert.Contains("Lakshmi", printed);
        Assert.Contains("9876543210", printed);
    }

    // ---- The owner's screen ----------------------------------------------------------------------

    private CustomersViewModel Screen() => new(new CustomerQuery(_temp.Database), Customers);

    [Fact]
    public void TheScreenOpensOnTheBestCustomersAndLoadsOneWhenPicked()
    {
        var dal = Stock("DAL001", "Toor Dal 1kg", 100m);
        var lakshmi = Known("9876543210", "Lakshmi");
        Sell(lakshmi, (dal, 2m));

        var screen = Screen();
        screen.Search();

        Assert.Single(screen.Results);
        Assert.True(screen.ShowsHint);

        screen.Selected = screen.Results[0];

        Assert.True(screen.HasSelection);
        Assert.Equal("Lakshmi", screen.Title);
        Assert.Equal("200.00", screen.Spent);
        Assert.Equal(12, screen.Months.Count);
        Assert.Equal("Toor Dal 1kg", screen.TopItems[0].Name);
        Assert.Single(screen.RecentBills);
        Assert.Equal("In today.", screen.SinceLastVisit);
    }

    [Fact]
    public void SearchingNarrowsTheList()
    {
        Known("9876543210", "Lakshmi");
        Known("9123456780", "Ravi");

        var screen = Screen();
        screen.SearchText = "ravi";

        Assert.Single(screen.Results);
        Assert.Equal("Ravi", screen.Results[0].Name);
    }

    [Fact]
    public void ANameSavedHereIsTheNameTheTillUses()
    {
        Known("9876543210");

        var screen = Screen();
        screen.Search();
        screen.Selected = screen.Results[0];
        screen.EditName = "Lakshmi";

        Assert.Null(screen.SaveName());
        Assert.Equal("Lakshmi", Customers.FindByMobile("9876543210")!.Name);
        Assert.Equal("Lakshmi", screen.Title);
    }

    [Fact]
    public void AForgottenCustomerLeavesTheListAndTheScreen()
    {
        var dal = Stock("DAL001", "Toor Dal 1kg", 100m);
        var lakshmi = Known("9876543210", "Lakshmi");
        Sell(lakshmi, (dal, 1m));

        var screen = Screen();
        screen.Search();
        screen.Selected = screen.Results[0];

        Assert.Null(screen.Forget());

        Assert.Empty(screen.Results);
        Assert.False(screen.HasSelection);
        Assert.Contains("1 bill kept", screen.Status);
    }

    [Fact]
    public void ACustomerWhoHasNeverBoughtAnythingSaysSo()
    {
        Known("9876543210", "Lakshmi");

        var screen = Screen();
        screen.Search();
        screen.Selected = screen.Results[0];

        Assert.Equal("0", screen.Visits);
        Assert.Equal("Has not bought anything yet.", screen.SinceLastVisit);
        Assert.Empty(screen.TopItems);
    }
}
