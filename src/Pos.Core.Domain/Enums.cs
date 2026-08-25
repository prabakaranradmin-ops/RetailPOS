namespace Pos.Core.Domain;

/// <summary>How an item is sold, which decides whether quantity may be fractional.</summary>
public enum UnitType
{
    Each = 0,
    Kilogram = 1,
    Litre = 2,
    Metre = 3,
}

/// <remarks>
/// Values are persisted in <c>payments.tender_type</c>, so existing members must keep their
/// numbers. Add new ones at the end.
/// </remarks>
public enum TenderType
{
    Cash = 0,
    Card = 1,
    Upi = 2,
    StoreCredit = 3,

    /// <summary>
    /// Loyalty points, settled as a payment rather than as a discount. Points offset what the
    /// customer hands over; they never alter a line's price or its GST.
    /// </summary>
    LoyaltyPoints = 4,
}

public static class TenderTypeExtensions
{
    /// <summary>
    /// Only cash can be handed over in excess of the bill, because only cash gives change back.
    /// Every other tender is taken for an exact amount.
    /// </summary>
    public static bool AllowsOverTender(this TenderType type) => type == TenderType.Cash;
}

public enum InvoiceStatus
{
    /// <summary>Being built at the till; not yet settled or parked.</summary>
    Draft = 0,

    /// <summary>Parked against a recall token so the lane can serve the next customer.</summary>
    Held = 1,

    /// <summary>Fully tendered and printed.</summary>
    Settled = 2,

    Cancelled = 3,
}

public static class UnitTypeExtensions
{
    /// <summary>
    /// Weighed and measured goods take fractional quantities; discrete goods do not.
    /// </summary>
    public static bool AllowsFractionalQuantity(this UnitType unit) => unit != UnitType.Each;
}
