using System.Data;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Pos.App.Charts;

/// <summary>
/// A grid of squares shaded by how much each holds - the days of the week against the hours - so the
/// busy evenings and the dead afternoons show at a glance.
/// </summary>
/// <remarks>
/// Pointing at a square, or moving onto it with the arrow keys, outlines it and gives its figures.
/// A square big enough to hold its amount writes it in, dark on the bright squares and light on the
/// dim ones, so it can be read from across the room as well as hovered.
/// </remarks>
public sealed class HeatmapChart : ChartSurface
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(HeatmapData), typeof(HeatmapChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((HeatmapChart)d).OnDataChanged()));

    private Rect _grid;
    private double _cellWidth;
    private double _cellHeight;
    private (int Row, int Column)? _hover;
    private (int Row, int Column)? _focused;
    private Point? _pointer;

    public HeatmapData? Data
    {
        get => (HeatmapData?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public (int Row, int Column)? ActiveCell => _hover ?? _focused;

    private void OnDataChanged()
    {
        _hover = null;
        _focused = null;
        ActiveDescription = string.Empty;
        Replay();
        OnFiguresChanged();
    }

    /// <summary>Moves the outline by a number of rows and columns, stopping at the edges.</summary>
    public void Move(int rows, int columns)
    {
        if (Data is not { } d || d.RowLabels.Count == 0 || d.ColumnLabels.Count == 0)
            return;

        var (row, column) = _focused ?? Busiest(d);
        _focused = (Math.Clamp(row + rows, 0, d.RowLabels.Count - 1), Math.Clamp(column + columns, 0, d.ColumnLabels.Count - 1));
        _hover = null;
        Describe(_focused.Value);
        InvalidateVisual();
    }

    private static (int Row, int Column) Busiest(HeatmapData d) =>
        d.Cells.Count == 0 ? (0, 0) : d.Cells.MaxBy(c => c.Value) is { } top ? (top.Row, top.Column) : (0, 0);

    private void Describe((int Row, int Column) at)
    {
        if (Data is not { } d)
            return;

        ActiveDescription = d.At(at.Row, at.Column) is { } cell
            ? $"{cell.Title}: {d.ValueFormat(cell.Value)}." + (string.IsNullOrWhiteSpace(cell.Detail) ? string.Empty : $" {cell.Detail}.")
            : $"{d.RowLabels[at.Row]}, {d.ColumnLabels[at.Column]}: nothing.";
    }

    public override string Summary
    {
        get
        {
            if (Data is not { HasData: true } d)
                return $"{Title}. {Data?.EmptyText}";

            var busiest = d.Cells.MaxBy(c => c.Value)!;
            return $"{Title}. The busiest is {busiest.Title}, {d.ValueFormat(busiest.Value)}. The arrow keys move from square to square.";
        }
    }

    public override DataTable ToTable()
    {
        var table = new DataTable(Title);

        if (Data is not { } d)
            return table;

        table.Columns.Add(" ", typeof(string));

        foreach (var column in d.ColumnLabels)
            table.Columns.Add(table.Columns.Contains(column) ? column + " " : column, typeof(string));

        for (var r = 0; r < d.RowLabels.Count; r++)
        {
            var row = table.NewRow();
            row[0] = d.RowLabels[r];

            for (var c = 0; c < d.ColumnLabels.Count; c++)
                row[c + 1] = d.At(r, c) is { Value: > 0 } cell ? d.ValueFormat(cell.Value) : string.Empty;

            table.Rows.Add(row);
        }

        return table;
    }

    // ---- Mouse and keyboard ---------------------------------------------------------------------------

    private (int Row, int Column)? CellAt(Point point)
    {
        if (Data is not { } d || !_grid.Contains(point) || _cellWidth <= 0 || _cellHeight <= 0)
            return null;

        var column = Math.Clamp((int)((point.X - _grid.Left) / _cellWidth), 0, d.ColumnLabels.Count - 1);
        var row = Math.Clamp((int)((point.Y - _grid.Top) / _cellHeight), 0, d.RowLabels.Count - 1);
        return (row, column);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var point = e.GetPosition(this);
        _pointer = point;

        var cell = CellAt(point);

        if (cell != _hover && cell is { } now)
            Describe(now);

        _hover = cell;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        _pointer = null;
        InvalidateVisual();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        if (_focused is null && Data is { HasData: true } d)
        {
            _focused = Busiest(d);
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

        if (e.Handled || Data is null || Keyboard.Modifiers != ModifierKeys.None)
            return;

        switch (e.Key)
        {
            case Key.Left: Move(0, -1); break;
            case Key.Right: Move(0, 1); break;
            case Key.Up: Move(-1, 0); break;
            case Key.Down: Move(1, 0); break;
            case Key.Home: Move(0, -Data.ColumnLabels.Count); break;
            case Key.End: Move(0, Data.ColumnLabels.Count); break;
            default: return;
        }

        e.Handled = true;
    }

    // ---- Drawing ----------------------------------------------------------------------------------------

    protected override void RenderChart(DrawingContext dc, Size size, ChartPalette palette)
    {
        var d = Data;

        if (d is null || d.RowLabels.Count == 0 || d.ColumnLabels.Count == 0)
        {
            DrawEmpty(dc, palette, new Rect(size), d?.EmptyText ?? "Nothing to show.");
            return;
        }

        var rowLabels = d.RowLabels.Select(l => Text(l, 11.5, palette.Muted)).ToList();
        var left = Math.Ceiling(rowLabels.Max(l => l.Width)) + 12;
        const double keyWidth = 56;
        const double bottom = 22;

        _grid = new Rect(left, 4, Math.Max(10, size.Width - left - keyWidth - 6), Math.Max(10, size.Height - 4 - bottom));
        _cellWidth = _grid.Width / d.ColumnLabels.Count;
        _cellHeight = _grid.Height / d.RowLabels.Count;

        var max = d.Max;
        var active = ActiveCell;
        const double inset = 1.5;

        for (var r = 0; r < d.RowLabels.Count; r++)
        {
            var label = rowLabels[r];
            dc.DrawText(label, new Point(left - 10 - label.Width, _grid.Top + (r * _cellHeight) + ((_cellHeight - label.Height) / 2)));

            for (var c = 0; c < d.ColumnLabels.Count; c++)
            {
                var cell = d.At(r, c);
                var share = max <= 0 || cell is null ? 0 : cell.Value / max;
                var colour = palette.Heat(share);

                // Fading in from the empty colour, busiest squares last to arrive.
                colour = ChartPalette.Lerp(palette.Heat(0), colour, Progress);

                var fill = new SolidColorBrush(colour);
                fill.Freeze();

                var square = new Rect(_grid.Left + (c * _cellWidth) + inset, _grid.Top + (r * _cellHeight) + inset,
                    Math.Max(1, _cellWidth - (2 * inset)), Math.Max(1, _cellHeight - (2 * inset)));

                dc.DrawRoundedRectangle(fill, null, square, 3, 3);

                if (cell is { Value: > 0 } && square.Width >= 44 && square.Height >= 20 && Progress > 0.6)
                {
                    var light = ChartPalette.Luminance(colour) > 0.35;
                    var figure = Text(d.CompactFormat(cell.Value), 10.5, light ? palette.Page : palette.Ink, bold: true);

                    if (figure.Width < square.Width - 6)
                        dc.DrawText(figure, new Point(square.X + ((square.Width - figure.Width) / 2), square.Y + ((square.Height - figure.Height) / 2)));
                }

                if (active is { } at && at.Row == r && at.Column == c)
                    dc.DrawRoundedRectangle(null, MakePen(palette.Ink, 2), square, 3, 3);
            }
        }

        // The hours along the bottom, as many as fit without touching.
        var widest = d.ColumnLabels.Max(l => Text(l, 11, palette.Dim).Width);
        var every = Math.Max(1, (int)Math.Ceiling((widest + 8) / _cellWidth));

        for (var c = 0; c < d.ColumnLabels.Count; c += every)
        {
            var label = Text(d.ColumnLabels[c], 11, palette.Dim);
            dc.DrawText(label, new Point(_grid.Left + (c * _cellWidth) + ((_cellWidth - label.Width) / 2), _grid.Bottom + 5));
        }

        DrawKey(dc, palette, d, size, keyWidth);

        if (!d.HasData)
        {
            DrawEmpty(dc, palette, _grid, d.EmptyText);
            return;
        }

        if (active is { } shown)
        {
            var cell = d.At(shown.Row, shown.Column);
            var centre = new Point(_grid.Left + ((shown.Column + 0.5) * _cellWidth), _grid.Top + ((shown.Row + 0.5) * _cellHeight));
            var anchor = _hover is not null && _pointer is { } pointer ? pointer : centre;

            var title = cell?.Title ?? $"{d.RowLabels[shown.Row]}, {d.ColumnLabels[shown.Column]}";
            var value = cell is null ? "nothing" : d.ValueFormat(cell.Value);

            DrawTooltip(dc, palette, new Rect(size), anchor, title,
                [new TooltipRow(palette.Heat(max <= 0 || cell is null ? 0 : cell.Value / max), d.ValueName, value)],
                cell is { Detail.Length: > 0 } ? [cell.Detail] : null);
        }
    }

    /// <summary>The scale down the right: what the brightest and the dimmest squares stand for.</summary>
    private void DrawKey(DrawingContext dc, ChartPalette palette, HeatmapData d, Size size, double keyWidth)
    {
        var bar = new Rect(size.Width - keyWidth + 8, _grid.Top + 16, 10, Math.Max(20, _grid.Height - 32));
        var gradient = new LinearGradientBrush { StartPoint = new Point(0, 1), EndPoint = new Point(0, 0) };

        for (var i = 0; i <= 10; i++)
            gradient.GradientStops.Add(new GradientStop(palette.Heat(i / 10.0), i / 10.0));

        gradient.Freeze();
        dc.DrawRoundedRectangle(gradient, null, bar, 3, 3);

        var high = Text(d.CompactFormat(d.Max), 10.5, palette.Dim);
        var low = Text(d.CompactFormat(0), 10.5, palette.Dim);

        dc.DrawText(high, new Point(bar.X + (bar.Width / 2) - (high.Width / 2), bar.Top - high.Height - 2));
        dc.DrawText(low, new Point(bar.X + (bar.Width / 2) - (low.Width / 2), bar.Bottom + 2));
    }
}
