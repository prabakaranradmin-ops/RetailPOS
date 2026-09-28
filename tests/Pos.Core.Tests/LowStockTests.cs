using System.Text;
using Pos.Core.Analytics;
using Pos.Core.Configuration;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Domain.Import;
using Pos.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Pos.Core.Tests;

/// <summary>
/// When a shelf counts as low — its own reorder level, or a share of full — and the stock sheet
/// that changes counts in bulk.
/// </summary>
public class LowStockTests(ITestOutputHelper output) : IDisposable
{
    private const string Lane = "L1";

    private readonly TempDatabase _temp = new();
    private decimal _percent = LowStock.DefaultPercent;

    public void Dispose() => _temp.Dispose();

    private StockRepository Stock => new(_temp.Database, () => _percent);

    /// <summary>Loads one item the way a catalogue file does.</summary>
    private Item Load(string sku, decimal? stock, decimal? reorder = null, decimal? full = null, UnitType unit = UnitType.Each, string? name = null)
    {
        _temp.Items.UpsertRange([Catalogue.Item(sku: sku, name: name ?? $"Item {sku}", unit: unit) with
        {
            StockQty = stock,
            ReorderLevel = reorder,
            FullLevel = full,
        }]);

        return _temp.Items.FindBySku(sku)!;
    }

    private decimal? FullOf(string sku) => _temp.Items.FindBySku(sku)!.FullLevel;

    // ---- The rule --------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, 100.0, 10.0, 10.0)]   // a tenth of full
    [InlineData(null, 7.0, 10.0, 0.7)]      // a share of a small shelf, not rounded to nothing
    [InlineData(null, 50.0, 25.0, 12.5)]    // the owner's own share
    [InlineData(5.0, 100.0, 10.0, 5.0)]     // the shop's reorder level wins
    [InlineData(null, 100.0, 0.0, null)]    // switched off
    [InlineData(null, null, 10.0, null)]    // nothing known about full
    [InlineData(null, 0.0, 10.0, null)]     // never had anything on the shelf
    public void AnItemWarnsAtItsReorderLevelOrItsShareOfFull(double? reorder, double? full, double percent, double? expected) =>
        Assert.Equal((decimal?)expected, LowStock.WarnAt((decimal?)reorder, (decimal?)full, (decimal)percent));

    [Fact]
    public void AtTheLineIsLowAndOneAboveIsNot()
    {
        Assert.True(LowStock.IsLow(10m, null, 100m, 10m));
        Assert.False(LowStock.IsLow(11m, null, 100m, 10m));
        Assert.False(LowStock.IsLow(null, null, 100m, 10m));
    }

    // ---- What full is ----------------------------------------------------------------------------

    [Fact]
    public void ANewItemStartsFullAtItsFirstCount()
    {
        Load("RICE", stock: 40m);

        Assert.Equal(40m, FullOf("RICE"));
    }

    [Fact]
    public void AnItemNobodyCountsHasNoFullLevel()
    {
        Load("LOOSE", stock: null);

        Assert.Null(FullOf("LOOSE"));
    }

    /// <summary>
    /// A price revision reloads last month's file with last month's counts. That is not a stocktake,
    /// and it must not shrink full to whatever the shelf was down to then.
    /// </summary>
    [Fact]
    public void ReloadingALowerCountDoesNotShrinkFullButAHigherOneRaisesIt()
    {
        Load("RICE", stock: 40m);

        Load("RICE", stock: 12m);
        Assert.Equal(40m, FullOf("RICE"));

        Load("RICE", stock: 60m);
        Assert.Equal(60m, FullOf("RICE"));
    }

    [Fact]
    public void AFullLevelInTheFileIsTheOneThatStands()
    {
        Load("RICE", stock: 40m);
        Load("RICE", stock: null, full: 25m);

        Assert.Equal(25m, FullOf("RICE"));
    }

    [Fact]
    public void SalesAndVoidsNeverMoveFull()
    {
        var rice = Load("RICE", stock: 40m);

        Stock.Move(rice.Id, -30m, StockReason.Sale, Lane, "RM/1");
        Stock.Move(rice.Id, 5m, StockReason.Void, Lane, "RM/1");

        Assert.Equal(15m, _temp.Items.FindBySku("RICE")!.StockQty);
        Assert.Equal(40m, FullOf("RICE"));
    }

    [Fact]
    public void ADeliveryAboveFullRaisesItAndABreakageDoesNotLowerIt()
    {
        var rice = Load("RICE", stock: 40m);

        Stock.Set(rice.Id, 55m, StockReason.Adjust, Lane, "delivery");
        Assert.Equal(55m, FullOf("RICE"));

        Stock.Set(rice.Id, 50m, StockReason.Adjust, Lane, "two packets torn");
        Assert.Equal(55m, FullOf("RICE"));
    }

    // ---- The reorder list ------------------------------------------------------------------------

    [Fact]
    public void AnItemWithNoReorderLevelIsLowAtTenPercentOfFull()
    {
        var at = Load("AT", stock: 100m);
        var above = Load("ABOVE", stock: 100m);

        Stock.Set(at.Id, 10m, StockReason.Adjust, Lane);
        Stock.Set(above.Id, 11m, StockReason.Adjust, Lane);

        var low = Assert.Single(Stock.ListLow());

        Assert.Equal("AT", low.Sku);
        Assert.Equal(100m, low.FullLevel);
        Assert.Equal(10m, low.WarnAt);
        Assert.Equal(10m, low.PercentLeft);
        Assert.Equal(90m, low.ToOrder);
    }

    [Fact]
    public void TheShareIsTheOwners()
    {
        var rice = Load("RICE", stock: 100m);
        Stock.Set(rice.Id, 20m, StockReason.Adjust, Lane);

        Assert.Empty(Stock.ListLow());

        _percent = 25m;
        var low = Assert.Single(Stock.ListLow());
        Assert.Equal(25m, low.WarnAt);
    }

    /// <summary>
    /// Switched off really is off. The share is bound as a number: bound as text, SQLite would
    /// rank '0' above 0 and the rule would fire anyway.
    /// </summary>
    [Fact]
    public void ZeroSwitchesTheShareOfFullOffButNotTheReorderLevels()
    {
        var rice = Load("RICE", stock: 100m);
        Stock.Set(rice.Id, 1m, StockReason.Adjust, Lane);
        Load("DAL", stock: 3m, reorder: 5m);

        _percent = 0m;

        Assert.Equal("DAL", Assert.Single(Stock.ListLow()).Sku);
    }

    [Fact]
    public void TheShopsOwnReorderLevelWinsOverTheShare()
    {
        var dal = Load("DAL", stock: 100m, reorder: 5m);
        Stock.Set(dal.Id, 8m, StockReason.Adjust, Lane);

        // 8 is below a tenth of full, but the shop said to warn at 5.
        Assert.Empty(Stock.ListLow());

        Stock.Set(dal.Id, 5m, StockReason.Adjust, Lane);
        Assert.Equal(5m, Assert.Single(Stock.ListLow()).WarnAt);
    }

    [Fact]
    public void TheTillAndTheOwnersFiguresUseTheSameRule()
    {
        var rice = Load("RICE", stock: 100m);
        Stock.Set(rice.Id, 9m, StockReason.Adjust, Lane);

        var item = _temp.Items.FindBySku("RICE")!;
        Assert.True(item.IsLowAt(10m));
        Assert.False(item.IsLowAt(5m));

        var dashboard = new DashboardQuery(_temp.Database, 10m).Gather(Lane, DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now);
        Assert.Equal("RICE", Assert.Single(dashboard.LowStock).Sku);
        Assert.Empty(new DashboardQuery(_temp.Database, 5m).Gather(Lane, DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now).LowStock);
    }

    // ---- An existing shop upgrading -------------------------------------------------------------

    /// <summary>
    /// A shop that already counts stock should not have to restock everything before the new rule
    /// knows what full is. The upgrade takes the most each item is known to have held.
    /// </summary>
    [Fact]
    public void UpgradingTakesFullFromTheHighestCountOnRecord()
    {
        using var old = new TempDatabase(migrate: false);
        using var connection = old.Database.OpenConnection();

        Migrator.Migrate(connection);
        Execute(connection, "ALTER TABLE items DROP COLUMN full_qty; PRAGMA user_version = 11;");

        Execute(connection, """
            INSERT INTO items (id, sku, hsn_code, name, mrp, sell_price, gst_rate, stock_qty) VALUES
              (1, 'DELIVERED', '0713', 'Delivered', '10', '10', '5', '12'),
              (2, 'NEVER', '0713', 'Never moved', '10', '10', '5', '30'),
              (3, 'LOOSE', '0713', 'Not counted', '10', '10', '5', NULL);
            INSERT INTO stock_movements (item_id, moved_at, lane_id, delta, balance_after, reason) VALUES
              (1, '2026-09-01 10:00:00+05:30', 'L1', '48', '60', 'Adjust'),
              (1, '2026-09-02 10:00:00+05:30', 'L1', '-48', '12', 'Sale');
            """);

        Migrator.Migrate(connection);

        Assert.Equal(60m, old.Items.FindBySku("DELIVERED")!.FullLevel);
        Assert.Equal(30m, old.Items.FindBySku("NEVER")!.FullLevel);
        Assert.Null(old.Items.FindBySku("LOOSE")!.FullLevel);
    }

    // ---- The stock sheet: writing ----------------------------------------------------------------

    [Fact]
    public void TheSheetListsEveryActiveItemWithItsCountAndFull()
    {
        Load("DAL", stock: 12m, name: "Toor Dal 1kg");
        Load("OIL", stock: null, name: "Oil, Groundnut 1L");
        Load("MAL", stock: null, unit: UnitType.Muzham, name: "மல்லிகை பூ");

        var sheet = StockSheet.Write(Stock.Sheet());
        output.WriteLine(sheet);

        var lines = sheet.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();

        Assert.Equal("sku,name,unit,have,full_level,new_count", lines[0]);
        Assert.Contains("DAL,Toor Dal 1kg,Pcs,12,12,", lines);
        Assert.Contains("OIL,\"Oil, Groundnut 1L\",Pcs,,,", lines);
        Assert.Contains("MAL,மல்லிகை பூ,Muzham,,,", lines);
    }

    // ---- The stock sheet: reading ----------------------------------------------------------------

    private StockSheetPlan Read(string csv) => StockSheet.Read(new StringReader(csv), Stock.Sheet());

    [Fact]
    public void OnlyTheRowsFilledInAreChanges()
    {
        Load("DAL", stock: 12m);
        Load("RICE", stock: 4m);
        Load("SUGAR", stock: 7m);

        var plan = Read("""
            sku,name,unit,have,full_level,new_count
            DAL,Item DAL,Pcs,12,12,40
            RICE,Item RICE,Pcs,4,4,
            SUGAR,Item SUGAR,Pcs,7,7,7
            """);

        Assert.True(plan.IsClean);
        var change = Assert.Single(plan.Changes);
        Assert.Equal("DAL", change.Sku);
        Assert.Equal(12m, change.Before);
        Assert.Equal(40m, change.NewCount);
        Assert.Null(change.NewFullLevel);
        Assert.Equal(1, plan.Blank);
        Assert.Equal(1, plan.Unchanged);
    }

    [Fact]
    public void OnlySkuAndNewCountAreNeededAndCaseDoesNotMatter()
    {
        Load("DAL", stock: 12m);

        var plan = Read("SKU,New_Count\ndal,30\n");

        Assert.Equal(30m, Assert.Single(plan.Changes).NewCount);
    }

    /// <summary>All or nothing, and every problem at once, the same as a catalogue file.</summary>
    [Fact]
    public void ASheetWithMistakesChangesNothingAndSaysWhere()
    {
        Load("DAL", stock: 12m);
        Load("SOAP", stock: 5m);
        Load("SUGAR", stock: 7m, unit: UnitType.Kilogram);

        var plan = Read("""
            sku,new_count
            DAL,40
            GHOST,5
            SOAP,-3
            SOAP,4
            DAL2,
            SUGAR,2.75
            ,9
            """);

        Assert.False(plan.IsClean);
        Assert.Empty(plan.Changes);

        Assert.Contains(plan.Problems, p => p.Line == 3 && p.Problem.Contains("No item in the catalogue has SKU 'GHOST'"));
        Assert.Contains(plan.Problems, p => p.Line == 4 && p.Problem.Contains("negative"));
        Assert.Contains(plan.Problems, p => p.Line == 5 && p.Problem.Contains("already on line 4"));
        Assert.Contains(plan.Problems, p => p.Line == 8 && p.Problem.Contains("no SKU"));
        Assert.DoesNotContain(plan.Problems, p => p.Line == 7);
    }

    [Fact]
    public void AThingSoldWholeIsCountedWhole()
    {
        Load("SOAP", stock: 5m);
        Load("SUGAR", stock: 7m, unit: UnitType.Kilogram);

        var plan = Read("sku,new_count\nSOAP,4.5\nSUGAR,2.75\n");

        var problem = Assert.Single(plan.Problems);
        Assert.Equal(2, problem.Line);
        Assert.Contains("whole Pcs", problem.Problem);
    }

    [Fact]
    public void ASheetWithoutANewCountColumnIsRefused()
    {
        Load("DAL", stock: 12m);

        var plan = Read("sku,have\nDAL,40\n");

        Assert.Contains(plan.Problems, p => p.Column == "new_count");
    }

    // ---- The stock sheet: applying ---------------------------------------------------------------

    [Fact]
    public void ApplyingASheetChangesCountsAndStartsCountingNewOnes()
    {
        var dal = Load("DAL", stock: 12m);
        Load("OIL", stock: null);

        var plan = Read("sku,new_count\nDAL,40\nOIL,24\n");
        var counted = Stock.ApplySheet(plan.Changes, Lane, "stock sheet");

        Assert.Equal(2, counted);
        Assert.Equal(40m, _temp.Items.FindBySku("DAL")!.StockQty);
        Assert.Equal(24m, _temp.Items.FindBySku("OIL")!.StockQty);

        // A count that took the shelf higher is what full now means.
        Assert.Equal(40m, FullOf("DAL"));
        Assert.Equal(24m, FullOf("OIL"));

        // And the ledger says why, like any other change.
        var movement = Stock.History(dal.Id).First();
        Assert.Equal(StockReason.Count, movement.Reason);
        Assert.Equal(28m, movement.Delta);
        Assert.Equal("stock sheet", movement.Reference);
    }

    [Fact]
    public void AFullLevelWrittenOnTheSheetStandsOverTheCount()
    {
        Load("DAL", stock: 12m);

        var plan = Read("sku,full_level,new_count\nDAL,30,40\n");
        Stock.ApplySheet(plan.Changes, Lane);

        Assert.Equal(40m, _temp.Items.FindBySku("DAL")!.StockQty);
        Assert.Equal(30m, FullOf("DAL"));
    }

    [Fact]
    public void ASheetSavedAndLoadedBackUnchangedChangesNothing()
    {
        Load("DAL", stock: 12m);
        Load("OIL", stock: null);

        var plan = Read(StockSheet.Write(Stock.Sheet()));

        Assert.True(plan.IsClean);
        Assert.Empty(plan.Changes);
    }

    // ---- The catalogue and the settings ---------------------------------------------------------

    [Fact]
    public void TheCatalogueFileMaySayWhatFullIs()
    {
        var result = new ItemImporter(_temp.Items).Import(new StringReader(
            "sku,barcode,name,hsn_code,unit,mrp,selling_price,gst_rate,is_weighed,stock_qty,full_level\n" +
            "DAL,,Toor Dal 1kg,0713,Pcs,189,189,5,false,12,60\n" +
            "BAD,,Bad Row,0713,Pcs,10,10,5,false,1,-4\n"),
            updateExisting: false,
            dryRun: false);

        Assert.Contains(result.Problems, p => p.Column == "full_level" && p.Problem.Contains("negative"));

        var clean = new ItemImporter(_temp.Items).Import(new StringReader(
            "sku,barcode,name,hsn_code,unit,mrp,selling_price,gst_rate,is_weighed,stock_qty,full_level\n" +
            "DAL,,Toor Dal 1kg,0713,Pcs,189,189,5,false,12,60\n"),
            updateExisting: false,
            dryRun: false);

        Assert.True(clean.Committed, string.Join("; ", clean.Problems));
        Assert.Equal(60m, FullOf("DAL"));
    }

    [Fact]
    public void TheShareIsSavedToTheSettingsFileAndCheckedWhenRead()
    {
        var path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(path, """{ "laneId": "L4" }""", new UTF8Encoding(true));

            Assert.Equal(LowStock.DefaultPercent, PosSettings.LoadOrDefault(path).LowStockPercent);

            SettingsFile.SetLowStockPercent(path, 15m);
            Assert.Equal(15m, PosSettings.LoadOrDefault(path).LowStockPercent);
            Assert.Equal("L4", PosSettings.LoadOrDefault(path).LaneId);

            Assert.Throws<ArgumentOutOfRangeException>(() => SettingsFile.SetLowStockPercent(path, 100m));

            File.WriteAllText(path, """{ "laneId": "L4", "lowStockPercent": 150 }""", new UTF8Encoding(true));
            var refused = Assert.Throws<InvalidOperationException>(() => PosSettings.LoadOrDefault(path));
            Assert.Contains("lowStockPercent", refused.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void Execute(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
