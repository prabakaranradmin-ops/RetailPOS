using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Pos.App.Views;

/// <summary>
/// Turns a 0..1 share into a star-sized grid length, so a bar can be drawn as a proportion of
/// whatever space its row happens to get.
/// </summary>
/// <remarks>
/// Star sizing rather than a pixel width, because a pixel width would have to be computed against
/// the container's actual size and recomputed every time the window resized. Two columns whose
/// stars are the share and its remainder always add up to the space available, at any width, with
/// no layout pass of our own.
///
/// The floor of 0.0001 matters: a zero-star column is not "no width" to WPF's layout, and a row of
/// them divides by zero and collapses the whole bar.
/// </remarks>
public sealed class StarShareConverter : IValueConverter
{
    /// <summary>True to return the share itself, false to return what is left of the whole.</summary>
    public bool Remainder { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var share = value is double d && !double.IsNaN(d) ? Math.Clamp(d, 0d, 1d) : 0d;
        var wanted = Remainder ? 1d - share : share;

        return new GridLength(Math.Max(wanted, 0.0001d), GridUnitType.Star);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("A bar is drawn from a figure, never read back into one.");
}
