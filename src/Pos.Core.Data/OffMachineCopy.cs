using System.Globalization;
using System.Security.Cryptography;

namespace Pos.Core.Data;

/// <summary>A drive a backup can be copied to: a pen drive, or in a test a folder standing in for one.</summary>
/// <param name="Root">Its root, such as <c>E:\</c>.</param>
/// <param name="Name">How a person knows it: <c>SHOP (E:)</c>.</param>
public sealed record CopyDrive(string Root, string Name);

/// <param name="Path">Where the copy is, when there is one.</param>
/// <param name="Detail">What happened, in words for the screen.</param>
/// <param name="Pruned">Older copies removed from the drive to stay within the limit.</param>
public sealed record CopyResult(bool Succeeded, string? Path, string Detail, IReadOnlyList<string> Pruned);

/// <summary>
/// Copies the lane's backups off this computer, onto a pen drive.
/// </summary>
/// <remarks>
/// <para>
/// A snapshot in the backups folder protects against a damaged database, and nothing else. It sits
/// on the same disk, so a disk that dies, a PC that is stolen or a fire takes the books and every
/// backup of them together. A copy on a pen drive that goes home with the owner is the only one that
/// survives those. It needs no network, so it fits a lane that never has one.
/// </para>
/// <para>
/// A drive becomes the shop's backup drive the first time something is copied to it, which creates
/// a <c>RetailPOS backups</c> folder on it. From then on the day close copies to it by itself
/// whenever it is plugged in. A drive without the folder is never written to unless somebody asks,
/// because a customer's pen drive left in the PC is not the shop's to fill.
/// </para>
/// <para>
/// The copy is checked byte for byte against the snapshot, which was itself checked when it was
/// taken. It is not opened as a database, because opening one writes to it.
/// </para>
/// </remarks>
public sealed class OffMachineCopy
{
    /// <summary>The folder on a drive that marks it as the shop's backup drive.</summary>
    public const string FolderName = "RetailPOS backups";

    /// <summary>Copies kept on the drive for each lane before the oldest are removed: two weeks of closes.</summary>
    public const int DefaultKeep = 14;

    /// <summary>How old the last copy may get before the till starts saying so.</summary>
    public const int WarnAfterDays = 7;

    private const string LedgerName = "copied-off-this-computer.txt";

    private readonly string _backupDirectory;
    private readonly string _laneId;
    private readonly Func<IReadOnlyList<CopyDrive>> _drives;

    /// <param name="backupDirectory">The lane's own backups folder, where the record of copies is kept.</param>
    /// <param name="drives">The drives plugged in now. The machine's removable drives unless a test says otherwise.</param>
    public OffMachineCopy(string backupDirectory, string laneId, Func<IReadOnlyList<CopyDrive>>? drives = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(laneId);

        _backupDirectory = backupDirectory;
        _laneId = laneId;
        _drives = drives ?? RemovableDrives;
    }

    /// <summary>The pen drives and memory cards plugged into this machine and ready to use.</summary>
    /// <remarks>
    /// Removable drives only. A USB hard disk usually reports itself as fixed, like the PC's own,
    /// and telling the two apart reliably is not possible from here.
    /// </remarks>
    public static IReadOnlyList<CopyDrive> RemovableDrives()
    {
        var found = new List<CopyDrive>();

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType != DriveType.Removable || !drive.IsReady)
                        continue;

                    var letter = drive.Name.TrimEnd('\\');
                    var label = drive.VolumeLabel;

                    found.Add(new CopyDrive(drive.RootDirectory.FullName, string.IsNullOrWhiteSpace(label) ? $"the pen drive ({letter})" : $"{label} ({letter})"));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A card reader with no card, or a drive pulled while being looked at.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return found;
    }

    /// <summary>Every drive plugged in now.</summary>
    public IReadOnlyList<CopyDrive> Plugged()
    {
        try
        {
            return _drives();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The drives plugged in now that are already the shop's backup drive.</summary>
    public IReadOnlyList<CopyDrive> Marked() =>
        Plugged().Where(d => Directory.Exists(Path.Combine(d.Root, FolderName))).ToList();

    /// <summary>
    /// The drive to copy to when somebody asks: the shop's backup drive if one is plugged in, or
    /// failing that the only pen drive there is. Null when there is none, or several and none of
    /// them is the shop's, since guessing would be writing to somebody else's drive.
    /// </summary>
    public CopyDrive? Choose()
    {
        if (Marked() is [var marked, ..])
            return marked;

        return Plugged() is [var only] ? only : null;
    }

    /// <summary>Where this lane's copies go on a drive.</summary>
    public string FolderOn(CopyDrive drive) => Path.Combine(drive.Root, FolderName, _laneId);

    /// <summary>
    /// Copies one snapshot onto a drive, checks the copy against it byte for byte, and keeps the
    /// newest <paramref name="keep"/> on the drive.
    /// </summary>
    public CopyResult Copy(string snapshot, CopyDrive drive, DateTimeOffset copiedAt, int keep = DefaultKeep)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot);
        ArgumentNullException.ThrowIfNull(drive);

        if (keep < 1)
            throw new ArgumentOutOfRangeException(nameof(keep), keep, "At least one copy has to be kept.");

        if (!File.Exists(snapshot))
            return Failed($"There is no snapshot at {snapshot} to copy.");

        var folder = FolderOn(drive);
        var target = Path.Combine(folder, Path.GetFileName(snapshot));

        // Written under another name and renamed once checked, so a drive pulled half way leaves a
        // .part file that nothing will mistake for a backup.
        var partial = target + ".part";

        try
        {
            Directory.CreateDirectory(folder);

            using (var source = new FileStream(snapshot, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var copy = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
            {
                source.CopyTo(copy);
                copy.Flush(flushToDisk: true);
            }

            if (!SameBytes(snapshot, partial))
            {
                TryDelete(partial);
                return Failed($"The copy on {drive.Name} did not match the snapshot, so it was removed. Try another pen drive.");
            }

            File.Move(partial, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(partial);
            return Failed($"Could not copy to {drive.Name}: {ex.Message}");
        }

        var pruned = Prune(folder, keep);
        Record(copiedAt, target);

        return new CopyResult(true, target, $"Copied to {drive.Name}, checked.", pruned);
    }

    /// <summary>The snapshots on a drive for this lane, newest first.</summary>
    public IReadOnlyList<FileInfo> CopiesOn(CopyDrive drive)
    {
        var folder = FolderOn(drive);

        try
        {
            return Directory.Exists(folder)
                ? new DirectoryInfo(folder).EnumerateFiles("pos-*.db").OrderByDescending(f => f.Name, StringComparer.Ordinal).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>When a backup last went onto a pen drive from this lane, or null for never.</summary>
    public DateTimeOffset? LastCopied()
    {
        var ledger = Path.Combine(_backupDirectory, LedgerName);

        try
        {
            if (!File.Exists(ledger))
                return null;

            DateTimeOffset? latest = null;

            foreach (var line in File.ReadLines(ledger))
            {
                var stamp = line.Split('\t')[0];

                if (DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) && (latest is null || at > latest))
                    latest = at;
            }

            return latest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// What to tell somebody when the last copy is too old, or null when it is recent enough.
    /// </summary>
    public string? Overdue(DateTimeOffset now)
    {
        if (LastCopied() is not { } last)
            return "The shop's books have never been copied off this computer.";

        var days = (int)(now.Date - last.Date).TotalDays;

        return days >= WarnAfterDays
            ? $"The shop's books were last copied off this computer {days} days ago."
            : null;
    }

    private static CopyResult Failed(string detail) => new(false, null, detail, []);

    private static bool SameBytes(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length)
            return false;

        using var first = File.OpenRead(a);
        using var second = File.OpenRead(b);

        return SHA256.HashData(first).AsSpan().SequenceEqual(SHA256.HashData(second));
    }

    private static List<string> Prune(string folder, int keep)
    {
        var pruned = new List<string>();

        try
        {
            foreach (var old in new DirectoryInfo(folder).EnumerateFiles("pos-*.db")
                         .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                         .Skip(keep)
                         .ToList())
            {
                old.Delete();
                pruned.Add(old.Name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Old copies left behind cost space on the drive, not the copy just made.
        }

        return pruned;
    }

    private void Record(DateTimeOffset copiedAt, string target)
    {
        try
        {
            Directory.CreateDirectory(_backupDirectory);
            File.AppendAllText(
                Path.Combine(_backupDirectory, LedgerName),
                $"{copiedAt.ToString("o", CultureInfo.InvariantCulture)}\t{target}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The copy is made and checked; failing to note it only means the reminder comes early.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
