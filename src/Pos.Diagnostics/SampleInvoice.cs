using Pos.Core.Domain;

namespace Pos.Diagnostics;

/// <summary>
/// A representative invoice for the printer test: two tax slabs, a discount, a weighed line, a
/// split tender with change, and loyalty movement — so a test print exercises every part of the
/// layout rather than only the easy ones.
/// </summary>
internal static class SampleInvoice
{
    public static SettledInvoice Build(string laneId)
    {
        var customer = new Customer
        {
            Id = 1,
            MobileNo = "9876543210",
            Name = "Test Customer",
            StateCode = "33",
            LoyaltyBalance = 412,
        };

        InvoiceLine[] lines =
        [
            Line(1, "Toor Dal 1kg", "0713", "8901234567890", 189m, 5m),
            Line(2, "Sugar Loose", "1701", null, 45m, 5m, quantity: 2.75m, unit: UnitType.Kilogram),
            Line(3, "Shampoo 340ml", "3305", "8901234567897", 299m, 18m, discount: 49m),
            Line(4, "Premium Organic Cold Pressed Groundnut Oil 5 Litre Tin", "1512", "8901234567901", 1_299m, 5m),
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
            RecalledFromToken: "H007");

        return new SettledInvoice(0, $"{laneId}-{DateTimeOffset.Now.Year}-000000", sale);
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
