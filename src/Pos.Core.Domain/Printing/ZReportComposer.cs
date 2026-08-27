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
public sealed class ZReportComposer
{
    private readonly StoreProfile _store;
    private readonly TaxMode _taxMode;

    /// <param name="taxMode">
    /// What this lane issues. A composition lane's day-end report has no tax section — it collected
    /// none and may not.
    /// </param>
    public ZReportComposer(
        StoreProfile store,
        int paperWidthChars = ReceiptBuilder.Width80Mm,
        ReceiptLanguage language = ReceiptLanguage.English,
        TaxMode taxMode = TaxMode.Gst)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _taxMode = taxMode;
        PaperWidthChars = paperWidthChars;
        Language = language;
        Labels = ReceiptLabels.For(language);
    }

    public int PaperWidthChars { get; }

    public ReceiptLanguage Language { get; }

    /// <summary>
    /// The same phrasebook the receipt uses. A lane that printed its bills in Tamil and its day-end
    /// report in English would be calling the same figures two different things.
    /// </summary>
    public ReceiptLabels Labels { get; }

    /// <param name="lowStock">
    /// What needs reordering, printed at the foot so the shop has the list on paper without anyone
    /// running a command. Supplied only when a day is being closed — never on a reprint, because a
    /// report pulled out months later would otherwise carry today's shelves under last spring's
    /// takings.
    /// </param>
    /// <summary>
    /// What to reorder, at the foot of the day's report.
    /// </summary>
    /// <remarks>
    /// Capped, and deliberately short. The point is a list somebody can act on before opening
    /// tomorrow, not an inventory printout — a shop whose whole catalogue has fallen below its
    /// reorder levels has a problem no length of till roll is going to solve.
    /// </remarks>
    private void WriteLowStock(ReceiptBuilder report, IReadOnlyList<StockLevel>? lowStock)
    {
        const int most = 15;

        if (lowStock is null || lowStock.Count == 0)
            return;

        report.Rule();
        report.Text(Labels.LowStock, bold: true);

        foreach (var level in lowStock.Take(most))
        {
            report.Columns(
                level.Name.Length > 26 ? level.Name[..25] + "…" : level.Name,
                $"{Quantity(level.Quantity)} / {Quantity(level.ReorderLevel ?? 0m)}");
        }

        if (lowStock.Count > most)
            report.Text($"  ... and {lowStock.Count - most} more");
    }

    private static string Quantity(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    public ReceiptBuilder Compose(
        DayCloseSummary day,
        bool isReprint = false,
        IReadOnlyList<StockLevel>? lowStock = null)
    {
        ArgumentNullException.ThrowIfNull(day);

        var report = new ReceiptBuilder(PaperWidthChars);

        report.Text(_store.Name, TextAlignment.Center, bold: true, widthMultiplier: 2, heightMultiplier: 2);

        if (!string.IsNullOrWhiteSpace(_store.Gstin))
            report.Text($"GSTIN {_store.Gstin}", TextAlignment.Center);

        report.Blank();
        report.Text(Labels.DayEndReport, TextAlignment.Center, bold: true);

        if (isReprint)
            report.Text(Labels.Reprint, TextAlignment.Center, bold: true);

        report.Rule();
        report.Columns(Labels.Lane, day.LaneId);
        report.Columns(Labels.Closed, day.ClosedAt.ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture));
        report.Columns(Labels.FirstSale, day.OpenedAt?.ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture) ?? "—");
        report.Columns(Labels.ReportNumber, day.Id > 0 ? day.Id.ToString(CultureInfo.InvariantCulture) : "—");
        report.Rule();

        if (day.TookNothing)
        {
            report.Blank();
            report.Text(Labels.NoSalesInThisPeriod, TextAlignment.Center, bold: true);
            report.Blank();
            WriteHeldBills(report, day);
            report.Cut();
            return report;
        }

        // What has to be counted, first and large.
        report.Text(Labels.CashInDrawerShouldBe, TextAlignment.Center);
        report.Text(Amount(day.CashExpected), TextAlignment.Center, bold: true, widthMultiplier: 2, heightMultiplier: 2);
        report.Blank();
        report.Columns($"  {Labels.CashTaken}", Amount(day.TotalOf(TenderType.Cash)));
        report.Columns($"  {Labels.ChangeGiven}", Amount(day.ChangeGiven));
        report.Rule();

        report.Text(Labels.Sales, bold: true);
        report.Columns(Labels.Invoices, day.InvoiceCount.ToString(CultureInfo.InvariantCulture));
        report.Columns(Labels.GrossSales, Amount(day.GrossSales));
        report.Columns(Labels.Discount, Amount(day.TotalDiscount));
        report.Columns(Labels.NetSales, Amount(day.NetSales), bold: true);
        report.Rule();

        // A composition lane collected no tax and is not permitted to. The whole block goes, rather
        // than printing a column of zeroes and a "0%" slab against the day's entire takings — which
        // would read as a shop that applied a nil rate to taxable supplies.
        if (_taxMode == TaxMode.Composition)
        {
            report.Columns(Labels.Subtotal, Amount(day.TaxableValue), bold: true);
            report.Rule();
        }
        else
        {
            report.Text(Labels.Tax, bold: true);
            report.Columns(Labels.TaxableValue, Amount(day.TaxableValue));
            report.Columns(Labels.Cgst, Amount(day.TotalCgst));
            report.Columns(Labels.Sgst, Amount(day.TotalSgst));

            if (day.TotalIgst > 0m)
                report.Columns(Labels.Igst, Amount(day.TotalIgst));

            report.Columns(Labels.TotalTax, Amount(day.TotalTax));

            if (day.TaxSlabs.Count > 0)
            {
                report.Blank();
                report.Row(Labels.TaxSummaryRate, new ColumnValue(Labels.TaxSummaryTaxable, 12), new ColumnValue(Labels.TaxSummaryTax, 10));

                foreach (var slab in day.TaxSlabs)
                    report.Row($"{Rate(slab.GstRate)}%", new ColumnValue(Amount(slab.TaxableValue), 12), new ColumnValue(Amount(slab.Tax), 10));
            }

            report.Rule();
        }

        report.Text(Labels.Tenders, bold: true);

        foreach (var tender in day.Tenders)
            report.Columns($"{Label(tender.Type)} ({tender.PaymentCount})", Amount(tender.Amount));

        if (day.PointsRedeemed > 0 || day.PointsEarned > 0)
        {
            report.Rule();
            report.Text(Labels.RewardPoints, bold: true);
            report.Columns(Labels.Redeemed, day.PointsRedeemed.ToString(CultureInfo.InvariantCulture));
            report.Columns(Labels.Earned, day.PointsEarned.ToString(CultureInfo.InvariantCulture));
        }

        WriteVoids(report, day);
        WriteCashiers(report, day);

        WriteReconciliation(report, day);
        WriteHeldBills(report, day);
        WriteLowStock(report, lowStock);

        report.Cut();
        return report;
    }

    /// <summary>
    /// Voided sales, on their own line.
    /// </summary>
    /// <remarks>
    /// They are not takings and carry no tax, so they are nowhere in the figures above. But a
    /// report that simply omits them cannot be reconciled against the invoice run — the numbers
    /// would have gaps with no explanation on the page. Printing the count and value is what lets
    /// somebody tie the two views together.
    /// </remarks>
    private void WriteVoids(ReceiptBuilder report, DayCloseSummary day)
    {
        if (day.VoidedCount == 0)
            return;

        report.Rule();
        report.Text(Labels.Voided, bold: true);
        report.Columns(Labels.InvoicesVoided, day.VoidedCount.ToString(CultureInfo.InvariantCulture));
        report.Columns(Labels.ValueVoided, Amount(day.VoidedValue));
        report.Text(Labels.VoidsExcludedNote);
    }

    /// <summary>
    /// Who traded, and how much cash each of them holds. This is what turns "the drawer is 500
    /// short" from an unanswerable question into a shift to ask about.
    /// </summary>
    private void WriteCashiers(ReceiptBuilder report, DayCloseSummary day)
    {
        var cashiers = day.CashierTotals;

        // With one person on the till all day this says nothing the rest of the report does not.
        if (cashiers.Count <= 1)
            return;

        report.Rule();
        report.Text(Labels.ByCashier, bold: true);
        report.Row(Labels.CashierName, new ColumnValue(Labels.Sales, 11), new ColumnValue(Labels.CashHeld, 11));

        foreach (var cashier in cashiers)
            report.Row(cashier.Label, new ColumnValue(Amount(cashier.NetSales), 11), new ColumnValue(Amount(cashier.CashHeld), 11));
    }

    /// <summary>
    /// The checks the report has to satisfy, printed rather than assumed. A day that does not
    /// reconcile is a day somebody has to look at, and the report is where they will look.
    /// </summary>
    private void WriteReconciliation(ReceiptBuilder report, DayCloseSummary day)
    {
        report.Rule('=');

        var tendered = day.Tenders.Sum(t => t.Amount) - day.ChangeGiven;
        var salesCheck = day.GrossSales - day.TotalDiscount == day.NetSales;
        var taxCheck = day.TaxableValue + day.TotalTax == day.NetSales;
        var tenderCheck = tendered == day.NetSales;

        if (salesCheck && taxCheck && tenderCheck)
        {
            report.Text(Labels.Reconciled, TextAlignment.Center);
            return;
        }

        report.Text(Labels.DoesNotReconcile, TextAlignment.Center, bold: true);

        if (!salesCheck)
            report.Columns($"  {Labels.GrossLessDiscount}", Amount(day.GrossSales - day.TotalDiscount));

        if (!taxCheck)
            report.Columns($"  {Labels.TaxablePlusTax}", Amount(day.TaxableValue + day.TotalTax));

        if (!tenderCheck)
            report.Columns($"  {Labels.TendersLessChange}", Amount(tendered));

        report.Columns($"  {Labels.NetSales}", Amount(day.NetSales));
    }

    private void WriteHeldBills(ReceiptBuilder report, DayCloseSummary day)
    {
        if (day.HeldBillsOutstanding == 0)
            return;

        report.Blank();
        report.Text($"{day.HeldBillsOutstanding} {Labels.BillsStillParked}", TextAlignment.Center, bold: true);
        report.Text(Labels.ParkedBillsNote, TextAlignment.Center);
    }

    private static string Amount(decimal value) => value.ToString("N2", CultureInfo.InvariantCulture);

    private static string Rate(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private string Label(TenderType type) => type switch
    {
        TenderType.Cash => Labels.Cash,
        TenderType.Card => Labels.Card,
        TenderType.Upi => Labels.Upi,
        TenderType.StoreCredit => Labels.Credit,
        TenderType.LoyaltyPoints => Labels.LoyaltyPoints,
        _ => type.ToString(),
    };
}
