using System.Windows;
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
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(BillingViewModel.IsEditing) && viewModel.IsEditing)
                Dispatcher.BeginInvoke(() => { EditBox.Focus(); EditBox.SelectAll(); });
        };

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

    private bool ShouldRoute(Key key)
    {
        if (_viewModel.IsEditing)
        {
            // While editing a cell, leave text keys to the editor and route only the rest.
            return !TextEditingKeys.Contains(key);
        }

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
