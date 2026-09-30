using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Domain.Printing;
using Pos.TestSupport;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// Use-by dates: which deliveries are near theirs and probably still on the shelf, worked out from
/// the count with the newest deliveries on the shelf first - and where the shop is told.
/// </summary>
public class ExpiryTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 9, 29);

    private readonly TempDatabase _temp = new();

    public void Dispose() => _temp.Dispose();

    private static Expiry.Delivery Came(int daysAgo, decimal quantity, int? expiresIn, string batch = "B1") =>
        new(batch, expiresIn is { } days ? Today.AddDays(days) : null, Today.AddDays(-daysAgo), "Murugan Traders", quantity);

    private static List<ExpiryWarning> Warn(decimal? have, params Expiry.Delivery[] newestFirst) =>
        [.. Expiry.For(1, "MILK", "Aavin Milk 500ml", UnitType.Each, have, newestFirst, Today)];

    // ---- What is probably on the shelf -----------------------------------------------------------

    /// <summary>
    /// Fifteen on the shelf; three deliveries of ten. The newest ten are on the shelf, then five of the
    /// middle one. The oldest has been sold, as far as the count can tell, expired or not.
    /// </summary>
    [Fact]
    public void TheCountIsLaidOnTheNewestDeliveriesFirst()
    {
        var warning = Assert.Single(Warn(15m, Came(2, 10m, 60), Came(20, 10m, 5), Came(40, 10m, -3)));

        Assert.Equal(5m, warning.LikelyOnShelf);
        Assert.Equal(5, warning.DaysLeft);
        Assert.False(warning.IsExpired);
        Assert.Equal("sell it first, or return it to the supplier", warning.Advice);
    }

    [Fact]
    public void AnExpiredDeliveryTheCountReachesIsSaid()
    {
        var warnings = Warn(25m, Came(2, 10m, 60), Came(20, 10m, 5), Came(40, 10m, -3));

        Assert.Equal(2, warnings.Count);

        var expired = warnings.Single(w => w.IsExpired);
        Assert.Equal(5m, expired.LikelyOnShelf);
        Assert.Equal(-3, expired.DaysLeft);
        Assert.Equal("expired - take it off the shelf", expired.Advice);
    }

    /// <summary>An undated delivery still takes its share of the count, or an older dated one would look as if it were on the shelf.</summary>
    [Fact]
    public void AnUndatedDeliveryTakesItsShareOfTheCount()
    {
        Assert.Empty(Warn(10m, Came(2, 10m, null), Came(20, 10m, 5)));
    }

    [Fact]
    public void ADateMoreThanAMonthAwayIsNotYetWorthSaying()
    {
        Assert.Empty(Warn(10m, Came(2, 10m, Expiry.WarnDays + 1)));
        Assert.Single(Warn(10m, Came(2, 10m, Expiry.WarnDays)));
    }

    [Fact]
    public void AShelfCountedBelowNothingHasNothingOnIt() =>
        Assert.Empty(Warn(-4m, Came(2, 10m, 3)));

    /// <summary>Not counted: said while the date is recent, so a delivery from two years ago is not warned about for ever.</summary>
    [Fact]
    public void AnUncountedItemIsWarnedAboutRecentDatesOnly()
    {
        var warnings = Warn(null, Came(5, 10m, 20), Came(30, 10m, -10), Came(90, 10m, -40));

        Assert.Equal([20, -10], warnings.Select(w => w.DaysLeft));
        Assert.All(warnings, w => Assert.Null(w.LikelyOnShelf));
    }

    [Fact]
    public void TheCashierIsToldOnlyAboutAPassedDateOrToday()
    {
        Assert.Equal("Check the date: a delivery of this expired on 26 Sep.", Expiry.CheckNote(Warn(25m, Came(2, 10m, 60), Came(20, 10m, 5), Came(40, 10m, -3))));
        Assert.Equal("Check the date: a delivery of this expires today.", Expiry.CheckNote(Warn(10m, Came(2, 10m, 0))));
        Assert.Null(Expiry.CheckNote(Warn(10m, Came(2, 10m, 5))));
    }

    // ---- From the books --------------------------------------------------------------------------

    private Supplier Supplier() =>
        new PurchaseRepository(_temp.Database).AddSupplier(new Supplier(0, "Murugan Traders", "9443012345", "33AEIPH7795F1Z9", "33", true));

    private long Buy(Supplier supplier, string billNo, Item item, decimal quantity, DateOnly received, DateOnly? expires, string? batch = null)
    {
        var line = PurchaseLine.Price(new PurchaseLineEntry(item, quantity, 20m, 5m, 0m, batch, expires), interState: false, chargesGst: true);
        return new PurchaseRepository(_temp.Database)
            .Record(new PurchaseBill(supplier, billNo, received, [line], InterState: false), "L1", DateTimeOffset.Now, null).PurchaseId;
    }

    private Item Milk(decimal? stock)
    {
        _temp.Items.UpsertRange([Catalogue.Item(sku: "MILK", name: "Aavin Milk 500ml", price: 24m) with { StockQty = stock }]);
        return _temp.Items.FindBySku("MILK")!;
    }

    [Fact]
    public void TheRepositoryReadsTheDatesOffThePurchaseBills()
    {
        var murugan = Supplier();
        var milk = Milk(0m);
        Buy(murugan, "MT/1", milk, 10m, Today.AddDays(-3), Today.AddDays(-1), "A17");
        Buy(murugan, "MT/2", milk, 10m, Today.AddDays(-1), Today.AddDays(6), "A19");

        // Twenty delivered; twelve left means eight sold, so all of the newer ten and two of the older.
        new StockRepository(_temp.Database).Set(milk.Id, 12m, StockReason.Count, "L1");

        var warnings = new ExpiryRepository(_temp.Database).Expiring(Today);

        Assert.Equal(2, warnings.Count);
        Assert.Equal("A17", warnings[0].BatchNo);
        Assert.True(warnings[0].IsExpired);
        Assert.Equal(2m, warnings[0].LikelyOnShelf);
        Assert.Equal("Murugan Traders", warnings[0].Supplier);
        Assert.Equal(10m, warnings[1].LikelyOnShelf);

        Assert.Equal(2, new ExpiryRepository(_temp.Database).ExpiringFor(milk.Id, Today).Count);
    }

    /// <summary>Once the old stock is off the shelf and the count corrected, it drops off the list.</summary>
    [Fact]
    public void CorrectingTheCountTakesTheOldDeliveryOffTheList()
    {
        var murugan = Supplier();
        var milk = Milk(0m);
        Buy(murugan, "MT/1", milk, 10m, Today.AddDays(-3), Today.AddDays(-1));
        Buy(murugan, "MT/2", milk, 10m, Today.AddDays(-1), Today.AddDays(6));

        new StockRepository(_temp.Database).Set(milk.Id, 8m, StockReason.Adjust, "L1", "expired milk thrown away");

        var warning = Assert.Single(new ExpiryRepository(_temp.Database).Expiring(Today));
        Assert.False(warning.IsExpired);
    }

    [Fact]
    public void ACancelledBillIsNotADelivery()
    {
        var murugan = Supplier();
        var milk = Milk(0m);
        var wrong = Buy(murugan, "MT/1", milk, 10m, Today.AddDays(-3), Today.AddDays(-1));
        new PurchaseRepository(_temp.Database).Void(wrong, "entered twice", "L1", DateTimeOffset.Now);

        Assert.Empty(new ExpiryRepository(_temp.Database).Expiring(Today));
    }

    // ---- The day-end report ----------------------------------------------------------------------

    [Fact]
    public void TheDayEndReportSaysWhichDatesToCheck()
    {
        // A day that sold something: like the reorder list, the dates are at the foot of a day's
        // takings, and a day with none prints only its drawer.
        var day = new DayCloseRepository(_temp.Database).Preview("L1", DateTimeOffset.Now) with { InvoiceCount = 1 };
        var dates = Warn(25m, Came(2, 10m, 60), Came(20, 10m, 5), Came(40, 10m, -3));

        var text = new ZReportComposer(new StoreProfile { Name = "Sri Murugan Stores" }).Compose(day, dates: dates).ToPlainText();

        Assert.Contains("CHECK THE DATES", text);
        Assert.Contains("EXPIRED 26-09", text);
        Assert.Contains("04-10", text);
    }
}
