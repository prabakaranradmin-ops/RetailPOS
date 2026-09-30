namespace Pos.App.ViewModels;

public sealed partial class BillingViewModel
{
    private bool _showingKeys;

    /// <summary>
    /// The key sheet is open: every key on this lane and what it does, read off the keymap.
    /// </summary>
    /// <remarks>
    /// Six actions have no room on the strip along the foot - the UPI slip, the khata statement,
    /// the bill on WhatsApp, a business customer, the cashier, voiding a sale - and two of them were
    /// mentioned nowhere on the screen at all. F1 is where anybody looks for help on Windows.
    /// </remarks>
    public bool IsShowingKeys
    {
        get => _showingKeys;
        private set => Set(ref _showingKeys, value);
    }

    /// <summary>Opens the key sheet, or closes it again. Esc closes it too, and so does any other action.</summary>
    public void ShowKeys()
    {
        var open = !_showingKeys;

        ClearPendingConfirmations();
        IsShowingKeys = open;
    }
}
