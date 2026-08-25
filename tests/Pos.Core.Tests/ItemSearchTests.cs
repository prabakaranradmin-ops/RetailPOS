using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// SRS 2.1: one box, match priority exact barcode then SKU prefix then name substring, active
/// items only, capped result count.
/// </summary>
public class ItemSearchTests
{
    private static ItemRepository Seed(TempDatabase temp, params Item[] items)
    {
        temp.Items.AddRange(items);
        return temp.Items;
    }

    [Fact]
    public void AnExactBarcodeWinsOverEverythingElse()
    {
        using var temp = new TempDatabase();
        var items = Seed(temp,
            Catalogue.Item(sku: "A1", barcode: "8901234567890", name: "Toor Dal 1kg"),
            // Deliberately named after the barcode, so a name match would outrank it if priority
            // were not enforced.
            Catalogue.Item(sku: "A2", barcode: "8909999999999", name: "8901234567890 lookalike"));

        var results = items.Search("8901234567890");

        Assert.Single(results);
        Assert.Equal("A1", results[0].Sku);
    }

    [Fact]
    public void SkuPrefixOutranksNameSubstring()
    {
        using var temp = new TempDatabase();
        var items = Seed(temp,
            Catalogue.Item(sku: "DAL500", name: "Chana Whole 500g"),
            Catalogue.Item(sku: "ZZZ001", name: "Toor Dal 1kg"));

        var results = items.Search("DAL");

        Assert.Equal(2, results.Count);
        Assert.Equal("DAL500", results[0].Sku);
        Assert.Equal("ZZZ001", results[1].Sku);
    }

    [Fact]
    public void NameMatchesOnASubstringNotJustAPrefix()
    {
        using var temp = new TempDatabase();
        var items = Seed(temp, Catalogue.Item(sku: "A1", name: "Premium Toor Dal 1kg"));

        Assert.Single(items.Search("Toor"));
        Assert.Single(items.Search("Dal"));
    }

    [Fact]
    public void SearchIsCaseInsensitive()
    {
        using var temp = new TempDatabase();
        var items = Seed(temp, Catalogue.Item(sku: "A1", name: "Toor Dal 1kg"));

        Assert.Single(items.Search("toor"));
        Assert.Single(items.Search("TOOR"));
    }

    [Fact]
    public void InactiveItemsNeverAppear()
    {
        using var temp = new TempDatabase();
        var items = Seed(temp,
            Catalogue.Item(sku: "A1", barcode: "8901234567890", name: "Toor Dal 1kg", active: false));

        Assert.Empty(items.Search("Toor"));
        Assert.Empty(items.Search("8901234567890"));
        Assert.Null(items.FindByBarcode("8901234567890"));
    }

    [Fact]
    public void ResultsAreCapped()
    {
        using var temp = new TempDatabase();
        temp.Items.AddRange(Catalogue.Generate(500));

        var results = temp.Items.Search("Dal", limit: 10);

        Assert.Equal(10, results.Count);
    }

    [Fact]
    public void AnEmptyOrBlankQueryReturnsNothing()
    {
        using var temp = new TempDatabase();
        Seed(temp, Catalogue.Item(sku: "A1", name: "Toor Dal 1kg"));

        Assert.Empty(temp.Items.Search(""));
        Assert.Empty(temp.Items.Search("   "));
        Assert.Null(temp.Items.FindByBarcode(""));
    }

    /// <summary>
    /// A stray wildcard character must be searched for literally, not turned into a query that
    /// matches the whole catalogue.
    /// </summary>
    [Fact]
    public void LikeWildcardsInTheQueryAreTreatedAsText()
    {
        using var temp = new TempDatabase();
        var items = Seed(temp,
            Catalogue.Item(sku: "A1", name: "Toor Dal 1kg"),
            Catalogue.Item(sku: "A2", name: "Discount 50% Pack"));

        // A bare '%' finds the one item whose name really contains a percent sign, rather than
        // matching the whole catalogue as an unescaped wildcard would.
        var percent = items.Search("%");
        Assert.Single(percent);
        Assert.Equal("A2", percent[0].Sku);

        // Nothing contains an underscore, so the single-character wildcard matches nothing.
        Assert.Empty(items.Search("_"));

        var literal = items.Search("50%");
        Assert.Single(literal);
        Assert.Equal("A2", literal[0].Sku);
    }

    [Fact]
    public void FindByBarcodeReturnsNothingWhenNoItemCarriesIt()
    {
        using var temp = new TempDatabase();
        Seed(temp, Catalogue.Item(sku: "A1", barcode: "8901234567890"));

        Assert.Null(temp.Items.FindByBarcode("8909999999999"));
    }

    [Fact]
    public void ABarcodeSearchIgnoresSurroundingWhitespace()
    {
        using var temp = new TempDatabase();
        Seed(temp, Catalogue.Item(sku: "A1", barcode: "8901234567890"));

        Assert.NotNull(temp.Items.FindByBarcode("  8901234567890  "));
        Assert.Single(temp.Items.Search(" 8901234567890 "));
    }

    [Fact]
    public void ANonPositiveLimitIsRejected()
    {
        using var temp = new TempDatabase();

        Assert.Throws<ArgumentOutOfRangeException>(() => temp.Items.Search("dal", limit: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => temp.Items.Search("dal", limit: -1));
    }
}
