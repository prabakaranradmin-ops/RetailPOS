using Pos.Core.Configuration;
using Pos.Core.Domain;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// Which build this is, and what that decides.
/// </summary>
/// <remarks>
/// Two builds go out from one codebase: one that charges GST and one that does not. The difference
/// is stamped into the executable rather than kept in a settings file, because a settings file can
/// be copied from the wrong lane, edited by the wrong person, or never copied at all — and the
/// question it answers is which legal document the shop issues.
///
/// The test assembly carries no stamp, so <see cref="ProductVariant.Current"/> reads as
/// <see cref="Variant.Gst"/> here. That is itself the behaviour worth pinning: an unstamped or
/// unrecognised build must fall back to charging GST.
/// </remarks>
public class ProductVariantTests
{
    /// <summary>
    /// The safer way to be wrong. A shop that charges GST and gets a tax invoice is correct; one
    /// that does not and gets a tax invoice has issued a document claiming tax it never collected.
    /// </summary>
    [Fact]
    public void AnUnstampedBuildChargesGst()
    {
        Assert.Equal(Variant.Gst, ProductVariant.Current);
        Assert.False(ProductVariant.ChargesNoTax);
    }

    /// <summary>On the GST build the shop decides, and either answer is honoured.</summary>
    [Theory]
    [InlineData(TaxMode.Gst, TaxMode.Gst)]
    [InlineData(TaxMode.Composition, TaxMode.Composition)]
    public void AGstBuildUsesWhateverTheSettingsAskFor(TaxMode configured, TaxMode expected)
    {
        Assert.Equal(expected, ProductVariant.Resolve(configured, Variant.Gst));
    }

    /// <summary>
    /// The rule the no-tax build exists for: a settings file carried over from a GST lane cannot
    /// make it start issuing tax invoices.
    /// </summary>
    [Theory]
    [InlineData(TaxMode.Gst)]
    [InlineData(TaxMode.Composition)]
    public void ANoTaxBuildIssuesABillOfSupplyWhateverTheSettingsSay(TaxMode configured)
    {
        Assert.Equal(TaxMode.Composition, ProductVariant.Resolve(configured, Variant.NoTax));
    }

    /// <summary>The unstamped default flows through the parameterless overload too.</summary>
    [Fact]
    public void TheOverloadWithoutAVariantUsesTheBuildsOwn()
    {
        Assert.Equal(TaxMode.Gst, ProductVariant.Resolve(TaxMode.Gst));
        Assert.Equal(ProductVariant.Describe(Variant.Gst), ProductVariant.Description);
    }

    [Fact]
    public void EachBuildDescribesItselfInWordsAScreenCanShow()
    {
        Assert.Contains("TAX INVOICE", ProductVariant.Describe(Variant.Gst));
        Assert.Contains("BILL OF SUPPLY", ProductVariant.Describe(Variant.NoTax));
    }
}
