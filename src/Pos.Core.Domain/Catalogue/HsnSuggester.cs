namespace Pos.Core.Domain.Catalogue;

/// <summary>
/// Offers an HSN code and its slab for a product being added by hand.
/// </summary>
/// <remarks>
/// What the shop already sells always outranks the shipped table. A code sitting on an item in this
/// catalogue is a decision the shop took, probably with its accountant, and it is current in a way
/// <see cref="HsnDirectory"/> cannot be — rates move when the GST Council says so, and a table built
/// into an installer does not.
///
/// Nothing here fills a field on its own. Every suggestion carries the reason it is being made, and
/// the shopkeeper picks: a wrong HSN prints on every invoice of that product for as long as the
/// shop sells it, and neither this nor anything else in the software can tell that it is wrong.
/// </remarks>
/// <param name="search">
/// Finds catalogue items matching a word. The repository's own search, so the suggestion comes off
/// the same index the till searches on.
/// </param>
public sealed class HsnSuggester(Func<string, IReadOnlyList<Item>> search)
{
    private readonly Func<string, IReadOnlyList<Item>> _search = search ?? throw new ArgumentNullException(nameof(search));

    /// <summary>How many suggestions are worth showing. Past a handful it is a list to read, not help.</summary>
    public const int Limit = 5;

    public IReadOnlyList<HsnSuggestion> For(string productName)
    {
        if (string.IsNullOrWhiteSpace(productName))
            return [];

        var suggestions = new List<HsnSuggestion>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var found in FromOwnCatalogue(productName))
        {
            // Keyed on the pair, not the code. The same HSN at two different slabs is two different
            // answers, and collapsing them would hide the one the shopkeeper needed to notice.
            if (seen.Add($"{found.HsnCode}@{found.GstRate}"))
                suggestions.Add(found);
        }

        foreach (var known in HsnDirectory.Lookup(productName))
        {
            if (seen.Add($"{known.HsnCode}@{known.GstRate}"))
                suggestions.Add(known);
        }

        return suggestions.Take(Limit).ToList();
    }

    /// <summary>
    /// What the shop already sells under a similar name, most words in common first.
    /// </summary>
    /// <remarks>
    /// Every significant word is searched separately rather than the whole name at once. A shop
    /// adding "Hamam Soap 100g" almost never has that exact name already; it has "Lux Soap 100g",
    /// and the word they share is the one that identifies what the thing is.
    /// </remarks>
    private IEnumerable<HsnSuggestion> FromOwnCatalogue(string productName)
    {
        var words = HsnDirectory.Words(productName);

        if (words.Count == 0)
            return [];

        var scored = new Dictionary<long, (Item Item, int Hits)>();

        foreach (var word in words)
        {
            IReadOnlyList<Item> matches;

            try
            {
                matches = _search(word);
            }
            catch
            {
                // A catalogue that cannot be read costs a suggestion, not the ability to add an
                // item. The shopkeeper types the code themselves, which is what they did before
                // this existed.
                continue;
            }

            foreach (var item in matches)
            {
                if (string.IsNullOrWhiteSpace(item.HsnCode))
                    continue;

                var hits = scored.TryGetValue(item.Id, out var already) ? already.Hits + 1 : 1;
                scored[item.Id] = (item, hits);
            }
        }

        return scored.Values
            .OrderByDescending(match => match.Hits)
            .ThenBy(match => match.Item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(match => new HsnSuggestion(
                match.Item.HsnCode,
                match.Item.GstRate,
                $"your {match.Item.Name} is {match.Item.HsnCode} at {HsnDirectory.Rate(match.Item.GstRate)}%",
                FromOwnCatalogue: true));
    }
}
