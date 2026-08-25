using System.Text;

namespace Pos.Core.Hardware.Weighing;

/// <summary>
/// Turns a byte stream from a scale into readings.
/// </summary>
/// <remarks>
/// Framing is part of the protocol, not separate from it: one family terminates frames with CR/LF,
/// another wraps them in STX and ETX. A reader therefore owns both the framing and the parsing,
/// and is fed raw bytes as they arrive in whatever chunks the driver produces.
/// </remarks>
public interface IWeightFrameReader
{
    /// <summary>Named so diagnostics can report which protocol a lane's scale is actually speaking.</summary>
    string Name { get; }

    /// <summary>Feeds bytes in, gets any frames that completed out.</summary>
    IReadOnlyList<WeightFrame> Consume(byte[] data);

    /// <summary>Drops any partial frame, for when the stream is restarted.</summary>
    void Reset();
}

/// <summary>
/// The comma-separated continuous format: <c>ST,GS,+  1.234kg</c>, terminated by CR/LF, with an
/// optional XOR checksum. Used by Essae, Contech and similar counter scales.
/// </summary>
public sealed class LineWeightFrameReader : IWeightFrameReader
{
    private const int MaxFrameLength = 128;

    private readonly StringBuilder _buffer = new();
    private bool _overflowed;

    public string Name => "line (ST,GS,+1.234kg)";

    public IReadOnlyList<WeightFrame> Consume(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var readings = new List<WeightFrame>();

        foreach (var b in data)
        {
            var character = (char)b;

            if (character is '\r' or '\n')
            {
                if (_buffer.Length > 0 && !_overflowed && WeightFrameParser.TryParse(_buffer.ToString(), out var frame))
                    readings.Add(frame);

                _buffer.Clear();
                _overflowed = false;
                continue;
            }

            _buffer.Append(character);

            if (_buffer.Length > MaxFrameLength)
            {
                _buffer.Clear();
                _overflowed = true;
            }
        }

        return readings;
    }

    public void Reset()
    {
        _buffer.Clear();
        _overflowed = false;
    }
}

/// <summary>
/// The STX-framed format used by Toledo, CAS and the scales that follow them.
/// </summary>
/// <remarks>
/// Two shapes are in the field and both are accepted:
/// <list type="bullet">
/// <item>
/// The checked form — <c>STX status sign wwwww uu BCC ETX</c> — where the block check character is
/// the XOR of the nine payload characters. A frame whose BCC does not match is dropped.
/// </item>
/// <item>
/// The bare form — <c>STX + 1.250 kg CR</c> — which carries no status field at all.
/// </item>
/// </list>
/// <para>
/// The bare form creates a real problem: with no status field there is no way to know from the
/// frame whether the pan has settled, and billing a moving reading charges for a weight that was
/// never there. Rather than assume stable, this reader settles the reading itself — a value is
/// reported stable only once it has arrived unchanged <see cref="SettleFrames"/> times running.
/// That is a deliberate substitute for a field the protocol does not carry, and it is worth
/// confirming against the actual scale before a pilot.
/// </para>
/// </remarks>
public sealed class StxEtxWeightFrameReader : IWeightFrameReader
{
    public const byte Stx = 0x02;
    public const byte Etx = 0x03;

    private const int MaxFrameLength = 64;

    /// <summary>Identical readings needed before a status-less frame is called stable.</summary>
    public const int SettleFrames = 3;

    private readonly StringBuilder _buffer = new();
    private bool _inFrame;
    private bool _overflowed;

    private decimal _lastValue;
    private int _repeats;

    public string Name => "STX/ETX (Toledo/CAS)";

    public IReadOnlyList<WeightFrame> Consume(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var readings = new List<WeightFrame>();

        foreach (var b in data)
        {
            if (b == Stx)
            {
                // A second STX means the previous frame was cut short. Start again from here.
                _buffer.Clear();
                _inFrame = true;
                _overflowed = false;
                continue;
            }

            if (!_inFrame)
                continue;

            if (b is Etx or (byte)'\r' or (byte)'\n')
            {
                if (_buffer.Length > 0 && !_overflowed && TryParseFrame(_buffer.ToString(), b == Etx, out var frame))
                    readings.Add(frame);

                _buffer.Clear();
                _inFrame = false;
                _overflowed = false;
                continue;
            }

            _buffer.Append((char)b);

            if (_buffer.Length > MaxFrameLength)
            {
                _buffer.Clear();
                _overflowed = true;
            }
        }

        return readings;
    }

    private bool TryParseFrame(string payload, bool checkedForm, out WeightFrame frame)
    {
        frame = default;

        // The checked form ends in a block check character covering everything before it.
        if (checkedForm && payload.Length >= 2)
        {
            var body = payload[..^1];

            if (Bcc(body) != payload[^1])
                return false;

            payload = body;
        }

        return TryParseBody(payload, out frame);
    }

    /// <summary>
    /// Reads the body, which may or may not open with a status character. A leading '+' or '-' is
    /// a sign, so anything else in that position is the status field.
    /// </summary>
    private bool TryParseBody(string body, out WeightFrame frame)
    {
        frame = default;

        var text = body.TrimEnd();

        if (text.Length == 0)
            return false;

        WeightStability? declared = null;

        if (text[0] is not ('+' or '-') && !char.IsDigit(text[0]) && text[0] != ' ')
        {
            declared = text[0] switch
            {
                'S' or 's' => WeightStability.Stable,
                'U' or 'u' or 'M' or 'm' => WeightStability.Unstable,
                'O' or 'o' or 'E' or 'e' => WeightStability.Overload,
                _ => null,
            };

            if (declared is null)
                return false;

            text = text[1..];
        }

        if (!WeightFrameParser.TryParseMagnitude(text, out var kilograms))
            return false;

        var stability = declared ?? Settle(kilograms);

        if (declared is not null)
            Settle(kilograms);

        frame = new WeightFrame(stability, WeightMode.Gross, kilograms);
        return true;
    }

    /// <summary>
    /// Software stability for a protocol variant that carries no status. A reading counts as
    /// settled only once it has repeated unchanged.
    /// </summary>
    private WeightStability Settle(decimal kilograms)
    {
        if (kilograms == _lastValue)
            _repeats++;
        else
        {
            _lastValue = kilograms;
            _repeats = 1;
        }

        return _repeats >= SettleFrames ? WeightStability.Stable : WeightStability.Unstable;
    }

    /// <summary>Block check character: XOR of every byte it covers.</summary>
    public static char Bcc(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var check = 0;

        foreach (var character in payload)
            check ^= character;

        return (char)check;
    }

    /// <summary>Builds a frame, for the fake scale and the tests.</summary>
    public static byte[] Format(WeightFrame reading, string unit = "kg", bool withStatus = true, bool withBcc = true)
    {
        var status = withStatus
            ? reading.Stability switch
            {
                WeightStability.Stable => "S",
                WeightStability.Unstable => "U",
                _ => "O",
            }
            : string.Empty;

        var sign = reading.Kilograms < 0m ? "-" : "+";
        var magnitude = Math.Abs(reading.Kilograms).ToString("00.000", System.Globalization.CultureInfo.InvariantCulture);
        var body = $"{status}{sign}{magnitude}{unit}";

        var bytes = new List<byte> { Stx };
        bytes.AddRange(Encoding.ASCII.GetBytes(body));

        if (withBcc)
        {
            bytes.Add((byte)Bcc(body));
            bytes.Add(Etx);
        }
        else
        {
            bytes.Add((byte)'\r');
        }

        return [.. bytes];
    }

    public void Reset()
    {
        _buffer.Clear();
        _inFrame = false;
        _overflowed = false;
        _repeats = 0;
        _lastValue = 0m;
    }
}

/// <summary>
/// Runs several readers side by side and latches onto whichever one the scale is actually speaking.
/// </summary>
/// <remarks>
/// A store rarely knows which protocol its scale is set to, and the setting is often behind a
/// service menu. Feeding the stream to every reader until one produces a valid frame turns that
/// from a support call into a detail nobody has to think about. Once a reader has produced a
/// frame, the others are dropped, so a noisy line cannot make the scale appear to change protocol
/// mid-shift.
/// </remarks>
public sealed class AutoDetectingWeightFrameReader : IWeightFrameReader
{
    private readonly List<IWeightFrameReader> _candidates;
    private IWeightFrameReader? _detected;

    public AutoDetectingWeightFrameReader(params IWeightFrameReader[] candidates)
    {
        if (candidates is null || candidates.Length == 0)
            candidates = [new LineWeightFrameReader(), new StxEtxWeightFrameReader()];

        _candidates = [.. candidates];
    }

    public string Name => _detected is null
        ? $"auto ({string.Join(", ", _candidates.Select(c => c.Name))})"
        : $"auto — detected {_detected.Name}";

    /// <summary>Which protocol the scale turned out to be speaking, once one has been seen.</summary>
    public IWeightFrameReader? Detected => _detected;

    public IReadOnlyList<WeightFrame> Consume(byte[] data)
    {
        if (_detected is not null)
            return _detected.Consume(data);

        foreach (var candidate in _candidates)
        {
            var readings = candidate.Consume(data);

            if (readings.Count == 0)
                continue;

            _detected = candidate;
            return readings;
        }

        return [];
    }

    public void Reset()
    {
        _detected = null;

        foreach (var candidate in _candidates)
            candidate.Reset();
    }
}
