using System.Data;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Pos.App.Charts;

/// <summary>
/// Figures across a row of categories - days, hours, months - as bars, lines or filled areas.
/// </summary>
/// <remarks>
/// <para>
/// Pointing anywhere over a category picks the whole category: a dashed line drops through it and
/// one tooltip lists every series there, so a day's takings and its seven-day average are read
/// together rather than by hunting for the one thin line. The arrow keys do the same from the
/// keyboard, one category at a time.
/// </para>
/// <para>
/// Dragging across the chart zooms into that stretch - a busy fortnight out of ninety days - and a
/// double-click, or 0, shows everything again. Each name in the legend can be clicked, or its
/// number pressed, to take that series off the chart and put it back.
/// </para>
/// </remarks>
public sealed class CategoryChart : ChartSurface
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(CategoryChartData), typeof(CategoryChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((CategoryChart)d).OnDataChanged()));

    /// <summary>A zoomed chart never shows fewer categories than this; two bars say nothing a tooltip would not.</summary>
    private const int FewestShown = 3;

    private readonly HashSet<int> _hidden = [];
    private readonly List<(Rect Area, int Series)> _legend = [];

    private int _start;
    private int _end;
    private int? _hover;
    private int? _focused;
    private Point? _pointer;
    private double? _dragFrom;
    private double? _dragTo;
    private Rect _plot;

    public CategoryChartData? Data
    {
        get => (CategoryChartData?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    /// <summary>The first category on show.</summary>
    public int VisibleStart => _start;

    /// <summary>One past the last category on show.</summary>
    public int VisibleEnd => _end;

    /// <summary>The category the tooltip is on: the mouse's while it is over the chart, else the keyboard's.</summary>
    public int? ActiveIndex => _hover ?? _focused;

    public override bool IsZoomed => Data is { } d && (_start > 0 || _end < d.Count);

    public bool IsSeriesShown(int series) => !_hidden.Contains(series);

    /// <summary>Where the figures are drawn, inside the axes. Known once the chart has been drawn.</summary>
    public Rect PlotArea => _plot;

    private void OnDataChanged()
    {
        _hidden.Clear();
        _start = 0;
        _end = Data?.Count ?? 0;
        _hover = null;
        _focused = IsKeyboardFocused && Data is { Count: > 0 } ? DefaultIndex() : null;
        ActiveDescription = string.Empty;

        Replay();
        OnZoomChanged();
        OnFiguresChanged();
    }

    // ---- What the chart can be asked to do -------------------------------------------------------------

    /// <summary>Shows only categories <paramref name="start"/> to <paramref name="end"/>, end not included.</summary>
    public void ZoomTo(int start, int end)
    {
        if (Data is not { Count: > 0 } d)
            return;

        start = Math.Clamp(start, 0, d.Count - 1);
        end = Math.Clamp(end, start + 1, d.Count);

        var fewest = Math.Min(FewestShown, d.Count);

        if (end - start < fewest)
        {
            end = Math.Min(d.Count, start + fewest);
            start = Math.Max(0, end - fewest);
        }

        if (start == _start && end == _end)
            return;

        _start = start;
        _end = end;

        if (_focused is { } focused)
            _focused = Math.Clamp(focused, _start, _end - 1);

        InvalidateVisual();
        OnZoomChanged();
    }

    public override void ResetZoom()
    {
        if (Data is { Count: > 0 } d)
            ZoomTo(0, d.Count);
    }

    /// <summary>Takes a series off the chart, or puts it back.</summary>
    public void ToggleSeries(int series)
    {
        if (Data is not { } d || series < 0 || series >= d.Series.Count)
            return;

        if (!_hidden.Remove(series))
            _hidden.Add(series);

        if (ActiveIndex is { } active)
            Describe(active);

        InvalidateVisual();
    }

    /// <summary>Puts the tooltip on a category, sliding a zoomed chart along to keep it in view.</summary>
    public void MoveTo(int index)
    {
        if (Data is not { Count: > 0 } d)
            return;

        index = Math.Clamp(index, 0, d.Count - 1);
        var span = _end - _start;

        if (index < _start)
            ZoomTo(index, index + span);
        else if (index >= _end)
            ZoomTo(index - span + 1, index + 1);

        _focused = index;
        _hover = null;
        Describe(index);
        InvalidateVisual();
    }

    private void ZoomAround(int index, double factor)
    {
        if (Data is not { Count: > 0 } d)
            return;

        var span = Math.Clamp((int)Math.Round((_end - _start) * factor), Math.Min(FewestShown, d.Count), d.Count);
        var start = Math.Clamp(index - (span / 2), 0, d.Count - span);

        ZoomTo(start, start + span);
        _focused = Math.Clamp(index, _start, _end - 1);
        Describe(_focused.Value);
        InvalidateVisual();
    }

    /// <summary>Where the keyboard starts: the latest category with anything in it.</summary>
    private int DefaultIndex()
    {
        var d = Data!;

        for (var i = _end - 1; i >= _start; i--)
        {
            if (d.Series.Where((_, s) => !_hidden.Contains(s)).Any(s => Math.Abs(ValueAt(s, i)) > 1e-9))
                return i;
        }

        return Math.Max(_start, _end - 1);
    }

    private static double ValueAt(ChartSeries series, int index) =>
        index >= 0 && index < series.Values.Count ? series.Values[index] : 0;

    private void Describe(int index)
    {
        if (Data is not { } d || index < 0 || index >= d.Count)
            return;

        var figures = d.Series
            .Select((s, i) => (s, i))
            .Where(x => !_hidden.Contains(x.i))
            .Select(x => $"{x.s.Name} {(x.s.Format ?? d.ValueFormat)(ValueAt(x.s, index))}");

        var note = d.Notes is { } notes && index < notes.Count ? notes[index] : null;

        ActiveDescription = $"{d.Titles[index]}: {string.Join(", ", figures)}."
                            + (string.IsNullOrWhiteSpace(note) ? string.Empty : $" {note}.");
    }

    public override string Summary
    {
        get
        {
            if (Data is not { Count: > 0 } d)
                return Title;

            if (!d.HasData)
                return $"{Title}. {d.EmptyText}";

            var first = d.Series[0];

            if (first.Values.Count == 0)
                return Title;

            var peak = Enumerable.Range(0, Math.Min(d.Count, first.Values.Count)).MaxBy(i => first.Values[i]);

            return $"{Title}, {d.Titles[0]} to {d.Titles[^1]}. Highest {first.Name.ToLowerInvariant()}: "
                   + $"{(first.Format ?? d.ValueFormat)(first.Values[peak])}, {d.Titles[peak]}. "
                   + "The arrow keys read each one.";
        }
    }

    public override DataTable ToTable()
    {
        var table = new DataTable(Title);

        if (Data is not { } d)
            return table;

        table.Columns.Add(d.CategoryName, typeof(string));

        foreach (var series in d.Series)
        {
            var name = series.Name;

            for (var n = 2; table.Columns.Contains(name); n++)
                name = $"{series.Name} ({n})";

            table.Columns.Add(name, typeof(string));
        }

        for (var i = 0; i < d.Count; i++)
        {
            var row = table.NewRow();
            row[0] = d.Titles[i];

            for (var s = 0; s < d.Series.Count; s++)
                row[s + 1] = (d.Series[s].Format ?? d.ValueFormat)(ValueAt(d.Series[s], i));

            table.Rows.Add(row);
        }

        return table;
    }

    // ---- Mouse --------------------------------------------------------------------------------------

    private int IndexAt(double x)
    {
        var count = Math.Max(1, _end - _start);
        var slot = _plot.Width / count;
        return Math.Clamp(_start + (int)Math.Floor((x - _plot.Left) / slot), _start, _end - 1);
    }

    private int? LegendAt(Point point)
    {
        foreach (var (area, series) in _legend)
        {
            if (area.Contains(point))
                return series;
        }

        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (Data is not { Count: > 0 })
            return;

        var point = e.GetPosition(this);
        _pointer = point;

        if (_dragFrom is not null)
        {
            _dragTo = Math.Clamp(point.X, _plot.Left, _plot.Right);
            InvalidateVisual();
            return;
        }

        var overLegend = LegendAt(point) is not null;
        Cursor = overLegend ? Cursors.Hand : _plot.Contains(point) ? Cursors.Cross : null;

        int? index = !overLegend && _plot.Contains(point) ? IndexAt(point.X) : null;

        if (index != _hover && index is { } now)
            Describe(now);

        _hover = index;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);

        _pointer = null;
        Cursor = null;

        if (_dragFrom is null)
        {
            _hover = null;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (Data is not { Count: > 0 })
            return;

        var point = e.GetPosition(this);

        if (LegendAt(point) is { } series)
        {
            ToggleSeries(series);
            e.Handled = true;
            return;
        }

        if (!_plot.Contains(point))
            return;

        if (e.ClickCount == 2)
        {
            ResetZoom();
            e.Handled = true;
            return;
        }

        _dragFrom = point.X;
        _dragTo = point.X;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (_dragFrom is not { } from || _dragTo is not { } to)
            return;

        _dragFrom = null;
        _dragTo = null;
        ReleaseMouseCapture();

        // A click is not a drag. Twelve pixels is well past a hand's wobble and well short of a bar.
        if (Math.Abs(to - from) >= 12)
            ZoomTo(IndexAt(Math.Min(from, to)), IndexAt(Math.Max(from, to)) + 1);

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);

        if (_dragFrom is null)
            return;

        _dragFrom = null;
        _dragTo = null;
        InvalidateVisual();
    }

    // ---- Keyboard -------------------------------------------------------------------------------------

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        if (Data is { Count: > 0 } && _focused is null)
        {
            _focused = DefaultIndex();
            Describe(_focused.Value);
        }

        base.OnGotKeyboardFocus(e);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        _focused = null;
        base.OnLostKeyboardFocus(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Handled || Data is not { Count: > 0 } d || Keyboard.Modifiers != ModifierKeys.None)
            return;

        var current = _focused ?? _hover ?? DefaultIndex();

        switch (e.Key)
        {
            case Key.Left: MoveTo(current - 1); break;
            case Key.Right: MoveTo(current + 1); break;
            case Key.Home: MoveTo(0); break;
            case Key.End: MoveTo(d.Count - 1); break;
            case Key.Add or Key.OemPlus: ZoomAround(current, 0.5); break;
            case Key.Subtract or Key.OemMinus: ZoomAround(current, 2); break;
            case Key.D0 or Key.NumPad0 or Key.Back: ResetZoom(); break;
            case >= Key.D1 and <= Key.D9: ToggleSeries(e.Key - Key.D1); break;
            case >= Key.NumPad1 and <= Key.NumPad9: ToggleSeries(e.Key - Key.NumPad1); break;
            default: return;
        }

        e.Handled = true;
    }

    // ---- Drawing ----------------------------------------------------------------------------------------

    protected override void RenderChart(DrawingContext dc, Size size, ChartPalette palette)
    {
        _legend.Clear();

        var d = Data;

        if (d is null || d.Count == 0)
        {
            _plot = new Rect(size);
            DrawEmpty(dc, palette, new Rect(size), d?.EmptyText ?? "Nothing to show.");
            return;
        }

        if (_end <= _start || _end > d.Count)
        {
            _start = 0;
            _end = d.Count;
        }

        var shown = Enumerable.Range(0, d.Series.Count).Where(i => !_hidden.Contains(i)).ToList();
        var legendHeight = d.Series.Count > 1 ? 24.0 : 4.0;
        var count = _end - _start;

        // ---- Scales

        (double Low, double High, bool Whole) Extent(ChartAxis axis)
        {
            var values = shown
                .Where(i => d.Series[i].Axis == axis)
                .SelectMany(i => Enumerable.Range(_start, count).Select(k => ValueAt(d.Series[i], k)))
                .ToList();

            return values.Count == 0
                ? (0, 0, true)
                : (values.Min(), values.Max(), values.All(v => Math.Abs(v - Math.Round(v)) < 1e-9));
        }

        var lines = Math.Clamp((int)((size.Height - legendHeight - 30) / 46), 3, 6);
        var (low, high, whole) = Extent(ChartAxis.Primary);
        var primary = NiceScale.For(low, high, lines, wholeSteps: whole && high >= 1);

        NiceScale? secondary = null;

        if (shown.Any(i => d.Series[i].Axis == ChartAxis.Secondary))
        {
            var (low2, high2, whole2) = Extent(ChartAxis.Secondary);
            secondary = NiceScale.For(low2, high2, lines, wholeSteps: whole2 && high2 >= 1);
        }

        var primaryLabels = primary.Ticks().Select(t => (Tick: t, Text: Text(d.AxisFormat(t), 11, palette.Dim))).ToList();
        var secondaryFormat = d.SecondaryAxisFormat ?? ChartFormat.CompactCount;
        var secondaryLabels = secondary is { } sec
            ? sec.Ticks().Select(t => (Tick: t, Text: Text(secondaryFormat(t), 11, palette.Dim))).ToList()
            : [];

        var left = Math.Ceiling(primaryLabels.Max(l => l.Text.Width)) + 14;
        var right = secondaryLabels.Count > 0 ? Math.Ceiling(secondaryLabels.Max(l => l.Text.Width)) + 14 : 10;
        var top = legendHeight + 8;
        const double bottom = 26;

        _plot = new Rect(left, top, Math.Max(10, size.Width - left - right), Math.Max(10, size.Height - top - bottom));

        double Y(double value, NiceScale scale) => _plot.Bottom - (scale.Fraction(value) * _plot.Height);
        NiceScale ScaleOf(ChartSeries series) => series.Axis == ChartAxis.Secondary && secondary is { } s ? s : primary;

        var slot = _plot.Width / count;
        double Centre(int index) => _plot.Left + ((index - _start + 0.5) * slot);

        // ---- Gridlines and the axes' figures

        var gridPen = MakePen(palette.Grid, 1);
        var axisPen = MakePen(palette.AxisLine, 1);

        foreach (var (tick, text) in primaryLabels)
        {
            var y = Math.Round(Y(tick, primary));
            HairLine(dc, Math.Abs(tick) < 1e-12 ? axisPen : gridPen, new Point(_plot.Left, y), new Point(_plot.Right, y));
            dc.DrawText(text, new Point(_plot.Left - 10 - text.Width, y - (text.Height / 2)));
        }

        if (secondary is { } secondScale)
        {
            foreach (var (tick, text) in secondaryLabels)
                dc.DrawText(text, new Point(_plot.Right + 10, Y(tick, secondScale) - (text.Height / 2)));
        }

        var active = ActiveIndex is { } a && a >= _start && a < _end ? a : (int?)null;

        // A soft band behind the category being read, so the eye finds it without the tooltip.
        if (active is { } band && _dragFrom is null)
        {
            var bandBrush = palette.SeriesBrush(0, 0.07);
            dc.DrawRectangle(bandBrush, null, new Rect(_plot.Left + ((band - _start) * slot), _plot.Top, slot, _plot.Height));
        }

        if (!d.HasData || shown.Count == 0)
        {
            DrawLegend(dc, d, palette);
            DrawCategoryLabels(dc, d, palette, size, slot, Centre, null);
            DrawEmpty(dc, palette, _plot, shown.Count == 0 ? "Every series is hidden. Click a name above, or press its number, to show it again." : d.EmptyText);
            return;
        }

        // ---- Bars: side by side within their slot, growing up out of the axis as the chart arrives

        var columns = shown.Where(i => d.Series[i].Kind == SeriesKind.Column).ToList();

        if (columns.Count > 0)
        {
            var group = slot * (count > 45 ? 0.84 : count > 14 ? 0.72 : 0.6);
            var gap = columns.Count > 1 ? Math.Min(3, group * 0.06) : 0;
            var width = Math.Max(1, (group - (gap * (columns.Count - 1))) / columns.Count);
            var radius = Math.Min(4, width / 3);

            for (var c = 0; c < columns.Count; c++)
            {
                var series = d.Series[columns[c]];
                var scale = ScaleOf(series);
                var zero = Y(Math.Clamp(0, scale.Min, scale.Max), scale);
                var fill = palette.Fade(series.Colour, 0.95, 0.55);
                var lit = palette.Fade(series.Colour, 1, 0.85);

                for (var i = _start; i < _end; i++)
                {
                    var value = ValueAt(series, i);

                    if (Math.Abs(value) < 1e-12)
                        continue;

                    var x = Centre(i) - (group / 2) + (c * (width + gap));
                    var reach = Math.Max(1.5, Math.Abs(zero - Y(value, scale)) * Progress);
                    var bar = value >= 0 ? new Rect(x, zero - reach, width, reach) : new Rect(x, zero, width, reach);

                    dc.DrawGeometry(i == active ? lit : fill, null, ChartGeometry.Bar(bar, radius, value >= 0));
                }
            }
        }

        // ---- Lines and areas: drawn left to right as the chart arrives

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, _plot.Left + ((_plot.Width + 12) * Progress), size.Height)));

        foreach (var index in shown.Where(i => d.Series[i].Kind != SeriesKind.Column))
        {
            var series = d.Series[index];
            var scale = ScaleOf(series);
            var points = Enumerable.Range(_start, count).Select(k => new Point(Centre(k), Y(ValueAt(series, k), scale))).ToList();

            if (points.Count == 1)
            {
                dc.DrawEllipse(palette.SeriesBrush(series.Colour), null, points[0], 3.5, 3.5);
                continue;
            }

            if (series.Kind == SeriesKind.Area)
            {
                var floor = Y(Math.Clamp(0, scale.Min, scale.Max), scale);
                dc.DrawGeometry(palette.Fade(series.Colour, 0.34, 0.02), null, ChartGeometry.SmoothLine(points, floor));
            }

            dc.DrawGeometry(null, MakePen(palette.SeriesBrush(series.Colour), series.Dashed ? 2 : 2.4, series.Dashed), ChartGeometry.SmoothLine(points));
        }

        dc.Pop();

        // ---- The category being read: a dashed line down through it, and a dot on each line

        if (active is { } at && _dragFrom is null)
        {
            var x = Centre(at);
            HairLine(dc, MakePen(palette.Muted, 1, dashed: true), new Point(x, _plot.Top), new Point(x, _plot.Bottom));

            foreach (var index in shown.Where(i => d.Series[i].Kind != SeriesKind.Column))
            {
                var series = d.Series[index];
                dc.DrawEllipse(palette.SeriesBrush(series.Colour), MakePen(palette.Surface, 2), new Point(x, Y(ValueAt(series, at), ScaleOf(series))), 4.5, 4.5);
            }
        }

        DrawLegend(dc, d, palette);
        DrawCategoryLabels(dc, d, palette, size, slot, Centre, _dragFrom is null ? active : null);

        // ---- Zooming: the stretch being dragged across, and a note while zoomed in

        if (_dragFrom is { } from && _dragTo is { } to)
        {
            var dragged = new Rect(Math.Min(from, to), _plot.Top, Math.Abs(to - from), _plot.Height);
            dc.DrawRectangle(palette.SeriesBrush(0, 0.12), MakePen(palette.SeriesBrush(0, 0.7), 1), dragged);
        }
        else if (IsZoomed)
        {
            var note = Text($"{d.Labels[_start]} to {d.Labels[_end - 1]} · 0 or double-click shows all", 11, palette.Dim);
            dc.DrawText(note, new Point(Math.Max(_plot.Left, _plot.Right - note.Width), 4));
        }

        // ---- The tooltip, over everything

        if (active is { } shownAt && _dragFrom is null)
        {
            var rows = shown
                .Select(i => d.Series[i])
                .Select(s => new TooltipRow(palette.Series(s.Colour), s.Name, (s.Format ?? d.ValueFormat)(ValueAt(s, shownAt)), s.Kind != SeriesKind.Column, s.Dashed))
                .ToList();

            var note = d.Notes is { } notes && shownAt < notes.Count ? notes[shownAt] : null;
            var anchorY = _hover is not null && _pointer is { } pointer ? pointer.Y : _plot.Top + (_plot.Height * 0.3);

            DrawTooltip(dc, palette, new Rect(size), new Point(Centre(shownAt), anchorY), d.Titles[shownAt], rows,
                string.IsNullOrWhiteSpace(note) ? null : [note]);
        }
    }

    /// <summary>The names across the top, each one a switch for its series.</summary>
    private void DrawLegend(DrawingContext dc, CategoryChartData d, ChartPalette palette)
    {
        if (d.Series.Count < 2)
            return;

        var x = _plot.Left;

        for (var i = 0; i < d.Series.Count; i++)
        {
            var series = d.Series[i];
            var hidden = _hidden.Contains(i);
            var name = Text(series.Name, 11.5, hidden ? palette.Dim : palette.Muted);
            var colour = palette.Series(series.Colour);
            var brush = new SolidColorBrush(hidden ? ChartPalette.WithAlpha(colour, 0.35) : colour);
            brush.Freeze();

            const double middle = 11;

            if (series.Kind == SeriesKind.Column)
            {
                var square = new Rect(x, middle - 5, 10, 10);

                if (hidden)
                    dc.DrawRoundedRectangle(null, MakePen(brush, 1.2), square, 2.5, 2.5);
                else
                    dc.DrawRoundedRectangle(brush, null, square, 2.5, 2.5);
            }
            else
            {
                dc.DrawLine(MakePen(brush, 2.5, series.Dashed), new Point(x, middle), new Point(x + 14, middle));
            }

            var textAt = new Point(x + 20, middle - (name.Height / 2));
            dc.DrawText(name, textAt);

            if (hidden)
                dc.DrawLine(MakePen(palette.Dim, 1), new Point(textAt.X, middle), new Point(textAt.X + name.Width, middle));

            var width = 20 + name.Width + 18;
            _legend.Add((new Rect(x - 4, 0, width, 22), i));
            x += width;
        }
    }

    /// <summary>
    /// What runs along the bottom, thinned out until the labels no longer touch, and the one being
    /// read picked out in a chip so it is never among those left out.
    /// </summary>
    private void DrawCategoryLabels(DrawingContext dc, CategoryChartData d, ChartPalette palette, Size size, double slot, Func<int, double> centre, int? active)
    {
        var widest = 0.0;

        for (var i = _start; i < _end; i++)
            widest = Math.Max(widest, Text(d.Labels[i], 11, palette.Dim).Width);

        // Every nth, counted from the first category of all rather than the first on show, so the
        // labels stay put while a zoomed chart slides along instead of jumping between days.
        var every = Math.Max(1, (int)Math.Ceiling((widest + 12) / Math.Max(1, slot)));
        var y = _plot.Bottom + 7;

        for (var i = _start; i < _end; i++)
        {
            if (i % every != 0 || string.IsNullOrEmpty(d.Labels[i]) || i == active)
                continue;

            var label = Text(d.Labels[i], 11, palette.Dim);
            var x = Math.Clamp(centre(i) - (label.Width / 2), 0, Math.Max(0, size.Width - label.Width));
            dc.DrawText(label, new Point(x, y));
        }

        if (active is not { } at)
            return;

        var text = string.IsNullOrEmpty(d.Labels[at]) ? d.Titles[at] : d.Labels[at];
        var chip = Text(text, 11, palette.Ink, bold: true);
        var width = chip.Width + 12;
        var left = Math.Clamp(centre(at) - (width / 2), 0, Math.Max(0, size.Width - width));
        var box = new Rect(left, y - 3, width, chip.Height + 5);

        dc.DrawRoundedRectangle(palette.Tooltip, MakePen(palette.Accent, 1), box, 4, 4);
        dc.DrawText(chip, new Point(box.X + 6, box.Y + 2.5));
    }
}
