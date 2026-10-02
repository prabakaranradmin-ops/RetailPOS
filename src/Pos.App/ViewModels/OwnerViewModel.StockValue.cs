using Pos.Core.Analytics;
using Pos.Core.Domain;

namespace Pos.App.ViewModels;

/// <summary>What the counted shelves are worth now, for the Stock tab.</summary>
public sealed partial class OwnerViewModel
{
    private string _stockWorthLine = string.Empty;
    private string _stockWorthDetail = string.Empty;

    /// <summary>At cost, at the selling price and at MRP, in one line.</summary>
    public string StockWorthLine
    {
        get => _stockWorthLine;
        private set => Set(ref _stockWorthLine, value);
    }

    /// <summary>Which departments hold the most, and what the value leaves out.</summary>
    public string StockWorthDetail
    {
        get => _stockWorthDetail;
        private set => Set(ref _stockWorthDetail, value);
    }

    private void FillStockValue(DashboardData d)
    {
        var stock = d.Stock;

        if (stock.CountedItems == 0)
        {
            StockWorthLine = "Nothing counted is on the shelves, so there is no stock value to give.";
            StockWorthDetail = stock.BelowZero > 0 ? $"{Plural.Of(stock.BelowZero, "count")} below zero - those need a recount." : string.Empty;
            return;
        }

        StockWorthLine = stock.WithoutCost == stock.CountedItems
            ? $"What the shelves are worth: {Show.Money(stock.AtSellingPrice)} at selling price, {Show.Money(stock.AtMrp)} at MRP."
            : $"What the shelves are worth: {Show.Money(stock.AtCost)} at cost, {Show.Money(stock.AtSellingPrice)} at selling price, {Show.Money(stock.AtMrp)} at MRP.";

        var detail = new List<string>();

        if (stock.Categories.Count > 0)
        {
            detail.Add("Most in " + string.Join(", ", stock.Categories.Take(3).Select(c => $"{c.Category} {Show.Money(c.AtSellingPrice)}")));
        }

        if (stock.WithoutCost > 0)
        {
            detail.Add(stock.WithoutCost == stock.CountedItems
                ? "no item has a cost price yet, so there is no value at cost"
                : $"{Plural.Of(stock.WithoutCost, "item")} with no cost price {(stock.WithoutCost == 1 ? "is" : "are")} not in the value at cost");
        }
        else
        {
            detail.Add($"{Show.Money(stock.Margin)} over cost if it all sold at today's prices");
        }

        if (stock.BelowZero > 0)
            detail.Add($"{Plural.Of(stock.BelowZero, "count")} below zero left out - recount {(stock.BelowZero == 1 ? "it" : "them")}");

        StockWorthDetail = string.Join("; ", detail) + ".";
    }
}
