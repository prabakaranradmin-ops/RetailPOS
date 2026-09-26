using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Pos.App.ViewModels;
using Pos.Core.Configuration;
using Pos.Core.Domain;

namespace Pos.App.Views;

/// <summary>
/// The owner's screen. Figures, what needs reordering, loading a catalogue, and the two settings an
/// owner should be able to change without opening a text editor.
/// </summary>
public partial class OwnerView : Window
{
    private readonly OwnerViewModel _viewModel;
    private readonly CatalogueImportViewModel _catalogue;
    private readonly HardwareViewModel _hardware;

    /// <summary>
    /// Suppresses the radio buttons' Checked handlers while the code sets them to match the current
    /// state. Without it, showing the window would fire a mode change on the way in.
    /// </summary>
    private bool _settingUp = true;

    public OwnerView(OwnerViewModel viewModel, CatalogueImportViewModel catalogue, HardwareViewModel hardware)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(hardware);

        InitializeComponent();

        _viewModel = viewModel;
        _catalogue = catalogue;
        _hardware = hardware;
        DataContext = viewModel;

        HardwareTab.DataContext = hardware;

        // The catalogue tab answers to its own view model. Scoped to that one branch of the tree so
        // the rest of the window keeps binding to the figures without qualification.
        CatalogueTab.DataContext = catalogue;

        // A catalogue that has just landed changes the reorder list and, where the file carried cost
        // prices, the margins beside the figures. Re-reading here means the owner does not have to
        // know that, or close the screen and open it again to see it.
        catalogue.Imported += (_, _) => _viewModel.Refresh();

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
        // the four sections is a mouse or Ctrl+Tab, and neither is discoverable — which is how a
        // screen ends up with two sections nobody knows are there.
        if (e.KeyboardDevice.Modifiers == ModifierKeys.Control && e.Key is Key.D1 or Key.D2 or Key.D3 or Key.D4 or Key.D5)
        {
            Tabs.SelectedIndex = e.Key - Key.D1;
            e.Handled = true;
        }
    }

    // ---- Hardware --------------------------------------------------------------------------------

    private async void CheckPrinter_Click(object sender, RoutedEventArgs e) => await _hardware.CheckPrinter();

    private async void CheckDrawer_Click(object sender, RoutedEventArgs e) => await _hardware.CheckDrawer();

    private async void CheckScale_Click(object sender, RoutedEventArgs e) => await _hardware.CheckScale();

    private async void ListPorts_Click(object sender, RoutedEventArgs e) => await _hardware.ListPorts();

    private async void CheckScanner_Click(object sender, RoutedEventArgs e)
    {
        if (_hardware.ScannerTypesLikeAKeyboard && _hardware.ScannedCode.Trim().Length == 0)
        {
            Say("This lane's scanner types like a keyboard. Click in the box, scan an item so the "
                + "code appears there, then check it.");
            return;
        }

        await _hardware.CheckScanner();
    }

    /// <summary>The paper width to preview against, which is not always the lane's own.</summary>
    private int PreviewWidth() => Width32.IsChecked == true ? 32 : 48;

    private void Preview_Click(object sender, RoutedEventArgs e) => _hardware.ShowPreview(PreviewWidth());

    private void PreviewImage_Click(object sender, RoutedEventArgs e)
    {
        // Written under the lane's own folder rather than beside the program: the install folder
        // may be read-only, and this is the lane's working output, not part of the build.
        _hardware.RenderPreviewImage(Path.Combine(App.DataDirectory, "previews"));

        if (_hardware.PreviewImagePath is not { } path)
            return;

        // Loaded fully into memory and released, so the file is not locked by the control. Without
        // this, drawing a second preview fails on a file the window is still holding open.
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();

        PreviewImage.Source = image;
    }

    // ---- Loading a catalogue ---------------------------------------------------------------------

    private void BrowseCatalogue_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose the catalogue file",
            Filter = "Catalogue (*.csv)|*.csv|Every file (*.*)|*.*",
            CheckFileExists = true,
        };

        // Start where they were last time. A shop reloading a price list goes back to the same
        // folder every time, and a dialog that opens somewhere else makes them navigate twice.
        var last = _catalogue.FilePath.Trim();

        if (last.Length > 0)
        {
            try
            {
                var folder = Path.GetDirectoryName(Path.GetFullPath(last));

                if (folder is not null && Directory.Exists(folder))
                    dialog.InitialDirectory = folder;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Whatever is in the box is not a path. The dialog opens wherever it would have.
            }
        }

        if (dialog.ShowDialog(this) == true)
            _catalogue.FilePath = dialog.FileName;
    }

    private void ImportMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_settingUp || sender is not RadioButton { Tag: string tag })
            return;

        _catalogue.UpdateExisting = tag == "Update";
    }

    private void CheckCatalogue_Click(object sender, RoutedEventArgs e) => _catalogue.Check();

    private void ImportCatalogue_Click(object sender, RoutedEventArgs e)
    {
        // Only when existing items are in play. A first load adds what was not there and is undone
        // by correcting the file and loading it again; an update writes over prices the shop is
        // already trading on, and those are gone once they are replaced.
        if (_catalogue.UpdateExisting && MessageBox.Show(
                this,
                "Prices, names and barcodes in this file will replace what the catalogue holds for "
                + "items already in it.\n\nBills already issued do not change — each one records what "
                + "it was sold at. Shelf counts are left alone unless the file gives new ones.",
                "Change items already in the catalogue?",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        _catalogue.Import();
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
