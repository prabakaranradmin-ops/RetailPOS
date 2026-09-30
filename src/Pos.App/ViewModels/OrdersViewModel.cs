using System.Collections.ObjectModel;
using System.Globalization;
using Pos.Core.Analytics;
using Pos.Core.Domain;

namespace Pos.App.ViewModels;

/// <summary>
/// The owner's Orders tab: what to order, from whom, and how long the shelf will last meanwhile.
/// </summary>
/// <remarks>
/// Nothing here writes to the books. It reads them, copies an order for pasting into a message to a
/// supplier, and saves the list where the owner says. Sending it is the owner's, from their own
/// phone: the till stays offline.
/// </remarks>
public sealed class OrdersViewModel : ObservableObject
{
    private static readonly CultureInfo Indian = CultureInfo.GetCultureInfo("en-IN");

    private readonly Func<int, OrderList> _gather;
    private readonly Action<string> _copy;
    private readonly Func<int, string?>? _saveCoverDays;
    private readonly string _shopName;
    private readonly Func<DateOnly> _today;

    private OrderList? _list;
    private SupplierOrder? _selected;
    private int _coverDays;
    private string _coverDaysText;
    private string _status = string.Empty;

    /// <param name="gather">Reads the order list for a cover of so many days.</param>
    /// <param name="copy">Puts text on the clipboard.</param>
    /// <param name="saveCoverDays">Keeps a changed cover for next time; null when it cannot be kept. Returns why not, or null.</param>
    public OrdersViewModel(
        Func<int, OrderList> gather,
        Action<string> copy,
        string shopName,
        int coverDays = Reorder.DefaultCoverDays,
        Func<int, string?>? saveCoverDays = null,
        Func<DateOnly>? today = null)
    {
        _gather = gather ?? throw new ArgumentNullException(nameof(gather));
        _copy = copy ?? throw new ArgumentNullException(nameof(copy));
        _shopName = string.IsNullOrWhiteSpace(shopName) ? "the shop" : shopName.Trim();
        _saveCoverDays = saveCoverDays;
        _today = today ?? (() => DateOnly.FromDateTime(DateTime.Today));
        _coverDays = Reorder.IsValidCoverDays(coverDays) ? coverDays : Reorder.DefaultCoverDays;
        _coverDaysText = _coverDays.ToString(CultureInfo.InvariantCulture);
    }

    public ObservableCollection<SupplierOrder> Suppliers { get; } = [];

    /// <summary>The lines of the supplier picked on the left.</summary>
    public ObservableCollection<ReorderLine> Lines { get; } = [];

    public bool IsLoaded => _list is not null;

    public int CoverDays => _coverDays;

    /// <summary>What is typed in the days box, before it is applied.</summary>
    public string CoverDaysText
    {
        get => _coverDaysText;
        set => Set(ref _coverDaysText, value ?? string.Empty);
    }

    public SupplierOrder? SelectedSupplier
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value))
                return;

            Lines.Clear();

            foreach (var line in value?.Lines ?? [])
                Lines.Add(line);

            Raise(nameof(SupplierHeading));
            Raise(nameof(EstimateLine));
            Raise(nameof(CanCopy));
        }
    }

    /// <summary>What the list is, or which kind of empty it is.</summary>
    public string Headline => _list switch
    {
        null => string.Empty,
        { IsEmpty: true } => $"Nothing needs ordering: every counted item lasts {_coverDays} days or more at the rate it sells, and nothing is low.",
        { } list => $"{Plural.Of(list.Lines, "item")} to order from {Plural.Of(list.Suppliers.Count, "supplier")}, to last {_coverDays} days at the rate each sold over the last {Plural.Of(list.DaysMeasured, "day")}.",
    };

    public string SupplierHeading => _selected is { } s
        ? s.Phone is { Length: > 0 } phone ? $"{s.Supplier}  ·  {phone}" : s.Supplier
        : "Pick a supplier on the left.";

    public string EstimateLine => _selected switch
    {
        null => string.Empty,
        { } s when s.Lines.All(l => l.LastRate is null) =>
            "No cost to show: none of these has been bought on a purchase bill yet, so there is no rate paid.",
        { } s => $"About {Money(s.EstimatedCost)} at the rates last paid, before tax"
                 + (s.EstimateIsComplete ? "." : " - some items have not been bought on a bill yet, so have no rate."),
    };

    public bool CanCopy => _selected is { Lines.Count: > 0 };

    public bool CanSave => _list is { IsEmpty: false };

    public string SuggestedFileName => $"order-list-{_today():yyyy-MM-dd}.csv";

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>Reads the list from the books, keeping the supplier that was picked if it is still there.</summary>
    public void Load()
    {
        var keep = _selected?.SupplierId;
        var keptNone = _selected is { SupplierId: null };

        try
        {
            _list = _gather(_coverDays);
            Status = string.Empty;
        }
        catch (Exception ex)
        {
            // The owner's screen must not take the till down with it.
            _list = null;
            Status = $"The order list could not be read: {ex.Message}";
        }

        Suppliers.Clear();

        foreach (var supplier in _list?.Suppliers ?? [])
            Suppliers.Add(supplier);

        _selected = null;
        SelectedSupplier = Suppliers.FirstOrDefault(s => s.SupplierId == keep && (keep is not null || keptNone))
                           ?? Suppliers.FirstOrDefault();

        Raise(nameof(IsLoaded));
        Raise(nameof(Headline));
        Raise(nameof(CanSave));
    }

    /// <summary>Applies the days typed in the box, keeps them for next time, and works the list out again.</summary>
    /// <returns>Null when it worked, or why not.</returns>
    public string? ApplyCoverDays()
    {
        if (!int.TryParse(CoverDaysText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var days) || !Reorder.IsValidCoverDays(days))
            return Status = $"'{CoverDaysText.Trim()}' is not a number of days. An order can cover 1 to 120 days.";

        _coverDays = days;
        CoverDaysText = days.ToString(CultureInfo.InvariantCulture);
        Raise(nameof(CoverDays));

        var notKept = _saveCoverDays?.Invoke(days);
        Load();

        Status = notKept is null
            ? $"Orders now cover {days} days."
            : $"Orders cover {days} days for now, but it could not be kept: {notKept}";

        return notKept;
    }

    /// <summary>Copies the picked supplier's order as a message, for pasting to them.</summary>
    public string? Copy()
    {
        if (_selected is not { Lines.Count: > 0 } supplier)
            return Status = "Pick a supplier with something to order.";

        try
        {
            _copy(supplier.Message(_shopName, _today()));
        }
        catch (Exception ex)
        {
            // Another program can be holding the clipboard.
            return Status = $"Could not copy it: {ex.Message}";
        }

        Status = supplier.SupplierId is null
            ? $"Copied {Plural.Of(supplier.Lines.Count, "item")} that {(supplier.Lines.Count == 1 ? "has" : "have")} not been bought on a bill yet."
            : $"Copied the order for {supplier.Supplier}: {Plural.Of(supplier.Lines.Count, "item")}. Paste it into a message to them.";

        return null;
    }

    /// <summary>Saves the whole list, every supplier, as a spreadsheet.</summary>
    public string? Save(string path)
    {
        if (_list is not { IsEmpty: false } list)
            return Status = "There is nothing to order, so nothing to save.";

        try
        {
            OrderListFiles.Write(list, path);
            Status = $"Saved {Plural.Of(list.Lines, "item")} for {Plural.Of(list.Suppliers.Count, "supplier")} to {path}.";
            return null;
        }
        catch (Exception ex)
        {
            return Status = $"Could not save it: {ex.Message}";
        }
    }

    private static string Money(decimal value) => Show.Money(value);
}
