using Pos.App.ViewModels;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// What the cashier is told about the shelf when an item is rung up.
/// </summary>
/// <remarks>
/// This is the only place a cashier finds out — they never open a report. It is appended to the
/// status line they already read rather than raised as a banner or a dialog, because none of it is
/// worth a keystroke: the sale goes through either way.
/// </remarks>
public class StockNoteTests
{
    private static Item Item(decimal? stock, decimal? reorder = null) =>
        Catalogue.Item(id: 1, name: "Bath Soap 100g") with { StockQty = stock, ReorderLevel = reorder };

    /// <summary>
    /// The case that matters most. An item nobody counts must produce no note at all — a shop
    /// weighing rice out of a sack would otherwise get a warning on every scan about a figure it
    /// never asked the software to keep.
    /// </summary>
    [Fact]
    public void AnUncountedItemSaysNothing()
    {
        Assert.Equal(string.Empty, BillingViewModel.StockNote(Item(stock: null)));
        Assert.Equal(string.Empty, BillingViewModel.StockNote(Item(stock: null, reorder: 5m)));
    }

    [Fact]
    public void PlentyOnTheShelfSaysNothing()
    {
        Assert.Equal(string.Empty, BillingViewModel.StockNote(Item(stock: 40m, reorder: 5m)));
    }

    [Fact]
    public void ACountedItemWithNoReorderLevelSaysNothingUntilItIsGone()
    {
        Assert.Equal(string.Empty, BillingViewModel.StockNote(Item(stock: 2m, reorder: null)));
        Assert.Contains("none left", BillingViewModel.StockNote(Item(stock: 0m, reorder: null)));
    }

    [Fact]
    public void RunningLowSaysHowManyAreLeft()
    {
        Assert.Contains("Only 3 left", BillingViewModel.StockNote(Item(stock: 3m, reorder: 5m)));
    }

    /// <summary>
    /// Out of stock says what the count says and that the sale is happening regardless, because
    /// the cashier is holding the item and does not need a debate about it.
    /// </summary>
    [Fact]
    public void NoneLeftSaysSoAndSaysTheSaleGoesAhead()
    {
        var note = BillingViewModel.StockNote(Item(stock: 0m, reorder: 5m));

        Assert.Contains("none left", note);
        Assert.Contains("selling anyway", note);
    }

    [Fact]
    public void ANegativeCountIsShownRatherThanHidden()
    {
        var note = BillingViewModel.StockNote(Item(stock: -2m, reorder: 5m));

        Assert.Contains("-2", note);
        Assert.Contains("selling anyway", note);
    }

    /// <summary>A weighed line reads as a weight, not as a whole number of things.</summary>
    [Fact]
    public void AFractionalCountReadsAsOne()
    {
        Assert.Contains("Only 1.5 left", BillingViewModel.StockNote(Item(stock: 1.5m, reorder: 5m)));
    }
}
