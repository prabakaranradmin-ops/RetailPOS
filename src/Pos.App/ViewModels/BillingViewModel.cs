using System.Collections.ObjectModel;
using System.Globalization;
using Pos.App.Input;
using Pos.Core.Data;
using Pos.Core.Domain;

namespace Pos.App.ViewModels;

public enum BillingMode
{
    Billing = 0,

    /// <summary>Picking a parked bill from the recall list.</summary>
    Recall = 1,
}

/// <summary>The only cells the cashier can type into (SRS 2.2).</summary>
public enum EditableColumn
{
    Quantity = 0,
    Discount = 1,
}

/// <summary>
/// The billing screen. Owns the search box, the line grid and the parked-bill list, and exposes
/// every one of them as an action so the whole flow is reachable from the keyboard (SRS UR-03).
/// </summary>
public sealed class BillingViewModel : ObservableObject, IBillingActions, IDisposable
{
    private readonly InvoiceEngine _bill;
    private readonly ItemRepository _items;
    private readonly ScannerInputClassifier _classifier;
    private readonly SearchDebouncer _debouncer;
    private readonly Func<DateTimeOffset> _now;

    private string _searchText = string.Empty;
    private string _resultsForText = string.Empty;
    private bool _suppressSearchNotification;

    private int _selectedResultIndex = -1;
    private int _selectedLineIndex = -1;
    private int _selectedHeldBillIndex = -1;

    private BillingMode _mode = BillingMode.Billing;
    private EditableColumn? _editingColumn;
    private string _editBuffer = string.Empty;
    private string _statusMessage = string.Empty;

    private bool _pendingNewBillConfirmation;
    private int _nextHoldToken = 1;
    private bool _disposed;

    public BillingViewModel(
        InvoiceEngine bill,
        ItemRepository items,
        IDelayScheduler scheduler,
        IClock clock,
        TimeSpan? debounceWindow = null,
        TimeSpan? maxKeystrokeGap = null,
        Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(bill);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(clock);

        _bill = bill;
        _items = items;
        _now = now ?? (() => DateTimeOffset.Now);
        _classifier = new ScannerInputClassifier(clock, maxKeystrokeGap);
        _debouncer = new SearchDebouncer(scheduler, OnDebounceElapsed, debounceWindow);
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

    // ---- Editing and mode --------------------------------------------------------------------

    public BillingMode Mode
    {
        get => _mode;
        private set
        {
            if (Set(ref _mode, value))
                Raise(nameof(IsRecalling));
        }
    }

    public bool IsRecalling => _mode == BillingMode.Recall;

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

    /// <summary>Text of the cell being edited. Bound two-way to the in-place editor.</summary>
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

    // ---- Held bills --------------------------------------------------------------------------

    public ObservableCollection<HeldBill> HeldBills { get; } = [];

    public int SelectedHeldBillIndex
    {
        get => _selectedHeldBillIndex;
        set
        {
            if (Set(ref _selectedHeldBillIndex, value))
                Raise(nameof(SelectedHeldBill));
        }
    }

    public HeldBill? SelectedHeldBill =>
        _selectedHeldBillIndex >= 0 && _selectedHeldBillIndex < HeldBills.Count
            ? HeldBills[_selectedHeldBillIndex]
            : null;

    // ---- Actions -----------------------------------------------------------------------------

    public void FocusSearch()
    {
        ClearPendingNewBill();
        CancelEdit();
        Mode = BillingMode.Billing;
        SearchFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    public void MoveUp() => Move(-1);

    public void MoveDown() => Move(+1);

    public void Commit()
    {
        ClearPendingNewBill();

        if (Mode == BillingMode.Recall)
        {
            RecallSelected();
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
        ClearPendingNewBill();

        if (Mode == BillingMode.Recall)
        {
            Mode = BillingMode.Billing;
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
        ClearPendingNewBill();

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
        ClearPendingNewBill();
        CancelEdit();

        if (_bill.IsEmpty)
        {
            StatusMessage = "Nothing to hold.";
            return;
        }

        var token = $"H{_nextHoldToken++:D3}";
        HeldBills.Add(new HeldBill(token, _now(), _bill.SnapshotLines(), _bill.Customer));

        ClearBill();
        StatusMessage = $"Bill parked as {token}.";
    }

    public void RecallBill()
    {
        ClearPendingNewBill();
        CancelEdit();

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

    // ---- Search internals --------------------------------------------------------------------

    private void OnDebounceElapsed(string text)
    {
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
        if (IsResultListOpen && _resultsForText == text)
        {
            if (SelectedResult is { } chosen)
            {
                AddItem(chosen);
                return;
            }
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
        ClearPendingNewBill();

        if (Mode == BillingMode.Recall)
        {
            SelectedHeldBillIndex = Clamp(_selectedHeldBillIndex + delta, HeldBills.Count);
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
        ClearPendingNewBill();

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
        ClearPendingNewBill();

        if (Mode == BillingMode.Recall)
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

        if (!decimal.TryParse(EditBuffer, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
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
        EditBuffer = string.Empty;
    }

    private void RecallSelected()
    {
        if (SelectedHeldBill is not { } held)
            return;

        if (!_bill.IsEmpty)
        {
            StatusMessage = "Park or discard the current bill before recalling another.";
            return;
        }

        _bill.Restore(held.Lines, held.Customer);
        RebuildLines();
        HeldBills.Remove(held);

        SelectedHeldBillIndex = -1;
        Mode = BillingMode.Billing;
        RefreshTotals();

        StatusMessage = $"Recalled {held.Token}.";
    }

    private void ClearBill()
    {
        _bill.Clear();
        Lines.Clear();
        SelectedLineIndex = -1;
        ClearSearch();
        RefreshTotals();
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

    private void RefreshTotals()
    {
        Raise(nameof(Totals));
        Raise(nameof(GrandTotal));
    }

    private static int Clamp(int index, int count) =>
        count == 0 ? -1 : Math.Clamp(index, 0, count - 1);

    private static string Format(decimal value) =>
        value.ToString("0.####", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _debouncer.Dispose();
    }
}
