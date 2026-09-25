using System.IO;
using Pos.App.ViewModels;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// Loading a catalogue from the owner's screen, which is the only way a shop that will not open a
/// command prompt can price its shelves.
/// </summary>
/// <remarks>
/// What a catalogue may contain is settled in the importer and tested there. These are about the
/// screen in front of it: that nothing is written until a check comes back clean, that the check
/// stops counting once what it was run against has changed, and that a file which cannot be read is
/// a message rather than a till that has fallen over mid-queue.
/// </remarks>
public class CatalogueImportTests : IDisposable
{
    private const string Header = "sku,barcode,name,hsn_code,unit,mrp,selling_price,gst_rate,is_weighed";

    private const string TwoGoodRows = $"""
        {Header}
        DAL001,8901234567890,Toor Dal 1kg,0713,Pcs,189.00,189.00,5,false
        SUG001,,Sugar Loose,1701,Kg,45.00,45.00,5,true
        """;

    private readonly TempDatabase _temp = new();
    private readonly Dictionary<string, string> _files = [];

    public void Dispose() => _temp.Dispose();

    private ItemRepository Items => new(_temp.Database);

    /// <summary>
    /// A catalogue screen reading files that only exist in this test. The path is a key rather than
    /// somewhere on disk, so a missing file can be asked for without arranging one.
    /// </summary>
    private CatalogueImportViewModel Build() => new(
        Items,
        path => _files.TryGetValue(path, out var content)
            ? new StringReader(content)
            : throw new FileNotFoundException($"No such file: {path}", path));

    private CatalogueImportViewModel Loaded(string name, string content)
    {
        _files[name] = content;

        var screen = Build();
        screen.FilePath = name;

        return screen;
    }

    // ---- The ordinary run ------------------------------------------------------------------------

    [Fact]
    public void AFileIsCheckedFirstAndNothingIsWrittenByTheCheck()
    {
        var screen = Loaded("catalogue.csv", TwoGoodRows);

        Assert.False(screen.CanImport);

        screen.Check();

        Assert.True(screen.CanImport);
        Assert.Empty(screen.Problems);
        Assert.Contains("add 2", screen.Verdict);
        Assert.Contains("Nothing has been written", screen.Verdict);

        // The catalogue is exactly as it was: a check is a question, not an instruction.
        Assert.Equal(0, Items.Count());
    }

    [Fact]
    public void AndThenTheImportLandsIt()
    {
        var screen = Loaded("catalogue.csv", TwoGoodRows);

        screen.Check();
        screen.Import();

        Assert.Equal(2, Items.Count());
        Assert.Equal("Toor Dal 1kg", Items.FindBySku("DAL001")!.Name);
        Assert.Contains("2 item(s) added", screen.Verdict);
    }

    /// <summary>
    /// The till reads the item master straight out of the database, so a catalogue loaded while the
    /// shop is open is sellable at the counter without anybody restarting anything.
    /// </summary>
    [Fact]
    public void WhatLandsCanBeScannedImmediately()
    {
        var screen = Loaded("catalogue.csv", TwoGoodRows);

        screen.Check();
        screen.Import();

        Assert.Equal("DAL001", Items.FindByBarcode("8901234567890")!.Sku);
    }

    // ---- What stops it ---------------------------------------------------------------------------

    [Fact]
    public void ABadFileIsRefusedWithEveryProblemAtOnceAndNothingIsWritten()
    {
        var screen = Loaded("bad.csv", $"""
            {Header}
            DAL001,8901234567890,Toor Dal 1kg,0713,Pcs,189.00,999.00,5,false
            SUG001,,Sugar Loose,1701,Kg,45.00,45.00,7,true
            """);

        screen.Check();

        Assert.False(screen.CanImport);
        Assert.Equal(2, screen.ProblemCount);
        Assert.Equal(2, screen.Problems.Count);

        // Named by line and column, because the fix happens in a spreadsheet rather than here.
        Assert.Contains(screen.Problems, p => p.Where == "line 2" && p.Column == "selling_price");
        Assert.Contains(screen.Problems, p => p.Where == "line 3" && p.Column == "gst_rate");

        Assert.Equal(0, Items.Count());
    }

    /// <summary>
    /// The button is the only thing holding this back, so it is worth proving that going round it
    /// writes nothing either.
    /// </summary>
    [Fact]
    public void ImportingWithoutCheckingIsRefused()
    {
        var screen = Loaded("catalogue.csv", TwoGoodRows);

        screen.Import();

        Assert.Equal(0, Items.Count());
        Assert.Contains("Check the file first", screen.Verdict);
    }

    [Fact]
    public void ImportingATwiceCheckedFileOnlyLoadsItOnce()
    {
        var screen = Loaded("catalogue.csv", TwoGoodRows);

        screen.Check();
        screen.Import();
        screen.Import();

        Assert.Equal(2, Items.Count());
    }

    /// <summary>
    /// A clean check belongs to the file it was run against. Picking another one and pressing import
    /// would otherwise write a file nobody had looked at.
    /// </summary>
    [Fact]
    public void ChoosingAnotherFileWithdrawsTheCheck()
    {
        var screen = Loaded("catalogue.csv", TwoGoodRows);
        screen.Check();

        Assert.True(screen.CanImport);

        _files["other.csv"] = TwoGoodRows;
        screen.FilePath = "other.csv";

        Assert.False(screen.CanImport);
        Assert.Contains("Check the file again", screen.Verdict);
    }

    /// <summary>
    /// And to the mode. Insert-only and update give different answers about the same rows, so a
    /// check run as one is not permission to import as the other.
    /// </summary>
    [Fact]
    public void ChangingTheModeWithdrawsItToo()
    {
        var screen = Loaded("catalogue.csv", TwoGoodRows);
        screen.Check();

        Assert.True(screen.CanImport);

        screen.UpdateExisting = true;

        Assert.False(screen.CanImport);
    }

    [Fact]
    public void AFileThatChangedAfterTheCheckIsRefusedRatherThanWritten()
    {
        var screen = Loaded("catalogue.csv", TwoGoodRows);
        screen.Check();

        Assert.True(screen.CanImport);

        // Somebody saved the spreadsheet again between the check and the press, and broke a rate.
        _files["catalogue.csv"] = $"""
            {Header}
            DAL001,8901234567890,Toor Dal 1kg,0713,Pcs,189.00,189.00,7,false
            """;

        screen.Import();

        Assert.Equal(0, Items.Count());
        Assert.False(screen.CanImport);
        Assert.Contains("changed since it was checked", screen.Verdict);
        Assert.Contains(screen.Problems, p => p.Column == "gst_rate");
    }

    // ---- A re-import -----------------------------------------------------------------------------

    [Fact]
    public void ASkuAlreadyInTheCatalogueIsAMistakeUntilTheOwnerSaysItIsAPriceChange()
    {
        var first = Loaded("catalogue.csv", TwoGoodRows);
        first.Check();
        first.Import();

        var revision = Loaded("revised.csv", $"""
            {Header}
            DAL001,8901234567890,Toor Dal 1kg,0713,Pcs,199.00,199.00,5,false
            """);

        revision.Check();

        Assert.False(revision.CanImport);
        Assert.Contains(revision.Problems, p => p.Column == "sku");
        Assert.Equal(189.00m, Items.FindBySku("DAL001")!.SellPrice);

        revision.UpdateExisting = true;
        revision.Check();
        revision.Import();

        Assert.Equal(199.00m, Items.FindBySku("DAL001")!.SellPrice);
        Assert.Equal(2, Items.Count());
    }

    /// <summary>
    /// The reorder list is built from what the catalogue carries, so a file with counts in it has to
    /// reach the stock tab as well as the item master.
    /// </summary>
    [Fact]
    public void ShelfCountsInTheFileReachTheReorderList()
    {
        var screen = Loaded("catalogue.csv", $"""
            sku,barcode,name,hsn_code,unit,mrp,selling_price,gst_rate,is_weighed,stock_qty,reorder_level
            DAL001,8901234567890,Toor Dal 1kg,0713,Pcs,189.00,189.00,5,false,3,10
            """);

        screen.Check();
        screen.Import();

        var low = new StockRepository(_temp.Database).ListLow(50);

        Assert.Single(low);
        Assert.True(low[0].IsLow);
    }

    [Fact]
    public void AnImportAnnouncesItselfSoTheScreenAroundItCanReRead()
    {
        var screen = Loaded("catalogue.csv", TwoGoodRows);

        var announced = 0;
        screen.Imported += (_, _) => announced++;

        screen.Check();
        Assert.Equal(0, announced);

        screen.Import();
        Assert.Equal(1, announced);
    }

    // ---- When the file will not open -------------------------------------------------------------

    /// <summary>
    /// Open in Excel, moved, renamed, on a pen drive that has been pulled out. None of them may take
    /// the till down: the owner's screen says what happened and the counter carries on selling.
    /// </summary>
    [Fact]
    public void AFileThatCannotBeReadIsAMessageRatherThanACrash()
    {
        var screen = Build();
        screen.FilePath = "not-there.csv";

        screen.Check();

        Assert.False(screen.CanImport);
        Assert.Contains("could not be read", screen.Verdict);
        Assert.Empty(screen.Problems);
    }

    [Fact]
    public void AnEmptyFileIsNotSomethingToImport()
    {
        var screen = Loaded("empty.csv", Header);

        screen.Check();

        Assert.False(screen.CanImport);
        Assert.Contains("no item rows", screen.Verdict);
    }

    [Fact]
    public void NothingCanBeCheckedUntilAFileIsChosen()
    {
        var screen = Build();

        Assert.False(screen.CanCheck);
        Assert.False(screen.CanImport);

        screen.Check();

        Assert.Contains("Choose the catalogue file first", screen.Verdict);
    }
}
