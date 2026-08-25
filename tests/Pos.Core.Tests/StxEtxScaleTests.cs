using System.Text;
using Pos.Core.Hardware.Serial;
using Pos.Core.Hardware.Weighing;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// The STX-framed scale protocol used by Toledo, CAS and the scales that follow them:
/// <c>STX status sign wwwww uu BCC ETX</c>, and the bare <c>STX + 1.250 kg CR</c> variant.
/// </summary>
public class StxEtxScaleTests
{
    private static byte[] Frame(string body, bool withBcc = true)
    {
        var bytes = new List<byte> { StxEtxWeightFrameReader.Stx };
        bytes.AddRange(Encoding.ASCII.GetBytes(body));

        if (withBcc)
        {
            bytes.Add((byte)StxEtxWeightFrameReader.Bcc(body));
            bytes.Add(StxEtxWeightFrameReader.Etx);
        }
        else
        {
            bytes.Add((byte)'\r');
        }

        return [.. bytes];
    }

    private static WeightFrame Single(IWeightFrameReader reader, byte[] data) =>
        Assert.Single(reader.Consume(data));

    // ---- The checked form ---------------------------------------------------------------------

    [Theory]
    [InlineData("S+01.250kg", WeightStability.Stable, 1.250)]
    [InlineData("U+01.250kg", WeightStability.Unstable, 1.250)]
    [InlineData("O+99.999kg", WeightStability.Overload, 99.999)]
    [InlineData("S-00.020kg", WeightStability.Stable, -0.020)]
    [InlineData("S+00.000kg", WeightStability.Stable, 0.0)]
    public void AFrameWithAStatusAndAValidBccParses(string body, WeightStability stability, double kilograms)
    {
        var reading = Single(new StxEtxWeightFrameReader(), Frame(body));

        Assert.Equal(stability, reading.Stability);
        Assert.Equal((decimal)kilograms, reading.Kilograms);
    }

    [Fact]
    public void TheBlockCheckCharacterIsAnXorOfTheBody()
    {
        Assert.Equal((char)0x03, StxEtxWeightFrameReader.Bcc("AB"));
        Assert.Equal((char)0x00, StxEtxWeightFrameReader.Bcc(""));
    }

    /// <summary>
    /// A frame whose block check does not match arrived corrupted. Another one follows in a
    /// fraction of a second, so dropping it costs nothing and trusting it costs a wrong price.
    /// </summary>
    [Fact]
    public void AFrameWithABadBlockCheckIsDropped()
    {
        var good = Frame("S+01.250kg");
        var corrupted = good.ToArray();
        corrupted[^2] ^= 0xFF;

        Assert.Empty(new StxEtxWeightFrameReader().Consume(corrupted));
    }

    [Fact]
    public void GramsAndPoundsAreNormalisedToKilograms()
    {
        Assert.Equal(0.5m, Single(new StxEtxWeightFrameReader(), Frame("S+500.00g ")).Kilograms);
        Assert.Equal(0.45359237m, Single(new StxEtxWeightFrameReader(), Frame("S+01.000lb")).Kilograms);
    }

    // ---- Framing ------------------------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public void AFrameSplitAcrossReadsIsReassembled(int chunkSize)
    {
        var reader = new StxEtxWeightFrameReader();
        var frame = Frame("S+01.250kg");
        var readings = new List<WeightFrame>();

        for (var offset = 0; offset < frame.Length; offset += chunkSize)
            readings.AddRange(reader.Consume(frame[offset..Math.Min(offset + chunkSize, frame.Length)]));

        Assert.Equal(1.250m, Assert.Single(readings).Kilograms);
    }

    [Fact]
    public void SeveralFramesInOneReadAllArrive()
    {
        var stream = Frame("S+01.000kg").Concat(Frame("S+02.000kg")).Concat(Frame("S+03.000kg")).ToArray();

        var readings = new StxEtxWeightFrameReader().Consume(stream);

        Assert.Equal(3, readings.Count);
        Assert.Equal(3.000m, readings[^1].Kilograms);
    }

    [Fact]
    public void BytesArrivingBeforeAnyStxAreIgnored()
    {
        var stream = Encoding.ASCII.GetBytes("junk before the frame").Concat(Frame("S+01.250kg")).ToArray();

        Assert.Equal(1.250m, Single(new StxEtxWeightFrameReader(), stream).Kilograms);
    }

    /// <summary>A second STX means the frame before it was cut short, so it is abandoned.</summary>
    [Fact]
    public void ATruncatedFrameIsAbandonedWhenTheNextOneStarts()
    {
        var reader = new StxEtxWeightFrameReader();

        reader.Consume([StxEtxWeightFrameReader.Stx, .. Encoding.ASCII.GetBytes("S+01.2")]);
        var readings = reader.Consume(Frame("S+02.500kg"));

        Assert.Equal(2.500m, Assert.Single(readings).Kilograms);
    }

    [Fact]
    public void AnEndlessFrameDoesNotGrowWithoutBound()
    {
        var reader = new StxEtxWeightFrameReader();

        reader.Consume([StxEtxWeightFrameReader.Stx, .. Encoding.ASCII.GetBytes(new string('x', 10_000))]);

        Assert.Equal(1.250m, Single(reader, Frame("S+01.250kg")).Kilograms);
    }

    [Theory]
    [InlineData("Z+01.250kg")]  // unknown status
    [InlineData("S+abcdefkg")]  // not a number
    [InlineData("S")]           // nothing but a status
    public void AFrameThatCannotBeUnderstoodIsDropped(string body)
    {
        Assert.Empty(new StxEtxWeightFrameReader().Consume(Frame(body)));
    }

    // ---- The bare form, with no status field --------------------------------------------------

    /// <summary>
    /// The bare variant carries no status, so the reader settles the reading itself. Assuming
    /// stable would mean billing a weight while the pan is still moving.
    /// </summary>
    [Fact]
    public void AStatuslessFrameIsUnstableUntilItRepeats()
    {
        var reader = new StxEtxWeightFrameReader();

        Assert.Equal(WeightStability.Unstable, Single(reader, Frame("+  1.250 kg", withBcc: false)).Stability);
        Assert.Equal(WeightStability.Unstable, Single(reader, Frame("+  1.250 kg", withBcc: false)).Stability);
        Assert.Equal(WeightStability.Stable, Single(reader, Frame("+  1.250 kg", withBcc: false)).Stability);
    }

    [Fact]
    public void AChangingStatuslessReadingNeverSettles()
    {
        var reader = new StxEtxWeightFrameReader();

        foreach (var weight in new[] { "+  1.250 kg", "+  1.260 kg", "+  1.255 kg", "+  1.251 kg" })
            Assert.Equal(WeightStability.Unstable, Single(reader, Frame(weight, withBcc: false)).Stability);
    }

    [Fact]
    public void ASettledStatuslessReadingBecomesUnstableAgainWhenTheWeightMoves()
    {
        var reader = new StxEtxWeightFrameReader();

        for (var i = 0; i < StxEtxWeightFrameReader.SettleFrames; i++)
            reader.Consume(Frame("+  1.250 kg", withBcc: false));

        Assert.Equal(WeightStability.Unstable, Single(reader, Frame("+  2.000 kg", withBcc: false)).Stability);
    }

    [Fact]
    public void TheFormatterProducesFramesTheReaderAccepts()
    {
        foreach (var withBcc in new[] { true, false })
        {
            var reader = new StxEtxWeightFrameReader();
            var bytes = StxEtxWeightFrameReader.Format(new WeightFrame(WeightStability.Stable, WeightMode.Gross, 2.5m), withBcc: withBcc);

            Assert.Equal(2.5m, Assert.Single(reader.Consume(bytes)).Kilograms);
        }
    }

    // ---- Auto-detection -----------------------------------------------------------------------

    /// <summary>
    /// A store rarely knows which protocol its scale is set to, and the setting is often behind a
    /// service menu. The reader works it out from the stream instead.
    /// </summary>
    [Fact]
    public void TheStxProtocolIsDetectedFromTheStream()
    {
        var reader = new AutoDetectingWeightFrameReader();

        var reading = Assert.Single(reader.Consume(Frame("S+01.250kg")));

        Assert.Equal(1.250m, reading.Kilograms);
        Assert.IsType<StxEtxWeightFrameReader>(reader.Detected);
    }

    [Fact]
    public void TheLineProtocolIsDetectedFromTheStream()
    {
        var reader = new AutoDetectingWeightFrameReader();

        var reading = Assert.Single(reader.Consume(Encoding.ASCII.GetBytes("ST,GS,+  1.234kg\r\n")));

        Assert.Equal(1.234m, reading.Kilograms);
        Assert.IsType<LineWeightFrameReader>(reader.Detected);
    }

    /// <summary>
    /// Once a protocol has been seen, the others are dropped — so a noisy line cannot make the
    /// scale appear to change protocol mid-shift.
    /// </summary>
    [Fact]
    public void OnceDetectedTheProtocolDoesNotChange()
    {
        var reader = new AutoDetectingWeightFrameReader();

        reader.Consume(Frame("S+01.250kg"));
        Assert.IsType<StxEtxWeightFrameReader>(reader.Detected);

        reader.Consume(Encoding.ASCII.GetBytes("ST,GS,+  9.999kg\r\n"));
        Assert.IsType<StxEtxWeightFrameReader>(reader.Detected);
    }

    [Fact]
    public void NothingIsDetectedFromNoise()
    {
        var reader = new AutoDetectingWeightFrameReader();

        Assert.Empty(reader.Consume(Encoding.ASCII.GetBytes("!!! not a scale !!!\r\n")));
        Assert.Null(reader.Detected);
    }

    // ---- Through the service ------------------------------------------------------------------

    [Fact]
    public void TheScaleServiceReadsTheStxProtocolEndToEnd()
    {
        var port = new FakeSerialPort();
        using var scale = new SerialScaleService(port, new StxEtxWeightFrameReader());
        scale.Start();

        port.Receive(Frame("S+00.200kg"));
        Assert.True(scale.Tare());

        port.Receive(Frame("S+01.200kg"));

        Assert.Equal(1.200m, scale.Current.Gross);
        Assert.Equal(1.000m, scale.Current.Net);
        Assert.True(scale.Current.CanBeBilled);
    }

    [Fact]
    public void TheScaleServiceAutoDetectsWhenNotToldWhichProtocol()
    {
        var port = new FakeSerialPort();
        using var scale = new SerialScaleService(port);
        scale.Start();

        port.Receive(Frame("S+01.250kg"));

        Assert.Equal(1.250m, scale.Current.Gross);
        Assert.Contains("Toledo/CAS", scale.Name);
    }

    [Fact]
    public void AnUnstableStxReadingStillCannotBeBilled()
    {
        var port = new FakeSerialPort();
        using var scale = new SerialScaleService(port, new StxEtxWeightFrameReader());
        scale.Start();

        port.Receive(Frame("U+01.250kg"));

        Assert.False(scale.Current.CanBeBilled);
    }
}
