namespace Pos.Core.Domain;

/// <summary>One kind of cash movement through the drawer on a day-end report.</summary>
/// <param name="Kind">What it was: <c>SupplierPayment</c>, <c>Refund</c>, and later others.</param>
/// <param name="Amount">Signed: negative left the drawer.</param>
public readonly record struct CashMovementTotal(string Kind, int Count, decimal Amount);

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

/// <param name="Name">Who was on the till, or null for sales rung up with nobody set.</param>
/// <param name="InvoiceCount">How many sales they took.</param>
/// <param name="NetSales">What those came to.</param>
/// <param name="CashHeld">
/// Cash they took less change they gave. What attributes a drawer difference to a shift.
/// </param>
public readonly record struct CashierTotal(string? Name, int InvoiceCount, decimal NetSales, decimal CashHeld)
{
    public string Label => Name ?? "(not recorded)";
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
/// left for the night. Orders are not counted here: see <paramref name="OrdersWaiting"/>.
/// </param>
/// <param name="OrdersWaiting">
/// Phone and WhatsApp orders saved and not yet paid for. Meant to wait - for the customer to come,
/// or the delivery to go - so they are reported apart from bills that were parked and forgotten.
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
    int HeldBillsOutstanding,
    int VoidedCount = 0,
    decimal VoidedValue = 0m,
    IReadOnlyList<CashierTotal>? Cashiers = null,
    decimal CreditCollected = 0m,
    decimal CreditCollectedCash = 0m,
    int CreditCollectedCount = 0,
    decimal CashPaidOut = 0m,
    decimal CashPaidIn = 0m,
    IReadOnlyList<CashMovementTotal>? DrawerMovements = null,
    int ReturnsCount = 0,
    decimal ReturnsValue = 0m,
    decimal ReturnsTax = 0m,
    int OrdersWaiting = 0,
    decimal? CashCounted = null,
    string? CountedBy = null)
{
    // CashCounted is what the cashier counted in the drawer at the close, typed before the till
    // showed CashExpected, so the count is a count and not a copy. Null when the day was closed
    // without one.

    /// <summary>Counted less expected: above zero the drawer is over, below it short. Null when not counted.</summary>
    public decimal? CashDifference => CashCounted is { } counted ? counted - CashExpected : null;

    // Returns are credit notes: goods brought back against a bill, from today or any earlier day.
    // They are a second document, not a change to the sale, so they touch none of the sales figures
    // above and the reconciliations still hold. A cash refund left the drawer, and is one of the
    // DrawerMovements (kind Refund) - which is how CashExpected already allows for it. ReturnsTax is
    // the output tax the returns took back, which comes off what the day owes in GST.

    /// <summary>Takings less what was refunded on returns: what the day actually kept.</summary>
    public decimal NetAfterReturns => NetSales - ReturnsValue;

    /// <summary>True when anything came back on a credit note.</summary>
    public bool HadReturns => ReturnsCount > 0;

    // CreditCollected is money customers paid back against earlier store credit. It is not a
    // sale - no tax, no invoice - so it is outside NetSales and outside Tenders, and every
    // reconciliation above still holds. The part paid in cash is in the drawer, though, so
    // CashExpected = cash taken - change given + CreditCollectedCash.
    //
    // CashPaidOut and CashPaidIn are cash that left or entered the drawer other than through a sale:
    // a supplier paid from the till, an expense, the float. Positive figures both. Neither is a sale
    // either, so CashExpected = cash taken - change given + CreditCollectedCash - CashPaidOut + CashPaidIn.

    /// <summary>Each kind of drawer movement on this report, with its count and signed total.</summary>
    public IReadOnlyList<CashMovementTotal> DrawerMovementTotals => DrawerMovements ?? [];

    /// <summary>
    /// True when money moved even though nothing was sold — credit paid back, a supplier paid from
    /// the till. A report of "no sales" alone would leave that cash unexplained.
    /// </summary>
    public bool MovedMoneyWithoutSales => CollectedCredit || CashPaidOut != 0m || CashPaidIn != 0m || HadReturns;

    /// <summary>Credit paid back by card or UPI: to the bank, not the drawer.</summary>
    public decimal CreditCollectedToBank => CreditCollected - CreditCollectedCash;

    /// <summary>True when customers paid anything back on credit during this report.</summary>
    public bool CollectedCredit => CreditCollected != 0m;

    public decimal TotalTax => TotalCgst + TotalSgst + TotalIgst;

    /// <summary>Who traded on this report. Empty when nobody was recorded.</summary>
    public IReadOnlyList<CashierTotal> CashierTotals => Cashiers ?? [];

    /// <summary>
    /// True when more than one person is named, which is when a drawer difference becomes worth
    /// attributing rather than just noting.
    /// </summary>
    public bool HasMultipleCashiers => CashierTotals.Count(c => c.Name is not null) > 1;

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
    /// <param name="cashCounted">What was counted in the drawer, or null for a close made without a count.</param>
    /// <param name="countedBy">Who counted it.</param>
    DayCloseSummary Close(string laneId, DateTimeOffset closedAt, decimal? cashCounted = null, string? countedBy = null);

    /// <summary>The most recent close for this lane, for reprinting.</summary>
    DayCloseSummary? FindLatest(string laneId);

    DayCloseSummary? FindById(long id);

    /// <summary>
    /// The closes this lane has taken, most recent first.
    /// </summary>
    /// <remarks>
    /// A lighter row than the full report, deliberately. A listing wants a date, a count and a
    /// figure; reading the tender split, the tax slabs and the cashier breakdown for thirty closes
    /// in order to print thirty lines is work nobody asked for.
    /// </remarks>
    IReadOnlyList<DayCloseEntry> List(string laneId, int limit = 30);
}

/// <summary>One line of the day-close listing: enough to find the report you meant.</summary>
public sealed record DayCloseEntry(
    long Id,
    DateTimeOffset ClosedAt,
    DateTimeOffset? OpenedAt,
    int InvoiceCount,
    decimal NetSales,
    decimal CashExpected,
    decimal? CashCounted = null,
    string? CountedBy = null)
{
    /// <summary>Counted less expected: above zero over, below zero short. Null when not counted.</summary>
    public decimal? CashDifference => CashCounted is { } counted ? counted - CashExpected : null;
}
