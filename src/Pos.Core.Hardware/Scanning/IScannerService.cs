using System.Text;
using Pos.Core.Hardware.Serial;

namespace Pos.Core.Hardware.Scanning;

/// <param name="Code">What the scanner read.</param>
/// <param name="Symbology">What kind of code it appears to be.</param>
/// <param name="CheckDigitValid">
/// False only when the symbology has a check digit and it does not agree. A code with no check
/// digit to test reports true.
/// </param>
public readonly record struct ScannedBarcode(string Code, Symbology Symbology, bool CheckDigitValid);

/// <summary>
/// Delivers barcodes from a scanner (SRS 2.6).
/// </summary>
/// <remarks>
/// Most retail scanners present as a keyboard and type the barcode followed by Enter, which needs
/// no driver and never reaches this interface — the UI recognises that burst by its timing
/// (ARCHITECTURE.md section 4) and feeds it to
/// <see cref="KeyboardWedgeScannerService"/>. This interface exists for the other case: a scanner
/// on a serial line, and a common surface for the diagnostics tool to test either kind through.
/// </remarks>
public interface IScannerService : IDisposable
{
    bool IsConfigured { get; }

    string Name { get; }

    bool IsConnected { get; }

    event EventHandler<ScannedBarcode>? BarcodeScanned;

    void Start();

    void Stop();
}

/// <summary>Shared plumbing: turn a raw code into an event, once.</summary>
public abstract class ScannerServiceBase : IScannerService
{
    public abstract bool IsConfigured { get; }

    public abstract string Name { get; }

    public virtual bool IsConnected => IsConfigured;

    public event EventHandler<ScannedBarcode>? BarcodeScanned;

    public virtual void Start()
    {
    }

    public virtual void Stop()
    {
    }

    /// <summary>
    /// Publishes a code. Blank reads are dropped rather than raised: a scanner that catches a
    /// glint sends an empty line, and nobody wants a search fired for nothing.
    /// </summary>
    protected void Publish(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return;

        var trimmed = code.Trim();

        BarcodeScanned?.Invoke(this, new ScannedBarcode(
            trimmed,
            Barcode.Identify(trimmed),
            Barcode.IsValid(trimmed)));
    }

    public virtual void Dispose() => GC.SuppressFinalize(this);
}

/// <summary>
/// The keyboard-emulation path. The UI hands over a burst it has already recognised as a scan, and
/// this puts it through the same pipeline a serial scanner's reads go through, so both kinds of
/// scanner are validated and reported identically.
/// </summary>
public sealed class KeyboardWedgeScannerService : ScannerServiceBase
{
    public override bool IsConfigured => true;

    public override string Name => "keyboard wedge (HID)";

    /// <summary>Called by the UI when a keystroke burst has been classified as a scan.</summary>
    public void Accept(string code) => Publish(code);
}

/// <summary>A scanner on a serial line, delivering one code per terminated line.</summary>
public sealed class SerialScannerService : ScannerServiceBase
{
    private const int MaxCodeLength = 256;

    private readonly ISerialPort _port;
    private readonly StringBuilder _buffer = new();
    private readonly object _gate = new();

    private bool _overflowed;
    private bool _started;
    private bool _disposed;

    public SerialScannerService(ISerialPort port)
    {
        ArgumentNullException.ThrowIfNull(port);
        _port = port;
    }

    public override bool IsConfigured => true;

    public override string Name => $"serial scanner ({_port.PortName})";

    public override bool IsConnected => _started && _port.IsOpen;

    public override void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_started)
            return;

        _port.DataReceived += OnDataReceived;

        if (!_port.IsOpen)
            _port.Open();

        _started = true;
    }

    public override void Stop()
    {
        if (!_started)
            return;

        _port.DataReceived -= OnDataReceived;
        _started = false;

        lock (_gate)
            _buffer.Clear();
    }

    private void OnDataReceived(object? sender, byte[] data)
    {
        var codes = new List<string>();

        lock (_gate)
        {
            foreach (var b in data)
            {
                var character = (char)b;

                if (character is '\r' or '\n')
                {
                    // Whatever is left of an overflowed run is the tail of line noise, not a
                    // barcode. Publishing it would fire a search for garbage, so the whole run is
                    // dropped and the next terminator starts a clean read.
                    if (_buffer.Length > 0 && !_overflowed)
                        codes.Add(_buffer.ToString());

                    _buffer.Clear();
                    _overflowed = false;
                    continue;
                }

                _buffer.Append(character);

                // A code this long is line noise, not a barcode.
                if (_buffer.Length > MaxCodeLength)
                {
                    _buffer.Clear();
                    _overflowed = true;
                }
            }
        }

        foreach (var code in codes)
            Publish(code);
    }

    public override void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Stop();
        _port.Dispose();
        base.Dispose();
    }
}

/// <summary>Stands in on a lane with no scanner attached, and lets tests fire scans by hand.</summary>
public sealed class FakeScannerService : ScannerServiceBase
{
    public override bool IsConfigured { get; } = true;

    public override string Name => "fake scanner";

    public bool Started { get; private set; }

    public override bool IsConnected => Started;

    public override void Start() => Started = true;

    public override void Stop() => Started = false;

    /// <summary>Fires a scan as if the device had read it.</summary>
    public void Scan(string code) => Publish(code);
}
