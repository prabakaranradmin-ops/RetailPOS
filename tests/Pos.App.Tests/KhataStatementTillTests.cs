using System.Text;
using System.Windows.Input;
using Pos.App.ViewModels;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// The khata at the counter: Ctrl+K prints a customer's statement and puts a message of it on the
/// clipboard, and paying it back by UPI in F8 shows a code for what they owe.
/// </summary>
public class KhataStatementTillTests
{
    private static readonly UpiPayee Murugan = new("murugan.stores@okaxis", "Sri Murugan Stores");

    private static BillingHarness Till()
    {
        var till = new BillingHarness(
            Catalogue.Item(sku: "DAL001", barcode: "8901234567890", name: "Toor Dal 1kg", price: 189m));

        till.ViewModel.ShopName = "Sri Murugan Stores";
        return till;
    }

    /// <summary>Lakshmi buys a dal on credit: she owes 189.00.</summary>
    private static Customer OwesForADal(BillingHarness till)
    {
        var lakshmi = till.AddCustomer("9500012345", name: "Lakshmi");

        till.Press(Key.F7);
        till.ViewModel.EditBuffer = "9500012345";
        till.Press(Key.Enter);
        till.Scan("8901234567890");
        till.Press(Key.F12);
        till.Press(Key.Down);
        till.Press(Key.Down);
        till.Press(Key.Down);
        till.Press(Key.Enter);
        till.Press(Key.Enter);

        Assert.Equal(189m, till.Credit.Balance(lakshmi.Id));
        return lakshmi;
    }

    /// <summary>F8, and Lakshmi picked: the pane is waiting for what she pays.</summary>
    private static void PickInF8(BillingHarness till)
    {
        till.Press(Key.F8);
        till.ViewModel.EditBuffer = "9500012345";
        till.Press(Key.Enter);

        Assert.True(till.ViewModel.IsCollectingAmount);
    }

    // ---- Ctrl+K ----------------------------------------------------------------------------------

    [Fact]
    public void CtrlKInF8PrintsTheStatementAndCopiesAMessage()
    {
        using var till = Till();
        var copied = new List<string>();
        till.ViewModel.CopyText = copied.Add;
        OwesForADal(till);
        PickInF8(till);

        Assert.True(till.Press(Key.K, ModifierKeys.Control));

        Assert.Contains("KHATA STATEMENT", Encoding.ASCII.GetString(till.Printer.LastJob));
        Assert.Contains("Lakshmi's statement printed: ₹189.00 owed", till.ViewModel.StatusMessage);
        Assert.Contains("A message for them is on the clipboard", till.ViewModel.StatusMessage);
        Assert.StartsWith("Sri Murugan Stores: khata for Lakshmi", Assert.Single(copied));

        // Printing it changes nothing: she is still picked, still owing, the pane still open.
        Assert.True(till.ViewModel.IsCollectingAmount);
    }

    [Fact]
    public void CtrlKWorksForTheCustomerOnTheBill()
    {
        using var till = Till();
        OwesForADal(till);

        till.Press(Key.F7);
        till.ViewModel.EditBuffer = "9500012345";
        till.Press(Key.Enter);
        till.Press(Key.K, ModifierKeys.Control);

        Assert.Contains("Lakshmi's statement printed", till.ViewModel.StatusMessage);
    }

    [Fact]
    public void WithTheShopsUpiIdTheStatementCarriesTheCode()
    {
        using var till = Till();
        till.ViewModel.Upi = Murugan;
        OwesForADal(till);
        PickInF8(till);

        till.Press(Key.K, ModifierKeys.Control);

        Assert.Contains("murugan.stores@okaxis", Encoding.ASCII.GetString(till.Printer.LastJob));
    }

    [Fact]
    public void CtrlKWithNobodyPickedSaysHow()
    {
        using var till = Till();

        till.Press(Key.K, ModifierKeys.Control);
        Assert.Contains("F8 and pick them", till.ViewModel.StatusMessage);

        till.Press(Key.F8);
        till.Press(Key.K, ModifierKeys.Control);
        Assert.Contains("Pick the customer first", till.ViewModel.StatusMessage);
    }

    [Fact]
    public void ACustomerWhoOwesNothingHasNoStatementToPrint()
    {
        using var till = Till();
        till.AddCustomer("9500012345", name: "Lakshmi");
        var jobs = till.Printer.Jobs.Count;

        till.Press(Key.F7);
        till.ViewModel.EditBuffer = "9500012345";
        till.Press(Key.Enter);
        till.Press(Key.K, ModifierKeys.Control);

        Assert.Contains("nothing on the khata", till.ViewModel.StatusMessage);
        Assert.Equal(jobs, till.Printer.Jobs.Count);
    }

    // ---- Paying it back by UPI -------------------------------------------------------------------

    [Fact]
    public void PayingTheKhataByUpiShowsACodeForWhatIsOwed()
    {
        using var till = Till();
        till.ViewModel.Upi = Murugan;
        OwesForADal(till);
        PickInF8(till);

        Assert.False(till.ViewModel.ShowsUpiQr);

        till.Press(Key.Down);

        Assert.Equal(
            "upi://pay?pa=murugan.stores@okaxis&pn=Sri%20Murugan%20Stores&tn=Khata%209500012345&am=189.00&cu=INR",
            till.ViewModel.UpiQr);

        till.ViewModel.EditBuffer = "100";
        Assert.EndsWith("&am=100.00&cu=INR", till.ViewModel.UpiQr);

        till.ViewModel.EditBuffer = "200";
        Assert.False(till.ViewModel.ShowsUpiQr);
        Assert.Contains("more than is due", till.ViewModel.UpiHint);

        till.ViewModel.EditBuffer = "100";
        till.Press(Key.Enter);

        Assert.Equal(89m, till.Credit.Balance(till.Customers.FindByMobile("9500012345")!.Id));
        Assert.False(till.ViewModel.ShowsUpiQr);
    }

    [Fact]
    public void CtrlQPrintsTheKhataCode()
    {
        using var till = Till();
        till.ViewModel.Upi = Murugan;
        decimal? printed = null;
        till.ViewModel.PrintUpiSlip = (_, amount, _) =>
        {
            printed = amount;
            return null;
        };
        OwesForADal(till);
        PickInF8(till);
        till.Press(Key.Down);

        till.Press(Key.Q, ModifierKeys.Control);

        Assert.Equal(189m, printed);
    }

    /// <summary>
    /// The customer's screen shows the code while they pay their khata by UPI - and nothing about
    /// what they owe while they are only being looked up.
    /// </summary>
    [Fact]
    public void TheCustomersScreenShowsTheKhataCodeOnlyWhilePayingByUpi()
    {
        using var till = Till();
        till.ViewModel.Upi = Murugan;
        OwesForADal(till);
        using var display = new CustomerDisplayViewModel(till.ViewModel, "Sri Murugan Stores");

        PickInF8(till);
        Assert.NotEqual(CustomerDisplayState.Khata, display.State);
        Assert.Null(display.UpiQr);

        till.Press(Key.Down);
        Assert.Equal(CustomerDisplayState.Khata, display.State);
        Assert.Equal(till.ViewModel.UpiQr, display.UpiQr);
        Assert.Equal("₹189.00", display.UpiAmount);

        till.Press(Key.Escape);
        Assert.Equal(CustomerDisplayState.Welcome, display.State);
        Assert.Null(display.UpiQr);
    }
}
