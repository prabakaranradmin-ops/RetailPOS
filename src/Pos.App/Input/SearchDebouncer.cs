namespace Pos.App.Input;

/// <summary>
/// Holds back a typed query until the cashier stops typing, so a six-character search runs one
/// database query instead of six (SRS 2.1).
/// </summary>
public sealed class SearchDebouncer : IDisposable
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMilliseconds(150);

    private readonly IDelayScheduler _scheduler;
    private readonly Action<string> _onElapsed;

    private IDisposable? _pending;
    private string _pendingText = string.Empty;
    private bool _disposed;

    public SearchDebouncer(IDelayScheduler scheduler, Action<string> onElapsed, TimeSpan? window = null)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(onElapsed);

        _scheduler = scheduler;
        _onElapsed = onElapsed;
        Window = window ?? DefaultWindow;
    }

    public TimeSpan Window { get; set; }

    /// <summary>True while a query is waiting for the window to close.</summary>
    public bool IsPending => _pending is not null;

    /// <summary>
    /// Registers a keystroke. Each call restarts the window, so the query fires only once the
    /// cashier has paused.
    /// </summary>
    public void Notify(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Cancel();
        _pendingText = text ?? string.Empty;
        _pending = _scheduler.Schedule(Window, Fire);
    }

    /// <summary>
    /// Runs the query now, discarding any pending one. This is the scanner path: a burst has
    /// already been classified as a scan, so there is nothing to wait for.
    /// </summary>
    public void Flush(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Cancel();
        _pendingText = text ?? string.Empty;
        Fire();
    }

    /// <summary>Drops any pending query without running it.</summary>
    public void Cancel()
    {
        _pending?.Dispose();
        _pending = null;
    }

    private void Fire()
    {
        _pending = null;
        _onElapsed(_pendingText);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Cancel();
    }
}
