using System.Windows;

namespace Pos.App.Views;

/// <summary>
/// What a section in the owner's list carries besides its name: its icon, and the key that opens it.
/// </summary>
/// <remarks>
/// Kept apart from the name so the name stays the name - "Stock", not "Stock  (Ctrl+2)" - and the key
/// can be drawn quieter beside it, where it is found by anybody who looks and ignored by anybody who
/// already knows it.
/// </remarks>
public static class Nav
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached(
        "Glyph", typeof(string), typeof(Nav), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ShortcutProperty = DependencyProperty.RegisterAttached(
        "Shortcut", typeof(string), typeof(Nav), new PropertyMetadata(string.Empty));

    public static string GetGlyph(DependencyObject element) => (string)element.GetValue(GlyphProperty);

    public static void SetGlyph(DependencyObject element, string value) => element.SetValue(GlyphProperty, value);

    public static string GetShortcut(DependencyObject element) => (string)element.GetValue(ShortcutProperty);

    public static void SetShortcut(DependencyObject element, string value) => element.SetValue(ShortcutProperty, value);
}
