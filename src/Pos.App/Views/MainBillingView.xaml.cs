using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pos.App.Input;
using Pos.App.ViewModels;
using Pos.Core.Configuration;
using Pos.Core.Domain;

namespace Pos.App.Views;

public partial class MainBillingView : Window
{
    private readonly BillingViewModel _viewModel;
    private readonly KeyboardRouter _router;

    /// <summary>
    /// Keys that mean "edit the text" while the caret is in a non-empty search box. They stay with
    /// the text box there, and route to the bill only once the box is empty — otherwise pressing
    /// Delete to fix a typo would silently remove a line from the invoice.
    /// </summary>
    private static readonly HashSet<Key> TextEditingKeys =
    [
        Key.Delete, Key.Back, Key.Add, Key.Subtract, Key.OemPlus, Key.OemMinus,
    ];

    /// <summary>
    /// The same set inside a pane that has its own box, minus Delete. Backspace is what anyone
    /// actually uses to fix a mistyped amount, which frees Delete to keep meaning "remove" —
    /// removing the last payment taken, rather than a character nobody was going to delete.
    /// </summary>
    private static readonly HashSet<Key> PaneTextEditingKeys =
    [
        Key.Back, Key.Add, Key.Subtract, Key.OemPlus, Key.OemMinus,
    ];

    public MainBillingView(BillingViewModel viewModel, Keymap keymap, PosSettings settings)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(keymap);
        ArgumentNullException.ThrowIfNull(settings);

        InitializeComponent();
        DarkChrome.Apply(this);

        _viewModel = viewModel;
        _router = new KeyboardRouter(keymap, viewModel);

        // The messages and the pane footers name the keys this lane actually uses, which on a lane
        // that has rebound them is not Enter, Esc and Shift+F12.
        viewModel.CommitKey = CommitKeyName(keymap);
        viewModel.CancelKey = KeyName(keymap, PosAction.Cancel) ?? "Esc";
        viewModel.CloseDayKey = KeyName(keymap, PosAction.CloseDay) ?? "Shift+F12";

        DataContext = viewModel;
        Title = $"RetailPOS — Billing — lane {settings.LaneId}";
        KeyPills.ItemsSource = BuildKeyPills(keymap);
        KeySheet.ItemsSource = BuildKeySheet(keymap);
        KeySheetFooter.Text = $"{KeyName(keymap, PosAction.ShowKeys) ?? "F1"} or {viewModel.CancelKey} closes this.";

        // The strip along the top. The shop's own name rather than the product's, because the
        // person reading it works there and already knows what the software is called.
        ShopName.Text = string.IsNullOrWhiteSpace(settings.Store.Name) ? "—" : settings.Store.Name;
        LaneLabel.Text = $"Lane {settings.LaneId}";

        ApplyTaxMode();

        viewModel.SearchFocusRequested += (_, _) => FocusSearchBox();
        viewModel.OwnerViewRequested += (_, _) => OpenOwnerView();

        // A scan that matched nothing is selected, so the next scan replaces it rather than being
        // added to the end of it and failing as well, and it sounds - the cashier is looking at the
        // goods and the scanner, not at the screen, and the scanner has already beeped "read".
        viewModel.SearchRejected += (_, _) =>
        {
            SearchBox.SelectAll();
            System.Media.SystemSounds.Hand.Play();
        };

        LineGrid.SizeChanged += (_, _) => FitColumns();

        Screen.SizeChanged += (_, _) => FitPanes();
        KeyStrip.SizeChanged += (_, _) => FitPanes();
        MessageArea.SizeChanged += (_, _) => FitPanes();
        viewModel.PropertyChanged += (_, e) =>
        {
            // The owner can switch what this lane issues from their own screen, so the columns
            // follow the view model rather than a value read once at startup.
            if (e.PropertyName is null or nameof(BillingViewModel.ShowsTax))
                ApplyTaxMode();

            OnViewModelPropertyChanged(e.PropertyName);
        };

        Loaded += (_, _) => FocusSearchBox();
    }

    /// <summary>
    /// How the owner's screen is built when it is asked for. Set by the composition root, and
    /// returns null when the PIN in front of it was not answered.
    /// </summary>
    public Func<Window?>? OwnerViewFactory { get; set; }

    private void OpenOwnerView()
    {
        if (OwnerViewFactory is null)
            return;

        var window = OwnerViewFactory();

        if (window is null)
            return;

        window.Owner = this;
        window.ShowDialog();

        // Back to billing with the caret where the next scan lands, and with the columns matching
        // whatever the owner may have just changed.
        ApplyTaxMode();
        FocusSearchBox();
    }

    /// <summary>
    /// Takes the tax columns off the screen on a composition lane.
    /// </summary>
    /// <remarks>
    /// Once at construction rather than bound, because the mode is a property of the lane and does
    /// not change while the till is running — changing it means editing settings.json and starting
    /// the till again, which is right for something that decides what document the shop issues.
    ///
    /// The columns are collapsed rather than removed so their widths and order stay exactly as
    /// declared, and so the grid on a GST lane is untouched by any of this.
    /// </remarks>
    /// <summary>
    /// What the two builds show differently on this screen, which is now very little.
    /// </summary>
    /// <remarks>
    /// The grid no longer carries per-line tax at all, and the bill card shows only the discount,
    /// so there are no columns to hide and no figures to suppress. What is left is telling the
    /// cashier which kind of bill this lane issues — which matters, because it decides what comes
    /// out of the printer.
    /// </remarks>
    private void ApplyTaxMode()
    {
        var showing = _viewModel.ShowsTax;

        BuildChip.Text = showing ? "GST" : "NO TAX";
        TaxNote.Text = showing ? "Incl. all taxes" : "Bill of supply — no GST charged";

        // "Before GST" on a lane that charges none would be a column about a tax that is not there.
        RateColumn.Header = showing ? "Before GST" : "Price";
    }

    /// <summary>
    /// Every key press passes through here first, so an action fires wherever focus happens to be.
    /// That is what makes the flow keyboard-only rather than keyboard-only-if-the-right-control-
    /// has-focus.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (e.Handled)
            return;

        // The tenders lie across the pane, so the arrows that point along them move along them
        // too. Only while paying: in the scan box, left and right move the caret.
        if (_viewModel.IsTendering && Keyboard.Modifiers == ModifierKeys.None && e.Key is Key.Left or Key.Right)
        {
            _router.Run(e.Key == Key.Left ? PosAction.MoveUp : PosAction.MoveDown);
            e.Handled = true;
            return;
        }

        if (!ShouldRoute(e.Key))
            return;

        if (_router.Handle(e.Key, Keyboard.Modifiers))
            e.Handled = true;
    }

    /// <summary>
    /// Moves the caret to whichever box the current mode expects, so the cashier never has to
    /// reach for the mouse when a pane opens.
    /// </summary>
    private void OnViewModelPropertyChanged(string? propertyName)
    {
        switch (propertyName)
        {
            case nameof(BillingViewModel.IsEditing) when _viewModel.IsEditing:
                Dispatcher.BeginInvoke(() => Focus(EditBox));
                break;

            case nameof(BillingViewModel.IsTendering) when _viewModel.IsTendering:
                Dispatcher.BeginInvoke(() => Focus(TenderBox));
                break;

            case nameof(BillingViewModel.IsFindingCustomer) when _viewModel.IsFindingCustomer:
                Dispatcher.BeginInvoke(() => Focus(CustomerBox));
                break;

            case nameof(BillingViewModel.IsReprinting) when _viewModel.IsReprinting:
                Dispatcher.BeginInvoke(() => Focus(ReprintBox));
                break;

            case nameof(BillingViewModel.IsVoiding) when _viewModel.IsVoiding:
                Dispatcher.BeginInvoke(() => Focus(VoidBox));
                break;

            case nameof(BillingViewModel.IsSettingCashier) when _viewModel.IsSettingCashier:
                Dispatcher.BeginInvoke(() => Focus(CashierBox));
                break;

            case nameof(BillingViewModel.IsCollecting) when _viewModel.IsCollecting:
                Dispatcher.BeginInvoke(() => Focus(CollectBox));
                break;

            case nameof(BillingViewModel.IsReturning) when _viewModel.IsReturning:
                Dispatcher.BeginInvoke(() => Focus(ReturnBox));
                break;

            case nameof(BillingViewModel.IsUsingDrawer) when _viewModel.IsUsingDrawer:
                Dispatcher.BeginInvoke(() => Focus(DrawerBox));
                break;

            case nameof(BillingViewModel.IsUsingQuickKeys) when _viewModel.IsUsingQuickKeys:
                Dispatcher.BeginInvoke(() => Focus(QuickKeyBox));
                break;

            case nameof(BillingViewModel.IsTakingOrder) when _viewModel.IsTakingOrder:
                Dispatcher.BeginInvoke(() => Focus(OrderBox));
                break;

            case nameof(BillingViewModel.IsSettingBusiness) when _viewModel.IsSettingBusiness:
                Dispatcher.BeginInvoke(() => Focus(BusinessBox));
                break;

            // From the GSTIN to the address: the box is the same, its text is not, and it is typed over.
            case nameof(BillingViewModel.BusinessStage) when _viewModel.IsSettingBusiness:
                Dispatcher.BeginInvoke(() => Focus(BusinessBox));
                break;

            // The list scrolls with the arrows, so the highlighted line of a long bill stays in view.
            case nameof(BillingViewModel.SelectedReturnLineIndex) when _viewModel.SelectedReturnLineIndex >= 0:
                ReturnList.ScrollIntoView(_viewModel.ReturnLines[_viewModel.SelectedReturnLineIndex]);
                break;

            case nameof(BillingViewModel.IsTendering)
                or nameof(BillingViewModel.IsFindingCustomer)
                or nameof(BillingViewModel.IsReprinting)
                or nameof(BillingViewModel.IsVoiding)
                or nameof(BillingViewModel.IsSettingCashier)
                or nameof(BillingViewModel.IsCollecting)
                or nameof(BillingViewModel.IsReturning)
                or nameof(BillingViewModel.IsUsingDrawer)
                or nameof(BillingViewModel.IsUsingQuickKeys)
                or nameof(BillingViewModel.IsTakingOrder)
                or nameof(BillingViewModel.IsSettingBusiness):
                if (!InAPane())
                    Dispatcher.BeginInvoke(FocusSearchBox);
                break;
        }
    }

    /// <summary>True while a pane with its own text box is open over the billing screen.</summary>
    private bool InAPane() =>
        _viewModel.IsTendering
        || _viewModel.IsFindingCustomer
        || _viewModel.IsReprinting
        || _viewModel.IsVoiding
        || _viewModel.IsSettingCashier
        || _viewModel.IsCollecting
        || _viewModel.IsReturning
        || _viewModel.IsUsingDrawer
        || _viewModel.IsUsingQuickKeys
        || _viewModel.IsTakingOrder
        || _viewModel.IsSettingBusiness;

    private static void Focus(TextBox box)
    {
        box.Focus();
        box.SelectAll();
    }

    private bool ShouldRoute(Key key)
    {
        // A pane with its own text box takes ordinary typing; navigation and function keys still
        // route, which is what drives the pane.
        if (_viewModel.IsEditing || InAPane())
            return !PaneTextEditingKeys.Contains(key);

        if (!SearchBox.IsKeyboardFocused || SearchBox.Text.Length == 0)
            return true;

        return !TextEditingKeys.Contains(key);
    }

    private void FocusSearchBox()
    {
        SearchBox.Focus();
        SearchBox.CaretIndex = SearchBox.Text.Length;
    }

    /// <summary>
    /// Below this width the grid drops its two reference columns, HSN and barcode.
    /// </summary>
    /// <remarks>
    /// On a 1280-wide screen, and on a 1366 screen at 125% scaling, the columns were squeezed until
    /// the item name - the one the cashier reads - was cut to a few letters, while the HSN code
    /// nobody reads at the counter kept its full width. The name wins. Both are still on the
    /// receipt, and on the item in the owner's catalogue.
    /// </remarks>
    internal const double ReferenceColumnsMinWidth = 1000;

    /// <summary>
    /// Below this width - a 1024 × 768 till - the rate before tax goes as well, and the name may
    /// narrow further, so that the line total still fits.
    /// </summary>
    /// <remarks>
    /// On a 1024-wide screen the grid gets about 600 units, and the columns' own floors came to 890:
    /// Rate, Disc and Total fell off the edge, and nothing scrolls sideways by design.
    /// </remarks>
    internal const double NarrowGridWidth = 720;

    private void FitColumns()
    {
        var width = LineGrid.ActualWidth;
        var reference = width >= ReferenceColumnsMinWidth ? Visibility.Visible : Visibility.Collapsed;
        var rate = width >= NarrowGridWidth ? Visibility.Visible : Visibility.Collapsed;
        var itemFloor = width >= NarrowGridWidth ? 220d : 150d;

        if (HsnColumn.Visibility == reference && BarcodeColumn.Visibility == reference
            && RateColumn.Visibility == rate && ItemColumn.MinWidth == itemFloor)
            return;

        HsnColumn.Visibility = reference;
        BarcodeColumn.Visibility = reference;
        RateColumn.Visibility = rate;
        ItemColumn.MinWidth = itemFloor;

        // The grid shares out star widths once and does not do it again when a column comes or
        // goes: without this the name kept its old share and the space the two left sat empty
        // after Total. Setting the star again has it shared out afresh.
        ItemColumn.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
    }

    /// <summary>
    /// Keeps the panes above the message bar: tells them how much of the foot of the screen the
    /// bar, the note over it and the keys take (see <see cref="PaneScrim"/>).
    /// </summary>
    private void FitPanes()
    {
        if (Screen.ActualHeight <= 0)
            return;

        var first = StandingNotePanel.IsVisible ? (FrameworkElement)StandingNotePanel : MessageStrip;
        var top = first.TranslatePoint(new Point(0, 0), Screen).Y - first.Margin.Top;
        var inset = Math.Max(0, Math.Round(Screen.ActualHeight - top));

        if (Resources["PaneInset"] is double current && Math.Abs(current - inset) < 0.5)
            return;

        Resources["PaneInset"] = inset;
    }

    /// <summary>
    /// The confirm key as the messages say it: "Enter" rather than the "Return" that Key.Enter
    /// prints as, since the two are the same key and Enter is what is written on it.
    /// </summary>
    internal static string CommitKeyName(Keymap keymap)
    {
        var gesture = keymap.GesturesFor(PosAction.Commit).FirstOrDefault();

        if (gesture == default)
            return "Enter";

        var name = gesture.ToString();

        return gesture.Key == Key.Enter ? name.Replace("Return", "Enter", StringComparison.Ordinal) : name;
    }

    /// <summary>
    /// A key as it is written on the keyboard: "Esc" rather than "Escape", "Enter" rather than
    /// "Return", "Delete". Null when the action has no key on this lane.
    /// </summary>
    internal static string? KeyName(Keymap keymap, PosAction action)
    {
        var gesture = keymap.GesturesFor(action).FirstOrDefault();

        return gesture == default ? null : Written(gesture);
    }

    private static string Written(KeyStroke gesture) => gesture.ToString()
        .Replace("Return", "Enter", StringComparison.Ordinal)
        .Replace("Escape", "Esc", StringComparison.Ordinal)
        .Replace("OemPlus", "+", StringComparison.Ordinal)
        .Replace("OemMinus", "−", StringComparison.Ordinal)
        .Replace("Add", "+ (keypad)", StringComparison.Ordinal)
        .Replace("Subtract", "− (keypad)", StringComparison.Ordinal);

    /// <summary>One row of the key sheet.</summary>
    public sealed record KeySheetRow(string Group, string Keys, string Text);

    /// <summary>
    /// Every action on the lane with every key bound to it, in the sheet's order - from the live
    /// keymap, so a rebound key is listed as it is on this lane.
    /// </summary>
    internal static List<KeySheetRow> BuildKeySheet(Keymap keymap) =>
        [
            .. PosActionText.Sheet
                .Select(entry => (entry, keys: keymap.GesturesFor(entry.Action).Select(Written).ToList()))
                .Where(x => x.keys.Count > 0)
                .Select(x => new KeySheetRow(x.entry.Group, string.Join("  ", x.keys), x.entry.Text)),
        ];

    /// <summary>A key on the strip was clicked: the same as pressing it.</summary>
    private void KeyPill_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: KeyPill pill })
            _router.Run(pill.Action);
    }

    private void PayPill_Click(object sender, RoutedEventArgs e) => _router.Run(PosAction.Tender);

    /// <summary>One key as the dock renders it.</summary>
    /// <param name="Key">The key, as printed on the cap: "F2", "Ctrl+H".</param>
    /// <param name="Label">What it does, in a word or two.</param>
    /// <param name="Action">What a click on it does - the same as the key.</param>
    public sealed record KeyPill(string Key, string Label, PosAction Action)
    {
        /// <summary>What a screen reader says for it: "F2, Search".</summary>
        public string Spoken => $"{Key}, {Label}";

        /// <summary>
        /// A key that cannot be undone, drawn in the danger colour and set apart: closing the day
        /// was a pill like "New" beside it.
        /// </summary>
        public bool IsDanger => Action is PosAction.CloseDay;
    }

    /// <summary>
    /// The keys along the bottom, read off the live keymap so a rebound key is described correctly.
    /// </summary>
    /// <remarks>
    /// Pay is deliberately absent: it has its own pill on the right of the dock, apart from the
    /// keys that only move things around, because it is the one that takes money.
    /// </remarks>
    private static List<KeyPill> BuildKeyPills(Keymap keymap)
    {
        (PosAction Action, string Label)[] shown =
        [
            (PosAction.FocusSearch, "Search"),
            (PosAction.EditQuantity, "Qty"),
            (PosAction.EditDiscount, "Discount"),
            (PosAction.DeleteLine, "Remove"),
            (PosAction.HoldBill, "Hold"),
            (PosAction.RecallBill, "Recall"),
            (PosAction.FindCustomer, "Customer"),
            (PosAction.ReceivePayment, "Khata payment"),
            (PosAction.ReturnGoods, "Return"),
            (PosAction.CashDrawer, "Cash in/out"),
            (PosAction.QuickKeys, "Loose"),
            (PosAction.TakeOrder, "Order"),
            (PosAction.OwnerView, "Owner"),
            (PosAction.ReprintInvoice, "Reprint"),
            (PosAction.NewBill, "New"),
            (PosAction.CloseDay, "Close day"),
        ];

        var pills = new List<KeyPill>(shown.Length);

        foreach (var (action, label) in shown)
        {
            var gesture = keymap.GesturesFor(action).FirstOrDefault();

            if (gesture != default)
                pills.Add(new KeyPill(gesture.ToString(), label, action));
        }

        return pills;
    }
}
