using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// Dead stock: counted, on the shelf, and not sold for two months - most money tied up first.
/// </summary>
public class DeadStockTests : IDisposable
{
    private readonly TempDatabase _temp = new();

    public void Dispose() => _temp.Dispose();

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    /// <summary>A clock stopped at a given moment, for a sale rung up in the past.</summary>
    private sealed class At(DateTimeOffset moment) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => moment.ToUniversalTime();
    }

    private Item Load(string sku, decimal? stock, decimal? cost = null, decimal price = 100m)
    {
        _temp.Items.UpsertRange([Catalogue.Item(sku: sku, name: $"Item {sku}", price: price) with { StockQty = stock, CostPrice = cost }]);
        return _temp.Items.FindBySku(sku)!;
    }

    private void SellDaysAgo(Item item, int daysAgo)
    {
        var bill = new InvoiceEngine("33");
        bill.AddItem(item);

        var basket = new TenderBasket(bill.Totals.AmountPayable);
        basket.Add(TenderType.Cash, bill.Totals.AmountPayable);

        new CheckoutService(new InvoiceRepository(_temp.Database), new CustomerRepository(_temp.Database),
                new RecordingDrawerService(), null, new At(DateTimeOffset.Now.AddDays(-daysAgo)))
            .Complete("L1", bill, basket);
    }

    /// <summary>What an item was added to the catalogue days ago looks like: its first price that long ago.</summary>
    private void AddedDaysAgo(Item item, int daysAgo)
    {
        using var connection = _temp.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE price_changes SET changed_at = $at WHERE item_id = $id;";
        command.Parameters.AddWithValue("$at", DateTime.Now.AddDays(-daysAgo).ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", item.Id);
        command.ExecuteNonQuery();
    }

    private IReadOnlyList<DeadStockItem> Dead(int days = DeadStock.Days) =>
        new DeadStockRepository(_temp.Database).NotSelling(Today, days);

    [Fact]
    public void SomethingNotSoldForTwoMonthsIsDead()
    {
        var soap = Load("SOAP", stock: 12m, cost: 30m);
        AddedDaysAgo(soap, 200);
        SellDaysAgo(soap, 75);

        var dead = Assert.Single(Dead());

        Assert.Equal("Item SOAP", dead.Name);
        Assert.Equal(75, dead.DaysSinceSold);
        Assert.Equal(12m, dead.Have);
        Assert.Equal(360.00m, dead.TiedUp);
        Assert.Equal("move it to the front, or stop ordering it", dead.Advice);
    }

    [Fact]
    public void SomethingSoldThisMonthIsNot()
    {
        var soap = Load("SOAP", stock: 12m);
        AddedDaysAgo(soap, 200);
        SellDaysAgo(soap, 10);

        Assert.Empty(Dead());
    }

    [Fact]
    public void NeverSoldIsDeadOnlyOnceItHasBeenInTheShopAWhile()
    {
        var old = Load("OLD", stock: 5m, cost: 40m);
        var fresh = Load("NEW", stock: 5m, cost: 40m);
        AddedDaysAgo(old, 90);

        var dead = Assert.Single(Dead());

        Assert.Equal("OLD", dead.Sku);
        Assert.True(dead.NeverSold);
        Assert.Null(dead.DaysSinceSold);
        Assert.StartsWith("never sold", dead.Advice);
    }

    /// <summary>Only a count says anything is on the shelf: uncounted and empty shelves are not dead stock.</summary>
    [Fact]
    public void OnlyWhatIsCountedAndOnTheShelfCounts()
    {
        foreach (var item in new[] { Load("LOOSE", stock: null), Load("GONE", stock: 0m) })
            AddedDaysAgo(item, 200);

        Assert.Empty(Dead());
    }

    [Fact]
    public void AVoidedSaleIsNotASale()
    {
        var soap = Load("SOAP", stock: 12m);
        AddedDaysAgo(soap, 200);
        SellDaysAgo(soap, 75);
        SellDaysAgo(soap, 3);

        var latest = new InvoiceRepository(_temp.Database).FindLatest("L1")!;
        new InvoiceRepository(_temp.Database).Void(latest.InvoiceNo, DateTimeOffset.Now, "rung up by mistake");

        Assert.Equal(75, Assert.Single(Dead()).DaysSinceSold);
    }

    [Fact]
    public void TheMostMoneyTiedUpComesFirst()
    {
        var cheap = Load("CHEAP", stock: 10m, cost: 5m);
        var dear = Load("DEAR", stock: 2m, cost: 400m);
        var unknown = Load("UNPRICED", stock: 50m);

        foreach (var item in new[] { cheap, dear, unknown })
            AddedDaysAgo(item, 200);

        Assert.Equal(["DEAR", "CHEAP", "UNPRICED"], Dead().Select(d => d.Sku));
    }

    [Fact]
    public void ALongerWindowListsLess()
    {
        var soap = Load("SOAP", stock: 12m);
        AddedDaysAgo(soap, 200);
        SellDaysAgo(soap, 75);

        Assert.Single(Dead(60));
        Assert.Empty(Dead(90));
    }

    [Fact]
    public void FourMonthsUnsoldIsWorthClearingAtALoss()
    {
        var soap = Load("SOAP", stock: 12m);
        AddedDaysAgo(soap, 300);
        SellDaysAgo(soap, 130);

        Assert.Equal("put it on offer to clear it", Assert.Single(Dead()).Advice);
    }

    [Theory]
    [InlineData(13)]
    [InlineData(366)]
    public void AWindowThatMeansNothingIsRefused(int days) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Dead(days));
}
