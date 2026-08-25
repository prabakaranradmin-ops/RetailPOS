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
        decimal quantity = 0m, taxable = 0m, discount = 0m;
        decimal cgst = 0m, sgst = 0m, igst = 0m, grandTotal = 0m;

        foreach (var line in lines)
        {
            var tax = line.Tax;
            count++;
            quantity += line.Quantity;
            // Accumulate at internal precision and round once at the end, so a long bill does
            // not collect a paisa of error per line.
            taxable += tax.TaxableValue;
            discount += line.Discount;
            cgst += tax.Cgst;
            sgst += tax.Sgst;
            igst += tax.Igst;
            grandTotal += tax.LineTotal;
        }

        return new InvoiceTotals(
            count,
            quantity,
            Money.ToPresentation(taxable),
            Money.ToPresentation(discount),
            Money.ToPresentation(cgst),
            Money.ToPresentation(sgst),
            Money.ToPresentation(igst),
            Money.ToPresentation(grandTotal));
    }
}
