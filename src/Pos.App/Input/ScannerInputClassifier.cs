namespace Pos.App.Input;

public enum InputKind
{
    /// <summary>A person typing. Apply the debounce window before querying.</summary>
    Typed = 0,

    /// <summary>A scanner burst. Skip the debounce and resolve by exact barcode straight away.</summary>
    Scanner = 1,
}

/// <summary>
/// Decides whether a burst of keystrokes came from a barcode scanner or from a person, using the
/// timing rule in ARCHITECTURE.md section 4: a scanner delivers its characters in a tight burst
/// terminated by Enter, faster than a human can type.
/// </summary>
/// <remarks>
/// This is a heuristic and the architecture doc says so. Real scanners vary in polling rate, so
/// both thresholds are settable rather than baked in, and callers are expected to fall back to a
/// normal search when a burst classified as a scan does not resolve to a barcode. Getting it wrong
/// therefore costs a failed index seek, not a failed sale.
/// </remarks>
public sealed class ScannerInputClassifier
{
    public static readonly TimeSpan DefaultMaxKeystrokeGap = TimeSpan.FromMilliseconds(30);

    /// <summary>
    /// Shortest burst that may be called a scan. Not in the architecture spec, but without a floor
    /// a one-character burst classifies as a scan vacuously — it has no gaps to be too slow — and
    /// so would pressing Enter on an empty box. Real barcodes are far longer than this.
    /// </summary>
    public const int DefaultMinScanLength = 4;

    private readonly IClock _clock;

    private int _keystrokeCount;
    private TimeSpan _lastKeystroke;
    private bool _burstIntact = true;

    public ScannerInputClassifier(IClock clock, TimeSpan? maxKeystrokeGap = null, int minScanLength = DefaultMinScanLength)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (minScanLength < 1)
            throw new ArgumentOutOfRangeException(nameof(minScanLength), minScanLength, "Minimum scan length must be at least one character.");

        _clock = clock;
        MaxKeystrokeGap = maxKeystrokeGap ?? DefaultMaxKeystrokeGap;
        MinScanLength = minScanLength;
    }

    /// <summary>
    /// Longest gap between two keystrokes that still counts as part of one burst. Tune per device.
    /// </summary>
    public TimeSpan MaxKeystrokeGap { get; set; }

    public int MinScanLength { get; }

    /// <summary>Characters recorded in the burst so far, not counting the terminating Enter.</summary>
    public int KeystrokeCount => _keystrokeCount;

    /// <summary>Records one character keystroke. Call for every key that reaches the search box.</summary>
    public void RecordKeystroke()
    {
        var now = _clock.Elapsed;

        if (_keystrokeCount > 0 && now - _lastKeystroke > MaxKeystrokeGap)
            _burstIntact = false;

        _lastKeystroke = now;
        _keystrokeCount++;
    }

    /// <summary>
    /// Records the terminating Enter and classifies the burst. The gap before Enter is part of the
    /// burst: a scanner sends its terminator as fast as it sends everything else, so a human
    /// pausing before pressing Enter is exactly the case this has to catch.
    /// </summary>
    public InputKind ClassifyOnEnter()
    {
        var now = _clock.Elapsed;

        var intact = _burstIntact
            && _keystrokeCount > 0
            && now - _lastKeystroke <= MaxKeystrokeGap;

        return intact && _keystrokeCount >= MinScanLength ? InputKind.Scanner : InputKind.Typed;
    }

    /// <summary>Starts a new burst. Call after every Enter and whenever the box is cleared.</summary>
    public void Reset()
    {
        _keystrokeCount = 0;
        _lastKeystroke = default;
        _burstIntact = true;
    }
}
