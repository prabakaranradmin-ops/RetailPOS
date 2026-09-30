namespace Pos.Core.Domain;

/// <summary>Something on the shelf that has stopped selling, and the money sitting in it.</summary>
/// <param name="LastSold">When it last went out on a bill that stands; null for never.</param>
/// <param name="CostEach">What one cost, from its latest purchase bill or the catalogue; null when never said.</param>
public sealed record DeadStockItem(
    long ItemId,
    string Sku,
    string Name,
    string? Category,
    UnitType Unit,
    decimal Have,
    DateOnly? LastSold,
    int? DaysSinceSold,
    decimal? CostEach)
{
    /// <summary>The money on the shelf in it, at cost. Null without a cost price.</summary>
    public decimal? TiedUp => CostEach is { } cost ? decimal.Round(cost * Have, 2, MidpointRounding.ToEven) : null;

    public bool NeverSold => LastSold is null;

    /// <summary>What to do about it, in a few words.</summary>
    public string Advice => DaysSinceSold switch
    {
        null => "never sold - return it, or stop ordering it",
        > DeadStock.LongDays => "put it on offer to clear it",
        _ => "move it to the front, or stop ordering it",
    };
}

/// <summary>
/// What counts as dead stock: counted, on the shelf, and not sold for two months.
/// </summary>
/// <remarks>
/// Only counted items, because only a count says anything is on the shelf. An item that has never
/// sold is included once it has been in the shop longer than the window - bought on a purchase bill
/// or added to the catalogue before it - so a delivery that came in yesterday is not dead yet.
/// </remarks>
public static class DeadStock
{
    /// <summary>Not sold for this many days is dead.</summary>
    public const int Days = 60;

    /// <summary>Not sold for this many days is worth clearing at a loss.</summary>
    public const int LongDays = 120;

    public static bool IsValidDays(int days) => days is >= 14 and <= 365;
}

/// <summary>Where the dead stock list is read from.</summary>
public interface IDeadStockStore
{
    /// <summary>Counted items with stock, not sold for <paramref name="days"/> days, most money first.</summary>
    IReadOnlyList<DeadStockItem> NotSelling(DateOnly today, int days = DeadStock.Days);
}
