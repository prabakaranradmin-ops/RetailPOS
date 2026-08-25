using System.Text;
using Pos.Core.Hardware.Printing;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// The ESC/POS command bytes, asserted literally. These are the sequences a printer will actually
/// receive, and a printer cannot tell you that a byte was wrong — it just does the wrong thing, or
/// nothing.
/// </summary>
public class EscPosTests
{
    [Fact]
    public void InitialiseIsEscAt()
    {
        Assert.Equal(new byte[] { 0x1B, 0x40 }, EscPos.Initialize());
    }

    [Theory]
    [InlineData(TextAlignment.Left, 0)]
    [InlineData(TextAlignment.Center, 1)]
    [InlineData(TextAlignment.Right, 2)]
    public void AlignIsEscLowerAWithTheMode(TextAlignment alignment, byte expected)
    {
        Assert.Equal(new byte[] { 0x1B, 0x61, expected }, EscPos.Align(alignment));
    }

    [Fact]
    public void BoldIsEscUpperEWithAFlag()
    {
        Assert.Equal(new byte[] { 0x1B, 0x45, 1 }, EscPos.Bold(true));
        Assert.Equal(new byte[] { 0x1B, 0x45, 0 }, EscPos.Bold(false));
    }

    [Theory]
    [InlineData(0, 0x00)]
    [InlineData(1, 0x01)]
    [InlineData(2, 0x02)]
    public void UnderlineIsEscHyphenWithTheDotCount(int dots, byte expected)
    {
        Assert.Equal(new byte[] { 0x1B, 0x2D, expected }, EscPos.Underline(dots));
    }

    /// <summary>
    /// GS ! packs width into the high nibble and height into the low one, each as multiplier minus
    /// one. Getting the nibbles the wrong way round gives text that is tall instead of wide, which
    /// is exactly the kind of bug a byte-level test catches and a visual check does not.
    /// </summary>
    [Theory]
    [InlineData(1, 1, 0x00)]
    [InlineData(2, 1, 0x10)]
    [InlineData(1, 2, 0x01)]
    [InlineData(2, 2, 0x11)]
    [InlineData(8, 8, 0x77)]
    [InlineData(3, 5, 0x24)]
    public void TextSizePacksWidthHighAndHeightLow(int width, int height, byte expected)
    {
        Assert.Equal(new byte[] { 0x1D, 0x21, expected }, EscPos.TextSize(width, height));
    }

    [Fact]
    public void NormalTextSizeIsAPlainOneByOne()
    {
        Assert.Equal(new byte[] { 0x1D, 0x21, 0x00 }, EscPos.NormalTextSize());
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(9, 1)]
    [InlineData(1, 9)]
    public void AnOutOfRangeTextSizeIsRejected(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EscPos.TextSize(width, height));
    }

    [Fact]
    public void FeedIsEscLowerDWithTheLineCount()
    {
        Assert.Equal(new byte[] { 0x1B, 0x64, 3 }, EscPos.Feed(3));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    public void AnOutOfRangeFeedIsRejected(int lines)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EscPos.Feed(lines));
    }

    /// <summary>
    /// The blade sits above the print head, so a cut with no feed first slices through the last
    /// few printed lines. The feed is part of the command for that reason.
    /// </summary>
    [Fact]
    public void CutFeedsBeforeItCuts()
    {
        Assert.Equal(new byte[] { 0x1B, 0x64, 4, 0x1D, 0x56, 1 }, EscPos.Cut());
        Assert.Equal(new byte[] { 0x1B, 0x64, 4, 0x1D, 0x56, 0 }, EscPos.Cut(CutMode.Full));
        Assert.Equal(new byte[] { 0x1D, 0x56, 1 }, EscPos.Cut(CutMode.Partial, feedBeforeCut: 0));
    }

    // ---- Drawer pulse -------------------------------------------------------------------------

    /// <summary>
    /// ESC p sends its timings in units of 2ms, so 60ms on the wire is a 30. Sending the
    /// milliseconds through unconverted would ask the solenoid to hold for a quarter of a second.
    /// </summary>
    [Fact]
    public void TheDrawerPulseIsSentInTwoMillisecondUnits()
    {
        Assert.Equal(new byte[] { 0x1B, 0x70, 0, 30, 60 }, EscPos.KickDrawer());
        Assert.Equal(new byte[] { 0x1B, 0x70, 0, 50, 100 }, EscPos.KickDrawer(onMilliseconds: 100, offMilliseconds: 200));
    }

    [Fact]
    public void TheSecondDrawerSitsOnPinFive()
    {
        Assert.Equal(new byte[] { 0x1B, 0x70, 1, 30, 60 }, EscPos.KickDrawer(pin: 1));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(-1)]
    public void AnUnknownDrawerPinIsRejected(int pin)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EscPos.KickDrawer(pin));
    }

    /// <summary>
    /// A pulse longer than the field can carry would wrap around to a short one, which is worse
    /// than refusing it: the drawer would silently stop opening.
    /// </summary>
    [Theory]
    [InlineData(511)]
    [InlineData(1_000)]
    [InlineData(-1)]
    public void AnOutOfRangePulseIsRejected(int milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EscPos.KickDrawer(onMilliseconds: milliseconds));
        Assert.Throws<ArgumentOutOfRangeException>(() => EscPos.KickDrawer(offMilliseconds: milliseconds));
    }

    // ---- Text ---------------------------------------------------------------------------------

    [Fact]
    public void TextEncodesToSingleBytes()
    {
        Assert.Equal("Toor Dal"u8.ToArray(), EscPos.Text("Toor Dal"));
    }

    [Fact]
    public void LineAppendsALineFeed()
    {
        Assert.Equal(new byte[] { (byte)'A', (byte)'B', 0x0A }, EscPos.Line("AB"));
    }

    [Fact]
    public void EmptyTextProducesNoBytes()
    {
        Assert.Empty(EscPos.Text(null));
        Assert.Empty(EscPos.Text(""));
    }

    /// <summary>
    /// A thermal printer has no font for Indic scripts. Substituting a visible marker is the
    /// honest outcome — the receipt shows something is missing rather than the printer emitting
    /// whatever bytes fall out of an unmappable character.
    /// </summary>
    [Fact]
    public void CharactersTheCodePageCannotHoldBecomeAMarker()
    {
        var bytes = EscPos.Text("தேயிலை");

        Assert.All(bytes, b => Assert.Equal((byte)'?', b));
    }

    [Fact]
    public void EncodingUnmappableCharactersNeverThrows()
    {
        var exception = Record.Exception(() => EscPos.Text("₹100 · 日本語 · emoji 🎉"));

        Assert.Null(exception);
    }

    [Fact]
    public void AnAlternativeEncodingCanBeSupplied()
    {
        Assert.Equal(new byte[] { 0x41 }, EscPos.Text("A", Encoding.ASCII));
    }

    [Fact]
    public void SelectCodePageIsEscLowerT()
    {
        Assert.Equal(new byte[] { 0x1B, 0x74, 16 }, EscPos.SelectCodePage(16));
    }
}
