using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Domain.Import;
using Pos.TestSupport;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// One barcode, two MRPs: when an MRP goes up, the packs already on the shelf keep the old one, and
/// the catalogue remembers it until the count says they have sold.
/// </summary>
public class OlderMrpTests : IDisposable
{
    private readonly TempDatabase _temp = new();

    public void Dispose() => _temp.Dispose();

    private ItemRepository Items => _temp.Items;

    private Item Ghee(decimal? stock = 12m, decimal mrp = 550m, decimal price = 540m)
    {
        Items.UpsertRange([Catalogue.Item(sku: "GHEE500", barcode: "8901234500017", name: "Ghee 500ml", price: mrp) with
        {
            SellPrice = price,
            StockQty = stock,
        }]);

        return Items.FindBySku("GHEE500")!;
    }

    /// <summary>A re-import with the new MRP and price and no stock figure, which leaves the count alone.</summary>
    private Item Reprice(decimal mrp, decimal price)
    {
        Items.UpsertRange([Catalogue.Item(sku: "GHEE500", barcode: "8901234500017", name: "Ghee 500ml", price: mrp) with { SellPrice = price }]);
        return Items.FindBySku("GHEE500")!;
    }

    [Fact]
    public void AnItemFreshOnTheShelfHasOneMrp()
    {
        var ghee = Ghee();

        Assert.Null(ghee.OlderMrp);
        Assert.Null(ghee.OlderPrice);
        Assert.Null(ghee.OlderLeft);
        Assert.False(ghee.HasOlderMrp);
    }

    [Fact]
    public void AnMrpRiseKeepsTheOldMrpAndPriceForThePacksOnTheShelf()
    {
        Ghee(stock: 12m, mrp: 550m, price: 540m);

        var ghee = Reprice(mrp: 585m, price: 575m);

        Assert.Equal(585m, ghee.Mrp);
        Assert.Equal(575m, ghee.SellPrice);
        Assert.Equal(550m, ghee.OlderMrp);
        Assert.Equal(540m, ghee.OlderPrice);
        Assert.Equal(12m, ghee.OlderLeft);
        Assert.True(ghee.HasOlderMrp);

        // Found by barcode too, which is how the scanner finds it.
        Assert.Equal(550m, Items.FindByBarcode("8901234500017")!.OlderMrp);
    }

    [Fact]
    public void ARiseThroughThePriceSheetIsCaughtTheSameWay()
    {
        var ghee = Ghee(stock: 5m);

        new PriceRepository(_temp.Database).Apply([new PriceChange(ghee.Id, ghee.Sku, ghee.Name, 550m, 585m, 540m, 575m)]);

        var after = Items.FindBySku("GHEE500")!;
        Assert.Equal(550m, after.OlderMrp);
        Assert.Equal(540m, after.OlderPrice);
        Assert.Equal(5m, after.OlderLeft);
    }

    [Fact]
    public void AnMrpThatComesDownLeavesOneMrp()
    {
        // Every pack may be sold at the new, lower price, whatever is printed on it.
        Ghee(stock: 12m);

        Assert.False(Reprice(mrp: 520m, price: 510m).HasOlderMrp);
    }

    [Theory]
    [InlineData(550)]
    [InlineData(540)]
    public void AnMrpThatComesBackDownToTheOlderOneOrBelowForgetsIt(int mrp)
    {
        Ghee(stock: 12m);
        Reprice(mrp: 585m, price: 575m);

        var ghee = Reprice(mrp: mrp, price: mrp - 10m);

        Assert.Null(ghee.OlderMrp);
        Assert.Null(ghee.OlderPrice);
        Assert.Null(ghee.OlderLeft);
    }

    [Fact]
    public void AnMrpThatComesPartWayDownKeepsTheOlderOne()
    {
        // The older packs at ₹550 are still cheaper than the new ₹560.
        Ghee(stock: 12m);
        Reprice(mrp: 585m, price: 575m);

        var ghee = Reprice(mrp: 560m, price: 555m);

        Assert.Equal(550m, ghee.OlderMrp);
        Assert.True(ghee.HasOlderMrp);
    }

    [Fact]
    public void ASecondRiseKeepsTheMrpJustBeforeIt()
    {
        // The packs from before the first rise are not told apart from those before the second:
        // one older MRP, the one the most recent packs on the shelf carry.
        Ghee(stock: 12m);
        Reprice(mrp: 585m, price: 575m);

        var ghee = Reprice(mrp: 610m, price: 600m);

        Assert.Equal(585m, ghee.OlderMrp);
        Assert.Equal(575m, ghee.OlderPrice);
        Assert.Equal(12m, ghee.OlderLeft);
    }

    [Theory]
    [InlineData(550, 550)]
    [InlineData(600, 550)]
    public void AnOlderMrpAtOrAboveTodaysIsNoChoice(int older, int today)
    {
        var item = Catalogue.Item(price: today) with { OlderMrp = older, OlderPrice = older, OlderLeft = 4m };

        Assert.False(item.HasOlderMrp);
        Assert.Same(item, item.AtOlderMrp());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void OlderPacksAllSoldIsNoChoice(int? left)
    {
        var item = Catalogue.Item(price: 585m) with { OlderMrp = 550m, OlderPrice = 540m, OlderLeft = left };

        Assert.False(item.HasOlderMrp);
    }

    [Fact]
    public void APriceChangeAloneLeavesOneMrp()
    {
        // The shop's own price under the same MRP: nothing printed on the packs has changed.
        Ghee(stock: 12m);

        Assert.False(Reprice(mrp: 550m, price: 530m).HasOlderMrp);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-2)]
    public void AnItemNobodyCountsOrWithNoneOnTheShelfLeavesOneMrp(int? stock)
    {
        // No figure to say when the older packs are gone, or none of them to sell.
        Ghee(stock: stock);

        Assert.False(Reprice(mrp: 585m, price: 575m).HasOlderMrp);
    }

    [Fact]
    public void SellingOlderPacksCountsThemOff()
    {
        Ghee(stock: 12m);
        var ghee = Reprice(mrp: 585m, price: 575m);

        Assert.Equal(9m, Items.SoldAtOlderMrp(ghee.Id, 550m, 3m));
        Assert.Equal(9m, Items.FindBySku("GHEE500")!.OlderLeft);

        // Still the older MRP and price; only how many are left has changed.
        Assert.Equal(550m, Items.FindBySku("GHEE500")!.OlderMrp);
        Assert.Equal(540m, Items.FindBySku("GHEE500")!.OlderPrice);
    }

    [Fact]
    public void TheLastOlderPacksSoldForgetTheOlderMrp()
    {
        Ghee(stock: 4m);
        var ghee = Reprice(mrp: 585m, price: 575m);

        Assert.Null(Items.SoldAtOlderMrp(ghee.Id, 550m, 4m));

        var after = Items.FindBySku("GHEE500")!;
        Assert.Null(after.OlderMrp);
        Assert.Null(after.OlderPrice);
        Assert.Null(after.OlderLeft);
        Assert.False(after.HasOlderMrp);
    }

    [Fact]
    public void SellingMoreThanTheCountSaidForgetsTheOlderMrpToo()
    {
        // The count was a little out; the packs in hand are what the shop has.
        Ghee(stock: 2m);
        var ghee = Reprice(mrp: 585m, price: 575m);

        Assert.Null(Items.SoldAtOlderMrp(ghee.Id, 550m, 5m));
        Assert.False(Items.FindBySku("GHEE500")!.HasOlderMrp);
    }

    [Fact]
    public void ASaleAtAnMrpTheCatalogueNoLongerHoldsCountsNothing()
    {
        // The MRP rose twice while the bill was open: the older MRP now is not the one sold.
        Ghee(stock: 6m);
        var ghee = Reprice(mrp: 585m, price: 575m);

        Assert.Null(Items.SoldAtOlderMrp(ghee.Id, 500m, 1m));
        Assert.Equal(6m, Items.FindBySku("GHEE500")!.OlderLeft);
    }

    [Fact]
    public void AnItemWithOneMrpCountsNothing()
    {
        var ghee = Ghee();

        Assert.Null(Items.SoldAtOlderMrp(ghee.Id, 550m, 1m));
        Assert.False(Items.FindBySku("GHEE500")!.HasOlderMrp);
    }

    [Fact]
    public void AnItemAtItsOlderMrpCarriesTheOlderMrpAndPriceAndNothingElseChanges()
    {
        Ghee(stock: 12m);
        var ghee = Reprice(mrp: 585m, price: 575m);

        var older = ghee.AtOlderMrp();

        Assert.Equal(550m, older.Mrp);
        Assert.Equal(540m, older.SellPrice);
        Assert.Equal(ghee.Id, older.Id);
        Assert.Equal(ghee.Barcode, older.Barcode);
        Assert.Equal(ghee.GstRate, older.GstRate);
        Assert.Equal(ghee.HsnCode, older.HsnCode);

        // One MRP on it now: it is never asked about twice.
        Assert.False(older.HasOlderMrp);
    }

    [Fact]
    public void AnItemWithOneMrpIsItselfAtItsOlderMrp()
    {
        var ghee = Ghee();

        Assert.Same(ghee, ghee.AtOlderMrp());
    }
}
