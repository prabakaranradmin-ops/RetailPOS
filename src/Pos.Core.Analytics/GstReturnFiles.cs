using System.Globalization;
using System.Text;
using Pos.Core.Domain;

namespace Pos.Core.Analytics;

/// <summary>
/// Writes a month's return figures as the files an accountant works from: one page to read, and
/// the tables as CSV in the layout of the GST offline tool's templates.
/// </summary>
/// <remarks>
/// <para>
/// The page is for checking; the CSVs are for not retyping. Each CSV is one table of GSTR-1 - B2CS,
/// B2CL, nil-rated, the HSN summary, the documents issued - with the column headings the offline
/// tool's own templates use, so the figures can be pasted or imported rather than keyed in from a
/// printout. The portal changes its templates from time to time; the page says so, and says to check
/// the headings against the version of the tool in use.
/// </para>
/// <para>
/// Numbers are written plainly - no thousands separators, no currency sign, a full stop for the
/// decimal - because a CSV is read by software, and "1,234.50" in a comma-separated file is two
/// columns.
/// </para>
/// </remarks>
public static class GstReturnFiles
{
    private static readonly CultureInfo India = CultureInfo.GetCultureInfo("en-IN");

    /// <summary>What a bill number run is called in the documents-issued table.</summary>
    public const string InvoicesForOutwardSupply = GstDocumentSeries.OutwardInvoices;

    /// <summary>
    /// Writes the page to <paramref name="pagePath"/> and the CSVs beside it, named after it.
    /// </summary>
    /// <returns>Every file written, the page first.</returns>
    public static IReadOnlyList<string> Write(GstReturnData data, string pagePath, string shopName, string? gstin)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(pagePath);

        var folder = Path.GetDirectoryName(Path.GetFullPath(pagePath))!;
        var stem = Path.GetFileNameWithoutExtension(pagePath);

        Directory.CreateDirectory(folder);

        // With a byte-order mark: Excel opens a CSV without one in the machine's ANSI code page, and
        // an item description in Tamil comes out as mojibake in the accountant's spreadsheet.
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

        var tables = new List<(string Suffix, string Text)>
        {
            ("b2cs", B2cs(data)),
            ("exemp", Exempt(data)),
            ("hsn(b2c)", HsnSummary(data)),
            ("docs", Documents(data)),
        };

        // Only when there is one. An empty B2CL file invites the question of what was left out of it.
        if (data.LargeInterState.Count > 0)
            tables.Insert(1, ("b2cl", B2cl(data)));

        if (data.LargeCreditNotes.Count > 0)
            tables.Insert(tables.FindIndex(t => t.Suffix == "exemp"), ("cdnur", Cdnur(data)));

        // Bills to businesses come first in the return, and their HSN summary sits beside the other.
        if (data.B2b.Count > 0)
            tables.Insert(0, ("b2b", B2b(data)));

        if (data.B2bCreditNotes.Count > 0)
            tables.Insert(tables.FindIndex(t => t.Suffix == "exemp"), ("cdnr", Cdnr(data)));

        if (data.HsnB2b.Count > 0)
            tables.Insert(tables.FindIndex(t => t.Suffix == "hsn(b2c)"), ("hsn(b2b)", HsnSummary(data.HsnB2b)));

        if (data.Purchases.Count > 0)
            tables.Add(("purchases", PurchaseRegister(data)));

        var written = new List<string> { pagePath };
        var csvNames = tables.Select(t => $"{stem}-{t.Suffix}.csv").ToList();

        File.WriteAllText(pagePath, RenderPage(data, shopName, gstin, csvNames), utf8);

        foreach (var (suffix, text) in tables)
        {
            var path = Path.Combine(folder, $"{stem}-{suffix}.csv");
            File.WriteAllText(path, text, utf8);
            written.Add(path);
        }

        return written;
    }

    /// <summary>A file name for a month's return: "gst-T1-2026-09".</summary>
    public static string Stem(GstReturnData data) =>
        $"gst-{data.LaneId}-{data.Month.ToString("yyyy-MM", CultureInfo.InvariantCulture)}";

    // ---- The CSVs ------------------------------------------------------------------------------

    /// <summary>Bills to businesses, one row per rate, with the buyer's GSTIN: what the buyer claims against.</summary>
    public static string B2b(GstReturnData data)
    {
        var csv = new StringBuilder();
        csv.AppendLine("GSTIN/UIN of Recipient,Receiver Name,Invoice Number,Invoice date,Invoice Value,Place Of Supply,Reverse Charge,Applicable % of Tax Rate,Invoice Type,E-Commerce GSTIN,Rate,Taxable Value,Cess Amount");

        foreach (var row in data.B2b)
        {
            csv.AppendLine(Row(
                row.Gstin,
                row.Name,
                row.InvoiceNo,
                row.Date.ToString("dd-MMM-yy", CultureInfo.InvariantCulture),
                Amount(row.InvoiceValue),
                Place(row.PlaceOfSupply),
                "N",
                "",
                "Regular B2B",
                "",
                Rate(row.Rate),
                Amount(row.TaxableValue),
                ""));
        }

        return csv.ToString();
    }

    /// <summary>Credit notes against bills to businesses, one row per rate, with the buyer's GSTIN.</summary>
    public static string Cdnr(GstReturnData data)
    {
        var csv = new StringBuilder();
        csv.AppendLine("GSTIN/UIN of Recipient,Receiver Name,Note Number,Note Date,Note Type,Place Of Supply,Reverse Charge,Note Supply Type,Note Value,Applicable % of Tax Rate,Rate,Taxable Value,Cess Amount");

        foreach (var row in data.B2bCreditNotes)
        {
            csv.AppendLine(Row(
                row.Gstin,
                row.Name,
                row.NoteNo,
                row.NoteDate.ToString("dd-MMM-yy", CultureInfo.InvariantCulture),
                "C",
                Place(row.PlaceOfSupply),
                "N",
                "Regular B2B",
                Amount(row.NoteValue),
                "",
                Rate(row.Rate),
                Amount(row.TaxableValue),
                ""));
        }

        return csv.ToString();
    }

    public static string B2cs(GstReturnData data)
    {
        var csv = new StringBuilder();
        csv.AppendLine("Type,Place Of Supply,Rate,Applicable % of Tax Rate,Taxable Value,Cess Amount,E-Commerce GSTIN");

        foreach (var row in data.RateWise)
            csv.AppendLine(Row("OE", Place(row.PlaceOfSupply), Rate(row.Rate), "", Amount(row.TaxableValue), "", ""));

        return csv.ToString();
    }

    public static string B2cl(GstReturnData data)
    {
        var csv = new StringBuilder();
        csv.AppendLine("Invoice Number,Invoice date,Invoice Value,Place Of Supply,Applicable % of Tax Rate,Rate,Taxable Value,Cess Amount,E-Commerce GSTIN");

        foreach (var row in data.LargeInterState)
        {
            csv.AppendLine(Row(
                row.InvoiceNo,
                row.Date.ToString("dd-MMM-yy", CultureInfo.InvariantCulture),
                Amount(row.InvoiceValue),
                Place(row.PlaceOfSupply),
                "",
                Rate(row.Rate),
                Amount(row.TaxableValue),
                "",
                ""));
        }

        return csv.ToString();
    }

    /// <summary>
    /// Credit notes against large inter-state bills to customers without a GSTIN, one row per rate.
    /// Every other return is already netted out of the B2CS figures.
    /// </summary>
    public static string Cdnur(GstReturnData data)
    {
        var csv = new StringBuilder();
        csv.AppendLine("UR Type,Note Number,Note Date,Note Type,Place Of Supply,Note Value,Applicable % of Tax Rate,Rate,Taxable Value,Cess Amount");

        foreach (var row in data.LargeCreditNotes)
        {
            csv.AppendLine(Row(
                "B2CL",
                row.NoteNo,
                row.NoteDate.ToString("dd-MMM-yy", CultureInfo.InvariantCulture),
                "C",
                Place(row.PlaceOfSupply),
                Amount(row.NoteValue),
                "",
                Rate(row.Rate),
                Amount(row.TaxableValue),
                ""));
        }

        return csv.ToString();
    }

    /// <summary>
    /// Nil-rated supplies, to businesses with a GSTIN and to everybody else, within the state and into
    /// others: the table's four rows, every one written even when it is zero.
    /// </summary>
    public static string Exempt(GstReturnData data)
    {
        var csv = new StringBuilder();
        csv.AppendLine("Description,Nil Rated Supplies,Exempted(other than nil rated/non GST supply),Non-GST supplies");
        csv.AppendLine(Row("Inter-State supplies to registered persons", Amount(data.NilB2bInterState), Amount(0m), Amount(0m)));
        csv.AppendLine(Row("Intra-State supplies to registered persons", Amount(data.NilB2bIntraState), Amount(0m), Amount(0m)));
        csv.AppendLine(Row("Inter-State supplies to unregistered persons", Amount(data.NilInterState), Amount(0m), Amount(0m)));
        csv.AppendLine(Row("Intra-State supplies to unregistered persons", Amount(data.NilIntraState), Amount(0m), Amount(0m)));
        return csv.ToString();
    }

    public static string HsnSummary(GstReturnData data) => HsnSummary(data.Hsn);

    /// <summary>An HSN summary: of supplies to customers without a GSTIN, or of those to businesses.</summary>
    public static string HsnSummary(IReadOnlyList<GstHsnRow> rows)
    {
        var csv = new StringBuilder();
        csv.AppendLine("HSN,Description,UQC,Total Quantity,Total Value,Rate,Taxable Value,Integrated Tax Amount,Central Tax Amount,State/UT Tax Amount,Cess Amount");

        foreach (var row in rows)
        {
            csv.AppendLine(Row(
                row.Hsn,
                row.Description,
                row.Uqc,
                row.Quantity.ToString("0.###", CultureInfo.InvariantCulture),
                Amount(row.TotalValue),
                Rate(row.Rate),
                Amount(row.TaxableValue),
                Amount(row.Igst),
                Amount(row.Cgst),
                Amount(row.Sgst),
                ""));
        }

        return csv.ToString();
    }

    public static string Documents(GstReturnData data)
    {
        var csv = new StringBuilder();
        csv.AppendLine("Nature of Document,Sr. No. From,Sr. No. To,Total Number,Cancelled");

        foreach (var run in data.Documents)
        {
            csv.AppendLine(Row(
                run.Nature,
                run.From,
                run.To,
                run.Total.ToString(CultureInfo.InvariantCulture),
                run.Cancelled.ToString(CultureInfo.InvariantCulture)));
        }

        return csv.ToString();
    }

    /// <summary>
    /// Every purchase bill dated in the month, one row each: what the accountant matches against
    /// the suppliers' own filings in GSTR-2B before claiming the input tax in GSTR-3B.
    /// </summary>
    public static string PurchaseRegister(GstReturnData data)
    {
        var csv = new StringBuilder();
        csv.AppendLine("Supplier GSTIN,Supplier,Bill No,Bill Date,Taxable Value,Integrated Tax,Central Tax,State/UT Tax,Bill Total,Input Tax Claimable");

        foreach (var row in data.Purchases)
        {
            csv.AppendLine(Row(
                row.SupplierGstin ?? "",
                row.SupplierName,
                row.BillNo,
                row.BillDate.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture),
                Amount(row.TaxableValue),
                Amount(row.Igst),
                Amount(row.Cgst),
                Amount(row.Sgst),
                Amount(row.Total),
                row.ChargesGst ? "Yes" : "No"));
        }

        return csv.ToString();
    }

    private static string Row(params string[] fields) => string.Join(',', fields.Select(Field));

    /// <summary>Quoted when it has to be: an item named "Rice, Ponni" must stay one field.</summary>
    private static string Field(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;

    private static string Amount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Rate(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>"33-Tamil Nadu". A blank place of supply stays blank, for the accountant to fill.</summary>
    private static string Place(string code) => code.Length == 0 ? "" : GstStates.Label(code);

    // ---- The page ------------------------------------------------------------------------------

    public static string RenderPage(GstReturnData d, string shopName, string? gstin, IReadOnlyList<string>? csvNames = null)
    {
        ArgumentNullException.ThrowIfNull(d);

        var monthName = d.Month.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        var p = new StringBuilder(32 * 1024);

        p.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
        p.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        p.Append($"<title>{Escape(shopName)} — GST return, {monthName}</title>\n");
        p.Append(DashboardPage.Styles);
        p.Append("<style>.warn{border-left:4px solid var(--bad)} .warn li{margin:0 0 8px} .files code{font-family:ui-monospace,Consolas,monospace}</style>\n");
        p.Append("</head>\n<body>\n<div class=\"wrap\">\n");

        p.Append("<header class=\"top\">");
        p.Append($"<p class=\"eyebrow\">GST return &middot; {monthName} &middot; lane {Escape(d.LaneId)}</p>");
        p.Append($"<h1>{Escape(shopName)}</h1>");
        p.Append("<p class=\"meta\">");
        if (!string.IsNullOrWhiteSpace(gstin))
            p.Append($"GSTIN {Escape(gstin)} &middot; ");
        p.Append($"Supplied from {Escape(GstStates.Label(d.OutletStateCode))} &middot; generated {d.GeneratedAt.ToString("dd MMM yyyy, HH:mm", India)}</p>");
        p.Append("</header>");

        p.Append("<h2>Before filing</h2>");
        p.Append("<div class=\"panel warn\"><ul>");
        foreach (var warning in d.Warnings)
            p.Append($"<li>{Escape(warning)}</li>");
        p.Append("<li>These are this lane's bills only. A shop with more than one till files one return: add each lane's figures together.</li>");
        p.Append("<li>Figures are read from the bills as issued. Cancelled bills are left out of every figure and counted in the documents issued. Goods returned on credit notes this month are taken off.</li>");
        p.Append("<li>The CSV files use the column headings of the GST offline tool's templates. The portal changes them from time to time — check them against the version of the tool you use.</li>");
        p.Append("</ul></div>");

        p.Append("<h2>This month</h2>");
        p.Append("<div class=\"kpis\">");
        Card(p, "Taxable value", Money(d.TaxableValue), $"{Plural.Of(d.TaxInvoices, "tax invoice")}");
        Card(p, "CGST", Money(d.Cgst), "central tax");
        Card(p, "SGST", Money(d.Sgst), "state tax");
        if (d.Igst != 0m)
            Card(p, "IGST", Money(d.Igst), "inter-state");
        Card(p, "Nil rated", Money(d.NilRated), "sold at 0%");
        if (d.InputTax != 0m)
            Card(p, "Input tax", Money(d.InputTax), "on purchase bills");
        if (d.CreditNotes > 0)
            Card(p, "Returns", Money(d.CreditNotesValue), $"{Plural.Of(d.CreditNotes, "credit note")}, netted out");
        p.Append("</div>");

        if (d.B2b.Count > 0)
        {
            p.Append("<h2>Bills to businesses <span class=\"meta\">B2B</span></h2>");
            p.Append($"<p class=\"lede\">{Plural.Of(d.B2bInvoices, "bill")} to customers with a GSTIN, listed bill by bill and rate by rate. The buyer claims the tax on these, so they need filing on time.</p>");
            p.Append("<div class=\"panel\"><div class=\"scroller\"><table><thead><tr><th>Buyer GSTIN</th><th>Buyer</th><th>Bill</th><th>Date</th><th class=\"n\">Bill value</th><th>Place of supply</th><th class=\"n\">Rate</th><th class=\"n\">Taxable value</th><th class=\"n\">CGST</th><th class=\"n\">SGST</th><th class=\"n\">IGST</th></tr></thead><tbody>");
            foreach (var row in d.B2b)
            {
                p.Append($"<tr><td class=\"mono\">{Escape(row.Gstin)}</td><td>{Escape(row.Name)}</td><td class=\"mono\">{Escape(row.InvoiceNo)}</td>");
                p.Append($"<td>{row.Date.ToString("dd MMM yyyy", India)}</td><td class=\"n\">{Money(row.InvoiceValue)}</td><td>{Escape(GstStates.Label(row.PlaceOfSupply))}</td>");
                p.Append($"<td class=\"n\">{Rate(row.Rate)}%</td><td class=\"n\">{Money(row.TaxableValue)}</td><td class=\"n\">{Money(row.Cgst)}</td><td class=\"n\">{Money(row.Sgst)}</td><td class=\"n\">{Money(row.Igst)}</td></tr>");
            }
            p.Append($"<tr class=\"total\"><td>Total</td><td></td><td></td><td></td><td></td><td></td><td></td><td class=\"n\">{Money(d.B2b.Sum(r => r.TaxableValue))}</td>");
            p.Append($"<td class=\"n\">{Money(d.B2b.Sum(r => r.Cgst))}</td><td class=\"n\">{Money(d.B2b.Sum(r => r.Sgst))}</td><td class=\"n\">{Money(d.B2b.Sum(r => r.Igst))}</td></tr>");
            p.Append("</tbody></table></div></div>");
        }

        if (d.B2bCreditNotes.Count > 0)
        {
            p.Append("<h2>Returns from businesses <span class=\"meta\">CDNR</span></h2>");
            p.Append("<p class=\"lede\">Credit notes against bills to customers with a GSTIN, note by note. They are not taken off any other figure.</p>");
            p.Append("<div class=\"panel\"><div class=\"scroller\"><table><thead><tr><th>Buyer GSTIN</th><th>Buyer</th><th>Credit note</th><th>Date</th><th>Against bill</th><th class=\"n\">Note value</th><th class=\"n\">Rate</th><th class=\"n\">Taxable value</th><th class=\"n\">Tax</th></tr></thead><tbody>");
            foreach (var row in d.B2bCreditNotes)
            {
                p.Append($"<tr><td class=\"mono\">{Escape(row.Gstin)}</td><td>{Escape(row.Name)}</td><td class=\"mono\">{Escape(row.NoteNo)}</td><td>{row.NoteDate.ToString("dd MMM yyyy", India)}</td>");
                p.Append($"<td class=\"mono\">{Escape(row.InvoiceNo)}</td><td class=\"n\">{Money(row.NoteValue)}</td><td class=\"n\">{Rate(row.Rate)}%</td>");
                p.Append($"<td class=\"n\">{Money(row.TaxableValue)}</td><td class=\"n\">{Money(row.Cgst + row.Sgst + row.Igst)}</td></tr>");
            }
            p.Append("</tbody></table></div></div>");
        }

        p.Append("<h2>Sales to customers without a GSTIN <span class=\"meta\">B2CS</span></h2>");
        p.Append("<p class=\"lede\">By place of supply and rate. Everything sold over the counter is here, apart from goods at 0% and any large inter-state bill listed after it.</p>");
        p.Append("<div class=\"panel\"><div class=\"scroller\">");

        if (d.RateWise.Count == 0)
        {
            p.Append("<p class=\"empty\">Nothing taxable was sold this month.</p>");
        }
        else
        {
            p.Append("<table><thead><tr><th>Place of supply</th><th class=\"n\">Rate</th><th class=\"n\">Taxable value</th><th class=\"n\">CGST</th><th class=\"n\">SGST</th><th class=\"n\">IGST</th><th class=\"n\">Tax</th></tr></thead><tbody>");
            foreach (var row in d.RateWise)
            {
                p.Append($"<tr><td>{Escape(row.PlaceOfSupply.Length == 0 ? "(unknown — see above)" : GstStates.Label(row.PlaceOfSupply))}</td>");
                p.Append($"<td class=\"n\">{Rate(row.Rate)}%</td><td class=\"n\">{Money(row.TaxableValue)}</td>");
                p.Append($"<td class=\"n\">{Money(row.Cgst)}</td><td class=\"n\">{Money(row.Sgst)}</td><td class=\"n\">{Money(row.Igst)}</td><td class=\"n strong\">{Money(row.Tax)}</td></tr>");
            }

            p.Append($"<tr class=\"total\"><td>Total</td><td></td><td class=\"n\">{Money(d.RateWise.Sum(r => r.TaxableValue))}</td>");
            p.Append($"<td class=\"n\">{Money(d.RateWise.Sum(r => r.Cgst))}</td><td class=\"n\">{Money(d.RateWise.Sum(r => r.Sgst))}</td>");
            p.Append($"<td class=\"n\">{Money(d.RateWise.Sum(r => r.Igst))}</td><td class=\"n\">{Money(d.RateWise.Sum(r => r.Tax))}</td></tr>");
            p.Append("</tbody></table>");
        }

        p.Append("</div></div>");

        if (d.LargeInterState.Count > 0)
        {
            p.Append("<h2>Large bills into another state <span class=\"meta\">B2CL</span></h2>");
            p.Append($"<p class=\"lede\">Bills over {Money(GstReturnQuery.LargeInterStateThreshold)} to a customer without a GSTIN in another state are listed one by one.</p>");
            p.Append("<div class=\"panel\"><div class=\"scroller\"><table><thead><tr><th>Bill</th><th>Date</th><th class=\"n\">Bill value</th><th>Place of supply</th><th class=\"n\">Rate</th><th class=\"n\">Taxable value</th><th class=\"n\">IGST</th></tr></thead><tbody>");
            foreach (var row in d.LargeInterState)
            {
                p.Append($"<tr><td class=\"mono\">{Escape(row.InvoiceNo)}</td><td>{row.Date.ToString("dd MMM yyyy", India)}</td><td class=\"n\">{Money(row.InvoiceValue)}</td>");
                p.Append($"<td>{Escape(row.PlaceOfSupply.Length == 0 ? "(unknown)" : GstStates.Label(row.PlaceOfSupply))}</td><td class=\"n\">{Rate(row.Rate)}%</td>");
                p.Append($"<td class=\"n\">{Money(row.TaxableValue)}</td><td class=\"n\">{Money(row.Igst)}</td></tr>");
            }
            p.Append("</tbody></table></div></div>");
        }

        if (d.LargeCreditNotes.Count > 0)
        {
            p.Append("<h2>Returns against large inter-state bills <span class=\"meta\">CDNUR</span></h2>");
            p.Append("<p class=\"lede\">Credit notes against bills listed one by one above are listed one by one too. Every other return is already taken off the B2CS figures.</p>");
            p.Append("<div class=\"panel\"><div class=\"scroller\"><table><thead><tr><th>Credit note</th><th>Date</th><th>Against bill</th><th class=\"n\">Note value</th><th>Place of supply</th><th class=\"n\">Rate</th><th class=\"n\">Taxable value</th><th class=\"n\">IGST</th></tr></thead><tbody>");
            foreach (var row in d.LargeCreditNotes)
            {
                p.Append($"<tr><td class=\"mono\">{Escape(row.NoteNo)}</td><td>{row.NoteDate.ToString("dd MMM yyyy", India)}</td><td class=\"mono\">{Escape(row.InvoiceNo)}</td>");
                p.Append($"<td class=\"n\">{Money(row.NoteValue)}</td><td>{Escape(row.PlaceOfSupply.Length == 0 ? "(unknown)" : GstStates.Label(row.PlaceOfSupply))}</td>");
                p.Append($"<td class=\"n\">{Rate(row.Rate)}%</td><td class=\"n\">{Money(row.TaxableValue)}</td><td class=\"n\">{Money(row.Igst)}</td></tr>");
            }
            p.Append("</tbody></table></div></div>");
        }

        p.Append("<h2>Nil rated <span class=\"meta\">exempt, nil and non-GST</span></h2>");
        p.Append("<div class=\"panel\"><table class=\"plain\"><tbody>");
        p.Append($"<tr><td>Within {Escape(GstStates.Name(d.OutletStateCode) ?? "the state")}</td><td class=\"n\">{Money(d.NilIntraState)}</td></tr>");
        p.Append($"<tr><td>Into other states</td><td class=\"n\">{Money(d.NilInterState)}</td></tr>");

        if (d.NilB2bIntraState != 0m || d.NilB2bInterState != 0m)
        {
            p.Append($"<tr><td>To businesses within the state</td><td class=\"n\">{Money(d.NilB2bIntraState)}</td></tr>");
            p.Append($"<tr><td>To businesses in other states</td><td class=\"n\">{Money(d.NilB2bInterState)}</td></tr>");
        }

        p.Append("</tbody></table></div>");

        if (d.HsnB2b.Count > 0)
        {
            p.Append("<h2>HSN summary of bills to businesses <span class=\"meta\">HSN B2B</span></h2>");
            p.Append("<p class=\"lede\">The return keeps supplies to customers with a GSTIN apart from the rest, code by code.</p>");
            p.Append("<div class=\"panel\"><div class=\"scroller\"><table><thead><tr><th>HSN</th><th>Description</th><th>UQC</th><th class=\"n\">Quantity</th><th class=\"n\">Rate</th><th class=\"n\">Total value</th><th class=\"n\">Taxable value</th><th class=\"n\">CGST</th><th class=\"n\">SGST</th><th class=\"n\">IGST</th></tr></thead><tbody>");
            foreach (var row in d.HsnB2b)
            {
                p.Append($"<tr><td class=\"mono\">{Escape(row.Hsn)}</td><td>{Escape(row.Description)}</td><td class=\"mono\">{Escape(row.Uqc)}</td>");
                p.Append($"<td class=\"n\">{row.Quantity.ToString("#,##0.###", India)}</td><td class=\"n\">{Rate(row.Rate)}%</td><td class=\"n\">{Money(row.TotalValue)}</td>");
                p.Append($"<td class=\"n\">{Money(row.TaxableValue)}</td><td class=\"n\">{Money(row.Cgst)}</td><td class=\"n\">{Money(row.Sgst)}</td><td class=\"n\">{Money(row.Igst)}</td></tr>");
            }
            p.Append("</tbody></table></div></div>");
        }

        p.Append("<h2>HSN summary</h2>");
        p.Append("<p class=\"lede\">Every code sold, in the unit it was sold in. A traditional unit is reported as it was sold - a padi as a padi, under OTH - never converted to kilograms nobody weighed.</p>");
        p.Append("<div class=\"panel\"><div class=\"scroller\">");

        if (d.Hsn.Count == 0)
        {
            p.Append("<p class=\"empty\">Nothing was sold this month.</p>");
        }
        else
        {
            p.Append("<table><thead><tr><th>HSN</th><th>Description</th><th>UQC</th><th class=\"n\">Quantity</th><th class=\"n\">Rate</th><th class=\"n\">Total value</th><th class=\"n\">Taxable value</th><th class=\"n\">CGST</th><th class=\"n\">SGST</th><th class=\"n\">IGST</th></tr></thead><tbody>");
            foreach (var row in d.Hsn)
            {
                p.Append($"<tr><td class=\"mono\">{Escape(row.Hsn)}</td><td>{Escape(row.Description)}</td><td class=\"mono\">{Escape(row.Uqc)}</td>");
                p.Append($"<td class=\"n\">{row.Quantity.ToString("#,##0.###", India)}</td><td class=\"n\">{Rate(row.Rate)}%</td><td class=\"n\">{Money(row.TotalValue)}</td>");
                p.Append($"<td class=\"n\">{Money(row.TaxableValue)}</td><td class=\"n\">{Money(row.Cgst)}</td><td class=\"n\">{Money(row.Sgst)}</td><td class=\"n\">{Money(row.Igst)}</td></tr>");
            }

            p.Append($"<tr class=\"total\"><td>Total</td><td></td><td></td><td></td><td></td><td class=\"n\">{Money(d.Hsn.Sum(r => r.TotalValue))}</td>");
            p.Append($"<td class=\"n\">{Money(d.Hsn.Sum(r => r.TaxableValue))}</td><td class=\"n\">{Money(d.Hsn.Sum(r => r.Cgst))}</td>");
            p.Append($"<td class=\"n\">{Money(d.Hsn.Sum(r => r.Sgst))}</td><td class=\"n\">{Money(d.Hsn.Sum(r => r.Igst))}</td></tr>");
            p.Append("</tbody></table>");
        }

        p.Append("</div></div>");

        p.Append("<h2>Documents issued</h2>");
        p.Append("<div class=\"panel\"><div class=\"scroller\">");

        if (d.Documents.Count == 0)
        {
            p.Append("<p class=\"empty\">No bills were issued this month.</p>");
        }
        else
        {
            p.Append("<table><thead><tr><th>Nature of document</th><th>From</th><th>To</th><th class=\"n\">Total</th><th class=\"n\">Cancelled</th></tr></thead><tbody>");
            foreach (var run in d.Documents)
            {
                p.Append($"<tr><td>{Escape(run.Nature)}</td><td class=\"mono\">{Escape(run.From)}</td><td class=\"mono\">{Escape(run.To)}</td>");
                p.Append($"<td class=\"n\">{run.Total:N0}</td><td class=\"n\">{run.Cancelled:N0}</td></tr>");
            }
            p.Append("</tbody></table>");
        }

        p.Append("</div></div>");

        p.Append("<h2>Purchases and input tax <span class=\"meta\">for GSTR-3B</span></h2>");
        p.Append("<p class=\"lede\">Purchase bills dated in the month, as entered on the owner's screen. Input tax is claimed only once the supplier's own filing shows it in GSTR-2B - match these first.</p>");
        p.Append("<div class=\"panel\"><div class=\"scroller\">");

        if (d.Purchases.Count == 0)
        {
            p.Append("<p class=\"empty\">No purchase bills dated in this month were entered.</p>");
        }
        else
        {
            p.Append("<h3>Input tax by rate</h3><table><thead><tr><th class=\"n\">Rate</th><th class=\"n\">Taxable value</th><th class=\"n\">IGST</th><th class=\"n\">CGST</th><th class=\"n\">SGST</th><th class=\"n\">Tax</th></tr></thead><tbody>");
            foreach (var row in d.Inputs)
            {
                p.Append($"<tr><td class=\"n\">{Rate(row.Rate)}%</td><td class=\"n\">{Money(row.TaxableValue)}</td><td class=\"n\">{Money(row.Igst)}</td>");
                p.Append($"<td class=\"n\">{Money(row.Cgst)}</td><td class=\"n\">{Money(row.Sgst)}</td><td class=\"n strong\">{Money(row.Tax)}</td></tr>");
            }
            p.Append($"<tr class=\"total\"><td>Total</td><td class=\"n\">{Money(d.Inputs.Sum(i => i.TaxableValue))}</td><td class=\"n\">{Money(d.Inputs.Sum(i => i.Igst))}</td>");
            p.Append($"<td class=\"n\">{Money(d.Inputs.Sum(i => i.Cgst))}</td><td class=\"n\">{Money(d.Inputs.Sum(i => i.Sgst))}</td><td class=\"n\">{Money(d.InputTax)}</td></tr>");
            p.Append("</tbody></table>");

            p.Append("<h3 style=\"margin-top:22px\">Purchase register</h3><table><thead><tr><th>Supplier</th><th>GSTIN</th><th>Bill</th><th>Date</th><th class=\"n\">Taxable</th><th class=\"n\">Tax</th><th class=\"n\">Total</th></tr></thead><tbody>");
            foreach (var row in d.Purchases)
            {
                p.Append($"<tr><td>{Escape(row.SupplierName)}</td><td class=\"mono\">{Escape(row.SupplierGstin ?? "no GSTIN")}</td><td class=\"mono\">{Escape(row.BillNo)}</td>");
                p.Append($"<td>{row.BillDate.ToString("dd MMM yyyy", India)}</td><td class=\"n\">{Money(row.TaxableValue)}</td>");
                p.Append($"<td class=\"n\">{(row.ChargesGst ? Money(row.Igst + row.Cgst + row.Sgst) : "no GST")}</td><td class=\"n\">{Money(row.Total)}</td></tr>");
            }
            p.Append("</tbody></table>");
        }

        p.Append("</div></div>");

        if (csvNames is { Count: > 0 })
        {
            p.Append("<h2>The files</h2><div class=\"panel files\"><p class=\"lede\">Saved beside this page:</p><ul>");
            foreach (var name in csvNames)
                p.Append($"<li><code>{Escape(name)}</code></li>");
            p.Append("</ul></div>");
        }

        p.Append("</div>\n</body>\n</html>");
        return p.ToString();
    }

    private static void Card(StringBuilder p, string label, string value, string note) =>
        p.Append($"<div class=\"kpi\"><div class=\"l\">{Escape(label)}</div><div class=\"v\">{value}</div><div class=\"n\">{Escape(note)}</div></div>");

    private static string Money(decimal value) => "Rs " + value.ToString("N2", India);

    private static string Escape(string? text) => string.IsNullOrEmpty(text)
        ? string.Empty
        : text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
