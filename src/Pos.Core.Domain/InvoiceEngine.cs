namespace Pos.Core.Domain;

/// <summary>
/// The bill currently open at the till. Owns the line collection and all the edits the cashier
/// can make to it; totals are always derived from the lines, never tracked separately.
/// </summary>
public sealed class InvoiceEngine
{
    private readonly List<InvoiceLine> _lines = [];

    /// <param name="outletStateCode">
    /// GST state code of the outlet. Compared against the customer's state code to decide
    /// between a CGST/SGST split and a single IGST charge.
    /// </param>
    /// <param name="taxMode">
    /// Whether this lane issues tax invoices or bills of supply. Defaults to <see cref="TaxMode.Gst"/>,
    /// which is what every lane was before the setting existed.
    /// </param>
    public InvoiceEngine(string outletStateCode, TaxMode taxMode = TaxMode.Gst)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outletStateCode);
        OutletStateCode = outletStateCode;
        TaxMode = taxMode;
    }

    public string OutletStateCode { get; }

    /// <summary>Whether this bill is a tax invoice or a bill of supply.</summary>
    public TaxMode TaxMode { get; private set; }

    /// <summary>
    /// Changes what kind of bill this lane issues, from the owner's screen.
    /// </summary>
    /// <remarks>
    /// Refused while a bill is on the screen. Every line already carries the tax it was rung up
    /// with, so switching mid-bill would leave one bill holding lines priced two different ways and
    /// a total that reconciles with neither. Clearing the screen first is a one-line rule the owner
    /// can act on; silently re-pricing what a customer is standing there watching is not.
    ///
    /// Bills already settled are untouched: each records its own mode, so the shop's history stays
    /// exactly as it was issued.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A bill is open.</exception>
    public void SetTaxMode(TaxMode mode)
    {
        if (!IsEmpty)
            throw new InvalidOperationException("Finish or clear the bill on screen before changing what kind of bill this lane issues.");

        TaxMode = mode;
    }

    public IReadOnlyList<InvoiceLine> Lines => _lines;

    public Customer? Customer { get; private set; }

    /// <summary>
    /// True when the customer's place of supply is outside the outlet's state. A walk-in with no
    /// customer attached is treated as intra-state, which is the normal counter sale.
    /// </summary>
    public bool IsInterState =>
        !string.IsNullOrWhiteSpace(Customer?.StateCode) &&
        !string.Equals(Customer.StateCode, OutletStateCode, StringComparison.OrdinalIgnoreCase);

    public InvoiceTotals Totals => InvoiceTotals.From(_lines);

    public bool IsEmpty => _lines.Count == 0;

    /// <summary>
    /// Attaching or changing the customer can flip the whole bill between CGST/SGST and IGST,
    /// so every existing line is re-flagged.
    /// </summary>
    public void SetCustomer(Customer? customer)
    {
        Customer = customer;
        var interState = IsInterState;

        foreach (var line in _lines)
            line.IsInterState = interState;
    }

    /// <summary>Appends the item as a new line. Per SRS 2.1 a selection always adds at quantity 1.</summary>
    public InvoiceLine AddItem(Item item, decimal quantity = 1m)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!item.IsActive)
            throw new InvalidOperationException($"{item.Name} is not an active item and cannot be billed.");

        var line = InvoiceLine.FromItem(item, quantity, IsInterState, TaxMode);
        _lines.Add(line);
        return line;
    }

    /// <summary>Restores a line as-is. Used by recall, which must not re-derive anything from the item master.</summary>
    public void AddExistingLine(InvoiceLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        _lines.Add(line);
    }

    public void RemoveAt(int index)
    {
        GuardIndex(index);
        _lines.RemoveAt(index);
    }

    public void SetQuantity(int index, decimal quantity)
    {
        GuardIndex(index);
        _lines[index].Quantity = quantity;
    }

    /// <summary>
    /// Nudges a line's quantity by <paramref name="delta"/>, for the increment/decrement keypress.
    /// Decrementing to zero removes the line rather than throwing, which is what the cashier expects
    /// from holding the minus key down.
    /// </summary>
    public void AdjustQuantity(int index, decimal delta)
    {
        GuardIndex(index);

        var target = _lines[index].Quantity + delta;

        if (target <= 0m)
            _lines.RemoveAt(index);
        else
            _lines[index].Quantity = target;
    }

    public void SetDiscount(int index, decimal discount)
    {
        GuardIndex(index);
        _lines[index].Discount = discount;
    }

    public void Clear()
    {
        _lines.Clear();
        Customer = null;
    }

    /// <summary>Deep copy of the current lines, for parking the bill.</summary>
    public List<InvoiceLine> SnapshotLines() => _lines.Select(l => l.Clone()).ToList();

    /// <summary>Replaces the whole bill with a previously parked one.</summary>
    public void Restore(IEnumerable<InvoiceLine> lines, Customer? customer)
    {
        ArgumentNullException.ThrowIfNull(lines);

        _lines.Clear();
        Customer = customer;
        _lines.AddRange(lines);
    }

    private void GuardIndex(int index)
    {
        if (index < 0 || index >= _lines.Count)
            throw new ArgumentOutOfRangeException(nameof(index), index, "No such invoice line.");
    }
}
