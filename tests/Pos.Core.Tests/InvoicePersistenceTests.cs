using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// Invoice numbering and storage: ARCHITECTURE.md section 6 for the lane-prefixed number, and the
/// requirement that a saved invoice comes back exactly as it was rung up.
/// </summary>
public class InvoicePersistenceTests : IDisposable
{
    private const string HomeState = "33";

    private readonly TempDatabase _temp = new();

    private InvoiceRepository Invoices => new(_temp.Database);

    public void Dispose() => _temp.Dispose();

    private static SaleDraft Sale(
        string lane = "L1",
        int year = 2026,
        Customer? customer = null,
        string? recalledFrom = null,
        params InvoiceLine[] lines)
    {
        if (lines.Length == 0)
            lines = [Line("Toor Dal 1kg", 189m, 5m)];

        var totals = InvoiceTotals.From(lines);

        return new SaleDraft(
            lane,
            new DateTimeOffset(year, 8, 25, 10, 30, 0, TimeSpan.FromHours(5.5)),
            customer,
            lines,
            totals,
            [new Tender(TenderType.Cash, totals.GrandTotal)],
            ChangeDue: 0m,
            PointsRedeemed: 0,
            PointsEarned: 0,
            RecalledFromToken: recalledFrom);
    }

    private static InvoiceLine Line(string name, decimal price, decimal gst, decimal quantity = 1m, decimal discount = 0m, UnitType unit = UnitType.Each) =>
        InvoiceLine.Rehydrate(1, name, "0713", "8901234567890", null, unit, price, price, true, gst, quantity, discount, false);

    // ---- Numbering ---------------------------------------------------------------------------

    [Fact]
    public void TheFirstInvoiceOnALaneIsNumberOne()
    {
        var saved = Invoices.Save(Sale());

        Assert.Equal("INV/26-27/L1-1", saved.InvoiceNo);
    }

    [Fact]
    public void TheShopsOwnPrefixAndFormatAreUsed()
    {
        var repository = new InvoiceRepository(_temp.Database, new InvoiceNumberFormat
        {
            StorePrefix = "RM",
            IncludeLaneSegment = false,
        });

        Assert.Equal("RM/26-27/1", repository.Save(Sale()).InvoiceNo);
        Assert.Equal("RM/26-27/2", repository.Save(Sale()).InvoiceNo);
    }

    [Fact]
    public void TheSequenceCanBePaddedForAFixedWidthNumber()
    {
        var repository = new InvoiceRepository(_temp.Database, new InvoiceNumberFormat
        {
            StorePrefix = "RM",
            IncludeLaneSegment = false,
            SequencePadding = 5,
        });

        Assert.Equal("RM/26-27/00001", repository.Save(Sale()).InvoiceNo);
    }

    [Fact]
    public void NumbersRunConsecutivelyWithinALaneAndYear()
    {
        var numbers = Enumerable.Range(0, 5).Select(_ => Invoices.Save(Sale()).InvoiceNo).ToList();

        Assert.Equal(
            ["INV/26-27/L1-1", "INV/26-27/L1-2", "INV/26-27/L1-3", "INV/26-27/L1-4", "INV/26-27/L1-5"],
            numbers);
    }

    [Fact]
    public void EachLaneKeepsItsOwnSequence()
    {
        Assert.Equal("INV/26-27/L1-1", Invoices.Save(Sale(lane: "L1")).InvoiceNo);
        Assert.Equal("INV/26-27/L2-1", Invoices.Save(Sale(lane: "L2")).InvoiceNo);
        Assert.Equal("INV/26-27/L1-2", Invoices.Save(Sale(lane: "L1")).InvoiceNo);
        Assert.Equal("INV/26-27/L2-2", Invoices.Save(Sale(lane: "L2")).InvoiceNo);
    }

    [Fact]
    public void TheSequenceRestartsInANewFinancialYear()
    {
        Assert.Equal("INV/26-27/L1-1", Invoices.Save(Sale(year: 2026)).InvoiceNo);
        Assert.Equal("INV/27-28/L1-1", Invoices.Save(Sale(year: 2027)).InvoiceNo);
        Assert.Equal("INV/26-27/L1-2", Invoices.Save(Sale(year: 2026)).InvoiceNo);
    }

    /// <summary>
    /// The year that matters is the one the return is filed for. A bill on 31 March and a bill the
    /// next morning belong to different financial years despite being a day apart, and two bills
    /// eleven months apart in the same financial year share a sequence.
    /// </summary>
    [Fact]
    public void TheYearTurnsOverInAprilNotInJanuary()
    {
        var march = Sale() with { CreatedAt = new DateTimeOffset(2027, 3, 31, 20, 0, 0, TimeSpan.FromHours(5.5)) };
        var april = Sale() with { CreatedAt = new DateTimeOffset(2027, 4, 1, 9, 0, 0, TimeSpan.FromHours(5.5)) };
        var january = Sale() with { CreatedAt = new DateTimeOffset(2028, 1, 15, 9, 0, 0, TimeSpan.FromHours(5.5)) };

        Assert.Equal("INV/26-27/L1-1", Invoices.Save(march).InvoiceNo);
        Assert.Equal("INV/27-28/L1-1", Invoices.Save(april).InvoiceNo);

        // Mid-January is still the financial year that opened the previous April.
        Assert.Equal("INV/27-28/L1-2", Invoices.Save(january).InvoiceNo);
    }

    /// <summary>
    /// The Phase 5 gate, provable now: lanes numbering independently cannot collide, because the
    /// lane id is inside the number. No coordinating service is involved.
    /// </summary>
    [Fact]
    public void NumbersFromManyLanesNumberingAtOnceAreAllDistinct()
    {
        string[] lanes = ["L1", "L2", "L3", "COUNTER-A", "COUNTER-B"];
        const int perLane = 40;

        var numbers = new System.Collections.Concurrent.ConcurrentBag<string>();

        Parallel.ForEach(lanes, lane =>
        {
            var repository = new InvoiceRepository(_temp.Database);

            for (var i = 0; i < perLane; i++)
                numbers.Add(repository.Save(Sale(lane: lane)).InvoiceNo);
        });

        Assert.Equal(lanes.Length * perLane, numbers.Count);
        Assert.Equal(lanes.Length * perLane, numbers.Distinct().Count());
    }

    /// <summary>
    /// Two threads billing on the same lane must not be handed the same number. This is the case
    /// the IMMEDIATE transaction exists for.
    /// </summary>
    [Fact]
    public void ConcurrentSalesOnOneLaneNeverShareANumber()
    {
        const int sales = 60;
        var numbers = new System.Collections.Concurrent.ConcurrentBag<string>();

        Parallel.For(0, sales, _ =>
        {
            var repository = new InvoiceRepository(_temp.Database);
            numbers.Add(repository.Save(Sale(lane: "L1")).InvoiceNo);
        });

        Assert.Equal(sales, numbers.Distinct().Count());

        // Consecutive, with no holes: a sequence that skips numbers is a compliance problem.
        var sequences = numbers.Select(n => int.Parse(n.Split('-')[^1])).OrderBy(n => n).ToList();
        Assert.Equal(Enumerable.Range(1, sales), sequences);
    }

    [Fact]
    public void AnInvoiceWithNoLinesIsRefusedAndBurnsNoNumber()
    {
        var empty = Sale() with { Lines = [] };

        Assert.Throws<InvalidOperationException>(() => Invoices.Save(empty));

        // The next real sale still gets number one, so the failure consumed nothing.
        Assert.Equal("INV/26-27/L1-1", Invoices.Save(Sale()).InvoiceNo);
    }

    // ---- Round trip --------------------------------------------------------------------------

    [Fact]
    public void ASavedInvoiceComesBackExactlyAsItWasRungUp()
    {
        var lines = new[]
        {
            Line("Toor Dal 1kg", 189m, 5m),
            Line("Sugar Loose", 45m, 5m, quantity: 2.75m, unit: UnitType.Kilogram),
            Line("Shampoo 340ml", 299m, 18m, discount: 49m),
        };

        var original = Sale(lines: lines);
        var saved = Invoices.Save(original);

        var reloaded = Invoices.FindByInvoiceNo(saved.InvoiceNo);

        Assert.NotNull(reloaded);
        Assert.Equal(saved.InvoiceNo, reloaded.InvoiceNo);
        Assert.Equal(original.Totals.GrandTotal, reloaded.Sale.Totals.GrandTotal);
        Assert.Equal(original.Totals.SubtotalTaxable, reloaded.Sale.Totals.SubtotalTaxable);
        Assert.Equal(original.Totals.TotalCgst, reloaded.Sale.Totals.TotalCgst);
        Assert.Equal(original.Totals.TotalSgst, reloaded.Sale.Totals.TotalSgst);
        Assert.Equal(3, reloaded.Sale.Lines.Count);

        Assert.Equal("Sugar Loose", reloaded.Sale.Lines[1].NameSnapshot);
        Assert.Equal(2.75m, reloaded.Sale.Lines[1].Quantity);
        Assert.Equal(UnitType.Kilogram, reloaded.Sale.Lines[1].Unit);
        Assert.Equal(49m, reloaded.Sale.Lines[2].Discount);

        // Recomputed tax on the reloaded lines matches what was charged.
        Assert.Equal(original.Totals.GrandTotal, InvoiceTotals.From(reloaded.Sale.Lines).GrandTotal);
    }

    [Fact]
    public void LinesComeBackInTheOrderTheyWereRungUp()
    {
        var lines = Enumerable.Range(1, 8)
            .Select(i => Line($"Item {i}", 10m * i, 5m))
            .ToArray();

        var saved = Invoices.Save(Sale(lines: lines));
        var reloaded = Invoices.FindByInvoiceNo(saved.InvoiceNo)!;

        Assert.Equal(
            lines.Select(l => l.NameSnapshot),
            reloaded.Sale.Lines.Select(l => l.NameSnapshot));
    }

    [Fact]
    public void EveryPaymentIsStoredWithItsReference()
    {
        var sale = Sale() with
        {
            Payments =
            [
                new Tender(TenderType.LoyaltyPoints, 30.00m, "60 points"),
                new Tender(TenderType.Card, 100.00m, "AUTH 55123"),
                new Tender(TenderType.Cash, 59.00m),
            ],
            ChangeDue = 0m,
            PointsRedeemed = 60,
            PointsEarned = 3,
        };

        var saved = Invoices.Save(sale);
        var reloaded = Invoices.FindByInvoiceNo(saved.InvoiceNo)!;

        Assert.Equal(3, reloaded.Sale.Payments.Count);
        Assert.Equal(TenderType.LoyaltyPoints, reloaded.Sale.Payments[0].Type);
        Assert.Equal("60 points", reloaded.Sale.Payments[0].ReferenceNo);
        Assert.Equal("AUTH 55123", reloaded.Sale.Payments[1].ReferenceNo);
        Assert.Null(reloaded.Sale.Payments[2].ReferenceNo);
        Assert.Equal(60, reloaded.Sale.PointsRedeemed);
        Assert.Equal(3, reloaded.Sale.PointsEarned);
    }

    [Fact]
    public void TheCustomerAndTheHoldTokenSurviveTheRoundTrip()
    {
        var customers = new CustomerRepository(_temp.Database);
        var customer = customers.Add(new Customer { MobileNo = "9876543210", Name = "Anitha", StateCode = HomeState, LoyaltyBalance = 120 });

        var saved = Invoices.Save(Sale(customer: customer, recalledFrom: "H003"));
        var reloaded = Invoices.FindByInvoiceNo(saved.InvoiceNo)!;

        Assert.Equal("9876543210", reloaded.Sale.Customer!.MobileNo);
        Assert.Equal("Anitha", reloaded.Sale.Customer.Name);
        Assert.Equal(120, reloaded.Sale.Customer.LoyaltyBalance);
        Assert.Equal("H003", reloaded.Sale.RecalledFromToken);
    }

    [Fact]
    public void ChangeGivenIsRecorded()
    {
        var sale = Sale() with
        {
            Payments = [new Tender(TenderType.Cash, 200.00m)],
            ChangeDue = 11.00m,
        };

        var saved = Invoices.Save(sale);

        Assert.Equal(11.00m, Invoices.FindByInvoiceNo(saved.InvoiceNo)!.Sale.ChangeDue);
    }

    [Fact]
    public void LookingUpAnInvoiceThatDoesNotExistReturnsNothing()
    {
        Assert.Null(Invoices.FindByInvoiceNo("INV/26-27/L9-404"));
        Assert.Null(Invoices.FindByInvoiceNo(""));
    }

    /// <summary>
    /// The tax charged is stored on the line, not re-derived on read. A reprint has to show the
    /// tax that was actually charged even if the engine's rules move on afterwards.
    /// </summary>
    [Fact]
    public void TheTaxChargedIsStoredOnTheLineRatherThanRecomputed()
    {
        var saved = Invoices.Save(Sale(lines: [Line("Chocolate Bar", 1.76m, 28m)]));

        using var connection = _temp.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT taxable_value, cgst_amount, sgst_amount, igst_amount, line_total
            FROM invoice_lines WHERE invoice_id = $id;
            """;
        command.Parameters.AddWithValue("$id", saved.Id);

        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());

        Assert.Equal(1.3750m, reader.GetDecimal(0));
        Assert.Equal(0.19m, reader.GetDecimal(1));
        Assert.Equal(0.19m, reader.GetDecimal(2));
        Assert.Equal(0m, reader.GetDecimal(3));
        Assert.Equal(1.76m, reader.GetDecimal(4));
    }
}
