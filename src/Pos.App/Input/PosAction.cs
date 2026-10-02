namespace Pos.App.Input;

/// <summary>
/// Everything the cashier can do from the keyboard. Actions are named for intent, not for a key,
/// because the key that triggers them is configurable.
/// </summary>
/// <remarks>
/// Up, down, commit and cancel are single actions rather than one pair per pane. What they act on
/// depends on where the cashier is — the open result list or the line grid — and that context
/// belongs in the view model, not spread across the keymap.
/// </remarks>
public enum PosAction
{
    /// <summary>Put the caret in the search box, ready to scan or type.</summary>
    FocusSearch,

    MoveUp,
    MoveDown,

    /// <summary>Take the current result, or finish the cell being edited.</summary>
    Commit,

    /// <summary>Back out: close the result list, or abandon the cell being edited.</summary>
    Cancel,

    DeleteLine,
    IncrementQuantity,
    DecrementQuantity,
    EditQuantity,
    EditDiscount,

    HoldBill,
    RecallBill,
    NewBill,

    /// <summary>Open the tender pane and start taking payment.</summary>
    Tender,

    /// <summary>Attach a customer to the bill, by mobile number.</summary>
    FindCustomer,

    /// <summary>Print a duplicate of a past invoice.</summary>
    ReprintInvoice,

    /// <summary>Close the day and print the Z-report.</summary>
    CloseDay,

    /// <summary>Cancel a sale that has already been settled.</summary>
    VoidInvoice,

    /// <summary>Say who is on the till, at the start of a shift or when it changes.</summary>
    SetCashier,

    /// <summary>Take a customer's payment against what they owe on credit.</summary>
    ReceivePayment,

    /// <summary>
    /// Open the owner's screen: the figures, what needs reordering, and the lane's settings.
    /// </summary>
    OwnerView,

    /// <summary>Take goods back against a past bill, and issue a credit note for them.</summary>
    ReturnGoods,

    /// <summary>Record the float, an expense, or cash put into or taken out of the drawer.</summary>
    CashDrawer,

    /// <summary>Add a loose item - nothing to scan - off its quick key.</summary>
    QuickKeys,

    /// <summary>Take an order over the phone or on WhatsApp, or save the bill as one.</summary>
    TakeOrder,

    /// <summary>While taking UPI: print the code with the amount, for the customer to scan.</summary>
    PrintUpiCode,

    /// <summary>Print the customer's khata statement, and copy a message of it to send them.</summary>
    KhataStatement,

    /// <summary>The bill on the customer's WhatsApp: instead of paper while paying, or afterwards.</summary>
    DigitalBill,

    /// <summary>The customer on the bill is a business: their GSTIN and address.</summary>
    BusinessCustomer,

    /// <summary>An item not in the catalogue: its name, price and GST slab, typed in and flagged for the owner.</summary>
    OpenItem,

    /// <summary>
    /// Every key and what it does, read off the keymap - including the ones with no room on the
    /// strip, such as the UPI slip, the khata statement and voiding a sale.
    /// </summary>
    ShowKeys,
}

/// <summary>What each action is called on the key sheet and the strip.</summary>
public static class PosActionText
{
    /// <summary>A short description, in the order and groups the key sheet lists them.</summary>
    public static IReadOnlyList<(string Group, PosAction Action, string Text)> Sheet { get; } =
    [
        ("The bill", PosAction.FocusSearch, "Scan or search"),
        ("The bill", PosAction.Commit, "Add the item, or go ahead"),
        ("The bill", PosAction.Cancel, "Back out"),
        ("The bill", PosAction.MoveUp, "Up a line or a choice"),
        ("The bill", PosAction.MoveDown, "Down a line or a choice"),
        ("The bill", PosAction.EditQuantity, "Change the quantity"),
        ("The bill", PosAction.IncrementQuantity, "One more"),
        ("The bill", PosAction.DecrementQuantity, "One fewer"),
        ("The bill", PosAction.EditDiscount, "Discount the line"),
        ("The bill", PosAction.DeleteLine, "Remove the line"),
        ("The bill", PosAction.QuickKeys, "Loose items off their keys"),
        ("The bill", PosAction.OpenItem, "An item not in the catalogue"),
        ("The bill", PosAction.HoldBill, "Hold the bill"),
        ("The bill", PosAction.RecallBill, "Take back a held bill or an order"),
        ("The bill", PosAction.NewBill, "Start a new bill"),
        ("Paying", PosAction.Tender, "Pay and print"),
        ("Paying", PosAction.PrintUpiCode, "Print the UPI code with the amount"),
        ("Paying", PosAction.DigitalBill, "The bill on WhatsApp instead of paper"),
        ("Customers", PosAction.FindCustomer, "Put a customer on the bill"),
        ("Customers", PosAction.BusinessCustomer, "A business customer's GSTIN"),
        ("Customers", PosAction.ReceivePayment, "Take a khata payment"),
        ("Customers", PosAction.KhataStatement, "Print a khata statement"),
        ("Customers", PosAction.TakeOrder, "Take a phone or WhatsApp order"),
        ("Customers", PosAction.ReturnGoods, "Goods coming back"),
        ("The day", PosAction.CashDrawer, "Float, expenses, cash in and out"),
        ("The day", PosAction.ReprintInvoice, "Reprint a bill"),
        ("The day", PosAction.VoidInvoice, "Void a paid bill"),
        ("The day", PosAction.SetCashier, "Who is on the till"),
        ("The day", PosAction.OwnerView, "The owner's screen"),
        ("The day", PosAction.CloseDay, "Close the day"),
        ("The day", PosAction.ShowKeys, "This sheet"),
    ];
}
