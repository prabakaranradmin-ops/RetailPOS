using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pos.App.Input;
using Pos.App.ViewModels;

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
        KeyHints.Text = BuildKeyHints(keymap);

        viewModel.SearchFocusRequested += (_, _) => FocusSearchBox();
        viewModel.PropertyChanged += (_, e) => OnViewModelPropertyChanged(e.PropertyName);

        Loaded += (_, _) => FocusSearchBox();
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

            case nameof(BillingViewModel.IsTendering) or nameof(BillingViewModel.IsFindingCustomer):
                if (!_viewModel.IsTendering && !_viewModel.IsFindingCustomer)
                    Dispatcher.BeginInvoke(FocusSearchBox);
                break;
        }
    }

    private static void Focus(TextBox box)
    {
        box.Focus();
        box.SelectAll();
    }

    private bool ShouldRoute(Key key)
    {
        // A pane with its own text box takes ordinary typing; navigation and function keys still
        // route, which is what drives the pane.
        if (_viewModel.IsEditing || _viewModel.IsTendering || _viewModel.IsFindingCustomer)
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

    private static string BuildKeyHints(Keymap keymap)
    {
        // Read the hints off the live keymap rather than hardcoding them, so a rebound key is
        // described correctly on screen.
        (PosAction Action, string Label)[] shown =
        [
            (PosAction.FocusSearch, "search"),
            (PosAction.EditQuantity, "qty"),
            (PosAction.EditDiscount, "discount"),
            (PosAction.DeleteLine, "delete"),
            (PosAction.HoldBill, "hold"),
            (PosAction.RecallBill, "recall"),
            (PosAction.FindCustomer, "customer"),
            (PosAction.Tender, "pay"),
            (PosAction.NewBill, "new"),
        ];

        var parts = new List<string>(shown.Length);

        foreach (var (action, label) in shown)
        {
            var gesture = keymap.GesturesFor(action).FirstOrDefault();

            if (gesture != default)
                parts.Add($"{gesture} {label}");
        }

        return string.Join("   ", parts);
    }
}
