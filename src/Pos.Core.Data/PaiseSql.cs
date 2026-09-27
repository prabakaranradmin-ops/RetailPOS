using System.Globalization;

namespace Pos.Core.Data;

/// <summary>
/// Turns a stored amount into whole paise inside SQL, so money is summed exactly.
/// </summary>
/// <remarks>
/// Amounts are stored as text to keep decimals exact, and SQLite has no decimal type. Summing them
/// as REAL would accumulate binary floating-point error across a year of bills; every amount in the
/// books has at most two decimal places, so multiplying by 100 and rounding lands exactly on an
/// integer, and integer addition is exact however many rows there are. Shared so that the dashboard
/// and a customer's history cannot disagree about what the same bills came to.
/// </remarks>
public static class PaiseSql
{
    private const string Pattern = "CAST(ROUND(CAST({0} AS REAL) * 100) AS INTEGER)";

    /// <summary>One amount column as integer paise.</summary>
    public static string Of(string column) => string.Format(CultureInfo.InvariantCulture, Pattern, column);

    /// <summary>The integer paise total of an amount column.</summary>
    public static string Sum(string column) => $"SUM({Of(column)})";

    public static decimal Rupees(long paise) => paise / 100m;
}
