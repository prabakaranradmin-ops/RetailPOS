using System.Collections.ObjectModel;
using Pos.Core.Domain;
using Pos.Core.Domain.Catalogue;

namespace Pos.App.ViewModels;

/// <summary>One thing sold at the till that is not in the catalogue, however many times it was sold.</summary>
public sealed class OpenItemRow(OpenItemGroup group)
{
    public OpenItemGroup Group { get; } = group ?? throw new ArgumentNullException(nameof(group));

    public string Name => Group.Latest.Name;

    /// <summary>"₹45.00 · 18% · HSN 3808", or "no HSN code" where none was suggested.</summary>
    public string Sold
    {
        get
        {
            var latest = Group.Latest;
            var code = latest.Hsn.Trim().Length > 0 ? $"HSN {latest.Hsn}" : "no HSN code";
            var times = Group.Times == 1 ? "sold once" : $"sold {Group.Times} times";

            return $"{Show.Money(latest.Price)}  ·  {HsnDirectory.Rate(latest.GstRate)}%  ·  {code}  ·  {times}";
        }
    }

    /// <summary>When it last sold and on which bill, who sold it, and the barcode a scan read.</summary>
    public string Detail
    {
        get
        {
            var latest = Group.Latest;
            var parts = new List<string> { $"last {Show.DateAndTime(latest.SoldAt)} on {latest.InvoiceNo}" };

            if (Group.Cashiers.Count > 0)
                parts.Add("by " + string.Join(", ", Group.Cashiers));

            if (latest.Barcode is { Length: > 0 } barcode)
                parts.Add("barcode " + barcode);

            return string.Join("  ·  ", parts);
        }
    }
}

/// <summary>
/// The owner's list of what the till sold that is not in the catalogue: each to add properly, or to
/// leave out.
/// </summary>
/// <remarks>
/// Adding goes through the form under the list, the same one the owner adds any item with, filled
/// in from the till - so an item sold this way meets every check a catalogue item does before it
/// becomes one. Only once that form has added it does the list let it go.
/// </remarks>
public sealed class OpenItemsViewModel : ObservableObject
{
    private readonly IOpenItemStore _store;
    private readonly NewItemViewModel _newItem;
    private readonly TimeProvider _clock;

    private OpenItemRow? _selected;
    private OpenItemRow? _adding;
    private string _status = string.Empty;

    public OpenItemsViewModel(IOpenItemStore store, NewItemViewModel newItem, TimeProvider? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _newItem = newItem ?? throw new ArgumentNullException(nameof(newItem));
        _clock = clock ?? TimeProvider.System;

        _newItem.Added += (_, _) => Added();
    }

    public ObservableCollection<OpenItemRow> Rows { get; } = [];

    public OpenItemRow? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
                Raise(nameof(CanAct));
        }
    }

    public bool CanAct => _selected is not null;

    public bool HasWaiting => Rows.Count > 0;

    /// <summary>A line over the list: how many, and what to do with them.</summary>
    public string Summary
    {
        get
        {
            if (Rows.Count == 0)
                return "Nothing sold at the till is waiting to be added.";

            var sales = Rows.Sum(r => r.Group.Times);
            var things = Rows.Count == 1 ? "1 item" : $"{Rows.Count} items";
            var times = sales == Rows.Count ? string.Empty : $", {sales} sales in all";

            return $"{things} sold at the till {(Rows.Count == 1 ? "is" : "are")} not in the catalogue{times}. " +
                   "Pick one and add it, so the till finds it next time, or take it off the list if the shop will not stock it.";
        }
    }

    /// <summary>For the figures tab, so the owner hears of them without opening the catalogue. Empty when there are none.</summary>
    public string Notice => Rows.Count == 0
        ? string.Empty
        : $"{(Rows.Count == 1 ? "1 item" : $"{Rows.Count} items")} sold at the till {(Rows.Count == 1 ? "is" : "are")} not in the catalogue yet. Ctrl+3 to add {(Rows.Count == 1 ? "it" : "them")}.";

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>Reads the list afresh, keeping the same item picked where it is still there.</summary>
    public void Load()
    {
        var picked = _selected?.Group.Latest.LineId;

        IReadOnlyList<OpenItemGroup> groups;

        try
        {
            groups = OpenItemGroup.Of(_store.Waiting());
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            // The owner's screen must not take the till down with it.
            Status = $"The list could not be read: {ex.Message}";
            groups = [];
        }

        Rows.Clear();

        foreach (var group in groups)
            Rows.Add(new OpenItemRow(group));

        Selected = Rows.FirstOrDefault(r => r.Group.LineIds.Contains(picked ?? -1)) ?? Rows.FirstOrDefault();

        if (_adding is not null && !Rows.Any(r => r.Group.Latest.LineId == _adding.Group.Latest.LineId))
            _adding = null;

        Raise(nameof(HasWaiting));
        Raise(nameof(Summary));
        Raise(nameof(Notice));
    }

    /// <summary>Fills the add-an-item form from the one picked. It leaves the list once the form has added it.</summary>
    public bool StartAdding()
    {
        if (_selected is not { } row)
        {
            Status = "Pick one from the list first.";
            return false;
        }

        _adding = row;
        _newItem.StartFrom(row.Group.Latest);

        Status = $"{row.Name} is in the form below. Give it your own SKU, check it, and Add it.";
        return true;
    }

    /// <summary>The form was cleared or filled with something else: what it adds next is not this.</summary>
    public void ForgetAdding() => _adding = null;

    /// <summary>Takes the picked one off the list without adding it: a one-off the shop will not stock.</summary>
    public bool LeaveOut()
    {
        if (_selected is not { } row)
        {
            Status = "Pick one from the list first.";
            return false;
        }

        if (!DealtWith(row, addedAs: null))
            return false;

        Status = $"{row.Name} is off the list. Its sales stay on the bills as they were.";
        Load();
        return true;
    }

    private void Added()
    {
        if (_adding is not { } row)
            return;

        _adding = null;

        if (!DealtWith(row, _newItem.LastAddedSku))
            return;

        Status = $"{row.Name} is in the catalogue now and off this list.";
        Load();
    }

    private bool DealtWith(OpenItemRow row, string? addedAs)
    {
        try
        {
            _store.DealtWith(row.Group.LineIds, _clock.GetLocalNow(), addedAs);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            Status = $"It could not be taken off the list: {ex.Message}";
            return false;
        }
    }
}
