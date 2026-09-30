namespace Pos.Core.Domain;

/// <summary>A delivery with a use-by date, and how much of it is probably still on the shelf.</summary>
/// <param name="BatchNo">The batch printed on the supplier's bill, when it gave one.</param>
/// <param name="Delivered">How many came on that bill.</param>
/// <param name="LikelyOnShelf">
/// How many of them are probably still on the shelf, worked out from the count; null for an item
/// the shop does not count, where only a look at the shelf can say.
/// </param>
public sealed record ExpiryWarning(
    long ItemId,
    string Sku,
    string Name,
    UnitType Unit,
    string? BatchNo,
    DateOnly Expires,
    DateOnly Received,
    string Supplier,
    decimal Delivered,
    decimal? LikelyOnShelf,
    int DaysLeft)
{
    public bool IsExpired => DaysLeft < 0;

    /// <summary>What to do about it, in a few words.</summary>
    public string Advice => DaysLeft switch
    {
        < 0 => "expired - take it off the shelf",
        0 => "expires today",
        <= 7 => "sell it first, or return it to the supplier",
        _ => "put it at the front",
    };
}

/// <summary>
/// Which deliveries are close to their use-by date and probably still on the shelf.
/// </summary>
/// <remarks>
/// <para>
/// The till does not track stock batch by batch, so what is left of each delivery is worked out:
/// a shop sells the oldest first, so the units on the shelf now are the most recent ones delivered.
/// The count is laid against the deliveries newest first, and a delivery gets whatever of the count
/// is left when it is reached. A delivery the count does not reach has, as far as the books can
/// tell, been sold.
/// </para>
/// <para>
/// That is an estimate, said as one. A count corrected after the old stock is taken off the shelf
/// moves it off the list, which is the loop an owner closes by acting on it.
/// </para>
/// </remarks>
public static class Expiry
{
    /// <summary>How far ahead a use-by date is warned about.</summary>
    public const int WarnDays = 30;

    /// <summary>How long after its date an expired delivery on an uncounted item is still mentioned.</summary>
    public const int ExpiredUncountedDays = 30;

    /// <summary>One delivery of one item, as the books have it.</summary>
    public sealed record Delivery(string? BatchNo, DateOnly? Expires, DateOnly Received, string Supplier, decimal Quantity);

    /// <summary>
    /// The warnings for one item: its deliveries newest first, and its count now.
    /// </summary>
    /// <param name="have">The shelf count, or null for an item nobody counts.</param>
    public static IEnumerable<ExpiryWarning> For(
        long itemId,
        string sku,
        string name,
        UnitType unit,
        decimal? have,
        IEnumerable<Delivery> newestFirst,
        DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(newestFirst);

        var left = have is { } count ? Math.Max(count, 0m) : (decimal?)null;
        var until = today.AddDays(WarnDays);

        foreach (var delivery in newestFirst)
        {
            decimal? onShelf = null;

            if (left is { } remaining)
            {
                onShelf = Math.Min(delivery.Quantity, remaining);
                left = remaining - onShelf;
            }

            if (delivery.Expires is not { } expires || expires > until)
                continue;

            var daysLeft = expires.DayNumber - today.DayNumber;

            // Counted: only what the count says is still there. Not counted: a recent date only,
            // or a delivery from two years ago would be warned about for ever.
            var worthSaying = onShelf is { } shelf
                ? shelf > 0m
                : daysLeft >= -ExpiredUncountedDays;

            if (worthSaying)
                yield return new ExpiryWarning(itemId, sku, name, unit, delivery.BatchNo, expires, delivery.Received, delivery.Supplier, delivery.Quantity, onShelf, daysLeft);
        }
    }

    /// <summary>
    /// What the cashier is told on scanning an item with a delivery past, or at, its date: a nudge to
    /// look at the packet in their hand, never a refusal.
    /// </summary>
    public static string? CheckNote(IEnumerable<ExpiryWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);

        var worst = warnings.Where(w => w.DaysLeft <= 0).OrderBy(w => w.Expires).FirstOrDefault();

        return worst is null
            ? null
            : worst.IsExpired
                ? $"Check the date: a delivery of this expired on {worst.Expires.ToString("dd MMM", System.Globalization.CultureInfo.InvariantCulture)}."
                : "Check the date: a delivery of this expires today.";
    }
}

/// <summary>Where the deliveries with use-by dates are read from.</summary>
public interface IExpiryStore
{
    /// <summary>Everything within <see cref="Expiry.WarnDays"/> of its date and probably on the shelf, soonest first.</summary>
    IReadOnlyList<ExpiryWarning> Expiring(DateOnly today);

    /// <summary>The same, for one item: what the till asks when it is scanned.</summary>
    IReadOnlyList<ExpiryWarning> ExpiringFor(long itemId, DateOnly today);
}
