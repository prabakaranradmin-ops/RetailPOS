using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Domain.Printing;
using Pos.Core.Hardware.Printing;

namespace Pos.App.ViewModels;

/// <summary>One snapshot on disk, as the restore list shows it.</summary>
/// <param name="TakenAt">When it was taken, read from the file name rather than its timestamp.</param>
public sealed record SnapshotRow(string Path, DateTimeOffset? TakenAt, long Bytes)
{
    public string Taken => TakenAt is { } at
        ? at.ToString("dd MMM yyyy  HH:mm", CultureInfo.InvariantCulture)
        : "unknown";

    public string Size => $"{Bytes / 1024:N0} KB";

    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>What has to be typed to arm a restore from this snapshot.</summary>
    public string Confirmation => TakenAt is { } at
        ? at.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)
        : string.Empty;
}

/// <summary>
/// Backups, the database's health, restoring, and the day-end reports already taken — the last of
/// the lane's upkeep that still needed a command prompt.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here is new behaviour. Every action drives the same class the <c>pos</c> tool drives —
/// <see cref="DatabaseBackup"/>, <see cref="DatabaseRestore"/>, <see cref="PosDatabase.CheckIntegrity"/>,
/// <see cref="IDayCloseStore"/> — so a shop that never opens a command prompt gets exactly the
/// behaviour the tool would have given it, including the refusals.
/// </para>
/// <para>
/// Everything runs off the UI thread. A full integrity check reads every page of the database and a
/// backup copies the whole file; on the dispatcher thread either would freeze the till's own window
/// while a customer waited.
/// </para>
/// </remarks>
public sealed class MaintenanceViewModel : ObservableObject
{
    private readonly PosDatabase _database;
    private readonly string _dataDirectory;
    private readonly IDayCloseStore _closes;
    private readonly ZReportComposer _composer;
    private readonly string _laneId;
    private readonly Func<ReceiptBuilder, PrintOutcome> _print;
    private readonly Action<Action> _post;

    private bool _busy;
    private string _summary = string.Empty;
    private string _reportText = string.Empty;
    private bool _thorough = true;
    private bool _healthy;
    private int _keep = DatabaseBackup.DefaultKeep;
    private SnapshotRow? _selectedSnapshot;
    private DayCloseEntry? _selectedClose;
    private string _typedConfirmation = string.Empty;

    /// <param name="print">
    /// Sends a composed report to this lane's printer. Injected so the screen can be tested without
    /// one attached, and so a lane with no printer configured fails as a message rather than a throw.
    /// </param>
    /// <param name="post">Runs an action on the UI thread. Tests pass one that runs it directly.</param>
    public MaintenanceViewModel(
        PosDatabase database,
        string dataDirectory,
        IDayCloseStore closes,
        ZReportComposer composer,
        string laneId,
        Func<ReceiptBuilder, PrintOutcome> print,
        Action<Action> post)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        _closes = closes ?? throw new ArgumentNullException(nameof(closes));
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _laneId = laneId ?? throw new ArgumentNullException(nameof(laneId));
        _print = print ?? throw new ArgumentNullException(nameof(print));
        _post = post ?? throw new ArgumentNullException(nameof(post));

        RefreshSnapshots();
        RefreshCloses();
    }

    /// <summary>Where the snapshots live, shown so somebody can copy one to a memory stick.</summary>
    public string BackupDirectory => Path.Combine(_dataDirectory, "backups");

    /// <summary>Progress from whatever is running, newest last.</summary>
    public ObservableCollection<string> Log { get; } = [];

    /// <summary>The snapshots on disk, newest first.</summary>
    public ObservableCollection<SnapshotRow> Snapshots { get; } = [];

    /// <summary>The day-end reports this lane has already taken, newest first.</summary>
    public ObservableCollection<DayCloseEntry> Closes { get; } = [];

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    /// <summary>A past Z-report, as it would read on paper.</summary>
    public string ReportText
    {
        get => _reportText;
        private set
        {
            if (!Set(ref _reportText, value))
                return;

            Raise(nameof(HasReport));
            Raise(nameof(ShowsLog));
        }
    }

    public bool HasReport => ReportText.Length > 0;

    /// <summary>
    /// The panel shows one thing at a time: the running job's progress, or a report that was asked
    /// for. Stacking them would leave somebody reading a backup's log under last Tuesday's Z-report.
    /// </summary>
    public bool ShowsLog => !HasReport;

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value))
            {
                Raise(nameof(CanRun));
                Raise(nameof(CanCompact));
                Raise(nameof(CanRestore));
            }
        }
    }

    public bool CanRun => !IsBusy;

    /// <summary>
    /// How many snapshots to keep. Older ones are removed after a successful backup.
    /// </summary>
    public int Keep
    {
        get => _keep;
        set => Set(ref _keep, value < 1 ? 1 : value);
    }

    /// <summary>
    /// Whether the integrity check reads every page or only the structure. Full by default: a quick
    /// check is for a lane that is mid-trade and cannot spare the seconds.
    /// </summary>
    public bool Thorough
    {
        get => _thorough;
        set => Set(ref _thorough, value);
    }

    /// <summary>
    /// True once a check has come back clean. Compacting is offered only then — see
    /// <see cref="Compact"/>.
    /// </summary>
    public bool IsHealthy
    {
        get => _healthy;
        private set
        {
            if (Set(ref _healthy, value))
                Raise(nameof(CanCompact));
        }
    }

    public bool CanCompact => IsHealthy && !IsBusy;

    public SnapshotRow? SelectedSnapshot
    {
        get => _selectedSnapshot;
        set
        {
            if (!Set(ref _selectedSnapshot, value))
                return;

            // A confirmation belongs to the snapshot it was typed for. Picking a different one has
            // to be confirmed again, or a restore of last week's file could be armed by a phrase
            // typed against yesterday's.
            TypedConfirmation = string.Empty;

            Raise(nameof(RestoreTarget));
            Raise(nameof(CanRestore));
        }
    }

    public DayCloseEntry? SelectedClose
    {
        get => _selectedClose;
        set
        {
            if (Set(ref _selectedClose, value))
                Raise(nameof(CanShowReport));
        }
    }

    public bool CanShowReport => SelectedClose is not null && !IsBusy;

    /// <summary>
    /// What the operator has typed to confirm a restore.
    /// </summary>
    /// <remarks>
    /// A dialog is the wrong guard here. Restoring throws away every sale rung up since the snapshot
    /// was taken, and a question with a Yes button is answered by reflex — the command line asks for
    /// a typed y/N for the same reason. Typing the snapshot's own date proves the operator read
    /// which one they picked, not merely that something was about to happen.
    /// </remarks>
    public string TypedConfirmation
    {
        get => _typedConfirmation;
        set
        {
            if (Set(ref _typedConfirmation, value))
                Raise(nameof(CanRestore));
        }
    }

    public string RestoreTarget => SelectedSnapshot is { } snapshot
        ? $"{snapshot.FileName} — taken {snapshot.Taken}. Everything sold since then will be gone. " +
          $"Type {snapshot.Confirmation} to confirm."
        : "Pick a snapshot from the list.";

    public bool CanRestore => Armed && !IsBusy;

    /// <summary>
    /// Whether a snapshot is picked and its date typed, ignoring whether anything is running.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="CanRestore"/> on purpose. The job checks this rather than
    /// <c>CanRestore</c>, because by the time it runs <see cref="IsBusy"/> is already true — a guard
    /// written against <c>CanRestore</c> would refuse every restore it was asked to perform.
    /// </remarks>
    private bool Armed =>
        SelectedSnapshot is { } snapshot &&
        snapshot.Confirmation.Length > 0 &&
        string.Equals(TypedConfirmation.Trim(), snapshot.Confirmation, StringComparison.Ordinal);

    /// <summary>Re-reads the snapshots on disk, newest first.</summary>
    public void RefreshSnapshots()
    {
        Snapshots.Clear();

        try
        {
            var backup = new DatabaseBackup(_database, BackupDirectory);

            foreach (var file in backup.Existing().OrderByDescending(f => f.Name))
                Snapshots.Add(new SnapshotRow(file.FullName, DatabaseBackup.TimestampOf(file.Name), file.Length));
        }
        catch (Exception ex)
        {
            // A folder that cannot be read costs the list, not the screen.
            Summary = $"The backup folder could not be read: {ex.Message}";
        }
    }

    /// <summary>Re-reads the day-end reports already taken.</summary>
    public void RefreshCloses()
    {
        Closes.Clear();

        try
        {
            foreach (var entry in _closes.List(_laneId, 60))
                Closes.Add(entry);

            // The newest, picked already. The report somebody reaches for is nearly always last
            // night's - the sheet that jammed - and with nothing picked, "Read it" and "Print a
            // duplicate" sat disabled until the mouse found the list.
            SelectedClose = Closes.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Summary = $"The day-end reports could not be read: {ex.Message}";
        }
    }

    /// <summary>Takes a snapshot now, and prunes to <see cref="Keep"/>.</summary>
    public Task Backup() => Run("Backup", () =>
    {
        var backup = new DatabaseBackup(_database, BackupDirectory);
        var result = backup.Create(DateTimeOffset.Now, Keep);

        foreach (var problem in result.Problems)
            Say(problem);

        if (!result.Succeeded)
            return "Backup FAILED. The lane has no new snapshot.";

        Say($"Wrote {result.Path}");
        Say($"{result.Bytes / 1024:N0} KB, verified.");

        if (result.Pruned.Count > 0)
            Say($"Removed {result.Pruned.Count} older snapshot(s), keeping {Keep}.");

        var held = backup.Existing().Count;
        Say($"{held} snapshot(s) on hand.");

        return $"Backed up: {result.Bytes / 1024:N0} KB, verified, {held} snapshot(s) on hand.";
    },
    then: RefreshSnapshots);

    /// <summary>Checks the database, and says what is wrong rather than offering to fix it.</summary>
    public Task Check() => Run("Check", () =>
    {
        var thorough = Thorough;

        Say($"Checking {_database.DatabasePath}");
        Say($"{new FileInfo(_database.DatabasePath).Length / 1024:N0} KB, {(thorough ? "full" : "quick")} check");

        var report = _database.CheckIntegrity(thorough);

        if (report.IsHealthy)
        {
            Say("No problems found.");
            _post(() => IsHealthy = true);
            return "The database is sound.";
        }

        foreach (var problem in report.Problems)
            Say($"PROBLEM: {problem}");

        // Deliberately not offering to repair, exactly as the command does not. A damaged till
        // database is the shop's book of account, and the right first move is a copy of the file
        // and a look at the backups, not a tool that rewrites it.
        Say(string.Empty);
        Say("Take a copy of the file before doing anything else, then restore from a backup.");

        _post(() => IsHealthy = false);
        return $"PROBLEMS FOUND: {report.Problems.Count}. Do not keep trading on this lane.";
    });

    /// <summary>
    /// Compacts the database. Offered only after a clean check, because compacting rewrites every
    /// page — which on a damaged file is the one operation most likely to finish the job.
    /// </summary>
    public Task Compact() => Run("Compact", () =>
    {
        if (!IsHealthy)
            return "Check the database first. Compacting a damaged file can destroy what is left of it.";

        var before = new FileInfo(_database.DatabasePath).Length;

        Say("Compacting...");
        _database.Vacuum();

        var after = new FileInfo(_database.DatabasePath).Length;

        Say($"Now {after / 1024:N0} KB.");

        return $"Compacted: {before / 1024:N0} KB to {after / 1024:N0} KB.";
    });

    /// <summary>
    /// Puts a snapshot back in place of the live database.
    /// </summary>
    /// <remarks>
    /// Guarded twice: the snapshot is inspected before anything is touched, and the operator has had
    /// to type its date. The database being replaced is moved aside rather than deleted, so a
    /// restore of the wrong snapshot is itself recoverable.
    /// </remarks>
    public Task Restore()
    {
        // Read before the background thread starts. These belong to the window, and the snapshot
        // picked when the button was pressed is the one to restore even if the list refreshes.
        var armed = Armed;
        var chosen = SelectedSnapshot;

        return Run("Restore", () =>
        {
            if (!armed || chosen is not { } snapshot)
                return "Pick a snapshot and type its date first.";

            var restore = new DatabaseRestore(_database.DatabasePath);

            Say($"Restoring {_database.DatabasePath}");
            Say($"     from {snapshot.Path}");

            var inspection = restore.Inspect(snapshot.Path);

            if (!inspection.IsHealthy)
            {
                foreach (var problem in inspection.Problems)
                    Say($"PROBLEM: {problem}");

                Say("Nothing was changed. Try an older snapshot.");
                return "The snapshot is not usable, so nothing was changed.";
            }

            Say("Snapshot checked and sound.");

            var result = restore.Restore(snapshot.Path, DateTimeOffset.Now);

            Say(result.Detail);

            if (result.MovedAsidePath is { } aside)
                Say($"The previous database was kept at {aside} — it is not deleted.");

            if (!result.Succeeded)
                return $"Restore FAILED: {result.Detail}";

            return "Restored. Close the till and open it again before selling anything.";
        },
        then: () =>
        {
            TypedConfirmation = string.Empty;
            RefreshSnapshots();
            RefreshCloses();
        });
    }

    /// <summary>Reads a past day-end report onto the screen, printing nothing.</summary>
    public Task ShowReport() => Run("Day-end report", () =>
    {
        if (SelectedClose is not { } chosen)
            return "Pick a report from the list first.";

        if (_closes.FindById(chosen.Id) is not { } report)
            return $"There is no day-end report numbered {chosen.Id}.";

        var text = _composer.Compose(report).ToPlainText();
        _post(() => ReportText = text);

        return $"Report {chosen.Id}, closed {chosen.ClosedAt:dd MMM yyyy HH:mm}.";
    });

    /// <summary>
    /// Prints a duplicate of a past day-end report, marked as a reprint on the paper.
    /// </summary>
    public Task Reprint() => Run("Reprint", () =>
    {
        if (SelectedClose is not { } chosen)
            return "Pick a report from the list first.";

        if (_closes.FindById(chosen.Id) is not { } report)
            return $"There is no day-end report numbered {chosen.Id}.";

        // Composed as a reprint for the screen as well as the paper, so what is shown is what came
        // out — a duplicate that did not say so is the one an inspector would ask about.
        var composed = _composer.Compose(report, isReprint: true);

        _post(() => ReportText = composed.ToPlainText());

        var outcome = _print(composed);

        Say(outcome.Detail);

        return outcome.Succeeded
            ? $"Duplicate of report {chosen.Id} printed, marked as a reprint."
            : $"Did not print: {outcome.Detail}";
    });

    private void Say(string line) => _post(() => Log.Add(line));

    /// <summary>
    /// Runs one job off the UI thread, reporting as it goes.
    /// </summary>
    /// <remarks>
    /// Nothing here is allowed to throw out. A locked file, a folder that has gone, a snapshot on a
    /// memory stick somebody pulled — each becomes a line in the log and a summary, because the
    /// owner's screen sits on top of a till that has to carry on selling either way.
    /// </remarks>
    private async Task Run(string what, Func<string> job, Action? then = null)
    {
        if (IsBusy)
            return;

        IsBusy = true;
        Log.Clear();

        // Back to the log. A report left on screen while a backup runs beneath it reads as though
        // the backup produced it.
        ReportText = string.Empty;

        Summary = $"{what}: running…";

        try
        {
            Summary = await Task.Run(() =>
            {
                try
                {
                    return job();
                }
                catch (Exception ex)
                {
                    Say($"FAILED: {ex.Message}");
                    return $"{what} failed: {ex.Message}";
                }
            });
        }
        finally
        {
            IsBusy = false;
            then?.Invoke();
        }
    }
}
