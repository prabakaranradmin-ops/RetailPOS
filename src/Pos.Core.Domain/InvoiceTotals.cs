using Pos.Core.Tax;

namespace Pos.Core.Domain;

/// <summary>
/// Aggregated invoice figures. <see cref="GrandTotal"/> is the sum of the line totals shown in
/// the grid rather than a separately derived figure, so the printed total always reconciles
/// line by line with what the cashier and customer can see.
/// </summary>
/// <param name="RoundOff">
/// What the grand total was nudged by to reach a whole rupee, between -0.50 and +0.50, or zero on a
/// lane that does not round. It is an adjustment to what is payable and nothing else: no line
/// price, no taxable value and no part of the tax split moves with it.
/// </param>
public readonly record struct InvoiceTotals(
    int LineCount,
    decimal TotalQuantity,
    decimal SubtotalTaxable,
    decimal TotalDiscount,
    decimal TotalCgst,
    decimal TotalSgst,
    decimal TotalIgst,
    decimal GrandTotal,
    decimal RoundOff = 0m)
{
    public decimal TotalTax => TotalCgst + TotalSgst + TotalIgst;

    /// <summary>
    /// What the customer actually hands over, and what the tender has to settle.
    /// </summary>
    /// <remarks>
    /// Every figure that has to match the drawer is taken from here rather than from
    /// <see cref="GrandTotal"/>: the tender, the change, the cash line on the Z-report and the
    /// takings on the dashboard. A bill counted one way and reconciled the other would leave a
    /// shopkeeper hunting a rupee that was never missing.
    /// </remarks>
    public decimal AmountPayable => GrandTotal + RoundOff;

    public static InvoiceTotals Empty => new(0, 0m, 0m, 0m, 0m, 0m, 0m, 0m);

    /// <param name="roundToRupee">
    /// Whether this lane settles to the whole rupee. Off by default so that a caller which has not
    /// been told about the shop's setting cannot quietly invent a round-off of its own.
    /// </param>
    public static InvoiceTotals From(IEnumerable<InvoiceLine> lines, bool roundToRupee = false)
    {
        var count = 0;
        decimal quantity = 0m, discount = 0m;
        decimal cgst = 0m, sgst = 0m, igst = 0m, grandTotal = 0m;

        foreach (var line in lines)
        {
            var tax = line.Tax;
            count++;
            quantity += line.Quantity;
            discount += line.Discount;

            // The split components and the line total are already at presentation precision, so
            // these sums are exact — no rounding error accumulates across a long bill.
            cgst += tax.Cgst;
            sgst += tax.Sgst;
            igst += tax.Igst;
            grandTotal += tax.LineTotal;
        }

        var totalTax = Money.ToPresentation(cgst) + Money.ToPresentation(sgst) + Money.ToPresentation(igst);
        var total = Money.ToPresentation(grandTotal);

        // Banker's rounding, the same rule every other step uses, so a bill ending in exactly fifty
        // paise goes to the even rupee rather than always up. Half of them round down, which over a
        // day's trading is the difference between a rounding rule and a levy.
        var roundOff = roundToRupee ? Money.Round(total, 0) - total : 0m;

        return new InvoiceTotals(
            count,
            quantity,
            // Taxable value is what is left of the invoice after its tax, rather than an
            // independent sum of the lines' taxable values.
            //
            // Both are defensible to within a paisa, and they disagree by exactly that on some
            // bills: each line total is rounded to paise on its own, so the sum of the rounded
            // line totals is not always the rounded sum of the unrounded parts. Deriving the
            // taxable value from the total makes the three headline figures on the invoice add up
            // by construction — which is how anyone reading a GST invoice, or filing from one,
            // expects them to behave. The alternative leaves a stray paisa that has to be
            // explained on every return it appears in.
            total - totalTax,
            Money.ToPresentation(discount),
            Money.ToPresentation(cgst),
            Money.ToPresentation(sgst),
            Money.ToPresentation(igst),
            total,
            roundOff);
    }
}
