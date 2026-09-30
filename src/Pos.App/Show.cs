using System.Globalization;

namespace Pos.App;

/// <summary>
/// How the screens write money, dates and times: one way, everywhere.
/// </summary>
/// <remarks>
/// <para>
/// The screens used to write the same things several ways side by side: "2,000.00" in one message
/// and "2250.00" in the next, "29 Sep 2026" beside "29 Sept 2026" and "29-09-2026", 12-hour times
/// here and 24-hour ones there. Each was fine on its own; together they made a reader stop and
/// check whether two figures meant the same kind of thing.
/// </para>
/// <para>
/// Money is grouped the Indian way (₹1,23,456.50) with the rupee sign, which a screen can draw. Paper
/// keeps "Rs": not every thermal printer has the glyph. Dates are "30 Sep 2026" in every language
/// setting - the Indian English culture spells the month "Sept" - and times are 24-hour.
/// </para>
/// </remarks>
public static class Show
{
    private static readonly CultureInfo Indian = CultureInfo.GetCultureInfo("en-IN");

    /// <summary>A true minus, which lines up with the digits where a hyphen would not.</summary>
    public const string Minus = "−";

    /// <summary>₹1,23,456.50; −₹12.50.</summary>
    public static string Money(decimal value) =>
        (value < 0 ? Minus : string.Empty) + "₹" + Math.Abs(value).ToString("N2", Indian);

    /// <summary>1,23,456.50 - a figure in a column or beside a label that already says rupees.</summary>
    public static string Figure(decimal value) =>
        (value < 0 ? Minus : string.Empty) + Math.Abs(value).ToString("N2", Indian);

    /// <summary>30 Sep 2026.</summary>
    public static string Date(DateOnly date) => date.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    /// <inheritdoc cref="Date(DateOnly)"/>
    public static string Date(DateTimeOffset at) => at.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>14:05.</summary>
    public static string Time(DateTimeOffset at) => at.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>30 Sep 2026, 14:05.</summary>
    public static string DateAndTime(DateTimeOffset at) => $"{Date(at)}, {Time(at)}";
}
