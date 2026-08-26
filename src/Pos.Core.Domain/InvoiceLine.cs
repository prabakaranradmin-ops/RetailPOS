using Pos.Core.Tax;

namespace Pos.Core.Domain;

/// <summary>
/// One priced row of an invoice. Item name and HSN are copied onto the line rather than
/// looked up through the item id, so a reprint years later still shows what was actually sold.
/// </summary>
public sealed class InvoiceLine
{
    private decimal _quantity;
    private decimal _discount;
    private bool _isInterState;
    private TaxLineResult? _cachedTax;

    public required long ItemId { get; init; }
    public required string NameSnapshot { get; init; }
    public required string HsnSnapshot { get; init; }
    public string? BarcodeSnapshot { get; init; }
    public string? BatchNo { get; init; }
    public UnitType Unit { get; init; } = UnitType.Each;

    /// <summary>
    /// The department the item was in when it was sold, and what the shop was paying for it then.
    /// </summary>
    /// <remarks>
    /// Snapshotted for the same reason the tax is: a shop moves an item between departments and
    /// renegotiates its cost, and reading either from the catalogue at report time would restate
    /// last quarter's figures every time one of them changed. Both are null on lines sold before
    /// the catalogue carried them, which is honest — nobody knew the cost then.
    /// </remarks>
    public string? CategorySnapshot { get; init; }

    /// <inheritdoc cref="CategorySnapshot"/>
    public decimal? CostSnapshot { get; init; }

    /// <summary>Printed MRP, kept for display only — it does not feed the tax maths.</summary>
    public decimal Mrp { get; init; }

    /// <summary>The price the tax engine works from: MRP-style when <see cref="IsTaxInclusive"/>, else the net rate.</summary>
    public required decimal UnitPrice { get; init; }

    public bool IsTaxInclusive { get; init; } = true;

    public decimal GstRate { get; init; }

    public decimal Quantity
    {
        get => _quantity;
        set
        {
            if (value <= 0m)
                throw new ArgumentOutOfRangeException(nameof(value), value, "Quantity must be greater than zero.");

            if (!Unit.AllowsFractionalQuantity() && decimal.Truncate(value) != value)
                throw new ArgumentOutOfRangeException(nameof(value), value, $"{NameSnapshot} is sold by the piece and cannot take a fractional quantity.");

            _quantity = value;
            _cachedTax = null;
        }
    }

    public decimal Discount
    {
        get => _discount;
        set
        {
            if (value < 0m)
                throw new ArgumentOutOfRangeException(nameof(value), value, "Discount cannot be negative.");

            if (value > _quantity * UnitPrice)
                throw new ArgumentOutOfRangeException(nameof(value), value, "Discount cannot exceed the line value.");

            _discount = value;
            _cachedTax = null;
        }
    }

    /// <summary>
    /// Set from the invoice when the customer (and therefore the place of supply) changes.
    /// </summary>
    public bool IsInterState
    {
        get => _isInterState;
        internal set
        {
            if (_isInterState == value)
                return;

            _isInterState = value;
            _cachedTax = null;
        }
    }

    public TaxLineResult Tax => _cachedTax ??= TaxEngine.Calculate(
        new TaxLineInput(_quantity, UnitPrice, _discount, GstRate, _isInterState, IsTaxInclusive));

    /// <summary>Tax-exclusive rate for one unit, as shown in the grid.</summary>
    public decimal UnitRateExclTax => Money.ToPresentation(Tax.TaxableValue / _quantity);

    public decimal CgstRate => _isInterState ? 0m : GstRate / 2m;
    public decimal SgstRate => _isInterState ? 0m : GstRate / 2m;
    public decimal IgstRate => _isInterState ? GstRate : 0m;

    public decimal LineTotal => Tax.LineTotal;

    /// <summary>
    /// Copies this line, including its quantity and discount. Used by hold/recall so a parked
    /// bill is restored to exactly the state it was parked in.
    /// </summary>
    public InvoiceLine Clone() => new()
    {
        ItemId = ItemId,
        NameSnapshot = NameSnapshot,
        HsnSnapshot = HsnSnapshot,
        BarcodeSnapshot = BarcodeSnapshot,
        BatchNo = BatchNo,
        Unit = Unit,
        Mrp = Mrp,
        UnitPrice = UnitPrice,
        IsTaxInclusive = IsTaxInclusive,
        GstRate = GstRate,
        Quantity = Quantity,
        Discount = Discount,
        IsInterState = IsInterState,
    };

    /// <summary>
    /// Rebuilds a line from stored fields, for reading an invoice or a parked bill back.
    /// </summary>
    /// <remarks>
    /// Nothing is re-derived from the item master: the stored name, HSN and price are used exactly
    /// as they were written, so a reprint shows what was sold rather than what the item looks like
    /// today. Going through a factory also fixes the order the quantity and discount setters run
    /// in, since both validate against fields that must already be populated.
    /// </remarks>
    public static InvoiceLine Rehydrate(
        long itemId,
        string nameSnapshot,
        string hsnSnapshot,
        string? barcodeSnapshot,
        string? batchNo,
        UnitType unit,
        decimal mrp,
        decimal unitPrice,
        bool isTaxInclusive,
        decimal gstRate,
        decimal quantity,
        decimal discount,
        bool isInterState,

        // Appended, and optional, so that adding them did not have to touch a hundred call sites
        // that have nothing to say about either.
        string? categorySnapshot = null,
        decimal? costSnapshot = null) => new()
    {
        ItemId = itemId,
        NameSnapshot = nameSnapshot,
        HsnSnapshot = hsnSnapshot,
        BarcodeSnapshot = barcodeSnapshot,
        BatchNo = batchNo,
        Unit = unit,
        Mrp = mrp,
        UnitPrice = unitPrice,
        IsTaxInclusive = isTaxInclusive,
        GstRate = gstRate,
        Quantity = quantity,
        Discount = discount,
        IsInterState = isInterState,
        CategorySnapshot = categorySnapshot,
        CostSnapshot = costSnapshot,
    };

    /// <summary>Builds a line from an item master record at quantity 1.</summary>
    public static InvoiceLine FromItem(Item item, decimal quantity = 1m, bool isInterState = false) => new()
    {
        ItemId = item.Id,
        NameSnapshot = item.Name,
        HsnSnapshot = item.HsnCode,
        BarcodeSnapshot = item.Barcode,
        Unit = item.UnitType,
        Mrp = item.Mrp,
        UnitPrice = item.SellPrice,
        IsTaxInclusive = item.IsTaxInclusive,
        GstRate = item.GstRate,
        Quantity = quantity,
        IsInterState = isInterState,
        CategorySnapshot = item.Category,
        CostSnapshot = item.CostPrice,
    };
}
