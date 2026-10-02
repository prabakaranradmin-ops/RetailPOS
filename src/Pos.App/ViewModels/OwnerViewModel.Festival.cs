using System.Collections.ObjectModel;
using System.Globalization;
using Pos.App.Charts;
using Pos.Core.Analytics;
using Pos.Core.Domain;

namespace Pos.App.ViewModels;

/// <summary>One day around the festival, this year's beside last year's.</summary>
public sealed record FestivalDayRow(string Day, string ThisYear, string ThisSales, string LastYear, string LastSales, string Change);

/// <summary>A department or an item, this festival against the last.</summary>
public sealed record FestivalCompareRow(string Name, string ThisYear, string LastYear, string Change);

/// <summary>
/// A festival this year against the same festival last year: the takings, the bills, the basket, each
/// day counted from the festival itself, the departments, and what sold.
/// </summary>
/// <remarks>
/// The owner says which day the festival fell on each year, because the ones that matter most to a
/// grocery - Deepavali, Ayudha Pooja, Vinayagar Chathurthi - move by weeks from one year to the next,
/// and a calendar of them built into the till would be wrong the year nobody updated it. A festival on
/// a fixed date is offered as the same date last year.
/// </remarks>
public sealed partial class OwnerViewModel
{
    private Func<DateTimeOffset, DateTimeOffset, int, DashboardData>? _festivalGather;
    private DateOnly _festivalToday;
    private string _festivalName = string.Empty;
    private string _festivalDay = string.Empty;
    private string _festivalLastDay = string.Empty;
    private string _festivalBefore = "7";
    private string _festivalAfter = "1";
    private string _festivalStatus = string.Empty;
    private FestivalComparison? _festival;

    /// <summary>
    /// Gives the screen what it needs to compare a festival: the dashboard's own gathering, for any
    /// window, and how many items to read.
    /// </summary>
    public void UseFestivals(Func<DateTimeOffset, DateTimeOffset, int, DashboardData> gather, DateOnly? today = null)
    {
        _festivalGather = gather ?? throw new ArgumentNullException(nameof(gather));
        _festivalToday = today ?? DateOnly.FromDateTime(DateTime.Today);

        FestivalDay = Day(_festivalToday);
        FestivalLastDay = Day(_festivalToday.AddYears(-1));
        Raise(nameof(CanCompareFestivals));
    }

    public bool CanCompareFestivals => _festivalGather is not null;

    /// <summary>What it is called, for the line that sums it up. Optional.</summary>
    public string FestivalName
    {
        get => _festivalName;
        set => Set(ref _festivalName, value);
    }

    /// <summary>The festival day this year, as typed: 08-11-2026.</summary>
    public string FestivalDay
    {
        get => _festivalDay;
        set
        {
            if (!Set(ref _festivalDay, value))
                return;

            // A festival on a fixed date falls on the same date last year; offered, and typed over
            // for one that moves.
            if (TryDay(value, out var day))
                FestivalLastDay = Day(day.AddYears(-1));
        }
    }

    /// <summary>The same festival's day last year, as typed.</summary>
    public string FestivalLastDay
    {
        get => _festivalLastDay;
        set => Set(ref _festivalLastDay, value);
    }

    public string FestivalDaysBefore
    {
        get => _festivalBefore;
        set => Set(ref _festivalBefore, value);
    }

    public string FestivalDaysAfter
    {
        get => _festivalAfter;
        set => Set(ref _festivalAfter, value);
    }

    /// <summary>Why it could not be compared, or what to bear in mind about what was.</summary>
    public string FestivalStatus
    {
        get => _festivalStatus;
        private set => Set(ref _festivalStatus, value);
    }

    public bool HasFestival => _festival is { HasAnything: true };

    /// <summary>The comparison in a line: takings, bills and the basket, each against last year.</summary>
    public string FestivalLine { get; private set; } = string.Empty;

    public CategoryChartData? FestivalChart { get; private set; }

    public ObservableCollection<FestivalDayRow> FestivalDays { get; } = [];

    public ObservableCollection<FestivalCompareRow> FestivalDepartments { get; } = [];

    public ObservableCollection<FestivalCompareRow> FestivalItems { get; } = [];

    /// <summary>What sold last festival and not this one: worth asking whether it was on the shelf.</summary>
    public ObservableCollection<FestivalCompareRow> FestivalMissing { get; } = [];

    public bool HasFestivalMissing => FestivalMissing.Count > 0;

    /// <summary>Reads both years' days from the books and lays them side by side.</summary>
    /// <returns>True when there was something to compare.</returns>
    public bool CompareFestival()
    {
        if (_festivalGather is null)
            return Refuse("This screen cannot compare festivals.");

        if (!TryDay(_festivalDay, out var day))
            return Refuse($"'{_festivalDay.Trim()}' is not a date. Type the festival day as 08-11-2026.");

        if (!TryDay(_festivalLastDay, out var lastDay))
            return Refuse($"'{_festivalLastDay.Trim()}' is not a date. Type last year's festival day as 20-10-2025.");

        if (!int.TryParse(_festivalBefore.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var before)
            || !int.TryParse(_festivalAfter.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var after))
            return Refuse("Days before and after are whole numbers: 7 before, 1 after.");

        var thisYear = new FestivalWindow(day, before, after);
        var lastYear = new FestivalWindow(lastDay, before, after);

        if (thisYear.Problem() is { } problem)
            return Refuse(problem);

        if (lastYear.Last >= thisYear.First)
            return Refuse("Last year's days have to end before this year's begin. Check the two dates.");

        if (thisYear.First > _festivalToday)
            return Refuse($"The days from {Show.Date(thisYear.First)} have not come yet.");

        FestivalComparison festival;

        try
        {
            festival = FestivalComparison.Of(
                _festivalName.Trim(),
                thisYear, _festivalGather(Midnight(thisYear.First), Midnight(thisYear.Last.AddDays(1)), FestivalComparison.ItemsRead),
                lastYear, _festivalGather(Midnight(lastYear.First), Midnight(lastYear.Last.AddDays(1)), FestivalComparison.ItemsRead));
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            return Refuse($"The figures could not be read: {ex.Message}");
        }

        FillFestival(festival);

        FestivalStatus = !festival.HasAnything
            ? "No sales on these days in either year."
            : thisYear.Last >= _festivalToday
                ? $"This year's days run to {Show.Date(thisYear.Last)}: the figures will grow until then."
                : string.Empty;

        return festival.HasAnything;
    }

    private bool Refuse(string why)
    {
        FillFestival(null);
        FestivalStatus = why;
        return false;
    }

    private void FillFestival(FestivalComparison? festival)
    {
        _festival = festival;

        FestivalDays.Clear();
        FestivalDepartments.Clear();
        FestivalItems.Clear();
        FestivalMissing.Clear();

        if (festival is not null)
        {
            foreach (var d in festival.Days)
            {
                FestivalDays.Add(new FestivalDayRow(
                    OwnerCharts.FestivalDayLabel(d.Offset, shortForm: false),
                    d.ThisDate.ToString("ddd d MMM", CultureInfo.InvariantCulture),
                    Show.Money(d.ThisSales),
                    d.LastDate.ToString("ddd d MMM", CultureInfo.InvariantCulture),
                    Show.Money(d.LastSales),
                    Change(d.ThisSales, d.LastSales)));
            }

            foreach (var c in festival.Categories)
                FestivalDepartments.Add(new FestivalCompareRow(c.Category, Show.Money(c.ThisSales), Show.Money(c.LastSales), Change(c.ThisSales, c.LastSales)));

            foreach (var i in festival.Items)
            {
                FestivalItems.Add(new FestivalCompareRow(
                    i.Name,
                    $"{Quantity(i.ThisQuantity, i.Unit)} · {Show.Money(i.ThisSales)}",
                    i.LastQuantity == 0m ? "none" : $"{Quantity(i.LastQuantity, i.Unit)} · {Show.Money(i.LastSales)}",
                    Change(i.ThisSales, i.LastSales)));
            }

            foreach (var i in festival.NotThisYear)
                FestivalMissing.Add(new FestivalCompareRow(i.Name, "none", $"{Quantity(i.LastQuantity, i.Unit)} · {Show.Money(i.LastSales)}", "−100%"));
        }

        FestivalLine = festival is { HasAnything: true } ? Summarise(festival) : string.Empty;
        FestivalChart = festival is { HasAnything: true } ? OwnerCharts.Festival(festival) : null;

        Raise(nameof(FestivalLine));
        Raise(nameof(FestivalChart));
        Raise(nameof(HasFestival));
        Raise(nameof(HasFestivalMissing));
    }

    private static string Summarise(FestivalComparison f)
    {
        var name = f.Name.Length > 0 ? $"{f.Name} {f.ThisYear.Day.Year}" : $"The {f.ThisYear.Day.Year} festival";
        var window = $"{Plural.Of(f.ThisYear.DaysBefore, "day")} before to {Plural.Of(f.ThisYear.DaysAfter, "day")} after";

        return $"{name}, {window}: {Show.Money(f.This.NetSales)} against {Show.Money(f.Last.NetSales)} in {f.LastYear.Day.Year}, {Said(f.This.NetSales, f.Last.NetSales)}. "
             + $"{Plural.Of(f.This.Bills, "bill")} against {f.Last.Bills.ToString("N0", Indian)}, {Said(f.This.Bills, f.Last.Bills)}; "
             + $"a basket of {Show.Money(f.This.AverageBasket)} against {Show.Money(f.Last.AverageBasket)}.";

        static string Said(decimal now, decimal then) => FestivalComparison.Change(now, then) switch
        {
            null => "with nothing to compare it with",
            0m => "the same",
            > 0m and var up => $"up {up.ToString("0.0", CultureInfo.InvariantCulture)}%",
            var down => $"down {(-down.Value).ToString("0.0", CultureInfo.InvariantCulture)}%",
        };
    }

    /// <summary>"+12.5%", "−4.0%", "new" when there was nothing last year, "—" when there is nothing either year.</summary>
    private static string Change(decimal now, decimal then) => FestivalComparison.Change(now, then) switch
    {
        null => now == 0m ? "—" : "new",
        var change => (change >= 0m ? "+" : "−") + Math.Abs(change.Value).ToString("0.0", CultureInfo.InvariantCulture) + "%",
    };

    private static string Quantity(decimal quantity, string unit) =>
        $"{quantity.ToString("0.###", CultureInfo.InvariantCulture)} {unit}";

    private static string Day(DateOnly day) => day.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);

    private static bool TryDay(string text, out DateOnly day) =>
        DateOnly.TryParseExact(text.Trim(), ["dd-MM-yyyy", "d-M-yyyy", "dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

    private static DateTimeOffset Midnight(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }
}
