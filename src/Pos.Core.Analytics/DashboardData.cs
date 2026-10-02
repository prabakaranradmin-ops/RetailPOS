namespace Pos.Core.Analytics;

/// <summary>The four figures a shopkeeper checks first.</summary>
/// <param name="Bills">Settled invoices. Voided ones are not sales and are counted separately.</param>
/// <param name="Cash">Cash taken, before change handed back. See <see cref="CashInDrawer"/>.</param>
/// <param name="Bank">Card and UPI: the money that should arrive in the bank account.</param>
/// <param name="Credit">
/// Paid on store credit - money the customer still owes. Not in the drawer and not in the bank.
/// </param>
/// <param name="PointsRedeemed">
/// Paid with loyalty points - money the shop gave away. It never arrives anywhere.
/// </param>
/// <remarks>
/// Split four ways because the four answer different questions. It used to be cash and "digital",
/// with digital meaning every other tender, shown as "what should reach the bank" - which counted
/// what customers owe and what the shop gave away as money on its way to the bank account.
/// </remarks>
public sealed record Kpis(
    int Bills,
    decimal GrossSales,
    decimal Discount,
    decimal NetSales,
    decimal Tax,
    decimal Cash,
    decimal Bank,
    decimal Credit,
    decimal PointsRedeemed,
    decimal ChangeGiven)
{
    /// <summary>Average basket value: what a customer spends per visit.</summary>
    public decimal AverageBasket => Bills == 0 ? 0m : decimal.Round(NetSales / Bills, 2, MidpointRounding.ToEven);

    /// <summary>What should be in the drawer: cash taken, less change handed back.</summary>
    public decimal CashInDrawer => Cash - ChangeGiven;

    public static Kpis Empty { get; } = new(0, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m);
}

/// <param name="Hour">0-23, the shop's local hour.</param>
public sealed record HourlyBucket(int Hour, int Bills, decimal NetSales);

public sealed record DailyPoint(DateOnly Date, int Bills, decimal NetSales, decimal Discount);

/// <param name="Weekday">Monday is 1, Sunday is 7 — as a shopkeeper counts a week, not as .NET does.</param>
/// <param name="HourBand">The band's opening hour, on a two-hour grid: 8 means 8am to 10am.</param>
public sealed record WeekdayHourCell(int Weekday, int HourBand, int Bills, decimal NetSales);

public sealed record TopItem(string Name, string Hsn, decimal Quantity, string Unit, decimal NetSales, int Bills);

public sealed record TenderSlice(string Tender, int Count, decimal Amount);

/// <param name="Category">The department, or "Uncategorised" for items the shop has not filed.</param>
public sealed record CategorySlice(string Category, decimal NetSales, decimal Quantity, int Lines);

/// <summary>
/// Where one item sits on the volume-against-margin grid.
/// </summary>
/// <param name="MarginPercent">
/// What the shop kept, as a share of what it charged. Only ever present when the item carried a
/// cost price at the time it was sold.
/// </param>
public sealed record ItemPerformance(
    string Name,
    string Category,
    decimal Quantity,
    decimal NetSales,
    decimal Cost,
    decimal MarginPercent)
{
    public decimal Profit => NetSales - Cost;
}

/// <summary>
/// The margin-against-velocity picture, and an honest account of how much of the shop it covers.
/// </summary>
/// <param name="Priced">Items that carried a cost price and can therefore be placed.</param>
/// <param name="UnpricedItems">How many distinct items could not be, for want of a cost.</param>
/// <param name="UnpricedSales">What those items sold for, so the gap can be judged rather than guessed.</param>
public sealed record MarginPicture(
    IReadOnlyList<ItemPerformance> Priced,
    int UnpricedItems,
    decimal UnpricedSales,
    decimal MedianQuantity,
    decimal MedianMargin)
{
    public decimal PricedSales => Priced.Sum(i => i.NetSales);

    /// <summary>Share of the window's takings this picture can actually speak for.</summary>
    public decimal Coverage => PricedSales + UnpricedSales == 0m
        ? 0m
        : decimal.Round(PricedSales / (PricedSales + UnpricedSales) * 100m, 1, MidpointRounding.ToEven);
}

/// <param name="Rate">The GST slab, as a percentage.</param>
public sealed record GstSlab(decimal Rate, decimal TaxableValue, decimal Cgst, decimal Sgst, decimal Igst)
{
    public decimal TotalTax => Cgst + Sgst + Igst;
}

public sealed record VoidSummary(int Count, decimal Value);

/// <summary>One person's exceptions at the till over the window.</summary>
/// <param name="Cashier">Who was on the till, or <see cref="TillExceptions.Nobody"/> when nobody had said.</param>
/// <param name="Refused">Times the owner's PIN was asked for and not given, and sign-ons refused.</param>
public sealed record CashierExceptions(
    string Cashier,
    int Voids,
    decimal Voided,
    int Discounts,
    decimal Discounted,
    int CashRefunds,
    decimal CashRefunded,
    int CashOuts,
    decimal CashTakenOut,
    int Refused,
    int OverKhataLimit = 0,
    decimal OverKhataLimitValue = 0m)
{
    public int Total => Voids + Discounts + CashRefunds + CashOuts + Refused + OverKhataLimit;
}

/// <summary>
/// The till's exceptions in the window - voids, discounts typed by hand, cash refunds, cash out and
/// refused PINs - by who was on the till, and the latest of them one by one.
/// </summary>
/// <param name="Latest">The most recent, newest first, at most <see cref="DashboardQuery.LatestExceptions"/>.</param>
/// <param name="RecordedSince">
/// When this lane began keeping the record at all, or null when it has nothing in it yet. A window
/// that starts before then is not a window in which nothing happened.
/// </param>
public sealed record TillExceptions(
    IReadOnlyList<CashierExceptions> ByCashier,
    IReadOnlyList<Pos.Core.Domain.TillEvent> Latest,
    DateTimeOffset? RecordedSince)
{
    /// <summary>What the record says of a sale made with nobody named on the till.</summary>
    public const string Nobody = "Nobody named";

    public static TillExceptions None { get; } = new([], [], null);

    public bool Any => ByCashier.Count > 0;
}

/// <summary>One day-end close: what the drawer should have held, what was counted, and who was on the till.</summary>
/// <param name="OnTheTill">Everybody who took cash or moved it through the drawer in the day it closed.</param>
public sealed record ClosedDrawer(
    long ReportId,
    DateTimeOffset ClosedAt,
    decimal Expected,
    decimal? Counted,
    string? CountedBy,
    IReadOnlyList<string> OnTheTill)
{
    /// <summary>Counted less expected: over above zero, short below. Null when not counted.</summary>
    public decimal? Difference => Counted is { } counted ? counted - Expected : null;
}

/// <summary>
/// One person's days at the till in the window, and how the drawer came out on them.
/// </summary>
/// <remarks>
/// A day two people worked counts for both of them: the drawer was shared, and the count cannot say
/// whose hands the difference passed through. A pattern across many days says more than any one.
/// </remarks>
public sealed record PersonDrawer(string Name, int Days, int Counted, int ShortDays, decimal Short, int OverDays, decimal Over);

/// <summary>What one department's shelves are worth.</summary>
public sealed record StockValueByCategory(string Category, int Items, decimal AtCost, decimal AtSellingPrice);

/// <summary>
/// What the counted shelves are worth now: at cost, at the selling price and at MRP.
/// </summary>
/// <remarks>
/// Counted items with something on the shelf only. A count below zero is the count and the shelf
/// having parted company, not stock, and is counted apart rather than taken off the value.
/// </remarks>
/// <param name="AtCost">At the latest cost price, for the items that have one.</param>
/// <param name="CostedAtSellingPrice">The same items at their selling price: what <see cref="AtCost"/> would sell for.</param>
/// <param name="WithoutCost">Counted items on the shelf with no cost price, so not in <see cref="AtCost"/>.</param>
public sealed record StockValue(
    int CountedItems,
    decimal AtCost,
    decimal CostedAtSellingPrice,
    decimal AtSellingPrice,
    decimal AtMrp,
    int WithoutCost,
    int BelowZero,
    IReadOnlyList<StockValueByCategory> Categories)
{
    public static StockValue None { get; } = new(0, 0m, 0m, 0m, 0m, 0, 0, []);

    /// <summary>What the costed stock would make if it all sold at today's prices.</summary>
    public decimal Margin => CostedAtSellingPrice - AtCost;
}

/// <summary>The drawer at each close in the window, and each person's days, over and short.</summary>
public sealed record DrawerCounts(IReadOnlyList<ClosedDrawer> Closes, IReadOnlyList<PersonDrawer> ByPerson)
{
    public static DrawerCounts None { get; } = new([], []);

    public int CountedCloses => Closes.Count(c => c.Counted is not null);

    public int ShortCloses => Closes.Count(c => c.Difference < 0m);

    public decimal ShortTotal => -Closes.Where(c => c.Difference < 0m).Sum(c => c.Difference!.Value);

    public int OverCloses => Closes.Count(c => c.Difference > 0m);

    public decimal OverTotal => Closes.Where(c => c.Difference > 0m).Sum(c => c.Difference!.Value);
}

/// <summary>Goods brought back on credit notes in the window, and what was refunded for them.</summary>
public sealed record ReturnSummary(int Count, decimal Value)
{
    public static ReturnSummary None { get; } = new(0, 0m);
}

/// <param name="IdentifiedBills">Bills rung up against a customer the shop knows by mobile number.</param>
public sealed record CustomerMix(
    int IdentifiedBills,
    decimal IdentifiedSales,
    int WalkInBills,
    decimal WalkInSales,
    int DistinctCustomers,
    int ReturningCustomers)
{
    public int TotalBills => IdentifiedBills + WalkInBills;
}

public sealed record PointsDay(DateOnly Date, int Earned, int Redeemed);

/// <param name="OutstandingBalance">What every enrolled customer between them could still redeem.</param>
public sealed record PointsFlow(int Earned, int Redeemed, int OutstandingBalance, IReadOnlyList<PointsDay> Daily);

/// <summary>
/// Everything the dashboard draws, gathered in one pass so the page is a single consistent picture
/// of the shop rather than a set of figures taken at slightly different moments.
/// </summary>
public sealed record DashboardData
{
    public required string LaneId { get; init; }
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>Today alone — the header cards.</summary>
    public required Kpis Today { get; init; }

    /// <summary>The whole window the rest of the page covers.</summary>
    public required Kpis Range { get; init; }

    public required IReadOnlyList<HourlyBucket> Hourly { get; init; }
    public required IReadOnlyList<DailyPoint> Daily { get; init; }
    public required IReadOnlyList<WeekdayHourCell> WeekdayByHour { get; init; }
    public required IReadOnlyList<TopItem> TopItems { get; init; }
    public required IReadOnlyList<CategorySlice> Categories { get; init; }
    public required MarginPicture Margins { get; init; }
    public required IReadOnlyList<TenderSlice> Tenders { get; init; }
    public required IReadOnlyList<GstSlab> GstSlabs { get; init; }
    public required VoidSummary Voids { get; init; }

    /// <summary>
    /// Credit notes issued in the window. Beside the sales rather than netted out of them: the
    /// figures above are the bills as issued, and a return is its own document.
    /// </summary>
    public ReturnSummary Returns { get; init; } = ReturnSummary.None;

    /// <summary>
    /// What the shop spent in the window that was not stock, by category, wherever it was paid from.
    /// </summary>
    public IReadOnlyList<Pos.Core.Domain.ExpenseTotal> Expenses { get; init; } = [];

    public decimal ExpensesTotal => Expenses.Sum(e => e.Amount);

    /// <summary>Voids, discounts typed by hand, cash refunds, cash out and refused PINs, by who did them.</summary>
    public TillExceptions Exceptions { get; init; } = TillExceptions.None;

    /// <summary>The drawer counted at each close in the window, over or short, and by who was on the till.</summary>
    public DrawerCounts Drawers { get; init; } = DrawerCounts.None;
    public required CustomerMix Customers { get; init; }
    public required PointsFlow Points { get; init; }

    /// <summary>
    /// What needs reordering.
    /// </summary>
    /// <remarks>
    /// Unlike everything else here this is not about the window at all — it is the state of the
    /// shelves right now. It is on this page because it is the thing an owner reading the figures
    /// is most likely to act on, and because they are already here.
    /// </remarks>
    public IReadOnlyList<Pos.Core.Domain.StockLevel> LowStock { get; init; } = [];

    /// <summary>What the counted shelves are worth now. Like <see cref="LowStock"/>, about today, not the window.</summary>
    public StockValue Stock { get; init; } = StockValue.None;

    /// <summary>How long the whole gather took. Shown on the page, because it is a promise.</summary>
    public required TimeSpan Elapsed { get; init; }
}
