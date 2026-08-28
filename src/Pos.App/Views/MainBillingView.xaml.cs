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

        _viewModel = viewModel;
        _router = new KeyboardRouter(keymap, viewModel);

        DataContext = viewModel;
        Title = $"RetailPOS — Billing — lane {settings.LaneId}";
        KeyPills.ItemsSource = BuildKeyPills(keymap);

        // The strip along the top. The shop's own name rather than the product's, because the
        // person reading it works there and already knows what the software is called.
        ShopName.Text = string.IsNullOrWhiteSpace(settings.Store.Name) ? "—" : settings.Store.Name;
        LaneLabel.Text = $"Lane {settings.LaneId}";

        ApplyTaxMode();

        viewModel.SearchFocusRequested += (_, _) => FocusSearchBox();
        viewModel.OwnerViewRequested += (_, _) => OpenOwnerView();
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

            case nameof(BillingViewModel.IsTendering)
                or nameof(BillingViewModel.IsFindingCustomer)
                or nameof(BillingViewModel.IsReprinting)
                or nameof(BillingViewModel.IsVoiding)
                or nameof(BillingViewModel.IsSettingCashier):
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
        || _viewModel.IsSettingCashier;

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

    /// <summary>One key as the dock renders it.</summary>
    public sealed record KeyPill(string Key, string Label);

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
                pills.Add(new KeyPill(gesture.ToString(), label));
        }

        return pills;
    }
}
