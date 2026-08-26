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

    /// <summary>
    /// Which part of the shop this belongs to — Staples, Dairy, Household. Optional, and null means
    /// the shop has not said, not that the item belongs nowhere.
    /// </summary>
    public string? Category { get; init; }

    /// <summary>
    /// What the shop pays for one of these, tax inclusive to match <see cref="SellPrice"/>.
    /// </summary>
    /// <remarks>
    /// Optional, because a shopkeeper rarely has a cost to hand for every line of a first catalogue
    /// and refusing the import over it would stop a shop opening. Everything that uses it treats
    /// absence as a fact rather than a zero, so an item with no cost is left out of a margin figure
    /// instead of appearing to earn the whole of what it sells for.
    /// </remarks>
    public decimal? CostPrice { get; init; }

    /// <summary>
    /// What one sale of this earns, as a percentage of what it sells for. Null when the cost is
    /// unknown, which is not the same as zero.
    /// </summary>
    public decimal? MarginPercent =>
        CostPrice is null || SellPrice <= 0m
            ? null
            : decimal.Round((SellPrice - CostPrice.Value) / SellPrice * 100m, 2, MidpointRounding.ToEven);

    public bool IsActive { get; init; } = true;
}
