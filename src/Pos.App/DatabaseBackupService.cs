using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Logging;

namespace Pos.App;

/// <summary>
/// Lets day-end close ask for a backup without the domain knowing how a SQLite file is copied.
/// </summary>
/// <remarks>
/// When the shop's backup pen drive is plugged in, the snapshot goes onto it as well. When it is
/// not, and the last copy off this computer is a week old or more, the outcome says so: the close
/// is the one moment somebody is reliably standing at the till to read it.
/// </remarks>
public sealed class DatabaseBackupService(
    DatabaseBackup backup,
    int keep = DatabaseBackup.DefaultKeep,
    IPosLog? log = null,
    OffMachineCopy? offMachine = null) : IBackupService
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

        return outcome.Succeeded && offMachine is not null
            ? outcome with { OffMachine = CopyOff(result.Path, takenAt) }
            : outcome;
    }

    /// <summary>Copies the snapshot to every one of the shop's backup drives plugged in, or says one is overdue.</summary>
    private string? CopyOff(string snapshot, DateTimeOffset takenAt)
    {
        var drives = offMachine!.Marked();

        if (drives.Count == 0)
        {
            return offMachine.Overdue(takenAt) is { } overdue
                ? $"{overdue} Plug in the backup pen drive before closing, or copy one from the owner's screen, Maintenance."
                : null;
        }

        var said = new List<string>();

        foreach (var drive in drives)
        {
            var copy = offMachine.Copy(snapshot, drive, takenAt);

            if (copy.Succeeded)
                _log.Info("backup", $"copied to {copy.Path}");
            else
                _log.Error("backup", $"COPY OFF THIS COMPUTER FAILED: {copy.Detail}");

            said.Add(copy.Succeeded ? copy.Detail : $"PEN DRIVE COPY FAILED: {copy.Detail}");
        }

        return string.Join(" ", said);
    }
}
