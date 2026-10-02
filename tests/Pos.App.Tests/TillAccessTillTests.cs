using System.Windows.Input;
using Pos.App.ViewModels;
using Pos.Core.Configuration;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// Cashiers signing on with their own PIN, and the owner's PIN in front of a void, a big discount, a
/// cash refund, cash out of the drawer and closing the day - all at the till, by keystroke.
/// </summary>
/// <remarks>
/// The PINs here are checked by a stand-in that compares them directly, so the tests run in
/// milliseconds; the real, deliberately slow check is used once, at the end, to show the till is
/// wired to it.
/// </remarks>
public class TillAccessTillTests
{
    private const string Dal = "8901234567890";
    private const string OwnersPin = "4826";

    private static PinCredential Fake(string pin) => new() { Salt = "c2FsdA==", Hash = pin, Iterations = 1 };

    private static bool FakeCheck(string? pin, PinCredential? stored) => stored is not null && pin is not null && stored.Hash == pin;

    private static BillingHarness Till(
        Action<ApprovalSettings>? approvals = null,
        bool ownerPin = true,
        (string Name, string Pin)[]? cashiers = null)
    {
        var till = new BillingHarness(Catalogue.Item(sku: "DAL001", barcode: Dal, name: "Toor Dal 1kg", price: 189m, gstRate: 5m));

        var settings = new PosSettings();

        if (ownerPin)
            settings.Security.DashboardPin = Fake(OwnersPin);

        approvals?.Invoke(settings.Approvals);

        foreach (var (name, pin) in cashiers ?? [])
            settings.Cashiers.Add(new CashierSettings { Name = name, Pin = Fake(pin) });

        till.ViewModel.Security = new TillSecurity(settings, new TillEventRepository(till.Database), FakeCheck);
        return till;
    }

    private static IReadOnlyList<TillEvent> Recorded(BillingHarness till) =>
        new TillEventRepository(till.Database).List(DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now.AddHours(1));

    private static SettledInvoice Sell(BillingHarness till)
    {
        till.Scan(Dal);
        till.Press(Key.F12);
        till.Press(Key.Enter);
        till.Press(Key.Enter);
        return till.Invoices.FindLatest(BillingHarness.LaneId)!;
    }

    private static void Type(BillingHarness till, string text)
    {
        till.ViewModel.EditBuffer = text;
        till.Press(Key.Enter);
    }

    private static void Approve(BillingHarness till, string pin = OwnersPin)
    {
        till.ViewModel.ApprovalPin = pin;
        till.Press(Key.Enter);
    }

    /// <summary>Ctrl+Shift+V, Enter to show the last bill, Enter again to void it.</summary>
    private static void AskToVoid(BillingHarness till)
    {
        till.Press(Key.V, ModifierKeys.Control | ModifierKeys.Shift);
        till.Press(Key.Enter);
        till.Press(Key.Enter);
    }

    // ---- Voids -----------------------------------------------------------------------------------

    /// <summary>Nothing is asked unless the owner said so - but the void is still written down.</summary>
    [Fact]
    public void AVoidIsNotAskedAboutUnlessTheOwnerSaysSoButIsStillRecorded()
    {
        using var till = Till();
        var sale = Sell(till);

        AskToVoid(till);

        Assert.False(till.ViewModel.IsApproving);
        Assert.True(till.Invoices.FindByInvoiceNo(sale.InvoiceNo)!.IsVoided);

        var voided = Assert.Single(Recorded(till), e => e.Kind == TillEventKind.Voided);
        Assert.Equal(sale.InvoiceNo, voided.Reference);
        Assert.Equal(sale.GrandTotal, voided.Amount);
        Assert.Null(voided.Approved);
    }

    [Fact]
    public void AVoidWaitsForTheOwnersPinWithTheBillAndAmountOnScreen()
    {
        using var till = Till(a => a.Voids = true);
        var sale = Sell(till);

        AskToVoid(till);

        Assert.True(till.ViewModel.IsApproving);
        Assert.Contains(sale.InvoiceNo, till.ViewModel.ApprovalWhat);
        Assert.Contains("₹189.00", till.ViewModel.ApprovalWhat);
        Assert.False(till.Invoices.FindByInvoiceNo(sale.InvoiceNo)!.IsVoided);

        Approve(till);

        Assert.False(till.ViewModel.IsApproving);
        Assert.True(till.Invoices.FindByInvoiceNo(sale.InvoiceNo)!.IsVoided);
        Assert.Equal(true, Assert.Single(Recorded(till), e => e.Kind == TillEventKind.Voided).Approved);
    }

    /// <summary>While the owner's PIN is asked for, no other key can open something else and step round it.</summary>
    [Fact]
    public void WhileThePinIsAskedForNoOtherKeyDoesAnything()
    {
        using var till = Till(a => a.Voids = true);
        Sell(till);
        AskToVoid(till);

        till.Press(Key.F12);
        till.Press(Key.F9);
        till.Press(Key.M, ModifierKeys.Control);
        till.Press(Key.F12, ModifierKeys.Shift);
        till.Press(Key.D, ModifierKeys.Control);

        Assert.True(till.ViewModel.IsApproving);
        Assert.Equal(BillingMode.Void, till.ViewModel.Mode);
        Assert.False(till.ViewModel.IsConfirmingDayClose);
    }

    [Fact]
    public void AWrongPinThreeTimesDoesNothingAndIsRecorded()
    {
        using var till = Till(a => a.Voids = true);
        var sale = Sell(till);
        AskToVoid(till);

        Approve(till, "1111");
        Assert.True(till.ViewModel.IsApproving);
        Assert.Contains("2 left", till.ViewModel.StatusMessage);

        Approve(till, "2222");
        Assert.Contains("1 left", till.ViewModel.StatusMessage);

        Approve(till, "3333");

        Assert.False(till.ViewModel.IsApproving);
        Assert.Contains("wrong three times", till.ViewModel.StatusMessage);
        Assert.False(till.Invoices.FindByInvoiceNo(sale.InvoiceNo)!.IsVoided);

        var refused = Assert.Single(Recorded(till));
        Assert.Equal(TillEventKind.ApprovalRefused, refused.Kind);
        Assert.Equal(false, refused.Approved);
        Assert.Equal(sale.InvoiceNo, refused.Reference);
    }

    /// <summary>Esc backs out of the PIN and leaves the void pane as it was, nothing voided.</summary>
    [Fact]
    public void BackingOutOfThePinLeavesEverythingAsItWas()
    {
        using var till = Till(a => a.Voids = true);
        var sale = Sell(till);
        AskToVoid(till);

        till.Press(Key.Escape);

        Assert.False(till.ViewModel.IsApproving);
        Assert.True(till.ViewModel.IsVoiding);
        Assert.False(till.Invoices.FindByInvoiceNo(sale.InvoiceNo)!.IsVoided);
        Assert.Contains("Not approved", till.ViewModel.StatusMessage);
        Assert.Contains("backed out", Assert.Single(Recorded(till)).Detail);
    }

    /// <summary>An approval nobody can give would stop the till, so without the owner's PIN nothing is asked.</summary>
    [Fact]
    public void WithoutAnOwnersPinNothingIsAsked()
    {
        using var till = Till(a => a.Voids = true, ownerPin: false);
        var sale = Sell(till);

        AskToVoid(till);

        Assert.False(till.ViewModel.IsApproving);
        Assert.True(till.Invoices.FindByInvoiceNo(sale.InvoiceNo)!.IsVoided);
    }

    // ---- Discounts -------------------------------------------------------------------------------

    private static void Discount(BillingHarness till, string amount)
    {
        till.Press(Key.F4);
        Type(till, amount);
    }

    /// <summary>₹30 off a ₹189 line is 15.87% - over the 10% the owner set.</summary>
    [Fact]
    public void ADiscountOverTheShareWaitsForTheOwner()
    {
        using var till = Till(a => a.DiscountAbovePercent = 10m);
        till.Scan(Dal);

        Discount(till, "30");

        Assert.True(till.ViewModel.IsApproving);
        Assert.Contains("₹30.00 off Toor Dal 1kg", till.ViewModel.ApprovalWhat);
        Assert.Contains("15.87%", till.ViewModel.ApprovalWhat);
        Assert.Equal(0m, till.ViewModel.Lines[0].Line.Discount);

        Approve(till);

        Assert.Equal(30m, till.ViewModel.Lines[0].Line.Discount);
        var given = Assert.Single(Recorded(till));
        Assert.Equal(TillEventKind.Discounted, given.Kind);
        Assert.Equal(30m, given.Amount);
        Assert.Equal(true, given.Approved);
        Assert.Contains("15.87% off Toor Dal 1kg", given.Detail);
    }

    [Fact]
    public void ADiscountWithinTheShareIsGivenAtOnceAndRecorded()
    {
        using var till = Till(a => a.DiscountAbovePercent = 10m);
        till.Scan(Dal);

        Discount(till, "10");

        Assert.False(till.ViewModel.IsApproving);
        Assert.Equal(10m, till.ViewModel.Lines[0].Line.Discount);
        Assert.Null(Assert.Single(Recorded(till)).Approved);
    }

    /// <summary>Less off, or none, is never something to ask the owner about.</summary>
    [Fact]
    public void LoweringADiscountIsNeverAsked()
    {
        using var till = Till(a => a.DiscountAbovePercent = 0m);
        till.Scan(Dal);

        Discount(till, "40");
        Approve(till);

        Discount(till, "20");
        Assert.False(till.ViewModel.IsApproving);
        Assert.Equal(20m, till.ViewModel.Lines[0].Line.Discount);

        Discount(till, "0");
        Assert.False(till.ViewModel.IsApproving);
        Assert.Equal(0m, till.ViewModel.Lines[0].Line.Discount);

        // The two given; taking it off is not a discount.
        Assert.Equal(2, Recorded(till).Count(e => e.Kind == TillEventKind.Discounted));
    }

    // ---- Refunds ---------------------------------------------------------------------------------

    /// <summary>F9, the last bill, one back, done picking, the reason - which issues it.</summary>
    private static void ReturnOne(BillingHarness till, int refundDown = 0)
    {
        till.Press(Key.F9);
        till.Press(Key.Enter);
        Type(till, "1");
        till.Press(Key.Enter);

        for (var i = 0; i < refundDown; i++)
            till.Press(Key.Down);

        Type(till, "wrong brand");
    }

    [Fact]
    public void ARefundInCashWaitsForTheOwner()
    {
        using var till = Till(a => a.CashRefunds = true);
        var sale = Sell(till);

        ReturnOne(till);

        Assert.True(till.ViewModel.IsApproving);
        Assert.Contains("refund ₹189.00 in cash", till.ViewModel.ApprovalWhat, StringComparison.OrdinalIgnoreCase);
        Assert.True(till.ViewModel.IsReturning);

        Approve(till);

        Assert.False(till.ViewModel.IsReturning);
        Assert.Contains("hand back ₹189.00 in cash", till.ViewModel.StatusMessage);

        var refunded = Assert.Single(Recorded(till));
        Assert.Equal(TillEventKind.CashRefunded, refunded.Kind);
        Assert.Equal(true, refunded.Approved);
        Assert.Contains(sale.InvoiceNo, refunded.Detail);
    }

    /// <summary>A refund by UPI leaves its own trail at the bank, and goes through without asking.</summary>
    [Fact]
    public void ARefundByUpiIsNotAsked()
    {
        using var till = Till(a => a.CashRefunds = true);
        Sell(till);

        ReturnOne(till, refundDown: 1);

        Assert.False(till.ViewModel.IsApproving);
        Assert.False(till.ViewModel.IsReturning);
        Assert.Contains("by UPI", till.ViewModel.StatusMessage);
        Assert.Empty(Recorded(till));
    }

    // ---- Cash out of the drawer ------------------------------------------------------------------

    private static void Float(BillingHarness till)
    {
        till.Press(Key.M, ModifierKeys.Control);
        Type(till, "3000");
    }

    [Fact]
    public void CashTakenOutWaitsForTheOwnerButTheFloatDoesNot()
    {
        using var till = Till(a => a.CashOut = true);

        Float(till);
        Assert.False(till.ViewModel.IsApproving);

        till.Press(Key.M, ModifierKeys.Control);
        for (var i = 0; i < 4; i++)
            till.Press(Key.Down);

        Type(till, "1000");
        Type(till, "to the bank");

        Assert.True(till.ViewModel.IsApproving);
        Assert.Contains("take ₹1,000.00 out of the drawer", till.ViewModel.ApprovalWhat, StringComparison.OrdinalIgnoreCase);

        Approve(till);

        Assert.Contains("₹1,000.00 taken out of the drawer", till.ViewModel.StatusMessage);
        var taken = Assert.Single(Recorded(till));
        Assert.Equal(TillEventKind.CashTakenOut, taken.Kind);
        Assert.Equal("to the bank", taken.Detail);
        Assert.Equal(true, taken.Approved);
    }

    [Fact]
    public void AnExpensePaidFromTheDrawerWaitsForTheOwner()
    {
        using var till = Till(a => a.CashOut = true);
        Float(till);

        till.Press(Key.M, ModifierKeys.Control);
        Type(till, "120");
        till.Press(Key.Down);
        Type(till, "auto");

        Assert.True(till.ViewModel.IsApproving);

        till.Press(Key.Escape);

        Assert.True(till.ViewModel.IsUsingDrawer);
        Assert.Empty(till.CashDrawer.Expenses(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddMinutes(1)));
    }

    // ---- Closing the day -------------------------------------------------------------------------

    [Fact]
    public void ClosingTheDayWaitsForTheOwner()
    {
        using var till = Till(a => a.CloseDay = true);
        Sell(till);

        till.Press(Key.F12, ModifierKeys.Shift);
        Type(till, "189");
        till.Press(Key.F12, ModifierKeys.Shift);

        Assert.True(till.ViewModel.IsApproving);
        Assert.Null(till.DayCloses.FindLatest(BillingHarness.LaneId));

        Approve(till);

        Assert.StartsWith("Day closed.", till.ViewModel.StatusMessage);

        // The count typed before the owner's PIN is the one kept with the close.
        Assert.Equal(189m, till.DayCloses.FindLatest(BillingHarness.LaneId)!.CashCounted);
        Assert.Equal(true, Assert.Single(Recorded(till), e => e.Kind == TillEventKind.DayClosed).Approved);
    }

    // ---- Signing on ------------------------------------------------------------------------------

    private static readonly (string, string)[] Staff = [("Murugan", "1357"), ("Lakshmi", "2468")];

    [Fact]
    public void ACashierSignsOnWithTheirNameAndTheirOwnPin()
    {
        using var till = Till(cashiers: Staff);

        Assert.True(till.Press(Key.U, ModifierKeys.Control));
        Assert.True(till.ViewModel.IsSettingCashier);
        Assert.True(till.ViewModel.UsesCashierPins);
        Assert.Equal(["Murugan", "Lakshmi"], till.ViewModel.CashierChoices);

        till.Press(Key.Down);
        till.ViewModel.CashierPin = "2468";
        till.Press(Key.Enter);

        Assert.False(till.ViewModel.IsSettingCashier);
        Assert.Equal("Lakshmi", till.ViewModel.CashierName);
        Assert.Equal("Lakshmi", Assert.Single(Recorded(till), e => e.Kind == TillEventKind.SignedOn).Cashier);
    }

    /// <summary>Another cashier's PIN is not yours: the PIN has to belong to the name picked.</summary>
    [Fact]
    public void SomebodyElsesPinDoesNotSignYouOn()
    {
        using var till = Till(cashiers: Staff);

        till.Press(Key.U, ModifierKeys.Control);
        till.ViewModel.CashierPin = "2468";
        till.Press(Key.Enter);

        Assert.True(till.ViewModel.IsSettingCashier);
        Assert.Null(till.ViewModel.CashierName);
        Assert.Contains("not Murugan's PIN", till.ViewModel.StatusMessage);
    }

    [Fact]
    public void AWrongPinThreeTimesIsRecordedAndNobodyIsSignedOn()
    {
        using var till = Till(cashiers: Staff);
        till.Press(Key.U, ModifierKeys.Control);

        for (var i = 0; i < 3; i++)
        {
            till.ViewModel.CashierPin = "0000";
            till.Press(Key.Enter);
        }

        Assert.False(till.ViewModel.IsSettingCashier);
        Assert.Null(till.ViewModel.CashierName);
        Assert.Equal("Murugan", Assert.Single(Recorded(till), e => e.Kind == TillEventKind.SignOnRefused).Cashier);
    }

    /// <summary>On a lane with cashiers, no money is taken until somebody has signed on - and then it is theirs.</summary>
    [Fact]
    public void NoMoneyIsTakenUntilSomebodySignsOnAndThenTheSaleIsTheirs()
    {
        using var till = Till(cashiers: Staff);
        till.Scan(Dal);

        till.Press(Key.F12);

        Assert.False(till.ViewModel.IsTendering);
        Assert.Contains("Sign on before taking money", till.ViewModel.StatusMessage);
        Assert.Contains("Ctrl+U", till.ViewModel.StatusMessage);

        till.Press(Key.U, ModifierKeys.Control);
        till.ViewModel.CashierPin = "1357";
        till.Press(Key.Enter);

        till.Press(Key.F12);
        till.Press(Key.Enter);
        till.Press(Key.Enter);

        Assert.Equal("Murugan", till.Invoices.FindLatest(BillingHarness.LaneId)!.Sale.CashierName);
    }

    /// <summary>A lane with no cashiers set up keeps the typed name, and takes money without a sign-on.</summary>
    [Fact]
    public void ALaneWithNoCashiersTypesANameAsBefore()
    {
        using var till = Till();

        till.Press(Key.U, ModifierKeys.Control);

        Assert.False(till.ViewModel.UsesCashierPins);
        Assert.True(till.ViewModel.TypesCashierName);
        Assert.False(till.ViewModel.NeedsSignOn);

        Type(till, "Selvi");
        Assert.Equal("Selvi", till.ViewModel.CashierName);
    }

    // ---- The real check --------------------------------------------------------------------------

    /// <summary>The till is wired to the real, deliberately slow PIN check, not only to a stand-in.</summary>
    [Fact]
    public void TheRealPinCheckApprovesTheRealPin()
    {
        using var till = new BillingHarness(Catalogue.Item(sku: "DAL001", barcode: Dal, name: "Toor Dal 1kg", price: 189m));
        var settings = new PosSettings { Approvals = { Voids = true } };
        settings.Security.DashboardPin = DashboardLock.Create("4826");
        till.ViewModel.Security = new TillSecurity(settings, new TillEventRepository(till.Database));

        var sale = Sell(till);
        AskToVoid(till);

        Approve(till, "4825");
        Assert.True(till.ViewModel.IsApproving);

        Approve(till, "4826");
        Assert.True(till.Invoices.FindByInvoiceNo(sale.InvoiceNo)!.IsVoided);
    }
}
