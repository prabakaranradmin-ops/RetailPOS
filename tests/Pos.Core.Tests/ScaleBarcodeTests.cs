using Pos.Core.Domain;
using Pos.Core.Hardware.Scanning;
using Pos.TestSupport;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// Labels from the shop's own scale: an in-store EAN-13 carrying the item's code and its weight or
/// price.
/// </summary>
public class ScaleBarcodeTests
{
    private static readonly ScaleBarcodeFormat ByWeight = ScaleBarcodeFormat.Default;
    private static readonly ScaleBarcodeFormat ByPrice = new(["21"], Value: ScaleValue.Price, Decimals: 2);

    private static string Label(string twelve) => Barcode.WithCheckDigit(twelve);

    [Fact]
    public void AWeightLabelIsReadAsTheItemAndItsWeight()
    {
        var label = ByWeight.Read(Label("200012301250"))!;

        Assert.Equal("00123", label.ItemCode);
        Assert.Equal("123", label.ShortItemCode);
        Assert.Equal(ScaleValue.Weight, label.Kind);
        Assert.Equal(1.250m, label.Value);
    }

    [Fact]
    public void APriceLabelIsReadAsTheItemAndItsPrice()
    {
        var label = ByPrice.Read(Label("210045604500"))!;

        Assert.Equal("00456", label.ItemCode);
        Assert.Equal(ScaleValue.Price, label.Kind);
        Assert.Equal(45.00m, label.Value);
    }

    [Theory]
    [InlineData("8901234567890")]  // a product's own barcode, not an in-store one
    [InlineData("20001230125")]    // too short
    [InlineData("SUG001")]
    public void AnythingElseIsNotAScaleLabel(string code) => Assert.Null(ByWeight.Read(code));

    [Fact]
    public void AMisreadCheckDigitIsNotGuessedAt()
    {
        var good = Label("200012301250");
        var bad = good[..12] + (char)('0' + ((good[12] - '0' + 1) % 10));

        Assert.Null(ByWeight.Read(bad));
    }

    [Fact]
    public void ALaneWithScaleLabelsOffReadsNone() =>
        Assert.Null(ScaleBarcodeFormat.Off.Read(Label("200012301250")));

    [Fact]
    public void AOneDigitPrefixWithASixDigitItemCodeIsALayoutToo()
    {
        var format = new ScaleBarcodeFormat(["2"], ItemDigits: 6);

        Assert.Null(format.Problem());
        Assert.Equal("001234", format.Read(Label("200123401250"))!.ItemCode);
    }

    [Theory]
    [InlineData(new[] { "20", "2" }, 5, 5, 3, "same length")]
    [InlineData(new[] { "2x" }, 5, 5, 3, "digits only")]
    [InlineData(new[] { "20" }, 5, 4, 3, "do not make an EAN-13")]
    [InlineData(new[] { "20" }, 5, 5, 4, "0 to 3 decimal places")]
    public void ALayoutThatCannotBeRightIsSaid(string[] prefixes, int item, int value, int decimals, string says) =>
        Assert.Contains(says, new ScaleBarcodeFormat(prefixes, item, value, ScaleValue.Weight, decimals).Problem());

    // ---- Onto the bill ---------------------------------------------------------------------------

    private static Item Onion(decimal price = 40m) => Catalogue.Item(sku: "123", name: "Onion", price: price, unit: UnitType.Kilogram);

    [Fact]
    public void AWeightIsTheQuantity()
    {
        var how = ByWeight.Read(Label("200012301250"))!.ForItem(Onion(), out var problem);

        Assert.Null(problem);
        Assert.Equal((1.250m, 0m), how);
    }

    [Fact]
    public void APriceThatDividesExactlyIsTheQuantity()
    {
        var how = ByPrice.Read(Label("210012304500"))!.ForItem(Onion(40m), out _);

        Assert.Equal((1.125m, 0m), how);
    }

    /// <summary>
    /// 45.00 of onions at 33.33 a kilo is 1.35013 kg. Rounded up to 1.351 kg it comes to 45.03, and the
    /// 0.03 comes off as a discount: the customer pays the label.
    /// </summary>
    [Fact]
    public void APriceThatDoesNotDivideIsRoundedUpToAGramAndTheRestDiscounted()
    {
        var how = ByPrice.Read(Label("210012304500"))!.ForItem(Onion(33.33m), out _)!.Value;

        Assert.Equal(1.351m, how.Quantity);
        Assert.Equal(0.03m, how.Discount);

        var bill = new InvoiceEngine("33");
        bill.AddItem(Onion(33.33m), how.Quantity);
        bill.SetDiscount(0, how.Discount);

        Assert.Equal(45.00m, bill.Totals.GrandTotal);
    }

    [Fact]
    public void AWeightForSomethingSoldByThePieceIsRefused()
    {
        var soap = Catalogue.Item(sku: "123", name: "Bath Soap", price: 30m);

        Assert.Null(ByWeight.Read(Label("200012301250"))!.ForItem(soap, out var problem));
        Assert.Contains("not by weight", problem);
    }

    [Fact]
    public void APriceForPiecesMustBeAWholeNumberOfThem()
    {
        var soap = Catalogue.Item(sku: "123", name: "Bath Soap", price: 30m);

        Assert.Equal((3m, 0m), ByPrice.Read(Label("210012309000"))!.ForItem(soap, out _));
        Assert.Null(ByPrice.Read(Label("210012304500"))!.ForItem(soap, out var problem));
        Assert.Contains("not a whole number", problem);
    }

    [Fact]
    public void NothingWeighedIsNothingSold()
    {
        Assert.Null(ByWeight.Read(Label("200012300000"))!.ForItem(Onion(), out var problem));
        Assert.Contains("nothing was weighed", problem);
    }
}
