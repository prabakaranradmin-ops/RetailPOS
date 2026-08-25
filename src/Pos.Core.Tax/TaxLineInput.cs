namespace Pos.Core.Tax;

/// <summary>
/// Everything the GST engine needs to price one invoice line. Deliberately a plain
/// value type with no reference to the item master, so the engine stays pure.
/// </summary>
/// <param name="Quantity">Units sold. Fractional for weighed items (kg, litre).</param>
/// <param name="UnitPrice">
/// Per-unit price. MRP when <paramref name="IsTaxInclusive"/> is true, otherwise the
/// tax-exclusive rate.
/// </param>
/// <param name="Discount">Absolute rupee discount applied to the whole line, not per unit.</param>
/// <param name="GstRate">Combined GST percentage for the item (e.g. 18 for 18%).</param>
/// <param name="IsInterState">
/// True when the customer's state differs from the outlet's, which routes the whole tax
/// to IGST instead of splitting it into CGST/SGST.
/// </param>
/// <param name="IsTaxInclusive">True for MRP-style pricing where the tax is already inside the price.</param>
public readonly record struct TaxLineInput(
    decimal Quantity,
    decimal UnitPrice,
    decimal Discount,
    decimal GstRate,
    bool IsInterState,
    bool IsTaxInclusive);
