using Pos.Core.Domain;

namespace Pos.Diagnostics;

/// <summary>
/// A representative invoice for the printer test: two tax slabs, a discount, a weighed line, a
/// split tender with change, and loyalty movement — so a test print exercises every part of the
/// layout rather than only the easy ones.
/// </summary>
internal static class SampleInvoice
{
    /// <param name="taxMode">
    /// What the lane issues. A composition lane must preview the bill of supply it will actually
    /// print — previewing a tax invoice would show the shopkeeper a document they never issue, and
    /// this preview is how they check the bill before the shop opens.
    /// </param>
    public static SettledInvoice Build(
        string laneId,
        InvoiceNumberFormat? numberFormat = null,
        TaxMode taxMode = TaxMode.Gst)
    {
        var customer = new Customer
        {
            Id = 1,
            MobileNo = "9876543210",
            Name = "Test Customer",
            StateCode = "33",
            LoyaltyBalance = 412,
        };

        // On a composition lane the rates go to zero here, the same way the till drops them when it
        // makes a real line — so the preview's totals are the ones the shop will actually take.
        var rate = taxMode == TaxMode.Composition ? 0m : 1m;

        InvoiceLine[] lines =
        [
            Line(1, "Toor Dal 1kg", "0713", "8901234567890", 189m, 5m * rate),
            Line(2, "Sugar Loose", "1701", null, 45m, 5m * rate, quantity: 2.75m, unit: UnitType.Kilogram),
            Line(3, "Shampoo 340ml", "3305", "8901234567897", 299m, 18m * rate, discount: 49m),
            Line(4, "Premium Organic Cold Pressed Groundnut Oil 5 Litre Tin", "1512", "8901234567901", 1_299m, 5m * rate),
        ];

        var totals = InvoiceTotals.From(lines);

        Tender[] payments =
        [
            new(TenderType.LoyaltyPoints, 100.00m, "200 points"),
            new(TenderType.Card, 1_000.00m, "AUTH 55123"),
            new(TenderType.Cash, 600.00m),
        ];

        var sale = new SaleDraft(
            laneId,
            DateTimeOffset.Now,
            customer,
            lines,
            totals,
            payments,
            ChangeDue: Math.Max(0m, payments.Sum(p => p.Amount) - totals.GrandTotal),
            PointsRedeemed: 200,
            PointsEarned: 32,
            RecalledFromToken: "H007",
            CashierName: null,
            TaxMode: taxMode);

        // Numbered the way this lane actually numbers, so a test print shows the shop what its own
        // bill numbers will look like rather than a placeholder in some other shape.
        var format = numberFormat ?? InvoiceNumberFormat.Default;
        var number = format.Format(laneId, FiscalYear.For(DateTimeOffset.Now), 1);

        return new SettledInvoice(0, number, sale);
    }

    private static InvoiceLine Line(
        long id,
        string name,
        string hsn,
        string? barcode,
        decimal price,
        decimal gst,
        decimal quantity = 1m,
        decimal discount = 0m,
        UnitType unit = UnitType.Each) =>
        InvoiceLine.Rehydrate(id, name, hsn, barcode, null, unit, price, price, true, gst, quantity, discount, false);
}
