using System.Diagnostics;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Pos.Core.Tests;

/// <summary>
/// NFR-01: item lookup plus grid append completes in well under 100ms at catalogue sizes up to
/// ~100k active SKUs. These build a real catalogue of that size and measure.
/// </summary>
/// <remarks>
/// Budgets here are deliberately far below the 100ms requirement rather than right at it. A test
/// that only fails at 99ms tells you nothing until the day it is already too slow, and CI runners
/// are slower and noisier than a till. The measured figures are written to the test output so a
/// regression is visible as a trend, not just as a pass or a fail.
/// </remarks>
public class LookupLatencyTests(ITestOutputHelper output)
{
    private const int CatalogueSize = 100_000;

    /// <summary>Ceiling for the scanner path — a single seek on a unique index.</summary>
    private const int BarcodeBudgetMs = 20;

    /// <summary>Ceiling for typed search, which has to scan for a name substring.</summary>
    private const int SearchBudgetMs = 60;

    private static TempDatabase BuildCatalogue()
    {
        var temp = new TempDatabase();
        temp.Items.AddRange(Catalogue.Generate(CatalogueSize));
        return temp;
    }

    private static double Measure(int iterations, Action action)
    {
        // One warm-up pass, so the figure reflects steady-state cost rather than the first query's
        // page cache misses and statement preparation.
        action();

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
            action();
        stopwatch.Stop();

        return stopwatch.Elapsed.TotalMilliseconds / iterations;
    }

    [Fact]
    public void CatalogueBuildsToTheExpectedSize()
    {
        using var temp = BuildCatalogue();
        Assert.Equal(CatalogueSize, temp.Items.Count());
    }

    [Fact]
    public void ScanningABarcodeResolvesWellInsideTheBudget()
    {
        using var temp = BuildCatalogue();
        var barcodes = new[] { "8900000000001", "8900000050000", "8900000099999", "8900000012345" };
        var next = 0;

        var average = Measure(200, () => temp.Items.FindByBarcode(barcodes[next++ % barcodes.Length]));

        output.WriteLine($"barcode lookup over {CatalogueSize:N0} SKUs: {average:F3} ms average");
        Assert.True(average < BarcodeBudgetMs, $"Barcode lookup averaged {average:F3} ms, over the {BarcodeBudgetMs} ms budget.");
    }

    [Fact]
    public void TypedSearchResolvesWellInsideTheBudget()
    {
        using var temp = BuildCatalogue();
        var queries = new[] { "Toor", "Basmati", "Sugar", "Soap", "Coffee" };
        var next = 0;

        var average = Measure(50, () => temp.Items.Search(queries[next++ % queries.Length]));

        output.WriteLine($"typed search over {CatalogueSize:N0} SKUs: {average:F3} ms average");
        Assert.True(average < SearchBudgetMs, $"Typed search averaged {average:F3} ms, over the {SearchBudgetMs} ms budget.");
    }

    /// <summary>
    /// A query matching nothing is the worst case for the substring scan: there is no early exit
    /// and the result limit never kicks in.
    /// </summary>
    [Fact]
    public void AQueryThatMatchesNothingStaysInsideTheBudget()
    {
        using var temp = BuildCatalogue();

        var average = Measure(20, () => temp.Items.Search("zzzznosuchitem"));

        output.WriteLine($"worst-case miss over {CatalogueSize:N0} SKUs: {average:F3} ms average");
        Assert.True(average < SearchBudgetMs, $"A missing-item search averaged {average:F3} ms, over the {SearchBudgetMs} ms budget.");
    }

    /// <summary>
    /// The requirement covers lookup *and* the line reaching the grid, so this measures the whole
    /// step the cashier actually waits on.
    /// </summary>
    [Fact]
    public void ScanToLineOnTheBillStaysWellInsideTheBudget()
    {
        using var temp = BuildCatalogue();
        var bill = new InvoiceEngine("33");
        var next = 1;

        var average = Measure(200, () =>
        {
            var item = temp.Items.FindByBarcode($"890{next++ % CatalogueSize + 1:D10}");

            if (item is not null)
                bill.AddItem(item);
        });

        output.WriteLine($"scan to line appended: {average:F3} ms average, {bill.Lines.Count:N0} lines on the bill");
        Assert.True(average < BarcodeBudgetMs, $"Scan to line averaged {average:F3} ms, over the {BarcodeBudgetMs} ms budget.");
    }
}
