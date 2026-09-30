using Pos.Core.Domain;

namespace Pos.App.ViewModels;

/// <summary>What a price or stock sheet leaves alone, said as a sentence whatever the counts.</summary>
public static class SheetWords
{
    /// <summary>
    /// "3 rows are blank and 1 row gives the price the item already has: those are left alone."
    /// Empty when the sheet leaves nothing alone.
    /// </summary>
    /// <remarks>
    /// This was one fixed sentence, "{rows} are blank and {n} match what the price already is",
    /// which read "1 row are blank and 0 match" - a verb for several rows, and a count of none said
    /// out loud.
    /// </remarks>
    /// <param name="what">The figure the sheet sets: "price" or "count".</param>
    public static string LeftAlone(int blank, int unchanged, string what)
    {
        var parts = new List<string>();

        if (blank > 0)
            parts.Add($"{Plural.Of(blank, "row")} {(blank == 1 ? "is" : "are")} blank");

        if (unchanged > 0)
            parts.Add($"{Plural.Of(unchanged, "row")} {(unchanged == 1 ? "gives" : "give")} the {what} the item already has");

        return parts.Count == 0
            ? string.Empty
            : $"{string.Join(" and ", parts)}: {(blank + unchanged == 1 ? "it is" : "those are")} left alone.";
    }
}
