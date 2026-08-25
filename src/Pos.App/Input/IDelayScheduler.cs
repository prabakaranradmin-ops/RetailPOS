using System.Windows.Threading;

namespace Pos.App.Input;

/// <summary>
/// Runs a callback after a delay. Abstracted so the debounce window can be tested with virtual
/// time instead of by sleeping.
/// </summary>
public interface IDelayScheduler
{
    /// <summary>
    /// Schedules <paramref name="callback"/> to run once after <paramref name="delay"/>. Disposing
    /// the returned handle cancels it if it has not fired yet.
    /// </summary>
    IDisposable Schedule(TimeSpan delay, Action callback);
}

/// <summary>
/// Schedules onto the WPF dispatcher, so debounced searches land on the UI thread and can touch
/// bound collections directly.
/// </summary>
public sealed class DispatcherDelayScheduler : IDelayScheduler
{
    private readonly Dispatcher _dispatcher;

    public DispatcherDelayScheduler(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
    }

    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var timer = new DispatcherTimer(DispatcherPriority.Input, _dispatcher) { Interval = delay };

        timer.Tick += OnTick;
        timer.Start();

        return new Handle(timer, OnTick);

        void OnTick(object? sender, EventArgs e)
        {
            timer.Stop();
            timer.Tick -= OnTick;
            callback();
        }
    }

    private sealed class Handle(DispatcherTimer timer, EventHandler tick) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            timer.Stop();
            timer.Tick -= tick;
        }
    }
}
