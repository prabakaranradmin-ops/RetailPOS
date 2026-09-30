using System.Windows.Input;
using Pos.App.ViewModels;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// Orders over the phone or on WhatsApp, with Ctrl+O: pasted onto the bill, saved as an order, found
/// in F6, and paid for when the customer comes or the delivery goes.
/// </summary>
public class OrderTillTests
{
    private static BillingHarness Till()
    {
        var till = new BillingHarness(
            Catalogue.Item(sku: "DAL001", barcode: "8901234567890", name: "Toor Dal 1kg", price: 189m),
            Catalogue.Item(sku: "SUG001", name: "Sugar Loose", price: 45m, unit: UnitType.Kilogram));

        till.ViewModel.ShopName = "Sri Murugan Stores";
        return till;
    }

    private static void Paste(BillingHarness till, string message)
    {
        Assert.True(till.Press(Key.O, ModifierKeys.Control));
        Assert.True(till.ViewModel.IsTakingOrder);
        till.ViewModel.EditBuffer = message;
        till.Press(Key.Enter);
    }

    [Fact]
    public void APastedMessageGoesOnTheBill()
    {
        using var till = Till();

        Paste(till, "Hi\n1. toor dal 1kg x 2\n2. sugar 1/2 kg\n3. mangoes 2 kg");

        Assert.False(till.ViewModel.IsTakingOrder);
        Assert.Equal(2, till.ViewModel.Lines.Count);
        Assert.Equal(2m, till.ViewModel.Lines[0].Line.Quantity);
        Assert.Equal(0.5m, till.ViewModel.Lines[1].Line.Quantity);
        Assert.Contains("2 of 3 lines", till.ViewModel.StatusMessage);
        Assert.Contains("Not found: mangoes 2 kg", till.ViewModel.StatusMessage);

        // And kept under the bill, where the next keystroke - F7 for the customer - cannot wipe it.
        Assert.Equal(["mangoes 2 kg"], till.ViewModel.OrderMisses);

        till.Press(Key.F7);
        Assert.True(till.ViewModel.HasOrderMisses);
    }

    /// <summary>The misses go once the cashier has seen them: Esc on the bill, or the order saved.</summary>
    [Fact]
    public void EscPutsTheMissesAway()
    {
        using var till = Till();
        Paste(till, "toor dal 1kg\nmangoes 2 kg");
        Assert.True(till.ViewModel.HasOrderMisses);

        till.Press(Key.Escape);

        Assert.False(till.ViewModel.HasOrderMisses);
        Assert.Single(till.ViewModel.Lines);
    }

    [Fact]
    public void AnOrderNeedsTheCustomer()
    {
        using var till = Till();
        Paste(till, "sugar 2kg");

        till.Press(Key.O, ModifierKeys.Control);

        Assert.False(till.ViewModel.IsTakingOrder);
        Assert.Contains("Attach the customer first", till.ViewModel.StatusMessage);
    }

    /// <summary>Saved with where it is going: parked, the bill clears, and a reply is on the clipboard.</summary>
    [Fact]
    public void AnOrderIsSavedWithItsNoteAndAReplyToSend()
    {
        using var till = Till();
        var copied = new List<string>();
        till.ViewModel.CopyText = copied.Add;
        till.AddCustomer("9876543210", name: "Lakshmi");

        Paste(till, "sugar 2kg");
        till.Press(Key.F7);
        till.ViewModel.EditBuffer = "9876543210";
        till.Press(Key.Enter);

        till.Press(Key.O, ModifierKeys.Control);
        Assert.Equal(OrderStage.Save, till.ViewModel.OrderStage);
        till.Press(Key.Down);
        till.ViewModel.EditBuffer = "deliver to 12 North Street by 6pm";
        till.Press(Key.Enter);

        Assert.Empty(till.ViewModel.Lines);
        Assert.Contains("Order H001 saved for Lakshmi, ₹90.00", till.ViewModel.StatusMessage);

        var reply = Assert.Single(copied);
        Assert.Equal("Your order at Sri Murugan Stores: 1 item, Rs 90.00. deliver to 12 North Street by 6pm. We will let you know when it is ready. Order H001.", reply);

        var held = Assert.Single(till.HeldBills.List(BillingHarness.LaneId));
        Assert.True(held.IsOrder);
        Assert.Equal(OrderKind.WhatsApp, held.Order!.Kind);
        Assert.Equal("WhatsApp order - deliver to 12 North Street by 6pm", held.OrderLine);
    }

    /// <summary>Orders wait at the top of F6, and come back saying what they are.</summary>
    [Fact]
    public void AnOrderWaitsInTheRecallListAndIsPaidForLater()
    {
        using var till = Till();
        var customer = till.AddCustomer("9876543210", name: "Lakshmi");
        var sugar = till.Items.FindBySku("SUG001")!;
        var line = new InvoiceEngine("33").AddItem(sugar, 2m);

        till.HeldBills.Park(BillingHarness.LaneId, "H001", DateTimeOffset.Now.AddHours(-1), customer, [line], new OrderInfo(OrderKind.Phone, "collect at 6"));
        till.HeldBills.Park(BillingHarness.LaneId, "H002", DateTimeOffset.Now, null, [line]);

        till.Press(Key.F6);
        Assert.True(till.ViewModel.HeldBills[0].IsOrder);
        Assert.False(till.ViewModel.HeldBills[1].IsOrder);

        till.Press(Key.Enter);
        Assert.Contains("Order H001, Phone: collect at 6", till.ViewModel.StatusMessage);

        till.Press(Key.F12);
        till.Press(Key.Enter);
        till.Press(Key.Enter);

        Assert.NotNull(till.Invoices.FindLatest(BillingHarness.LaneId));
        Assert.DoesNotContain(till.HeldBills.List(BillingHarness.LaneId), h => h.Token == "H001");
    }

    [Fact]
    public void ARecalledOrderParkedAgainIsStillAnOrder()
    {
        using var till = Till();
        var customer = till.AddCustomer("9876543210", name: "Lakshmi");
        var line = new InvoiceEngine("33").AddItem(till.Items.FindBySku("SUG001")!, 2m);
        till.HeldBills.Park(BillingHarness.LaneId, "H001", DateTimeOffset.Now, customer, [line], new OrderInfo(OrderKind.WhatsApp, "deliver"));

        till.Press(Key.F6);
        till.Press(Key.Enter);
        till.Press(Key.F5);

        Assert.Contains("Order parked again", till.ViewModel.StatusMessage);
        Assert.Equal(OrderKind.WhatsApp, Assert.Single(till.HeldBills.List(BillingHarness.LaneId)).Order!.Kind);
    }

    /// <summary>Shift+Enter is not an action: it is left for the box, to start the next line of the order.</summary>
    [Fact]
    public void ShiftEnterIsLeftForTheBoxToStartANewLine()
    {
        using var till = Till();
        till.Press(Key.O, ModifierKeys.Control);

        Assert.False(till.Press(Key.Enter, ModifierKeys.Shift));
        Assert.True(till.ViewModel.IsTakingOrder);
    }

    [Fact]
    public void EscapeTakesNoOrder()
    {
        using var till = Till();
        till.Press(Key.O, ModifierKeys.Control);
        till.ViewModel.EditBuffer = "sugar 2kg";

        till.Press(Key.Escape);

        Assert.False(till.ViewModel.IsTakingOrder);
        Assert.Empty(till.ViewModel.Lines);
    }
}
