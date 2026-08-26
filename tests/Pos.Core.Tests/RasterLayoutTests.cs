using Pos.Core.Hardware.Printing;
using Pos.TestSupport;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// Where drawn text lands on the paper, and what goes down the wire to put it there.
/// </summary>
/// <remarks>
/// The character path aligns columns by counting characters, which is exactly right for a
/// monospaced printer font and meaningless for Tamil drawn in a proportional face. These tests are
/// the other half: they assert positions in dots, which is the only thing that decides whether a
/// Tamil label collides with the figure beside it.
/// </remarks>
public class RasterLayoutTests
{
    private const int Dots = RasterOptions.Dots80Mm;
    private const int Chars = ReceiptBuilder.Width80Mm;

    /// <summary>Twelve dots per character across 48 characters is exactly the 576-dot head.</summary>
    private const int DotsPerChar = Dots / Chars;

    private static (ReceiptBuilder Receipt, RecordingTextRasterizer Rasterizer, RasterOptions Options) Lane(
        RasterMode mode = RasterMode.Always,
        int widthChars = Chars)
    {
        var rasterizer = new RecordingTextRasterizer();
        var dots = RasterOptions.DotsForCharacterWidth(widthChars);

        return (new ReceiptBuilder(widthChars), rasterizer, new RasterOptions(rasterizer, dots, mode));
    }

    // ---- Which lines get drawn ----------------------------------------------------------------

    [Fact]
    public void NothingIsDrawnWithoutARasteriser()
    {
        var receipt = new ReceiptBuilder(Chars);
        receipt.Text("மொத்தம்");

        // Without one the Tamil goes down the character path and comes out as '?', which is the
        // honest outcome rather than a silent nothing.
        var bytes = receipt.ToEscPos();

        Assert.Equal(-1, IndexOfRasterHeader(bytes));
        Assert.Contains("??????", System.Text.Encoding.ASCII.GetString(bytes));
    }

    /// <summary>
    /// Auto draws only what has to be drawn. English is sent as characters, which is faster,
    /// sharper and a fraction of the data; a line carrying Tamil is drawn because no thermal
    /// printer has a glyph for it.
    /// </summary>
    [Theory]
    [InlineData("TOTAL", false)]
    [InlineData("Rs. 1,184.00", false)]
    [InlineData("HSN 0713  GST 5%", false)]
    [InlineData("மொத்தம்", true)]
    [InlineData("Cash 0.00  UPI 1184.00  மொத்தம்", true)]
    [InlineData("Café Latte", true)]
    public void AutoDrawsOnlyTheLinesThePrinterHasNoGlyphsFor(string text, bool expectDrawn)
    {
        var (receipt, rasterizer, options) = Lane(RasterMode.Auto);
        receipt.Text(text);

        receipt.ToEscPos(raster: options);

        Assert.Equal(expectDrawn, rasterizer.Runs.Count > 0);
    }

    [Fact]
    public void AlwaysDrawsEveryLineIncludingPlainEnglish()
    {
        var (receipt, rasterizer, options) = Lane(RasterMode.Always);
        receipt.Text("TOTAL");
        receipt.Columns("Cash", "189.00");

        receipt.ToEscPos(raster: options);

        Assert.Equal(3, rasterizer.Runs.Count);
    }

    [Fact]
    public void NeverDrawsNothingHoweverForeignTheText()
    {
        var (receipt, rasterizer, _) = Lane();
        receipt.Text("இதுவரை பெற்ற மொத்த புள்ளிகள்");

        receipt.ToEscPos(raster: new RasterOptions(rasterizer, Dots, RasterMode.Never));

        Assert.Empty(rasterizer.Runs);
    }

    // ---- Where the text lands ------------------------------------------------------------------

    [Theory]
    [InlineData(TextAlignment.Left)]
    [InlineData(TextAlignment.Center)]
    [InlineData(TextAlignment.Right)]
    public void AFullWidthLineIsAlignedAgainstThePaperNotItsCharacterCount(TextAlignment alignment)
    {
        var (receipt, rasterizer, options) = Lane();
        receipt.Text("ரவி மளிகை", alignment);

        receipt.ToEscPos(raster: options);
        var run = rasterizer.Run("ரவி மளிகை");
        var width = rasterizer.Measure(run.Text, run.Style);

        var expected = alignment switch
        {
            TextAlignment.Right => Dots - width,
            TextAlignment.Center => (Dots - width) / 2,
            _ => 0,
        };

        Assert.Equal(expected, run.X);
    }

    /// <summary>
    /// A label on the left and a figure hard against the right margin, which is what the totals
    /// block is made of. The figure has to end flush with the paper's edge in either language.
    /// </summary>
    [Theory]
    [InlineData("Taxable value")]
    [InlineData("வரிக்குரிய தொகை")]
    public void ColumnsPutsTheLabelLeftAndTheFigureFlushRight(string label)
    {
        var (receipt, rasterizer, options) = Lane();
        receipt.Columns(label, "1,746.86");

        receipt.ToEscPos(raster: options);

        Assert.Equal(0, rasterizer.Run(label).X);
        Assert.Equal(Dots, rasterizer.Run("1,746.86").Right(rasterizer));
    }

    /// <summary>
    /// The item table. The name starts at the left margin and each figure ends against the right
    /// edge of its own column, so the columns line up down the bill whatever the names are.
    /// </summary>
    [Theory]
    [InlineData("Item", "Rate", "Qty", "Amount")]
    [InlineData("பொருளின் பெயர்", "விலை", "அளவு", "தொகை")]
    public void ARowRightAlignsEachFigureInItsOwnColumn(string name, string rate, string quantity, string amount)
    {
        var (receipt, rasterizer, options) = Lane();
        receipt.Row(name, new ColumnValue(rate, 9), new ColumnValue(quantity, 6), new ColumnValue(amount, 10));

        receipt.ToEscPos(raster: options);

        Assert.Equal(0, rasterizer.Run(name).X);

        // Description takes 48 - (10 + 7 + 11) = 20 characters; each figure ends at the right edge
        // of its column, one character of gutter after the previous one.
        Assert.Equal((20 + 1 + 9) * DotsPerChar, rasterizer.Run(rate).Right(rasterizer));
        Assert.Equal((20 + 1 + 9 + 1 + 6) * DotsPerChar, rasterizer.Run(quantity).Right(rasterizer));
        Assert.Equal(Chars * DotsPerChar, rasterizer.Run(amount).Right(rasterizer));
    }

    [Fact]
    public void CellsPlacesEachCellAtItsOwnOffset()
    {
        var (receipt, rasterizer, options) = Lane();

        receipt.Cells(
            new ReceiptCell("பில் நம்பர்", 12),
            new ReceiptCell("RM/26-27/11358", 16),
            new ReceiptCell("தேதி", 9),
            new ReceiptCell("21-08-2026", 11, TextAlignment.Right));

        receipt.ToEscPos(raster: options);

        Assert.Equal(0, rasterizer.Run("பில் நம்பர்").X);
        Assert.Equal(12 * DotsPerChar, rasterizer.Run("RM/26-27/11358").X);
        Assert.Equal(28 * DotsPerChar, rasterizer.Run("தேதி").X);
        Assert.Equal(48 * DotsPerChar, rasterizer.Run("21-08-2026").Right(rasterizer));
    }

    /// <summary>
    /// The failure this whole layer exists to prevent: two runs on one line touching. A Tamil label
    /// is measured in dots and an ASCII figure in characters, and nothing in the character padding
    /// knows about the first.
    /// </summary>
    [Fact]
    public void NoTwoRunsOnALineOverlap()
    {
        var (receipt, rasterizer, options) = Lane();

        receipt.Cells(
            new ReceiptCell("Cash", 7),
            new ReceiptCell("600.00", 9, TextAlignment.Right),
            new ReceiptCell(string.Empty, 2),
            new ReceiptCell("UPI", 7),
            new ReceiptCell("0.00", 9, TextAlignment.Right),
            new ReceiptCell("மொத்தம்", 14, TextAlignment.Right));

        receipt.ToEscPos(raster: options);

        var ordered = rasterizer.Runs.OrderBy(r => r.X).ToList();

        for (var i = 1; i < ordered.Count; i++)
            Assert.True(ordered[i].X >= ordered[i - 1].Right(rasterizer), $"'{ordered[i - 1].Text}' runs into '{ordered[i].Text}'.");
    }

    [Fact]
    public void AnOverlongRunStartsAtItsColumnRatherThanBeingPushedOffThePaper()
    {
        var (receipt, rasterizer, options) = Lane();

        // Forty characters asked to fit in a ten-character cell.
        receipt.Cells(new ReceiptCell(new string('X', 40), 10, TextAlignment.Right));

        receipt.ToEscPos(raster: options);

        Assert.Equal(0, rasterizer.Runs[0].X);
    }

    // ---- Scale ---------------------------------------------------------------------------------

    /// <summary>
    /// A doubled heading is laid out against the whole paper, not against the half-width line the
    /// character path wraps it to — otherwise a centred shop name prints hard against the left.
    /// </summary>
    [Fact]
    public void ADoubledHeadingIsCentredOnTheWholePaper()
    {
        var (receipt, rasterizer, options) = Lane();
        receipt.Text("ரவி மளிகை", TextAlignment.Center, bold: true, widthMultiplier: 2, heightMultiplier: 2);

        receipt.ToEscPos(raster: options);
        var run = rasterizer.Run("ரவி மளிகை");

        Assert.Equal(new RasterTextStyle(true, 2, 2), run.Style);
        Assert.Equal((Dots - rasterizer.Measure(run.Text, run.Style)) / 2, run.X);
    }

    [Fact]
    public void ATallerLineGetsATallerStrip()
    {
        var (receipt, rasterizer, options) = Lane();
        receipt.Text("ரவி மளிகை", heightMultiplier: 2);

        var bytes = receipt.ToEscPos(raster: options);

        // yL yH in the raster header carry the band height.
        var header = IndexOfRasterHeader(bytes);
        Assert.True(header >= 0);
        Assert.Equal(RecordingTextRasterizer.BaseLineHeight * 2, bytes[header + 6] | (bytes[header + 7] << 8));
    }

    // ---- Narrow paper --------------------------------------------------------------------------

    [Fact]
    public void FiftyEightMillimetrePaperLaysOutAgainstItsOwnDotWidth()
    {
        var (receipt, rasterizer, options) = Lane(widthChars: ReceiptBuilder.Width58Mm);
        receipt.Columns("மொத்தம்", "1,184.00");

        receipt.ToEscPos(raster: options);

        Assert.Equal(RasterOptions.Dots58Mm, options.PaperWidthDots);
        Assert.Equal(RasterOptions.Dots58Mm, rasterizer.Run("1,184.00").Right(rasterizer));
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private static byte[] RasterHeader() => [EscPos.Gs, (byte)'v', (byte)'0', 0];

    private static int IndexOfRasterHeader(byte[] bytes)
    {
        var header = RasterHeader();

        for (var i = 0; i + header.Length < bytes.Length; i++)
        {
            if (bytes[i] == header[0] && bytes[i + 1] == header[1] && bytes[i + 2] == header[2] && bytes[i + 3] == header[3])
                return i;
        }

        return -1;
    }
}
