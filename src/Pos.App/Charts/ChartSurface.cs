using System.Data;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Pos.App.Charts;

/// <summary>One line of a tooltip: a colour key, what it is, and its figure.</summary>
/// <param name="Swatch">The series colour, or null for a line with no key.</param>
/// <param name="Line">Keyed with a short line rather than a square: the series is drawn as a line.</param>
public readonly record struct TooltipRow(Color? Swatch, string Label, string Value, bool Line = false, bool Dashed = false);

/// <summary>
/// What every chart on the owner's screen has in common: it draws itself, answers the mouse and the
/// keyboard alike, says what it shows to a screen reader, and can be saved as a picture or read as
/// a table.
/// </summary>
/// <remarks>
/// <para>
/// Drawn by hand, like the charts on the saved web page, and for the same reason: a charting library
/// is a download, a licence and a dependency that has to keep working offline on a machine that has
/// never seen the internet. What a shop needs from a chart is a short list, and every item on it is
/// here.
/// </para>
/// <para>
/// Hovering is not the only way in. Each chart takes the keyboard focus like any other control, and
/// the arrow keys walk its figures with the same tooltip the mouse gets, so an owner who never
/// touches the mouse - the till is built for them - reads the same numbers.
/// </para>
/// </remarks>
public abstract class ChartSurface : FrameworkElement
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(ChartSurface), new FrameworkPropertyMetadata(string.Empty));

    /// <summary>
    /// Turns the entrance animation off everywhere. Tests lay charts out and photograph them in one
    /// step, and a bar still growing would be measured half-drawn.
    /// </summary>
    public static bool AnimationsEnabled { get; set; } = true;

    private const double AnimationMs = 560;

    private DateTime _animationStarted;
    private bool _animating;
    private ChartAutomationPeer? _peer;
    private string _activeDescription = string.Empty;

    static ChartSurface()
    {
        FocusableProperty.OverrideMetadata(typeof(ChartSurface), new FrameworkPropertyMetadata(true));

        // The chart draws its own focus ring round the plot, in the accent colour. The system's dotted
        // rectangle is black and all but invisible on these cards.
        FocusVisualStyleProperty.OverrideMetadata(typeof(ChartSurface), new FrameworkPropertyMetadata(null));
        ClipToBoundsProperty.OverrideMetadata(typeof(ChartSurface), new FrameworkPropertyMetadata(true));
    }

    protected ChartSurface()
    {
        SnapsToDevicePixels = true;
        Unloaded += (_, _) => StopAnimation();
    }

    /// <summary>What the chart shows, for its card, its saved picture and a screen reader.</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>How far the entrance has got, 0 to 1, eased.</summary>
    protected double Progress { get; private set; } = 1;

    /// <summary>The figure the focus or the mouse is on, said in words.</summary>
    public string ActiveDescription
    {
        get => _activeDescription;
        protected set
        {
            if (_activeDescription == value)
                return;

            var before = _activeDescription;
            _activeDescription = value;

            _peer?.RaisePropertyChangedEvent(AutomationElementIdentifiers.HelpTextProperty, before, value);
        }
    }

    /// <summary>The whole chart in a sentence: what it covers and where it peaks.</summary>
    public abstract string Summary { get; }

    /// <summary>The figures behind the chart, one row per bar, slice or square.</summary>
    public abstract DataTable ToTable();

    /// <summary>Raised when the chart is zoomed in or back out, so its card can offer a way back.</summary>
    public event EventHandler? ZoomChanged;

    /// <summary>Raised when the chart is given new figures, so a table showing the old ones can follow.</summary>
    public event EventHandler? FiguresChanged;

    protected void OnFiguresChanged() => FiguresChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>True while only part of the figures is on show.</summary>
    public virtual bool IsZoomed => false;

    /// <summary>Shows every figure again.</summary>
    public virtual void ResetZoom()
    {
    }

    protected void OnZoomChanged() => ZoomChanged?.Invoke(this, EventArgs.Empty);

    // ---- Drawing -----------------------------------------------------------------------------------

    protected sealed override void OnRender(DrawingContext dc)
    {
        var size = new Size(ActualWidth, ActualHeight);

        if (size.Width < 8 || size.Height < 8)
            return;

        // Transparent, not nothing: without a fill the empty parts of the chart would not take the
        // mouse, and hovering between two bars would lose the tooltip.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(size));

        var palette = ChartPalette.Current;
        RenderChart(dc, size, palette);

        if (IsKeyboardFocused)
        {
            var ring = new Pen(palette.Accent, 2);
            ring.Freeze();
            dc.DrawRoundedRectangle(null, ring, new Rect(1, 1, size.Width - 2, size.Height - 2), 7, 7);
        }
    }

    protected abstract void RenderChart(DrawingContext dc, Size size, ChartPalette palette);

    /// <summary>Starts the entrance again. Called whenever the figures change.</summary>
    protected void Replay()
    {
        if (!AnimationsEnabled || !SystemParameters.ClientAreaAnimation)
        {
            Progress = 1;
            InvalidateVisual();
            return;
        }

        _animationStarted = DateTime.UtcNow;
        Progress = 0;

        if (!_animating)
        {
            CompositionTarget.Rendering += OnFrame;
            _animating = true;
        }

        InvalidateVisual();
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var elapsed = (DateTime.UtcNow - _animationStarted).TotalMilliseconds / AnimationMs;

        if (elapsed >= 1 || !IsVisible)
        {
            Progress = 1;
            StopAnimation();
        }
        else
        {
            // Ease out: quick to start, settling gently, so the figures are readable almost at once.
            Progress = 1 - Math.Pow(1 - elapsed, 3);
        }

        InvalidateVisual();
    }

    private void StopAnimation()
    {
        if (!_animating)
            return;

        CompositionTarget.Rendering -= OnFrame;
        _animating = false;
        Progress = 1;
    }

    // ---- Shared drawing helpers ----------------------------------------------------------------------

    private static readonly FontFamily UiFont = new("Segoe UI");
    private static readonly FontFamily FigureFont = new("Cascadia Mono, Consolas, Courier New");

    protected FormattedText Text(string text, double size, Brush brush, bool bold = false, bool figures = false) =>
        new(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(figures ? FigureFont : UiFont, FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    /// <summary>A crisp one-pixel line, placed on the pixel rather than across two.</summary>
    protected static void HairLine(DrawingContext dc, Pen pen, Point from, Point to)
    {
        var guidelines = new GuidelineSet();
        guidelines.GuidelinesX.Add(from.X + 0.5);
        guidelines.GuidelinesX.Add(to.X + 0.5);
        guidelines.GuidelinesY.Add(from.Y + 0.5);
        guidelines.GuidelinesY.Add(to.Y + 0.5);

        dc.PushGuidelineSet(guidelines);
        dc.DrawLine(pen, from, to);
        dc.Pop();
    }

    protected static Pen MakePen(Brush brush, double thickness, bool dashed = false)
    {
        var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };

        if (dashed)
            pen.DashStyle = new DashStyle([3.5, 3], 0);

        pen.Freeze();
        return pen;
    }

    /// <summary>
    /// A tooltip card beside <paramref name="anchor"/>, kept inside <paramref name="bounds"/>: a
    /// heading, a line per figure with its colour key, and notes underneath.
    /// </summary>
    protected void DrawTooltip(
        DrawingContext dc,
        ChartPalette palette,
        Rect bounds,
        Point anchor,
        string heading,
        IReadOnlyList<TooltipRow> rows,
        IReadOnlyList<string>? notes = null)
    {
        const double pad = 10;
        const double gap = 4;
        const double key = 10;

        var title = Text(heading, 12.5, palette.Ink, bold: true);
        var labels = rows.Select(r => Text(r.Label, 12, palette.Muted)).ToList();
        var values = rows.Select(r => Text(r.Value, 12, palette.Ink, bold: true, figures: true)).ToList();
        var noteTexts = (notes ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => Text(n, 11.5, palette.Dim)).ToList();

        var rowHeight = Math.Max(16, labels.Count == 0 ? 0 : labels.Max(l => l.Height));
        var labelWidth = labels.Count == 0 ? 0 : labels.Max(l => l.WidthIncludingTrailingWhitespace);
        var valueWidth = values.Count == 0 ? 0 : values.Max(v => v.WidthIncludingTrailingWhitespace);
        var rowsWidth = rows.Count == 0 ? 0 : key + 8 + labelWidth + 16 + valueWidth;
        var notesWidth = noteTexts.Count == 0 ? 0 : noteTexts.Max(n => n.WidthIncludingTrailingWhitespace);

        var width = Math.Min(bounds.Width - 8, Math.Max(title.WidthIncludingTrailingWhitespace, Math.Max(rowsWidth, notesWidth)) + (2 * pad));
        var height = pad + title.Height
                     + (rows.Count == 0 ? 0 : 6 + (rows.Count * (rowHeight + gap)) - gap)
                     + (noteTexts.Count == 0 ? 0 : 9 + noteTexts.Sum(n => n.Height + 2))
                     + pad;

        // Beside the point, on whichever side has the room; never over the thing being pointed at.
        var x = anchor.X + 16;
        if (x + width > bounds.Right - 4)
            x = anchor.X - 16 - width;
        x = Math.Clamp(x, bounds.Left + 4, Math.Max(bounds.Left + 4, bounds.Right - 4 - width));

        var y = Math.Clamp(anchor.Y - (height / 2), bounds.Top + 4, Math.Max(bounds.Top + 4, bounds.Bottom - 4 - height));
        var card = new Rect(x, y, width, height);

        // A soft shadow from three widening rings: enough to lift the card off a busy chart without
        // an effect, which would blur every figure drawn under it.
        for (var i = 3; i >= 1; i--)
        {
            var shadow = new SolidColorBrush(Color.FromArgb((byte)(22 * (4 - i)), 0, 0, 0));
            shadow.Freeze();
            var grow = i * 2.0;
            dc.DrawRoundedRectangle(shadow, null, new Rect(card.X - grow, card.Y - grow + 2, card.Width + (2 * grow), card.Height + (2 * grow)), 9 + grow, 9 + grow);
        }

        dc.DrawRoundedRectangle(palette.Tooltip, MakePen(palette.TooltipEdge, 1), card, 8, 8);

        var top = card.Y + pad;
        dc.DrawText(title, new Point(card.X + pad, top));
        top += title.Height;

        if (rows.Count > 0)
        {
            top += 6;

            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var middle = top + (rowHeight / 2);

                if (row.Swatch is { } colour)
                {
                    var brush = new SolidColorBrush(colour);
                    brush.Freeze();

                    if (row.Line)
                        dc.DrawLine(MakePen(brush, 2.5, row.Dashed), new Point(card.X + pad, middle), new Point(card.X + pad + key, middle));
                    else
                        dc.DrawRoundedRectangle(brush, null, new Rect(card.X + pad, middle - (key / 2), key, key), 2.5, 2.5);
                }

                dc.DrawText(labels[i], new Point(card.X + pad + key + 8, middle - (labels[i].Height / 2)));
                dc.DrawText(values[i], new Point(card.Right - pad - values[i].WidthIncludingTrailingWhitespace, middle - (values[i].Height / 2)));
                top += rowHeight + gap;
            }

            top -= gap;
        }

        if (noteTexts.Count > 0)
        {
            top += 4;
            HairLine(dc, MakePen(palette.TooltipEdge, 1), new Point(card.X + pad, top), new Point(card.Right - pad, top));
            top += 5;

            foreach (var note in noteTexts)
            {
                dc.DrawText(note, new Point(card.X + pad, top));
                top += note.Height + 2;
            }
        }
    }

    /// <summary>A sentence in the middle of the chart, for when there is nothing to draw.</summary>
    protected void DrawEmpty(DrawingContext dc, ChartPalette palette, Rect area, string text)
    {
        var message = Text(text, 13, palette.Dim);
        message.MaxTextWidth = Math.Max(40, area.Width - 24);
        message.TextAlignment = TextAlignment.Center;
        dc.DrawText(message, new Point(area.X + 12, area.Y + ((area.Height - message.Height) / 2)));
    }

    // ---- Keyboard focus ------------------------------------------------------------------------------

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);

        // Clicked into, the chart takes the keyboard too, so the arrows carry on from where the mouse was.
        if (!IsKeyboardFocused)
            Focus();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        InvalidateVisual();
    }

    // ---- Saving -----------------------------------------------------------------------------------

    /// <summary>
    /// The chart as a picture, drawn at twice the size so it stays sharp pasted into a message or a
    /// document, on the card colour so it does not come out on a transparent black.
    /// </summary>
    public BitmapSource Snapshot(double scale = 2)
    {
        var width = Math.Max(1, ActualWidth);
        var height = Math.Max(1, ActualHeight);

        var visual = new DrawingVisual();

        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(ChartPalette.Current.Surface, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(this) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Writes <see cref="Snapshot"/> to a PNG file.</summary>
    public void SavePng(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Snapshot()));

        using var file = File.Create(path);
        encoder.Save(file);
    }

    // ---- Screen readers -------------------------------------------------------------------------------

    protected override AutomationPeer OnCreateAutomationPeer() => _peer = new ChartAutomationPeer(this);

    /// <summary>
    /// A chart as a screen reader meets it: its title as its name, and as its help text whatever the
    /// arrows last landed on, or the summary before they have landed anywhere.
    /// </summary>
    private sealed class ChartAutomationPeer(ChartSurface owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => "Chart";

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

        protected override string GetNameCore()
        {
            var chart = (ChartSurface)Owner;
            return string.IsNullOrWhiteSpace(chart.Title) ? base.GetNameCore() : chart.Title;
        }

        protected override string GetHelpTextCore()
        {
            var chart = (ChartSurface)Owner;
            return string.IsNullOrEmpty(chart.ActiveDescription) ? chart.Summary : chart.ActiveDescription;
        }

        protected override bool IsKeyboardFocusableCore() => true;
    }
}
