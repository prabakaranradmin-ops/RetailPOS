using System.Collections.ObjectModel;
using System.Globalization;
using Pos.Core.Domain;
using Pos.Core.Domain.Catalogue;
using Pos.Core.Domain.Import;

namespace Pos.App.ViewModels;

/// <summary>Where selling an item not in the catalogue has got to.</summary>
public enum OpenItemStage
{
    /// <summary>What it is, as the bill should say.</summary>
    Name = 0,

    /// <summary>What one costs the customer, tax included.</summary>
    Price = 1,

    /// <summary>The GST slab, and the HSN code where something suggests one.</summary>
    Rate = 2,
}

/// <summary>One GST slab the till offers for an item not in the catalogue.</summary>
/// <param name="Hsn">The HSN code that comes with it, or empty for none.</param>
public sealed record OpenItemRate(string Label, decimal Rate, string Hsn);

/// <summary>
/// Ctrl+I: an item on the shelf that is not in the catalogue, sold as a line of its own so the
/// queue does not wait, and left on the owner's list to add properly.
/// </summary>
/// <remarks>
/// <para>
/// Three steps: what it is, its price, its slab. A scan that matched nothing is kept as its barcode,
/// and a name typed into the search that found nothing becomes its name, so neither is typed twice.
/// </para>
/// <para>
/// The slab is never picked for the cashier. The list starts with nothing highlighted, so the first
/// <c>↓</c> is a choice made by looking; a slab guessed on the cashier's behalf would be tax charged
/// wrong with nobody having decided it. What the shop already sells under a similar name comes
/// first, with its HSN code, the way the owner's form suggests one.
/// </para>
/// </remarks>
public sealed partial class BillingViewModel
{
    /// <summary>How many HSN suggestions the till shows. The owner's form shows more; the counter needs few.</summary>
    private const int OpenItemSuggestions = 3;

    private OpenItemStage _openItemStage;
    private string _openItemName = string.Empty;
    private decimal _openItemPrice;
    private string? _openItemBarcode;
    private int _selectedOpenRateIndex = -1;
    private HsnSuggester? _hsnSuggester;

    public bool IsAddingOpenItem => _mode == BillingMode.OpenItem;

    /// <summary>The key that sells an item not in the catalogue, as this lane's keymap has it.</summary>
    public string OpenItemKey { get; set; } = "Ctrl+I";

    public OpenItemStage OpenItemStage
    {
        get => _openItemStage;
        private set
        {
            if (!Set(ref _openItemStage, value))
                return;

            Raise(nameof(OpenItemPrompt));
            Raise(nameof(IsChoosingOpenRate));
            Raise(nameof(IsTypingOpenItem));
            Raise(nameof(OpenItemSoFar));
        }
    }

    /// <summary>The name or price box is showing.</summary>
    public bool IsTypingOpenItem => IsAddingOpenItem && _openItemStage != OpenItemStage.Rate;

    /// <summary>The slab list is showing.</summary>
    public bool IsChoosingOpenRate => IsAddingOpenItem && _openItemStage == OpenItemStage.Rate;

    public ObservableCollection<OpenItemRate> OpenItemRates { get; } = [];

    /// <summary>The slab highlighted, or -1 before the cashier has picked one.</summary>
    public int SelectedOpenRateIndex
    {
        get => _selectedOpenRateIndex;
        set => Set(ref _selectedOpenRateIndex, OpenItemRates.Count == 0 ? -1 : Math.Clamp(value, -1, OpenItemRates.Count - 1));
    }

    public string OpenItemPrompt => _openItemStage switch
    {
        OpenItemStage.Name => "What is it? As the bill should say it.",
        OpenItemStage.Price => "What does one cost the customer, tax included?",
        _ => "Which GST slab? Look before you pick: the till does not guess the tax.",
    };

    /// <summary>What has been settled so far, above the box: the barcode, then the name, then the price.</summary>
    public string OpenItemSoFar
    {
        get
        {
            var parts = new List<string>();

            if (_openItemBarcode is { } barcode)
                parts.Add($"Barcode {barcode}, not in the catalogue");

            if (_openItemStage > OpenItemStage.Name)
                parts.Add(_openItemName);

            if (_openItemStage > OpenItemStage.Price)
                parts.Add(Show.Money(_openItemPrice));

            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>
    /// Ctrl+I: an item not in the catalogue. Whatever is in the search box is carried in - a barcode
    /// that matched nothing as the barcode, words as the name.
    /// </summary>
    public void AddOpenItem()
    {
        ClearPendingConfirmations();
        CancelEdit();

        if (Mode != BillingMode.Billing)
        {
            StatusMessage = "Finish what is open first.";
            return;
        }

        var typed = _searchText.Trim();

        _openItemBarcode = OpenItem.LooksLikeBarcode(typed) ? typed : null;
        _openItemName = string.Empty;
        _openItemPrice = 0m;
        OpenItemRates.Clear();
        _selectedOpenRateIndex = -1;

        ClearSearch();

        OpenItemStage = OpenItemStage.Name;
        Mode = BillingMode.OpenItem;
        EditBuffer = _openItemBarcode is null ? typed : string.Empty;
        RaiseOpenItem();

        StatusMessage = _openItemBarcode is { } code
            ? $"{code} is not in the catalogue. Type what it is, then its price; the owner is told to add it."
            : "An item not in the catalogue: what it is, then its price. The owner is told to add it.";
    }

    private void CommitOpenItem()
    {
        switch (_openItemStage)
        {
            case OpenItemStage.Name:
                if (OpenItem.NameProblem(EditBuffer) is { } nameProblem)
                {
                    StatusMessage = nameProblem;
                    return;
                }

                _openItemName = EditBuffer.Trim();
                OpenItemStage = OpenItemStage.Price;
                EditBuffer = string.Empty;
                StatusMessage = $"{_openItemName}: its price, as the customer pays it.";
                return;

            case OpenItemStage.Price:
                var typed = EditBuffer.Trim();

                if (!TryParseAmount(typed, out var price))
                {
                    StatusMessage = typed.Length == 0 ? "Type its price first." : $"'{typed}' is not a price.";
                    return;
                }

                if (OpenItem.PriceProblem(price) is { } priceProblem)
                {
                    StatusMessage = priceProblem;
                    return;
                }

                _openItemPrice = price;

                // A bill of supply charges no tax, so there is no slab to choose.
                if (_bill.TaxMode == TaxMode.Composition)
                {
                    FinishOpenItem(new OpenItemRate(string.Empty, 0m, string.Empty));
                    return;
                }

                FillOpenItemRates();
                OpenItemStage = OpenItemStage.Rate;
                EditBuffer = string.Empty;
                StatusMessage = $"{_openItemName} at {Show.Money(price)}. Down to its GST slab, then {CommitKey}.";
                return;

            default:
                if (_selectedOpenRateIndex < 0 || _selectedOpenRateIndex >= OpenItemRates.Count)
                {
                    StatusMessage = "Pick its GST slab with ↓ first. The till does not guess the tax.";
                    return;
                }

                FinishOpenItem(OpenItemRates[_selectedOpenRateIndex]);
                return;
        }
    }

    private void FinishOpenItem(OpenItemRate rate)
    {
        Item item;

        try
        {
            item = OpenItem.For(_openItemName, _openItemPrice, rate.Rate, rate.Hsn, _openItemBarcode);
        }
        catch (ArgumentException ex)
        {
            StatusMessage = ex.Message.Split(" (Parameter", StringSplitOptions.None)[0];
            return;
        }

        CloseOpenItem();
        AddItem(item);

        var tax = _bill.TaxMode == TaxMode.Composition ? string.Empty : $" at {HsnDirectory.Rate(rate.Rate)}% GST";
        StatusMessage = $"{item.Name} added{tax}. It is not in the catalogue, so the owner will see it to add.";
    }

    /// <summary>Esc goes back a step, so a price typed wrong is retyped rather than the whole item; from the name, out.</summary>
    private void BackOutOfOpenItem()
    {
        switch (_openItemStage)
        {
            case OpenItemStage.Rate:
                OpenItemStage = OpenItemStage.Price;
                EditBuffer = _openItemPrice.ToString("0.##", CultureInfo.InvariantCulture);
                OpenItemRates.Clear();
                StatusMessage = "Back to the price.";
                return;

            case OpenItemStage.Price:
                OpenItemStage = OpenItemStage.Name;
                EditBuffer = _openItemName;
                StatusMessage = "Back to the name.";
                return;

            default:
                CloseOpenItem();
                StatusMessage = "Nothing added.";
                return;
        }
    }

    private void MoveInOpenItem(int delta)
    {
        if (_openItemStage != OpenItemStage.Rate || OpenItemRates.Count == 0)
            return;

        // From nothing picked, the first press lands on an end of the list rather than skipping one.
        SelectedOpenRateIndex = _selectedOpenRateIndex < 0
            ? (delta > 0 ? 0 : OpenItemRates.Count - 1)
            : Math.Clamp(_selectedOpenRateIndex + delta, 0, OpenItemRates.Count - 1);
    }

    /// <summary>
    /// The slabs to choose from: what the shop already sells under a similar name, with its HSN code,
    /// then each slab in force with no code.
    /// </summary>
    private void FillOpenItemRates()
    {
        OpenItemRates.Clear();

        foreach (var suggestion in Suggest(_openItemName))
        {
            OpenItemRates.Add(new OpenItemRate(
                $"{HsnDirectory.Rate(suggestion.GstRate)}%  ·  HSN {suggestion.HsnCode}  ·  {suggestion.Reason}",
                suggestion.GstRate,
                suggestion.HsnCode));
        }

        foreach (var slab in OpenItem.Slabs)
            OpenItemRates.Add(new OpenItemRate($"{HsnDirectory.Rate(slab)}%  ·  no HSN code", slab, string.Empty));

        // Raised whether or not it changed: the list is new, and nothing in it is picked yet.
        _selectedOpenRateIndex = -1;
        Raise(nameof(SelectedOpenRateIndex));
    }

    /// <summary>
    /// HSN suggestions for the name, off the catalogue the till already searches. A catalogue that
    /// cannot be read costs the suggestions and nothing else: the plain slabs are still there.
    /// </summary>
    private IEnumerable<HsnSuggestion> Suggest(string name)
    {
        _hsnSuggester ??= new HsnSuggester(word => _items.Search(word));

        IReadOnlyList<HsnSuggestion> found;

        try
        {
            found = _hsnSuggester.For(name);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            return [];
        }

        return found
            .Where(s => s.HsnCode.Trim().Length > 0 && ItemCsvParser.ValidGstRates.Contains(s.GstRate))
            .Take(OpenItemSuggestions);
    }

    private void CloseOpenItem()
    {
        Mode = BillingMode.Billing;
        EditBuffer = string.Empty;
        OpenItemRates.Clear();
        _selectedOpenRateIndex = -1;
        _openItemBarcode = null;
        OpenItemStage = OpenItemStage.Name;
        RaiseOpenItem();
    }

    private void RaiseOpenItem()
    {
        Raise(nameof(IsTypingOpenItem));
        Raise(nameof(IsChoosingOpenRate));
        Raise(nameof(OpenItemPrompt));
        Raise(nameof(OpenItemSoFar));
        Raise(nameof(SelectedOpenRateIndex));
    }
}
