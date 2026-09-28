namespace Pos.Core.Domain;

/// <summary>How an item is sold, which decides whether quantity may be fractional.</summary>
/// <remarks>
/// Persisted as its number in <c>items.unit_type</c> and on every stored line, so existing members
/// keep their numbers and new ones go at the end. What each one is called and whether it takes a
/// fraction is in <see cref="Units"/>.
/// </remarks>
public enum UnitType
{
    Each = 0,
    Kilogram = 1,
    Litre = 2,
    Metre = 3,

    // The traditional Tamil units of sale.
    Seepu = 4,
    Thaar = 5,
    Kothu = 6,
    Kulai = 7,
    Kattu = 8,
    Pidi = 9,
    Mattai = 10,
    Kooru = 11,
    Koodai = 12,
    Sulai = 13,
    Pal = 14,
    Muzhu = 15,
    Keetru = 16,
    Jodi = 17,
    Kavuli = 18,
    Suvadu = 19,
    Adukku = 20,
    Saram = 21,
    Attai = 22,
    Pottalam = 23,
    Sittigai = 24,
    Thuli = 25,
    Aazhakku = 26,
    Uzhakku = 27,
    Padi = 28,
    AraiPadi = 29,
    Marakkaal = 30,
    Kalam = 31,
    Moottai = 32,
    Veesai = 33,
    Thulaam = 34,
    Muzham = 35,
    Saan = 36,
    Maaru = 37,
    Panthu = 38,
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
    /// Weighed and measured goods take fractional quantities; counted goods do not — a piece, a
    /// comb of bananas, a strip of sachets.
    /// </summary>
    public static bool AllowsFractionalQuantity(this UnitType unit) => Units.Of(unit).Fractional;
}
