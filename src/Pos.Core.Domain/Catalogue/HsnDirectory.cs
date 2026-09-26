using System.Globalization;

namespace Pos.Core.Domain.Catalogue;

/// <param name="HsnCode">The code being offered.</param>
/// <param name="GstRate">The slab that usually goes with it.</param>
/// <param name="Reason">
/// Why this is being offered, in words the shopkeeper can judge — "because your Lux Soap 100g is
/// 3401". A suggestion nobody can see the basis of is a guess they have no way to check.
/// </param>
/// <param name="FromOwnCatalogue">
/// True when this came from something the shop already sells. Those rank first and are the only
/// ones that reflect a decision the shop has actually taken.
/// </param>
public sealed record HsnSuggestion(string HsnCode, decimal GstRate, string Reason, bool FromOwnCatalogue);

/// <summary>
/// A starter list of the HSN codes an Indian grocery uses most, for a catalogue with nothing in it
/// yet to match against.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are suggestions, not advice.</b> HSN classification and the slab that follows from it
/// are the shop's responsibility and its accountant's call. The codes here are the common ones for
/// ordinary grocery lines, at four digits; a shop over the turnover threshold has to report six or
/// eight, and which applies depends on its own figures.
/// </para>
/// <para>
/// Two things make anything in this table capable of being wrong for a given shop. Rates move when
/// the GST Council says so, and this table does not. And on staples the rate turns on whether the
/// goods are pre-packaged and labelled or sold loose — the same dal is nil-rated out of a sack and
/// 5% in a printed packet. Where that distinction bites, the entry says so rather than picking one.
/// </para>
/// <para>
/// This is why anything the shop already sells outranks everything here: its own catalogue reflects
/// decisions it actually took, and is current in a way a shipped table cannot be.
/// </para>
/// </remarks>
public static class HsnDirectory
{
    /// <param name="Keywords">Matched against the words of a product name, case-insensitively.</param>
    /// <param name="Note">
    /// The catch, where there is one. Shown beside the suggestion so the shopkeeper sees the reason
    /// to think rather than only the number.
    /// </param>
    public sealed record Entry(string[] Keywords, string HsnCode, decimal GstRate, string What, string? Note = null);

    /// <summary>The packaged-or-loose caveat, which applies to most unprocessed staples.</summary>
    private const string PackagedOrLoose =
        "nil if sold loose or unbranded, 5% pre-packaged and labelled — check which you sell";

    public static IReadOnlyList<Entry> Entries { get; } =
    [
        // ---- Household and personal care. Rates here are stable and the classification is easy. ----
        new(["soap", "bathing", "hamam", "lux", "lifebuoy", "santoor"], "3401", 18m, "bathing soap"),
        new(["detergent", "washing", "powder", "surf", "ariel", "rin", "tide"], "3402", 18m, "detergent and washing powder"),
        new(["dishwash", "dish", "vim", "scrub"], "3402", 18m, "dishwashing liquid and bars"),
        new(["phenyl", "cleaner", "harpic", "lizol", "floor"], "3402", 18m, "floor and toilet cleaner"),
        new(["shampoo", "clinic", "sunsilk", "dove"], "3305", 18m, "shampoo"),
        new(["hair", "oil", "parachute", "navratna"], "3305", 18m, "hair oil", "coconut oil sold as edible oil is 1513, not 3305"),
        new(["toothpaste", "colgate", "pepsodent", "closeup"], "3306", 18m, "toothpaste"),
        new(["toothbrush", "brush"], "9603", 18m, "toothbrush"),
        new(["agarbatti", "incense", "dhoop"], "3307", 5m, "agarbatti and incense"),
        new(["candle"], "3406", 12m, "candles"),
        new(["matchbox", "matches", "match"], "3605", 5m, "matches"),
        new(["sanitary", "napkin", "pad", "whisper", "stayfree"], "9619", 0m, "sanitary napkins"),

        // ---- Staples. Where the packaged-or-loose rule does its damage. ----
        new(["dal", "dhal", "toor", "tur", "moong", "urad", "chana", "masoor", "pulses", "lentil"], "0713", 5m, "pulses and dal", PackagedOrLoose),
        new(["rice", "basmati", "ponni", "sona"], "1006", 5m, "rice", PackagedOrLoose),
        new(["wheat", "atta", "flour", "maida", "rava", "sooji"], "1101", 5m, "wheat flour and atta", PackagedOrLoose),
        new(["sugar"], "1701", 5m, "sugar"),
        new(["salt"], "2501", 0m, "salt"),
        new(["tea", "chai"], "0902", 5m, "tea"),
        new(["coffee"], "0901", 5m, "coffee"),
        new(["turmeric", "manjal", "chilli", "coriander", "cumin", "jeera", "masala", "spice"], "0910", 5m, "spices"),
        new(["oil", "sunflower", "groundnut", "gingelly", "sesame", "mustard", "refined"], "1512", 5m, "edible oil", "the exact code follows the seed: 1512 sunflower, 1508 groundnut, 1514 mustard"),
        new(["ghee"], "0405", 12m, "ghee"),
        new(["butter"], "0405", 12m, "butter"),
        new(["milk"], "0401", 0m, "fresh milk", "flavoured or condensed milk is taxed differently"),
        new(["curd", "yoghurt", "yogurt"], "0403", 0m, "curd", "5% pre-packaged and labelled"),
        new(["paneer"], "0406", 5m, "paneer", "nil if not pre-packaged"),
        new(["egg", "eggs"], "0407", 0m, "eggs"),
        new(["bread"], "1905", 0m, "bread", "rusk, buns and pizza bread are not nil-rated"),

        // ---- Packaged food. ----
        new(["biscuit", "cookies", "britannia", "parle"], "1905", 18m, "biscuits"),
        new(["namkeen", "mixture", "snack", "chips", "kurkure", "lays"], "2106", 12m, "namkeen and savoury snacks"),
        new(["chocolate"], "1806", 18m, "chocolate"),
        new(["noodles", "pasta", "maggi", "vermicelli", "semiya"], "1902", 18m, "noodles and pasta"),
        new(["juice"], "2009", 12m, "fruit juice"),
        new(["soft", "drink", "cola", "pepsi", "soda", "aerated"], "2202", 28m, "aerated drinks", "aerated drinks also carry compensation cess"),
        new(["water", "mineral", "bisleri", "packaged"], "2201", 18m, "packaged drinking water", "20-litre cans are 12%"),
    ];

    /// <summary>
    /// Offers what this table knows about a product name, best match first.
    /// </summary>
    /// <remarks>
    /// Matching is on whole words, so "oil" in "Sunflower Oil" hits and the "oil" inside "Boiled"
    /// does not. A name matching several entries gets several suggestions and the shopkeeper picks:
    /// "Coconut Oil" is genuinely two different codes depending on whether it is sold to cook with
    /// or to put on hair, and nothing here can know which this shop means.
    /// </remarks>
    public static IReadOnlyList<HsnSuggestion> Lookup(string productName)
    {
        if (string.IsNullOrWhiteSpace(productName))
            return [];

        var words = Words(productName);

        if (words.Count == 0)
            return [];

        return Entries
            .Select(entry => (Entry: entry, Hits: entry.Keywords.Count(words.Contains)))
            .Where(match => match.Hits > 0)
            .OrderByDescending(match => match.Hits)
            .ThenBy(match => match.Entry.HsnCode, StringComparer.Ordinal)
            .Select(match => new HsnSuggestion(
                match.Entry.HsnCode,
                match.Entry.GstRate,
                match.Entry.Note is { } note
                    ? $"{match.Entry.What} — {note}"
                    : match.Entry.What,
                FromOwnCatalogue: false))
            .ToList();
    }

    /// <summary>
    /// The words of a name, lowercased, with sizes and pack counts dropped.
    /// </summary>
    /// <remarks>
    /// "1kg", "500ml" and "5" carry nothing about what a thing is, and leaving them in lets two
    /// unrelated products match because both come in 500ml.
    /// </remarks>
    internal static HashSet<string> Words(string name)
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in name.Split([' ', '\t', ',', '.', '-', '/', '(', ')', '\''], StringSplitOptions.RemoveEmptyEntries))
        {
            var word = raw.Trim().ToLowerInvariant();

            // Anything starting with a digit is a size or a count: 1kg, 500ml, 250g, 6.
            if (word.Length < 2 || char.IsDigit(word[0]))
                continue;

            words.Add(word);
        }

        return words;
    }

    /// <summary>A slab as it is typed into a form: "5", "18", "12.5" — never "18.00".</summary>
    public static string Rate(decimal rate) => rate.ToString("0.##", CultureInfo.InvariantCulture);
}
