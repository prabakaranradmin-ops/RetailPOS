using System.Globalization;
using Pos.Core.Hardware.Printing;
using Pos.Core.Tax;

namespace Pos.Core.Domain.Printing;

/// <summary>
/// Turns a settled invoice into a receipt.
/// </summary>
/// <remarks>
/// This sits in the domain rather than in the hardware layer because what belongs on a GST invoice
/// is a matter of tax law, not of printers. The hardware layer knows how to lay out columns and
/// cut paper; it has no business knowing what an HSN code is. The split also means the content can
/// be tested by reading the receipt as text.
/// </remarks>
public sealed class ReceiptComposer(StoreProfile store, int paperWidthChars = ReceiptBuilder.Width80Mm)
{
    private readonly StoreProfile _store = store ?? throw new ArgumentNullException(nameof(store));

    public int PaperWidthChars { get; } = paperWidthChars;

    public ReceiptBuilder Compose(SettledInvoice invoice, bool isReprint = false)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        var sale = invoice.Sale;
        var receipt = new ReceiptBuilder(PaperWidthChars);

        WriteHeader(receipt, invoice, isReprint);
        WriteLines(receipt, sale);
        WriteTotals(receipt, sale);
        WriteTaxSummary(receipt, sale);
        WritePayments(receipt, sale);
        WriteLoyalty(receipt, sale);
        WriteFooter(receipt);

        return receipt;
    }

    private void WriteHeader(ReceiptBuilder receipt, SettledInvoice invoice, bool isReprint)
    {
        receipt.Text(_store.Name, TextAlignment.Center, bold: true, widthMultiplier: 2, heightMultiplier: 2);

        foreach (var line in new[] { _store.AddressLine1, _store.AddressLine2 })
        {
            if (!string.IsNullOrWhiteSpace(line))
                receipt.Text(line, TextAlignment.Center);
        }

        if (!string.IsNullOrWhiteSpace(_store.Phone))
            receipt.Text($"Ph: {_store.Phone}", TextAlignment.Center);

        if (!string.IsNullOrWhiteSpace(_store.Gstin))
            receipt.Text($"GSTIN: {_store.Gstin}", TextAlignment.Center);

        receipt.Blank();
        receipt.Text("TAX INVOICE", TextAlignment.Center, bold: true);

        // A reprint has to say so on its face, or it can be passed off as a second sale.
        if (isReprint)
            receipt.Text("** REPRINT **", TextAlignment.Center, bold: true);

        receipt.Rule();

        var sale = invoice.Sale;
        receipt.Columns("Invoice", invoice.InvoiceNo);
        receipt.Columns("Date", sale.CreatedAt.ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture));
        receipt.Columns("Lane", sale.LaneId);

        if (sale.Customer is { } customer)
        {
            receipt.Columns("Customer", customer.Name ?? customer.MobileNo);

            if (customer.Name is not null)
                receipt.Columns("Mobile", customer.MobileNo);
        }

        if (sale.RecalledFromToken is { } token)
            receipt.Columns("Parked as", token);

        receipt.Rule();
    }

    private void WriteLines(ReceiptBuilder receipt, SaleDraft sale)
    {
        receipt.Row("Item", new ColumnValue("Qty", 6), new ColumnValue("Rate", 9), new ColumnValue("Amount", 10));
        receipt.Rule();

        foreach (var line in sale.Lines)
        {
            receipt.Row(
                line.NameSnapshot,
                new ColumnValue(Quantity(line), 6),
                new ColumnValue(Amount(line.Mrp), 9),
                new ColumnValue(Amount(line.LineTotal), 10));

            // HSN belongs on a GST invoice, and the discount has to be visible or the customer
            // cannot reconcile the line against the shelf price.
            var detail = $"  HSN {line.HsnSnapshot}  GST {Rate(line.GstRate)}%";

            if (line.Discount > 0m)
                detail += $"  less {Amount(line.Discount)}";

            receipt.Text(detail);
        }

        receipt.Rule();
    }

    private void WriteTotals(ReceiptBuilder receipt, SaleDraft sale)
    {
        var totals = sale.Totals;

        receipt.Columns("Taxable value", Amount(totals.SubtotalTaxable));

        if (totals.TotalDiscount > 0m)
            receipt.Columns("Discount", Amount(totals.TotalDiscount));

        if (totals.TotalCgst > 0m || totals.TotalSgst > 0m)
        {
            receipt.Columns("CGST", Amount(totals.TotalCgst));
            receipt.Columns("SGST", Amount(totals.TotalSgst));
        }

        if (totals.TotalIgst > 0m)
            receipt.Columns("IGST", Amount(totals.TotalIgst));

        receipt.Rule('=');
        receipt.Columns($"TOTAL {_store.CurrencyPrefix}", Amount(totals.GrandTotal), bold: true);
        receipt.Rule('=');
        receipt.Columns($"Items: {totals.LineCount}", $"Qty: {Quantity(totals.TotalQuantity)}");
    }

    /// <summary>
    /// The rate-wise tax breakup a GST invoice is expected to carry, so the tax at each slab can be
    /// read off without recomputing it from the lines.
    /// </summary>
    private void WriteTaxSummary(ReceiptBuilder receipt, SaleDraft sale)
    {
        var slabs = sale.Lines
            .GroupBy(line => line.GstRate)
            .OrderBy(group => group.Key)
            .Select(group => new
            {
                Rate = group.Key,
                Taxable = Money.ToPresentation(group.Sum(l => l.Tax.TaxableValue)),
                Tax = Money.ToPresentation(group.Sum(l => l.Tax.SplitTax)),
            })
            .Where(slab => slab.Taxable > 0m || slab.Tax > 0m)
            .ToList();

        if (slabs.Count == 0)
            return;

        receipt.Blank();
        receipt.Text("Tax summary", bold: true);
        receipt.Row("Rate", new ColumnValue("Taxable", 12), new ColumnValue("Tax", 10));

        foreach (var slab in slabs)
            receipt.Row($"{Rate(slab.Rate)}%", new ColumnValue(Amount(slab.Taxable), 12), new ColumnValue(Amount(slab.Tax), 10));
    }

    private void WritePayments(ReceiptBuilder receipt, SaleDraft sale)
    {
        if (sale.Payments.Count == 0)
            return;

        receipt.Blank();
        receipt.Text("Payment", bold: true);

        foreach (var payment in sale.Payments)
        {
            receipt.Columns(Label(payment.Type), Amount(payment.Amount));

            if (!string.IsNullOrWhiteSpace(payment.ReferenceNo))
                receipt.Text($"  {payment.ReferenceNo}");
        }

        if (sale.ChangeDue > 0m)
            receipt.Columns("Change", Amount(sale.ChangeDue), bold: true);
    }

    private void WriteLoyalty(ReceiptBuilder receipt, SaleDraft sale)
    {
        if (sale.Customer is null || (sale.PointsRedeemed == 0 && sale.PointsEarned == 0))
            return;

        receipt.Blank();
        receipt.Text("Reward points", bold: true);

        if (sale.PointsRedeemed > 0)
            receipt.Columns("Redeemed", sale.PointsRedeemed.ToString(CultureInfo.InvariantCulture));

        if (sale.PointsEarned > 0)
            receipt.Columns("Earned", sale.PointsEarned.ToString(CultureInfo.InvariantCulture));

        receipt.Columns("Balance", sale.Customer.LoyaltyBalance.ToString(CultureInfo.InvariantCulture));
    }

    private void WriteFooter(ReceiptBuilder receipt)
    {
        receipt.Blank();

        if (!string.IsNullOrWhiteSpace(_store.FooterMessage))
            receipt.Text(_store.FooterMessage, TextAlignment.Center);

        receipt.Cut();
    }

    private static string Amount(decimal value) => value.ToString("N2", CultureInfo.InvariantCulture);

    private static string Rate(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Quantity(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Quantity(InvoiceLine line) => Quantity(line.Quantity);

    private static string Label(TenderType type) => type switch
    {
        TenderType.Cash => "Cash",
        TenderType.Card => "Card",
        TenderType.Upi => "UPI",
        TenderType.StoreCredit => "Store credit",
        TenderType.LoyaltyPoints => "Loyalty points",
        _ => type.ToString(),
    };
}
