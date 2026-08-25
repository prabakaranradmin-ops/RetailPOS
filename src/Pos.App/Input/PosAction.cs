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
}
