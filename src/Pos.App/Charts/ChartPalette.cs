using System.Windows;
using System.Windows.Media;

namespace Pos.App.Charts;

/// <summary>
/// The colours a chart draws with, read from the theme so a chart and the card around it can never
/// disagree about what "accent" means.
/// </summary>
/// <remarks>
/// Every brush has a fallback, so a chart drawn where the theme is not loaded - a unit test, or a
/// picture saved from a window being torn down - still draws in the right colours rather than
/// throwing or coming out black.
/// </remarks>
public sealed class ChartPalette
{
    private static readonly Color[] SeriesFallback =
    [
        Color.FromRgb(0x38, 0xBD, 0xF8), // sky - the accent
        Color.FromRgb(0x10, 0xB9, 0x81), // emerald - money taken
        Color.FromRgb(0xF5, 0xB5, 0x44), // amber - an average, or money off
        Color.FromRgb(0xA7, 0x8B, 0xFA), // violet - a count beside the money
        Color.FromRgb(0xF4, 0x72, 0xB6), // rose
        Color.FromRgb(0x2D, 0xD4, 0xBF), // teal
        Color.FromRgb(0x94, 0xA3, 0xB8), // slate - everything else
    ];

    private readonly Color[] _series;

    private ChartPalette()
    {
        _series = new Color[SeriesFallback.Length];

        for (var i = 0; i < _series.Length; i++)
            _series[i] = Read($"ChartColour{i}", SeriesFallback[i]);

        Ink = Freeze(Read("Ink", Color.FromRgb(0xF8, 0xFA, 0xFC)));
        Muted = Freeze(Read("InkMuted", Color.FromRgb(0x94, 0xA3, 0xB8)));
        Dim = Freeze(Read("InkDim", Color.FromRgb(0x82, 0x92, 0xA8)));
        Grid = Freeze(Read("ChartGrid", Color.FromRgb(0x1C, 0x25, 0x33)));
        AxisLine = Freeze(Read("ChartAxisLine", Color.FromRgb(0x33, 0x3F, 0x52)));
        Surface = Freeze(Read("SurfaceRaised", Color.FromRgb(0x11, 0x17, 0x22)));
        Page = Freeze(Read("Surface", Color.FromRgb(0x0A, 0x0D, 0x13)));
        Tooltip = Freeze(Read("ChartTooltip", Color.FromRgb(0x0C, 0x12, 0x1C)));
        TooltipEdge = Freeze(Read("ChartTooltipEdge", Color.FromRgb(0x2E, 0x3B, 0x4F)));
        Accent = Freeze(Read("Accent", SeriesFallback[0]));

        HeatStops =
        [
            Read("ChartHeat0", Color.FromRgb(0x15, 0x1E, 0x2C)),
            Read("ChartHeat1", Color.FromRgb(0x0E, 0x3B, 0x57)),
            Read("ChartHeat2", Color.FromRgb(0x17, 0x7F, 0xB5)),
            Read("ChartHeat3", Color.FromRgb(0x7D, 0xD3, 0xFC)),
        ];
    }

    /// <summary>The palette as the theme stands now.</summary>
    public static ChartPalette Current => new();

    public SolidColorBrush Ink { get; }
    public SolidColorBrush Muted { get; }
    public SolidColorBrush Dim { get; }
    public SolidColorBrush Grid { get; }
    public SolidColorBrush AxisLine { get; }
    public SolidColorBrush Surface { get; }
    public SolidColorBrush Page { get; }
    public SolidColorBrush Tooltip { get; }
    public SolidColorBrush TooltipEdge { get; }
    public SolidColorBrush Accent { get; }

    /// <summary>The heatmap's scale, from an empty square to the busiest one.</summary>
    public Color[] HeatStops { get; }

    public int SeriesCount => _series.Length;

    /// <summary>A series colour. Past the end of the palette it wraps rather than failing.</summary>
    public Color Series(int index) => _series[((index % _series.Length) + _series.Length) % _series.Length];

    public SolidColorBrush SeriesBrush(int index, double opacity = 1) =>
        Freeze(WithAlpha(Series(index), opacity));

    /// <summary>Top to bottom: full colour fading to a trace of it, for bars and areas.</summary>
    public LinearGradientBrush Fade(int index, double top, double bottom)
    {
        var colour = Series(index);
        var brush = new LinearGradientBrush(WithAlpha(colour, top), WithAlpha(colour, bottom), 90);
        brush.Freeze();
        return brush;
    }

    /// <summary>A heatmap square's colour, <paramref name="share"/> being 0 for empty and 1 for the busiest.</summary>
    public Color Heat(double share)
    {
        if (share <= 0)
            return HeatStops[0];

        share = Math.Min(1, share);

        // Three runs between the four stops. The first starts a little way in, so the quietest hour
        // that sold anything is still told apart from one that sold nothing.
        var position = 0.12 + (share * 0.88);
        var segment = position < 0.5 ? 0 : position < 0.8 ? 1 : 2;
        var (from, to) = segment switch { 0 => (0.0, 0.5), 1 => (0.5, 0.8), _ => (0.8, 1.0) };

        return Lerp(HeatStops[segment], HeatStops[segment + 1], (position - from) / (to - from));
    }

    public static Color WithAlpha(Color colour, double opacity) =>
        Color.FromArgb((byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255), colour.R, colour.G, colour.B);

    public static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (byte)Math.Round(a.A + ((b.A - a.A) * t)),
            (byte)Math.Round(a.R + ((b.R - a.R) * t)),
            (byte)Math.Round(a.G + ((b.G - a.G) * t)),
            (byte)Math.Round(a.B + ((b.B - a.B) * t)));
    }

    /// <summary>Relative luminance, to choose dark or light text on a coloured square.</summary>
    public static double Luminance(Color colour)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(colour.R)) + (0.7152 * Channel(colour.G)) + (0.0722 * Channel(colour.B));
    }

    private static SolidColorBrush Freeze(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }

    private static Color Read(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) switch
        {
            SolidColorBrush brush => brush.Color,
            Color colour => colour,
            _ => fallback,
        };
}
