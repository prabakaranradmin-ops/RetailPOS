using Microsoft.Data.Sqlite;
using Pos.Core.Data;
using Pos.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Pos.Core.Tests;

/// <summary>
/// Detecting a damaged database file.
/// </summary>
/// <remarks>
/// A till's database is the shop's book of account. Corruption on a page nobody has read yet is
/// silent until someone reads it, and that will be during a GST filing rather than at a convenient
/// moment — so the check needs to exist, needs to be runnable before a trading day, and needs to
/// actually fail on a damaged file. The last part is what these tests are for: a health check that
/// has never been seen to fail is not known to work.
/// </remarks>
public class DatabaseIntegrityTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "pos-integrity-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string NewDatabasePath()
    {
        Directory.CreateDirectory(_directory);
        return Path.Combine(_directory, $"{Guid.NewGuid():N}.db");
    }

    /// <summary>Builds a database with enough in it to span several pages.</summary>
    private PosDatabase BuildPopulated(int items = 2_000)
    {
        var database = new PosDatabase(NewDatabasePath());
        database.EnsureMigrated();
        new ItemRepository(database).AddRange(Catalogue.Generate(items));

        // Fold the write-ahead log into the file itself, so what is on disk is the whole database
        // and damaging it means damaging real pages.
        using (var connection = database.OpenConnection())
        using (var checkpoint = connection.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            checkpoint.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        return database;
    }

    // ---- A healthy file -----------------------------------------------------------------------

    [Fact]
    public void AFreshDatabaseIsHealthy()
    {
        using var temp = new TempDatabase();

        var report = temp.Database.CheckIntegrity();

        Assert.True(report.IsHealthy, report.ToString());
        Assert.Empty(report.Problems);
        Assert.Equal("ok", report.ToString());
    }

    [Fact]
    public void ADatabaseWithARealDayOfTradingInItIsHealthy()
    {
        var database = BuildPopulated();

        Assert.True(database.CheckIntegrity().IsHealthy);
        Assert.True(database.CheckIntegrity(thorough: false).IsHealthy);
    }

    [Fact]
    public void VacuumLeavesTheDatabaseHealthyAndReadable()
    {
        var database = BuildPopulated(500);
        var before = new ItemRepository(database).Count();

        database.Vacuum();

        Assert.True(database.CheckIntegrity().IsHealthy);
        Assert.Equal(before, new ItemRepository(database).Count());
    }

    // ---- A damaged file -----------------------------------------------------------------------

    /// <summary>
    /// Scribbling over a data page is what a failing disk or a bad USB stick does. The check has to
    /// notice.
    /// </summary>
    [Fact]
    public void ScribblingOverADataPageIsDetected()
    {
        var database = BuildPopulated();
        var path = database.DatabasePath;

        SqliteConnection.ClearAllPools();

        using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            // Well past the header, into pages holding item rows and index entries.
            file.Seek(file.Length / 2, SeekOrigin.Begin);
            file.Write(new byte[4_096]);
            file.Flush(true);
        }

        SqliteConnection.ClearAllPools();

        var report = new PosDatabase(path).CheckIntegrity();

        output.WriteLine(report.ToString());

        Assert.False(report.IsHealthy, "A page of zeroes in the middle of the file went unnoticed.");
        Assert.NotEmpty(report.Problems);
    }

    /// <summary>A file cut short — an interrupted copy, a full disk — is not a database.</summary>
    [Fact]
    public void ATruncatedFileIsDetected()
    {
        var database = BuildPopulated();
        var path = database.DatabasePath;

        SqliteConnection.ClearAllPools();

        using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
            file.SetLength(file.Length / 3);

        SqliteConnection.ClearAllPools();

        var report = new PosDatabase(path).CheckIntegrity();

        output.WriteLine(report.ToString());
        Assert.False(report.IsHealthy);
    }

    /// <summary>
    /// The worst case: a file damaged so badly SQLite will not open it at all. That has to come
    /// back as a report, not as an exception thrown out of a health check.
    /// </summary>
    [Fact]
    public void AFileThatIsNotADatabaseIsReportedRatherThanThrown()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "not-a-database.db");
        File.WriteAllText(path, "this is a text file that somebody renamed");

        var report = new PosDatabase(path).CheckIntegrity();

        output.WriteLine(report.ToString());

        Assert.False(report.IsHealthy);
        Assert.NotEmpty(report.Problems);
    }

    [Fact]
    public void TheHeaderBeingWreckedIsDetected()
    {
        var database = BuildPopulated(200);
        var path = database.DatabasePath;

        SqliteConnection.ClearAllPools();

        using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            file.Seek(0, SeekOrigin.Begin);
            file.Write("NOT SQLITE FORMAT 3\0"u8);
            file.Flush(true);
        }

        SqliteConnection.ClearAllPools();

        var report = new PosDatabase(path).CheckIntegrity();

        output.WriteLine(report.ToString());
        Assert.False(report.IsHealthy);
    }
}
