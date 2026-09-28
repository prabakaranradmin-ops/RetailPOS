using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Pos.Core.Data;
using Pos.Core.Domain;

namespace Pos.Core.Analytics;

/// <summary>Taxable sales to customers without a GSTIN, at one rate, supplied into one state.</summary>
public sealed record GstRateRow(string PlaceOfSupply, decimal Rate, decimal TaxableValue, decimal Cgst, decimal Sgst, decimal Igst)
{
    public decimal Tax => Cgst + Sgst + Igst;
}

/// <summary>
/// One rate on one large bill into another state, which a return lists bill by bill rather than
/// folding into the totals.
/// </summary>
public sealed record GstLargeInvoiceRow(
    string InvoiceNo,
    DateOnly Date,
    decimal InvoiceValue,
    string PlaceOfSupply,
    decimal Rate,
    decimal TaxableValue,
    decimal Igst);

/// <summary>One line of the HSN summary: a code, sold in one unit, at one rate.</summary>
public sealed record GstHsnRow(
    string Hsn,
    string Description,
    string Uqc,
    decimal Rate,
    decimal Quantity,
    decimal TotalValue,
    decimal TaxableValue,
    decimal Igst,
    decimal Cgst,
    decimal Sgst);

/// <summary>
/// One run of bill numbers: the first and last issued in the month, how many, and how many of those
/// were cancelled.
/// </summary>
public sealed record GstDocumentSeries(string From, string To, int Total, int Cancelled);

/// <summary>
/// What one lane sold in one month, arranged the way the monthly GST return asks for it.
/// </summary>
/// <remarks>
/// Every figure is read from the bills as they were issued - the taxable value, CGST, SGST and IGST
/// already worked out on each line by the tax engine. Nothing is recalculated here, so the return
/// cannot disagree with the bills it summarises.
/// </remarks>
public sealed record GstReturnData
{
    public required string LaneId { get; init; }

    /// <summary>The first day of the month the figures cover.</summary>
    public required DateOnly Month { get; init; }

    /// <summary>Where the shop is. Every sale not marked inter-state was supplied here.</summary>
    public required string OutletStateCode { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>Taxable supplies to customers without a GSTIN, by place of supply and rate (B2CS).</summary>
    public required IReadOnlyList<GstRateRow> RateWise { get; init; }

    /// <summary>Large bills into another state, listed one by one (B2CL).</summary>
    public required IReadOnlyList<GstLargeInvoiceRow> LargeInterState { get; init; }

    /// <summary>What was sold at 0% within the state.</summary>
    public required decimal NilIntraState { get; init; }

    /// <summary>What was sold at 0% into another state.</summary>
    public required decimal NilInterState { get; init; }

    public required IReadOnlyList<GstHsnRow> Hsn { get; init; }

    public required IReadOnlyList<GstDocumentSeries> Documents { get; init; }

    /// <summary>Tax invoices that stand - issued in the month and not cancelled.</summary>
    public required int TaxInvoices { get; init; }

    /// <summary>
    /// Bills of supply issued under the composition scheme. Not part of this return; counted so a
    /// month that changed scheme part way through says so.
    /// </summary>
    public required int BillsOfSupply { get; init; }

    public required decimal BillsOfSupplyValue { get; init; }

    /// <summary>Things the accountant has to look at before filing.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    public decimal TaxableValue => RateWise.Sum(r => r.TaxableValue) + LargeInterState.Sum(r => r.TaxableValue);
    public decimal Cgst => RateWise.Sum(r => r.Cgst);
    public decimal Sgst => RateWise.Sum(r => r.Sgst);
    public decimal Igst => RateWise.Sum(r => r.Igst) + LargeInterState.Sum(r => r.Igst);
    public decimal Tax => Cgst + Sgst + Igst;
    public decimal NilRated => NilIntraState + NilInterState;

    public bool HasAnything => TaxInvoices > 0 || BillsOfSupply > 0 || Documents.Count > 0;
}

/// <summary>
/// Reads a month of one lane's bills and arranges them for the monthly GST return.
/// </summary>
/// <remarks>
/// <para>
/// Read-only, like the rest of the owner's figures, and summed in exact paise (<see cref="PaiseSql"/>)
/// so the tax filed is the tax on the bills to the paisa. Quantities are summed in exact thousandths
/// for the same reason: the HSN summary files a total quantity, and a kilogram total added up in
/// floating point is a few grams out by the end of a month.
/// </para>
/// <para>
/// <b>One lane at a time.</b> Each till keeps its own books and its own run of bill numbers. A shop
/// with two tills files one return, so its accountant adds the lanes' figures together; the bill
/// number runs are listed separately, which is how a return records them anyway.
/// </para>
/// </remarks>
public sealed partial class GstReturnQuery(PosDatabase database)
{
    /// <summary>
    /// The value above which a bill to a customer without a GSTIN, in another state, is listed on its
    /// own (B2CL) rather than folded into the totals. One lakh since 1 August 2024; it was two and a
    /// half before that.
    /// </summary>
    public const decimal LargeInterStateThreshold = 100_000m;

    private readonly PosDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

    private const string Settled = "i.voided_at IS NULL";

    /// <summary>A bill issued as a tax invoice. Bills from before the column existed were all tax invoices.</summary>
    private const string TaxInvoice = "i.tax_mode <> 'Composition'";

    private const string InWindow = "i.lane_id = $lane AND i.created_at >= $from AND i.created_at < $to";

    public GstReturnData Gather(string laneId, DateOnly month, string outletStateCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(laneId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outletStateCode);

        month = new DateOnly(month.Year, month.Month, 1);

        var from = LocalMidnight(month);
        var to = LocalMidnight(month.AddMonths(1));
        var home = outletStateCode.Trim().PadLeft(2, '0');

        using var connection = _database.OpenConnection();

        var warnings = new List<string>();
        var lines = ReadLines(connection, laneId, from, to);
        var large = ReadLargeInterState(connection, laneId, from, to, home);
        var documents = ReadDocuments(connection, laneId, from, to, warnings);
        var (taxInvoices, billsOfSupply, billsOfSupplyValue) = ReadCounts(connection, laneId, from, to);

        var rateWise = new Dictionary<(string Pos, decimal Rate), (long Taxable, long Cgst, long Sgst, long Igst)>();
        var hsn = new Dictionary<(string Hsn, string Uqc, decimal Rate), HsnTotal>();
        long nilIntra = 0, nilInter = 0;
        var unknownPlace = 0;

        foreach (var line in lines)
        {
            var pos = line.InterState ? line.CustomerState : home;

            if (line.InterState && pos.Length == 0)
                unknownPlace++;

            if (line.Rate == 0m)
            {
                // Nothing is taxed at 0%, so the value of the goods is the whole of it.
                if (line.InterState)
                    nilInter += line.Total;
                else
                    nilIntra += line.Total;
            }
            else if (!line.Large)
            {
                var key = (pos, line.Rate);
                rateWise.TryGetValue(key, out var sum);
                rateWise[key] = (sum.Taxable + line.Taxable, sum.Cgst + line.Cgst, sum.Sgst + line.Sgst, sum.Igst + line.Igst);
            }

            // The HSN summary covers every supply, whatever list it was reported in above.
            var hsnKey = (line.Hsn, Uqc.For(line.Unit), line.Rate);

            if (!hsn.TryGetValue(hsnKey, out var total))
                hsn[hsnKey] = total = new HsnTotal();

            total.Add(line);
        }

        if (unknownPlace > 0)
        {
            warnings.Add($"{unknownPlace} line(s) were billed as inter-state to a customer whose state is no longer on record " +
                         "(the customer was forgotten, or no state was entered). They are in the figures under a blank place of supply - " +
                         "your accountant needs to say which state they went to.");
        }

        var shortCodes = hsn.Keys.Select(k => k.Hsn).Where(code => code.Trim().Length < 4).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList();

        if (shortCodes.Count > 0)
        {
            warnings.Add($"HSN code(s) shorter than four digits: {string.Join(", ", shortCodes)}. A return needs at least four; " +
                         "correct them in the catalogue so next month's bills carry the full code.");
        }

        if (billsOfSupply > 0)
        {
            warnings.Add($"{billsOfSupply} bill(s) this month were bills of supply under the composition scheme, worth " +
                         $"{Money(billsOfSupplyValue)}. They are not part of GSTR-1 and are not in the figures below - a composition " +
                         "dealer reports turnover on CMP-08 instead.");
        }

        if (nilIntra + nilInter > 0)
        {
            warnings.Add("Everything sold at 0% is listed as nil rated. Goods exempted by notification rather than rated at nil - " +
                         "fresh vegetables, fruit, flowers, fresh milk, eggs - belong in the exempted column; your accountant moves them.");
        }

        return new GstReturnData
        {
            LaneId = laneId,
            Month = month,
            OutletStateCode = home,
            GeneratedAt = DateTimeOffset.Now,
            RateWise = [.. rateWise
                .OrderBy(r => r.Key.Pos, StringComparer.Ordinal)
                .ThenBy(r => r.Key.Rate)
                .Select(r => new GstRateRow(r.Key.Pos, r.Key.Rate, Rupees(r.Value.Taxable), Rupees(r.Value.Cgst), Rupees(r.Value.Sgst), Rupees(r.Value.Igst)))],
            LargeInterState = large,
            NilIntraState = Rupees(nilIntra),
            NilInterState = Rupees(nilInter),
            Hsn = [.. hsn
                .OrderBy(h => h.Key.Hsn, StringComparer.Ordinal)
                .ThenBy(h => h.Key.Rate)
                .ThenBy(h => h.Key.Uqc, StringComparer.Ordinal)
                .Select(h => h.Value.ToRow(h.Key.Hsn, h.Key.Uqc, h.Key.Rate))],
            Documents = documents,
            TaxInvoices = taxInvoices,
            BillsOfSupply = billsOfSupply,
            BillsOfSupplyValue = billsOfSupplyValue,
            Warnings = warnings,
        };
    }

    /// <summary>Midnight at the start of a day, at the offset this machine keeps on that day.</summary>
    private static DateTimeOffset LocalMidnight(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private sealed record LineFact(
        string Hsn,
        UnitType Unit,
        decimal Rate,
        bool InterState,
        string CustomerState,
        string Name,
        bool Large,
        long QuantityThousandths,
        long Total,
        long Taxable,
        long Cgst,
        long Sgst,
        long Igst);

    /// <summary>
    /// The bills in the month that go on the list of large inter-state bills: sold into another
    /// state, to a customer without a GSTIN, for more than <see cref="LargeInterStateThreshold"/>.
    /// </summary>
    private static string LargeBills => $"""
        SELECT i.id FROM invoices i
        WHERE {InWindow} AND {Settled} AND {TaxInvoice}
          AND EXISTS (SELECT 1 FROM invoice_lines x WHERE x.invoice_id = i.id AND x.is_inter_state = 1)
          AND {PaiseSql.Of("i.grand_total")} + {PaiseSql.Of("i.round_off")} > $threshold
        """;

    private static List<LineFact> ReadLines(SqliteConnection connection, string lane, DateTimeOffset from, DateTimeOffset to)
    {
        using var command = Prepare(connection, lane, from, to, $"""
            WITH large AS ({LargeBills})
            SELECT l.hsn_snapshot,
                   l.unit_type,
                   CAST(l.gst_rate AS REAL) AS rate,
                   l.is_inter_state,
                   COALESCE(TRIM(c.state_code), '') AS pos,
                   l.name_snapshot,
                   i.id IN (SELECT id FROM large) AS big,
                   SUM(CAST(ROUND(CAST(l.quantity AS REAL) * 1000) AS INTEGER)),
                   COALESCE({PaiseSql.Sum("l.line_total")}, 0),
                   COALESCE({PaiseSql.Sum("l.taxable_value")}, 0),
                   COALESCE({PaiseSql.Sum("l.cgst_amount")}, 0),
                   COALESCE({PaiseSql.Sum("l.sgst_amount")}, 0),
                   COALESCE({PaiseSql.Sum("l.igst_amount")}, 0)
            FROM invoice_lines l
            JOIN invoices i ON i.id = l.invoice_id
            LEFT JOIN customers c ON c.id = i.customer_id
            WHERE {InWindow} AND {Settled} AND {TaxInvoice}
            GROUP BY l.hsn_snapshot, l.unit_type, rate, l.is_inter_state, pos, l.name_snapshot, big;
            """);

        var lines = new List<LineFact>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var pos = reader.GetString(4);

            lines.Add(new LineFact(
                reader.GetString(0).Trim(),
                (UnitType)reader.GetInt32(1),
                Math.Round((decimal)reader.GetDouble(2), 2),
                reader.GetBoolean(3),
                pos.Length == 0 ? pos : pos.PadLeft(2, '0'),
                reader.GetString(5),
                reader.GetBoolean(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.GetInt64(9),
                reader.GetInt64(10),
                reader.GetInt64(11),
                reader.GetInt64(12)));
        }

        return lines;
    }

    private static List<GstLargeInvoiceRow> ReadLargeInterState(SqliteConnection connection, string lane, DateTimeOffset from, DateTimeOffset to, string home)
    {
        using var command = Prepare(connection, lane, from, to, $"""
            WITH large AS ({LargeBills})
            SELECT i.invoice_no,
                   substr(i.created_at, 1, 10),
                   {PaiseSql.Of("i.grand_total")} + {PaiseSql.Of("i.round_off")},
                   COALESCE(TRIM(c.state_code), ''),
                   CAST(l.gst_rate AS REAL) AS rate,
                   COALESCE({PaiseSql.Sum("l.taxable_value")}, 0),
                   COALESCE({PaiseSql.Sum("l.igst_amount")}, 0)
            FROM invoices i
            JOIN invoice_lines l ON l.invoice_id = i.id
            LEFT JOIN customers c ON c.id = i.customer_id
            WHERE i.id IN (SELECT id FROM large) AND CAST(l.gst_rate AS REAL) > 0
            GROUP BY i.id, rate
            ORDER BY i.id, rate;
            """);

        var rows = new List<GstLargeInvoiceRow>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var pos = reader.GetString(3);

            rows.Add(new GstLargeInvoiceRow(
                reader.GetString(0),
                DateOnly.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                Rupees(reader.GetInt64(2)),
                pos.Length == 0 ? pos : pos.PadLeft(2, '0'),
                Math.Round((decimal)reader.GetDouble(4), 2),
                Rupees(reader.GetInt64(5)),
                Rupees(reader.GetInt64(6))));
        }

        return rows;
    }

    private static (int TaxInvoices, int BillsOfSupply, decimal BillsOfSupplyValue) ReadCounts(
        SqliteConnection connection, string lane, DateTimeOffset from, DateTimeOffset to)
    {
        using var command = Prepare(connection, lane, from, to, $"""
            SELECT COALESCE(SUM({TaxInvoice}), 0),
                   COALESCE(SUM(NOT ({TaxInvoice})), 0),
                   COALESCE(SUM(CASE WHEN {TaxInvoice} THEN 0 ELSE {PaiseSql.Of("i.grand_total")} + {PaiseSql.Of("i.round_off")} END), 0)
            FROM invoices i
            WHERE {InWindow} AND {Settled};
            """);

        using var reader = command.ExecuteReader();
        reader.Read();

        return (reader.GetInt32(0), reader.GetInt32(1), Rupees(reader.GetInt64(2)));
    }

    /// <summary>The bill number runs issued in the month, cancelled bills included - they were issued.</summary>
    private static List<GstDocumentSeries> ReadDocuments(
        SqliteConnection connection, string lane, DateTimeOffset from, DateTimeOffset to, List<string> warnings)
    {
        using var command = Prepare(connection, lane, from, to, $"""
            SELECT i.invoice_no, i.voided_at IS NOT NULL
            FROM invoices i
            WHERE {InWindow}
            ORDER BY i.id;
            """);

        var runs = new List<(string Prefix, List<(long Number, string No, bool Cancelled)> Bills)>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var no = reader.GetString(0);
            var match = TrailingNumber().Match(no);
            var prefix = match.Success ? no[..match.Index] : no;
            var number = match.Success && long.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0L;

            var run = runs.FirstOrDefault(r => r.Prefix == prefix);

            if (run.Bills is null)
            {
                run = (prefix, []);
                runs.Add(run);
            }

            run.Bills.Add((number, no, reader.GetBoolean(1)));
        }

        var series = new List<GstDocumentSeries>();

        foreach (var (prefix, bills) in runs)
        {
            var first = bills.MinBy(b => b.Number);
            var last = bills.MaxBy(b => b.Number);

            series.Add(new GstDocumentSeries(first.No, last.No, bills.Count, bills.Count(b => b.Cancelled)));

            // A return states the first and last number and how many there were. If those do not
            // agree, bills are missing from the run, and that is a question to answer before filing.
            var span = last.Number - first.Number + 1;

            if (span != bills.Count)
                warnings.Add($"The bill numbers from {first.No} to {last.No} span {span} numbers but {bills.Count} bills were issued. Find out why before filing.");
        }

        return series;
    }

    [GeneratedRegex(@"\d+$")]
    private static partial Regex TrailingNumber();

    private sealed class HsnTotal
    {
        private long _quantity, _total, _taxable, _cgst, _sgst, _igst;
        private readonly Dictionary<string, long> _byName = new(StringComparer.Ordinal);

        public void Add(LineFact line)
        {
            _quantity += line.QuantityThousandths;
            _total += line.Total;
            _taxable += line.Taxable;
            _cgst += line.Cgst;
            _sgst += line.Sgst;
            _igst += line.Igst;

            _byName.TryGetValue(line.Name, out var value);
            _byName[line.Name] = value + line.Total;
        }

        /// <summary>
        /// Described by what sold most under it. A return's description is free text; the item that
        /// brought in most is the one that tells the accountant what the code was used for.
        /// </summary>
        public GstHsnRow ToRow(string hsn, string uqc, decimal rate)
        {
            var top = _byName.OrderByDescending(n => n.Value).ThenBy(n => n.Key, StringComparer.Ordinal).First().Key;
            var others = _byName.Count - 1;
            var description = others > 0 ? $"{top} and {others} more" : top;

            return new GstHsnRow(
                hsn,
                description,
                uqc,
                rate,
                _quantity / 1000m,
                Rupees(_total),
                Rupees(_taxable),
                Rupees(_igst),
                Rupees(_cgst),
                Rupees(_sgst));
        }
    }

    private static SqliteCommand Prepare(SqliteConnection connection, string lane, DateTimeOffset from, DateTimeOffset to, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$lane", lane);

        // Bound as DateTimeOffset, never as a formatted string: the driver writes created_at with a
        // space between date and time, and a "T" sorts after it - which once dropped the first day
        // of every window from the owner's figures.
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        command.Parameters.AddWithValue("$threshold", (long)(LargeInterStateThreshold * 100m));
        return command;
    }

    private static decimal Rupees(long paise) => PaiseSql.Rupees(paise);

    private static string Money(decimal value) => "Rs " + value.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
}
