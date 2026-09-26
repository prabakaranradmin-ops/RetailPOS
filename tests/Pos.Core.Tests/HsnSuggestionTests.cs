using Pos.Core.Domain;
using Pos.Core.Domain.Catalogue;
using Pos.TestSupport;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// Offering an HSN code for a product being added by hand.
/// </summary>
/// <remarks>
/// The rule these hold down is precedence: what the shop already sells outranks the shipped table,
/// always. The shop's own codes are decisions it took with its accountant and are current; a table
/// built into an installer is neither, and it goes stale the first time the GST Council moves a
/// rate.
///
/// Nothing here asserts that a code is correct for a shop. It cannot — that is the shop's call,
/// which is why every suggestion carries the reason it is being offered.
/// </remarks>
public class HsnSuggestionTests
{
    private static HsnSuggester Suggester(params Item[] catalogue) =>
        new(word => catalogue
            .Where(item => item.Name.Contains(word, StringComparison.OrdinalIgnoreCase))
            .ToList());

    private static Item Sold(string name, string hsn, decimal gst) =>
        Catalogue.Item(id: Math.Abs(name.GetHashCode()) % 10_000, name: name, gstRate: gst) with { HsnCode = hsn };

    // ---- The shop's own catalogue first ----------------------------------------------------------

    [Fact]
    public void ItOffersTheCodeTheShopAlreadyUsesForSomethingSimilar()
    {
        var suggester = Suggester(Sold("Lux Soap 100g", "3401", 18m));

        var suggestion = Assert.IsType<HsnSuggestion>(suggester.For("Hamam Soap 100g").FirstOrDefault());

        Assert.Equal("3401", suggestion.HsnCode);
        Assert.Equal(18m, suggestion.GstRate);
        Assert.True(suggestion.FromOwnCatalogue);

        // And says where it came from, because a number with no basis is a guess.
        Assert.Contains("Lux Soap 100g", suggestion.Reason);
    }

    /// <summary>
    /// The whole point of the precedence rule. A shop selling soap at a rate of its own gets its
    /// own answer, not the one shipped in the table.
    /// </summary>
    [Fact]
    public void ItsOwnCodeOutranksTheShippedTable()
    {
        var suggester = Suggester(Sold("Lux Soap 100g", "3401", 12m));

        var first = suggester.For("Hamam Soap 100g")[0];

        Assert.True(first.FromOwnCatalogue);
        Assert.Equal(12m, first.GstRate);
    }

    /// <summary>
    /// A size is not a description. Without dropping them, two unrelated products match because
    /// both happen to come in 100g.
    /// </summary>
    [Fact]
    public void SizesAndPackCountsDoNotMakeThingsSimilar()
    {
        var suggester = Suggester(Sold("Toor Dal 100g", "0713", 5m));

        var suggestions = suggester.For("Hamam Soap 100g");

        Assert.DoesNotContain(suggestions, s => s.FromOwnCatalogue);
    }

    [Fact]
    public void AnItemWithNoCodeOnItIsNotOffered()
    {
        var suggester = Suggester(Sold("Lux Soap 100g", string.Empty, 18m));

        Assert.DoesNotContain(suggester.For("Hamam Soap 100g"), s => s.FromOwnCatalogue);
    }

    /// <summary>The same code at two rates is two answers, and collapsing them hides one.</summary>
    [Fact]
    public void TheSameCodeAtADifferentRateIsOfferedSeparately()
    {
        var suggester = Suggester(
            Sold("Loose Rice", "1006", 0m),
            Sold("Packed Rice", "1006", 5m));

        var mine = suggester.For("Ponni Rice").Where(s => s.FromOwnCatalogue).ToList();

        Assert.Equal(2, mine.Count);
        Assert.Contains(mine, s => s.GstRate == 0m);
        Assert.Contains(mine, s => s.GstRate == 5m);
    }

    /// <summary>
    /// A catalogue that cannot be read costs a suggestion, not the ability to add an item — the
    /// shopkeeper types the code, which is what they did before this existed.
    /// </summary>
    [Fact]
    public void ACatalogueThatWillNotAnswerStillLeavesTheShippedTable()
    {
        var suggester = new HsnSuggester(_ => throw new InvalidOperationException("the database is locked"));

        var suggestions = suggester.For("Hamam Soap 100g");

        Assert.NotEmpty(suggestions);
        Assert.All(suggestions, s => Assert.False(s.FromOwnCatalogue));
    }

    // ---- The shipped table -----------------------------------------------------------------------

    [Fact]
    public void AnEmptyCatalogueStillGetsSomethingToStartFrom()
    {
        var suggester = Suggester();

        var suggestion = Assert.IsType<HsnSuggestion>(suggester.For("Hamam Soap 100g").FirstOrDefault());

        Assert.Equal("3401", suggestion.HsnCode);
        Assert.False(suggestion.FromOwnCatalogue);
    }

    [Theory]
    [InlineData("Clinic Plus Shampoo 175ml", "3305")]
    [InlineData("Toor Dal 1kg", "0713")]
    [InlineData("Tata Salt 1kg", "2501")]
    [InlineData("Sugar Loose", "1701")]
    [InlineData("Colgate Toothpaste 100g", "3306")]
    [InlineData("Surf Excel Washing Powder", "3402")]
    public void CommonGroceryLinesAreRecognisedByName(string name, string expected)
    {
        Assert.Equal(expected, HsnDirectory.Lookup(name)[0].HsnCode);
    }

    /// <summary>
    /// Where the rate genuinely depends on something the software cannot see, the suggestion says
    /// so instead of picking one and sounding certain.
    /// </summary>
    [Fact]
    public void AStapleCarriesThePackagedOrLooseWarning()
    {
        var suggestion = HsnDirectory.Lookup("Toor Dal 1kg")[0];

        Assert.Contains("loose", suggestion.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pre-packaged", suggestion.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// "Oil" is two different codes depending on whether it is cooked with or put on hair, and
    /// nothing here can know which. Both are offered and the shopkeeper picks.
    /// </summary>
    [Fact]
    public void AnAmbiguousNameOffersBothAnswersRatherThanGuessing()
    {
        var codes = HsnDirectory.Lookup("Coconut Hair Oil 200ml").Select(s => s.HsnCode).ToList();

        Assert.Contains("3305", codes);
        Assert.Contains("1512", codes);
    }

    [Fact]
    public void AnUnknownNameOffersNothingRatherThanSomethingWrong()
    {
        Assert.Empty(HsnDirectory.Lookup("Zxqv Brand Whatnot"));
        Assert.Empty(HsnDirectory.Lookup(string.Empty));
    }

    /// <summary>Every rate in the shipped table is a real slab, or it could never be imported.</summary>
    [Fact]
    public void EveryShippedRateIsAnActualGstSlab()
    {
        Assert.All(
            HsnDirectory.Entries,
            entry => Assert.Contains(entry.GstRate, Pos.Core.Domain.Import.ItemCsvParser.ValidGstRates));
    }

    [Fact]
    public void TheListIsCappedSoItStaysSomethingToReadRatherThanScroll()
    {
        var crowded = Enumerable.Range(1, 20)
            .Select(i => Sold($"Soap Number {i}", $"34{i:D2}", 18m))
            .ToArray();

        Assert.True(Suggester(crowded).For("Hamam Soap").Count <= HsnSuggester.Limit);
    }
}
