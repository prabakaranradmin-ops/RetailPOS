using Pos.Core.Tax;

namespace Pos.Core.Domain;

/// <summary>One line of a sold bill, as far as returning it goes.</summary>
/// <param name="LineNo">Its position on the bill, from 1.</param>
/// <param name="Charged">
/// The figures stored with the sale: what was actually charged, which a return takes back from,
/// rather than what today's engine would work the line out to.
/// </param>
/// <param name="AlreadyReturned">What earlier credit notes have taken back.</param>
/// <param name="Credited">What those credit notes came to, so the last return takes exactly the rest.</param>
public sealed record ReturnableLine(int LineNo, InvoiceLine Line, LineFigures Charged, decimal AlreadyReturned, LineFigures Credited)
{
    public decimal Sold => Line.Quantity;

    public decimal Remaining => Sold - AlreadyReturned;

    public bool FullyReturned => Remaining <= 0m;
}

/// <summary>A line's money, to the paisa: its taxable value, its tax and its total.</summary>
public readonly record struct LineFigures(decimal TaxableValue, decimal Cgst, decimal Sgst, decimal Igst, decimal LineTotal)
{
    public static LineFigures None => default;

    public static LineFigures Of(InvoiceLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var tax = line.Tax;
        return new LineFigures(tax.TaxableValueForDisplay, tax.Cgst, tax.Sgst, tax.Igst, tax.LineTotal);
    }

    public static LineFigures operator +(LineFigures a, LineFigures b) =>
        new(a.TaxableValue + b.TaxableValue, a.Cgst + b.Cgst, a.Sgst + b.Sgst, a.Igst + b.Igst, a.LineTotal + b.LineTotal);
}

/// <summary>A sold bill, with how much of each line can still come back.</summary>
/// <param name="RefundedSoFar">What earlier credit notes against it refunded, round-off included.</param>
/// <param name="PointsReversedSoFar">The points those credit notes took back.</param>
public sealed record ReturnableBill(
    SettledInvoice Invoice,
    IReadOnlyList<ReturnableLine> Lines,
    decimal RefundedSoFar = 0m,
    int PointsReversedSoFar = 0)
{
    public bool AnythingLeft => Lines.Any(l => !l.FullyReturned);
}

/// <summary>One line of a credit note: goods coming back, and the tax on them reversed.</summary>
/// <param name="Restocked">Whether it goes back on the shelf. A damaged return does not.</param>
public sealed record CreditNoteLine(
    int InvoiceLineNo,
    long ItemId,
    string Name,
    string Hsn,
    UnitType Unit,
    decimal Quantity,
    decimal GstRate,
    bool InterState,
    decimal TaxableValue,
    decimal Cgst,
    decimal Sgst,
    decimal Igst,
    decimal LineTotal,
    bool Restocked)
{
    public decimal Tax => Cgst + Sgst + Igst;

    /// <summary>
    /// Prices a return of <paramref name="quantity"/> from a sold line, the way the sale was priced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A part return goes through the same tax engine as the sale, with the line's discount shared
    /// out in proportion: two of five packets returned take two fifths of the discount with them.
    /// </para>
    /// <para>
    /// <b>The last return takes exactly what is left.</b> Pricing each part return afresh rounds each
    /// one to the paisa on its own, and three returns of a third could together credit a paisa more
    /// than the line was sold for. So the return that brings a line back to nothing is not priced at
    /// all: it is the line as sold, less everything already credited. A whole line returned in one go
    /// is the same case, and credits the sale to the paisa.
    /// </para>
    /// </remarks>
    public static CreditNoteLine Price(ReturnableLine returnable, decimal quantity, bool restock)
    {
        ArgumentNullException.ThrowIfNull(returnable);

        var line = returnable.Line;

        if (quantity <= 0m)
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "Nothing to return.");

        if (quantity > returnable.Remaining)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity,
                $"Only {returnable.Remaining:0.###} of {line.NameSnapshot} can come back: {returnable.Sold:0.###} sold, {returnable.AlreadyReturned:0.###} already returned.");
        }

        if (!line.Unit.AllowsFractionalQuantity() && decimal.Truncate(quantity) != quantity)
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, $"{line.NameSnapshot} was sold whole, and comes back whole.");

        decimal taxable, cgst, sgst, igst, total;

        if (quantity == returnable.Remaining)
        {
            var sold = returnable.Charged;
            var credited = returnable.Credited;

            taxable = sold.TaxableValue - credited.TaxableValue;
            cgst = sold.Cgst - credited.Cgst;
            sgst = sold.Sgst - credited.Sgst;
            igst = sold.Igst - credited.Igst;
            total = sold.LineTotal - credited.LineTotal;
        }
        else
        {
            var discountShare = decimal.Round(line.Discount * quantity / line.Quantity, 2, MidpointRounding.ToEven);

            var priced = TaxEngine.Calculate(new TaxLineInput(
                quantity,
                line.UnitPrice,
                discountShare,
                line.GstRate,
                line.IsInterState,
                line.IsTaxInclusive));

            taxable = priced.TaxableValueForDisplay;
            cgst = priced.Cgst;
            sgst = priced.Sgst;
            igst = priced.Igst;
            total = priced.LineTotal;
        }

        return new CreditNoteLine(
            returnable.LineNo,
            line.ItemId,
            line.NameSnapshot,
            line.HsnSnapshot,
            line.Unit,
            quantity,
            line.GstRate,
            line.IsInterState,
            taxable,
            cgst,
            sgst,
            igst,
            total,
            restock);
    }
}

/// <summary>A return, worked out and ready to issue.</summary>
/// <param name="Refund">Cash, UPI, card, or <see cref="TenderType.StoreCredit"/> to take it off the customer's khata.</param>
/// <param name="RoundOff">
/// Worked to the rupee on a lane that rounds, as the sale was: a counter refunds in coins it has.
/// </param>
public sealed record CreditNoteDraft(
    ReturnableBill Bill,
    IReadOnlyList<CreditNoteLine> Lines,
    TenderType Refund,
    string Reason,
    decimal RoundOff,
    int PointsReversed)
{
    public decimal TaxableValue => Lines.Sum(l => l.TaxableValue);
    public decimal Cgst => Lines.Sum(l => l.Cgst);
    public decimal Sgst => Lines.Sum(l => l.Sgst);
    public decimal Igst => Lines.Sum(l => l.Igst);
    public decimal LinesTotal => Lines.Sum(l => l.LineTotal);

    /// <summary>What goes back to the customer.</summary>
    public decimal Refunded => LinesTotal + RoundOff;

    /// <summary>
    /// Works out a return from what the cashier picked.
    /// </summary>
    /// <param name="roundToRupee">Whether the lane settles to the rupee, as it did the sale.</param>
    public static CreditNoteDraft Build(
        ReturnableBill bill,
        IReadOnlyList<(int LineNo, decimal Quantity, bool Restock)> picked,
        TenderType refund,
        string? reason,
        bool roundToRupee)
    {
        ArgumentNullException.ThrowIfNull(bill);
        ArgumentNullException.ThrowIfNull(picked);

        if (picked.Count == 0 || picked.All(p => p.Quantity <= 0m))
            throw new ArgumentException("Nothing has been picked to come back.", nameof(picked));

        if (refund is TenderType.LoyaltyPoints)
            throw new ArgumentException("A return is refunded in money, or off the khata - not in points.", nameof(refund));

        if (refund is TenderType.StoreCredit && bill.Invoice.Sale.Customer is null)
            throw new ArgumentException("Only a bill with a customer on it can be refunded to a khata.", nameof(refund));

        var lines = picked
            .Where(p => p.Quantity > 0m)
            .Select(p =>
            {
                var returnable = bill.Lines.SingleOrDefault(l => l.LineNo == p.LineNo)
                    ?? throw new ArgumentException($"The bill has no line {p.LineNo}.", nameof(picked));

                return CreditNoteLine.Price(returnable, p.Quantity, p.Restock);
            })
            .ToList();

        var total = lines.Sum(l => l.LineTotal);
        var roundOff = roundToRupee ? Money.Round(total, 0) - total : 0m;

        var points = PointsToReverse(bill, lines, total + roundOff);

        return new CreditNoteDraft(bill, lines, refund, string.IsNullOrWhiteSpace(reason) ? "Goods returned" : reason.Trim(), roundOff, points);
    }

    /// <summary>
    /// The points earned on the sale that come back off with these goods.
    /// </summary>
    /// <remarks>
    /// In proportion to the money, and worked on everything returned so far rather than on this
    /// return alone, less what earlier returns already took: three returns of a third of a bill that
    /// earned seven points take 2, 2 and 3, not 2, 2 and 2. The return that brings the whole bill back
    /// takes whatever is left, so a customer who returns everything keeps none of it.
    /// </remarks>
    private static int PointsToReverse(ReturnableBill bill, IReadOnlyList<CreditNoteLine> lines, decimal refund)
    {
        var sale = bill.Invoice.Sale;
        var earned = sale.PointsEarned;
        var saleTotal = sale.Totals.AmountPayable;

        if (sale.Customer is null || earned <= 0 || saleTotal <= 0m)
            return 0;

        var everythingBack = bill.Lines.All(l =>
            l.Remaining == lines.Where(c => c.InvoiceLineNo == l.LineNo).Sum(c => c.Quantity));

        // Multiplied before it is divided: 6 × 100 / 300 is 2, where 6 × (100 / 300) is 1.99… and
        // floors to 1.
        var target = everythingBack
            ? earned
            : (int)Math.Min(earned, decimal.Floor(earned * (bill.RefundedSoFar + refund) / saleTotal));

        return Math.Max(0, target - bill.PointsReversedSoFar);
    }
}

/// <summary>A credit note as issued.</summary>
public sealed record CreditNote(
    long Id,
    string Number,
    DateTimeOffset CreatedAt,
    string LaneId,
    string InvoiceNo,
    DateTimeOffset InvoiceDate,
    Customer? Customer,
    TaxMode TaxMode,
    string Reason,
    TenderType Refund,
    IReadOnlyList<CreditNoteLine> Lines,
    decimal RoundOff,
    int PointsReversed,
    string? CashierName)
{
    public decimal TaxableValue => Lines.Sum(l => l.TaxableValue);
    public decimal Cgst => Lines.Sum(l => l.Cgst);
    public decimal Sgst => Lines.Sum(l => l.Sgst);
    public decimal Igst => Lines.Sum(l => l.Igst);
    public decimal LinesTotal => Lines.Sum(l => l.LineTotal);
    public decimal Refunded => LinesTotal + RoundOff;
}

/// <summary>
/// Where returns are recorded: the credit notes, and what each did to the shelf, the khata and the
/// points.
/// </summary>
public interface ICreditNoteStore
{
    /// <summary>A bill and what can still come back from it, or null for no such bill.</summary>
    ReturnableBill? Returnable(string invoiceNo);

    /// <summary>
    /// Issues a credit note in one transaction: its number, its lines, the shelf count of what goes
    /// back on the shelf, the khata if refunded there, and the customer's points.
    /// </summary>
    /// <remarks>
    /// Checks the quantities again inside the transaction, so two lanes returning the same goods at
    /// once cannot together return more than was sold.
    /// </remarks>
    CreditNote Issue(CreditNoteDraft draft, string laneId, DateTimeOffset at, string? cashierName);

    CreditNote? Find(string creditNoteNo);
}
