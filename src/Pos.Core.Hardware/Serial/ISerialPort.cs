using System.IO.Ports;
using System.Text;

namespace Pos.Core.Hardware.Serial;

/// <summary>
/// The slice of a serial port this application needs.
/// </summary>
/// <remarks>
/// <see cref="System.IO.Ports.SerialPort"/> is sealed and only works with a real port, so every
/// peripheral that talks over RS232 — scale, drawer, some scanners — goes through this instead.
/// That is what lets the frame parsing and the disconnect handling be tested by feeding bytes in,
/// rather than by plugging something in.
/// </remarks>
public interface ISerialPort : IDisposable
{
    string PortName { get; }

    bool IsOpen { get; }

    void Open();

    void Close();

    void Write(byte[] data);

    /// <summary>Raised as bytes arrive. Frames may be split across calls and must be reassembled.</summary>
    event EventHandler<byte[]>? DataReceived;
}

/// <summary>Serial line settings. The defaults are what most retail scales ship configured for.</summary>
public sealed record SerialPortSettings(
    string PortName,
    int BaudRate = 9600,
    Parity Parity = Parity.None,
    int DataBits = 8,
    StopBits StopBits = StopBits.One,
    int ReadTimeoutMs = 500,
    int WriteTimeoutMs = 500);

/// <summary>The real thing, wrapping <see cref="System.IO.Ports.SerialPort"/>.</summary>
/// <remarks>
/// Like the raw spool printer, this is a shim thin enough that attaching a device is the only real
/// test of it. Everything that reads what it produces is tested separately.
/// </remarks>
public sealed class SystemSerialPort : ISerialPort
{
    private readonly SerialPort _port;
    private bool _disposed;

    public SystemSerialPort(SerialPortSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.PortName);

        _port = new SerialPort(settings.PortName, settings.BaudRate, settings.Parity, settings.DataBits, settings.StopBits)
        {
            ReadTimeout = settings.ReadTimeoutMs,
            WriteTimeout = settings.WriteTimeoutMs,
        };

        _port.DataReceived += OnDataReceived;
    }

    public string PortName => _port.PortName;

    public bool IsOpen => !_disposed && _port.IsOpen;

    public event EventHandler<byte[]>? DataReceived;

    public void Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_port.IsOpen)
            _port.Open();
    }

    public void Close()
    {
        if (!_disposed && _port.IsOpen)
            _port.Close();
    }

    public void Write(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        ObjectDisposedException.ThrowIf(_disposed, this);

        _port.Write(data, 0, data.Length);
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            var waiting = _port.BytesToRead;

            if (waiting <= 0)
                return;

            var buffer = new byte[waiting];
            var read = _port.Read(buffer, 0, waiting);

            if (read > 0)
                DataReceived?.Invoke(this, read == buffer.Length ? buffer : buffer[..read]);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
            // The device was unplugged between the notification and the read. Dropping the chunk is
            // correct: the reader is stream-oriented and a partial frame is discarded anyway.
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _port.DataReceived -= OnDataReceived;

        try
        {
            if (_port.IsOpen)
                _port.Close();
        }
        catch (IOException)
        {
            // Closing a port whose device has already gone is not worth surfacing.
        }

        _port.Dispose();
    }

    /// <summary>Serial ports the machine currently has, for the diagnostics tool.</summary>
    public static IReadOnlyList<string> AvailablePorts() => SerialPort.GetPortNames();
}

/// <summary>
/// A serial port with nothing on the other end but the test. Bytes pushed through
/// <see cref="Receive(string)"/> arrive exactly as a device's would, including being split
/// mid-frame.
/// </summary>
public sealed class FakeSerialPort(string portName = "COM-FAKE") : ISerialPort
{
    private readonly List<byte> _written = [];

    public string PortName { get; } = portName;

    public bool IsOpen { get; private set; }

    /// <summary>Set to make the next <see cref="Open"/> or <see cref="Write"/> fail.</summary>
    public Exception? FailWith { get; set; }

    public IReadOnlyList<byte> Written => _written;

    public event EventHandler<byte[]>? DataReceived;

    public void Open()
    {
        if (FailWith is { } failure)
            throw failure;

        IsOpen = true;
    }

    public void Close() => IsOpen = false;

    public void Write(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (FailWith is { } failure)
            throw failure;

        if (!IsOpen)
            throw new InvalidOperationException("The port is closed.");

        _written.AddRange(data);
    }

    /// <summary>Delivers bytes as if the device had sent them.</summary>
    public void Receive(byte[] data) => DataReceived?.Invoke(this, data);

    public void Receive(string text) => Receive(Encoding.ASCII.GetBytes(text));

    /// <summary>
    /// Delivers text a few bytes at a time, the way a real port does. Frame reassembly has to cope
    /// with a reading arriving in three pieces, and this is how that gets tested.
    /// </summary>
    public void ReceiveInChunks(string text, int chunkSize)
    {
        if (chunkSize < 1)
            throw new ArgumentOutOfRangeException(nameof(chunkSize), chunkSize, "A chunk is at least one byte.");

        var bytes = Encoding.ASCII.GetBytes(text);

        for (var offset = 0; offset < bytes.Length; offset += chunkSize)
            Receive(bytes[offset..Math.Min(offset + chunkSize, bytes.Length)]);
    }

    public string WrittenText() => Encoding.ASCII.GetString([.. _written]);

    public void ClearWritten() => _written.Clear();

    public void Dispose() => IsOpen = false;
}
