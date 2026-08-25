namespace Pos.Core.Domain;

/// <summary>An item master record. Read-only as far as the billing screen is concerned.</summary>
/// <remarks>
/// A record rather than a class so a copy with one field changed — an imported item matched to the
/// id it already has — is a `with` expression rather than a hand-written clone that quietly drops
/// a field when a new one is added.
/// </remarks>
public sealed record Item
{
    public long Id { get; init; }
    public required string Sku { get; init; }
    public string? Barcode { get; init; }
    public required string HsnCode { get; init; }
    public required string Name { get; init; }

    /// <summary>Printed maximum retail price, tax inclusive.</summary>
    public decimal Mrp { get; init; }

    /// <summary>Price actually charged. Equals <see cref="Mrp"/> unless the store discounts the item.</summary>
    public decimal SellPrice { get; init; }

    public decimal GstRate { get; init; }

    /// <summary>True when <see cref="SellPrice"/> already contains the GST, which is the retail default.</summary>
    public bool IsTaxInclusive { get; init; } = true;

    public UnitType UnitType { get; init; } = UnitType.Each;

    public bool IsActive { get; init; } = true;
}
