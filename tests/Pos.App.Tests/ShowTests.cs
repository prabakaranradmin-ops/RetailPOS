using System.Globalization;
using Pos.App.Views;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// Money, dates and times on the screens, written one way: ₹ and Indian grouping, "30 Sep 2026",
/// and 24-hour times - never "Sept", whatever the language setting.
/// </summary>
public class ShowTests
{
    [Theory]
    [InlineData(1149, "₹1,149.00")]
    [InlineData(123456.5, "₹1,23,456.50")]
    [InlineData(-0.25, "−₹0.25")]
    [InlineData(0, "₹0.00")]
    public void MoneyIsGroupedTheIndianWayWithTheRupeeSign(decimal value, string expected) =>
        Assert.Equal(expected, Show.Money(value));

    [Fact]
    public void AFigureHasNoSign() =>
        Assert.Equal("1,23,456.50", Show.Figure(123456.5m));

    [Fact]
    public void SeptemberIsSepAndTheDayHasNoLeadingNought()
    {
        var at = new DateTimeOffset(2026, 9, 4, 19, 5, 0, TimeSpan.FromHours(5.5));

        Assert.Equal("4 Sep 2026", Show.Date(at));
        Assert.Equal("4 Sep 2026", Show.Date(new DateOnly(2026, 9, 4)));
        Assert.Equal("19:05", Show.Time(at));
        Assert.Equal("4 Sep 2026, 19:05", Show.DateAndTime(at));
    }

    /// <summary>The same in a binding, on a window set to Indian English, which spells it "Sept".</summary>
    [Theory]
    [InlineData(null, "4 Sep 2026")]
    [InlineData("day", "4 Sep")]
    [InlineData("time", "19:05")]
    [InlineData("dateTime", "4 Sep 2026, 19:05")]
    [InlineData("dayTime", "4 Sep, 19:05")]
    public void TheDateConverterWritesItTheSameWay(string? parameter, string expected)
    {
        var at = new DateTimeOffset(2026, 9, 4, 19, 5, 0, TimeSpan.FromHours(5.5));

        Assert.Equal(expected, new ShowConverter().Convert(at, typeof(string), parameter, CultureInfo.GetCultureInfo("en-IN")));
    }

    [Fact]
    public void TheDateConverterTakesEveryKindOfDateAndNothing()
    {
        var converter = new ShowConverter();
        var culture = CultureInfo.GetCultureInfo("en-IN");

        Assert.Equal("4 Sep 2026", converter.Convert(new DateOnly(2026, 9, 4), typeof(string), null, culture));
        Assert.Equal("4 Sep 2026", converter.Convert(new DateTime(2026, 9, 4, 10, 0, 0), typeof(string), null, culture));
        Assert.Equal(string.Empty, converter.Convert(null, typeof(string), null, culture));
    }
}
