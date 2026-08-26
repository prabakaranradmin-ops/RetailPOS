namespace Pos.Core.Domain;

/// <summary>
/// An Indian financial year: 1 April to 31 March.
/// </summary>
/// <remarks>
/// Invoice numbers are filed against this, not against the calendar year. A bill raised in February
/// 2026 belongs to FY 2025-26 and a bill raised in April 2026 opens FY 2026-27, so a sequence that
/// restarts on 1 January restarts in the middle of the year the returns are actually filed for.
/// </remarks>
public readonly record struct FiscalYear(int StartYear)
{
    /// <summary>The month a financial year opens on.</summary>
    public const int FirstMonth = 4;

    public static FiscalYear For(DateTimeOffset moment) =>
        new(moment.Month >= FirstMonth ? moment.Year : moment.Year - 1);

    public static FiscalYear For(DateTime moment) =>
        new(moment.Month >= FirstMonth ? moment.Year : moment.Year - 1);

    public int EndYear => StartYear + 1;

    /// <summary>The two-digit form printed on a bill, as in "26-27".</summary>
    public string ShortLabel => $"{StartYear % 100:D2}-{EndYear % 100:D2}";

    /// <summary>The full form, as in "2026-2027".</summary>
    public string LongLabel => $"{StartYear}-{EndYear}";

    public override string ToString() => ShortLabel;
}
