using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pos.App.ViewModels;
using Pos.Core.Configuration;
using Pos.Core.Domain;

namespace Pos.App.Views;

/// <summary>
/// The owner's screen. Figures, what needs reordering, and the two settings an owner should be able
/// to change without opening a text editor.
/// </summary>
public partial class OwnerView : Window
{
    private readonly OwnerViewModel _viewModel;

    /// <summary>
    /// Suppresses the radio buttons' Checked handlers while the code sets them to match the current
    /// state. Without it, showing the window would fire a mode change on the way in.
    /// </summary>
    private bool _settingUp = true;

    public OwnerView(OwnerViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null or nameof(OwnerViewModel.ShowsTax))
                ApplyTaxMode();

            if (e.PropertyName is null or nameof(OwnerViewModel.IsPinSet))
                ApplyPinState();
        };

        Loaded += (_, _) =>
        {
            _viewModel.Refresh();

            // On the no-tax build there is no choice to show: it cannot issue a tax invoice, so the
            // chooser is replaced by a statement of what this build does.
            var switchable = !ProductVariant.ChargesNoTax;

            TaxModeCard.Visibility = switchable ? Visibility.Visible : Visibility.Collapsed;
            NoTaxCard.Visibility = switchable ? Visibility.Collapsed : Visibility.Visible;

            ModeGst.IsChecked = _viewModel.TaxMode == TaxMode.Gst;
            ModeComposition.IsChecked = _viewModel.TaxMode == TaxMode.Composition;

            ApplyTaxMode();
            ApplyPinState();

            _settingUp = false;
        };
    }

    /// <summary>Shows the GST breakdown, or says why there is none.</summary>
    private void ApplyTaxMode()
    {
        GstCard.Visibility = _viewModel.ShowsTax ? Visibility.Visible : Visibility.Collapsed;
        NoGstCard.Visibility = _viewModel.ShowsTax ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyPinState() =>
        PinState.Text = _viewModel.IsPinSet
            ? "A PIN is set. This screen asks for it before it opens."
            : "No PIN is set. Anyone at this till can open this screen and read the shop's figures.";

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        // Escape goes back to billing, unless a text box is mid-edit and the cashier means to
        // abandon what they typed rather than leave the screen.
        if (e.Key == Key.Escape && Keyboard.FocusedElement is not TextBox { Text.Length: > 0 })
        {
            Close();
            e.Handled = true;
        }

        if (e.Key == Key.F5)
        {
            _viewModel.Refresh();
            e.Handled = true;
        }

        // The till is driven from the keyboard, and so is this. Without these the only way between
        // the three sections is a mouse or Ctrl+Tab, and neither is discoverable — which is how a
        // screen ends up with two sections nobody knows are there.
        if (e.KeyboardDevice.Modifiers == ModifierKeys.Control && e.Key is Key.D1 or Key.D2 or Key.D3)
        {
            Tabs.SelectedIndex = e.Key - Key.D1;
            e.Handled = true;
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _viewModel.Refresh();

    private void Days_Checked(object sender, RoutedEventArgs e)
    {
        if (_settingUp || sender is not RadioButton { Tag: string tag })
            return;

        if (int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days))
            _viewModel.Days = days;
    }

    private void Adjust_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.ApplyAdjustment() is { } problem)
            Say(problem);
    }

    private void TaxMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_settingUp || sender is not RadioButton { Tag: string tag })
            return;

        if (!Enum.TryParse<TaxMode>(tag, out var mode) || mode == _viewModel.TaxMode)
            return;

        // Changing what document the shop issues is worth stopping for, both ways round: it is a
        // legal distinction rather than a display preference.
        var going = mode == TaxMode.Composition
            ? "This lane will start issuing a BILL OF SUPPLY and will charge no GST.\n\n"
              + "Only do this if the shop is registered under the composition scheme. Bills already "
              + "issued do not change."
            : "This lane will start issuing a TAX INVOICE and will charge GST.\n\n"
              + "Only do this if the shop is registered to collect it. Bills already issued do not change.";

        if (MessageBox.Show(this, going, "Change what this lane issues?",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            Restore();
            return;
        }

        if (_viewModel.SetTaxMode(mode) is { } refused)
        {
            Say(refused);
            Restore();
        }

        void Restore()
        {
            _settingUp = true;
            ModeGst.IsChecked = _viewModel.TaxMode == TaxMode.Gst;
            ModeComposition.IsChecked = _viewModel.TaxMode == TaxMode.Composition;
            _settingUp = false;
        }
    }

    private void SavePin_Click(object sender, RoutedEventArgs e)
    {
        var pin = PinBox.Password;

        if (pin.Length == 0)
        {
            Say("Type the PIN twice, then press Save.");
            return;
        }

        // Twice, because it is never echoed and cannot be recovered — a mistyped one would lock the
        // owner out of their own figures until somebody hand-edited settings.json.
        if (pin != PinBoxAgain.Password)
        {
            Say("Those two did not match.");
            return;
        }

        if (_viewModel.SetPin(pin) is { } problem)
        {
            Say(problem);
            return;
        }

        PinBox.Clear();
        PinBoxAgain.Clear();
    }

    private void ClearPin_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsPinSet)
        {
            Say("There is no PIN to remove.");
            return;
        }

        if (MessageBox.Show(this,
                "Remove the PIN? Anyone at this till will then be able to open this screen and read "
                + "the shop's turnover, margins and cost prices.",
                "Remove the PIN?", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        if (_viewModel.SetPin(null) is { } problem)
            Say(problem);
    }

    private void Say(string message) =>
        MessageBox.Show(this, message, "RetailPOS", MessageBoxButton.OK, MessageBoxImage.Information);
}
