namespace Pos.Core.Data;

/// <summary>
/// <see cref="Pos.Core.Domain.LowStock"/> as SQL, for the queries that list low stock without
/// reading every item into memory first.
/// </summary>
/// <remarks>
/// Both need a <c>$pct</c> parameter bound as a number, never as text: SQLite ranks any text above
/// any number, so a text '0' would pass <c>$pct &gt; 0</c> and a switched-off rule would still fire.
/// </remarks>
public static class LowStockSql
{
    /// <summary>The count an item warns at: its reorder level, or its share of full.</summary>
    public static string WarnAt(string alias) =>
        $"COALESCE(CAST({alias}.reorder_level AS REAL), " +
        $"CASE WHEN $pct > 0 AND CAST({alias}.full_qty AS REAL) > 0 THEN ROUND(CAST({alias}.full_qty AS REAL) * $pct / 100.0, 3) END)";

    /// <summary>A counted item at or below the count it warns at.</summary>
    public static string IsLow(string alias) =>
        $"({alias}.stock_qty IS NOT NULL AND CAST({alias}.stock_qty AS REAL) <= {WarnAt(alias)})";

    /// <summary>The value to bind as <c>$pct</c>.</summary>
    public static double Percent(decimal percent) => (double)percent;
}
