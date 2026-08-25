using Microsoft.Data.Sqlite;
using Pos.Core.Data;
using Pos.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Pos.Core.Tests;

/// <summary>
/// Taking a snapshot of the lane's database.
/// </summary>
/// <remarks>
/// A copy nobody has checked is a copy nobody knows they can restore, and the moment that is
/// discovered is the moment it is needed. So these care as much about the snapshot being sound and
/// readable as about it existing.
/// </remarks>
public class DatabaseBackupTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempDatabase _temp = new();

    private readonly string _backupDirectory =
        Path.Combine(Path.GetTempPath(), "pos-backup-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _temp.Dispose();

        try
        {
            if (Directory.Exists(_backupDirectory))
                Directory.Delete(_backupDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private DatabaseBackup Backup => new(_temp.Database, _backupDirectory);

    private void SeedCatalogue(int items = 500) => _temp.Items.AddRange(Catalogue.Generate(items));

    [Fact]
    public void ASnapshotIsWrittenAndVerified()
    {
        SeedCatalogue();

        var result = Backup.Create(DateTimeOffset.Now);

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        Assert.True(result.Verified);
        Assert.True(File.Exists(result.Path));
        Assert.True(result.Bytes > 0);

        output.WriteLine($"{result.Path} — {result.Bytes:N0} bytes");
    }

    /// <summary>The point of a backup: everything is in it, and it opens.</summary>
    [Fact]
    public void TheSnapshotHoldsEverythingTheOriginalDid()
    {
        SeedCatalogue(1_200);
        var expected = _temp.Items.Count();

        var result = Backup.Create(DateTimeOffset.Now);

        var restored = new PosDatabase(result.Path);
        Assert.Equal(expected, new ItemRepository(restored).Count());
        Assert.True(restored.CheckIntegrity().IsHealthy);
        Assert.Equal(Migrator.LatestVersion, GetVersion(restored));
    }

    private static int GetVersion(PosDatabase database)
    {
        using var connection = database.OpenConnection();
        return Migrator.GetVersion(connection);
    }

    /// <summary>
    /// The snapshot is a separate database, not a live view. Trading on after a backup must not
    /// change what was captured.
    /// </summary>
    [Fact]
    public void TradingAfterABackupDoesNotChangeIt()
    {
        SeedCatalogue(100);

        var result = Backup.Create(DateTimeOffset.Now);
        var captured = new ItemRepository(new PosDatabase(result.Path)).Count();

        _temp.Items.AddRange(Catalogue.Generate(50, seed: 99).Select(i => i with { Sku = "LATER-" + i.Sku, Barcode = null }));

        Assert.Equal(150, _temp.Items.Count());
        Assert.Equal(captured, new ItemRepository(new PosDatabase(result.Path)).Count());
    }

    /// <summary>A backup must not block anyone billing — the till cannot pause for it.</summary>
    [Fact]
    public void BillingCarriesOnWhileTheBackupRuns()
    {
        SeedCatalogue(2_000);

        using var connection = _temp.Database.OpenConnection();
        using var reading = connection.CreateCommand();
        reading.CommandText = "SELECT COUNT(*) FROM items;";
        var before = Convert.ToInt32(reading.ExecuteScalar());

        var result = Backup.Create(DateTimeOffset.Now);

        // The same open connection still works, and a write goes through afterwards.
        Assert.Equal(before, Convert.ToInt32(reading.ExecuteScalar()));
        Assert.True(result.Succeeded, string.Join("; ", result.Problems));

        _temp.Items.Add(Catalogue.Item(sku: "AFTER-BACKUP", name: "Sold during the backup"));
        Assert.Equal(before + 1, _temp.Items.Count());
    }

    [Fact]
    public void SnapshotsAreNamedByWhenTheyWereTaken()
    {
        SeedCatalogue(10);

        var takenAt = new DateTimeOffset(2026, 8, 26, 21, 30, 15, TimeSpan.FromHours(5.5));
        var result = Backup.Create(takenAt);

        Assert.Equal("pos-20260826-213015.db", Path.GetFileName(result.Path));
        Assert.Equal(takenAt.DateTime, DatabaseBackup.TimestampOf(result.Path)!.Value.DateTime);
    }

    /// <summary>A backup must never quietly replace another one.</summary>
    [Fact]
    public void TwoSnapshotsInTheSameSecondBothSurvive()
    {
        SeedCatalogue(10);

        var takenAt = new DateTimeOffset(2026, 8, 26, 21, 30, 15, TimeSpan.FromHours(5.5));

        var first = Backup.Create(takenAt);
        var second = Backup.Create(takenAt);

        Assert.NotEqual(first.Path, second.Path);
        Assert.True(File.Exists(first.Path));
        Assert.True(File.Exists(second.Path));
    }

    [Fact]
    public void OlderSnapshotsArePrunedToTheRetentionLimit()
    {
        SeedCatalogue(10);

        var backup = Backup;
        var start = new DateTimeOffset(2026, 8, 1, 21, 0, 0, TimeSpan.FromHours(5.5));

        for (var day = 0; day < 10; day++)
            backup.Create(start.AddDays(day), keep: 5);

        var remaining = backup.Existing();

        Assert.Equal(5, remaining.Count);

        // The five kept are the five most recent.
        Assert.Equal("pos-20260810-210000.db", remaining[0].Name);
        Assert.Equal("pos-20260806-210000.db", remaining[^1].Name);
    }

    [Fact]
    public void TheNewestSnapshotIsListedFirst()
    {
        SeedCatalogue(10);

        var backup = Backup;
        var start = new DateTimeOffset(2026, 8, 1, 21, 0, 0, TimeSpan.FromHours(5.5));

        backup.Create(start);
        backup.Create(start.AddDays(1));
        backup.Create(start.AddDays(2));

        Assert.Equal(
            ["pos-20260803-210000.db", "pos-20260802-210000.db", "pos-20260801-210000.db"],
            backup.Existing().Select(f => f.Name).ToArray());
    }

    [Fact]
    public void KeepingFewerThanOneSnapshotIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Backup.Create(DateTimeOffset.Now, keep: 0));
    }

    /// <summary>A backup that cannot be written has to say so rather than throwing.</summary>
    [Fact]
    public void AFailedBackupIsReportedRatherThanThrown()
    {
        SeedCatalogue(10);

        // A path that cannot be a directory, because a file of that name is in the way.
        var blocked = Path.Combine(Path.GetTempPath(), $"pos-blocked-{Guid.NewGuid():N}");
        File.WriteAllText(blocked, "in the way");

        try
        {
            var result = new DatabaseBackup(_temp.Database, blocked).Create(DateTimeOffset.Now);

            Assert.False(result.Succeeded);
            Assert.NotEmpty(result.Problems);
            output.WriteLine(string.Join("; ", result.Problems));
        }
        finally
        {
            File.Delete(blocked);
        }
    }

    [Fact]
    public void ListingAnEmptyOrAbsentFolderIsNotAnError()
    {
        Assert.Empty(new DatabaseBackup(_temp.Database, Path.Combine(_backupDirectory, "never-used")).Existing());
    }

    [Fact]
    public void SomethingElseInTheFolderIsIgnored()
    {
        SeedCatalogue(10);
        Directory.CreateDirectory(_backupDirectory);
        File.WriteAllText(Path.Combine(_backupDirectory, "notes.txt"), "not a backup");

        var backup = Backup;
        backup.Create(DateTimeOffset.Now);

        Assert.Single(backup.Existing());
    }
}
