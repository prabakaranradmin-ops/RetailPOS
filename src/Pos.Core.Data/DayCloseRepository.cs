using System.Globalization;
using Microsoft.Data.Sqlite;
using Pos.Core.Domain;
using Pos.Core.Tax;

namespace Pos.Core.Data;

/// <summary>
/// Computes and stores a lane's Z-report.
/// </summary>
public sealed class DayCloseRepository : IDayCloseStore
{
    private readonly PosDatabase _database;
    private readonly IHeldBillStore? _heldBills;

    /// <param name="database">The lane's database.</param>
    /// <param name="heldBills">
    /// Optional. When supplied, the report says how many bills are still parked — not sales, but
    /// something somebody has to deal with before the lane is left for the night.
    /// </param>
    public DayCloseRepository(PosDatabase database, IHeldBillStore? heldBills = null)
    {
        ArgumentNullException.ThrowIfNull(database);

        _database = database;
        _heldBills = heldBills;
    }

    public DayCloseSummary Preview(string laneId, DateTimeOffset asOf)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(laneId);

        using var connection = _database.OpenConnection();
        return Compute(connection, null, laneId, asOf, 0);
    }

    /// <summary>
    /// Computes the report and stamps the invoices it covers, in one transaction.
    /// </summary>
    /// <remarks>
    /// The stamping is what makes this safe to run twice: a second close finds no unreported
    /// invoices and produces an empty report rather than double-counting the day. It is also what
    /// makes an old Z-report reproducible — the invoices it covered are still identifiable years
    /// later, whatever anyone later decides a "day" means.
    /// </remarks>
    public DayCloseSummary Close(string laneId, DateTimeOffset closedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(laneId);

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);

        var summary = Compute(connection, transaction, laneId, closedAt, 0);
        var id = InsertHeader(connection, transaction, summary);

        InsertTenders(connection, transaction, id, summary.Tenders);
        InsertTaxSlabs(connection, transaction, id, summary.TaxSlabs);

        using (var stamp = connection.CreateCommand())
        {
            stamp.Transaction = transaction;

            // Voided invoices are stamped as well as settled ones. They contribute nothing to
            // takings, but they appear on this report's audit line and must not appear again on
            // tomorrow's — and stamping them is also what stops a void being attempted on an
            // invoice whose day has already been reported.
            stamp.CommandText = """
                UPDATE invoices
                SET day_close_id = $id
                WHERE lane_id = $lane AND day_close_id IS NULL AND status IN ($settled, $cancelled);
                """;
            stamp.Parameters.AddWithValue("$id", id);
            stamp.Parameters.AddWithValue("$lane", laneId);
            stamp.Parameters.AddWithValue("$settled", (int)InvoiceStatus.Settled);
            stamp.Parameters.AddWithValue("$cancelled", (int)InvoiceStatus.Cancelled);
            stamp.ExecuteNonQuery();
        }

        // Repayments are claimed by the close that reported them, as invoices are, so each is
        // counted on exactly one Z-report however many times the lane closes.
        using (var stamp = connection.CreateCommand())
        {
            stamp.Transaction = transaction;
            stamp.CommandText = "UPDATE credit_payments SET day_close_id = $id WHERE lane_id = $lane AND day_close_id IS NULL;";
            stamp.Parameters.AddWithValue("$id", id);
            stamp.Parameters.AddWithValue("$lane", laneId);
            stamp.ExecuteNonQuery();
        }

        // And so is cash moved through the drawer, and every credit note issued.
        foreach (var table in new[] { "cash_movements", "credit_notes" })
        {
            using var stamp = connection.CreateCommand();
            stamp.Transaction = transaction;
            stamp.CommandText = $"UPDATE {table} SET day_close_id = $id WHERE lane_id = $lane AND day_close_id IS NULL;";
            stamp.Parameters.AddWithValue("$id", id);
            stamp.Parameters.AddWithValue("$lane", laneId);
            stamp.ExecuteNonQuery();
        }

        transaction.Commit();

        var (parked, orders) = CountHeldBills(laneId);

        return summary with { Id = id, HeldBillsOutstanding = parked, OrdersWaiting = orders };
    }

    public DayCloseSummary? FindLatest(string laneId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(laneId);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM day_closes WHERE lane_id = $lane ORDER BY closed_at DESC, id DESC LIMIT 1;";
        command.Parameters.AddWithValue("$lane", laneId);

        var id = command.ExecuteScalar();

        return id is null or DBNull ? null : Read(connection, Convert.ToInt64(id));
    }

    public DayCloseSummary? FindById(long id)
    {
        using var connection = _database.OpenConnection();
        return Read(connection, id);
    }

    public IReadOnlyList<DayCloseEntry> List(string laneId, int limit = 30)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(laneId);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, closed_at, opened_at, invoice_count, net_sales, cash_expected
            FROM day_closes
            WHERE lane_id = $lane
            ORDER BY closed_at DESC, id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$lane", laneId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 3650));

        var entries = new List<DayCloseEntry>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            entries.Add(new DayCloseEntry(
                reader.GetInt64(0),
                reader.GetDateTimeOffset(1),
                reader.IsDBNull(2) ? null : reader.GetDateTimeOffset(2),
                reader.GetInt32(3),
                reader.GetDecimal(4),
                reader.GetDecimal(5)));
        }

        return entries;
    }

    // ---- Computing -----------------------------------------------------------------------------

    private DayCloseSummary Compute(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string laneId,
        DateTimeOffset closedAt,
        long id)
    {
        const string unreported = "lane_id = $lane AND day_close_id IS NULL AND status = $settled";

        int invoiceCount;
        DateTimeOffset? openedAt = null;
        decimal discount = 0m, net = 0m, cgst = 0m, sgst = 0m, igst = 0m, change = 0m;
        int redeemed = 0, earned = 0;

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT COUNT(*),
                       MIN(created_at),
                       COALESCE(SUM(CAST(total_discount AS REAL)), 0),
                       -- What was taken, so the figure the drawer is counted against is the one the
                       -- customers actually paid. On a lane that rounds, the grand total alone is
                       -- out by up to fifty paise a bill.
                       COALESCE(SUM(CAST(grand_total AS REAL) + CAST(round_off AS REAL)), 0),
                       COALESCE(SUM(CAST(total_cgst AS REAL)), 0),
                       COALESCE(SUM(CAST(total_sgst AS REAL)), 0),
                       COALESCE(SUM(CAST(total_igst AS REAL)), 0),
                       COALESCE(SUM(CAST(change_due AS REAL)), 0),
                       COALESCE(SUM(points_redeemed), 0),
                       COALESCE(SUM(points_earned), 0)
                FROM invoices
                WHERE {unreported};
                """;
            Bind(command, laneId);

            using var reader = command.ExecuteReader();
            reader.Read();

            invoiceCount = reader.GetInt32(0);

            if (!reader.IsDBNull(1))
                openedAt = reader.GetDateTimeOffset(1);

            discount = Round(reader.GetDouble(2));
            net = Round(reader.GetDouble(3));
            cgst = Round(reader.GetDouble(4));
            sgst = Round(reader.GetDouble(5));
            igst = Round(reader.GetDouble(6));
            change = Round(reader.GetDouble(7));
            redeemed = reader.GetInt32(8);
            earned = reader.GetInt32(9);
        }

        var tenders = new List<TenderTotal>();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT p.tender_type, SUM(CAST(p.amount AS REAL)), COUNT(*)
                FROM payments p
                JOIN invoices i ON i.id = p.invoice_id
                WHERE i.{unreported}
                GROUP BY p.tender_type
                ORDER BY p.tender_type;
                """;
            Bind(command, laneId);

            using var reader = command.ExecuteReader();

            while (reader.Read())
                tenders.Add(new TenderTotal((TenderType)reader.GetInt32(0), Round(reader.GetDouble(1)), reader.GetInt32(2)));
        }

        // Voided sales are not takings and carry no tax liability, but a report that simply omits
        // them cannot be reconciled against the invoice run — the numbers would have holes with no
        // explanation. Count and value go on their own line.
        var voidedCount = 0;
        var voidedValue = 0m;

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT COUNT(*), COALESCE(SUM(CAST(grand_total AS REAL) + CAST(round_off AS REAL)), 0)
                FROM invoices
                WHERE lane_id = $lane AND day_close_id IS NULL AND status = $cancelled;
                """;
            command.Parameters.AddWithValue("$lane", laneId);
            command.Parameters.AddWithValue("$cancelled", (int)InvoiceStatus.Cancelled);

            using var reader = command.ExecuteReader();
            reader.Read();

            voidedCount = reader.GetInt32(0);
            voidedValue = Round(reader.GetDouble(1));
        }

        var cashiers = new List<CashierTotal>();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT COALESCE(i.cashier_name, ''),
                       COUNT(*),
                       SUM(CAST(i.grand_total AS REAL) + CAST(i.round_off AS REAL)),
                       COALESCE(SUM((SELECT SUM(CAST(p.amount AS REAL)) FROM payments p
                                     WHERE p.invoice_id = i.id AND p.tender_type = $cash)), 0)
                       - COALESCE(SUM(CAST(i.change_due AS REAL)), 0)
                FROM invoices i
                WHERE i.{unreported}
                GROUP BY COALESCE(i.cashier_name, '')
                ORDER BY COALESCE(i.cashier_name, '');
                """;
            Bind(command, laneId);
            command.Parameters.AddWithValue("$cash", (int)TenderType.Cash);

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                cashiers.Add(new CashierTotal(
                    reader.GetString(0) is { Length: > 0 } name ? name : null,
                    reader.GetInt32(1),
                    Round(reader.GetDouble(2)),
                    Round(reader.GetDouble(3))));
            }
        }

        var slabs = new List<TaxSlabTotal>();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT CAST(l.gst_rate AS REAL),
                       SUM(CAST(l.taxable_value AS REAL)),
                       SUM(CAST(l.cgst_amount AS REAL)),
                       SUM(CAST(l.sgst_amount AS REAL)),
                       SUM(CAST(l.igst_amount AS REAL))
                FROM invoice_lines l
                JOIN invoices i ON i.id = l.invoice_id
                WHERE i.{unreported}
                GROUP BY CAST(l.gst_rate AS REAL)
                ORDER BY CAST(l.gst_rate AS REAL);
                """;
            Bind(command, laneId);

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                slabs.Add(new TaxSlabTotal(
                    Round(reader.GetDouble(0)),
                    Round(reader.GetDouble(1)),
                    Round(reader.GetDouble(2)),
                    Round(reader.GetDouble(3)),
                    Round(reader.GetDouble(4))));
            }
        }

        // Credit paid back since the last close. Not sales, so it touches none of the figures above;
        // the cash part is in the drawer, so it is added to what the drawer should hold.
        var (collected, collectedCash, collectedCount, collectedByCashier) =
            ReadCollections(connection, transaction, "lane_id = $lane AND day_close_id IS NULL", laneId, 0);

        MergeCollectedCash(cashiers, collectedByCashier);

        // Cash in or out of the drawer other than through a sale - a supplier paid from the till.
        var (paidOut, paidIn, movements, movedByCashier) =
            ReadMovements(connection, transaction, "lane_id = $lane AND day_close_id IS NULL", laneId, 0);

        MergeCollectedCash(cashiers, movedByCashier);

        // Goods brought back. A cash refund is already among the movements above.
        var (returnsCount, returnsValue, returnsTax) =
            ReadReturns(connection, transaction, "lane_id = $lane AND day_close_id IS NULL", laneId, 0);

        // What should be in the drawer: notes taken in, less change handed back, plus credit paid
        // back in cash, less what was paid out of it and plus what was put in. Not held at zero once
        // money has been paid out: a supplier paid from the float leaves a drawer short of the
        // day's takings, and the report has to say so rather than round it away.
        var cashExpected = Math.Max(0m, tenders.FirstOrDefault(t => t.Type == TenderType.Cash).Amount - change) + collectedCash
                           - paidOut + paidIn;

        var (parked, orders) = CountHeldBills(laneId);

        return new DayCloseSummary(
            id,
            laneId,
            closedAt,
            openedAt,
            invoiceCount,
            GrossSales: net + discount,
            TotalDiscount: discount,
            NetSales: net,
            // Derived from the total rather than summed separately, for the same reason it is on an
            // invoice: the three figures have to add up.
            TaxableValue: net - (cgst + sgst + igst),
            TotalCgst: cgst,
            TotalSgst: sgst,
            TotalIgst: igst,
            CashExpected: cashExpected,
            ChangeGiven: change,
            PointsRedeemed: redeemed,
            PointsEarned: earned,
            Tenders: tenders,
            TaxSlabs: slabs,
            HeldBillsOutstanding: parked,
            VoidedCount: voidedCount,
            VoidedValue: voidedValue,
            Cashiers: cashiers,
            CreditCollected: collected,
            CreditCollectedCash: collectedCash,
            CreditCollectedCount: collectedCount,
            CashPaidOut: paidOut,
            CashPaidIn: paidIn,
            DrawerMovements: movements,
            ReturnsCount: returnsCount,
            ReturnsValue: returnsValue,
            ReturnsTax: returnsTax,
            OrdersWaiting: orders);

        static void Bind(SqliteCommand command, string laneId)
        {
            command.Parameters.AddWithValue("$lane", laneId);
            command.Parameters.AddWithValue("$settled", (int)InvoiceStatus.Settled);
        }
    }

    /// <summary>
    /// Credit paid back, in all and in cash, and the cash part by who took it.
    /// </summary>
    /// <param name="where">Either the unreported repayments of a lane, or those one close stamped.</param>
    private static (decimal Total, decimal Cash, int Count, Dictionary<string, decimal> CashByCashier) ReadCollections(
        SqliteConnection connection, SqliteTransaction? transaction, string where, string laneId, long closeId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT COALESCE(cashier_name, ''),
                   COUNT(*),
                   COALESCE({PaiseSql.Sum("amount")}, 0),
                   COALESCE(SUM(CASE WHEN tender_type = $cash THEN {PaiseSql.Of("amount")} ELSE 0 END), 0)
            FROM credit_payments
            WHERE {where}
            GROUP BY COALESCE(cashier_name, '');
            """;
        command.Parameters.AddWithValue("$lane", laneId);
        command.Parameters.AddWithValue("$id", closeId);
        command.Parameters.AddWithValue("$cash", (int)TenderType.Cash);

        long total = 0, cash = 0;
        var count = 0;
        var byCashier = new Dictionary<string, decimal>(StringComparer.Ordinal);

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            count += reader.GetInt32(1);
            total += reader.GetInt64(2);
            cash += reader.GetInt64(3);

            if (reader.GetInt64(3) != 0)
                byCashier[reader.GetString(0)] = PaiseSql.Rupees(reader.GetInt64(3));
        }

        return (PaiseSql.Rupees(total), PaiseSql.Rupees(cash), count, byCashier);
    }

    /// <summary>
    /// Cash that went into or out of the drawer other than through a sale: in total each way, by
    /// kind, and by who was on the till.
    /// </summary>
    private static (decimal PaidOut, decimal PaidIn, List<CashMovementTotal> ByKind, Dictionary<string, decimal> ByCashier) ReadMovements(
        SqliteConnection connection, SqliteTransaction? transaction, string where, string laneId, long closeId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT kind, COALESCE(cashier_name, ''), COUNT(*), {PaiseSql.Sum("amount")}
            FROM cash_movements
            WHERE {where}
            GROUP BY kind, COALESCE(cashier_name, '')
            ORDER BY kind;
            """;
        command.Parameters.AddWithValue("$lane", laneId);
        command.Parameters.AddWithValue("$id", closeId);

        long paidOut = 0, paidIn = 0;
        var byKind = new List<CashMovementTotal>();
        var byCashier = new Dictionary<string, decimal>(StringComparer.Ordinal);

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var kind = reader.GetString(0);
            var cashier = reader.GetString(1);
            var count = reader.GetInt32(2);
            var paise = reader.GetInt64(3);

            if (paise < 0)
                paidOut -= paise;
            else
                paidIn += paise;

            var index = byKind.FindIndex(k => k.Kind == kind);

            byKind = index < 0
                ? [.. byKind, new CashMovementTotal(kind, count, PaiseSql.Rupees(paise))]
                : [.. byKind.Select((k, i) => i == index ? k with { Count = k.Count + count, Amount = k.Amount + PaiseSql.Rupees(paise) } : k)];

            byCashier[cashier] = byCashier.GetValueOrDefault(cashier) + PaiseSql.Rupees(paise);
        }

        return (PaiseSql.Rupees(paidOut), PaiseSql.Rupees(paidIn), byKind, byCashier);
    }

    /// <summary>Credit notes: how many, what they refunded, and the tax they took back.</summary>
    private static (int Count, decimal Value, decimal Tax) ReadReturns(
        SqliteConnection connection, SqliteTransaction? transaction, string where, string laneId, long closeId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT COUNT(*),
                   COALESCE(SUM({CreditRepository.RefundedPaiseSql("credit_notes")}), 0),
                   COALESCE({PaiseSql.Sum("total_cgst")}, 0) + COALESCE({PaiseSql.Sum("total_sgst")}, 0)
                     + COALESCE({PaiseSql.Sum("total_igst")}, 0)
            FROM credit_notes
            WHERE {where};
            """;
        command.Parameters.AddWithValue("$lane", laneId);
        command.Parameters.AddWithValue("$id", closeId);

        using var reader = command.ExecuteReader();
        reader.Read();

        return (reader.GetInt32(0), PaiseSql.Rupees(reader.GetInt64(1)), PaiseSql.Rupees(reader.GetInt64(2)));
    }

    /// <summary>
    /// Adds each cashier's cash repayments to the cash they hold.
    /// </summary>
    /// <remarks>
    /// The by-cashier split exists to say whose shift a drawer difference belongs to. Cash one of
    /// them took back on credit is in the drawer on their shift, so leaving it out would make the
    /// shifts disagree with the drawer by exactly that amount and point at the wrong person.
    /// </remarks>
    private static void MergeCollectedCash(List<CashierTotal> cashiers, Dictionary<string, decimal> collectedByCashier)
    {
        foreach (var (key, cash) in collectedByCashier)
        {
            var name = key.Length == 0 ? null : key;
            var index = cashiers.FindIndex(c => c.Name == name);

            if (index >= 0)
                cashiers[index] = cashiers[index] with { CashHeld = cashiers[index].CashHeld + cash };
            else
                cashiers.Add(new CashierTotal(name, 0, 0m, cash));
        }
    }

    /// <summary>
    /// Bills still parked, and orders taken over the phone that are waiting to be collected or
    /// delivered. They are counted apart because only the first are loose ends: an order is meant to
    /// wait overnight.
    /// </summary>
    private (int Parked, int Orders) CountHeldBills(string laneId)
    {
        try
        {
            var held = _heldBills?.List(laneId) ?? [];
            var orders = held.Count(h => h.IsOrder);

            return (held.Count - orders, orders);
        }
        catch (SqliteException)
        {
            // A count for a warning line is not worth failing a close over.
            return (0, 0);
        }
    }

    // ---- Storing -------------------------------------------------------------------------------

    private static long InsertHeader(SqliteConnection connection, SqliteTransaction transaction, DayCloseSummary summary)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO day_closes
              (lane_id, closed_at, opened_at, invoice_count, gross_sales, total_discount, net_sales,
               taxable_value, total_cgst, total_sgst, total_igst, cash_expected, points_redeemed,
               points_earned, voided_count, voided_value, credit_collected, credit_collected_cash,
               cash_paid_out, cash_paid_in, returns_count, returns_value, returns_tax)
            VALUES
              ($lane, $closedAt, $openedAt, $count, $gross, $discount, $net,
               $taxable, $cgst, $sgst, $igst, $cash, $redeemed,
               $earned, $voidedCount, $voidedValue, $collected, $collectedCash,
               $paidOut, $paidIn, $returnsCount, $returnsValue, $returnsTax);
            SELECT last_insert_rowid();
            """;

        command.Parameters.AddWithValue("$lane", summary.LaneId);
        command.Parameters.AddWithValue("$closedAt", summary.ClosedAt);
        command.Parameters.AddWithValue("$openedAt", (object?)summary.OpenedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$count", summary.InvoiceCount);
        command.Parameters.AddWithValue("$gross", summary.GrossSales);
        command.Parameters.AddWithValue("$discount", summary.TotalDiscount);
        command.Parameters.AddWithValue("$net", summary.NetSales);
        command.Parameters.AddWithValue("$taxable", summary.TaxableValue);
        command.Parameters.AddWithValue("$cgst", summary.TotalCgst);
        command.Parameters.AddWithValue("$sgst", summary.TotalSgst);
        command.Parameters.AddWithValue("$igst", summary.TotalIgst);
        command.Parameters.AddWithValue("$cash", summary.CashExpected);
        command.Parameters.AddWithValue("$redeemed", summary.PointsRedeemed);
        command.Parameters.AddWithValue("$earned", summary.PointsEarned);
        command.Parameters.AddWithValue("$voidedCount", summary.VoidedCount);
        command.Parameters.AddWithValue("$voidedValue", summary.VoidedValue);
        command.Parameters.AddWithValue("$collected", summary.CreditCollected);
        command.Parameters.AddWithValue("$collectedCash", summary.CreditCollectedCash);
        command.Parameters.AddWithValue("$paidOut", summary.CashPaidOut);
        command.Parameters.AddWithValue("$paidIn", summary.CashPaidIn);
        command.Parameters.AddWithValue("$returnsCount", summary.ReturnsCount);
        command.Parameters.AddWithValue("$returnsValue", summary.ReturnsValue);
        command.Parameters.AddWithValue("$returnsTax", summary.ReturnsTax);

        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void InsertTenders(SqliteConnection connection, SqliteTransaction transaction, long id, IReadOnlyList<TenderTotal> tenders)
    {
        foreach (var tender in tenders)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO day_close_tenders (day_close_id, tender_type, amount, payment_count)
                VALUES ($id, $type, $amount, $count);
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$type", (int)tender.Type);
            command.Parameters.AddWithValue("$amount", tender.Amount);
            command.Parameters.AddWithValue("$count", tender.PaymentCount);
            command.ExecuteNonQuery();
        }
    }

    private static void InsertTaxSlabs(SqliteConnection connection, SqliteTransaction transaction, long id, IReadOnlyList<TaxSlabTotal> slabs)
    {
        foreach (var slab in slabs)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO day_close_tax_slabs (day_close_id, gst_rate, taxable_value, cgst, sgst, igst)
                VALUES ($id, $rate, $taxable, $cgst, $sgst, $igst);
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$rate", slab.GstRate);
            command.Parameters.AddWithValue("$taxable", slab.TaxableValue);
            command.Parameters.AddWithValue("$cgst", slab.Cgst);
            command.Parameters.AddWithValue("$sgst", slab.Sgst);
            command.Parameters.AddWithValue("$igst", slab.Igst);
            command.ExecuteNonQuery();
        }
    }

    // ---- Reading -------------------------------------------------------------------------------

    private DayCloseSummary? Read(SqliteConnection connection, long id)
    {
        DayCloseSummary summary;

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT lane_id, closed_at, opened_at, invoice_count, gross_sales, total_discount,
                       net_sales, taxable_value, total_cgst, total_sgst, total_igst, cash_expected,
                       points_redeemed, points_earned, voided_count, voided_value,
                       credit_collected, credit_collected_cash, cash_paid_out, cash_paid_in,
                       returns_count, returns_value, returns_tax
                FROM day_closes WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);

            using var reader = command.ExecuteReader();

            if (!reader.Read())
                return null;

            var net = reader.GetDecimal(6);
            var discount = reader.GetDecimal(5);

            summary = new DayCloseSummary(
                id,
                reader.GetString(0),
                reader.GetDateTimeOffset(1),
                reader.IsDBNull(2) ? null : reader.GetDateTimeOffset(2),
                reader.GetInt32(3),
                reader.GetDecimal(4),
                discount,
                net,
                reader.GetDecimal(7),
                reader.GetDecimal(8),
                reader.GetDecimal(9),
                reader.GetDecimal(10),
                reader.GetDecimal(11),
                ChangeGiven: 0m,
                reader.GetInt32(12),
                reader.GetInt32(13),
                Tenders: [],
                TaxSlabs: [],
                HeldBillsOutstanding: 0,
                VoidedCount: reader.GetInt32(14),
                VoidedValue: reader.GetDecimal(15),
                CreditCollected: reader.GetDecimal(16),
                CreditCollectedCash: reader.GetDecimal(17),
                CashPaidOut: reader.GetDecimal(18),
                CashPaidIn: reader.GetDecimal(19),
                ReturnsCount: reader.GetInt32(20),
                ReturnsValue: reader.GetDecimal(21),
                ReturnsTax: reader.GetDecimal(22));
        }

        // Recomputed from the invoices this close stamped, rather than stored a second time. The
        // link makes an old report's cashier breakdown reproducible without another table.
        var cashiers = new List<CashierTotal>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COALESCE(i.cashier_name, ''),
                       COUNT(*),
                       SUM(CAST(i.grand_total AS REAL) + CAST(i.round_off AS REAL)),
                       COALESCE(SUM((SELECT SUM(CAST(p.amount AS REAL)) FROM payments p
                                     WHERE p.invoice_id = i.id AND p.tender_type = $cash)), 0)
                       - COALESCE(SUM(CAST(i.change_due AS REAL)), 0)
                FROM invoices i
                WHERE i.day_close_id = $id AND i.status = $settled
                GROUP BY COALESCE(i.cashier_name, '')
                ORDER BY COALESCE(i.cashier_name, '');
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$settled", (int)InvoiceStatus.Settled);
            command.Parameters.AddWithValue("$cash", (int)TenderType.Cash);

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                cashiers.Add(new CashierTotal(
                    reader.GetString(0) is { Length: > 0 } name ? name : null,
                    reader.GetInt32(1),
                    Round(reader.GetDouble(2)),
                    Round(reader.GetDouble(3))));
            }
        }

        var tenders = new List<TenderTotal>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT tender_type, amount, payment_count FROM day_close_tenders WHERE day_close_id = $id ORDER BY tender_type;";
            command.Parameters.AddWithValue("$id", id);

            using var reader = command.ExecuteReader();

            while (reader.Read())
                tenders.Add(new TenderTotal((TenderType)reader.GetInt32(0), reader.GetDecimal(1), reader.GetInt32(2)));
        }

        var slabs = new List<TaxSlabTotal>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT gst_rate, taxable_value, cgst, sgst, igst FROM day_close_tax_slabs WHERE day_close_id = $id ORDER BY CAST(gst_rate AS REAL);";
            command.Parameters.AddWithValue("$id", id);

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                slabs.Add(new TaxSlabTotal(
                    reader.GetDecimal(0), reader.GetDecimal(1), reader.GetDecimal(2), reader.GetDecimal(3), reader.GetDecimal(4)));
            }
        }

        var cash = tenders.FirstOrDefault(t => t.Type == TenderType.Cash).Amount;

        // The cashier split is recomputed, so the cash repayments this close stamped have to be
        // folded back in exactly as they were on the night.
        var (_, _, collectedCount, collectedByCashier) =
            ReadCollections(connection, null, "day_close_id = $id", summary.LaneId, id);

        MergeCollectedCash(cashiers, collectedByCashier);

        var (_, _, movements, movedByCashier) =
            ReadMovements(connection, null, "day_close_id = $id", summary.LaneId, id);

        MergeCollectedCash(cashiers, movedByCashier);

        return summary with
        {
            Tenders = tenders,
            TaxSlabs = slabs,
            Cashiers = cashiers,
            CreditCollectedCount = collectedCount,
            DrawerMovements = movements,

            // Change is not stored; it is what the stored drawer figure leaves unexplained. The
            // drawer also holds credit paid back in cash and has had cash paid out of it and put in,
            // and without undoing those first a reprinted report would show them as change.
            ChangeGiven = Money.ToPresentation(cash - (summary.CashExpected - summary.CreditCollectedCash + summary.CashPaidOut - summary.CashPaidIn)),
        };
    }

    /// <summary>
    /// Money is stored as text and summed by SQLite as a double, so it comes back with a floating
    /// tail. Every figure aggregated here is already a whole number of paise, so rounding to two
    /// places recovers exactly what was stored.
    /// </summary>
    private static decimal Round(double value) =>
        Money.ToPresentation(decimal.Parse(value.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture));
}
