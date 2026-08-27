using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Domain.Printing;
using Pos.Core.Hardware.Drawer;
using Pos.TestSupport;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// What is left on the shelf.
/// </summary>
/// <remarks>
/// Two rules run through all of this and are worth stating once.
///
/// Null is not zero. An item the catalogue never gave a count to is not counted, and must never
/// produce a warning — a shop weighing rice out of a sack is not tracking it, and telling a cashier
/// it has run out would be noise about something nobody is measuring.
///
/// A sale is never blocked. The shelf is the authority on what a customer can buy; the database
/// only records what happened. A count may go negative, and when it does that is information, not
/// an error to be clamped away.
/// </remarks>
public class StockTests : IDisposable
{
    private const string Lane = "L1";
    private const string HomeState = "33";

    private readonly TempDatabase _temp = new();
    private readonly RecordingDrawerService _drawer = new();

    public void Dispose() => _temp.Dispose();

    private StockRepository Stock => new(_temp.Database);
    private ItemRepository Items => new(_temp.Database);

    /// <summary>Puts one item in the catalogue and returns it with the id it was given.</summary>
    private Item Stocked(
        string sku,
        decimal? stock,
        decimal? reorder = null,
        decimal price = 50m,
        UnitType unit = UnitType.Each)
    {
        Items.UpsertRange([Catalogue.Item(sku: sku, name: $"Item {sku}", price: price, unit: unit) with
        {
            StockQty = stock,
            ReorderLevel = reorder,
        }]);

        return Items.FindBySku(sku)!;
    }

    private SettledInvoice Sell(Item item, decimal quantity = 1m)
    {
        var bill = new InvoiceEngine(HomeState);
        bill.AddItem(item, quantity);

        var basket = new TenderBasket(bill.Totals.GrandTotal);
        basket.Add(TenderType.Cash, bill.Totals.GrandTotal);

        return new CheckoutService(
            new InvoiceRepository(_temp.Database),
            new CustomerRepository(_temp.Database),
            _drawer,
            null,
            TimeProvider.System,
            stock: Stock).Complete(Lane, bill, basket).Invoice;
    }

    // ---- Counted, and not counted ----------------------------------------------------------------

    [Fact]
    public void AnItemWithNoCountIsNotCountedRatherThanEmpty()
    {
        var item = Stocked("LOOSE", stock: null);

        Assert.False(item.IsStockTracked);
        Assert.False(item.IsOutOfStock);
        Assert.False(item.IsLowStock);
        Assert.Null(item.StockQty);
    }

    [Fact]
    public void AnItemCountedAtZeroIsOutOfStock()
    {
        var item = Stocked("GONE", stock: 0m);

        Assert.True(item.IsStockTracked);
        Assert.True(item.IsOutOfStock);
    }

    [Fact]
    public void LowMeansAtOrBelowTheLevelNotOnlyBelowIt()
    {
        Assert.True(Stocked("A", stock: 3m, reorder: 3m).IsLowStock);
        Assert.True(Stocked("B", stock: 2m, reorder: 3m).IsLowStock);
        Assert.False(Stocked("C", stock: 4m, reorder: 3m).IsLowStock);
    }

    [Fact]
    public void AnItemWithNoReorderLevelIsNeverLowHoweverFewAreLeft()
    {
        Assert.False(Stocked("D", stock: 0m, reorder: null).IsLowStock);
    }

    // ---- Selling ---------------------------------------------------------------------------------

    [Fact]
    public void SellingTakesItOffTheShelf()
    {
        var item = Stocked("RICE", stock: 10m);
        Sell(item, 3m);

        Assert.Equal(7m, Items.FindBySku("RICE")!.StockQty);
    }

    /// <summary>
    /// The rule that matters at a counter: the count never stops a sale, and the shortfall is
    /// recorded rather than clamped. A negative figure is the signal that the count and the shelf
    /// have parted company, which is the only useful thing to say about it.
    /// </summary>
    [Fact]
    public void SellingMoreThanTheCountKnowsAboutStillSellsAndGoesNegative()
    {
        var item = Stocked("SOAP", stock: 2m);

        var invoice = Sell(item, 5m);

        Assert.NotNull(invoice);
        Assert.Equal(-3m, Items.FindBySku("SOAP")!.StockQty);
    }

    [Fact]
    public void SellingAnUncountedItemChangesNothingAndFailsNothing()
    {
        // Weighed, because loose goods are exactly the thing a shop does not keep a running count
        // of — a fractional quantity is the realistic case here, not an incidental one.
        var item = Stocked("LOOSE", stock: null, unit: UnitType.Kilogram);

        Assert.NotNull(Sell(item, 2.5m));
        Assert.Null(Items.FindBySku("LOOSE")!.StockQty);
    }

    /// <summary>
    /// A till with no stock store at all bills exactly as it always did. This is the guard on the
    /// promise that adding stock did not disturb billing.
    /// </summary>
    [Fact]
    public void ALaneThatDoesNotCountStockBillsUnchanged()
    {
        var item = Stocked("RICE", stock: 10m);

        var bill = new InvoiceEngine(HomeState);
        bill.AddItem(item, 3m);

        var basket = new TenderBasket(bill.Totals.GrandTotal);
        basket.Add(TenderType.Cash, bill.Totals.GrandTotal);

        var invoice = new CheckoutService(
            new InvoiceRepository(_temp.Database),
            new CustomerRepository(_temp.Database),
            _drawer,
            null,
            TimeProvider.System).Complete(Lane, bill, basket).Invoice;

        Assert.NotNull(invoice);
        Assert.Equal(10m, Items.FindBySku("RICE")!.StockQty);
    }

    // ---- Voiding ---------------------------------------------------------------------------------

    /// <summary>The goods are back on the shelf, so the count has to be too.</summary>
    [Fact]
    public void VoidingPutsItBack()
    {
        var item = Stocked("RICE", stock: 10m);
        var invoice = Sell(item, 3m);

        Assert.Equal(7m, Items.FindBySku("RICE")!.StockQty);

        new CheckoutService(
            new InvoiceRepository(_temp.Database),
            new CustomerRepository(_temp.Database),
            _drawer,
            null,
            TimeProvider.System,
            stock: Stock).VoidSale(invoice.InvoiceNo, "rung up wrong");

        Assert.Equal(10m, Items.FindBySku("RICE")!.StockQty);
    }

    // ---- The ledger ------------------------------------------------------------------------------

    /// <summary>
    /// The current figure alone cannot answer "it says four and there are two, when did that
    /// happen", which is the only question anybody actually asks of a stock count.
    /// </summary>
    [Fact]
    public void EverySaleLeavesAMovementSayingWhatItWasAndWhatItBecame()
    {
        var item = Stocked("RICE", stock: 10m);
        var invoice = Sell(item, 3m);

        var movement = Assert.Single(Stock.History(item.Id));

        Assert.Equal(StockReason.Sale, movement.Reason);
        Assert.Equal(-3m, movement.Delta);
        Assert.Equal(7m, movement.BalanceAfter);
        Assert.Equal(invoice.InvoiceNo, movement.Reference);
        Assert.Equal(Lane, movement.LaneId);
    }

    [Fact]
    public void AVoidLeavesItsOwnMovementRatherThanErasingTheSale()
    {
        var item = Stocked("RICE", stock: 10m);
        var invoice = Sell(item, 3m);

        new CheckoutService(
            new InvoiceRepository(_temp.Database),
            new CustomerRepository(_temp.Database),
            _drawer,
            null,
            TimeProvider.System,
            stock: Stock).VoidSale(invoice.InvoiceNo);

        var history = Stock.History(item.Id);

        Assert.Equal(2, history.Count);
        Assert.Equal(StockReason.Void, history[0].Reason);
        Assert.Equal(3m, history[0].Delta);
        Assert.Equal(StockReason.Sale, history[1].Reason);
    }

    [Fact]
    public void AHandCorrectionRecordsWhatItWasChangedFromAndWhy()
    {
        var item = Stocked("RICE", stock: 10m);

        Assert.Equal(24m, Stock.Set(item.Id, 24m, StockReason.Adjust, Lane, "delivery"));

        var movement = Assert.Single(Stock.History(item.Id));

        Assert.Equal(StockReason.Adjust, movement.Reason);
        Assert.Equal(14m, movement.Delta);
        Assert.Equal(24m, movement.BalanceAfter);
        Assert.Equal("delivery", movement.Reference);
    }

    [Fact]
    public void MovingAnUncountedItemDoesNothingAndSaysSo()
    {
        var item = Stocked("LOOSE", stock: null);

        Assert.Null(Stock.Move(item.Id, -1m, StockReason.Sale, Lane));
        Assert.Empty(Stock.History(item.Id));
    }

    [Fact]
    public void MovingAnItemThatDoesNotExistIsRefusedRatherThanThrowing()
    {
        Assert.Null(Stock.Move(999_999, -1m, StockReason.Sale, Lane));
    }

    // ---- The listings ----------------------------------------------------------------------------

    [Fact]
    public void OnlyCountedItemsAreListed()
    {
        Stocked("COUNTED", stock: 5m);
        Stocked("LOOSE", stock: null);

        var listed = Stock.List();

        Assert.Single(listed);
        Assert.Equal("COUNTED", listed[0].Sku);
    }

    [Fact]
    public void TheLowListIsOnlyWhatIsAtOrBelowItsLevel()
    {
        Stocked("PLENTY", stock: 50m, reorder: 5m);
        Stocked("LOW", stock: 4m, reorder: 5m);
        Stocked("EXACTLY", stock: 5m, reorder: 5m);
        Stocked("NOLEVEL", stock: 0m, reorder: null);

        var low = Stock.ListLow().Select(l => l.Sku).ToList();

        Assert.Contains("LOW", low);
        Assert.Contains("EXACTLY", low);
        Assert.DoesNotContain("PLENTY", low);
        Assert.DoesNotContain("NOLEVEL", low);
    }

    /// <summary>
    /// Ordered by how far below the line each item is, because the point of the list is what to buy
    /// first. Quantities are stored as text, so this also pins the numeric comparison — sorted as
    /// strings, 9 comes out below 10.
    /// </summary>
    [Fact]
    public void TheMostDepletedComesFirstAndNumbersSortAsNumbers()
    {
        Stocked("A", stock: 9m, reorder: 10m);
        Stocked("B", stock: 10m, reorder: 30m);
        Stocked("C", stock: 2m, reorder: 4m);

        var order = Stock.ListLow().Select(l => l.Sku).ToList();

        // B is 20 short, C is 2 short, A is 1 short.
        Assert.Equal(["B", "C", "A"], order);
    }

    [Fact]
    public void ShortByIsWhatItTakesToGetBackToTheLevel()
    {
        Stocked("RICE", stock: 2m, reorder: 10m);

        var level = Assert.Single(Stock.ListLow());

        Assert.Equal(8m, level.ShortBy);
        Assert.True(level.IsLow);
        Assert.False(level.IsOut);
    }

    [Fact]
    public void ALaneThatCountsNothingListsNothingRatherThanFailing()
    {
        Assert.Empty(Stock.List());
        Assert.Empty(Stock.ListLow());
    }

    // ---- Re-importing ----------------------------------------------------------------------------

    /// <summary>
    /// A shop re-imports to change prices far more often than to restate its shelves, and the file
    /// it re-imports is usually the one it first loaded. Overwriting would silently reset every
    /// count to a figure from weeks ago, and the only sign would be reorder warnings nobody could
    /// explain.
    /// </summary>
    [Fact]
    public void ReImportingWithAnEmptyStockCellLeavesTheLiveCountAlone()
    {
        Stocked("RICE", stock: 10m, reorder: 4m);
        Sell(Items.FindBySku("RICE")!, 3m);

        Assert.Equal(7m, Items.FindBySku("RICE")!.StockQty);

        // The same row again with a new price and nothing said about stock.
        Items.UpsertRange([Catalogue.Item(sku: "RICE", name: "Item RICE", price: 60m) with
        {
            StockQty = null,
            ReorderLevel = null,
        }]);

        var after = Items.FindBySku("RICE")!;

        Assert.Equal(60m, after.SellPrice);
        Assert.Equal(7m, after.StockQty);
        Assert.Equal(4m, after.ReorderLevel);
    }

    [Fact]
    public void ReImportingWithAStockCellDoesRestateIt()
    {
        Stocked("RICE", stock: 10m);

        Items.UpsertRange([Catalogue.Item(sku: "RICE", name: "Item RICE", price: 50m) with { StockQty = 40m }]);

        Assert.Equal(40m, Items.FindBySku("RICE")!.StockQty);
    }

    // ---- On the day-end report -------------------------------------------------------------------

    private static string PrintZReport(IReadOnlyList<StockLevel>? lowStock, bool isReprint = false)
    {
        var day = new DayCloseSummary(
            Id: 1, LaneId: Lane, ClosedAt: DateTimeOffset.Now, OpenedAt: DateTimeOffset.Now.AddHours(-9),
            InvoiceCount: 12, GrossSales: 5_000m, TotalDiscount: 0m, NetSales: 5_000m,
            TaxableValue: 4_761.90m, TotalCgst: 119.05m, TotalSgst: 119.05m, TotalIgst: 0m,
            CashExpected: 5_000m, ChangeGiven: 0m, PointsRedeemed: 0, PointsEarned: 0,
            Tenders: [new TenderTotal(TenderType.Cash, 5_000m, 12)],
            TaxSlabs: [new TaxSlabTotal(5m, 4_761.90m, 119.05m, 119.05m, 0m)],
            HeldBillsOutstanding: 0);

        return new ZReportComposer(new StoreProfile { Name = "Ravi Maligai" })
            .Compose(day, isReprint, lowStock)
            .ToPlainText();
    }

    [Fact]
    public void TheDayEndReportCarriesTheReorderList()
    {
        Stocked("SOAP", stock: 3m, reorder: 5m);

        var report = PrintZReport(Stock.ListLow());

        Assert.Contains("TO REORDER", report);
        Assert.Contains("Item SOAP", report);
    }

    /// <summary>
    /// A report pulled out of the file months later must not carry today's shelves under last
    /// spring's takings — so the list goes on the original and never on a duplicate.
    /// </summary>
    [Fact]
    public void ADuplicateDoesNotCarryTodaysShelves()
    {
        Assert.DoesNotContain("TO REORDER", PrintZReport(lowStock: null, isReprint: true));
    }

    /// <summary>
    /// A shop that counts nothing gets no section at all, rather than an empty one. An empty panel
    /// reads as "nothing is low", which is a different and far more comforting claim than
    /// "nothing is counted".
    /// </summary>
    [Fact]
    public void ALaneThatCountsNothingGetsNoSection()
    {
        Assert.DoesNotContain("TO REORDER", PrintZReport(Stock.ListLow()));
    }
}
