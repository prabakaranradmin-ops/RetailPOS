using Pos.Core.Analytics;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// The drawer at each close in the owner's figures: counted, over or short, and how it came out on
/// each person's days.
/// </summary>
public class DrawerTrendTests : IDisposable
{
    private const string Lane = "L1";

    private readonly TempDatabase _temp = new();

    public void Dispose() => _temp.Dispose();

    private DayCloseRepository Closes => new(_temp.Database, new HeldBillRepository(_temp.Database));

    private DashboardData Gather() =>
        new DashboardQuery(_temp.Database).Gather(Lane, DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));

    /// <summary>One ₹189 dal, by whoever is named, paid the way given.</summary>
    private void Sell(string cashier, TenderType tender = TenderType.Cash)
    {
        if (_temp.Items.FindBySku("DAL001") is null)
            _temp.Items.AddRange([Catalogue.Item(sku: "DAL001", barcode: "8901234567890", name: "Toor Dal 1kg", price: 189m, gstRate: 5m)]);

        var bill = new InvoiceEngine("33");
        bill.AddItem(_temp.Items.FindBySku("DAL001")!);

        var basket = new TenderBasket(bill.Totals.GrandTotal);
        basket.Add(tender, bill.Totals.GrandTotal);

        new CheckoutService(
            new InvoiceRepository(_temp.Database),
            new CustomerRepository(_temp.Database),
            new RecordingDrawerService(),
            cashier: () => cashier).Complete(Lane, bill, basket);
    }

    [Fact]
    public void AWindowWithNoClosesHasNothingToShow()
    {
        var drawers = Gather().Drawers;

        Assert.Empty(drawers.Closes);
        Assert.Empty(drawers.ByPerson);
    }

    /// <summary>
    /// Whoever took cash or moved it through the drawer was on it. Somebody who only took UPI never
    /// touched the drawer, and is not counted for it.
    /// </summary>
    [Fact]
    public void OnTheTillIsWhoeverTouchedTheDrawer()
    {
        Sell("Murugan");
        Sell("Lakshmi", TenderType.Upi);
        new CashDrawerRepository(_temp.Database).Record(new DrawerEntry(DrawerEntryKind.CashIn, 500m), Lane, DateTimeOffset.Now, "Selvi");

        Closes.Close(Lane, DateTimeOffset.Now, cashCounted: 689m, countedBy: "Murugan");

        var close = Assert.Single(Gather().Drawers.Closes);
        Assert.Equal(["Murugan", "Selvi"], close.OnTheTill);
        Assert.Equal(689m, close.Expected);
        Assert.Equal(0m, close.Difference);
        Assert.Equal("Murugan", close.CountedBy);
    }

    [Fact]
    public void EachPersonsDaysAreTotalledAndASharedDayCountsForBoth()
    {
        // Day one: Murugan alone, short by 9.
        Sell("Murugan");
        Closes.Close(Lane, DateTimeOffset.Now.AddMinutes(-20), cashCounted: 180m, countedBy: "Murugan");

        // Day two: Murugan and Lakshmi, over by 11.
        Sell("Murugan");
        Sell("Lakshmi");
        Closes.Close(Lane, DateTimeOffset.Now.AddMinutes(-10), cashCounted: 389m, countedBy: "Lakshmi");

        // Day three: Lakshmi alone, not counted.
        Sell("Lakshmi");
        Closes.Close(Lane, DateTimeOffset.Now.AddMinutes(-1));

        var drawers = Gather().Drawers;

        // Oldest first, as the chart reads.
        Assert.Equal([-9m, 11m, null], drawers.Closes.Select(c => c.Difference));
        Assert.Equal(2, drawers.CountedCloses);
        Assert.Equal(1, drawers.ShortCloses);
        Assert.Equal(9m, drawers.ShortTotal);
        Assert.Equal(1, drawers.OverCloses);
        Assert.Equal(11m, drawers.OverTotal);

        // Most short first.
        var murugan = drawers.ByPerson[0];
        Assert.Equal("Murugan", murugan.Name);
        Assert.Equal(2, murugan.Days);
        Assert.Equal(2, murugan.Counted);
        Assert.Equal(1, murugan.ShortDays);
        Assert.Equal(9m, murugan.Short);
        Assert.Equal(1, murugan.OverDays);
        Assert.Equal(11m, murugan.Over);

        var lakshmi = drawers.ByPerson[1];
        Assert.Equal(2, lakshmi.Days);
        Assert.Equal(1, lakshmi.Counted);
        Assert.Equal(0, lakshmi.ShortDays);
        Assert.Equal(1, lakshmi.OverDays);
    }

    [Fact]
    public void TheSavedPageCarriesTheDrawers()
    {
        Sell("Murugan");
        Closes.Close(Lane, DateTimeOffset.Now, cashCounted: 180m, countedBy: "Murugan");

        var page = DashboardPage.Render(Gather(), "Sri Lakshmi Stores");

        Assert.Contains("The drawer at closing", page);
        Assert.Contains("Counted at 1 of 1 closes", page);
        Assert.Contains("Murugan", page);
    }
}
