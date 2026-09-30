using Pos.Core.Domain;
using Pos.Core.Hardware.Scanning;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>A label off the shop's scale, scanned at the till.</summary>
public class ScaleLabelTillTests
{
    private static BillingHarness Till(ScaleBarcodeFormat format)
    {
        var till = new BillingHarness(
            Catalogue.Item(sku: "123", name: "Onion", price: 40m, unit: UnitType.Kilogram),
            Catalogue.Item(sku: "456", name: "Bath Soap", price: 30m));

        till.ViewModel.ScaleLabels = format;
        return till;
    }

    [Fact]
    public void AWeightLabelGoesOnTheBillAtItsWeight()
    {
        using var till = Till(ScaleBarcodeFormat.Default);

        till.Scan(Barcode.WithCheckDigit("200012301250"));

        var line = Assert.Single(till.ViewModel.Lines);
        Assert.Equal("Onion", line.Name);
        Assert.Equal(1.250m, line.Line.Quantity);
        Assert.Equal(50.00m, till.ViewModel.GrandTotal);
        Assert.Contains("1.25 kg off the scale label", till.ViewModel.StatusMessage);
    }

    [Fact]
    public void APriceLabelComesToThePriceOnTheLabel()
    {
        using var till = Till(new ScaleBarcodeFormat(["21"], Value: ScaleValue.Price, Decimals: 2));

        till.Scan(Barcode.WithCheckDigit("210012304700"));

        Assert.Equal(47.00m, till.ViewModel.GrandTotal);
    }

    [Fact]
    public void ALabelForAnItemTheCatalogueDoesNotHaveIsSaid()
    {
        using var till = Till(ScaleBarcodeFormat.Default);

        till.Scan(Barcode.WithCheckDigit("200099901250"));

        Assert.Empty(till.ViewModel.Lines);
        Assert.Contains("no item has that SKU", till.ViewModel.StatusMessage);
    }

    [Fact]
    public void AWeightLabelForAPieceIsRefused()
    {
        using var till = Till(ScaleBarcodeFormat.Default);

        till.Scan(Barcode.WithCheckDigit("200045601250"));

        Assert.Empty(till.ViewModel.Lines);
        Assert.Contains("not by weight", till.ViewModel.StatusMessage);
    }

    [Fact]
    public void WithScaleLabelsOffAnInStoreCodeIsJustAnUnknownBarcode()
    {
        using var till = Till(ScaleBarcodeFormat.Off);

        till.Scan(Barcode.WithCheckDigit("200012301250"));

        Assert.Empty(till.ViewModel.Lines);
    }
}
