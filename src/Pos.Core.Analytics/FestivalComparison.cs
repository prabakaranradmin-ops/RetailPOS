namespace Pos.Core.Analytics;

/// <summary>The days around a festival: so many before it, the day itself, so many after.</summary>
public sealed record FestivalWindow(DateOnly Day, int DaysBefore, int DaysAfter)
{
    /// <summary>The most days either side a comparison looks at: a month and a half around the day.</summary>
    public const int MostDaysEitherSide = 45;

    public DateOnly First => Day.AddDays(-DaysBefore);

    /// <summary>The last day in it, included.</summary>
    public DateOnly Last => Day.AddDays(DaysAfter);

    public int Days => DaysBefore + DaysAfter + 1;

    /// <summary>What is wrong with it, or null.</summary>
    public string? Problem() =>
        DaysBefore < 0 || DaysAfter < 0 ? "Days before and after cannot be fewer than none."
        : DaysBefore > MostDaysEitherSide || DaysAfter > MostDaysEitherSide ? $"At most {MostDaysEitherSide} days either side of the day."
        : null;
}

/// <summary>One day of the window, this year's beside last year's, counted from the festival day.</summary>
/// <param name="Offset">Days from the festival: -2 two days before, 0 the day itself.</param>
public sealed record FestivalDay(int Offset, DateOnly ThisDate, DateOnly LastDate, decimal ThisSales, decimal LastSales, int ThisBills, int LastBills);

public sealed record FestivalCategory(string Category, decimal ThisSales, decimal LastSales);

public sealed record FestivalItem(string Name, string Unit, decimal ThisQuantity, decimal LastQuantity, decimal ThisSales, decimal LastSales);

/// <summary>
/// A festival this year against the same festival last year, day for day from the festival itself.
/// </summary>
/// <remarks>
/// Counted from the day rather than by the calendar: Deepavali moves by weeks from one year to the
/// next, and the week before it is what is being compared, whatever dates it fell on. The owner says
/// which day it was each year; a fixed festival such as Pongal is the same date, and that is offered.
/// </remarks>
public sealed record FestivalComparison(
    string Name,
    FestivalWindow ThisYear,
    FestivalWindow LastYear,
    Kpis This,
    Kpis Last,
    IReadOnlyList<FestivalDay> Days,
    IReadOnlyList<FestivalCategory> Categories,
    IReadOnlyList<FestivalItem> Items,
    IReadOnlyList<FestivalItem> NotThisYear)
{
    /// <summary>How many items the comparison reads from each window: enough that "not sold this time" is true.</summary>
    public const int ItemsRead = 2_000;

    /// <summary>The items this year shown, most first.</summary>
    public const int ItemsShown = 15;

    /// <summary>The items sold last time and not this time shown, most first.</summary>
    public const int MissingShown = 10;

    public bool HasAnything => This.Bills > 0 || Last.Bills > 0;

    /// <summary>The change from last year as a share of it, to one place; null when there was nothing last year.</summary>
    public static decimal? Change(decimal now, decimal then) =>
        then == 0m ? null : decimal.Round((now - then) / then * 100m, 1, MidpointRounding.ToEven);

    /// <summary>
    /// Lays two windows' figures side by side. Each is the dashboard's own gathering for that window,
    /// read with enough items that one missing from this year's list was truly not sold.
    /// </summary>
    public static FestivalComparison Of(string name, FestivalWindow thisYear, DashboardData thisData, FestivalWindow lastYear, DashboardData lastData)
    {
        ArgumentNullException.ThrowIfNull(thisYear);
        ArgumentNullException.ThrowIfNull(lastYear);
        ArgumentNullException.ThrowIfNull(thisData);
        ArgumentNullException.ThrowIfNull(lastData);

        if (thisYear.DaysBefore != lastYear.DaysBefore || thisYear.DaysAfter != lastYear.DaysAfter)
            throw new ArgumentException("Both years have to be the same days either side of the festival.", nameof(lastYear));

        var thisDaily = thisData.Daily.ToDictionary(d => d.Date);
        var lastDaily = lastData.Daily.ToDictionary(d => d.Date);

        var days = Enumerable.Range(-thisYear.DaysBefore, thisYear.Days)
            .Select(offset =>
            {
                var now = thisYear.Day.AddDays(offset);
                var then = lastYear.Day.AddDays(offset);
                var a = thisDaily.GetValueOrDefault(now);
                var b = lastDaily.GetValueOrDefault(then);
                return new FestivalDay(offset, now, then, a?.NetSales ?? 0m, b?.NetSales ?? 0m, a?.Bills ?? 0, b?.Bills ?? 0);
            })
            .ToList();

        var categories = thisData.Categories.Select(c => c.Category)
            .Union(lastData.Categories.Select(c => c.Category), StringComparer.OrdinalIgnoreCase)
            .Select(category => new FestivalCategory(
                category,
                thisData.Categories.Where(c => string.Equals(c.Category, category, StringComparison.OrdinalIgnoreCase)).Sum(c => c.NetSales),
                lastData.Categories.Where(c => string.Equals(c.Category, category, StringComparison.OrdinalIgnoreCase)).Sum(c => c.NetSales)))
            .OrderByDescending(c => c.ThisSales)
            .ThenByDescending(c => c.LastSales)
            .ThenBy(c => c.Category, StringComparer.OrdinalIgnoreCase)
            .ToList();

        static string Key(TopItem item) => $"{item.Name.Trim()}\u0001{item.Unit}";

        var lastItems = lastData.TopItems.GroupBy(Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var thisItems = thisData.TopItems.GroupBy(Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var items = thisData.TopItems
            .OrderByDescending(i => i.NetSales)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .Take(ItemsShown)
            .Select(i =>
            {
                var before = lastItems.GetValueOrDefault(Key(i));
                return new FestivalItem(i.Name, i.Unit, i.Quantity, before?.Quantity ?? 0m, i.NetSales, before?.NetSales ?? 0m);
            })
            .ToList();

        // What sold last time and not at all this time: the first thing to ask is whether it was on
        // the shelf.
        var missing = lastData.TopItems
            .Where(i => !thisItems.ContainsKey(Key(i)))
            .OrderByDescending(i => i.NetSales)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MissingShown)
            .Select(i => new FestivalItem(i.Name, i.Unit, 0m, i.Quantity, 0m, i.NetSales))
            .ToList();

        return new FestivalComparison(name, thisYear, lastYear, thisData.Range, lastData.Range, days, categories, items, missing);
    }
}
