using System.Collections.ObjectModel;
using System.Globalization;
using Pos.App.Input;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Hardware.Drawer;
using Pos.Core.Hardware.Printing;
using Pos.Core.Loyalty;

namespace Pos.App.ViewModels;

public enum BillingMode
{
    Billing = 0,

    /// <summary>Picking a parked bill from the recall list.</summary>
    Recall = 1,

    /// <summary>Taking payment.</summary>
    Tender = 2,

    /// <summary>Looking a customer up by mobile number.</summary>
    Customer = 3,

    /// <summary>Finding a past invoice to print again.</summary>
    Reprint = 4,

    /// <summary>Finding a settled sale to cancel.</summary>
    Void = 5,

    /// <summary>Saying who is on the till.</summary>
    Cashier = 6,
}

/// <summary>The only cells the cashier can type into (SRS 2.2).</summary>
public enum EditableColumn
{
    Quantity = 0,
    Discount = 1,
}

/// <summary>
/// The billing screen. Owns the search box, the line grid, the tender pane and the parked-bill
/// list, and exposes every one of them as an action so the whole flow is reachable from the
/// keyboard (SRS UR-03).
/// </summary>
public sealed class BillingViewModel : ObservableObject, IBillingActions, IDisposable
{
    private readonly InvoiceEngine _bill;
    private readonly ItemRepository _items;
    private readonly IHeldBillStore _heldBills;
    private readonly ICustomerStore _customers;
    private readonly CheckoutService _checkout;
    private readonly string _laneId;
    private readonly ScannerInputClassifier _classifier;
    private readonly SearchDebouncer _debouncer;
    private readonly Func<DateTimeOffset> _now;

    private string _searchText = string.Empty;
    private string _resultsForText = string.Empty;
    private bool _suppressSearchNotification;

    private int _selectedResultIndex = -1;
    private int _selectedLineIndex = -1;
    private int _selectedHeldBillIndex = -1;
    private int _selectedTenderTypeIndex;

    private BillingMode _mode = BillingMode.Billing;
    private EditableColumn? _editingColumn;
    private string _editBuffer = string.Empty;
    private string _statusMessage = string.Empty;

    private TenderBasket? _basket;
    private int _pointsRedeemed;
    private string? _recalledFromToken;
    private CheckoutResult? _lastSale;

    private readonly IInvoiceStore? _invoices;
    private readonly DayCloseService? _dayClose;

    private string? _cashierName;
    private string? _pendingVoidInvoiceNo;

    private bool _pendingNewBillConfirmation;
    private bool _pendingCustomerCreate;
    private bool _pendingDayClose;
    private bool _disposed;

    public BillingViewModel(
        InvoiceEngine bill,
        ItemRepository items,
        IHeldBillStore heldBills,
        ICustomerStore customers,
        CheckoutService checkout,
        string laneId,
        IDelayScheduler scheduler,
        IClock clock,
        TimeSpan? debounceWindow = null,
        TimeSpan? maxKeystrokeGap = null,
        Func<DateTimeOffset>? now = null,
        IInvoiceStore? invoices = null,
        DayCloseService? dayClose = null,
        string? cashierName = null)
    {
        ArgumentNullException.ThrowIfNull(bill);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(heldBills);
        ArgumentNullException.ThrowIfNull(customers);
        ArgumentNullException.ThrowIfNull(checkout);
        ArgumentException.ThrowIfNullOrWhiteSpace(laneId);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(clock);

        _invoices = invoices;
        _dayClose = dayClose;
        _cashierName = string.IsNullOrWhiteSpace(cashierName) ? null : cashierName.Trim();
        _bill = bill;
        _items = items;
        _heldBills = heldBills;
        _customers = customers;
        _checkout = checkout;
        _laneId = laneId;
        _now = now ?? (() => DateTimeOffset.Now);
        _classifier = new ScannerInputClassifier(clock, maxKeystrokeGap);
        _debouncer = new SearchDebouncer(scheduler, OnDebounceElapsed, debounceWindow);

        Lines.CollectionChanged += (_, _) => Renumber();

        RefreshHeldBills();
    }

    /// <summary>Raised when the caret should be put back in the search box.</summary>
    public event EventHandler? SearchFocusRequested;

    // ---- Search ------------------------------------------------------------------------------

    /// <summary>
    /// Bound two-way to the search box. Each change is one keystroke as far as scanner
    /// classification is concerned, and it restarts the debounce window.
    /// </summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            var incoming = value ?? string.Empty;

            if (!Set(ref _searchText, incoming))
                return;

            if (_suppressSearchNotification)
                return;

            _classifier.RecordKeystroke();
            _debouncer.Notify(incoming);
        }
    }

    public ObservableCollection<Item> SearchResults { get; } = [];

    public int SelectedResultIndex
    {
        get => _selectedResultIndex;
        set
        {
            if (Set(ref _selectedResultIndex, value))
                Raise(nameof(SelectedResult));
        }
    }

    public Item? SelectedResult =>
        _selectedResultIndex >= 0 && _selectedResultIndex < SearchResults.Count
            ? SearchResults[_selectedResultIndex]
            : null;

    public bool IsResultListOpen => SearchResults.Count > 0;

    // ---- Grid --------------------------------------------------------------------------------

    public ObservableCollection<InvoiceLineViewModel> Lines { get; } = [];

    /// <summary>
    /// Numbers the rows 1..n after any change to the bill.
    /// </summary>
    /// <remarks>
    /// Done by watching the collection rather than at each of the six places that add or remove a
    /// line. A row number that is right in five of those places and stale in the sixth is worse
    /// than none at all, because the one it is wrong on is the row somebody is pointing at.
    /// </remarks>
    private void Renumber()
    {
        for (var i = 0; i < Lines.Count; i++)
            Lines[i].LineNumber = i + 1;
    }

    public int SelectedLineIndex
    {
        get => _selectedLineIndex;
        set
        {
            if (Set(ref _selectedLineIndex, value))
                Raise(nameof(SelectedLine));
        }
    }

    public InvoiceLineViewModel? SelectedLine =>
        _selectedLineIndex >= 0 && _selectedLineIndex < Lines.Count ? Lines[_selectedLineIndex] : null;

    public InvoiceTotals Totals => _bill.Totals;

    /// <summary>Shown large and high-contrast per NFR-02.</summary>
    public decimal GrandTotal => _bill.Totals.GrandTotal;

    /// <summary>
    /// Step used by the increment and decrement keys. Weighed goods move in a smaller step, since
    /// nudging loose sugar by a whole kilo is never what the cashier meant.
    /// </summary>
    public decimal PieceQuantityStep { get; set; } = 1m;

    public decimal WeighedQuantityStep { get; set; } = 0.1m;

    // ---- Customer ----------------------------------------------------------------------------

    public Customer? Customer => _bill.Customer;

    public string CustomerLabel =>
        _bill.Customer is null ? "Walk-in" : _bill.Customer.Name ?? _bill.Customer.MobileNo;

    public int LoyaltyBalance => _bill.Customer?.LoyaltyBalance ?? 0;

    public bool HasCustomer => _bill.Customer is not null;

    // ---- Editing and mode --------------------------------------------------------------------

    public BillingMode Mode
    {
        get => _mode;
        private set
        {
            if (!Set(ref _mode, value))
                return;

            Raise(nameof(IsRecalling));
            Raise(nameof(IsTendering));
            Raise(nameof(IsFindingCustomer));
            Raise(nameof(IsReprinting));
            Raise(nameof(IsVoiding));
            Raise(nameof(IsSettingCashier));
        }
    }

    public bool IsRecalling => _mode == BillingMode.Recall;

    public bool IsTendering => _mode == BillingMode.Tender;

    public bool IsFindingCustomer => _mode == BillingMode.Customer;

    public bool IsReprinting => _mode == BillingMode.Reprint;

    public bool IsVoiding => _mode == BillingMode.Void;

    public bool IsSettingCashier => _mode == BillingMode.Cashier;

    /// <summary>Who is on the till. Recorded against every sale they ring up.</summary>
    public string? CashierName
    {
        get => _cashierName;
        private set
        {
            if (Set(ref _cashierName, string.IsNullOrWhiteSpace(value) ? null : value.Trim()))
                Raise(nameof(CashierLabel));
        }
    }

    public string CashierLabel => _cashierName ?? "not set";

    public EditableColumn? EditingColumn
    {
        get => _editingColumn;
        private set
        {
            if (Set(ref _editingColumn, value))
                Raise(nameof(IsEditing));
        }
    }

    public bool IsEditing => _editingColumn.HasValue;

    /// <summary>
    /// Whatever the cashier is typing right now — a cell value, a payment amount, or a mobile
    /// number. Which one depends on the mode.
    /// </summary>
    public string EditBuffer
    {
        get => _editBuffer;
        set => Set(ref _editBuffer, value ?? string.Empty);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value ?? string.Empty);
    }

    // ---- Parked bills ------------------------------------------------------------------------

    public ObservableCollection<HeldBillSummary> HeldBills { get; } = [];

    public int SelectedHeldBillIndex
    {
        get => _selectedHeldBillIndex;
        set
        {
            if (Set(ref _selectedHeldBillIndex, value))
                Raise(nameof(SelectedHeldBill));
        }
    }

    public HeldBillSummary? SelectedHeldBill =>
        _selectedHeldBillIndex >= 0 && _selectedHeldBillIndex < HeldBills.Count
            ? HeldBills[_selectedHeldBillIndex]
            : null;

    // ---- Tender ------------------------------------------------------------------------------

    /// <summary>Tender types offered in the pane, in the order the arrow keys walk them.</summary>
    public IReadOnlyList<TenderType> TenderTypes { get; } =
        [TenderType.Cash, TenderType.Card, TenderType.Upi, TenderType.StoreCredit, TenderType.LoyaltyPoints];

    public int SelectedTenderTypeIndex
    {
        get => _selectedTenderTypeIndex;
        set
        {
            if (Set(ref _selectedTenderTypeIndex, value))
                Raise(nameof(SelectedTenderType));
        }
    }

    public TenderType SelectedTenderType => TenderTypes[Math.Clamp(_selectedTenderTypeIndex, 0, TenderTypes.Count - 1)];

    public ObservableCollection<Tender> Payments { get; } = [];

    public decimal AmountDue => _basket?.AmountDue ?? 0m;

    public decimal AmountTendered => _basket?.TotalTendered ?? 0m;

    public decimal AmountRemaining => _basket?.Remaining ?? 0m;

    public decimal ChangeDue => _basket?.ChangeDue ?? 0m;

    public bool IsFullyTendered => _basket?.IsSettled ?? false;

    /// <summary>Most points this customer may put against the bill, given the cap and their balance.</summary>
    public int MaxRedeemablePoints => _checkout.QuoteRedemption(AmountDue, _bill.Customer).Points;

    /// <summary>The completed sale, for the confirmation line after settlement.</summary>
    public CheckoutResult? LastSale
    {
        get => _lastSale;
        private set
        {
            if (Set(ref _lastSale, value))
                Raise(nameof(LastInvoiceNo));
        }
    }

    public string LastInvoiceNo => _lastSale?.Invoice.InvoiceNo ?? string.Empty;

    // ---- What the side panel shows ------------------------------------------------------------

    /// <summary>
    /// The bill's own number, or what to say instead while it has none.
    /// </summary>
    /// <remarks>
    /// A bill on the screen has not got a number yet, and this deliberately does not invent one.
    /// The number is minted inside the same transaction that writes the invoice, which is what
    /// stops an abandoned sale burning one and leaving a gap in a run somebody has to explain to an
    /// auditor. Showing "the next number" here would be showing a number that may never be issued.
    /// </remarks>
    public string InvoiceNoLabel => _bill.IsEmpty && _lastSale is not null
        ? _lastSale.Invoice.InvoiceNo
        : "issued when paid";

    /// <summary>True while the number shown belongs to the sale just settled rather than this one.</summary>
    public bool ShowingLastInvoiceNo => _bill.IsEmpty && _lastSale is not null;

    /// <summary>Today's date, for the panel.</summary>
    /// <remarks>
    /// Not the injected <see cref="IClock"/>: that one is monotonic on purpose, for measuring the
    /// millisecond gaps that tell a scanner burst from typing, and it has no wall time to give.
    /// This is a label a cashier glances at, and it re-reads whenever the bill changes.
    /// </remarks>
    public string TodayLabel => DateTimeOffset.Now.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);

    public decimal TenderedCash => TenderedOf(TenderType.Cash);
    public decimal TenderedCard => TenderedOf(TenderType.Card);
    public decimal TenderedUpi => TenderedOf(TenderType.Upi);
    public decimal TenderedCredit => TenderedOf(TenderType.StoreCredit);
    public decimal TenderedPoints => TenderedOf(TenderType.LoyaltyPoints);

    private decimal TenderedOf(TenderType type) =>
        Payments.Where(p => p.Type == type).Sum(p => p.Amount);

    /// <summary>Points this bill would earn if it were settled as it stands.</summary>
    /// <remarks>
    /// A projection, and labelled as one on the screen. It moves as the bill does, and a customer
    /// attached after the fact changes it — which is exactly why it must not be presented as
    /// something already banked.
    /// </remarks>
    public int PointsEarning => _bill.Customer is null
        ? 0
        : LoyaltyEngine.PointsEarned(
            Math.Max(0m, GrandTotal - TenderedPoints),
            _checkout.LoyaltyRules);

    public int PointsRedeemedNow => _pointsRedeemed;

    /// <summary>
    /// What the customer is saving against the printed price, or nothing when they are not.
    /// </summary>
    /// <remarks>
    /// The same figure the receipt prints as "today's saving": MRP less what is actually charged,
    /// which is the shelf discount, plus anything taken off the line by hand. Blank rather than
    /// "Saved 0.00" when there is nothing in it — a zero saving is not worth a line on the screen.
    /// </remarks>
    public string SavingsLabel
    {
        get
        {
            var saved = Lines.Sum(l => (l.Line.Mrp - l.Line.UnitPrice) * l.Line.Quantity)
                        + Totals.TotalDiscount;

            return saved > 0m ? $"Saved {saved:N2}" : string.Empty;
        }
    }

    /// <summary>Raises everything the side panel reads. Called wherever the bill or the basket moves.</summary>
    private void RefreshSidePanel()
    {
        foreach (var name in new[]
                 {
                     nameof(InvoiceNoLabel), nameof(ShowingLastInvoiceNo), nameof(TodayLabel),
                     nameof(TenderedCash), nameof(TenderedCard), nameof(TenderedUpi),
                     nameof(TenderedCredit), nameof(TenderedPoints),
                     nameof(PointsEarning), nameof(PointsRedeemedNow), nameof(ChangeDue),
                     nameof(SavingsLabel),
                 })
        {
            Raise(name);
        }
    }

    // ---- Actions -----------------------------------------------------------------------------

    public void FocusSearch()
    {
        ClearPendingConfirmations();

        if (Mode == BillingMode.Tender)
            return;

        CancelEdit();
        Mode = BillingMode.Billing;
        SearchFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    public void MoveUp() => Move(-1);

    public void MoveDown() => Move(+1);

    public void Commit()
    {
        ClearPendingNewBill();

        switch (Mode)
        {
            case BillingMode.Recall:
                RecallSelected();
                return;

            case BillingMode.Tender:
                CommitTender();
                return;

            case BillingMode.Customer:
                CommitCustomerLookup();
                return;

            case BillingMode.Reprint:
                CommitReprint();
                return;

            case BillingMode.Void:
                CommitVoid();
                return;

            case BillingMode.Cashier:
                CommitCashier();
                return;
        }

        if (IsEditing)
        {
            CommitEdit();
            return;
        }

        CommitSearch();
    }

    public void Cancel()
    {
        ClearPendingConfirmations();

        switch (Mode)
        {
            case BillingMode.Recall:
                Mode = BillingMode.Billing;
                StatusMessage = string.Empty;
                return;

            case BillingMode.Tender:
                AbandonTender();
                return;

            case BillingMode.Customer:
            case BillingMode.Reprint:
            case BillingMode.Void:
            case BillingMode.Cashier:
                _pendingVoidInvoiceNo = null;
                Mode = BillingMode.Billing;
                EditBuffer = string.Empty;
                StatusMessage = string.Empty;
                return;
        }

        if (IsEditing)
        {
            CancelEdit();
            return;
        }

        if (IsResultListOpen || _searchText.Length > 0)
            ClearSearch();
    }

    public void DeleteLine()
    {
        ClearPendingConfirmations();

        if (Mode == BillingMode.Tender)
        {
            RemoveLastPayment();
            return;
        }

        if (SelectedLine is null)
        {
            StatusMessage = "Select a line to delete.";
            return;
        }

        var index = _selectedLineIndex;
        var name = Lines[index].Name;

        CancelEdit();
        _bill.RemoveAt(index);
        Lines.RemoveAt(index);
        ClampLineSelection();
        RefreshTotals();

        StatusMessage = $"{name} removed.";
    }

    public void IncrementQuantity() => Nudge(+1);

    public void DecrementQuantity() => Nudge(-1);

    public void EditQuantity() => BeginEdit(EditableColumn.Quantity);

    public void EditDiscount() => BeginEdit(EditableColumn.Discount);

    public void HoldBill()
    {
        ClearPendingConfirmations();
        CancelEdit();

        if (Mode == BillingMode.Tender)
        {
            StatusMessage = "Finish or abandon the payment before parking this bill.";
            return;
        }

        if (_bill.IsEmpty)
        {
            StatusMessage = "Nothing to hold.";
            return;
        }

        var token = _heldBills.NextToken(_laneId);
        _heldBills.Park(_laneId, token, _now(), _bill.Customer, _bill.SnapshotLines());

        ClearBill();
        RefreshHeldBills();

        StatusMessage = $"Bill parked as {token}.";
    }

    public void RecallBill()
    {
        ClearPendingConfirmations();
        CancelEdit();

        if (Mode == BillingMode.Tender)
        {
            StatusMessage = "Finish or abandon the payment first.";
            return;
        }

        RefreshHeldBills();

        if (HeldBills.Count == 0)
        {
            StatusMessage = "No parked bills.";
            return;
        }

        Mode = BillingMode.Recall;
        SelectedHeldBillIndex = 0;
        StatusMessage = "Choose a parked bill, then press the commit key.";
    }

    /// <summary>
    /// Starts a fresh bill. On a bill that already has lines this asks for the key a second time
    /// rather than discarding the sale on one stray press; any other action cancels the request.
    /// </summary>
    public void NewBill()
    {
        CancelEdit();

        if (Mode == BillingMode.Tender)
        {
            StatusMessage = "Finish or abandon the payment first.";
            return;
        }

        if (_bill.IsEmpty)
        {
            ClearBill();
            return;
        }

        if (!_pendingNewBillConfirmation)
        {
            _pendingNewBillConfirmation = true;
            StatusMessage = "Press the new-bill key again to discard this bill.";
            return;
        }

        _pendingNewBillConfirmation = false;
        ClearBill();
        StatusMessage = "Bill discarded.";
    }

    /// <summary>Opens the tender pane against the bill's current total.</summary>
    public void Tender()
    {
        ClearPendingConfirmations();
        CancelEdit();

        if (Mode == BillingMode.Tender)
            return;

        if (_bill.IsEmpty)
        {
            StatusMessage = "Nothing to tender.";
            return;
        }

        _basket = new TenderBasket(_bill.Totals.GrandTotal);
        _pointsRedeemed = 0;
        Payments.Clear();
        SelectedTenderTypeIndex = 0;
        EditBuffer = string.Empty;
        Mode = BillingMode.Tender;
        RefreshTender();

        StatusMessage = $"{AmountDue:0.00} due. Choose a tender, type an amount, then commit.";
    }

    /// <summary>Attaches a customer by mobile number, which is what unlocks loyalty on the bill.</summary>
    public void FindCustomer()
    {
        ClearPendingConfirmations();
        CancelEdit();

        if (Mode == BillingMode.Tender)
        {
            StatusMessage = "Attach the customer before taking payment.";
            return;
        }

        Mode = BillingMode.Customer;
        EditBuffer = string.Empty;
        StatusMessage = "Type the customer's mobile number, then commit.";
    }

    /// <summary>Prints a duplicate of a past invoice.</summary>
    public void ReprintInvoice()
    {
        ClearPendingConfirmations();
        CancelEdit();

        if (_invoices is null)
        {
            StatusMessage = "Reprinting is not available on this lane.";
            return;
        }

        if (Mode == BillingMode.Tender)
        {
            StatusMessage = "Finish or abandon the payment first.";
            return;
        }

        Mode = BillingMode.Reprint;
        EditBuffer = string.Empty;
        StatusMessage = "Commit for the last bill, or type an invoice number or mobile number.";
    }

    /// <summary>
    /// Closes the day and prints the Z-report. Asks twice, because a close cannot be undone and the
    /// key is right beside the one that takes payment.
    /// </summary>
    public void CloseDay()
    {
        ClearPendingNewBill();
        _pendingCustomerCreate = false;
        CancelEdit();

        if (_dayClose is null)
        {
            StatusMessage = "Day-end close is not available on this lane.";
            return;
        }

        if (Mode == BillingMode.Tender)
        {
            StatusMessage = "Finish or abandon the payment first.";
            return;
        }

        if (!_bill.IsEmpty)
        {
            StatusMessage = "Finish, park or discard the bill on screen before closing the day.";
            return;
        }

        if (!_pendingDayClose)
        {
            var preview = _dayClose.Preview(_laneId);

            _pendingDayClose = true;
            StatusMessage = preview.TookNothing
                ? "Nothing has been sold since the last close. Press again to close anyway."
                : $"{preview.InvoiceCount} invoice(s), {preview.NetSales:0.00} net, {preview.CashExpected:0.00} expected in the drawer. Press again to close.";

            return;
        }

        _pendingDayClose = false;

        var result = _dayClose.Close(_laneId);
        var message = $"Day closed. Report {result.Day.Id}: {result.Day.InvoiceCount} invoice(s), {result.Day.NetSales:0.00} net, {result.Day.CashExpected:0.00} expected in the drawer.";

        if (!result.Print.Succeeded && result.Print.Status == PrintStatus.Failed)
            message += " The report did not print — reprint it once the printer is fixed.";

        if (!result.Backup.Succeeded)
            message += $" BACKUP FAILED: {result.Backup.Detail}";

        RefreshHeldBills();
        StatusMessage = message;
    }

    /// <summary>Cancels a sale that has already been settled.</summary>
    public void VoidInvoice()
    {
        ClearPendingConfirmations();
        CancelEdit();

        if (_invoices is null)
        {
            StatusMessage = "Voiding is not available on this lane.";
            return;
        }

        if (Mode == BillingMode.Tender)
        {
            StatusMessage = "Finish or abandon the payment first.";
            return;
        }

        _pendingVoidInvoiceNo = null;
        Mode = BillingMode.Void;
        EditBuffer = string.Empty;
        StatusMessage = "Commit for the last bill, or type the invoice number to void.";
    }

    /// <summary>Sets who is on the till, at the start of a shift or when it changes.</summary>
    public void SetCashier()
    {
        ClearPendingConfirmations();
        CancelEdit();

        if (Mode == BillingMode.Tender)
        {
            StatusMessage = "Finish or abandon the payment first.";
            return;
        }

        Mode = BillingMode.Cashier;
        EditBuffer = _cashierName ?? string.Empty;
        StatusMessage = "Type the cashier's name. Every sale from here on is recorded against it.";
    }

    /// <summary>Raised when the owner's screen should open. The view owns the window; this does not.</summary>
    public event EventHandler? OwnerViewRequested;

    /// <summary>
    /// Opens the owner's screen &mdash; the figures, what to reorder, and the lane's settings.
    /// </summary>
    /// <remarks>
    /// Refused mid-payment. Everything behind it is read-only or a settings change, but a cashier
    /// halfway through taking money should not have another window take the keyboard.
    /// </remarks>
    public void OwnerView()
    {
        ClearPendingConfirmations();
        CancelEdit();

        if (Mode == BillingMode.Tender)
        {
            StatusMessage = "Finish or abandon the payment first.";
            return;
        }

        OwnerViewRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Whether the tax columns belong on the screen. False on a composition lane.</summary>
    public bool ShowsTax => _bill.TaxMode == TaxMode.Gst;

    /// <summary>
    /// Changes what kind of bill this lane issues.
    /// </summary>
    /// <returns>Null when it worked, or why it did not.</returns>
    public string? TrySetTaxMode(TaxMode mode)
    {
        try
        {
            _bill.SetTaxMode(mode);
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }

        // The grid's tax columns and the totals panel follow this.
        Raise(nameof(ShowsTax));
        RefreshTotals();

        StatusMessage = mode == TaxMode.Composition
            ? "This lane now issues a BILL OF SUPPLY. No tax is charged."
            : "This lane now issues a TAX INVOICE. GST is charged and shown.";

        return null;
    }

    // ---- Search internals --------------------------------------------------------------------

    private void OnDebounceElapsed(string text)
    {
        // A debounced query that lands after the cashier has moved on to paying is not wanted.
        if (Mode != BillingMode.Billing)
            return;

        var trimmed = (text ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            ClearResults();
            return;
        }

        RunSearch(trimmed);
    }

    private void CommitSearch()
    {
        var text = _searchText.Trim();
        var kind = _classifier.ClassifyOnEnter();
        _classifier.Reset();
        _debouncer.Cancel();

        if (text.Length == 0)
            return;

        if (kind == InputKind.Scanner)
        {
            var scanned = _items.FindByBarcode(text);

            if (scanned is not null)
            {
                AddItem(scanned);
                return;
            }

            // Classification is a timing heuristic, not a hardware signal. A burst that looked
            // like a scan but matches no barcode falls through to the ordinary search rather than
            // being reported as a failed scan.
        }

        // A result list built from this exact text is what the cashier is looking at, so Enter
        // takes their highlighted choice. Anything else is stale and gets re-queried.
        if (IsResultListOpen && _resultsForText == text && SelectedResult is { } chosen)
        {
            AddItem(chosen);
            return;
        }

        RunSearch(text);

        if (SearchResults.Count == 0)
        {
            StatusMessage = $"No item matches '{text}'.";
            return;
        }

        if (SearchResults.Count == 1)
        {
            AddItem(SearchResults[0]);
            return;
        }

        StatusMessage = $"{SearchResults.Count} matches. Move to one and press the commit key.";
    }

    private void RunSearch(string text)
    {
        var results = _items.Search(text);

        SearchResults.Clear();
        foreach (var item in results)
            SearchResults.Add(item);

        _resultsForText = text;
        SelectedResultIndex = SearchResults.Count > 0 ? 0 : -1;
        Raise(nameof(IsResultListOpen));
    }

    private void AddItem(Item item)
    {
        var line = _bill.AddItem(item);

        Lines.Add(new InvoiceLineViewModel(line));
        SelectedLineIndex = Lines.Count - 1;

        ClearSearch();
        RefreshTotals();

        StatusMessage = $"{item.Name} added.{StockNote(item)}";
    }

    /// <summary>
    /// What to say about the shelf when an item is rung up, or nothing at all.
    /// </summary>
    /// <remarks>
    /// This is the only place a cashier finds out — they never open a report. It is appended to the
    /// line they already read rather than given a banner or a dialog, because none of it is worth a
    /// keystroke: the sale goes through either way, and the shelf rather than the database is the
    /// authority on what is actually there.
    ///
    /// The figure quoted is the one before this sale, which is what the cashier can check against
    /// what is in their hand.
    /// </remarks>
    internal static string StockNote(Item item)
    {
        if (!item.IsStockTracked)
            return string.Empty;

        if (item.IsOutOfStock)
            return $"  Stock says none left ({item.StockQty:0.###}) — selling anyway.";

        return item.IsLowStock ? $"  Only {item.StockQty:0.###} left." : string.Empty;
    }

    private void ClearResults()
    {
        if (SearchResults.Count > 0)
            SearchResults.Clear();

        _resultsForText = string.Empty;
        SelectedResultIndex = -1;
        Raise(nameof(IsResultListOpen));
    }

    private void ClearSearch()
    {
        _debouncer.Cancel();
        _classifier.Reset();
        SetSearchTextSilently(string.Empty);
        ClearResults();
    }

    /// <summary>
    /// Clears the box without it counting as a keystroke — otherwise adding an item would look
    /// like typing and kick off a search for the empty string.
    /// </summary>
    private void SetSearchTextSilently(string text)
    {
        _suppressSearchNotification = true;
        try
        {
            SearchText = text;
        }
        finally
        {
            _suppressSearchNotification = false;
        }
    }

    // ---- Grid internals ----------------------------------------------------------------------

    private void Move(int delta)
    {
        ClearPendingConfirmations();

        switch (Mode)
        {
            case BillingMode.Recall:
                SelectedHeldBillIndex = Clamp(_selectedHeldBillIndex + delta, HeldBills.Count);
                return;

            case BillingMode.Tender:
                SelectedTenderTypeIndex = Math.Clamp(_selectedTenderTypeIndex + delta, 0, TenderTypes.Count - 1);
                EditBuffer = string.Empty;
                Raise(nameof(SelectedTenderType));
                return;

            case BillingMode.Customer:
                return;
        }

        // With a result list open the arrows belong to it; with the box quiet they walk the bill.
        if (IsResultListOpen)
        {
            SelectedResultIndex = Clamp(_selectedResultIndex + delta, SearchResults.Count);
            return;
        }

        SelectedLineIndex = Clamp(_selectedLineIndex + delta, Lines.Count);
    }

    private void Nudge(int direction)
    {
        ClearPendingConfirmations();

        if (Mode != BillingMode.Billing)
            return;

        if (SelectedLine is null)
        {
            StatusMessage = "Select a line first.";
            return;
        }

        var index = _selectedLineIndex;
        var step = Lines[index].Unit.AllowsFractionalQuantity() ? WeighedQuantityStep : PieceQuantityStep;
        var linesBefore = _bill.Lines.Count;

        _bill.AdjustQuantity(index, step * direction);

        if (_bill.Lines.Count < linesBefore)
        {
            Lines.RemoveAt(index);
            ClampLineSelection();
        }
        else
        {
            Lines[index].Refresh();
        }

        RefreshTotals();
    }

    private void BeginEdit(EditableColumn column)
    {
        ClearPendingConfirmations();

        if (Mode != BillingMode.Billing)
            return;

        if (SelectedLine is not { } line)
        {
            StatusMessage = "Select a line first.";
            return;
        }

        EditingColumn = column;
        EditBuffer = Format(column == EditableColumn.Quantity ? line.Quantity : line.Discount);
        StatusMessage = string.Empty;
    }

    private void CommitEdit()
    {
        if (EditingColumn is not { } column || SelectedLine is null)
            return;

        if (!TryParseAmount(EditBuffer, out var value))
        {
            StatusMessage = $"'{EditBuffer}' is not a number.";
            return;
        }

        try
        {
            if (column == EditableColumn.Quantity)
                _bill.SetQuantity(_selectedLineIndex, value);
            else
                _bill.SetDiscount(_selectedLineIndex, value);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            // The edit stays open so the cashier can correct the figure in place.
            StatusMessage = ex.Message.Split(" (Parameter", StringSplitOptions.None)[0];
            return;
        }

        Lines[_selectedLineIndex].Refresh();
        RefreshTotals();
        CancelEdit();
    }

    private void CancelEdit()
    {
        EditingColumn = null;

        if (Mode == BillingMode.Billing)
            EditBuffer = string.Empty;
    }

    // ---- Tender internals --------------------------------------------------------------------

    private void CommitTender()
    {
        if (_basket is null)
            return;

        var typed = EditBuffer.Trim();

        // An empty box with the bill already covered means "that's everything, finish the sale".
        if (typed.Length == 0 && _basket.IsSettled)
        {
            CompleteSale();
            return;
        }

        if (SelectedTenderType == TenderType.LoyaltyPoints)
        {
            AddLoyaltyTender(typed);
            return;
        }

        if (typed.Length == 0)
        {
            // No amount typed: take the whole remaining balance under this tender.
            typed = Format(_basket.Remaining);
        }

        if (!TryParseAmount(typed, out var amount))
        {
            StatusMessage = $"'{EditBuffer}' is not an amount.";
            return;
        }

        try
        {
            _basket.Add(SelectedTenderType, amount);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidOperationException)
        {
            StatusMessage = ex.Message.Split(" (Parameter", StringSplitOptions.None)[0];
            return;
        }

        EditBuffer = string.Empty;
        RefreshTender();

        StatusMessage = _basket.IsSettled
            ? _basket.ChangeDue > 0m
                ? $"Change {_basket.ChangeDue:0.00}. Commit again to finish."
                : "Paid in full. Commit again to finish."
            : $"{_basket.Remaining:0.00} still due.";
    }

    /// <summary>
    /// Loyalty is entered in points, not rupees — that is the number the customer knows. An empty
    /// box redeems the most the rules allow.
    /// </summary>
    private void AddLoyaltyTender(string typed)
    {
        if (_basket is null)
            return;

        if (_bill.Customer is null)
        {
            StatusMessage = "Attach a customer before redeeming points.";
            return;
        }

        if (_basket.Contains(TenderType.LoyaltyPoints))
        {
            StatusMessage = "Points have already been redeemed on this bill.";
            return;
        }

        int requested;

        if (typed.Length == 0)
        {
            requested = int.MaxValue;
        }
        else if (!int.TryParse(typed, NumberStyles.Integer, CultureInfo.InvariantCulture, out requested) || requested < 0)
        {
            StatusMessage = $"'{typed}' is not a number of points.";
            return;
        }

        var redemption = _checkout.Redeem(_basket.AmountDue, _bill.Customer, requested);

        if (!redemption.IsSomething)
        {
            StatusMessage = "No points can be redeemed on this bill.";
            return;
        }

        // The redemption may still exceed what is left to pay if other tenders came first.
        if (redemption.Value > _basket.Remaining)
        {
            StatusMessage = $"Only {_basket.Remaining:0.00} is left to pay; redeem fewer points.";
            return;
        }

        _basket.Add(TenderType.LoyaltyPoints, redemption.Value, $"{redemption.Points} points");
        _pointsRedeemed = redemption.Points;

        EditBuffer = string.Empty;
        RefreshTender();

        StatusMessage = $"{redemption.Points} points redeemed, worth {redemption.Value:0.00}. {_basket.Remaining:0.00} still due.";
    }

    private void RemoveLastPayment()
    {
        if (_basket is null || _basket.IsEmpty)
        {
            StatusMessage = "No payment to remove.";
            return;
        }

        var index = _basket.Tenders.Count - 1;

        if (_basket.Tenders[index].Type == TenderType.LoyaltyPoints)
            _pointsRedeemed = 0;

        _basket.RemoveAt(index);
        EditBuffer = string.Empty;
        RefreshTender();

        StatusMessage = $"Payment removed. {_basket.Remaining:0.00} due.";
    }

    private void CompleteSale()
    {
        if (_basket is null)
            return;

        CheckoutResult result;

        try
        {
            result = _checkout.Complete(_laneId, _bill, _basket, _pointsRedeemed, _recalledFromToken);
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = ex.Message;
            return;
        }

        LastSale = result;

        var message = $"{result.Invoice.InvoiceNo} settled for {result.Invoice.GrandTotal:0.00}.";

        if (result.ChangeDue > 0m)
            message += $" Change {result.ChangeDue:0.00}.";

        if (result.PointsEarned > 0)
            message += $" {result.PointsEarned} points earned, balance {result.NewLoyaltyBalance}.";

        if (result.Drawer == DrawerKickResult.Failed)
            message += " The cash drawer did not open — open it by hand.";

        // A printer that is failing has to be visible on every sale. It is logged either way, but a
        // log nobody reads until the evening is a shop that traded all day handing out no bills.
        if (result.Print.Status == PrintStatus.Failed)
            message += $" THE RECEIPT DID NOT PRINT: {result.Print.Detail}. Fix the printer, then reprint this bill.";

        _basket = null;
        _pointsRedeemed = 0;
        _recalledFromToken = null;
        Payments.Clear();
        Mode = BillingMode.Billing;

        ClearBill();
        RefreshTender();

        StatusMessage = message;
    }

    private void AbandonTender()
    {
        _basket = null;
        _pointsRedeemed = 0;
        Payments.Clear();
        EditBuffer = string.Empty;
        Mode = BillingMode.Billing;
        RefreshTender();

        StatusMessage = "Payment abandoned. The bill is still here.";
    }

    private void RefreshTender()
    {
        Payments.Clear();

        if (_basket is not null)
        {
            foreach (var payment in _basket.Tenders)
                Payments.Add(payment);
        }

        Raise(nameof(AmountDue));
        Raise(nameof(AmountTendered));
        Raise(nameof(AmountRemaining));
        Raise(nameof(ChangeDue));
        Raise(nameof(IsFullyTendered));
        Raise(nameof(MaxRedeemablePoints));
    }

    // ---- Customer internals ------------------------------------------------------------------

    private void CommitCustomerLookup()
    {
        var mobile = EditBuffer.Trim();

        if (mobile.Length == 0)
        {
            StatusMessage = "Type a mobile number.";
            return;
        }

        var existing = _customers.FindByMobile(mobile);

        if (existing is not null)
        {
            AttachCustomer(existing);
            return;
        }

        // Creating a customer on a mistyped number is worse than making the cashier confirm, so
        // the first press reports and the second creates.
        if (!_pendingCustomerCreate)
        {
            _pendingCustomerCreate = true;
            StatusMessage = $"No customer on {mobile}. Commit again to add them.";
            return;
        }

        _pendingCustomerCreate = false;
        AttachCustomer(_customers.Add(new Customer { MobileNo = mobile, StateCode = _bill.OutletStateCode }));
    }

    private void AttachCustomer(Customer customer)
    {
        _bill.SetCustomer(customer);

        foreach (var line in Lines)
            line.Refresh();

        EditBuffer = string.Empty;
        Mode = BillingMode.Billing;
        RefreshTotals();
        RefreshCustomer();

        StatusMessage = $"{CustomerLabel} attached. {customer.LoyaltyBalance} points.";
    }

    /// <summary>
    /// Finds the invoice to reprint. An empty box means the last bill, which is what "reprint"
    /// nearly always means; otherwise the text is tried as an invoice number and then as a mobile
    /// number, because a customer asking for a duplicate has their phone rather than the number.
    /// </summary>
    private void CommitReprint()
    {
        if (_invoices is null)
            return;

        var typed = EditBuffer.Trim();

        var invoice = typed.Length == 0
            ? _invoices.FindLatest(_laneId)
            : _invoices.FindByInvoiceNo(typed) ?? _invoices.FindLatestForMobile(typed);

        if (invoice is null)
        {
            StatusMessage = typed.Length == 0
                ? "This lane has not billed anything yet."
                : $"No invoice found for '{typed}'.";

            return;
        }

        var outcome = _checkout.Reprint(invoice);

        EditBuffer = string.Empty;
        Mode = BillingMode.Billing;

        StatusMessage = outcome.Status switch
        {
            PrintStatus.Printed => $"{invoice.InvoiceNo} reprinted.",
            PrintStatus.NoPrinterConfigured => $"Found {invoice.InvoiceNo}, but this lane has no printer.",
            _ => $"{invoice.InvoiceNo} did not print: {outcome.Detail}",
        };
    }

    /// <summary>
    /// Finds the sale to cancel, shows what it is, and asks again. Voiding undoes a sale the
    /// customer has already paid for, so it is never one keypress away from happening.
    /// </summary>
    private void CommitVoid()
    {
        if (_invoices is null)
            return;

        var typed = EditBuffer.Trim();

        // Second press on the same invoice: do it.
        if (_pendingVoidInvoiceNo is { } confirmed && (typed.Length == 0 || typed == confirmed))
        {
            _pendingVoidInvoiceNo = null;

            try
            {
                var result = _checkout.VoidSale(confirmed, reason: null);

                var message = $"{result.Invoice.InvoiceNo} voided for {result.Invoice.GrandTotal:0.00}.";

                if (result.LoyaltyReversed)
                    message += $" Points put back, balance {result.NewLoyaltyBalance}.";

                if (result.Invoice.Sale.Payments.Any(p => p.Type == TenderType.Cash))
                    message += " Return the cash from the drawer.";

                EditBuffer = string.Empty;
                Mode = BillingMode.Billing;
                RefreshCustomer();
                StatusMessage = message;
            }
            catch (InvalidOperationException ex)
            {
                EditBuffer = string.Empty;
                StatusMessage = ex.Message;
            }

            return;
        }

        var invoice = typed.Length == 0
            ? _invoices.FindLatest(_laneId)
            : _invoices.FindByInvoiceNo(typed);

        if (invoice is null)
        {
            StatusMessage = typed.Length == 0
                ? "This lane has nothing to void."
                : $"No invoice found for '{typed}'.";

            return;
        }

        if (invoice.IsVoided)
        {
            StatusMessage = $"{invoice.InvoiceNo} was already voided.";
            return;
        }

        if (_invoices.IsReported(invoice.InvoiceNo))
        {
            StatusMessage = $"{invoice.InvoiceNo} is on a day-end report already and cannot be voided.";
            return;
        }

        _pendingVoidInvoiceNo = invoice.InvoiceNo;
        EditBuffer = invoice.InvoiceNo;

        StatusMessage = $"Void {invoice.InvoiceNo} for {invoice.GrandTotal:0.00}, " +
            $"{invoice.Sale.Lines.Count} line(s)? Commit again to confirm.";
    }

    private void CommitCashier()
    {
        var typed = EditBuffer.Trim();

        CashierName = typed;
        EditBuffer = string.Empty;
        Mode = BillingMode.Billing;

        StatusMessage = typed.Length == 0
            ? "Cashier cleared. Sales will not be attributed to anyone."
            : $"{typed} is on the till.";
    }

    // ---- Parked bill internals ---------------------------------------------------------------

    private void RefreshHeldBills()
    {
        HeldBills.Clear();

        foreach (var summary in _heldBills.List(_laneId))
            HeldBills.Add(summary);

        SelectedHeldBillIndex = HeldBills.Count > 0 ? Math.Min(Math.Max(_selectedHeldBillIndex, 0), HeldBills.Count - 1) : -1;
    }

    private void RecallSelected()
    {
        if (SelectedHeldBill is not { } summary)
            return;

        if (!_bill.IsEmpty)
        {
            StatusMessage = "Park or discard the current bill before recalling another.";
            return;
        }

        var recalled = _heldBills.Recall(_laneId, summary.Token);

        if (recalled is null)
        {
            // Someone else took it, or it was discarded since the list was drawn.
            RefreshHeldBills();
            StatusMessage = $"{summary.Token} is no longer parked.";
            return;
        }

        _bill.Restore(recalled.Lines, recalled.Customer);
        _recalledFromToken = recalled.Token;

        RebuildLines();
        RefreshHeldBills();
        RefreshTotals();
        RefreshCustomer();

        SelectedHeldBillIndex = -1;
        Mode = BillingMode.Billing;

        StatusMessage = $"Recalled {recalled.Token}.";
    }

    private void ClearBill()
    {
        _bill.Clear();
        Lines.Clear();
        SelectedLineIndex = -1;
        _recalledFromToken = null;
        ClearSearch();
        RefreshTotals();
        RefreshCustomer();
    }

    private void RebuildLines()
    {
        Lines.Clear();

        foreach (var line in _bill.Lines)
            Lines.Add(new InvoiceLineViewModel(line));

        SelectedLineIndex = Lines.Count > 0 ? Lines.Count - 1 : -1;
    }

    private void ClampLineSelection() =>
        SelectedLineIndex = Lines.Count == 0 ? -1 : Math.Min(_selectedLineIndex, Lines.Count - 1);

    private void ClearPendingNewBill() => _pendingNewBillConfirmation = false;

    private void ClearPendingConfirmations()
    {
        _pendingNewBillConfirmation = false;
        _pendingCustomerCreate = false;
        _pendingDayClose = false;
    }

    private void RefreshTotals()
    {
        Raise(nameof(Totals));
        Raise(nameof(GrandTotal));
        Raise(nameof(MaxRedeemablePoints));

        // The side panel reads the payment split and the projected points off the same state, and
        // both move whenever the bill does.
        RefreshSidePanel();
    }

    private void RefreshCustomer()
    {
        Raise(nameof(Customer));
        Raise(nameof(CustomerLabel));
        Raise(nameof(LoyaltyBalance));
        Raise(nameof(HasCustomer));
        Raise(nameof(MaxRedeemablePoints));

        // Attaching a customer is what makes the bill earn anything at all.
        Raise(nameof(PointsEarning));
    }

    private static int Clamp(int index, int count) =>
        count == 0 ? -1 : Math.Clamp(index, 0, count - 1);

    private static string Format(decimal value) =>
        value.ToString("0.####", CultureInfo.InvariantCulture);

    private static bool TryParseAmount(string text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _debouncer.Dispose();
    }
}
