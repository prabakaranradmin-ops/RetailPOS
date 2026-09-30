using System.Data;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Pos.App.Charts;

/// <summary>
/// Items placed by how fast they sell against how much they earn, each drawn as big as what it took,
/// and the field split into four at the shop's own middle on each.
/// </summary>
/// <remarks>
/// <para>
/// The same picture as the saved web page's, made to answer questions: pointing at an item, or
/// walking the items with the arrow keys, gives its name and its figures, and each of the four boxes
/// can be switched off from the legend to see the rest more clearly.
/// </para>
/// <para>
/// How much sold runs on a logarithmic scale once the fastest seller outsells the slowest many times
/// over. A grocery sells rice by the hundred kilos and saffron by the gram, and on an even scale
/// every item but the top three would sit in a heap against the left-hand edge.
/// </para>
/// </remarks>
public sealed class BubbleChart : ChartSurface
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(BubbleData), typeof(BubbleChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((BubbleChart)d).OnDataChanged()));

    /// <summary>The spread past which "how much sold" goes onto a logarithmic scale.</summary>
    private const double LogSpread = 20;

    private readonly HashSet<int> _hiddenGroups = [];
    private readonly List<(Rect Area, int Group)> _legend = [];
    private readonly List<(Point Centre, double Radius, int Point)> _placed = [];

    private Rect _plot;
    private int? _hover;
    private int? _focused;
    private Point? _pointer;

    public BubbleData? Data
    {
        get => (BubbleData?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public int? ActivePoint => _hover ?? _focused;

    /// <summary>True when how much sold is on a logarithmic scale.</summary>
    public bool IsLogarithmic => Data is { } d && UsesLog(d);

    public bool IsGroupShown(int group) => !_hiddenGroups.Contains(group);

    private void OnDataChanged()
    {
        _hiddenGroups.Clear();
        _hover = null;
        _focused = null;
        ActiveDescription = string.Empty;
        Replay();
        OnFiguresChanged();
    }

    private static bool UsesLog(BubbleData d)
    {
        var positive = d.Points.Where(p => p.X > 0).Select(p => p.X).ToList();
        return positive.Count == d.Points.Count && positive.Count > 1 && positive.Max() / positive.Min() >= LogSpread;
    }

    public void ToggleGroup(int group)
    {
        if (Data is not { } d || group < 0 || group >= d.Groups.Count)
            return;

        if (!_hiddenGroups.Remove(group))
            _hiddenGroups.Add(group);

        if (_focused is { } focused && _hiddenGroups.Contains(d.Points[focused].Group))
            _focused = null;

        InvalidateVisual();
    }

    /// <summary>The items on show, left to right, as the arrow keys walk them.</summary>
    private List<int> Walk()
    {
        var d = Data!;
        return Enumerable.Range(0, d.Points.Count)
            .Where(i => !_hiddenGroups.Contains(d.Points[i].Group))
            .OrderBy(i => d.Points[i].X)
            .ThenBy(i => d.Points[i].Y)
            .ToList();
    }

    public void Step(int direction)
    {
        if (Data is null)
            return;

        var order = Walk();

        if (order.Count == 0)
            return;

        var at = _focused is { } current ? order.IndexOf(current) : -1;
        var next = at < 0 ? (direction > 0 ? 0 : order.Count - 1) : Math.Clamp(at + direction, 0, order.Count - 1);

        _focused = order[next];
        _hover = null;
        Describe(_focused.Value);
        InvalidateVisual();
    }

    private void Describe(int index)
    {
        if (Data is not { } d || index < 0 || index >= d.Points.Count)
            return;

        var point = d.Points[index];
        var group = point.Group >= 0 && point.Group < d.Groups.Count ? d.Groups[point.Group].Name : string.Empty;

        ActiveDescription = $"{point.Label}: {d.XTitle} {d.XFormat(point.X)}, {d.YTitle} {d.YFormat(point.Y)}. "
                            + string.Join(". ", point.Lines) + (group.Length > 0 ? $". {group}." : ".");
    }

    public override string Summary
    {
        get
        {
            if (Data is not { HasData: true } d)
                return $"{Title}. {Data?.EmptyText}";

            var counts = d.Groups.Select((g, i) => $"{d.Points.Count(p => p.Group == i)} {g.Name.ToLowerInvariant()}");
            return $"{Title}: {d.Points.Count} items - {string.Join(", ", counts)}. The arrow keys read each one.";
        }
    }

    public override DataTable ToTable()
    {
        var table = new DataTable(Title);

        if (Data is not { } d)
            return table;

        table.Columns.Add("Item", typeof(string));
        table.Columns.Add(Capitalise(d.XTitle), typeof(string));
        table.Columns.Add(Capitalise(d.YTitle), typeof(string));
        table.Columns.Add("Box", typeof(string));
        table.Columns.Add("Detail", typeof(string));

        foreach (var point in d.Points.OrderByDescending(p => p.Size))
        {
            table.Rows.Add(point.Label, d.XFormat(point.X), d.YFormat(point.Y),
                point.Group >= 0 && point.Group < d.Groups.Count ? d.Groups[point.Group].Name : string.Empty,
                string.Join(", ", point.Lines));
        }

        return table;

        static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }

    // ---- Mouse and keyboard ---------------------------------------------------------------------------

    private int? PointAt(Point position)
    {
        int? nearest = null;
        var best = double.MaxValue;

        foreach (var (centre, radius, index) in _placed)
        {
            var distance = (position - centre).Length;

            if (distance <= radius + 4 && distance < best)
            {
                best = distance;
                nearest = index;
            }
        }

        return nearest;
    }

    private int? LegendAt(Point position)
    {
        foreach (var (area, group) in _legend)
        {
            if (area.Contains(position))
                return group;
        }

        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var position = e.GetPosition(this);
        _pointer = position;

        var legend = LegendAt(position);
        var point = legend is null ? PointAt(position) : null;

        Cursor = legend is not null ? Cursors.Hand : null;

        if (point != _hover && point is { } now)
            Describe(now);

        _hover = point;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        _pointer = null;
        Cursor = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (LegendAt(e.GetPosition(this)) is { } group)
        {
            ToggleGroup(group);
            e.Handled = true;
        }
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        if (_focused is null && Data is { HasData: true })
            Step(+1);

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

        if (e.Handled || Data is null || Keyboard.Modifiers != ModifierKeys.None)
            return;

        switch (e.Key)
        {
            case Key.Right or Key.Down: Step(+1); break;
            case Key.Left or Key.Up: Step(-1); break;
            case Key.Home: _focused = null; Step(+1); break;
            case Key.End: _focused = null; Step(-1); break;
            case >= Key.D1 and <= Key.D9: ToggleGroup(e.Key - Key.D1); break;
            case >= Key.NumPad1 and <= Key.NumPad9: ToggleGroup(e.Key - Key.NumPad1); break;
            default: return;
        }

        e.Handled = true;
    }

    // ---- Drawing ----------------------------------------------------------------------------------------

    protected override void RenderChart(DrawingContext dc, Size size, ChartPalette palette)
    {
        _legend.Clear();
        _placed.Clear();

        var d = Data;

        if (d is null || !d.HasData)
        {
            DrawEmpty(dc, palette, new Rect(size), d?.EmptyText ?? "Nothing to show.");
            return;
        }

        var log = UsesLog(d);

        // ---- The vertical scale, and room for its figures

        var lines = Math.Clamp((int)((size.Height - 70) / 46), 3, 6);
        var yScale = NiceScale.For(d.Points.Min(p => p.Y), d.Points.Max(p => p.Y), lines);
        var yLabels = yScale.Ticks().Select(t => (Tick: t, Text: Text(d.YFormat(t), 11, palette.Dim))).ToList();

        var left = Math.Ceiling(yLabels.Max(l => l.Text.Width)) + 14;
        const double top = 30;
        const double bottom = 42;
        _plot = new Rect(left, top, Math.Max(10, size.Width - left - 14), Math.Max(10, size.Height - top - bottom));

        // ---- The horizontal scale: even, or logarithmic when the sellers are orders of magnitude apart

        double xLow, xHigh;
        List<double> xTicks;

        if (log)
        {
            xLow = Math.Pow(10, Math.Floor(Math.Log10(d.Points.Min(p => p.X))));
            xHigh = Math.Pow(10, Math.Ceiling(Math.Log10(d.Points.Max(p => p.X))));
            xTicks = [];

            for (var decade = xLow; decade <= xHigh * 1.0001; decade *= 10)
            {
                foreach (var step in new[] { 1.0, 2.0, 5.0 })
                {
                    var tick = decade * step;
                    if (tick >= xLow * 0.9999 && tick <= xHigh * 1.0001)
                        xTicks.Add(tick);
                }
            }
        }
        else
        {
            var xScale = NiceScale.For(0, d.Points.Max(p => p.X), Math.Clamp((int)(_plot.Width / 90), 3, 8));
            xLow = xScale.Min;
            xHigh = xScale.Max;
            xTicks = xScale.Ticks().ToList();
        }

        double X(double value) => log
            ? _plot.Left + ((Math.Log10(Math.Max(value, xLow)) - Math.Log10(xLow)) / Math.Max(1e-9, Math.Log10(xHigh) - Math.Log10(xLow)) * _plot.Width)
            : _plot.Left + ((value - xLow) / Math.Max(1e-9, xHigh - xLow) * _plot.Width);

        double Y(double value) => _plot.Bottom - (yScale.Fraction(value) * _plot.Height);

        // ---- Gridlines both ways, and the figures along each axis

        var grid = MakePen(palette.Grid, 1);
        var axis = MakePen(palette.AxisLine, 1);

        foreach (var (tick, text) in yLabels)
        {
            var y = Math.Round(Y(tick));
            HairLine(dc, Math.Abs(tick) < 1e-12 ? axis : grid, new Point(_plot.Left, y), new Point(_plot.Right, y));
            dc.DrawText(text, new Point(_plot.Left - 10 - text.Width, y - (text.Height / 2)));
        }

        var lastLabelRight = double.MinValue;

        foreach (var tick in xTicks)
        {
            var x = Math.Round(X(tick));
            HairLine(dc, grid, new Point(x, _plot.Top), new Point(x, _plot.Bottom));

            var label = Text(d.XFormat(tick), 11, palette.Dim);
            var labelLeft = x - (label.Width / 2);

            // Thinned where the log scale bunches its ticks together at the top of a decade.
            if (labelLeft > lastLabelRight + 6)
            {
                dc.DrawText(label, new Point(labelLeft, _plot.Bottom + 5));
                lastLabelRight = labelLeft + label.Width;
            }
        }

        var xTitle = Text(d.XTitle + (log ? " (log scale)" : string.Empty) + " →", 11, palette.Muted);
        dc.DrawText(xTitle, new Point(_plot.Right - xTitle.Width, _plot.Bottom + 22));

        var yTitle = Text("↑ " + d.YTitle, 11, palette.Muted);
        dc.DrawText(yTitle, new Point(4, 6));

        // ---- The shop's own middle, and the name of each box in its corner

        var splitPen = MakePen(palette.Muted, 1, dashed: true);
        var splitX = d.XSplit is { } sx ? Math.Clamp(X(sx), _plot.Left, _plot.Right) : _plot.Left + (_plot.Width / 2);
        var splitY = d.YSplit is { } sy ? Math.Clamp(Y(sy), _plot.Top, _plot.Bottom) : _plot.Top + (_plot.Height / 2);

        HairLine(dc, splitPen, new Point(splitX, _plot.Top), new Point(splitX, _plot.Bottom));
        HairLine(dc, splitPen, new Point(_plot.Left, splitY), new Point(_plot.Right, splitY));

        for (var g = 0; g < Math.Min(4, d.Groups.Count); g++)
        {
            var name = Text(d.Groups[g].Name.ToUpperInvariant(), 10.5, palette.SeriesBrush(d.Groups[g].Colour, 0.9), bold: true);
            var right = g is 1 or 3;
            var lower = g is 2 or 3;

            var x = right ? _plot.Right - name.Width - 6 : _plot.Left + 6;
            var y = lower ? _plot.Bottom - name.Height - 4 : _plot.Top + 4;
            dc.DrawText(name, new Point(x, y));
        }

        // ---- The items, biggest first so the small ones stay on top where they can be pointed at

        var biggest = Math.Max(1e-9, d.Points.Max(p => p.Size));
        var active = ActivePoint;

        foreach (var index in Enumerable.Range(0, d.Points.Count).OrderByDescending(i => d.Points[i].Size))
        {
            var point = d.Points[index];

            if (_hiddenGroups.Contains(point.Group))
                continue;

            var colour = palette.Series(point.Group >= 0 && point.Group < d.Groups.Count ? d.Groups[point.Group].Colour : 6);
            var radius = (5 + (Math.Sqrt(Math.Max(0, point.Size) / biggest) * 16)) * Progress;
            var centre = new Point(X(point.X), Y(point.Y));
            var lit = index == active;
            var fade = active is null || lit ? 1.0 : 0.4;

            var fill = new SolidColorBrush(ChartPalette.WithAlpha(colour, (lit ? 0.85 : 0.5) * fade));
            fill.Freeze();
            var edge = new SolidColorBrush(ChartPalette.WithAlpha(colour, fade));
            edge.Freeze();

            dc.DrawEllipse(fill, MakePen(lit ? palette.Ink : edge, lit ? 2 : 1.4), centre, Math.Max(1, radius), Math.Max(1, radius));
            _placed.Add((centre, radius, index));
        }

        DrawLegend(dc, d, palette);

        // ---- The tooltip

        if (active is { } at && !_hiddenGroups.Contains(d.Points[at].Group))
        {
            var point = d.Points[at];
            var group = point.Group >= 0 && point.Group < d.Groups.Count ? d.Groups[point.Group] : null;
            var anchor = _hover is not null && _pointer is { } pointer ? pointer : new Point(X(point.X), Y(point.Y));
            var notes = point.Lines.ToList();

            if (group is not null)
                notes.Add($"{group.Name}: {group.Advice}");

            DrawTooltip(dc, palette, new Rect(size), anchor, point.Label,
            [
                new TooltipRow(group is null ? null : palette.Series(group.Colour), Capitalise(d.XTitle), d.XFormat(point.X)),
                new TooltipRow(null, Capitalise(d.YTitle), d.YFormat(point.Y)),
            ],
            notes);
        }

        static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }

    private void DrawLegend(DrawingContext dc, BubbleData d, ChartPalette palette)
    {
        var yTitleWidth = Text("↑ " + d.YTitle, 11, palette.Muted).Width;
        var x = Math.Max(_plot.Left, yTitleWidth + 24);

        for (var g = 0; g < d.Groups.Count; g++)
        {
            var group = d.Groups[g];
            var hidden = _hiddenGroups.Contains(g);
            var name = Text(group.Name, 11.5, hidden ? palette.Dim : palette.Muted);
            var colour = palette.Series(group.Colour);
            var brush = new SolidColorBrush(hidden ? ChartPalette.WithAlpha(colour, 0.35) : colour);
            brush.Freeze();

            const double middle = 13;

            if (hidden)
                dc.DrawEllipse(null, MakePen(brush, 1.2), new Point(x + 5, middle), 5, 5);
            else
                dc.DrawEllipse(brush, null, new Point(x + 5, middle), 5, 5);

            var textAt = new Point(x + 16, middle - (name.Height / 2));
            dc.DrawText(name, textAt);

            if (hidden)
                dc.DrawLine(MakePen(palette.Dim, 1), new Point(textAt.X, middle), new Point(textAt.X + name.Width, middle));

            var width = 16 + name.Width + 16;
            _legend.Add((new Rect(x - 4, 2, width, 22), g));
            x += width;
        }
    }
}
