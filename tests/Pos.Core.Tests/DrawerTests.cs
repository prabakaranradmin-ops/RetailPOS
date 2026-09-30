using Pos.Core.Analytics;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Domain.Printing;
using Pos.Core.Hardware.Drawer;
using Pos.TestSupport;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// The float, expenses, and cash put into and taken out of the drawer: what each does to the drawer,
/// to the day-end report, and to the owner's figures.
/// </summary>
public class DrawerTests : IDisposable
{
    private const string Lane = "L1";

    private readonly TempDatabase _temp = new();
    private readonly RecordingDrawerService _drawer = new();

    public void Dispose() => _temp.Dispose();

    private CashDrawerRepository Store => new(_temp.Database);

    private DayCloseRepository Closes => new(_temp.Database);

    private DrawerEntryRecorded Record(DrawerEntryKind kind, decimal amount, string? category = null, string? note = null) =>
        Store.Record(new DrawerEntry(kind, amount, category, note), Lane, DateTimeOffset.Now, "Priya");

    private void SellForCash(decimal price)
    {
        _temp.Items.UpsertRange([Catalogue.Item(sku: "DAL", price: price)]);

        var bill = new InvoiceEngine("33");
        bill.AddItem(_temp.Items.FindBySku("DAL")!);

        var basket = new TenderBasket(bill.Totals.AmountPayable);
        basket.Add(TenderType.Cash, bill.Totals.AmountPayable);

        new CheckoutService(new InvoiceRepository(_temp.Database), new CustomerRepository(_temp.Database), _drawer)
            .Complete(Lane, bill, basket);
    }

    // ---- What can be recorded --------------------------------------------------------------------

    [Theory]
    [InlineData(DrawerEntryKind.OpeningFloat, 0, null, null, "more than nothing")]
    [InlineData(DrawerEntryKind.CashIn, -5, null, null, "more than nothing")]
    [InlineData(DrawerEntryKind.OpeningFloat, 10.005, null, null, "finer than a paisa")]
    [InlineData(DrawerEntryKind.ExpenseFromDrawer, 50, null, null, "what the expense was for")]
    [InlineData(DrawerEntryKind.ExpenseFromOutside, 50, " ", null, "what the expense was for")]
    [InlineData(DrawerEntryKind.CashOut, 1000, null, "  ", "where the cash is going")]
    [InlineData(DrawerEntryKind.CashIn, 2000000, null, null, "more than any drawer")]
    public void AnEntryThatCannotBeRightIsRefused(DrawerEntryKind kind, decimal amount, string? category, string? note, string says)
    {
        var entry = new DrawerEntry(kind, amount, category, note);

        Assert.Contains(says, entry.Problem());
        Assert.Throws<ArgumentException>(() => Store.Record(entry, Lane, DateTimeOffset.Now, null));
    }

    [Theory]
    [InlineData(DrawerEntryKind.OpeningFloat, 2000)]
    [InlineData(DrawerEntryKind.CashIn, 500)]
    [InlineData(DrawerEntryKind.ExpenseFromDrawer, -50)]
    [InlineData(DrawerEntryKind.CashOut, -1000)]
    [InlineData(DrawerEntryKind.ExpenseFromOutside, 0)]
    public void EachKindMovesTheDrawerItsOwnWay(DrawerEntryKind kind, decimal change)
    {
        var amount = Math.Abs(change) is 0m ? 1200m : Math.Abs(change);

        Assert.Equal(change is 0m ? 0m : change, new DrawerEntry(kind, amount, "Rent", "to the bank").DrawerChange);
    }

    // ---- The drawer and the day ------------------------------------------------------------------

    /// <summary>
    /// A float of 2,000, a sale of 100 in cash, 50 on tea from the drawer, 1,000 to the bank: the
    /// drawer should hold 1,050. The rent paid by bank is an expense but touches none of it.
    /// </summary>
    [Fact]
    public void TheDrawerFigureAccountsForEverythingThatWentInAndOut()
    {
        Record(DrawerEntryKind.OpeningFloat, 2000m);
        SellForCash(100m);
        Record(DrawerEntryKind.ExpenseFromDrawer, 50m, "Tea and snacks");
        Record(DrawerEntryKind.CashOut, 1000m, note: "to the bank");
        Record(DrawerEntryKind.ExpenseFromOutside, 8000m, "Rent", "September, by NEFT");

        var day = Closes.Preview(Lane, DateTimeOffset.Now);

        Assert.Equal(1_050.00m, day.CashExpected);
        Assert.Equal(1_050.00m, day.CashPaidOut);
        Assert.Equal(2_000.00m, day.CashPaidIn);
        Assert.Equal(100.00m, day.NetSales);

        var kinds = day.DrawerMovementTotals.ToDictionary(m => m.Kind, m => m.Amount);
        Assert.Equal(2_000.00m, kinds[DrawerKinds.Float]);
        Assert.Equal(-50.00m, kinds[DrawerKinds.Expense]);
        Assert.Equal(-1_000.00m, kinds[DrawerKinds.CashOut]);
        Assert.Equal(3, kinds.Count);
    }

    [Fact]
    public void AFloatAloneIsADayToCloseWithTheFloatToCount()
    {
        Record(DrawerEntryKind.OpeningFloat, 1500m);

        var day = Closes.Preview(Lane, DateTimeOffset.Now);

        Assert.True(day.TookNothing);
        Assert.True(day.MovedMoneyWithoutSales);
        Assert.Equal(1_500.00m, day.CashExpected);
    }

    [Fact]
    public void TheFloatSoFarIsWhatWasRecordedSinceTheLastClose()
    {
        Record(DrawerEntryKind.OpeningFloat, 1000m);
        Assert.Equal(1_500.00m, Record(DrawerEntryKind.OpeningFloat, 500m).FloatSinceClose);

        Closes.Close(Lane, DateTimeOffset.Now);

        Assert.Equal(0m, Store.FloatSinceLastClose(Lane));
    }

    /// <summary>A reprint works change out from the stored drawer figure, so the float must come back out first.</summary>
    [Fact]
    public void AReprintedReportStillShowsTheRightChange()
    {
        Record(DrawerEntryKind.OpeningFloat, 2000m);
        SellForCash(100m);
        Record(DrawerEntryKind.ExpenseFromDrawer, 50m, "Tea and snacks");

        var closed = Closes.Close(Lane, DateTimeOffset.Now);
        var reprinted = Closes.FindById(closed.Id)!;

        Assert.Equal(closed.ChangeGiven, reprinted.ChangeGiven);
        Assert.Equal(2_050.00m, reprinted.CashExpected);
    }

    [Fact]
    public void TheDayEndReportNamesEachMovement()
    {
        Record(DrawerEntryKind.OpeningFloat, 2000m);
        SellForCash(100m);
        Record(DrawerEntryKind.ExpenseFromDrawer, 50m, "Tea and snacks");
        Record(DrawerEntryKind.CashIn, 500m);
        Record(DrawerEntryKind.CashOut, 1000m, note: "to the bank");

        var text = new ZReportComposer(new StoreProfile { Name = "Sri Murugan Stores" })
            .Compose(Closes.Preview(Lane, DateTimeOffset.Now)).ToPlainText();

        Assert.Contains("Opening float (1)", text);
        Assert.Contains("Expenses paid (1)", text);
        Assert.Contains("Cash put in (1)", text);
        Assert.Contains("Cash taken out (1)", text);
        Assert.Contains("1,550.00", text);
        Assert.Contains("Reconciled", text);
    }

    // ---- Expenses --------------------------------------------------------------------------------

    [Fact]
    public void AnExpensePaidFromTheDrawerIsBothAnExpenseAndACashMovement()
    {
        var recorded = Record(DrawerEntryKind.ExpenseFromDrawer, 120m, "Transport and delivery", "auto from the market");

        var expense = recorded.Expense!;
        Assert.Equal(ExpensePaidFrom.Drawer, expense.PaidFrom);
        Assert.Equal("Transport and delivery", expense.Category);
        Assert.Equal("auto from the market", expense.Note);
        Assert.Equal("Priya", expense.CashierName);

        Assert.Equal(-120.00m, Assert.Single(Closes.Preview(Lane, DateTimeOffset.Now).DrawerMovementTotals).Amount);
    }

    [Fact]
    public void AnExpensePaidFromOutsideLeavesTheDrawerAlone()
    {
        var recorded = Record(DrawerEntryKind.ExpenseFromOutside, 2400m, "Electricity");

        Assert.Equal(ExpensePaidFrom.Outside, recorded.Expense!.PaidFrom);

        var day = Closes.Preview(Lane, DateTimeOffset.Now);
        Assert.Empty(day.DrawerMovementTotals);
        Assert.False(day.MovedMoneyWithoutSales);
    }

    [Fact]
    public void ExpensesAddUpByWhatTheyWereFor()
    {
        Record(DrawerEntryKind.ExpenseFromDrawer, 30m, "Tea and snacks");
        Record(DrawerEntryKind.ExpenseFromDrawer, 45m, "Tea and snacks");
        Record(DrawerEntryKind.ExpenseFromOutside, 2400m, "Electricity");
        Record(DrawerEntryKind.CashIn, 500m);

        var from = DateTimeOffset.Now.AddDays(-1);
        var to = DateTimeOffset.Now.AddMinutes(1);

        var totals = Store.ExpenseTotals(from, to);
        Assert.Equal(["Electricity", "Tea and snacks"], totals.Select(t => t.Category));
        Assert.Equal(75.00m, totals[1].Amount);
        Assert.Equal(2, totals[1].Count);

        Assert.Equal(3, Store.Expenses(from, to).Count);
    }

    [Fact]
    public void TheOwnersFiguresCountEveryExpenseWhereverItWasPaidFrom()
    {
        SellForCash(100m);
        Record(DrawerEntryKind.ExpenseFromDrawer, 30m, "Tea and snacks");
        Record(DrawerEntryKind.ExpenseFromOutside, 2400m, "Electricity");

        var to = DateTimeOffset.Now.AddMinutes(1);
        var data = new DashboardQuery(_temp.Database).Gather(Lane, to.AddDays(-1), to);

        Assert.Equal(2_430.00m, data.ExpensesTotal);
        Assert.Equal(2, data.Expenses.Count);
        Assert.Contains("Electricity", DashboardPage.Render(data, "Sri Murugan Stores"));
    }

    // ---- At the counter --------------------------------------------------------------------------

    [Fact]
    public void TheDrawerOpensForCashAndNotForAnExpensePaidElsewhere()
    {
        var service = new CashDrawerService(Store, _drawer);

        Assert.Equal(DrawerKickResult.Opened, service.Record(new DrawerEntry(DrawerEntryKind.OpeningFloat, 1000m), Lane).Drawer);
        Assert.Equal(1, _drawer.KickCount);

        Assert.Equal(DrawerKickResult.NoDrawerAttached, service.Record(new DrawerEntry(DrawerEntryKind.ExpenseFromOutside, 900m, "Rent"), Lane).Drawer);
        Assert.Equal(1, _drawer.KickCount);
    }

    [Fact]
    public void TheServiceRefusesBeforeItWritesAnything()
    {
        var service = new CashDrawerService(Store, _drawer);

        Assert.Throws<ArgumentException>(() => service.Record(new DrawerEntry(DrawerEntryKind.CashOut, 500m), Lane));

        Assert.Empty(Closes.Preview(Lane, DateTimeOffset.Now).DrawerMovementTotals);
        Assert.Equal(0, _drawer.KickCount);
    }
}
