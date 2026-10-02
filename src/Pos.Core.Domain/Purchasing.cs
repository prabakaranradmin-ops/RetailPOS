using Pos.Core.Tax;

namespace Pos.Core.Domain;

/// <summary>A wholesaler the shop buys from.</summary>
/// <param name="StateCode">Where they supply from. Decides CGST and SGST, or IGST, on their bills.</param>
/// <param name="ChargesGst">
/// True for a regular GST-registered dealer, whose bills carry GST the shop can claim back. False
/// for an unregistered or composition dealer, whose bills carry none.
/// </param>
public sealed record Supplier(
    long Id,
    string Name,
    string? Phone,
    string? Gstin,
    string StateCode,
    bool ChargesGst,
    string? Address = null,
    bool IsActive = true);

/// <summary>How a supplier was paid.</summary>
public enum SupplierPaymentMethod
{
    /// <summary>Cash out of the till drawer. The only one the day-end drawer count has to know about.</summary>
    DrawerCash = 0,

    /// <summary>Cash from somewhere other than the till — the owner's pocket, the safe.</summary>
    OtherCash = 1,

    Upi = 2,

    Bank = 3,

    Cheque = 4,
}

/// <summary>One line of a supplier's bill, as typed in from the paper.</summary>
/// <param name="Rate">Per unit, before tax — how a wholesaler's bill states it.</param>
/// <param name="Discount">Off the whole line, before tax.</param>
public sealed record PurchaseLineEntry(
    Item Item,
    decimal Quantity,
    decimal Rate,
    decimal GstRate,
    decimal Discount = 0m,
    string? BatchNo = null,
    DateOnly? ExpiryDate = null);

/// <summary>One line of a supplier's bill, priced.</summary>
/// <param name="TaxableValue">Quantity times rate, less discount. Two decimals.</param>
/// <param name="LineTotal">What the line costs the shop, tax included.</param>
public sealed record PurchaseLine(
    long ItemId,
    string Name,
    string Hsn,
    UnitType Unit,
    decimal Quantity,
    decimal Rate,
    decimal Discount,
    decimal GstRate,
    decimal TaxableValue,
    decimal Cgst,
    decimal Sgst,
    decimal Igst,
    decimal LineTotal,
    string? BatchNo = null,
    DateOnly? ExpiryDate = null)
{
    public decimal Tax => Cgst + Sgst + Igst;

    /// <summary>
    /// What one unit cost, tax included — the figure the catalogue's cost price is kept in, so a
    /// margin is worked out against the tax-inclusive price it sells at.
    /// </summary>
    public decimal UnitCost => decimal.Round(LineTotal / Quantity, 2, MidpointRounding.ToEven);

    /// <summary>
    /// Prices a line the way the supplier's bill does: tax added on top of the rate. The same engine
    /// the till uses, so a purchase is rounded to the paisa exactly as a sale is.
    /// </summary>
    /// <param name="chargesGst">False for a supplier whose bills carry no GST: the rate is all of it.</param>
    public static PurchaseLine Price(PurchaseLineEntry entry, bool interState, bool chargesGst)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!entry.Item.UnitType.AllowsFractionalQuantity() && decimal.Truncate(entry.Quantity) != entry.Quantity)
            throw new ArgumentOutOfRangeException(nameof(entry), entry.Quantity, $"{entry.Item.Name} comes in whole {Units.Of(entry.Item.UnitType).Code}.");

        var rate = chargesGst ? entry.GstRate : 0m;

        var tax = TaxEngine.Calculate(new TaxLineInput(
            entry.Quantity,
            entry.Rate,
            entry.Discount,
            rate,
            interState,
            IsTaxInclusive: false));

        return new PurchaseLine(
            entry.Item.Id,
            entry.Item.Name,
            entry.Item.HsnCode,
            entry.Item.UnitType,
            entry.Quantity,
            entry.Rate,
            entry.Discount,
            rate,
            tax.TaxableValueForDisplay,
            tax.Cgst,
            tax.Sgst,
            tax.Igst,
            tax.LineTotal,
            string.IsNullOrWhiteSpace(entry.BatchNo) ? null : entry.BatchNo.Trim(),
            entry.ExpiryDate);
    }
}

/// <summary>A supplier's bill, ready to record.</summary>
/// <param name="RoundOff">
/// What the supplier's printed total differs from its lines by, so the entry owes what the paper
/// says. Never more than a rupee either way: a bigger difference is a line typed wrong.
/// </param>
public sealed record PurchaseBill(
    Supplier Supplier,
    string BillNo,
    DateOnly BillDate,
    IReadOnlyList<PurchaseLine> Lines,
    bool InterState,
    decimal RoundOff = 0m,
    string? Note = null)
{
    /// <summary>The most a supplier's round-off can be before it is a mistake instead.</summary>
    public const decimal MostRoundOff = 1.00m;

    public decimal TaxableValue => Lines.Sum(l => l.TaxableValue);
    public decimal Cgst => Lines.Sum(l => l.Cgst);
    public decimal Sgst => Lines.Sum(l => l.Sgst);
    public decimal Igst => Lines.Sum(l => l.Igst);
    public decimal Tax => Cgst + Sgst + Igst;
    public decimal LinesTotal => Lines.Sum(l => l.LineTotal);

    /// <summary>What the shop owes for it.</summary>
    public decimal Total => LinesTotal + RoundOff;

    /// <summary>
    /// Matches the total to the one printed on the paper, or says why it cannot.
    /// </summary>
    /// <returns>The bill with its round-off set, or the reason the two do not agree.</returns>
    public (PurchaseBill? Bill, string? Problem) MatchPrinted(decimal printedTotal)
    {
        var difference = printedTotal - LinesTotal;

        if (Math.Abs(difference) > MostRoundOff)
        {
            return (null, $"The lines come to {LinesTotal:N2} but the bill says {printedTotal:N2}. "
                        + $"A difference of {Math.Abs(difference):N2} is more than a round-off — a line is typed wrong or missing.");
        }

        return (this with { RoundOff = difference }, null);
    }
}

/// <summary>A recorded purchase, for listing.</summary>
public sealed record PurchaseSummary(
    long Id,
    long SupplierId,
    string SupplierName,
    string BillNo,
    DateOnly BillDate,
    DateTimeOffset ReceivedAt,
    int LineCount,
    decimal Total,
    bool ChargesGst,
    DateTimeOffset? VoidedAt)
{
    public bool IsVoided => VoidedAt is not null;
}

/// <summary>What recording a purchase did, beyond the bill itself.</summary>
/// <param name="CountsMoved">Lines whose item is counted, so the shelf went up.</param>
/// <param name="NotCounted">Items the shop does not count; the bill is recorded, the shelf is not.</param>
/// <param name="CostAboveSellingPrice">
/// Items that now cost more than they sell for. Worth a look before the next one is sold at a loss.
/// </param>
public sealed record PurchaseRecorded(
    long PurchaseId,
    int CountsMoved,
    IReadOnlyList<string> NotCounted,
    IReadOnlyList<string> CostAboveSellingPrice);

/// <summary>A supplier the shop owes something to, for the list of who is owed what.</summary>
public sealed record SupplierBalance(Supplier Supplier, decimal Owed, DateOnly? LastBill);

/// <summary>A line of a supplier's bill, as far as sending it back goes: how much came, and how much has gone back.</summary>
public sealed record ReturnablePurchaseLine(int LineNo, long ItemId, string Name, UnitType Unit, decimal Bought, decimal SentBack, decimal LineTotal)
{
    /// <summary>How much of it can still go back.</summary>
    public decimal Left => Bought - SentBack;
}

/// <summary>Goods going back to the supplier: which line of their bill, and how many.</summary>
public sealed record SupplierReturnPick(int LineNo, decimal Quantity);

/// <summary>
/// A debit note: goods sent back to a supplier, priced as their bill charged for them, and taken off
/// what the shop owes them.
/// </summary>
/// <param name="NotCounted">Items the shop does not count, so the shelf was not touched for them.</param>
public sealed record SupplierReturn(
    long Id,
    string Number,
    long PurchaseId,
    string SupplierName,
    string BillNo,
    DateTimeOffset ReturnedAt,
    string Reason,
    decimal TaxableValue,
    decimal Tax,
    decimal Total,
    int Lines,
    IReadOnlyList<string> NotCounted);

/// <summary>A payment to a supplier.</summary>
public sealed record SupplierPayment(
    long Id,
    long SupplierId,
    DateTimeOffset PaidAt,
    SupplierPaymentMethod Method,
    decimal Amount,
    string? Reference);

/// <summary>
/// The suppliers, what was bought from them, and what the shop owes them.
/// </summary>
/// <remarks>
/// Nothing is stored as a balance. What the shop owes a supplier is its bills that were not
/// cancelled, less what it has paid them, summed in exact paise each time — the same arrangement
/// as a customer's khata, the other way round.
/// </remarks>
public interface IPurchaseStore
{
    IReadOnlyList<Supplier> Suppliers(bool activeOnly = true);

    Supplier? FindSupplier(long id);

    /// <summary>Adds a supplier. A name or GSTIN already used is refused.</summary>
    Supplier AddSupplier(Supplier supplier);

    void UpdateSupplier(Supplier supplier);

    /// <summary>
    /// Records a supplier's bill in one transaction: the bill and its lines, the shelf count of
    /// every counted item it brought, and each item's cost price. A bill number already entered for
    /// that supplier is refused.
    /// </summary>
    PurchaseRecorded Record(PurchaseBill bill, string laneId, DateTimeOffset receivedAt, string? cashierName);

    /// <summary>
    /// Cancels a bill entered by mistake: it stops being owed, and the shelf gives back what its
    /// receipt put on. The record stays, marked.
    /// </summary>
    void Void(long purchaseId, string reason, string laneId, DateTimeOffset at);

    IReadOnlyList<PurchaseSummary> Recent(int limit = 50, long? supplierId = null);

    IReadOnlyList<PurchaseLine> Lines(long purchaseId);

    decimal Owed(long supplierId);

    /// <summary>Every supplier with their balance, most owed first. Zero balances included when asked.</summary>
    IReadOnlyList<SupplierBalance> Balances(bool owingOnly = false);

    /// <summary>
    /// Pays a supplier. Paid from the till drawer, it is also taken out of what the drawer should
    /// hold at the day's close.
    /// </summary>
    SupplierPayment Pay(long supplierId, decimal amount, SupplierPaymentMethod method, string? reference, string laneId, DateTimeOffset paidAt, string? cashierName);

    /// <summary>The supplier's account, newest first, each line with what was owed after it.</summary>
    IReadOnlyList<CreditMovement> History(long supplierId, int limit = 50);

    /// <summary>Each line of a bill, with how much of it has already gone back.</summary>
    IReadOnlyList<ReturnablePurchaseLine> Returnable(long purchaseId);

    /// <summary>
    /// Sends goods back to the supplier on a debit note, in one transaction: the note and its lines,
    /// the shelf count of every counted item, and what the shop owes the supplier.
    /// </summary>
    SupplierReturn SendBack(long purchaseId, IReadOnlyList<SupplierReturnPick> picks, string reason, string laneId, DateTimeOffset at);
}
