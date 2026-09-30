using System.Collections.ObjectModel;
using System.Globalization;
using Pos.Core.Domain;
using Pos.Core.Hardware.Drawer;
using Pos.Core.Hardware.Printing;

namespace Pos.App.ViewModels;

/// <summary>Where a return has got to on the till.</summary>
public enum ReturnStage
{
    /// <summary>Which bill the goods were sold on.</summary>
    FindBill = 0,

    /// <summary>What is coming back, line by line.</summary>
    PickGoods = 1,

    /// <summary>How the money goes back, and why.</summary>
    Refund = 2,
}

/// <summary>One way a return can be refunded, as the till lists it.</summary>
public sealed record RefundOption(TenderType Type, string Label);

/// <summary>A line of the bill being returned against, with what is coming back from it.</summary>
public sealed class ReturnLineRow(ReturnableLine source) : ObservableObject
{
    private decimal _returning;
    private bool _damaged;
    private decimal _refund;

    public ReturnableLine Source { get; } = source ?? throw new ArgumentNullException(nameof(source));

    public int LineNo => Source.LineNo;

    public string Name => Source.Line.NameSnapshot;

    public string Unit => Units.ScreenLabel(Source.Line.Unit);

    public decimal Sold => Source.Sold;

    public decimal Remaining => Source.Remaining;

    public bool FullyReturned => Source.FullyReturned;

    /// <summary>What has already come back on earlier credit notes, said only when something has.</summary>
    public string Note => Source.FullyReturned
        ? "all returned"
        : Source.AlreadyReturned > 0m
            ? $"{Source.AlreadyReturned.ToString("0.###", CultureInfo.InvariantCulture)} returned before"
            : string.Empty;

    /// <summary>How much of it is coming back now. Zero when none.</summary>
    public decimal Returning
    {
        get => _returning;
        set
        {
            if (Set(ref _returning, value))
                Raise(nameof(IsPicked));
        }
    }

    /// <summary>Refunded but not put back on the shelf.</summary>
    public bool Damaged
    {
        get => _damaged;
        set => Set(ref _damaged, value);
    }

    /// <summary>What this line refunds, as priced the way it was sold.</summary>
    public decimal Refund
    {
        get => _refund;
        set => Set(ref _refund, value);
    }

    public bool IsPicked => _returning > 0m;
}

public sealed partial class BillingViewModel
{
    /// <summary>Returns, or null on a lane wired without them.</summary>
    private readonly ReturnService? _returns;

    private ReturnableBill? _returnBill;
    private ReturnStage _returnStage;
    private int _selectedReturnLineIndex = -1;
    private int _selectedRefundIndex;
    private CreditNoteDraft? _returnDraft;

    /// <summary>The lines of the bill being returned against.</summary>
    public ObservableCollection<ReturnLineRow> ReturnLines { get; } = [];

    /// <summary>How this return can be refunded. The khata only when the bill had a customer on it.</summary>
    public ObservableCollection<RefundOption> RefundOptions { get; } = [];

    public ReturnStage ReturnStage
    {
        get => _returnStage;
        private set
        {
            if (!Set(ref _returnStage, value))
                return;

            Raise(nameof(IsPickingReturn));
            Raise(nameof(IsChoosingRefund));
            Raise(nameof(ReturnPrompt));
        }
    }

    /// <summary>True once the bill is found, while the goods are being picked or refunded.</summary>
    public bool IsPickingReturn => _returnStage != ReturnStage.FindBill;

    public bool IsChoosingRefund => _returnStage == ReturnStage.Refund;

    public int SelectedReturnLineIndex
    {
        get => _selectedReturnLineIndex;
        private set => Set(ref _selectedReturnLineIndex, value);
    }

    public int SelectedRefundIndex
    {
        get => _selectedRefundIndex;
        private set => Set(ref _selectedRefundIndex, value);
    }

    /// <summary>Which bill, and when, over the lines.</summary>
    public string ReturnBillLabel => _returnBill is { } bill
        ? $"{bill.Invoice.InvoiceNo}  ·  {Show.DateAndTime(bill.Invoice.Sale.CreatedAt)}  ·  {Show.Money(bill.Invoice.AmountPayable)}"
            + (bill.Invoice.Sale.Customer is { } c ? $"  ·  {c.Name ?? c.MobileNo}" : string.Empty)
        : string.Empty;

    /// <summary>What the box is asking for at this stage.</summary>
    public string ReturnPrompt => _returnStage switch
    {
        ReturnStage.FindBill => $"Bill number - {CommitKey} for this lane's last bill",
        ReturnStage.PickGoods => $"Quantity coming back on the highlighted line (add d if damaged, a for all of it, * for the whole bill). {CommitKey} on an empty box when done.",
        _ => $"Reason (optional). Up and down for how it is refunded; {CommitKey} to refund.",
    };

    /// <summary>What the return comes to so far: the refund and the tax it takes back.</summary>
    public string ReturnSummary
    {
        get
        {
            if (_returnDraft is not { } draft)
                return ReturnLines.Any(l => l.IsPicked) ? string.Empty : "Nothing picked yet.";

            var tax = draft.Cgst + draft.Sgst + draft.Igst;
            var summary = $"Refund {Show.Money(draft.Refunded)}";

            if (tax != 0m)
                summary += $"  ·  tax taken back {Show.Money(tax)}";

            if (draft.RoundOff != 0m)
                summary += $"  ·  round off {(draft.RoundOff > 0m ? "+" : string.Empty)}{Show.Money(draft.RoundOff)}";

            if (draft.PointsReversed > 0)
                summary += $"  ·  {Plural.Of(draft.PointsReversed, "point")} off";

            return summary;
        }
    }

    /// <summary>
    /// Starts taking goods back against a past bill.
    /// </summary>
    /// <remarks>
    /// Only with the bill empty, as with a payment against credit: a return is its own document, and
    /// doing one halfway through somebody else's bill is how the two get mixed up.
    /// </remarks>
    public void ReturnGoods()
    {
        ClearPendingConfirmations();
        CancelEdit();

        if (_returns is null)
        {
            StatusMessage = "Returns are not available on this lane.";
            return;
        }

        if (Mode == BillingMode.Tender || !_bill.IsEmpty)
        {
            StatusMessage = "Finish, hold or clear the bill first - a return is its own document, not part of a sale.";
            return;
        }

        ResetReturn();
        Mode = BillingMode.Return;
        EditBuffer = string.Empty;

        StatusMessage = "Which bill were the goods sold on? Type its number, or press Enter for this lane's last bill.";
    }

    private void CommitReturn()
    {
        switch (_returnStage)
        {
            case ReturnStage.FindBill:
                FindReturnBill();
                return;

            case ReturnStage.PickGoods:
                PickReturnLine();
                return;

            case ReturnStage.Refund:
                IssueReturn();
                return;
        }
    }

    private void FindReturnBill()
    {
        if (_returns is null)
            return;

        var typed = EditBuffer.Trim();
        var number = typed.Length > 0 ? typed : _invoices?.FindLatest(_laneId)?.InvoiceNo;

        if (number is null)
        {
            StatusMessage = "This lane has not billed anything yet. Type the bill number.";
            return;
        }

        ReturnableBill? bill;

        try
        {
            bill = _returns.Find(number);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            StatusMessage = $"The bill could not be read: {ex.Message}";
            return;
        }

        if (bill is null)
        {
            StatusMessage = $"No bill '{number}'. Type it as printed, as in INV/26-27/L1-12.";
            return;
        }

        if (bill.Invoice.IsVoided)
        {
            StatusMessage = $"{bill.Invoice.InvoiceNo} was voided, so nothing on it can come back.";
            return;
        }

        if (!bill.AnythingLeft)
        {
            StatusMessage = $"Everything on {bill.Invoice.InvoiceNo} has already come back.";
            return;
        }

        _returnBill = bill;
        ReturnLines.Clear();

        foreach (var line in bill.Lines)
            ReturnLines.Add(new ReturnLineRow(line));

        RefundOptions.Clear();
        RefundOptions.Add(new RefundOption(TenderType.Cash, "Cash"));
        RefundOptions.Add(new RefundOption(TenderType.Upi, "UPI"));
        RefundOptions.Add(new RefundOption(TenderType.Card, "Card"));

        if (bill.Invoice.Sale.Customer is not null)
            RefundOptions.Add(new RefundOption(TenderType.StoreCredit, "Off their khata"));

        SelectedRefundIndex = 0;
        SelectedReturnLineIndex = ReturnLines.ToList().FindIndex(l => !l.FullyReturned);
        ReturnStage = ReturnStage.PickGoods;
        EditBuffer = string.Empty;
        RaiseReturn();

        StatusMessage = $"{bill.Invoice.InvoiceNo}: arrow to a line, type how many are coming back and press Enter.";
    }

    /// <summary>
    /// Takes what was typed as the quantity coming back on the highlighted line, or moves on to the
    /// refund when nothing was typed.
    /// </summary>
    private void PickReturnLine()
    {
        var typed = EditBuffer.Trim().ToLowerInvariant();

        if (typed.Length == 0)
        {
            if (!ReturnLines.Any(l => l.IsPicked))
            {
                StatusMessage = "Nothing is coming back yet. Type a quantity on a line, or * for the whole bill.";
                return;
            }

            if (Reprice() is null)
                return;

            ReturnStage = ReturnStage.Refund;
            StatusMessage = $"{ReturnSummary}. How is it being refunded? Up and down, then Enter.";
            return;
        }

        // The whole bill: everything still to come back, on every line.
        if (typed is "*" or "*d")
        {
            foreach (var row in ReturnLines.Where(r => !r.FullyReturned))
            {
                row.Returning = row.Remaining;
                row.Damaged = typed.EndsWith('d');
            }

            EditBuffer = string.Empty;

            if (Reprice() is not null)
                StatusMessage = $"The whole bill is coming back. {ReturnSummary}. Enter to choose the refund.";

            return;
        }

        if (SelectedReturnLine is not { } line)
        {
            StatusMessage = "Arrow to the line that is coming back.";
            return;
        }

        if (line.FullyReturned)
        {
            StatusMessage = $"All of {line.Name} has already come back.";
            return;
        }

        var damaged = typed.EndsWith('d');
        var figure = damaged ? typed[..^1].Trim() : typed;
        decimal quantity;

        if (figure is "a" or "all")
        {
            quantity = line.Remaining;
        }
        else if (!TryParseAmount(figure, out quantity) || quantity < 0m)
        {
            StatusMessage = $"'{EditBuffer.Trim()}' is not a quantity.";
            return;
        }

        if (quantity > line.Remaining)
        {
            StatusMessage = $"Only {line.Remaining:0.###} {line.Unit} of {line.Name} can come back.";
            return;
        }

        if (!line.Source.Line.Unit.AllowsFractionalQuantity() && decimal.Truncate(quantity) != quantity)
        {
            StatusMessage = $"{line.Name} was sold whole, and comes back whole.";
            return;
        }

        var before = (line.Returning, line.Damaged);
        line.Returning = quantity;
        line.Damaged = damaged && quantity > 0m;

        // Priced as it is typed, so a quantity the bill cannot take back is refused on the line it
        // was typed on rather than at the end. Nothing picked at all is not a refusal: it is a zero
        // typed over the only line that was coming back.
        if (Reprice() is null && ReturnLines.Any(l => l.IsPicked))
        {
            (line.Returning, line.Damaged) = before;
            Reprice();
            return;
        }

        EditBuffer = string.Empty;

        // On to the next line still open, so a bill of returns is typed straight down.
        var next = ReturnLines.ToList().FindIndex(_selectedReturnLineIndex + 1, r => !r.FullyReturned);

        if (next >= 0)
            SelectedReturnLineIndex = next;

        StatusMessage = quantity == 0m
            ? $"{line.Name} is not coming back. {ReturnSummary}"
            : $"{quantity:0.###} {line.Unit} of {line.Name}{(line.Damaged ? ", damaged" : string.Empty)}. {ReturnSummary}. Enter on an empty box when done.";
    }

    private ReturnLineRow? SelectedReturnLine =>
        _selectedReturnLineIndex >= 0 && _selectedReturnLineIndex < ReturnLines.Count ? ReturnLines[_selectedReturnLineIndex] : null;

    /// <summary>Prices what has been picked, line by line and in all. Null, with the reason said, if it cannot be.</summary>
    private CreditNoteDraft? Reprice()
    {
        _returnDraft = null;

        foreach (var row in ReturnLines)
            row.Refund = 0m;

        if (_returns is null || _returnBill is null || !ReturnLines.Any(l => l.IsPicked))
        {
            RaiseReturn();
            return null;
        }

        try
        {
            _returnDraft = _returns.Draft(_returnBill, Picked(), SelectedRefund, reason: null);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            StatusMessage = ex.Message.Split(" (Parameter", StringSplitOptions.None)[0];
            RaiseReturn();
            return null;
        }

        foreach (var priced in _returnDraft.Lines)
        {
            if (ReturnLines.FirstOrDefault(r => r.LineNo == priced.InvoiceLineNo) is { } row)
                row.Refund = priced.LineTotal;
        }

        RaiseReturn();
        return _returnDraft;
    }

    private List<(int LineNo, decimal Quantity, bool Restock)> Picked() =>
        ReturnLines.Where(r => r.IsPicked).Select(r => (r.LineNo, r.Returning, !r.Damaged)).ToList();

    private TenderType SelectedRefund =>
        RefundOptions.Count == 0 ? TenderType.Cash : RefundOptions[Math.Clamp(_selectedRefundIndex, 0, RefundOptions.Count - 1)].Type;

    private void IssueReturn()
    {
        if (_returns is null || _returnBill is null)
            return;

        CreditNoteDraft draft;

        try
        {
            draft = _returns.Draft(_returnBill, Picked(), SelectedRefund, EditBuffer);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            StatusMessage = ex.Message.Split(" (Parameter", StringSplitOptions.None)[0];
            return;
        }

        ReturnResult result;

        try
        {
            result = _returns.Issue(draft, _laneId);
        }
        catch (InvalidOperationException ex)
        {
            // Somebody returned from the same bill on another lane, the khata owes too little: said,
            // and the return left as it is so the refund can be changed rather than started again.
            StatusMessage = ex.Message;
            return;
        }

        var note = result.Note;
        var message = note.Refund switch
        {
            TenderType.StoreCredit => $"{note.Number}: {Show.Money(note.Refunded)} taken off their khata.",
            TenderType.Cash => $"{note.Number}: hand back {Show.Money(note.Refunded)} in cash.",
            _ => $"{note.Number}: refund {Show.Money(note.Refunded)} by {(note.Refund == TenderType.Upi ? "UPI" : "card")}.",
        };

        if (note.PointsReversed > 0)
            message += $" {Plural.Of(note.PointsReversed, "point")} taken back.";

        if (result.Drawer == DrawerKickResult.Failed)
            message += " THE DRAWER DID NOT OPEN - use the key.";

        if (result.Print.Status == PrintStatus.Failed)
            message += $" THE CREDIT NOTE DID NOT PRINT: {result.Print.Detail}.";

        ResetReturn();
        Mode = BillingMode.Billing;
        EditBuffer = string.Empty;
        StatusMessage = message;
    }

    private void MoveInReturn(int delta)
    {
        switch (_returnStage)
        {
            case ReturnStage.PickGoods when ReturnLines.Count > 0:
                SelectedReturnLineIndex = Math.Clamp(_selectedReturnLineIndex + delta, 0, ReturnLines.Count - 1);
                EditBuffer = string.Empty;
                return;

            case ReturnStage.Refund when RefundOptions.Count > 0:
                SelectedRefundIndex = Math.Clamp(_selectedRefundIndex + delta, 0, RefundOptions.Count - 1);

                // The khata can change the round-off and nothing else, but the summary says what
                // this refund comes to, so it is worked out again.
                Reprice();
                return;
        }
    }

    /// <summary>Delete on a line: it is not coming back after all.</summary>
    private void UnpickReturnLine()
    {
        if (_returnStage != ReturnStage.PickGoods || SelectedReturnLine is not { IsPicked: true } line)
            return;

        line.Returning = 0m;
        line.Damaged = false;
        Reprice();
        StatusMessage = $"{line.Name} is not coming back. {ReturnSummary}";
    }

    /// <summary>Escape: one stage back, and out of the return from its first.</summary>
    private void BackOutOfReturn()
    {
        switch (_returnStage)
        {
            case ReturnStage.Refund:
                ReturnStage = ReturnStage.PickGoods;
                EditBuffer = string.Empty;
                StatusMessage = "Back to the goods. Enter on an empty box when done.";
                return;

            case ReturnStage.PickGoods:
                ResetReturn();
                EditBuffer = string.Empty;
                StatusMessage = "Which bill were the goods sold on?";
                return;

            default:
                ResetReturn();
                Mode = BillingMode.Billing;
                EditBuffer = string.Empty;
                StatusMessage = "Nothing returned.";
                return;
        }
    }

    private void ResetReturn()
    {
        _returnBill = null;
        _returnDraft = null;
        ReturnLines.Clear();
        RefundOptions.Clear();
        SelectedReturnLineIndex = -1;
        SelectedRefundIndex = 0;
        ReturnStage = ReturnStage.FindBill;
        RaiseReturn();
    }

    private void RaiseReturn()
    {
        Raise(nameof(ReturnBillLabel));
        Raise(nameof(ReturnSummary));
        Raise(nameof(ReturnPrompt));
    }
}
