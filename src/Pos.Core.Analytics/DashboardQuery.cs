using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Pos.Core.Data;
using Pos.Core.Domain;

namespace Pos.Core.Analytics;

/// <summary>
/// Reads the shop's own books and works out what the dashboard shows.
/// </summary>
/// <remarks>
/// <para>
/// Read-only, and deliberately nowhere near the billing path. Every figure is aggregated by SQLite
/// against an index rather than by pulling rows into memory, so the cost is a function of how much
/// of the range is being summed and not of how many years the shop has been trading. SQLite in WAL
/// mode lets a reader run while the till is writing, so producing a dashboard mid-afternoon does
/// not make a cashier wait.
/// </para>
/// <para>
/// <b>Money is summed in whole paise, as integers.</b> Amounts are stored as text to keep decimals
/// exact, and SQLite has no decimal type — <c>SUM(CAST(x AS REAL))</c> would accumulate binary
/// floating-point error across hundreds of thousands of rows and quietly produce a GST figure that
/// is a few paise out. Every amount in the books has at most two decimal places, so multiplying by
/// 100 and rounding lands exactly on an integer, and integer addition is exact however many rows
/// there are. The conversion back to rupees happens once, here.
/// </para>
/// </remarks>
public sealed class DashboardQuery(PosDatabase database)
{
    private readonly PosDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

    /// <summary>Amount columns are text; this turns one into exact paise for summing.</summary>
    private const string Paise = "CAST(ROUND(CAST({0} AS REAL) * 100) AS INTEGER)";

    private static string Sum(string column) => $"SUM({string.Format(CultureInfo.InvariantCulture, Paise, column)})";

    /// <summary>
    /// A settled sale: not voided, and not a parked bill waiting to be recalled.
    /// </summary>
    /// <remarks>
    /// Voided invoices keep their row and their number — that is what makes the run auditable — but
    /// they are not takings and must not reach a single figure on this page. They get a count and a
    /// value of their own instead.
    /// </remarks>
    private const string Settled = "i.voided_at IS NULL AND i.hold_token IS NULL";

    public DashboardData Gather(string laneId, DateTimeOffset from, DateTimeOffset to, int topItems = 10)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(laneId);

        if (to < from)
            throw new ArgumentOutOfRangeException(nameof(to), "The window ends before it starts.");

        var clock = Stopwatch.StartNew();

        using var connection = _database.OpenConnection();

        var now = DateTimeOffset.Now;
        var startOfToday = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);

        // One pass over the invoices, folded in memory into everything that comes from them.
        //
        // This used to be seven queries — takings, the hourly rush, the daily trend, the weekday
        // grid, voids, the customer split, points — each walking the same rows for its own reason.
        // On two years of a busy shop that was seven scans of a quarter of a million invoices and
        // took nearly four seconds. Grouping once by day, hour and the two flags that matter gives
        // at most a few tens of thousands of rows, and everything else is arithmetic over those.
        var facts = ReadInvoiceFacts(connection, laneId, from, to);
        var lines = ReadLineFacts(connection, laneId, from, to);
        var tenders = ReadTenders(connection, laneId, from, to);

        var data = new DashboardData
        {
            LaneId = laneId,
            From = from,
            To = to,
            GeneratedAt = now,
            Today = Fold(facts.Where(f => f.At >= startOfToday && f.At <= now), tenders: null),
            Range = Fold(facts, tenders),
            Hourly = FoldHourly(facts),
            Daily = FoldDaily(facts, from, to),
            WeekdayByHour = FoldWeekdayByHour(facts),
            TopItems = FoldTopItems(lines, topItems),
            Tenders = tenders,
            GstSlabs = FoldGstSlabs(lines),
            Voids = FoldVoids(facts),
            Customers = ReadCustomerMix(connection, laneId, from, to, facts),
            Points = FoldPoints(connection, facts),
            Elapsed = clock.Elapsed,
        };

        clock.Stop();
        return data with { Elapsed = clock.Elapsed };
    }

    /// <summary>
    /// One row per day, hour, whether the customer was known, and whether the bill was voided.
    /// </summary>
    /// <param name="At">The start of the hour, in the shop's own time.</param>
    private sealed record InvoiceFacts(
        DateTimeOffset At,
        DateOnly Date,
        int Hour,
        bool Identified,
        bool Voided,
        int Bills,
        decimal Total,
        decimal Discount,
        decimal Tax,
        decimal Change,
        int PointsEarned,
        int PointsRedeemed);

    private static List<InvoiceFacts> ReadInvoiceFacts(SqliteConnection connection, string lane, DateTimeOffset from, DateTimeOffset to)
    {
        using var command = Prepare(connection, lane, from, to, $"""
            SELECT substr(i.created_at, 1, 10) AS day,
                   CAST(substr(i.created_at, 12, 2) AS INTEGER) AS hour,
                   i.customer_id IS NOT NULL AS identified,
                   i.voided_at IS NOT NULL AS voided,
                   COUNT(*),
                   COALESCE({Sum("i.grand_total")}, 0),
                   COALESCE({Sum("i.total_discount")}, 0),
                   COALESCE({Sum("i.total_cgst")}, 0) + COALESCE({Sum("i.total_sgst")}, 0) + COALESCE({Sum("i.total_igst")}, 0),
                   COALESCE({Sum("i.change_due")}, 0),
                   COALESCE(SUM(i.points_earned), 0),
                   COALESCE(SUM(i.points_redeemed), 0)
            FROM invoices i
            WHERE i.lane_id = $lane AND i.hold_token IS NULL
              AND i.created_at >= $from AND i.created_at < $to
            GROUP BY day, hour, identified, voided;
            """);

        var facts = new List<InvoiceFacts>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var date = DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var hour = reader.GetInt32(1);

            facts.Add(new InvoiceFacts(
                new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue).AddHours(hour), from.Offset),
                date,
                hour,
                reader.GetBoolean(2),
                reader.GetBoolean(3),
                reader.GetInt32(4),
                Rupees(reader.GetInt64(5)),
                Rupees(reader.GetInt64(6)),
                Rupees(reader.GetInt64(7)),
                Rupees(reader.GetInt64(8)),
                reader.GetInt32(9),
                reader.GetInt32(10)));
        }

        return facts;
    }

    // ---- Folding ------------------------------------------------------------------------------
    //
    // Voided bills are dropped here rather than in SQL, because the same scan has to count them for
    // the voids figure. Everything below works from settled rows only.

    private static Kpis Fold(IEnumerable<InvoiceFacts> facts, IReadOnlyList<TenderSlice>? tenders)
    {
        var bills = 0;
        decimal net = 0, discount = 0, tax = 0, change = 0;

        foreach (var f in facts.Where(f => !f.Voided))
        {
            bills += f.Bills;
            net += f.Total;
            discount += f.Discount;
            tax += f.Tax;
            change += f.Change;
        }

        // Today's card wants a cash split too, but running a second payments query for one day is
        // not worth it — the day's own figures come from the same scan, and the split is only shown
        // for the range when it has been read anyway.
        var cash = tenders?.Where(t => t.Tender == "Cash").Sum(t => t.Amount) ?? 0m;
        var digital = tenders?.Where(t => t.Tender != "Cash").Sum(t => t.Amount) ?? 0m;

        return new Kpis(bills, net + discount, discount, net, tax, cash, digital, change);
    }

    private static List<HourlyBucket> FoldHourly(IReadOnlyList<InvoiceFacts> facts)
    {
        var bills = new int[24];
        var sales = new decimal[24];

        foreach (var f in facts.Where(f => !f.Voided))
        {
            bills[f.Hour] += f.Bills;
            sales[f.Hour] += f.Total;
        }

        // Every hour the shop could have traded in, including the ones it did not: an empty 4pm is
        // information, and a chart that omits it hides the gap.
        return [.. Enumerable.Range(0, 24).Select(h => new HourlyBucket(h, bills[h], sales[h]))];
    }

    private static List<DailyPoint> FoldDaily(IReadOnlyList<InvoiceFacts> facts, DateTimeOffset from, DateTimeOffset to)
    {
        var byDay = new Dictionary<DateOnly, (int Bills, decimal Net, decimal Discount)>();

        foreach (var f in facts.Where(f => !f.Voided))
        {
            var current = byDay.TryGetValue(f.Date, out var existing) ? existing : (0, 0m, 0m);
            byDay[f.Date] = (current.Item1 + f.Bills, current.Item2 + f.Total, current.Item3 + f.Discount);
        }

        var days = new List<DailyPoint>();

        for (var d = DateOnly.FromDateTime(from.Date); d <= DateOnly.FromDateTime(to.Date); d = d.AddDays(1))
        {
            var v = byDay.TryGetValue(d, out var value) ? value : (0, 0m, 0m);
            days.Add(new DailyPoint(d, v.Item1, v.Item2, v.Item3));
        }

        return days;
    }

    private static List<WeekdayHourCell> FoldWeekdayByHour(IReadOnlyList<InvoiceFacts> facts)
    {
        // Two-hour bands, because a grocery's rhythm is not sharp enough for a single hour to say
        // anything and a 7x24 grid of mostly-empty cells reads as noise.
        var cells = new Dictionary<(int Weekday, int Band), (int Bills, decimal Sales)>();

        foreach (var f in facts.Where(f => !f.Voided))
        {
            var weekday = f.Date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)f.Date.DayOfWeek;
            var key = (weekday, f.Hour / 2 * 2);
            var current = cells.TryGetValue(key, out var existing) ? existing : (0, 0m);
            cells[key] = (current.Item1 + f.Bills, current.Item2 + f.Total);
        }

        var matrix = new List<WeekdayHourCell>();

        for (var weekday = 1; weekday <= 7; weekday++)
        {
            for (var band = 0; band < 24; band += 2)
            {
                var value = cells.TryGetValue((weekday, band), out var v) ? v : (0, 0m);
                matrix.Add(new WeekdayHourCell(weekday, band, value.Item1, value.Item2));
            }
        }

        return matrix;
    }

    private static VoidSummary FoldVoids(IReadOnlyList<InvoiceFacts> facts)
    {
        var voided = facts.Where(f => f.Voided).ToList();
        return new VoidSummary(voided.Sum(f => f.Bills), voided.Sum(f => f.Total));
    }

    private static PointsFlow FoldPoints(SqliteConnection connection, IReadOnlyList<InvoiceFacts> facts)
    {
        var byDay = new Dictionary<DateOnly, (int Earned, int Redeemed)>();

        foreach (var f in facts.Where(f => !f.Voided))
        {
            var current = byDay.TryGetValue(f.Date, out var existing) ? existing : (0, 0);
            byDay[f.Date] = (current.Item1 + f.PointsEarned, current.Item2 + f.PointsRedeemed);
        }

        var daily = byDay.OrderBy(e => e.Key).Select(e => new PointsDay(e.Key, e.Value.Earned, e.Value.Redeemed)).ToList();

        // What the shop still owes in points. Not scoped to the window — a liability is whatever it
        // is today, whichever days the page happens to be showing.
        using var balance = connection.CreateCommand();
        balance.CommandText = "SELECT COALESCE(SUM(loyalty_balance), 0) FROM customers;";
        var outstanding = Convert.ToInt32(balance.ExecuteScalar(), CultureInfo.InvariantCulture);

        return new PointsFlow(daily.Sum(d => d.Earned), daily.Sum(d => d.Redeemed), outstanding, daily);
    }

    /// <summary>
    /// The known-against-walk-in split comes from the same scan; only "how many came back" needs
    /// the books again, because that is a count of customers rather than of bills and cannot be
    /// recovered from figures already grouped by hour.
    /// </summary>
    private static CustomerMix ReadCustomerMix(
        SqliteConnection connection,
        string lane,
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyList<InvoiceFacts> facts)
    {
        var identified = (Bills: 0, Sales: 0m);
        var walkIn = (Bills: 0, Sales: 0m);

        foreach (var f in facts.Where(f => !f.Voided))
        {
            if (f.Identified)
                identified = (identified.Bills + f.Bills, identified.Sales + f.Total);
            else
                walkIn = (walkIn.Bills + f.Bills, walkIn.Sales + f.Total);
        }

        using var command = Prepare(connection, lane, from, to, $"""
            SELECT COUNT(*), COALESCE(SUM(CASE WHEN visits > 1 THEN 1 ELSE 0 END), 0) FROM (
              SELECT i.customer_id, COUNT(*) AS visits
              FROM invoices i
              WHERE i.lane_id = $lane AND {Settled} AND i.customer_id IS NOT NULL
                AND i.created_at >= $from AND i.created_at < $to
              GROUP BY i.customer_id
            );
            """);

        using var reader = command.ExecuteReader();
        var distinct = 0;
        var returning = 0;

        if (reader.Read())
        {
            distinct = reader.GetInt32(0);
            returning = reader.GetInt32(1);
        }

        return new CustomerMix(identified.Bills, identified.Sales, walkIn.Bills, walkIn.Sales, distinct, returning);
    }

    // ---- The figures ---------------------------------------------------------------------------

    /// <summary>What each item sold, and what tax it carried, in one walk of the lines.</summary>
    private sealed record LineFacts(
        string Name,
        string Hsn,
        UnitType Unit,
        decimal Rate,
        decimal Quantity,
        decimal LineTotal,
        decimal Taxable,
        decimal Cgst,
        decimal Sgst,
        decimal Igst,
        int Bills);

    /// <summary>
    /// One pass over the invoice lines, serving both the item table and the GST breakup.
    /// </summary>
    /// <remarks>
    /// They were two queries, each joining a million lines to the invoices that carry the date. An
    /// item has one GST rate, so grouping by both leaves the same number of rows either way and the
    /// second scan bought nothing.
    /// </remarks>
    private static List<LineFacts> ReadLineFacts(SqliteConnection connection, string lane, DateTimeOffset from, DateTimeOffset to)
    {
        using var command = Prepare(connection, lane, from, to, $"""
            SELECT l.name_snapshot,
                   l.hsn_snapshot,
                   MAX(l.unit_type),
                   CAST(l.gst_rate AS REAL) AS rate,
                   COALESCE(SUM(CAST(l.quantity AS REAL)), 0),
                   COALESCE({Sum("l.line_total")}, 0),
                   COALESCE({Sum("l.taxable_value")}, 0),
                   COALESCE({Sum("l.cgst_amount")}, 0),
                   COALESCE({Sum("l.sgst_amount")}, 0),
                   COALESCE({Sum("l.igst_amount")}, 0),
                   COUNT(DISTINCT l.invoice_id)
            FROM invoice_lines l
            JOIN invoices i ON i.id = l.invoice_id
            WHERE i.lane_id = $lane AND {Settled} AND i.created_at >= $from AND i.created_at < $to
            GROUP BY l.name_snapshot, l.hsn_snapshot, rate;
            """);

        var lines = new List<LineFacts>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            // Quantity is the one figure summed as a real rather than as paise: a weighed line
            // carries three decimals, and nothing is filed from a kilogram total.
            lines.Add(new LineFacts(
                reader.GetString(0),
                reader.GetString(1),
                (UnitType)reader.GetInt32(2),
                Math.Round((decimal)reader.GetDouble(3), 2),
                Math.Round((decimal)reader.GetDouble(4), 3),
                Rupees(reader.GetInt64(5)),
                Rupees(reader.GetInt64(6)),
                Rupees(reader.GetInt64(7)),
                Rupees(reader.GetInt64(8)),
                Rupees(reader.GetInt64(9)),
                reader.GetInt32(10)));
        }

        return lines;
    }

    private static List<TopItem> FoldTopItems(IReadOnlyList<LineFacts> lines, int count) =>
        [.. lines
            .GroupBy(l => (l.Name, l.Hsn))
            .Select(g => new TopItem(
                g.Key.Name,
                g.Key.Hsn,
                g.Sum(l => l.Quantity),
                UnitLabel(g.First().Unit),
                g.Sum(l => l.LineTotal),
                g.Sum(l => l.Bills)))
            .OrderByDescending(i => i.NetSales)
            .Take(count)];

    private static List<GstSlab> FoldGstSlabs(IReadOnlyList<LineFacts> lines) =>
        [.. lines
            .GroupBy(l => l.Rate)
            .Select(g => new GstSlab(
                g.Key,
                g.Sum(l => l.Taxable),
                g.Sum(l => l.Cgst),
                g.Sum(l => l.Sgst),
                g.Sum(l => l.Igst)))
            .OrderBy(s => s.Rate)];

    private static List<TenderSlice> ReadTenders(SqliteConnection connection, string lane, DateTimeOffset from, DateTimeOffset to)
    {
        using var command = Prepare(connection, lane, from, to, $"""
            SELECT p.tender_type, COUNT(*), COALESCE({Sum("p.amount")}, 0)
            FROM payments p
            JOIN invoices i ON i.id = p.invoice_id
            WHERE i.lane_id = $lane AND {Settled} AND i.created_at >= $from AND i.created_at < $to
            GROUP BY p.tender_type
            ORDER BY 3 DESC;
            """);

        var slices = new List<TenderSlice>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
            slices.Add(new TenderSlice(Label((TenderType)reader.GetInt32(0)), reader.GetInt32(1), Rupees(reader.GetInt64(2))));

        return slices;
    }


    // ---- Plumbing ------------------------------------------------------------------------------

    private static SqliteCommand Prepare(SqliteConnection connection, string lane, DateTimeOffset from, DateTimeOffset to, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$lane", lane);
        command.Parameters.AddWithValue("$from", Timestamp(from));
        command.Parameters.AddWithValue("$to", Timestamp(to));
        return command;
    }

    /// <summary>
    /// The same shape <c>created_at</c> is written in, so the comparison is a string comparison the
    /// index can seek on rather than a conversion applied to every row.
    /// </summary>
    private static string Timestamp(DateTimeOffset moment) => moment.ToString("O", CultureInfo.InvariantCulture);

    private static decimal Rupees(long paise) => paise / 100m;

    private static string Label(TenderType tender) => tender switch
    {
        TenderType.Cash => "Cash",
        TenderType.Card => "Card",
        TenderType.Upi => "UPI",
        TenderType.StoreCredit => "Store credit",
        TenderType.LoyaltyPoints => "Loyalty points",
        _ => tender.ToString(),
    };

    private static string UnitLabel(UnitType unit) => unit switch
    {
        UnitType.Kilogram => "kg",
        UnitType.Litre => "L",
        UnitType.Metre => "m",
        _ => "pc",
    };
}
