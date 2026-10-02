using Pos.Core.Domain;

namespace Pos.TestSupport;

/// <summary>
/// Stands in for the backup during tests, recording that one was asked for and letting a test make
/// it fail — because "the day closed but the backup did not" is a case that has to be reported
/// rather than swallowed.
/// </summary>
public sealed class RecordingBackupService : IBackupService
{
    public int Calls { get; private set; }

    public DateTimeOffset? LastTakenAt { get; private set; }

    /// <summary>Set to make the next backup report failure.</summary>
    public string? FailWith { get; set; }

    /// <summary>What the next backup says about the copy off this computer.</summary>
    public string? OffMachine { get; set; }

    public BackupOutcome Create(DateTimeOffset takenAt)
    {
        Calls++;
        LastTakenAt = takenAt;

        return FailWith is { } reason
            ? new BackupOutcome(false, string.Empty, reason)
            : new BackupOutcome(true, $"pos-{takenAt:yyyyMMdd-HHmmss}.db", "verified", OffMachine);
    }
}
