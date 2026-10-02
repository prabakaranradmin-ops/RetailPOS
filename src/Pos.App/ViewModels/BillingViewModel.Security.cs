using System.Collections.ObjectModel;
using Pos.Core.Configuration;
using Pos.Core.Domain;

namespace Pos.App.ViewModels;

/// <summary>
/// The cashiers' PINs, and the owner's PIN in front of the till's riskier actions.
/// </summary>
/// <remarks>
/// <para>
/// An approval is asked for at the last moment, when the action is about to happen: the void has
/// been found and confirmed, the discount typed, the refund chosen. The owner then approves the
/// thing itself, with its amount in front of them, rather than a screen somebody is about to use.
/// </para>
/// <para>
/// While the owner's PIN is being asked for, the till does nothing else. Enter tries the PIN and
/// Esc backs out, and every other key is ignored, so a refused approval cannot be stepped round by
/// opening something else.
/// </para>
/// </remarks>
public sealed partial class BillingViewModel
{
    /// <summary>How many wrong PINs before the till stops asking.</summary>
    public const int PinAttempts = 3;

    private TillSecurity _security = TillSecurity.None;
    private PendingApproval? _approval;
    private int _approvalMisses;
    private string _approvalPin = string.Empty;
    private string _cashierPin = string.Empty;
    private int _selectedCashierIndex;
    private int _signOnMisses;
    private bool _signedOn;

    /// <summary>Something waiting for the owner's PIN, and what to do once it is given.</summary>
    private sealed record PendingApproval(
        Guarded Action,
        string What,
        TillEventKind Kind,
        string? Reference,
        decimal? Amount,
        Action<bool?> Then);

    /// <summary>The cashiers, the approvals and the record. Nothing guarded and nothing recorded unless set.</summary>
    public TillSecurity Security
    {
        get => _security;
        set
        {
            _security = value ?? TillSecurity.None;
            RefreshCashierChoices();
            Raise(nameof(UsesCashierPins));
            Raise(nameof(TypesCashierName));
            Raise(nameof(NeedsSignOn));
        }
    }

    /// <summary>Raised when the PIN boxes on screen should be emptied: after every try, right or wrong.</summary>
    public event EventHandler? PinEntryCleared;

    /// <summary>The key that signs a cashier on, as the messages name it. Set from the lane's keymap.</summary>
    public string SignOnKey { get; set; } = "Ctrl+U";

    // ---- The owner's approval ------------------------------------------------------------------

    /// <summary>True while the till is waiting for the owner's PIN, and doing nothing else.</summary>
    public bool IsApproving => _approval is not null;

    /// <summary>What the owner is being asked to approve, in words with the amount in them.</summary>
    public string ApprovalWhat => _approval is { } pending ? Capitalise(pending.What) : string.Empty;

    /// <summary>Set from the PIN box on screen as it is typed. Never shown.</summary>
    public string ApprovalPin
    {
        get => _approvalPin;
        set => _approvalPin = value ?? string.Empty;
    }

    /// <summary>
    /// Holds every key but Enter and Esc while the owner's PIN is asked for.
    /// </summary>
    public bool HoldsTheKeyboard => IsApproving;

    /// <summary>
    /// Runs an action, first asking for the owner's PIN when this lane wants it asked.
    /// </summary>
    /// <param name="what">What is being approved, as the end of "The owner's PIN to …".</param>
    /// <param name="then">
    /// The action itself. It is told whether the owner approved it: true when they did, null when
    /// this lane did not ask.
    /// </param>
    private void Guard(
        Guarded action,
        string what,
        TillEventKind kind,
        string? reference,
        decimal? amount,
        Action<bool?> then,
        decimal discountPercent = 0m)
    {
        if (!_security.Needs(action, discountPercent))
        {
            then(null);
            return;
        }

        _approval = new PendingApproval(action, what, kind, reference, amount, then);
        _approvalMisses = 0;
        ClearPins();

        Raise(nameof(IsApproving));
        Raise(nameof(ApprovalWhat));

        StatusMessage = $"The owner's PIN to {what}. {CommitKey} to approve, {CancelKey} to back out.";
    }

    private void CommitApproval()
    {
        if (_approval is not { } pending)
            return;

        var pin = _approvalPin;
        ClearPins();

        if (_security.IsOwnersPin(pin))
        {
            CloseApproval();
            pending.Then(true);
            return;
        }

        _approvalMisses++;

        if (_approvalMisses < PinAttempts)
        {
            StatusMessage = $"That is not the owner's PIN. {PinAttempts - _approvalMisses} left.";
            return;
        }

        CloseApproval();
        Refused(pending, "the PIN was wrong three times");
        StatusMessage = $"Not approved: the PIN was wrong three times. Nothing was done.";
    }

    private void CancelApproval()
    {
        if (_approval is not { } pending)
            return;

        CloseApproval();
        Refused(pending, "backed out without the owner's PIN");
        StatusMessage = "Not approved. Nothing was done.";
    }

    private void Refused(PendingApproval pending, string why) =>
        _security.Record(_laneId, _now(), TillEventKind.ApprovalRefused, _cashierName,
            pending.Reference, pending.Amount, approved: false, detail: $"{Capitalise(pending.What)}: {why}.");

    private void CloseApproval()
    {
        _approval = null;
        _approvalMisses = 0;
        ClearPins();

        Raise(nameof(IsApproving));
        Raise(nameof(ApprovalWhat));
    }

    // ---- Signing on ----------------------------------------------------------------------------

    /// <summary>Whether the people on this till sign on with a PIN of their own.</summary>
    public bool UsesCashierPins => _security.UsesCashierPins;

    /// <summary>Whether the cashier types their name, as on a lane with no cashiers set up.</summary>
    public bool TypesCashierName => !UsesCashierPins;

    /// <summary>The cashiers to choose from when signing on.</summary>
    public ObservableCollection<string> CashierChoices { get; } = [];

    public int SelectedCashierIndex
    {
        get => _selectedCashierIndex;
        private set => Set(ref _selectedCashierIndex, value);
    }

    /// <summary>Set from the PIN box on screen as it is typed. Never shown.</summary>
    public string CashierPin
    {
        get => _cashierPin;
        set => _cashierPin = value ?? string.Empty;
    }

    /// <summary>
    /// Whether whoever is on the till has signed on with their PIN, on a lane that has PINs. Money is
    /// not taken until they have, or the sale would be recorded against nobody.
    /// </summary>
    public bool NeedsSignOn => UsesCashierPins && !_signedOn;

    private void RefreshCashierChoices()
    {
        CashierChoices.Clear();

        foreach (var name in _security.CashierNames)
            CashierChoices.Add(name);

        var current = _cashierName is null ? -1 : CashierChoices.IndexOf(_cashierName);
        SelectedCashierIndex = current >= 0 ? current : 0;
    }

    private void BeginSignOn()
    {
        RefreshCashierChoices();
        _signOnMisses = 0;
        ClearPins();

        Mode = BillingMode.Cashier;
        EditBuffer = string.Empty;

        StatusMessage = CashierChoices.Count == 0
            ? "No cashiers are set up. The owner adds them on the owner's screen, Settings."
            : $"Up and down to your name, type your PIN, {CommitKey}.";
    }

    private void MoveInSignOn(int delta)
    {
        if (CashierChoices.Count == 0)
            return;

        SelectedCashierIndex = Math.Clamp(_selectedCashierIndex + delta, 0, CashierChoices.Count - 1);
        ClearPins();
    }

    private void CommitSignOn()
    {
        if (CashierChoices.Count == 0)
            return;

        var name = CashierChoices[Math.Clamp(_selectedCashierIndex, 0, CashierChoices.Count - 1)];
        var pin = _cashierPin;
        ClearPins();

        if (_security.IsCashiersPin(name, pin))
        {
            CashierName = name;
            _signedOn = true;
            _signOnMisses = 0;
            Raise(nameof(NeedsSignOn));

            _security.Record(_laneId, _now(), TillEventKind.SignedOn, name);

            Mode = BillingMode.Billing;
            StatusMessage = $"{name} is on the till.";
            return;
        }

        _signOnMisses++;

        if (_signOnMisses < PinAttempts)
        {
            StatusMessage = $"That is not {name}'s PIN. {PinAttempts - _signOnMisses} left.";
            return;
        }

        _security.Record(_laneId, _now(), TillEventKind.SignOnRefused, name, detail: "The PIN was wrong three times.");

        Mode = BillingMode.Billing;
        StatusMessage = $"Not signed on: the PIN for {name} was wrong three times.";
    }

    private void ClearPins()
    {
        _approvalPin = string.Empty;
        _cashierPin = string.Empty;
        PinEntryCleared?.Invoke(this, EventArgs.Empty);
    }

    private static string Capitalise(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
