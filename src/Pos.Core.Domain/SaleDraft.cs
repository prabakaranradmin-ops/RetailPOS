namespace Pos.Core.Domain;

/// <summary>
/// A finished sale, ready to be written down. Carries no invoice number: the number is minted by
/// the store inside the same transaction as the insert, so an abandoned save cannot burn one.
/// </summary>
/// <param name="LaneId">Which till took the sale. Becomes the prefix of the invoice number.</param>
/// <param name="CreatedAt">When it was settled.</param>
/// <param name="Customer">Null for a walk-in.</param>
/// <param name="Lines">Priced lines, in the order they were rung up.</param>
/// <param name="Totals">Aggregates, reconciling line by line with <paramref name="Lines"/>.</param>
/// <param name="Payments">How it was settled. May be several (SRS 2.4).</param>
/// <param name="ChangeDue">Cash handed back, if cash was over-tendered.</param>
/// <param name="PointsRedeemed">Loyalty points spent, settled as a tender rather than a discount.</param>
/// <param name="PointsEarned">Points accrued on the net bill after redemption (SRS section 4).</param>
/// <param name="RecalledFromToken">
/// The hold token this bill was parked under before it was settled, if it was parked at all.
/// Kept so a reprint can be traced back to the parked bill.
/// </param>
public sealed record SaleDraft(
    string LaneId,
    DateTimeOffset CreatedAt,
    Customer? Customer,
    IReadOnlyList<InvoiceLine> Lines,
    InvoiceTotals Totals,
    IReadOnlyList<Tender> Payments,
    decimal ChangeDue,
    int PointsRedeemed,
    int PointsEarned,
    string? RecalledFromToken);

/// <summary>A sale as it now exists in the database, with the number it was given.</summary>
public sealed record SettledInvoice(long Id, string InvoiceNo, SaleDraft Sale)
{
    public decimal GrandTotal => Sale.Totals.GrandTotal;
}

/// <summary>A parked bill, restored in full.</summary>
public sealed record HeldBill(
    long Id,
    string Token,
    DateTimeOffset HeldAt,
    Customer? Customer,
    IReadOnlyList<InvoiceLine> Lines);

/// <summary>
/// One row of the recall list. SRS 2.5 asks for token, timestamp, item count and customer, which
/// is all this carries — the lines themselves are only read when a bill is actually recalled.
/// </summary>
public sealed record HeldBillSummary(
    long Id,
    string Token,
    DateTimeOffset HeldAt,
    int ItemCount,
    string CustomerLabel,
    decimal GrandTotal);
