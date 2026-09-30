namespace Pos.Core.Domain;

public sealed class Customer
{
    public long Id { get; init; }

    /// <summary>Primary lookup key at the till, and the field the customer index is built on.</summary>
    public required string MobileNo { get; init; }

    public string? Name { get; init; }

    public int LoyaltyBalance { get; set; }

    /// <summary>
    /// GST state code. Compared against the outlet's own state code to decide whether a sale
    /// is intra-state (CGST/SGST) or inter-state (IGST).
    /// </summary>
    public string? StateCode { get; init; }

    /// <summary>
    /// The GSTIN of a customer registered for GST - a business. Their bills are then tax invoices to
    /// a registered buyer, carrying it, and filed in the return bill by bill (B2B).
    /// </summary>
    public string? Gstin { get; init; }

    /// <summary>Where a business customer is, as their bills print it.</summary>
    public string? Address { get; init; }

    public bool IsBusiness => Gstin is not null;
}
