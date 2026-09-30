using System.Globalization;
using System.Text;

namespace Pos.Core.Domain;

/// <summary>
/// How fast things sell, how long the shelf will last, and how much to order.
/// </summary>
/// <remarks>
/// <para>
/// The rate is what sold over the last four weeks, less what came back, per day. Four weeks takes in
/// every day of the week four times, so a Sunday rush or a closed Tuesday does not tilt it, and is
/// short enough to follow a season as it turns. A lane that has not been selling that long is
/// measured over the days it has.
/// </para>
/// <para>
/// An order covers the days until the next one: enough to sell at that rate for the cover the owner
/// sets, two weeks unless changed, less what is on the shelf, and never less than what gets the item
/// back up to its reorder level. It is rounded up to whole units - a wholesaler sells a kilo of dal,
/// not 0.7 of one.
/// </para>
/// </remarks>
public static class Reorder
{
    /// <summary>How many days of sales the rate is taken over.</summary>
    public const int WindowDays = 28;

    /// <summary>How many days an order is meant to last, unless the owner says otherwise.</summary>
    public const int DefaultCoverDays = 14;

    public static bool IsValidCoverDays(int days) => days is >= 1 and <= 120;

    /// <summary>
    /// The days the rate is measured over: the window, or fewer when the lane's first sale was more
    /// recent than that.
    /// </summary>
    public static int DaysMeasured(DateOnly? firstSale, DateOnly today)
    {
        if (firstSale is not { } first)
            return WindowDays;

        return Math.Clamp(today.DayNumber - first.DayNumber + 1, 1, WindowDays);
    }

    /// <summary>What sells in a day, or null for something that has not sold at all.</summary>
    public static decimal? PerDay(decimal soldLessReturned, int days) =>
        soldLessReturned > 0m && days > 0 ? decimal.Round(soldLessReturned / days, 3, MidpointRounding.ToEven) : null;

    /// <summary>How many days what is on the shelf lasts at that rate. Null for something not selling.</summary>
    public static decimal? DaysLeft(decimal have, decimal? perDay) =>
        perDay is { } rate && rate > 0m ? decimal.Round(Math.Max(have, 0m) / rate, 1, MidpointRounding.ToEven) : null;

    /// <summary>
    /// How many to order, in whole units, or zero when the shelf will last the cover.
    /// </summary>
    /// <param name="reorderLevel">A level the shop set: an order is never less than what gets back to it.</param>
    /// <param name="isLow">Whether the low-stock rule says it is low, for something that is not selling.</param>
    /// <param name="fillTo">What "full" is, for something low that is not selling: it is filled back up to it.</param>
    public static decimal Suggest(decimal have, decimal? perDay, int coverDays, decimal? reorderLevel, bool isLow, decimal? fillTo)
    {
        var onShelf = Math.Max(have, 0m);
        decimal want;

        if (perDay is { } rate && rate > 0m)
        {
            want = rate * coverDays - onShelf;

            if (reorderLevel is { } level && level - onShelf > want)
                want = level - onShelf;
        }
        else
        {
            // Nothing sold in the window. Only if it is low anyway, and then back to full or to its
            // level: an order for something nobody is buying is money on a shelf.
            want = !isLow ? 0m : fillTo is { } full && full > onShelf ? full - onShelf : reorderLevel is { } level ? level - onShelf : 0m;
        }

        return want <= 0m ? 0m : decimal.Ceiling(want);
    }
}

/// <summary>One item on an order list.</summary>
/// <param name="PerDay">What it sells in a day, or null when it has not sold in the window.</param>
/// <param name="LastRate">What the supplier last charged a unit, before tax, or null when never bought.</param>
public sealed record ReorderLine(
    long ItemId,
    string Sku,
    string Name,
    UnitType Unit,
    decimal Have,
    decimal? PerDay,
    decimal? DaysLeft,
    decimal Order,
    decimal? LastRate,
    DateOnly? LastBought)
{
    /// <summary>What the order comes to at the last rate, before tax. Null when there is no rate.</summary>
    public decimal? Cost => LastRate is { } rate ? decimal.Round(rate * Order, 2, MidpointRounding.ToEven) : null;
}

/// <summary>What to order from one supplier - the one each item was last bought from.</summary>
/// <param name="SupplierId">Null for items never bought on a purchase bill, grouped together.</param>
public sealed record SupplierOrder(long? SupplierId, string Supplier, string? Phone, IReadOnlyList<ReorderLine> Lines)
{
    public const string NoSupplier = "Not bought from anyone yet";

    /// <summary>At the last rates, before tax, for the lines that have one.</summary>
    public decimal EstimatedCost => Lines.Sum(l => l.Cost ?? 0m);

    /// <summary>Whether every line has a last rate, so the estimate is the whole order.</summary>
    public bool EstimateIsComplete => Lines.All(l => l.LastRate is not null);

    /// <summary>
    /// The order as a message to send: the shop, the date, and one line per item with its unit. What
    /// a shopkeeper pastes into a message to the wholesaler.
    /// </summary>
    public string Message(string shopName, DateOnly date)
    {
        var text = new StringBuilder();

        text.Append(CultureInfo.InvariantCulture, $"Order from {shopName} - {date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)}");
        text.AppendLine();

        for (var i = 0; i < Lines.Count; i++)
        {
            var line = Lines[i];
            text.Append(CultureInfo.InvariantCulture, $"{i + 1}. {line.Name} - {line.Order.ToString("0.###", CultureInfo.InvariantCulture)} {Units.ScreenLabel(line.Unit)}");
            text.AppendLine();
        }

        return text.ToString();
    }
}
