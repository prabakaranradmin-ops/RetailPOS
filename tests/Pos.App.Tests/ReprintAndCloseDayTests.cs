using System.Windows.Input;
using Pos.App.Input;
using Pos.App.ViewModels;
using Pos.Core.Domain;
using Pos.Core.Hardware.Printing;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// The two operations a lane needs that are not part of ringing up a sale: reprinting a bill a
/// customer asks for, and closing the day. Driven through the router with the shipped keymap, like
/// the rest of the keyboard suite.
/// </summary>
public class ReprintAndCloseDayTests
{
    private static Item Dal => Catalogue.Item(sku: "DAL001", barcode: "8901234567890", name: "Toor Dal 1kg", price: 189m, gstRate: 5m);
    private static Item Rice => Catalogue.Item(sku: "RICE01", barcode: "8901234567920", name: "Basmati Rice 5kg", price: 649m, gstRate: 5m);

    private static BillingHarness Till() => new(Dal, Rice);

    /// <summary>Rings up one barcode and settles it in cash.</summary>
    private static void Sell(BillingHarness till, string barcode)
    {
        till.Scan(barcode);
        till.Press(Key.F12);
        till.Press(Key.Enter);
        till.Press(Key.Enter);
    }

    // ---- Reprint --------------------------------------------------------------------------------

    [Fact]
    public void ReprintIsReachableFromTheKeyboard()
    {
        using var till = Till();

        Assert.True(till.Press(Key.P, ModifierKeys.Control));
        Assert.True(till.ViewModel.IsReprinting);
    }

    /// <summary>An empty box means the last bill, which is what "reprint" nearly always means.</summary>
    [Fact]
    public void CommittingWithAnEmptyBoxReprintsTheLastBill()
    {
        using var till = Till();
        Sell(till, "8901234567890");

        var invoiceNo = till.ViewModel.LastInvoiceNo;
        till.Printer.Clear();

        till.Press(Key.P, ModifierKeys.Control);
        till.Press(Key.Enter);

        Assert.False(till.ViewModel.IsReprinting);
        Assert.Single(till.Printer.Jobs);
        Assert.Contains($"{invoiceNo} reprinted", till.ViewModel.StatusMessage);
    }

    /// <summary>A duplicate has to say it is one, or it can be passed off as a second sale.</summary>
    [Fact]
    public void AReprintIsMarkedAsADuplicate()
    {
        using var till = Till();
        Sell(till, "8901234567890");

        till.Printer.Clear();
        till.Press(Key.P, ModifierKeys.Control);
        till.Press(Key.Enter);

        var paper = System.Text.Encoding.Latin1.GetString(till.Printer.LastJob);
        Assert.Contains("REPRINT", paper);
    }

    [Fact]
    public void AnInvoiceNumberFindsThatBill()
    {
        using var till = Till();
        Sell(till, "8901234567890");
        var first = till.ViewModel.LastInvoiceNo;

        Sell(till, "8901234567920");

        till.Printer.Clear();
        till.Press(Key.P, ModifierKeys.Control);
        till.ViewModel.EditBuffer = first;
        till.Press(Key.Enter);

        Assert.Contains($"{first} reprinted", till.ViewModel.StatusMessage);
    }

    /// <summary>
    /// A customer asking for a duplicate has their phone, not the invoice number.
    /// </summary>
    [Fact]
    public void AMobileNumberFindsThatCustomersLastBill()
    {
        using var till = Till();
        till.AddCustomer("9876543210", name: "Anitha");

        till.Scan("8901234567890");
        till.Press(Key.F7);
        till.ViewModel.EditBuffer = "9876543210";
        till.Press(Key.Enter);

        till.Press(Key.F12);
        till.Press(Key.Enter);
        till.Press(Key.Enter);

        var invoiceNo = till.ViewModel.LastInvoiceNo;
        till.Printer.Clear();

        till.Press(Key.P, ModifierKeys.Control);
        till.ViewModel.EditBuffer = "9876543210";
        till.Press(Key.Enter);

        Assert.Contains($"{invoiceNo} reprinted", till.ViewModel.StatusMessage);
        Assert.Single(till.Printer.Jobs);
    }

    [Fact]
    public void AnUnknownReferenceIsReportedAndPrintsNothing()
    {
        using var till = Till();
        Sell(till, "8901234567890");
        till.Printer.Clear();

        till.Press(Key.P, ModifierKeys.Control);
        till.ViewModel.EditBuffer = "L9-2026-000404";
        till.Press(Key.Enter);

        Assert.True(till.ViewModel.IsReprinting);
        Assert.Empty(till.Printer.Jobs);
        Assert.Contains("No invoice found", till.ViewModel.StatusMessage);
    }

    [Fact]
    public void ReprintingOnALaneThatHasSoldNothingSaysSo()
    {
        using var till = Till();

        till.Press(Key.P, ModifierKeys.Control);
        till.Press(Key.Enter);

        Assert.Contains("has not billed anything", till.ViewModel.StatusMessage);
    }

    [Fact]
    public void EscapeLeavesTheReprintPromptWithoutPrinting()
    {
        using var till = Till();
        Sell(till, "8901234567890");
        till.Printer.Clear();

        till.Press(Key.P, ModifierKeys.Control);
        till.Press(Key.Escape);

        Assert.False(till.ViewModel.IsReprinting);
        Assert.Empty(till.Printer.Jobs);
    }

    [Fact]
    public void ReprintingIsRefusedMidPayment()
    {
        using var till = Till();
        till.Scan("8901234567890");
        till.Press(Key.F12);

        till.Press(Key.P, ModifierKeys.Control);

        Assert.False(till.ViewModel.IsReprinting);
        Assert.True(till.ViewModel.IsTendering);
    }

    // ---- Day-end close --------------------------------------------------------------------------

    /// <summary>
    /// A close cannot be undone and the key sits beside the one that takes payment, so it asks
    /// twice — and shows what it is about to close on the first press.
    /// </summary>
    [Fact]
    public void ClosingTheDayTakesTwoPressesAndPreviewsFirst()
    {
        using var till = Till();
        Sell(till, "8901234567890");

        till.Press(Key.F12, ModifierKeys.Shift);

        Assert.Contains("1 invoice(s)", till.ViewModel.StatusMessage);
        Assert.Contains("Press again to close", till.ViewModel.StatusMessage);
        Assert.Null(till.DayCloses.FindLatest(BillingHarness.LaneId));

        till.Press(Key.F12, ModifierKeys.Shift);

        var day = till.DayCloses.FindLatest(BillingHarness.LaneId);
        Assert.NotNull(day);
        Assert.Equal(1, day.InvoiceCount);
        Assert.Equal(189.00m, day.NetSales);
        Assert.Contains("Day closed", till.ViewModel.StatusMessage);
    }

    [Fact]
    public void AnyOtherActionCancelsAPendingClose()
    {
        using var till = Till();
        Sell(till, "8901234567890");

        till.Press(Key.F12, ModifierKeys.Shift);
        till.Press(Key.Down);
        till.Press(Key.F12, ModifierKeys.Shift);

        // The second Shift+F12 was a fresh first press, so nothing closed.
        Assert.Null(till.DayCloses.FindLatest(BillingHarness.LaneId));
        Assert.Contains("Press again to close", till.ViewModel.StatusMessage);
    }

    /// <summary>
    /// A bill on screen at close time has not been paid for. Closing around it would leave takings
    /// that do not match the drawer.
    /// </summary>
    [Fact]
    public void ClosingIsRefusedWhileABillIsOnScreen()
    {
        using var till = Till();
        Sell(till, "8901234567890");
        till.Scan("8901234567920");

        till.Press(Key.F12, ModifierKeys.Shift);

        Assert.Contains("before closing the day", till.ViewModel.StatusMessage);
        Assert.Null(till.DayCloses.FindLatest(BillingHarness.LaneId));
    }

    [Fact]
    public void ClosingIsRefusedMidPayment()
    {
        using var till = Till();
        till.Scan("8901234567890");
        till.Press(Key.F12);

        till.Press(Key.F12, ModifierKeys.Shift);

        Assert.True(till.ViewModel.IsTendering);
        Assert.Null(till.DayCloses.FindLatest(BillingHarness.LaneId));
    }

    [Fact]
    public void ClosingPrintsTheReportAndTakesABackup()
    {
        using var till = Till();
        Sell(till, "8901234567890");
        till.Printer.Clear();

        till.Press(Key.F12, ModifierKeys.Shift);
        till.Press(Key.F12, ModifierKeys.Shift);

        var paper = System.Text.Encoding.Latin1.GetString(Assert.Single(till.Printer.Jobs));
        Assert.Contains("DAY-END REPORT", paper);
        Assert.Contains("CASH IN DRAWER SHOULD BE", paper);

        Assert.Equal(1, till.Backups.Calls);
    }

    /// <summary>
    /// A backup that failed is the one thing on this path nobody may miss — the day's books are
    /// exactly what a lost file costs.
    /// </summary>
    [Fact]
    public void AFailedBackupIsReportedLoudlyButTheDayStillCloses()
    {
        using var till = Till();
        till.Backups.FailWith = "the backup folder is full";

        Sell(till, "8901234567890");

        till.Press(Key.F12, ModifierKeys.Shift);
        till.Press(Key.F12, ModifierKeys.Shift);

        Assert.Contains("BACKUP FAILED", till.ViewModel.StatusMessage);
        Assert.Contains("the backup folder is full", till.ViewModel.StatusMessage);

        // Closed all the same: the figures are saved, and a close that half happened would be worse.
        Assert.NotNull(till.DayCloses.FindLatest(BillingHarness.LaneId));
    }

    /// <summary>
    /// A printer out of paper must not stop the day being closed. The report can be reprinted from
    /// the saved figures.
    /// </summary>
    [Fact]
    public void AFailedPrintDoesNotStopTheClose()
    {
        using var till = Till();
        Sell(till, "8901234567890");
        till.Printer.FailWith = "out of paper";

        till.Press(Key.F12, ModifierKeys.Shift);
        till.Press(Key.F12, ModifierKeys.Shift);

        Assert.NotNull(till.DayCloses.FindLatest(BillingHarness.LaneId));
        Assert.Contains("did not print", till.ViewModel.StatusMessage);
    }

    [Fact]
    public void ClosingALaneThatSoldNothingAsksTwiceAndSaysSo()
    {
        using var till = Till();

        till.Press(Key.F12, ModifierKeys.Shift);
        Assert.Contains("Nothing has been sold", till.ViewModel.StatusMessage);

        till.Press(Key.F12, ModifierKeys.Shift);

        var day = till.DayCloses.FindLatest(BillingHarness.LaneId);
        Assert.NotNull(day);
        Assert.True(day.TookNothing);
    }

    /// <summary>A sale rung up after a close belongs to the next day's report, not the last one.</summary>
    [Fact]
    public void SalesAfterACloseBelongToTheNextReport()
    {
        using var till = Till();

        Sell(till, "8901234567890");
        till.Press(Key.F12, ModifierKeys.Shift);
        till.Press(Key.F12, ModifierKeys.Shift);

        Sell(till, "8901234567920");
        till.Press(Key.F12, ModifierKeys.Shift);
        till.Press(Key.F12, ModifierKeys.Shift);

        var latest = till.DayCloses.FindLatest(BillingHarness.LaneId)!;

        Assert.Equal(1, latest.InvoiceCount);
        Assert.Equal(649.00m, latest.NetSales);
    }

    // ---- Still bound ----------------------------------------------------------------------------

    [Fact]
    public void BothNewActionsAreBoundAndHandled()
    {
        using var till = Till();

        foreach (var action in new[] { PosAction.ReprintInvoice, PosAction.CloseDay })
        {
            var gesture = Assert.Single(Keymap.Default.GesturesFor(action).Take(1));
            Assert.True(till.Press(gesture.Key, gesture.Modifiers), $"{action} is bound to {gesture} but was not handled.");
        }
    }

    /// <summary>
    /// Closing the day is deliberately awkward to reach. An unmodified F12 takes payment, and the
    /// two must never be confusable.
    /// </summary>
    [Fact]
    public void PlainF12TendersAndShiftF12Closes()
    {
        Assert.Equal(PosAction.Tender, Keymap.Default.Resolve(Key.F12, ModifierKeys.None));
        Assert.Equal(PosAction.CloseDay, Keymap.Default.Resolve(Key.F12, ModifierKeys.Shift));
    }
}
