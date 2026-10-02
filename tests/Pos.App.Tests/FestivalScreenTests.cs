using Pos.App.ViewModels;
using Pos.Core.Analytics;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// A festival against last year's on the owner's figures tab: the days typed, the comparison in
/// words and day by day, and each thing that cannot be compared said.
/// </summary>
public class FestivalScreenTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 11, 20);

    private readonly TempDatabase _temp = new();

    public void Dispose() => _temp.Dispose();

    private sealed class At(DateTimeOffset moment) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => moment.ToUniversalTime();
    }

    private OwnerViewModel Owner(bool festivals = true)
    {
        var owner = new OwnerViewModel(
            "L1",
            _ => throw new InvalidOperationException("not read in this test"),
            new StockRepository(_temp.Database),
            TaxMode.Gst,
            isPinSet: false,
            applyTaxMode: _ => null,
            applyPin: _ => null);

        if (festivals)
            owner.UseFestivals((from, to, items) => new DashboardQuery(_temp.Database).Gather("L1", from, to, items), Today);

        return owner;
    }

    private void Sell(DateOnly day, decimal price, string name = "Motichoor Laddu 500g")
    {
        var sku = $"S{Math.Abs(name.GetHashCode()) % 100_000}";
        _temp.Items.UpsertRange([Catalogue.Item(sku: sku, name: name, price: price, gstRate: 0m) with { Category = "Sweets" }]);

        var bill = new InvoiceEngine("33");
        bill.AddItem(_temp.Items.FindBySku(sku)!);
        var basket = new TenderBasket(bill.Totals.AmountPayable);
        basket.Add(TenderType.Cash, bill.Totals.AmountPayable);

        new CheckoutService(new InvoiceRepository(_temp.Database), new CustomerRepository(_temp.Database), new RecordingDrawerService(),
            clock: new At(new DateTimeOffset(day.ToDateTime(new TimeOnly(11, 0)), TimeSpan.FromHours(5.5)))).Complete("L1", bill, basket);
    }

    private static void Deepavali(OwnerViewModel owner)
    {
        owner.FestivalName = "Deepavali";
        owner.FestivalDay = "08-11-2026";
        owner.FestivalLastDay = "20-10-2025";
        owner.FestivalDaysBefore = "2";
        owner.FestivalDaysAfter = "1";
    }

    [Fact]
    public void ItOpensOnTodayAgainstTheSameDateLastYear()
    {
        var owner = Owner();

        Assert.True(owner.CanCompareFestivals);
        Assert.Equal("20-11-2026", owner.FestivalDay);
        Assert.Equal("20-11-2025", owner.FestivalLastDay);
        Assert.Equal("7", owner.FestivalDaysBefore);
        Assert.Equal("1", owner.FestivalDaysAfter);
    }

    [Fact]
    public void ATypedDayOffersTheSameDateLastYearForAFestivalThatDoesNotMove()
    {
        var owner = Owner();

        owner.FestivalDay = "14-01-2026";

        Assert.Equal("14-01-2025", owner.FestivalLastDay);
    }

    [Fact]
    public void TheComparisonIsSaidInALineAndLaidOutDayByDay()
    {
        Sell(new DateOnly(2026, 11, 7), 400m);
        Sell(new DateOnly(2026, 11, 8), 300m, "Ghee 500ml");
        Sell(new DateOnly(2025, 10, 19), 200m);

        var owner = Owner();
        Deepavali(owner);

        Assert.True(owner.CompareFestival());

        Assert.True(owner.HasFestival);
        Assert.Equal(string.Empty, owner.FestivalStatus);
        Assert.Equal(
            "Deepavali 2026, 2 days before to 1 day after: ₹700.00 against ₹200.00 in 2025, up 250.0%. "
            + "2 bills against 1, up 100.0%; a basket of ₹350.00 against ₹200.00.",
            owner.FestivalLine);

        Assert.Equal(
        [
            new FestivalDayRow("2 days before", "Fri 6 Nov", "₹0.00", "Sat 18 Oct", "₹0.00", "—"),
            new FestivalDayRow("1 day before", "Sat 7 Nov", "₹400.00", "Sun 19 Oct", "₹200.00", "+100.0%"),
            new FestivalDayRow("The day itself", "Sun 8 Nov", "₹300.00", "Mon 20 Oct", "₹0.00", "new"),
            new FestivalDayRow("1 day after", "Mon 9 Nov", "₹0.00", "Tue 21 Oct", "₹0.00", "—"),
        ], owner.FestivalDays);

        Assert.Equal([new FestivalCompareRow("Sweets", "₹700.00", "₹200.00", "+250.0%")], owner.FestivalDepartments);
        Assert.Contains(owner.FestivalItems, i => i.Name == "Ghee 500ml" && i.LastYear == "none" && i.Change == "new");
        Assert.NotNull(owner.FestivalChart);
        Assert.Equal(["2026", "2025"], owner.FestivalChart!.Series.Select(s => s.Name));
        Assert.Equal(["−2", "−1", "Day", "+1"], owner.FestivalChart.Labels);
    }

    [Fact]
    public void WhatSoldLastTimeAndNotThisTimeIsListed()
    {
        Sell(new DateOnly(2026, 11, 8), 300m, "Ghee 500ml");
        Sell(new DateOnly(2025, 10, 20), 100m, "Sparklers Box");

        var owner = Owner();
        Deepavali(owner);
        owner.CompareFestival();

        Assert.True(owner.HasFestivalMissing);
        Assert.Equal([new FestivalCompareRow("Sparklers Box", "none", "1 pc · ₹100.00", "−100%")], owner.FestivalMissing);
    }

    [Fact]
    public void AFestivalStillGoingIsSaidToBeSo()
    {
        Sell(new DateOnly(2026, 11, 19), 400m);
        Sell(new DateOnly(2025, 11, 19), 200m);

        var owner = Owner();
        owner.FestivalDay = "20-11-2026";
        owner.FestivalDaysBefore = "1";
        owner.FestivalDaysAfter = "2";

        Assert.True(owner.CompareFestival());
        Assert.Equal("This year's days run to 22 Nov 2026: the figures will grow until then.", owner.FestivalStatus);
    }

    [Fact]
    public void NoSalesEitherYearIsSaid()
    {
        var owner = Owner();
        Deepavali(owner);

        Assert.False(owner.CompareFestival());
        Assert.False(owner.HasFestival);
        Assert.Equal("No sales on these days in either year.", owner.FestivalStatus);
    }

    [Theory]
    [InlineData("8th Nov", "20-10-2025", "2", "1", "'8th Nov' is not a date. Type the festival day as 08-11-2026.")]
    [InlineData("08-11-2026", "last year", "2", "1", "'last year' is not a date. Type last year's festival day as 20-10-2025.")]
    [InlineData("08-11-2026", "20-10-2025", "two", "1", "Days before and after are whole numbers: 7 before, 1 after.")]
    [InlineData("08-11-2026", "20-10-2025", "60", "1", "At most 45 days either side of the day.")]
    [InlineData("08-11-2026", "07-11-2026", "2", "1", "Last year's days have to end before this year's begin. Check the two dates.")]
    [InlineData("25-12-2026", "25-12-2025", "2", "1", "The days from 23 Dec 2026 have not come yet.")]
    public void WhatCannotBeComparedIsSaidAndNothingIsShown(string day, string last, string before, string after, string said)
    {
        Sell(new DateOnly(2026, 11, 8), 300m);

        var owner = Owner();
        owner.FestivalDay = day;
        owner.FestivalLastDay = last;
        owner.FestivalDaysBefore = before;
        owner.FestivalDaysAfter = after;

        Assert.False(owner.CompareFestival());
        Assert.Equal(said, owner.FestivalStatus);
        Assert.False(owner.HasFestival);
        Assert.Empty(owner.FestivalDays);
    }

    [Fact]
    public void AScreenWithoutTheFiguresOffersNone()
    {
        var owner = Owner(festivals: false);

        Assert.False(owner.CanCompareFestivals);
        Assert.False(owner.CompareFestival());
        Assert.Equal("This screen cannot compare festivals.", owner.FestivalStatus);
    }

    [Theory]
    [InlineData(0, true, "Day")]
    [InlineData(-3, true, "−3")]
    [InlineData(2, true, "+2")]
    [InlineData(0, false, "The day itself")]
    [InlineData(-1, false, "1 day before")]
    [InlineData(3, false, "3 days after")]
    public void EachDayIsNamedFromTheFestival(int offset, bool shortForm, string label) =>
        Assert.Equal(label, OwnerCharts.FestivalDayLabel(offset, shortForm));
}
