using System.Globalization;
using Pos.Core.Hardware.Printing;

namespace Pos.Core.Domain.Printing;

/// <summary>
/// Lays out the Z-report a lane prints when it stops trading.
/// </summary>
/// <remarks>
/// Ordered for the person holding it. The cash figure comes first and large, because the first
/// thing anyone does with a Z-report is count the drawer against it; everything else is read
/// afterwards, or filed. The reconciliation lines are printed rather than assumed, so a report that
/// does not add up says so on its face instead of waiting to be discovered on a return.
/// </remarks>
public sealed class ZReportComposer(StoreProfile store, int paperWidthChars = ReceiptBuilder.Width80Mm)
{
    private readonly StoreProfile _store = store ?? throw new ArgumentNullException(nameof(store));

    public int PaperWidthChars { get; } = paperWidthChars;

    public ReceiptBuilder Compose(DayCloseSummary day, bool isReprint = false)
    {
        ArgumentNullException.ThrowIfNull(day);

        var report = new ReceiptBuilder(PaperWidthChars);

        report.Text(_store.Name, TextAlignment.Center, bold: true, widthMultiplier: 2, heightMultiplier: 2);

        if (!string.IsNullOrWhiteSpace(_store.Gstin))
            report.Text($"GSTIN: {_store.Gstin}", TextAlignment.Center);

        report.Blank();
        report.Text("DAY-END REPORT (Z)", TextAlignment.Center, bold: true);

        if (isReprint)
            report.Text("** REPRINT **", TextAlignment.Center, bold: true);

        report.Rule();
        report.Columns("Lane", day.LaneId);
        report.Columns("Closed", day.ClosedAt.ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture));
        report.Columns("First sale", day.OpenedAt?.ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture) ?? "—");
        report.Columns("Report no", day.Id > 0 ? day.Id.ToString(CultureInfo.InvariantCulture) : "not saved");
        report.Rule();

        if (day.TookNothing)
        {
            report.Blank();
            report.Text("NO SALES IN THIS PERIOD", TextAlignment.Center, bold: true);
            report.Blank();
            WriteHeldBills(report, day);
            report.Cut();
            return report;
        }

        // What has to be counted, first and large.
        report.Text("CASH IN DRAWER SHOULD BE", TextAlignment.Center);
        report.Text(Amount(day.CashExpected), TextAlignment.Center, bold: true, widthMultiplier: 2, heightMultiplier: 2);
        report.Blank();
        report.Columns("  Cash taken", Amount(day.TotalOf(TenderType.Cash)));
        report.Columns("  Change given", Amount(day.ChangeGiven));
        report.Rule();

        report.Text("Sales", bold: true);
        report.Columns("Invoices", day.InvoiceCount.ToString(CultureInfo.InvariantCulture));
        report.Columns("Gross sales", Amount(day.GrossSales));
        report.Columns("Discounts", Amount(day.TotalDiscount));
        report.Columns("Net sales", Amount(day.NetSales), bold: true);
        report.Rule();

        report.Text("Tax", bold: true);
        report.Columns("Taxable value", Amount(day.TaxableValue));
        report.Columns("CGST", Amount(day.TotalCgst));
        report.Columns("SGST", Amount(day.TotalSgst));

        if (day.TotalIgst > 0m)
            report.Columns("IGST", Amount(day.TotalIgst));

        report.Columns("Total tax", Amount(day.TotalTax));

        if (day.TaxSlabs.Count > 0)
        {
            report.Blank();
            report.Row("Slab", new ColumnValue("Taxable", 12), new ColumnValue("Tax", 10));

            foreach (var slab in day.TaxSlabs)
                report.Row($"{Rate(slab.GstRate)}%", new ColumnValue(Amount(slab.TaxableValue), 12), new ColumnValue(Amount(slab.Tax), 10));
        }

        report.Rule();

        report.Text("Tenders", bold: true);

        foreach (var tender in day.Tenders)
            report.Columns($"{Label(tender.Type)} ({tender.PaymentCount})", Amount(tender.Amount));

        if (day.PointsRedeemed > 0 || day.PointsEarned > 0)
        {
            report.Rule();
            report.Text("Reward points", bold: true);
            report.Columns("Redeemed", day.PointsRedeemed.ToString(CultureInfo.InvariantCulture));
            report.Columns("Earned", day.PointsEarned.ToString(CultureInfo.InvariantCulture));
        }

        WriteReconciliation(report, day);
        WriteHeldBills(report, day);

        report.Cut();
        return report;
    }

    /// <summary>
    /// The checks the report has to satisfy, printed rather than assumed. A day that does not
    /// reconcile is a day somebody has to look at, and the report is where they will look.
    /// </summary>
    private static void WriteReconciliation(ReceiptBuilder report, DayCloseSummary day)
    {
        report.Rule('=');

        var tendered = day.Tenders.Sum(t => t.Amount) - day.ChangeGiven;
        var salesCheck = day.GrossSales - day.TotalDiscount == day.NetSales;
        var taxCheck = day.TaxableValue + day.TotalTax == day.NetSales;
        var tenderCheck = tendered == day.NetSales;

        if (salesCheck && taxCheck && tenderCheck)
        {
            report.Text("Reconciled: sales, tax and tenders all agree.", TextAlignment.Center);
            return;
        }

        report.Text("*** DOES NOT RECONCILE ***", TextAlignment.Center, bold: true);

        if (!salesCheck)
            report.Columns("  gross less discount", Amount(day.GrossSales - day.TotalDiscount));

        if (!taxCheck)
            report.Columns("  taxable plus tax", Amount(day.TaxableValue + day.TotalTax));

        if (!tenderCheck)
            report.Columns("  tenders less change", Amount(tendered));

        report.Columns("  net sales", Amount(day.NetSales));
    }

    private static void WriteHeldBills(ReceiptBuilder report, DayCloseSummary day)
    {
        if (day.HeldBillsOutstanding == 0)
            return;

        report.Blank();
        report.Text($"{day.HeldBillsOutstanding} bill(s) still parked", TextAlignment.Center, bold: true);
        report.Text("These are not sales. Recall or discard them.", TextAlignment.Center);
    }

    private static string Amount(decimal value) => value.ToString("N2", CultureInfo.InvariantCulture);

    private static string Rate(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Label(TenderType type) => type switch
    {
        TenderType.Cash => "Cash",
        TenderType.Card => "Card",
        TenderType.Upi => "UPI",
        TenderType.StoreCredit => "Store credit",
        TenderType.LoyaltyPoints => "Loyalty points",
        _ => type.ToString(),
    };
}
