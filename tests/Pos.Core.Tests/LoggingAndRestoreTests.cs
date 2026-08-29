using Microsoft.Data.Sqlite;
using Pos.Core.Data;
using Pos.Core.Logging;
using Pos.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Pos.Core.Tests;

/// <summary>
/// The lane's log. Its purpose is a pilot that can be diagnosed, so what matters is that entries
/// reach disk, stay readable, and never take the till down.
/// </summary>
public class FileLogTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "pos-log-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private FileLog New(LogLevel minimum = LogLevel.Debug, long maxBytes = FileLog.DefaultMaxBytes, int retentionDays = FileLog.DefaultRetentionDays, TimeProvider? clock = null) =>
        new(_directory, minimum, maxBytes, retentionDays, clock);

    private string ReadAll() =>
        string.Concat(new DirectoryInfo(_directory).EnumerateFiles("pos-*.log").OrderBy(f => f.Name).Select(f => File.ReadAllText(f.FullName)));

    [Fact]
    public void AnEntryReachesDiskImmediately()
    {
        using var log = New();

        log.Info("sale", "L1-2026-000001 total 189.00");

        // Not after a flush, not on dispose — a log still in a buffer when the power goes out is a
        // log of exactly the moment nobody can explain.
        Assert.Contains("L1-2026-000001 total 189.00", ReadAll());
    }

    /// <summary>
    /// A byte order mark turns the first line into a near-match that looks like a match, for
    /// whoever is grepping the file to work out what a lane did.
    /// </summary>
    [Fact]
    public void TheFileHasNoByteOrderMark()
    {
        using var log = New();

        log.Info("startup", "lane L1");

        var bytes = File.ReadAllBytes(new DirectoryInfo(_directory).EnumerateFiles("pos-*.log").Single().FullName);

        Assert.NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal((byte)'2', bytes[0]);
    }

    [Fact]
    public void EveryEntryCarriesATimeALevelAndACategory()
    {
        using var log = New();

        log.Warn("printer", "out of paper");

        var line = ReadAll().Trim();

        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\s+WARN\s+printer\s+out of paper$", line);
    }

    [Fact]
    public void AnExceptionIsRecordedWithItsTypeAndStack()
    {
        using var log = New();

        try
        {
            throw new InvalidOperationException("the drawer is jammed");
        }
        catch (Exception ex)
        {
            log.Error("drawer", "kick failed", ex);
        }

        var text = ReadAll();

        Assert.Contains("InvalidOperationException: the drawer is jammed", text);
        Assert.Contains("at ", text);
    }

    /// <summary>An entry that spans lines would break every grep run against the file.</summary>
    [Fact]
    public void AMultiLineMessageIsFlattenedOntoOneLine()
    {
        using var log = New();

        log.Info("import", "line one\r\nline two\nline three");

        var lines = File.ReadAllLines(new DirectoryInfo(_directory).EnumerateFiles("pos-*.log").Single().FullName);

        Assert.Single(lines);
        Assert.Contains("line one line two line three", lines[0]);
    }

    [Fact]
    public void EntriesBelowTheThresholdAreNotWritten()
    {
        using var log = New(minimum: LogLevel.Warning);

        log.Debug("x", "debug");
        log.Info("x", "info");
        log.Warn("x", "warning");

        var text = ReadAll();

        Assert.DoesNotContain("debug", text);
        Assert.DoesNotContain("info", text);
        Assert.Contains("warning", text);
    }

    [Fact]
    public void TheFileIsNamedForTheDay()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 26, 21, 30, 0, TimeSpan.FromHours(5.5)));
        using var log = New(clock: clock);

        log.Info("x", "hello");

        Assert.Equal("pos-20260826.log", Path.GetFileName(new DirectoryInfo(_directory).EnumerateFiles().Single().FullName));
    }

    [Fact]
    public void TheDayRollsOverToANewFile()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 26, 23, 59, 0, TimeSpan.FromHours(5.5)));
        using var log = New(clock: clock);

        log.Info("x", "before midnight");
        clock.Advance(TimeSpan.FromMinutes(2));
        log.Info("x", "after midnight");

        Assert.Equal(2, log.Existing().Count);
        Assert.Contains("before midnight", File.ReadAllText(Path.Combine(_directory, "pos-20260826.log")));
        Assert.Contains("after midnight", File.ReadAllText(Path.Combine(_directory, "pos-20260827.log")));
    }

    /// <summary>
    /// <para>
    /// The file is named for the date the timestamp carries, whatever timezone the machine
    /// happens to be set to.
    /// </para>
    /// <para>
    /// The rollover test above only catches this on a machine that is not in IST: it feeds
    /// +05:30 timestamps, so on an Indian lane the buggy conversion and the correct one agree
    /// and the test passes without proving anything. It went green on the build machine and red
    /// on CI, which runs in UTC, for a year of commits.
    /// </para>
    /// <para>
    /// Every case here is the same wall-clock time on the same date, written with a different
    /// offset, and every one must produce the same filename. Reading the machine's timezone
    /// instead of the timestamp's own can satisfy at most the one case that happens to match the
    /// machine, so this fails somewhere no matter where it is run.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(-8)]
    [InlineData(0)]
    [InlineData(5.5)]
    [InlineData(13)]
    public void TheFileIsNamedForTheTimestampsOwnDayNotTheMachines(double offsetHours)
    {
        var clock = new FakeTimeProvider(
            new DateTimeOffset(2026, 8, 26, 23, 59, 0, TimeSpan.FromHours(offsetHours)));

        using var log = New(clock: clock);

        log.Info("x", "late on the twenty-sixth");

        Assert.Equal(
            "pos-20260826.log",
            Path.GetFileName(new DirectoryInfo(_directory).EnumerateFiles().Single().FullName));
    }

    /// <summary>An unusually busy day must not grow one file without limit.</summary>
    [Fact]
    public void ABigDayRollsToAnotherPart()
    {
        using var log = New(maxBytes: 2_000);

        for (var i = 0; i < 200; i++)
            log.Info("sale", $"invoice number {i} with enough text on the line to make the file grow");

        var files = log.Existing();

        Assert.True(files.Count > 1, "the file should have rolled to another part");
        Assert.Contains(files, f => f.Name.Contains(".1.", StringComparison.Ordinal));
    }

    [Fact]
    public void OldDaysArePrunedOnceTheyPassRetention()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.FromHours(5.5)));
        using var log = New(retentionDays: 7, clock: clock);

        for (var day = 0; day < 20; day++)
        {
            log.Info("x", $"day {day}");
            clock.Advance(TimeSpan.FromDays(1));
        }

        log.Info("x", "today");

        Assert.True(log.Existing().Count <= 9, $"kept {log.Existing().Count} files with a 7-day window");
        Assert.DoesNotContain(log.Existing(), f => f.Name == "pos-20260601.log");
    }

    /// <summary>
    /// A lane that cannot write its log still has to be able to sell things. A disk problem must
    /// never surface as an exception in the middle of a sale.
    /// </summary>
    [Fact]
    public void AFailureToWriteIsSwallowed()
    {
        // A path that cannot be a directory, because a file of that name is in the way.
        var blocked = Path.Combine(Path.GetTempPath(), $"pos-log-blocked-{Guid.NewGuid():N}");
        File.WriteAllText(blocked, "in the way");

        try
        {
            using var log = new FileLog(blocked);

            var exception = Record.Exception(() => log.Info("sale", "this cannot be written anywhere"));

            Assert.Null(exception);
        }
        finally
        {
            File.Delete(blocked);
        }
    }

    [Fact]
    public void WritingFromManyThreadsAtOnceLosesNothing()
    {
        using var log = New();

        Parallel.For(0, 200, i => log.Info("sale", $"entry {i:D3}"));

        var text = ReadAll();

        for (var i = 0; i < 200; i++)
            Assert.Contains($"entry {i:D3}", text);
    }

    [Fact]
    public void TheNullLogWritesNothingAndNeverThrows()
    {
        var exception = Record.Exception(() => NullLog.Instance.Error("x", "boom", new InvalidOperationException()));

        Assert.Null(exception);
    }

    /// <summary>A clock the test drives, so day rollover and retention can be checked without waiting.</summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly TimeZoneInfo _zone;
        private DateTimeOffset _now;

        public FakeTimeProvider(DateTimeOffset start)
        {
            _now = start;
            _zone = TimeZoneInfo.CreateCustomTimeZone("test", start.Offset, "test", "test");
        }

        public override DateTimeOffset GetUtcNow() => _now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone => _zone;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}

/// <summary>
/// Putting a snapshot back as the live database. Nothing here may destroy anything: a restore that
/// half happened would leave a shop with no books at all.
/// </summary>
public class DatabaseRestoreTests : IDisposable
{
    private readonly ITestOutputHelper output;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "pos-restore-tests", Guid.NewGuid().ToString("N"));

    public DatabaseRestoreTests(ITestOutputHelper testOutput)
    {
        output = testOutput;
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string LivePath => Path.Combine(_directory, "pos.db");

    /// <summary>Builds a live database with a known number of items and takes a snapshot of it.</summary>
    private (PosDatabase Live, string Snapshot, int ItemsInSnapshot) Prepare(int itemsAtSnapshot = 40, int itemsAfter = 15)
    {
        var live = new PosDatabase(LivePath);
        live.EnsureMigrated();
        new ItemRepository(live).AddRange(Catalogue.Generate(itemsAtSnapshot));

        var backup = new DatabaseBackup(live, Path.Combine(_directory, "backups"));
        var snapshot = backup.Create(DateTimeOffset.Now).Path;

        // Trade on afterwards, so a restore is visibly a step backwards.
        new ItemRepository(live).AddRange(
            Catalogue.Generate(itemsAfter, seed: 7).Select(i => i with { Sku = "AFTER-" + i.Sku, Barcode = null }));

        SqliteConnection.ClearAllPools();
        return (live, snapshot, itemsAtSnapshot);
    }

    [Fact]
    public void ASoundSnapshotIsRestoredAndTheLanePicksItUp()
    {
        var (_, snapshot, itemsAtSnapshot) = Prepare();

        var result = new DatabaseRestore(LivePath).Restore(snapshot, DateTimeOffset.Now);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal(itemsAtSnapshot, new ItemRepository(new PosDatabase(LivePath)).Count());

        output.WriteLine(result.Detail);
    }

    /// <summary>The database being replaced is renamed, never deleted.</summary>
    [Fact]
    public void ThePreviousDatabaseIsKept()
    {
        var (_, snapshot, _) = Prepare();

        var result = new DatabaseRestore(LivePath).Restore(snapshot, DateTimeOffset.Now);

        Assert.NotNull(result.MovedAsidePath);
        Assert.True(File.Exists(result.MovedAsidePath));
        Assert.Contains(".damaged.", result.MovedAsidePath);

        // And it is still a working database, so nothing is lost if the restore was a mistake.
        Assert.True(new PosDatabase(result.MovedAsidePath!).CheckIntegrity().IsHealthy);
    }

    /// <summary>
    /// Replacing a working database with a damaged copy is the one outcome worse than the problem
    /// being fixed.
    /// </summary>
    [Fact]
    public void ADamagedSnapshotIsRefusedAndNothingIsTouched()
    {
        var (_, snapshot, _) = Prepare();
        var itemsBefore = new ItemRepository(new PosDatabase(LivePath)).Count();

        SqliteConnection.ClearAllPools();

        using (var file = new FileStream(snapshot, FileMode.Open, FileAccess.ReadWrite))
        {
            file.Seek(file.Length / 2, SeekOrigin.Begin);
            file.Write(new byte[4_096]);
        }

        SqliteConnection.ClearAllPools();

        var result = new DatabaseRestore(LivePath).Restore(snapshot, DateTimeOffset.Now);

        Assert.False(result.Succeeded);
        Assert.Null(result.MovedAsidePath);
        Assert.Contains("not usable", result.Detail);
        Assert.Equal(itemsBefore, new ItemRepository(new PosDatabase(LivePath)).Count());

        output.WriteLine(result.Detail);
    }

    [Fact]
    public void AMissingSnapshotIsRefused()
    {
        Prepare();

        var result = new DatabaseRestore(LivePath).Restore(Path.Combine(_directory, "nope.db"), DateTimeOffset.Now);

        Assert.False(result.Succeeded);
        Assert.Contains("not usable", result.Detail);
    }

    [Fact]
    public void InspectingASnapshotDoesNotRestoreIt()
    {
        var (_, snapshot, itemsAtSnapshot) = Prepare();
        var before = new ItemRepository(new PosDatabase(LivePath)).Count();

        Assert.True(new DatabaseRestore(LivePath).Inspect(snapshot).IsHealthy);
        Assert.NotEqual(itemsAtSnapshot, before);
        Assert.Equal(before, new ItemRepository(new PosDatabase(LivePath)).Count());
    }

    /// <summary>
    /// The write-ahead log belongs to the database that was moved. Left behind, SQLite would try to
    /// replay it onto the restored file.
    /// </summary>
    [Fact]
    public void TheWriteAheadLogGoesWithTheDatabaseItBelongsTo()
    {
        var (_, snapshot, _) = Prepare();

        // Leave a WAL behind by writing without checkpointing.
        var live = new PosDatabase(LivePath);
        using (var connection = live.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO customers (mobile_no, loyalty_balance) VALUES ('9999999999', 0);";
            command.ExecuteNonQuery();
        }

        var result = new DatabaseRestore(LivePath).Restore(snapshot, DateTimeOffset.Now);

        Assert.True(result.Succeeded, result.Detail);
        Assert.False(File.Exists(LivePath + "-wal"), "a stale write-ahead log was left beside the restored database");
    }

    [Fact]
    public void RestoringOntoALaneWithNoDatabaseYetJustWorks()
    {
        var (_, snapshot, itemsAtSnapshot) = Prepare();

        SqliteConnection.ClearAllPools();
        File.Delete(LivePath);
        foreach (var companion in new[] { "-wal", "-shm" })
        {
            if (File.Exists(LivePath + companion))
                File.Delete(LivePath + companion);
        }

        var result = new DatabaseRestore(LivePath).Restore(snapshot, DateTimeOffset.Now);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Null(result.MovedAsidePath);
        Assert.Equal(itemsAtSnapshot, new ItemRepository(new PosDatabase(LivePath)).Count());
    }

    [Fact]
    public void TheRestoredDatabaseIsAtASchemaThisBuildUnderstands()
    {
        var (_, snapshot, _) = Prepare();

        new DatabaseRestore(LivePath).Restore(snapshot, DateTimeOffset.Now);

        using var connection = new PosDatabase(LivePath).OpenConnection();
        Assert.Equal(Migrator.LatestVersion, Migrator.GetVersion(connection));
    }
}
