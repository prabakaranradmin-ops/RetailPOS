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

    /// <summary>Narrowest the quantity column goes, so "1 Pcs" never has to be cut.</summary>
    private const int MinQuantityWidth = 5;

    /// <summary>
    /// Widest it goes. "12.5 Marakkaal" is as long as a real quantity gets; a bill carrying one
    /// stacks its long item names above the figures, which is better than cutting the unit.
    /// </summary>
    private const int MaxQuantityWidth = 16;

    public ReceiptComposer(
        StoreProfile store,
        int paperWidthChars = ReceiptBuilder.Width80Mm,
        ReceiptLanguage language = ReceiptLanguage.English,
        ReceiptLayout layout = ReceiptLayout.Standard)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        PaperWidthChars = paperWidthChars;
        Language = language;
        Labels = ReceiptLabels.For(language);
        Layout = layout;
    }

    public int PaperWidthChars { get; }

    public ReceiptLanguage Language { get; }

    public ReceiptLabels Labels { get; }

    /// <summary>
    /// Which bill the next sale gets. Settable so the owner's choice reaches the till at once,
    /// rather than at the next restart with the setting and the paper disagreeing until then.
    /// </summary>
    public ReceiptLayout Layout { get; set; }

    private bool Paired => PaperWidthChars >= MinPairedLayoutWidth;

    public ReceiptBuilder Compose(SettledInvoice invoice, bool isReprint = false)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        if (Layout == ReceiptLayout.Compact)
            return ComposeCompact(invoice, isReprint);

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

    /// <summary>
    /// The slip for a customer who pays back what they owed on credit.
    /// </summary>
    /// <remarks>
    /// Headed as a payment, never as a tax invoice: nothing was sold and nothing was taxed - the
    /// goods and the tax were on the bill they bought on credit. It is what the customer keeps to
    /// show they paid, which is the only thing that settles a disagreement about a khata.
    /// </remarks>
    public ReceiptBuilder ComposeCollection(Customer customer, CreditPayment payment, decimal stillOwed)
    {
        ArgumentNullException.ThrowIfNull(customer);
        ArgumentNullException.ThrowIfNull(payment);

        var receipt = new ReceiptBuilder(PaperWidthChars);

        WriteShop(receipt);
        receipt.Text(Labels.PaymentReceived, TextAlignment.Center, bold: true);
        receipt.Rule();

        var at = payment.ReceivedAt.LocalDateTime;
        receipt.Columns(Labels.Date, at.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture));
        receipt.Columns(Labels.Time, at.ToString("hh:mm tt", CultureInfo.InvariantCulture));
        receipt.Columns(Labels.Customer, customer.Name ?? customer.MobileNo);

        if (customer.Name is not null)
            receipt.Columns(Labels.Mobile, customer.MobileNo);

        receipt.Rule();
        receipt.Columns($"{Labels.AmountPaid} ({Label(payment.Tender)})", Amount(payment.Amount), bold: true);
        receipt.Columns(Labels.StillOwed, Amount(stillOwed));
        receipt.Rule();
        receipt.Text(Labels.PaymentSlipNote, TextAlignment.Center);
        receipt.Cut();

        return receipt;
    }

    /// <summary>
    /// A slip with a UPI code for the exact amount, for a counter with no screen facing the customer.
    /// </summary>
    /// <remarks>
    /// Not a bill, and it says so: nothing is sold by it and it carries no tax. The bill prints once
    /// the payment is taken, as it always does. The amount is printed large above the code, because
    /// it is what the customer checks against their app before approving.
    /// </remarks>
    public ReceiptBuilder ComposeUpiSlip(UpiPayee payee, decimal amount, string link)
    {
        ArgumentNullException.ThrowIfNull(payee);
        ArgumentException.ThrowIfNullOrWhiteSpace(link);

        var receipt = new ReceiptBuilder(PaperWidthChars);

        WriteShop(receipt);
        receipt.Text(Labels.ScanToPay, TextAlignment.Center, bold: true);
        receipt.Text($"Rs {Amount(amount)}", TextAlignment.Center, bold: true, widthMultiplier: 2, heightMultiplier: 2);
        receipt.Qr(link);
        receipt.Text(payee.Name, TextAlignment.Center);
        receipt.Text(payee.Id, TextAlignment.Center);
        receipt.Rule();
        receipt.Text(Labels.UpiSlipNote, TextAlignment.Center);
        receipt.Cut();

        return receipt;
    }

    /// <summary>
    /// A customer's khata statement: what they owed at the start, each bill, payment and return with
    /// the balance after it, what they owe now and how old it is.
    /// </summary>
    /// <remarks>
    /// Not a bill, and it says so. With the shop's UPI ID it ends with a code for everything owed, so
    /// the customer can settle it on the spot or later from the paper. The dates are the day and
    /// month only beside each line, with the full period at the head, to keep the lines on one row.
    /// </remarks>
    public ReceiptBuilder ComposeKhataStatement(KhataStatement statement, UpiPayee? upi = null)
    {
        ArgumentNullException.ThrowIfNull(statement);

        var receipt = new ReceiptBuilder(PaperWidthChars);
        var customer = statement.Customer;

        WriteShop(receipt);
        receipt.Text(Labels.KhataStatement, TextAlignment.Center, bold: true);
        receipt.Rule();
        receipt.Columns(Labels.Customer, customer.Name ?? customer.MobileNo);

        if (customer.Name is not null)
            receipt.Columns(Labels.Mobile, customer.MobileNo);

        receipt.Columns(Labels.Period, $"{Day(statement.From)} - {Day(statement.To)}");
        receipt.Rule();
        receipt.Columns(Labels.OpeningBalance, Amount(statement.Opening));

        foreach (var line in statement.Lines)
        {
            var entry = line.Entry;
            var what = entry.Kind switch
            {
                KhataEntryKind.Bought => $"{Labels.EntryBill} {entry.Reference}",
                KhataEntryKind.Returned => $"{Labels.EntryReturned} {entry.Reference}",
                _ => entry.Tender is { } tender ? $"{Labels.EntryPaid}, {Label(tender)}" : Labels.EntryPaid,
            };

            receipt.Row(
                $"{entry.At.ToString("dd-MM", CultureInfo.InvariantCulture)} {what}",
                new ColumnValue(Signed(entry.Change), 10),
                new ColumnValue(Amount(line.BalanceAfter), 11));
        }

        receipt.Rule();

        if (statement.Bills > 0)
            receipt.Columns($"{Labels.BoughtOnCredit} ({statement.Bills})", Amount(statement.Bought));

        if (statement.Paid > 0m)
            receipt.Columns(Labels.PaidBack, Amount(statement.Paid));

        if (statement.Returned > 0m)
            receipt.Columns(Labels.ReturnedGoods, Amount(statement.Returned));

        receipt.Columns(Labels.OwedNow, Amount(Math.Max(0m, statement.Closing)), bold: true);

        var ageing = statement.Ageing;

        if (ageing.OldestUnpaid is { } oldest)
        {
            receipt.Columns(Labels.OldestUnpaid, $"{Day(oldest)} ({ageing.DaysWaiting(statement.To)} {Labels.Days})");

            foreach (var (label, amount) in new[]
                     {
                         (Labels.AgeUpTo30, ageing.UpTo30Days),
                         (Labels.Age31To60, ageing.Days31To60),
                         (Labels.Age61To90, ageing.Days61To90),
                         (Labels.AgeOver90, ageing.Over90Days),
                     })
            {
                if (amount > 0m)
                    receipt.Columns(label, Amount(amount));
            }
        }

        receipt.Rule();

        if (statement.OwesAnything && upi is not null)
        {
            receipt.Text(Labels.ScanToPay, TextAlignment.Center, bold: true);
            receipt.Text($"Rs {Amount(statement.Closing)}", TextAlignment.Center, bold: true, widthMultiplier: 2, heightMultiplier: 2);
            receipt.Qr(UpiLink.For(upi, statement.Closing, note: $"Khata {customer.MobileNo}"));
            receipt.Text(upi.Id, TextAlignment.Center);
            receipt.Rule();
        }

        receipt.Text(statement.OwesAnything ? Labels.StatementNote : Labels.NothingOwed, TextAlignment.Center);
        receipt.Cut();

        return receipt;

        static string Day(DateOnly day) => day.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);

        static string Signed(decimal change) => (change > 0m ? "+" : "-") + Amount(Math.Abs(change));
    }

    /// <summary>
    /// The credit note for goods a customer brought back.
    /// </summary>
    /// <remarks>
    /// Its own document with its own number, naming the bill the goods were sold on and the date of
    /// it, as a credit note has to. The tax is printed as tax taken back, at the same split the sale
    /// charged, so it can be set against the bill line for line. One layout for both bill styles:
    /// a return is rare enough that a shop reads every word of it.
    /// </remarks>
    public ReceiptBuilder ComposeCreditNote(CreditNote note, bool isReprint = false)
    {
        ArgumentNullException.ThrowIfNull(note);

        var receipt = new ReceiptBuilder(PaperWidthChars);
        var composition = note.TaxMode == TaxMode.Composition;

        WriteShop(receipt);
        receipt.Text(Labels.CreditNote, TextAlignment.Center, bold: true);

        if (isReprint)
            receipt.Text(Labels.Reprint, TextAlignment.Center, bold: true);

        receipt.Rule();

        var at = note.CreatedAt;
        Pair(receipt, $"{Labels.CreditNote}: {note.Number}", at.ToString("dd-MM-yyyy hh:mm tt", CultureInfo.InvariantCulture));
        Pair(receipt, $"{Labels.AgainstBill}: {note.InvoiceNo}", note.InvoiceDate.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture));

        if (note.Customer is { } customer)
            Pair(receipt, $"{Labels.Customer}: {customer.Name ?? customer.MobileNo}", customer.Name is null ? string.Empty : customer.MobileNo);

        receipt.Rule();

        var quantityWidth = Math.Clamp(
            note.Lines.Select(l => Units.WithQuantity(l.Quantity, l.Unit, Language).Length).Append(Labels.Quantity.Length).Max(),
            MinQuantityWidth,
            MaxQuantityWidth);

        receipt.Row(Labels.ItemShort, new ColumnValue(Labels.Quantity, quantityWidth), new ColumnValue(Labels.Amount, 10));
        receipt.Rule();

        foreach (var line in note.Lines)
        {
            receipt.Row(
                line.Name,
                new ColumnValue(Units.WithQuantity(line.Quantity, line.Unit, Language), quantityWidth),
                new ColumnValue(Amount(line.LineTotal), 10));

            var detail = composition
                ? $"  HSN {line.Hsn}"
                : $"  HSN {line.Hsn}  GST {Rate(line.GstRate)}%";

            if (!line.Restocked)
                detail += $"  ({Labels.Damaged})";

            receipt.Text(detail);
        }

        receipt.Rule();
        receipt.Columns(composition ? Labels.Subtotal : Labels.TaxableValue, Amount(note.TaxableValue));

        if (!composition)
        {
            if (note.Cgst != 0m || note.Sgst != 0m)
            {
                receipt.Columns(Labels.Cgst, Amount(note.Cgst));
                receipt.Columns(Labels.Sgst, Amount(note.Sgst));
            }

            if (note.Igst != 0m)
                receipt.Columns(Labels.Igst, Amount(note.Igst));

            receipt.Columns(Labels.TaxReversed, Amount(note.Cgst + note.Sgst + note.Igst));
        }

        if (note.RoundOff != 0m)
            receipt.Columns(Labels.RoundOff, Amount(note.RoundOff));

        receipt.Rule('=');
        var how = note.Refund is TenderType.StoreCredit ? Labels.OffTheKhata : Label(note.Refund);
        receipt.Text($"{Labels.Refund} ({how}) : {_store.CurrencyPrefix} {Amount(note.Refunded)}", TextAlignment.Right, bold: true, heightMultiplier: 2);
        receipt.Rule('=');

        receipt.Columns(Labels.Reason, note.Reason);

        if (note.PointsReversed > 0)
            receipt.Columns(Labels.RewardPoints, $"-{note.PointsReversed.ToString(CultureInfo.InvariantCulture)}");

        receipt.Blank();
        var stamp = at.ToString("dd-MM-yyyy hh:mm tt", CultureInfo.InvariantCulture);
        receipt.Text(note.CashierName is { Length: > 0 } cashier ? $"{cashier}/{note.LaneId}/{stamp}" : $"{note.LaneId}/{stamp}");
        receipt.Cut();

        return receipt;
    }

    /// <summary>Who the shop is: name, address, numbers, licences. Shared by every document it prints.</summary>
    private void WriteShop(ReceiptBuilder receipt)
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
    }

    private void WriteHeader(ReceiptBuilder receipt, SettledInvoice invoice, bool isReprint)
    {
        WriteShop(receipt);

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
        WriteBuyer(receipt, invoice.Sale);
        receipt.Rule();
    }

    /// <summary>
    /// A bill to a business: who it was to, their GSTIN, where they are, and the state the goods
    /// were supplied into - which a tax invoice to a registered buyer has to say.
    /// </summary>
    private void WriteBuyer(ReceiptBuilder receipt, SaleDraft sale)
    {
        if (sale.Buyer is not { } buyer)
            return;

        receipt.Rule();
        receipt.Text($"{Labels.BillTo}: {buyer.Name}", bold: true);
        receipt.Columns(Labels.BuyerGstin, buyer.Gstin, bold: true);

        if (!string.IsNullOrWhiteSpace(buyer.Address))
            receipt.Text(buyer.Address);

        receipt.Columns(Labels.PlaceOfSupply, GstStates.Label(buyer.StateCode));
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

            if (sale.RecalledFromToken is { } held)
                receipt.Columns(Labels.HeldAs, held);

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
            receipt.Columns(Labels.HeldAs, token);
    }

    private void WriteLines(ReceiptBuilder receipt, SaleDraft sale)
    {
        // Price, then quantity, then amount — the order they multiply out in, and the order an
        // Indian counter bill prints them.
        var quantityWidth = QuantityWidth(sale);

        receipt.Row(
            Labels.ItemName,
            new ColumnValue(Labels.Rate, 9),
            new ColumnValue(Labels.Quantity, quantityWidth),
            new ColumnValue(Labels.Amount, 10));

        receipt.Rule();

        foreach (var line in sale.Lines)
        {
            receipt.Row(
                line.NameSnapshot,
                new ColumnValue(Amount(line.Mrp), 9),
                new ColumnValue(Quantity(line), quantityWidth),
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

            // Which offer gave the discount: the customer sees why the third soap cost nothing.
            if (line.OfferName is { } offer)
                receipt.Text($"  {Labels.Offer}: {offer}");
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

        // Only when there is one, and never as "0.00". A line saying nothing happened is a line the
        // customer has to read to discover that. It sits below the tax and above the total because
        // that is the order the arithmetic runs in: the lines make the total, the round-off nudges
        // it, and what is left is payable.
        if (totals.RoundOff != 0m)
            receipt.Columns(Labels.RoundOff, Amount(totals.RoundOff));

        // A total quantity only means something when every line is counted in the same thing.
        // Three pieces, two and three-quarter kilos and a comb of bananas do not add up to 6.75.
        var units = sale.Lines.Select(line => line.Unit).Distinct().ToList();

        if (units.Count == 1)
            receipt.Columns($"{Labels.Items}: {totals.LineCount}", $"{Labels.TotalQuantity}: {Units.WithQuantity(totals.TotalQuantity, units[0], Language)}");
        else
            receipt.Text($"{Labels.Items}: {totals.LineCount}");

        // What the customer pays, where the arithmetic above arrives at it, big enough to read across
        // the counter. It used to be only in the tender block below, the same size as "Card 0.00";
        // the compact bill already did this. Double height only, as there: double width would push a
        // five-figure total off the paper.
        receipt.Rule('=');
        receipt.Text($"{Labels.Total}  {_store.CurrencyPrefix} {Amount(totals.AmountPayable)}", TextAlignment.Right, bold: true, heightMultiplier: 2);
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

        // What was handed over, which on a rounding lane is not the grand total. The tender lines
        // beside it are actual money, so the figure they are checked against has to be too.
        var total = Amount(sale.Totals.AmountPayable);

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

    /// <summary>
    /// The quantity with what it is counted in — "2 சீப்பு", "2.75 Kg" — because a bare 2 does
    /// not say whether the customer took two combs of bananas or two kilos.
    /// </summary>
    private string Quantity(InvoiceLine line) => Units.WithQuantity(line.Quantity, line.Unit, Language);

    /// <summary>As wide as this bill's longest quantity needs, within limits.</summary>
    private int QuantityWidth(SaleDraft sale) =>
        Math.Clamp(
            sale.Lines.Select(line => Quantity(line).Length).Append(Labels.Quantity.Length).Max(),
            MinQuantityWidth,
            MaxQuantityWidth);

    // ---- The compact bill --------------------------------------------------------------------

    /// <summary>
    /// The shorter counter bill: item, quantity with its unit, and amount; the HSN and rate under
    /// each line; one large total; only the tenders that were used.
    /// </summary>
    /// <remarks>
    /// Shorter, not lighter. Everything a tax invoice has to carry is still here — the GSTIN, the
    /// number and date, the HSN and rate of every line, and the tax at each slab — and a bill of
    /// supply still carries its declaration. What goes is what a customer does not read: the four
    /// tender boxes printed as zeros, and the taxable subtotal above the tax block that already
    /// breaks it down.
    /// </remarks>
    private ReceiptBuilder ComposeCompact(SettledInvoice invoice, bool isReprint)
    {
        var sale = invoice.Sale;
        var receipt = new ReceiptBuilder(PaperWidthChars);

        WriteShop(receipt);

        receipt.Text(
            sale.TaxMode == TaxMode.Composition ? Labels.BillOfSupply : Labels.TaxInvoice,
            TextAlignment.Center,
            bold: true);

        if (isReprint)
            receipt.Text(Labels.Reprint, TextAlignment.Center, bold: true);

        receipt.Rule();
        WriteCompactIdentity(receipt, invoice);
        receipt.Rule();
        WriteCompactLines(receipt, sale);
        WriteCompactTotals(receipt, sale);
        WriteTaxSummary(receipt, sale);
        WriteCompactPayments(receipt, sale);
        WriteSavingsAndPoints(receipt, sale);

        // Who billed it, on which till, and when: the line a shop reads back when a customer
        // returns with a query about "the bill from Tuesday evening".
        receipt.Blank();
        var stamp = sale.CreatedAt.ToString("dd-MM-yyyy hh:mm tt", CultureInfo.InvariantCulture);
        receipt.Text(sale.CashierName is { Length: > 0 } cashier
            ? $"{cashier}/{sale.LaneId}/{stamp}"
            : $"{sale.LaneId}/{stamp}");

        WriteFooter(receipt, sale);
        return receipt;
    }

    private void WriteCompactIdentity(ReceiptBuilder receipt, SettledInvoice invoice)
    {
        var sale = invoice.Sale;
        var when = sale.CreatedAt.ToString(Paired ? "dd-MM-yyyy hh:mm tt" : "dd-MM-yy HH:mm", CultureInfo.InvariantCulture);

        Pair(receipt, $"{Labels.BillNumber}: {invoice.InvoiceNo}", when);

        // A walk-in customer is still a customer on the bill: "CASH" says nobody was named, which
        // a blank line would leave the reader to guess.
        var customer = sale.Customer is { } c ? c.Name ?? c.MobileNo : Labels.CashCustomer;
        var mobile = sale.Customer is { Name: not null } named ? named.MobileNo : string.Empty;

        Pair(receipt, $"{Labels.Customer}: {customer}", mobile);

        if (sale.RecalledFromToken is { } token)
            receipt.Columns(Labels.HeldAs, token);

        WriteBuyer(receipt, sale);
    }

    /// <summary>
    /// Two facts on one line when they fit, and on two when they do not.
    /// </summary>
    /// <remarks>
    /// <see cref="ReceiptBuilder.Columns"/> keeps the right-hand figure by cutting the left, which
    /// is right for a label and wrong here: the left is the bill number or the customer's name, and
    /// a bill number cut to "INV/26-2" is a different bill.
    /// </remarks>
    private void Pair(ReceiptBuilder receipt, string left, string right)
    {
        if (right.Length == 0)
        {
            receipt.Text(left);
            return;
        }

        if (left.Length + 1 + right.Length <= PaperWidthChars)
        {
            receipt.Columns(left, right);
            return;
        }

        receipt.Text(left);
        receipt.Text(right, TextAlignment.Right);
    }

    private void WriteCompactLines(ReceiptBuilder receipt, SaleDraft sale)
    {
        var quantityWidth = QuantityWidth(sale);

        receipt.Row(
            Labels.ItemShort,
            new ColumnValue(Labels.Quantity, quantityWidth),
            new ColumnValue(Labels.Amount, 10));

        receipt.Rule();

        foreach (var line in sale.Lines)
        {
            receipt.Row(
                line.NameSnapshot,
                new ColumnValue(Quantity(line), quantityWidth),
                new ColumnValue(Amount(line.LineTotal), 10));

            // The rate is here rather than in a column of its own: it is what a customer checks
            // a loose line against — two muzham at thirty — and it costs no width down here.
            var detail = sale.TaxMode == TaxMode.Composition
                ? $"  (HSN:{line.HsnSnapshot})"
                : $"  (HSN:{line.HsnSnapshot}) GST:{Rate(line.GstRate)}%";

            detail += $"  @{Amount(line.Mrp)}";

            if (line.Discount > 0m)
                detail += $"  less {Amount(line.Discount)}";

            receipt.Text(detail);

            // Which offer gave the discount: the customer sees why the third soap cost nothing.
            if (line.OfferName is { } offer)
                receipt.Text($"  {Labels.Offer}: {offer}");
        }

        receipt.Rule();
    }

    private void WriteCompactTotals(ReceiptBuilder receipt, SaleDraft sale)
    {
        var totals = sale.Totals;

        // The total is the sum of the amounts printed above it, which are already after discount —
        // each discounted line says "less" beside it, and the saving is repeated at the foot. A
        // discount line here would read as coming off a second time.
        receipt.Columns($"{Labels.Items}: {totals.LineCount}", $"{Labels.Total}: {Amount(totals.GrandTotal)}");

        if (totals.RoundOff != 0m)
            receipt.Columns(string.Empty, $"{Labels.RoundOff}: {(totals.RoundOff > 0m ? "+" : string.Empty)}{Amount(totals.RoundOff)}");

        // The figure the customer hands over, big enough to read across the counter. Double height
        // only: double width would halve the line and push a five-figure total off the paper.
        receipt.Rule('=');
        receipt.Text(
            $"{Labels.TotalAmount} : {_store.CurrencyPrefix} {Amount(totals.AmountPayable)}",
            TextAlignment.Right,
            bold: true,
            heightMultiplier: 2);
        receipt.Rule('=');
    }

    /// <summary>Only the tenders that were used — on this bill a zero is noise, not a figure.</summary>
    private void WriteCompactPayments(ReceiptBuilder receipt, SaleDraft sale)
    {
        var byType = sale.Payments
            .GroupBy(p => p.Type)
            .OrderBy(g => g.Key)
            .Select(g => (Type: g.Key, Amount: Money.ToPresentation(g.Sum(p => p.Amount))))
            .Where(t => t.Amount != 0m)
            .ToList();

        if (byType.Count == 0 && sale.ChangeDue <= 0m)
            return;

        receipt.Blank();

        foreach (var (type, amount) in byType)
            receipt.Columns(Label(type), Amount(amount));

        if (sale.ChangeDue > 0m)
            receipt.Columns(Labels.Change, Amount(sale.ChangeDue), bold: true);

        foreach (var payment in sale.Payments)
        {
            if (payment.Type is TenderType.LoyaltyPoints || string.IsNullOrWhiteSpace(payment.ReferenceNo))
                continue;

            receipt.Text($"{Label(payment.Type)} {payment.ReferenceNo}");
        }
    }

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
