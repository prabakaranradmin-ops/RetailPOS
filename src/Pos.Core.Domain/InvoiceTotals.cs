using Pos.Core.Tax;

namespace Pos.Core.Domain;

/// <summary>
/// Aggregated invoice figures. <see cref="GrandTotal"/> is the sum of the line totals shown in
/// the grid rather than a separately derived figure, so the printed total always reconciles
/// line by line with what the cashier and customer can see.
/// </summary>
public readonly record struct InvoiceTotals(
    int LineCount,
    decimal TotalQuantity,
    decimal SubtotalTaxable,
    decimal TotalDiscount,
    decimal TotalCgst,
    decimal TotalSgst,
    decimal TotalIgst,
    decimal GrandTotal)
{
    public decimal TotalTax => TotalCgst + TotalSgst + TotalIgst;

    public static InvoiceTotals Empty => new(0, 0m, 0m, 0m, 0m, 0m, 0m, 0m);

    public static InvoiceTotals From(IEnumerable<InvoiceLine> lines)
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
            total);
    }
}
