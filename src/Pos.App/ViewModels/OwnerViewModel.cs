using System.Collections.ObjectModel;
using System.Globalization;
using Pos.Core.Analytics;
using Pos.Core.Configuration;
using Pos.Core.Domain;
using Pos.Core.Domain.Printing;

namespace Pos.App.ViewModels;

/// <summary>One bar in the hourly chart, already scaled to the tallest.</summary>
public sealed record HourBar(string Label, decimal Amount, double Fraction, string Tooltip);

/// <summary>One day in the trend, already scaled to the best day in the window.</summary>
/// <remarks>
/// Separate from <see cref="HourBar"/> only so the two cannot be bound to each other's chart by
/// accident: a day labelled "09" beside an hour labelled "09" would look like data, not a mistake.
/// </remarks>
public sealed record TrendBar(string Label, decimal Amount, double Fraction, string Tooltip);

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
    private readonly Func<int, string, string?>? _saveWebPage;
    private readonly Func<ReceiptLayout, string?>? _applyReceiptLayout;
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
        Func<PinCredential?, string?> applyPin,

        // Writes the figures now on screen as a web page, for sending to an accountant. Optional so
        // the screen still builds on a lane wired without one; the button is off when it is absent.
        Func<int, string, string?>? saveWebPage = null,

        // Which bill the lane prints, and how to change it. Optional for the same reason: the
        // choice is not offered on a lane wired without somewhere to save it.
        ReceiptLayout receiptLayout = ReceiptLayout.Standard,
        Func<ReceiptLayout, string?>? applyReceiptLayout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(laneId);
        ArgumentNullException.ThrowIfNull(gather);

        _laneId = laneId;
        _stock = stock ?? throw new ArgumentNullException(nameof(stock));
        _applyTaxMode = applyTaxMode ?? throw new ArgumentNullException(nameof(applyTaxMode));
        _applyPin = applyPin ?? throw new ArgumentNullException(nameof(applyPin));
        _saveWebPage = saveWebPage;
        _applyReceiptLayout = applyReceiptLayout;
        _gather = () => gather(_days);

        TaxMode = taxMode;
        IsPinSet = isPinSet;
        ReceiptLayout = receiptLayout;
    }

    // ---- What is on screen -----------------------------------------------------------------------

    public string LaneId => _laneId;

    public ObservableCollection<HourBar> Hourly { get; } = [];
    public ObservableCollection<TrendBar> Daily { get; } = [];
    public ObservableCollection<RankedRow> TopItems { get; } = [];
    public ObservableCollection<RankedRow> Tenders { get; } = [];
    public ObservableCollection<RankedRow> Categories { get; } = [];
    public ObservableCollection<RankedRow> BestMargins { get; } = [];
    public ObservableCollection<RankedRow> WorstMargins { get; } = [];
    public ObservableCollection<GstSlab> GstSlabs { get; } = [];
    public ObservableCollection<StockLevel> Stock { get; } = [];

    public string PeriodNetSales { get; private set; } = "0.00";
    public string PeriodBills { get; private set; } = "0";
    public string PeriodCash { get; private set; } = "0.00";
    /// <summary>Card and UPI only: the money that should reach the bank.</summary>
    public string PeriodBank { get; private set; } = "0.00";

    /// <summary>
    /// What was not paid in money that reaches anybody: owed on store credit, or paid in points.
    /// Empty when there was neither, so the card does not carry a line of zeroes every day.
    /// </summary>
    public string PeriodNotBanked { get; private set; } = string.Empty;
    public string PeriodDiscount { get; private set; } = "0.00";

    /// <summary>What a customer spends per visit — computed already, never shown until now.</summary>
    public string PeriodAverageBasket { get; private set; } = "0.00";

    public string TodayNetSales { get; private set; } = "0.00";
    public string TodayBills { get; private set; } = "0";

    // ---- What the shop earned, as opposed to what it took ----------------------------------------

    /// <summary>Takings less cost, over the items that carried a cost price.</summary>
    public string PeriodProfit { get; private set; } = "0.00";

    /// <summary>That profit as a share of what those items sold for.</summary>
    public string PeriodMargin { get; private set; } = "—";

    /// <summary>
    /// How much of the window's takings the margin figures can honestly speak for.
    /// </summary>
    /// <remarks>
    /// Shown beside every margin on this screen rather than tucked away. An owner who reads a margin
    /// built on a third of the catalogue and believes it covers the shop will make a confident bad
    /// decision, and the figure itself gives no hint either way.
    /// </remarks>
    public string MarginCoverage { get; private set; } = string.Empty;

    /// <summary>False when no item sold in the window carried a cost price at all.</summary>
    public bool HasMargins { get; private set; }

    // ---- Cancelled sales -------------------------------------------------------------------------

    public string VoidLine { get; private set; } = string.Empty;
    public bool HasVoids { get; private set; }

    // ---- Who is buying ---------------------------------------------------------------------------

    public string CustomerLine { get; private set; } = string.Empty;
    public string ReturningLine { get; private set; } = string.Empty;

    // ---- Loyalty, and what it owes ---------------------------------------------------------------

    public string PointsLine { get; private set; } = string.Empty;
    public string PointsOwed { get; private set; } = "0";

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

    /// <summary>Which of the two bill layouts the lane prints.</summary>
    public ReceiptLayout ReceiptLayout { get; private set; }

    /// <summary>Whether the layout can be changed from here at all.</summary>
    public bool CanChooseLayout => _applyReceiptLayout is not null;

    /// <summary>
    /// Switches the bill layout. Unlike the tax mode it needs no open bill to be finished first:
    /// both layouts print the same invoice, so nothing on the till has to agree with it.
    /// </summary>
    /// <returns>Null when it worked, or why it did not.</returns>
    public string? SetReceiptLayout(ReceiptLayout layout)
    {
        if (layout == ReceiptLayout)
            return null;

        if (_applyReceiptLayout is null)
            return "This lane has nowhere to save the layout.";

        var refused = _applyReceiptLayout(layout);

        // A save that failed still changed this session, and the message says so; the screen has
        // to show what the till will actually print.
        ReceiptLayout = layout;
        Raise(nameof(ReceiptLayout));

        Status = refused ?? (layout == ReceiptLayout.Compact
            ? "The next bill prints in the compact layout: item, quantity and amount, with one large total."
            : "The next bill prints in the standard layout: price, quantity and amount columns, with every tender.");

        return refused;
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
        PeriodBank = Money(d.Range.Bank);
        PeriodNotBanked = (d.Range.Credit, d.Range.PointsRedeemed) switch
        {
            (0m, 0m) => string.Empty,
            (var credit, 0m) => $"{Money(credit)} owed on store credit - not in the bank",
            (0m, var points) => $"{Money(points)} paid in points - not in the bank",
            (var credit, var points) => $"{Money(credit)} owed on store credit, {Money(points)} paid in points - neither in the bank",
        };
        PeriodDiscount = Money(d.Range.Discount);
        PeriodAverageBasket = Money(d.Range.AverageBasket);

        TodayNetSales = Money(d.Today.NetSales);
        TodayBills = d.Today.Bills.ToString("N0", Indian);

        ReadIn = $"read in {d.Elapsed.TotalMilliseconds:N0} ms";

        FillMargins(d);
        FillVoids(d);
        FillCustomers(d);
        FillPoints(d);

        foreach (var name in new[]
                 {
                     nameof(PeriodNetSales), nameof(PeriodBills), nameof(PeriodCash), nameof(PeriodBank), nameof(PeriodNotBanked),
                     nameof(PeriodDiscount), nameof(PeriodAverageBasket), nameof(TodayNetSales),
                     nameof(TodayBills), nameof(ReadIn),
                     nameof(PeriodProfit), nameof(PeriodMargin), nameof(MarginCoverage), nameof(HasMargins),
                     nameof(VoidLine), nameof(HasVoids),
                     nameof(CustomerLine), nameof(ReturningLine),
                     nameof(PointsLine), nameof(PointsOwed),
                 })
        {
            Raise(name);
        }

        // The trend. Bills per day would be a flatter, less useful line than what they came to, so
        // the bar is takings and the count rides along in the tooltip.
        Daily.Clear();
        var bestDay = d.Daily.Count == 0 ? 0m : d.Daily.Max(p => p.NetSales);

        // The series is dense — a day the shop did not trade is a zero rather than a gap, or the
        // chart would put Monday beside Thursday and read as a steady week. That makes ninety bars
        // at the longest setting, so only every nth one is labelled; the rest keep their bar and
        // their tooltip. Labels under all ninety would be a grey smear.
        var every = d.Daily.Count <= 31 ? 1 : (d.Daily.Count + 14) / 15;

        for (var i = 0; i < d.Daily.Count; i++)
        {
            var day = d.Daily[i];

            Daily.Add(new TrendBar(
                i % every == 0 ? day.Date.ToString("dd MMM", Indian) : string.Empty,
                day.NetSales,
                bestDay == 0m ? 0 : (double)(day.NetSales / bestDay),
                $"{day.Date.ToString("ddd dd MMM", Indian)} — {Money(day.NetSales)} over {day.Bills} bill(s)"
                + (day.Discount > 0m ? $", {Money(day.Discount)} discounted" : string.Empty)));
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

    /// <summary>
    /// What the shop earned, and an honest account of how much of the shop that covers.
    /// </summary>
    /// <remarks>
    /// Margins exist only where an item carried a cost price at the moment it was sold, which on a
    /// catalogue loaded without a <c>cost_price</c> column is nowhere at all. That case shows as a
    /// sentence saying so, not as a profit of zero — a shop reading zero would conclude it earned
    /// nothing rather than that nobody had told the software what anything cost.
    /// </remarks>
    private void FillMargins(DashboardData d)
    {
        var priced = d.Margins.Priced;

        HasMargins = priced.Count > 0;

        BestMargins.Clear();
        WorstMargins.Clear();

        if (!HasMargins)
        {
            PeriodProfit = "—";
            PeriodMargin = "—";
            MarginCoverage = d.Margins.UnpricedItems == 0
                ? "No item sold in this period carried a cost price, so there is nothing to work a margin from."
                : $"None of the {d.Margins.UnpricedItems} item(s) sold carried a cost price. Add a cost_price "
                  + "column to the catalogue and import it again to see what the shop earns.";
            return;
        }

        var profit = priced.Sum(i => i.Profit);
        var pricedSales = d.Margins.PricedSales;

        PeriodProfit = Money(profit);
        PeriodMargin = pricedSales == 0m
            ? "—"
            : (profit / pricedSales * 100m).ToString("N1", Indian) + "%";

        MarginCoverage = d.Margins.UnpricedItems == 0
            ? $"Covers all {Money(pricedSales)} of this period's takings."
            : $"Covers {d.Margins.Coverage.ToString("N1", Indian)}% of takings — {d.Margins.UnpricedItems} item(s) "
              + $"worth {Money(d.Margins.UnpricedSales)} carry no cost price and are left out.";

        // Ranked by what each item actually earned rather than by its percentage. A 60% margin on
        // something that sells twice a month is a worse use of shelf space than 8% on rice, and a
        // list ordered by percentage puts the wrong one at the top.
        Rank(BestMargins, priced.OrderByDescending(i => i.Profit).Take(6));
        Rank(WorstMargins, priced.OrderBy(i => i.Profit).Take(6));

        void Rank(ObservableCollection<RankedRow> into, IEnumerable<ItemPerformance> items)
        {
            var rows = items.ToList();
            var widest = rows.Count == 0 ? 0m : rows.Max(i => Math.Abs(i.Profit));

            foreach (var item in rows)
            {
                into.Add(new RankedRow(
                    item.Name,
                    $"{item.MarginPercent.ToString("N1", Indian)}% on {item.Quantity.ToString("0.###", Indian)} sold",
                    Money(item.Profit),
                    widest == 0m ? 0 : (double)(Math.Abs(item.Profit) / widest)));
            }
        }
    }

    /// <summary>
    /// Sales that were rung up and then cancelled.
    /// </summary>
    /// <remarks>
    /// Shown even when the figure is small. A void is the ordinary correction for a mistake at the
    /// counter, and it is also the shape a till fraud takes; the point of putting it on the owner's
    /// screen is that a number which climbs is visible without anybody going looking for it.
    /// </remarks>
    private void FillVoids(DashboardData d)
    {
        HasVoids = d.Voids.Count > 0;

        // Against everything rung up, not against what survived. A voided sale stops being a settled
        // bill the moment it is cancelled, so measuring it against settled bills alone would report
        // a shop that cancelled its only sale as having cancelled nothing out of nothing.
        var rungUp = d.Range.Bills + d.Voids.Count;

        VoidLine = d.Voids.Count == 0
            ? "No sale was cancelled in this period."
            : $"{d.Voids.Count} sale(s) cancelled, {Money(d.Voids.Value)} in all"
              + (rungUp == 0
                  ? "."
                  : $" — {((decimal)d.Voids.Count / rungUp * 100m).ToString("N1", Indian)}% of bills rung up.");
    }

    private void FillCustomers(DashboardData d)
    {
        var mix = d.Customers;

        CustomerLine = mix.TotalBills == 0
            ? "No bills in this period."
            : $"{mix.IdentifiedBills} of {mix.TotalBills} bill(s) went to somebody the shop knows "
              + $"({Money(mix.IdentifiedSales)}), the rest to walk-ins ({Money(mix.WalkInSales)}).";

        ReturningLine = mix.DistinctCustomers == 0
            ? "No customer was identified by mobile number, so there is nothing to tell about regulars."
            : $"{mix.DistinctCustomers} customer(s) seen, {mix.ReturningCustomers} of them more than once"
              + (mix.DistinctCustomers == 0
                  ? "."
                  : $" ({((decimal)mix.ReturningCustomers / mix.DistinctCustomers * 100m).ToString("N0", Indian)}% came back).");
    }

    /// <summary>
    /// Loyalty in and out, and what the scheme still owes.
    /// </summary>
    /// <remarks>
    /// <c>OutstandingBalance</c> is the one figure here that is a liability rather than a
    /// performance: every point enrolled customers hold can be spent against a future bill, and a
    /// shop that has never seen the total has no idea what it has promised away.
    /// </remarks>
    private void FillPoints(DashboardData d)
    {
        PointsOwed = d.Points.OutstandingBalance.ToString("N0", Indian);

        PointsLine = d.Points.Earned == 0 && d.Points.Redeemed == 0
            ? "No points were earned or spent in this period."
            : $"{d.Points.Earned.ToString("N0", Indian)} earned, "
              + $"{d.Points.Redeemed.ToString("N0", Indian)} spent in this period.";
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

    /// <summary>True when this lane can write the figures out as a page.</summary>
    public bool CanSaveWebPage => _saveWebPage is not null;

    /// <summary>
    /// Writes the figures now on screen as a web page, over the same period the screen is showing.
    /// </summary>
    /// <remarks>
    /// The page carries turnover, margins and cost prices, and it is an ordinary file once written —
    /// the PIN that guards this screen cannot follow it out. Whoever saves it chooses where, and the
    /// screen says so rather than leaving it somewhere predictable by default.
    /// </remarks>
    /// <returns>What went wrong, or null when it was written.</returns>
    public string? SaveAsWebPage(string path)
    {
        if (_saveWebPage is null)
            return "Saving the figures as a page is not set up on this lane.";

        if (string.IsNullOrWhiteSpace(path))
            return "Choose where to save it first.";

        var problem = _saveWebPage(_days, path);

        Status = problem ?? $"Saved the last {_days} days to {path}.";
        return problem;
    }

    private static string Money(decimal value) => value.ToString("N2", Indian);
}
