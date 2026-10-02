using System.Globalization;
using Pos.App.Charts;
using Pos.Core.Analytics;

namespace Pos.App.ViewModels;

/// <summary>
/// The owner's figures turned into charts: what each one plots, what its tooltip says, and in what
/// colour. Nothing is counted here - every figure comes from the same gather the rest of the screen
/// and the saved web page read - so a chart can never disagree with the number printed beside it.
/// </summary>
public static class OwnerCharts
{
    /// <summary>
    /// Dates in the one spelling everywhere: "29 Sep". The Indian English culture writes September as
    /// "Sept", which put "29 Sept 2026" beside "29 Sep 2026" on the same screen.
    /// </summary>
    private static readonly CultureInfo Dates = CultureInfo.InvariantCulture;

    // The theme's chart colours, by what they mean.
    private const int Sky = 0;
    private const int Green = 1;
    private const int Amber = 2;
    private const int Violet = 3;
    private const int Rose = 4;
    private const int Teal = 5;
    private const int Slate = 6;

    /// <summary>"1 bill", "3 bills".</summary>
    public static string Plural(int count, string one, string? many = null) =>
        count == 1 ? $"1 {one}" : $"{count.ToString("N0", CultureInfo.GetCultureInfo("en-IN"))} {many ?? one + "s"}";

    /// <summary>
    /// Takings day by day, with a seven-day average over the top: a grocery's week has a shape, and
    /// the raw bars are mostly that shape repeating. The average shows whether the weeks are growing.
    /// </summary>
    public static CategoryChartData SalesTrend(IReadOnlyList<DailyPoint> days)
    {
        var takings = days.Select(d => (double)d.NetSales).ToList();
        var average = new List<double>(days.Count);

        for (var i = 0; i < days.Count; i++)
        {
            var from = Math.Max(0, i - 6);
            average.Add(takings.Skip(from).Take(i - from + 1).Average());
        }

        return new CategoryChartData(
            Labels: days.Select(d => d.Date.ToString("dd MMM", Dates)).ToList(),
            Titles: days.Select(d => d.Date.ToString("ddd d MMM yyyy", Dates)).ToList(),
            Series:
            [
                new ChartSeries("Takings", SeriesKind.Column, takings, Green),
                new ChartSeries("7-day average", SeriesKind.Line, average, Amber, Dashed: true),
            ],
            AxisFormat: ChartFormat.CompactMoney,
            ValueFormat: ChartFormat.Money,
            Notes: days.Select(d => Plural(d.Bills, "bill")
                                    + (d.Discount > 0m ? $" · {ChartFormat.Money((double)d.Discount)} off" : string.Empty)).ToList(),
            EmptyText: "No sales in this period.",
            CategoryName: "Day");
    }

    /// <summary>
    /// The shop's day, hour by hour: takings as bars, and how many bills on a second axis, because a
    /// busy hour of small baskets and a quiet hour of one big order look alike in rupees.
    /// </summary>
    /// <remarks>
    /// Trimmed to the hours the shop trades in, and never narrower than seven in the morning to nine
    /// at night, so a first day with two sales still looks like a day rather than two fat bars.
    /// </remarks>
    public static CategoryChartData Hours(IReadOnlyList<HourlyBucket> hours)
    {
        var trading = hours.Where(h => h.Bills > 0 || h.NetSales > 0m).Select(h => h.Hour).ToList();
        var first = Math.Min(trading.Count == 0 ? 7 : trading.Min(), 7);
        var last = Math.Max(trading.Count == 0 ? 21 : trading.Max(), 21);

        var shown = Enumerable.Range(first, last - first + 1)
            .Select(h => hours.FirstOrDefault(b => b.Hour == h) ?? new HourlyBucket(h, 0, 0m))
            .ToList();

        return new CategoryChartData(
            Labels: shown.Select(h => h.Hour.ToString("00", Dates)).ToList(),
            Titles: shown.Select(h => $"{h.Hour:00}:00 to {(h.Hour + 1) % 24:00}:00").ToList(),
            Series:
            [
                new ChartSeries("Takings", SeriesKind.Column, shown.Select(h => (double)h.NetSales).ToList(), Sky),
                new ChartSeries("Bills", SeriesKind.Line, shown.Select(h => (double)h.Bills).ToList(), Violet,
                    ChartAxis.Secondary, v => ChartFormat.Count(v)),
            ],
            AxisFormat: ChartFormat.CompactMoney,
            ValueFormat: ChartFormat.Money,
            Notes: shown.Select(h => h.Bills == 0 ? string.Empty : $"{ChartFormat.Money((double)(h.NetSales / h.Bills))} a bill").ToList(),
            SecondaryAxisFormat: ChartFormat.CompactCount,
            EmptyText: "No sales in this period.",
            CategoryName: "Hour");
    }

    private static readonly string[] Weekdays = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];

    /// <summary>
    /// The week against the hours in two-hour bands, shaded by takings: which evenings are busy and
    /// which afternoons are dead, the time a delivery or a stock count fits.
    /// </summary>
    public static HeatmapData Week(IReadOnlyList<WeekdayHourCell> cells)
    {
        var sold = cells.Where(c => c.NetSales > 0m).Select(c => c.HourBand).ToList();
        var first = Math.Max(0, Math.Min(sold.Count == 0 ? 8 : sold.Min() - 2, 8));
        var last = Math.Min(22, Math.Max(sold.Count == 0 ? 20 : sold.Max() + 2, 20));
        first -= first % 2;

        var bands = new List<int>();
        for (var band = first; band <= last; band += 2)
            bands.Add(band);

        var heat = new List<HeatCell>();

        foreach (var cell in cells.Where(c => c.NetSales > 0m || c.Bills > 0))
        {
            var column = bands.IndexOf(cell.HourBand);

            if (column < 0 || cell.Weekday < 1 || cell.Weekday > 7)
                continue;

            heat.Add(new HeatCell(
                cell.Weekday - 1,
                column,
                (double)cell.NetSales,
                $"{Weekdays[cell.Weekday - 1]}, {cell.HourBand:00}:00 to {(cell.HourBand + 2) % 24:00}:00",
                Plural(cell.Bills, "bill")));
        }

        return new HeatmapData(
            RowLabels: Weekdays.Select(d => d[..3]).ToList(),
            ColumnLabels: bands.Select(b => b.ToString("00", Dates)).ToList(),
            Cells: heat,
            ValueFormat: ChartFormat.Money,
            CompactFormat: ChartFormat.CompactMoney,
            EmptyText: "No sales in this period.",
            ValueName: "Takings");
    }

    /// <summary>How customers paid, each tender in the colour that says where its money went.</summary>
    public static DonutData Tenders(IReadOnlyList<TenderSlice> tenders) =>
        new(
            tenders
                .Where(t => t.Amount > 0m)
                .OrderByDescending(t => t.Amount)
                .Select(t => new DonutSlice(t.Tender, (double)t.Amount, Plural(t.Count, "bill"), TenderColour(t.Tender)))
                .ToList(),
            "taken",
            ChartFormat.Money,
            "Nothing taken in this period.");

    /// <summary>
    /// Cash in the money colour, UPI in the accent, owed on the khata in amber like money not yet in
    /// hand, points in rose; anything else in the quiet slate.
    /// </summary>
    private static int TenderColour(string tender) => tender.ToUpperInvariant() switch
    {
        "CASH" => Green,
        "UPI" => Sky,
        "CARD" => Violet,
        var t when t.Contains("CREDIT", StringComparison.Ordinal) || t.Contains("KHATA", StringComparison.Ordinal) => Amber,
        var t when t.Contains("POINT", StringComparison.Ordinal) => Rose,
        _ => Slate,
    };

    /// <summary>Where the takings came from, by department.</summary>
    public static DonutData Departments(IReadOnlyList<CategorySlice> categories)
    {
        int[] colours = [Sky, Green, Violet, Amber, Teal, Rose, Slate];

        return new DonutData(
            categories
                .Where(c => c.NetSales > 0m)
                .OrderByDescending(c => c.NetSales)
                .Select((c, i) => new DonutSlice(c.Category, (double)c.NetSales, Plural(c.Lines, "line"), colours[Math.Min(i, colours.Length - 1)]))
                .ToList(),
            "taken",
            ChartFormat.Money,
            "Nothing taken in this period.");
    }

    /// <summary>The four boxes of the margin map, in reading order: top left, top right, bottom left, bottom right.</summary>
    public static readonly IReadOnlyList<BubbleGroup> MarginGroups =
    [
        new("Hidden gems", "earn well, sell slowly", "worth better shelf space", Violet),
        new("Stars", "sell fast, earn well", "keep them in stock", Green),
        new("Dead weight", "slow and thin", "candidates for clearance", Rose),
        new("Volume drivers", "sell fast, earn little", "what brings people in", Sky),
    ];

    /// <summary>
    /// Every item that carried a cost price, placed by how much sold against what it kept, drawn as
    /// big as what it took, and split at the shop's own middle on each - half its items sell faster
    /// than the upright line, half earn more than the level one.
    /// </summary>
    public static BubbleData Margins(MarginPicture margins)
    {
        var medianQuantity = (double)margins.MedianQuantity;
        var medianMargin = (double)margins.MedianMargin;

        var points = margins.Priced
            .Select(item =>
            {
                var fast = (double)item.Quantity >= medianQuantity;
                var well = (double)item.MarginPercent >= medianMargin;
                var group = (fast, well) switch
                {
                    (false, true) => 0,
                    (true, true) => 1,
                    (false, false) => 2,
                    _ => 3,
                };

                return new BubblePoint(
                    item.Name,
                    (double)item.Quantity,
                    (double)item.MarginPercent,
                    (double)item.NetSales,
                    [$"{ChartFormat.Money((double)item.NetSales)} taken", $"{ChartFormat.Money((double)item.Profit)} kept"],
                    group);
            })
            .ToList();

        return new BubbleData(
            points,
            MarginGroups,
            "units sold",
            "margin",
            medianQuantity,
            medianMargin,
            ChartFormat.Quantity,
            ChartFormat.Percent,
            "No item sold in this period carried a cost price, so there is no margin to place.");
    }

    /// <summary>Loyalty points given and taken day by day: what the scheme is promising away.</summary>
    public static CategoryChartData Points(PointsFlow points) =>
        new(
            Labels: points.Daily.Select(d => d.Date.ToString("dd MMM", Dates)).ToList(),
            Titles: points.Daily.Select(d => d.Date.ToString("ddd d MMM yyyy", Dates)).ToList(),
            Series:
            [
                new ChartSeries("Earned", SeriesKind.Area, points.Daily.Select(d => (double)d.Earned).ToList(), Violet),
                new ChartSeries("Spent", SeriesKind.Line, points.Daily.Select(d => (double)d.Redeemed).ToList(), Rose),
            ],
            AxisFormat: ChartFormat.CompactCount,
            ValueFormat: v => Plural((int)Math.Round(v), "point"),
            EmptyText: "No points were earned or spent in this period.",
            CategoryName: "Day");

    /// <summary>What one customer spent, month by month, over the last year.</summary>
    public static CategoryChartData CustomerMonths(IReadOnlyList<MonthlySpend> months) =>
        new(
            Labels: months.Select(m => m.Month.ToString("MMM", Dates)).ToList(),
            Titles: months.Select(m => m.Month.ToString("MMMM yyyy", Dates)).ToList(),
            Series: [new ChartSeries("Spent", SeriesKind.Column, months.Select(m => (double)m.Spent).ToList(), Sky)],
            AxisFormat: ChartFormat.CompactMoney,
            ValueFormat: ChartFormat.Money,
            Notes: months.Select(m => m.Bills == 0 ? "Not in this month" : Plural(m.Bills, "bill")).ToList(),
            EmptyText: "Nothing bought in the last year.",
            CategoryName: "Month");

    /// <summary>
    /// The drawer at each close: over above the line in green, short below it in rose, nothing for a
    /// close that was not counted. Two series rather than one, so the colour says which way it went
    /// as well as the side of the line.
    /// </summary>
    public static CategoryChartData Drawers(Pos.Core.Analytics.DrawerCounts drawers) =>
        new(
            Labels: drawers.Closes.Select(c => c.ClosedAt.ToString("dd MMM", Dates)).ToList(),
            Titles: drawers.Closes.Select(c => $"Report {c.ReportId}, closed {c.ClosedAt.ToString("ddd d MMM yyyy, HH:mm", Dates)}").ToList(),
            Series:
            [
                new ChartSeries("Over", SeriesKind.Column, drawers.Closes.Select(c => (double)Math.Max(0m, c.Difference ?? 0m)).ToList(), Green),
                new ChartSeries("Short", SeriesKind.Column, drawers.Closes.Select(c => (double)Math.Min(0m, c.Difference ?? 0m)).ToList(), Rose),
            ],
            AxisFormat: ChartFormat.CompactMoney,
            ValueFormat: v => ChartFormat.Money(Math.Abs(v)),
            Notes: drawers.Closes.Select(c => c.Counted is null
                ? "Not counted"
                : $"Counted {ChartFormat.Money((double)c.Counted.Value)} by {c.CountedBy ?? "nobody named"}, expected {ChartFormat.Money((double)c.Expected)}"
                  + (c.OnTheTill.Count > 0 ? $" · on the till: {string.Join(", ", c.OnTheTill)}" : string.Empty)).ToList(),
            EmptyText: "Every drawer counted in this period came out exactly right - or none was counted.",
            CategoryName: "Close");

    /// <summary>
    /// A festival's days, this year's takings beside last year's, counted from the day itself so the
    /// two weeks line up however far the festival moved.
    /// </summary>
    public static CategoryChartData Festival(FestivalComparison festival) =>
        new(
            Labels: festival.Days.Select(d => FestivalDayLabel(d.Offset, shortForm: true)).ToList(),
            Titles: festival.Days.Select(d => $"{FestivalDayLabel(d.Offset, shortForm: false)}: {d.ThisDate.ToString("ddd d MMM yyyy", Dates)} against {d.LastDate.ToString("ddd d MMM yyyy", Dates)}").ToList(),
            Series:
            [
                new ChartSeries($"{festival.ThisYear.Day.Year}", SeriesKind.Column, festival.Days.Select(d => (double)d.ThisSales).ToList(), Green),
                new ChartSeries($"{festival.LastYear.Day.Year}", SeriesKind.Column, festival.Days.Select(d => (double)d.LastSales).ToList(), Slate),
            ],
            AxisFormat: ChartFormat.CompactMoney,
            ValueFormat: ChartFormat.Money,
            Notes: festival.Days.Select(d => $"{Plural(d.ThisBills, "bill")} against {Plural(d.LastBills, "bill")}").ToList(),
            EmptyText: "No sales on these days in either year.",
            CategoryName: "Day");

    /// <summary>"3 before", "the day", "1 after" - or written out for the tooltip.</summary>
    public static string FestivalDayLabel(int offset, bool shortForm) => offset switch
    {
        0 => shortForm ? "Day" : "The day itself",
        < 0 => shortForm ? $"−{-offset}" : $"{Plural(-offset, "day")} before",
        _ => shortForm ? $"+{offset}" : $"{Plural(offset, "day")} after",
    };

    /// <summary>Takings day by day, for the small line under the period's total.</summary>
    public static IReadOnlyList<double> Spark(IReadOnlyList<DailyPoint> days) =>
        days.Select(d => (double)d.NetSales).ToList();
}
