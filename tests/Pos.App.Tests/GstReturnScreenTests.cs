using Pos.App.ViewModels;
using Pos.Core.Analytics;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// The owner's GST tab: which month it opens on, where it stops, and what it says when there is
/// nothing to file or the books cannot be read.
/// </summary>
public class GstReturnScreenTests
{
    private static readonly DateOnly Today = new(2026, 10, 14);

    private readonly List<DateOnly> _asked = [];
    private readonly List<(GstReturnData Data, string Path)> _saved = [];
    private Exception? _failWith;

    private GstReturnViewModel Screen(bool withSales = true) => new(
        month =>
        {
            _asked.Add(month);

            if (_failWith is not null)
                throw _failWith;

            return Month(month, withSales);
        },
        (data, path) =>
        {
            _saved.Add((data, path));
            return [path, path + "-b2cs.csv", path + "-hsn.csv"];
        },
        Today);

    private static GstReturnData Month(DateOnly month, bool withSales) => new()
    {
        LaneId = "T1",
        Month = month,
        OutletStateCode = "33",
        GeneratedAt = DateTimeOffset.Now,
        RateWise = withSales ? [new GstRateRow("33", 5m, 700m, 17.5m, 17.5m, 0m)] : [],
        LargeInterState = [],
        NilIntraState = withSales ? 135m : 0m,
        NilInterState = 0m,
        Hsn = withSales ? [new GstHsnRow("0713", "Toor Dal 1kg", "PCS-PIECES", 5m, 3m, 735m, 700m, 0m, 17.5m, 17.5m)] : [],
        Documents = withSales ? [new GstDocumentSeries("RM/26-27/T1-1", "RM/26-27/T1-3", 3, 1)] : [],
        TaxInvoices = withSales ? 2 : 0,
        BillsOfSupply = 0,
        BillsOfSupplyValue = 0m,
        Warnings = withSales ? ["Everything sold at 0% is listed as nil rated."] : [],
    };

    /// <summary>A return is filed for a month that has finished.</summary>
    [Fact]
    public void ItOpensOnLastMonth()
    {
        var screen = Screen();
        screen.Load();

        Assert.Equal(new DateOnly(2026, 9, 1), screen.Month);
        Assert.Equal("September 2026", screen.MonthLabel);
        Assert.Equal("A finished month.", screen.MonthNote);
    }

    [Fact]
    public void ItShowsWhatTheMonthCameTo()
    {
        var screen = Screen();
        screen.Load();

        Assert.Equal("700.00", screen.TaxableValue);
        Assert.Equal("17.50", screen.Cgst);
        Assert.Equal("35.00", screen.Tax);
        Assert.Equal("135.00", screen.NilRated);
        Assert.Equal("2", screen.TaxInvoices);
        Assert.Single(screen.RateWise);
        Assert.Single(screen.Hsn);
        Assert.Single(screen.Documents);
        Assert.True(screen.HasWarnings);
        Assert.False(screen.HasIgst);
        Assert.True(screen.CanSave);
        Assert.Equal("gst-T1-2026-09.html", screen.SuggestedFileName);
    }

    /// <summary>The current month can be looked at, and says it is not over; there is no month after it.</summary>
    [Fact]
    public void ItGoesNoLaterThanThisMonth()
    {
        var screen = Screen();
        screen.Load();

        Assert.True(screen.CanGoLater);
        screen.LaterMonth();

        Assert.Equal(new DateOnly(2026, 10, 1), screen.Month);
        Assert.Contains("not over", screen.MonthNote);
        Assert.False(screen.CanGoLater);

        screen.LaterMonth();
        Assert.Equal(new DateOnly(2026, 10, 1), screen.Month);
    }

    [Fact]
    public void EarlierMonthsCanBeReadBackAcrossTheYear()
    {
        var screen = Screen();
        screen.Load();

        for (var i = 0; i < 9; i++)
            screen.EarlierMonth();

        Assert.Equal(new DateOnly(2025, 12, 1), screen.Month);
        Assert.Equal(new DateOnly(2025, 12, 1), _asked[^1]);
    }

    [Fact]
    public void SavingWritesTheMonthOnScreen()
    {
        var screen = Screen();
        screen.Load();

        Assert.Null(screen.Save(@"C:\accounts\gst-T1-2026-09.html"));

        var (data, path) = Assert.Single(_saved);
        Assert.Equal(new DateOnly(2026, 9, 1), data.Month);
        Assert.Equal(@"C:\accounts\gst-T1-2026-09.html", path);
        Assert.Contains("Saved 3 files", screen.Status);
        Assert.Contains("keep them private", screen.Status);
    }

    [Fact]
    public void AMonthWithNothingInItSaysSoAndSavesNothing()
    {
        var screen = Screen(withSales: false);
        screen.Load();

        Assert.True(screen.IsEmpty);
        Assert.False(screen.CanSave);
        Assert.NotNull(screen.Save(@"C:\accounts\empty.html"));
        Assert.Empty(_saved);
    }

    /// <summary>The owner's screen must not take the till down with it.</summary>
    [Fact]
    public void BooksThatCannotBeReadAreAMessageNotACrash()
    {
        _failWith = new InvalidOperationException("the database is busy");

        var screen = Screen();
        screen.Load();

        Assert.Contains("the database is busy", screen.Status);
        Assert.False(screen.IsLoaded);
        Assert.False(screen.CanSave);
        Assert.Empty(screen.Hsn);
    }
}
