using Pos.Core.Analytics;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// A festival against the same festival last year, counted from the day itself: Deepavali on
/// 20 October 2025 and on 8 November 2026 line up day for day though they are weeks apart.
/// </summary>
public class FestivalComparisonTests : IDisposable
{
    private const string Lane = "L1";

    private static readonly DateOnly ThisDeepavali = new(2026, 11, 8);
    private static readonly DateOnly LastDeepavali = new(2025, 10, 20);

    private readonly TempDatabase _temp = new();

    public void Dispose() => _temp.Dispose();

    private sealed class At(DateTimeOffset moment) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => moment.ToUniversalTime();
    }

    private Item Stock(string sku, string name, decimal price, string category)
    {
        _temp.Items.UpsertRange([Catalogue.Item(sku: sku, name: name, price: price, gstRate: 0m) with { Category = category }]);
        return _temp.Items.FindBySku(sku)!;
    }

    private Item Sweets => Stock("LADDU", "Motichoor Laddu 500g", 200m, "Sweets");
    private Item Ghee => Stock("GHEE", "Ghee 500ml", 300m, "Dairy");
    private Item Crackers => Stock("CRACK", "Sparklers Box", 100m, "Seasonal");

    private void Sell(DateOnly day, int hour, params Item[] items)
    {
        var bill = new InvoiceEngine("33");

        foreach (var item in items)
            bill.AddItem(item);

        var basket = new TenderBasket(bill.Totals.AmountPayable);
        basket.Add(TenderType.Cash, bill.Totals.AmountPayable);

        var at = new DateTimeOffset(day.ToDateTime(new TimeOnly(hour, 0)), TimeSpan.FromHours(5.5));
        new CheckoutService(new InvoiceRepository(_temp.Database), new CustomerRepository(_temp.Database), new RecordingDrawerService(), clock: new At(at))
            .Complete(Lane, bill, basket);
    }

    private DashboardData Gather(FestivalWindow window)
    {
        static DateTimeOffset Midnight(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5.5));
        return new DashboardQuery(_temp.Database).Gather(Lane, Midnight(window.First), Midnight(window.Last.AddDays(1)), FestivalComparison.ItemsRead);
    }

    private FestivalComparison Compare(int before = 2, int after = 1)
    {
        var thisYear = new FestivalWindow(ThisDeepavali, before, after);
        var lastYear = new FestivalWindow(LastDeepavali, before, after);
        return FestivalComparison.Of("Deepavali", thisYear, Gather(thisYear), lastYear, Gather(lastYear));
    }

    [Fact]
    public void EachDayIsSetBesideTheSameDayFromLastYearsFestival()
    {
        Sell(ThisDeepavali.AddDays(-1), 18, Sweets, Sweets);   // ₹400 the eve, this year
        Sell(ThisDeepavali, 10, Ghee);                          // ₹300 the day
        Sell(LastDeepavali.AddDays(-1), 18, Sweets);            // ₹200 the eve, last year
        Sell(LastDeepavali.AddDays(1), 11, Crackers);           // ₹100 the day after

        var festival = Compare();

        Assert.Equal(
        [
            new FestivalDay(-2, new DateOnly(2026, 11, 6), new DateOnly(2025, 10, 18), 0m, 0m, 0, 0),
            new FestivalDay(-1, new DateOnly(2026, 11, 7), new DateOnly(2025, 10, 19), 400m, 200m, 1, 1),
            new FestivalDay(0, new DateOnly(2026, 11, 8), new DateOnly(2025, 10, 20), 300m, 0m, 1, 0),
            new FestivalDay(1, new DateOnly(2026, 11, 9), new DateOnly(2025, 10, 21), 0m, 100m, 0, 1),
        ], festival.Days);
    }

    [Fact]
    public void TheWindowsTotalsAreEachYearsOwn()
    {
        Sell(ThisDeepavali.AddDays(-1), 18, Sweets, Sweets);
        Sell(ThisDeepavali, 10, Ghee);
        Sell(LastDeepavali.AddDays(-1), 18, Sweets);

        // A sale outside either window is in neither.
        Sell(ThisDeepavali.AddDays(-3), 10, Ghee);
        Sell(LastDeepavali.AddDays(2), 10, Ghee);

        var festival = Compare();

        Assert.Equal(700m, festival.This.NetSales);
        Assert.Equal(2, festival.This.Bills);
        Assert.Equal(200m, festival.Last.NetSales);
        Assert.Equal(1, festival.Last.Bills);
        Assert.True(festival.HasAnything);
    }

    [Fact]
    public void DepartmentsFromEitherYearAreSetSideBySideMostThisYearFirst()
    {
        Sell(ThisDeepavali, 10, Sweets, Ghee);
        Sell(LastDeepavali, 10, Sweets, Sweets, Crackers);

        Assert.Equal(
        [
            new FestivalCategory("Dairy", 300m, 0m),
            new FestivalCategory("Sweets", 200m, 400m),
            new FestivalCategory("Seasonal", 0m, 100m),
        ], Compare().Categories);
    }

    [Fact]
    public void WhatSoldThisTimeCarriesLastTimesFiguresAndWhatDidNotSellIsListed()
    {
        Sell(ThisDeepavali, 10, Sweets, Ghee);
        Sell(LastDeepavali, 10, Sweets, Sweets, Crackers);

        var festival = Compare();

        Assert.Equal(
        [
            new FestivalItem("Ghee 500ml", "pc", 1m, 0m, 300m, 0m),
            new FestivalItem("Motichoor Laddu 500g", "pc", 1m, 2m, 200m, 400m),
        ], festival.Items);

        // The sparklers sold last Deepavali and not at all this one: was there a box on the shelf?
        Assert.Equal([new FestivalItem("Sparklers Box", "pc", 0m, 1m, 0m, 100m)], festival.NotThisYear);
    }

    [Fact]
    public void NoSalesInEitherYearIsNothing()
    {
        var festival = Compare();

        Assert.False(festival.HasAnything);
        Assert.All(festival.Days, d => Assert.Equal(0m, d.ThisSales + d.LastSales));
    }

    [Theory]
    [InlineData(700, 200, 250.0)]
    [InlineData(200, 700, -71.4)]
    [InlineData(300, 300, 0.0)]
    [InlineData(105, 100, 5.0)]
    public void TheChangeIsAShareOfLastYearToOnePlace(int now, int then, double change) =>
        Assert.Equal((decimal)change, FestivalComparison.Change(now, then));

    [Fact]
    public void NothingLastYearHasNoShareToSpeakOf() => Assert.Null(FestivalComparison.Change(300m, 0m));

    [Fact]
    public void TheTwoYearsMustBeTheSameDaysEitherSide()
    {
        var thisYear = new FestivalWindow(ThisDeepavali, 2, 1);
        var lastYear = new FestivalWindow(LastDeepavali, 3, 1);

        Assert.Throws<ArgumentException>(() => FestivalComparison.Of("Deepavali", thisYear, Gather(thisYear), lastYear, Gather(lastYear)));
    }

    [Fact]
    public void AWindowIsTheDaysBeforeTheDayAndTheDaysAfter()
    {
        var window = new FestivalWindow(ThisDeepavali, 7, 1);

        Assert.Equal(new DateOnly(2026, 11, 1), window.First);
        Assert.Equal(new DateOnly(2026, 11, 9), window.Last);
        Assert.Equal(9, window.Days);
        Assert.Null(window.Problem());
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    [InlineData(46, 0)]
    [InlineData(0, 46)]
    public void AWindowThatCannotBeIsRefused(int before, int after) =>
        Assert.NotNull(new FestivalWindow(ThisDeepavali, before, after).Problem());
}
