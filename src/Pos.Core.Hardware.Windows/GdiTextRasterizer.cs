using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.Versioning;
using Pos.Core.Hardware.Printing;

namespace Pos.Core.Hardware.Windows;

/// <summary>
/// Draws receipt text with the operating system's font engine, so a script the printer has no glyph
/// for still reaches paper.
/// </summary>
/// <remarks>
/// <para>
/// Tamil is not a font substitution problem. A syllable is assembled from several code points and
/// reordered — the vowel sign in <c>கெ</c> is stored after the consonant and drawn before it — so
/// nothing that maps bytes to glyphs one at a time can render it, which is every thermal printer
/// ever made. Handing the shaping to the OS and sending the result as dots sidesteps the printer's
/// fonts and code pages completely.
/// </para>
/// <para>
/// The dot grid is the printer's, not the screen's: sizes here are in printer dots at 203dpi, where
/// the built-in font is 12 wide by 24 tall. Text is rendered without anti-aliasing because the
/// output has exactly two colours and a grey edge pixel would either vanish or become a full black
/// dot, neither of which is what the shape wanted.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class GdiTextRasterizer : ITextRasterizer, IDisposable
{
    /// <summary>
    /// Families tried in order. Nirmala UI carries Tamil and Latin in one face and has shipped with
    /// Windows since 8, so a bilingual receipt comes out in one typeface rather than two.
    /// </summary>
    public static readonly string[] DefaultFontPreference =
    [
        "Nirmala UI",
        "Latha",
        "Arial Unicode MS",
        "Segoe UI",
    ];

    /// <summary>
    /// Em size in dots for ordinary text. Chosen so a line box lands near the 24 dots of the
    /// printer's own font, which keeps a drawn line the same height as a typed one and stops a
    /// bilingual receipt from looking like two receipts.
    /// </summary>
    public const float DefaultEmSizeDots = 19f;

    private readonly Dictionary<(int Size, bool Bold), Font> _fonts = [];
    private readonly Bitmap _measuringSurface;
    private readonly Graphics _measuring;
    private readonly float _baseEmSize;
    private bool _disposed;

    public GdiTextRasterizer(string? fontFamily = null, float baseEmSizeDots = DefaultEmSizeDots)
    {
        if (baseEmSizeDots is <= 0 or > 200)
            throw new ArgumentOutOfRangeException(nameof(baseEmSizeDots), baseEmSizeDots, "A receipt font is between 1 and 200 dots.");

        _baseEmSize = baseEmSizeDots;
        FontFamily = ResolveFamily(fontFamily);

        _measuringSurface = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
        _measuring = Graphics.FromImage(_measuringSurface);
        _measuring.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
    }

    /// <summary>The family actually in use, which may not be the one asked for.</summary>
    public string FontFamily { get; }

    /// <summary>
    /// Picks the first installed family from the preference list, so a lane whose Windows build
    /// lacks the ideal font degrades to the next one instead of failing to print.
    /// </summary>
    private static string ResolveFamily(string? requested)
    {
        using var installed = new InstalledFontCollection();
        var available = installed.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var preference = string.IsNullOrWhiteSpace(requested)
            ? DefaultFontPreference
            : [requested, .. DefaultFontPreference];

        foreach (var candidate in preference)
        {
            if (available.Contains(candidate))
                return candidate;
        }

        return System.Drawing.FontFamily.GenericSansSerif.Name;
    }

    /// <summary>
    /// ESC/POS scales width and height independently. A font can only be scaled in both at once, so
    /// height comes from the em size and width from a transform on top of it.
    /// </summary>
    private (float EmSize, float HorizontalScale) Scaling(RasterTextStyle style)
    {
        var height = Math.Max(1, style.HeightMultiplier);
        var width = Math.Max(1, style.WidthMultiplier);

        return (_baseEmSize * height, (float)width / height);
    }

    private Font FontFor(RasterTextStyle style)
    {
        var (emSize, _) = Scaling(style);
        var key = ((int)Math.Round(emSize * 4), style.Bold);

        if (!_fonts.TryGetValue(key, out var font))
        {
            font = new Font(FontFamily, emSize, style.Bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
            _fonts[key] = font;
        }

        return font;
    }

    public int LineHeight(RasterTextStyle style)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return (int)Math.Ceiling(FontFor(style).GetHeight(_measuring));
    }

    public int Measure(string text, RasterTextStyle style)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrEmpty(text))
            return 0;

        var (_, horizontalScale) = Scaling(style);
        var width = _measuring.MeasureString(text, FontFor(style), int.MaxValue, StringFormat.GenericTypographic).Width;

        return (int)Math.Ceiling(width * horizontalScale);
    }

    public void Draw(MonochromeBitmap target, string text, int x, int y, RasterTextStyle style)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(target);

        if (string.IsNullOrEmpty(text))
            return;

        var height = LineHeight(style);
        var width = Measure(text, style);

        if (width <= 0 || height <= 0)
            return;

        var (_, horizontalScale) = Scaling(style);

        using var surface = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(surface))
        {
            graphics.Clear(Color.White);
            graphics.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
            graphics.SmoothingMode = SmoothingMode.None;
            graphics.PixelOffsetMode = PixelOffsetMode.None;

            if (Math.Abs(horizontalScale - 1f) > 0.001f)
                graphics.ScaleTransform(horizontalScale, 1f);

            graphics.DrawString(text, FontFor(style), Brushes.Black, 0f, 0f, StringFormat.GenericTypographic);
        }

        Threshold(surface, target, x, y);
    }

    /// <summary>
    /// Copies the drawn glyphs onto the strip as ink. Any pixel darker than mid-grey becomes a dot;
    /// the rest is left alone rather than cleared, so two runs can share a line without the second
    /// one erasing the first.
    /// </summary>
    private static void Threshold(Bitmap source, MonochromeBitmap target, int offsetX, int offsetY)
    {
        var rectangle = new Rectangle(0, 0, source.Width, source.Height);
        var data = source.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        try
        {
            unsafe
            {
                var scan0 = (byte*)data.Scan0;

                for (var row = 0; row < data.Height; row++)
                {
                    var destinationY = offsetY + row;

                    if (destinationY < 0 || destinationY >= target.Height)
                        continue;

                    var line = scan0 + ((long)row * data.Stride);

                    for (var column = 0; column < data.Width; column++)
                    {
                        var destinationX = offsetX + column;

                        if (destinationX < 0 || destinationX >= target.Width)
                            continue;

                        var pixel = line + (column * 4);

                        // Rec. 601 luma, near enough for text that is drawn in pure black on white.
                        var luma = ((pixel[2] * 299) + (pixel[1] * 587) + (pixel[0] * 114)) / 1000;

                        if (luma < 128)
                            target[destinationX, destinationY] = true;
                    }
                }
            }
        }
        finally
        {
            source.UnlockBits(data);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        foreach (var font in _fonts.Values)
            font.Dispose();

        _fonts.Clear();
        _measuring.Dispose();
        _measuringSurface.Dispose();
    }
}
