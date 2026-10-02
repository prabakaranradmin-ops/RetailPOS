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
using Pos.Core.Domain.Catalogue;
using Pos.Core.Domain.Printing;

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
    private readonly NewItemViewModel _newItem;
    private readonly MaintenanceViewModel _maintenance;
    private readonly CustomersViewModel _customers;
    private readonly GstReturnViewModel _gst;
    private readonly PurchasesViewModel _purchases;
    private readonly OrdersViewModel _orders;
    private readonly OffersViewModel? _offers;
    private readonly PricesViewModel _prices;
    private readonly OpenItemsViewModel? _openItems;

    /// <summary>
    /// Suppresses the radio buttons' Checked handlers while the code sets them to match the current
    /// state. Without it, showing the window would fire a mode change on the way in.
    /// </summary>
    private bool _settingUp = true;

    public OwnerView(
        OwnerViewModel viewModel,
        CatalogueImportViewModel catalogue,
        HardwareViewModel hardware,
        NewItemViewModel newItem,
        MaintenanceViewModel maintenance,
        CustomersViewModel customers,
        GstReturnViewModel gst,
        PurchasesViewModel purchases,
        PricesViewModel prices,
        OrdersViewModel orders,

        // The offers card. Optional: a screen built without it shows no card.
        OffersViewModel? offers = null,

        // What the till sold that is not in the catalogue. Optional in the same way.
        OpenItemsViewModel? openItems = null)
    {
        ArgumentNullException.ThrowIfNull(orders);
        ArgumentNullException.ThrowIfNull(prices);
        ArgumentNullException.ThrowIfNull(customers);
        ArgumentNullException.ThrowIfNull(gst);
        ArgumentNullException.ThrowIfNull(purchases);
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(hardware);
        ArgumentNullException.ThrowIfNull(newItem);
        ArgumentNullException.ThrowIfNull(maintenance);

        InitializeComponent();
        TitleBar.Apply(this);

        _viewModel = viewModel;
        _catalogue = catalogue;
        _hardware = hardware;
        _newItem = newItem;
        _maintenance = maintenance;
        _customers = customers;
        _gst = gst;
        _purchases = purchases;
        _orders = orders;
        _prices = prices;
        DataContext = viewModel;

        HardwareTab.DataContext = hardware;
        MaintenanceTab.DataContext = maintenance;
        CustomersTab.DataContext = customers;
        GstTab.DataContext = gst;
        PurchasesTab.DataContext = purchases;
        OrdersTab.DataContext = orders;
        PricesPanel.DataContext = prices;
        _offers = offers;
        OffersPanel.DataContext = offers;
        OffersPanel.Visibility = offers is null ? Visibility.Collapsed : Visibility.Visible;
        _openItems = openItems;
        OpenItemsCard.DataContext = openItems;
        OpenItemsNotice.DataContext = openItems;

        // The list is read when the tab is first opened rather than with the window, so opening
        // the owner's screen to glance at today's takings does not also read every customer.
        Tabs.SelectionChanged += (_, e) =>
        {
            if (e.OriginalSource != Tabs)
                return;

            _viewModel.ClearStatus();

            // Ctrl+3 lands in the item name box, so adding one product is Ctrl+3 then typing.
            if (Tabs.SelectedItem == CatalogueTabItem)
            {
                _prices.LoadLabels();
                _offers?.Load();
                _openItems?.Load();
                Dispatcher.BeginInvoke(() => NewItemName.Focus(), System.Windows.Threading.DispatcherPriority.Input);
                return;
            }

            // Read when first opened, like the customers: a month of lines is not worth reading for
            // an owner who only came to look at today's takings.
            // Ctrl+9 lands in the supplier search, so finding who the delivery is from is the next keystroke.
            if (Tabs.SelectedItem == PurchasesTabItem)
            {
                if (!_purchases.IsLoaded)
                    _purchases.Load();

                Dispatcher.BeginInvoke(() => SupplierSearchBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
                return;
            }

            // Worked out afresh each time it is opened: a delivery entered on the tab before changes it.
            // Ctrl+0 lands on the supplier list, so the arrows walk the suppliers straight away.
            if (Tabs.SelectedItem == OrdersTabItem)
            {
                _orders.Load();
                Dispatcher.BeginInvoke(() =>
                {
                    if (OrderSupplierList.SelectedIndex >= 0
                        && OrderSupplierList.ItemContainerGenerator.ContainerFromIndex(OrderSupplierList.SelectedIndex) is ListBoxItem item)
                        item.Focus();
                    else
                        CoverDaysBox.Focus();
                }, System.Windows.Threading.DispatcherPriority.Input);
                return;
            }

            if (Tabs.SelectedItem == GstTabItem)
            {
                if (!_gst.IsLoaded)
                    _gst.Load();

                Dispatcher.BeginInvoke(() => GstEarlier.Focus(), System.Windows.Threading.DispatcherPriority.Input);
                return;
            }

            if (Tabs.SelectedItem is not TabItem { Content: Grid { Name: "CustomersTab" } })
                return;

            if (_customers.Results.Count == 0)
                _customers.Search();

            // Straight into the search box, so Ctrl+7 then typing a name is the whole lookup. Left
            // to itself focus stays on the tab header, and the only way into the box is the mouse.
            Dispatcher.BeginInvoke(() => CustomerSearch.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        };
        SingleItemPanel.DataContext = newItem;

        // An item added by hand changes the reorder list the same way a file does, and needs a label.
        newItem.Added += (_, _) =>
        {
            _catalogue.RefreshHeld();
            _viewModel.Refresh();
            _prices.LoadLabels();
        };

        // The catalogue tab answers to its own view model. Scoped to that one branch of the tree so
        // the rest of the window keeps binding to the figures without qualification.
        CatalogueTab.DataContext = catalogue;

        // A catalogue that has just landed changes the reorder list and, where the file carried cost
        // prices, the margins beside the figures. Re-reading here means the owner does not have to
        // know that, or close the screen and open it again to see it.
        catalogue.Imported += (_, _) =>
        {
            _viewModel.Refresh();
            _prices.LoadLabels();
        };

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

            // Read with the window, small as it is, so the figures tab can say when any are waiting.
            _openItems?.Load();

            // On the no-tax build there is no choice to show: it cannot issue a tax invoice, so the
            // chooser is replaced by a statement of what this build does.
            var switchable = !ProductVariant.ChargesNoTax;

            TaxModeCard.Visibility = switchable ? Visibility.Visible : Visibility.Collapsed;
            NoTaxCard.Visibility = switchable ? Visibility.Collapsed : Visibility.Visible;

            ModeGst.IsChecked = _viewModel.TaxMode == TaxMode.Gst;
            ModeComposition.IsChecked = _viewModel.TaxMode == TaxMode.Composition;

            LayoutCard.Visibility = _viewModel.CanChooseLayout ? Visibility.Visible : Visibility.Collapsed;
            LowStockCard.Visibility = _viewModel.CanChangeLowStockPercent ? Visibility.Visible : Visibility.Collapsed;
            UpiCard.Visibility = _viewModel.CanChangeUpiId ? Visibility.Visible : Visibility.Collapsed;
            LayoutStandard.IsChecked = _viewModel.ReceiptLayout == ReceiptLayout.Standard;
            LayoutCompact.IsChecked = _viewModel.ReceiptLayout == ReceiptLayout.Compact;

            LookCard.Visibility = _viewModel.CanChooseScreenTheme ? Visibility.Visible : Visibility.Collapsed;
            LookByTimeOfDay.IsChecked = _viewModel.ScreenTheme == ScreenTheme.ByTimeOfDay;
            LookMorning.IsChecked = _viewModel.ScreenTheme == ScreenTheme.Morning;
            LookNoon.IsChecked = _viewModel.ScreenTheme == ScreenTheme.Noon;
            LookEvening.IsChecked = _viewModel.ScreenTheme == ScreenTheme.Evening;
            LookNight.IsChecked = _viewModel.ScreenTheme == ScreenTheme.Night;

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
        if (e.KeyboardDevice.Modifiers == ModifierKeys.Control &&
            e.Key is Key.D1 or Key.D2 or Key.D3 or Key.D4 or Key.D5 or Key.D6 or Key.D7 or Key.D8 or Key.D9)
        {
            Tabs.SelectedIndex = e.Key - Key.D1;
            e.Handled = true;
        }

        // The tenth section is Ctrl+0, the key after 9 on the row.
        if (e.KeyboardDevice.Modifiers == ModifierKeys.Control && e.Key == Key.D0)
        {
            Tabs.SelectedItem = OrdersTabItem;
            e.Handled = true;
        }

        // Page Down and Page Up scroll the tab. Without them most of this screen - the margins,
        // the trend, a customer's khata, a report being read back - could only be reached with a
        // mouse, on an application that is meant to be driven from the keyboard end to end.
        if (e.KeyboardDevice.Modifiers == ModifierKeys.None && e.Key is Key.PageDown or Key.PageUp
            && PageScroller() is { } scroller)
        {
            if (e.Key == Key.PageDown)
                scroller.PageDown();
            else
                scroller.PageUp();

            e.Handled = true;
        }
    }

    /// <summary>
    /// The part of the current tab that Page Down should move: the last visible area marked
    /// <c>Tag="Page"</c> that has anything to scroll.
    /// </summary>
    /// <remarks>
    /// The last rather than the first, because on the tabs with two - Hardware, Maintenance - the
    /// second is the content being looked at: the drawn bill, the report read back. Null when
    /// nothing on the tab needs scrolling, so a grid that pages its own rows still gets the key.
    /// </remarks>
    private ScrollViewer? PageScroller()
    {
        if (Tabs.SelectedItem is not TabItem { Content: DependencyObject content })
            return null;

        ScrollViewer? found = null;

        void Walk(DependencyObject node)
        {
            if (node is ScrollViewer { Tag: "Page", IsVisible: true } scroller && scroller.ScrollableHeight > 0)
                found = scroller;

            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
                Walk(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
        }

        Walk(content);
        return found;
    }

    // ---- Maintenance -----------------------------------------------------------------------------

    private async void Backup_Click(object sender, RoutedEventArgs e) => await _maintenance.Backup();

    private async void CopyOff_Click(object sender, RoutedEventArgs e) => await _maintenance.CopyToPenDrive();

    private async void CheckDb_Click(object sender, RoutedEventArgs e) => await _maintenance.Check();

    private async void Compact_Click(object sender, RoutedEventArgs e) => await _maintenance.Compact();

    private async void ShowReport_Click(object sender, RoutedEventArgs e) => await _maintenance.ShowReport();

    private async void Reprint_Click(object sender, RoutedEventArgs e) => await _maintenance.Reprint();

    // ---- Customers -------------------------------------------------------------------------------

    /// <summary>
    /// Down or Enter in the search box drops into the results, on the first match.
    /// </summary>
    /// <remarks>
    /// A text box swallows the arrow keys, so without this the list under it could only be reached
    /// with the mouse or a run of Tabs. From the list the arrows move between customers and each one
    /// is shown as it is reached.
    /// </remarks>
    private void CustomerSearch_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Down or Key.Enter) || _customers.Results.Count == 0)
            return;

        _customers.Selected ??= _customers.Results[0];

        CustomerResults.UpdateLayout();
        CustomerResults.ScrollIntoView(_customers.Selected);

        if (CustomerResults.ItemContainerGenerator.ContainerFromItem(_customers.Selected) is DataGridRow row)
            row.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        else
            CustomerResults.Focus();

        e.Handled = true;
    }

    private void SaveBusiness_Click(object sender, RoutedEventArgs e)
    {
        if (_customers.SaveBusiness() is { } problem)
            Say(problem);
    }

    private void SaveKhataLimit_Click(object sender, RoutedEventArgs e)
    {
        if (_customers.SaveLimit() is { } problem)
            Say(problem);
    }

    /// <summary>Enter in the limit box saves it, so the limit is set without reaching for the button.</summary>
    private void KhataLimit_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;

        if (_customers.SaveLimit() is { } problem)
            Say(problem);
    }

    private void SaveCustomerName_Click(object sender, RoutedEventArgs e)
    {
        if (_customers.SaveName() is { } problem)
            Say(problem);
    }

    private void ForgetCustomer_Click(object sender, RoutedEventArgs e)
    {
        if (!_customers.HasSelection)
        {
            Say("Pick a customer from the list first.");
            return;
        }

        // Said in full, because it cannot be taken back: there is no copy of a forgotten customer
        // anywhere in the lane, which is the point of it.
        if (!ConfirmDialog.Ask(
                this,
                "Forget this customer?",
                $"Forget {_customers.Title} ({_customers.Mobile})?\n\n"
                + "Their name, mobile number and loyalty points are deleted. Their bills stay in the "
                + "shop's books but no longer say who they were for.\n\n"
                + "This cannot be undone. Snapshots taken before now still hold them until they age out.",
                "Forget them",
                "Keep them",
                DialogKind.Danger))
        {
            return;
        }

        if (_customers.Forget() is { } problem)
            Say(problem);
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        // The typed date already armed the button. This says out loud what is about to be lost,
        // because the number of sales involved is the part somebody has not worked out for
        // themselves — and it is the last point at which they can stop.
        if (!ConfirmDialog.Ask(
                this,
                "Put this snapshot back?",
                "This replaces the lane's database with the snapshot you picked.\n\n"
                + "Every sale rung up since it was taken will be gone, including any day already "
                + "closed on them. The database being replaced is moved aside rather than deleted.\n\n"
                + "Close the till before doing this.",
                "Put it back",
                "Leave it",
                DialogKind.Danger))
        {
            return;
        }

        await _maintenance.Restore();
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

    // A new bill starts at its top. Left where the last one was read to, a freshly drawn bill opens
    // on its foot, and the owner checking a layout sees the tender block and not the heading.
    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        _hardware.ShowPreview(PreviewWidth());
        PreviewTextScroll.ScrollToTop();
    }

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
        PreviewImageScroll.ScrollToTop();
    }

    // ---- A festival against last year's -------------------------------------------------------------

    private void CompareFestival_Click(object sender, RoutedEventArgs e) => _viewModel.CompareFestival();

    // ---- The GST return ---------------------------------------------------------------------------

    private void GstEarlier_Click(object sender, RoutedEventArgs e) => _gst.EarlierMonth();

    private void GstLater_Click(object sender, RoutedEventArgs e) => _gst.LaterMonth();

    private void GstSave_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save the GST return for the accountant",
            Filter = "Web page (*.html)|*.html",
            FileName = _gst.SuggestedFileName,
            AddExtension = true,
            DefaultExt = ".html",
        };

        if (dialog.ShowDialog(this) != true)
            return;

        if (_gst.Save(dialog.FileName) is { } problem)
            Say(problem);
    }

    private void DayBookSave_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save the day book for the accountant",
            Filter = "Day book (*.csv)|*.csv",
            FileName = _gst.SuggestedDayBookName,
            AddExtension = true,
            DefaultExt = ".csv",
        };

        if (dialog.ShowDialog(this) != true)
            return;

        if (_gst.SaveDayBook(dialog.FileName) is { } problem)
            Say(problem);
    }

    // ---- Prices and labels -----------------------------------------------------------------------

    private void SavePriceSheet_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save a price sheet to fill in",
            Filter = "Spreadsheet (*.csv)|*.csv",
            FileName = _prices.SuggestedSheetName,
            AddExtension = true,
            DefaultExt = ".csv",
        };

        if (dialog.ShowDialog(this) == true)
            _prices.SaveSheet(dialog.FileName);
    }

    private void LoadPriceSheet_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Load the filled-in price sheet",
            Filter = "Spreadsheet (*.csv)|*.csv|Every file (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true)
            return;

        if (_prices.CheckSheet(dialog.FileName) is not { } plan)
            return;

        // Said before anything is written, with the prices below cost and the likely typos named.
        if (!ConfirmDialog.Ask(this, "Change the prices?", PricesViewModel.Question(plan), "Change the prices", "Leave them"))
            return;

        // Printing the labels it made due is the next thing to do, and their card is below the
        // fold: the owner was told "print them below" and left looking at the unit list. Brought
        // into view once the list has been laid out with them in it.
        if (_prices.ApplySheet(plan) is null)
            Dispatcher.BeginInvoke(() => LabelsCard.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void PrintLabels_Click(object sender, RoutedEventArgs e) => _prices.Print();

    // ---- Offers and schemes ----------------------------------------------------------------------

    private void SaveOfferSheet_Click(object sender, RoutedEventArgs e)
    {
        if (_offers is null)
            return;

        var dialog = new SaveFileDialog
        {
            Title = "Save the offers sheet to fill in",
            Filter = "Spreadsheet (*.csv)|*.csv",
            FileName = _offers.SuggestedSheetName,
            AddExtension = true,
            DefaultExt = ".csv",
        };

        if (dialog.ShowDialog(this) == true)
            _offers.SaveSheet(dialog.FileName);
    }

    private void LoadOfferSheet_Click(object sender, RoutedEventArgs e)
    {
        if (_offers is null)
            return;

        var dialog = new OpenFileDialog
        {
            Title = "Load the filled-in offers sheet",
            Filter = "Spreadsheet (*.csv)|*.csv|Every file (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true)
            return;

        if (_offers.CheckSheet(dialog.FileName) is not { } plan)
            return;

        // Said before anything changes: what starts, what ends.
        if (!ConfirmDialog.Ask(this, "Run these offers?", _offers.Question(plan), "Run them", "Not now"))
            return;

        _offers.ApplySheet(plan);
    }

    private void SaveLabelsPage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save the shelf labels as a page to print",
            Filter = "Web page (*.html)|*.html",
            FileName = _prices.SuggestedLabelsName,
            AddExtension = true,
            DefaultExt = ".html",
        };

        if (dialog.ShowDialog(this) == true)
            _prices.SavePage(dialog.FileName);
    }

    // ---- Orders ----------------------------------------------------------------------------------

    private void CoverDaysBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        _orders.ApplyCoverDays();
        e.Handled = true;
    }

    private void ApplyCoverDays_Click(object sender, RoutedEventArgs e) => _orders.ApplyCoverDays();

    private void CopyOrder_Click(object sender, RoutedEventArgs e) => _orders.Copy();

    private void SaveOrderList_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save the order list",
            Filter = "Spreadsheet (*.csv)|*.csv",
            FileName = _orders.SuggestedFileName,
            AddExtension = true,
            DefaultExt = ".csv",
        };

        if (dialog.ShowDialog(this) != true)
            return;

        _orders.Save(dialog.FileName);
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

    // ---- One item at a time ----------------------------------------------------------------------

    private void AcceptHsn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: HsnSuggestion suggestion })
            _newItem.Accept(suggestion);
    }

    private void AddItem_Click(object sender, RoutedEventArgs e) => _newItem.Save();

    private void ClearItem_Click(object sender, RoutedEventArgs e)
    {
        _newItem.Clear();
        _openItems?.ForgetAdding();
    }

    /// <summary>The till's item into the form below, and the caret into the one box it cannot fill: the SKU.</summary>
    private void AddOpenItem_Click(object sender, RoutedEventArgs e)
    {
        if (_openItems?.StartAdding() == true)
        {
            NewItemSku.BringIntoView();
            NewItemSku.Focus();
        }
    }

    private void LeaveOutOpenItem_Click(object sender, RoutedEventArgs e) => _openItems?.LeaveOut();

    private void ImportCatalogue_Click(object sender, RoutedEventArgs e)
    {
        // Only when existing items are in play. A first load adds what was not there and is undone
        // by correcting the file and loading it again; an update writes over prices the shop is
        // already trading on, and those are gone once they are replaced.
        if (_catalogue.UpdateExisting && !ConfirmDialog.Ask(
                this,
                "Change items already in the catalogue?",
                "Prices, names and barcodes in this file will replace what the catalogue holds for "
                + "items already in it.\n\nBills already issued do not change — each one records what "
                + "it was sold at. Shelf counts are left alone unless the file gives new ones.",
                "Change them",
                "Leave them",
                DialogKind.Warning))
        {
            return;
        }

        _catalogue.Import();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _viewModel.Refresh();

    private void SaveWebPage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save the figures as a web page",
            Filter = "Web page (*.html)|*.html|Every file (*.*)|*.*",
            FileName = $"figures-{DateTime.Now:yyyy-MM-dd}.html",
            AddExtension = true,
            DefaultExt = ".html",
        };

        if (dialog.ShowDialog(this) != true)
            return;

        if (_viewModel.SaveAsWebPage(dialog.FileName) is { } problem)
        {
            Say(problem);
            return;
        }

        // Said once, at the moment the file exists. The PIN guards this screen; it cannot guard a
        // file, and an owner who does not know that leaves their margins in the lane folder.
        Say($"Saved to {dialog.FileName}.\n\nThat file holds the shop's turnover, margins and cost "
            + "prices, and anyone who can open this computer can read it. Keep it somewhere private, "
            + "and delete it once it has been sent.");
    }

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

    // ---- Purchases ---------------------------------------------------------------------------

    private void AddSupplier_Click(object sender, RoutedEventArgs e) => _purchases.AddSupplier();

    private void PaySupplier_Click(object sender, RoutedEventArgs e)
    {
        if (_purchases.Pay() is { } problem)
            Say(problem);
    }

    /// <summary>Down goes into the matches; Enter takes the only one there is.</summary>
    private void PurchaseItemBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && _purchases.ItemMatches.Count > 0)
        {
            PurchaseMatches.SelectedIndex = 0;
            PurchaseMatches.UpdateLayout();
            (PurchaseMatches.ItemContainerGenerator.ContainerFromIndex(0) as UIElement)?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (_purchases.LineItem is null && _purchases.ItemMatches.Count == 1)
                _purchases.LineItem = _purchases.ItemMatches[0];

            if (_purchases.LineItem is not null)
                LineQuantityBox.Focus();

            e.Handled = true;
        }
    }

    private void PurchaseMatches_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        TakeMatch();
        e.Handled = true;
    }

    private void PurchaseMatches_MouseDoubleClick(object sender, MouseButtonEventArgs e) => TakeMatch();

    private void TakeMatch()
    {
        if (PurchaseMatches.SelectedItem is not Item item)
            return;

        _purchases.LineItem = item;
        LineQuantityBox.Focus();
    }

    /// <summary>Enter in any of the line's boxes puts the line on the bill and goes back for the next item.</summary>
    private void PurchaseLineField_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        AddPurchaseLine();
        e.Handled = true;
    }

    private void AddPurchaseLine_Click(object sender, RoutedEventArgs e) => AddPurchaseLine();

    private void AddPurchaseLine()
    {
        if (_purchases.AddLine() is null)
            PurchaseItemBox.Focus();
    }

    private void PurchaseLinesGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || _purchases.SelectedLine is not { } line)
            return;

        _purchases.RemoveLine(line);
        e.Handled = true;
    }

    private void SavePurchase_Click(object sender, RoutedEventArgs e)
    {
        // Said before it happens: a bill puts stock on the shelf and money on an account, and the
        // number typed in the wrong box is easiest to catch now.
        if (!ConfirmDialog.Ask(this, "Save the bill?", _purchases.SaveQuestion, "Save the bill", "Not yet"))
            return;

        if (_purchases.SaveBill() is { } problem)
            Say(problem);
        else
            SupplierSearchBox.Focus();
    }

    private void ClearPurchase_Click(object sender, RoutedEventArgs e) => _purchases.ClearBill();

    private void VoidPurchase_Click(object sender, RoutedEventArgs e)
    {
        if (_purchases.SelectedBill is not { } bill)
            return;

        // "Keep it" rather than "Cancel": a Cancel button on a question about cancelling a bill
        // could be read as the answer yes.
        if (!ConfirmDialog.Ask(this, "Cancel the bill?",
                $"Cancel bill {bill.BillNo} from {bill.SupplierName}, for {bill.Total:N2}?\n\nWhat it put on the shelf comes back off, and it is no longer owed. The entry stays in the book, marked cancelled.",
                "Cancel the bill", "Keep it", DialogKind.Danger))
        {
            return;
        }

        if (_purchases.VoidBill() is { } problem)
            Say(problem);
    }

    /// <summary>
    /// Asked first, like cancelling a bill: a debit note is a document the supplier holds too, and
    /// it cannot be taken back here.
    /// </summary>
    private void SendBack_Click(object sender, RoutedEventArgs e)
    {
        if (_purchases.SelectedBill is not { } bill)
            return;

        var going = _purchases.ReturnRows.Where(r => r.Quantity is > 0m).ToList();

        if (!ConfirmDialog.Ask(this, "Send the goods back?",
                $"Send {Plural.Of(going.Count, "line")} back to {bill.SupplierName} on a debit note against bill {bill.BillNo}?\n\n"
                + string.Join("\n", going.Select(r => $"  {r.Name}: {r.Quantity:0.###}"))
                + "\n\nCounted goods come off the shelf, and what they cost comes off what the shop owes the supplier.",
                "Send them back", "Not yet", DialogKind.Danger))
        {
            return;
        }

        if (_purchases.SendBack() is { } problem)
            Say(problem);
    }

    // ---- The stock sheet ---------------------------------------------------------------------

    private void SaveStockSheet_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save a stock sheet to fill in",
            Filter = "Spreadsheet (*.csv)|*.csv",
            FileName = $"stock-sheet-{DateTime.Now:yyyy-MM-dd}.csv",
            AddExtension = true,
            DefaultExt = ".csv",
        };

        if (dialog.ShowDialog(this) != true)
            return;

        if (_viewModel.SaveStockSheet(dialog.FileName) is { } problem)
            Say(problem);
    }

    private void LoadStockSheet_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Load the filled-in stock sheet",
            Filter = "Spreadsheet (*.csv)|*.csv|Every file (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true)
            return;

        if (_viewModel.CheckStockSheet(dialog.FileName) is not { } plan)
            return;

        // Said before anything is written: a stocktake loaded against the wrong day's sheet is a
        // hundred wrong counts, and this is the moment it can still be stopped.
        var ask = $"Change {Plural.Of(plan.Counts, "count")}"
                + (plan.FullLevels > 0 ? $" and {Plural.Of(plan.FullLevels, "full level")}" : string.Empty)
                + "?\n\n"
                + (SheetWords.LeftAlone(plan.Blank, plan.Unchanged, "count") is { Length: > 0 } leftAlone ? leftAlone + " " : string.Empty)
                + "Prices and everything else about the items stay as they are.";

        if (!ConfirmDialog.Ask(this, "Load the stock sheet?", ask, "Change the counts", "Leave them"))
            return;

        if (_viewModel.ApplyStockSheet(plan) is { } problem)
            Say(problem);
    }

    private void SaveLowStockPercent_Click(object sender, RoutedEventArgs e) => SaveLowStockPercent();

    private void LowStockPercent_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        SaveLowStockPercent();
        e.Handled = true;
    }

    private void SaveLowStockPercent()
    {
        if (_viewModel.SetLowStockPercent() is { } problem)
            Say(problem);
    }

    private void SaveUpiId_Click(object sender, RoutedEventArgs e) => SaveUpiId();

    private void UpiId_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        SaveUpiId();
        e.Handled = true;
    }

    private void SaveUpiId()
    {
        if (_viewModel.SetUpiId() is { } problem)
            Say(problem);
    }

    private void SaveStatement_Click(object sender, RoutedEventArgs e)
    {
        if (_customers.StatementPage() is not { } page)
        {
            Say(_customers.Status);
            return;
        }

        SaveStatementPage(page, $"khata-{_customers.Mobile}-{DateTime.Today:yyyy-MM-dd}.html", 1);
    }

    private void SaveEveryoneOwing_Click(object sender, RoutedEventArgs e)
    {
        if (_customers.EveryoneOwingPage() is not { } page)
        {
            Say(_customers.Status);
            return;
        }

        SaveStatementPage(page, $"khata-everyone-{DateTime.Today:yyyy-MM-dd}.html", -1);
    }

    private void SaveStatementPage(string page, string fileName, int statements)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save the khata statement",
            Filter = "Web page (*.html)|*.html",
            FileName = fileName,
            AddExtension = true,
            DefaultExt = ".html",
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            File.WriteAllText(dialog.FileName, page, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Say($"It could not be saved: {ex.Message}");
            return;
        }

        // One page per statement, each a <section>: counted rather than carried separately.
        var count = statements > 0 ? statements : System.Text.RegularExpressions.Regex.Matches(page, "<section class=\"statement\">").Count;
        _customers.Saved(dialog.FileName, count);
    }

    private void SaveBillPage_Click(object sender, RoutedEventArgs e)
    {
        if (_customers.BillPage() is not { } page || _customers.SelectedBill is not { } bill)
        {
            Say(_customers.Status);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save the bill",
            Filter = "Web page (*.html)|*.html",
            FileName = $"{bill.InvoiceNo.Replace('/', '-')}.html",
            AddExtension = true,
            DefaultExt = ".html",
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            File.WriteAllText(dialog.FileName, page, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Say($"{bill.InvoiceNo} saved to {dialog.FileName}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Say($"It could not be saved: {ex.Message}");
        }
    }

    private void CopyStatement_Click(object sender, RoutedEventArgs e)
    {
        if (_customers.CopyStatement() is { } problem)
            Say(problem);
    }

    private void CopyReminder_Click(object sender, RoutedEventArgs e)
    {
        if (_customers.CopyReminder() is { } problem)
            Say(problem);
    }

    private void SaveCatalogueTemplate_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save a catalogue template",
            Filter = "Spreadsheet (*.csv)|*.csv",
            FileName = "catalog_template.csv",
            AddExtension = true,
            DefaultExt = ".csv",
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            // The template shipped beside the program, carried inside it, byte for byte - mark and all,
            // so Excel opens its Tamil example as Tamil.
            using var source = typeof(OwnerView).Assembly.GetManifestResourceStream("catalog_template.csv")
                ?? throw new InvalidOperationException("the template is missing from this build");
            using var target = File.Create(dialog.FileName);
            source.CopyTo(target);

            _catalogue.Say($"Saved a catalogue template to {dialog.FileName}. Its example rows show every column; replace them with your own items.");
        }
        catch (Exception ex)
        {
            Say($"Could not save it: {ex.Message}");
        }
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

        if (!ConfirmDialog.Ask(this, "Change what this lane issues?", going, "Change it", "Leave it", DialogKind.Warning))
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

    /// <summary>
    /// No confirmation, unlike the tax mode: the layout is a matter of paper, not of law, and the
    /// other choice is one key away.
    /// </summary>
    private void Layout_Checked(object sender, RoutedEventArgs e)
    {
        if (_settingUp || sender is not RadioButton { Tag: string tag })
            return;

        if (!Enum.TryParse<ReceiptLayout>(tag, out var layout))
            return;

        if (_viewModel.SetReceiptLayout(layout) is { } problem)
            Say(problem);
    }

    /// <summary>
    /// No confirmation either: a look changes nothing but colours, it shows the moment it is picked,
    /// and the one before is a key away.
    /// </summary>
    private void Look_Checked(object sender, RoutedEventArgs e)
    {
        if (_settingUp || sender is not RadioButton { Tag: string tag })
            return;

        if (!Enum.TryParse<ScreenTheme>(tag, out var look))
            return;

        if (_viewModel.SetScreenTheme(look) is { } problem)
            Say(problem);
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

        if (!ConfirmDialog.Ask(this, "Remove the PIN?",
                "Anyone at this till will then be able to open this screen and read the shop's turnover, "
                + "margins and cost prices.",
                "Remove the PIN", "Keep it", DialogKind.Danger))
        {
            return;
        }

        if (_viewModel.SetPin(null) is { } problem)
            Say(problem);
    }

    // ---- Who works the till, and what waits for the owner ----------------------------------------

    private void AddCashier_Click(object sender, RoutedEventArgs e) => AddCashier();

    /// <summary>Enter in the second PIN box adds the cashier, so the whole card is done from the keyboard.</summary>
    private void NewCashierPinAgain_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        AddCashier();
    }

    private void AddCashier()
    {
        if (_viewModel.AddCashier(NewCashierName.Text, NewCashierPin.Password, NewCashierPinAgain.Password) is { } problem)
        {
            // The PINs go, the name stays: retyping a name is no hardship, and a PIN left in a box
            // after a refusal is one more person who might see it typed.
            NewCashierPin.Clear();
            NewCashierPinAgain.Clear();
            Say(problem);
            return;
        }

        NewCashierName.Clear();
        NewCashierPin.Clear();
        NewCashierPinAgain.Clear();
        NewCashierName.Focus();
    }

    private void RemoveCashier_Click(object sender, RoutedEventArgs e)
    {
        if (CashierNamesList.SelectedItem is not string name)
        {
            Say("Pick the cashier to take off first.");
            return;
        }

        if (!ConfirmDialog.Ask(this, $"Take {name} off?",
                $"{name} will not be able to sign on at the till. Their past sales keep their name.",
                "Take them off", "Keep them", DialogKind.Danger))
        {
            return;
        }

        if (_viewModel.RemoveCashier(name) is { } problem)
            Say(problem);
    }

    private void DiscountLimit_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        SaveDiscountLimit();
    }

    private void DiscountLimit_LostFocus(object sender, RoutedEventArgs e) => SaveDiscountLimit();

    /// <summary>Saves the share, only while discounts are being asked about; otherwise it waits for the tick.</summary>
    private void SaveDiscountLimit()
    {
        if (!_viewModel.ApproveDiscounts)
            return;

        if (_viewModel.SaveDiscountLimit() is { } problem)
            Say(problem);
    }

    private void Say(string message) => ConfirmDialog.Tell(this, "RetailPOS", message);
}
