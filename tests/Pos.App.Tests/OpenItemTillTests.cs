using System.Windows.Input;
using Pos.App.ViewModels;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// Ctrl+I at the till: an item on the shelf but not in the catalogue, sold by typing what it is, its
/// price and its slab - every step by keystroke - and left on the owner's list.
/// </summary>
public class OpenItemTillTests
{
    private const string Dal = "8901234567890";

    private static BillingHarness Till() =>
        new(Catalogue.Item(sku: "DAL001", barcode: Dal, name: "Toor Dal 1kg", price: 189m),
            Catalogue.Item(sku: "LUX100", barcode: "8901030865012", name: "Lux Soap 100g", price: 38m, gstRate: 5m, hsn: "3401"));

    private static void Open(BillingHarness till) => till.Press(Key.I, ModifierKeys.Control);

    private static void Type(BillingHarness till, string text)
    {
        till.ViewModel.EditBuffer = text;
        till.Press(Key.Enter);
    }

    /// <summary>Ctrl+I, the name, the price, and down to the slab with no HSN code at <paramref name="rate"/>.</summary>
    private static void Sell(BillingHarness till, string name, string price, decimal rate)
    {
        Open(till);
        Type(till, name);
        Type(till, price);

        while (till.ViewModel.SelectedOpenRateIndex < 0
               || till.ViewModel.OpenItemRates[till.ViewModel.SelectedOpenRateIndex] is not { Hsn.Length: 0 } plain
               || plain.Rate != rate)
        {
            till.Press(Key.Down);
        }

        till.Press(Key.Enter);
    }

    private static void Pay(BillingHarness till)
    {
        till.Press(Key.F12);
        till.Press(Key.Enter);
        till.Press(Key.Enter);
    }

    // ---- The three steps ------------------------------------------------------------------------

    [Fact]
    public void CtrlIAsksWhatItIsThenItsPriceThenItsSlab()
    {
        using var till = Till();

        Open(till);

        Assert.True(till.ViewModel.IsAddingOpenItem);
        Assert.True(till.ViewModel.IsTypingOpenItem);
        Assert.Equal(OpenItemStage.Name, till.ViewModel.OpenItemStage);

        Type(till, "Mosquito coil");
        Assert.Equal(OpenItemStage.Price, till.ViewModel.OpenItemStage);
        Assert.Equal("Mosquito coil", till.ViewModel.OpenItemSoFar);
        Assert.Equal(string.Empty, till.ViewModel.EditBuffer);

        Type(till, "45");
        Assert.True(till.ViewModel.IsChoosingOpenRate);
        Assert.False(till.ViewModel.IsTypingOpenItem);
        Assert.Equal("Mosquito coil  ·  ₹45.00", till.ViewModel.OpenItemSoFar);

        // Nothing picked: the first Down is a choice made by looking.
        Assert.Equal(-1, till.ViewModel.SelectedOpenRateIndex);
        Assert.Empty(till.ViewModel.Lines);
    }

    [Fact]
    public void ThePlainSlabsAreThoseInForceWithNoCode()
    {
        using var till = Till();
        Open(till);
        Type(till, "Mosquito coil");
        Type(till, "45");

        var plain = till.ViewModel.OpenItemRates.Where(r => r.Hsn.Length == 0).ToList();

        Assert.Equal([0m, 5m, 18m, 40m], plain.Select(r => r.Rate));
        Assert.Equal("18%  ·  no HSN code", plain[2].Label);
    }

    [Fact]
    public void EnterBeforeASlabIsPickedPicksNothing()
    {
        using var till = Till();
        Open(till);
        Type(till, "Mosquito coil");
        Type(till, "45");

        till.Press(Key.Enter);

        Assert.True(till.ViewModel.IsChoosingOpenRate);
        Assert.Empty(till.ViewModel.Lines);
        Assert.Equal("Pick its GST slab with ↓ first. The till does not guess the tax.", till.ViewModel.StatusMessage);
    }

    [Fact]
    public void TheSlabPickedGoesOnTheBillAtThePriceTyped()
    {
        using var till = Till();

        Sell(till, "Mosquito coil", "45", 18m);

        Assert.False(till.ViewModel.IsAddingOpenItem);

        var line = Assert.Single(till.ViewModel.Lines);
        Assert.True(line.Line.IsOpen());
        Assert.Equal("Mosquito coil", line.Name);
        Assert.Equal(45m, line.Mrp);
        Assert.Equal(45m, line.LineTotal);
        Assert.Equal(18m, line.Line.GstRate);
        Assert.Equal(string.Empty, line.Hsn);
        Assert.Equal(1m, line.Quantity);

        // ₹45 at 18% inclusive: ₹38.1356 before tax, ₹6.8644 of tax, split ₹3.43 and ₹3.43.
        Assert.Equal(38.1356m, line.Line.Tax.TaxableValue);
        Assert.Equal(6.8644m, line.Line.Tax.TotalTax);
        Assert.Equal(3.43m, line.Line.Tax.Cgst);
        Assert.Equal(3.43m, line.Line.Tax.Sgst);

        Assert.Equal("Mosquito coil added at 18% GST. It is not in the catalogue, so the owner will see it to add.", till.ViewModel.StatusMessage);
    }

    [Fact]
    public void TheArrowsWalkTheSlabsAndStopAtTheEnds()
    {
        using var till = Till();
        Open(till);
        Type(till, "Mosquito coil");
        Type(till, "45");
        var last = till.ViewModel.OpenItemRates.Count - 1;

        // Up from nothing lands on the last, not past it.
        till.Press(Key.Up);
        Assert.Equal(last, till.ViewModel.SelectedOpenRateIndex);
        till.Press(Key.Down);
        Assert.Equal(last, till.ViewModel.SelectedOpenRateIndex);

        for (var i = 0; i <= last + 1; i++)
            till.Press(Key.Up);

        Assert.Equal(0, till.ViewModel.SelectedOpenRateIndex);
    }

    [Fact]
    public void ItCanBeSoldMoreThanOnceAndChangedLikeAnyLine()
    {
        using var till = Till();

        Sell(till, "Mosquito coil", "45", 18m);
        till.Press(Key.Add);
        Sell(till, "Agarbathi", "30", 5m);

        Assert.Equal(2, till.ViewModel.Lines.Count);
        Assert.Equal(2m, till.ViewModel.Lines[0].Quantity);
        Assert.Equal(120m, till.ViewModel.Lines.Sum(l => l.LineTotal));
    }

    // ---- What is carried in ---------------------------------------------------------------------

    [Fact]
    public void WordsThatFoundNothingBecomeItsName()
    {
        using var till = Till();

        till.TypeAndWait("mosquito coil");
        Open(till);

        Assert.Equal("mosquito coil", till.ViewModel.EditBuffer);
        Assert.Equal(string.Empty, till.ViewModel.SearchText);
    }

    [Fact]
    public void AScanThatMatchedNothingIsKeptAsItsBarcode()
    {
        using var till = Till();

        till.Scan("8901234599990");
        Assert.Contains("Ctrl+I sells it anyway", till.ViewModel.StatusMessage);

        Open(till);

        Assert.Equal(string.Empty, till.ViewModel.EditBuffer);
        Assert.Equal("Barcode 8901234599990, not in the catalogue", till.ViewModel.OpenItemSoFar);

        Type(till, "Mosquito coil");
        Type(till, "45");
        till.Press(Key.Down);
        till.Press(Key.Enter);

        Assert.Equal("8901234599990", Assert.Single(till.ViewModel.Lines).Line.BarcodeSnapshot);
    }

    [Fact]
    public void WhatTheShopAlreadySellsSuggestsTheCodeAndSlabFirst()
    {
        using var till = Till();
        Open(till);
        Type(till, "Hamam Soap 100g");
        Type(till, "42");

        var first = till.ViewModel.OpenItemRates[0];
        Assert.Equal(5m, first.Rate);
        Assert.Equal("3401", first.Hsn);
        Assert.Equal("5%  ·  HSN 3401  ·  your Lux Soap 100g is 3401 at 5%", first.Label);

        till.Press(Key.Down);
        till.Press(Key.Enter);

        var line = Assert.Single(till.ViewModel.Lines);
        Assert.Equal("3401", line.Hsn);
        Assert.Equal(5m, line.Line.GstRate);
    }

    // ---- What is refused ------------------------------------------------------------------------

    [Fact]
    public void ANameOfOneLetterIsRefused()
    {
        using var till = Till();
        Open(till);

        Type(till, "x");

        Assert.Equal(OpenItemStage.Name, till.ViewModel.OpenItemStage);
        Assert.Equal("Type what it is, so the bill says and the owner knows what to add.", till.ViewModel.StatusMessage);
    }

    [Theory]
    [InlineData("", "Type its price first.")]
    [InlineData("forty", "'forty' is not a price.")]
    [InlineData("0", "The price has to be more than nothing.")]
    [InlineData("12.345", "A price goes to the paisa: two places after the point at most.")]
    [InlineData("100000.01", "More than ₹1,00,000 for something not in the catalogue. Add it to the catalogue first.")]
    public void APriceThatWillNotDoIsRefusedSayingWhy(string typed, string message)
    {
        using var till = Till();
        Open(till);
        Type(till, "Mosquito coil");

        Type(till, typed);

        Assert.Equal(OpenItemStage.Price, till.ViewModel.OpenItemStage);
        Assert.Equal(message, till.ViewModel.StatusMessage);
    }

    [Fact]
    public void NoOtherKeyGetsRoundIt()
    {
        using var till = Till();
        till.Scan(Dal);
        Open(till);
        Type(till, "Mosquito coil");

        foreach (var key in new[] { Key.F12, Key.Delete, Key.Add, Key.Subtract, Key.F5, Key.F1 })
            till.Press(key);

        Assert.True(till.ViewModel.IsAddingOpenItem);
        Assert.False(till.ViewModel.IsTendering);
        Assert.Equal(1m, Assert.Single(till.ViewModel.Lines).Quantity);
    }

    [Fact]
    public void NotWhileSomethingElseIsOpen()
    {
        using var till = Till();
        till.Scan(Dal);
        till.Press(Key.F12);

        Open(till);

        Assert.True(till.ViewModel.IsTendering);
        Assert.False(till.ViewModel.IsAddingOpenItem);
        Assert.Equal("Finish what is open first.", till.ViewModel.StatusMessage);
    }

    // ---- Backing out ----------------------------------------------------------------------------

    [Fact]
    public void EscGoesBackAStepKeepingWhatWasTyped()
    {
        using var till = Till();
        Open(till);
        Type(till, "Mosquito coil");
        Type(till, "45.5");

        till.Press(Key.Escape);
        Assert.Equal(OpenItemStage.Price, till.ViewModel.OpenItemStage);
        Assert.Equal("45.5", till.ViewModel.EditBuffer);
        Assert.Empty(till.ViewModel.OpenItemRates);

        till.Press(Key.Escape);
        Assert.Equal(OpenItemStage.Name, till.ViewModel.OpenItemStage);
        Assert.Equal("Mosquito coil", till.ViewModel.EditBuffer);

        till.Press(Key.Escape);
        Assert.False(till.ViewModel.IsAddingOpenItem);
        Assert.Empty(till.ViewModel.Lines);
        Assert.Equal("Nothing added.", till.ViewModel.StatusMessage);
    }

    [Fact]
    public void ABarcodeCarriedInIsForgottenOnceTheItemIsDone()
    {
        using var till = Till();
        till.Scan("8901234599990");
        Open(till);
        till.Press(Key.Escape);

        Open(till);

        Assert.Equal(string.Empty, till.ViewModel.OpenItemSoFar);
    }

    // ---- A bill of supply -----------------------------------------------------------------------

    [Fact]
    public void ABillOfSupplyAsksNoSlab()
    {
        using var till = Till();
        Assert.Null(till.ViewModel.TrySetTaxMode(TaxMode.Composition));

        Open(till);
        Type(till, "Mosquito coil");
        Type(till, "45");

        Assert.False(till.ViewModel.IsAddingOpenItem);

        var line = Assert.Single(till.ViewModel.Lines);
        Assert.Equal(0m, line.Line.GstRate);
        Assert.Equal(45m, line.LineTotal);
        Assert.Equal("Mosquito coil added. It is not in the catalogue, so the owner will see it to add.", till.ViewModel.StatusMessage);
    }

    // ---- After the sale -------------------------------------------------------------------------

    [Fact]
    public void TheSaleGoesThroughAndWaitsForTheOwner()
    {
        using var till = Till();
        till.Scan(Dal);
        Sell(till, "Mosquito coil", "45", 18m);

        Pay(till);

        Assert.Empty(till.ViewModel.Lines);

        var waiting = Assert.Single(new OpenItemRepository(till.Database).Waiting());
        Assert.Equal("Mosquito coil", waiting.Name);
        Assert.Equal(45m, waiting.Price);
        Assert.Equal(18m, waiting.GstRate);
    }

    [Fact]
    public void AHeldBillWithOneOnItComesBackWithIt()
    {
        using var till = Till();
        Sell(till, "Mosquito coil", "45", 18m);

        till.Press(Key.F5);
        Assert.Empty(till.ViewModel.Lines);
        till.Press(Key.F6);
        till.Press(Key.Enter);

        var line = Assert.Single(till.ViewModel.Lines);
        Assert.True(line.Line.IsOpen());
        Assert.Equal("Mosquito coil", line.Name);
        Assert.Equal(45m, line.LineTotal);
    }
}
