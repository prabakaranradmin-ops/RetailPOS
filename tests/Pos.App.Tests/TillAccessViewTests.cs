using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pos.App.Input;
using Pos.App.Views;
using Pos.Core.Configuration;
using Pos.Core.Data;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// The PIN boxes on the real billing window: what is typed reaches the till, nothing typed is shown,
/// and the box is empty again after every try.
/// </summary>
/// <remarks>
/// A PIN cannot be bound like the other boxes, since a password box will not give its text to a
/// binding, so it is carried by hand from the box to the till. These check that hand-off on the window
/// itself rather than on the view model.
/// </remarks>
public class TillAccessViewTests
{
    private const string Dal = "8901234567890";

    private static PinCredential Fake(string pin) => new() { Salt = "c2FsdA==", Hash = pin, Iterations = 1 };

    private static BillingHarness Till(PosSettings settings)
    {
        var till = new BillingHarness(Catalogue.Item(sku: "DAL001", barcode: Dal, name: "Toor Dal 1kg", price: 189m));
        till.ViewModel.Security = new TillSecurity(settings, new TillEventRepository(till.Database),
            (pin, stored) => stored is not null && pin == stored.Hash);
        return till;
    }

    private static PosSettings Lane() => new() { LaneId = "L1", OutletStateCode = "33", Store = { Name = "Sri Lakshmi Stores" } };

    [Fact]
    public void TheOwnersPinTypedInTheBoxApprovesAndTheBoxIsEmptiedAfter()
    {
        var settings = Lane();
        settings.Security.DashboardPin = Fake("4826");
        settings.Approvals.Voids = true;

        using var till = Till(settings);

        till.Scan(Dal);
        till.Press(Key.F12);
        till.Press(Key.Enter);
        till.Press(Key.Enter);
        var sale = till.Invoices.FindLatest(BillingHarness.LaneId)!;

        Wpf.Run(() =>
        {
            var window = new MainBillingView(till.ViewModel, Keymap.Default, settings);

            try
            {
                Wpf.LayOutAt(window, 1366, 698);

                till.Press(Key.V, ModifierKeys.Control | ModifierKeys.Shift);
                till.Press(Key.Enter);
                till.Press(Key.Enter);
                Wpf.Settle(window);

                var pane = (FrameworkElement)window.FindName("ApprovalPane");
                var box = (PasswordBox)window.FindName("ApprovalBox");

                Assert.True(pane.IsVisible);

                // A wrong one: refused, and the box is empty for the next try.
                box.Password = "1111";
                till.Press(Key.Enter);
                Wpf.Settle(window);

                Assert.True(till.ViewModel.IsApproving);
                Assert.Equal(string.Empty, box.Password);

                box.Password = "4826";
                till.Press(Key.Enter);
                Wpf.Settle(window);

                Assert.False(pane.IsVisible);
                Assert.Equal(string.Empty, box.Password);
                Assert.True(till.Invoices.FindByInvoiceNo(sale.InvoiceNo)!.IsVoided);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ACashierSignsOnFromTheListAndTheirOwnPinBox()
    {
        var settings = Lane();
        settings.Cashiers.Add(new CashierSettings { Name = "Murugan", Pin = Fake("1357") });
        settings.Cashiers.Add(new CashierSettings { Name = "Lakshmi", Pin = Fake("2468") });

        using var till = Till(settings);

        Wpf.Run(() =>
        {
            var window = new MainBillingView(till.ViewModel, Keymap.Default, settings);

            try
            {
                Wpf.LayOutAt(window, 1366, 698);

                till.Press(Key.U, ModifierKeys.Control);
                Wpf.Settle(window);

                var list = (ListBox)window.FindName("CashierList");
                var pin = (PasswordBox)window.FindName("CashierPinBox");
                var typed = (TextBox)window.FindName("CashierBox");

                // The list and the PIN box, and not the box for a typed name.
                Assert.True(list.IsVisible);
                Assert.True(pin.IsVisible);
                Assert.False(typed.IsVisible);

                till.Press(Key.Down);
                Wpf.Settle(window);
                Assert.Equal(1, list.SelectedIndex);

                pin.Password = "2468";
                till.Press(Key.Enter);
                Wpf.Settle(window);

                Assert.Equal("Lakshmi", till.ViewModel.CashierName);
                Assert.Equal(string.Empty, pin.Password);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
