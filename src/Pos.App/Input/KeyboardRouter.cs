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

    private void Dispatch(PosAction action)
    {
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

            // Reached only if a new PosAction is added without wiring it here. Failing loudly in a
            // debug run beats a key that silently does nothing at the till.
            default: throw new NotSupportedException($"No handler is wired for {action}.");
        }
    }
}
