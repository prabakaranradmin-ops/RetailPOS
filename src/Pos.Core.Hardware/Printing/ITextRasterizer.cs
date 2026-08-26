namespace Pos.Core.Hardware.Printing;

/// <summary>How a receipt line is put on paper.</summary>
public enum RasterMode
{
    /// <summary>
    /// Never rasterise. Everything goes as characters through the printer's own fonts, and anything
    /// outside its code page is reduced to ASCII by <see cref="EscPos.Transliterate"/>.
    /// </summary>
    Never = 0,

    /// <summary>
    /// Rasterise only the lines that need it — the ones carrying characters no thermal printer has
    /// a glyph for. English lines still print as characters, which is faster, sharper, and uses a
    /// fraction of the data. This is how a bilingual grocery bill is normally produced, and it is
    /// why the Tamil on one looks like a different typeface from the English.
    /// </summary>
    Auto = 1,

    /// <summary>
    /// Rasterise every line, so the whole receipt is in one typeface. Costs roughly 1.7KB per line
    /// on 80mm paper instead of a few dozen bytes.
    /// </summary>
    Always = 2,
}

/// <summary>Weight and scale a run of text is drawn at.</summary>
public readonly record struct RasterTextStyle(bool Bold = false, int WidthMultiplier = 1, int HeightMultiplier = 1);

/// <summary>
/// Draws text into a <see cref="MonochromeBitmap"/>.
/// </summary>
/// <remarks>
/// An interface because rendering a glyph needs a font engine, and a font engine is a platform
/// dependency the layout has no business carrying. The layout asks where text would land and where
/// to put it; a platform implementation answers. Tests substitute a rasteriser with known metrics
/// and assert the layout exactly, which is a far sharper check than comparing pixels.
/// </remarks>
public interface ITextRasterizer
{
    /// <summary>Height in dots of one line drawn at this style, including its leading.</summary>
    int LineHeight(RasterTextStyle style);

    /// <summary>Width in dots this text would occupy if drawn at this style.</summary>
    int Measure(string text, RasterTextStyle style);

    /// <summary>
    /// Draws <paramref name="text"/> with its left edge at <paramref name="x"/> and the top of its
    /// line box at <paramref name="y"/>. Anything falling outside the target is clipped.
    /// </summary>
    void Draw(MonochromeBitmap target, string text, int x, int y, RasterTextStyle style);
}

/// <summary>What the raster path needs to know: who draws, how wide the paper is, and when to use it.</summary>
public sealed record RasterOptions(ITextRasterizer Rasterizer, int PaperWidthDots, RasterMode Mode = RasterMode.Auto)
{
    /// <summary>Print head width in dots on 80mm paper, at the usual 203dpi.</summary>
    public const int Dots80Mm = 576;

    /// <summary>Print head width in dots on 58mm paper, at the usual 203dpi.</summary>
    public const int Dots58Mm = 384;

    /// <summary>
    /// The dot width that goes with a character width, for a printer whose settings give only one
    /// of the two. Both standard widths work out at 12 dots per character.
    /// </summary>
    public static int DotsForCharacterWidth(int paperWidthChars) => paperWidthChars * 12;

    /// <summary>True when a line containing this text has to be drawn rather than typed.</summary>
    public static bool NeedsRaster(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        foreach (var c in text)
        {
            // Anything outside printable ASCII has no dependable glyph in a printer's code pages.
            // Tab and newline never reach here; the layout has already resolved them.
            if (c < 0x20 || c > 0x7E)
                return true;
        }

        return false;
    }
}
