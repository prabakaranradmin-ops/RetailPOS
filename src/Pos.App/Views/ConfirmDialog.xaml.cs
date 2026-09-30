using System.Windows;
using System.Windows.Media;

namespace Pos.App.Views;

/// <summary>What sort of question a dialog asks, which decides its icon and its colours.</summary>
public enum DialogKind
{
    /// <summary>An ordinary "shall I?".</summary>
    Question,

    /// <summary>Worth reading twice, but put right easily enough.</summary>
    Warning,

    /// <summary>Cannot be undone. The button that does it is drawn in red.</summary>
    Danger,

    /// <summary>Something to know, with one button to close it.</summary>
    Information,
}

/// <summary>
/// The till's own dialog: a question with buttons named for what they do, or a notice with one.
/// </summary>
public partial class ConfirmDialog : Window
{
    private ConfirmDialog(string title, string message, string confirm, string? cancel, DialogKind kind)
    {
        InitializeComponent();
        DarkChrome.Apply(this);

        Title = title;
        Heading.Text = title;
        Message.Text = message;
        Yes.Content = confirm;

        if (cancel is null)
        {
            // A notice: its one button both confirms and cancels, so Enter and Esc each close it.
            No.Visibility = Visibility.Collapsed;
            Yes.IsCancel = true;
        }
        else
        {
            No.Content = cancel;
        }

        var (glyph, ink, ground) = kind switch
        {
            DialogKind.Warning => (Glyphs.Warning, "Warning", "WarningSoft"),
            DialogKind.Danger => (Glyphs.Warning, "Danger", "DangerSoft"),
            DialogKind.Information => (Glyphs.Info, "Accent", "InfoSoft"),
            _ => (Glyphs.Info, "Accent", "InfoSoft"),
        };

        Symbol.Text = glyph;
        Symbol.Foreground = (Brush)FindResource(ink);
        Badge.Background = (Brush)FindResource(ground);

        if (kind == DialogKind.Danger)
            Yes.Style = (Style)FindResource("DangerButton");

        Loaded += (_, _) => Yes.Focus();
    }

    /// <summary>Asks, and says whether the answer was the button that goes ahead.</summary>
    /// <param name="confirm">What the button that goes ahead says: "Save the bill", not "OK".</param>
    public static bool Ask(Window? owner, string title, string message, string confirm, string cancel = "Cancel", DialogKind kind = DialogKind.Question)
    {
        var dialog = new ConfirmDialog(title, message, confirm, cancel, kind);
        Place(dialog, owner);
        return dialog.ShowDialog() == true;
    }

    /// <summary>Says something, with one button to close it.</summary>
    public static void Tell(Window? owner, string title, string message, DialogKind kind = DialogKind.Information)
    {
        var dialog = new ConfirmDialog(title, message, "OK", null, kind);
        Place(dialog, owner);
        dialog.ShowDialog();
    }

    private static void Place(Window dialog, Window? owner)
    {
        if (owner is { IsLoaded: true })
            dialog.Owner = owner;
        else
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    private void Yes_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void No_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
