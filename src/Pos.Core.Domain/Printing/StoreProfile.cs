namespace Pos.Core.Domain.Printing;

/// <summary>
/// What goes at the top of a receipt. A GST invoice has to identify who issued it, so the name,
/// address and GSTIN are not decoration.
/// </summary>
public sealed record StoreProfile
{
    public required string Name { get; init; }

    public string? AddressLine1 { get; init; }

    public string? AddressLine2 { get; init; }

    public string? Phone { get; init; }

    /// <summary>The outlet's GST identification number, printed on every invoice.</summary>
    public string? Gstin { get; init; }

    /// <summary>
    /// The outlet's FSSAI licence number. A shop selling food has to display it, and a grocery
    /// bill in India carries it under the GSTIN.
    /// </summary>
    public string? FssaiNumber { get; init; }

    /// <summary>
    /// The number a customer rings about a bill. Printed separately from <see cref="Phone"/>
    /// because on a multi-branch shop they are often not the same number.
    /// </summary>
    public string? CustomerCarePhone { get; init; }

    /// <summary>Printed at the foot. A thank-you, return policy, or nothing.</summary>
    public string? FooterMessage { get; init; }

    /// <summary>
    /// Prefix for money on the receipt. Defaults to "Rs." rather than the rupee sign because a
    /// thermal printer's built-in code pages generally have no glyph for it, and a missing glyph
    /// prints as a box or as nothing at all.
    /// </summary>
    public string CurrencyPrefix { get; init; } = "Rs.";
}
