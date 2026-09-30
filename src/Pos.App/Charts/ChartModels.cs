namespace Pos.App.Charts;

/// <summary>How a series is drawn against the categories.</summary>
public enum SeriesKind
{
    /// <summary>A bar per category. Several column series stand side by side in each slot.</summary>
    Column,

    /// <summary>A line through the categories.</summary>
    Line,

    /// <summary>A line with the space under it filled, fading towards the axis.</summary>
    Area,
}

/// <summary>Which vertical axis a series is measured against.</summary>
public enum ChartAxis
{
    /// <summary>The axis on the left.</summary>
    Primary,

    /// <summary>
    /// The axis on the right, for a series in other units - bills beside rupees - which would
    /// otherwise lie flat along the bottom of a scale built for the bigger numbers.
    /// </summary>
    Secondary,
}

/// <summary>One series of figures, a value for each category of the chart it is drawn on.</summary>
/// <param name="Name">What the legend and the tooltip call it.</param>
/// <param name="Colour">Which of the theme's chart colours it is drawn in, from 0.</param>
/// <param name="Format">How a value is written in the tooltip; the chart's own when null.</param>
/// <param name="Dashed">Drawn dashed: an average or a target, read against the solid figures.</param>
public sealed record ChartSeries(
    string Name,
    SeriesKind Kind,
    IReadOnlyList<double> Values,
    int Colour = 0,
    ChartAxis Axis = ChartAxis.Primary,
    Func<double, string>? Format = null,
    bool Dashed = false);

/// <summary>
/// Everything a category chart draws: what runs along the bottom, and the figures for each.
/// </summary>
/// <param name="Labels">What is written under each category - "29 Sep", "19".</param>
/// <param name="Titles">The same categories written out in full for the tooltip - "Tuesday 29 Sep 2026".</param>
/// <param name="Notes">
/// A line for each category under the figures in its tooltip - "3 bills, ₹49.00 off" - or null.
/// </param>
/// <param name="AxisFormat">How the left axis writes its figures.</param>
/// <param name="SecondaryAxisFormat">How the right axis writes its figures, when a series uses it.</param>
/// <param name="ValueFormat">How a value is written in the tooltip unless its series says otherwise.</param>
/// <param name="EmptyText">What the chart says when every figure is zero.</param>
/// <param name="CategoryName">What a category is, heading its column when the chart is read as a table: "Day".</param>
public sealed record CategoryChartData(
    IReadOnlyList<string> Labels,
    IReadOnlyList<string> Titles,
    IReadOnlyList<ChartSeries> Series,
    Func<double, string> AxisFormat,
    Func<double, string> ValueFormat,
    IReadOnlyList<string>? Notes = null,
    Func<double, string>? SecondaryAxisFormat = null,
    string EmptyText = "Nothing to show for this period.",
    string CategoryName = "Category")
{
    public int Count => Labels.Count;

    /// <summary>True when there is something other than zero to draw.</summary>
    public bool HasData => Series.Any(s => s.Values.Any(v => Math.Abs(v) > 1e-9));
}

/// <summary>One slice of a donut.</summary>
/// <param name="Detail">A line under the figures in its tooltip - "2 bills".</param>
public sealed record DonutSlice(string Label, double Value, string Detail, int Colour);

/// <summary>A whole divided into its parts: how customers paid, where the takings came from.</summary>
/// <param name="Caption">Written under the total in the middle - "taken".</param>
public sealed record DonutData(
    IReadOnlyList<DonutSlice> Slices,
    string Caption,
    Func<double, string> ValueFormat,
    string EmptyText = "Nothing taken in this period.")
{
    public double Total => Slices.Sum(s => Math.Max(0, s.Value));

    public bool HasData => Total > 1e-9;
}

/// <summary>One square of a heatmap.</summary>
/// <param name="Title">What its tooltip says it is - "Tuesday, 6 to 8 in the evening".</param>
/// <param name="Detail">A line under its figure - "3 bills".</param>
public sealed record HeatCell(int Row, int Column, double Value, string Title, string Detail);

/// <summary>A grid of squares shaded by how much each one holds: the week against the hours.</summary>
/// <param name="ValueName">What a square's figure is, in its tooltip: "Takings".</param>
public sealed record HeatmapData(
    IReadOnlyList<string> RowLabels,
    IReadOnlyList<string> ColumnLabels,
    IReadOnlyList<HeatCell> Cells,
    Func<double, string> ValueFormat,
    Func<double, string> CompactFormat,
    string EmptyText = "No sales in this period.",
    string ValueName = "Takings")
{
    public double Max => Cells.Count == 0 ? 0 : Cells.Max(c => c.Value);

    public bool HasData => Max > 1e-9;

    public HeatCell? At(int row, int column) => Cells.FirstOrDefault(c => c.Row == row && c.Column == column);
}

/// <summary>One item on the margin map.</summary>
/// <param name="X">How much of it sold.</param>
/// <param name="Y">What the shop kept, as a share of what it charged.</param>
/// <param name="Size">What it took, which decides how big it is drawn.</param>
/// <param name="Lines">Its tooltip, a line each, under its name.</param>
public sealed record BubblePoint(string Label, double X, double Y, double Size, IReadOnlyList<string> Lines, int Group);

/// <summary>One of the four boxes the margin map is split into.</summary>
/// <param name="Advice">What an owner might do about the items in it.</param>
public sealed record BubbleGroup(string Name, string Description, string Advice, int Colour);

/// <summary>
/// Items placed by how fast they sell against how much they earn, split into four at the shop's
/// own middle on each.
/// </summary>
/// <param name="Groups">
/// In reading order: top left, top right, bottom left, bottom right. A point's
/// <see cref="BubblePoint.Group"/> is an index into this list.
/// </param>
public sealed record BubbleData(
    IReadOnlyList<BubblePoint> Points,
    IReadOnlyList<BubbleGroup> Groups,
    string XTitle,
    string YTitle,
    double? XSplit,
    double? YSplit,
    Func<double, string> XFormat,
    Func<double, string> YFormat,
    string EmptyText = "No item sold in this period carried a cost price.")
{
    public bool HasData => Points.Count > 0;
}
