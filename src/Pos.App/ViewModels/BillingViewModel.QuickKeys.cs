using System.Collections.ObjectModel;
using System.Globalization;
using Pos.Core.Domain;

namespace Pos.App.ViewModels;

/// <summary>Where picking a quick key has got to.</summary>
public enum QuickKeyStage
{
    /// <summary>Which item: its key, or the arrows and Enter.</summary>
    Pick = 0,

    /// <summary>How much of it: a weight, a count, or Enter for one.</summary>
    Quantity = 1,
}

/// <summary>One quick key: the key it answers to and the loose item behind it.</summary>
public sealed record QuickKey(string Key, Item Item)
{
    public string Name => Item.Name;

    public string Price => $"{Item.SellPrice.ToString("N2", CultureInfo.InvariantCulture)} / {Units.ScreenLabel(Item.UnitType)}";
}

public sealed partial class BillingViewModel
{
    /// <summary>The keys, in the order they are handed out: the digits, then the letters.</summary>
    private static readonly string[] QuickKeyNames =
        ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M", "N"];

    private QuickKeyStage _quickKeyStage;
    private int _selectedQuickKeyIndex = -1;
    private Item? _quickItem;

    /// <summary>The loose items on the keys, most often sold first.</summary>
    public ObservableCollection<QuickKey> QuickKeyTiles { get; } = [];

    public bool IsUsingQuickKeys => _mode == BillingMode.QuickKeys;

    public QuickKeyStage QuickKeyStage
    {
        get => _quickKeyStage;
        private set
        {
            if (!Set(ref _quickKeyStage, value))
                return;

            Raise(nameof(IsPickingQuickKey));
            Raise(nameof(QuickKeyPrompt));
        }
    }

    public bool IsPickingQuickKey => _quickKeyStage == QuickKeyStage.Pick;

    public int SelectedQuickKeyIndex
    {
        get => _selectedQuickKeyIndex;
        set => Set(ref _selectedQuickKeyIndex, value);
    }

    public string QuickKeyPrompt => _quickKeyStage == QuickKeyStage.Pick || _quickItem is null
        ? $"Press an item's key - or arrow to it and press {CommitKey}."
        : _quickItem.UnitType.AllowsFractionalQuantity()
            ? $"{_quickItem.Name} at {Show.Money(_quickItem.SellPrice)} a {Units.ScreenLabel(_quickItem.UnitType)}: how many {Units.ScreenLabel(_quickItem.UnitType)}? {CommitKey} for one."
            : $"{_quickItem.Name} at {Show.Money(_quickItem.SellPrice)} each: how many? {CommitKey} for one.";

    /// <summary>
    /// Opens the quick keys: the loose items with nothing to scan, each on a key, so a bunch of
    /// coriander or a kilo of onions is one keystroke and a weight rather than a search.
    /// </summary>
    public void QuickKeys()
    {
        ClearPendingConfirmations();
        CancelEdit();

        if (Mode != BillingMode.Billing)
        {
            StatusMessage = "Finish what is open first.";
            return;
        }

        IReadOnlyList<Item> loose;

        try
        {
            loose = _items.LooseItems(QuickKeyNames.Length);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            StatusMessage = $"The quick keys could not be read: {ex.Message}";
            return;
        }

        if (loose.Count == 0)
        {
            // With where to add one: the owner's catalogue, a key and a tab away.
            StatusMessage = "No loose items yet. An item with no barcode - sold by weight, or by the bunch - gets a quick key: "
                            + "add them on the owner's screen (Ctrl+D), in Catalogue (Ctrl+3).";
            return;
        }

        QuickKeyTiles.Clear();

        for (var i = 0; i < loose.Count; i++)
            QuickKeyTiles.Add(new QuickKey(QuickKeyNames[i], loose[i]));

        _quickItem = null;
        SelectedQuickKeyIndex = 0;
        QuickKeyStage = QuickKeyStage.Pick;
        Mode = BillingMode.QuickKeys;
        EditBuffer = string.Empty;
        Raise(nameof(QuickKeyPrompt));

        StatusMessage = "Quick keys: press an item's key.";
    }

    /// <summary>Called as the box changes: a key typed while picking picks at once.</summary>
    private void OnQuickKeyTyped()
    {
        if (_quickKeyStage != QuickKeyStage.Pick)
            return;

        var typed = EditBuffer.Trim();

        if (typed.Length == 0)
            return;

        var index = QuickKeyTiles.ToList().FindIndex(k => string.Equals(k.Key, typed, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
        {
            StatusMessage = $"No quick key '{typed}'.";
            EditBuffer = string.Empty;
            return;
        }

        PickQuickKey(index);
    }

    private void PickQuickKey(int index)
    {
        SelectedQuickKeyIndex = index;
        _quickItem = QuickKeyTiles[index].Item;
        QuickKeyStage = QuickKeyStage.Quantity;
        EditBuffer = string.Empty;
        Raise(nameof(QuickKeyPrompt));
        StatusMessage = QuickKeyPrompt;
    }

    private void CommitQuickKey()
    {
        if (_quickKeyStage == QuickKeyStage.Pick)
        {
            if (_selectedQuickKeyIndex < 0 || _selectedQuickKeyIndex >= QuickKeyTiles.Count)
            {
                StatusMessage = "Press an item's key, or arrow to one.";
                return;
            }

            PickQuickKey(_selectedQuickKeyIndex);
            return;
        }

        if (_quickItem is not { } item)
            return;

        var typed = EditBuffer.Trim();
        var quantity = 1m;

        if (typed.Length > 0 && (!TryParseAmount(typed, out quantity) || quantity <= 0m))
        {
            StatusMessage = $"'{typed}' is not a quantity.";
            return;
        }

        if (!item.UnitType.AllowsFractionalQuantity() && decimal.Truncate(quantity) != quantity)
        {
            StatusMessage = $"{item.Name} is sold whole: {quantity:0.###} cannot be right.";
            return;
        }

        CloseQuickKeys();
        AddItem(item, quantity);
    }

    private void MoveInQuickKeys(int delta)
    {
        if (_quickKeyStage == QuickKeyStage.Pick && QuickKeyTiles.Count > 0)
            SelectedQuickKeyIndex = Math.Clamp(_selectedQuickKeyIndex + delta, 0, QuickKeyTiles.Count - 1);
    }

    /// <summary>Escape: from the quantity back to the keys, and from the keys back to the bill.</summary>
    private void BackOutOfQuickKeys()
    {
        if (_quickKeyStage == QuickKeyStage.Quantity)
        {
            _quickItem = null;
            QuickKeyStage = QuickKeyStage.Pick;
            EditBuffer = string.Empty;
            StatusMessage = "Quick keys: press an item's key.";
            return;
        }

        CloseQuickKeys();
        StatusMessage = string.Empty;
    }

    private void CloseQuickKeys()
    {
        _quickItem = null;
        QuickKeyStage = QuickKeyStage.Pick;
        Mode = BillingMode.Billing;
        EditBuffer = string.Empty;
    }
}
