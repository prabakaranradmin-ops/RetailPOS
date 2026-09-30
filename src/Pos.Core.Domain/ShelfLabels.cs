using Pos.Core.Domain.Import;

namespace Pos.Core.Domain;

/// <summary>What goes on the shelf edge under an item: its name, what it costs, and a code to scan.</summary>
/// <param name="Barcode">The item's barcode, or null for something sold loose; its SKU is printed then.</param>
/// <param name="ChangedAt">When the price on it was set.</param>
public sealed record ShelfLabel(
    long ItemId,
    string Sku,
    string Name,
    string? Barcode,
    UnitType Unit,
    decimal Mrp,
    decimal Price,
    DateTimeOffset? ChangedAt = null)
{
    /// <summary>What the customer saves against the MRP, or zero.</summary>
    public decimal Saving => Mrp > Price ? Mrp - Price : 0m;

    /// <summary>The code printed as bars: the barcode, or the SKU for an item without one.</summary>
    public string Code => string.IsNullOrWhiteSpace(Barcode) ? Sku : Barcode.Trim();
}

/// <summary>Where prices are changed in bulk, and where the shelf labels that are out of date come from.</summary>
public interface IPriceStore
{
    /// <summary>Every active item, for a price sheet.</summary>
    IReadOnlyList<PriceSheetItem> PriceSheet();

    /// <summary>Applies a checked price sheet in one go: every change, or none.</summary>
    /// <returns>How many items changed.</returns>
    int Apply(IReadOnlyList<PriceChange> changes);

    /// <summary>
    /// Items whose price changed, or which are new, since their label was last printed - the labels
    /// on the shelf that are wrong or missing. Oldest change first.
    /// </summary>
    IReadOnlyList<ShelfLabel> LabelsDue();

    /// <summary>Every active item, for a whole new set of labels.</summary>
    IReadOnlyList<ShelfLabel> AllLabels();

    /// <summary>Records that these items' labels are on the shelf now.</summary>
    void MarkLabelled(IEnumerable<long> itemIds, DateTimeOffset at);
}
