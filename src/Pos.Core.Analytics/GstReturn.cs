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
/// One run of document numbers: the first and last issued in the month, how many, and how many of
/// those were cancelled.
/// </summary>
/// <param name="Nature">What the documents are, in the words of the documents-issued table.</param>
public sealed record GstDocumentSeries(string From, string To, int Total, int Cancelled, string Nature = GstDocumentSeries.OutwardInvoices)
{
    public const string OutwardInvoices = "Invoices for outward supply";
    public const string CreditNotes = "Credit Note";
}

/// <summary>
/// One rate on a credit note against a large bill into another state. The bill was listed on its
/// own, so its return is too (CDNUR), rather than being netted out of the totals.
/// </summary>
public sealed record GstCreditNoteRow(
    string NoteNo,
    DateOnly NoteDate,
    string InvoiceNo,
    decimal NoteValue,
    string PlaceOfSupply,
    decimal Rate,
    decimal TaxableValue,
    decimal Igst);

/// <summary>One rate on one bill to a business, which the return lists bill by bill with its GSTIN (B2B).</summary>
public sealed record GstB2bRow(
    string Gstin,
    string Name,
    string InvoiceNo,
    DateOnly Date,
    decimal InvoiceValue,
    string PlaceOfSupply,
    decimal Rate,
    decimal TaxableValue,
    decimal Igst,
    decimal Cgst,
    decimal Sgst);

/// <summary>One rate on a credit note against a bill to a business, listed note by note (CDNR).</summary>
public sealed record GstB2bCreditNoteRow(
    string Gstin,
    string Name,
    string NoteNo,
    DateOnly NoteDate,
    string InvoiceNo,
    decimal NoteValue,
    string PlaceOfSupply,
    decimal Rate,
    decimal TaxableValue,
    decimal Igst,
    decimal Cgst,
    decimal Sgst);

/// <summary>Input tax on the month's purchase bills, at one rate.</summary>
public sealed record GstInputRow(decimal Rate, decimal TaxableValue, decimal Igst, decimal Cgst, decimal Sgst)
{
    public decimal Tax => Igst + Cgst + Sgst;
}

/// <summary>One supplier's bill in the month's purchase register.</summary>
/// <param name="ChargesGst">False for an unregistered or composition supplier, whose bill carries no input tax.</param>
public sealed record GstPurchaseRow(
    string? SupplierGstin,
    string SupplierName,
    string BillNo,
    DateOnly BillDate,
    decimal TaxableValue,
    decimal Igst,
    decimal Cgst,
    decimal Sgst,
    decimal Total,
    bool ChargesGst)
{
    /// <summary>The bill's tax at each rate it carried.</summary>
    public IReadOnlyList<GstInputRow> Rates { get; init; } = [];
}

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

    /// <summary>Input tax on the month's purchase bills from GST-registered suppliers, by rate.</summary>
    public IReadOnlyList<GstInputRow> Inputs { get; init; } = [];

    /// <summary>Every purchase bill dated in the month, for matching against GSTR-2B.</summary>
    public IReadOnlyList<GstPurchaseRow> Purchases { get; init; } = [];

    public decimal InputTax => Inputs.Sum(i => i.Tax);

    /// <summary>Credit notes issued in the month against tax invoices, from any month.</summary>
    public int CreditNotes { get; init; }

    /// <summary>What those credit notes refunded, tax included.</summary>
    public decimal CreditNotesValue { get; init; }

    /// <summary>
    /// Credit notes against large inter-state bills, listed one by one (CDNUR). Every other credit
    /// note is already netted out of <see cref="RateWise"/>, the nil figures and the HSN summary.
    /// </summary>
    public IReadOnlyList<GstCreditNoteRow> LargeCreditNotes { get; init; } = [];

    /// <summary>
    /// Bills to businesses, listed bill by bill with the buyer's GSTIN (B2B). None of them is in
    /// <see cref="RateWise"/>, <see cref="LargeInterState"/>, the unregistered nil figures or
    /// <see cref="Hsn"/>: those are for customers without a GSTIN.
    /// </summary>
    public IReadOnlyList<GstB2bRow> B2b { get; init; } = [];

    /// <summary>Credit notes against bills to businesses, note by note (CDNR). Not netted out anywhere else.</summary>
    public IReadOnlyList<GstB2bCreditNoteRow> B2bCreditNotes { get; init; } = [];

    /// <summary>The HSN summary of supplies to businesses, which the return keeps apart from the rest.</summary>
    public IReadOnlyList<GstHsnRow> HsnB2b { get; init; } = [];

    /// <summary>What was sold at 0% to businesses within the state.</summary>
    public decimal NilB2bIntraState { get; init; }

    /// <summary>What was sold at 0% to businesses in another state.</summary>
    public decimal NilB2bInterState { get; init; }

    /// <summary>How many bills went to businesses.</summary>
    public int B2bInvoices => B2b.Select(r => r.InvoiceNo).Distinct(StringComparer.Ordinal).Count();

    public decimal TaxableValue => RateWise.Sum(r => r.TaxableValue) + LargeInterState.Sum(r => r.TaxableValue) - LargeCreditNotes.Sum(r => r.TaxableValue)
                                   + B2b.Sum(r => r.TaxableValue) - B2bCreditNotes.Sum(r => r.TaxableValue);
    public decimal Cgst => RateWise.Sum(r => r.Cgst) + B2b.Sum(r => r.Cgst) - B2bCreditNotes.Sum(r => r.Cgst);
    public decimal Sgst => RateWise.Sum(r => r.Sgst) + B2b.Sum(r => r.Sgst) - B2bCreditNotes.Sum(r => r.Sgst);
    public decimal Igst => RateWise.Sum(r => r.Igst) + LargeInterState.Sum(r => r.Igst) - LargeCreditNotes.Sum(r => r.Igst)
                           + B2b.Sum(r => r.Igst) - B2bCreditNotes.Sum(r => r.Igst);
    public decimal Tax => Cgst + Sgst + Igst;
    public decimal NilRated => NilIntraState + NilInterState + NilB2bIntraState + NilB2bInterState;

    public bool HasAnything => TaxInvoices > 0 || BillsOfSupply > 0 || Documents.Count > 0 || Purchases.Count > 0;
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

    /// <summary>A bill to a customer without a GSTIN: B2CS, B2CL, the unregistered nil rows, the B2C HSN summary.</summary>
    private const string ToConsumer = "i.buyer_gstin IS NULL";

    /// <summary>A bill to a business, by the GSTIN copied onto it when it was sold: B2B, CDNR, the B2B HSN summary.</summary>
    private const string ToBusiness = "i.buyer_gstin IS NOT NULL";

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
        var lines = ReadLines(connection, laneId, from, to, ToConsumer);
        var large = ReadLargeInterState(connection, laneId, from, to, home);
        var documents = ReadDocuments(connection, laneId, from, to, warnings);
        var (returned, largeReturns, consumerNotes, consumerNotesValue) = ReadCreditNotes(connection, laneId, from, to, ToConsumer);

        // Bills to businesses, and what came back from them: their own tables, their own HSN summary.
        var businessLines = ReadLines(connection, laneId, from, to, ToBusiness);
        var (businessReturned, _, businessNotes, businessNotesValue) = ReadCreditNotes(connection, laneId, from, to, ToBusiness);
        var b2b = ReadB2b(connection, laneId, from, to);
        var cdnr = ReadCdnr(connection, laneId, from, to);
        var creditNotes = consumerNotes + businessNotes;
        var creditNotesValue = consumerNotesValue + businessNotesValue;
        var (taxInvoices, billsOfSupply, billsOfSupplyValue) = ReadCounts(connection, laneId, from, to);
        var purchases = ReadPurchases(connection, month);

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

        // Goods returned this month come off this month's figures, whichever month they were sold
        // in: a credit note is reported in the return for the period it was issued. Against a large
        // inter-state bill it is listed on its own instead, as the bill was.
        foreach (var line in returned)
        {
            var pos = line.InterState ? line.CustomerState : home;

            if (line.Rate == 0m)
            {
                if (line.InterState)
                    nilInter -= line.Total;
                else
                    nilIntra -= line.Total;
            }
            else if (!line.Large)
            {
                var key = (pos, line.Rate);
                rateWise.TryGetValue(key, out var sum);
                rateWise[key] = (sum.Taxable - line.Taxable, sum.Cgst - line.Cgst, sum.Sgst - line.Sgst, sum.Igst - line.Igst);
            }

            var hsnKey = (line.Hsn, Uqc.For(line.Unit), line.Rate);

            if (!hsn.TryGetValue(hsnKey, out var total))
                hsn[hsnKey] = total = new HsnTotal();

            total.Subtract(line);
        }

        // The same again for bills to businesses, kept apart as the return keeps them.
        var hsnB2b = new Dictionary<(string Hsn, string Uqc, decimal Rate), HsnTotal>();
        long nilB2bIntra = 0, nilB2bInter = 0;

        foreach (var line in businessLines)
        {
            if (line.Rate == 0m)
            {
                if (line.InterState)
                    nilB2bInter += line.Total;
                else
                    nilB2bIntra += line.Total;
            }

            var key = (line.Hsn, Uqc.For(line.Unit), line.Rate);

            if (!hsnB2b.TryGetValue(key, out var total))
                hsnB2b[key] = total = new HsnTotal();

            total.Add(line);
        }

        foreach (var line in businessReturned)
        {
            if (line.Rate == 0m)
            {
                if (line.InterState)
                    nilB2bInter -= line.Total;
                else
                    nilB2bIntra -= line.Total;
            }

            var key = (line.Hsn, Uqc.For(line.Unit), line.Rate);

            if (!hsnB2b.TryGetValue(key, out var total))
                hsnB2b[key] = total = new HsnTotal();

            total.Subtract(line);
        }

        if (consumerNotes > 0)
        {
            warnings.Add($"{Plural.Of(consumerNotes, "credit note")} {Were(consumerNotes)} issued this month for goods returned by customers without a GSTIN, worth {Money(consumerNotesValue)}. " +
                         "They are taken off the sales figures below - the B2CS rates, the nil-rated totals and the HSN summary - " +
                         "as the return expects for sales to customers without a GSTIN." +
                         (largeReturns.Count > 0 ? " Those against large inter-state bills are listed on their own (CDNUR)." : string.Empty));
        }

        if (b2b.Count > 0 || nilB2bIntra + nilB2bInter != 0)
        {
            var bills = b2b.Select(r => r.InvoiceNo).Distinct(StringComparer.Ordinal).Count();
            warnings.Add($"{Plural.Of(bills, "bill")} this month {Were(bills)} to businesses with a GSTIN. They are listed bill by bill (B2B) with the buyer's GSTIN, " +
                         "and have an HSN summary of their own; they are not in the B2CS figures. The buyer claims the tax on them, " +
                         "so file them before the 11th, or it will not show in their GSTR-2B.");
        }

        if (businessNotes > 0)
        {
            warnings.Add($"{Plural.Of(businessNotes, "credit note")} {Were(businessNotes)} against bills to businesses, worth {Money(businessNotesValue)}. " +
                         "They are listed note by note (CDNR) with the buyer's GSTIN, not netted out of any other figure.");
        }

        var wrongGstins = b2b.Select(r => r.Gstin).Concat(cdnr.Select(r => r.Gstin))
            .Distinct(StringComparer.Ordinal)
            .Where(g => Gstin.Problem(g) is not null)
            .ToList();

        if (wrongGstins.Count > 0)
        {
            warnings.Add($"These GSTINs on bills do not check out: {string.Join(", ", wrongGstins)}. The portal will refuse them - " +
                         "find out the right one from the buyer before filing.");
        }

        var negative = rateWise.Where(r => r.Value.Taxable < 0).Select(r => $"{Rate(r.Key.Rate)}%").Distinct().ToList();

        if (negative.Count > 0)
        {
            warnings.Add($"Returns this month came to more than sales at {string.Join(", ", negative)}, so the net figure is below zero. " +
                         "The portal may not take a negative B2CS row; your accountant carries it against the month the goods were sold in.");
        }

        if (unknownPlace > 0)
        {
            warnings.Add($"{Plural.Of(unknownPlace, "line")} {Were(unknownPlace)} billed as inter-state to a customer whose state is no longer on record " +
                         "(the customer was forgotten, or no state was entered). They are in the figures under a blank place of supply - " +
                         "your accountant needs to say which state they went to.");
        }

        var shortCodes = hsn.Keys.Select(k => k.Hsn).Where(code => code.Trim().Length < 4).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList();

        if (shortCodes.Count > 0)
        {
            warnings.Add($"{(shortCodes.Count == 1 ? "An HSN code" : "HSN codes")} shorter than four digits: {string.Join(", ", shortCodes)}. A return needs at least four; " +
                         "correct them in the catalogue so next month's bills carry the full code.");
        }

        if (billsOfSupply > 0)
        {
            warnings.Add($"{Plural.Of(billsOfSupply, "bill")} this month {Were(billsOfSupply)} {(billsOfSupply == 1 ? "a bill" : "bills")} of supply under the composition scheme, worth " +
                         $"{Money(billsOfSupplyValue)}. They are not part of GSTR-1 and are not in the figures below - a composition " +
                         "dealer reports turnover on CMP-08 instead.");
        }

        if (nilIntra + nilInter > 0)
        {
            warnings.Add("Everything sold at 0% is listed as nil rated. Goods exempted by notification rather than rated at nil - " +
                         "fresh vegetables, fruit, flowers, fresh milk, eggs - belong in the exempted column; your accountant moves them.");
        }

        var noInput = purchases.Where(p => !p.ChargesGst).ToList();

        if (noInput.Count > 0)
        {
            warnings.Add($"{Plural.Of(noInput.Count, "purchase bill")} came from suppliers who do not charge GST, worth {Money(noInput.Sum(p => p.Total))}. " +
                         "There is no input tax on them to claim; they are in the purchase register for the record.");
        }

        if (billsOfSupply > 0 && taxInvoices == 0 && purchases.Count > 0)
        {
            warnings.Add("This lane issued only bills of supply this month. A composition dealer cannot claim input tax; " +
                         "the purchase figures are for the shop's own records.");
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
            CreditNotes = creditNotes,
            CreditNotesValue = creditNotesValue,
            LargeCreditNotes = largeReturns,
            TaxInvoices = taxInvoices,
            BillsOfSupply = billsOfSupply,
            BillsOfSupplyValue = billsOfSupplyValue,
            Warnings = warnings,
            B2b = b2b,
            B2bCreditNotes = cdnr,
            HsnB2b = [.. hsnB2b
                .OrderBy(h => h.Key.Hsn, StringComparer.Ordinal)
                .ThenBy(h => h.Key.Rate)
                .ThenBy(h => h.Key.Uqc, StringComparer.Ordinal)
                .Select(h => h.Value.ToRow(h.Key.Hsn, h.Key.Uqc, h.Key.Rate))],
            NilB2bIntraState = Rupees(nilB2bIntra),
            NilB2bInterState = Rupees(nilB2bInter),
            Purchases = purchases,
            Inputs = [.. purchases
                .Where(p => p.ChargesGst)
                .SelectMany(p => p.Rates)
                .GroupBy(r => r.Rate)
                .OrderBy(g => g.Key)
                .Select(g => new GstInputRow(g.Key, g.Sum(r => r.TaxableValue), g.Sum(r => r.Igst), g.Sum(r => r.Cgst), g.Sum(r => r.Sgst)))],
        };
    }

    private sealed record PurchaseFact(
        string? SupplierGstin,
        string SupplierName,
        string BillNo,
        DateOnly BillDate,
        decimal TaxableValue,
        decimal Igst,
        decimal Cgst,
        decimal Sgst,
        decimal Total,
        bool ChargesGst,
        List<GstInputRow> Rates);

    /// <summary>
    /// The month's purchase bills that stand, by the date printed on them - the date input tax is
    /// claimed against - with their tax at each rate.
    /// </summary>
    /// <remarks>
    /// Not filtered by lane. A delivery is the shop's, not a till's; it is in whichever lane's books
    /// the owner entered it on, once.
    /// </remarks>
    private static List<GstPurchaseRow> ReadPurchases(SqliteConnection connection, DateOnly month)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT p.id, s.gstin, s.name, p.bill_no, p.bill_date, p.charges_gst, {PaiseSql.Of("p.total")},
                   CAST(l.gst_rate AS REAL) AS rate,
                   {PaiseSql.Sum("l.taxable_value")}, {PaiseSql.Sum("l.igst_amount")},
                   {PaiseSql.Sum("l.cgst_amount")}, {PaiseSql.Sum("l.sgst_amount")}
            FROM purchases p
            JOIN suppliers s ON s.id = p.supplier_id
            JOIN purchase_lines l ON l.purchase_id = p.id
            WHERE p.voided_at IS NULL AND p.bill_date >= $from AND p.bill_date < $to
            GROUP BY p.id, rate
            ORDER BY p.bill_date, p.id, rate;
            """;
        command.Parameters.AddWithValue("$from", month.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", month.AddMonths(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        var bills = new List<(long Id, PurchaseFact Fact)>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var rate = new GstInputRow(
                Math.Round((decimal)reader.GetDouble(7), 2),
                PaiseSql.Rupees(reader.GetInt64(8)),
                PaiseSql.Rupees(reader.GetInt64(9)),
                PaiseSql.Rupees(reader.GetInt64(10)),
                PaiseSql.Rupees(reader.GetInt64(11)));

            if (bills.Count == 0 || bills[^1].Id != id)
            {
                bills.Add((id, new PurchaseFact(
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    DateOnly.ParseExact(reader.GetString(4), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    0m, 0m, 0m, 0m,
                    PaiseSql.Rupees(reader.GetInt64(6)),
                    reader.GetInt32(5) != 0,
                    [])));
            }

            bills[^1].Fact.Rates.Add(rate);
        }

        return [.. bills.Select(b => new GstPurchaseRow(
            b.Fact.SupplierGstin,
            b.Fact.SupplierName,
            b.Fact.BillNo,
            b.Fact.BillDate,
            b.Fact.Rates.Sum(r => r.TaxableValue),
            b.Fact.Rates.Sum(r => r.Igst),
            b.Fact.Rates.Sum(r => r.Cgst),
            b.Fact.Rates.Sum(r => r.Sgst),
            b.Fact.Total,
            b.Fact.ChargesGst) { Rates = b.Fact.Rates })];
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
        WHERE {InWindow} AND {Settled} AND {TaxInvoice} AND {ToConsumer}
          AND EXISTS (SELECT 1 FROM invoice_lines x WHERE x.invoice_id = i.id AND x.is_inter_state = 1)
          AND {PaiseSql.Of("i.grand_total")} + {PaiseSql.Of("i.round_off")} > $threshold
        """;

    /// <param name="who"><see cref="ToConsumer"/> or <see cref="ToBusiness"/>: which side of the return.</param>
    private static List<LineFact> ReadLines(SqliteConnection connection, string lane, DateTimeOffset from, DateTimeOffset to, string who)
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
            WHERE {InWindow} AND {Settled} AND {TaxInvoice} AND {who}
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

    /// <summary>
    /// A bill that went on the list of large inter-state bills in its own month: into another state,
    /// for more than the threshold. Asked of a credit note's bill, which may be from any month.
    /// </summary>
    private static string WasLarge(string invoice) => $"""
        (EXISTS (SELECT 1 FROM invoice_lines x WHERE x.invoice_id = {invoice}.id AND x.is_inter_state = 1)
         AND {PaiseSql.Of($"{invoice}.grand_total")} + {PaiseSql.Of($"{invoice}.round_off")} > $threshold)
        """;

    /// <summary>
    /// The month's credit notes against tax invoices: their lines, grouped as the sales are, and
    /// those against large inter-state bills rate by rate.
    /// </summary>
    /// <param name="who">Credit notes against bills to customers without a GSTIN, or to businesses.</param>
    private static (List<LineFact> Lines, List<GstCreditNoteRow> Large, int Count, decimal Value) ReadCreditNotes(
        SqliteConnection connection, string lane, DateTimeOffset from, DateTimeOffset to, string who)
    {
        const string window = "n.lane_id = $lane AND n.created_at >= $from AND n.created_at < $to AND n.tax_mode <> 'Composition'";

        var lines = new List<LineFact>();

        using (var command = Prepare(connection, lane, from, to, $"""
            SELECT l.hsn_snapshot,
                   l.unit_type,
                   CAST(l.gst_rate AS REAL) AS rate,
                   l.is_inter_state,
                   COALESCE(TRIM(c.state_code), '') AS pos,
                   l.name_snapshot,
                   {WasLarge("i")} AS big,
                   SUM(CAST(ROUND(CAST(l.quantity AS REAL) * 1000) AS INTEGER)),
                   COALESCE({PaiseSql.Sum("l.line_total")}, 0),
                   COALESCE({PaiseSql.Sum("l.taxable_value")}, 0),
                   COALESCE({PaiseSql.Sum("l.cgst_amount")}, 0),
                   COALESCE({PaiseSql.Sum("l.sgst_amount")}, 0),
                   COALESCE({PaiseSql.Sum("l.igst_amount")}, 0)
            FROM credit_note_lines l
            JOIN credit_notes n ON n.id = l.credit_note_id
            JOIN invoices i ON i.id = n.invoice_id
            LEFT JOIN customers c ON c.id = i.customer_id
            WHERE {window} AND {who}
            GROUP BY l.hsn_snapshot, l.unit_type, rate, l.is_inter_state, pos, l.name_snapshot, big;
            """))
        {
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
        }

        var large = new List<GstCreditNoteRow>();

        using (var command = Prepare(connection, lane, from, to, $"""
            SELECT n.credit_note_no,
                   substr(n.created_at, 1, 10),
                   n.invoice_no,
                   {PaiseSql.Of("n.total")} + {PaiseSql.Of("n.round_off")},
                   COALESCE(TRIM(c.state_code), ''),
                   CAST(l.gst_rate AS REAL) AS rate,
                   COALESCE({PaiseSql.Sum("l.taxable_value")}, 0),
                   COALESCE({PaiseSql.Sum("l.igst_amount")}, 0)
            FROM credit_notes n
            JOIN credit_note_lines l ON l.credit_note_id = n.id
            JOIN invoices i ON i.id = n.invoice_id
            LEFT JOIN customers c ON c.id = i.customer_id
            WHERE {window} AND {who} AND {ToConsumer} AND {WasLarge("i")} AND CAST(l.gst_rate AS REAL) > 0
            GROUP BY n.id, rate
            ORDER BY n.id, rate;
            """))
        {
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var pos = reader.GetString(4);

                large.Add(new GstCreditNoteRow(
                    reader.GetString(0),
                    DateOnly.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    reader.GetString(2),
                    Rupees(reader.GetInt64(3)),
                    pos.Length == 0 ? pos : pos.PadLeft(2, '0'),
                    Math.Round((decimal)reader.GetDouble(5), 2),
                    Rupees(reader.GetInt64(6)),
                    Rupees(reader.GetInt64(7))));
            }
        }

        using (var command = Prepare(connection, lane, from, to, $"""
            SELECT COUNT(*), COALESCE(SUM({PaiseSql.Of("n.total")} + {PaiseSql.Of("n.round_off")}), 0)
            FROM credit_notes n
            JOIN invoices i ON i.id = n.invoice_id
            WHERE {window} AND {who};
            """))
        {
            using var reader = command.ExecuteReader();
            reader.Read();

            return (lines, large, reader.GetInt32(0), Rupees(reader.GetInt64(1)));
        }
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

    /// <summary>
    /// The month's bills to businesses, rate by rate, with the GSTIN and name copied onto each when it
    /// was sold. Supplied into the state the GSTIN is registered in.
    /// </summary>
    private static List<GstB2bRow> ReadB2b(SqliteConnection connection, string lane, DateTimeOffset from, DateTimeOffset to)
    {
        using var command = Prepare(connection, lane, from, to, $"""
            SELECT i.buyer_gstin,
                   COALESCE(i.buyer_name, i.buyer_gstin),
                   i.invoice_no,
                   substr(i.created_at, 1, 10),
                   {PaiseSql.Of("i.grand_total")} + {PaiseSql.Of("i.round_off")},
                   CAST(l.gst_rate AS REAL) AS rate,
                   COALESCE({PaiseSql.Sum("l.taxable_value")}, 0),
                   COALESCE({PaiseSql.Sum("l.igst_amount")}, 0),
                   COALESCE({PaiseSql.Sum("l.cgst_amount")}, 0),
                   COALESCE({PaiseSql.Sum("l.sgst_amount")}, 0)
            FROM invoices i
            JOIN invoice_lines l ON l.invoice_id = i.id
            WHERE {InWindow} AND {Settled} AND {TaxInvoice} AND {ToBusiness} AND CAST(l.gst_rate AS REAL) > 0
            GROUP BY i.id, rate
            ORDER BY i.id, rate;
            """);

        var rows = new List<GstB2bRow>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var gstin = reader.GetString(0);

            rows.Add(new GstB2bRow(
                gstin,
                reader.GetString(1),
                reader.GetString(2),
                DateOnly.ParseExact(reader.GetString(3), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                Rupees(reader.GetInt64(4)),
                Place(gstin),
                Math.Round((decimal)reader.GetDouble(5), 2),
                Rupees(reader.GetInt64(6)),
                Rupees(reader.GetInt64(7)),
                Rupees(reader.GetInt64(8)),
                Rupees(reader.GetInt64(9))));
        }

        return rows;
    }

    /// <summary>The month's credit notes against bills to businesses, rate by rate, from whenever the bill was.</summary>
    private static List<GstB2bCreditNoteRow> ReadCdnr(SqliteConnection connection, string lane, DateTimeOffset from, DateTimeOffset to)
    {
        using var command = Prepare(connection, lane, from, to, $"""
            SELECT i.buyer_gstin,
                   COALESCE(i.buyer_name, i.buyer_gstin),
                   n.credit_note_no,
                   substr(n.created_at, 1, 10),
                   n.invoice_no,
                   {PaiseSql.Of("n.total")} + {PaiseSql.Of("n.round_off")},
                   CAST(l.gst_rate AS REAL) AS rate,
                   COALESCE({PaiseSql.Sum("l.taxable_value")}, 0),
                   COALESCE({PaiseSql.Sum("l.igst_amount")}, 0),
                   COALESCE({PaiseSql.Sum("l.cgst_amount")}, 0),
                   COALESCE({PaiseSql.Sum("l.sgst_amount")}, 0)
            FROM credit_notes n
            JOIN credit_note_lines l ON l.credit_note_id = n.id
            JOIN invoices i ON i.id = n.invoice_id
            WHERE n.lane_id = $lane AND n.created_at >= $from AND n.created_at < $to AND n.tax_mode <> 'Composition'
              AND {ToBusiness} AND CAST(l.gst_rate AS REAL) > 0
            GROUP BY n.id, rate
            ORDER BY n.id, rate;
            """);

        var rows = new List<GstB2bCreditNoteRow>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var gstin = reader.GetString(0);

            rows.Add(new GstB2bCreditNoteRow(
                gstin,
                reader.GetString(1),
                reader.GetString(2),
                DateOnly.ParseExact(reader.GetString(3), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                reader.GetString(4),
                Rupees(reader.GetInt64(5)),
                Place(gstin),
                Math.Round((decimal)reader.GetDouble(6), 2),
                Rupees(reader.GetInt64(7)),
                Rupees(reader.GetInt64(8)),
                Rupees(reader.GetInt64(9)),
                Rupees(reader.GetInt64(10))));
        }

        return rows;
    }

    /// <summary>The state a GSTIN is registered in, as the return writes a place of supply's code.</summary>
    private static string Place(string gstin) => gstin.Length >= 2 ? gstin[..2] : string.Empty;

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

    /// <summary>
    /// The bill number runs issued in the month, cancelled bills included - they were issued - and
    /// the credit note runs after them.
    /// </summary>
    private static List<GstDocumentSeries> ReadDocuments(
        SqliteConnection connection, string lane, DateTimeOffset from, DateTimeOffset to, List<string> warnings)
    {
        var series = ReadRuns(connection, lane, from, to, warnings, GstDocumentSeries.OutwardInvoices, $"""
            SELECT i.invoice_no, i.voided_at IS NOT NULL
            FROM invoices i
            WHERE {InWindow}
            ORDER BY i.id;
            """);

        // A credit note is never cancelled: a wrong one is answered by a bill, not by undoing it.
        series.AddRange(ReadRuns(connection, lane, from, to, warnings, GstDocumentSeries.CreditNotes, """
            SELECT n.credit_note_no, 0
            FROM credit_notes n
            WHERE n.lane_id = $lane AND n.created_at >= $from AND n.created_at < $to
            ORDER BY n.id;
            """));

        return series;
    }

    private static List<GstDocumentSeries> ReadRuns(
        SqliteConnection connection, string lane, DateTimeOffset from, DateTimeOffset to, List<string> warnings, string nature, string sql)
    {
        using var command = Prepare(connection, lane, from, to, sql);

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

            series.Add(new GstDocumentSeries(first.No, last.No, bills.Count, bills.Count(b => b.Cancelled), nature));

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
        /// Takes returned goods back off. The names are left alone: what an HSN code was used for
        /// is what was sold under it, not what came back.
        /// </summary>
        public void Subtract(LineFact line)
        {
            _quantity -= line.QuantityThousandths;
            _total -= line.Total;
            _taxable -= line.Taxable;
            _cgst -= line.Cgst;
            _sgst -= line.Sgst;
            _igst -= line.Igst;

            _byName.TryAdd(line.Name, 0);
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

    /// <summary>"1 bill was", "2 bills were".</summary>
    private static string Were(int count) => count == 1 ? "was" : "were";

    private static string Rate(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
