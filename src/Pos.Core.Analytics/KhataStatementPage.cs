using System.Globalization;
using System.Text;
using Pos.Core.Domain;
using Pos.Core.Domain.Printing;
using Pos.Core.Hardware.Printing;

namespace Pos.Core.Analytics;

/// <summary>
/// Khata statements as a page to print on A4 or send as a file: one statement a page, each with the
/// shop's heading, every line with the balance after it, what is owed and how old it is - and a UPI
/// code for it when the shop has an ID.
/// </summary>
/// <remarks>
/// Several statements go in one file, each starting a new page, so the month-end round of everyone
/// who owes is one thing to print. White and plain: it is for paper.
/// </remarks>
public static class KhataStatementPage
{
    private static readonly CultureInfo India = CultureInfo.GetCultureInfo("en-IN");

    public static string Render(IReadOnlyList<KhataStatement> statements, StoreProfile store, UpiPayee? upi = null)
    {
        ArgumentNullException.ThrowIfNull(statements);
        ArgumentNullException.ThrowIfNull(store);

        var p = new StringBuilder(8 * 1024 * Math.Max(1, statements.Count));

        p.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
        p.Append($"<title>{Escape(store.Name)} — khata statement{(statements.Count == 1 ? $", {Escape(Who(statements[0].Customer))}" : "s")}</title>\n");
        p.Append("""
            <style>
              @page { size: A4; margin: 14mm; }
              * { box-sizing: border-box; }
              body { margin: 0; font-family: "Segoe UI", "Nirmala UI", Arial, sans-serif; color: #000; background: #fff; font-size: 10.5pt; }
              .statement { break-after: page; max-width: 180mm; margin: 0 auto; }
              .statement:last-child { break-after: auto; }
              header { text-align: center; border-bottom: 2px solid #000; padding-bottom: 3mm; }
              header h1 { font-size: 17pt; margin: 0; }
              header p { margin: 1mm 0 0; color: #333; }
              h2 { font-size: 12pt; letter-spacing: .08em; text-align: center; margin: 4mm 0; }
              .who { display: grid; grid-template-columns: auto 1fr auto 1fr; gap: 1mm 4mm; margin-bottom: 4mm; }
              .who dt { color: #555; }
              .who dd { margin: 0; font-weight: 600; }
              table { width: 100%; border-collapse: collapse; }
              th, td { padding: 1.4mm 2mm; border-bottom: 1px solid #ddd; text-align: left; vertical-align: top; }
              th { border-bottom: 1.5px solid #000; font-size: 9pt; text-transform: uppercase; letter-spacing: .04em; }
              .num { text-align: right; font-variant-numeric: tabular-nums; white-space: nowrap; }
              tr.opening td { color: #555; font-style: italic; }
              .summary { display: flex; justify-content: space-between; gap: 8mm; margin-top: 5mm; }
              .totals { flex: 1; }
              .totals div { display: flex; justify-content: space-between; padding: 1mm 0; }
              .totals .owed { font-size: 15pt; font-weight: 700; border-top: 2px solid #000; margin-top: 2mm; padding-top: 2mm; }
              .ageing { flex: 1; }
              .ageing div { display: flex; justify-content: space-between; padding: 1mm 0; color: #333; }
              .pay { display: flex; align-items: center; gap: 6mm; margin-top: 6mm; padding: 4mm; border: 1.5px solid #000; border-radius: 3mm; break-inside: avoid; }
              .pay svg { flex: none; }
              .pay .amount { font-size: 18pt; font-weight: 700; }
              .note { margin-top: 5mm; color: #555; font-size: 9pt; text-align: center; }
            </style>
            </head>
            <body>
            """);

        foreach (var statement in statements)
            Statement(p, statement, store, upi);

        p.Append("</body>\n</html>");
        return p.ToString();
    }

    private static void Statement(StringBuilder p, KhataStatement s, StoreProfile store, UpiPayee? upi)
    {
        var customer = s.Customer;

        p.Append("<section class=\"statement\">\n<header>");
        p.Append($"<h1>{Escape(store.Name)}</h1>");

        var address = string.Join(", ", new[] { store.AddressLine1, store.AddressLine2 }.Where(a => !string.IsNullOrWhiteSpace(a)));
        var phone = store.CustomerCarePhone ?? store.Phone;

        if (address.Length > 0 || !string.IsNullOrWhiteSpace(phone))
            p.Append($"<p>{Escape(address)}{(address.Length > 0 && !string.IsNullOrWhiteSpace(phone) ? " · " : "")}{Escape(phone)}</p>");

        p.Append("</header>\n<h2>KHATA STATEMENT</h2>\n<dl class=\"who\">");
        p.Append($"<dt>Customer</dt><dd>{Escape(Who(customer))}</dd>");
        p.Append($"<dt>Mobile</dt><dd>{Escape(customer.MobileNo)}</dd>");
        p.Append($"<dt>Period</dt><dd>{Day(s.From)} to {Day(s.To)}</dd>");
        p.Append($"<dt>Owed now</dt><dd>Rs {Money(Math.Max(0m, s.Closing))}</dd>");
        p.Append("</dl>\n");

        p.Append("<table>\n<thead><tr><th>Date</th><th>What</th><th class=\"num\">Bought</th><th class=\"num\">Paid / returned</th><th class=\"num\">Owed after</th></tr></thead>\n<tbody>\n");
        p.Append($"<tr class=\"opening\"><td>{Day(s.From)}</td><td>Owed at the start</td><td></td><td></td><td class=\"num\">{Money(s.Opening)}</td></tr>\n");

        foreach (var line in s.Lines)
        {
            var entry = line.Entry;
            var what = entry.Kind switch
            {
                KhataEntryKind.Bought => $"Bill {entry.Reference}",
                KhataEntryKind.Returned => $"Goods returned, credit note {entry.Reference}",
                _ => $"Paid, {Tender(entry.Tender)}",
            };

            var bought = entry.Kind == KhataEntryKind.Bought ? Money(entry.Amount) : string.Empty;
            var off = entry.Kind == KhataEntryKind.Bought ? string.Empty : Money(entry.Amount);

            p.Append($"<tr><td>{entry.At.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)}</td><td>{Escape(what)}</td>");
            p.Append($"<td class=\"num\">{bought}</td><td class=\"num\">{off}</td><td class=\"num\">{Money(line.BalanceAfter)}</td></tr>\n");
        }

        p.Append("</tbody>\n</table>\n<div class=\"summary\">\n<div class=\"totals\">");
        p.Append($"<div><span>Bought on khata ({Plural.Of(s.Bills, "bill")})</span><span class=\"num\">{Money(s.Bought)}</span></div>");
        p.Append($"<div><span>Paid back</span><span class=\"num\">{Money(s.Paid)}</span></div>");

        if (s.Returned > 0m)
            p.Append($"<div><span>Goods returned</span><span class=\"num\">{Money(s.Returned)}</span></div>");

        p.Append($"<div class=\"owed\"><span>Owed now</span><span class=\"num\">Rs {Money(Math.Max(0m, s.Closing))}</span></div>");
        p.Append("</div>\n");

        if (s.Ageing.OldestUnpaid is { } oldest)
        {
            p.Append("<div class=\"ageing\">");
            p.Append($"<div><span>Oldest unpaid bill</span><span>{Day(oldest)}, {s.Ageing.DaysWaiting(s.To)} days</span></div>");
            p.Append($"<div><span>Up to 30 days</span><span class=\"num\">{Money(s.Ageing.UpTo30Days)}</span></div>");
            p.Append($"<div><span>31 to 60 days</span><span class=\"num\">{Money(s.Ageing.Days31To60)}</span></div>");
            p.Append($"<div><span>61 to 90 days</span><span class=\"num\">{Money(s.Ageing.Days61To90)}</span></div>");
            p.Append($"<div><span>Over 90 days</span><span class=\"num\">{Money(s.Ageing.Over90Days)}</span></div>");
            p.Append("</div>\n");
        }

        p.Append("</div>\n");

        if (s.OwesAnything && upi is not null)
        {
            var link = UpiLink.For(upi, s.Closing, note: $"Khata {customer.MobileNo}");

            p.Append("<div class=\"pay\">");
            p.Append(QrSvg(link, "34mm"));
            p.Append($"<div><div>Scan to pay by UPI</div><div class=\"amount\">Rs {Money(s.Closing)}</div>");
            p.Append($"<div>to {Escape(upi.Name)} ({Escape(upi.Id)})</div></div>");
            p.Append("</div>\n");
        }

        p.Append($"<p class=\"note\">{(s.OwesAnything ? "Not a bill. This is the khata as the shop's books have it" : "Nothing is owed. Thank you")} — {DateTime.Today.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)}.</p>\n");
        p.Append("</section>\n");
    }

    /// <summary>
    /// A QR code as an SVG, one unit a module with the quiet zone around it, drawn crisp at any size.
    /// </summary>
    public static string QrSvg(string text, string size)
    {
        var code = QrCode.Encode(text);
        var side = code.Size + 8;
        var path = new StringBuilder(code.Size * code.Size * 4);

        for (var y = 0; y < code.Size; y++)
        {
            var x = 0;

            while (x < code.Size)
            {
                if (!code[x, y])
                {
                    x++;
                    continue;
                }

                var start = x;

                while (x < code.Size && code[x, y])
                    x++;

                path.Append(CultureInfo.InvariantCulture, $"M{start + 4} {y + 4}h{x - start}v1h-{x - start}z");
            }
        }

        return $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {side} {side}\" width=\"{size}\" height=\"{size}\" shape-rendering=\"crispEdges\" role=\"img\" aria-label=\"UPI code\">"
               + $"<rect width=\"{side}\" height=\"{side}\" fill=\"#fff\"/><path d=\"{path}\" fill=\"#000\"/></svg>";
    }

    private static string Who(Customer customer) => customer.Name ?? customer.MobileNo;

    private static string Tender(TenderType? tender) => tender switch
    {
        TenderType.Cash => "cash",
        TenderType.Card => "card",
        TenderType.Upi => "UPI",
        null => "money",
        _ => tender.ToString()!,
    };

    private static string Day(DateOnly day) => day.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);

    private static string Money(decimal value) => value.ToString("N2", India);

    private static string Escape(string? text) => string.IsNullOrEmpty(text)
        ? string.Empty
        : text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
