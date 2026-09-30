using System.Windows;
using System.Windows.Media;

namespace Pos.App.Charts;

/// <summary>
/// A small line under a headline figure showing the shape it came from - the takings day by day
/// under the period's total - with the latest day marked.
/// </summary>
/// <remarks>
/// No axes and no tooltip of its own: it answers "rising or falling" at a glance, and the full chart
/// further down the page answers everything else.
/// </remarks>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ColourProperty = DependencyProperty.Register(
        nameof(Colour), typeof(int), typeof(Sparkline),
        new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <summary>Which of the theme's chart colours it is drawn in.</summary>
    public int Colour
    {
        get => (int)GetValue(ColourProperty);
        set => SetValue(ColourProperty, value);
    }

    public Sparkline()
    {
        SnapsToDevicePixels = true;
        ClipToBounds = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values;

        if (values is not { Count: > 1 } || ActualWidth < 8 || ActualHeight < 6)
            return;

        var palette = ChartPalette.Current;
        var high = values.Max();
        var low = Math.Min(0, values.Min());
        var span = Math.Max(1e-9, high - low);

        const double pad = 3;
        var width = ActualWidth - (2 * pad);
        var height = ActualHeight - (2 * pad);

        var points = values
            .Select((v, i) => new Point(pad + (i * width / (values.Count - 1)), pad + height - ((v - low) / span * height)))
            .ToList();

        dc.DrawGeometry(palette.Fade(Colour, 0.28, 0.0), null, ChartGeometry.SmoothLine(points, pad + height));

        var line = new Pen(palette.SeriesBrush(Colour), 1.8) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        line.Freeze();
        dc.DrawGeometry(null, line, ChartGeometry.SmoothLine(points));

        var halo = new Pen(palette.Surface, 2);
        halo.Freeze();
        dc.DrawEllipse(palette.SeriesBrush(Colour), halo, points[^1], 3.2, 3.2);
    }
}
