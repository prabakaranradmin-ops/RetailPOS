using System.Collections.ObjectModel;
using Pos.Core.Domain;

namespace Pos.App.ViewModels;

/// <summary>One MRP a pack can carry, as the till offers it.</summary>
public sealed record MrpChoice(string Label, Item Item, bool IsOlder);

/// <summary>
/// One barcode with two MRPs on the shelf: the till asks which a pack carries.
/// </summary>
/// <remarks>
/// <para>
/// After an MRP goes up, the packs already on the shelf still have the old one printed on them, and
/// may not be sold for more than it says. A scanner reads the same barcode off both, so only the
/// cashier, looking at the pack, can tell them apart. The till asks until the count says the older
/// packs have sold, and then stops.
/// </para>
/// <para>
/// The newer MRP is offered first, since that is what most packs will carry as the old ones sell;
/// a second pack of the same item on the bill is offered whatever the last one was, so a run of the
/// same packs is Enter, Enter, Enter.
/// </para>
/// </remarks>
public sealed partial class BillingViewModel
{
    private int _selectedMrpIndex;
    private readonly HashSet<InvoiceLine> _olderMrpLines = [];
    private readonly Dictionary<long, bool> _lastMrpChoice = [];

    /// <summary>True while the till asks which MRP the pack in hand carries.</summary>
    public bool IsChoosingMrp => _mode == BillingMode.ChooseMrp;

    /// <summary>
    /// Only Up, Down, Enter and Esc - and typing - while the till asks about the item in hand: which
    /// MRP, or what an item not in the catalogue is. Nothing edits the bill behind the question.
    /// </summary>
    public bool TakesOnlyAChoice => IsChoosingMrp || IsAddingOpenItem;

    public ObservableCollection<MrpChoice> MrpChoices { get; } = [];

    /// <summary>The MRP highlighted, by the arrow keys or a click on the list.</summary>
    public int SelectedMrpIndex
    {
        get => _selectedMrpIndex;
        set => Set(ref _selectedMrpIndex, Math.Clamp(value, 0, Math.Max(0, MrpChoices.Count - 1)));
    }

    /// <summary>The item being asked about, for the pane's heading.</summary>
    public string MrpQuestion { get; private set; } = string.Empty;

    /// <summary>
    /// Puts an item on the bill as scanned or picked: straight on, or - with older packs still on
    /// the shelf at a lower MRP - after asking which MRP this one carries.
    /// </summary>
    private void AddPicked(Item item)
    {
        if (!item.HasOlderMrp)
        {
            AddItem(item);
            return;
        }

        MrpChoices.Clear();
        MrpChoices.Add(new MrpChoice($"MRP {Show.Money(item.Mrp)} - sells at {Show.Money(item.SellPrice)}", item, IsOlder: false));
        MrpChoices.Add(new MrpChoice(
            $"MRP {Show.Money(item.OlderMrp!.Value)} - the older packs, sells at {Show.Money(item.OlderPrice!.Value)} (about {item.OlderLeft!.Value:0.###} left)",
            item.AtOlderMrp(),
            IsOlder: true));

        // Raised whether or not it changed: the list, emptied since the last question, has no highlight.
        _selectedMrpIndex = _lastMrpChoice.TryGetValue(item.Id, out var older) && older ? 1 : 0;
        Raise(nameof(SelectedMrpIndex));
        MrpQuestion = $"{item.Name}: which MRP is printed on the pack?";
        Raise(nameof(MrpQuestion));

        ClearSearch();
        Mode = BillingMode.ChooseMrp;
        StatusMessage = $"{item.Name} has two MRPs on the shelf. Up and down to the one on the pack, {CommitKey}.";
    }

    private void MoveInMrp(int delta) =>
        SelectedMrpIndex = Math.Clamp(_selectedMrpIndex + delta, 0, Math.Max(0, MrpChoices.Count - 1));

    private void CommitMrp()
    {
        if (MrpChoices.Count == 0)
        {
            Mode = BillingMode.Billing;
            return;
        }

        var choice = MrpChoices[Math.Clamp(_selectedMrpIndex, 0, MrpChoices.Count - 1)];
        _lastMrpChoice[choice.Item.Id] = choice.IsOlder;

        MrpChoices.Clear();
        Mode = BillingMode.Billing;

        AddItem(choice.Item);

        // Kept so the sale can count the older packs off when it goes through.
        if (choice.IsOlder && _bill.Lines.Count > 0)
            _olderMrpLines.Add(_bill.Lines[^1]);

        if (choice.IsOlder)
            StatusMessage = $"{choice.Item.Name} at the older MRP, {Show.Money(choice.Item.Mrp)}.";
    }

    private void BackOutOfMrp()
    {
        MrpChoices.Clear();
        Mode = BillingMode.Billing;
        StatusMessage = "Nothing added.";
    }

    /// <summary>The lines on the bill sold at an older MRP, taken before the sale clears the bill.</summary>
    private List<(long ItemId, decimal Mrp, decimal Quantity)> OlderMrpSold() =>
        [.. _bill.Lines.Where(_olderMrpLines.Contains).Select(l => (l.ItemId, l.Mrp, l.Quantity))];

    /// <summary>
    /// Counts the older packs a sale took off what is left of them. Never fails the sale: the sale
    /// is made, and a count that cannot be written only means the question is asked a little longer.
    /// </summary>
    private void CountOlderMrpSold(List<(long ItemId, decimal Mrp, decimal Quantity)> sold)
    {
        _olderMrpLines.Clear();

        foreach (var (itemId, mrp, quantity) in sold)
        {
            try
            {
                if (_items.SoldAtOlderMrp(itemId, mrp, quantity) is null)
                    _lastMrpChoice.Remove(itemId);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            {
            }
        }
    }
}
