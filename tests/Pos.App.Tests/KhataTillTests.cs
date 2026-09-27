using System.Text;
using System.Windows.Input;
using Pos.App.Input;
using Pos.App.ViewModels;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// Credit at the counter: selling on the khata, and taking it back with F8 - driven by keystrokes,
/// the way a cashier does it.
/// </summary>
public class KhataTillTests
{
    private static BillingHarness Till() => new(
        Catalogue.Item(sku: "DAL001", barcode: "8901234567890", name: "Toor Dal 1kg", price: 189m));

    private static void Attach(BillingHarness till, string mobile)
    {
        till.Press(Key.F7);
        till.ViewModel.EditBuffer = mobile;
        till.Press(Key.Enter);
    }

    /// <summary>F12, down to store credit, take the whole bill on it, finish.</summary>
    private static void PayOnCredit(BillingHarness till)
    {
        till.Press(Key.F12);
        till.Press(Key.Down);
        till.Press(Key.Down);
        till.Press(Key.Down);

        Assert.Equal(TenderType.StoreCredit, till.ViewModel.SelectedTenderType);

        till.Press(Key.Enter);
        till.Press(Key.Enter);
    }

    private static void SellOnCredit(BillingHarness till, string mobile)
    {
        Attach(till, mobile);
        till.Scan("8901234567890");
        PayOnCredit(till);
    }

    // ---- Selling on credit ------------------------------------------------------------------------

    /// <summary>
    /// Said in the tender pane, where it can be put right, and not only refused by checkout at the
    /// end with the whole payment already entered.
    /// </summary>
    [Fact]
    public void StoreCreditIsRefusedOnAWalkInBill()
    {
        using var till = Till();
        till.Scan("8901234567890");

        PayOnCredit(till);

        Assert.True(till.ViewModel.IsTendering);
        Assert.Contains("needs a customer", till.ViewModel.StatusMessage);
        Assert.Null(till.Invoices.FindLatest(BillingHarness.LaneId));
    }

    [Fact]
    public void ACreditSaleSaysWhatTheCustomerNowOwes()
    {
        using var till = Till();
        till.AddCustomer("9876543210", name: "Lakshmi");

        SellOnCredit(till, "9876543210");

        Assert.False(till.ViewModel.IsTendering);
        Assert.Contains("Lakshmi now owes 189.00", till.ViewModel.StatusMessage);
        Assert.Equal(189m, till.Credit.Balance(till.Customers.FindByMobile("9876543210")!.Id));
    }

    /// <summary>The side panel says what they owe while they are standing at the counter.</summary>
    [Fact]
    public void AttachingSomebodyWhoOwesShowsItOnThePanel()
    {
        using var till = Till();
        till.AddCustomer("9876543210", name: "Lakshmi");
        SellOnCredit(till, "9876543210");

        Attach(till, "9876543210");

        Assert.True(till.ViewModel.CustomerOwesAnything);
        Assert.Equal(189m, till.ViewModel.CustomerOwes);
    }

    [Fact]
    public void SomebodyWhoOwesNothingShowsNoBalance()
    {
        using var till = Till();
        till.AddCustomer("9876543210", name: "Lakshmi");

        Attach(till, "9876543210");

        Assert.False(till.ViewModel.CustomerOwesAnything);
    }

    // ---- Taking it back: F8 -----------------------------------------------------------------------

    private static BillingHarness TillWithADebt()
    {
        var till = Till();
        till.AddCustomer("9876543210", name: "Lakshmi");
        SellOnCredit(till, "9876543210");
        return till;
    }

    private static void FindForPayment(BillingHarness till, string typed = "Lak")
    {
        till.Press(Key.F8);
        Assert.True(till.ViewModel.IsCollecting);

        till.ViewModel.EditBuffer = typed;
        till.Press(Key.Down);
        till.Press(Key.Enter);
    }

    [Fact]
    public void F8FindsTheCustomerByNameAndSaysWhatTheyOwe()
    {
        using var till = TillWithADebt();

        FindForPayment(till);

        Assert.True(till.ViewModel.IsCollectingAmount);
        Assert.Contains("owes 189.00", till.ViewModel.CollectPrompt);
    }

    [Fact]
    public void APartPaymentInCashIsTakenOpensTheDrawerAndPrintsASlip()
    {
        using var till = TillWithADebt();
        var kicksBefore = till.Drawer.KickCount;

        FindForPayment(till);
        till.ViewModel.EditBuffer = "100";
        till.Press(Key.Enter);

        Assert.False(till.ViewModel.IsCollecting);
        Assert.Contains("Lakshmi paid 100.00 by cash", till.ViewModel.StatusMessage);
        Assert.Contains("89.00 still owed", till.ViewModel.StatusMessage);
        Assert.Equal(kicksBefore + 1, till.Drawer.KickCount);

        var slip = Encoding.Latin1.GetString(till.Printer.LastJob);
        Assert.Contains("PAYMENT RECEIVED", slip);
        Assert.Contains("Not a tax invoice", slip);
        Assert.Contains("89.00", slip);
    }

    /// <summary>Enter with nothing typed settles the whole khata, as a blank tender takes the whole bill.</summary>
    [Fact]
    public void CommittingWithNoAmountTakesAllOfIt()
    {
        using var till = TillWithADebt();

        FindForPayment(till);
        till.Press(Key.Enter);

        Assert.Contains("Nothing more owed", till.ViewModel.StatusMessage);
        Assert.Equal(0m, till.Credit.Balance(till.Customers.FindByMobile("9876543210")!.Id));
    }

    /// <summary>UPI goes to the bank, so the drawer stays shut.</summary>
    [Fact]
    public void AUpiPaymentDoesNotOpenTheDrawer()
    {
        using var till = TillWithADebt();
        var kicksBefore = till.Drawer.KickCount;

        FindForPayment(till);
        till.Press(Key.Down);
        till.ViewModel.EditBuffer = "50";
        till.Press(Key.Enter);

        Assert.Contains("by UPI", till.ViewModel.StatusMessage);
        Assert.Equal(kicksBefore, till.Drawer.KickCount);
    }

    /// <summary>Too much is refused, and the box is left as it was so it can be corrected.</summary>
    [Fact]
    public void TakingMoreThanIsOwedIsRefusedWithoutLeavingThePayment()
    {
        using var till = TillWithADebt();

        FindForPayment(till);
        till.ViewModel.EditBuffer = "500";
        till.Press(Key.Enter);

        Assert.True(till.ViewModel.IsCollecting);
        Assert.Contains("owe 189.00", till.ViewModel.StatusMessage);
        Assert.Equal("500", till.ViewModel.EditBuffer);
    }

    [Fact]
    public void SomebodyWhoOwesNothingIsTurnedAwayBeforeAnAmountIsAskedFor()
    {
        using var till = Till();
        till.AddCustomer("9876543210", name: "Lakshmi");

        FindForPayment(till);

        Assert.False(till.ViewModel.IsCollectingAmount);
        Assert.Contains("owes nothing", till.ViewModel.StatusMessage);
    }

    /// <summary>
    /// A repayment is not part of a sale. Taking one halfway through somebody else's bill is how the
    /// two get mixed up at the counter and on the report.
    /// </summary>
    [Fact]
    public void F8WaitsForAnEmptyBill()
    {
        using var till = TillWithADebt();
        till.Scan("8901234567890");

        till.Press(Key.F8);

        Assert.False(till.ViewModel.IsCollecting);
        Assert.Contains("Finish, park or clear the bill first", till.ViewModel.StatusMessage);
    }

    [Fact]
    public void EscapeTakesNoPayment()
    {
        using var till = TillWithADebt();

        FindForPayment(till);
        till.ViewModel.EditBuffer = "100";
        till.Press(Key.Escape);

        Assert.False(till.ViewModel.IsCollecting);
        Assert.Equal(189m, till.Credit.Balance(till.Customers.FindByMobile("9876543210")!.Id));
    }

    [Fact]
    public void F8IsTheKeyForTakingAPayment()
    {
        Assert.Equal(PosAction.ReceivePayment, Keymap.Default.Resolve(Key.F8, ModifierKeys.None));
    }
}
