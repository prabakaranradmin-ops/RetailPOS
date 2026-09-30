using System.IO;
using Pos.App.ViewModels;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Hardware.Printing;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// Prices and shelf labels on the Catalogue tab, the way an owner does a price revision: save the
/// sheet, fill it in, load it back, print the labels it put out of date.
/// </summary>
public class PricesScreenTests : IDisposable
{
    private readonly TempDatabase _temp = new();
    private readonly LoopbackPrinterService _printer = new();
    private readonly List<IReadOnlyList<ShelfLabel>> _printed = [];

    public void Dispose() => _temp.Dispose();

    private string Folder => Path.GetDirectoryName(_temp.Database.DatabasePath)!;

    private PricesViewModel Screen(Func<IReadOnlyList<ShelfLabel>, PrintOutcome>? print = null)
    {
        var screen = new PricesViewModel(
            new PriceRepository(_temp.Database),
            print ?? (labels =>
            {
                _printed.Add(labels);
                return PrintOutcome.Printed(100);
            }),
            "Sri Murugan Stores",
            () => new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.FromHours(5.5)));

        screen.LoadLabels();
        return screen;
    }

    /// <summary>Two items, with their first labels already on the shelf.</summary>
    private void Stocked()
    {
        _temp.Items.AddRange(
        [
            Catalogue.Item(sku: "DAL", name: "Toor Dal 1kg", price: 189m),
            Catalogue.Item(sku: "SUG", name: "Sugar Loose", price: 45m, unit: UnitType.Kilogram),
        ]);

        var prices = new PriceRepository(_temp.Database);
        prices.MarkLabelled(prices.LabelsDue().Select(l => l.ItemId), DateTimeOffset.Now);
    }

    /// <summary>The sheet saved from the screen, with a new price typed against one line.</summary>
    private string FilledSheet(PricesViewModel screen, string sku, string newPrice)
    {
        var path = Path.Combine(Folder, screen.SuggestedSheetName);
        Assert.Null(screen.SaveSheet(path));

        var lines = File.ReadAllLines(path)
            .Select(line => line.StartsWith(sku + ",", StringComparison.Ordinal) ? line + newPrice : line);

        var filled = Path.Combine(Folder, "filled.csv");
        File.WriteAllLines(filled, lines);
        return filled;
    }

    [Fact]
    public void ARevisionFromTheSheetPutsTheItemOnTheLabelsDue()
    {
        Stocked();
        var screen = Screen();
        Assert.Empty(screen.Labels);
        Assert.Contains("Every shelf label is up to date", screen.LabelsHeadline);

        var plan = screen.CheckSheet(FilledSheet(screen, "DAL", "179"));

        Assert.NotNull(plan);
        Assert.Contains("Change the price of 1 item?", PricesViewModel.Question(plan!));
        Assert.Contains("\n\n1 row is blank: it is left alone. Nothing else", PricesViewModel.Question(plan!));

        Assert.Null(screen.ApplySheet(plan!));

        Assert.Equal("Toor Dal 1kg", Assert.Single(screen.Labels).Name);
        Assert.Contains("1 label due", screen.LabelsHeadline);
        Assert.Equal("1 price changed from the sheet. Its shelf label is due - print it below.", screen.Status);
    }

    /// <summary>
    /// It said "1 row are blank and 0 match what the price already is": the verb for several rows,
    /// and a count of none read out.
    /// </summary>
    [Theory]
    [InlineData(3, 1, "3 rows are blank and 1 row gives the price the item already has: those are left alone.")]
    [InlineData(1, 0, "1 row is blank: it is left alone.")]
    [InlineData(0, 2, "2 rows give the price the item already has: those are left alone.")]
    [InlineData(0, 1, "1 row gives the price the item already has: it is left alone.")]
    [InlineData(0, 0, "")]
    public void WhatASheetLeavesAloneIsSaidInTheRightNumber(int blank, int unchanged, string expected)
    {
        Assert.Equal(expected, SheetWords.LeftAlone(blank, unchanged, "price"));
    }

    [Fact]
    public void ASheetWithAMistakeChangesNothingAndSaysWhere()
    {
        Stocked();
        var screen = Screen();

        Assert.Null(screen.CheckSheet(FilledSheet(screen, "DAL", "250")));

        Assert.True(screen.HasSheetProblems);
        Assert.Contains("above the MRP", Assert.Single(screen.SheetProblems));
        Assert.Contains("No price was changed", screen.Status);
        Assert.Equal(189m, _temp.Items.FindBySku("DAL")!.SellPrice);
    }

    [Fact]
    public void TheQuestionNamesAPriceBelowCost()
    {
        _temp.Items.AddRange([Catalogue.Item(sku: "DAL", name: "Toor Dal 1kg", price: 189m) with { CostPrice = 150m }]);
        var screen = Screen();

        var plan = screen.CheckSheet(FilledSheet(screen, "DAL", "140"))!;

        Assert.Contains("below the 150.00 it costs", PricesViewModel.Question(plan));
    }

    [Fact]
    public void PrintedLabelsAreNoLongerDue()
    {
        Stocked();
        var screen = Screen();
        screen.ApplySheet(screen.CheckSheet(FilledSheet(screen, "SUG", "44"))!);

        Assert.Null(screen.Print());

        Assert.Equal("Sugar Loose", Assert.Single(Assert.Single(_printed)).Name);
        Assert.Empty(screen.Labels);
        Assert.Contains("Printed 1 label", screen.Status);
    }

    [Fact]
    public void LabelsThatDidNotPrintAreStillDue()
    {
        Stocked();
        var screen = Screen(_ => PrintOutcome.Failed("out of paper"));
        screen.ApplySheet(screen.CheckSheet(FilledSheet(screen, "SUG", "44"))!);

        Assert.NotNull(screen.Print());

        Assert.Single(screen.Labels);
        Assert.Contains("out of paper", screen.Status);
    }

    [Fact]
    public void WithNoPrinterTheScreenSaysToSaveAPage()
    {
        Stocked();
        var screen = Screen(_ => PrintOutcome.NotConfigured());
        screen.EveryItem = true;

        screen.Print();

        Assert.Contains("Save the labels as a page instead", screen.Status);
        Assert.Equal(2, screen.Labels.Count);
    }

    [Fact]
    public void ThePageIsSavedAndTheLabelsAreDone()
    {
        _temp.Items.AddRange([Catalogue.Item(sku: "DAL", name: "Toor Dal 1kg", barcode: "8901234567890", price: 189m)]);
        var screen = Screen();
        var path = Path.Combine(Folder, screen.SuggestedLabelsName);

        Assert.Equal("shelf-labels-2026-09-29.html", screen.SuggestedLabelsName);
        Assert.Null(screen.SavePage(path));

        Assert.Contains("<svg", File.ReadAllText(path));
        Assert.Empty(screen.Labels);
    }

    [Fact]
    public void EveryItemListsTheWholeCatalogue()
    {
        Stocked();
        var screen = Screen();

        screen.EveryItem = true;

        Assert.Equal(2, screen.Labels.Count);
        Assert.Contains("a label for everything", screen.LabelsHeadline);
        Assert.True(screen.CanPrint);
    }
}
