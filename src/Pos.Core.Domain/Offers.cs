using System.Globalization;

namespace Pos.Core.Domain;

/// <summary>What an offer gives.</summary>
public enum OfferKind
{
    /// <summary>Buy <see cref="Offer.Buy"/> of an item, get <see cref="Offer.Get"/> more of it free.</summary>
    BuyGet = 0,

    /// <summary>A share off an item, or off everything in a department.</summary>
    Percent = 1,

    /// <summary><see cref="Offer.Buy"/> of an item for <see cref="Offer.Price"/>: three for a hundred.</summary>
    MultiPrice = 2,

    /// <summary>Money off a bill of <see cref="Offer.MinBill"/> or more.</summary>
    BillAmount = 3,

    /// <summary>A share off a bill of <see cref="Offer.MinBill"/> or more.</summary>
    BillPercent = 4,

    /// <summary><see cref="Offer.FreeQuantity"/> of an item free with a bill of <see cref="Offer.MinBill"/> or more.</summary>
    FreeItem = 5,
}

/// <summary>
/// One of the shop's offers or schemes, and when it runs.
/// </summary>
/// <remarks>
/// Every kind comes out as a discount on the lines it applies to - never as a price change and never
/// as a separate deduction from the bill. The tax is then worked out on what was actually charged,
/// by the same rule as any discount, so an offer cannot put the GST out.
/// </remarks>
public sealed record Offer
{
    public long Id { get; init; }

    /// <summary>What the offer is called, on the screen, on the bill and in the figures.</summary>
    public required string Name { get; init; }

    public required OfferKind Kind { get; init; }

    /// <summary>The item it is on, by SKU: every kind but a department offer and the bill offers.</summary>
    public string? Sku { get; init; }

    /// <summary>
    /// The item the SKU is, once looked up in the catalogue. Null for a SKU the catalogue does not
    /// have, which leaves the offer off.
    /// </summary>
    public long? ItemId { get; init; }

    /// <summary>The department a percentage offer is on, instead of one item.</summary>
    public string? Category { get; init; }

    /// <summary>How many to buy (buy-get), or how many for the price (multi-price).</summary>
    public int Buy { get; init; }

    /// <summary>How many free, for buy-get.</summary>
    public int Get { get; init; }

    public decimal Percent { get; init; }

    /// <summary>Money off, for a bill offer.</summary>
    public decimal Amount { get; init; }

    /// <summary>What <see cref="Buy"/> of the item cost together, for multi-price.</summary>
    public decimal Price { get; init; }

    /// <summary>The smallest bill the offer is for.</summary>
    public decimal MinBill { get; init; }

    /// <summary>How much of the item is free, for a free-item offer.</summary>
    public decimal FreeQuantity { get; init; }

    public DateOnly? From { get; init; }

    public DateOnly? To { get; init; }

    /// <summary>The days of the week it runs on; every day when empty.</summary>
    public IReadOnlyList<DayOfWeek> Days { get; init; } = [];

    public bool IsBillOffer => Kind is OfferKind.BillAmount or OfferKind.BillPercent;

    /// <summary>True on a day within its dates, and on one of its days.</summary>
    public bool IsOn(DateOnly day) =>
        (From is null || day >= From) && (To is null || day <= To) && (Days.Count == 0 || Days.Contains(day.DayOfWeek));

    /// <summary>What is wrong with the offer as written, or null when it will run.</summary>
    public string? Problem()
    {
        if (string.IsNullOrWhiteSpace(Name))
            return "An offer needs a name.";

        if (Name.Length > 60)
            return "An offer's name is printed on the bill: keep it to 60 characters.";

        if (From is { } from && To is { } to && to < from)
            return $"It ends ({to:dd-MM-yyyy}) before it starts ({from:dd-MM-yyyy}).";

        var needsItem = Kind is OfferKind.BuyGet or OfferKind.MultiPrice or OfferKind.FreeItem;

        if (needsItem && string.IsNullOrWhiteSpace(Sku))
            return "It needs the SKU of the item it is on.";

        return Kind switch
        {
            OfferKind.BuyGet when Buy < 1 || Get < 1 => "Buy and get are whole numbers of 1 or more: buy 2, get 1.",
            OfferKind.Percent when string.IsNullOrWhiteSpace(Sku) == string.IsNullOrWhiteSpace(Category) => "A percentage is off one item (a SKU) or one department (a category) - one of the two.",
            OfferKind.Percent or OfferKind.BillPercent when Percent is <= 0m or >= 100m => "The percentage is more than 0 and less than 100.",
            OfferKind.MultiPrice when Buy < 2 => "A multi-price offer is for 2 or more: 3 for 100.",
            OfferKind.MultiPrice when Price <= 0m => "The price for all of them together is more than nothing.",
            OfferKind.BillAmount when Amount <= 0m => "The money off is more than nothing.",
            OfferKind.BillAmount when MinBill > 0m && Amount >= MinBill => "The money off is less than the smallest bill it is for.",
            OfferKind.FreeItem when FreeQuantity <= 0m => "How much is free is more than nothing.",
            OfferKind.FreeItem or OfferKind.BillAmount or OfferKind.BillPercent when MinBill <= 0m => "It needs the smallest bill it is for.",
            _ when decimal.Round(Amount, 2) != Amount || decimal.Round(Price, 2) != Price || decimal.Round(MinBill, 2) != MinBill => "Money is to the paisa: two places at most.",
            _ => null,
        };
    }

    /// <summary>The offer in a few words: "Buy 2, get 1 free", "10% off Staples".</summary>
    public string Describe()
    {
        var what = Kind switch
        {
            OfferKind.BuyGet => $"Buy {Buy}, get {Get} free",
            OfferKind.Percent => $"{Figure(Percent)}% off {(Category is { } category ? category : Sku)}",
            OfferKind.MultiPrice => $"{Buy} for Rs {Money(Price)}",
            OfferKind.BillAmount => $"Rs {Money(Amount)} off a bill of Rs {Money(MinBill)} or more",
            OfferKind.BillPercent => $"{Figure(Percent)}% off a bill of Rs {Money(MinBill)} or more",
            OfferKind.FreeItem => $"{Figure(FreeQuantity)} free with a bill of Rs {Money(MinBill)} or more",
            _ => Kind.ToString(),
        };

        if (Kind is OfferKind.BuyGet or OfferKind.MultiPrice or OfferKind.FreeItem)
            what += $" ({Sku})";

        return what;
    }

    /// <summary>When it runs, in words: "01-10-2026 to 31-10-2026, Wed", or "always".</summary>
    public string When()
    {
        var dates = (From, To) switch
        {
            (null, null) => "always",
            ({ } from, null) => $"from {from:dd-MM-yyyy}",
            (null, { } to) => $"until {to:dd-MM-yyyy}",
            ({ } from, { } to) => $"{from:dd-MM-yyyy} to {to:dd-MM-yyyy}",
        };

        return Days.Count == 0 ? dates : $"{dates}, {string.Join(" ", Days.Select(d => CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedDayName(d)))}";
    }

    private static string Money(decimal value) => value.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));

    private static string Figure(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>How much an offer gave over a period, and on how many bills.</summary>
/// <param name="OfferName">As the lines carry it: two offers on one line are named together.</param>
public sealed record OfferUse(string OfferName, int Bills, decimal Given);

/// <summary>Where the shop's offers are kept.</summary>
public interface IOfferStore
{
    /// <summary>Every offer, by name, with each SKU looked up in the catalogue.</summary>
    IReadOnlyList<Offer> All();

    /// <summary>Replaces every offer with these - what loading the offers sheet does.</summary>
    void ReplaceAll(IReadOnlyList<Offer> offers, DateTimeOffset loadedAt);

    /// <summary>What each offer gave on the bills between two moments, voided bills left out.</summary>
    IReadOnlyList<OfferUse> Given(DateTimeOffset from, DateTimeOffset to);
}

/// <summary>What the offers give one line of the bill.</summary>
/// <param name="Index">The line.</param>
/// <param name="Discount">To the paisa; nothing when no offer applies.</param>
/// <param name="OfferName">The offer, or offers joined with " + ", that gave it.</param>
public sealed record OfferDiscount(int Index, decimal Discount, string? OfferName);

/// <summary>
/// Works out which of the shop's offers a bill gets, as a discount on each line.
/// </summary>
/// <remarks>
/// <para>
/// Run again whenever the bill changes, so it is always the bill as it stands that is priced - add
/// the third soap and the free one appears; take it off and it goes.
/// </para>
/// <para>
/// The rules, in order:
/// </para>
/// <list type="number">
/// <item>A line discounted by hand is left exactly as it is: an offer never replaces a discount the
/// cashier gave, nor adds to it.</item>
/// <item>Each item's lines together get the one item offer that gives the customer the most - buy-get,
/// a percentage off it or its department, or multi-price. A free item for which the bill is big enough
/// counts here too. Buy-get and multi-price are for items counted in whole pieces.</item>
/// <item>Then the one bill offer the bill is big enough for that gives the most, spread across the
/// lines not discounted by hand in proportion to what is left on each, to the paisa.</item>
/// </list>
/// <para>
/// Every discount is to the paisa, rounded half to even like every figure here, and never more than
/// what the line comes to.
/// </para>
/// </remarks>
public static class OfferEngine
{
    public static IReadOnlyList<OfferDiscount> Work(IReadOnlyList<InvoiceLine> lines, IReadOnlyList<Offer> offers, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(offers);

        var live = offers.Where(o => o.IsOn(today) && o.Problem() is null).ToList();
        var discount = new decimal[lines.Count];
        var names = new string?[lines.Count];
        var open = Enumerable.Range(0, lines.Count).Where(i => !lines[i].IsDiscountedByHand).ToList();

        // What the bill comes to with the offers so far: hand discounts as given, offers as worked out.
        decimal Net(IEnumerable<int> which) =>
            which.Sum(i => lines[i].Gross - (lines[i].IsDiscountedByHand ? lines[i].Discount : discount[i]));

        // ---- 1. Item offers ---------------------------------------------------------------------
        foreach (var item in open.GroupBy(i => lines[i].ItemId))
        {
            var indices = item.ToList();
            var first = lines[indices[0]];
            var quantity = indices.Sum(i => lines[i].Quantity);
            var gross = indices.Sum(i => lines[i].Gross);
            var whole = !first.Unit.AllowsFractionalQuantity();

            (decimal Amount, Offer Offer)? best = null;

            foreach (var offer in live.Where(o => !o.IsBillOffer && o.Kind != OfferKind.FreeItem && Matches(o, first)))
            {
                var amount = offer.Kind switch
                {
                    OfferKind.BuyGet when whole => decimal.Floor(quantity / (offer.Buy + offer.Get)) * offer.Get * first.UnitPrice,
                    OfferKind.MultiPrice when whole => Math.Max(0m, decimal.Floor(quantity / offer.Buy) * ((offer.Buy * first.UnitPrice) - offer.Price)),
                    OfferKind.Percent => Round(gross * offer.Percent / 100m),
                    _ => 0m,
                };

                amount = Math.Min(Round(amount), gross);

                if (amount > 0m && (best is null || amount > best.Value.Amount))
                    best = (amount, offer);
            }

            if (best is { } chosen)
                FillFromTheLast(chosen.Amount, chosen.Offer.Name, indices);
        }

        // ---- A free item, for a big enough bill -----------------------------------------------------
        foreach (var offer in live.Where(o => o.Kind == OfferKind.FreeItem))
        {
            var itemLines = open.Where(i => lines[i].ItemId == offer.ItemId).ToList();

            if (itemLines.Count == 0)
                continue;

            // The rest of the bill has to reach the sum on its own: the gift does not qualify itself.
            var rest = Net(Enumerable.Range(0, lines.Count).Where(i => lines[i].ItemId != offer.ItemId));

            if (rest < offer.MinBill)
                continue;

            var free = Math.Min(offer.FreeQuantity, itemLines.Sum(i => lines[i].Quantity));
            var amount = Math.Min(Round(free * lines[itemLines[0]].UnitPrice), itemLines.Sum(i => lines[i].Gross));

            // Instead of an item offer on the same lines, when it gives more.
            if (amount <= itemLines.Sum(i => discount[i]))
                continue;

            foreach (var i in itemLines)
            {
                discount[i] = 0m;
                names[i] = null;
            }

            FillFromTheLast(amount, offer.Name, itemLines);
        }

        // ---- 2. The bill offer ----------------------------------------------------------------------
        var bill = Net(Enumerable.Range(0, lines.Count));
        var room = open.Sum(i => lines[i].Gross - discount[i]);

        (decimal Amount, Offer Offer)? bestBill = null;

        foreach (var offer in live.Where(o => o.IsBillOffer && bill >= o.MinBill))
        {
            var amount = offer.Kind == OfferKind.BillAmount ? offer.Amount : Round(bill * offer.Percent / 100m);
            amount = Math.Min(amount, room);

            if (amount > 0m && (bestBill is null || amount > bestBill.Value.Amount))
                bestBill = (amount, offer);
        }

        if (bestBill is { } billOffer)
            SpreadInProportion(billOffer.Amount, billOffer.Offer.Name);

        return open.Select(i => new OfferDiscount(i, discount[i], discount[i] > 0m ? names[i] : null)).ToList();

        // The free ones are the last ones: the third soap of three, not a third off each.
        void FillFromTheLast(decimal amount, string name, List<int> indices)
        {
            var left = amount;

            for (var k = indices.Count - 1; k >= 0 && left > 0m; k--)
            {
                var i = indices[k];
                var take = Math.Min(left, lines[i].Gross - discount[i]);

                if (take <= 0m)
                    continue;

                discount[i] += take;
                names[i] = name;
                left -= take;
            }
        }

        // A bill discount belongs to every line it came off, in proportion, so each is taxed on
        // what it actually sold for. The paise rounding leaves goes on the line with most room.
        void SpreadInProportion(decimal amount, string name)
        {
            var shares = open
                .Select(i => (Index: i, Room: lines[i].Gross - discount[i]))
                .Where(s => s.Room > 0m)
                .ToList();

            var total = shares.Sum(s => s.Room);

            if (total <= 0m)
                return;

            var given = new decimal[shares.Count];

            for (var k = 0; k < shares.Count; k++)
                given[k] = Math.Min(shares[k].Room, Round(amount * shares[k].Room / total));

            var gap = amount - given.Sum();

            foreach (var k in Enumerable.Range(0, shares.Count).OrderByDescending(k => shares[k].Room))
            {
                if (gap == 0m)
                    break;

                var adjust = gap > 0m ? Math.Min(gap, shares[k].Room - given[k]) : Math.Max(gap, -given[k]);
                given[k] += adjust;
                gap -= adjust;
            }

            for (var k = 0; k < shares.Count; k++)
            {
                if (given[k] <= 0m)
                    continue;

                var i = shares[k].Index;
                discount[i] += given[k];
                names[i] = names[i] is { } already ? $"{already} + {name}" : name;
            }
        }
    }

    private static bool Matches(Offer offer, InvoiceLine line) =>
        (offer.ItemId is { } item && item == line.ItemId)
        || (offer.Kind == OfferKind.Percent && offer.Category is { } category
            && string.Equals(line.CategorySnapshot?.Trim(), category.Trim(), StringComparison.OrdinalIgnoreCase));

    private static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.ToEven);
}
