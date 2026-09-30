using System.Globalization;
using System.Windows.Data;

namespace Pos.App.Views;

/// <summary>
/// Dates and times in a binding, written the one way <see cref="Show"/> writes them.
/// </summary>
/// <remarks>
/// <para>
/// The windows are set to Indian English so that figures group the Indian way - 1,23,456.00 - and
/// Indian English spells September "Sept". A date written with a format string in a binding would
/// then say "29 Sept" on one screen and "29 Sep" on another, so dates in bindings come through here.
/// </para>
/// <para>
/// The parameter says how much: <c>date</c> (30 Sep 2026, the default), <c>day</c> (30 Sep),
/// <c>time</c> (14:05), <c>dateTime</c> (30 Sep 2026, 14:05) or <c>dayTime</c> (30 Sep, 14:05).
/// </para>
/// </remarks>
public sealed class ShowConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        DateTimeOffset? at = value switch
        {
            DateTimeOffset offset => offset,
            DateTime time => new DateTimeOffset(time),
            DateOnly date => new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue)),
            _ => null,
        };

        if (at is not { } when)
            return string.Empty;

        return (parameter as string) switch
        {
            "day" => when.ToString("d MMM", CultureInfo.InvariantCulture),
            "time" => Show.Time(when),
            "dateTime" => Show.DateAndTime(when),
            "dayTime" => $"{when.ToString("d MMM", CultureInfo.InvariantCulture)}, {Show.Time(when)}",
            _ => Show.Date(when),
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
