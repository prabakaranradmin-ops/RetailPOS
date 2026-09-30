using System.Globalization;
using System.Text;
using Pos.Core.Domain;

namespace Pos.Core.Analytics;

/// <summary>The order list as a spreadsheet: one row per item, grouped by supplier.</summary>
public static class OrderListFiles
{
    public static string Csv(OrderList list)
    {
        ArgumentNullException.ThrowIfNull(list);

        var csv = new StringBuilder();
        csv.AppendLine("supplier,phone,sku,item,unit,have,sells_a_day,days_left,order,last_rate,estimated_cost");

        foreach (var supplier in list.Suppliers)
        {
            foreach (var line in supplier.Lines)
            {
                csv.AppendLine(string.Join(',', new[]
                {
                    Field(supplier.Supplier),
                    Field(supplier.Phone ?? ""),
                    Field(line.Sku),
                    Field(line.Name),
                    Units.ScreenLabel(line.Unit),
                    Number(line.Have),
                    line.PerDay is { } rate ? Number(rate) : "",
                    line.DaysLeft is { } days ? days.ToString("0.#", CultureInfo.InvariantCulture) : "",
                    Number(line.Order),
                    line.LastRate is { } last ? last.ToString("0.00", CultureInfo.InvariantCulture) : "",
                    line.Cost is { } cost ? cost.ToString("0.00", CultureInfo.InvariantCulture) : "",
                }));
            }
        }

        return csv.ToString();
    }

    /// <summary>Written with a byte-order mark, so Excel reads a Tamil item name as Tamil.</summary>
    public static void Write(OrderList list, string path) =>
        File.WriteAllText(path, Csv(list), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

    private static string Number(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Field(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
}
