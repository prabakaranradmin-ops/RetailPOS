using Microsoft.Data.Sqlite;
using Pos.Core.Data;
using Pos.TestSupport;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// Backups copied off this computer, onto a pen drive.
/// </summary>
/// <remarks>
/// The drives here are folders standing in for pen drives. What is tested is what a shop depends on:
/// the copy is the snapshot byte for byte, a drive is only filled when it is the shop's, and the
/// reminder comes when the last copy is a week old.
/// </remarks>
public class OffMachineCopyTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 21, 5, 0, TimeSpan.FromHours(5.5));

    private readonly TempDatabase _temp = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pos-offmachine-tests", Guid.NewGuid().ToString("N"));
    private readonly List<CopyDrive> _plugged = [];
    private int _sold;

    public OffMachineCopyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _temp.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Backups => Path.Combine(_root, "backups");

    private OffMachineCopy Copier(string lane = "L1") => new(Backups, lane, () => _plugged);

    private CopyDrive PlugIn(string name, bool alreadyTheShops = false)
    {
        var root = Directory.CreateDirectory(Path.Combine(_root, "drives", name)).FullName;

        if (alreadyTheShops)
            Directory.CreateDirectory(Path.Combine(root, OffMachineCopy.FolderName));

        var drive = new CopyDrive(root, $"{name} (E:)");
        _plugged.Add(drive);
        return drive;
    }

    private string Snapshot(DateTimeOffset at)
    {
        // Something new in the books each time, so no two snapshots are the same file.
        _temp.Items.AddRange([Catalogue.Item(sku: $"S{++_sold}", name: "Toor Dal 1kg", price: 189m)]);

        var result = new DatabaseBackup(_temp.Database, Backups).Create(at);
        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        return result.Path;
    }

    // ---- The copy --------------------------------------------------------------------------------

    [Fact]
    public void TheCopyIsTheSnapshotByteForByteInTheLanesFolder()
    {
        var drive = PlugIn("SHOP");
        var snapshot = Snapshot(Now);

        var copy = Copier().Copy(snapshot, drive, Now);

        Assert.True(copy.Succeeded, copy.Detail);
        Assert.Equal(Path.Combine(drive.Root, OffMachineCopy.FolderName, "L1", Path.GetFileName(snapshot)), copy.Path);
        Assert.Equal(File.ReadAllBytes(snapshot), File.ReadAllBytes(copy.Path!));
        Assert.Contains("SHOP (E:)", copy.Detail);

        // Nothing half-written is left beside it.
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(copy.Path)!, "*.part"));
    }

    /// <summary>The copy restores: it is a database with the shop's books in it, not merely a file of the right size.</summary>
    [Fact]
    public void TheCopyOpensAsTheShopsBooks()
    {
        var drive = PlugIn("SHOP");
        var copy = Copier().Copy(Snapshot(Now), drive, Now);

        var opened = new PosDatabase(copy.Path!);
        var report = opened.CheckIntegrity();

        // This database's handles only: clearing every pool would pull connections out from under
        // the tests running beside this one.
        using (var connection = new SqliteConnection(opened.ConnectionString))
            SqliteConnection.ClearPool(connection);

        Assert.True(report.IsHealthy, report.ToString());
    }

    [Fact]
    public void TwoLanesShareADriveWithoutMixingTheirCopies()
    {
        var drive = PlugIn("SHOP");
        var snapshot = Snapshot(Now);

        Copier("L1").Copy(snapshot, drive, Now);
        Copier("L2").Copy(snapshot, drive, Now);

        Assert.Single(Copier("L1").CopiesOn(drive));
        Assert.Single(Copier("L2").CopiesOn(drive));
        Assert.NotEqual(Copier("L1").FolderOn(drive), Copier("L2").FolderOn(drive));
    }

    [Fact]
    public void OnlyTheNewestCopiesAreKeptOnTheDrive()
    {
        var drive = PlugIn("SHOP");
        var copier = Copier();

        for (var day = 0; day < 5; day++)
            copier.Copy(Snapshot(Now.AddDays(day)), drive, Now.AddDays(day), keep: 3);

        var kept = copier.CopiesOn(drive);

        Assert.Equal(3, kept.Count);
        Assert.Equal($"pos-{Now.AddDays(4):yyyyMMdd-HHmmss}.db", kept[0].Name);
    }

    [Fact]
    public void ADriveThatCannotBeWrittenToIsAFailureInWordsNotAnException()
    {
        var snapshot = Snapshot(Now);

        // A "drive" whose root is a file, which no folder can be made inside: as near as a test gets
        // to a write-protected stick or one pulled out half way.
        var blocked = Path.Combine(_root, "not-a-drive");
        File.WriteAllText(blocked, "x");

        var copy = Copier().Copy(snapshot, new CopyDrive(blocked, "LOCKED (F:)"), Now);

        Assert.False(copy.Succeeded);
        Assert.Contains("LOCKED (F:)", copy.Detail);
        Assert.Null(Copier().LastCopied());
    }

    [Fact]
    public void ASnapshotThatIsNotThereIsNotCopied()
    {
        var copy = Copier().Copy(Path.Combine(Backups, "pos-20260101-000000.db"), PlugIn("SHOP"), Now);

        Assert.False(copy.Succeeded);
        Assert.Contains("no snapshot", copy.Detail);
    }

    // ---- Which drive -----------------------------------------------------------------------------

    /// <summary>A drive becomes the shop's with its first copy, and only then is it filled without asking.</summary>
    [Fact]
    public void ADriveBecomesTheShopsWithItsFirstCopy()
    {
        var drive = PlugIn("SHOP");
        var copier = Copier();

        Assert.Empty(copier.Marked());

        copier.Copy(Snapshot(Now), drive, Now);

        Assert.Equal([drive], copier.Marked());
    }

    [Fact]
    public void WithOnePenDriveInThatIsTheOneToCopyTo()
    {
        var only = PlugIn("SHOP");

        Assert.Equal(only, Copier().Choose());
    }

    /// <summary>A customer's pen drive left in the PC beside the shop's is not the one chosen.</summary>
    [Fact]
    public void TheShopsDriveIsChosenOverAnyOther()
    {
        PlugIn("CUSTOMER");
        var shops = PlugIn("SHOP", alreadyTheShops: true);

        Assert.Equal(shops, Copier().Choose());
    }

    /// <summary>Two drives and neither the shop's: guessing would be writing to somebody else's.</summary>
    [Fact]
    public void TwoStrangeDrivesAreNotGuessedBetween()
    {
        PlugIn("ONE");
        PlugIn("TWO");

        Assert.Null(Copier().Choose());
    }

    [Fact]
    public void NoDriveMeansNothingToChoose()
    {
        Assert.Null(Copier().Choose());
        Assert.Empty(Copier().Plugged());
    }

    // ---- The reminder ----------------------------------------------------------------------------

    [Fact]
    public void ALaneThatHasNeverCopiedIsTold()
    {
        Assert.Null(Copier().LastCopied());
        Assert.Contains("never been copied", Copier().Overdue(Now));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(6, false)]
    [InlineData(7, true)]
    [InlineData(30, true)]
    public void TheReminderComesWhenTheLastCopyIsAWeekOld(int daysAgo, bool reminded)
    {
        var copiedAt = Now.AddDays(-daysAgo);
        Copier().Copy(Snapshot(copiedAt), PlugIn("SHOP"), copiedAt);

        var overdue = Copier().Overdue(Now);

        Assert.Equal(copiedAt, Copier().LastCopied());
        Assert.Equal(reminded, overdue is not null);

        if (reminded)
            Assert.Contains($"{daysAgo} days ago", overdue);
    }

    /// <summary>The newest copy is the one that counts, whatever order the record was written in.</summary>
    [Fact]
    public void TheLastCopyIsTheNewestOneRecorded()
    {
        var drive = PlugIn("SHOP");
        var copier = Copier();

        copier.Copy(Snapshot(Now), drive, Now);
        copier.Copy(Snapshot(Now.AddDays(-3)), drive, Now.AddDays(-3));

        Assert.Equal(Now, copier.LastCopied());
    }
}
