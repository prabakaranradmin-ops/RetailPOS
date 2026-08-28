using Pos.App.ViewModels;
using Pos.Core.Analytics;
using Pos.Core.Configuration;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// The owner's screen itself: the figures, the reorder list, correcting a count, and the two
/// settings an owner can change without opening a text editor.
/// </summary>
public class OwnerViewModelTests : IDisposable
{
    private const string Lane = "L1";

    private readonly TempDatabase _temp = new();

    private TaxMode _mode = TaxMode.Gst;
    private string? _refuseTaxModeWith;
    private PinCredential? _pin;

    public void Dispose() => _temp.Dispose();

    private StockRepository Stock => new(_temp.Database);

    private Item Stocked(string sku, decimal? qty, decimal? reorder = null)
    {
        var items = new ItemRepository(_temp.Database);

        items.UpsertRange([Catalogue.Item(sku: sku, name: $"Item {sku}") with
        {
            StockQty = qty,
            ReorderLevel = reorder,
        }]);

        return items.FindBySku(sku)!;
    }

    private OwnerViewModel Build() => new(
        Lane,
        days =>
        {
            var to = DateTimeOffset.Now;
            var from = new DateTimeOffset(to.Date.AddDays(-(days - 1)), to.Offset);

            return new DashboardQuery(_temp.Database).Gather(Lane, from, to);
        },
        Stock,
        _mode,
        isPinSet: _pin is not null,
        applyTaxMode: mode =>
        {
            if (_refuseTaxModeWith is not null)
                return _refuseTaxModeWith;

            _mode = mode;
            return null;
        },
        applyPin: credential =>
        {
            _pin = credential;
            return null;
        });

    // ---- The reorder list ------------------------------------------------------------------------

    /// <summary>
    /// An empty list has two very different meanings, and saying which is the whole point. "Nothing
    /// is low" is reassuring; "nothing is counted" means the shop has not set stock up at all, and
    /// reading the second as the first is how somebody concludes their shelves are fine.
    /// </summary>
    [Fact]
    public void AnEmptyListSaysWhichKindOfEmptyItIs()
    {
        var owner = Build();
        owner.Refresh();

        Assert.Contains("No item in this catalogue is counted", owner.StockHeadline);

        Stocked("RICE", qty: 50m, reorder: 5m);
        owner.Refresh();

        Assert.Contains("Nothing is at or below", owner.StockHeadline);
    }

    [Fact]
    public void ItListsWhatNeedsReorderingAndCountsWhatHasRunOut()
    {
        Stocked("PLENTY", qty: 50m, reorder: 5m);
        Stocked("LOW", qty: 4m, reorder: 10m);
        Stocked("GONE", qty: 0m, reorder: 8m);

        var owner = Build();
        owner.Refresh();

        Assert.Equal(2, owner.Stock.Count);
        Assert.Equal(1, owner.OutCount);
        Assert.DoesNotContain(owner.Stock, s => s.Sku == "PLENTY");
    }

    [Fact]
    public void EverythingCountedShowsTheOnesThatAreFineToo()
    {
        Stocked("PLENTY", qty: 50m, reorder: 5m);
        Stocked("LOW", qty: 4m, reorder: 10m);

        var owner = Build();
        owner.Refresh();
        owner.LowOnly = false;

        Assert.Equal(2, owner.Stock.Count);
    }

    // ---- Correcting a count ----------------------------------------------------------------------

    [Fact]
    public void CorrectingACountWritesItAndSaysWhatChanged()
    {
        var item = Stocked("RICE", qty: 3m, reorder: 10m);

        var owner = Build();
        owner.Refresh();

        owner.SelectedStock = owner.Stock.Single();
        owner.NewQuantity = "48";
        owner.AdjustReason = "delivery";

        Assert.Null(owner.ApplyAdjustment());
        Assert.Contains("3", owner.Status);
        Assert.Contains("48", owner.Status);

        // Written through the ledger, so the change and its reason survive.
        var movement = Assert.Single(Stock.History(item.Id));
        Assert.Equal(StockReason.Adjust, movement.Reason);
        Assert.Equal(48m, movement.BalanceAfter);
        Assert.Equal("delivery", movement.Reference);
    }

    /// <summary>
    /// Prefilled with what is there now, so a correction is a small edit rather than a number typed
    /// from nothing — and a mis-click cannot silently write a stale figure.
    /// </summary>
    [Fact]
    public void PickingAnItemPrefillsItsCurrentCount()
    {
        Stocked("RICE", qty: 7m, reorder: 10m);

        var owner = Build();
        owner.Refresh();
        owner.SelectedStock = owner.Stock.Single();

        Assert.Equal("7", owner.NewQuantity);
        Assert.Contains("Item RICE", owner.AdjustTarget);
    }

    [Theory]
    [InlineData("")]
    [InlineData("lots")]
    [InlineData("-4")]
    public void ANonsenseCountIsRefusedRatherThanWritten(string typed)
    {
        Stocked("RICE", qty: 7m, reorder: 10m);

        var owner = Build();
        owner.Refresh();
        owner.SelectedStock = owner.Stock.Single();
        owner.NewQuantity = typed;

        Assert.False(owner.CanAdjust);
        Assert.NotNull(owner.ApplyAdjustment());
        Assert.Equal(7m, new ItemRepository(_temp.Database).FindBySku("RICE")!.StockQty);
    }

    [Fact]
    public void CorrectingNothingIsRefused()
    {
        var owner = Build();
        owner.Refresh();

        Assert.False(owner.CanAdjust);
        Assert.NotNull(owner.ApplyAdjustment());
    }

    // ---- Settings ---------------------------------------------------------------------------------

    [Fact]
    public void SwitchingToBillsOfSupplyTakesTheGstBreakdownOffTheScreen()
    {
        var owner = Build();
        owner.Refresh();

        Assert.True(owner.ShowsTax);

        Assert.Null(owner.SetTaxMode(TaxMode.Composition));

        Assert.False(owner.ShowsTax);
        Assert.Equal(TaxMode.Composition, owner.TaxMode);
        Assert.Empty(owner.GstSlabs);
        Assert.Contains("BILL OF SUPPLY", owner.Status);
    }

    /// <summary>
    /// When the till refuses — a bill is on the screen — the screen must say so and must not show
    /// the new mode as though it had taken.
    /// </summary>
    [Fact]
    public void ARefusedSwitchLeavesTheScreenSayingWhatIsActuallyTrue()
    {
        _refuseTaxModeWith = "Finish or clear the bill on screen first.";

        var owner = Build();
        owner.Refresh();

        Assert.NotNull(owner.SetTaxMode(TaxMode.Composition));

        Assert.Equal(TaxMode.Gst, owner.TaxMode);
        Assert.True(owner.ShowsTax);
        Assert.Contains("clear the bill", owner.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SettingTheSameModeAgainIsHarmless()
    {
        var owner = Build();
        Assert.Null(owner.SetTaxMode(TaxMode.Gst));
        Assert.Equal(TaxMode.Gst, owner.TaxMode);
    }

    [Fact]
    public void APinCanBeSetAndRemovedFromTheScreen()
    {
        var owner = Build();

        Assert.False(owner.IsPinSet);

        Assert.Null(owner.SetPin("Maligai26"));
        Assert.True(owner.IsPinSet);
        Assert.True(DashboardLock.Verify("Maligai26", _pin));

        Assert.Null(owner.SetPin(null));
        Assert.False(owner.IsPinSet);
        Assert.Null(_pin);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("0000")]
    [InlineData("1234")]
    public void AnObviousOrShortPinIsRefusedBeforeItIsStored(string pin)
    {
        var owner = Build();

        Assert.NotNull(owner.SetPin(pin));
        Assert.False(owner.IsPinSet);
        Assert.Null(_pin);
    }

    // ---- Reading the figures ----------------------------------------------------------------------

    /// <summary>
    /// The screen must not take the till down with it. A figure that cannot be read is a message
    /// here; the counter carries on selling either way.
    /// </summary>
    [Fact]
    public void AFailureToReadTheFiguresIsAMessageRatherThanACrash()
    {
        var owner = new OwnerViewModel(
            Lane,
            _ => throw new InvalidOperationException("the database is busy"),
            Stock,
            TaxMode.Gst,
            isPinSet: false,
            applyTaxMode: _ => null,
            applyPin: _ => null);

        owner.Refresh();

        Assert.Contains("the database is busy", owner.Status);
        Assert.False(owner.IsBusy);
    }

    [Fact]
    public void ChangingThePeriodReReadsTheFigures()
    {
        var owner = Build();
        owner.Refresh();

        owner.Days = 90;

        Assert.Equal(90, owner.Days);
        Assert.Equal(string.Empty, owner.Status);
    }
}
