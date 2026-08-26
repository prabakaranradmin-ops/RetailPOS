using Pos.Core.Hardware.Printing;

namespace Pos.TestSupport;

/// <summary>One call to <see cref="RecordingTextRasterizer.Draw"/>.</summary>
/// <param name="Text">What was drawn.</param>
/// <param name="X">Left edge, in printer dots.</param>
/// <param name="Y">Top of the line box, in printer dots.</param>
/// <param name="Style">Weight and scale it was drawn at.</param>
public readonly record struct DrawnRun(string Text, int X, int Y, RasterTextStyle Style)
{
    public int Right(RecordingTextRasterizer rasterizer) => X + rasterizer.Measure(Text, Style);
}

/// <summary>
/// A text rasteriser with arithmetic instead of a font: every character is the same width, and it
/// writes down where it was asked to draw rather than drawing anything.
/// </summary>
/// <remarks>
/// Layout is the part of drawn text worth asserting — whether a Tamil label starts at the left of
/// its column, whether a right-aligned figure ends flush against the margin, whether two cells
/// collide. None of that needs real glyphs, and with real glyphs none of it could be asserted
/// exactly: the answers would depend on which fonts the machine running the tests happens to have.
/// Real shaping is checked separately, against a real font, by looking at the pixels.
/// </remarks>
public sealed class RecordingTextRasterizer : ITextRasterizer
{
    private readonly List<DrawnRun> _runs = [];

    /// <summary>Dots per character at single scale. Twelve, matching a printer's own font.</summary>
    public const int CharacterWidth = 12;

    /// <summary>Dots per line at single scale.</summary>
    public const int BaseLineHeight = 24;

    public IReadOnlyList<DrawnRun> Runs => _runs;

    public void Clear() => _runs.Clear();

    public int LineHeight(RasterTextStyle style) => BaseLineHeight * Math.Max(1, style.HeightMultiplier);

    public int Measure(string text, RasterTextStyle style) =>
        string.IsNullOrEmpty(text) ? 0 : text.Length * CharacterWidth * Math.Max(1, style.WidthMultiplier);

    public void Draw(MonochromeBitmap target, string text, int x, int y, RasterTextStyle style)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (string.IsNullOrEmpty(text))
            return;

        _runs.Add(new DrawnRun(text, x, y, style));

        // Enough ink to prove the strip was reached and to show where: one dot per character,
        // clipped by the bitmap exactly as a real glyph would be.
        for (var i = 0; i < text.Length; i++)
            target[x + (i * CharacterWidth * Math.Max(1, style.WidthMultiplier)), y] = true;
    }

    /// <summary>The single run whose text matches, or a clear failure naming what was drawn.</summary>
    public DrawnRun Run(string text)
    {
        var matches = _runs.Where(r => r.Text == text).ToList();

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"'{text}' was never drawn. Drawn: {string.Join(" | ", _runs.Select(r => r.Text))}"),
            _ => throw new InvalidOperationException($"'{text}' was drawn {matches.Count} times."),
        };
    }
}
