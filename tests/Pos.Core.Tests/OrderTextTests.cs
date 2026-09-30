using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// Orders as people write them into a phone, read into lines the till can look up.
/// </summary>
public class OrderTextTests
{
    [Theory]
    [InlineData("2 kg sugar", "sugar", 2, "kg")]
    [InlineData("sugar 2kg", "sugar", 2, "kg")]
    [InlineData("Sugar - 2 Kgs", "Sugar", 2, "kg")]
    [InlineData("ghee 1/2 kg", "ghee", 0.5, "kg")]
    [InlineData("500g dal", "dal", 500, "g")]
    [InlineData("oil 1 ltr", "oil", 1, "l")]
    [InlineData("toor dal 1kg x 2", "toor dal 1kg", 2, null)]
    [InlineData("2 x toor dal 1kg", "toor dal 1kg", 2, null)]
    [InlineData("3 soap", "soap", 3, null)]
    [InlineData("soap 3", "soap", 3, null)]
    [InlineData("coriander", "coriander", 1, null)]
    public void ALineIsReadAsWhatAndHowMuch(string written, string what, double quantity, string? unit)
    {
        var line = Assert.Single(OrderText.Parse(written));

        Assert.Equal(what, line.What);
        Assert.Equal((decimal)quantity, line.Quantity);
        Assert.Equal(unit, line.Unit);
    }

    /// <summary>A WhatsApp message: a greeting, a numbered list, a bullet, blank lines, thanks.</summary>
    [Fact]
    public void AMessageIsReadLineByLineWithoutTheGreetings()
    {
        var lines = OrderText.Parse("""
            Hi
            1. Toor dal 1kg x 2
            2. sugar 2 kg

            - coriander
            Thanks
            """);

        Assert.Equal(["Toor dal 1kg", "sugar", "coriander"], lines.Select(l => l.What));
    }

    [Fact]
    public void AListTypedOnOneLineIsSplitAtTheCommas() =>
        Assert.Equal(["sugar", "dal", "soap"], OrderText.Parse("sugar 2kg, dal 1kg; soap 3").Select(l => l.What));

    [Fact]
    public void GramsAndMillilitresBecomeKilosAndLitres()
    {
        Assert.Equal(0.5m, new OrderLine("500g dal", "dal", 500m, "g").QuantityFor(UnitType.Kilogram));
        Assert.Equal(0.25m, new OrderLine("250 ml oil", "oil", 250m, "ml").QuantityFor(UnitType.Litre));
        Assert.Equal(2m, new OrderLine("2 kg sugar", "sugar", 2m, "kg").QuantityFor(UnitType.Kilogram));
    }

    // ---- Finding the item ------------------------------------------------------------------------

    private static readonly Item Sugar = Catalogue.Item(sku: "SUG", name: "Sugar Loose", price: 45m, unit: UnitType.Kilogram);
    private static readonly Item SoapSmall = Catalogue.Item(sku: "SOAP75", name: "Bath Soap 75g", price: 20m);
    private static readonly Item SoapBig = Catalogue.Item(sku: "SOAP100", name: "Bath Soap 100g", price: 30m);

    private static IReadOnlyList<Item> Search(string text) =>
        new[] { Sugar, SoapSmall, SoapBig }.Where(i => i.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();

    [Fact]
    public void AWeightForSomethingSoldByWeightIsTheQuantity()
    {
        var (item, quantity) = OrderText.Resolve(OrderText.Parse("500g sugar")[0], Search)!.Value;

        Assert.Equal(Sugar, item);
        Assert.Equal(0.5m, quantity);
    }

    /// <summary>"bath soap 100g" is one soap of the 100g kind, not a hundred soaps.</summary>
    [Fact]
    public void AWeightAgainstAPacketIsItsSize()
    {
        var (item, quantity) = OrderText.Resolve(OrderText.Parse("bath soap 100g")[0], Search)!.Value;

        Assert.Equal(SoapBig, item);
        Assert.Equal(1m, quantity);
    }

    [Fact]
    public void APacketIsCountedWhole()
    {
        var (_, quantity) = OrderText.Resolve(new OrderLine("soap 2.5", "soap", 2.5m, null), Search)!.Value;

        Assert.Equal(3m, quantity);
    }

    [Fact]
    public void SomethingTheShopDoesNotSellIsNotFound() =>
        Assert.Null(OrderText.Resolve(OrderText.Parse("2 kg mangoes")[0], Search));
}
