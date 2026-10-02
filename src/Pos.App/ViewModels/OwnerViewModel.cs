using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using Pos.Core.Analytics;
using Pos.Core.Configuration;
using Pos.Core.Domain;
using Pos.Core.Domain.Import;
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
public sealed partial class OwnerViewModel : ObservableObject
{
    private static readonly CultureInfo Indian = CultureInfo.GetCultureInfo("en-IN");

    private readonly Func<DashboardData> _gather;
    private readonly IStockStore _stock;
    private readonly Func<TaxMode, string?> _applyTaxMode;
    private readonly Func<PinCredential?, string?> _applyPin;
    private readonly Func<int, string, string?>? _saveWebPage;
    private readonly Func<ReceiptLayout, string?>? _applyReceiptLayout;
    private readonly Func<decimal, string?>? _applyLowStockPercent;
    private readonly Func<IReadOnlyList<ExpiryWarning>>? _expiring;
    private readonly Func<IReadOnlyList<DeadStockItem>>? _deadStock;
    private readonly Func<string?, string?>? _applyUpiId;
    private readonly Func<ScreenTheme, string?>? _applyScreenTheme;
    private string _upiIdText;
    private readonly string _laneId;
    private bool _showExpiring;
    private bool _showDead;
    private string _lowStockPercentText;

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
        Func<ReceiptLayout, string?>? applyReceiptLayout = null,

        // The share of full an item counts as low at, and how to change it for the lane.
        decimal lowStockPercent = LowStock.DefaultPercent,
        Func<decimal, string?>? applyLowStockPercent = null,

        // Deliveries near their use-by date. Optional: a lane wired without it shows no such list.
        Func<IReadOnlyList<ExpiryWarning>>? expiring = null,

        // What has stopped selling. Optional for the same reason.
        Func<IReadOnlyList<DeadStockItem>>? deadStock = null,

        // The shop's UPI ID for the code with the amount, and how to change it for the lane.
        string? upiId = null,
        Func<string?, string?>? applyUpiId = null,

        // How the screens look, and how to change it for the lane.
        ScreenTheme screenTheme = ScreenTheme.Night,
        Func<ScreenTheme, string?>? applyScreenTheme = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(laneId);
        ArgumentNullException.ThrowIfNull(gather);

        _laneId = laneId;
        _stock = stock ?? throw new ArgumentNullException(nameof(stock));
        _applyTaxMode = applyTaxMode ?? throw new ArgumentNullException(nameof(applyTaxMode));
        _applyPin = applyPin ?? throw new ArgumentNullException(nameof(applyPin));
        _saveWebPage = saveWebPage;
        _applyReceiptLayout = applyReceiptLayout;
        _applyLowStockPercent = applyLowStockPercent;
        _expiring = expiring;
        _deadStock = deadStock;
        _applyUpiId = applyUpiId;
        _applyScreenTheme = applyScreenTheme;
        ScreenTheme = screenTheme;
        UpiId = string.IsNullOrWhiteSpace(upiId) ? null : upiId.Trim();
        _upiIdText = UpiId ?? string.Empty;
        _gather = () => gather(_days);

        TaxMode = taxMode;
        IsPinSet = isPinSet;
        ReceiptLayout = receiptLayout;
        LowStockPercent = lowStockPercent;
        _lowStockPercentText = Percent(lowStockPercent);
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

    // ---- The charts ------------------------------------------------------------------------------

    /// <summary>Takings day by day with a seven-day average over them.</summary>
    public Pos.App.Charts.CategoryChartData? SalesChart { get; private set; }

    /// <summary>The shop's day: takings by the hour, and the bills behind them.</summary>
    public Pos.App.Charts.CategoryChartData? HoursChart { get; private set; }

    /// <summary>The week against the hours, shaded by takings.</summary>
    public Pos.App.Charts.HeatmapData? WeekChart { get; private set; }

    /// <summary>How customers paid.</summary>
    public Pos.App.Charts.DonutData? TenderChart { get; private set; }

    /// <summary>Where the takings came from, by department.</summary>
    public Pos.App.Charts.DonutData? DepartmentChart { get; private set; }

    /// <summary>Every priced item, by how fast it sells against what it earns.</summary>
    public Pos.App.Charts.BubbleData? MarginChart { get; private set; }

    /// <summary>Loyalty points given and spent, day by day.</summary>
    public Pos.App.Charts.CategoryChartData? PointsChart { get; private set; }

    /// <summary>Takings day by day, for the line under the period's total.</summary>
    public IReadOnlyList<double> SalesSpark { get; private set; } = [];

    /// <summary>The best day in the period, said in a line under the total.</summary>
    public string BestDayLine { get; private set; } = string.Empty;

    /// <summary>Under the departments: how their item values differ from the bills' net sales, when they do.</summary>
    public string DepartmentNote { get; private set; } = string.Empty;

    /// <summary>What the charts cover: "The last 30 days".</summary>
    public string PeriodText => _days == 1 ? "Today" : $"The last {_days} days";

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

    /// <summary>What the shop spent that was not stock, and on what.</summary>
    public string ExpensesLine { get; private set; } = string.Empty;

    /// <summary>What the priced items earned, less the period's expenses. Said only when both are known.</summary>
    public string AfterExpensesLine { get; private set; } = string.Empty;

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

    /// <summary>The Stock tab showing deliveries near their date instead of the counts.</summary>
    public bool ShowExpiring
    {
        get => _showExpiring;
        set
        {
            if (!Set(ref _showExpiring, value))
                return;

            if (value)
                LoadExpiring();

            Raise(nameof(ShowsStockList));
            Raise(nameof(ListHeadline));
        }
    }

    /// <summary>The Stock tab showing what has stopped selling instead of the counts.</summary>
    public bool ShowDead
    {
        get => _showDead;
        set
        {
            if (!Set(ref _showDead, value))
                return;

            if (value)
                LoadDead();

            Raise(nameof(ShowsStockList));
            Raise(nameof(ListHeadline));
        }
    }

    public bool ShowsStockList => !_showExpiring && !_showDead;

    public bool CanShowDead => _deadStock is not null;

    /// <summary>Counted items on the shelf that have not sold for two months, most money first.</summary>
    public ObservableCollection<DeadStockItem> DeadStockItems { get; } = [];

    public string DeadHeadline { get; private set; } = string.Empty;

    private void LoadDead()
    {
        DeadStockItems.Clear();

        if (_deadStock is null)
        {
            DeadHeadline = "This lane does not read what has stopped selling.";
            Raise(nameof(DeadHeadline));
            return;
        }

        try
        {
            foreach (var item in _deadStock())
                DeadStockItems.Add(item);
        }
        catch (Exception ex)
        {
            DeadHeadline = $"The list could not be read: {ex.Message}";
            Raise(nameof(DeadHeadline));
            Raise(nameof(ListHeadline));
            return;
        }

        var tiedUp = DeadStockItems.Sum(i => i.TiedUp ?? 0m);
        var never = DeadStockItems.Count(i => i.NeverSold);

        DeadHeadline = DeadStockItems.Count == 0
            ? $"Everything counted on the shelf has sold in the last {DeadStock.Days} days."
            : $"{Plural.Of(DeadStockItems.Count, "item")} on the shelf not sold in {DeadStock.Days} days"
              + (never > 0 ? $", {never} never sold" : string.Empty)
              + (tiedUp > 0m ? $" - {Show.Money(tiedUp)} tied up in them at cost." : ".")
              + " Put them on offer, return them, or stop ordering them.";

        Raise(nameof(DeadHeadline));
        Raise(nameof(ListHeadline));
    }

    public bool CanShowExpiring => _expiring is not null;

    /// <summary>Deliveries within a month of their use-by date and probably still on the shelf.</summary>
    public ObservableCollection<ExpiryWarning> ExpiryWarnings { get; } = [];

    public string ExpiryHeadline { get; private set; } = string.Empty;

    /// <summary>What the Stock tab's line above the list says, for whichever list is showing.</summary>
    public string ListHeadline => _showExpiring ? ExpiryHeadline : _showDead ? DeadHeadline : StockHeadline;

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

    /// <summary>
    /// Clears the message line. Called on moving to another section, where a message about the
    /// last one would read as being about this one.
    /// </summary>
    public void ClearStatus() => Status = string.Empty;

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
            Raise(nameof(AdjustBlocker));
            Raise(nameof(AdjustTarget));
        }
    }

    public string NewQuantity
    {
        get => _newQuantity;
        set
        {
            if (!Set(ref _newQuantity, value))
                return;

            Raise(nameof(CanAdjust));
            Raise(nameof(AdjustBlocker));
        }
    }

    /// <summary>Why "Apply" is greyed out, beside it; empty once it can be pressed.</summary>
    public string AdjustBlocker => CanAdjust
        ? string.Empty
        : SelectedStock is null
            ? "Pick an item in the list first."
            : "Type the new count: a number, 0 or more.";

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

    /// <summary>How the screens look: one of the four looks, or following the time of day.</summary>
    public ScreenTheme ScreenTheme { get; private set; }

    /// <summary>Whether the look can be changed from here at all.</summary>
    public bool CanChooseScreenTheme => _applyScreenTheme is not null;

    /// <summary>
    /// Changes how every screen looks, at once - the till behind this screen included - and keeps
    /// the choice for the next time the lane starts.
    /// </summary>
    /// <returns>Null when it worked, or why it did not.</returns>
    public string? SetScreenTheme(ScreenTheme theme)
    {
        if (theme == ScreenTheme)
            return null;

        if (_applyScreenTheme is null)
            return "This lane has nowhere to save how the screens look.";

        var refused = _applyScreenTheme(theme);

        ScreenTheme = theme;
        Raise(nameof(ScreenTheme));

        static string Clock(TimeOnly time) => time.ToString("h tt", CultureInfo.InvariantCulture).ToLowerInvariant();

        Status = refused ?? theme switch
        {
            ScreenTheme.ByTimeOfDay =>
                $"The screens follow the time of day: the morning look from {Clock(ScreenThemes.MorningFrom)}, noon from {Clock(ScreenThemes.NoonFrom)}, evening from {Clock(ScreenThemes.EveningFrom)} and night from {Clock(ScreenThemes.NightFrom)}.",
            ScreenTheme.Morning => "The screens are in the morning look: light and warm, for the early daylight.",
            ScreenTheme.Noon => "The screens are in the noon look: the brightest and crispest, for sunlight on the screen.",
            ScreenTheme.Evening => "The screens are in the evening look: dim, for dusk.",
            _ => "The screens are in the night look: dark, for the shop's own lights.",
        };

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
            RaiseApprovals();
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
        RaiseApprovals();
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
            (var credit, 0m) => $"{Show.Money(credit)} on khata, not yet paid - not in the bank",
            (0m, var points) => $"{Show.Money(points)} paid in points - not in the bank",
            (var credit, var points) => $"{Show.Money(credit)} on khata, not yet paid, and {Show.Money(points)} paid in points - neither in the bank",
        };
        PeriodDiscount = Money(d.Range.Discount);
        PeriodAverageBasket = Money(d.Range.AverageBasket);

        TodayNetSales = Money(d.Today.NetSales);
        TodayBills = d.Today.Bills.ToString("N0", Indian);

        ReadIn = $"read in {d.Elapsed.TotalMilliseconds:N0} ms";

        FillMargins(d);
        FillExpenses(d);
        FillVoids(d);
        FillExceptions(d);
        FillDrawers(d);
        FillStockValue(d);
        FillCustomers(d);
        FillPoints(d);
        FillCharts(d);

        foreach (var name in new[]
                 {
                     nameof(PeriodNetSales), nameof(PeriodBills), nameof(PeriodCash), nameof(PeriodBank), nameof(PeriodNotBanked),
                     nameof(PeriodDiscount), nameof(PeriodAverageBasket), nameof(TodayNetSales),
                     nameof(TodayBills), nameof(ReadIn),
                     nameof(PeriodProfit), nameof(PeriodMargin), nameof(MarginCoverage), nameof(HasMargins),
                     nameof(ExpensesLine), nameof(AfterExpensesLine),
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
                i % every == 0 ? day.Date.ToString("d MMM", CultureInfo.InvariantCulture) : string.Empty,
                day.NetSales,
                bestDay == 0m ? 0 : (double)(day.NetSales / bestDay),
                $"{day.Date.ToString("ddd", CultureInfo.InvariantCulture)} {Show.Date(day.Date)} — {Show.Money(day.NetSales)} over {Plural.Of(day.Bills, "bill")}"
                + (day.Discount > 0m ? $", {Show.Money(day.Discount)} discounted" : string.Empty)));
        }

        Hourly.Clear();
        var busiest = d.Hourly.Count == 0 ? 0m : d.Hourly.Max(h => h.NetSales);

        foreach (var hour in d.Hourly)
        {
            Hourly.Add(new HourBar(
                $"{hour.Hour:00}",
                hour.NetSales,
                busiest == 0m ? 0 : (double)(hour.NetSales / busiest),
                $"{hour.Hour:00}:00 — {Show.Money(hour.NetSales)} over {Plural.Of(hour.Bills, "bill")}"));
        }

        TopItems.Clear();
        var best = d.TopItems.Count == 0 ? 0m : d.TopItems.Max(i => i.NetSales);

        foreach (var item in d.TopItems)
        {
            TopItems.Add(new RankedRow(
                item.Name,
                $"{item.Quantity.ToString("0.###", Indian)} {item.Unit.ToLowerInvariant()} over {Plural.Of(item.Bills, "bill")}",
                Money(item.NetSales),
                best == 0m ? 0 : (double)(item.NetSales / best)));
        }

        Tenders.Clear();
        var biggestTender = d.Tenders.Count == 0 ? 0m : d.Tenders.Max(t => t.Amount);

        foreach (var tender in d.Tenders)
        {
            Tenders.Add(new RankedRow(
                tender.Tender,
                Plural.Of(tender.Count, "bill"),
                Money(tender.Amount),
                biggestTender == 0m ? 0 : (double)(tender.Amount / biggestTender)));
        }

        Categories.Clear();
        var biggestCategory = d.Categories.Count == 0 ? 0m : d.Categories.Max(c => c.NetSales);

        foreach (var slice in d.Categories)
        {
            Categories.Add(new RankedRow(
                slice.Category,
                Plural.Of(slice.Lines, "line"),
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
    /// <summary>
    /// The period's expenses, and what is left of the earnings once they are paid.
    /// </summary>
    /// <remarks>
    /// Profit here is takings less the cost of the goods, over the items that carried a cost. Tea,
    /// the auto and the electricity bill come out of that too, and an owner comparing a margin with
    /// the bank balance needs them taken off - said as a sentence, with what it covers.
    /// </remarks>
    private void FillExpenses(DashboardData d)
    {
        ExpensesLine = d.Expenses.Count == 0
            ? "No expenses were recorded in this period. Record them at the till with Ctrl+M."
            : $"{Money(d.ExpensesTotal)} spent on the running of the shop: "
              + string.Join(", ", d.Expenses.Select(e => $"{e.Category.ToLowerInvariant()} {Money(e.Amount)}")) + ".";

        AfterExpensesLine = HasMargins && d.Expenses.Count > 0
            ? $"{Money(d.Margins.Priced.Sum(i => i.Profit) - d.ExpensesTotal)} earned after expenses, on the items with a cost price."
            : string.Empty;
    }

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
                : $"None of the {Plural.Of(d.Margins.UnpricedItems, "item")} sold carried a cost price. Give items their "
                  + "cost price in the catalogue (Ctrl+3) to see what the shop earns.";
            return;
        }

        var profit = priced.Sum(i => i.Profit);
        var pricedSales = d.Margins.PricedSales;

        PeriodProfit = Money(profit);
        PeriodMargin = pricedSales == 0m
            ? "—"
            : (profit / pricedSales * 100m).ToString("N1", Indian) + "%";

        // No figure for what is covered when it is everything. "Covers all 873.25" sat under net sales
        // of 873.00 - item values before the bills' round-off - and an owner who sees two totals for
        // the same days stops trusting both.
        MarginCoverage = d.Margins.UnpricedItems == 0
            ? "Covers every item sold in this period."
            : $"Covers {d.Margins.Coverage.ToString("N1", Indian)}% of takings — {Plural.Of(d.Margins.UnpricedItems, "item")} "
              + $"worth {Show.Money(d.Margins.UnpricedSales)} carry no cost price and are left out.";

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
            : $"{Plural.Of(d.Voids.Count, "sale")} cancelled, {Show.Money(d.Voids.Value)} in all"
              + (rungUp == 0
                  ? "."
                  : $" — {((decimal)d.Voids.Count / rungUp * 100m).ToString("N1", Indian)}% of bills rung up.");

        // Returns sit beside the cancellations: both are money rung up that the shop did not keep.
        if (d.Returns.Count > 0)
        {
            HasVoids = true;
            VoidLine += $" {Plural.Of(d.Returns.Count, "return")} on credit notes, {Show.Money(d.Returns.Value)} refunded.";
        }
    }

    private void FillCustomers(DashboardData d)
    {
        var mix = d.Customers;

        CustomerLine = mix.TotalBills == 0
            ? "No bills in this period."
            : $"{mix.IdentifiedBills} of {Plural.Of(mix.TotalBills, "bill")} went to somebody the shop knows "
              + $"({Show.Money(mix.IdentifiedSales)}), the rest to walk-ins ({Show.Money(mix.WalkInSales)}).";

        ReturningLine = mix.DistinctCustomers == 0
            ? "No customer was identified by mobile number, so there is nothing to tell about regulars."
            : $"{Plural.Of(mix.DistinctCustomers, "customer")} seen, {mix.ReturningCustomers} of them more than once"
              + (mix.DistinctCustomers == 0
                  ? "."
                  : $" ({((decimal)mix.ReturningCustomers / mix.DistinctCustomers * 100m).ToString("N0", Indian)}% came back).");
    }

    /// <summary>The same figures again, as charts. Built from the one gather, so they cannot disagree.</summary>
    private void FillCharts(DashboardData d)
    {
        SalesChart = OwnerCharts.SalesTrend(d.Daily);
        HoursChart = OwnerCharts.Hours(d.Hourly);
        WeekChart = OwnerCharts.Week(d.WeekdayByHour);
        TenderChart = OwnerCharts.Tenders(d.Tenders);
        DepartmentChart = OwnerCharts.Departments(d.Categories);
        MarginChart = OwnerCharts.Margins(d.Margins);
        PointsChart = OwnerCharts.Points(d.Points);
        SalesSpark = OwnerCharts.Spark(d.Daily);

        var best = d.Daily.Where(p => p.NetSales > 0m).MaxBy(p => p.NetSales);
        BestDayLine = best is null
            ? string.Empty
            : $"Best day {best.Date.ToString("ddd d MMM", CultureInfo.InvariantCulture)}, {Show.Money(best.NetSales)}";

        // The departments add up the lines, before each bill's round-off; the net sales above are the
        // bills as paid. Where the two differ the chart says by how much, rather than leaving an
        // owner to find a second total for the same days and wonder which one is wrong.
        var lines = d.Categories.Sum(c => c.NetSales);
        var roundOff = d.Range.NetSales - lines;
        DepartmentNote = d.Categories.Count > 0 && roundOff != 0m
            ? $"Item values, before the bills' round-off of {(roundOff > 0 ? "+" : string.Empty)}{Show.Money(roundOff)}: "
              + $"the bills came to {Show.Money(d.Range.NetSales)}."
            : string.Empty;

        foreach (var name in new[]
                 {
                     nameof(SalesChart), nameof(HoursChart), nameof(WeekChart), nameof(TenderChart),
                     nameof(DepartmentChart), nameof(MarginChart), nameof(PointsChart), nameof(SalesSpark),
                     nameof(BestDayLine), nameof(PeriodText), nameof(DepartmentNote),
                 })
        {
            Raise(name);
        }
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
            ? "No item is counted yet. Save a stock sheet, fill in the counts, and load it back to start."
            : levels.Count == 0
                ? $"Nothing is low — nothing is at its reorder level or down to {LowRuleText}."
                : $"{Plural.Of(levels.Count, "item")}{(LowOnly ? " to reorder" : " counted")}, {OutCount} of them with none left. Low means at its reorder level, or down to {LowRuleText}.";

        // Said here too, so a delivery going out of date is seen by an owner who only came to reorder.
        if (LoadExpiring() is var dated and > 0)
            StockHeadline += dated == 1 ? " 1 delivery is near its date — Alt+X." : $" {dated} deliveries are near their date — Alt+X.";

        Raise(nameof(StockHeadline));
        Raise(nameof(ListHeadline));
        Raise(nameof(LowCount));
        Raise(nameof(OutCount));
    }

    /// <summary>Reads the deliveries near their date. Returns how many.</summary>
    private int LoadExpiring()
    {
        ExpiryWarnings.Clear();

        if (_expiring is null)
        {
            ExpiryHeadline = "This lane does not read use-by dates.";
            Raise(nameof(ExpiryHeadline));
            return 0;
        }

        try
        {
            foreach (var warning in _expiring())
                ExpiryWarnings.Add(warning);
        }
        catch (Exception ex)
        {
            ExpiryHeadline = $"The dates could not be read: {ex.Message}";
            Raise(nameof(ExpiryHeadline));
            return 0;
        }

        var expired = ExpiryWarnings.Count(w => w.IsExpired);

        ExpiryHeadline = ExpiryWarnings.Count == 0
            ? $"Nothing on the shelf is within {Expiry.WarnDays} days of its date - as far as the use-by dates on the purchase bills say."
            : $"{ExpiryWarnings.Count} deliver{(ExpiryWarnings.Count == 1 ? "y" : "ies")} within {Expiry.WarnDays} days of their date and probably on the shelf"
              + (expired > 0 ? $", {expired} already past it." : ".")
              + " Worked out from the count: once old stock is off the shelf, correct the count and it drops off.";

        Raise(nameof(ExpiryHeadline));
        Raise(nameof(ListHeadline));
        return ExpiryWarnings.Count;
    }

    /// <summary>"10% of full", or what the list says when the share-of-full rule is off.</summary>
    private string LowRuleText => LowStockPercent > 0m
        ? $"{Percent(LowStockPercent)}% of full"
        : "(share of full switched off)";

    // ---- The UPI code with the amount ---------------------------------------------------------

    /// <summary>The UPI ID the till's codes are made out to, or null when none is set.</summary>
    public string? UpiId { get; private set; }

    /// <summary>What is typed in the Settings box, before it is saved.</summary>
    public string UpiIdText
    {
        get => _upiIdText;
        set => Set(ref _upiIdText, value);
    }

    public bool CanChangeUpiId => _applyUpiId is not null;

    /// <summary>What the card says about the code now.</summary>
    public string UpiState => UpiId is { } id
        ? $"Customers paying by UPI get a code for the exact amount, paid to {id}."
        : "Not set: UPI is taken against the shop's own printed code, and the customer types the amount.";

    /// <summary>Sets the UPI ID, or takes it out when the box is empty - which turns the code off.</summary>
    /// <returns>Null when it took, or why not.</returns>
    public string? SetUpiId()
    {
        if (_applyUpiId is null)
            return Status = "This lane has nowhere to save it.";

        var id = string.IsNullOrWhiteSpace(UpiIdText) ? null : UpiIdText.Trim();

        if (id is not null && UpiPayee.Problem(id) is { } problem)
            return Status = problem;

        if (_applyUpiId(id) is { } failed)
            return Status = failed;

        UpiId = id;
        UpiIdText = id ?? string.Empty;
        Raise(nameof(UpiId));
        Raise(nameof(UpiState));

        Status = id is null
            ? "The UPI code is off. UPI is taken against the shop's own printed code."
            : $"UPI payments now get a code for the exact amount, paid to {id}. Try one on the till: F12, then UPI.";

        return null;
    }

    // ---- When stock counts as low ------------------------------------------------------------

    /// <summary>The share of full at which an item with no reorder level counts as low.</summary>
    public decimal LowStockPercent { get; private set; }

    /// <summary>What is typed in the Settings box, before it is saved.</summary>
    public string LowStockPercentText
    {
        get => _lowStockPercentText;
        set => Set(ref _lowStockPercentText, value);
    }

    public bool CanChangeLowStockPercent => _applyLowStockPercent is not null;

    /// <summary>Changes when stock counts as low, for this screen, the till and the day-end report.</summary>
    /// <returns>Null when it took, or why not.</returns>
    public string? SetLowStockPercent()
    {
        if (_applyLowStockPercent is null)
            return Status = "This lane has nowhere to save it.";

        var text = LowStockPercentText.Trim().TrimEnd('%').Trim();

        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var percent) || !LowStock.IsValidPercent(percent))
            return Status = $"'{LowStockPercentText}' is not a share of full. Use a number from 1 to 99, or 0 to switch it off.";

        if (_applyLowStockPercent(percent) is { } problem)
            return Status = problem;

        LowStockPercent = percent;
        LowStockPercentText = Percent(percent);
        Raise(nameof(LowStockPercent));

        LoadStock();

        Status = percent > 0m
            ? $"An item with no reorder level now counts as low at {Percent(percent)}% of full."
            : "The share-of-full rule is off. Only items with a reorder level of their own are warned about.";

        return null;
    }

    private static string Percent(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    // ---- The stock sheet ---------------------------------------------------------------------

    /// <summary>What is wrong with the sheet just loaded, line by line. Empty when nothing is.</summary>
    public ObservableCollection<string> StockSheetProblems { get; } = [];

    public bool HasStockSheetProblems => StockSheetProblems.Count > 0;

    /// <summary>
    /// Writes a stock sheet of every active item, to fill in on a walk round the shelves.
    /// </summary>
    /// <returns>Null when it was written, or why not.</returns>
    public string? SaveStockSheet(string path)
    {
        try
        {
            var items = _stock.Sheet();

            // With a byte-order mark, so Excel opens Tamil item names as Tamil.
            File.WriteAllText(path, StockSheet.Write(items), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            Status = $"Saved a stock sheet of {Plural.Of(items.Count, "item")} to {path}. Write what you counted in the column headed new_count, save it, and load it back here.";
            return null;
        }
        catch (Exception ex)
        {
            return Status = $"Could not save it: {ex.Message}";
        }
    }

    /// <summary>
    /// Reads a filled-in sheet and works out what it would change. Writes nothing.
    /// </summary>
    /// <returns>The plan when the sheet is clean, or null when it is not — with the problems listed.</returns>
    public StockSheetPlan? CheckStockSheet(string path)
    {
        StockSheetProblems.Clear();

        StockSheetPlan plan;

        try
        {
            using var reader = ItemCsvParser.OpenText(path);
            plan = StockSheet.Read(reader, _stock.Sheet());
        }
        catch (Exception ex)
        {
            Status = $"Could not read it: {ex.Message}";
            Raise(nameof(HasStockSheetProblems));
            return null;
        }

        if (!plan.IsClean)
        {
            // All of them, not the first: the fix happens in the spreadsheet, in one sitting.
            foreach (var problem in plan.Problems.Take(50))
                StockSheetProblems.Add($"Line {problem.Line}, {problem.Column}: {problem.Problem}");

            if (plan.Problems.Count > 50)
                StockSheetProblems.Add($"... and {plan.Problems.Count - 50} more.");

            Status = plan.Problems.Count == 1
                ? "One thing needs fixing in the sheet. Nothing was changed."
                : $"{plan.Problems.Count} things need fixing in the sheet. Nothing was changed.";

            Raise(nameof(HasStockSheetProblems));
            return null;
        }

        Raise(nameof(HasStockSheetProblems));

        if (plan.Changes.Count == 0)
        {
            Status = "Nothing in that sheet changes a count. Write what you counted in the column headed new_count.";
            return null;
        }

        return plan;
    }

    /// <summary>Applies a checked sheet: every change, or none.</summary>
    public string? ApplyStockSheet(StockSheetPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        try
        {
            var counted = _stock.ApplySheet(plan.Changes, _laneId, "stock sheet");

            Status = $"{Plural.Of(counted, "count")} changed{(plan.FullLevels > 0 ? $" and {Plural.Of(plan.FullLevels, "full level")}" : "")} from the stock sheet. "
                   + $"{Plural.Of(plan.Blank, "row")} left blank {(plan.Blank == 1 ? "was" : "were")} left alone.";

            LoadStock();
            return null;
        }
        catch (Exception ex)
        {
            return Status = $"Nothing was changed: {ex.Message}";
        }
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
