using System.Windows.Input;
using Pos.App.ViewModels;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// Reaching the owner's screen from the till, and the one thing it changes about billing.
/// </summary>
/// <remarks>
/// The screen exists because the figures and the reorder list were previously only reachable from a
/// command line, and a shopkeeper is not going to open a terminal to find out what to order. These
/// tests are about the route in and the effect out; the screen's own logic is in
/// <see cref="OwnerViewModelTests"/>.
/// </remarks>
public class OwnerScreenTests
{
    private static Item Soap() =>
        Catalogue.Item(id: 1, sku: "SOAP", barcode: "8901234567896", name: "Bath Soap", price: 48m, gstRate: 18m);

    [Fact]
    public void CtrlDAsksForTheOwnerScreen()
    {
        using var till = new BillingHarness(Soap());

        var asked = 0;
        till.ViewModel.OwnerViewRequested += (_, _) => asked++;

        Assert.True(till.Press(Key.D, ModifierKeys.Control));
        Assert.Equal(1, asked);
    }

    /// <summary>
    /// Everything behind it is read-only or a settings change, but a cashier halfway through taking
    /// money should not have another window take the keyboard out from under them.
    /// </summary>
    [Fact]
    public void ItIsRefusedWhileAPaymentIsBeingTaken()
    {
        using var till = new BillingHarness(Soap());
        till.Scan("8901234567896");
        till.Press(Key.F12);

        var asked = 0;
        till.ViewModel.OwnerViewRequested += (_, _) => asked++;

        till.Press(Key.D, ModifierKeys.Control);

        Assert.Equal(0, asked);
        Assert.Contains("payment", till.ViewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Switching what the lane issues ----------------------------------------------------------

    [Fact]
    public void TheOwnerCanSwitchThisLaneToBillsOfSupply()
    {
        using var till = new BillingHarness(Soap());

        Assert.True(till.ViewModel.ShowsTax);

        Assert.Null(till.ViewModel.TrySetTaxMode(TaxMode.Composition));
        Assert.False(till.ViewModel.ShowsTax);

        // And the next line rung up carries no tax at all.
        till.Scan("8901234567896");
        Assert.Equal(48m, till.ViewModel.Totals.GrandTotal);
        Assert.Equal(0m, till.ViewModel.Totals.TotalCgst);
        Assert.Equal(48m, till.ViewModel.Totals.SubtotalTaxable);
    }

    [Fact]
    public void AndBackAgain()
    {
        using var till = new BillingHarness(Soap());

        till.ViewModel.TrySetTaxMode(TaxMode.Composition);
        Assert.Null(till.ViewModel.TrySetTaxMode(TaxMode.Gst));

        Assert.True(till.ViewModel.ShowsTax);

        till.Scan("8901234567896");
        Assert.True(till.ViewModel.Totals.TotalCgst > 0m);
    }

    /// <summary>
    /// The screen's tax columns follow this property, so it has to announce itself changing or the
    /// grid would keep showing columns for a tax the lane no longer charges.
    /// </summary>
    [Fact]
    public void TheScreenIsToldSoTheTaxColumnsCanFollow()
    {
        using var till = new BillingHarness(Soap());

        var announced = 0;
        till.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null or nameof(BillingViewModel.ShowsTax))
                announced++;
        };

        till.ViewModel.TrySetTaxMode(TaxMode.Composition);

        Assert.True(announced > 0);
    }

    /// <summary>
    /// Refused while a bill is on screen. Every line already carries the tax it was rung up with, so
    /// switching mid-bill would leave one bill holding lines priced two ways and a total that
    /// reconciles with neither.
    /// </summary>
    [Fact]
    public void ItCannotBeSwitchedWithABillOnTheScreen()
    {
        using var till = new BillingHarness(Soap());
        till.Scan("8901234567896");

        var refused = till.ViewModel.TrySetTaxMode(TaxMode.Composition);

        Assert.NotNull(refused);
        Assert.Contains("clear the bill", refused, StringComparison.OrdinalIgnoreCase);

        // And nothing moved: the lane still issues what it did, and the bill is untouched.
        Assert.True(till.ViewModel.ShowsTax);
        Assert.Single(till.ViewModel.Lines);
        Assert.True(till.ViewModel.Totals.TotalCgst > 0m);
    }

    [Fact]
    public void ClearingTheBillMakesItSwitchableAgain()
    {
        using var till = new BillingHarness(Soap());
        till.Scan("8901234567896");

        Assert.NotNull(till.ViewModel.TrySetTaxMode(TaxMode.Composition));

        till.Press(Key.N, ModifierKeys.Control);
        till.Press(Key.N, ModifierKeys.Control);

        Assert.Empty(till.ViewModel.Lines);
        Assert.Null(till.ViewModel.TrySetTaxMode(TaxMode.Composition));
    }

    /// <summary>
    /// A sale settled before the switch keeps the mode it was issued under, so the shop's history
    /// stays exactly as it was billed.
    /// </summary>
    [Fact]
    public void SwitchingDoesNotRewriteWhatHasAlreadyBeenSold()
    {
        using var till = new BillingHarness(Soap());

        till.Scan("8901234567896");
        till.Press(Key.F12);
        till.ViewModel.EditBuffer = "48";
        till.Press(Key.Enter);
        till.Press(Key.Enter);

        var sold = till.Invoices.FindLatest(BillingHarness.LaneId)!;
        Assert.Equal(TaxMode.Gst, sold.Sale.TaxMode);

        till.ViewModel.TrySetTaxMode(TaxMode.Composition);

        var reread = till.Invoices.FindByInvoiceNo(sold.InvoiceNo)!;
        Assert.Equal(TaxMode.Gst, reread.Sale.TaxMode);
    }
}
