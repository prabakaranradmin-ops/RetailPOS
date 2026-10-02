using Pos.Core.Analytics;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// The owner's exceptions report: voids, discounts typed by hand, cash refunds, cash out and refused
/// PINs over the window, totalled by who was on the till, and the latest one by one.
/// </summary>
public class ExceptionsReportTests : IDisposable
{
    private const string Lane = "L1";

    private readonly TempDatabase _temp = new();
    private readonly DateTimeOffset _today = new(2026, 10, 2, 0, 0, 0, TimeSpan.FromHours(5.5));

    public void Dispose() => _temp.Dispose();

    private TillEventRepository Events => new(_temp.Database);

    private DashboardData Gather(int days = 30) =>
        new DashboardQuery(_temp.Database).Gather(Lane, _today.AddDays(-days), _today.AddDays(1));

    private void Record(int hour, TillEventKind kind, string? cashier, decimal? amount = null, string? reference = null, bool? approved = null, string lane = Lane, int day = 0) =>
        Events.Record(lane, _today.AddDays(day).AddHours(hour), kind, cashier, reference, amount, approved);

    [Fact]
    public void ALaneThatHasRecordedNothingSaysSoRatherThanNothingHappened()
    {
        var x = Gather().Exceptions;

        Assert.False(x.Any);
        Assert.Null(x.RecordedSince);
        Assert.Empty(x.Latest);
    }

    [Fact]
    public void EachKindIsTotalledByWhoWasOnTheTillToThePaisa()
    {
        Record(9, TillEventKind.Voided, "Murugan", 189.50m, "RM/26-27/L1-1");
        Record(10, TillEventKind.Voided, "Murugan", 40.25m, "RM/26-27/L1-2", approved: true);
        Record(11, TillEventKind.Discounted, "Murugan", 30.10m, "Toor Dal 1kg");
        Record(12, TillEventKind.CashRefunded, "Lakshmi", 100m, "CN/26-27/T1-1");
        Record(13, TillEventKind.CashTakenOut, "Lakshmi", 1000m, "Cash out");
        Record(14, TillEventKind.ApprovalRefused, "Lakshmi", 500m, approved: false);
        Record(15, TillEventKind.SignOnRefused, "Lakshmi");

        var x = Gather().Exceptions;

        Assert.Equal(2, x.ByCashier.Count);

        // Most first: Lakshmi has four, Murugan three.
        var lakshmi = x.ByCashier[0];
        Assert.Equal("Lakshmi", lakshmi.Cashier);
        Assert.Equal(1, lakshmi.CashRefunds);
        Assert.Equal(100m, lakshmi.CashRefunded);
        Assert.Equal(1, lakshmi.CashOuts);
        Assert.Equal(1000m, lakshmi.CashTakenOut);
        Assert.Equal(2, lakshmi.Refused);
        Assert.Equal(4, lakshmi.Total);

        var murugan = x.ByCashier[1];
        Assert.Equal(2, murugan.Voids);
        Assert.Equal(229.75m, murugan.Voided);
        Assert.Equal(1, murugan.Discounts);
        Assert.Equal(30.10m, murugan.Discounted);
    }

    /// <summary>A sign-on and a close are the day going as it should, and are not exceptions.</summary>
    [Fact]
    public void ASignOnAndACloseAreNotExceptions()
    {
        Record(8, TillEventKind.SignedOn, "Murugan");
        Record(21, TillEventKind.DayClosed, "Murugan", 12_000m);

        var x = Gather().Exceptions;

        Assert.False(x.Any);
        Assert.NotNull(x.RecordedSince);
    }

    [Fact]
    public void SomethingDoneWithNobodyOnTheTillIsPutAgainstNobody()
    {
        Record(9, TillEventKind.Voided, null, 50m);

        Assert.Equal(TillExceptions.Nobody, Assert.Single(Gather().Exceptions.ByCashier).Cashier);
    }

    [Fact]
    public void OnlyThisLaneAndThisWindowAreCounted()
    {
        Record(9, TillEventKind.Voided, "Murugan", 50m);
        Record(9, TillEventKind.Voided, "Murugan", 70m, lane: "L2");
        Record(9, TillEventKind.Voided, "Murugan", 90m, day: -40);

        var murugan = Assert.Single(Gather(days: 30).Exceptions.ByCashier);

        Assert.Equal(1, murugan.Voids);
        Assert.Equal(50m, murugan.Voided);
    }

    /// <summary>The record began forty days ago on this lane, though nothing in the window: it says when.</summary>
    [Fact]
    public void TheRecordSaysWhenItBegan()
    {
        Record(9, TillEventKind.Voided, "Murugan", 90m, day: -40);
        Record(9, TillEventKind.Voided, "Murugan", 50m, day: -2);

        Assert.Equal(_today.AddDays(-40).AddHours(9), Gather().Exceptions.RecordedSince);
    }

    [Fact]
    public void TheLatestAreListedNewestFirstAndNoMoreThanFifty()
    {
        for (var i = 0; i < DashboardQuery.LatestExceptions + 10; i++)
            Events.Record(Lane, _today.AddMinutes(i), TillEventKind.Discounted, "Murugan", "Toor Dal 1kg", 5m);

        var x = Gather().Exceptions;

        Assert.Equal(DashboardQuery.LatestExceptions, x.Latest.Count);
        Assert.Equal(_today.AddMinutes(DashboardQuery.LatestExceptions + 9), x.Latest[0].At);

        // The totals count every one, not only those listed.
        Assert.Equal(DashboardQuery.LatestExceptions + 10, x.ByCashier[0].Discounts);
    }

    [Fact]
    public void TheSavedPageCarriesTheSameTable()
    {
        Record(9, TillEventKind.Voided, "Murugan", 189.50m);

        var page = DashboardPage.Render(Gather(), "Sri Lakshmi Stores");

        Assert.Contains("At the till, by who", page);
        Assert.Contains("Murugan", page);
        Assert.Contains("189.50", page);
    }

    [Fact]
    public void ThePageLeavesTheTableOutUntilAnythingIsRecorded() =>
        Assert.DoesNotContain("At the till, by who", DashboardPage.Render(Gather(), "Sri Lakshmi Stores"));
}
