using System.Collections.ObjectModel;
using System.Globalization;
using Pos.App.Input;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Hardware.Drawer;

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

    private bool _pendingNewBillConfirmation;
    private bool _pendingCustomerCreate;
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
        Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(bill);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(heldBills);
        ArgumentNullException.ThrowIfNull(customers);
        ArgumentNullException.ThrowIfNull(checkout);
        ArgumentException.ThrowIfNullOrWhiteSpace(laneId);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(clock);

        _bill = bill;
        _items = items;
        _heldBills = heldBills;
        _customers = customers;
        _checkout = checkout;
        _laneId = laneId;
        _now = now ?? (() => DateTimeOffset.Now);
        _classifier = new ScannerInputClassifier(clock, maxKeystrokeGap);
        _debouncer = new SearchDebouncer(scheduler, OnDebounceElapsed, debounceWindow);

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
        }
    }

    public bool IsRecalling => _mode == BillingMode.Recall;

    public bool IsTendering => _mode == BillingMode.Tender;

    public bool IsFindingCustomer => _mode == BillingMode.Customer;

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

        StatusMessage = $"{item.Name} added.";
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
    }

    private void RefreshTotals()
    {
        Raise(nameof(Totals));
        Raise(nameof(GrandTotal));
        Raise(nameof(MaxRedeemablePoints));
    }

    private void RefreshCustomer()
    {
        Raise(nameof(Customer));
        Raise(nameof(CustomerLabel));
        Raise(nameof(LoyaltyBalance));
        Raise(nameof(HasCustomer));
        Raise(nameof(MaxRedeemablePoints));
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
