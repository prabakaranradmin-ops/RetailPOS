using System.Data;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Pos.App.Charts;

/// <summary>
/// A whole and its parts - how customers paid, where the takings came from - as a ring with the
/// total in the middle and a legend beside it.
/// </summary>
/// <remarks>
/// Pointing at a slice, or walking the slices with the arrow keys, lifts it out of the ring and puts
/// its share in the middle. Clicking a name in the legend, or pressing its number, leaves that part
/// out, and the rest are measured against what remains - "of the money that reached the drawer or
/// the bank, how much was UPI" is the question that answers.
/// </remarks>
public sealed class DonutChart : ChartSurface
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(DonutData), typeof(DonutChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((DonutChart)d).OnDataChanged()));

    private readonly HashSet<int> _hidden = [];
    private readonly List<(Rect Area, int Slice)> _legend = [];
    private readonly List<(double Start, double Sweep, int Slice)> _angles = [];

    private Point _centre;
    private double _outer;
    private double _inner;
    private int? _hover;
    private int? _focused;
    private Point? _pointer;

    public DonutData? Data
    {
        get => (DonutData?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public int? ActiveSlice => _hover ?? _focused;

    public bool IsSliceShown(int slice) => !_hidden.Contains(slice);

    private void OnDataChanged()
    {
        _hidden.Clear();
        _hover = null;
        _focused = null;
        ActiveDescription = string.Empty;
        Replay();
        OnFiguresChanged();
    }

    private double ShownTotal => Data is { } d
        ? d.Slices.Where((_, i) => !_hidden.Contains(i)).Sum(s => Math.Max(0, s.Value))
        : 0;

    private double ShareOf(int slice) => ShownTotal <= 0 || _hidden.Contains(slice) ? 0 : Math.Max(0, Data!.Slices[slice].Value) / ShownTotal;

    public void ToggleSlice(int slice)
    {
        if (Data is not { } d || slice < 0 || slice >= d.Slices.Count)
            return;

        if (!_hidden.Remove(slice))
            _hidden.Add(slice);

        if (_focused is { } focused && _hidden.Contains(focused))
            _focused = null;

        InvalidateVisual();
    }

    /// <summary>Walks the slices on show, round and round.</summary>
    public void Step(int direction)
    {
        if (Data is not { } d)
            return;

        var shown = Enumerable.Range(0, d.Slices.Count).Where(i => !_hidden.Contains(i) && d.Slices[i].Value > 0).ToList();

        if (shown.Count == 0)
            return;

        var at = _focused is { } current ? shown.IndexOf(current) : -1;
        var next = at < 0 ? (direction > 0 ? 0 : shown.Count - 1) : (at + direction + shown.Count) % shown.Count;

        _focused = shown[next];
        _hover = null;
        Describe(_focused.Value);
        InvalidateVisual();
    }

    private void Describe(int slice)
    {
        if (Data is not { } d || slice < 0 || slice >= d.Slices.Count)
            return;

        var s = d.Slices[slice];
        ActiveDescription = $"{s.Label}: {d.ValueFormat(s.Value)}, {ChartFormat.Percent(ShareOf(slice) * 100)} of the total."
                            + (string.IsNullOrWhiteSpace(s.Detail) ? string.Empty : $" {s.Detail}.");
    }

    public override string Summary
    {
        get
        {
            if (Data is not { HasData: true } d)
                return $"{Title}. {Data?.EmptyText}";

            var biggest = d.Slices.MaxBy(s => s.Value)!;
            return $"{Title}: {d.ValueFormat(d.Total)} in {d.Slices.Count} parts. The biggest is {biggest.Label}, "
                   + $"{ChartFormat.Percent(biggest.Value / d.Total * 100)}. The arrow keys read each one.";
        }
    }

    public override DataTable ToTable()
    {
        var table = new DataTable(Title);

        if (Data is not { } d)
            return table;

        table.Columns.Add("Part", typeof(string));
        table.Columns.Add("Amount", typeof(string));
        table.Columns.Add("Share", typeof(string));
        table.Columns.Add("Detail", typeof(string));

        foreach (var slice in d.Slices)
            table.Rows.Add(slice.Label, d.ValueFormat(slice.Value), ChartFormat.Percent(d.Total <= 0 ? 0 : slice.Value / d.Total * 100), slice.Detail);

        return table;
    }

    // ---- Mouse and keyboard ---------------------------------------------------------------------------

    private int? SliceAt(Point point)
    {
        var dx = point.X - _centre.X;
        var dy = point.Y - _centre.Y;
        var distance = Math.Sqrt((dx * dx) + (dy * dy));

        if (distance < _inner - 2 || distance > _outer + 8)
            return null;

        // Clockwise from twelve o'clock, as the slices are laid.
        var degrees = (Math.Atan2(dy, dx) * 180 / Math.PI) + 90;
        if (degrees < 0)
            degrees += 360;

        foreach (var (start, sweep, slice) in _angles)
        {
            if (degrees >= start && degrees < start + sweep)
                return slice;
        }

        return null;
    }

    private int? LegendAt(Point point)
    {
        foreach (var (area, slice) in _legend)
        {
            if (area.Contains(point))
                return slice;
        }

        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var point = e.GetPosition(this);
        _pointer = point;

        var legend = LegendAt(point);
        var slice = legend is null ? SliceAt(point) : null;

        Cursor = legend is not null ? Cursors.Hand : null;

        if (slice != _hover && slice is { } now)
            Describe(now);

        _hover = slice ?? (legend is { } named && !_hidden.Contains(named) ? named : null);
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

        if (LegendAt(e.GetPosition(this)) is { } slice)
        {
            ToggleSlice(slice);
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
            case >= Key.D1 and <= Key.D9: ToggleSlice(e.Key - Key.D1); break;
            case >= Key.NumPad1 and <= Key.NumPad9: ToggleSlice(e.Key - Key.NumPad1); break;
            default: return;
        }

        e.Handled = true;
    }

    // ---- Drawing ----------------------------------------------------------------------------------------

    protected override void RenderChart(DrawingContext dc, Size size, ChartPalette palette)
    {
        _legend.Clear();
        _angles.Clear();

        var d = Data;

        if (d is null || !d.HasData)
        {
            DrawEmpty(dc, palette, new Rect(size), d?.EmptyText ?? "Nothing to show.");
            return;
        }

        // The ring on the left and the legend beside it; on a narrow card, the legend underneath.
        var wide = size.Width >= 380;
        var diameter = wide
            ? Math.Min(size.Height - 16, size.Width * 0.44)
            : Math.Min(size.Width - 24, size.Height * 0.55);

        diameter = Math.Max(60, diameter);
        _outer = (diameter / 2) - 6;
        _inner = _outer * 0.62;
        _centre = wide ? new Point(8 + (diameter / 2), size.Height / 2) : new Point(size.Width / 2, 8 + (diameter / 2));

        var active = ActiveSlice;
        var total = ShownTotal;
        var gap = d.Slices.Count(s => s.Value > 0) > 1 ? 1.4 : 0;
        var start = 0.0;

        for (var i = 0; i < d.Slices.Count; i++)
        {
            var share = ShareOf(i);

            if (share <= 0)
                continue;

            var sweep = share * 360;
            _angles.Add((start, sweep, i));

            var lifted = i == active;
            var outer = lifted ? _outer + 6 : _outer;
            var drawn = Math.Max(0.5, (sweep - gap) * Progress);
            var opacity = active is null || lifted ? 1 : 0.55;

            var brush = new LinearGradientBrush(
                ChartPalette.WithAlpha(palette.Series(d.Slices[i].Colour), opacity),
                ChartPalette.WithAlpha(ChartPalette.Lerp(palette.Series(d.Slices[i].Colour), Colors.Black, 0.25), opacity),
                new Point(0, 0),
                new Point(1, 1));
            brush.Freeze();

            dc.DrawGeometry(brush, null, ChartGeometry.RingSlice(_centre, outer, _inner, (start * Progress) + (gap / 2), drawn));
            start += sweep;
        }

        // The middle: the total, or the share of the slice being read.
        if (active is { } at && at < d.Slices.Count && !_hidden.Contains(at))
        {
            var share = Text(ChartFormat.Percent(ShareOf(at) * 100), Math.Max(14, _inner * 0.42), palette.Ink, bold: true);
            var name = Text(d.Slices[at].Label, 11.5, palette.Muted);
            name.MaxTextWidth = Math.Max(20, (_inner * 2) - 16);
            name.TextAlignment = TextAlignment.Center;
            name.MaxLineCount = 1;
            name.Trimming = TextTrimming.CharacterEllipsis;

            dc.DrawText(share, new Point(_centre.X - (share.Width / 2), _centre.Y - share.Height + 4));
            dc.DrawText(name, new Point(_centre.X - (name.MaxTextWidth / 2), _centre.Y + 4));
        }
        else
        {
            var figure = Text(ChartFormat.MoneyShort(total), Math.Max(13, _inner * 0.34), palette.Ink, bold: true);
            var caption = Text(d.Caption, 11.5, palette.Dim);

            if (figure.Width > (_inner * 2) - 10)
                figure = Text(ChartFormat.CompactMoney(total), Math.Max(13, _inner * 0.34), palette.Ink, bold: true);

            dc.DrawText(figure, new Point(_centre.X - (figure.Width / 2), _centre.Y - figure.Height + 5));
            dc.DrawText(caption, new Point(_centre.X - (caption.Width / 2), _centre.Y + 5));
        }

        DrawLegend(dc, d, palette, size, wide, diameter);

        // A tooltip for the mouse only: the keyboard already has the share in the middle and the
        // legend row picked out, and a card over the ring would hide the slice being read.
        if (_hover is { } hovered && _pointer is { } pointer && !_hidden.Contains(hovered) && SliceAt(pointer) == hovered)
        {
            var slice = d.Slices[hovered];
            DrawTooltip(dc, palette, new Rect(size), pointer, slice.Label,
            [
                new TooltipRow(palette.Series(slice.Colour), "Amount", d.ValueFormat(slice.Value)),
                new TooltipRow(null, "Share", ChartFormat.Percent(ShareOf(hovered) * 100)),
            ],
            string.IsNullOrWhiteSpace(slice.Detail) ? null : [slice.Detail]);
        }
    }

    private void DrawLegend(DrawingContext dc, DonutData d, ChartPalette palette, Size size, bool wide, double diameter)
    {
        var left = wide ? diameter + 26 : 12;
        var width = wide ? size.Width - left - 6 : size.Width - 24;
        var rowHeight = 26.0;
        var top = wide ? Math.Max(4, (size.Height - (d.Slices.Count * rowHeight)) / 2) : diameter + 18;
        var active = ActiveSlice;

        for (var i = 0; i < d.Slices.Count; i++)
        {
            var slice = d.Slices[i];
            var hidden = _hidden.Contains(i);
            var y = top + (i * rowHeight);
            var row = new Rect(left - 6, y, width + 6, rowHeight - 2);

            if (i == active && !hidden)
                dc.DrawRoundedRectangle(palette.SeriesBrush(0, 0.08), null, row, 5, 5);

            var colour = palette.Series(slice.Colour);
            var swatch = new SolidColorBrush(hidden ? ChartPalette.WithAlpha(colour, 0.35) : colour);
            swatch.Freeze();

            var middle = y + ((rowHeight - 2) / 2);
            var key = new Rect(left, middle - 5, 10, 10);

            if (hidden)
                dc.DrawRoundedRectangle(null, MakePen(swatch, 1.2), key, 2.5, 2.5);
            else
                dc.DrawRoundedRectangle(swatch, null, key, 2.5, 2.5);

            var share = Text(hidden ? "hidden" : ChartFormat.Percent(ShareOf(i) * 100), 11.5, palette.Dim, figures: !hidden);
            var amount = Text(d.ValueFormat(slice.Value), 12, hidden ? palette.Dim : palette.Ink, bold: !hidden, figures: true);
            var name = Text(slice.Label, 12.5, hidden ? palette.Dim : palette.Ink);

            var shareX = left + width - share.Width;
            var amountX = shareX - 12 - amount.Width;

            name.MaxTextWidth = Math.Max(20, amountX - (left + 18) - 8);
            name.MaxLineCount = 1;
            name.Trimming = TextTrimming.CharacterEllipsis;

            dc.DrawText(name, new Point(left + 18, middle - (name.Height / 2)));
            dc.DrawText(amount, new Point(amountX, middle - (amount.Height / 2)));
            dc.DrawText(share, new Point(shareX, middle - (share.Height / 2)));

            _legend.Add((row, i));
        }
    }
}
