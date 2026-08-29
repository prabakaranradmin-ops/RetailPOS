using System.Globalization;
using System.Text;

namespace Pos.Core.Logging;

/// <summary>
/// A rolling text log on the lane's own disk.
/// </summary>
/// <remarks>
/// One file per day, rolled again if a day gets unusually large, and old files removed after a
/// retention window. Plain text and one line per entry, because the person who will read it is
/// standing at a till with Notepad, not running a log aggregator.
/// <para>
/// Every write is flushed. A log that is still in a buffer when the power goes out is a log of
/// exactly the moment nobody can explain, so the cost of flushing is paid on purpose.
/// </para>
/// <para>
/// Nothing here throws. A lane that cannot write its log still has to be able to sell things, so a
/// failure to log is swallowed — the alternative is a disk problem taking the till down mid-queue.
/// </para>
/// </remarks>
public sealed class FileLog : IPosLog, IDisposable
{
    /// <summary>Roll to a new part once a day's file passes this.</summary>
    public const long DefaultMaxBytes = 8 * 1024 * 1024;

    /// <summary>Days of history kept. A pilot wants a few weeks; a till has a small disk.</summary>
    public const int DefaultRetentionDays = 45;

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly long _maxBytes;
    private readonly int _retentionDays;
    private readonly TimeProvider _clock;
    private readonly LogLevel _minimum;

    /// <summary>
    /// No byte order mark. The first line of a log is read by whoever is diagnosing a lane, often
    /// through grep or a text editor, and a BOM turns that first line into a near-match that looks
    /// like a match and is not.
    /// </summary>
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private string? _currentPath;
    private DateOnly _currentDay;
    private bool _disposed;

    public FileLog(
        string directory,
        LogLevel minimum = LogLevel.Info,
        long maxBytes = DefaultMaxBytes,
        int retentionDays = DefaultRetentionDays,
        TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        _directory = directory;
        _minimum = minimum;
        _maxBytes = maxBytes > 0 ? maxBytes : DefaultMaxBytes;
        _retentionDays = Math.Max(1, retentionDays);
        _clock = clock ?? TimeProvider.System;
    }

    public string Directory => _directory;

    public void Write(LogLevel level, string category, string message, Exception? exception = null)
    {
        if (level < _minimum || _disposed)
            return;

        try
        {
            var now = _clock.GetLocalNow();
            var line = new StringBuilder(160)
                .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                .Append("  ")
                .Append(Label(level))
                .Append("  ")
                .Append(category.PadRight(12))
                .Append("  ")
                .Append(Flatten(message));

            if (exception is not null)
            {
                // The type and message on the same line so a scan of the file shows what broke;
                // the stack indented beneath for whoever needs it.
                line.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(Flatten(exception.Message));

                if (exception.StackTrace is { } stack)
                {
                    foreach (var frame in stack.Split('\n'))
                        line.Append(Environment.NewLine).Append("        ").Append(frame.TrimEnd());
                }
            }

            lock (_gate)
            {
                var path = ResolvePath(now);
                File.AppendAllText(path, line.Append(Environment.NewLine).ToString(), Utf8NoBom);
            }
        }
        catch (Exception)
        {
            // A lane that cannot write its log still has to be able to sell things.
        }
    }

    /// <summary>
    /// Picks today's file, rolling to a new part if the current one has grown past the limit, and
    /// tidying up old days when the date changes.
    /// </summary>
    private string ResolvePath(DateTimeOffset now)
    {
        // The date this timestamp already carries, not whatever date the machine's own timezone
        // would call the same instant. LocalDateTime re-converts, which throws away the offset the
        // clock handed over: a lane trading up to midnight on a machine whose timezone is set to
        // something other than the shop's would put both sides of midnight in one file. The day a
        // log entry belongs to is the shop's day, not the operating system's opinion of it.
        var today = DateOnly.FromDateTime(now.DateTime);

        if (_currentPath is null || today != _currentDay)
        {
            System.IO.Directory.CreateDirectory(_directory);
            _currentDay = today;
            _currentPath = PathFor(today, 0);
            Prune(today);
        }

        if (_maxBytes > 0 && File.Exists(_currentPath) && new FileInfo(_currentPath).Length >= _maxBytes)
        {
            for (var part = 1; part < 1_000; part++)
            {
                var candidate = PathFor(today, part);

                if (!File.Exists(candidate) || new FileInfo(candidate).Length < _maxBytes)
                {
                    _currentPath = candidate;
                    break;
                }
            }
        }

        return _currentPath;
    }

    private string PathFor(DateOnly day, int part) =>
        Path.Combine(_directory, part == 0
            ? $"pos-{day:yyyyMMdd}.log"
            : $"pos-{day:yyyyMMdd}.{part}.log");

    private void Prune(DateOnly today)
    {
        try
        {
            var cutoff = today.AddDays(-_retentionDays);

            foreach (var file in new DirectoryInfo(_directory).EnumerateFiles("pos-*.log"))
            {
                if (DayOf(file.Name) is { } day && day < cutoff)
                    file.Delete();
            }
        }
        catch (Exception)
        {
            // Failing to tidy up is not worth reporting from inside a logger.
        }
    }

    /// <summary>Reads the date back out of a log file's name.</summary>
    public static DateOnly? DayOf(string fileName)
    {
        var name = Path.GetFileName(fileName);

        if (!name.StartsWith("pos-", StringComparison.Ordinal) || name.Length < 12)
            return null;

        return DateOnly.TryParseExact(name.Substring(4, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : null;
    }

    /// <summary>Log files on disk, newest first.</summary>
    public IReadOnlyList<FileInfo> Existing()
    {
        if (!System.IO.Directory.Exists(_directory))
            return [];

        return new DirectoryInfo(_directory)
            .EnumerateFiles("pos-*.log")
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static string Label(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO ",
        LogLevel.Warning => "WARN ",
        _ => "ERROR",
    };

    /// <summary>Keeps an entry on one line, so the file stays greppable.</summary>
    private static string Flatten(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.Replace("\r", string.Empty, StringComparison.Ordinal).Replace('\n', ' ');

    public void Dispose() => _disposed = true;
}
