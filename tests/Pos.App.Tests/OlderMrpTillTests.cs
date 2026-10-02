using System.Windows.Input;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// One barcode, two MRPs on the shelf: the till asks which the pack in hand carries, sells the older
/// packs at the older price, and stops asking once the count says they have gone.
/// </summary>
public class OlderMrpTillTests
{
    private const string Ghee = "8901234500017";

    /// <summary>Ghee at ₹550, with <paramref name="stock"/> on the shelf, and then the MRP goes up to ₹585.</summary>
    private static BillingHarness Till(decimal? stock = 12m, bool rise = true)
    {
        var till = new BillingHarness(Catalogue.Item(sku: "GHEE500", barcode: Ghee, name: "Ghee 500ml", price: 550m, gstRate: 12m) with
        {
            SellPrice = 540m,
            StockQty = stock,
        });

        if (rise)
        {
            till.Items.UpsertRange([Catalogue.Item(sku: "GHEE500", barcode: Ghee, name: "Ghee 500ml", price: 585m, gstRate: 12m) with
            {
                SellPrice = 575m,
            }]);
        }

        return till;
    }

    private static Item Catalogued(BillingHarness till) => till.Items.FindBySku("GHEE500")!;

    private static void Sell(BillingHarness till)
    {
        till.Press(Key.F12);
        till.Press(Key.Enter);
        till.Press(Key.Enter);

        Assert.Empty(till.ViewModel.Lines);
    }

    [Fact]
    public void AnItemWithOneMrpGoesStraightOn()
    {
        using var till = Till(rise: false);

        till.Scan(Ghee);

        Assert.False(till.ViewModel.IsChoosingMrp);
        Assert.Equal(550m, Assert.Single(till.ViewModel.Lines).Mrp);
    }

    [Fact]
    public void AnItemWithTwoMrpsAsksWhichBeforeAnythingGoesOn()
    {
        using var till = Till();

        till.Scan(Ghee);

        Assert.True(till.ViewModel.IsChoosingMrp);
        Assert.Empty(till.ViewModel.Lines);
        Assert.Contains("Ghee 500ml", till.ViewModel.MrpQuestion);

        Assert.Collection(till.ViewModel.MrpChoices,
            newer =>
            {
                Assert.False(newer.IsOlder);
                Assert.Contains("MRP ₹585.00", newer.Label);
                Assert.Contains("₹575.00", newer.Label);
            },
            older =>
            {
                Assert.True(older.IsOlder);
                Assert.Contains("MRP ₹550.00", older.Label);
                Assert.Contains("₹540.00", older.Label);
                Assert.Contains("12 left", older.Label);
            });

        // The newer one first: what most packs carry as the old ones sell.
        Assert.Equal(0, till.ViewModel.SelectedMrpIndex);
    }

    [Fact]
    public void FoundBySearchItAsksTheSame()
    {
        using var till = Till();

        till.TypeAndWait("Ghee");
        till.Press(Key.Enter);

        Assert.True(till.ViewModel.IsChoosingMrp);
        Assert.Empty(till.ViewModel.Lines);
    }

    [Fact]
    public void EnterPutsItOnAtTheNewerMrp()
    {
        using var till = Till();

        till.Scan(Ghee);
        till.Press(Key.Enter);

        Assert.False(till.ViewModel.IsChoosingMrp);
        var line = Assert.Single(till.ViewModel.Lines);
        Assert.Equal(585m, line.Mrp);
        Assert.Equal(575m, line.LineTotal);
    }

    [Fact]
    public void DownAndEnterPutsItOnAtTheOlderMrpAndPrice()
    {
        using var till = Till();

        till.Scan(Ghee);
        till.Press(Key.Down);
        till.Press(Key.Enter);

        var line = Assert.Single(till.ViewModel.Lines);
        Assert.Equal(550m, line.Mrp);
        Assert.Equal(540m, line.LineTotal);

        // The tax is worked out of the older price like any other: ₹540 at 12% inclusive.
        Assert.Equal(482.1429m, line.Line.Tax.TaxableValue);
        Assert.Equal(482.14m, line.UnitRateExclTax);
        Assert.Contains("older MRP", till.ViewModel.StatusMessage);
    }

    [Fact]
    public void UpAndDownStopAtTheEnds()
    {
        using var till = Till();
        till.Scan(Ghee);

        till.Press(Key.Down);
        till.Press(Key.Down);
        Assert.Equal(1, till.ViewModel.SelectedMrpIndex);

        till.Press(Key.Up);
        till.Press(Key.Up);
        Assert.Equal(0, till.ViewModel.SelectedMrpIndex);
    }

    [Fact]
    public void EscPutsNothingOn()
    {
        using var till = Till();

        till.Scan(Ghee);
        till.Press(Key.Escape);

        Assert.False(till.ViewModel.IsChoosingMrp);
        Assert.Empty(till.ViewModel.Lines);
        Assert.Equal("Nothing added.", till.ViewModel.StatusMessage);
    }

    [Fact]
    public void NoOtherKeyGetsRoundTheQuestion()
    {
        // A pack already on the bill, and the question open over it: nothing edits that line behind
        // the pane, and nothing walks away from the pack in hand.
        using var till = Till();
        till.Scan(Ghee);
        till.Press(Key.Enter);
        till.Scan(Ghee);

        foreach (var key in new[] { Key.F12, Key.Delete, Key.Add, Key.Subtract, Key.F2, Key.F1 })
            till.Press(key);

        Assert.True(till.ViewModel.IsChoosingMrp);
        Assert.False(till.ViewModel.IsTendering);
        Assert.Equal(1m, Assert.Single(till.ViewModel.Lines).Quantity);

        till.Press(Key.Down);
        till.Press(Key.Enter);
        Assert.Equal(2, till.ViewModel.Lines.Count);
    }

    [Fact]
    public void AClickOnTheListPicksTheSameAsTheArrows()
    {
        using var till = Till();
        till.Scan(Ghee);

        till.ViewModel.SelectedMrpIndex = 1;
        till.Press(Key.Enter);

        Assert.Equal(550m, Assert.Single(till.ViewModel.Lines).Mrp);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(5, 1)]
    public void APickOffTheListStaysOnIt(int picked, int held)
    {
        using var till = Till();
        till.Scan(Ghee);

        till.ViewModel.SelectedMrpIndex = picked;

        Assert.Equal(held, till.ViewModel.SelectedMrpIndex);
    }

    [Fact]
    public void TheNextPackOfTheSameItemIsOfferedAsTheLastOneWas()
    {
        // A run of the same older packs is Down, Enter, then Enter, Enter, Enter.
        using var till = Till();

        till.Scan(Ghee);
        till.Press(Key.Down);
        till.Press(Key.Enter);

        till.Scan(Ghee);
        Assert.Equal(1, till.ViewModel.SelectedMrpIndex);
        till.Press(Key.Enter);

        Assert.All(till.ViewModel.Lines, line => Assert.Equal(550m, line.Mrp));
        Assert.Equal(2, till.ViewModel.Lines.Count);
    }

    [Fact]
    public void ANewerAndAnOlderPackAreTwoLinesAtTheirOwnPrices()
    {
        using var till = Till();

        till.Scan(Ghee);
        till.Press(Key.Enter);
        till.Scan(Ghee);
        till.Press(Key.Down);
        till.Press(Key.Enter);

        Assert.Collection(till.ViewModel.Lines,
            newer => Assert.Equal(575m, newer.LineTotal),
            older => Assert.Equal(540m, older.LineTotal));
        Assert.Equal(1_115m, till.ViewModel.Lines.Sum(l => l.LineTotal));
    }

    [Fact]
    public void OlderPacksSoldAreCountedOff()
    {
        using var till = Till(stock: 5m);

        till.Scan(Ghee);
        till.Press(Key.Down);
        till.Press(Key.Enter);

        // Offered the older again, as the last one was: Up for the newer pack.
        till.Scan(Ghee);
        till.Press(Key.Up);
        till.Press(Key.Enter);
        Sell(till);

        Assert.Equal(4m, Catalogued(till).OlderLeft);
        Assert.Equal(550m, Catalogued(till).OlderMrp);
    }

    [Fact]
    public void NewerPacksSoldLeaveTheOlderCountAlone()
    {
        using var till = Till(stock: 5m);

        till.Scan(Ghee);
        till.Press(Key.Enter);
        Sell(till);

        Assert.Equal(5m, Catalogued(till).OlderLeft);
    }

    [Fact]
    public void AnOlderPackSoldThreeAtATimeCountsThree()
    {
        using var till = Till(stock: 5m);

        till.Scan(Ghee);
        till.Press(Key.Down);
        till.Press(Key.Enter);
        till.Press(Key.Add);
        till.Press(Key.Add);
        Assert.Equal(3m, till.ViewModel.Lines[0].Quantity);
        Sell(till);

        Assert.Equal(2m, Catalogued(till).OlderLeft);
    }

    [Fact]
    public void OnceTheOlderPacksHaveSoldTheTillStopsAsking()
    {
        using var till = Till(stock: 1m);

        till.Scan(Ghee);
        till.Press(Key.Down);
        till.Press(Key.Enter);
        Sell(till);

        Assert.False(Catalogued(till).HasOlderMrp);

        till.Scan(Ghee);

        Assert.False(till.ViewModel.IsChoosingMrp);
        Assert.Equal(585m, Assert.Single(till.ViewModel.Lines).Mrp);
    }

    [Fact]
    public void AnOlderPackLeftOnABillThatIsNotSoldCountsNothing()
    {
        using var till = Till(stock: 5m);

        till.Scan(Ghee);
        till.Press(Key.Down);
        till.Press(Key.Enter);

        // Taken off the bill, and a newer pack sold instead.
        till.Press(Key.Delete);
        till.Scan(Ghee);
        till.Press(Key.Up);
        till.Press(Key.Enter);
        Assert.Equal(585m, Assert.Single(till.ViewModel.Lines).Mrp);
        Sell(till);

        Assert.Equal(5m, Catalogued(till).OlderLeft);
    }
}
