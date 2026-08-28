using System.Windows;
using Pos.Core.Configuration;

namespace Pos.App.Views;

/// <summary>
/// Asks for the PIN in front of the owner's screen.
/// </summary>
/// <remarks>
/// Three attempts, then it gives up. That count is not the real protection &mdash; the cost of each
/// guess is, which is why the stored credential is deliberately expensive to test against. Three is
/// here so somebody standing at the counter cannot simply sit and try.
/// </remarks>
public partial class PinPrompt : Window
{
    private const int Attempts = 3;

    private readonly PinCredential _credential;
    private int _used;

    private PinPrompt(PinCredential credential)
    {
        InitializeComponent();
        _credential = credential;
        Loaded += (_, _) => Entry.Focus();
    }

    /// <summary>
    /// Asks for the PIN. True when it was right, or when this lane has no PIN set at all.
    /// </summary>
    public static bool Passes(Window owner, SecuritySettings security)
    {
        ArgumentNullException.ThrowIfNull(security);

        if (!security.DashboardIsLocked)
            return true;

        var prompt = new PinPrompt(security.DashboardPin!) { Owner = owner };

        return prompt.ShowDialog() == true;
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (DashboardLock.Verify(Entry.Password, _credential))
        {
            DialogResult = true;
            return;
        }

        _used++;
        Entry.Clear();
        Entry.Focus();

        if (_used >= Attempts)
        {
            DialogResult = false;
            return;
        }

        Problem.Text = $"That is not the PIN. {Attempts - _used} left.";
        Problem.Visibility = Visibility.Visible;
    }
}
