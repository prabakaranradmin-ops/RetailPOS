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

    /// <summary>Short unit label for the grid — "kg" reads faster across a counter than "Kilogram".</summary>
    public string UnitLabel => Line.Unit switch
    {
        UnitType.Kilogram => "kg",
        UnitType.Litre => "L",
        UnitType.Metre => "m",
        _ => "pc",
    };
    public decimal Mrp => Line.Mrp;
    public decimal UnitRateExclTax => Line.UnitRateExclTax;
    public decimal Discount => Line.Discount;

    public decimal CgstRate => Line.CgstRate;
    public decimal SgstRate => Line.SgstRate;
    public decimal IgstRate => Line.IgstRate;

    public decimal TaxAmount => Line.Tax.SplitTax;
    public decimal LineTotal => Line.LineTotal;

    /// <summary>Re-reads every figure from the domain line after an edit.</summary>
    public void Refresh() => RaiseAll();
}
