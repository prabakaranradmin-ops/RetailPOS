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
public sealed class ReceiptComposer
{
    /// <summary>
    /// Narrowest paper that will take the side-by-side blocks — the bill number beside the date,
    /// and the four tenders two across. Below this they go one per line instead, which is not a
    /// preference: five cells on 32-character paper leaves six characters each, and a figure
    /// truncated to six characters is a wrong figure.
    /// </summary>
    private const int MinPairedLayoutWidth = 40;

    private readonly StoreProfile _store;

    public ReceiptComposer(
        StoreProfile store,
        int paperWidthChars = ReceiptBuilder.Width80Mm,
        ReceiptLanguage language = ReceiptLanguage.English)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        PaperWidthChars = paperWidthChars;
        Language = language;
        Labels = ReceiptLabels.For(language);
    }

    public int PaperWidthChars { get; }

    public ReceiptLanguage Language { get; }

    public ReceiptLabels Labels { get; }

    private bool Paired => PaperWidthChars >= MinPairedLayoutWidth;

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
        WriteSavingsAndPoints(receipt, sale);
        WriteFooter(receipt, sale);

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

        // The shop's own number is dropped when a customer care number is configured, rather than
        // printing two numbers a customer has to choose between.
        if (!string.IsNullOrWhiteSpace(_store.Phone) && string.IsNullOrWhiteSpace(_store.CustomerCarePhone))
            receipt.Text($"Ph: {_store.Phone}", TextAlignment.Center);

        if (!string.IsNullOrWhiteSpace(_store.Gstin))
            receipt.Text($"GSTIN {_store.Gstin}", TextAlignment.Center);

        // A shop selling food has to display its FSSAI licence, and the bill is where a customer
        // looks for it.
        if (!string.IsNullOrWhiteSpace(_store.FssaiNumber))
            receipt.Text($"FSSAI No {_store.FssaiNumber}", TextAlignment.Center);

        if (!string.IsNullOrWhiteSpace(_store.CustomerCarePhone))
            receipt.Text($"Customer Care - {_store.CustomerCarePhone}", TextAlignment.Center);

        receipt.Blank();

        // What the document is called is decided by the sale, not by how the lane is set up today.
        // A composition dealer who later registers normally must still reprint last year's bills as
        // the bills of supply they were.
        receipt.Text(
            invoice.Sale.TaxMode == TaxMode.Composition ? Labels.BillOfSupply : Labels.TaxInvoice,
            TextAlignment.Center,
            bold: true);

        // A reprint has to say so on its face, or it can be passed off as a second sale.
        if (isReprint)
            receipt.Text(Labels.Reprint, TextAlignment.Center, bold: true);

        receipt.Rule();
        WriteBillIdentity(receipt, invoice);
        receipt.Rule();
    }

    /// <summary>
    /// The bill number beside the date and the customer beside the time, which is how a counter
    /// bill packs four facts into two lines.
    /// </summary>
    private void WriteBillIdentity(ReceiptBuilder receipt, SettledInvoice invoice)
    {
        var sale = invoice.Sale;
        var date = sale.CreatedAt.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
        var time = sale.CreatedAt.ToString("hh:mm tt", CultureInfo.InvariantCulture);
        var customer = sale.Customer is { } c ? c.Name ?? c.MobileNo : null;

        if (!Paired)
        {
            receipt.Columns(Labels.BillNumber, invoice.InvoiceNo);
            receipt.Columns(Labels.Date, date);
            receipt.Columns(Labels.Time, time);
            receipt.Columns(Labels.Lane, sale.LaneId);

            if (customer is not null)
                receipt.Columns(Labels.Customer, customer);

            if (sale.Customer is { Name: not null } named)
                receipt.Columns(Labels.Mobile, named.MobileNo);

            if (sale.RecalledFromToken is { } parked)
                receipt.Columns(Labels.ParkedAs, parked);

            return;
        }

        // The label columns take what the longer of the two languages needs; the values take the
        // rest, with the wider share going to the invoice number and the customer name.
        var labelWidth = Math.Clamp(Math.Max(Labels.BillNumber.Length, Labels.Customer.Length) + 1, 9, 14);
        var rightLabelWidth = Math.Clamp(Math.Max(Labels.Date.Length, Labels.Time.Length) + 1, 5, 9);
        var rightValueWidth = 11;
        var leftValueWidth = PaperWidthChars - labelWidth - rightLabelWidth - rightValueWidth;

        receipt.Cells(
            new ReceiptCell(Labels.BillNumber, labelWidth),
            new ReceiptCell(invoice.InvoiceNo, leftValueWidth),
            new ReceiptCell(Labels.Date, rightLabelWidth),
            new ReceiptCell(date, rightValueWidth, TextAlignment.Right));

        receipt.Cells(
            new ReceiptCell(Labels.Customer, labelWidth),
            new ReceiptCell(customer ?? string.Empty, leftValueWidth),
            new ReceiptCell(Labels.Time, rightLabelWidth),
            new ReceiptCell(time, rightValueWidth, TextAlignment.Right));

        // Anything else only earns a line when it is actually true of this bill.
        if (sale.Customer is { Name: not null } withMobile)
            receipt.Columns(Labels.Mobile, withMobile.MobileNo);

        if (sale.RecalledFromToken is { } token)
            receipt.Columns(Labels.ParkedAs, token);
    }

    private void WriteLines(ReceiptBuilder receipt, SaleDraft sale)
    {
        // Price, then quantity, then amount — the order they multiply out in, and the order an
        // Indian counter bill prints them.
        receipt.Row(
            Labels.ItemName,
            new ColumnValue(Labels.Rate, 9),
            new ColumnValue(Labels.Quantity, 6),
            new ColumnValue(Labels.Amount, 10));

        receipt.Rule();

        foreach (var line in sale.Lines)
        {
            receipt.Row(
                line.NameSnapshot,
                new ColumnValue(Amount(line.Mrp), 9),
                new ColumnValue(Quantity(line), 6),
                new ColumnValue(Amount(line.LineTotal), 10));

            // HSN belongs on a bill of supply as much as on a tax invoice — it identifies the
            // goods. The rate does not: printing "GST 0%" against a line would say the shop applied
            // a nil rate, where the truth is that it is not permitted to charge at all.
            var detail = sale.TaxMode == TaxMode.Composition
                ? $"  HSN {line.HsnSnapshot}"
                : $"  HSN {line.HsnSnapshot}  GST {Rate(line.GstRate)}%";

            if (line.Discount > 0m)
                detail += $"  less {Amount(line.Discount)}";

            receipt.Text(detail);
        }

        receipt.Rule();
    }

    private void WriteTotals(ReceiptBuilder receipt, SaleDraft sale)
    {
        var totals = sale.Totals;

        // There is no "taxable value" on a bill of supply — nothing was taxed. It is a subtotal.
        receipt.Columns(
            sale.TaxMode == TaxMode.Composition ? Labels.Subtotal : Labels.TaxableValue,
            Amount(totals.SubtotalTaxable));

        if (totals.TotalDiscount > 0m)
            receipt.Columns(Labels.Discount, Amount(totals.TotalDiscount));

        if (totals.TotalCgst > 0m || totals.TotalSgst > 0m)
        {
            receipt.Columns(Labels.Cgst, Amount(totals.TotalCgst));
            receipt.Columns(Labels.Sgst, Amount(totals.TotalSgst));
        }

        if (totals.TotalIgst > 0m)
            receipt.Columns(Labels.Igst, Amount(totals.TotalIgst));

        receipt.Columns($"{Labels.Items}: {totals.LineCount}", $"{Labels.TotalQuantity}: {Quantity(totals.TotalQuantity)}");
    }

    /// <summary>
    /// The rate-wise tax breakup a GST invoice is expected to carry, so the tax at each slab can be
    /// read off without recomputing it from the lines.
    /// </summary>
    private void WriteTaxSummary(ReceiptBuilder receipt, SaleDraft sale)
    {
        // A bill of supply carries no rate-wise breakup. Every line is at zero, so the loop below
        // would otherwise print a tidy "0%" slab against the full value of the bill — which reads
        // as a shop declaring it charged nothing on a taxable supply, rather than a shop that is
        // not permitted to charge at all.
        if (sale.TaxMode == TaxMode.Composition)
            return;

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
        receipt.Text(Labels.TaxSummary, bold: true);
        receipt.Row(Labels.TaxSummaryRate, new ColumnValue(Labels.TaxSummaryTaxable, 12), new ColumnValue(Labels.TaxSummaryTax, 10));

        foreach (var slab in slabs)
            receipt.Row($"{Rate(slab.Rate)}%", new ColumnValue(Amount(slab.Taxable), 12), new ColumnValue(Amount(slab.Tax), 10));
    }

    /// <summary>
    /// The four tenders a counter deals in, printed two across with the bill total beside them —
    /// every one of them, whether or not it was used.
    /// </summary>
    /// <remarks>
    /// Printing the zeros is the point. A customer settling in cash can see at a glance that nothing
    /// went on a card, and a shopkeeper reconciling a drawer at closing gets the same four figures
    /// in the same four places on every bill of the day rather than a block whose shape depends on
    /// how the customer happened to pay.
    /// </remarks>
    private void WritePayments(ReceiptBuilder receipt, SaleDraft sale)
    {
        receipt.Rule('=');

        var byType = sale.Payments
            .GroupBy(p => p.Type)
            .ToDictionary(g => g.Key, g => Money.ToPresentation(g.Sum(p => p.Amount)));

        decimal Taken(TenderType type) => byType.TryGetValue(type, out var amount) ? amount : 0m;

        var total = Amount(sale.Totals.GrandTotal);

        if (Paired)
        {
            const int labelWidth = 7;
            const int valueWidth = 9;

            // A right-aligned figure fills its cell to the last character, so without a gutter of
            // its own the next label starts against it and the line reads "600.00UPI".
            const int gutterWidth = 2;
            var totalWidth = PaperWidthChars - (2 * (labelWidth + valueWidth)) - gutterWidth;

            receipt.Cells(
                new ReceiptCell(Labels.Cash, labelWidth),
                new ReceiptCell(Amount(Taken(TenderType.Cash)), valueWidth, TextAlignment.Right),
                new ReceiptCell(string.Empty, gutterWidth),
                new ReceiptCell(Labels.Upi, labelWidth),
                new ReceiptCell(Amount(Taken(TenderType.Upi)), valueWidth, TextAlignment.Right),
                new ReceiptCell(Labels.Total, totalWidth, TextAlignment.Right));

            receipt.Cells(
                new ReceiptCell(Labels.Card, labelWidth),
                new ReceiptCell(Amount(Taken(TenderType.Card)), valueWidth, TextAlignment.Right),
                new ReceiptCell(string.Empty, gutterWidth),
                new ReceiptCell(Labels.Credit, labelWidth),
                new ReceiptCell(Amount(Taken(TenderType.StoreCredit)), valueWidth, TextAlignment.Right),
                new ReceiptCell($"{_store.CurrencyPrefix} {total}", totalWidth, TextAlignment.Right));
        }
        else
        {
            receipt.Columns(Labels.Cash, Amount(Taken(TenderType.Cash)));
            receipt.Columns(Labels.Upi, Amount(Taken(TenderType.Upi)));
            receipt.Columns(Labels.Card, Amount(Taken(TenderType.Card)));
            receipt.Columns(Labels.Credit, Amount(Taken(TenderType.StoreCredit)));
            receipt.Columns(Labels.Total, $"{_store.CurrencyPrefix} {total}", bold: true);
        }

        // Points settle a bill like any other tender, so leaving them out of the block would make
        // the tenders fail to add up to the total on exactly the bills where a customer is most
        // likely to check.
        var points = Taken(TenderType.LoyaltyPoints);

        if (points > 0m)
            receipt.Columns(Labels.LoyaltyPoints, Amount(points));

        if (sale.ChangeDue > 0m)
            receipt.Columns(Labels.Change, Amount(sale.ChangeDue), bold: true);

        receipt.Rule('=');

        // Card and UPI references are what a customer disputes a charge with, so they belong on the
        // bill. A loyalty redemption has no such reference — its own line already says how many
        // points went — so printing one would just repeat the figure above it.
        foreach (var payment in sale.Payments)
        {
            if (payment.Type is TenderType.LoyaltyPoints || string.IsNullOrWhiteSpace(payment.ReferenceNo))
                continue;

            receipt.Text($"{Label(payment.Type)} {payment.ReferenceNo}");
        }
    }

    /// <summary>
    /// What the customer saved today, and what their points come to — the two lines a shopper
    /// actually looks at, so they get the foot of the bill to themselves.
    /// </summary>
    private void WriteSavingsAndPoints(ReceiptBuilder receipt, SaleDraft sale)
    {
        var totals = sale.Totals;

        if (totals.TotalDiscount > 0m)
            receipt.Text($"{Labels.TodaysSaving} : {Amount(totals.TotalDiscount)}", TextAlignment.Center);

        if (sale.Customer is not { } customer)
            return;

        if (sale.PointsRedeemed > 0)
            receipt.Text($"{Labels.PointsRedeemed} : {sale.PointsRedeemed.ToString(CultureInfo.InvariantCulture)}", TextAlignment.Center);

        if (sale.PointsEarned > 0)
            receipt.Text($"{Labels.PointsEarnedThisBill} : {sale.PointsEarned.ToString(CultureInfo.InvariantCulture)}", TextAlignment.Center);

        receipt.Text(
            $"{Labels.TotalPointsEarned} : {customer.LoyaltyBalance.ToString(CultureInfo.InvariantCulture)}",
            TextAlignment.Center);
    }

    private void WriteFooter(ReceiptBuilder receipt, SaleDraft sale)
    {
        receipt.Blank();

        // The declaration the rules require on a bill of supply. It goes above the shop's own
        // message, and it is not the shop's to edit — it is a phrase from the rules, in English,
        // on a Tamil bill as much as an English one.
        if (sale.TaxMode == TaxMode.Composition)
        {
            // Wrapped rather than printed as one line: the declaration is 67 characters and the
            // paper is 48 at its widest, so unwrapped it loses its second half — and the half it
            // loses is "not eligible to collect tax on supplies", which is the entire point of it.
            foreach (var line in Wrap(CompositionDeclaration.Text, PaperWidthChars))
                receipt.Text(line, TextAlignment.Center);

            receipt.Blank();
        }

        if (!string.IsNullOrWhiteSpace(_store.FooterMessage))
            receipt.Text(_store.FooterMessage, TextAlignment.Center);

        receipt.Cut();
    }

    /// <summary>Breaks text on spaces so no line exceeds <paramref name="width"/> characters.</summary>
    /// <remarks>
    /// A word longer than the paper is emitted on its own over-long line rather than being chopped
    /// mid-word. Nothing here produces one, and silently cutting a word is worse than a line that
    /// wraps in the printer.
    /// </remarks>
    private static List<string> Wrap(string text, int width)
    {
        var lines = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length > 0 && current.Length + 1 + word.Length > width)
            {
                lines.Add(current.ToString());
                current.Clear();
            }

            if (current.Length > 0)
                current.Append(' ');

            current.Append(word);
        }

        if (current.Length > 0)
            lines.Add(current.ToString());

        return lines;
    }

    private static string Amount(decimal value) => value.ToString("N2", CultureInfo.InvariantCulture);

    private static string Rate(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Quantity(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Quantity(InvoiceLine line) => Quantity(line.Quantity);

    private string Label(TenderType type) => type switch
    {
        TenderType.Cash => Labels.Cash,
        TenderType.Card => Labels.Card,
        TenderType.Upi => Labels.Upi,
        TenderType.StoreCredit => Labels.Credit,
        TenderType.LoyaltyPoints => Labels.LoyaltyPoints,
        _ => type.ToString(),
    };
}
