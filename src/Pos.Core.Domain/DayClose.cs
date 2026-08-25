namespace Pos.Core.Domain;

/// <param name="Type">How it was paid.</param>
/// <param name="Amount">Total taken under this tender.</param>
/// <param name="PaymentCount">How many payments made it up.</param>
public readonly record struct TenderTotal(TenderType Type, decimal Amount, int PaymentCount);

/// <param name="GstRate">The slab.</param>
/// <param name="TaxableValue">Value taxed at this rate.</param>
public readonly record struct TaxSlabTotal(decimal GstRate, decimal TaxableValue, decimal Cgst, decimal Sgst, decimal Igst)
{
    public decimal Tax => Cgst + Sgst + Igst;
}

/// <summary>
/// A lane's Z-report: everything it took between one close and the next.
/// </summary>
/// <remarks>
/// The figures are defined so they reconcile in both directions, which is what makes the report
/// checkable rather than merely informative:
/// <list type="bullet">
/// <item><see cref="NetSales"/> = <see cref="GrossSales"/> − <see cref="TotalDiscount"/></item>
/// <item><see cref="NetSales"/> = <see cref="TaxableValue"/> + <see cref="TotalTax"/></item>
/// <item><see cref="NetSales"/> = the sum of every tender taken, less <see cref="ChangeGiven"/></item>
/// </list>
/// </remarks>
/// <param name="Id">Row id, zero for a report that has not been saved.</param>
/// <param name="LaneId">Which till.</param>
/// <param name="ClosedAt">When the close was run.</param>
/// <param name="OpenedAt">When the first sale in the batch was rung up. Null for a day with none.</param>
/// <param name="GrossSales">What the goods came to at full price, before discounts.</param>
/// <param name="NetSales">What was actually billed, tax inclusive. The day's takings.</param>
/// <param name="CashExpected">
/// What should be in the drawer: cash taken less change given. The one figure on the report the
/// cashier can check by counting.
/// </param>
/// <param name="HeldBillsOutstanding">
/// Bills still parked at close. Not sales, but somebody has to deal with them before the lane is
/// left for the night.
/// </param>
public sealed record DayCloseSummary(
    long Id,
    string LaneId,
    DateTimeOffset ClosedAt,
    DateTimeOffset? OpenedAt,
    int InvoiceCount,
    decimal GrossSales,
    decimal TotalDiscount,
    decimal NetSales,
    decimal TaxableValue,
    decimal TotalCgst,
    decimal TotalSgst,
    decimal TotalIgst,
    decimal CashExpected,
    decimal ChangeGiven,
    int PointsRedeemed,
    int PointsEarned,
    IReadOnlyList<TenderTotal> Tenders,
    IReadOnlyList<TaxSlabTotal> TaxSlabs,
    int HeldBillsOutstanding)
{
    public decimal TotalTax => TotalCgst + TotalSgst + TotalIgst;

    /// <summary>True for a lane that closed without taking anything.</summary>
    public bool TookNothing => InvoiceCount == 0;

    public decimal TotalOf(TenderType type) =>
        Tenders.FirstOrDefault(t => t.Type == type).Amount;
}

/// <summary>Where Z-reports are written and read.</summary>
public interface IDayCloseStore
{
    /// <summary>
    /// Reports on everything this lane has sold since its last close, without closing anything.
    /// Used to show the cashier what they are about to commit to.
    /// </summary>
    DayCloseSummary Preview(string laneId, DateTimeOffset asOf);

    /// <summary>
    /// Closes the lane: computes the report, saves it, and stamps every invoice it covers so the
    /// same sale can never appear on two Z-reports.
    /// </summary>
    DayCloseSummary Close(string laneId, DateTimeOffset closedAt);

    /// <summary>The most recent close for this lane, for reprinting.</summary>
    DayCloseSummary? FindLatest(string laneId);

    DayCloseSummary? FindById(long id);
}
