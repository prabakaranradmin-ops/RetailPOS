using System.Collections.Concurrent;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Domain.Printing;
using Pos.Core.Hardware.Drawer;
using Pos.Core.Hardware.Printing;
using Pos.Core.Loyalty;
using Pos.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Pos.Core.Tests;

/// <summary>
/// The Phase 5 sweep: everything the earlier phases built, exercised together and under load,
/// rather than one unit at a time.
/// </summary>
public class RegressionSweepTests(ITestOutputHelper output) : IDisposable
{
    private const string HomeState = "33";

    private readonly TempDatabase _temp = new();

    public void Dispose() => _temp.Dispose();

    private static readonly StoreProfile Store = new()
    {
        Name = "Sri Lakshmi Stores",
        Gstin = "33AABCS1429B1ZX",
        FooterMessage = "Thank you, please visit again",
    };

    private CheckoutService NewCheckout(IDrawerService drawer, IPrinterService printer) =>
        new(new InvoiceRepository(_temp.Database),
            new CustomerRepository(_temp.Database),
            drawer,
            LoyaltyRules.Default,
            TimeProvider.System,
            printer,
            new ReceiptComposer(Store, printer.PaperWidthChars));

    private void SeedCatalogue() => _temp.Items.AddRange(
    [
        Catalogue.Item(sku: "DAL001", barcode: "8901234567890", name: "Toor Dal 1kg", price: 189m, gstRate: 5m),
        Catalogue.Item(sku: "RICE01", barcode: "8901234567891", name: "Basmati Rice 5kg", price: 649m, gstRate: 5m),
        Catalogue.Item(sku: "SHM001", barcode: "8901234567892", name: "Shampoo 340ml", price: 299m, gstRate: 18m),
        Catalogue.Item(sku: "SUG001", barcode: "8901234567893", name: "Sugar Loose", price: 45m, gstRate: 5m, unit: UnitType.Kilogram),
        Catalogue.Item(sku: "CHO001", barcode: "8901234567894", name: "Chocolate Bar 50g", price: 1.76m, gstRate: 28m),
    ]);

    // ---- One sale, through every phase --------------------------------------------------------

    /// <summary>
    /// A single transaction that touches everything built so far: search, weighed goods, a
    /// discount, a customer, a capped redemption, a split tender with change, persistence,
    /// printing and the drawer — then checks the books reconcile.
    /// </summary>
    [Fact]
    public void OneSaleExercisesEveryPhaseAndTheBooksReconcile()
    {
        SeedCatalogue();

        var drawer = new RecordingDrawerService();
        var printer = new LoopbackPrinterService();
        var checkout = NewCheckout(drawer, printer);
        var customers = new CustomerRepository(_temp.Database);
        var invoices = new InvoiceRepository(_temp.Database);

        var customer = customers.Add(new Customer { MobileNo = "9876543210", Name = "Anitha", StateCode = HomeState, LoyaltyBalance = 900 });

        // Phase 2: find the goods the way a cashier does.
        var bill = new InvoiceEngine(HomeState);
        bill.AddItem(_temp.Items.FindByBarcode("8901234567891")!);
        bill.AddItem(Assert.Single(_temp.Items.Search("Sugar")));
        bill.AddItem(_temp.Items.FindByBarcode("8901234567894")!);

        // Phase 1: weighed quantity, and a discount.
        bill.SetQuantity(1, 2.75m);
        bill.SetDiscount(0, 49m);
        bill.SetCustomer(customer);

        var totals = bill.Totals;

        // The books must add up before anything is taken.
        Assert.Equal(totals.GrandTotal, bill.Lines.Sum(l => l.LineTotal));
        Assert.Equal(totals.SubtotalTaxable + totals.TotalTax, totals.GrandTotal);
        Assert.Equal(0m, totals.TotalIgst);

        // Phase 4: points to the cap, then a split tender that over-pays in cash.
        var redemption = checkout.QuoteRedemption(totals.GrandTotal, customer);
        Assert.True(redemption.Value <= totals.GrandTotal * 0.30m);

        var basket = new TenderBasket(totals.GrandTotal);
        basket.Add(TenderType.LoyaltyPoints, redemption.Value, $"{redemption.Points} points");
        basket.Add(TenderType.Upi, 200.00m, "UPI/2026/1");
        basket.Add(TenderType.Cash, totals.GrandTotal - redemption.Value - 200.00m + 100.00m);

        var result = checkout.Complete("L1", bill, basket, redemption.Points);

        // Phase 4: the sale is on disk, numbered, with everything intact.
        var saved = invoices.FindByInvoiceNo(result.Invoice.InvoiceNo);
        Assert.NotNull(saved);
        Assert.Equal($"INV/{FiscalYear.For(DateTimeOffset.Now).ShortLabel}/L1-1", saved.InvoiceNo);
        Assert.Equal(3, saved.Sale.Lines.Count);
        Assert.Equal(3, saved.Sale.Payments.Count);
        Assert.Equal(totals.GrandTotal, saved.Sale.Totals.GrandTotal);
        Assert.Equal(100.00m, result.ChangeDue);

        // The tax on the saved invoice is what was charged, recomputable from the stored lines.
        Assert.Equal(totals.GrandTotal, InvoiceTotals.From(saved.Sale.Lines).GrandTotal);

        // Phase 4: the balance moved by exactly what was spent and earned.
        Assert.Equal(
            customer.LoyaltyBalance,
            LoyaltyEngine.NewBalance(900, result.PointsRedeemed, result.PointsEarned));
        Assert.Equal(customer.LoyaltyBalance, customers.FindByMobile("9876543210")!.LoyaltyBalance);

        // Phase 3: printed and kicked, and the receipt says what the invoice says.
        Assert.True(result.Print.Succeeded);
        Assert.Equal(DrawerKickResult.Opened, result.Drawer);

        var paper = new ReceiptComposer(Store).Compose(saved).ToPlainText();
        Assert.Contains(saved.InvoiceNo, paper);
        Assert.Contains("33AABCS1429B1ZX", paper);
        Assert.Contains(totals.GrandTotal.ToString("N2"), paper);

        output.WriteLine(paper);
    }

    /// <summary>
    /// Park a bill, serve two more customers, come back for it, and settle. The parked bill must
    /// come back exactly as it was, and must not have taken an invoice number while it waited.
    /// </summary>
    [Fact]
    public void ParkingAndRecallingAcrossOtherSalesKeepsTheNumberingUnbroken()
    {
        SeedCatalogue();

        var held = new HeldBillRepository(_temp.Database);
        var invoices = new InvoiceRepository(_temp.Database);
        var checkout = NewCheckout(new RecordingDrawerService(), new LoopbackPrinterService());

        var parked = new InvoiceEngine(HomeState);
        parked.AddItem(_temp.Items.FindByBarcode("8901234567891")!);
        parked.SetDiscount(0, 49m);
        var parkedTotal = parked.Totals.GrandTotal;

        var token = held.NextToken("L1");
        held.Park("L1", token, DateTimeOffset.Now, null, parked.SnapshotLines());

        // Two other customers go through while it waits.
        for (var i = 0; i < 2; i++)
        {
            var bill = new InvoiceEngine(HomeState);
            bill.AddItem(_temp.Items.FindByBarcode("8901234567890")!);

            var basket = new TenderBasket(bill.Totals.GrandTotal);
            basket.Add(TenderType.Cash, bill.Totals.GrandTotal);
            checkout.Complete("L1", bill, basket);
        }

        // Now the parked bill comes back.
        var recalled = held.Recall("L1", token);
        Assert.NotNull(recalled);

        var resumed = new InvoiceEngine(HomeState);
        resumed.Restore(recalled.Lines, recalled.Customer);

        Assert.Equal(parkedTotal, resumed.Totals.GrandTotal);
        Assert.Equal(49m, resumed.Lines[0].Discount);

        var finalBasket = new TenderBasket(resumed.Totals.GrandTotal);
        finalBasket.Add(TenderType.Card, resumed.Totals.GrandTotal, "AUTH 1");
        var settled = checkout.Complete("L1", resumed, finalBasket, recalledFromToken: token);

        // Three sales, numbered one two three with no hole where the parked bill waited.
        Assert.Equal($"INV/{FiscalYear.For(DateTimeOffset.Now).ShortLabel}/L1-3", settled.Invoice.InvoiceNo);
        Assert.Equal(token, invoices.FindByInvoiceNo(settled.Invoice.InvoiceNo)!.Sale.RecalledFromToken);
        Assert.Empty(held.List("L1"));
    }

    /// <summary>
    /// The three headline figures on any invoice must add up: taxable value plus tax is the total.
    /// This sweeps a wide spread of bills, because the case that breaks it is not obvious — line
    /// totals are rounded to paise individually, so summing the rounded lines and rounding the
    /// summed parts do not always agree.
    /// </summary>
    [Fact]
    public void TaxableValuePlusTaxIsTheGrandTotalOnEveryBill()
    {
        SeedCatalogue();

        var barcodes = new[] { "8901234567890", "8901234567891", "8901234567892", "8901234567893", "8901234567894" };
        var random = new Random(20260826);
        var checked_ = 0;

        for (var trial = 0; trial < 400; trial++)
        {
            var bill = new InvoiceEngine(HomeState);

            for (var line = 0; line < 1 + random.Next(5); line++)
            {
                var item = _temp.Items.FindByBarcode(barcodes[random.Next(barcodes.Length)])!;
                var added = bill.AddItem(item);

                added.Quantity = item.UnitType.AllowsFractionalQuantity()
                    ? Math.Round(0.05m + (decimal)random.NextDouble() * 3m, 3)
                    : 1 + random.Next(9);

                if (random.Next(3) == 0)
                    added.Discount = Math.Round(added.Quantity * added.UnitPrice * (decimal)random.NextDouble() * 0.4m, 2);
            }

            if (random.Next(4) == 0)
                bill.SetCustomer(new Customer { MobileNo = "9000000000", StateCode = "29" });

            var totals = bill.Totals;

            Assert.Equal(totals.GrandTotal, totals.SubtotalTaxable + totals.TotalTax);
            Assert.Equal(totals.GrandTotal, bill.Lines.Sum(l => l.LineTotal));
            Assert.True(totals.SubtotalTaxable >= 0m);
            checked_++;
        }

        output.WriteLine($"{checked_} randomly built bills all reconcile");
    }

    // ---- Under load ---------------------------------------------------------------------------

    /// <summary>
    /// Several lanes billing at once against their own connections, settling complete sales rather
    /// than only writing rows. Every number distinct, every lane's run unbroken, the books adding
    /// up, and the file still healthy afterwards.
    /// </summary>
    [Fact]
    public void ManyLanesBillingAtOnceKeepTheirNumberingAndTheirBooks()
    {
        SeedCatalogue();

        string[] lanes = ["L1", "L2", "L3", "COUNTER-A"];
        const int salesPerLane = 25;

        var numbers = new ConcurrentBag<string>();
        var failures = new ConcurrentBag<string>();

        Parallel.ForEach(lanes, lane =>
        {
            var items = new ItemRepository(_temp.Database);
            var checkout = NewCheckout(new RecordingDrawerService(), new LoopbackPrinterService());

            for (var i = 0; i < salesPerLane; i++)
            {
                try
                {
                    var bill = new InvoiceEngine(HomeState);
                    bill.AddItem(items.FindByBarcode("8901234567890")!);
                    bill.AddItem(items.FindByBarcode("8901234567894")!);
                    bill.SetQuantity(1, 3m);

                    var basket = new TenderBasket(bill.Totals.GrandTotal);
                    basket.Add(TenderType.Cash, bill.Totals.GrandTotal);

                    numbers.Add(checkout.Complete(lane, bill, basket).Invoice.InvoiceNo);
                }
                catch (Exception ex)
                {
                    failures.Add($"{lane}: {ex.Message}");
                }
            }
        });

        Assert.Empty(failures);
        Assert.Equal(lanes.Length * salesPerLane, numbers.Count);
        Assert.Equal(lanes.Length * salesPerLane, numbers.Distinct().Count());

        // Each lane's own run is consecutive with no holes.
        foreach (var lane in lanes)
        {
            // The lane segment sits between the financial year and the sequence: INV/26-27/L1-7.
            var sequences = numbers
                .Where(n => n.Contains($"/{lane}-", StringComparison.Ordinal))
                .Select(n => int.Parse(n.Split('-')[^1]))
                .OrderBy(n => n)
                .ToList();

            Assert.Equal(Enumerable.Range(1, salesPerLane), sequences);
        }

        // Every invoice reads back, and its stored lines still reproduce its total.
        var invoices = new InvoiceRepository(_temp.Database);

        foreach (var number in numbers)
        {
            var saved = invoices.FindByInvoiceNo(number);
            Assert.NotNull(saved);
            Assert.Equal(saved.Sale.Totals.GrandTotal, InvoiceTotals.From(saved.Sale.Lines).GrandTotal);
        }

        var report = _temp.Database.CheckIntegrity();
        Assert.True(report.IsHealthy, report.ToString());

        output.WriteLine($"{numbers.Count} sales across {lanes.Length} lanes, database {report}");
    }

    /// <summary>
    /// Parking and recalling from several lanes at once. A parked bill belongs to one lane, and no
    /// bill may be recalled twice however the timing falls.
    /// </summary>
    [Fact]
    public void ParkedBillsAreNeverRecalledTwiceUnderConcurrency()
    {
        SeedCatalogue();

        var held = new HeldBillRepository(_temp.Database);
        var item = _temp.Items.FindByBarcode("8901234567890")!;

        var lines = new InvoiceEngine(HomeState);
        lines.AddItem(item);

        const int parked = 40;

        for (var i = 1; i <= parked; i++)
            held.Park("L1", $"H{i:D3}", DateTimeOffset.Now.AddMinutes(-i), null, lines.SnapshotLines());

        Assert.Equal(parked, held.List("L1").Count);

        var recalled = new ConcurrentBag<string>();

        // Twice as many attempts as there are bills, all racing for the same tokens.
        Parallel.For(0, parked * 2, i =>
        {
            var store = new HeldBillRepository(_temp.Database);
            var token = $"H{(i % parked) + 1:D3}";

            if (store.Recall("L1", token) is not null)
                recalled.Add(token);
        });

        Assert.Equal(parked, recalled.Count);
        Assert.Equal(parked, recalled.Distinct().Count());
        Assert.Empty(held.List("L1"));
    }

    /// <summary>
    /// A long run of sales at the awkward prices — the ones with an odd paisa in the tax split —
    /// must leave the ledger adding up exactly, with no drift accumulated across the day.
    /// </summary>
    [Fact]
    public void ALongRunOfAwkwardPricesLeavesNoDrift()
    {
        SeedCatalogue();

        var checkout = NewCheckout(new RecordingDrawerService(), new LoopbackPrinterService());
        var invoices = new InvoiceRepository(_temp.Database);
        var chocolate = _temp.Items.FindByBarcode("8901234567894")!;  // 1.76 at 28%, the drift case

        decimal expectedTakings = 0m;

        for (var quantity = 1; quantity <= 60; quantity++)
        {
            var bill = new InvoiceEngine(HomeState);
            bill.AddItem(chocolate);
            bill.SetQuantity(0, quantity);

            var totals = bill.Totals;
            var basket = new TenderBasket(totals.GrandTotal);
            basket.Add(TenderType.Cash, totals.GrandTotal);

            checkout.Complete("L1", bill, basket);
            expectedTakings += totals.GrandTotal;
        }

        using var connection = _temp.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT SUM(CAST(grand_total AS REAL)),
                   SUM(CAST(subtotal_taxable AS REAL) + CAST(total_cgst AS REAL) + CAST(total_sgst AS REAL) + CAST(total_igst AS REAL))
            FROM invoices;
            """;

        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());

        var takings = (decimal)reader.GetDouble(0);
        var fromParts = (decimal)reader.GetDouble(1);

        Assert.Equal(expectedTakings, Math.Round(takings, 2));

        // Taxable plus tax reconstructs the total banked, to the paisa, across every invoice.
        Assert.Equal(Math.Round(takings, 2), Math.Round(fromParts, 2));

        output.WriteLine($"60 invoices, {expectedTakings:N2} taken, parts reconcile to {fromParts:N2}");
    }
}
