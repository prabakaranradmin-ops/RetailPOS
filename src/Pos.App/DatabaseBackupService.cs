using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Logging;

namespace Pos.App;

/// <summary>
/// Lets day-end close ask for a backup without the domain knowing how a SQLite file is copied.
/// </summary>
public sealed class DatabaseBackupService(
    DatabaseBackup backup,
    int keep = DatabaseBackup.DefaultKeep,
    IPosLog? log = null) : IBackupService
{
    private readonly DatabaseBackup _backup = backup ?? throw new ArgumentNullException(nameof(backup));
    private readonly IPosLog _log = log ?? NullLog.Instance;

    public BackupOutcome Create(DateTimeOffset takenAt)
    {
        var result = _backup.Create(takenAt, keep);

        var outcome = new BackupOutcome(
            result.Succeeded,
            result.Path,
            result.Succeeded ? $"{result.Bytes / 1024:N0} KB, verified" : string.Join("; ", result.Problems));

        // A backup that failed is the one event on this path nobody may miss later.
        if (outcome.Succeeded)
            _log.Info("backup", $"{result.Path} ({result.Bytes / 1024:N0} KB, verified)");
        else
            _log.Error("backup", $"BACKUP FAILED: {outcome.Detail}");

        return outcome;
    }
}
