using System.Globalization;
using System.Text;

namespace Pos.Core.Analytics;

/// <summary>
/// Draws the dashboard as one self-contained HTML file.
/// </summary>
/// <remarks>
/// <para>
/// A file rather than a screen in the till. A shop owner looks at this in the back room, on a phone,
/// or sends it to an accountant — none of which a window inside the billing application can do. It
/// also means the dashboard cannot slow a sale down or crash a counter: it is produced by a separate
/// command, reads the books without writing to them, and the till never knows it happened.
/// </para>
/// <para>
/// The charts are hand-drawn SVG. A charting library would be a download, a licence and a
/// dependency that has to keep working offline on a machine that has never seen the internet —
/// against perhaps two hundred lines of geometry for the six shapes this page actually needs.
/// </para>
/// </remarks>
public static class DashboardPage
{
    private static readonly CultureInfo India = CultureInfo.GetCultureInfo("en-IN");

    public static string Render(DashboardData d, string shopName)
    {
        ArgumentNullException.ThrowIfNull(d);

        var page = new StringBuilder(64 * 1024);

        page.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
        page.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        page.Append($"<title>{Escape(shopName)} — Dashboard</title>\n");
        page.Append(Styles);
        page.Append("</head>\n<body>\n<div class=\"wrap\">\n");

        WriteHeader(page, d, shopName);
        WriteKpis(page, d);
        WriteHourlyAndTrend(page, d);
        WriteItems(page, d);
        WriteReconciliation(page, d);
        WriteCustomers(page, d);
        WriteFooter(page, d);

        page.Append("</div>\n</body>\n</html>");
        return page.ToString();
    }

    // ---- Sections ------------------------------------------------------------------------------

    private static void WriteHeader(StringBuilder p, DashboardData d, string shopName)
    {
        var days = (int)Math.Round((d.To - d.From).TotalDays);

        p.Append("<header class=\"top\">");
        p.Append($"<p class=\"eyebrow\">Lane {Escape(d.LaneId)} &middot; {days} days to {d.To.ToString("dd MMM yyyy", India)}</p>");
        p.Append($"<h1>{Escape(shopName)}</h1>");
        p.Append($"<p class=\"meta\">Generated {d.GeneratedAt.ToString("dd MMM yyyy, HH:mm", India)} &middot; read from the lane's own books in {d.Elapsed.TotalMilliseconds:N0} ms</p>");
        p.Append("</header>");
    }

    private static void WriteKpis(StringBuilder p, DashboardData d)
    {
        var t = d.Today;

        p.Append("<h2>Today</h2><p class=\"lede\">Since midnight. Everything below this covers the whole window.</p>");
        p.Append("<div class=\"kpis\">");

        Card(p, "Net sales", Money(t.NetSales), $"gross {Money(t.GrossSales)}, less {Money(t.Discount)} off");
        Card(p, "Bills", t.Bills.ToString("N0", India), t.Bills == 0 ? "nothing yet" : $"{Money(t.AverageBasket)} average basket");
        Card(p, "Cash in drawer", Money(t.CashInDrawer), $"{Money(t.Cash)} taken, {Money(t.ChangeGiven)} change");
        Card(p, "Digital", Money(t.Digital), "UPI, cards and store credit");

        p.Append("</div>");
    }

    private static void WriteHourlyAndTrend(StringBuilder p, DashboardData d)
    {
        p.Append("<h2>When the shop is busy</h2>");
        p.Append("<p class=\"lede\">Where the rush actually falls, and whether takings are climbing or drifting. "
               + "The quiet hours are as useful as the busy ones — they are when a delivery or a stock count fits.</p>");

        p.Append("<div class=\"panel\"><h3>Takings by hour</h3>");
        p.Append(BarChart([.. d.Hourly.Select(h => ((double)h.NetSales, $"{h.Hour:00}"))],
            [.. d.Hourly.Select(h => $"{h.Hour:00}:00 — {Money(h.NetSales)} over {h.Bills} bill(s)")]));
        p.Append("</div>");

        p.Append("<div class=\"panel\"><h3>Daily takings</h3>");
        p.Append(LineChart(d.Daily));
        p.Append("</div>");

        p.Append("<div class=\"panel\"><h3>Day of week against time of day</h3>");
        p.Append("<p class=\"note\">Darker is busier. Read down a column to find a slot that is quiet every week.</p>");
        p.Append(Heatmap(d.WeekdayByHour));
        p.Append("</div>");
    }

    private static void WriteItems(StringBuilder p, DashboardData d)
    {
        p.Append("<h2>What sells</h2>");
        p.Append("<p class=\"lede\">By what it brings in, not by how many move — the two are rarely the same list, "
               + "and the first is the one that must never be out of stock.</p>");

        if (d.TopItems.Count == 0)
        {
            p.Append("<div class=\"panel\"><p class=\"empty\">No sales in this window.</p></div>");
            return;
        }

        var most = d.TopItems.Max(i => i.NetSales);

        p.Append("<div class=\"panel\"><div class=\"scroller\"><table class=\"items\"><thead><tr>");
        p.Append("<th>Item</th><th>HSN</th><th class=\"n\">Sold</th><th class=\"n\">Bills</th><th class=\"n\">Takings</th><th class=\"bar\"></th>");
        p.Append("</tr></thead><tbody>");

        foreach (var item in d.TopItems)
        {
            var width = most == 0m ? 0 : (double)(item.NetSales / most) * 100;

            p.Append($"<tr><td>{Escape(item.Name)}</td><td class=\"mono\">{Escape(item.Hsn)}</td>");
            p.Append($"<td class=\"n\">{item.Quantity.ToString("0.###", India)} {Escape(item.Unit)}</td>");
            p.Append($"<td class=\"n\">{item.Bills.ToString("N0", India)}</td>");
            p.Append($"<td class=\"n\">{Money(item.NetSales)}</td>");
            p.Append($"<td class=\"bar\"><span style=\"width:{width.ToString("0.#", CultureInfo.InvariantCulture)}%\"></span></td></tr>");
        }

        p.Append("</tbody></table></div></div>");
    }

    private static void WriteReconciliation(StringBuilder p, DashboardData d)
    {
        p.Append("<h2>Money and tax</h2>");
        p.Append("<p class=\"lede\">What to reconcile a drawer and a bank statement against, and the slab-wise "
               + "figures a GST return is filed from.</p>");

        p.Append("<div class=\"two\">");

        p.Append("<div class=\"panel\"><h3>How customers paid</h3>");

        if (d.Tenders.Count == 0)
        {
            p.Append("<p class=\"empty\">Nothing taken in this window.</p>");
        }
        else
        {
            p.Append(Donut(d.Tenders));
            p.Append("<table class=\"plain\">");

            foreach (var (tender, index) in d.Tenders.Select((t, i) => (t, i)))
            {
                p.Append($"<tr><td><span class=\"swatch\" style=\"background:{Slice(index)}\"></span>{Escape(tender.Tender)}</td>");
                p.Append($"<td class=\"n\">{tender.Count.ToString("N0", India)}</td><td class=\"n\">{Money(tender.Amount)}</td></tr>");
            }

            p.Append("</table>");
        }

        p.Append("</div>");

        p.Append("<div class=\"panel\"><h3>GST by slab</h3>");

        if (d.GstSlabs.Count == 0)
        {
            p.Append("<p class=\"empty\">No taxable sales in this window.</p>");
        }
        else
        {
            p.Append("<div class=\"scroller\"><table class=\"plain slabs\"><thead><tr><th>Slab</th><th class=\"n\">Taxable</th><th class=\"n\">CGST</th><th class=\"n\">SGST</th><th class=\"n\">IGST</th><th class=\"n\">Tax</th></tr></thead><tbody>");

            foreach (var slab in d.GstSlabs)
            {
                p.Append($"<tr><td class=\"mono\">{slab.Rate.ToString("0.##", India)}%</td>");
                p.Append($"<td class=\"n\">{Money(slab.TaxableValue)}</td><td class=\"n\">{Money(slab.Cgst)}</td>");
                p.Append($"<td class=\"n\">{Money(slab.Sgst)}</td><td class=\"n\">{Money(slab.Igst)}</td>");
                p.Append($"<td class=\"n strong\">{Money(slab.TotalTax)}</td></tr>");
            }

            p.Append($"<tr class=\"total\"><td>Total</td><td class=\"n\">{Money(d.GstSlabs.Sum(s => s.TaxableValue))}</td>");
            p.Append($"<td class=\"n\">{Money(d.GstSlabs.Sum(s => s.Cgst))}</td><td class=\"n\">{Money(d.GstSlabs.Sum(s => s.Sgst))}</td>");
            p.Append($"<td class=\"n\">{Money(d.GstSlabs.Sum(s => s.Igst))}</td><td class=\"n strong\">{Money(d.GstSlabs.Sum(s => s.TotalTax))}</td></tr>");
            p.Append("</tbody></table></div>");
        }

        p.Append("</div></div>");

        // Discounts and voids together: both are money that did not become takings, and a shift in
        // either is the first sign of something worth asking about.
        p.Append("<div class=\"panel\"><h3>Given away, and cancelled</h3>");
        p.Append("<div class=\"figures\">");
        Figure(p, "Discounts", Money(d.Range.Discount), $"{Percent(d.Range.Discount, d.Range.GrossSales)} of gross");
        Figure(p, "Bills voided", d.Voids.Count.ToString("N0", India), $"{Money(d.Voids.Value)} cancelled");
        Figure(p, "Points redeemed", d.Points.Redeemed.ToString("N0", India), "settled as a tender, not a discount");
        p.Append("</div>");
        p.Append("<p class=\"note\">Voided bills keep their number and stay in the books. They are excluded from every "
               + "other figure on this page, which is what lets the invoice run be checked for gaps.</p>");
        p.Append("</div>");
    }

    private static void WriteCustomers(StringBuilder p, DashboardData d)
    {
        var c = d.Customers;

        p.Append("<h2>Customers</h2>");
        p.Append("<p class=\"lede\">Only bills rung up against a mobile number can be told apart. A walk-in is "
               + "counted but not recognised, which is the honest limit of what the books know.</p>");

        p.Append("<div class=\"two\">");

        p.Append("<div class=\"panel\"><h3>Known against walk-in</h3>");

        if (c.TotalBills == 0)
        {
            p.Append("<p class=\"empty\">No bills in this window.</p>");
        }
        else
        {
            p.Append(StackedBar(
                [("Known customers", (double)c.IdentifiedSales), ("Walk-in", (double)c.WalkInSales)]));

            p.Append("<div class=\"figures\">");
            Figure(p, "Known", $"{c.IdentifiedBills:N0} bills", Money(c.IdentifiedSales));
            Figure(p, "Walk-in", $"{c.WalkInBills:N0} bills", Money(c.WalkInSales));
            Figure(p, "Came back", c.ReturningCustomers.ToString("N0", India), $"of {c.DistinctCustomers:N0} known customers");
            p.Append("</div>");
        }

        p.Append("</div>");

        p.Append("<div class=\"panel\"><h3>Loyalty points</h3>");
        p.Append(PointsChart(d.Points));
        p.Append("<div class=\"figures\">");
        Figure(p, "Earned", d.Points.Earned.ToString("N0", India), "in this window");
        Figure(p, "Redeemed", d.Points.Redeemed.ToString("N0", India), "in this window");
        Figure(p, "Outstanding", d.Points.OutstandingBalance.ToString("N0", India), "what the shop still owes");
        p.Append("</div></div>");

        p.Append("</div>");
    }

    private static void WriteFooter(StringBuilder p, DashboardData d)
    {
        p.Append("<footer>");
        p.Append("<p><strong>What this page cannot show, and why.</strong> The books record what was sold and for "
               + "how much, and nothing about what it cost or what shelf it came from. So there is no margin here, "
               + "no category split and no wastage — those need a cost price and a category on the catalogue, which "
               + "this version does not carry. Returns are not shown because this version does not do them.</p>");
        p.Append("<p>Read from the lane's database without writing to it. Producing this page cannot affect a sale, "
               + "and nothing on the till is aware it ran.</p>");
        p.Append("</footer>");
    }

    // ---- Pieces --------------------------------------------------------------------------------

    private static void Card(StringBuilder p, string label, string value, string note)
    {
        p.Append($"<div class=\"kpi\"><div class=\"l\">{Escape(label)}</div>");
        p.Append($"<div class=\"v\">{Escape(value)}</div>");
        p.Append($"<div class=\"n\">{Escape(note)}</div></div>");
    }

    private static void Figure(StringBuilder p, string label, string value, string note)
    {
        p.Append($"<div class=\"figure\"><div class=\"l\">{Escape(label)}</div>");
        p.Append($"<div class=\"v\">{Escape(value)}</div><div class=\"n\">{Escape(note)}</div></div>");
    }

    private static string BarChart(IReadOnlyList<(double Value, string Label)> bars, IReadOnlyList<string> titles)
    {
        const int width = 720;
        const int height = 180;
        const int pad = 24;

        var max = bars.Count == 0 ? 0 : bars.Max(b => b.Value);
        var svg = new StringBuilder();

        svg.Append($"<svg class=\"chart\" viewBox=\"0 0 {width} {height + 22}\" preserveAspectRatio=\"none\" role=\"img\">");

        var slot = (double)(width - pad) / Math.Max(1, bars.Count);

        for (var i = 0; i < bars.Count; i++)
        {
            var h = max <= 0 ? 0 : bars[i].Value / max * (height - pad);
            var x = pad + (i * slot);
            var y = height - h;

            svg.Append($"<rect class=\"bar\" x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(slot * 0.72)}\" height=\"{F(Math.Max(0, h))}\">");
            svg.Append($"<title>{Escape(titles[i])}</title></rect>");

            // Every third hour, so the axis stays readable at a phone's width.
            if (i % 3 == 0)
                svg.Append($"<text class=\"tick\" x=\"{F(x + (slot * 0.36))}\" y=\"{height + 15}\">{Escape(bars[i].Label)}</text>");
        }

        svg.Append($"<line class=\"axis\" x1=\"{pad}\" y1=\"{height}\" x2=\"{width}\" y2=\"{height}\" />");
        svg.Append("</svg>");
        return svg.ToString();
    }

    private static string LineChart(IReadOnlyList<DailyPoint> days)
    {
        if (days.Count == 0)
            return "<p class=\"empty\">No days in this window.</p>";

        const int width = 720;
        const int height = 200;
        const int pad = 28;

        var max = Math.Max(1d, days.Max(d => (double)d.NetSales));
        var step = (double)(width - pad) / Math.Max(1, days.Count - 1);

        string X(int i) => F(pad + (i * step));
        string Y(double v) => F(height - (v / max * (height - pad)));

        var line = new StringBuilder();
        var area = new StringBuilder($"M {X(0)} {F(height)} ");

        for (var i = 0; i < days.Count; i++)
        {
            var y = Y((double)days[i].NetSales);
            line.Append(i == 0 ? $"M {X(i)} {y} " : $"L {X(i)} {y} ");
            area.Append($"L {X(i)} {y} ");
        }

        area.Append($"L {X(days.Count - 1)} {F(height)} Z");

        // A seven-day mean over the top, because a grocery's week has a shape and the raw line is
        // mostly that shape repeating.
        var mean = new StringBuilder();

        for (var i = 0; i < days.Count; i++)
        {
            var from = Math.Max(0, i - 6);
            var average = days.Skip(from).Take(i - from + 1).Average(d => (double)d.NetSales);
            mean.Append(i == 0 ? $"M {X(i)} {Y(average)} " : $"L {X(i)} {Y(average)} ");
        }

        var svg = new StringBuilder();
        svg.Append($"<svg class=\"chart\" viewBox=\"0 0 {width} {height + 22}\" role=\"img\">");
        svg.Append($"<path class=\"area\" d=\"{area}\" />");
        svg.Append($"<path class=\"line\" d=\"{line}\" />");
        svg.Append($"<path class=\"mean\" d=\"{mean}\" />");
        svg.Append($"<line class=\"axis\" x1=\"{pad}\" y1=\"{height}\" x2=\"{width}\" y2=\"{height}\" />");
        svg.Append($"<text class=\"tick\" x=\"{pad}\" y=\"{height + 15}\" text-anchor=\"start\">{days[0].Date.ToString("dd MMM", India)}</text>");
        svg.Append($"<text class=\"tick\" x=\"{width}\" y=\"{height + 15}\" text-anchor=\"end\">{days[^1].Date.ToString("dd MMM", India)}</text>");
        svg.Append($"<text class=\"tick\" x=\"{pad}\" y=\"14\" text-anchor=\"start\">peak {Money((decimal)max)}</text>");
        svg.Append("</svg>");
        svg.Append("<p class=\"legend\"><span class=\"k line\"></span>daily <span class=\"k mean\"></span>seven-day average</p>");

        return svg.ToString();
    }

    private static string Heatmap(IReadOnlyList<WeekdayHourCell> cells)
    {
        string[] names = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
        var max = cells.Count == 0 ? 0m : cells.Max(c => c.NetSales);

        var html = new StringBuilder("<div class=\"scroller\"><table class=\"heat\"><thead><tr><th></th>");

        for (var band = 0; band < 24; band += 2)
            html.Append($"<th>{band:00}</th>");

        html.Append("</tr></thead><tbody>");

        for (var weekday = 1; weekday <= 7; weekday++)
        {
            html.Append($"<tr><th>{names[weekday - 1]}</th>");

            for (var band = 0; band < 24; band += 2)
            {
                var cell = cells.FirstOrDefault(c => c.Weekday == weekday && c.HourBand == band);
                var intensity = max == 0m || cell is null ? 0d : (double)(cell.NetSales / max);
                var title = cell is null || cell.Bills == 0
                    ? $"{names[weekday - 1]} {band:00}:00 — nothing"
                    : $"{names[weekday - 1]} {band:00}:00-{band + 2:00}:00 — {Money(cell.NetSales)} over {cell.Bills} bill(s)";

                html.Append($"<td style=\"--i:{intensity.ToString("0.###", CultureInfo.InvariantCulture)}\" title=\"{Escape(title)}\"></td>");
            }

            html.Append("</tr>");
        }

        html.Append("</tbody></table></div>");
        return html.ToString();
    }

    private static string Donut(IReadOnlyList<TenderSlice> slices)
    {
        var total = slices.Sum(s => (double)s.Amount);

        if (total <= 0)
            return "<p class=\"empty\">Nothing taken.</p>";

        const double radius = 60;
        const double thickness = 26;
        const double circumference = 2 * Math.PI * radius;

        var svg = new StringBuilder("<svg class=\"donut\" viewBox=\"0 0 160 160\" role=\"img\">");
        var offset = 0d;

        for (var i = 0; i < slices.Count; i++)
        {
            var share = (double)slices[i].Amount / total;
            var length = share * circumference;

            svg.Append($"<circle cx=\"80\" cy=\"80\" r=\"{F(radius)}\" fill=\"none\" stroke=\"{Slice(i)}\" ");
            svg.Append($"stroke-width=\"{F(thickness)}\" stroke-dasharray=\"{F(length)} {F(circumference - length)}\" ");
            svg.Append($"stroke-dashoffset=\"{F(-offset)}\" transform=\"rotate(-90 80 80)\">");
            svg.Append($"<title>{Escape($"{slices[i].Tender} — {Money(slices[i].Amount)}")}</title></circle>");

            offset += length;
        }

        svg.Append($"<text class=\"middle\" x=\"80\" y=\"84\">{Escape(Money((decimal)total))}</text>");
        svg.Append("</svg>");
        return svg.ToString();
    }

    private static string StackedBar(IReadOnlyList<(string Label, double Value)> parts)
    {
        var total = parts.Sum(p => p.Value);

        if (total <= 0)
            return "<p class=\"empty\">Nothing taken.</p>";

        var html = new StringBuilder("<div class=\"stack\">");

        for (var i = 0; i < parts.Count; i++)
        {
            var share = parts[i].Value / total * 100;

            if (share <= 0)
                continue;

            html.Append($"<span style=\"width:{share.ToString("0.##", CultureInfo.InvariantCulture)}%;background:{Slice(i)}\" ");
            html.Append($"title=\"{Escape($"{parts[i].Label} — {share:0.#}%")}\"></span>");
        }

        html.Append("</div>");
        return html.ToString();
    }

    private static string PointsChart(PointsFlow points)
    {
        if (points.Daily.Count == 0)
            return "<p class=\"empty\">No loyalty movement in this window.</p>";

        const int width = 340;
        const int height = 120;
        const int pad = 18;

        var max = Math.Max(1, points.Daily.Max(d => Math.Max(d.Earned, d.Redeemed)));
        var step = (double)(width - pad) / Math.Max(1, points.Daily.Count - 1);

        string Path(Func<PointsDay, int> pick)
        {
            var path = new StringBuilder();

            for (var i = 0; i < points.Daily.Count; i++)
            {
                var x = F(pad + (i * step));
                var y = F(height - ((double)pick(points.Daily[i]) / max * (height - pad)));
                path.Append(i == 0 ? $"M {x} {y} " : $"L {x} {y} ");
            }

            return path.ToString();
        }

        var svg = new StringBuilder($"<svg class=\"chart small\" viewBox=\"0 0 {width} {height + 6}\" role=\"img\">");
        svg.Append($"<path class=\"line\" d=\"{Path(d => d.Earned)}\" />");
        svg.Append($"<path class=\"mean\" d=\"{Path(d => d.Redeemed)}\" />");
        svg.Append($"<line class=\"axis\" x1=\"{pad}\" y1=\"{height}\" x2=\"{width}\" y2=\"{height}\" />");
        svg.Append("</svg>");
        svg.Append("<p class=\"legend\"><span class=\"k line\"></span>earned <span class=\"k mean\"></span>redeemed</p>");

        return svg.ToString();
    }

    // ---- Formatting ----------------------------------------------------------------------------

    /// <summary>Indian digit grouping: 1,84,000 rather than 184,000.</summary>
    private static string Money(decimal value) => "Rs " + value.ToString("N2", India);

    private static string Percent(decimal part, decimal whole) =>
        whole == 0m ? "0%" : (part / whole * 100m).ToString("0.#", India) + "%";

    private static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Six colours, reused round. Beyond six tenders a pie stops meaning anything anyway.</summary>
    private static string Slice(int index) =>
        (string[])["#1F7A4D", "#16628F", "#B7791F", "#8B3A62", "#4A5568", "#9C4221"] is var palette
            ? palette[index % palette.Length]
            : "#1F7A4D";

    private static string Escape(string? text) => string.IsNullOrEmpty(text)
        ? string.Empty
        : text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private const string Styles = """
        <style>
          :root {
            --paper:#F5F7F9; --card:#FFFFFF; --ink:#141A20; --soft:#556570; --faint:#7C8894;
            --rule:#DFE5EA; --accent:#16628F; --good:#1F7A4D; --shadow:0 1px 2px rgba(20,32,44,.06),0 6px 18px rgba(20,32,44,.06);
          }
          @media (prefers-color-scheme: dark) {
            :root:not([data-theme="light"]) {
              --paper:#0F141A; --card:#161D24; --ink:#E8EEF3; --soft:#98A6B2; --faint:#7A8794;
              --rule:#28313A; --accent:#58B4F0; --good:#5FCE94; --shadow:0 1px 2px rgba(0,0,0,.4),0 6px 18px rgba(0,0,0,.3);
            }
          }
          :root[data-theme="dark"] {
            --paper:#0F141A; --card:#161D24; --ink:#E8EEF3; --soft:#98A6B2; --faint:#7A8794;
            --rule:#28313A; --accent:#58B4F0; --good:#5FCE94; --shadow:0 1px 2px rgba(0,0,0,.4),0 6px 18px rgba(0,0,0,.3);
          }
          *{box-sizing:border-box}
          body{background:var(--paper);color:var(--ink);margin:0;padding:0 20px 80px;
               font-family:"Segoe UI",system-ui,-apple-system,sans-serif;font-size:15.5px;line-height:1.55}
          .wrap{max-width:1120px;margin:0 auto}
          header.top{padding:44px 0 22px;border-bottom:2px solid var(--ink);margin-bottom:8px}
          .eyebrow{font-size:12px;font-weight:600;letter-spacing:.12em;text-transform:uppercase;color:var(--accent);margin:0 0 10px}
          h1{font-size:clamp(28px,4.5vw,40px);line-height:1.1;margin:0 0 10px;letter-spacing:-.01em}
          .meta{color:var(--soft);font-size:14px;margin:0}
          h2{font-size:24px;margin:44px 0 4px;letter-spacing:-.01em}
          h3{font-size:15px;margin:0 0 14px;color:var(--soft);font-weight:600;
             text-transform:uppercase;letter-spacing:.06em}
          .lede{color:var(--soft);margin:0 0 20px;max-width:66ch}
          .note{color:var(--faint);font-size:13.5px;margin:12px 0 0}
          .empty{color:var(--faint);margin:0;padding:18px 0}
          .kpis{display:grid;grid-template-columns:repeat(auto-fit,minmax(210px,1fr));gap:14px;margin-bottom:8px}
          .kpi{background:var(--card);border:1px solid var(--rule);border-radius:10px;padding:18px 20px;box-shadow:var(--shadow)}
          .kpi .l{font-size:12px;text-transform:uppercase;letter-spacing:.07em;color:var(--faint)}
          .kpi .v{font-size:30px;font-weight:700;line-height:1.15;margin:6px 0 4px;font-variant-numeric:tabular-nums}
          .kpi .n{font-size:13px;color:var(--soft)}
          .panel{background:var(--card);border:1px solid var(--rule);border-radius:10px;padding:20px 22px;
                 box-shadow:var(--shadow);margin-bottom:16px}
          .two{display:grid;grid-template-columns:repeat(auto-fit,minmax(330px,1fr));gap:16px}
          .two .panel{margin-bottom:0}
          .chart{width:100%;height:auto;display:block;overflow:visible}
          .chart.small{max-width:360px}
          .bar{fill:var(--accent);opacity:.85}
          .bar:hover{opacity:1}
          .axis{stroke:var(--rule);stroke-width:1}
          .tick{fill:var(--faint);font-size:11px;text-anchor:middle}
          .area{fill:var(--accent);opacity:.12}
          .line{fill:none;stroke:var(--accent);stroke-width:2;stroke-linejoin:round}
          .mean{fill:none;stroke:var(--good);stroke-width:2;stroke-dasharray:4 3}
          .legend{font-size:13px;color:var(--soft);margin:10px 0 0}
          .k{display:inline-block;width:16px;height:3px;margin:0 6px 0 14px;vertical-align:middle}
          .k.line{background:var(--accent)} .k.mean{background:var(--good)}
          .legend .k:first-child{margin-left:0}
          .scroller{overflow-x:auto}
          table{border-collapse:collapse;width:100%;font-size:14.5px}
          th{text-align:left;font-size:11.5px;text-transform:uppercase;letter-spacing:.06em;
             color:var(--faint);font-weight:600;padding:0 10px 8px 0;white-space:nowrap}
          td{padding:8px 10px 8px 0;border-top:1px solid var(--rule);vertical-align:middle}
          td.n,th.n{text-align:right;font-variant-numeric:tabular-nums;white-space:nowrap}
          td.mono{font-family:ui-monospace,Consolas,monospace;font-size:.9em;color:var(--soft)}
          td.strong{font-weight:600}
          tr.total td{border-top:2px solid var(--ink);font-weight:600}
          .items td.bar{width:26%;padding-right:0}
          .items td.bar span{display:block;height:8px;border-radius:4px;background:var(--accent);opacity:.75;min-width:2px}
          .plain td{border-top:1px solid var(--rule)}
          .swatch{display:inline-block;width:10px;height:10px;border-radius:2px;margin-right:8px;vertical-align:baseline}
          .donut{width:160px;height:160px;display:block;margin:0 auto 12px}
          .donut .middle{text-anchor:middle;font-size:13px;font-weight:600;fill:var(--ink)}
          .heat th{padding:0 4px 6px;text-align:center;font-size:10.5px}
          .heat tbody th{text-align:right;padding-right:8px;vertical-align:middle}
          .heat td{width:34px;height:26px;border:1px solid var(--paper);border-top:1px solid var(--paper);padding:0;
                   border-radius:3px;background:color-mix(in srgb,var(--accent) calc(var(--i)*100%),transparent)}
          .stack{display:flex;height:22px;border-radius:5px;overflow:hidden;margin-bottom:16px;background:var(--rule)}
          .stack span{display:block;height:100%}
          .figures{display:flex;flex-wrap:wrap;gap:22px 34px;margin-top:6px}
          .figure .l{font-size:11.5px;text-transform:uppercase;letter-spacing:.06em;color:var(--faint)}
          .figure .v{font-size:20px;font-weight:600;font-variant-numeric:tabular-nums;line-height:1.3}
          .figure .n{font-size:13px;color:var(--soft)}
          footer{margin-top:56px;padding-top:22px;border-top:2px solid var(--ink);color:var(--faint);font-size:13.5px}
          footer strong{color:var(--soft)}
          @media (max-width:640px){body{padding:0 14px 60px;font-size:15px}.kpi .v{font-size:26px}}
        </style>
        """;
}
