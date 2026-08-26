using System.Windows.Input;
using Pos.App.Input;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// Voiding a sale and saying who is on the till, from the keyboard.
/// </summary>
public class VoidAndCashierFlowTests
{
    private static Item Dal => Catalogue.Item(sku: "DAL001", barcode: "8901234567890", name: "Toor Dal 1kg", price: 189m, gstRate: 5m);
    private static Item Rice => Catalogue.Item(sku: "RICE01", barcode: "8901234567920", name: "Basmati Rice 5kg", price: 649m, gstRate: 5m);

    private static BillingHarness Till() => new(Dal, Rice);

    private static void Sell(BillingHarness till, string barcode)
    {
        till.Scan(barcode);
        till.Press(Key.F12);
        till.Press(Key.Enter);
        till.Press(Key.Enter);
    }

    private static void Void(BillingHarness till, string? typed = null)
    {
        till.Press(Key.V, ModifierKeys.Control | ModifierKeys.Shift);

        if (typed is not null)
            till.ViewModel.EditBuffer = typed;

        till.Press(Key.Enter);   // shows what will be voided
        till.Press(Key.Enter);   // confirms
    }

    // ---- Voiding ---------------------------------------------------------------------------------

    [Fact]
    public void VoidingIsReachableFromTheKeyboard()
    {
        using var till = Till();

        Assert.True(till.Press(Key.V, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.True(till.ViewModel.IsVoiding);
    }

    /// <summary>
    /// Voiding undoes a sale the customer has already paid for, so it is never one keypress away.
    /// The first commit shows what will go; the second does it.
    /// </summary>
    [Fact]
    public void VoidingShowsTheSaleFirstAndTakesASecondCommit()
    {
        using var till = Till();
        Sell(till, "8901234567890");
        var invoiceNo = till.ViewModel.LastInvoiceNo;

        till.Press(Key.V, ModifierKeys.Control | ModifierKeys.Shift);
        till.Press(Key.Enter);

        Assert.True(till.ViewModel.IsVoiding);
        Assert.Contains(invoiceNo, till.ViewModel.StatusMessage);
        Assert.Contains("189.00", till.ViewModel.StatusMessage);
        Assert.Contains("Commit again", till.ViewModel.StatusMessage);
        Assert.False(till.Invoices.FindByInvoiceNo(invoiceNo)!.IsVoided);

        till.Press(Key.Enter);

        Assert.False(till.ViewModel.IsVoiding);
        Assert.True(till.Invoices.FindByInvoiceNo(invoiceNo)!.IsVoided);
        Assert.Contains("voided", till.ViewModel.StatusMessage);
    }

    [Fact]
    public void EscapeAbandonsAVoidBeforeItHappens()
    {
        using var till = Till();
        Sell(till, "8901234567890");
        var invoiceNo = till.ViewModel.LastInvoiceNo;

        till.Press(Key.V, ModifierKeys.Control | ModifierKeys.Shift);
        till.Press(Key.Enter);
        till.Press(Key.Escape);

        Assert.False(till.ViewModel.IsVoiding);
        Assert.False(till.Invoices.FindByInvoiceNo(invoiceNo)!.IsVoided);
    }

    [Fact]
    public void AnInvoiceNumberVoidsThatBillRatherThanTheLastOne()
    {
        using var till = Till();
        Sell(till, "8901234567890");
        var first = till.ViewModel.LastInvoiceNo;

        Sell(till, "8901234567920");
        var second = till.ViewModel.LastInvoiceNo;

        Void(till, first);

        Assert.True(till.Invoices.FindByInvoiceNo(first)!.IsVoided);
        Assert.False(till.Invoices.FindByInvoiceNo(second)!.IsVoided);
    }

    [Fact]
    public void VoidingTellsTheCashierToReturnTheCash()
    {
        using var till = Till();
        Sell(till, "8901234567890");

        Void(till);

        Assert.Contains("Return the cash", till.ViewModel.StatusMessage);
    }

    [Fact]
    public void AlreadyVoidedIsReportedRatherThanDoneTwice()
    {
        using var till = Till();
        Sell(till, "8901234567890");
        var invoiceNo = till.ViewModel.LastInvoiceNo;

        Void(till);

        till.Press(Key.V, ModifierKeys.Control | ModifierKeys.Shift);
        till.ViewModel.EditBuffer = invoiceNo;
        till.Press(Key.Enter);

        Assert.Contains("already voided", till.ViewModel.StatusMessage);
    }

    [Fact]
    public void AnUnknownInvoiceIsReported()
    {
        using var till = Till();
        Sell(till, "8901234567890");

        till.Press(Key.V, ModifierKeys.Control | ModifierKeys.Shift);
        till.ViewModel.EditBuffer = "L9-2026-000404";
        till.Press(Key.Enter);

        Assert.Contains("No invoice found", till.ViewModel.StatusMessage);
    }

    /// <summary>
    /// A closed day's figures have been printed and filed. Changing them afterwards alters a number
    /// somebody has already acted on.
    /// </summary>
    [Fact]
    public void AnInvoiceOnAClosedDayCannotBeVoidedFromTheTill()
    {
        using var till = Till();
        Sell(till, "8901234567890");
        var invoiceNo = till.ViewModel.LastInvoiceNo;

        till.Press(Key.F12, ModifierKeys.Shift);
        till.Press(Key.F12, ModifierKeys.Shift);

        till.Press(Key.V, ModifierKeys.Control | ModifierKeys.Shift);
        till.ViewModel.EditBuffer = invoiceNo;
        till.Press(Key.Enter);

        Assert.Contains("day-end report", till.ViewModel.StatusMessage);
        Assert.False(till.Invoices.FindByInvoiceNo(invoiceNo)!.IsVoided);
    }

    [Fact]
    public void VoidingIsRefusedMidPayment()
    {
        using var till = Till();
        till.Scan("8901234567890");
        till.Press(Key.F12);

        till.Press(Key.V, ModifierKeys.Control | ModifierKeys.Shift);

        Assert.False(till.ViewModel.IsVoiding);
        Assert.True(till.ViewModel.IsTendering);
    }

    /// <summary>A voided sale leaves the day's takings, and shows on the audit line instead.</summary>
    [Fact]
    public void AVoidedSaleDropsOutOfTheDaysTakings()
    {
        using var till = Till();

        Sell(till, "8901234567890");
        Sell(till, "8901234567920");
        Void(till);

        till.Press(Key.F12, ModifierKeys.Shift);
        till.Press(Key.F12, ModifierKeys.Shift);

        var day = till.DayCloses.FindLatest(BillingHarness.LaneId)!;

        Assert.Equal(1, day.InvoiceCount);
        Assert.Equal(189.00m, day.NetSales);
        Assert.Equal(1, day.VoidedCount);
        Assert.Equal(649.00m, day.VoidedValue);
    }

    // ---- Cashier ---------------------------------------------------------------------------------

    [Fact]
    public void NobodyIsOnTheTillUntilSomebodySaysSo()
    {
        using var till = Till();

        Assert.Null(till.ViewModel.CashierName);
        Assert.Equal("not set", till.ViewModel.CashierLabel);
    }

    [Fact]
    public void TheCashierIsSetFromTheKeyboard()
    {
        using var till = Till();

        till.Press(Key.U, ModifierKeys.Control);
        Assert.True(till.ViewModel.IsSettingCashier);

        till.ViewModel.EditBuffer = "Anitha";
        till.Press(Key.Enter);

        Assert.False(till.ViewModel.IsSettingCashier);
        Assert.Equal("Anitha", till.ViewModel.CashierName);
        Assert.Contains("Anitha is on the till", till.ViewModel.StatusMessage);
    }

    [Fact]
    public void EverySaleIsRecordedAgainstWhoeverIsOnTheTill()
    {
        using var till = Till();

        till.Press(Key.U, ModifierKeys.Control);
        till.ViewModel.EditBuffer = "Anitha";
        till.Press(Key.Enter);

        Sell(till, "8901234567890");

        var saved = till.Invoices.FindByInvoiceNo(till.ViewModel.LastInvoiceNo)!;
        Assert.Equal("Anitha", saved.Sale.CashierName);
    }

    /// <summary>The name is read when a sale completes, so a shift change mid-bill attributes correctly.</summary>
    [Fact]
    public void AShiftChangeAttributesTheNextSaleToTheNewCashier()
    {
        using var till = Till();

        till.Press(Key.U, ModifierKeys.Control);
        till.ViewModel.EditBuffer = "Anitha";
        till.Press(Key.Enter);
        Sell(till, "8901234567890");
        var first = till.ViewModel.LastInvoiceNo;

        till.Press(Key.U, ModifierKeys.Control);
        till.ViewModel.EditBuffer = "Karthik";
        till.Press(Key.Enter);
        Sell(till, "8901234567920");
        var second = till.ViewModel.LastInvoiceNo;

        Assert.Equal("Anitha", till.Invoices.FindByInvoiceNo(first)!.Sale.CashierName);
        Assert.Equal("Karthik", till.Invoices.FindByInvoiceNo(second)!.Sale.CashierName);
    }

    [Fact]
    public void ClearingTheNameStopsAttributingSales()
    {
        using var till = Till();

        till.Press(Key.U, ModifierKeys.Control);
        till.ViewModel.EditBuffer = "Anitha";
        till.Press(Key.Enter);

        till.Press(Key.U, ModifierKeys.Control);
        till.ViewModel.EditBuffer = "  ";
        till.Press(Key.Enter);

        Assert.Null(till.ViewModel.CashierName);
        Assert.Contains("not be attributed", till.ViewModel.StatusMessage);
    }

    /// <summary>
    /// What makes a drawer difference answerable: which shift held how much cash.
    /// </summary>
    [Fact]
    public void TheDayEndReportSplitsTakingsByCashier()
    {
        using var till = Till();

        till.Press(Key.U, ModifierKeys.Control);
        till.ViewModel.EditBuffer = "Anitha";
        till.Press(Key.Enter);
        Sell(till, "8901234567890");

        till.Press(Key.U, ModifierKeys.Control);
        till.ViewModel.EditBuffer = "Karthik";
        till.Press(Key.Enter);
        Sell(till, "8901234567920");

        till.Press(Key.F12, ModifierKeys.Shift);
        till.Press(Key.F12, ModifierKeys.Shift);

        var day = till.DayCloses.FindLatest(BillingHarness.LaneId)!;

        Assert.True(day.HasMultipleCashiers);
        Assert.Equal(2, day.CashierTotals.Count);
        Assert.Equal(189.00m, day.CashierTotals.Single(c => c.Name == "Anitha").NetSales);
        Assert.Equal(649.00m, day.CashierTotals.Single(c => c.Name == "Karthik").NetSales);
        Assert.Equal(189.00m, day.CashierTotals.Single(c => c.Name == "Anitha").CashHeld);
    }

    [Fact]
    public void OneCashierAllDayNeedsNoBreakdown()
    {
        using var till = Till();

        till.Press(Key.U, ModifierKeys.Control);
        till.ViewModel.EditBuffer = "Anitha";
        till.Press(Key.Enter);

        Sell(till, "8901234567890");
        Sell(till, "8901234567920");

        till.Press(Key.F12, ModifierKeys.Shift);
        till.Press(Key.F12, ModifierKeys.Shift);

        var day = till.DayCloses.FindLatest(BillingHarness.LaneId)!;

        Assert.False(day.HasMultipleCashiers);
        Assert.Single(day.CashierTotals);
    }

    [Fact]
    public void SalesWithNobodySetAreLabelledRatherThanHidden()
    {
        using var till = Till();
        Sell(till, "8901234567890");

        till.Press(Key.F12, ModifierKeys.Shift);
        till.Press(Key.F12, ModifierKeys.Shift);

        var day = till.DayCloses.FindLatest(BillingHarness.LaneId)!;
        var only = Assert.Single(day.CashierTotals);

        Assert.Null(only.Name);
        Assert.Equal("(not recorded)", only.Label);
    }

    // ---- Still bound -------------------------------------------------------------------------------

    [Fact]
    public void TheNewActionsAreBoundAndHandled()
    {
        using var till = Till();

        foreach (var action in new[] { PosAction.VoidInvoice, PosAction.SetCashier })
        {
            var gesture = Assert.Single(Keymap.Default.GesturesFor(action).Take(1));
            Assert.True(till.Press(gesture.Key, gesture.Modifiers), $"{action} is bound to {gesture} but was not handled.");
        }
    }

    /// <summary>
    /// Voiding is awkward to reach on purpose. Nothing a cashier presses all day may be one
    /// modifier away from cancelling a sale.
    /// </summary>
    [Fact]
    public void VoidingNeedsTwoModifiersAndCollidesWithNothing()
    {
        Assert.Equal(PosAction.VoidInvoice, Keymap.Default.Resolve(Key.V, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.Null(Keymap.Default.Resolve(Key.V, ModifierKeys.Control));
        Assert.Null(Keymap.Default.Resolve(Key.V, ModifierKeys.None));
    }
}
