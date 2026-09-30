using System.Globalization;

namespace Pos.App.Charts;

/// <summary>
/// How a chart writes its figures: rupees the Indian way, short on an axis and whole in a tooltip.
/// </summary>
/// <remarks>
/// <para>
/// Two forms, because the two places ask different things of a number. An axis label has a few
/// characters of room and only has to place a bar against a scale, so it says ₹12.5K. A tooltip is
/// where somebody reads the figure itself, so it says ₹12,480.50 to the paisa.
/// </para>
/// <para>
/// K, L and Cr rather than K, M and B: a shopkeeper counts in thousands, lakhs and crores, and a
/// chart that said ₹1.2M for twelve lakhs would be asking them to convert in their head.
/// </para>
/// </remarks>
public static class ChartFormat
{
    private static readonly CultureInfo Indian = CultureInfo.GetCultureInfo("en-IN");

    /// <summary>The rupee sign. On screen only; the thermal printer keeps "Rs".</summary>
    public const string Rupee = "₹";

    /// <summary>A true minus sign, which lines up with the digits where a hyphen would not.</summary>
    private const string Minus = "−";

    /// <summary>The whole figure, grouped the Indian way: ₹1,23,456.50.</summary>
    public static string Money(double value) =>
        (value < 0 ? Minus : string.Empty) + Rupee + Math.Abs(value).ToString("N2", Indian);

    /// <summary>The whole figure without the paise when there are none: ₹1,23,456 or ₹12.50.</summary>
    public static string MoneyShort(double value)
    {
        var abs = Math.Abs(value);
        var text = Math.Abs(abs - Math.Round(abs)) < 0.005 ? Math.Round(abs).ToString("N0", Indian) : abs.ToString("N2", Indian);
        return (value < 0 ? Minus : string.Empty) + Rupee + text;
    }

    /// <summary>A figure for an axis: ₹850, ₹12.5K, ₹3.4L, ₹1.2Cr.</summary>
    public static string CompactMoney(double value) => (value < 0 ? Minus : string.Empty) + Rupee + Compact(Math.Abs(value));

    /// <summary>A count for an axis or a tooltip: 7, 1,250, 12.5K.</summary>
    public static string CompactCount(double value)
    {
        var abs = Math.Abs(value);
        var text = abs < 10_000 ? abs.ToString(abs % 1 == 0 ? "N0" : "0.#", Indian) : Compact(abs);
        return (value < 0 ? Minus : string.Empty) + text;
    }

    /// <summary>A whole count, grouped: 1,23,456.</summary>
    public static string Count(double value) => value.ToString("N0", Indian);

    /// <summary>A share: 15.5%.</summary>
    public static string Percent(double value) => value.ToString(Math.Abs(value) >= 100 ? "0" : "0.#", Indian) + "%";

    /// <summary>A quantity as a shop weighs or counts it: 12, 1.25, 0.5.</summary>
    public static string Quantity(double value) => value.ToString("0.###", Indian);

    private static string Compact(double abs) => abs switch
    {
        < 1_000 => abs.ToString(abs < 10 && abs % 1 != 0 ? "0.#" : "0", Indian),
        < 1_00_000 => Short(abs / 1_000) + "K",
        < 1_00_00_000 => Short(abs / 1_00_000) + "L",
        _ => Short(abs / 1_00_00_000) + "Cr",
    };

    /// <summary>One decimal while it still says something - 12.5K - and none once it would not: 125K.</summary>
    private static string Short(double value) => value.ToString(value < 100 ? "0.#" : "0", Indian);
}
