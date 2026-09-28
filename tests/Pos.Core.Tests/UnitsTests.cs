using Pos.Core.Domain;
using Pos.Core.Domain.Printing;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// The units a shop sells in: every one named, stored by a number that never moves, read back from
/// any spelling a catalogue uses, and allowing a fraction only where a customer can buy part of one.
/// </summary>
public class UnitsTests
{
    [Fact]
    public void EveryUnitHasExactlyOneRow()
    {
        var types = Enum.GetValues<UnitType>();

        Assert.Equal(types.Length, Units.All.Count);
        Assert.Equal(types.OrderBy(t => t), Units.All.Select(u => u.Type).OrderBy(t => t));
        Assert.All(Units.All, u =>
        {
            Assert.False(string.IsNullOrWhiteSpace(u.Code));
            Assert.False(string.IsNullOrWhiteSpace(u.Tamil));
            Assert.False(string.IsNullOrWhiteSpace(u.Meaning));
        });
    }

    /// <summary>
    /// The number is what the database holds. Renumbering would turn every stored kilo of sugar into
    /// something else, so the numbers are pinned here rather than trusted to declaration order.
    /// </summary>
    [Theory]
    [InlineData(UnitType.Each, 0)]
    [InlineData(UnitType.Kilogram, 1)]
    [InlineData(UnitType.Litre, 2)]
    [InlineData(UnitType.Metre, 3)]
    [InlineData(UnitType.Seepu, 4)]
    [InlineData(UnitType.Kattu, 8)]
    [InlineData(UnitType.Saram, 21)]
    [InlineData(UnitType.Padi, 28)]
    [InlineData(UnitType.Muzham, 35)]
    [InlineData(UnitType.Panthu, 38)]
    public void StoredNumbersNeverMove(UnitType unit, int stored) => Assert.Equal(stored, (int)unit);

    [Theory]
    [InlineData("Pcs", UnitType.Each)]
    [InlineData("nos", UnitType.Each)]
    [InlineData("KG", UnitType.Kilogram)]
    [InlineData("கிலோ", UnitType.Kilogram)]
    [InlineData("ltr", UnitType.Litre)]
    [InlineData("seepu", UnitType.Seepu)]
    [InlineData("SEEPU", UnitType.Seepu)]
    [InlineData("சீப்பு", UnitType.Seepu)]
    [InlineData(" Kattu ", UnitType.Kattu)]
    [InlineData("pathai", UnitType.Keetru)]
    [InlineData("பத்தை", UnitType.Keetru)]
    [InlineData("sottu", UnitType.Thuli)]
    [InlineData("arai padi", UnitType.AraiPadi)]
    [InlineData("seru", UnitType.AraiPadi)]
    [InlineData("kuruni", UnitType.Marakkaal)]
    [InlineData("முழம்", UnitType.Muzham)]
    [InlineData("muzhusu", UnitType.Muzhu)]
    [InlineData("kavuli", UnitType.Kavuli)]
    public void AUnitIsReadFromAnySpellingItIsWrittenIn(string text, UnitType expected)
    {
        Assert.True(Units.TryParse(text, out var unit));
        Assert.Equal(expected, unit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bunch")]
    [InlineData("dozen")]
    public void AnythingElseIsNotAUnit(string text) => Assert.False(Units.TryParse(text, out _));

    [Fact]
    public void EveryRowReadsBackFromItsOwnNames()
    {
        foreach (var unit in Units.All)
        {
            Assert.True(Units.TryParse(unit.Code, out var byCode), unit.Code);
            Assert.Equal(unit.Type, byCode);

            Assert.True(Units.TryParse(unit.Tamil, out var byTamil), unit.Tamil);
            Assert.Equal(unit.Type, byTamil);

            foreach (var alias in unit.Aliases)
            {
                Assert.True(Units.TryParse(alias, out var byAlias), alias);
                Assert.Equal(unit.Type, byAlias);
            }
        }
    }

    [Theory]
    [InlineData(UnitType.Kilogram, true)]
    [InlineData(UnitType.Padi, true)]
    [InlineData(UnitType.AraiPadi, true)]
    [InlineData(UnitType.Muzham, true)]
    [InlineData(UnitType.Veesai, true)]
    [InlineData(UnitType.Each, false)]
    [InlineData(UnitType.Seepu, false)]
    [InlineData(UnitType.Kattu, false)]
    [InlineData(UnitType.Saram, false)]
    [InlineData(UnitType.Moottai, false)]
    public void OnlyWhatCanBeSoldInPartTakesAFraction(UnitType unit, bool fractional) =>
        Assert.Equal(fractional, unit.AllowsFractionalQuantity());

    [Fact]
    public void HalfACombOfBananasIsRefusedAtTheLine()
    {
        var line = Line(UnitType.Seepu, quantity: 1m);

        var refused = Assert.Throws<ArgumentOutOfRangeException>(() => line.Quantity = 1.5m);
        Assert.Contains("whole seepu", refused.Message);
    }

    [Fact]
    public void AMuzhamAndAHalfOfJasmineIsALine()
    {
        var line = Line(UnitType.Muzham, quantity: 1m);

        line.Quantity = 1.5m;

        Assert.Equal(1.5m, line.Quantity);
    }

    /// <summary>
    /// A unit changes what the bill says, never what it charges. The same price, quantity and rate
    /// in seepu as in pieces must come to the same rupee and the same paisa of tax.
    /// </summary>
    [Fact]
    public void TheUnitNeverChangesThePriceOrTheTax()
    {
        var inPieces = Line(UnitType.Each, quantity: 2m, price: 60m, gst: 5m);
        var inSeepu = Line(UnitType.Seepu, quantity: 2m, price: 60m, gst: 5m);

        Assert.Equal(120.00m, inSeepu.LineTotal);
        Assert.Equal(inPieces.LineTotal, inSeepu.LineTotal);
        Assert.Equal(inPieces.Tax, inSeepu.Tax);
    }

    [Fact]
    public void ANilRatedLineInATraditionalUnitCarriesNoTax()
    {
        var jasmine = Line(UnitType.Muzham, quantity: 2.5m, price: 30m, gst: 0m);

        Assert.Equal(75.00m, jasmine.LineTotal);
        Assert.Equal(0m, jasmine.Tax.SplitTax);
    }

    [Theory]
    [InlineData(2, UnitType.Seepu, ReceiptLanguage.Tamil, "2 சீப்பு")]
    [InlineData(2, UnitType.Seepu, ReceiptLanguage.English, "2 Seepu")]
    [InlineData(1.5, UnitType.Muzham, ReceiptLanguage.Tamil, "1.5 முழம்")]
    [InlineData(2.75, UnitType.Kilogram, ReceiptLanguage.Tamil, "2.75 Kg")]
    [InlineData(3, UnitType.Each, ReceiptLanguage.English, "3 Pcs")]
    [InlineData(0.5, UnitType.Padi, ReceiptLanguage.English, "0.5 Padi")]
    public void TheQuantityPrintsWithItsUnit(double quantity, UnitType unit, ReceiptLanguage language, string expected) =>
        Assert.Equal(expected, Units.WithQuantity((decimal)quantity, unit, language));

    /// <summary>
    /// A database written by a later build may hold a unit this one has never heard of. It still
    /// has to open and reprint that bill rather than refuse it.
    /// </summary>
    [Fact]
    public void AUnitFromALaterBuildIsShownByNumberRatherThanRefused()
    {
        var unknown = (UnitType)99;

        Assert.Equal("99", Units.Of(unknown).Code);
        Assert.True(unknown.AllowsFractionalQuantity());
        Assert.Equal("1 99", Units.WithQuantity(1m, unknown, ReceiptLanguage.English));
    }

    private static InvoiceLine Line(UnitType unit, decimal quantity, decimal price = 30m, decimal gst = 0m) =>
        InvoiceLine.Rehydrate(1, "Test", "0000", null, null, unit, price, price, true, gst, quantity, 0m, false);
}
