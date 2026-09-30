using System.Globalization;
using Pos.Core.Domain;

namespace Pos.App.ViewModels;

/// <summary>
/// One row of the invoice grid. Columns follow SRS 2.2; every figure is read straight from the
/// domain line so the grid can never disagree with what the tax engine computed.
/// </summary>
public sealed class InvoiceLineViewModel(InvoiceLine line) : ObservableObject
{
    public InvoiceLine Line { get; } = line ?? throw new ArgumentNullException(nameof(line));

    public string Name => Line.NameSnapshot;
    public string Hsn => Line.HsnSnapshot;

    /// <summary>Batch takes precedence over barcode in this column when the item carries one.</summary>
    public string BarcodeOrBatch => Line.BatchNo ?? Line.BarcodeSnapshot ?? string.Empty;

    public decimal Quantity => Line.Quantity;
    public UnitType Unit => Line.Unit;

    /// <summary>
    /// Short unit label for the grid — "kg" reads faster across a counter than "Kilogram". A
    /// traditional unit shows in Tamil, the word the customer asked for it by.
    /// </summary>
    public string UnitLabel => Units.Of(Line.Unit).Group == UnitGroup.Standard
        ? Units.ScreenLabel(Line.Unit)
        : Units.Of(Line.Unit).Tamil;
    public decimal Mrp => Line.Mrp;
    public decimal UnitRateExclTax => Line.UnitRateExclTax;
    public decimal Discount => Line.Discount;

    /// <summary>The offer the discount came from, under the item's name; empty for a hand discount or none.</summary>
    public string OfferName => Line.OfferName ?? string.Empty;

    public bool HasOffer => Line.OfferName is not null;

    /// <summary>
    /// Money off, as a minus - "−49.00" - or nothing at all.
    /// </summary>
    /// <remarks>
    /// A column of 0.00 reads as a figure worth checking, so a line with no discount used to show a
    /// dash; drawn in the discount's red, that put a red mark on every line of every bill. The cell
    /// is simply empty now, and the lines that do have money off stand out for having anything there.
    /// </remarks>
    public string DiscountLabel => Line.Discount > 0m
        ? "−" + Line.Discount.ToString("N2", CultureInfo.InvariantCulture)
        : string.Empty;

    /// <summary>
    /// Where this line sits on the bill, so a cashier and a customer can point at the same row.
    /// </summary>
    /// <remarks>
    /// Set by the view model that owns the collection rather than read off the domain line, which
    /// has no idea what position it holds and should not acquire one.
    /// </remarks>
    public int LineNumber
    {
        get => _lineNumber;
        set => Set(ref _lineNumber, value);
    }

    private int _lineNumber;

    public decimal CgstRate => Line.CgstRate;
    public decimal SgstRate => Line.SgstRate;
    public decimal IgstRate => Line.IgstRate;

    public decimal TaxAmount => Line.Tax.SplitTax;
    public decimal LineTotal => Line.LineTotal;

    /// <summary>Re-reads every figure from the domain line after an edit.</summary>
    public void Refresh() => RaiseAll();
}
