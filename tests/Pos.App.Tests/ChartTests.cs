using Pos.App.Charts;
using Pos.App.ViewModels;
using Pos.Core.Analytics;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// The owner's charts: round numbers on the axes, rupees the Indian way, and each chart plotting
/// what it says it plots.
/// </summary>
public class ChartTests
{
    // ---- Axes ------------------------------------------------------------------------------------

    /// <summary>The best day's 873.25 gives gridlines at 0, 200 ... 1,000 - not at 218.31.</summary>
    [Fact]
    public void AnAxisStepsInRoundNumbers()
    {
        var scale = NiceScale.For(0, 873.25);

        Assert.Equal(new NiceScale(0, 1000, 200), scale);
        Assert.Equal([0d, 200, 400, 600, 800, 1000], scale.Ticks());
    }

    /// <summary>A bar exactly at the top line would touch the frame: one more step of room.</summary>
    [Fact]
    public void DataOnTheTopLineGetsHeadroom() =>
        Assert.Equal(new NiceScale(0, 1000, 200), NiceScale.For(0, 800));

    /// <summary>A bar's height is its value, so a bar axis starts at zero unless told otherwise.</summary>
    [Fact]
    public void ZeroStaysOnTheAxis()
    {
        Assert.Equal(0, NiceScale.For(600, 800).Min);
        Assert.Equal(new NiceScale(600, 850, 50), NiceScale.For(600, 800, includeZero: false));
    }

    [Fact]
    public void NothingToShowStillGivesAnAxis()
    {
        Assert.Equal(new NiceScale(0, 1, 0.25), NiceScale.For(0, 0));
        Assert.Equal(new NiceScale(0, 1, 1), NiceScale.For(0, 0, wholeSteps: true));
        Assert.Equal(new NiceScale(0, 1, 0.25), NiceScale.For(double.NaN, double.PositiveInfinity));
    }

    /// <summary>Bills are counted whole: an axis of bills never has a line at half a bill.</summary>
    [Fact]
    public void ACountNeverStepsInFractions()
    {
        var scale = NiceScale.For(0, 2, wholeSteps: true);

        Assert.True(scale.Step >= 1, $"stepped by {scale.Step}");
        Assert.All(scale.Ticks(), t => Assert.Equal(Math.Round(t), t));
    }

    /// <summary>Ticks are counted, not added up, so a tenth added ten times still reaches the top.</summary>
    [Fact]
    public void SmallStepsDoNotDriftPastTheTop()
    {
        var ticks = new NiceScale(0, 1, 0.1).Ticks();

        Assert.Equal(11, ticks.Count);
        Assert.Equal(1.0, ticks[^1]);
    }

    // ---- Figures ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(123456.5, "₹1,23,456.50")]
    [InlineData(0, "₹0.00")]
    [InlineData(-12.5, "−₹12.50")]
    public void AFigureInATooltipIsWholeAndGroupedTheIndianWay(double value, string expected) =>
        Assert.Equal(expected, ChartFormat.Money(value));

    [Theory]
    [InlineData(123456, "₹1,23,456")]
    [InlineData(12.5, "₹12.50")]
    public void WholeRupeesDropThePaise(double value, string expected) =>
        Assert.Equal(expected, ChartFormat.MoneyShort(value));

    /// <summary>Thousands, lakhs and crores - the way a shopkeeper counts - never millions.</summary>
    [Theory]
    [InlineData(850, "₹850")]
    [InlineData(12500, "₹12.5K")]
    [InlineData(340000, "₹3.4L")]
    [InlineData(12000000, "₹1.2Cr")]
    [InlineData(0, "₹0")]
    [InlineData(-2000, "−₹2K")]
    public void AFigureOnAnAxisIsShort(double value, string expected) =>
        Assert.Equal(expected, ChartFormat.CompactMoney(value));

    [Theory]
    [InlineData(7, "7")]
    [InlineData(1250, "1,250")]
    [InlineData(12500, "12.5K")]
    public void CountsAreShortOnlyOnceTheyAreLong(double value, string expected) =>
        Assert.Equal(expected, ChartFormat.CompactCount(value));

    [Theory]
    [InlineData(15.54, "15.5%")]
    [InlineData(100, "100%")]
    public void SharesHaveOneDecimalAtMost(double value, string expected) =>
        Assert.Equal(expected, ChartFormat.Percent(value));

    // ---- What the owner's charts plot ------------------------------------------------------------

    private static IReadOnlyList<DailyPoint> Days(params decimal[] takings) =>
        takings.Select((t, i) => new DailyPoint(new DateOnly(2026, 9, 1).AddDays(i), i == 0 ? 1 : 3, t, i == 2 ? 15m : 0m)).ToList();

    /// <summary>The average of the day and the six before it; fewer at the start, where there are fewer.</summary>
    [Fact]
    public void TheTrendLineIsTheLastSevenDays()
    {
        var chart = OwnerCharts.SalesTrend(Days(100, 200, 300, 400, 500, 600, 700, 800, 900));
        var average = chart.Series[1].Values;

        Assert.Equal("Takings", chart.Series[0].Name);
        Assert.Equal([100d, 200, 300, 400, 500, 600, 700, 800, 900], chart.Series[0].Values);
        Assert.Equal(100, average[0]);
        Assert.Equal(150, average[1]);
        Assert.Equal(400, average[6]);
        Assert.Equal(600, average[8]);
        Assert.True(chart.Series[1].Dashed);
    }

    [Fact]
    public void EachDaySaysHowManyBillsAndWhatWasTakenOff()
    {
        var chart = OwnerCharts.SalesTrend(Days(100, 200, 300));

        Assert.Equal("01 Sep", chart.Labels[0]);
        Assert.Equal("Tue 1 Sep 2026", chart.Titles[0]);
        Assert.Equal("1 bill", chart.Notes![0]);
        Assert.Equal("3 bills · ₹15.00 off", chart.Notes[2]);
    }

    /// <summary>A first day with two sales still reads as a day, seven in the morning to nine at night.</summary>
    [Fact]
    public void TheHoursRunAtLeastFromSevenToNine()
    {
        var chart = OwnerCharts.Hours([new HourlyBucket(10, 2, 500m)]);

        Assert.Equal("07", chart.Labels[0]);
        Assert.Equal("21", chart.Labels[^1]);
        Assert.Equal(15, chart.Count);
        Assert.Equal(500, chart.Series[0].Values[3]);
        Assert.Equal(2, chart.Series[1].Values[3]);
        Assert.Equal(ChartAxis.Secondary, chart.Series[1].Axis);
        Assert.Equal("₹250.00 a bill", chart.Notes![3]);
        Assert.Equal("10:00 to 11:00", chart.Titles[3]);
    }

    [Fact]
    public void TheHoursWidenForAShopOpenLate()
    {
        var chart = OwnerCharts.Hours([new HourlyBucket(6, 1, 40m), new HourlyBucket(22, 1, 90m)]);

        Assert.Equal("06", chart.Labels[0]);
        Assert.Equal("22", chart.Labels[^1]);
        Assert.Equal("22:00 to 23:00", chart.Titles[^1]);
    }

    [Fact]
    public void TheWeekIsInTwoHourBandsMondayFirst()
    {
        var chart = OwnerCharts.Week(
        [
            new WeekdayHourCell(6, 18, 4, 1000m),
            new WeekdayHourCell(1, 10, 1, 100m),
        ]);

        Assert.Equal(["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"], chart.RowLabels);
        Assert.Equal(["08", "10", "12", "14", "16", "18", "20"], chart.ColumnLabels);

        var saturday = chart.At(5, 5);
        Assert.NotNull(saturday);
        Assert.Equal(1000, saturday!.Value);
        Assert.Equal("Saturday, 18:00 to 20:00", saturday.Title);
        Assert.Equal("4 bills", saturday.Detail);
        Assert.Equal(1000, chart.Max);
    }

    /// <summary>Each tender in the colour of where its money went, the biggest first, nothing for nothing.</summary>
    [Fact]
    public void TendersAreBiggestFirstInTheirOwnColours()
    {
        var chart = OwnerCharts.Tenders(
        [
            new TenderSlice("UPI", 4, 900m),
            new TenderSlice("Cash", 10, 1500m),
            new TenderSlice("Card", 0, 0m),
            new TenderSlice("Store credit", 1, 200m),
            new TenderSlice("Loyalty points", 1, 50m),
        ]);

        Assert.Equal(["Cash", "UPI", "Store credit", "Loyalty points"], chart.Slices.Select(s => s.Label));
        Assert.Equal([1, 0, 2, 4], chart.Slices.Select(s => s.Colour));
        Assert.Equal(2650, chart.Total);
        Assert.Equal("10 bills", chart.Slices[0].Detail);
    }

    /// <summary>
    /// The margin map's four boxes, split at the shop's own middle. An item exactly on the middle
    /// counts as the better side of it.
    /// </summary>
    [Fact]
    public void EachItemLandsInItsBoxOnTheMarginMap()
    {
        var chart = OwnerCharts.Margins(new MarginPicture(
            [
                new ItemPerformance("Star", "Grocery", 20m, 1000m, 700m, 30m),
                new ItemPerformance("Gem", "Grocery", 5m, 500m, 350m, 30m),
                new ItemPerformance("Weight", "Grocery", 5m, 100m, 90m, 10m),
                new ItemPerformance("Driver", "Grocery", 20m, 2000m, 1800m, 10m),
                new ItemPerformance("Middle", "Grocery", 10m, 300m, 240m, 20m),
            ],
            UnpricedItems: 0, UnpricedSales: 0m, MedianQuantity: 10m, MedianMargin: 20m));

        int GroupOf(string name) => chart.Points.Single(p => p.Label == name).Group;

        Assert.Equal("Stars", chart.Groups[GroupOf("Star")].Name);
        Assert.Equal("Hidden gems", chart.Groups[GroupOf("Gem")].Name);
        Assert.Equal("Dead weight", chart.Groups[GroupOf("Weight")].Name);
        Assert.Equal("Volume drivers", chart.Groups[GroupOf("Driver")].Name);
        Assert.Equal("Stars", chart.Groups[GroupOf("Middle")].Name);

        var star = chart.Points.Single(p => p.Label == "Star");
        Assert.Equal(["₹1,000.00 taken", "₹300.00 kept"], star.Lines);
        Assert.Equal(1000, star.Size);
    }

    // ---- The chart itself ------------------------------------------------------------------------

    private static CategoryChart Chart() => new()
    {
        Title = "Takings",
        Data = OwnerCharts.SalesTrend(Days(100, 200, 300, 400, 500, 600, 700, 800, 900, 1000)),
    };

    /// <summary>Zoomed in, never to fewer than three days: one bar on its own compares with nothing.</summary>
    [Fact]
    public void ZoomingInStopsAtThreeDays()
    {
        Wpf.Run(() =>
        {
            var chart = Chart();
            Assert.False(chart.IsZoomed);

            chart.ZoomTo(2, 3);

            Assert.Equal((2, 5), (chart.VisibleStart, chart.VisibleEnd));
            Assert.True(chart.IsZoomed);

            chart.ResetZoom();

            Assert.Equal((0, 10), (chart.VisibleStart, chart.VisibleEnd));
            Assert.False(chart.IsZoomed);
        });
    }

    /// <summary>Moving past the edge of a zoomed chart slides it along, so the day being read is on show.</summary>
    [Fact]
    public void ReadingPastTheEdgeOfAZoomSlidesItAlong()
    {
        Wpf.Run(() =>
        {
            var chart = Chart();
            chart.ZoomTo(2, 5);

            chart.MoveTo(8);

            Assert.Equal(8, chart.ActiveIndex);
            Assert.Equal((6, 9), (chart.VisibleStart, chart.VisibleEnd));
        });
    }

    /// <summary>What a screen reader is given for the day the keyboard is on: the date, every figure, the note.</summary>
    [Fact]
    public void TheDayBeingReadIsDescribedInFull()
    {
        Wpf.Run(() =>
        {
            var chart = Chart();

            chart.MoveTo(8);

            Assert.Equal("Wed 9 Sep 2026: Takings ₹900.00, 7-day average ₹600.00. 3 bills.", chart.ActiveDescription);

            chart.ToggleSeries(1);

            Assert.False(chart.IsSeriesShown(1));
            Assert.Equal("Wed 9 Sep 2026: Takings ₹900.00. 3 bills.", chart.ActiveDescription);

            chart.ToggleSeries(1);

            Assert.True(chart.IsSeriesShown(1));
        });
    }

    [Fact]
    public void TheSummaryNamesTheBestDay()
    {
        Wpf.Run(() =>
        {
            var chart = Chart();

            Assert.Contains("Highest takings: ₹1,000.00, Thu 10 Sep 2026.", chart.Summary);
        });
    }

    /// <summary>The table under the chart: every day, every series, whether shown or not.</summary>
    [Fact]
    public void TheChartReadsAsATable()
    {
        Wpf.Run(() =>
        {
            var chart = Chart();
            chart.ToggleSeries(1);

            var table = chart.ToTable();

            Assert.Equal(["Day", "Takings", "7-day average"], table.Columns.Cast<System.Data.DataColumn>().Select(c => c.ColumnName));
            Assert.Equal(10, table.Rows.Count);
            Assert.Equal("Tue 1 Sep 2026", table.Rows[0][0]);
            Assert.Equal("₹100.00", table.Rows[0][1]);
            Assert.Equal("₹150.00", table.Rows[1][2]);
        });
    }

    /// <summary>New figures start the chart afresh: not zoomed, every series back on.</summary>
    [Fact]
    public void NewFiguresStartTheChartAfresh()
    {
        Wpf.Run(() =>
        {
            var chart = Chart();
            chart.ZoomTo(2, 5);
            chart.ToggleSeries(0);

            chart.Data = OwnerCharts.SalesTrend(Days(5, 6, 7, 8));

            Assert.False(chart.IsZoomed);
            Assert.True(chart.IsSeriesShown(0));
            Assert.Equal((0, 4), (chart.VisibleStart, chart.VisibleEnd));
        });
    }
}
