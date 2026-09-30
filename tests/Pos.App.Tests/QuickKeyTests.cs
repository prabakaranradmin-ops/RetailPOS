using System.Windows.Input;
using Pos.App.ViewModels;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// Quick keys for loose produce, with F11 - the loose items, most often sold first, a key each.
/// </summary>
public class QuickKeyTests
{
    private static BillingHarness Till() => new(
        Catalogue.Item(sku: "ONION", name: "Onion", price: 40m, unit: UnitType.Kilogram),
        Catalogue.Item(sku: "CORIANDER", name: "Coriander bunch", price: 10m, unit: UnitType.Kattu),
        Catalogue.Item(sku: "JASMINE", name: "Jasmine", price: 30m, unit: UnitType.Muzham),
        Catalogue.Item(sku: "DAL001", barcode: "8901234567890", name: "Toor Dal 1kg", price: 189m));

    private static void Type(BillingHarness till, string text) => till.ViewModel.EditBuffer = text;

    [Fact]
    public void F11ListsTheLooseItemsAndNotThePacketsWithBarcodes()
    {
        using var till = Till();

        Assert.True(till.Press(Key.F11));

        Assert.True(till.ViewModel.IsUsingQuickKeys);
        Assert.Equal(3, till.ViewModel.QuickKeyTiles.Count);
        Assert.DoesNotContain(till.ViewModel.QuickKeyTiles, k => k.Name == "Toor Dal 1kg");
        Assert.Equal(["1", "2", "3"], till.ViewModel.QuickKeyTiles.Select(k => k.Key));
    }

    /// <summary>One keystroke picks, a weight and Enter adds: 1.25 kg of onions at 40 is 50.</summary>
    [Fact]
    public void AKeyAndAWeightPutItOnTheBill()
    {
        using var till = Till();
        till.Press(Key.F11);
        var onion = till.ViewModel.QuickKeyTiles.Single(k => k.Name == "Onion").Key;

        Type(till, onion);
        Assert.Equal(QuickKeyStage.Quantity, till.ViewModel.QuickKeyStage);
        Assert.Contains("how many kg", till.ViewModel.QuickKeyPrompt);

        Type(till, "1.25");
        till.Press(Key.Enter);

        Assert.False(till.ViewModel.IsUsingQuickKeys);
        var line = Assert.Single(till.ViewModel.Lines);
        Assert.Equal("Onion", line.Name);
        Assert.Equal(1.25m, line.Line.Quantity);
        Assert.Equal(50.00m, till.ViewModel.GrandTotal);
    }

    [Fact]
    public void EnterOnAnEmptyBoxIsOne()
    {
        using var till = Till();
        till.Press(Key.F11);

        Type(till, till.ViewModel.QuickKeyTiles.Single(k => k.Name == "Coriander bunch").Key);
        till.Press(Key.Enter);

        Assert.Equal(1m, Assert.Single(till.ViewModel.Lines).Line.Quantity);
    }

    [Fact]
    public void TheArrowsAndEnterPickToo()
    {
        using var till = Till();
        till.Press(Key.F11);

        till.Press(Key.Down);
        var picked = till.ViewModel.QuickKeyTiles[till.ViewModel.SelectedQuickKeyIndex].Name;
        till.Press(Key.Enter);
        till.Press(Key.Enter);

        Assert.Equal(picked, Assert.Single(till.ViewModel.Lines).Name);
    }

    [Fact]
    public void SomethingSoldWholeIsNotSoldInParts()
    {
        using var till = Till();
        till.Press(Key.F11);

        Type(till, till.ViewModel.QuickKeyTiles.Single(k => k.Name == "Coriander bunch").Key);
        Type(till, "1.5");
        till.Press(Key.Enter);

        Assert.True(till.ViewModel.IsUsingQuickKeys);
        Assert.Empty(till.ViewModel.Lines);
        Assert.Contains("sold whole", till.ViewModel.StatusMessage);
    }

    [Fact]
    public void AKeyWithNothingOnItIsSaid()
    {
        using var till = Till();
        till.Press(Key.F11);

        Type(till, "Z");

        Assert.Equal(QuickKeyStage.Pick, till.ViewModel.QuickKeyStage);
        Assert.Contains("No quick key 'Z'", till.ViewModel.StatusMessage);
        Assert.Equal(string.Empty, till.ViewModel.EditBuffer);
    }

    [Fact]
    public void EscapeGoesBackAStepThenToTheBill()
    {
        using var till = Till();
        till.Press(Key.F11);
        Type(till, "1");

        till.Press(Key.Escape);
        Assert.Equal(QuickKeyStage.Pick, till.ViewModel.QuickKeyStage);

        till.Press(Key.Escape);
        Assert.False(till.ViewModel.IsUsingQuickKeys);
        Assert.Empty(till.ViewModel.Lines);
    }

    /// <summary>What sold most in the last four weeks has the first key.</summary>
    [Fact]
    public void TheBusiestItemHasTheFirstKey()
    {
        using var till = Till();

        for (var i = 0; i < 2; i++)
        {
            till.Press(Key.F11);
            Type(till, till.ViewModel.QuickKeyTiles.Single(k => k.Name == "Jasmine").Key);
            till.Press(Key.Enter);
            till.Press(Key.F12);
            till.Press(Key.Enter);
            till.Press(Key.Enter);
        }

        till.Press(Key.F11);

        Assert.Equal("Jasmine", till.ViewModel.QuickKeyTiles[0].Name);
        Assert.Equal("1", till.ViewModel.QuickKeyTiles[0].Key);
    }

    [Fact]
    public void AnOpenBillStaysAndGetsTheLooseItemToo()
    {
        using var till = Till();
        till.Scan("8901234567890");

        till.Press(Key.F11);
        Type(till, "1");
        till.Press(Key.Enter);

        Assert.Equal(2, till.ViewModel.Lines.Count);
    }

    [Fact]
    public void AShopWithNothingLooseIsToldWhatGetsAKey()
    {
        using var till = new BillingHarness(Catalogue.Item(sku: "DAL001", barcode: "8901234567890"));

        till.Press(Key.F11);

        Assert.False(till.ViewModel.IsUsingQuickKeys);
        Assert.Contains("An item with no barcode", till.ViewModel.StatusMessage);
    }
}
