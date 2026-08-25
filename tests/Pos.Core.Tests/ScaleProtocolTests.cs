using Pos.Core.Hardware.Serial;
using Pos.Core.Hardware.Weighing;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// The scale's serial protocol, and the service that reassembles it. A misparsed frame prices
/// somebody's groceries wrong, so the parser is expected to reject anything it does not fully
/// understand rather than guess.
/// </summary>
public class ScaleProtocolTests
{
    // ---- Frame parsing ------------------------------------------------------------------------

    [Theory]
    [InlineData("ST,GS,+  1.234kg", WeightStability.Stable, WeightMode.Gross, 1.234)]
    [InlineData("US,GS,+  1.234kg", WeightStability.Unstable, WeightMode.Gross, 1.234)]
    [InlineData("OL,GS,+ 99.999kg", WeightStability.Overload, WeightMode.Gross, 99.999)]
    [InlineData("ST,NT,+  0.500kg", WeightStability.Stable, WeightMode.Net, 0.5)]
    [InlineData("ST,GS,-  0.020kg", WeightStability.Stable, WeightMode.Gross, -0.02)]
    [InlineData("ST,GS,+  0.000kg", WeightStability.Stable, WeightMode.Gross, 0.0)]
    public void AWellFormedFrameParses(string frame, WeightStability stability, WeightMode mode, double kilograms)
    {
        Assert.True(WeightFrameParser.TryParse(frame, out var reading));

        Assert.Equal(stability, reading.Stability);
        Assert.Equal(mode, reading.Mode);
        Assert.Equal((decimal)kilograms, reading.Kilograms);
    }

    /// <summary>Whatever unit the scale is set to, the till works in kilograms.</summary>
    [Theory]
    [InlineData("ST,GS,+  1.234kg", 1.234)]
    [InlineData("ST,GS,+500.000g", 0.5)]
    [InlineData("ST,GS,+  2.000", 2.0)]
    public void WeightsAreNormalisedToKilograms(string frame, double expected)
    {
        Assert.True(WeightFrameParser.TryParse(frame, out var reading));
        Assert.Equal((decimal)expected, reading.Kilograms);
    }

    [Fact]
    public void PoundsAreConverted()
    {
        Assert.True(WeightFrameParser.TryParse("ST,GS,+  1.000lb", out var reading));
        Assert.Equal(0.45359237m, reading.Kilograms);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("ST,GS")]                    // a field short
    [InlineData("ST,GS,+1.0,extra,fields")]  // too many fields
    [InlineData("XX,GS,+  1.234kg")]         // unknown status
    [InlineData("ST,XX,+  1.234kg")]         // unknown mode
    [InlineData("ST,GS,+abcdkg")]            // weight is not a number
    [InlineData("ST,GS,kg")]                 // no magnitude at all
    [InlineData("garbage")]
    public void AMalformedFrameIsRejectedRatherThanGuessedAt(string? frame)
    {
        Assert.False(WeightFrameParser.TryParse(frame, out _));
    }

    [Fact]
    public void CaseDoesNotMatter()
    {
        Assert.True(WeightFrameParser.TryParse("st,gs,+  1.234KG", out var reading));
        Assert.Equal(1.234m, reading.Kilograms);
    }

    // ---- Checksums ----------------------------------------------------------------------------

    [Fact]
    public void TheChecksumIsAnXorOfThePayload()
    {
        // Worked by hand: XOR of every byte of "AB" is 0x41 ^ 0x42 = 0x03.
        Assert.Equal(0x03, WeightFrameParser.Checksum("AB"));
        Assert.Equal(0x00, WeightFrameParser.Checksum(""));
    }

    [Fact]
    public void AFrameWithAValidChecksumParses()
    {
        var frame = WeightFrameParser.Format(new WeightFrame(WeightStability.Stable, WeightMode.Gross, 1.234m), withChecksum: true);

        Assert.Contains(',', frame);
        Assert.True(WeightFrameParser.TryParse(frame, out var reading));
        Assert.Equal(1.234m, reading.Kilograms);
    }

    /// <summary>
    /// A frame whose checksum does not match has been corrupted on the wire. Another one arrives
    /// in a fraction of a second, so dropping it costs nothing and trusting it costs a wrong price.
    /// </summary>
    [Fact]
    public void AFrameWithABrokenChecksumIsRejected()
    {
        var frame = WeightFrameParser.Format(new WeightFrame(WeightStability.Stable, WeightMode.Gross, 1.234m), withChecksum: true);
        var corrupted = frame[..^2] + "00";

        Assert.False(WeightFrameParser.TryParse(corrupted, out _));
    }

    /// <summary>Plenty of scales send no checksum at all, and those frames are still good.</summary>
    [Fact]
    public void AFrameWithNoChecksumIsStillAccepted()
    {
        Assert.True(WeightFrameParser.TryParse("ST,GS,+  1.234kg", out var reading));
        Assert.Equal(1.234m, reading.Kilograms);
    }

    [Fact]
    public void FramesRoundTripThroughTheFormatter()
    {
        foreach (var withChecksum in new[] { false, true })
        {
            var original = new WeightFrame(WeightStability.Stable, WeightMode.Net, 2.5m);
            var frame = WeightFrameParser.Format(original, withChecksum: withChecksum);

            Assert.True(WeightFrameParser.TryParse(frame, out var parsed));
            Assert.Equal(original, parsed);
        }
    }

    // ---- The service --------------------------------------------------------------------------

    private static (SerialScaleService Scale, FakeSerialPort Port) NewScale()
    {
        var port = new FakeSerialPort();
        var scale = new SerialScaleService(port);
        scale.Start();
        return (scale, port);
    }

    [Fact]
    public void AStreamedFrameBecomesTheCurrentReading()
    {
        var (scale, port) = NewScale();
        using var _ = scale;

        port.Receive("ST,GS,+  1.234kg\r\n");

        Assert.Equal(1.234m, scale.Current.Gross);
        Assert.Equal(WeightStability.Stable, scale.Current.Stability);
        Assert.True(scale.Current.CanBeBilled);
    }

    /// <summary>
    /// Serial data arrives in whatever chunks the driver feels like. A reading split across three
    /// reads has to come out as one frame.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public void FramesSplitAcrossReadsAreReassembled(int chunkSize)
    {
        var (scale, port) = NewScale();
        using var _ = scale;

        port.ReceiveInChunks("ST,GS,+  1.234kg\r\n", chunkSize);

        Assert.Equal(1.234m, scale.Current.Gross);
    }

    [Fact]
    public void SeveralFramesInOneReadAllArrive()
    {
        var (scale, port) = NewScale();
        using var _ = scale;

        var readings = new List<WeightReading>();
        scale.WeightChanged += (_, reading) => readings.Add(reading);

        port.Receive("ST,GS,+  1.000kg\r\nST,GS,+  2.000kg\r\nST,GS,+  3.000kg\r\n");

        Assert.Equal(3, readings.Count);
        Assert.Equal(3.000m, scale.Current.Gross);
    }

    [Fact]
    public void APartialFrameIsHeldUntilItsTerminatorArrives()
    {
        var (scale, port) = NewScale();
        using var _ = scale;

        port.Receive("ST,GS,+  1.2");
        Assert.Equal(0m, scale.Current.Gross);

        port.Receive("34kg\r\n");
        Assert.Equal(1.234m, scale.Current.Gross);
    }

    [Fact]
    public void AGarbledFrameIsDroppedAndTheNextOneStillLands()
    {
        var (scale, port) = NewScale();
        using var _ = scale;

        port.Receive("ST,GS,+  1.000kg\r\n");
        port.Receive("!!!noise!!!\r\n");

        Assert.Equal(1.000m, scale.Current.Gross);

        port.Receive("ST,GS,+  2.000kg\r\n");

        Assert.Equal(2.000m, scale.Current.Gross);
    }

    /// <summary>
    /// A line that never sends a terminator must not grow the buffer without limit — that is a
    /// till that slowly eats memory over a trading day. The noise costs the frame it is attached
    /// to and nothing beyond it.
    /// </summary>
    [Fact]
    public void AnEndlessFrameDoesNotGrowWithoutBound()
    {
        var (scale, port) = NewScale();
        using var _ = scale;

        port.Receive(new string('x', 10_000));
        port.Receive("\r\n");
        port.Receive("ST,GS,+  1.234kg\r\n");

        Assert.Equal(1.234m, scale.Current.Gross);
    }

    // ---- Stability ----------------------------------------------------------------------------

    /// <summary>
    /// The gate on pricing. An unstable number is one that is still changing, and billing it
    /// charges for a weight that was never on the scale.
    /// </summary>
    [Fact]
    public void AnUnstableReadingCannotBeBilled()
    {
        var (scale, port) = NewScale();
        using var _ = scale;

        port.Receive("US,GS,+  1.234kg\r\n");

        Assert.False(scale.Current.CanBeBilled);

        port.Receive("ST,GS,+  1.234kg\r\n");

        Assert.True(scale.Current.CanBeBilled);
    }

    [Fact]
    public void AnEmptyPanCannotBeBilled()
    {
        var (scale, port) = NewScale();
        using var _ = scale;

        port.Receive("ST,GS,+  0.000kg\r\n");

        Assert.False(scale.Current.CanBeBilled);
    }

    [Fact]
    public void AnOverloadedScaleCannotBeBilled()
    {
        var (scale, port) = NewScale();
        using var _ = scale;

        port.Receive("OL,GS,+ 99.999kg\r\n");

        Assert.False(scale.Current.CanBeBilled);
    }

    // ---- Tare ---------------------------------------------------------------------------------

    [Fact]
    public void TaringSubtractsTheContainerFromWhatFollows()
    {
        var (scale, port) = NewScale();
        using var _ = scale;

        port.Receive("ST,GS,+  0.200kg\r\n");
        Assert.True(scale.Tare());

        port.Receive("ST,GS,+  1.200kg\r\n");

        Assert.Equal(1.200m, scale.Current.Gross);
        Assert.Equal(0.200m, scale.Current.Tare);
        Assert.Equal(1.000m, scale.Current.Net);
    }

    /// <summary>
    /// Taring off a moving reading captures a number that was never really there, and every weight
    /// afterwards inherits the error.
    /// </summary>
    [Fact]
    public void TaringAnUnstableReadingIsRefused()
    {
        var (scale, port) = NewScale();
        using var _ = scale;

        port.Receive("US,GS,+  0.200kg\r\n");

        Assert.False(scale.Tare());
        Assert.Equal(0m, scale.Current.Tare);
    }

    [Fact]
    public void ClearingTheTareGoesBackToGross()
    {
        var (scale, port) = NewScale();
        using var _ = scale;

        port.Receive("ST,GS,+  0.200kg\r\n");
        scale.Tare();
        scale.ClearTare();

        port.Receive("ST,GS,+  1.200kg\r\n");

        Assert.Equal(1.200m, scale.Current.Net);
    }

    /// <summary>
    /// A scale that has already taken its own tare reports net. Applying ours on top would take
    /// the container off twice.
    /// </summary>
    [Fact]
    public void ATareTheScaleHasAlreadyAppliedIsNotAppliedAgain()
    {
        var (scale, port) = NewScale();
        using var _ = scale;

        port.Receive("ST,GS,+  0.200kg\r\n");
        scale.Tare();

        port.Receive("ST,NT,+  1.000kg\r\n");

        Assert.Equal(0m, scale.Current.Tare);
        Assert.Equal(1.000m, scale.Current.Net);
    }

    [Fact]
    public void TheNetWeightNeverGoesNegative()
    {
        var (scale, port) = NewScale();
        using var _ = scale;

        port.Receive("ST,GS,+  1.000kg\r\n");
        scale.Tare();

        // The container came off the pan after being tared.
        port.Receive("ST,GS,+  0.100kg\r\n");

        Assert.Equal(0m, scale.Current.Net);
        Assert.False(scale.Current.CanBeBilled);
    }

    // ---- Disconnection ------------------------------------------------------------------------

    [Fact]
    public void ALaneWithNoScaleReportsSoAndBillsNothing()
    {
        using var scale = new NoScaleService();

        Assert.False(scale.IsConfigured);
        Assert.False(scale.IsConnected);
        Assert.False(scale.Current.CanBeBilled);
        Assert.False(scale.Tare());
    }

    [Fact]
    public void StoppingTheScaleEndsTheStream()
    {
        var (scale, port) = NewScale();
        using var _ = scale;

        scale.Stop();
        port.Receive("ST,GS,+  1.234kg\r\n");

        Assert.Equal(0m, scale.Current.Gross);
        Assert.False(scale.IsConnected);
    }

    /// <summary>
    /// A scale unplugged mid-transaction stops reporting connected, so the UI can grey out the
    /// weigh key instead of letting a cashier bill the last weight it happened to see.
    /// </summary>
    [Fact]
    public void AScaleUnpluggedMidTransactionShowsAsDisconnected()
    {
        var (scale, port) = NewScale();
        using var _ = scale;

        port.Receive("ST,GS,+  1.234kg\r\n");
        Assert.True(scale.IsConnected);

        port.Close();

        Assert.False(scale.IsConnected);
    }
}
