namespace Pos.Core.Domain;

/// <summary>
/// When a shelf counts as low: the one rule the till, the owner's screen and the day-end report all
/// use.
/// </summary>
/// <remarks>
/// <para>
/// An item the shop gave a reorder level warns at that level — the shop said so, for that item.
/// Anything else warns when it is down to a share of full, 10% unless the owner changes it, where
/// full is the most the shelf has been stocked to. That covers the catalogue nobody wanted to give
/// two hundred reorder levels to.
/// </para>
/// <para>
/// Nothing warns about an item that is not counted, or one that has never had anything on the
/// shelf to be a share of.
/// </para>
/// </remarks>
public static class LowStock
{
    public const decimal DefaultPercent = 10m;

    /// <summary>
    /// The count at or below which an item is low, or null when nothing would ever make it so.
    /// </summary>
    /// <param name="percentOfFull">0 switches the share-of-full rule off; reorder levels still apply.</param>
    public static decimal? WarnAt(decimal? reorderLevel, decimal? fullLevel, decimal percentOfFull)
    {
        if (reorderLevel is { } level)
            return level;

        if (fullLevel is { } full && full > 0m && percentOfFull > 0m)
            return decimal.Round(full * percentOfFull / 100m, 3, MidpointRounding.ToEven);

        return null;
    }

    public static bool IsLow(decimal? have, decimal? reorderLevel, decimal? fullLevel, decimal percentOfFull) =>
        have is { } count && WarnAt(reorderLevel, fullLevel, percentOfFull) is { } warnAt && count <= warnAt;

    /// <summary>Whether a share of full is one the owner can mean: 0 (off) up to 99.</summary>
    public static bool IsValidPercent(decimal percent) => percent >= 0m && percent < 100m;
}
