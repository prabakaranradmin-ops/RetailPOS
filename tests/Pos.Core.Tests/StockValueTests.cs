using Pos.Core.Analytics;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// What the counted shelves are worth now - at cost, at the selling price and at MRP - and by
/// department.
/// </summary>
public class StockValueTests : IDisposable
{
    private readonly TempDatabase _temp = new();

    public void Dispose() => _temp.Dispose();

    private StockValue Value() =>
        new DashboardQuery(_temp.Database).Gather("L1", DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1)).Stock;

    private void Shelve(string sku, decimal? have, decimal price, decimal? cost, string? category = null, decimal? mrp = null, bool active = true) =>
        _temp.Items.UpsertRange([Catalogue.Item(sku: sku, name: $"Item {sku}", price: price, active: active) with
        {
            StockQty = have,
            CostPrice = cost,
            Category = category,
            Mrp = mrp ?? price,
        }]);

    [Fact]
    public void NothingCountedIsWorthNothing()
    {
        Shelve("A", have: null, price: 100m, cost: 80m);

        var value = Value();

        Assert.Equal(0, value.CountedItems);
        Assert.Equal(0m, value.AtSellingPrice);
    }

    [Fact]
    public void TheShelvesAreValuedAtCostAtSellingPriceAndAtMrpToThePaisa()
    {
        Shelve("DAL", have: 10m, price: 189m, cost: 150.50m, category: "Grocery", mrp: 195m);
        Shelve("OIL", have: 2.5m, price: 165m, cost: 140m, category: "Oils", mrp: 170m);

        var value = Value();

        Assert.Equal(2, value.CountedItems);
        Assert.Equal(1_505.00m + 350.00m, value.AtCost);
        Assert.Equal(1_890.00m + 412.50m, value.AtSellingPrice);
        Assert.Equal(1_950.00m + 425.00m, value.AtMrp);
        Assert.Equal(2_302.50m - 1_855.00m, value.Margin);
        Assert.Equal(0, value.WithoutCost);
    }

    /// <summary>
    /// An item with no cost price is in the selling value but not the cost one, and the margin is
    /// worked only over the items that have one - or it would count their whole price as profit.
    /// </summary>
    [Fact]
    public void AnItemWithNoCostIsLeftOutOfTheValueAtCostAndSaidSo()
    {
        Shelve("DAL", have: 10m, price: 100m, cost: 80m);
        Shelve("NEW", have: 5m, price: 50m, cost: null);

        var value = Value();

        Assert.Equal(800m, value.AtCost);
        Assert.Equal(1_250m, value.AtSellingPrice);
        Assert.Equal(1_000m, value.CostedAtSellingPrice);
        Assert.Equal(200m, value.Margin);
        Assert.Equal(1, value.WithoutCost);
    }

    /// <summary>Nothing on the shelf adds nothing, and a count below zero is a count gone wrong, not stock.</summary>
    [Fact]
    public void EmptyAndBelowZeroAreLeftOut()
    {
        Shelve("DAL", have: 10m, price: 100m, cost: 80m);
        Shelve("ZERO", have: 0m, price: 100m, cost: 80m);
        Shelve("MINUS", have: -3m, price: 100m, cost: 80m);
        Shelve("GONE", have: 7m, price: 100m, cost: 80m, active: false);

        var value = Value();

        Assert.Equal(1, value.CountedItems);
        Assert.Equal(1_000m, value.AtSellingPrice);
        Assert.Equal(1, value.BelowZero);
    }

    [Fact]
    public void EachDepartmentIsValuedMostFirst()
    {
        Shelve("DAL", have: 10m, price: 100m, cost: 80m, category: "Grocery");
        Shelve("RICE", have: 4m, price: 300m, cost: 250m, category: "grocery");
        Shelve("OIL", have: 1m, price: 165m, cost: 140m, category: "Oils");
        Shelve("MISC", have: 1m, price: 10m, cost: null);

        var departments = Value().Categories;

        // "Grocery" and "grocery" are one department, as the owner meant them.
        Assert.Equal(3, departments.Count);
        Assert.Equal("Grocery", departments[0].Category);
        Assert.Equal(2, departments[0].Items);
        Assert.Equal(1_800m, departments[0].AtCost);
        Assert.Equal(2_200m, departments[0].AtSellingPrice);
        Assert.Equal("Oils", departments[1].Category);
        Assert.Equal(DashboardQuery.Uncategorised, departments[2].Category);
    }

    [Fact]
    public void TheSavedPageCarriesTheValue()
    {
        Shelve("DAL", have: 10m, price: 189m, cost: 150m, category: "Grocery");

        var page = DashboardPage.Render(new DashboardQuery(_temp.Database).Gather("L1", DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1)), "Sri Lakshmi Stores");

        Assert.Contains("What the shelves are worth", page);
        Assert.Contains("1,890.00", page);
        Assert.Contains("Grocery", page);
    }
}
