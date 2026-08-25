using Pos.Core.Data;
using Pos.Core.Domain;

namespace Pos.App;

/// <summary>
/// Lets day-end close ask for a backup without the domain knowing how a SQLite file is copied.
/// </summary>
public sealed class DatabaseBackupService(DatabaseBackup backup, int keep = DatabaseBackup.DefaultKeep) : IBackupService
{
    private readonly DatabaseBackup _backup = backup ?? throw new ArgumentNullException(nameof(backup));

    public BackupOutcome Create(DateTimeOffset takenAt)
    {
        var result = _backup.Create(takenAt, keep);

        return new BackupOutcome(
            result.Succeeded,
            result.Path,
            result.Succeeded ? $"{result.Bytes / 1024:N0} KB, verified" : string.Join("; ", result.Problems));
    }
}
