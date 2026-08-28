using System.Collections.ObjectModel;
using System.Globalization;
using Pos.Core.Analytics;
using Pos.Core.Configuration;
using Pos.Core.Domain;

namespace Pos.App.ViewModels;

/// <summary>One bar in the hourly chart, already scaled to the tallest.</summary>
public sealed record HourBar(string Label, decimal Amount, double Fraction, string Tooltip);

/// <summary>One row of a ranked list with a bar beside it.</summary>
public sealed record RankedRow(string Name, string Detail, string Amount, double Fraction);

/// <summary>
/// The owner's screen: what the shop took, what needs reordering, and the two settings an owner
/// should be able to change without opening a text editor.
/// </summary>
/// <remarks>
/// This exists because the figures were previously only reachable from a command line. A shopkeeper
/// is not going to open a terminal to find out what to order, and a feature nobody reaches is not a
/// feature. The command-line versions stay for support and for the acceptance run, but nothing here
/// requires them.
/// </remarks>
public sealed class OwnerViewModel : ObservableObject
{
    private static readonly CultureInfo Indian = CultureInfo.GetCultureInfo("en-IN");

    private readonly Func<DashboardData> _gather;
    private readonly IStockStore _stock;
    private readonly Func<TaxMode, string?> _applyTaxMode;
    private readonly Func<PinCredential?, string?> _applyPin;
    private readonly string _laneId;

    private int _days = 30;
    private bool _busy;
    private string _status = string.Empty;
    private bool _lowOnly = true;
    private StockLevel? _selectedStock;
    private string _newQuantity = string.Empty;
    private string _adjustReason = string.Empty;

    public OwnerViewModel(
        string laneId,
        Func<int, DashboardData> gather,
        IStockStore stock,
        TaxMode taxMode,
        bool isPinSet,
        Func<TaxMode, string?> applyTaxMode,
        Func<PinCredential?, string?> applyPin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(laneId);
        ArgumentNullException.ThrowIfNull(gather);

        _laneId = laneId;
        _stock = stock ?? throw new ArgumentNullException(nameof(stock));
        _applyTaxMode = applyTaxMode ?? throw new ArgumentNullException(nameof(applyTaxMode));
        _applyPin = applyPin ?? throw new ArgumentNullException(nameof(applyPin));
        _gather = () => gather(_days);

        TaxMode = taxMode;
        IsPinSet = isPinSet;
    }

    // ---- What is on screen -----------------------------------------------------------------------

    public string LaneId => _laneId;

    public ObservableCollection<HourBar> Hourly { get; } = [];
    public ObservableCollection<RankedRow> TopItems { get; } = [];
    public ObservableCollection<RankedRow> Tenders { get; } = [];
    public ObservableCollection<RankedRow> Categories { get; } = [];
    public ObservableCollection<GstSlab> GstSlabs { get; } = [];
    public ObservableCollection<StockLevel> Stock { get; } = [];

    public string PeriodNetSales { get; private set; } = "0.00";
    public string PeriodBills { get; private set; } = "0";
    public string PeriodCash { get; private set; } = "0.00";
    public string PeriodDigital { get; private set; } = "0.00";
    public string PeriodDiscount { get; private set; } = "0.00";

    public string TodayNetSales { get; private set; } = "0.00";
    public string TodayBills { get; private set; } = "0";

    public string ReadIn { get; private set; } = string.Empty;

    /// <summary>Headline for the reorder list, so an empty one says which kind of empty it is.</summary>
    public string StockHeadline { get; private set; } = string.Empty;

    public int LowCount { get; private set; }
    public int OutCount { get; private set; }

    public bool ShowsTax => TaxMode == TaxMode.Gst;

    // ---- Controls --------------------------------------------------------------------------------

    /// <summary>How many days the figures cover. 7, 30 or 90 from the screen.</summary>
    public int Days
    {
        get => _days;
        set
        {
            if (Set(ref _days, value))
                Refresh();
        }
    }

    public bool LowOnly
    {
        get => _lowOnly;
        set
        {
            if (Set(ref _lowOnly, value))
                LoadStock();
        }
    }

    public bool IsBusy
    {
        get => _busy;
        private set => Set(ref _busy, value);
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public StockLevel? SelectedStock
    {
        get => _selectedStock;
        set
        {
            if (!Set(ref _selectedStock, value))
                return;

            // Prefill with what is there now, so correcting a count is a small edit rather than a
            // number typed from nothing — and so a mis-click cannot silently write a stale figure.
            NewQuantity = value is null ? string.Empty : value.Quantity.ToString("0.###", CultureInfo.InvariantCulture);
            Raise(nameof(CanAdjust));
            Raise(nameof(AdjustTarget));
        }
    }

    public string NewQuantity
    {
        get => _newQuantity;
        set
        {
            if (Set(ref _newQuantity, value))
                Raise(nameof(CanAdjust));
        }
    }

    public string AdjustReason
    {
        get => _adjustReason;
        set => Set(ref _adjustReason, value);
    }

    public string AdjustTarget => SelectedStock is { } level
        ? $"{level.Name}  —  counted {level.Quantity.ToString("0.###", Indian)} now"
        : "Pick an item from the list.";

    public bool CanAdjust =>
        SelectedStock is not null &&
        decimal.TryParse(NewQuantity, NumberStyles.Number, CultureInfo.InvariantCulture, out var q) &&
        q >= 0m;

    // ---- Settings --------------------------------------------------------------------------------

    public TaxMode TaxMode { get; private set; }

    public bool IsPinSet { get; private set; }

    /// <summary>
    /// Switches between issuing tax invoices and bills of supply.
    /// </summary>
    /// <returns>Null when it worked, or why it did not.</returns>
    public string? SetTaxMode(TaxMode mode)
    {
        if (mode == TaxMode)
            return null;

        var refused = _applyTaxMode(mode);

        if (refused is not null)
        {
            Status = refused;
            return refused;
        }

        TaxMode = mode;
        Raise(nameof(TaxMode));
        Raise(nameof(ShowsTax));

        // Re-read before saying anything: Refresh clears the status line on success, so setting the
        // confirmation first would wipe it and leave the owner with no sign the switch had taken.
        // Nothing already sold changes — each bill records the mode it was issued under.
        Refresh();

        Status = mode == TaxMode.Composition
            ? "This lane now issues a BILL OF SUPPLY. No tax is charged or shown."
            : "This lane now issues a TAX INVOICE. GST is charged and shown.";

        return null;
    }

    /// <summary>Sets, changes or clears the PIN in front of this screen.</summary>
    public string? SetPin(string? pin)
    {
        if (pin is null)
        {
            var cleared = _applyPin(null);

            if (cleared is not null)
                return cleared;

            IsPinSet = false;
            Raise(nameof(IsPinSet));
            Status = "The PIN has been removed. Anyone at this till can open this screen.";
            return null;
        }

        if (DashboardLock.Rejection(pin) is { } why)
            return why;

        var failed = _applyPin(DashboardLock.Create(pin));

        if (failed is not null)
            return failed;

        IsPinSet = true;
        Raise(nameof(IsPinSet));
        Status = "Saved. This screen will ask for the PIN from now on.";
        return null;
    }

    // ---- Loading ---------------------------------------------------------------------------------

    public void Refresh()
    {
        IsBusy = true;

        try
        {
            Fill(_gather());
            LoadStock();
            Status = string.Empty;
        }
        catch (Exception ex)
        {
            // The owner's screen must not take the till down with it. A figure that cannot be read
            // is a message on this screen; the counter carries on selling either way.
            Status = $"Could not read the figures: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Fill(DashboardData d)
    {
        PeriodNetSales = Money(d.Range.NetSales);
        PeriodBills = d.Range.Bills.ToString("N0", Indian);
        PeriodCash = Money(d.Range.Cash);
        PeriodDigital = Money(d.Range.Digital);
        PeriodDiscount = Money(d.Range.Discount);

        TodayNetSales = Money(d.Today.NetSales);
        TodayBills = d.Today.Bills.ToString("N0", Indian);

        ReadIn = $"read in {d.Elapsed.TotalMilliseconds:N0} ms";

        foreach (var name in new[]
                 {
                     nameof(PeriodNetSales), nameof(PeriodBills), nameof(PeriodCash), nameof(PeriodDigital),
                     nameof(PeriodDiscount), nameof(TodayNetSales), nameof(TodayBills), nameof(ReadIn),
                 })
        {
            Raise(name);
        }

        Hourly.Clear();
        var busiest = d.Hourly.Count == 0 ? 0m : d.Hourly.Max(h => h.NetSales);

        foreach (var hour in d.Hourly)
        {
            Hourly.Add(new HourBar(
                $"{hour.Hour:00}",
                hour.NetSales,
                busiest == 0m ? 0 : (double)(hour.NetSales / busiest),
                $"{hour.Hour:00}:00 — {Money(hour.NetSales)} over {hour.Bills} bill(s)"));
        }

        TopItems.Clear();
        var best = d.TopItems.Count == 0 ? 0m : d.TopItems.Max(i => i.NetSales);

        foreach (var item in d.TopItems)
        {
            TopItems.Add(new RankedRow(
                item.Name,
                $"{item.Quantity.ToString("0.###", Indian)} {item.Unit.ToLowerInvariant()} over {item.Bills} bill(s)",
                Money(item.NetSales),
                best == 0m ? 0 : (double)(item.NetSales / best)));
        }

        Tenders.Clear();
        var biggestTender = d.Tenders.Count == 0 ? 0m : d.Tenders.Max(t => t.Amount);

        foreach (var tender in d.Tenders)
        {
            Tenders.Add(new RankedRow(
                tender.Tender,
                $"{tender.Count} bill(s)",
                Money(tender.Amount),
                biggestTender == 0m ? 0 : (double)(tender.Amount / biggestTender)));
        }

        Categories.Clear();
        var biggestCategory = d.Categories.Count == 0 ? 0m : d.Categories.Max(c => c.NetSales);

        foreach (var slice in d.Categories)
        {
            Categories.Add(new RankedRow(
                slice.Category,
                $"{slice.Lines} line(s)",
                Money(slice.NetSales),
                biggestCategory == 0m ? 0 : (double)(slice.NetSales / biggestCategory)));
        }

        GstSlabs.Clear();

        // On a composition lane every slab is zero. Showing a table of zeroes would read as a shop
        // that applied a nil rate, so the screen leaves the whole block out instead.
        if (ShowsTax)
        {
            foreach (var slab in d.GstSlabs)
                GstSlabs.Add(slab);
        }
    }

    private void LoadStock()
    {
        Stock.Clear();

        var levels = LowOnly ? _stock.ListLow(500) : _stock.List(500);

        foreach (var level in levels)
            Stock.Add(level);

        LowCount = levels.Count(l => l.IsLow);
        OutCount = levels.Count(l => l.IsOut);

        // An empty list has two very different meanings, and saying which one is the whole point.
        StockHeadline = _stock.List(1).Count == 0
            ? "No item in this catalogue is counted. Add a stock_qty column to the catalogue and import it again to start."
            : levels.Count == 0
                ? "Nothing is at or below its reorder level."
                : $"{levels.Count} item(s){(LowOnly ? " to reorder" : " counted")}, {OutCount} of them with none left.";

        Raise(nameof(StockHeadline));
        Raise(nameof(LowCount));
        Raise(nameof(OutCount));
    }

    /// <summary>Corrects the selected item's count, recording what it was changed from and why.</summary>
    public string? ApplyAdjustment()
    {
        if (SelectedStock is not { } level)
            return "Pick an item from the list first.";

        if (!decimal.TryParse(NewQuantity, NumberStyles.Number, CultureInfo.InvariantCulture, out var quantity) || quantity < 0m)
            return $"'{NewQuantity}' is not a quantity.";

        var before = level.Quantity;
        var after = _stock.Set(level.ItemId, quantity, StockReason.Adjust, _laneId,
            string.IsNullOrWhiteSpace(AdjustReason) ? null : AdjustReason.Trim());

        if (after is null)
            return $"{level.Name} is not counted, so there is nothing to correct.";

        Status = $"{level.Name}: {before.ToString("0.###", Indian)} → {after.Value.ToString("0.###", Indian)}.";
        AdjustReason = string.Empty;

        LoadStock();
        return null;
    }

    private static string Money(decimal value) => value.ToString("N2", Indian);
}
