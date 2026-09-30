using System.Windows.Input;
using Pos.App.ViewModels;
using Pos.Core.Domain;
using Pos.Core.Hardware.Printing;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// Paying by UPI with a code for the exact amount: shown in the payment pane and on the customer's
/// screen as soon as UPI is picked, following what is typed, and printed on a slip with Ctrl+Q.
/// </summary>
public class UpiTillTests
{
    private static readonly UpiPayee Murugan = new("murugan.stores@okaxis", "Sri Murugan Stores");

    private static BillingHarness Till(UpiPayee? upi = null)
    {
        var till = new BillingHarness(
            Catalogue.Item(sku: "DAL001", barcode: "8901234567890", name: "Toor Dal 1kg", price: 189m),
            Catalogue.Item(sku: "SUG001", barcode: "8901234567906", name: "Sugar Loose", price: 45m));

        till.ViewModel.Upi = upi ?? Murugan;
        return till;
    }

    /// <summary>A bill of 234.00, with the payment pane open on UPI.</summary>
    private static void PayByUpi(BillingHarness till)
    {
        till.Scan("8901234567890");
        till.Scan("8901234567906");
        till.Press(Key.F12);
        till.Press(Key.Down);
        till.Press(Key.Down);

        Assert.Equal(TenderType.Upi, till.ViewModel.SelectedTenderType);
    }

    [Fact]
    public void PickingUpiShowsACodeForEverythingDue()
    {
        using var till = Till();

        PayByUpi(till);

        Assert.True(till.ViewModel.ShowsUpiQr);
        Assert.Equal("upi://pay?pa=murugan.stores@okaxis&pn=Sri%20Murugan%20Stores&am=234.00&cu=INR", till.ViewModel.UpiQr);
        Assert.Equal("₹234.00", till.ViewModel.UpiCaption);
        Assert.Equal("Sri Murugan Stores - murugan.stores@okaxis", till.ViewModel.UpiPayeeLine);
    }

    [Fact]
    public void CashShowsNoCode()
    {
        using var till = Till();
        PayByUpi(till);

        till.Press(Key.Up);
        till.Press(Key.Up);

        Assert.False(till.ViewModel.ShowsUpiQr);
        Assert.False(till.ViewModel.HasUpiHint);
    }

    /// <summary>Part by UPI: the code follows the amount typed, and is taken for that amount.</summary>
    [Fact]
    public void TheCodeFollowsTheAmountTyped()
    {
        using var till = Till();
        PayByUpi(till);

        till.ViewModel.EditBuffer = "100";

        Assert.EndsWith("&am=100.00&cu=INR", till.ViewModel.UpiQr);

        till.Press(Key.Enter);

        Assert.Equal(100m, till.ViewModel.TenderedUpi);
        Assert.EndsWith("&am=134.00&cu=INR", till.ViewModel.UpiQr);
    }

    [Fact]
    public void MoreThanIsDueIsNotAskedFor()
    {
        using var till = Till();
        PayByUpi(till);

        till.ViewModel.EditBuffer = "500";

        Assert.False(till.ViewModel.ShowsUpiQr);
        Assert.Contains("more than is due", till.ViewModel.UpiHint);
    }

    [Fact]
    public void WithNoUpiIdTheCashierIsToldWhy()
    {
        using var till = Till();
        till.ViewModel.Upi = null;
        PayByUpi(till);

        Assert.False(till.ViewModel.ShowsUpiQr);
        Assert.Contains("No UPI ID is set", till.ViewModel.UpiHint);

        // UPI is still taken, as it always was.
        till.Press(Key.Enter);
        till.Press(Key.Enter);
        Assert.NotNull(till.Invoices.FindLatest(BillingHarness.LaneId));
    }

    /// <summary>The cashier takes it once the customer's app says it is paid: an ordinary UPI tender.</summary>
    [Fact]
    public void EnterTakesTheUpiPaymentAndSettles()
    {
        using var till = Till();
        PayByUpi(till);

        till.Press(Key.Enter);
        till.Press(Key.Enter);

        Assert.False(till.ViewModel.IsTendering);
        Assert.False(till.ViewModel.ShowsUpiQr);
        Assert.Equal(TenderType.Upi, Assert.Single(till.ViewModel.LastSale!.Invoice.Sale.Payments).Type);
    }

    /// <summary>A merchant ID's reference is fixed for the payment, so the code is not changing under the phone.</summary>
    [Fact]
    public void AMerchantPaymentKeepsOneReference()
    {
        using var till = Till(Murugan with { MerchantCode = "5411" });
        PayByUpi(till);

        var first = Reference(till.ViewModel.UpiQr!);
        till.ViewModel.EditBuffer = "100";
        var second = Reference(till.ViewModel.UpiQr!);

        Assert.StartsWith(BillingHarness.LaneId, first);
        Assert.Equal(first, second);

        static string Reference(string link) => link.Split('&').Single(p => p.StartsWith("tr=", StringComparison.Ordinal))[3..];
    }

    // ---- Ctrl+Q --------------------------------------------------------------------------------

    [Fact]
    public void CtrlQPrintsTheCodeWithTheAmount()
    {
        using var till = Till();
        var printed = new List<(UpiPayee Payee, decimal Amount, string Link)>();
        till.ViewModel.PrintUpiSlip = (payee, amount, link) =>
        {
            printed.Add((payee, amount, link));
            return null;
        };
        PayByUpi(till);

        Assert.True(till.Press(Key.Q, ModifierKeys.Control));

        var slip = Assert.Single(printed);
        Assert.Equal(Murugan, slip.Payee);
        Assert.Equal(234m, slip.Amount);
        Assert.Equal(till.ViewModel.UpiQr, slip.Link);
        Assert.Contains("UPI code for ₹234.00 printed", till.ViewModel.StatusMessage);
        Assert.True(till.ViewModel.IsTendering);
    }

    [Fact]
    public void CtrlQOnTheTillItselfPrintsTheRealSlip()
    {
        using var till = Till();
        till.ViewModel.PrintUpiSlip = (payee, amount, link) =>
        {
            var outcome = till.Printer.Print(till.Receipts.ComposeUpiSlip(payee, amount, link).ToEscPos());
            return outcome.Succeeded ? null : outcome.Detail;
        };
        PayByUpi(till);

        till.Press(Key.Q, ModifierKeys.Control);

        Assert.Contains("SCAN TO PAY BY UPI", System.Text.Encoding.ASCII.GetString(till.Printer.LastJob));
    }

    [Fact]
    public void CtrlQIsRefusedAwayFromUpi()
    {
        using var till = Till();
        var printed = 0;
        till.ViewModel.PrintUpiSlip = (_, _, _) =>
        {
            printed++;
            return null;
        };

        till.Press(Key.Q, ModifierKeys.Control);
        Assert.Contains("while taking payment", till.ViewModel.StatusMessage);

        till.Scan("8901234567890");
        till.Press(Key.F12);
        till.Press(Key.Q, ModifierKeys.Control);
        Assert.Contains("Pick UPI first", till.ViewModel.StatusMessage);

        Assert.Equal(0, printed);
    }

    [Fact]
    public void APrinterThatFailsIsSaid()
    {
        using var till = Till();
        till.ViewModel.PrintUpiSlip = (_, _, _) => "out of paper";
        PayByUpi(till);

        till.Press(Key.Q, ModifierKeys.Control);

        Assert.Equal("The code did not print: out of paper", till.ViewModel.StatusMessage);
    }

    // ---- The customer's screen -------------------------------------------------------------------

    [Fact]
    public void TheCustomersScreenShowsTheCodeInPlaceOfTheLines()
    {
        using var till = Till();
        using var display = new CustomerDisplayViewModel(till.ViewModel, "Sri Murugan Stores");

        till.Scan("8901234567890");
        Assert.Null(display.UpiQr);
        Assert.True(display.ShowsLines);

        till.Press(Key.F12);
        Assert.Null(display.UpiQr);

        till.Press(Key.Down);
        till.Press(Key.Down);

        Assert.Equal(till.ViewModel.UpiQr, display.UpiQr);
        Assert.Equal("₹189.00", display.UpiAmount);
        Assert.False(display.ShowsLines);

        till.Press(Key.Escape);
        Assert.Null(display.UpiQr);
        Assert.True(display.ShowsLines);
    }
}
