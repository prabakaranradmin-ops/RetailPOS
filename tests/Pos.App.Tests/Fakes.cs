using Pos.App.Input;

namespace Pos.App.Tests;

/// <summary>
/// A clock the test drives by hand. Scanner classification turns on gaps of a few milliseconds,
/// which is not something to test by actually sleeping.
/// </summary>
public sealed class FakeClock : IClock
{
    public TimeSpan Elapsed { get; private set; }

    public void Advance(TimeSpan by) => Elapsed += by;

    public void Advance(double milliseconds) => Advance(TimeSpan.FromMilliseconds(milliseconds));
}

/// <summary>
/// A scheduler on virtual time. Nothing runs until the test advances the clock past the delay,
/// which is what lets the debounce window be asserted exactly rather than approximately.
/// </summary>
public sealed class VirtualScheduler : IDelayScheduler
{
    private readonly List<Entry> _entries = [];

    public TimeSpan Now { get; private set; }

    public int PendingCount => _entries.Count;

    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var entry = new Entry(Now + delay, callback, this);
        _entries.Add(entry);
        return entry;
    }

    /// <summary>
    /// Moves virtual time forward, running everything that comes due. A callback that schedules
    /// more work is handled: the loop re-checks after each one, and anything due later than the
    /// new "now" correctly stays pending.
    /// </summary>
    public void Advance(TimeSpan by)
    {
        Now += by;

        while (true)
        {
            var due = _entries
                .Where(entry => entry.DueAt <= Now)
                .OrderBy(entry => entry.DueAt)
                .FirstOrDefault();

            if (due is null)
                break;

            _entries.Remove(due);
            due.Callback();
        }
    }

    public void Advance(double milliseconds) => Advance(TimeSpan.FromMilliseconds(milliseconds));

    private sealed class Entry(TimeSpan dueAt, Action callback, VirtualScheduler owner) : IDisposable
    {
        public TimeSpan DueAt => dueAt;

        public Action Callback => callback;

        public void Dispose() => owner._entries.Remove(this);
    }
}
