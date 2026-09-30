namespace Pos.App.Charts;

/// <summary>
/// An axis with round numbers on it.
/// </summary>
/// <remarks>
/// <para>
/// The data decides roughly where an axis runs; this decides where its lines go. A scale that ran
/// from 0 to the best day's 873.25 in quarters would put gridlines at 218.31 and 436.63, which
/// nobody can read a bar against. Stepping in 1, 2 or 5 times a power of ten, and extending the
/// ends to the next step, gives 0, 200, 400 ... 1,000 instead.
/// </para>
/// <para>
/// Zero is kept on the axis by default. A bar's height is its value, and a bar chart whose axis
/// starts at 600 makes a day of 700 look like half of a day of 800.
/// </para>
/// </remarks>
public readonly record struct NiceScale(double Min, double Max, double Step)
{
    /// <summary>The span of the axis, never zero, so a value can always be placed on it.</summary>
    public double Range => Max - Min <= 0 ? 1 : Max - Min;

    /// <summary>Where a value falls along the axis, from 0 at <see cref="Min"/> to 1 at <see cref="Max"/>.</summary>
    public double Fraction(double value) => (value - Min) / Range;

    /// <summary>Every gridline, from the bottom of the axis to the top.</summary>
    public IReadOnlyList<double> Ticks()
    {
        var ticks = new List<double>();

        if (Step <= 0)
            return [Min];

        // Counted rather than accumulated, so 0.1 added ten times does not drift past 1.0 and lose
        // the top line.
        var count = (int)Math.Round((Max - Min) / Step);

        for (var i = 0; i <= count; i++)
            ticks.Add(Math.Round(Min + (i * Step), 10));

        return ticks;
    }

    /// <summary>
    /// A scale covering <paramref name="low"/> to <paramref name="high"/> in about
    /// <paramref name="lines"/> gridlines.
    /// </summary>
    /// <param name="wholeSteps">For counts: never a step of half a bill.</param>
    public static NiceScale For(double low, double high, int lines = 5, bool includeZero = true, bool wholeSteps = false)
    {
        if (double.IsNaN(low) || double.IsInfinity(low))
            low = 0;

        if (double.IsNaN(high) || double.IsInfinity(high))
            high = 0;

        if (low > high)
            (low, high) = (high, low);

        if (includeZero)
        {
            low = Math.Min(0, low);
            high = Math.Max(0, high);
        }

        // Nothing to spread out: a flat line, or no data at all. An axis from 0 to 1 at least puts
        // the zero line where it belongs, and the chart says in words that there is nothing to show.
        if (high - low < 1e-9)
        {
            if (Math.Abs(high) < 1e-9)
                return new NiceScale(0, 1, wholeSteps ? 1 : 0.25);

            var magnitude = Math.Pow(10, Math.Floor(Math.Log10(Math.Abs(high))));
            low = includeZero ? Math.Min(0, low) : low - magnitude;
            high += magnitude;
        }

        lines = Math.Max(2, lines);

        var step = Nice((high - low) / (lines - 1), round: true);

        if (wholeSteps)
            step = Math.Max(1, Math.Ceiling(step));

        var min = Math.Floor(low / step) * step;
        var max = Math.Ceiling(high / step) * step;

        // A value sitting exactly on the top line would have its bar touch the frame. One more step
        // of headroom only when the data reaches the very top.
        if (Math.Abs(max - high) < step * 1e-6 && high > 0)
            max += step;

        return new NiceScale(Math.Round(min, 10), Math.Round(max, 10), step);
    }

    /// <summary>1, 2, 2.5 or 5 times a power of ten, whichever is closest to <paramref name="value"/>.</summary>
    internal static double Nice(double value, bool round)
    {
        if (value <= 0 || double.IsNaN(value) || double.IsInfinity(value))
            return 1;

        var exponent = Math.Floor(Math.Log10(value));
        var power = Math.Pow(10, exponent);
        var fraction = value / power;

        double nice = round
            ? fraction < 1.5 ? 1 : fraction < 2.25 ? 2 : fraction < 3.5 ? 2.5 : fraction < 7.5 ? 5 : 10
            : fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 2.5 ? 2.5 : fraction <= 5 ? 5 : 10;

        return nice * power;
    }
}
