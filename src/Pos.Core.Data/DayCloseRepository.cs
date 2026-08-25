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
            stamp.CommandText = """
                UPDATE invoices
                SET day_close_id = $id
                WHERE lane_id = $lane AND day_close_id IS NULL AND status = $settled;
                """;
            stamp.Parameters.AddWithValue("$id", id);
            stamp.Parameters.AddWithValue("$lane", laneId);
            stamp.Parameters.AddWithValue("$settled", (int)InvoiceStatus.Settled);
            stamp.ExecuteNonQuery();
        }

        transaction.Commit();

        return summary with { Id = id, HeldBillsOutstanding = CountHeldBills(laneId) };
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
                       COALESCE(SUM(CAST(grand_total AS REAL)), 0),
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

        // What should be in the drawer: notes taken in, less change handed back.
        var cashExpected = Math.Max(0m, tenders.FirstOrDefault(t => t.Type == TenderType.Cash).Amount - change);

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
            HeldBillsOutstanding: CountHeldBills(laneId));

        static void Bind(SqliteCommand command, string laneId)
        {
            command.Parameters.AddWithValue("$lane", laneId);
            command.Parameters.AddWithValue("$settled", (int)InvoiceStatus.Settled);
        }
    }

    private int CountHeldBills(string laneId)
    {
        try
        {
            return _heldBills?.List(laneId).Count ?? 0;
        }
        catch (SqliteException)
        {
            // A count for a warning line is not worth failing a close over.
            return 0;
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
               taxable_value, total_cgst, total_sgst, total_igst, cash_expected, points_redeemed, points_earned)
            VALUES
              ($lane, $closedAt, $openedAt, $count, $gross, $discount, $net,
               $taxable, $cgst, $sgst, $igst, $cash, $redeemed, $earned);
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
                       points_redeemed, points_earned
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
                HeldBillsOutstanding: 0);
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

        return summary with
        {
            Tenders = tenders,
            TaxSlabs = slabs,
            ChangeGiven = Money.ToPresentation(cash - summary.CashExpected),
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
