namespace Pos.Core.Domain;

/// <summary>Why a stock figure changed.</summary>
public enum StockReason
{
    /// <summary>Sold across the counter.</summary>
    Sale,

    /// <summary>Put back because the sale was cancelled.</summary>
    Void,

    /// <summary>Set by a catalogue import.</summary>
    Import,

    /// <summary>Corrected by hand — a delivery, a breakage, a recount.</summary>
    Adjust,

    /// <summary>Counted on a stock sheet and loaded back — a delivery or a stocktake, in bulk.</summary>
    Count,
}

/// <summary>One movement in the stock ledger.</summary>
/// <param name="Delta">Signed: negative for a sale, positive for a delivery or a reversal.</param>
/// <param name="BalanceAfter">What the figure became, so a count can be walked back to where it broke.</param>
/// <param name="Reference">The invoice number, or the note somebody typed for a correction.</param>
public sealed record StockMovement(
    long Id,
    long ItemId,
    string ItemName,
    DateTimeOffset MovedAt,
    string LaneId,
    decimal Delta,
    decimal BalanceAfter,
    StockReason Reason,
    string? Reference);

/// <summary>An item's shelf figure, for a listing.</summary>
/// <param name="FullLevel">The most the shelf has been stocked to, or null if never known.</param>
/// <param name="WarnAt">
/// The count it warns at — its reorder level, or its share of full — worked out by whoever read it,
/// with the share of full that applied then. Null when nothing would make it low.
/// </param>
public sealed record StockLevel(
    long ItemId,
    string Sku,
    string Name,
    string? Category,
    decimal Quantity,
    decimal? ReorderLevel,
    UnitType Unit,
    decimal? FullLevel = null,
    decimal? WarnAt = null)
{
    public bool IsLow => (WarnAt ?? ReorderLevel) is { } floor && Quantity <= floor;

    public bool IsOut => Quantity <= 0m;

    /// <summary>How many to buy to get back to the reorder level, or null when there is no level.</summary>
    public decimal? ShortBy => ReorderLevel is { } floor && Quantity < floor ? floor - Quantity : null;

    /// <summary>What is left, as a share of full. Null when full is not known.</summary>
    public decimal? PercentLeft => FullLevel is { } full && full > 0m
        ? decimal.Round(Math.Max(Quantity, 0m) / full * 100m, 0, MidpointRounding.ToEven)
        : null;

    /// <summary>
    /// How many to buy: back up to full when full is known, else back to the reorder level. What
    /// the owner reads the list for.
    /// </summary>
    public decimal? ToOrder => FullLevel is { } full && full > Quantity
        ? full - Quantity
        : ShortBy;
}

/// <summary>One item as the stock sheet lists it: what it is, what the count says, and what full is.</summary>
/// <param name="Have">Null when the item is not counted yet. A new count on the sheet starts it.</param>
public sealed record StockSheetItem(long ItemId, string Sku, string Name, UnitType Unit, decimal? Have, decimal? FullLevel);

/// <summary>One change a loaded stock sheet will make.</summary>
/// <param name="NewCount">The count to set, or null to leave the count alone.</param>
/// <param name="NewFullLevel">The full level to set, or null to leave it alone.</param>
public sealed record StockSheetChange(long ItemId, string Sku, string Name, decimal? Before, decimal? NewCount, decimal? NewFullLevel);

/// <summary>
/// Reading and moving what is on the shelf.
/// </summary>
/// <remarks>
/// Deliberately not part of the checkout transaction's correctness contract: a sale that cannot
/// write its stock movement is still a sale. The books of account and the shelf count are different
/// things with different consequences for being wrong, and a till that refused to sell because it
/// could not decrement a number would be trading the important one for the trivial one.
/// </remarks>
public interface IStockStore
{
    /// <summary>
    /// Applies a signed change to an item's figure and records why. Does nothing for an item that
    /// is not counted.
    /// </summary>
    /// <returns>The new balance, or null if the item is not counted.</returns>
    decimal? Move(long itemId, decimal delta, StockReason reason, string laneId, string? reference = null);

    /// <summary>Sets an item's figure outright, recording the difference as the movement.</summary>
    decimal? Set(long itemId, decimal quantity, StockReason reason, string laneId, string? reference = null);

    /// <summary>Everything counted, most depleted first relative to its reorder level.</summary>
    IReadOnlyList<StockLevel> List(int limit = 500);

    /// <summary>Only what is at or below its reorder level.</summary>
    IReadOnlyList<StockLevel> ListLow(int limit = 500);

    /// <summary>One item's history, most recent first.</summary>
    IReadOnlyList<StockMovement> History(long itemId, int limit = 50);

    /// <summary>Every active item, counted or not, for a stock sheet.</summary>
    IReadOnlyList<StockSheetItem> Sheet();

    /// <summary>
    /// Applies a checked stock sheet in one go: every change, or none. A count on an item not yet
    /// counted starts counting it.
    /// </summary>
    /// <returns>How many counts changed.</returns>
    int ApplySheet(IReadOnlyList<StockSheetChange> changes, string laneId, string? reference = null);
}
