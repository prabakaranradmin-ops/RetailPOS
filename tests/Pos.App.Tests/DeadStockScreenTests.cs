using Pos.App.ViewModels;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>The Stock tab's "Not selling" list, as the owner sees it.</summary>
public class DeadStockScreenTests : IDisposable
{
    private readonly TempDatabase _temp = new();

    public void Dispose() => _temp.Dispose();

    private OwnerViewModel Owner(Func<IReadOnlyList<DeadStockItem>>? dead) => new(
        "L1",
        _ => throw new InvalidOperationException("not read in this test"),
        new StockRepository(_temp.Database),
        TaxMode.Gst,
        isPinSet: false,
        applyTaxMode: _ => null,
        applyPin: _ => null,
        deadStock: dead);

    private static DeadStockItem Item(string sku, decimal have, int? days, decimal? cost) =>
        new(1, sku, $"Item {sku}", "Household", UnitType.Each, have, days is { } d ? DateOnly.FromDateTime(DateTime.Today).AddDays(-d) : null, days, cost);

    [Fact]
    public void TheListSaysHowMuchIsTiedUp()
    {
        var owner = Owner(() => [Item("SOAP", 11m, 75, 30m), Item("OLD", 5m, null, 40m)]);

        owner.ShowDead = true;

        Assert.False(owner.ShowsStockList);
        Assert.Equal(2, owner.DeadStockItems.Count);
        Assert.Contains("2 items on the shelf not sold in 60 days, 1 never sold", owner.ListHeadline);
        Assert.Contains("530.00 tied up in them at cost", owner.ListHeadline);
    }

    [Fact]
    public void NothingDeadIsSaid()
    {
        var owner = Owner(() => []);

        owner.ShowDead = true;

        Assert.Contains("Everything counted on the shelf has sold", owner.ListHeadline);
    }

    [Fact]
    public void BackToTheCountsShowsTheCounts()
    {
        var owner = Owner(() => []);
        owner.ShowDead = true;

        owner.ShowDead = false;

        Assert.True(owner.ShowsStockList);
    }

    [Fact]
    public void ALaneWithoutItDoesNotOfferIt() => Assert.False(Owner(null).CanShowDead);
}
