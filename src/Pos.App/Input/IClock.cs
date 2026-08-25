using System.Diagnostics;

namespace Pos.App.Input;

/// <summary>
/// A monotonic time source. Scanner classification measures gaps of a few milliseconds, so it
/// must not read a clock that can jump backwards when the system time is corrected.
/// </summary>
public interface IClock
{
    /// <summary>Time elapsed since some fixed origin. Only differences are meaningful.</summary>
    TimeSpan Elapsed { get; }
}

/// <summary>The real clock, backed by a high-resolution stopwatch started at construction.</summary>
public sealed class SystemClock : IClock
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public TimeSpan Elapsed => _stopwatch.Elapsed;
}
