using System.Globalization;
using System.Windows.Data;

namespace Pos.App.Views;

/// <summary>
/// A count with its noun, in the right number: "1 item", "3 items". The parameter is the singular;
/// an irregular plural follows a bar, "person|people".
/// </summary>
/// <remarks>
/// Instead of "{0} items", which put "1 items" under a bill of one line, and "(s)", which put a
/// bracket in every other sentence on the screen.
/// </remarks>
public sealed class PluralConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = System.Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        var forms = (parameter as string ?? "item").Split('|');
        var noun = count == 1 ? forms[0] : forms.Length > 1 ? forms[1] : forms[0] + "s";

        return $"{count.ToString("0.###", CultureInfo.InvariantCulture)} {noun}";
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
