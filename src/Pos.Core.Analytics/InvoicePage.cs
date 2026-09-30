using System.Globalization;
using System.Text;
using Pos.Core.Domain;
using Pos.Core.Domain.Printing;
using Pos.Core.Tax;

namespace Pos.Core.Analytics;

/// <summary>
/// A settled bill as a full A4 tax invoice - or bill of supply - to print on any printer, or send as a
/// file: every line with its HSN, rate, taxable value and tax, a summary by HSN and rate, and the
/// total in words.
/// </summary>
/// <remarks>
/// The same bill as the counter printed, read from the same stored lines, so the figures cannot
/// differ from it; only the layout does. White and plain: it is for paper.
/// </remarks>
public static class InvoicePage
{
    private static readonly CultureInfo India = CultureInfo.GetCultureInfo("en-IN");

    /// <param name="outletStateCode">The shop's GST state, for the place of supply on a sale within it.</param>
    /// <param name="isCopy">Marks it as a copy, as a reprint is marked.</param>
    public static string Render(SettledInvoice invoice, StoreProfile store, string? outletStateCode = null, bool isCopy = false)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(store);

        var sale = invoice.Sale;
        var totals = sale.Totals;
        var composition = sale.TaxMode == TaxMode.Composition;
        var interState = sale.Lines.Any(l => l.IsInterState);
        var p = new StringBuilder(16 * 1024);

        p.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
        p.Append($"<title>{Escape(store.Name)} — {Escape(invoice.InvoiceNo)}</title>\n");
        p.Append("""
            <style>
              @page { size: A4; margin: 12mm; }
              * { box-sizing: border-box; }
              body { margin: 0; font-family: "Segoe UI", "Nirmala UI", Arial, sans-serif; color: #000; background: #fff; font-size: 9.5pt; }
              .invoice { max-width: 186mm; margin: 0 auto; }
              header { display: flex; justify-content: space-between; align-items: flex-start; border-bottom: 2px solid #000; padding-bottom: 3mm; }
              header h1 { font-size: 16pt; margin: 0; }
              header p { margin: .8mm 0 0; }
              .kind { text-align: right; }
              .kind h2 { margin: 0; font-size: 13pt; letter-spacing: .06em; }
              .copy { font-weight: 700; color: #555; }
              .parties { display: grid; grid-template-columns: 1fr 1fr; gap: 4mm; margin: 4mm 0; }
              .parties div { border: 1px solid #bbb; padding: 2mm 3mm; }
              .parties h3 { margin: 0 0 1mm; font-size: 8pt; text-transform: uppercase; color: #555; letter-spacing: .05em; }
              table { width: 100%; border-collapse: collapse; }
              th, td { border: 1px solid #bbb; padding: 1.2mm 1.6mm; vertical-align: top; }
              th { background: #eee; font-size: 8pt; text-transform: uppercase; }
              .num { text-align: right; font-variant-numeric: tabular-nums; white-space: nowrap; }
              .hsn { color: #555; font-size: 8pt; }
              .totals { width: 80mm; margin: 3mm 0 0 auto; }
              .totals td { border: none; padding: .8mm 1.6mm; }
              .totals .grand td { font-size: 12pt; font-weight: 700; border-top: 2px solid #000; }
              .words { margin-top: 2mm; font-weight: 600; }
              h4 { margin: 5mm 0 1.5mm; font-size: 9pt; }
              .foot { margin-top: 6mm; display: flex; justify-content: space-between; align-items: flex-end; }
              .sign { text-align: center; border-top: 1px solid #000; padding-top: 1mm; width: 55mm; }
              .note { color: #555; font-size: 8.5pt; }
            </style>
            </head>
            <body>
            <div class="invoice">
            """);

        // ---- The shop, and what this is ------------------------------------------------------------
        p.Append("<header><div>");
        p.Append($"<h1>{Escape(store.Name)}</h1>");

        foreach (var line in new[] { store.AddressLine1, store.AddressLine2 })
        {
            if (!string.IsNullOrWhiteSpace(line))
                p.Append($"<p>{Escape(line)}</p>");
        }

        if (!string.IsNullOrWhiteSpace(store.Gstin))
            p.Append($"<p><b>GSTIN</b> {Escape(store.Gstin)}</p>");

        if (!string.IsNullOrWhiteSpace(store.FssaiNumber))
            p.Append($"<p>FSSAI {Escape(store.FssaiNumber)}</p>");

        var phone = store.CustomerCarePhone ?? store.Phone;

        if (!string.IsNullOrWhiteSpace(phone))
            p.Append($"<p>Phone {Escape(phone)}</p>");

        p.Append("</div><div class=\"kind\">");
        p.Append($"<h2>{(composition ? "BILL OF SUPPLY" : "TAX INVOICE")}</h2>");

        if (isCopy)
            p.Append("<p class=\"copy\">COPY</p>");

        p.Append($"<p><b>No.</b> {Escape(invoice.InvoiceNo)}</p>");
        p.Append($"<p><b>Date</b> {sale.CreatedAt.ToString("dd-MM-yyyy hh:mm tt", CultureInfo.InvariantCulture)}</p>");
        p.Append("</div></header>\n");

        // ---- Who it is for, and where ------------------------------------------------------------
        var place = sale.Buyer is { } registered
            ? GstStates.Label(registered.StateCode)
            : interState && sale.Customer?.StateCode is { } theirs
                ? GstStates.Label(theirs)
                : outletStateCode is { } ours ? GstStates.Label(ours) : null;

        p.Append("<div class=\"parties\"><div><h3>Bill to</h3>");

        if (sale.Buyer is { } buyer)
        {
            p.Append($"<p><b>{Escape(buyer.Name)}</b></p>");
            p.Append($"<p><b>GSTIN</b> {Escape(buyer.Gstin)}</p>");

            if (!string.IsNullOrWhiteSpace(buyer.Address))
                p.Append($"<p>{Escape(buyer.Address)}</p>");

            if (sale.Customer is { } business)
                p.Append($"<p>Mobile {Escape(business.MobileNo)}</p>");
        }
        else if (sale.Customer is { } customer)
        {
            p.Append($"<p><b>{Escape(customer.Name ?? customer.MobileNo)}</b></p>");

            if (customer.Name is not null)
                p.Append($"<p>Mobile {Escape(customer.MobileNo)}</p>");
        }
        else
        {
            p.Append("<p>Cash sale</p>");
        }

        p.Append("</div><div><h3>Place of supply</h3>");
        p.Append($"<p>{Escape(place ?? "—")}</p>");

        if (!composition)
            p.Append($"<p class=\"note\">{(interState ? "Inter-state: IGST" : "Within the state: CGST and SGST")}</p>");

        p.Append("</div></div>\n");

        // ---- The lines ---------------------------------------------------------------------------
        p.Append("<table>\n<thead><tr><th>#</th><th>Item</th><th class=\"num\">Qty</th><th class=\"num\">Rate</th><th class=\"num\">Disc.</th>");

        if (!composition)
        {
            p.Append("<th class=\"num\">Taxable</th><th class=\"num\">GST</th>");
            p.Append(interState ? "<th class=\"num\">IGST</th>" : "<th class=\"num\">CGST</th><th class=\"num\">SGST</th>");
        }

        p.Append("<th class=\"num\">Amount</th></tr></thead>\n<tbody>\n");

        var number = 0;

        foreach (var line in sale.Lines)
        {
            number++;
            p.Append($"<tr><td>{number}</td><td>{Escape(line.NameSnapshot)}<div class=\"hsn\">HSN {Escape(line.HsnSnapshot)}{(line.OfferName is { } offer ? $" · Offer: {Escape(offer)}" : "")}</div></td>");
            p.Append($"<td class=\"num\">{Escape(Units.WithQuantity(line.Quantity, line.Unit, ReceiptLanguage.English))}</td>");
            p.Append($"<td class=\"num\">{Amount(line.Mrp)}</td>");
            p.Append($"<td class=\"num\">{(line.Discount > 0m ? Amount(line.Discount) : "")}</td>");

            if (!composition)
            {
                p.Append($"<td class=\"num\">{Amount(Money.ToPresentation(line.Tax.TaxableValue))}</td>");
                p.Append($"<td class=\"num\">{Rate(line.GstRate)}%</td>");

                if (interState)
                {
                    p.Append($"<td class=\"num\">{Amount(Money.ToPresentation(line.Tax.Igst))}</td>");
                }
                else
                {
                    p.Append($"<td class=\"num\">{Amount(Money.ToPresentation(line.Tax.Cgst))}</td>");
                    p.Append($"<td class=\"num\">{Amount(Money.ToPresentation(line.Tax.Sgst))}</td>");
                }
            }

            p.Append($"<td class=\"num\">{Amount(line.LineTotal)}</td></tr>\n");
        }

        p.Append("</tbody>\n</table>\n");

        // ---- The totals --------------------------------------------------------------------------
        p.Append("<table class=\"totals\">");
        p.Append($"<tr><td>{(composition ? "Subtotal" : "Taxable value")}</td><td class=\"num\">{Amount(totals.SubtotalTaxable)}</td></tr>");

        if (totals.TotalDiscount > 0m)
            p.Append($"<tr><td>Discount given</td><td class=\"num\">{Amount(totals.TotalDiscount)}</td></tr>");

        if (totals.TotalCgst > 0m || totals.TotalSgst > 0m)
        {
            p.Append($"<tr><td>CGST</td><td class=\"num\">{Amount(totals.TotalCgst)}</td></tr>");
            p.Append($"<tr><td>SGST</td><td class=\"num\">{Amount(totals.TotalSgst)}</td></tr>");
        }

        if (totals.TotalIgst > 0m)
            p.Append($"<tr><td>IGST</td><td class=\"num\">{Amount(totals.TotalIgst)}</td></tr>");

        if (totals.RoundOff != 0m)
            p.Append($"<tr><td>Round off</td><td class=\"num\">{Amount(totals.RoundOff)}</td></tr>");

        p.Append($"<tr class=\"grand\"><td>Total</td><td class=\"num\">Rs {Amount(totals.AmountPayable)}</td></tr>");
        p.Append("</table>\n");
        p.Append($"<p class=\"words\">{Escape(AmountInWords.Rupees(totals.AmountPayable))}</p>\n");

        // ---- By HSN and rate, as a return reads it -------------------------------------------------
        if (!composition)
        {
            p.Append("<h4>Tax by HSN and rate</h4>\n<table>\n<thead><tr><th>HSN</th><th class=\"num\">Rate</th><th class=\"num\">Taxable</th>");
            p.Append(interState ? "<th class=\"num\">IGST</th>" : "<th class=\"num\">CGST</th><th class=\"num\">SGST</th>");
            p.Append("<th class=\"num\">Tax</th></tr></thead>\n<tbody>\n");

            foreach (var group in sale.Lines.GroupBy(l => (l.HsnSnapshot, l.GstRate)).OrderBy(g => g.Key.HsnSnapshot, StringComparer.Ordinal).ThenBy(g => g.Key.GstRate))
            {
                var taxable = Money.ToPresentation(group.Sum(l => l.Tax.TaxableValue));
                var cgst = Money.ToPresentation(group.Sum(l => l.Tax.Cgst));
                var sgst = Money.ToPresentation(group.Sum(l => l.Tax.Sgst));
                var igst = Money.ToPresentation(group.Sum(l => l.Tax.Igst));

                p.Append($"<tr><td>{Escape(group.Key.HsnSnapshot)}</td><td class=\"num\">{Rate(group.Key.GstRate)}%</td><td class=\"num\">{Amount(taxable)}</td>");
                p.Append(interState
                    ? $"<td class=\"num\">{Amount(igst)}</td>"
                    : $"<td class=\"num\">{Amount(cgst)}</td><td class=\"num\">{Amount(sgst)}</td>");
                p.Append($"<td class=\"num\">{Amount(cgst + sgst + igst)}</td></tr>\n");
            }

            p.Append("</tbody>\n</table>\n");
        }

        // ---- Paid, and the foot ------------------------------------------------------------------
        var paid = string.Join(", ", sale.Payments.GroupBy(x => x.Type).Select(g => $"{Tender(g.Key)} {Amount(g.Sum(x => x.Amount))}"));
        p.Append($"<p class=\"note\">Paid: {Escape(paid)}{(sale.ChangeDue > 0m ? $"; change {Amount(sale.ChangeDue)}" : "")}.</p>\n");

        if (composition)
            p.Append($"<p><b>{Escape(CompositionDeclaration.Text)}</b></p>\n");

        p.Append("<div class=\"foot\"><div class=\"note\">");
        p.Append(Escape(string.IsNullOrWhiteSpace(store.FooterMessage) ? "Thank you." : store.FooterMessage));
        p.Append("</div>");
        p.Append($"<div class=\"sign\">For {Escape(store.Name)}</div></div>\n");

        p.Append("</div>\n</body>\n</html>");
        return p.ToString();
    }

    private static string Tender(TenderType type) => type switch
    {
        TenderType.Cash => "Cash",
        TenderType.Card => "Card",
        TenderType.Upi => "UPI",
        TenderType.StoreCredit => "Khata",
        TenderType.LoyaltyPoints => "Points",
        _ => type.ToString(),
    };

    private static string Amount(decimal value) => value.ToString("N2", India);

    private static string Rate(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Escape(string? text) => string.IsNullOrEmpty(text)
        ? string.Empty
        : text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
