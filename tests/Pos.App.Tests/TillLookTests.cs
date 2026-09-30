using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Pos.App.Input;
using Pos.App.Views;
using Pos.Core.Configuration;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// The billing screen's newer parts: keys that can be clicked, an empty bill that says what to do,
/// counts in the right number.
/// </summary>
public class TillLookTests
{
    private const string DalBarcode = "8901234567890";

    private static Item Dal => Catalogue.Item(sku: "DAL001", barcode: DalBarcode, name: "Toor Dal 1kg", price: 189m, gstRate: 5m);

    private static PosSettings Settings() => new()
    {
        LaneId = "L1",
        OutletStateCode = "33",
        Store = { Name = "Sri Lakshmi Stores", Gstin = "33AABCS1429B1ZX" },
    };

    /// <summary>Opens the billing screen on the harness's till, runs the body, and closes it again.</summary>
    private static void OnScreen(BillingHarness harness, Action<MainBillingView> body) =>
        Wpf.Run(() =>
        {
            var window = new MainBillingView(harness.ViewModel, Keymap.Default, Settings());

            try
            {
                Wpf.LayOut(window, 1366, 768);
                body(window);
            }
            finally
            {
                window.Close();
            }
        });

    private static Button Pill(MainBillingView window, string label) =>
        Wpf.Descendants<Button>((DependencyObject)window.FindName("KeyPills"))
            .Single(b => b.DataContext is MainBillingView.KeyPill pill && pill.Label == label);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));

    /// <summary>
    /// The keys along the foot of the screen look like buttons, so a click does what the key does.
    /// </summary>
    [Fact]
    public void ClickingAKeyOnTheStripDoesWhatTheKeyDoes()
    {
        using var harness = new BillingHarness(Dal);

        OnScreen(harness, window =>
        {
            Click(Pill(window, "Customer"));

            Assert.True(harness.ViewModel.IsFindingCustomer);
        });
    }

    [Fact]
    public void ClickingPayTakesThePayment()
    {
        using var harness = new BillingHarness(Dal);
        harness.Scan(DalBarcode);

        OnScreen(harness, window =>
        {
            Click((Button)window.FindName("PayPill"));

            Assert.True(harness.ViewModel.IsTendering);
        });
    }

    /// <summary>
    /// The keys never take the keyboard: after a click the caret is still where the next scan lands.
    /// </summary>
    [Fact]
    public void TheKeysOnTheStripNeverTakeTheKeyboard()
    {
        using var harness = new BillingHarness(Dal);

        OnScreen(harness, window =>
        {
            Assert.All(Wpf.Descendants<Button>((DependencyObject)window.FindName("KeyPills")), b => Assert.False(b.Focusable));
            Assert.False(((Button)window.FindName("PayPill")).Focusable);
        });
    }

    /// <summary>A screen reader says the key and what it does: "F7, Customer".</summary>
    [Fact]
    public void EachKeyIsNamedForAScreenReader()
    {
        using var harness = new BillingHarness(Dal);

        OnScreen(harness, window =>
        {
            var pill = Pill(window, "Customer");

            Assert.Equal("F7, Customer", System.Windows.Automation.AutomationProperties.GetName(pill));
        });
    }

    [Fact]
    public void AnEmptyBillSaysWhatToDo()
    {
        using var harness = new BillingHarness(Dal);

        OnScreen(harness, window =>
        {
            var empty = (FrameworkElement)window.FindName("EmptyBill");

            Assert.True(empty.IsVisible);

            harness.Scan(DalBarcode);
            window.UpdateLayout();

            Assert.False(empty.IsVisible);
        });
    }

    /// <summary>The tenders lie across the pane, so ← and → move along them as ↑ and ↓ do.</summary>
    [Fact]
    public void LeftAndRightChooseTheTender()
    {
        using var harness = new BillingHarness(Dal);
        harness.Scan(DalBarcode);
        harness.Press(Key.F12);

        OnScreen(harness, window =>
        {
            var first = harness.ViewModel.SelectedTenderTypeIndex;

            Press(window, Key.Right);
            Assert.Equal(first + 1, harness.ViewModel.SelectedTenderTypeIndex);

            Press(window, Key.Left);
            Assert.Equal(first, harness.ViewModel.SelectedTenderTypeIndex);
        });
    }

    /// <summary>Outside the payment pane, left and right are the scan box's: they move its caret.</summary>
    [Fact]
    public void LeftAndRightAreLeftToTheScanBoxOtherwise()
    {
        using var harness = new BillingHarness(Dal);

        OnScreen(harness, window =>
        {
            var handled = Press(window, Key.Right);

            Assert.False(handled);
            Assert.False(harness.ViewModel.IsTendering);
        });
    }

    private static bool Press(Window window, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };

        window.RaiseEvent(args);
        return args.Handled;
    }

    /// <summary>Closing the day is set apart on the strip, in the danger colour; nothing else is.</summary>
    [Fact]
    public void OnlyClosingTheDayIsMarkedAsDangerous()
    {
        using var harness = new BillingHarness(Dal);

        OnScreen(harness, window =>
        {
            var pills = Wpf.Descendants<Button>((DependencyObject)window.FindName("KeyPills"))
                .Select(b => (MainBillingView.KeyPill)b.DataContext)
                .ToList();

            Assert.Equal([PosAction.CloseDay], pills.Where(p => p.IsDanger).Select(p => p.Action));
        });
    }

    /// <summary>
    /// Every box, list and table on the till has a name a screen reader can say - the panes' ones
    /// too, though they are hidden until their key is pressed.
    /// </summary>
    [Fact]
    public void EveryBoxListAndTableOnTheTillHasAName()
    {
        using var harness = new BillingHarness(Dal);

        OnScreen(harness, window =>
        {
            var unnamed = Wpf.Descendants<Control>(window)
                .Where(c => c is TextBox or ListBox or DataGrid)
                .Where(c => string.IsNullOrWhiteSpace(
                    System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(c)?.GetName()))
                .Select(c => string.IsNullOrEmpty(c.Name) ? c.GetType().Name : c.Name)
                .ToList();

            Assert.True(unnamed.Count == 0, "These have no name a screen reader can say: " + string.Join(", ", unnamed));
        });
    }

    // ---- The side panel --------------------------------------------------------------------------

    /// <summary>
    /// An empty bill shows no discount, payment or points sections: with them the panel scrolled
    /// on a 720-high screen, and "Discount 0.00" took the room.
    /// </summary>
    [Fact]
    public void TheSidePanelShowsOnlyWhatHasSomethingInIt()
    {
        using var harness = new BillingHarness(Dal);
        var vm = harness.ViewModel;

        harness.Scan(DalBarcode);
        Assert.False(vm.HasDiscount);
        Assert.False(vm.HasPayments);
        Assert.False(vm.HasCustomer);

        harness.Press(Key.F4);
        vm.EditBuffer = "9";
        harness.Press(Key.Enter);
        Assert.True(vm.HasDiscount);

        harness.Press(Key.F12);
        vm.EditBuffer = "50";
        harness.Press(Key.Enter);
        Assert.True(vm.HasPayments);
    }

    // ---- What is left to do about the last sale --------------------------------------------------

    /// <summary>
    /// "The receipt did not print" stays in view after the next message replaces it, until the next
    /// bill starts - it used to vanish at the next keystroke, before anybody had reprinted anything.
    /// </summary>
    [Fact]
    public void AWarningAboutTheLastSaleStaysUntilTheNextBill()
    {
        using var harness = new BillingHarness(Dal);
        var vm = harness.ViewModel;
        harness.Printer.FailWith = "out of paper";

        harness.Scan(DalBarcode);
        harness.Press(Key.F12);
        harness.Press(Key.Enter);
        harness.Press(Key.Enter);

        Assert.Contains("DID NOT PRINT", vm.StandingNote);
        Assert.False(vm.ShowsStandingNote);

        harness.Press(Key.F6);
        Assert.Equal("No parked bills.", vm.StatusMessage);
        Assert.True(vm.ShowsStandingNote);

        harness.Scan(DalBarcode);
        Assert.Equal(string.Empty, vm.StandingNote);
        Assert.False(vm.ShowsStandingNote);
    }

    [Fact]
    public void AnOrdinarySaleLeavesNothingStanding()
    {
        using var harness = new BillingHarness(Dal);

        harness.Scan(DalBarcode);
        harness.Press(Key.F12);
        harness.Press(Key.Enter);
        harness.Press(Key.Enter);

        Assert.Equal(string.Empty, harness.ViewModel.StandingNote);
    }

    /// <summary>Puts one dal on the khata for a customer, leaving the note that says what they owe.</summary>
    private static void SellOnKhata(BillingHarness harness, string mobile)
    {
        harness.Press(Key.F7);
        harness.ViewModel.EditBuffer = mobile;
        harness.Press(Key.Enter);
        harness.Scan(DalBarcode);
        harness.Press(Key.F12);
        harness.Press(Key.Down);
        harness.Press(Key.Down);
        harness.Press(Key.Down);
        harness.Press(Key.Enter);
        harness.Press(Key.Enter);
    }

    private static void TakeKhataPayment(BillingHarness harness, string mobile, string amount)
    {
        harness.Press(Key.F8);
        harness.ViewModel.EditBuffer = mobile;
        harness.Press(Key.Enter);
        harness.ViewModel.EditBuffer = amount;
        harness.Press(Key.Enter);
    }

    /// <summary>
    /// "Lakshmi now owes ₹189.00" goes once she pays some of it back: left up, it sat above
    /// "₹89.00 still owed" and the two disagreed.
    /// </summary>
    [Fact]
    public void TheLastSalesKhataNoteGoesWhenThatCustomerPays()
    {
        using var harness = new BillingHarness(Dal);
        harness.AddCustomer("9500012345", name: "Lakshmi");

        SellOnKhata(harness, "9500012345");
        Assert.Contains("Lakshmi now owes ₹189.00", harness.ViewModel.StandingNote);

        TakeKhataPayment(harness, "9500012345", "100");

        Assert.Equal("Lakshmi paid ₹100.00 by cash. ₹89.00 still owed.", harness.ViewModel.StatusMessage);
        Assert.Equal(string.Empty, harness.ViewModel.StandingNote);
    }

    /// <summary>Somebody else paying off their khata says nothing new about the last sale.</summary>
    [Fact]
    public void AnotherCustomersKhataPaymentLeavesTheNote()
    {
        using var harness = new BillingHarness(Dal);
        harness.AddCustomer("9500012345", name: "Lakshmi");
        harness.AddCustomer("9500054321", name: "Murugan");

        SellOnKhata(harness, "9500054321");
        SellOnKhata(harness, "9500012345");

        TakeKhataPayment(harness, "9500054321", "100");

        Assert.StartsWith("Murugan paid ₹100.00", harness.ViewModel.StatusMessage);
        Assert.Contains("Lakshmi now owes ₹189.00", harness.ViewModel.StandingNote);
        Assert.True(harness.ViewModel.ShowsStandingNote);
    }

    // ---- Counts ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(1, "item", "1 item")]
    [InlineData(3, "item", "3 items")]
    [InlineData(0, "item", "0 items")]
    [InlineData(2.5, "item", "2.5 items")]
    [InlineData(1, "person|people", "1 person")]
    [InlineData(4, "person|people", "4 people")]
    public void ACountTakesItsNounInTheRightNumber(double count, string noun, string expected) =>
        Assert.Equal(expected, new PluralConverter().Convert(count, typeof(string), noun, CultureInfo.InvariantCulture));
}
