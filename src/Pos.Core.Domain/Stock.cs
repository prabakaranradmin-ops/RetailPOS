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
public sealed record StockLevel(
    long ItemId,
    string Sku,
    string Name,
    string? Category,
    decimal Quantity,
    decimal? ReorderLevel,
    UnitType Unit)
{
    public bool IsLow => ReorderLevel is { } floor && Quantity <= floor;

    public bool IsOut => Quantity <= 0m;

    /// <summary>How many to buy to get back to the reorder level, or null when there is no level.</summary>
    public decimal? ShortBy => ReorderLevel is { } floor && Quantity < floor ? floor - Quantity : null;
}

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
}
