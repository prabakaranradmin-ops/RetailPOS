using System.Windows;
using System.Windows.Media;
using Pos.Core.Hardware.Printing;

namespace Pos.App.Views;

/// <summary>
/// Draws a QR code on screen: the same symbol the printer prints, module for module, black on white
/// with its quiet zone, as large as the space it is given.
/// </summary>
/// <remarks>
/// Modules are drawn to whole pixels with anti-aliasing off. A phone camera reads crisp squares; a
/// code smoothed into grey edges by scaling is one it may not.
/// </remarks>
public sealed class QrCodeView : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(QrCodeView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnTextChanged));

    /// <summary>The quiet zone the standard asks for, in modules, on every side.</summary>
    private const int QuietModules = 4;

    public QrCodeView()
    {
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        SnapsToDevicePixels = true;
    }

    /// <summary>What the code carries. Empty or too long: nothing is drawn.</summary>
    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>The code being drawn, or null.</summary>
    public QrCode? Code { get; private set; }

    private static void OnTextChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        var view = (QrCodeView)owner;

        try
        {
            view.Code = e.NewValue is string { Length: > 0 } text ? QrCode.Encode(text) : null;
        }
        catch (ArgumentException)
        {
            // Longer than a code holds. Nothing drawn beats a code that cannot be read.
            view.Code = null;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var side = Math.Min(availableSize.Width, availableSize.Height);
        return double.IsInfinity(side) ? new Size(200, 200) : new Size(side, side);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        var side = Math.Min(ActualWidth, ActualHeight);

        if (Code is not { } code || side <= 0)
            return;

        var modules = code.Size + (2 * QuietModules);

        // Whole pixels per module where the space allows, so every module is the same size.
        var unit = Math.Max(1, Math.Floor(side / modules));

        if (unit * modules > side)
            unit = side / modules;

        var drawn = unit * modules;
        var left = Math.Floor((ActualWidth - drawn) / 2);
        var top = Math.Floor((ActualHeight - drawn) / 2);

        drawing.DrawRectangle(Brushes.White, null, new Rect(left, top, drawn, drawn));

        var dark = new StreamGeometry { FillRule = FillRule.Nonzero };

        using (var figures = dark.Open())
        {
            for (var y = 0; y < code.Size; y++)
            {
                var x = 0;

                while (x < code.Size)
                {
                    if (!code[x, y])
                    {
                        x++;
                        continue;
                    }

                    // A run of dark modules along the row is one rectangle, not several touching ones.
                    var start = x;

                    while (x < code.Size && code[x, y])
                        x++;

                    var x0 = left + ((start + QuietModules) * unit);
                    var x1 = left + ((x + QuietModules) * unit);
                    var y0 = top + ((y + QuietModules) * unit);
                    var y1 = y0 + unit;

                    figures.BeginFigure(new Point(x0, y0), isFilled: true, isClosed: true);
                    figures.PolyLineTo([new Point(x1, y0), new Point(x1, y1), new Point(x0, y1)], isStroked: false, isSmoothJoin: false);
                }
            }
        }

        dark.Freeze();
        drawing.DrawGeometry(Brushes.Black, null, dark);
    }
}
