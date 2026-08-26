using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Pos.Core.Data;

/// <param name="Succeeded">Whether the lane is now running on the snapshot.</param>
/// <param name="MovedAsidePath">Where the previous database was put. Never deleted.</param>
/// <param name="Detail">What happened, in words.</param>
public sealed record RestoreResult(bool Succeeded, string? MovedAsidePath, string Detail);

/// <summary>
/// Puts a snapshot back in place as the lane's live database.
/// </summary>
/// <remarks>
/// Ordered so that nothing is destroyed at any point. The snapshot is checked before anything is
/// touched, the database being replaced is renamed rather than deleted, and the result is opened
/// and read before the operation reports success. If any step fails, the lane is left with what it
/// had — a restore that half happened would leave a shop with no books at all.
/// <para>
/// Restoring is not a repair. It puts the shop back to the moment the snapshot was taken, and
/// everything sold since is gone. That is a decision for a person, which is why nothing here runs
/// automatically.
/// </para>
/// </remarks>
public sealed class DatabaseRestore(string livePath)
{
    private readonly string _livePath = !string.IsNullOrWhiteSpace(livePath)
        ? livePath
        : throw new ArgumentException("The live database path is required.", nameof(livePath));

    public string LivePath => _livePath;

    /// <summary>Checks a snapshot without restoring it.</summary>
    public IntegrityReport Inspect(string snapshotPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);

        if (!File.Exists(snapshotPath))
            return new IntegrityReport(false, [$"There is no file at '{snapshotPath}'."]);

        var report = new PosDatabase(snapshotPath).CheckIntegrity();
        SqliteConnection.ClearAllPools();

        return report;
    }

    public RestoreResult Restore(string snapshotPath, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);

        // Never restore from something that has not been read end to end. Replacing a working
        // database with a damaged copy is the one outcome worse than the problem being fixed.
        var report = Inspect(snapshotPath);

        if (!report.IsHealthy)
            return new RestoreResult(false, null, $"The snapshot is not usable, so nothing was changed: {report}");

        string? movedAside = null;

        try
        {
            SqliteConnection.ClearAllPools();

            if (File.Exists(_livePath))
            {
                movedAside = $"{_livePath}.damaged.{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
                File.Move(_livePath, movedAside);

                // The write-ahead log and shared-memory file belong to the database that was moved.
                // Leaving them behind would have SQLite try to replay them onto the restored file.
                foreach (var companion in new[] { "-wal", "-shm" })
                {
                    var path = _livePath + companion;

                    if (File.Exists(path))
                        File.Move(path, movedAside + companion);
                }
            }

            File.Copy(snapshotPath, _livePath, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Put it back if the copy failed after the move, so the lane is not left with nothing.
            if (movedAside is not null && !File.Exists(_livePath) && File.Exists(movedAside))
            {
                try
                {
                    File.Move(movedAside, _livePath);
                    movedAside = null;
                }
                catch (IOException)
                {
                    return new RestoreResult(false, movedAside, $"The restore failed and the original could not be put back. It is at '{movedAside}'. Error: {ex.Message}");
                }
            }

            return new RestoreResult(false, movedAside, $"The restore failed, so nothing was changed: {ex.Message}");
        }

        // Prove the lane can actually use what was just put in place.
        var restored = new PosDatabase(_livePath);
        var check = restored.CheckIntegrity();

        if (!check.IsHealthy)
            return new RestoreResult(false, movedAside, $"The restored file did not pass its check: {check}");

        try
        {
            var items = new ItemRepository(restored).Count();

            using (var connection = restored.OpenConnection())
            {
                var version = Migrator.GetVersion(connection);

                if (version > Migrator.LatestVersion)
                    return new RestoreResult(false, movedAside, $"The snapshot is at schema version {version}, newer than this build understands ({Migrator.LatestVersion}).");
            }

            SqliteConnection.ClearAllPools();

            return new RestoreResult(
                true,
                movedAside,
                $"Restored from '{Path.GetFileName(snapshotPath)}'. {items:N0} item(s) in the catalogue.");
        }
        catch (SqliteException ex)
        {
            return new RestoreResult(false, movedAside, $"The restored file could not be read: {ex.Message}");
        }
    }
}
