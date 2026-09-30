using Pos.App.ViewModels;
using Pos.Core.Analytics;
using Pos.Core.Configuration;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// The figures the owner's screen was computing on every refresh and throwing away: what the shop
/// earned, the day-by-day trend, cancelled sales, who is buying, and what loyalty still owes.
/// </summary>
/// <remarks>
/// None of this is new analysis — <see cref="DashboardQuery"/> already gathered all of it in the
/// same pass. These pin the presentation, and in particular the honesty of it: a margin built on
/// part of the catalogue has to say so, and a shop that has never entered a cost price has to be
/// told that rather than shown a profit of zero.
/// </remarks>
public class OwnerAnalyticsTests : IDisposable
{
    private const string Lane = "L1";
    private const string HomeState = "33";

    private readonly TempDatabase _temp = new();

    public void Dispose() => _temp.Dispose();

    private ItemRepository Items => new(_temp.Database);

    private OwnerViewModel Build() => new(
        Lane,
        days =>
        {
            var to = DateTimeOffset.Now;
            var from = new DateTimeOffset(to.Date.AddDays(-(days - 1)), to.Offset);

            return new DashboardQuery(_temp.Database).Gather(Lane, from, to);
        },
        new StockRepository(_temp.Database),
        TaxMode.Gst,
        isPinSet: false,
        applyTaxMode: _ => null,
        applyPin: _ => null);

    /// <summary>Adds one item, with or without a cost price.</summary>
    private Item Add(string sku, string name, decimal price, decimal? cost, decimal gstRate = 5m)
    {
        Items.UpsertRange([Catalogue.Item(sku: sku, name: name, price: price, gstRate: gstRate) with
        {
            CostPrice = cost,
        }]);

        return Items.FindBySku(sku)!;
    }

    private CheckoutService Checkout() => new(
        new InvoiceRepository(_temp.Database),
        new CustomerRepository(_temp.Database),
        new RecordingDrawerService());

    /// <summary>Rings up and settles one sale, optionally against a known customer.</summary>
    private SettledInvoice Sell(Item item, decimal quantity = 1m, Customer? customer = null)
    {
        var bill = new InvoiceEngine(HomeState);
        bill.AddItem(item);

        if (quantity != 1m)
            bill.SetQuantity(0, quantity);

        if (customer is not null)
            bill.SetCustomer(customer);

        var total = bill.Totals.GrandTotal;
        var basket = new TenderBasket(total);
        basket.Add(TenderType.Cash, total);

        return Checkout().Complete(Lane, bill, basket).Invoice;
    }

    private Customer Known(string mobile)
    {
        var customers = new CustomerRepository(_temp.Database);
        customers.Add(new Customer { MobileNo = mobile, Name = $"Customer {mobile}" });
        return customers.FindByMobile(mobile)!;
    }

    // ---- What the shop earned --------------------------------------------------------------------

    [Fact]
    public void ProfitAndMarginAreShownWhenTheCatalogueCarriesCostPrices()
    {
        // Sells for 100, cost 60. The margin is worked on what the shop charged.
        Sell(Add("DAL001", "Toor Dal 1kg", price: 100m, cost: 60m));

        var screen = Build();
        screen.Refresh();

        Assert.True(screen.HasMargins);
        Assert.NotEqual("—", screen.PeriodProfit);
        Assert.NotEqual("—", screen.PeriodMargin);
        Assert.EndsWith("%", screen.PeriodMargin, StringComparison.Ordinal);
    }

    /// <summary>
    /// A shop that has never entered a cost price must be told so. Showing a profit of zero would
    /// have it conclude it earned nothing, which is a different and much worse statement.
    /// </summary>
    [Fact]
    public void AShopWithNoCostPricesIsToldWhyRatherThanShownZero()
    {
        Sell(Add("DAL001", "Toor Dal 1kg", price: 100m, cost: null));

        var screen = Build();
        screen.Refresh();

        Assert.False(screen.HasMargins);
        Assert.Equal("—", screen.PeriodProfit);
        Assert.Equal("—", screen.PeriodMargin);
        Assert.Contains("cost price", screen.MarginCoverage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0.00", screen.PeriodProfit);
    }

    /// <summary>
    /// The coverage line is the whole point of putting margins on this screen: a figure built on
    /// half the shop that does not say so is worse than no figure at all.
    /// </summary>
    [Fact]
    public void APartlyPricedCatalogueSaysHowMuchOfTheShopTheMarginCovers()
    {
        Sell(Add("DAL001", "Toor Dal 1kg", price: 100m, cost: 60m));
        Sell(Add("OIL001", "Groundnut Oil 1L", price: 200m, cost: null));

        var screen = Build();
        screen.Refresh();

        Assert.True(screen.HasMargins);
        Assert.Contains("%", screen.MarginCoverage, StringComparison.Ordinal);
        Assert.Contains("no cost price", screen.MarginCoverage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AFullyPricedCatalogueSaysItCoversEverything()
    {
        Sell(Add("DAL001", "Toor Dal 1kg", price: 100m, cost: 60m));

        var screen = Build();
        screen.Refresh();

        Assert.Equal("Covers every item sold in this period.", screen.MarginCoverage);
    }

    /// <summary>
    /// The departments add up the lines; net sales are the bills as paid, after their round-off.
    /// Where the two differ the chart says by how much, instead of showing an owner a second total
    /// for the same days - "covers all 873.25" beside net sales of 873.00.
    /// </summary>
    [Fact]
    public void ARoundOffBetweenTheLinesAndTheBillsIsExplainedNotLeftAsASecondTotal()
    {
        var dal = Add("DAL001", "Toor Dal 1kg", price: 100.25m, cost: 60m);
        var bill = new InvoiceEngine(HomeState, roundToRupee: true);
        bill.AddItem(dal);

        var basket = new TenderBasket(bill.Totals.AmountPayable);
        basket.Add(TenderType.Cash, bill.Totals.AmountPayable);
        Checkout().Complete(Lane, bill, basket);

        var screen = Build();
        screen.Refresh();

        Assert.Equal("Item values, before the bills' round-off of −₹0.25: the bills came to ₹100.00.", screen.DepartmentNote);
        Assert.DoesNotContain("100.25", screen.MarginCoverage);
    }

    [Fact]
    public void WithNoRoundOffTheDepartmentsNeedNoNote()
    {
        Sell(Add("DAL001", "Toor Dal 1kg", price: 100m, cost: 60m));

        var screen = Build();
        screen.Refresh();

        Assert.Equal(string.Empty, screen.DepartmentNote);
    }

    /// <summary>
    /// Ranked by rupees earned rather than by percentage. A wide margin on something that barely
    /// sells earns the shop less than a thin one on its staple, and ordering by percentage puts the
    /// wrong item at the top of an owner's attention.
    /// </summary>
    [Fact]
    public void TheBestEarnerIsTheOneThatEarnedMostNotTheWidestMargin()
    {
        // 80% margin, sold once: earns 80.
        Sell(Add("SPICE1", "Saffron 1g", price: 100m, cost: 20m));

        // 20% margin, sold ten times: earns 200.
        Sell(Add("DAL001", "Toor Dal 1kg", price: 100m, cost: 80m), quantity: 10m);

        var screen = Build();
        screen.Refresh();

        Assert.NotEmpty(screen.BestMargins);
        Assert.Equal("Toor Dal 1kg", screen.BestMargins[0].Name);
    }

    [Fact]
    public void TheWorstEarnerIsListedSoAPriceRevisionHasSomewhereToStart()
    {
        Sell(Add("DAL001", "Toor Dal 1kg", price: 100m, cost: 20m));
        Sell(Add("SUG001", "Sugar 1kg", price: 100m, cost: 98m));

        var screen = Build();
        screen.Refresh();

        Assert.NotEmpty(screen.WorstMargins);
        Assert.Equal("Sugar 1kg", screen.WorstMargins[0].Name);
    }

    // ---- The headline figure that was always computed and never shown ----------------------------

    [Fact]
    public void TheAverageBasketIsShown()
    {
        var item = Add("DAL001", "Toor Dal 1kg", price: 100m, cost: 60m);
        Sell(item);
        Sell(item);

        var screen = Build();
        screen.Refresh();

        // Two bills at 100 each: the basket is 100, not 200.
        Assert.Equal("100.00", screen.PeriodAverageBasket);
    }

    // ---- The trend -------------------------------------------------------------------------------

    [Fact]
    public void TheDayByDayTrendHasABarForEachDayThatTraded()
    {
        Sell(Add("DAL001", "Toor Dal 1kg", price: 100m, cost: 60m));

        var screen = Build();
        screen.Refresh();

        Assert.NotEmpty(screen.Daily);

        // The tallest day is full height, so the chart always fills its box.
        Assert.Equal(1d, screen.Daily.Max(b => b.Fraction), 3);
    }

    [Fact]
    public void ATrendBarSaysWhatItWasMadeOf()
    {
        Sell(Add("DAL001", "Toor Dal 1kg", price: 100m, cost: 60m));

        var screen = Build();
        screen.Refresh();

        Assert.Matches(@"\d bills?\b", screen.Daily[^1].Tooltip);
    }

    /// <summary>
    /// The series is dense: a day the shop did not trade is a zero in the trend, not a gap. A
    /// chart that skipped quiet days would put Monday next to Thursday and read as a steady week.
    /// </summary>
    [Fact]
    public void ADayWithNoSalesIsAZeroInTheTrendRatherThanAGap()
    {
        Sell(Add("DAL001", "Toor Dal 1kg", price: 100m, cost: 60m));

        var screen = Build();
        screen.Refresh();

        // Thirty days asked for, thirty bars back, one of which has today's takings in it.
        Assert.Equal(30, screen.Daily.Count);
        Assert.Single(screen.Daily, b => b.Amount > 0m);
    }

    [Fact]
    public void ALaneThatHasSoldNothingDrawsAFlatTrendRatherThanFailing()
    {
        var screen = Build();
        screen.Refresh();

        Assert.Equal(30, screen.Daily.Count);
        Assert.All(screen.Daily, b => Assert.Equal(0d, b.Fraction));
    }

    // ---- Cancelled sales -------------------------------------------------------------------------

    [Fact]
    public void ALaneWithNoCancellationsSaysSoPlainly()
    {
        Sell(Add("DAL001", "Toor Dal 1kg", price: 100m, cost: 60m));

        var screen = Build();
        screen.Refresh();

        Assert.False(screen.HasVoids);
        Assert.Contains("No sale was cancelled", screen.VoidLine, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ACancelledSaleIsCountedAndValued()
    {
        var invoice = Sell(Add("DAL001", "Toor Dal 1kg", price: 100m, cost: 60m));

        Checkout().VoidSale(invoice.InvoiceNo, reason: "rung up twice");

        var screen = Build();
        screen.Refresh();

        Assert.True(screen.HasVoids);
        Assert.Contains("1 sale cancelled", screen.VoidLine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("% of bills", screen.VoidLine, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Who is buying ---------------------------------------------------------------------------

    [Fact]
    public void WalkInsAndKnownCustomersAreToldApart()
    {
        var item = Add("DAL001", "Toor Dal 1kg", price: 100m, cost: 60m);

        Sell(item);
        Sell(item, customer: Known("9876543210"));

        var screen = Build();
        screen.Refresh();

        Assert.Contains("1 of 2 bills", screen.CustomerLine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("walk-in", screen.CustomerLine, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ACustomerWhoCameBackIsCountedAsReturning()
    {
        var item = Add("DAL001", "Toor Dal 1kg", price: 100m, cost: 60m);
        var regular = Known("9876543210");

        Sell(item, customer: regular);
        Sell(item, customer: regular);

        var screen = Build();
        screen.Refresh();

        Assert.Contains("came back", screen.ReturningLine, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ALaneThatIdentifiesNobodySaysThereIsNothingToTell()
    {
        Sell(Add("DAL001", "Toor Dal 1kg", price: 100m, cost: 60m));

        var screen = Build();
        screen.Refresh();

        Assert.Contains("nothing to tell", screen.ReturningLine, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Loyalty, and what it owes ---------------------------------------------------------------

    /// <summary>
    /// Points outstanding are a liability, not a performance figure: every one of them can be spent
    /// against a future bill, and a shop that has never seen the total has no idea what it promised.
    /// </summary>
    [Fact]
    public void ThePointsTheShopStillOwesAreShown()
    {
        Sell(Add("DAL001", "Toor Dal 1kg", price: 100m, cost: 60m), customer: Known("9876543210"));

        var screen = Build();
        screen.Refresh();

        Assert.NotNull(screen.PointsOwed);
        Assert.NotEmpty(screen.PointsLine);
    }

    [Fact]
    public void ALaneRunningNoLoyaltySchemeSaysSo()
    {
        Sell(Add("DAL001", "Toor Dal 1kg", price: 100m, cost: 60m));

        var screen = Build();
        screen.Refresh();

        Assert.Equal("0", screen.PointsOwed);
        Assert.Contains("No points", screen.PointsLine, StringComparison.OrdinalIgnoreCase);
    }

    // ---- The screen must not take the till down --------------------------------------------------

    [Fact]
    public void EverythingSurvivesALaneThatHasNeverTraded()
    {
        var screen = Build();
        screen.Refresh();

        Assert.Equal(string.Empty, screen.Status);
        Assert.False(screen.HasMargins);
        Assert.False(screen.HasVoids);
        Assert.NotEmpty(screen.CustomerLine);
        Assert.NotEmpty(screen.MarginCoverage);
    }

    // ---- A bill that was parked before it was paid ------------------------------------------------

    /// <summary>
    /// A sale that spent time parked is still a sale.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>invoices.hold_token</c> changed meaning in migration 003. It was designed to mark a row as
    /// a parked bill; when parked bills moved to their own table it was kept to record which parked
    /// bill a <em>settled</em> invoice was recalled from. The dashboard went on reading it the old
    /// way, as "not a sale", so every bill that was parked and then paid for disappeared from the
    /// owner's figures — while the day-end report, which never used the column, counted it.
    /// </para>
    /// <para>
    /// Found by the acceptance run, whose walkthrough parks and recalls its only sale: the
    /// Maintenance tab listed a close of one bill for 495.00, and the figures tab beside it said the
    /// lane had sold nothing. Parking is ordinary at a counter — a customer goes back for something
    /// they forgot — so this was not a corner case.
    /// </para>
    /// </remarks>
    [Fact]
    public void ABillThatWasParkedAndRecalledStillCountsAsASale()
    {
        var item = Add("DAL001", "Toor Dal 1kg", price: 100m, cost: 60m);

        var bill = new InvoiceEngine(HomeState);
        bill.AddItem(item);

        var basket = new TenderBasket(bill.Totals.GrandTotal);
        basket.Add(TenderType.Cash, bill.Totals.GrandTotal);

        // What a recalled bill carries into checkout: the token it was parked under.
        new CheckoutService(
                new InvoiceRepository(_temp.Database),
                new CustomerRepository(_temp.Database),
                new RecordingDrawerService())
            .Complete(Lane, bill, basket, recalledFromToken: "H007");

        var screen = Build();
        screen.Refresh();

        Assert.Equal("1", screen.PeriodBills);
        Assert.Equal("100.00", screen.PeriodNetSales);
        Assert.Equal("1", screen.TodayBills);
        Assert.True(screen.HasMargins, "A recalled sale's cost has to reach the margin figures too.");
    }

    /// <summary>A sale rung up today shows under today, not only under the period.</summary>
    [Fact]
    public void ASaleRungUpTodayShowsUnderToday()
    {
        Sell(Add("DAL001", "Toor Dal 1kg", price: 100m, cost: 60m));

        var screen = Build();
        screen.Refresh();

        Assert.Equal("1", screen.TodayBills);
        Assert.Equal("100.00", screen.TodayNetSales);
    }

    // ---- The bug this feature uncovered ----------------------------------------------------------

    /// <summary>
    /// The cost and the category have to survive being settled.
    /// </summary>
    /// <remarks>
    /// <see cref="InvoiceEngine.SnapshotLines"/> clones every line on its way into the sale, and
    /// <see cref="InvoiceLine.Clone"/> was missing both fields — so every sale ever settled through
    /// the till stored a null cost and a null category. Nothing looked broken: the bill, the tax and
    /// the receipt were all correct. Only the figures built on them were empty, and an empty margin
    /// reads exactly like a shop that has not entered its cost prices.
    /// </remarks>
    [Fact]
    public void TheCostAndCategorySurviveBeingSettled()
    {
        var item = Add("DAL001", "Toor Dal 1kg", price: 100m, cost: 60m);

        var bill = new InvoiceEngine(HomeState);
        var engineLine = bill.AddItem(item);

        Assert.Equal(60m, engineLine.CostSnapshot);

        var basket = new TenderBasket(bill.Totals.GrandTotal);
        basket.Add(TenderType.Cash, bill.Totals.GrandTotal);

        var settled = Checkout().Complete(Lane, bill, basket).Invoice;

        Assert.Equal(60m, settled.Sale.Lines[0].CostSnapshot);
    }

    /// <summary>
    /// The same loss filed every sale the shop ever made under "Uncategorised", which made the
    /// department chart on the owner's screen say nothing at all.
    /// </summary>
    [Fact]
    public void ASaleIsFiledUnderItsOwnDepartment()
    {
        var items = Items;

        items.UpsertRange([Catalogue.Item(sku: "DAL001", name: "Toor Dal 1kg", price: 100m) with
        {
            Category = "Staples",
            CostPrice = 60m,
        }]);

        Sell(items.FindBySku("DAL001")!);

        var screen = Build();
        screen.Refresh();

        Assert.Contains(screen.Categories, c => c.Name == "Staples");
    }
}
