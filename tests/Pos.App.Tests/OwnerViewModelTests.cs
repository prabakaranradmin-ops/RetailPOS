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
        },
        upiId: _upiId,
        applyUpiId: id =>
        {
            _upiId = id;
            return null;
        },
        screenTheme: ScreenTheme.Night,
        applyScreenTheme: look =>
        {
            _look = look;
            return _refuseLookWith;
        });

    private ScreenTheme? _look;
    private string? _refuseLookWith;

    private decimal _percent = LowStock.DefaultPercent;

    private string? _upiId;

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

    // ---- The UPI code with the amount ---------------------------------------------------------

    [Fact]
    public void TheUpiIdIsSetAndTheCardSaysWhatItDoes()
    {
        var owner = Build();
        Assert.Contains("Not set", owner.UpiState);

        owner.UpiIdText = " murugan.stores@okaxis ";
        Assert.Null(owner.SetUpiId());

        Assert.Equal("murugan.stores@okaxis", _upiId);
        Assert.Equal("murugan.stores@okaxis", owner.UpiIdText);
        Assert.Contains("paid to murugan.stores@okaxis", owner.UpiState);
        Assert.Contains("F12, then UPI", owner.Status);
    }

    [Theory]
    [InlineData("murugan")]
    [InlineData("murugan stores@okaxis")]
    [InlineData("9876543210")]
    public void AUpiIdThatCannotBeRightIsRefused(string typed)
    {
        _upiId = "murugan.stores@okaxis";
        var owner = Build();
        owner.UpiIdText = typed;

        Assert.NotNull(owner.SetUpiId());
        Assert.Equal("murugan.stores@okaxis", _upiId);
        Assert.Contains("is not a UPI ID", owner.Status);
    }

    [Fact]
    public void AnEmptyBoxTurnsTheCodeOff()
    {
        _upiId = "murugan.stores@okaxis";
        var owner = Build();
        owner.UpiIdText = "";

        Assert.Null(owner.SetUpiId());

        Assert.Null(_upiId);
        Assert.Contains("Not set", owner.UpiState);
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
            Assert.Contains("2 items", owner.Status);

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
            Assert.Contains("2 counts changed", owner.Status);

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

    // ---- Who works the till, and what waits for the owner ---------------------------------------

    private readonly List<string> _added = [];
    private readonly List<string> _removed = [];
    private ApprovalSettings? _savedApprovals;

    private OwnerViewModel WithAccess(params string[] cashiers)
    {
        var owner = Build();

        owner.UseTillAccess(
            cashiers,
            addCashier: (name, pin) =>
            {
                Assert.True(pin.Length >= 4);
                _added.Add(name);
                return null;
            },
            removeCashier: name =>
            {
                _removed.Add(name);
                return null;
            },
            new ApprovalSettings(),
            applyApprovals: approvals =>
            {
                _savedApprovals = approvals;
                return null;
            });

        return owner;
    }

    [Fact]
    public void ACashierIsAddedWithTheirOwnPinTypedTwice()
    {
        var owner = WithAccess();

        Assert.True(owner.CanManageAccess);
        Assert.False(owner.HasCashiers);

        Assert.Null(owner.AddCashier(" Lakshmi ", "2580", "2580"));

        Assert.Equal(["Lakshmi"], _added);
        Assert.Equal(["Lakshmi"], owner.Cashiers);
        Assert.Contains("sign on with their own PIN", owner.Status);
    }

    [Theory]
    [InlineData("", "2580", "2580", "Type the cashier's name.")]
    [InlineData("Murugan", "2580", "2580", "There is already a cashier called Murugan.")]
    [InlineData("Lakshmi", "2580", "2581", "The two PINs do not match.")]
    [InlineData("Lakshmi", "1234", "1234", "straight run")]
    [InlineData("Lakshmi", "12", "12", "at least 4")]
    public void ACashierIsNotAddedWithAProblem(string name, string pin, string again, string said)
    {
        var owner = WithAccess("Murugan");

        Assert.Contains(said, owner.AddCashier(name, pin, again));
        Assert.Empty(_added);
    }

    [Fact]
    public void ACashierIsTakenOff()
    {
        var owner = WithAccess("Murugan", "Lakshmi");

        Assert.Null(owner.RemoveCashier("Murugan"));

        Assert.Equal(["Murugan"], _removed);
        Assert.Equal(["Lakshmi"], owner.Cashiers);
        Assert.Contains("past sales keep their name", owner.Status);
    }

    /// <summary>Nothing can be asked of an owner who has no PIN to give.</summary>
    [Fact]
    public void ApprovalsWaitForTheOwnersPin()
    {
        var owner = WithAccess();

        Assert.False(owner.IsPinSet);
        Assert.False(owner.CanAskForApproval);
        Assert.Contains("Set the owner's PIN above first", owner.ApprovalNote);

        Assert.Null(owner.SetPin("Maligai26"));

        Assert.True(owner.CanAskForApproval);
        Assert.Contains("records whether it was given", owner.ApprovalNote);
    }

    [Fact]
    public void EachApprovalIsSavedAsItIsTicked()
    {
        var owner = WithAccess();
        owner.SetPin("Maligai26");

        owner.ApproveVoids = true;
        Assert.True(_savedApprovals!.Voids);
        Assert.Contains("voiding a bill", owner.Status);

        owner.DiscountLimitText = "15";
        owner.ApproveDiscounts = true;
        Assert.Equal(15m, _savedApprovals.DiscountAbovePercent);

        owner.ApproveCloseDay = true;
        Assert.True(_savedApprovals.CloseDay);
        Assert.Contains("voiding a bill, a discount over 15% of a line and closing the day", owner.Status);

        owner.ApproveDiscounts = false;
        Assert.Null(_savedApprovals.DiscountAbovePercent);
    }

    [Fact]
    public void AShareThatCannotBeRightIsNotSaved()
    {
        var owner = WithAccess();
        owner.ApproveDiscounts = true;
        var saved = _savedApprovals;

        owner.DiscountLimitText = "150";

        Assert.NotNull(owner.SaveDiscountLimit());
        Assert.Same(saved, _savedApprovals);
    }

    // ---- At the till, by who ---------------------------------------------------------------------

    [Fact]
    public void BeforeAnythingIsRecordedTheReportSaysWhatItWillShow()
    {
        var owner = Build();
        owner.Refresh();

        Assert.False(owner.HasExceptions);
        Assert.Contains("Nothing recorded yet", owner.ExceptionsLine);
    }

    [Fact]
    public void EachPersonsExceptionsAreShownWithTheLatestInWords()
    {
        var events = new TillEventRepository(_temp.Database);
        var now = DateTimeOffset.Now;

        events.Record(Lane, now.AddMinutes(-30), TillEventKind.Voided, "Murugan", "RM/26-27/L1-7", 189.50m, approved: true);
        events.Record(Lane, now.AddMinutes(-20), TillEventKind.Discounted, "Lakshmi", "Toor Dal 1kg", 30m, detail: "15.87% off Toor Dal 1kg");
        events.Record(Lane, now.AddMinutes(-10), TillEventKind.ApprovalRefused, "Lakshmi", "RM/26-27/L1-8", 40m, approved: false,
            detail: "Void RM/26-27/L1-8 for ₹40.00: backed out without the owner's PIN.");

        var owner = Build();
        owner.Refresh();

        Assert.True(owner.HasExceptions);
        Assert.Equal(2, owner.ExceptionsByCashier.Count);

        var lakshmi = owner.ExceptionsByCashier[0];
        Assert.Equal("Lakshmi", lakshmi.Cashier);
        Assert.Equal("1 · ₹30.00", lakshmi.Discounts);
        Assert.Equal("1", lakshmi.Refused);
        Assert.Equal("—", lakshmi.Voids);

        Assert.Equal("1 · ₹189.50", owner.ExceptionsByCashier[1].Voids);

        Assert.Equal(3, owner.LatestExceptions.Count);
        Assert.Equal("refused", owner.LatestExceptions[0].Owner);
        Assert.Equal("15.87% off Toor Dal 1kg", owner.LatestExceptions[1].What);
        Assert.Equal(string.Empty, owner.LatestExceptions[1].Owner);
        Assert.Equal("Voided RM/26-27/L1-7", owner.LatestExceptions[2].What);
        Assert.Equal("approved", owner.LatestExceptions[2].Owner);

        Assert.Contains("1 void, ₹189.50", owner.ExceptionsLine);
        Assert.Contains("1 discount typed by hand, ₹30.00", owner.ExceptionsLine);
        Assert.Contains("1 PIN asked for and not given", owner.ExceptionsLine);
    }

    // ---- The drawer at closing -------------------------------------------------------------------

    [Fact]
    public void WithNoCloseThePeriodSaysSo()
    {
        var owner = Build();
        owner.Refresh();

        Assert.False(owner.HasDrawerCloses);
        Assert.Equal("No day was closed in this period.", owner.DrawersLine);
    }

    [Fact]
    public void EachCloseIsShownOverOrShortAndEachPersonsDays()
    {
        var closes = new DayCloseRepository(_temp.Database, new HeldBillRepository(_temp.Database));

        SellDalAs("Murugan");
        closes.Close(Lane, DateTimeOffset.Now.AddMinutes(-10), cashCounted: 180m, countedBy: "Murugan");
        SellDalAs("Murugan");
        closes.Close(Lane, DateTimeOffset.Now.AddMinutes(-5));

        var owner = Build();
        owner.Refresh();

        Assert.True(owner.HasDrawerCloses);
        Assert.Equal("Counted at 1 of 2 closes; short once, ₹9.00 in all; never over.", owner.DrawersLine);

        // Newest first in the list.
        Assert.Equal("not counted", owner.DrawerCloses[0].Counted);
        Assert.Equal("₹180.00 by Murugan", owner.DrawerCloses[1].Counted);
        Assert.Equal("short by ₹9.00", owner.DrawerCloses[1].Result);
        Assert.Equal("Murugan", owner.DrawerCloses[1].OnTheTill);

        var murugan = Assert.Single(owner.DrawersByPerson);
        Assert.Equal("2 days, 1 counted", murugan.Days);
        Assert.Equal("1 day · ₹9.00", murugan.Short);
        Assert.Equal("—", murugan.Over);

        // The chart: one short bar, below the line; nothing for the close not counted.
        Assert.Equal([0d, 0d], owner.DrawerChart!.Series[0].Values);
        Assert.Equal([-9d, 0d], owner.DrawerChart.Series[1].Values);
    }

    /// <summary>A ₹189 dal, in the catalogue once.</summary>
    private Item Dal()
    {
        var items = new ItemRepository(_temp.Database);

        if (items.FindBySku("DAL001") is { } known)
            return known;

        items.UpsertRange([Catalogue.Item(sku: "DAL001", name: "Toor Dal 1kg", price: 189m)]);
        return items.FindBySku("DAL001")!;
    }

    private void SellDalAs(string cashier)
    {
        var bill = new InvoiceEngine("33");
        bill.AddItem(Dal());

        var basket = new TenderBasket(bill.Totals.GrandTotal);
        basket.Add(TenderType.Cash, bill.Totals.GrandTotal);

        new CheckoutService(new InvoiceRepository(_temp.Database), new CustomerRepository(_temp.Database),
            new RecordingDrawerService(), cashier: () => cashier).Complete(Lane, bill, basket);
    }

    // ---- What the shelves are worth ----------------------------------------------------------------

    [Fact]
    public void TheStockTabSaysWhatTheShelvesAreWorth()
    {
        var items = new ItemRepository(_temp.Database);
        items.UpsertRange(
        [
            Catalogue.Item(sku: "DAL", name: "Toor Dal 1kg", price: 189m) with { StockQty = 10m, CostPrice = 150m, Category = "Grocery", Mrp = 195m },
            Catalogue.Item(sku: "NEW", name: "New Soap", price: 40m) with { StockQty = 5m },
        ]);

        var owner = Build();
        owner.Refresh();

        Assert.Equal("What the shelves are worth: ₹1,500.00 at cost, ₹2,090.00 at selling price, ₹2,150.00 at MRP.", owner.StockWorthLine);
        Assert.Contains("Most in Grocery ₹1,890.00", owner.StockWorthDetail);
        Assert.Contains("1 item with no cost price is not in the value at cost", owner.StockWorthDetail);
    }

    [Fact]
    public void WithNothingCountedThereIsNoStockValue()
    {
        var owner = Build();
        owner.Refresh();

        Assert.Equal("Nothing counted is on the shelves, so there is no stock value to give.", owner.StockWorthLine);
    }

    // ---- How the screens look --------------------------------------------------------------------

    [Theory]
    [InlineData(ScreenTheme.Morning, "morning look")]
    [InlineData(ScreenTheme.Noon, "noon look")]
    [InlineData(ScreenTheme.Evening, "evening look")]
    [InlineData(ScreenTheme.ByTimeOfDay, "the morning look from 6 am, noon from 11 am, evening from 4 pm and night from 7 pm")]
    public void TheLookCanBeChangedFromTheScreen(ScreenTheme look, string said)
    {
        var owner = Build();

        Assert.True(owner.CanChooseScreenTheme);
        Assert.Equal(ScreenTheme.Night, owner.ScreenTheme);

        Assert.Null(owner.SetScreenTheme(look));

        Assert.Equal(look, _look);
        Assert.Equal(look, owner.ScreenTheme);
        Assert.Contains(said, owner.Status);

        Assert.Null(owner.SetScreenTheme(ScreenTheme.Night));
        Assert.Equal(ScreenTheme.Night, _look);
        Assert.Contains("night look", owner.Status);
    }

    /// <summary>Picking the look already on screen is not a change, and is not saved again.</summary>
    [Fact]
    public void PickingTheLookAlreadyShowingDoesNothing()
    {
        var owner = Build();

        Assert.Null(owner.SetScreenTheme(ScreenTheme.Night));
        Assert.Null(_look);
    }

    /// <summary>
    /// The screens have already changed when the file fails to save, so the choice shows as made -
    /// and the owner is told it will not outlast a restart.
    /// </summary>
    [Fact]
    public void ALookThatCouldNotBeSavedStillShowsAsChosen()
    {
        _refuseLookWith = "Changed for this session, but it could not be saved: disk full";
        var owner = Build();

        Assert.NotNull(owner.SetScreenTheme(ScreenTheme.Noon));

        Assert.Equal(ScreenTheme.Noon, owner.ScreenTheme);
        Assert.Contains("could not be saved", owner.Status);
    }

    [Fact]
    public void ALaneWiredWithNowhereToSaveTheLookDoesNotOfferIt()
    {
        var owner = new OwnerViewModel(Lane, _ => throw new InvalidOperationException(), Stock, TaxMode.Gst, false, _ => null, _ => null);

        Assert.False(owner.CanChooseScreenTheme);
        Assert.NotNull(owner.SetScreenTheme(ScreenTheme.Morning));
        Assert.Equal(ScreenTheme.Night, owner.ScreenTheme);
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
