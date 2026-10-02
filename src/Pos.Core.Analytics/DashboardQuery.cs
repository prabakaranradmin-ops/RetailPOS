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
/// <param name="lowStockPercent">The share of full an item without a reorder level warns at.</param>
public sealed class DashboardQuery(PosDatabase database, decimal lowStockPercent = LowStock.DefaultPercent)
{
    private readonly PosDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly decimal _lowStockPercent = lowStockPercent;

    /// <summary>Amount columns are text; this sums one as exact paise. See <see cref="PaiseSql"/>.</summary>
    private static string Sum(string column) => PaiseSql.Sum(column);

    /// <summary>The same conversion without the SUM, for use inside one.</summary>
    private static string Paise0(string column) => PaiseSql.Of(column);

    /// <summary>
    /// A settled sale: anything in <c>invoices</c> that has not been voided.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Voided invoices keep their row and their number — that is what makes the run auditable — but
    /// they are not takings and must not reach a single figure on this page. They get a count and a
    /// value of their own instead.
    /// </para>
    /// <para>
    /// <strong><c>hold_token</c> is not a filter.</strong> It once marked a row as a parked bill,
    /// but migration 003 moved parked bills to <c>held_bills</c> — a parked bill never has a row
    /// here at all — and the column now records which parked bill a <em>settled</em> invoice was
    /// recalled from. Filtering on it dropped every sale that had been parked and then paid for:
    /// the day-end report counted it and this page said the lane had sold nothing.
    /// </para>
    /// </remarks>
    private const string Settled = "i.voided_at IS NULL";

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

        // Today's split gets its own read. It used to be skipped as not worth a query, which left
        // today's cash at zero while today's change was counted - so the page's "cash in drawer"
        // for today came out negative on any day the till had given change. One day of payments
        // against the same index is a few milliseconds.
        var todayTenders = ReadTenders(connection, laneId, startOfToday > from ? startOfToday : from, to);

        var data = new DashboardData
        {
            LaneId = laneId,
            From = from,
            To = to,
            GeneratedAt = now,
            Today = Fold(facts.Where(f => f.At >= startOfToday && f.At <= now), todayTenders),
            Range = Fold(facts, tenders),
            Hourly = FoldHourly(facts),
            Daily = FoldDaily(facts, from, to),
            WeekdayByHour = FoldWeekdayByHour(facts),
            TopItems = FoldTopItems(lines, topItems),
            Categories = FoldCategories(lines),
            Margins = FoldMargins(lines),
            Tenders = tenders,
            GstSlabs = FoldGstSlabs(lines),
            Voids = FoldVoids(facts),
            Returns = ReadReturns(connection, laneId, from, to),
            Expenses = CashDrawerRepository.ReadTotals(connection, from, to),
            Exceptions = ReadExceptions(connection, laneId, from, to),
            Drawers = ReadDrawers(connection, laneId, from, to),
            Customers = ReadCustomerMix(connection, laneId, from, to, facts),
            Points = FoldPoints(connection, facts),
            LowStock = ReadLowStock(connection),
            Stock = ReadStockValue(connection),
            Elapsed = clock.Elapsed,
        };

        clock.Stop();
        return data with { Elapsed = clock.Elapsed };
    }

    /// <summary>
    /// What is at or below its reorder level, most depleted first.
    /// </summary>
    /// <remarks>
    /// The one query here that ignores the window entirely: the shelves are in whatever state they
    /// are in today, and an owner deciding what to order does not want last month's shortages.
    ///
    /// Cheap enough not to matter to the page's timing — it reads a partial index over the item
    /// master, not the invoice history that everything else on this page walks.
    /// </remarks>
    private List<StockLevel> ReadLowStock(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT i.id, i.sku, i.name, i.category, i.stock_qty, i.reorder_level, i.unit_type, i.full_qty,
                   {LowStockSql.WarnAt("i")} AS warn
            FROM items i
            WHERE i.is_active = 1
              AND {LowStockSql.IsLow("i")}
            ORDER BY CAST(i.stock_qty AS REAL) - warn, i.name
            LIMIT 50;
            """;
        command.Parameters.AddWithValue("$pct", LowStockSql.Percent(_lowStockPercent));

        using var reader = command.ExecuteReader();
        var levels = new List<StockLevel>();

        while (reader.Read())
        {
            levels.Add(new StockLevel(
                ItemId: reader.GetInt64(0),
                Sku: reader.GetString(1),
                Name: reader.GetString(2),
                Category: reader.IsDBNull(3) ? null : reader.GetString(3),
                Quantity: reader.GetDecimal(4),
                ReorderLevel: reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                Unit: (UnitType)reader.GetInt32(6),
                FullLevel: reader.IsDBNull(7) ? null : reader.GetDecimal(7),
                WarnAt: Math.Round((decimal)reader.GetDouble(8), 3)));
        }

        return levels;
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

    /// <summary>Credit notes issued in the window, by the date of the note rather than of the sale.</summary>
    private static ReturnSummary ReadReturns(SqliteConnection connection, string lane, DateTimeOffset from, DateTimeOffset to)
    {
        using var command = Prepare(connection, lane, from, to, $"""
            SELECT COUNT(*), COALESCE({Sum("n.total")}, 0) + COALESCE({Sum("n.round_off")}, 0)
            FROM credit_notes n
            WHERE n.lane_id = $lane
              AND n.created_at >= $from AND n.created_at < $to;
            """);

        using var reader = command.ExecuteReader();
        reader.Read();

        return new ReturnSummary(reader.GetInt32(0), Rupees(reader.GetInt64(1)));
    }

    /// <summary>
    /// What the counted shelves are worth now, added up in C# from each item so the figures stay
    /// exact; SQLite would add the text amounts as floating point.
    /// </summary>
    private static StockValue ReadStockValue(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT stock_qty, cost_price, sell_price, mrp, category
            FROM items
            WHERE is_active = 1 AND stock_qty IS NOT NULL;
            """;

        int counted = 0, withoutCost = 0, belowZero = 0;
        decimal atCost = 0m, costedAtSelling = 0m, atSelling = 0m, atMrp = 0m;
        var byCategory = new Dictionary<string, (int Items, decimal Cost, decimal Selling)>(StringComparer.OrdinalIgnoreCase);

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var quantity = reader.GetDecimal(0);

            if (quantity < 0m)
            {
                belowZero++;
                continue;
            }

            if (quantity == 0m)
                continue;

            counted++;

            var selling = Pos.Core.Tax.Money.ToPresentation(quantity * reader.GetDecimal(2));
            atSelling += selling;
            atMrp += Pos.Core.Tax.Money.ToPresentation(quantity * reader.GetDecimal(3));

            var category = reader.IsDBNull(4) || string.IsNullOrWhiteSpace(reader.GetString(4)) ? Uncategorised : reader.GetString(4).Trim();
            var so = byCategory.GetValueOrDefault(category);

            if (reader.IsDBNull(1))
            {
                withoutCost++;
                byCategory[category] = (so.Items + 1, so.Cost, so.Selling + selling);
                continue;
            }

            var cost = Pos.Core.Tax.Money.ToPresentation(quantity * reader.GetDecimal(1));
            atCost += cost;
            costedAtSelling += selling;
            byCategory[category] = (so.Items + 1, so.Cost + cost, so.Selling + selling);
        }

        return new StockValue(
            counted,
            atCost,
            costedAtSelling,
            atSelling,
            atMrp,
            withoutCost,
            belowZero,
            [.. byCategory
                .Select(c => new StockValueByCategory(c.Key, c.Value.Items, c.Value.Cost, c.Value.Selling))
                .OrderByDescending(c => c.AtSellingPrice)
                .ThenBy(c => c.Category, StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>How many of the window's exceptions are listed one by one; the totals count them all.</summary>
    public const int LatestExceptions = 50;

    /// <summary>The kinds an owner asks about. A sign-on or a close is the day going as it should.</summary>
    private static readonly TillEventKind[] ExceptionKinds =
    [
        TillEventKind.Voided,
        TillEventKind.Discounted,
        TillEventKind.CashRefunded,
        TillEventKind.CashTakenOut,
        TillEventKind.ApprovalRefused,
        TillEventKind.SignOnRefused,
        TillEventKind.OverKhataLimit,
    ];

    /// <summary>
    /// The till's exceptions in the window, totalled by who was on the till.
    /// </summary>
    /// <remarks>
    /// Read row by row and added up here rather than summed in SQL, because the amounts are kept as
    /// exact decimals in text, and SQLite would add them as floating point. There are tens of these
    /// in a day, not tens of thousands.
    /// </remarks>
    private static TillExceptions ReadExceptions(SqliteConnection connection, string lane, DateTimeOffset from, DateTimeOffset to)
    {
        var kinds = string.Join(", ", ExceptionKinds.Select(k => $"'{k}'"));

        using var command = Prepare(connection, lane, from, to, $"""
            SELECT id, lane_id, happened_at, kind, cashier_name, reference, amount, approved, detail
            FROM till_events
            WHERE lane_id = $lane
              AND happened_at >= $from AND happened_at < $to
              AND kind IN ({kinds})
            ORDER BY happened_at DESC, id DESC;
            """);

        var events = new List<TillEvent>();

        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (!Enum.TryParse<TillEventKind>(reader.GetString(3), out var kind))
                    continue;

                events.Add(new TillEvent(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetDateTimeOffset(2),
                    kind,
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                    reader.IsDBNull(7) ? null : reader.GetInt64(7) == 1,
                    reader.IsDBNull(8) ? null : reader.GetString(8)));
            }
        }

        using var since = connection.CreateCommand();
        since.CommandText = "SELECT MIN(happened_at) FROM till_events WHERE lane_id = $lane;";
        since.Parameters.AddWithValue("$lane", lane);

        var first = since.ExecuteScalar() is string stamp
            && DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                ? at
                : (DateTimeOffset?)null;

        var byCashier = events
            .GroupBy(e => e.Cashier ?? TillExceptions.Nobody, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CashierExceptions(
                g.First().Cashier ?? TillExceptions.Nobody,
                g.Count(e => e.Kind == TillEventKind.Voided),
                g.Where(e => e.Kind == TillEventKind.Voided).Sum(e => e.Amount ?? 0m),
                g.Count(e => e.Kind == TillEventKind.Discounted),
                g.Where(e => e.Kind == TillEventKind.Discounted).Sum(e => e.Amount ?? 0m),
                g.Count(e => e.Kind == TillEventKind.CashRefunded),
                g.Where(e => e.Kind == TillEventKind.CashRefunded).Sum(e => e.Amount ?? 0m),
                g.Count(e => e.Kind == TillEventKind.CashTakenOut),
                g.Where(e => e.Kind == TillEventKind.CashTakenOut).Sum(e => e.Amount ?? 0m),
                g.Count(e => e.Kind is TillEventKind.ApprovalRefused or TillEventKind.SignOnRefused),
                g.Count(e => e.Kind == TillEventKind.OverKhataLimit),
                g.Where(e => e.Kind == TillEventKind.OverKhataLimit).Sum(e => e.Amount ?? 0m)))
            .OrderByDescending(c => c.Total)
            .ThenBy(c => c.Cashier, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new TillExceptions(byCashier, events.Take(LatestExceptions).ToList(), first);
    }

    /// <summary>
    /// The closes in the window, oldest first, with who was on the till for each, and each person's
    /// days over and short.
    /// </summary>
    /// <remarks>
    /// Who was on the till is read from the books the close stamped: the bills paid in cash and the
    /// cash moved through the drawer. Somebody who only took UPI that day never touched the drawer.
    /// </remarks>
    private static DrawerCounts ReadDrawers(SqliteConnection connection, string lane, DateTimeOffset from, DateTimeOffset to)
    {
        var closes = new List<(long Id, DateTimeOffset At, decimal Expected, decimal? Counted, string? By)>();

        using (var command = Prepare(connection, lane, from, to, """
            SELECT id, closed_at, cash_expected, cash_counted, counted_by
            FROM day_closes
            WHERE lane_id = $lane AND closed_at >= $from AND closed_at < $to
            ORDER BY closed_at, id;
            """))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                closes.Add((
                    reader.GetInt64(0),
                    reader.GetDateTimeOffset(1),
                    reader.GetDecimal(2),
                    reader.IsDBNull(3) ? null : reader.GetDecimal(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }

        if (closes.Count == 0)
            return DrawerCounts.None;

        var drawers = new List<ClosedDrawer>();

        foreach (var close in closes)
        {
            using var who = connection.CreateCommand();
            who.CommandText = """
                SELECT DISTINCT name FROM (
                    SELECT i.cashier_name AS name
                    FROM invoices i
                    WHERE i.day_close_id = $id AND i.status = $settled
                      AND EXISTS (SELECT 1 FROM payments p WHERE p.invoice_id = i.id AND p.tender_type = $cash)
                    UNION
                    SELECT m.cashier_name AS name
                    FROM cash_movements m
                    WHERE m.day_close_id = $id)
                WHERE name IS NOT NULL AND trim(name) <> ''
                ORDER BY name;
                """;
            who.Parameters.AddWithValue("$id", close.Id);
            who.Parameters.AddWithValue("$settled", (int)InvoiceStatus.Settled);
            who.Parameters.AddWithValue("$cash", (int)TenderType.Cash);

            var names = new List<string>();

            using (var reader = who.ExecuteReader())
            {
                while (reader.Read())
                    names.Add(reader.GetString(0).Trim());
            }

            drawers.Add(new ClosedDrawer(close.Id, close.At, close.Expected, close.Counted, close.By, names));
        }

        var byPerson = drawers
            .SelectMany(d => d.OnTheTill.Select(name => (Name: name, Drawer: d)))
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new PersonDrawer(
                g.First().Name,
                g.Count(),
                g.Count(p => p.Drawer.Counted is not null),
                g.Count(p => p.Drawer.Difference < 0m),
                -g.Where(p => p.Drawer.Difference < 0m).Sum(p => p.Drawer.Difference!.Value),
                g.Count(p => p.Drawer.Difference > 0m),
                g.Where(p => p.Drawer.Difference > 0m).Sum(p => p.Drawer.Difference!.Value)))
            .OrderByDescending(p => p.Short)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new DrawerCounts(drawers, byPerson);
    }

    private static List<InvoiceFacts> ReadInvoiceFacts(SqliteConnection connection, string lane, DateTimeOffset from, DateTimeOffset to)
    {
        using var command = Prepare(connection, lane, from, to, $"""
            SELECT substr(i.created_at, 1, 10) AS day,
                   CAST(substr(i.created_at, 12, 2) AS INTEGER) AS hour,
                   i.customer_id IS NOT NULL AS identified,
                   i.voided_at IS NOT NULL AS voided,
                   COUNT(*),
                   COALESCE({Sum("i.grand_total")}, 0) + COALESCE({Sum("i.round_off")}, 0),
                   COALESCE({Sum("i.total_discount")}, 0),
                   COALESCE({Sum("i.total_cgst")}, 0) + COALESCE({Sum("i.total_sgst")}, 0) + COALESCE({Sum("i.total_igst")}, 0),
                   COALESCE({Sum("i.change_due")}, 0),
                   COALESCE(SUM(i.points_earned), 0),
                   COALESCE(SUM(i.points_redeemed), 0)
            FROM invoices i
            WHERE i.lane_id = $lane
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

        decimal Taken(params TenderType[] kinds) =>
            tenders?.Where(t => kinds.Any(k => t.Tender == Label(k))).Sum(t => t.Amount) ?? 0m;

        // Each tender in exactly one place. Store credit is owed, not banked, and points are given
        // away, not banked; lumping them in with card and UPI overstated the bank by both.
        var cash = Taken(TenderType.Cash);
        var bank = Taken(TenderType.Card, TenderType.Upi);
        var credit = Taken(TenderType.StoreCredit);
        var points = Taken(TenderType.LoyaltyPoints);

        return new Kpis(bills, net + discount, discount, net, tax, cash, bank, credit, points, change);
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
        string? Category,
        decimal? Cost,
        decimal Quantity,
        decimal LineTotal,
        decimal Taxable,
        decimal Cgst,
        decimal Sgst,
        decimal Igst,
        int Bills);

    /// <summary>What a department is called when the shop has not said.</summary>
    public const string Uncategorised = "Uncategorised";

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
                   l.category_snapshot AS category,
                   l.cost_snapshot IS NOT NULL AS priced,

                   -- What the shop paid for everything sold in this group, in paise. A line with no
                   -- cost contributes nothing here and is counted apart, so an unpriced item cannot
                   -- masquerade as one bought for free.
                   COALESCE(SUM(CASE WHEN l.cost_snapshot IS NOT NULL
                                     THEN {Paise0("l.cost_snapshot")} * CAST(l.quantity AS REAL)
                                     ELSE 0 END), 0),

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
            GROUP BY l.name_snapshot, l.hsn_snapshot, rate, category, priced;
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
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetBoolean(5) ? Rupees((long)reader.GetDouble(6)) : null,
                Math.Round((decimal)reader.GetDouble(7), 3),
                Rupees(reader.GetInt64(8)),
                Rupees(reader.GetInt64(9)),
                Rupees(reader.GetInt64(10)),
                Rupees(reader.GetInt64(11)),
                Rupees(reader.GetInt64(12)),
                reader.GetInt32(13)));
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

    /// <summary>
    /// What each part of the shop brought in. Items the shop has not filed go into one honest
    /// bucket rather than being dropped — a department chart that silently omits a third of the
    /// takings is worse than one that says so.
    /// </summary>
    private static List<CategorySlice> FoldCategories(IReadOnlyList<LineFacts> lines) =>
        [.. lines
            .GroupBy(l => string.IsNullOrWhiteSpace(l.Category) ? Uncategorised : l.Category!.Trim())
            .Select(g => new CategorySlice(g.Key, g.Sum(l => l.LineTotal), g.Sum(l => l.Quantity), g.Sum(l => l.Bills)))
            .OrderByDescending(c => c.NetSales)];

    /// <summary>
    /// Volume against margin, for the items that can answer both.
    /// </summary>
    /// <remarks>
    /// An item is only placed if it carried a cost when it was sold. The rest are counted and their
    /// takings reported, so the reader can see how much of the shop the picture speaks for — the
    /// alternative is a quadrant that looks complete while describing a fraction of the trade, and
    /// somebody clearing a shelf on the strength of it.
    /// </remarks>
    private static MarginPicture FoldMargins(IReadOnlyList<LineFacts> lines)
    {
        var byItem = lines.GroupBy(l => l.Name).ToList();
        var priced = new List<ItemPerformance>();
        var unpricedItems = 0;
        var unpricedSales = 0m;

        foreach (var group in byItem)
        {
            var withCost = group.Where(l => l.Cost is not null).ToList();
            var sales = withCost.Sum(l => l.LineTotal);
            var cost = withCost.Sum(l => l.Cost!.Value);

            // Anything sold without a cost recorded, whether the whole item or part of its history.
            var missing = group.Where(l => l.Cost is null).Sum(l => l.LineTotal);

            if (missing > 0m)
            {
                unpricedSales += missing;

                if (withCost.Count == 0)
                    unpricedItems++;
            }

            if (withCost.Count == 0 || sales <= 0m)
                continue;

            priced.Add(new ItemPerformance(
                group.Key,
                string.IsNullOrWhiteSpace(withCost[0].Category) ? Uncategorised : withCost[0].Category!.Trim(),
                withCost.Sum(l => l.Quantity),
                sales,
                cost,
                decimal.Round((sales - cost) / sales * 100m, 2, MidpointRounding.ToEven)));
        }

        // The grid is split at the middle of what this shop actually does, not at an arbitrary
        // margin or volume. A quadrant drawn against a fixed line says more about the line than the
        // shop, and every shop's normal is different.
        return new MarginPicture(
            [.. priced.OrderByDescending(i => i.NetSales)],
            unpricedItems,
            unpricedSales,
            Median([.. priced.Select(i => i.Quantity)]),
            Median([.. priced.Select(i => i.MarginPercent)]));
    }

    private static decimal Median(List<decimal> values)
    {
        if (values.Count == 0)
            return 0m;

        values.Sort();
        var middle = values.Count / 2;

        return values.Count % 2 == 1
            ? values[middle]
            : decimal.Round((values[middle - 1] + values[middle]) / 2m, 2, MidpointRounding.ToEven);
    }

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
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        return command;
    }

    // The window is bound as a DateTimeOffset, so the driver formats it exactly as it formatted
    // created_at when the invoice was written, and the comparison stays a plain string comparison
    // the index can seek on.
    //
    // It used to be formatted here with "O", under a comment saying that was the shape created_at
    // is written in. It is not: the driver writes "2026-09-21 10:00:00+05:30", with a space, and "O"
    // gives "2026-09-21T00:00:00.0000000+05:30", with a T. A space sorts before a T, so every sale
    // on the first day of a window compared as earlier than that day's midnight and was left out -
    // the first day of every 7, 30 and 90-day view, missing from every figure on the page.

    private static decimal Rupees(long paise) => paise / 100m;

    private static string Label(TenderType tender) => tender switch
    {
        TenderType.Cash => "Cash",
        TenderType.Card => "Card",
        TenderType.Upi => "UPI",
        TenderType.StoreCredit => "Khata",
        TenderType.LoyaltyPoints => "Loyalty points",
        _ => tender.ToString(),
    };

    private static string UnitLabel(UnitType unit) => Units.ScreenLabel(unit);
}
