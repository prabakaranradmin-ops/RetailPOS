using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Domain.Printing;
using Pos.Core.Hardware.Printing;
using Pos.Core.Loyalty;
using Pos.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Pos.Core.Tests;

/// <summary>
/// Day-end close: the Z-report a lane produces when it stops trading, and the figures a cashier
/// counts the drawer against.
/// </summary>
public class DayCloseTests(ITestOutputHelper output) : IDisposable
{
    private const string Lane = "L1";
    private const string HomeState = "33";

    private readonly TempDatabase _temp = new();

    public void Dispose() => _temp.Dispose();

    private static readonly StoreProfile Store = new() { Name = "Sri Lakshmi Stores", Gstin = "33AABCS1429B1ZX" };

    private HeldBillRepository Held => new(_temp.Database);
    private DayCloseRepository Closes => new(_temp.Database, Held);

    private CheckoutService NewCheckout() =>
        new(new InvoiceRepository(_temp.Database), new CustomerRepository(_temp.Database), new RecordingDrawerService());

    private void SeedCatalogue() => _temp.Items.AddRange(
    [
        Catalogue.Item(sku: "DAL001", barcode: "8901234567890", name: "Toor Dal 1kg", price: 189m, gstRate: 5m),
        Catalogue.Item(sku: "SHM001", barcode: "8901234567920", name: "Shampoo 340ml", price: 299m, gstRate: 18m),
        Catalogue.Item(sku: "CHO001", barcode: "8901234567906", name: "Chocolate Bar", price: 1.76m, gstRate: 28m),
    ]);

    /// <summary>Rings up and settles one sale, returning what it came to.</summary>
    private decimal Sell(string barcode, TenderType tender = TenderType.Cash, decimal? handedOver = null, string lane = Lane, decimal quantity = 1m)
    {
        var bill = new InvoiceEngine(HomeState);
        bill.AddItem(_temp.Items.FindByBarcode(barcode)!);

        if (quantity != 1m)
            bill.SetQuantity(0, quantity);

        var total = bill.Totals.GrandTotal;
        var basket = new TenderBasket(total);
        basket.Add(tender, handedOver ?? total);

        NewCheckout().Complete(lane, bill, basket);
        return total;
    }

    // ---- The figures ---------------------------------------------------------------------------

    [Fact]
    public void AClosedDayReportsWhatWasSold()
    {
        SeedCatalogue();

        var first = Sell("8901234567890");
        var second = Sell("8901234567920");

        var day = Closes.Close(Lane, DateTimeOffset.Now);

        Assert.Equal(2, day.InvoiceCount);
        Assert.Equal(first + second, day.NetSales);
        Assert.True(day.Id > 0);
        Assert.NotNull(day.OpenedAt);
    }

    /// <summary>
    /// The three reconciliations the report prints. A day that does not satisfy them is a day
    /// somebody has to look at.
    /// </summary>
    [Fact]
    public void TheReportReconcilesInEveryDirection()
    {
        SeedCatalogue();

        Sell("8901234567890", handedOver: 200m);
        Sell("8901234567920", TenderType.Card);
        Sell("8901234567906", quantity: 7m);

        var day = Closes.Close(Lane, DateTimeOffset.Now);

        Assert.Equal(day.NetSales, day.GrossSales - day.TotalDiscount);
        Assert.Equal(day.NetSales, day.TaxableValue + day.TotalTax);
        Assert.Equal(day.NetSales, day.Tenders.Sum(t => t.Amount) - day.ChangeGiven);
    }

    /// <summary>
    /// The one figure on the report a cashier can check by hand: notes taken in, less change given
    /// back.
    /// </summary>
    [Fact]
    public void CashExpectedIsWhatShouldBeInTheDrawer()
    {
        SeedCatalogue();

        var first = Sell("8901234567890", handedOver: 500m);   // 189.00, change 311.00
        var second = Sell("8901234567920", handedOver: 300m);  // 299.00, change 1.00
        Sell("8901234567906", TenderType.Upi);                 // no cash at all

        var day = Closes.Close(Lane, DateTimeOffset.Now);

        Assert.Equal(800.00m, day.TotalOf(TenderType.Cash));
        Assert.Equal(312.00m, day.ChangeGiven);
        Assert.Equal(first + second, day.CashExpected);
        Assert.Equal(488.00m, day.CashExpected);
    }

    [Fact]
    public void DiscountsSeparateGrossFromNet()
    {
        SeedCatalogue();

        var bill = new InvoiceEngine(HomeState);
        bill.AddItem(_temp.Items.FindByBarcode("8901234567920")!);
        bill.SetDiscount(0, 49m);

        var basket = new TenderBasket(bill.Totals.GrandTotal);
        basket.Add(TenderType.Cash, bill.Totals.GrandTotal);
        NewCheckout().Complete(Lane, bill, basket);

        var day = Closes.Close(Lane, DateTimeOffset.Now);

        Assert.Equal(49.00m, day.TotalDiscount);
        Assert.Equal(250.00m, day.NetSales);
        Assert.Equal(299.00m, day.GrossSales);
    }

    [Fact]
    public void TendersAreBrokenOutWithTheirCounts()
    {
        SeedCatalogue();

        Sell("8901234567890");
        Sell("8901234567890");
        Sell("8901234567920", TenderType.Card);
        Sell("8901234567906", TenderType.Upi);

        var day = Closes.Close(Lane, DateTimeOffset.Now);

        Assert.Equal(2, day.Tenders.Single(t => t.Type == TenderType.Cash).PaymentCount);
        Assert.Equal(378.00m, day.TotalOf(TenderType.Cash));
        Assert.Equal(299.00m, day.TotalOf(TenderType.Card));
        Assert.Equal(1.76m, day.TotalOf(TenderType.Upi));
        Assert.Equal(0m, day.TotalOf(TenderType.StoreCredit));
    }

    /// <summary>The shape a GST return wants the day in.</summary>
    [Fact]
    public void TaxIsBrokenOutBySlab()
    {
        SeedCatalogue();

        Sell("8901234567890");   // 5%
        Sell("8901234567920");   // 18%
        Sell("8901234567906");   // 28%

        var day = Closes.Close(Lane, DateTimeOffset.Now);

        Assert.Equal([5m, 18m, 28m], day.TaxSlabs.Select(s => s.GstRate).ToArray());
        Assert.Equal(day.TotalTax, day.TaxSlabs.Sum(s => s.Tax));

        foreach (var slab in day.TaxSlabs)
            output.WriteLine($"{slab.GstRate}%  taxable {slab.TaxableValue:N2}  tax {slab.Tax:N2}");
    }

    [Fact]
    public void LoyaltyMovementIsTotalledForTheDay()
    {
        SeedCatalogue();

        var customers = new CustomerRepository(_temp.Database);
        var customer = customers.Add(new Customer { MobileNo = "9876543210", StateCode = HomeState, LoyaltyBalance = 5_000 });

        var bill = new InvoiceEngine(HomeState);
        bill.AddItem(_temp.Items.FindByBarcode("8901234567920")!);
        bill.SetCustomer(customer);

        var checkout = NewCheckout();
        var redemption = checkout.QuoteRedemption(bill.Totals.GrandTotal, customer);

        var basket = new TenderBasket(bill.Totals.GrandTotal);
        basket.Add(TenderType.LoyaltyPoints, redemption.Value, $"{redemption.Points} points");
        basket.Add(TenderType.Cash, bill.Totals.GrandTotal - redemption.Value);
        var result = checkout.Complete(Lane, bill, basket, redemption.Points);

        var day = Closes.Close(Lane, DateTimeOffset.Now);

        Assert.Equal(redemption.Points, day.PointsRedeemed);
        Assert.Equal(result.PointsEarned, day.PointsEarned);
        Assert.Equal(redemption.Value, day.TotalOf(TenderType.LoyaltyPoints));
    }

    // ---- Boundaries -----------------------------------------------------------------------------

    /// <summary>
    /// The property the whole design turns on: a sale appears on exactly one Z-report, ever.
    /// </summary>
    [Fact]
    public void ASaleNeverAppearsOnTwoReports()
    {
        SeedCatalogue();

        var first = Sell("8901234567890");
        var firstClose = Closes.Close(Lane, DateTimeOffset.Now);

        var second = Sell("8901234567920");
        var secondClose = Closes.Close(Lane, DateTimeOffset.Now);

        Assert.Equal(first, firstClose.NetSales);
        Assert.Equal(second, secondClose.NetSales);
        Assert.Equal(1, firstClose.InvoiceCount);
        Assert.Equal(1, secondClose.InvoiceCount);
    }

    /// <summary>Closing twice must not double-count the day.</summary>
    [Fact]
    public void ClosingTwiceInARowFindsNothingTheSecondTime()
    {
        SeedCatalogue();
        Sell("8901234567890");

        var first = Closes.Close(Lane, DateTimeOffset.Now);
        var second = Closes.Close(Lane, DateTimeOffset.Now);

        Assert.Equal(1, first.InvoiceCount);
        Assert.Equal(0, second.InvoiceCount);
        Assert.True(second.TookNothing);
        Assert.Equal(0m, second.NetSales);
    }

    [Fact]
    public void EachLaneClosesItsOwnTakings()
    {
        SeedCatalogue();

        var one = Sell("8901234567890", lane: "L1");
        var two = Sell("8901234567920", lane: "L2");

        var first = Closes.Close("L1", DateTimeOffset.Now);
        var second = Closes.Close("L2", DateTimeOffset.Now);

        Assert.Equal(one, first.NetSales);
        Assert.Equal(two, second.NetSales);
    }

    [Fact]
    public void ALaneThatSoldNothingStillCloses()
    {
        var day = Closes.Close(Lane, DateTimeOffset.Now);

        Assert.True(day.TookNothing);
        Assert.Equal(0m, day.NetSales);
        Assert.Null(day.OpenedAt);
        Assert.Empty(day.Tenders);
    }

    /// <summary>A preview must not close anything, or the cashier could never look before leaping.</summary>
    [Fact]
    public void APreviewChangesNothing()
    {
        SeedCatalogue();
        var total = Sell("8901234567890");

        var preview = Closes.Preview(Lane, DateTimeOffset.Now);
        Assert.Equal(total, preview.NetSales);
        Assert.Equal(0, preview.Id);

        var closed = Closes.Close(Lane, DateTimeOffset.Now);
        Assert.Equal(total, closed.NetSales);
        Assert.Equal(1, closed.InvoiceCount);
    }

    /// <summary>
    /// Parked bills are not sales, but somebody has to deal with them before the lane is left for
    /// the night.
    /// </summary>
    [Fact]
    public void StillParkedBillsAreFlaggedOnTheReport()
    {
        SeedCatalogue();
        Sell("8901234567890");

        var parked = new InvoiceEngine(HomeState);
        parked.AddItem(_temp.Items.FindByBarcode("8901234567920")!);
        Held.Park(Lane, Held.NextToken(Lane), DateTimeOffset.Now, null, parked.SnapshotLines());

        var day = Closes.Close(Lane, DateTimeOffset.Now);

        Assert.Equal(1, day.HeldBillsOutstanding);
        Assert.Equal(1, day.InvoiceCount);

        var report = new ZReportComposer(Store).Compose(day).ToPlainText();
        Assert.Contains("1 bill(s) still parked", report);
    }

    // ---- Round trip ------------------------------------------------------------------------------

    [Fact]
    public void ASavedReportReadsBackAsItWasPrinted()
    {
        SeedCatalogue();

        Sell("8901234567890", handedOver: 500m);
        Sell("8901234567920", TenderType.Card);

        var closed = Closes.Close(Lane, DateTimeOffset.Now);
        var reloaded = Closes.FindById(closed.Id);

        Assert.NotNull(reloaded);
        Assert.Equal(closed.NetSales, reloaded.NetSales);
        Assert.Equal(closed.GrossSales, reloaded.GrossSales);
        Assert.Equal(closed.CashExpected, reloaded.CashExpected);
        Assert.Equal(closed.ChangeGiven, reloaded.ChangeGiven);
        Assert.Equal(closed.TotalTax, reloaded.TotalTax);
        Assert.Equal(closed.Tenders.Count, reloaded.Tenders.Count);
        Assert.Equal(closed.TaxSlabs.Count, reloaded.TaxSlabs.Count);

        // And it still reconciles after the round trip.
        Assert.Equal(reloaded.NetSales, reloaded.TaxableValue + reloaded.TotalTax);
        Assert.Equal(reloaded.NetSales, reloaded.Tenders.Sum(t => t.Amount) - reloaded.ChangeGiven);
    }

    [Fact]
    public void TheLatestReportIsFindableForReprinting()
    {
        SeedCatalogue();

        Sell("8901234567890");
        var first = Closes.Close(Lane, DateTimeOffset.Now);

        Sell("8901234567920");
        var second = Closes.Close(Lane, DateTimeOffset.Now.AddMinutes(1));

        Assert.Equal(second.Id, Closes.FindLatest(Lane)!.Id);
        Assert.NotEqual(first.Id, Closes.FindLatest(Lane)!.Id);
        Assert.Null(Closes.FindLatest("NOSUCHLANE"));
    }

    // ---- The printed report ----------------------------------------------------------------------

    [Fact]
    public void TheReportCarriesWhatTheCashierNeeds()
    {
        SeedCatalogue();

        Sell("8901234567890", handedOver: 500m);
        Sell("8901234567920", TenderType.Card);

        var day = Closes.Close(Lane, DateTimeOffset.Now);
        var report = new ZReportComposer(Store).Compose(day).ToPlainText();

        output.WriteLine(report);

        Assert.Contains("DAY-END REPORT (Z)", report);
        Assert.Contains("CASH IN DRAWER SHOULD BE", report);
        Assert.Contains(day.CashExpected.ToString("N2"), report);
        Assert.Contains("Net sales", report);
        Assert.Contains("CGST", report);
        Assert.Contains("Reconciled", report);
        Assert.Contains("33AABCS1429B1ZX", report);
    }

    [Fact]
    public void AReprintIsMarkedAsOne()
    {
        SeedCatalogue();
        Sell("8901234567890");

        var day = Closes.Close(Lane, DateTimeOffset.Now);

        Assert.DoesNotContain("REPRINT", new ZReportComposer(Store).Compose(day).ToPlainText());
        Assert.Contains("** REPRINT **", new ZReportComposer(Store).Compose(day, isReprint: true).ToPlainText());
    }

    [Fact]
    public void ADayWithNoSalesPrintsSayingSo()
    {
        var report = new ZReportComposer(Store).Compose(Closes.Close(Lane, DateTimeOffset.Now)).ToPlainText();

        Assert.Contains("NO SALES IN THIS PERIOD", report);
    }

    // ---- The report in Tamil ---------------------------------------------------------------------

    /// <summary>Seeds, sells, and closes once — the catalogue cannot be seeded twice.</summary>
    private DayCloseSummary ATradingDay()
    {
        SeedCatalogue();
        Sell("8901234567890", handedOver: 500m);
        Sell("8901234567920", TenderType.Card);

        return Closes.Close(Lane, DateTimeOffset.Now);
    }

    private static string InTamil(DayCloseSummary day, bool isReprint = false) =>
        new ZReportComposer(Store, ReceiptBuilder.Width80Mm, ReceiptLanguage.Tamil)
            .Compose(day, isReprint)
            .ToPlainText();

    /// <summary>
    /// A lane printing its bills in Tamil has to close its day in Tamil too. The figures are the
    /// shopkeeper's own, and calling them one thing on the receipt and another on the report is how
    /// a drawer difference becomes an argument.
    /// </summary>
    [Theory]
    [InlineData("நாள் இறுதி அறிக்கை (Z)")]
    [InlineData("பணப்பெட்டியில் இருக்க வேண்டிய தொகை")]
    [InlineData("வந்த ரொக்கம்")]
    [InlineData("கொடுத்த மீதம்")]
    [InlineData("விற்பனை")]
    [InlineData("பில்கள்")]
    [InlineData("நிகர விற்பனை")]
    [InlineData("மொத்த வரி")]
    [InlineData("பணம் செலுத்திய முறை")]
    public void TheDayEndReportPrintsInTamilOnATamilLane(string label)
    {
        var report = InTamil(ATradingDay());
        output.WriteLine(report);

        Assert.Contains(label, report);
    }

    [Fact]
    public void TheTamilReportKeepsTheGstTermsAndTheFiguresUnchanged()
    {
        var day = ATradingDay();

        var english = new ZReportComposer(Store).Compose(day).ToPlainText();
        var tamil = InTamil(day);

        // The tax names are what an inspector looks for, and the tender names match the receipt.
        foreach (var unchanged in new[] { "CGST", "SGST", "Cash", "Card", "33AABCS1429B1ZX" })
        {
            Assert.Contains(unchanged, english);
            Assert.Contains(unchanged, tamil);
        }

        // Every figure on the report is the same figure in both.
        foreach (var figure in new[] { day.CashExpected, day.NetSales, day.GrossSales, day.TotalTax })
        {
            Assert.Contains(figure.ToString("N2"), english);
            Assert.Contains(figure.ToString("N2"), tamil);
        }
    }

    [Fact]
    public void TheTamilReportSaysWhenItReconcilesAndWhenNothingSold()
    {
        var day = ATradingDay();

        Assert.Contains("சரிபார்க்கப்பட்டது", InTamil(day));
        Assert.Contains("** REPRINT **", InTamil(day, isReprint: true));

        // Closing again after everything has been reported takes nothing, which is the state a
        // lane is in if somebody closes twice by accident.
        Assert.Contains("இந்த நேரத்தில் விற்பனை இல்லை", InTamil(Closes.Close(Lane, DateTimeOffset.Now)));
    }

    [Theory]
    [InlineData(48)]
    [InlineData(32)]
    public void TheTamilReportFitsThePaper(int width)
    {
        SeedCatalogue();
        Sell("8901234567890", handedOver: 500m);

        var day = Closes.Close(Lane, DateTimeOffset.Now);
        var report = new ZReportComposer(Store, width, ReceiptLanguage.Tamil).Compose(day).ToPlainText();

        foreach (var line in report.Split('\n'))
            Assert.True(line.TrimEnd('\r').Length <= width, $"'{line}' is {line.TrimEnd('\r').Length} of {width} characters.");
    }

    /// <summary>
    /// The Tamil on the report has to be drawn, exactly as it is on a receipt, or the day's figures
    /// come out under a row of '?'.
    /// </summary>
    [Fact]
    public void TheTamilReportIsDrawnRatherThanSentAsCharacters()
    {
        SeedCatalogue();
        Sell("8901234567890", handedOver: 500m);

        var day = Closes.Close(Lane, DateTimeOffset.Now);
        var report = new ZReportComposer(Store, ReceiptBuilder.Width80Mm, ReceiptLanguage.Tamil).Compose(day);

        var rasterizer = new RecordingTextRasterizer();
        report.ToEscPos(raster: new RasterOptions(rasterizer, RasterOptions.Dots80Mm, RasterMode.Auto));

        var drawn = rasterizer.Runs.Select(r => r.Text).ToList();

        Assert.Contains("நாள் இறுதி அறிக்கை (Z)", drawn);
        Assert.Contains("பணப்பெட்டியில் இருக்க வேண்டிய தொகை", drawn);
        Assert.DoesNotContain("CGST", drawn);
    }

    [Theory]
    [InlineData(48)]
    [InlineData(32)]
    public void TheReportFitsThePaper(int width)
    {
        SeedCatalogue();
        Sell("8901234567890", handedOver: 500m);
        Sell("8901234567920", TenderType.Card);

        var report = new ZReportComposer(Store, width).Compose(Closes.Close(Lane, DateTimeOffset.Now)).ToPlainText();

        foreach (var line in report.Split(Environment.NewLine))
            Assert.True(line.Length <= width, $"'{line}' is {line.Length} characters on {width}-wide paper.");
    }
}
