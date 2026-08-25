using Pos.App.Input;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// ARCHITECTURE.md section 4: a burst of keystrokes with no gap larger than the threshold, ending
/// in Enter, is a scanner. Anything else is a person typing.
/// </summary>
public class ScannerInputClassifierTests
{
    private const double ScannerGapMs = 5;
    private const double HumanGapMs = 120;

    private static (ScannerInputClassifier Classifier, FakeClock Clock) New(
        double? maxGapMs = null,
        int minScanLength = ScannerInputClassifier.DefaultMinScanLength)
    {
        var clock = new FakeClock();
        var classifier = new ScannerInputClassifier(
            clock,
            maxGapMs is null ? null : TimeSpan.FromMilliseconds(maxGapMs.Value),
            minScanLength);

        return (classifier, clock);
    }

    /// <summary>Types <paramref name="length"/> characters, each <paramref name="gapMs"/> after the last.</summary>
    private static void Burst(ScannerInputClassifier classifier, FakeClock clock, int length, double gapMs)
    {
        for (var i = 0; i < length; i++)
        {
            clock.Advance(gapMs);
            classifier.RecordKeystroke();
        }
    }

    [Fact]
    public void AFastBurstEndingInEnterIsAScan()
    {
        var (classifier, clock) = New();

        Burst(classifier, clock, length: 13, gapMs: ScannerGapMs);
        clock.Advance(ScannerGapMs);

        Assert.Equal(InputKind.Scanner, classifier.ClassifyOnEnter());
    }

    [Fact]
    public void TypingAtHumanSpeedIsNotAScan()
    {
        var (classifier, clock) = New();

        Burst(classifier, clock, length: 6, gapMs: HumanGapMs);
        clock.Advance(HumanGapMs);

        Assert.Equal(InputKind.Typed, classifier.ClassifyOnEnter());
    }

    /// <summary>
    /// One slow keystroke anywhere in the burst disqualifies the whole thing — a scanner does not
    /// pause in the middle of a barcode.
    /// </summary>
    [Fact]
    public void ASingleSlowKeystrokeMidBurstDisqualifiesTheScan()
    {
        var (classifier, clock) = New();

        Burst(classifier, clock, length: 6, gapMs: ScannerGapMs);
        clock.Advance(HumanGapMs);
        classifier.RecordKeystroke();
        Burst(classifier, clock, length: 6, gapMs: ScannerGapMs);
        clock.Advance(ScannerGapMs);

        Assert.Equal(InputKind.Typed, classifier.ClassifyOnEnter());
    }

    /// <summary>
    /// The gap before Enter counts. A cashier who types a code quickly and then pauses before
    /// committing is the exact case that would otherwise be misread as a scan.
    /// </summary>
    [Fact]
    public void PausingBeforeEnterDisqualifiesTheScan()
    {
        var (classifier, clock) = New();

        Burst(classifier, clock, length: 13, gapMs: ScannerGapMs);
        clock.Advance(HumanGapMs);

        Assert.Equal(InputKind.Typed, classifier.ClassifyOnEnter());
    }

    [Fact]
    public void EnterOnAnEmptyBoxIsNotAScan()
    {
        var (classifier, _) = New();

        Assert.Equal(InputKind.Typed, classifier.ClassifyOnEnter());
    }

    /// <summary>
    /// A burst too short to be a barcode is not a scan, however fast it arrived. Without this
    /// floor a one-character burst qualifies vacuously, having no gaps to be too slow.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ABurstShorterThanTheFloorIsNotAScan(int length)
    {
        var (classifier, clock) = New(minScanLength: 4);

        Burst(classifier, clock, length, gapMs: ScannerGapMs);
        clock.Advance(ScannerGapMs);

        Assert.Equal(InputKind.Typed, classifier.ClassifyOnEnter());
    }

    [Fact]
    public void ABurstAtTheFloorLengthIsAScan()
    {
        var (classifier, clock) = New(minScanLength: 4);

        Burst(classifier, clock, length: 4, gapMs: ScannerGapMs);
        clock.Advance(ScannerGapMs);

        Assert.Equal(InputKind.Scanner, classifier.ClassifyOnEnter());
    }

    /// <summary>A gap exactly at the threshold is still part of the burst; one past it is not.</summary>
    [Fact]
    public void TheThresholdIsInclusive()
    {
        var (atThreshold, clockA) = New(maxGapMs: 30);
        Burst(atThreshold, clockA, length: 8, gapMs: 30);
        clockA.Advance(30);
        Assert.Equal(InputKind.Scanner, atThreshold.ClassifyOnEnter());

        var (pastThreshold, clockB) = New(maxGapMs: 30);
        Burst(pastThreshold, clockB, length: 8, gapMs: 31);
        clockB.Advance(31);
        Assert.Equal(InputKind.Typed, pastThreshold.ClassifyOnEnter());
    }

    /// <summary>
    /// The threshold depends on the scanner's polling rate, so it has to be settable per site.
    /// </summary>
    [Fact]
    public void TheThresholdIsConfigurable()
    {
        var (tolerant, clock) = New(maxGapMs: 200);

        Burst(tolerant, clock, length: 8, gapMs: 150);
        clock.Advance(150);

        Assert.Equal(InputKind.Scanner, tolerant.ClassifyOnEnter());
    }

    [Fact]
    public void ResetStartsAFreshBurst()
    {
        var (classifier, clock) = New();

        Burst(classifier, clock, length: 6, gapMs: HumanGapMs);
        classifier.Reset();
        Assert.Equal(0, classifier.KeystrokeCount);

        Burst(classifier, clock, length: 13, gapMs: ScannerGapMs);
        clock.Advance(ScannerGapMs);

        Assert.Equal(InputKind.Scanner, classifier.ClassifyOnEnter());
    }

    [Fact]
    public void AMinimumScanLengthBelowOneIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScannerInputClassifier(new FakeClock(), minScanLength: 0));
    }
}
