using System.Collections.ObjectModel;
using System.Globalization;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Domain.Import;

namespace Pos.App.ViewModels;

/// <summary>A payment method as the owner's list offers it.</summary>
public sealed record PaymentMethodChoice(SupplierPaymentMethod Method, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The Purchases tab: the shop's suppliers, entering the bill that came with a delivery, what is
/// owed to each, and paying them.
/// </summary>
/// <remarks>
/// A bill is typed in the order it is read off the paper: the supplier, the bill number and date,
/// then each line — the item, how many, the rate before tax — and finally the total printed at the
/// bottom, which is checked against the lines so a mistyped line is caught before it is saved.
/// Everything a bill does happens when it is saved and not before: the shelf goes up, each item's
/// cost price becomes what was paid, and the supplier is owed the total.
/// </remarks>
public sealed class PurchasesViewModel : ObservableObject
{
    private static readonly CultureInfo Indian = CultureInfo.GetCultureInfo("en-IN");
    private static readonly string[] DateFormats = ["dd-MM-yyyy", "d-M-yyyy", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yy", "dd/MM/yy", "yyyy-MM-dd"];

    private readonly IPurchaseStore _purchases;
    private readonly Func<string, IReadOnlyList<Item>> _search;
    private readonly Func<string, Item?> _exact;
    private readonly Func<string?> _cashier;
    private readonly string _laneId;
    private readonly string _outletState;
    private readonly Func<DateTimeOffset> _now;

    private IReadOnlyList<SupplierBalance> _allSuppliers = [];
    private SupplierBalance? _selectedSupplier;
    private string _supplierSearch = string.Empty;
    private bool _onlyOwed;
    private string _status = string.Empty;

    public PurchasesViewModel(
        IPurchaseStore purchases,
        Func<string, IReadOnlyList<Item>> search,
        Func<string, Item?> exact,
        string laneId,
        string outletStateCode,
        Func<string?>? cashier = null,
        Func<DateTimeOffset>? now = null)
    {
        _purchases = purchases ?? throw new ArgumentNullException(nameof(purchases));
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _exact = exact ?? throw new ArgumentNullException(nameof(exact));
        ArgumentException.ThrowIfNullOrWhiteSpace(laneId);
        _laneId = laneId;
        _outletState = outletStateCode.Trim().PadLeft(2, '0');
        _cashier = cashier ?? (() => null);
        _now = now ?? (() => DateTimeOffset.Now);

        _billDate = Today.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
        _newSupplierState = _outletState;
        _paymentMethod = PaymentMethods[0];
        _paidNowMethod = PaymentMethods[0];

        Follow(nameof(CanSaveBill), nameof(SaveBillBlocker));
        Follow(nameof(CanPay), nameof(PayBlocker));
        Follow(nameof(CanVoid), nameof(VoidBlocker));
    }

    // Why each of the three buttons is greyed out, beside it; empty once it can be pressed.

    /// <summary>What "Save the bill" is waiting for.</summary>
    public string SaveBillBlocker => CanSaveBill
        ? string.Empty
        : SelectedSupplier is null ? "Pick the supplier first."
        : BillNo.Trim().Length == 0 ? "Type the bill number from their bill."
        : "Add the bill's lines.";

    /// <summary>What "Record the payment" is waiting for.</summary>
    public string PayBlocker => CanPay
        ? string.Empty
        : SelectedSupplier is null ? "Pick the supplier first."
        : SelectedSupplier.Owed <= 0m ? "Nothing is owed to them."
        : "Type the amount paid.";

    /// <summary>What "Cancel this bill" is waiting for.</summary>
    public string VoidBlocker => CanVoid
        ? string.Empty
        : SelectedBill is null ? "Pick the bill in the list."
        : SelectedBill.IsVoided ? "That bill is already cancelled."
        : "Say why it is being cancelled.";

    private DateOnly Today => DateOnly.FromDateTime(_now().LocalDateTime);

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public bool IsLoaded { get; private set; }

    /// <summary>Reads the suppliers and the recent bills. Done when the tab is first opened.</summary>
    public void Load()
    {
        try
        {
            RefreshSuppliers();
            RefreshRecent();
            IsLoaded = true;
        }
        catch (Exception ex)
        {
            Status = $"The purchases could not be read: {ex.Message}";
        }
    }

    // ---- Suppliers ---------------------------------------------------------------------------

    public ObservableCollection<SupplierBalance> Suppliers { get; } = [];

    public string SupplierSearch
    {
        get => _supplierSearch;
        set
        {
            if (Set(ref _supplierSearch, value))
                FilterSuppliers();
        }
    }

    /// <summary>Only suppliers the shop owes something — the list for deciding who to pay.</summary>
    public bool OnlyOwed
    {
        get => _onlyOwed;
        set
        {
            if (Set(ref _onlyOwed, value))
                FilterSuppliers();
        }
    }

    /// <summary>What the shop owes all its suppliers together.</summary>
    public string TotalOwedLine => IsOwingSuppliers
        ? $"{Show.Money(_allSuppliers.Sum(s => s.Owed))} owed to {Plural.Of(_allSuppliers.Count(s => s.Owed > 0m), "supplier")}."
        : "Nothing is owed to any supplier.";

    /// <summary>
    /// Whether that line is something to see to: amber when it is, the quiet ink when nothing is
    /// owed - it was red either way, and "nothing is owed" in red reads as a fault.
    /// </summary>
    public bool IsOwingSuppliers => _allSuppliers.Any(s => s.Owed > 0m);

    public SupplierBalance? SelectedSupplier
    {
        get => _selectedSupplier;
        set
        {
            if (!Set(ref _selectedSupplier, value))
                return;

            LoadHistory();
            Raise(nameof(HasSupplier));
            Raise(nameof(SupplierLine));
            Raise(nameof(OwedLine));
            Raise(nameof(InterState));
            Raise(nameof(BillHeading));
            Raise(nameof(CanSaveBill));
            Raise(nameof(CanPay));
        }
    }

    public bool HasSupplier => SelectedSupplier is not null;

    /// <summary>Who the supplier is, in one line: GSTIN, state, phone, and whether they charge GST.</summary>
    public string SupplierLine => SelectedSupplier?.Supplier is { } s
        ? string.Join("  ·  ", new[]
          {
              s.Gstin is { } g ? $"GSTIN {g}" : "no GSTIN",
              GstStates.Label(s.StateCode),
              s.Phone,
              s.ChargesGst ? "charges GST" : "charges no GST",
          }.Where(part => !string.IsNullOrEmpty(part)))
        : "Pick a supplier from the list, or add one.";

    public string OwedLine => SelectedSupplier is { } s
        ? s.Owed > 0m ? $"You owe {s.Supplier.Name} {Money(s.Owed)}." : $"Nothing is owed to {s.Supplier.Name}."
        : string.Empty;

    /// <summary>A supplier in another state bills IGST; one in the same state, CGST and SGST.</summary>
    public bool InterState => SelectedSupplier?.Supplier.StateCode is { } state && state != _outletState;

    public ObservableCollection<CreditMovement> History { get; } = [];

    private void RefreshSuppliers(long? keep = null)
    {
        keep ??= SelectedSupplier?.Supplier.Id;
        _allSuppliers = _purchases.Balances();
        FilterSuppliers();
        Raise(nameof(TotalOwedLine));
        Raise(nameof(IsOwingSuppliers));

        if (keep is { } id)
            SelectedSupplier = Suppliers.FirstOrDefault(s => s.Supplier.Id == id) ?? _allSuppliers.FirstOrDefault(s => s.Supplier.Id == id);
    }

    private void FilterSuppliers()
    {
        var term = _supplierSearch.Trim();

        Suppliers.Clear();

        foreach (var supplier in _allSuppliers)
        {
            if (_onlyOwed && supplier.Owed <= 0m)
                continue;

            if (term.Length > 0 && !supplier.Supplier.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                && !(supplier.Supplier.Gstin?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false))
                continue;

            Suppliers.Add(supplier);
        }
    }

    private void LoadHistory()
    {
        History.Clear();

        if (SelectedSupplier is not { } s)
            return;

        foreach (var movement in _purchases.History(s.Supplier.Id, 30))
            History.Add(movement);
    }

    // ---- Adding a supplier -------------------------------------------------------------------

    private string _newSupplierName = string.Empty;
    private string _newSupplierPhone = string.Empty;
    private string _newSupplierGstin = string.Empty;
    private string _newSupplierState;
    private bool _newSupplierChargesGst = true;

    public string NewSupplierName { get => _newSupplierName; set => Set(ref _newSupplierName, value); }

    public string NewSupplierPhone { get => _newSupplierPhone; set => Set(ref _newSupplierPhone, value); }

    /// <summary>A GSTIN decides the state, so typing one fills the state in.</summary>
    public string NewSupplierGstin
    {
        get => _newSupplierGstin;
        set
        {
            if (!Set(ref _newSupplierGstin, value))
                return;

            if (Gstin.IsValid(value))
                NewSupplierState = Gstin.StateCode(value);

            Raise(nameof(NewSupplierGstinNote));
        }
    }

    /// <summary>Said as the GSTIN is typed, so a wrong one is caught before it is saved.</summary>
    public string NewSupplierGstinNote => _newSupplierGstin.Trim().Length == 0
        ? "Leave blank for a supplier with no GSTIN. Their bills carry no GST."
        : Gstin.Problem(_newSupplierGstin) ?? $"A {GstStates.Name(Gstin.StateCode(_newSupplierGstin))} GSTIN.";

    public string NewSupplierState { get => _newSupplierState; set => Set(ref _newSupplierState, value); }

    /// <summary>Off for a composition dealer, who has a GSTIN but may not charge GST.</summary>
    public bool NewSupplierChargesGst { get => _newSupplierChargesGst; set => Set(ref _newSupplierChargesGst, value); }

    public string? AddSupplier()
    {
        try
        {
            var added = _purchases.AddSupplier(new Supplier(
                0,
                NewSupplierName,
                NewSupplierPhone,
                NewSupplierGstin.Trim().Length == 0 ? null : NewSupplierGstin,
                NewSupplierState,
                NewSupplierChargesGst));

            NewSupplierName = NewSupplierPhone = NewSupplierGstin = string.Empty;
            NewSupplierState = _outletState;
            NewSupplierChargesGst = true;

            RefreshSuppliers(added.Id);
            Status = added.ChargesGst
                ? $"{added.Name} added, from {GstStates.Name(added.StateCode)}. Their bills carry GST you can claim."
                : $"{added.Name} added. Their bills carry no GST.";

            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Status = ex.Message;
        }
    }

    // ---- Entering a bill ---------------------------------------------------------------------

    private string _billNo = string.Empty;
    private string _billDate;
    private string _itemQuery = string.Empty;
    private Item? _lineItem;
    private string _lineQuantity = string.Empty;
    private string _lineRate = string.Empty;
    private string _lineGst = string.Empty;
    private string _lineDiscount = string.Empty;
    private string _lineBatch = string.Empty;
    private string _lineExpiry = string.Empty;
    private string _printedTotal = string.Empty;
    private string _paidNow = string.Empty;
    private PaymentMethodChoice _paidNowMethod;

    public string BillHeading => SelectedSupplier is { } s
        ? $"ENTER A BILL FROM {s.Supplier.Name.ToUpperInvariant()}"
        : "ENTER A BILL — PICK THE SUPPLIER FIRST";

    public string BillNo
    {
        get => _billNo;
        set
        {
            if (Set(ref _billNo, value))
                Raise(nameof(CanSaveBill));
        }
    }

    /// <summary>The date printed on the bill, dd-mm-yyyy. Today unless changed.</summary>
    public string BillDate { get => _billDate; set => Set(ref _billDate, value); }

    /// <summary>Part of a name, a SKU or a barcode. A scanned barcode picks the item outright.</summary>
    public string ItemQuery
    {
        get => _itemQuery;
        set
        {
            if (!Set(ref _itemQuery, value))
                return;

            ItemMatches.Clear();
            var term = value.Trim();

            if (term.Length == 0)
                return;

            if (_exact(term) is { } exact)
            {
                LineItem = exact;
                return;
            }

            foreach (var item in _search(term).Take(8))
                ItemMatches.Add(item);
        }
    }

    public ObservableCollection<Item> ItemMatches { get; } = [];

    /// <summary>The item the next line is for. Picking one fills in its GST rate.</summary>
    public Item? LineItem
    {
        get => _lineItem;
        set
        {
            if (!Set(ref _lineItem, value))
                return;

            if (value is not null)
            {
                LineGst = (SelectedSupplier?.Supplier.ChargesGst ?? true)
                    ? value.GstRate.ToString("0.##", CultureInfo.InvariantCulture)
                    : "0";
                ItemMatches.Clear();
            }

            Raise(nameof(LineItemLine));
        }
    }

    public string LineItemLine => LineItem is { } item
        ? $"{item.Name}  ·  {item.Sku}  ·  sold in {Units.Of(item.UnitType).Code}  ·  sells at {Money(item.SellPrice)}"
          + (item.CostPrice is { } cost ? $"  ·  last cost {Money(cost)}" : string.Empty)
        : "Type part of a name, a SKU or scan a barcode, then pick the item.";

    public string LineQuantity { get => _lineQuantity; set => Set(ref _lineQuantity, value); }

    /// <summary>The rate before tax, per unit, as the bill prints it.</summary>
    public string LineRate { get => _lineRate; set => Set(ref _lineRate, value); }

    public string LineGst { get => _lineGst; set => Set(ref _lineGst, value); }

    public string LineDiscount { get => _lineDiscount; set => Set(ref _lineDiscount, value); }

    public string LineBatch { get => _lineBatch; set => Set(ref _lineBatch, value); }

    /// <summary>Best before, for anything that has one. dd-mm-yyyy, or blank.</summary>
    public string LineExpiry { get => _lineExpiry; set => Set(ref _lineExpiry, value); }

    public ObservableCollection<PurchaseLine> Lines { get; } = [];

    public PurchaseLine? SelectedLine { get; set; }

    /// <summary>Prices the line typed and adds it to the bill.</summary>
    public string? AddLine()
    {
        if (SelectedSupplier is not { } supplier)
            return Status = "Pick the supplier first: whether they charge GST, and from which state, decides the tax.";

        if (LineItem is not { } item)
            return Status = "Pick the item first.";

        if (!TryQuantity(LineQuantity, out var quantity) || quantity <= 0m)
            return Status = $"'{LineQuantity}' is not a quantity.";

        if (!TryAmount(LineRate, out var rate) || rate < 0m)
            return Status = $"'{LineRate}' is not a rate.";

        if (!TryAmount(LineGst.TrimEnd('%'), out var gst) || !ItemCsvParser.ValidGstRates.Contains(gst))
            return Status = $"'{LineGst}' is not a GST rate. Use one of {string.Join(", ", ItemCsvParser.ValidGstRates.Select(r => r.ToString("0.##", CultureInfo.InvariantCulture)))}.";

        var discount = 0m;

        if (LineDiscount.Trim().Length > 0 && (!TryAmount(LineDiscount, out discount) || discount < 0m || discount > quantity * rate))
            return Status = $"'{LineDiscount}' is not a discount on this line.";

        DateOnly? expiry = null;

        if (LineExpiry.Trim().Length > 0)
        {
            if (!DateOnly.TryParseExact(LineExpiry.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                return Status = $"'{LineExpiry}' is not a date. Write it as 31-12-2026.";

            expiry = parsed;
        }

        try
        {
            var line = PurchaseLine.Price(
                new PurchaseLineEntry(item, quantity, rate, gst, discount, LineBatch, expiry),
                InterState,
                supplier.Supplier.ChargesGst);

            Lines.Add(line);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return Status = ex.Message.Split(" (Parameter", 2)[0];
        }

        ItemQuery = string.Empty;
        LineItem = null;
        LineQuantity = LineRate = LineDiscount = LineBatch = LineExpiry = string.Empty;
        LineGst = string.Empty;

        RaiseTotals();
        Status = $"{Plural.Of(Lines.Count, "line")} on the bill, {Money(Lines.Sum(l => l.LineTotal))} so far.";
        return null;
    }

    public void RemoveLine(PurchaseLine line)
    {
        if (Lines.Remove(line))
            RaiseTotals();
    }

    public string TaxableLine => Money(Lines.Sum(l => l.TaxableValue));

    public string TaxLine => InterState
        ? $"IGST {Money(Lines.Sum(l => l.Igst))}"
        : $"CGST {Money(Lines.Sum(l => l.Cgst))}  ·  SGST {Money(Lines.Sum(l => l.Sgst))}";

    public string LinesTotalLine => Money(Lines.Sum(l => l.LineTotal));

    /// <summary>The total printed at the bottom of the paper. Checked against the lines when saving.</summary>
    public string PrintedTotal { get => _printedTotal; set => Set(ref _printedTotal, value); }

    /// <summary>Anything paid for this bill on the spot. Blank if it goes on the account.</summary>
    public string PaidNow { get => _paidNow; set => Set(ref _paidNow, value); }

    public PaymentMethodChoice PaidNowMethod { get => _paidNowMethod; set => Set(ref _paidNowMethod, value); }

    public bool CanSaveBill => SelectedSupplier is not null && BillNo.Trim().Length > 0 && Lines.Count > 0;

    private void RaiseTotals()
    {
        Raise(nameof(TaxableLine));
        Raise(nameof(TaxLine));
        Raise(nameof(LinesTotalLine));
        Raise(nameof(CanSaveBill));
    }

    /// <summary>What saving will do, said before it does it.</summary>
    public string SaveQuestion => SelectedSupplier is { } s
        ? $"Save bill {BillNo.Trim()} from {s.Supplier.Name}: {Plural.Of(Lines.Count, "line")}, {Money(BillTotalForQuestion)}?\n\n"
          + "The shelf count of every counted item goes up, each item's cost price becomes what this bill charged, "
          + $"and {s.Supplier.Name} is owed the total."
        : string.Empty;

    private decimal BillTotalForQuestion =>
        TryAmount(PrintedTotal, out var printed) ? printed : Lines.Sum(l => l.LineTotal);

    /// <summary>Checks the bill against its printed total and saves it.</summary>
    /// <returns>Null when it was saved, or why not.</returns>
    public string? SaveBill()
    {
        if (SelectedSupplier is not { } supplier)
            return Status = "Pick the supplier first.";

        if (BillNo.Trim().Length == 0)
            return Status = "Type the bill number as printed on the supplier's bill.";

        if (Lines.Count == 0)
            return Status = "Add the lines on the bill first.";

        if (!DateOnly.TryParseExact(BillDate.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var billDate))
            return Status = $"'{BillDate}' is not a date. Write it as 31-12-2026.";

        if (billDate > Today.AddDays(1))
            return Status = $"The bill is dated {billDate:dd-MM-yyyy}, which has not happened yet.";

        var bill = new PurchaseBill(supplier.Supplier, BillNo.Trim(), billDate, [.. Lines], InterState);

        if (PrintedTotal.Trim().Length > 0)
        {
            if (!TryAmount(PrintedTotal, out var printed))
                return Status = $"'{PrintedTotal}' is not an amount.";

            var (matched, problem) = bill.MatchPrinted(printed);

            if (problem is not null)
                return Status = problem;

            bill = matched!;
        }

        decimal paidNow = 0m;

        if (PaidNow.Trim().Length > 0 && (!TryAmount(PaidNow, out paidNow) || paidNow < 0m || paidNow > bill.Total))
            return Status = $"'{PaidNow}' is not something that could be paid on a bill of {Money(bill.Total)}.";

        PurchaseRecorded recorded;

        try
        {
            recorded = _purchases.Record(bill, _laneId, _now(), _cashier());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Status = ex.Message;
        }

        var said = new List<string>
        {
            $"Bill {bill.BillNo} from {supplier.Supplier.Name} saved: {Plural.Of(bill.Lines.Count, "line")}, {Money(bill.Total)}.",
            recorded.CountsMoved > 0 ? $"{Plural.Of(recorded.CountsMoved, "shelf count")} went up." : "No counted item was on it.",
        };

        if (recorded.NotCounted.Count > 0)
            said.Add($"Not counted, so no shelf figure changed: {string.Join(", ", recorded.NotCounted)}.");

        if (recorded.CostAboveSellingPrice.Count > 0)
            said.Add($"Now costing more than they sell for — {string.Join("; ", recorded.CostAboveSellingPrice)}.");

        if (paidNow > 0m)
        {
            try
            {
                _purchases.Pay(supplier.Supplier.Id, paidNow, PaidNowMethod.Method, $"with bill {bill.BillNo}", _laneId, _now(), _cashier());
                said.Add($"Paid {Money(paidNow)} {PurchaseRepository.MethodText(PaidNowMethod.Method)}.");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                said.Add($"The bill is saved, but the payment was not: {ex.Message}");
            }
        }

        ClearBill();
        RefreshSuppliers(supplier.Supplier.Id);
        RefreshRecent();

        Status = string.Join(" ", said);
        return null;
    }

    public void ClearBill()
    {
        Lines.Clear();
        BillNo = PrintedTotal = PaidNow = string.Empty;
        BillDate = Today.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
        ItemQuery = string.Empty;
        LineItem = null;
        LineQuantity = LineRate = LineGst = LineDiscount = LineBatch = LineExpiry = string.Empty;
        RaiseTotals();
    }

    // ---- Paying a supplier -------------------------------------------------------------------

    public IReadOnlyList<PaymentMethodChoice> PaymentMethods { get; } =
    [
        new(SupplierPaymentMethod.DrawerCash, "Cash from the till"),
        new(SupplierPaymentMethod.OtherCash, "Cash, not from the till"),
        new(SupplierPaymentMethod.Upi, "UPI"),
        new(SupplierPaymentMethod.Bank, "Bank transfer"),
        new(SupplierPaymentMethod.Cheque, "Cheque"),
    ];

    private string _paymentAmount = string.Empty;
    private PaymentMethodChoice _paymentMethod;
    private string _paymentReference = string.Empty;

    public string PaymentAmount
    {
        get => _paymentAmount;
        set
        {
            if (Set(ref _paymentAmount, value))
                Raise(nameof(CanPay));
        }
    }

    public PaymentMethodChoice PaymentMethod { get => _paymentMethod; set => Set(ref _paymentMethod, value); }

    /// <summary>A UPI reference, a cheque number — whatever proves the payment later.</summary>
    public string PaymentReference { get => _paymentReference; set => Set(ref _paymentReference, value); }

    public bool CanPay => SelectedSupplier is { Owed: > 0m } && PaymentAmount.Trim().Length > 0;

    public string? Pay()
    {
        if (SelectedSupplier is not { } supplier)
            return Status = "Pick the supplier first.";

        if (!TryAmount(PaymentAmount, out var amount) || amount <= 0m)
            return Status = $"'{PaymentAmount}' is not an amount.";

        try
        {
            _purchases.Pay(supplier.Supplier.Id, amount, PaymentMethod.Method, PaymentReference, _laneId, _now(), _cashier());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Status = ex.Message.Split(" (Parameter", 2)[0];
        }

        PaymentAmount = PaymentReference = string.Empty;
        RefreshSuppliers(supplier.Supplier.Id);

        Status = PaymentMethod.Method == SupplierPaymentMethod.DrawerCash
            ? $"Paid {supplier.Supplier.Name} {Money(amount)} from the till. The day-end report takes it off what the drawer should hold."
            : $"Paid {supplier.Supplier.Name} {Money(amount)} by {PurchaseRepository.MethodText(PaymentMethod.Method)}.";

        return null;
    }

    // ---- Bills already entered ---------------------------------------------------------------

    public ObservableCollection<PurchaseSummary> Recent { get; } = [];

    private PurchaseSummary? _selectedBill;
    private string _voidReason = string.Empty;

    public PurchaseSummary? SelectedBill
    {
        get => _selectedBill;
        set
        {
            if (Set(ref _selectedBill, value))
                Raise(nameof(CanVoid));
        }
    }

    public string VoidReason
    {
        get => _voidReason;
        set
        {
            if (Set(ref _voidReason, value))
                Raise(nameof(CanVoid));
        }
    }

    public bool CanVoid => SelectedBill is { IsVoided: false } && VoidReason.Trim().Length > 0;

    /// <summary>Cancels a bill entered by mistake. The record stays; the stock and the amount owed do not.</summary>
    public string? VoidBill()
    {
        if (SelectedBill is not { IsVoided: false } bill)
            return Status = "Pick a bill that has not been cancelled.";

        try
        {
            _purchases.Void(bill.Id, VoidReason, _laneId, _now());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Status = ex.Message.Split(" (Parameter", 2)[0];
        }

        VoidReason = string.Empty;
        RefreshSuppliers();
        RefreshRecent();

        Status = $"Bill {bill.BillNo} from {bill.SupplierName} cancelled. What it put on the shelf is taken back off, and it is no longer owed. It can now be entered again.";
        return null;
    }

    private void RefreshRecent()
    {
        Recent.Clear();

        foreach (var bill in _purchases.Recent(40))
            Recent.Add(bill);
    }

    // ---- Parsing -----------------------------------------------------------------------------

    private static bool TryAmount(string text, out decimal value) =>
        decimal.TryParse(text.Replace(",", string.Empty, StringComparison.Ordinal).Replace("₹", string.Empty, StringComparison.Ordinal).Trim(),
            NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    private static bool TryQuantity(string text, out decimal value) => TryAmount(text, out value);

    private static string Money(decimal value) => value.ToString("N2", Indian);
}
