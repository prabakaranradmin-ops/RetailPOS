using Pos.Core.Analytics;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Domain.Import;
using Pos.TestSupport;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// The two optional catalogue columns, and the two charts they exist for.
/// </summary>
/// <remarks>
/// Both are optional in every sense that matters: absent from the file, absent from a row, or
/// absent from the history of an item sold before the shop started recording them. The tests that
/// earn their keep here are the ones proving that absence is carried through as absence — an item
/// with no cost must never appear to keep everything it sells for.
/// </remarks>
public class CategoryAndCostTests : IDisposable
{
    private const string Lane = "L1";
    private const string HomeState = "33";

    private readonly TempDatabase _temp = new();
    private readonly DateTimeOffset _today = new(2026, 8, 26, 10, 0, 0, TimeSpan.FromHours(5.5));

    public void Dispose() => _temp.Dispose();

    private static ImportResult Import(ItemRepository items, string csv, bool update = false) =>
        new ItemImporter(items).Import(new StringReader(csv), update);

    private const string Header = "sku,barcode,name,hsn_code,unit,mrp,selling_price,gst_rate,is_weighed";
    private const string HeaderPlus = Header + ",category,cost_price";

    // ---- Import: the columns are genuinely optional ---------------------------------------------

    [Fact]
    public void ACatalogueWrittenBeforeTheseColumnsExistedStillImports()
    {
        var items = new ItemRepository(_temp.Database);

        var result = Import(items, $"""
            {Header}
            DAL001,8901234567890,Toor Dal 1kg,0713,Pcs,189.00,189.00,5,false
            """);

        Assert.True(result.IsClean);
        Assert.Equal(1, result.Inserted);

        var item = items.FindBySku("DAL001")!;
        Assert.Null(item.Category);
        Assert.Null(item.CostPrice);
        Assert.Null(item.MarginPercent);
    }

    [Fact]
    public void BothColumnsAreReadWhenTheyArePresent()
    {
        var items = new ItemRepository(_temp.Database);

        var result = Import(items, $"""
            {HeaderPlus}
            DAL001,8901234567890,Toor Dal 1kg,0713,Pcs,189.00,189.00,5,false,Staples,162.00
            """);

        Assert.True(result.IsClean);

        var item = items.FindBySku("DAL001")!;
        Assert.Equal("Staples", item.Category);
        Assert.Equal(162.00m, item.CostPrice);

        // (189 - 162) / 189 = 14.29%
        Assert.Equal(14.29m, item.MarginPercent);
    }

    /// <summary>A blank cell in a column that exists means "not said", not zero.</summary>
    [Fact]
    public void ABlankCellLeavesTheValueUnset()
    {
        var items = new ItemRepository(_temp.Database);

        var result = Import(items, $"""
            {HeaderPlus}
            DAL001,8901234567890,Toor Dal 1kg,0713,Pcs,189.00,189.00,5,false,,
            """);

        Assert.True(result.IsClean);

        var item = items.FindBySku("DAL001")!;
        Assert.Null(item.Category);
        Assert.Null(item.CostPrice);
    }

    [Fact]
    public void AReImportCanAddThemToACatalogueThatIsAlreadyLoaded()
    {
        var items = new ItemRepository(_temp.Database);

        Import(items, $"""
            {Header}
            DAL001,8901234567890,Toor Dal 1kg,0713,Pcs,189.00,189.00,5,false
            """);

        var result = Import(items, $"""
            {HeaderPlus}
            DAL001,8901234567890,Toor Dal 1kg,0713,Pcs,189.00,189.00,5,false,Staples,162.00
            """, update: true);

        Assert.True(result.IsClean);
        Assert.Equal(1, result.Updated);
        Assert.Equal("Staples", items.FindBySku("DAL001")!.Category);
    }

    // ---- Import: what is refused ---------------------------------------------------------------

    [Fact]
    public void ACostAboveTheSellingPriceIsRefused()
    {
        var result = Import(new ItemRepository(_temp.Database), $"""
            {HeaderPlus}
            DAL001,8901234567890,Toor Dal 1kg,0713,Pcs,189.00,189.00,5,false,Staples,200.00
            """);

        Assert.False(result.IsClean);
        Assert.Contains(result.Problems, p => p.ToString().Contains("above the selling price", StringComparison.Ordinal));
    }

    [Fact]
    public void ANegativeCostIsRefused()
    {
        var result = Import(new ItemRepository(_temp.Database), $"""
            {HeaderPlus}
            DAL001,8901234567890,Toor Dal 1kg,0713,Pcs,189.00,189.00,5,false,Staples,-5.00
            """);

        Assert.False(result.IsClean);
    }

    [Fact]
    public void ACostEqualToTheSellingPriceIsAllowed()
    {
        // A shop selling something at exactly what it paid is unusual, not wrong.
        var result = Import(new ItemRepository(_temp.Database), $"""
            {HeaderPlus}
            DAL001,8901234567890,Toor Dal 1kg,0713,Pcs,189.00,189.00,5,false,Staples,189.00
            """);

        Assert.True(result.IsClean);
    }

    [Fact]
    public void ACostThatIsNotANumberIsRefusedRatherThanIgnored()
    {
        var result = Import(new ItemRepository(_temp.Database), $"""
            {HeaderPlus}
            DAL001,8901234567890,Toor Dal 1kg,0713,Pcs,189.00,189.00,5,false,Staples,about 160
            """);

        Assert.False(result.IsClean);
    }

    // ---- The snapshot ---------------------------------------------------------------------------

    /// <summary>
    /// Both are recorded onto the line as it is sold. Changing the catalogue afterwards must not
    /// restate a bill that has already been issued.
    /// </summary>
    [Fact]
    public void TheDepartmentAndCostAreFixedAtTheMomentOfSale()
    {
        var items = new ItemRepository(_temp.Database);

        Import(items, $"""
            {HeaderPlus}
            DAL001,8901234567890,Toor Dal 1kg,0713,Pcs,189.00,189.00,5,false,Staples,162.00
            """);

        var invoices = new InvoiceRepository(_temp.Database);
        var sold = InvoiceLine.FromItem(items.FindBySku("DAL001")!);
        var saved = invoices.Save(Sale([sold]));

        // The shop moves it and renegotiates its cost the next morning.
        Import(items, $"""
            {HeaderPlus}
            DAL001,8901234567890,Toor Dal 1kg,0713,Pcs,189.00,189.00,5,false,Pulses,150.00
            """, update: true);

        var reloaded = invoices.FindByInvoiceNo(saved.InvoiceNo)!;

        Assert.Equal("Staples", reloaded.Sale.Lines[0].CategorySnapshot);
        Assert.Equal(162.00m, reloaded.Sale.Lines[0].CostSnapshot);
    }

    [Fact]
    public void AnItemWithNoCostSellsPerfectlyWellAndCarriesNoCostOnTheLine()
    {
        var items = new ItemRepository(_temp.Database);

        Import(items, $"""
            {Header}
            DAL001,8901234567890,Toor Dal 1kg,0713,Pcs,189.00,189.00,5,false
            """);

        var invoices = new InvoiceRepository(_temp.Database);
        var saved = invoices.Save(Sale([InvoiceLine.FromItem(items.FindBySku("DAL001")!)]));
        var reloaded = invoices.FindByInvoiceNo(saved.InvoiceNo)!;

        Assert.Equal(189m, reloaded.Sale.Totals.GrandTotal);
        Assert.Null(reloaded.Sale.Lines[0].CostSnapshot);
        Assert.Null(reloaded.Sale.Lines[0].CategorySnapshot);
    }

    // ---- The charts -----------------------------------------------------------------------------

    [Fact]
    public void TakingsAreSplitByDepartmentWithTheUnfiledInTheirOwnBucket()
    {
        var items = new ItemRepository(_temp.Database);

        Import(items, $"""
            {HeaderPlus}
            DAL001,8901234567890,Toor Dal 1kg,0713,Pcs,189.00,189.00,5,false,Staples,162.00
            SHP001,8901234567906,Shampoo,3305,Pcs,299.00,299.00,18,false,Personal Care,196.00
            MYS001,8901234567913,Mystery Item,0713,Pcs,100.00,100.00,5,false,,
            """);

        var invoices = new InvoiceRepository(_temp.Database);

        foreach (var sku in new[] { "DAL001", "SHP001", "MYS001" })
            invoices.Save(Sale([InvoiceLine.FromItem(items.FindBySku(sku)!)]));

        var d = Dashboard();

        Assert.Equal(3, d.Categories.Count);
        Assert.Equal(299m, d.Categories.Single(c => c.Category == "Personal Care").NetSales);
        Assert.Equal(189m, d.Categories.Single(c => c.Category == "Staples").NetSales);
        Assert.Equal(100m, d.Categories.Single(c => c.Category == "Uncategorised").NetSales);

        // Nothing vanishes: the departments add up to the takings.
        Assert.Equal(d.Range.NetSales, d.Categories.Sum(c => c.NetSales));
    }

    [Fact]
    public void MarginIsComputedFromWhatWasPaidAndWhatWasCharged()
    {
        var items = new ItemRepository(_temp.Database);

        Import(items, $"""
            {HeaderPlus}
            DAL001,8901234567890,Toor Dal 1kg,0713,Pcs,200.00,200.00,5,false,Staples,150.00
            """);

        var invoices = new InvoiceRepository(_temp.Database);
        invoices.Save(Sale([InvoiceLine.FromItem(items.FindBySku("DAL001")!, quantity: 3m)]));

        var d = Dashboard();
        var item = Assert.Single(d.Margins.Priced);

        Assert.Equal(600m, item.NetSales);
        Assert.Equal(450m, item.Cost);
        Assert.Equal(150m, item.Profit);
        Assert.Equal(25m, item.MarginPercent);
        Assert.Equal(100m, d.Margins.Coverage);
    }

    /// <summary>
    /// The failure this whole design guards against: an item with no cost recorded must not be
    /// plotted as keeping the whole of what it sells for. It is excluded, and the page says how
    /// much of the shop it therefore cannot speak for.
    /// </summary>
    [Fact]
    public void ItemsWithNoCostAreExcludedFromMarginAndReportedAsAGap()
    {
        var items = new ItemRepository(_temp.Database);

        Import(items, $"""
            {HeaderPlus}
            DAL001,8901234567890,Toor Dal 1kg,0713,Pcs,200.00,200.00,5,false,Staples,150.00
            MYS001,8901234567906,Mystery Item,0713,Pcs,300.00,300.00,5,false,Staples,
            """);

        var invoices = new InvoiceRepository(_temp.Database);
        invoices.Save(Sale([InvoiceLine.FromItem(items.FindBySku("DAL001")!)]));
        invoices.Save(Sale([InvoiceLine.FromItem(items.FindBySku("MYS001")!)]));

        var d = Dashboard();

        Assert.Single(d.Margins.Priced);
        Assert.Equal("Toor Dal 1kg", d.Margins.Priced[0].Name);

        Assert.Equal(1, d.Margins.UnpricedItems);
        Assert.Equal(300m, d.Margins.UnpricedSales);

        // 200 of 500 is 40% of the takings covered.
        Assert.Equal(40m, d.Margins.Coverage);
    }

    [Fact]
    public void AShopWithNoCostsAtAllGetsAnEmptyPictureRatherThanAWrongOne()
    {
        var items = new ItemRepository(_temp.Database);

        Import(items, $"""
            {Header}
            DAL001,8901234567890,Toor Dal 1kg,0713,Pcs,189.00,189.00,5,false
            """);

        new InvoiceRepository(_temp.Database).Save(Sale([InvoiceLine.FromItem(items.FindBySku("DAL001")!)]));

        var d = Dashboard();

        Assert.Empty(d.Margins.Priced);
        Assert.Equal(0m, d.Margins.Coverage);
        Assert.Equal(189m, d.Margins.UnpricedSales);

        // And the page says so, and says what to do about it, rather than drawing an empty grid.
        var html = DashboardPage.Render(d, "Test Shop");
        Assert.Contains("no margin to show", html);
        Assert.Contains("cost_price", html);
        Assert.Contains("Uncategorised", html);
    }

    [Fact]
    public void TheQuadrantSplitsAtTheShopsOwnMiddle()
    {
        var items = new ItemRepository(_temp.Database);

        Import(items, $"""
            {HeaderPlus}
            A001,8901234567890,Fast Thin,0713,Pcs,100.00,100.00,5,false,Staples,90.00
            B001,8901234567906,Slow Fat,0713,Pcs,100.00,100.00,5,false,Staples,20.00
            C001,8901234567913,Middle,0713,Pcs,100.00,100.00,5,false,Staples,50.00
            """);

        var invoices = new InvoiceRepository(_temp.Database);
        invoices.Save(Sale([InvoiceLine.FromItem(items.FindBySku("A001")!, quantity: 50m)]));
        invoices.Save(Sale([InvoiceLine.FromItem(items.FindBySku("B001")!, quantity: 1m)]));
        invoices.Save(Sale([InvoiceLine.FromItem(items.FindBySku("C001")!, quantity: 10m)]));

        var d = Dashboard();

        // Medians of 50/1/10 units and 10/80/50 per cent.
        Assert.Equal(10m, d.Margins.MedianQuantity);
        Assert.Equal(50m, d.Margins.MedianMargin);
    }

    // ---- Helpers --------------------------------------------------------------------------------

    private DashboardData Dashboard() =>
        new DashboardQuery(_temp.Database).Gather(Lane, _today.AddDays(-1), _today.AddDays(1));

    private SaleDraft Sale(InvoiceLine[] lines)
    {
        var totals = InvoiceTotals.From(lines);

        return new SaleDraft(Lane, _today, null, lines, totals,
            [new Tender(TenderType.Cash, totals.GrandTotal)], 0m, 0, 0, null);
    }
}
