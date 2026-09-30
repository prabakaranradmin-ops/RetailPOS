using System.ComponentModel;
using System.Globalization;
using Pos.Core.Domain;

namespace Pos.App.ViewModels;

public sealed partial class BillingViewModel
{
    private UpiPayee? _upi;

    /// <summary>
    /// The reference a merchant UPI ID's payment carries: the lane and the moment the payment pane
    /// opened. Fixed for the whole payment, so the code does not change while it is being scanned.
    /// </summary>
    private string? _upiReference;

    /// <summary>What the customer paying back in F8 owed when they were picked: what the code asks for at most.</summary>
    private decimal _upiCollectOwed;

    /// <summary>
    /// The shop's UPI ID, for a QR code with the exact amount while taking payment. Null on a lane
    /// with none set: UPI is then taken as before, against the shop's own printed code.
    /// </summary>
    public UpiPayee? Upi
    {
        get => _upi;
        set
        {
            _upi = value;
            RaiseUpi();
        }
    }

    /// <summary>
    /// Prints a slip with the code on it, for a counter with no screen facing the customer. Set by
    /// the composition root; returns why it did not print, or null.
    /// </summary>
    public Func<UpiPayee, decimal, string, string?>? PrintUpiSlip { get; set; }

    /// <summary>
    /// True with UPI picked where money is being taken: the payment pane for a bill, or F8 for a
    /// khata once the customer is picked.
    /// </summary>
    private bool UpiPicked => IsTendering
        ? SelectedTenderType == TenderType.Upi
        : IsCollectingAmount && CollectTenders[Math.Clamp(SelectedCollectTenderIndex, 0, CollectTenders.Count - 1)] == TenderType.Upi;

    /// <summary>
    /// What the code asks for, with UPI picked: the amount typed, or everything still due - on the
    /// bill, or on the khata - when nothing is. Nothing, and no code, for more than that or for no
    /// amount.
    /// </summary>
    public decimal UpiAmount
    {
        get
        {
            if (!UpiPicked)
                return 0m;

            var most = IsTendering ? Math.Max(0m, _basket?.Remaining ?? 0m) : Math.Max(0m, _upiCollectOwed);
            var typed = EditBuffer.Trim();

            if (typed.Length == 0)
                return most;

            return TryParseAmount(typed, out var amount) && amount > 0m && amount <= most ? amount : 0m;
        }
    }

    /// <summary>The <c>upi://pay</c> link the code carries, or null when there is none to show.</summary>
    /// <remarks>
    /// A khata payment carries a note naming the customer's number, so the shop can tell in its own
    /// UPI app whose money it was.
    /// </remarks>
    public string? UpiQr => _upi is { } payee && UpiAmount is var amount && amount > 0m
        ? UpiLink.For(payee, amount, note: IsCollecting && _collectCustomer is { } who ? $"Khata {who.MobileNo}" : null, reference: _upiReference)
        : null;

    public bool ShowsUpiQr => UpiQr is not null;

    /// <summary>The amount, large, beside the code: what the customer checks before approving.</summary>
    public string UpiCaption => ShowsUpiQr ? Show.Money(UpiAmount) : string.Empty;

    /// <summary>Who the money goes to, as the customer's app will show it.</summary>
    public string UpiPayeeLine => _upi is { } payee ? $"{payee.Name} - {payee.Id}" : string.Empty;

    /// <summary>
    /// With UPI picked and no code showing, why - so a cashier is never left wondering where it went.
    /// </summary>
    public string UpiHint
    {
        get
        {
            if (!UpiPicked || ShowsUpiQr)
                return string.Empty;

            if (_upi is null)
                return "No UPI ID is set for a QR code with the amount. The owner sets it in Settings (Ctrl+D).";

            var nothingLeft = IsTendering ? _basket is { Remaining: <= 0m } : _upiCollectOwed <= 0m;

            return nothingLeft
                ? "Nothing is left to pay."
                : "The amount typed is not one the customer can be asked for: more than is due, or not an amount.";
        }
    }

    public bool HasUpiHint => UpiHint.Length > 0;

    /// <summary>
    /// The UPI side of F8 while there is no code on it: why not, with UPI picked; how to get one,
    /// with cash or card.
    /// </summary>
    /// <remarks>
    /// The side was there only while the code was, so the khata pane widened by the code's width
    /// when UPI was picked and narrowed again when it was not - the card jumped sideways on an arrow
    /// key. It now keeps the space, and says something useful in it.
    /// </remarks>
    public string CollectUpiNote
    {
        get
        {
            if (!IsCollectingAmount || ShowsUpiQr)
                return string.Empty;

            if (UpiPicked)
                return UpiHint;

            return _upi is null
                ? "With the shop's UPI ID set, a code with the amount shows here. The owner sets it in Settings (Ctrl+D)."
                : "Pick UPI with the arrows, and a code with the amount shows here for their phone to scan.";
        }
    }

    /// <summary>Ctrl+Q: prints the code with the amount on a slip, for the customer to scan.</summary>
    public void PrintUpiCode()
    {
        if (!IsTendering && !IsCollectingAmount)
        {
            StatusMessage = "Ctrl+Q prints a UPI code with the amount while taking payment: F12 for a bill, or F8 for a khata, then UPI.";
            return;
        }

        if (_upi is null)
        {
            StatusMessage = "No UPI ID is set for this shop. The owner sets it in Settings (Ctrl+D).";
            return;
        }

        if (!UpiPicked)
        {
            StatusMessage = "Pick UPI first with the arrows, then Ctrl+Q prints its code.";
            return;
        }

        if (UpiQr is not { } link)
        {
            StatusMessage = UpiHint;
            return;
        }

        var amount = UpiAmount;

        if (PrintUpiSlip is not { } print)
        {
            StatusMessage = "This lane has no printer for the code. The customer can scan it on the screen.";
            return;
        }

        StatusMessage = print(_upi, amount, link) is { } problem
            ? $"The code did not print: {problem}"
            : $"UPI code for {Show.Money(amount)} printed. Take it once the customer's app says it is paid, with {CommitKey}.";
    }

    /// <summary>Everything the code depends on, so it follows what is typed and picked as it changes.</summary>
    private void OnChangedForUpi(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Mode))
            _upiReference = IsTendering || IsCollecting ? $"{_laneId}{_now():yyMMddHHmmss}" : null;

        // Read once, when the customer is picked: not on every keystroke of the amount.
        if (e.PropertyName == nameof(IsCollectingAmount))
            _upiCollectOwed = _collectCustomer is { } customer ? SafeOwed(customer) : 0m;

        if (e.PropertyName is nameof(EditBuffer) or nameof(SelectedTenderType) or nameof(Mode) or nameof(AmountRemaining)
            or nameof(IsCollectingAmount) or nameof(SelectedCollectTenderIndex))
        {
            RaiseUpi();
        }
    }

    private void RaiseUpi()
    {
        Raise(nameof(UpiAmount));
        Raise(nameof(UpiQr));
        Raise(nameof(ShowsUpiQr));
        Raise(nameof(UpiCaption));
        Raise(nameof(UpiPayeeLine));
        Raise(nameof(UpiHint));
        Raise(nameof(HasUpiHint));
        Raise(nameof(CollectUpiNote));
    }
}
