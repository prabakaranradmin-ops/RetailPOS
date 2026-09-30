using Pos.Core.Analytics;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// How fast things sell, how long the shelf lasts, and what to order from whom. The rate is sold
/// less returned over the days measured; an order covers so many days at that rate, less the shelf,
/// rounded up to whole units.
/// </summary>
public class ReorderTests : IDisposable
{
    private const string Lane = "L1";
    private const string HomeState = "33";
    private const string TamilNaduGstin = "33AEIPH7795F1Z9";

    private readonly TempDatabase _temp = new();

    public void Dispose() => _temp.Dispose();

    // ---- The arithmetic --------------------------------------------------------------------------

    [Theory]
    [InlineData(56, 28, 2.0)]
    [InlineData(10, 28, 0.357)]
    [InlineData(3, 1, 3.0)]
    public void TheRateIsWhatSoldLessWhatCameBackPerDay(decimal sold, int days, decimal perDay) =>
        Assert.Equal(perDay, Reorder.PerDay(sold, days));

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void SomethingThatHasNotSoldHasNoRate(decimal sold) => Assert.Null(Reorder.PerDay(sold, 28));

    [Fact]
    public void DaysLeftIsTheShelfOverTheRate()
    {
        Assert.Equal(5.0m, Reorder.DaysLeft(10m, 2m));
        Assert.Equal(2.8m, Reorder.DaysLeft(1m, 0.357m));
        Assert.Equal(0m, Reorder.DaysLeft(-3m, 2m));
        Assert.Null(Reorder.DaysLeft(10m, null));
    }

    /// <summary>A lane selling for ten days is measured over ten, not diluted over twenty-eight.</summary>
    [Fact]
    public void ANewLaneIsMeasuredOverTheDaysItHasBeenSelling()
    {
        var today = new DateOnly(2026, 9, 29);

        Assert.Equal(28, Reorder.DaysMeasured(null, today));
        Assert.Equal(1, Reorder.DaysMeasured(today, today));
        Assert.Equal(10, Reorder.DaysMeasured(today.AddDays(-9), today));
        Assert.Equal(28, Reorder.DaysMeasured(today.AddDays(-200), today));
    }

    [Fact]
    public void AnOrderCoversTheDaysAtTheRateLessTheShelf()
    {
        Assert.Equal(18m, Reorder.Suggest(have: 10m, perDay: 2m, coverDays: 14, reorderLevel: null, isLow: false, fillTo: null));
        Assert.Equal(0m, Reorder.Suggest(have: 30m, perDay: 2m, coverDays: 14, reorderLevel: null, isLow: false, fillTo: null));
    }

    /// <summary>4.9 needed less 1 on the shelf is 3.9, and a wholesaler sells 4.</summary>
    [Fact]
    public void AnOrderIsRoundedUpToWholeUnits() =>
        Assert.Equal(4m, Reorder.Suggest(have: 1m, perDay: 0.35m, coverDays: 14, reorderLevel: null, isLow: false, fillTo: null));

    [Fact]
    public void AnOrderIsNeverLessThanWhatGetsBackToTheReorderLevel() =>
        Assert.Equal(8m, Reorder.Suggest(have: 2m, perDay: 0.1m, coverDays: 14, reorderLevel: 10m, isLow: true, fillTo: null));

    [Fact]
    public void AShelfCountedBelowNothingIsOrderedAsEmpty() =>
        Assert.Equal(14m, Reorder.Suggest(have: -3m, perDay: 1m, coverDays: 14, reorderLevel: null, isLow: true, fillTo: null));

    /// <summary>Not selling: ordered only when low anyway, and then back to full.</summary>
    [Fact]
    public void SomethingNotSellingIsOrderedOnlyWhenLow()
    {
        Assert.Equal(0m, Reorder.Suggest(have: 5m, perDay: null, coverDays: 14, reorderLevel: null, isLow: false, fillTo: 24m));
        Assert.Equal(22m, Reorder.Suggest(have: 2m, perDay: null, coverDays: 14, reorderLevel: null, isLow: true, fillTo: 24m));
        Assert.Equal(4m, Reorder.Suggest(have: 1m, perDay: null, coverDays: 14, reorderLevel: 5m, isLow: true, fillTo: null));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(120, true)]
    [InlineData(121, false)]
    public void AnOrderCoversOneToAHundredAndTwentyDays(int days, bool valid) =>
        Assert.Equal(valid, Reorder.IsValidCoverDays(days));

    [Fact]
    public void TheOrderReadsAsAMessageToSend()
    {
        var order = new SupplierOrder(1, "Murugan Traders", "9443012345",
        [
            new ReorderLine(1, "DAL", "Toor Dal 1kg", UnitType.Each, 2m, 2m, 1m, 26m, 100m, null),
            new ReorderLine(2, "SUG", "Sugar Loose", UnitType.Kilogram, 3m, 1.5m, 2m, 18m, 40m, null),
        ]);

        var message = order.Message("Sri Murugan Stores", new DateOnly(2026, 9, 29));

        Assert.StartsWith("Order from Sri Murugan Stores - 29 Sep 2026", message);
        Assert.Contains("1. Toor Dal 1kg - 26 pc", message);
        Assert.Contains("2. Sugar Loose - 18 kg", message);
        Assert.Equal(3_320.00m, order.EstimatedCost);
    }

    // ---- From the books --------------------------------------------------------------------------

    private PurchaseRepository Purchases => new(_temp.Database);

    private Item Load(string sku, decimal? stock, decimal price = 100m, UnitType unit = UnitType.Each, decimal? reorder = null)
    {
        _temp.Items.UpsertRange([Catalogue.Item(sku: sku, name: $"Item {sku}", price: price, unit: unit) with { StockQty = stock, ReorderLevel = reorder }]);
        return _temp.Items.FindBySku(sku)!;
    }

    /// <param name="registered">A GSTIN is unique to one supplier, so a second one in a test has none.</param>
    private Supplier Supplier(string name, bool registered = true) =>
        Purchases.AddSupplier(new Supplier(0, name, "9443012345", registered ? TamilNaduGstin : null, "33", registered));

    private long Buy(Supplier supplier, string billNo, Item item, decimal quantity, decimal rate, DateOnly? date = null)
    {
        var line = PurchaseLine.Price(new PurchaseLineEntry(item, quantity, rate, 5m, 0m), interState: false, chargesGst: supplier.ChargesGst);
        var bill = new PurchaseBill(supplier, billNo, date ?? DateOnly.FromDateTime(DateTime.Today), [line], InterState: false);
        return Purchases.Record(bill, Lane, DateTimeOffset.Now, null).PurchaseId;
    }

    private SettledInvoice Sell(Item item, decimal quantity)
    {
        var bill = new InvoiceEngine(HomeState);
        bill.AddItem(item, quantity);

        var basket = new TenderBasket(bill.Totals.AmountPayable);
        basket.Add(TenderType.Cash, bill.Totals.AmountPayable);

        return new CheckoutService(new InvoiceRepository(_temp.Database), new CustomerRepository(_temp.Database),
                new RecordingDrawerService(), null, TimeProvider.System, stock: new StockRepository(_temp.Database))
            .Complete(Lane, bill, basket).Invoice;
    }

    private OrderList Orders(int cover = 14) => new OrderListQuery(_temp.Database).Gather(cover);

    /// <summary>
    /// Ten on the shelf, ten delivered, four sold today: the lane has sold for one day, at four a
    /// day. Sixteen left is four days; two weeks at four a day is 56, less the 16, is 40.
    /// </summary>
    [Fact]
    public void WhatToOrderComesFromTheRateAndTheShelf()
    {
        var dal = Load("DAL", stock: 10m);
        Buy(Supplier("Murugan Traders"), "MT/1", dal, 10m, 100m);
        Sell(dal, 4m);

        var order = Assert.Single(Orders().Suppliers);
        var line = Assert.Single(order.Lines);

        Assert.Equal("Murugan Traders", order.Supplier);
        Assert.Equal(16m, line.Have);
        Assert.Equal(4m, line.PerDay);
        Assert.Equal(4.0m, line.DaysLeft);
        Assert.Equal(40m, line.Order);
        Assert.Equal(100m, line.LastRate);
        Assert.Equal(4_000.00m, line.Cost);
    }

    [Fact]
    public void ALongerCoverOrdersMore() =>
        Assert.True(OrderFor(cover: 28) > OrderFor(cover: 7));

    private decimal OrderFor(int cover)
    {
        if (_temp.Items.FindBySku("DAL") is null)
        {
            var dal = Load("DAL", stock: 20m);
            Sell(dal, 4m);
        }

        return Orders(cover).Suppliers.Single().Lines.Single().Order;
    }

    /// <summary>Goods that came back were not used up, so they do not count towards the rate.</summary>
    [Fact]
    public void ReturnsComeOffTheRate()
    {
        var dal = Load("DAL", stock: 20m);
        var sale = Sell(dal, 4m);

        var notes = new CreditNoteRepository(_temp.Database);
        notes.Issue(CreditNoteDraft.Build(notes.Returnable(sale.InvoiceNo)!, [(1, 2m, true)], TenderType.Cash, null, false), Lane, DateTimeOffset.Now, null);

        Assert.Equal(2m, Orders().Suppliers.Single().Lines.Single().PerDay);
    }

    /// <summary>The wholesaler the shop went to last is the one the order is for.</summary>
    [Fact]
    public void AnItemIsOrderedFromTheSupplierItWasLastBoughtFrom()
    {
        var dal = Load("DAL", stock: 0m);
        Buy(Supplier("Murugan Traders"), "MT/1", dal, 5m, 100m, DateOnly.FromDateTime(DateTime.Today).AddDays(-10));
        Buy(Supplier("Lakshmi Agencies", registered: false), "LA/7", dal, 5m, 96m, DateOnly.FromDateTime(DateTime.Today).AddDays(-2));
        Sell(dal, 8m);

        var order = Assert.Single(Orders().Suppliers);

        Assert.Equal("Lakshmi Agencies", order.Supplier);
        Assert.Equal(96m, order.Lines.Single().LastRate);
    }

    [Fact]
    public void ACancelledBillDoesNotMakeSomebodyTheSupplier()
    {
        var dal = Load("DAL", stock: 0m);
        Buy(Supplier("Murugan Traders"), "MT/1", dal, 5m, 100m, DateOnly.FromDateTime(DateTime.Today).AddDays(-10));
        var wrong = Buy(Supplier("Lakshmi Agencies", registered: false), "LA/7", dal, 5m, 96m);
        Purchases.Void(wrong, "entered against the wrong supplier", Lane, DateTimeOffset.Now);
        Sell(dal, 2m);

        Assert.Equal("Murugan Traders", Assert.Single(Orders().Suppliers).Supplier);
    }

    /// <summary>
    /// Nothing is left off for want of a supplier: an item never bought on a bill is listed with the
    /// others like it, after the suppliers.
    /// </summary>
    [Fact]
    public void ItemsNeverBoughtOnABillAreListedTogetherLast()
    {
        var dal = Load("DAL", stock: 10m);
        var salt = Load("SALT", stock: 1m);
        Buy(Supplier("Murugan Traders"), "MT/1", dal, 10m, 100m);
        Sell(dal, 4m);
        Sell(salt, 1m);

        var suppliers = Orders().Suppliers;

        Assert.Equal(["Murugan Traders", SupplierOrder.NoSupplier], suppliers.Select(s => s.Supplier));
        Assert.Null(suppliers[1].SupplierId);
        Assert.Null(suppliers[1].Lines.Single().LastRate);
        Assert.False(suppliers[1].EstimateIsComplete);
    }

    [Fact]
    public void WithinASupplierWhatRunsOutFirstIsFirst()
    {
        var murugan = Supplier("Murugan Traders");
        var dal = Load("DAL", stock: 10m);
        var rice = Load("RICE", stock: 10m);
        Buy(murugan, "MT/1", dal, 10m, 100m);
        Buy(murugan, "MT/2", rice, 2m, 40m);
        Sell(dal, 2m);
        Sell(rice, 6m);

        Assert.Equal(["Item RICE", "Item DAL"], Orders().Suppliers.Single().Lines.Select(l => l.Name));
    }

    [Fact]
    public void AShelfThatWillLastIsNotOnTheList()
    {
        var dal = Load("DAL", stock: 200m);
        Sell(dal, 1m);

        Assert.True(Orders().IsEmpty);
    }

    [Fact]
    public void SomethingNotCountedIsNotOnTheList()
    {
        var loose = Load("LOOSE", stock: null);
        Sell(loose, 5m);

        Assert.True(Orders().IsEmpty);
    }

    [Fact]
    public void TheListSavesAsASpreadsheet()
    {
        var dal = Load("DAL", stock: 10m);
        Buy(Supplier("Murugan Traders, Madurai"), "MT/1", dal, 10m, 100m);
        Sell(dal, 4m);

        var csv = OrderListFiles.Csv(Orders()).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("supplier,phone,sku,item,unit,have,sells_a_day,days_left,order,last_rate,estimated_cost", csv[0]);
        Assert.Equal("\"Murugan Traders, Madurai\",9443012345,DAL,Item DAL,pc,16,4,4,40,100.00,4000.00", csv[1]);
    }

    // ---- The stock list --------------------------------------------------------------------------

    [Fact]
    public void TheStockListSaysHowManyDaysAreLeft()
    {
        var dal = Load("DAL", stock: 20m);
        Load("SALT", stock: 5m);
        Sell(dal, 4m);

        var levels = new StockRepository(_temp.Database).List();

        var selling = levels.Single(l => l.Sku == "DAL");
        Assert.Equal(4m, selling.PerDay);
        Assert.Equal(4.0m, selling.DaysLeft);

        var still = levels.Single(l => l.Sku == "SALT");
        Assert.Null(still.PerDay);
        Assert.Null(still.DaysLeft);
    }
}
