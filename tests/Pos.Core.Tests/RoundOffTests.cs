using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Domain.Printing;
using Pos.Core.Hardware.Drawer;
using Pos.Core.Loyalty;
using Pos.TestSupport;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// The rupee round-off: the paise a counter gives up so the drawer holds money a shopkeeper
/// actually stocks.
/// </summary>
/// <remarks>
/// The rule this file exists to hold down is that rounding moves what is <em>payable</em> and
/// nothing else. Not a line price, not the taxable value, not a paisa of CGST or SGST. It is the
/// same rule loyalty points already follow — a payment, not a discount — and for the same reason: a
/// GST return filed from these bills has to read identically whether the lane rounds or not.
///
/// Every figure here is asserted exactly, never within a tolerance.
/// </remarks>
public class RoundOffTests : IDisposable
{
    private const string Lane = "L1";
    private const string HomeState = "33";

    private readonly TempDatabase _temp = new();
    private readonly RecordingDrawerService _drawer = new();

    public void Dispose() => _temp.Dispose();

    private InvoiceRepository Invoices => new(_temp.Database);

    private CheckoutService NewCheckout(LoyaltyRules? rules = null) =>
        new(Invoices, new CustomerRepository(_temp.Database), _drawer, rules, TimeProvider.System);

    /// <param name="rounds">Whether this lane settles to the whole rupee.</param>
    private static InvoiceEngine BillOf(decimal price, bool rounds, decimal gst = 18m)
    {
        var bill = new InvoiceEngine(HomeState, TaxMode.Gst, roundToRupee: rounds);

        bill.AddItem(Catalogue.Item(
            id: 1, sku: "SOAP01", barcode: "8901000000019",
            name: "Hamam Soap 100g", price: price, gstRate: gst));

        return bill;
    }

    // ---- The table -------------------------------------------------------------------------------

    /// <summary>
    /// What each ending rounds to, to the paisa.
    /// </summary>
    /// <remarks>
    /// Banker's rounding, the rule every other step in the engine uses, so a bill ending in exactly
    /// fifty paise goes to the <em>even</em> rupee rather than always up. That is why 94.50 falls to
    /// 94 and 95.50 climbs to 96: across a day of trading half the midpoints go each way, which is
    /// the difference between a rounding rule and a levy on every customer.
    /// </remarks>
    [Theory]
    // price      round-off   payable
    [InlineData(94.50, -0.50, 94.00)]   // midpoint, down to the even rupee
    [InlineData(95.50, +0.50, 96.00)]   // midpoint, up to the even rupee
    [InlineData(94.40, -0.40, 94.00)]   // nearer the rupee below
    [InlineData(94.60, +0.40, 95.00)]   // nearer the rupee above
    [InlineData(94.01, -0.01, 94.00)]   // a single paisa given up
    [InlineData(94.99, +0.01, 95.00)]   // a single paisa added
    [InlineData(95.00, 0.00, 95.00)]    // already whole, so nothing to do
    public void EachEndingRoundsToTheRupeeExactly(double price, double roundOff, double payable)
    {
        var totals = BillOf((decimal)price, rounds: true).Totals;

        Assert.Equal((decimal)price, totals.GrandTotal);
        Assert.Equal((decimal)roundOff, totals.RoundOff);
        Assert.Equal((decimal)payable, totals.AmountPayable);

        // Whatever the adjustment, what is payable is a whole number of rupees.
        Assert.Equal(decimal.Truncate(totals.AmountPayable), totals.AmountPayable);
    }

    [Fact]
    public void ALaneThatDoesNotRoundIsLeftExactlyAsItWas()
    {
        var totals = BillOf(94.50m, rounds: false).Totals;

        Assert.Equal(0m, totals.RoundOff);
        Assert.Equal(94.50m, totals.GrandTotal);
        Assert.Equal(94.50m, totals.AmountPayable);
    }

    /// <summary>Nobody has opted in yet at the point a bare engine is constructed.</summary>
    [Fact]
    public void RoundingIsOffUnlessTheLaneAsksForIt()
    {
        Assert.False(new InvoiceEngine(HomeState).RoundsToRupee);
        Assert.Equal(0m, InvoiceTotals.From(BillOf(94.50m, rounds: true).Lines).RoundOff);
    }

    // ---- What rounding may not touch -------------------------------------------------------------

    /// <summary>
    /// The whole point. Two lanes ring up the same basket, one rounding and one not, and every
    /// figure that reaches a GST return comes out identical — only what is payable moves.
    /// </summary>
    [Theory]
    [InlineData(94.50)]
    [InlineData(95.50)]
    [InlineData(1137.40)]
    [InlineData(38.60)]
    public void TheTaxIsUntouchedByRounding(double price)
    {
        var rounded = BillOf((decimal)price, rounds: true).Totals;
        var plain = BillOf((decimal)price, rounds: false).Totals;

        Assert.Equal(plain.SubtotalTaxable, rounded.SubtotalTaxable);
        Assert.Equal(plain.TotalCgst, rounded.TotalCgst);
        Assert.Equal(plain.TotalSgst, rounded.TotalSgst);
        Assert.Equal(plain.TotalIgst, rounded.TotalIgst);
        Assert.Equal(plain.TotalTax, rounded.TotalTax);
        Assert.Equal(plain.GrandTotal, rounded.GrandTotal);

        // And the three headline figures still add up the way anyone reading the invoice expects.
        Assert.Equal(rounded.GrandTotal, rounded.SubtotalTaxable + rounded.TotalTax);
    }

    /// <summary>A bill of many lines rounds once, at the foot, not line by line.</summary>
    [Fact]
    public void ItIsTheBillThatRoundsRatherThanEachLine()
    {
        var bill = new InvoiceEngine(HomeState, TaxMode.Gst, roundToRupee: true);

        for (var i = 1; i <= 3; i++)
        {
            bill.AddItem(Catalogue.Item(
                id: i, sku: $"SKU{i:D4}", barcode: $"890{i:D10}",
                name: $"Item {i}", price: 12.30m, gstRate: 18m));
        }

        var totals = bill.Totals;

        // Three lines of 12.30 make 36.90, which is one adjustment of ten paise — not three of
        // seventy, which is what rounding each line first would have produced.
        Assert.Equal(36.90m, totals.GrandTotal);
        Assert.Equal(0.10m, totals.RoundOff);
        Assert.Equal(37.00m, totals.AmountPayable);
    }

    // ---- Settlement ------------------------------------------------------------------------------

    [Fact]
    public void TheTenderSettlesAgainstWhatIsPayable()
    {
        var bill = BillOf(94.50m, rounds: true);
        var basket = new TenderBasket(bill.Totals.AmountPayable);
        basket.Add(TenderType.Cash, 100m);

        var result = NewCheckout().Complete(Lane, bill, basket);

        Assert.Equal(94.00m, result.Invoice.AmountPayable);
        Assert.Equal(94.50m, result.Invoice.GrandTotal);
        Assert.Equal(-0.50m, result.Invoice.RoundOff);

        // Change is against what was actually owed, so the drawer balances at closing time.
        Assert.Equal(6.00m, result.ChangeDue);
    }

    /// <summary>
    /// Paying the unrounded figure is refused rather than quietly accepted. Fifty paise a bill is
    /// invisible on one sale and a shortfall the cashier gets blamed for by the end of the day.
    /// </summary>
    [Fact]
    public void ABasketRaisedAgainstTheUnroundedTotalIsRefused()
    {
        var bill = BillOf(94.50m, rounds: true);
        var basket = new TenderBasket(94.50m);
        basket.Add(TenderType.Cash, 94.50m);

        var refused = Assert.Throws<InvalidOperationException>(
            () => NewCheckout().Complete(Lane, bill, basket));

        Assert.Contains("94.50", refused.Message);
        Assert.Contains("94.00", refused.Message);
    }

    /// <summary>
    /// A reprint has to reproduce the bill that was issued. The round-off is stored rather than
    /// worked out again, so turning the setting off tomorrow cannot restate a bill from last week.
    /// </summary>
    [Fact]
    public void TheRoundOffIsKeptWithTheInvoiceAndReadsBackUnchanged()
    {
        var bill = BillOf(94.50m, rounds: true);
        var basket = new TenderBasket(bill.Totals.AmountPayable);
        basket.Add(TenderType.Cash, 94.00m);

        var saved = NewCheckout().Complete(Lane, bill, basket).Invoice;
        var reread = Invoices.FindByInvoiceNo(saved.InvoiceNo)!;

        Assert.Equal(-0.50m, reread.RoundOff);
        Assert.Equal(94.50m, reread.GrandTotal);
        Assert.Equal(94.00m, reread.AmountPayable);
    }

    /// <summary>A bill issued before the lane rounded reads back as what it was: unrounded.</summary>
    [Fact]
    public void AnInvoiceThatWasNeverRoundedReadsBackWithNoAdjustment()
    {
        var bill = BillOf(94.50m, rounds: false);
        var basket = new TenderBasket(bill.Totals.AmountPayable);
        basket.Add(TenderType.Cash, 94.50m);

        var saved = NewCheckout().Complete(Lane, bill, basket).Invoice;
        var reread = Invoices.FindByInvoiceNo(saved.InvoiceNo)!;

        Assert.Equal(0m, reread.RoundOff);
        Assert.Equal(94.50m, reread.AmountPayable);
    }

    // ---- Loyalty ---------------------------------------------------------------------------------

    /// <summary>
    /// Points are earned on what the customer paid, not on a figure they were never charged.
    /// </summary>
    [Fact]
    public void PointsAccrueOnWhatWasActuallyPaid()
    {
        var customers = new CustomerRepository(_temp.Database);
        var customer = customers.Add(new Customer { MobileNo = "9080678177", Name = "Ravi", StateCode = HomeState });

        // One point per 50 rupees. A bill of 99.60 pays 100 and earns two; the unrounded figure
        // would have earned one.
        var rules = new LoyaltyRules(30m, 0.50m, 50m);

        var bill = BillOf(99.60m, rounds: true);
        bill.SetCustomer(customer);

        var basket = new TenderBasket(bill.Totals.AmountPayable);
        basket.Add(TenderType.Cash, 100m);

        var result = NewCheckout(rules).Complete(Lane, bill, basket);

        Assert.Equal(100.00m, result.Invoice.AmountPayable);
        Assert.Equal(2, result.Invoice.Sale.PointsEarned);
    }

    // ---- The receipt -----------------------------------------------------------------------------

    private string Printed(InvoiceEngine bill)
    {
        var basket = new TenderBasket(bill.Totals.AmountPayable);
        basket.Add(TenderType.Cash, bill.Totals.AmountPayable);

        var invoice = NewCheckout().Complete(Lane, bill, basket).Invoice;

        var composer = new ReceiptComposer(
            new StoreProfile { Name = "Ravi Stores", Gstin = "33AEIPH7795F1Z9" },
            paperWidthChars: 48,
            ReceiptLanguage.English);

        return composer.Compose(invoice).ToPlainText();
    }

    [Fact]
    public void TheBillShowsTheRoundOffAndChargesTheRoundedTotal()
    {
        var printed = Printed(BillOf(94.50m, rounds: true));

        Assert.Contains("Round off", printed);
        Assert.Contains("-0.50", printed);
        Assert.Contains("94.00", printed);
    }

    /// <summary>
    /// No line when there is nothing to say. A "Round off 0.00" is a line the customer has to read
    /// to find out that nothing happened.
    /// </summary>
    [Fact]
    public void AWholeRupeeBillPrintsNoRoundOffLineAtAll()
    {
        var printed = Printed(BillOf(95.00m, rounds: true));

        Assert.DoesNotContain("Round off", printed);
        Assert.Contains("95.00", printed);
    }

    [Fact]
    public void ALaneThatDoesNotRoundPrintsNoRoundOffLineEither()
    {
        var printed = Printed(BillOf(94.50m, rounds: false));

        Assert.DoesNotContain("Round off", printed);
        Assert.Contains("94.50", printed);
    }
}
