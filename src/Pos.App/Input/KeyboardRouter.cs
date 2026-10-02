using System.Windows.Input;

namespace Pos.App.Input;

/// <summary>
/// What a key gesture can ask the billing screen to do. The view model implements it; the router
/// knows nothing about billing beyond this list.
/// </summary>
public interface IBillingActions
{
    void FocusSearch();
    void MoveUp();
    void MoveDown();
    void Commit();
    void Cancel();
    void DeleteLine();
    void IncrementQuantity();
    void DecrementQuantity();
    void EditQuantity();
    void EditDiscount();
    void HoldBill();
    void RecallBill();
    void NewBill();
    void Tender();
    void FindCustomer();
    void ReprintInvoice();
    void CloseDay();
    void VoidInvoice();
    void SetCashier();

    /// <summary>Take a customer's payment against what they owe on credit.</summary>
    void ReceivePayment();

    /// <summary>Open the owner's screen: the figures, the reorder list and the lane's settings.</summary>
    void OwnerView();

    /// <summary>Take goods back against a past bill, on a credit note.</summary>
    void ReturnGoods();

    /// <summary>The float, an expense, or cash in or out of the drawer.</summary>
    void CashDrawer();

    /// <summary>A loose item off its quick key.</summary>
    void QuickKeys();

    /// <summary>An order over the phone or on WhatsApp.</summary>
    void TakeOrder();

    /// <summary>The UPI code with the amount, on paper.</summary>
    void PrintUpiCode();

    /// <summary>A customer's khata statement, on paper and on the clipboard.</summary>
    void PrintKhataStatement();

    /// <summary>A bill to the customer's phone.</summary>
    void SendDigitalBill();

    /// <summary>A GSTIN for the customer on the bill.</summary>
    void SetBusiness();

    /// <summary>An item not in the catalogue, typed in.</summary>
    void AddOpenItem();

    /// <summary>The key sheet: open it, or close it again.</summary>
    void ShowKeys();

    /// <summary>
    /// True while the till takes only Enter and Esc - the owner's PIN being asked for - so no other
    /// key can step round it.
    /// </summary>
    bool HoldsTheKeyboard => false;

    /// <summary>
    /// True while the till takes only Up, Down, Enter and Esc - a question about the item in hand -
    /// so no key edits the bill behind the question or walks away from it. Typing still reaches the
    /// pane's own box.
    /// </summary>
    bool TakesOnlyAChoice => false;
}

/// <summary>
/// Turns a key press into an action. The single place where a keystroke becomes intent, which is
/// what keeps the keymap swappable and the view models free of key handling.
/// </summary>
public sealed class KeyboardRouter
{
    private readonly IBillingActions _target;

    public KeyboardRouter(Keymap keymap, IBillingActions target)
    {
        ArgumentNullException.ThrowIfNull(keymap);
        ArgumentNullException.ThrowIfNull(target);

        Keymap = keymap;
        _target = target;
    }

    public Keymap Keymap { get; set; }

    /// <summary>
    /// Runs the action bound to this gesture.
    /// </summary>
    /// <returns>
    /// True if the gesture was bound and handled, so the caller can mark the key event handled and
    /// stop it reaching the focused control. False leaves the key to normal text entry.
    /// </returns>
    public bool Handle(Key key, ModifierKeys modifiers)
    {
        var action = Keymap.Resolve(key, modifiers);

        if (action is null)
            return false;

        Dispatch(action.Value);
        return true;
    }

    /// <summary>
    /// Runs an action without a key: a click on the key strip at the foot of the screen, which does
    /// exactly what the key it shows does.
    /// </summary>
    public void Run(PosAction action) => Dispatch(action);

    private void Dispatch(PosAction action)
    {
        // Swallowed, not passed on: a key that opened something else while the owner's PIN was
        // asked for would be a way round the question.
        if (_target.HoldsTheKeyboard && action is not (PosAction.Commit or PosAction.Cancel))
            return;

        if (_target.TakesOnlyAChoice && action is not (PosAction.MoveUp or PosAction.MoveDown or PosAction.Commit or PosAction.Cancel))
            return;

        switch (action)
        {
            case PosAction.FocusSearch: _target.FocusSearch(); break;
            case PosAction.MoveUp: _target.MoveUp(); break;
            case PosAction.MoveDown: _target.MoveDown(); break;
            case PosAction.Commit: _target.Commit(); break;
            case PosAction.Cancel: _target.Cancel(); break;
            case PosAction.DeleteLine: _target.DeleteLine(); break;
            case PosAction.IncrementQuantity: _target.IncrementQuantity(); break;
            case PosAction.DecrementQuantity: _target.DecrementQuantity(); break;
            case PosAction.EditQuantity: _target.EditQuantity(); break;
            case PosAction.EditDiscount: _target.EditDiscount(); break;
            case PosAction.HoldBill: _target.HoldBill(); break;
            case PosAction.RecallBill: _target.RecallBill(); break;
            case PosAction.NewBill: _target.NewBill(); break;
            case PosAction.Tender: _target.Tender(); break;
            case PosAction.FindCustomer: _target.FindCustomer(); break;
            case PosAction.ReprintInvoice: _target.ReprintInvoice(); break;
            case PosAction.CloseDay: _target.CloseDay(); break;
            case PosAction.VoidInvoice: _target.VoidInvoice(); break;
            case PosAction.SetCashier: _target.SetCashier(); break;
            case PosAction.ReceivePayment: _target.ReceivePayment(); break;
            case PosAction.OwnerView: _target.OwnerView(); break;
            case PosAction.ReturnGoods: _target.ReturnGoods(); break;
            case PosAction.CashDrawer: _target.CashDrawer(); break;
            case PosAction.QuickKeys: _target.QuickKeys(); break;
            case PosAction.TakeOrder: _target.TakeOrder(); break;
            case PosAction.PrintUpiCode: _target.PrintUpiCode(); break;
            case PosAction.KhataStatement: _target.PrintKhataStatement(); break;
            case PosAction.DigitalBill: _target.SendDigitalBill(); break;
            case PosAction.BusinessCustomer: _target.SetBusiness(); break;
            case PosAction.OpenItem: _target.AddOpenItem(); break;
            case PosAction.ShowKeys: _target.ShowKeys(); break;

            // Reached only if a new PosAction is added without wiring it here. Failing loudly in a
            // debug run beats a key that silently does nothing at the till.
            default: throw new NotSupportedException($"No handler is wired for {action}.");
        }
    }
}
