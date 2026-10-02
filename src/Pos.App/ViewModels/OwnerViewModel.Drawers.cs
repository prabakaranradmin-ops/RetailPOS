using System.Collections.ObjectModel;
using Pos.App.Charts;
using Pos.Core.Analytics;
using Pos.Core.Domain;

namespace Pos.App.ViewModels;

/// <summary>One person's days at the till over the period, as the owner's screen shows them.</summary>
public sealed record DrawerPersonRow(string Name, string Days, string Short, string Over);

/// <summary>One close, as the owner's screen lists it.</summary>
public sealed record DrawerCloseRow(DateTimeOffset ClosedAt, string Report, string Expected, string Counted, string Result, string OnTheTill);

/// <summary>
/// The drawer at each close, over and short, and how it came out on each person's days.
/// </summary>
public sealed partial class OwnerViewModel
{
    private string _drawersLine = string.Empty;

    public CategoryChartData? DrawerChart { get; private set; }

    public ObservableCollection<DrawerPersonRow> DrawersByPerson { get; } = [];

    public ObservableCollection<DrawerCloseRow> DrawerCloses { get; } = [];

    public string DrawersLine
    {
        get => _drawersLine;
        private set => Set(ref _drawersLine, value);
    }

    public bool HasDrawerCloses => DrawerCloses.Count > 0;

    private void FillDrawers(DashboardData d)
    {
        var drawers = d.Drawers;

        DrawersByPerson.Clear();
        DrawerCloses.Clear();

        foreach (var p in drawers.ByPerson)
        {
            DrawersByPerson.Add(new DrawerPersonRow(
                p.Name,
                p.Counted == p.Days
                    ? Plural.Of(p.Days, "day")
                    : $"{Plural.Of(p.Days, "day")}, {p.Counted.ToString("N0", Indian)} counted",
                p.ShortDays == 0 ? "—" : $"{Plural.Of(p.ShortDays, "day")} · {Show.Money(p.Short)}",
                p.OverDays == 0 ? "—" : $"{Plural.Of(p.OverDays, "day")} · {Show.Money(p.Over)}"));
        }

        // Newest first in the list; the chart runs oldest to newest, left to right.
        foreach (var c in drawers.Closes.Reverse())
        {
            DrawerCloses.Add(new DrawerCloseRow(
                c.ClosedAt,
                $"Report {c.ReportId}",
                Show.Money(c.Expected),
                c.Counted is { } counted ? $"{Show.Money(counted)}{(c.CountedBy is { } by ? $" by {by}" : string.Empty)}" : "not counted",
                c.Difference switch
                {
                    null => string.Empty,
                    0m => "exactly right",
                    > 0m => $"over by {Show.Money(c.Difference.Value)}",
                    _ => $"short by {Show.Money(-c.Difference.Value)}",
                },
                c.OnTheTill.Count == 0 ? "—" : string.Join(", ", c.OnTheTill)));
        }

        DrawerChart = OwnerCharts.Drawers(drawers);
        DrawersLine = SummariseDrawers(drawers);

        Raise(nameof(DrawerChart));
        Raise(nameof(HasDrawerCloses));
    }

    private static string SummariseDrawers(DrawerCounts drawers)
    {
        if (drawers.Closes.Count == 0)
            return "No day was closed in this period.";

        if (drawers.CountedCloses == 0)
            return $"{Plural.Of(drawers.Closes.Count, "close")} in this period, none counted. The till asks for the count at every close: Shift+F12, count, type it, Enter.";

        var said = new List<string>
        {
            $"Counted at {drawers.CountedCloses.ToString("N0", Indian)} of {Plural.Of(drawers.Closes.Count, "close")}",
        };

        said.Add(drawers.ShortCloses == 0 ? "never short" : $"short {Times(drawers.ShortCloses)}, {Show.Money(drawers.ShortTotal)} in all");
        said.Add(drawers.OverCloses == 0 ? "never over" : $"over {Times(drawers.OverCloses)}, {Show.Money(drawers.OverTotal)} in all");

        return string.Join("; ", said) + ".";

        static string Times(int count) => count switch
        {
            1 => "once",
            2 => "twice",
            _ => $"{count.ToString("N0", Indian)} times",
        };
    }
}
