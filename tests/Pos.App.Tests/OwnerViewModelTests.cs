using System.IO;
using Pos.App.ViewModels;
using Pos.Core.Analytics;
using Pos.Core.Configuration;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Domain.Printing;
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

    private StockRepository Stock => new(_temp.Database, () => _percent);

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
        },
        receiptLayout: ReceiptLayout.Standard,
        applyReceiptLayout: layout =>
        {
            _layout = layout;
            return _refuseLayoutWith;
        },
        lowStockPercent: _percent,
        applyLowStockPercent: percent =>
        {
            _percent = percent;
            return null;
        });

    private decimal _percent = LowStock.DefaultPercent;

    private ReceiptLayout? _layout;
    private string? _refuseLayoutWith;

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

        Assert.Contains("No item is counted yet", owner.StockHeadline);

        Stocked("RICE", qty: 50m, reorder: 5m);
        owner.Refresh();

        Assert.Contains("Nothing is low", owner.StockHeadline);
        Assert.Contains("10% of full", owner.StockHeadline);
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

    // ---- When stock counts as low ------------------------------------------------------------

    [Fact]
    public void TheShareOfFullCanBeChangedAndTheListFollows()
    {
        var rice = Stocked("RICE", qty: 100m);
        Stock.Set(rice.Id, 20m, StockReason.Adjust, Lane);

        var owner = Build();
        owner.Refresh();

        Assert.Empty(owner.Stock);
        Assert.Equal("10", owner.LowStockPercentText);

        owner.LowStockPercentText = "25";
        Assert.Null(owner.SetLowStockPercent());

        Assert.Equal(25m, _percent);
        Assert.Equal(25m, owner.LowStockPercent);
        Assert.Equal("RICE", Assert.Single(owner.Stock).Sku);
        Assert.Contains("25% of full", owner.StockHeadline);
        Assert.Contains("25% of full", owner.Status);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("100")]
    [InlineData("-5")]
    public void AShareThatCannotBeMeantIsRefused(string typed)
    {
        var owner = Build();
        owner.LowStockPercentText = typed;

        Assert.NotNull(owner.SetLowStockPercent());
        Assert.Equal(LowStock.DefaultPercent, _percent);
    }

    [Fact]
    public void ZeroSwitchesTheShareOffAndSaysSo()
    {
        var owner = Build();
        owner.LowStockPercentText = "0%";

        Assert.Null(owner.SetLowStockPercent());

        Assert.Equal(0m, _percent);
        Assert.Contains("off", owner.Status);
    }

    // ---- The stock sheet -----------------------------------------------------------------------

    [Fact]
    public void AStockSheetIsSavedFilledInAndLoadedBack()
    {
        Stocked("DAL", qty: 12m);
        Stocked("OIL", qty: null);

        var owner = Build();
        owner.Refresh();

        var path = Path.Combine(Path.GetTempPath(), $"sheet-{Guid.NewGuid():N}.csv");

        try
        {
            Assert.Null(owner.SaveStockSheet(path));
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(path).Take(3).ToArray());
            Assert.Contains("2 item(s)", owner.Status);

            // Filled in the way Excel would give it back: the new counts in the last column.
            var filled = File.ReadAllLines(path)
                .Select(line => line.StartsWith("DAL,", StringComparison.Ordinal) ? line + "40"
                    : line.StartsWith("OIL,", StringComparison.Ordinal) ? line + "24"
                    : line);
            File.WriteAllLines(path, filled, new System.Text.UTF8Encoding(true));

            var plan = owner.CheckStockSheet(path);

            Assert.NotNull(plan);
            Assert.Equal(2, plan!.Counts);
            Assert.False(owner.HasStockSheetProblems);

            Assert.Null(owner.ApplyStockSheet(plan));
            Assert.Contains("2 count(s) changed", owner.Status);

            var items = new ItemRepository(_temp.Database);
            Assert.Equal(40m, items.FindBySku("DAL")!.StockQty);
            Assert.Equal(24m, items.FindBySku("OIL")!.StockQty);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ASheetWithMistakesListsThemAndChangesNothing()
    {
        Stocked("DAL", qty: 12m);

        var owner = Build();
        var path = Path.Combine(Path.GetTempPath(), $"sheet-{Guid.NewGuid():N}.csv");

        try
        {
            File.WriteAllText(path, "sku,new_count\nDAL,forty\nGHOST,3\n");

            Assert.Null(owner.CheckStockSheet(path));

            Assert.True(owner.HasStockSheetProblems);
            Assert.Equal(2, owner.StockSheetProblems.Count);
            Assert.Contains("Line 2", owner.StockSheetProblems[0]);
            Assert.Contains("Nothing was changed", owner.Status);
            Assert.Equal(12m, new ItemRepository(_temp.Database).FindBySku("DAL")!.StockQty);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ASheetThatChangesNothingSaysSo()
    {
        Stocked("DAL", qty: 12m);

        var owner = Build();
        var path = Path.Combine(Path.GetTempPath(), $"sheet-{Guid.NewGuid():N}.csv");

        try
        {
            Assert.Null(owner.SaveStockSheet(path));

            Assert.Null(owner.CheckStockSheet(path));
            Assert.False(owner.HasStockSheetProblems);
            Assert.Contains("Nothing in that sheet changes a count", owner.Status);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheBillLayoutCanBeSwitchedFromTheScreen()
    {
        var owner = Build();

        Assert.True(owner.CanChooseLayout);
        Assert.Equal(ReceiptLayout.Standard, owner.ReceiptLayout);

        Assert.Null(owner.SetReceiptLayout(ReceiptLayout.Compact));

        Assert.Equal(ReceiptLayout.Compact, _layout);
        Assert.Equal(ReceiptLayout.Compact, owner.ReceiptLayout);
        Assert.Contains("compact", owner.Status, StringComparison.OrdinalIgnoreCase);

        Assert.Null(owner.SetReceiptLayout(ReceiptLayout.Standard));
        Assert.Equal(ReceiptLayout.Standard, _layout);
    }

    /// <summary>
    /// The till's composer has already switched when the file fails to save, so the screen shows the
    /// layout the next bill will actually print in — and says that it will not survive a restart.
    /// </summary>
    [Fact]
    public void ALayoutThatCouldNotBeSavedStillShowsWhatTheTillWillPrint()
    {
        _refuseLayoutWith = "Changed for this session, but it could not be saved: disk full";
        var owner = Build();

        Assert.NotNull(owner.SetReceiptLayout(ReceiptLayout.Compact));

        Assert.Equal(ReceiptLayout.Compact, owner.ReceiptLayout);
        Assert.Contains("could not be saved", owner.Status);
    }

    [Fact]
    public void ALaneWiredWithNowhereToSaveTheLayoutDoesNotOfferIt()
    {
        var owner = new OwnerViewModel(Lane, _ => throw new InvalidOperationException(), Stock, TaxMode.Gst, false, _ => null, _ => null);

        Assert.False(owner.CanChooseLayout);
        Assert.NotNull(owner.SetReceiptLayout(ReceiptLayout.Compact));
        Assert.Equal(ReceiptLayout.Standard, owner.ReceiptLayout);
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
