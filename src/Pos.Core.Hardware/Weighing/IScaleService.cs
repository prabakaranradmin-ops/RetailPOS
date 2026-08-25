using System.Text;
using Pos.Core.Hardware.Serial;

namespace Pos.Core.Hardware.Weighing;

/// <summary>A reading as the till sees it, after any software tare has been applied.</summary>
/// <param name="Gross">Everything on the pan.</param>
/// <param name="Tare">What is being subtracted for the container.</param>
/// <param name="Stability">Whether the reading has settled.</param>
public readonly record struct WeightReading(decimal Gross, decimal Tare, WeightStability Stability)
{
    /// <summary>What the customer is charged for. Never negative, however the tare was set.</summary>
    public decimal Net => Math.Max(0m, Gross - Tare);

    /// <summary>
    /// True only for a settled, positive reading. This is the gate on pricing: an unstable number
    /// is a number that is still changing, and billing one means charging for a weight that was
    /// never on the scale.
    /// </summary>
    public bool CanBeBilled => Stability == WeightStability.Stable && Net > 0m;

    public static WeightReading None => new(0m, 0m, WeightStability.Unstable);
}

/// <summary>
/// Reads a weight off the counter scale for loose goods (SRS 2.6).
/// </summary>
/// <remarks>
/// Retail scales come in two behaviours and this covers both (ARCHITECTURE.md section 5): most
/// stream readings continuously whether anyone asked or not, and some answer only when polled.
/// Either way the till keeps the latest reading and the caller takes it when the cashier presses
/// the key.
/// </remarks>
public interface IScaleService : IDisposable
{
    bool IsConfigured { get; }

    string Name { get; }

    /// <summary>False once the device has stopped sending, so the UI can grey out the weigh key.</summary>
    bool IsConnected { get; }

    /// <summary>The most recent reading. Unstable until the pan settles.</summary>
    WeightReading Current { get; }

    /// <summary>Raised whenever a new reading arrives.</summary>
    event EventHandler<WeightReading>? WeightChanged;

    void Start();

    void Stop();

    /// <summary>
    /// Takes the current gross as the tare, so what follows is the contents of the container.
    /// </summary>
    /// <returns>False if the reading is not settled enough to be taken as a tare.</returns>
    bool Tare();

    void ClearTare();
}

/// <summary>
/// A scale on a serial line, reassembling the frames it streams.
/// </summary>
public sealed class SerialScaleService : IScaleService
{
    /// <summary>
    /// Longest a frame may be before the buffer is treated as garbage and dropped. A real frame is
    /// around twenty characters; anything far longer means the line is noisy or the scale is
    /// speaking a different protocol, and the buffer must not be allowed to grow without limit.
    /// </summary>
    private const int MaxFrameLength = 128;

    private readonly ISerialPort _port;
    private readonly StringBuilder _buffer = new();

    // Frames arrive on the serial port's own thread while the UI reads Current from its own.
    private readonly object _gate = new();

    private WeightReading _current = WeightReading.None;
    private decimal _tare;
    private bool _started;
    private bool _disposed;

    public SerialScaleService(ISerialPort port)
    {
        ArgumentNullException.ThrowIfNull(port);
        _port = port;
    }

    public bool IsConfigured => true;

    public string Name => $"serial scale ({_port.PortName})";

    public bool IsConnected => _started && _port.IsOpen;

    public WeightReading Current
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    public event EventHandler<WeightReading>? WeightChanged;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_started)
            return;

        _port.DataReceived += OnDataReceived;

        if (!_port.IsOpen)
            _port.Open();

        _started = true;
    }

    public void Stop()
    {
        if (!_started)
            return;

        _port.DataReceived -= OnDataReceived;
        _started = false;

        lock (_gate)
            _buffer.Clear();
    }

    public bool Tare()
    {
        lock (_gate)
        {
            // Taring off a moving reading captures a number that was never really there, and every
            // weight afterwards inherits the error.
            if (_current.Stability != WeightStability.Stable)
                return false;

            _tare = _current.Gross;
            _current = _current with { Tare = _tare };
        }

        WeightChanged?.Invoke(this, Current);
        return true;
    }

    public void ClearTare()
    {
        lock (_gate)
        {
            _tare = 0m;
            _current = _current with { Tare = 0m };
        }

        WeightChanged?.Invoke(this, Current);
    }

    private void OnDataReceived(object? sender, byte[] data)
    {
        var completed = new List<WeightReading>();

        lock (_gate)
        {
            foreach (var b in data)
            {
                var character = (char)b;

                if (character is '\r' or '\n')
                {
                    if (_buffer.Length > 0)
                    {
                        if (WeightFrameParser.TryParse(_buffer.ToString(), out var frame))
                        {
                            // A frame the scale has already tared reports net; applying our own
                            // tare on top would subtract the container twice.
                            var tare = frame.Mode == WeightMode.Net ? 0m : _tare;
                            _current = new WeightReading(frame.Kilograms, tare, frame.Stability);
                            completed.Add(_current);
                        }

                        _buffer.Clear();
                    }

                    continue;
                }

                _buffer.Append(character);

                if (_buffer.Length > MaxFrameLength)
                    _buffer.Clear();
            }
        }

        foreach (var reading in completed)
            WeightChanged?.Invoke(this, reading);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Stop();
        _port.Dispose();
    }
}

/// <summary>Stands in on a lane with no scale.</summary>
public sealed class NoScaleService : IScaleService
{
    public bool IsConfigured => false;

    public string Name => "none";

    public bool IsConnected => false;

    public WeightReading Current => WeightReading.None;

    public event EventHandler<WeightReading>? WeightChanged
    {
        add { }
        remove { }
    }

    public void Start()
    {
    }

    public void Stop()
    {
    }

    public bool Tare() => false;

    public void ClearTare()
    {
    }

    public void Dispose()
    {
    }
}
