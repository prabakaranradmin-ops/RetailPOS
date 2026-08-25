using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Pos.Core.Data;

/// <param name="Path">Where the snapshot was written.</param>
/// <param name="Bytes">Its size.</param>
/// <param name="Verified">Whether the copy itself passed an integrity check.</param>
/// <param name="Problems">What is wrong, when something is.</param>
/// <param name="Pruned">Older snapshots removed to stay within the retention limit.</param>
public sealed record BackupResult(
    string Path,
    long Bytes,
    bool Verified,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Pruned)
{
    public bool Succeeded => Problems.Count == 0 && Verified;
}

/// <summary>
/// Takes a snapshot of the lane's database.
/// </summary>
/// <remarks>
/// Uses <c>VACUUM INTO</c>, which builds a fresh, compacted database from a read transaction. That
/// matters at a till: it does not block anyone billing, and the result is a clean file rather than
/// a byte copy that might catch a half-written page or miss the write-ahead log.
/// <para>
/// Every snapshot is verified before it is called a backup. A copy nobody has checked is a copy
/// nobody knows they can restore, and the moment that is discovered is the moment it is needed.
/// </para>
/// </remarks>
public sealed class DatabaseBackup
{
    /// <summary>Snapshots kept before the oldest are removed.</summary>
    public const int DefaultKeep = 30;

    private const string FilePrefix = "pos-";
    private const string FileSuffix = ".db";

    private readonly PosDatabase _database;

    public DatabaseBackup(PosDatabase database, string directory)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        _database = database;
        Directory = directory;
    }

    public string Directory { get; }

    public BackupResult Create(DateTimeOffset takenAt, int keep = DefaultKeep)
    {
        if (keep < 1)
            throw new ArgumentOutOfRangeException(nameof(keep), keep, "At least one snapshot has to be kept.");

        var problems = new List<string>();
        var path = Path.Combine(Directory, $"{FilePrefix}{takenAt:yyyyMMdd-HHmmss}{FileSuffix}");

        try
        {
            System.IO.Directory.CreateDirectory(Directory);

            // VACUUM INTO refuses to overwrite, which is the behaviour wanted — a backup must never
            // quietly replace another one.
            if (File.Exists(path))
                path = Path.Combine(Directory, $"{FilePrefix}{takenAt:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}{FileSuffix}");

            using (var connection = _database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "VACUUM INTO $path;";
                command.Parameters.AddWithValue("$path", path);
                command.ExecuteNonQuery();
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            problems.Add($"The backup could not be written: {ex.Message}");
            return new BackupResult(path, 0, false, problems, []);
        }

        var bytes = new FileInfo(path).Length;

        // Check the copy, not the original. The point is to know this file can be restored.
        var report = new PosDatabase(path).CheckIntegrity();
        SqliteConnection.ClearAllPools();

        if (!report.IsHealthy)
            problems.AddRange(report.Problems.Select(p => $"The snapshot is damaged: {p}"));

        var pruned = Prune(keep, problems);

        return new BackupResult(path, bytes, report.IsHealthy, problems, pruned);
    }

    /// <summary>Removes the oldest snapshots once there are more than <paramref name="keep"/>.</summary>
    private List<string> Prune(int keep, List<string> problems)
    {
        var pruned = new List<string>();

        try
        {
            var snapshots = Existing().Skip(keep).ToList();

            foreach (var snapshot in snapshots)
            {
                File.Delete(snapshot.FullName);
                pruned.Add(snapshot.Name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Failing to tidy up is worth mentioning but is not a failed backup.
            problems.Add($"Older snapshots could not be removed: {ex.Message}");
        }

        return pruned;
    }

    /// <summary>Snapshots on disk, newest first.</summary>
    public IReadOnlyList<FileInfo> Existing()
    {
        if (!System.IO.Directory.Exists(Directory))
            return [];

        return new DirectoryInfo(Directory)
            .EnumerateFiles($"{FilePrefix}*{FileSuffix}")
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Reads the timestamp back out of a snapshot's name.</summary>
    public static DateTimeOffset? TimestampOf(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);

        if (!name.StartsWith(FilePrefix, StringComparison.Ordinal))
            return null;

        var stamp = name[FilePrefix.Length..];

        if (stamp.Length > 15)
            stamp = stamp[..15];

        return DateTimeOffset.TryParseExact(stamp, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
            ? parsed
            : null;
    }
}
