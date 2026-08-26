namespace Pos.Core.Analytics;

/// <summary>The four figures a shopkeeper checks first.</summary>
/// <param name="Bills">Settled invoices. Voided ones are not sales and are counted separately.</param>
public sealed record Kpis(
    int Bills,
    decimal GrossSales,
    decimal Discount,
    decimal NetSales,
    decimal Tax,
    decimal Cash,
    decimal Digital,
    decimal ChangeGiven)
{
    /// <summary>Average basket value: what a customer spends per visit.</summary>
    public decimal AverageBasket => Bills == 0 ? 0m : decimal.Round(NetSales / Bills, 2, MidpointRounding.ToEven);

    /// <summary>What should be in the drawer: cash taken, less change handed back.</summary>
    public decimal CashInDrawer => Cash - ChangeGiven;

    public static Kpis Empty { get; } = new(0, 0m, 0m, 0m, 0m, 0m, 0m, 0m);
}

/// <param name="Hour">0-23, the shop's local hour.</param>
public sealed record HourlyBucket(int Hour, int Bills, decimal NetSales);

public sealed record DailyPoint(DateOnly Date, int Bills, decimal NetSales, decimal Discount);

/// <param name="Weekday">Monday is 1, Sunday is 7 — as a shopkeeper counts a week, not as .NET does.</param>
/// <param name="HourBand">The band's opening hour, on a two-hour grid: 8 means 8am to 10am.</param>
public sealed record WeekdayHourCell(int Weekday, int HourBand, int Bills, decimal NetSales);

public sealed record TopItem(string Name, string Hsn, decimal Quantity, string Unit, decimal NetSales, int Bills);

public sealed record TenderSlice(string Tender, int Count, decimal Amount);

/// <param name="Rate">The GST slab, as a percentage.</param>
public sealed record GstSlab(decimal Rate, decimal TaxableValue, decimal Cgst, decimal Sgst, decimal Igst)
{
    public decimal TotalTax => Cgst + Sgst + Igst;
}

public sealed record VoidSummary(int Count, decimal Value);

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
    public required IReadOnlyList<TenderSlice> Tenders { get; init; }
    public required IReadOnlyList<GstSlab> GstSlabs { get; init; }
    public required VoidSummary Voids { get; init; }
    public required CustomerMix Customers { get; init; }
    public required PointsFlow Points { get; init; }

    /// <summary>How long the whole gather took. Shown on the page, because it is a promise.</summary>
    public required TimeSpan Elapsed { get; init; }
}
