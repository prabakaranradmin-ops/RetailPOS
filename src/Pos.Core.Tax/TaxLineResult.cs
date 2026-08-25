namespace Pos.Core.Tax;

/// <summary>
/// The priced form of one invoice line.
/// </summary>
/// <param name="Gross">Quantity x unit price, less the line discount.</param>
/// <param name="TaxableValue">Tax-exclusive value of the line, carried at 4 decimals.</param>
/// <param name="TotalTax">Whole GST amount before it is split, carried at 4 decimals.</param>
/// <param name="Cgst">Central GST, 2 decimals. Zero on inter-state lines.</param>
/// <param name="Sgst">State GST, 2 decimals. Zero on inter-state lines.</param>
/// <param name="Igst">Integrated GST, 2 decimals. Zero on intra-state lines.</param>
/// <param name="LineTotal">Tax-inclusive amount charged for the line, 2 decimals.</param>
public readonly record struct TaxLineResult(
    decimal Gross,
    decimal TaxableValue,
    decimal TotalTax,
    decimal Cgst,
    decimal Sgst,
    decimal Igst,
    decimal LineTotal)
{
    /// <summary>Sum of the three split components — what actually lands on the GST return.</summary>
    public decimal SplitTax => Cgst + Sgst + Igst;

    /// <summary>Taxable value rounded for display and for the printed invoice.</summary>
    public decimal TaxableValueForDisplay => Money.ToPresentation(TaxableValue);
}
