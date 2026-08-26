using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Domain.Printing;
using Pos.Core.Hardware.Drawer;
using Pos.Core.Hardware.Printing;
using Pos.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Pos.Core.Tests;

/// <summary>
/// What actually lands on the paper, and how printing behaves as part of completing a sale.
/// </summary>
public class ReceiptTests(ITestOutputHelper output) : IDisposable
{
    private const string Lane = "L1";
    private const string HomeState = "33";

    private readonly TempDatabase _temp = new();
    private readonly RecordingDrawerService _drawer = new();
    private readonly LoopbackPrinterService _printer = new();

    private static readonly StoreProfile Store = new()
    {
        Name = "Sri Lakshmi Stores",
        AddressLine1 = "42 Bazaar Street",
        AddressLine2 = "Coimbatore 641001",
        Phone = "0422 2345678",
        Gstin = "33AABCS1429B1ZX",
        FooterMessage = "Thank you, please visit again",
    };

    private InvoiceRepository Invoices => new(_temp.Database);
    private CustomerRepository Customers => new(_temp.Database);
    private ReceiptComposer Composer => new(Store);

    private CheckoutService NewCheckout() =>
        new(Invoices, Customers, _drawer, null, TimeProvider.System, _printer, Composer);

    private InvoiceEngine BillWith(params (string Name, decimal Price, decimal Gst)[] items)
    {
        var bill = new InvoiceEngine(HomeState);
        var id = 1;

        foreach (var (name, price, gst) in items)
        {
            bill.AddItem(Catalogue.Item(id: id, sku: $"SKU{id:D4}", barcode: $"890{id:D10}", name: name, price: price, gstRate: gst));
            id++;
        }

        return bill;
    }

    public void Dispose() => _temp.Dispose();

    private CheckoutResult CompleteCashSale(InvoiceEngine bill, decimal cash)
    {
        var basket = new TenderBasket(bill.Totals.GrandTotal);
        basket.Add(TenderType.Cash, cash);
        return NewCheckout().Complete(Lane, bill, basket);
    }

    // ---- Content ------------------------------------------------------------------------------

    [Fact]
    public void TheReceiptCarriesEverythingAGstInvoiceNeeds()
    {
        var bill = BillWith(("Toor Dal 1kg", 189m, 5m), ("Shampoo 340ml", 299m, 18m));
        var result = CompleteCashSale(bill, 500m);

        var paper = Composer.Compose(result.Invoice).ToPlainText();
        output.WriteLine(paper);

        Assert.Contains("Sri Lakshmi Stores", paper);
        Assert.Contains("GSTIN 33AABCS1429B1ZX", paper);
        Assert.Contains("TAX INVOICE", paper);
        Assert.Contains(result.Invoice.InvoiceNo, paper);
        Assert.Contains("Toor Dal 1kg", paper);
        Assert.Contains("Shampoo 340ml", paper);
        Assert.Contains("HSN 0713", paper);
        Assert.Contains("CGST", paper);
        Assert.Contains("SGST", paper);
        Assert.Contains("TOTAL", paper);
        Assert.Contains("Rs. 488.00", paper);
        Assert.Contains("Thank you, please visit again", paper);
    }

    /// <summary>
    /// The four tenders a counter deals in are printed on every bill whether or not they were used,
    /// so the same four figures land in the same four places on every receipt of the day.
    /// </summary>
    [Fact]
    public void EveryTenderIsShownEvenWhenItWasNotUsed()
    {
        var paper = Composer.Compose(CompleteCashSale(BillWith(("Toor Dal 1kg", 189m, 5m)), 189m).Invoice).ToPlainText();

        var first = paper.Split('\n').Single(l => l.Contains("Cash") && l.Contains("UPI"));
        var second = paper.Split('\n').Single(l => l.Contains("Card") && l.Contains("Credit"));

        Assert.Contains("189.00", first);
        Assert.Contains("0.00", first);
        Assert.Contains("0.00", second);

        // A right-aligned figure fills its cell, so without a gutter the next label runs into it.
        Assert.DoesNotContain("0.00UPI", first);
        Assert.DoesNotContain("0.00Credit", second);
    }

    /// <summary>
    /// The FSSAI licence and customer care number a grocery bill carries, printed only when the
    /// shop has actually been given them.
    /// </summary>
    [Fact]
    public void TheFssaiLicenceAndCustomerCareNumberArePrintedWhenConfigured()
    {
        var invoice = CompleteCashSale(BillWith(("Toor Dal 1kg", 189m, 5m)), 189m).Invoice;

        var profile = Store with
        {
            FssaiNumber = "12426020000127",
            CustomerCarePhone = "9080678177",
        };

        var paper = new ReceiptComposer(profile).Compose(invoice).ToPlainText();

        Assert.Contains("FSSAI No 12426020000127", paper);
        Assert.Contains("Customer Care - 9080678177", paper);

        // The shop's own number gives way to the care number rather than printing beside it.
        Assert.DoesNotContain("Ph: ", paper);

        var without = Composer.Compose(invoice).ToPlainText();
        Assert.DoesNotContain("FSSAI", without);
        Assert.DoesNotContain("Customer Care", without);
        Assert.Contains("Ph: 0422 2345678", without);
    }

    /// <summary>
    /// The rate-wise breakup a GST invoice is expected to carry. A bill spanning two slabs must
    /// show both, with the tax at each.
    /// </summary>
    [Fact]
    public void TheTaxSummaryBreaksDownByRate()
    {
        var bill = BillWith(("Toor Dal 1kg", 189m, 5m), ("Shampoo 340ml", 299m, 18m));
        var paper = Composer.Compose(CompleteCashSale(bill, 500m).Invoice).ToPlainText();

        Assert.Contains("Tax summary", paper);
        Assert.Contains("5%", paper);
        Assert.Contains("18%", paper);
    }

    [Fact]
    public void PaymentsChangeAndReferencesAreAllShown()
    {
        var bill = BillWith(("Toor Dal 1kg", 189m, 5m));
        var basket = new TenderBasket(bill.Totals.GrandTotal);
        basket.Add(TenderType.Card, 100.00m, "AUTH 55123");
        basket.Add(TenderType.Cash, 150.00m);

        var result = NewCheckout().Complete(Lane, bill, basket);
        var paper = Composer.Compose(result.Invoice).ToPlainText();

        Assert.Contains("Card", paper);
        Assert.Contains("AUTH 55123", paper);
        Assert.Contains("Cash", paper);
        Assert.Contains("Change", paper);
        Assert.Contains("61.00", paper);
    }

    [Fact]
    public void LoyaltyMovementIsPrintedForACustomer()
    {
        var customer = Customers.Add(new Customer { MobileNo = "9876543210", Name = "Anitha", StateCode = HomeState, LoyaltyBalance = 5_000 });
        var bill = BillWith(("Basmati Rice 5kg", 1_000m, 5m));
        bill.SetCustomer(customer);

        var checkout = NewCheckout();
        var redemption = checkout.QuoteRedemption(bill.Totals.GrandTotal, customer);

        var basket = new TenderBasket(bill.Totals.GrandTotal);
        basket.Add(TenderType.LoyaltyPoints, redemption.Value, $"{redemption.Points} points");
        basket.Add(TenderType.Cash, bill.Totals.GrandTotal - redemption.Value);

        var result = checkout.Complete(Lane, bill, basket, redemption.Points);
        var paper = Composer.Compose(result.Invoice).ToPlainText();

        Assert.Contains("Anitha", paper);
        Assert.Contains($"Points redeemed : {redemption.Points}", paper);
        Assert.Contains("Points earned : ", paper);

        // The running balance a shopper checks the bill for, and the points as a tender that make
        // the four-way block add up to the total.
        Assert.Contains("Total points earned : ", paper);
        Assert.Contains("Points", paper);
    }

    /// <summary>
    /// Discounted bills say what the customer saved. It is the line a shopper looks for, and it is
    /// the total of the line discounts rather than anything recomputed.
    /// </summary>
    [Fact]
    public void TheSavingsLineShowsTheDiscountAndOnlyAppearsWhenThereIsOne()
    {
        var bill = BillWith(("Shampoo 340ml", 299m, 18m));
        bill.SetDiscount(0, 49m);

        var basket = new TenderBasket(bill.Totals.GrandTotal);
        basket.Add(TenderType.Cash, bill.Totals.GrandTotal);

        var discounted = Composer.Compose(NewCheckout().Complete(Lane, bill, basket).Invoice).ToPlainText();
        Assert.Contains("Today's saving : 49.00", discounted);

        var undiscounted = Composer.Compose(CompleteCashSale(BillWith(("Toor Dal 1kg", 189m, 5m)), 189m).Invoice).ToPlainText();
        Assert.DoesNotContain("Today's saving", undiscounted);
    }

    [Fact]
    public void AWalkInReceiptHasNoLoyaltySectionAndNoCustomerName()
    {
        var paper = Composer.Compose(CompleteCashSale(BillWith(("Toor Dal 1kg", 189m, 5m)), 189m).Invoice).ToPlainText();

        Assert.DoesNotContain("Total points earned", paper);
        Assert.DoesNotContain("Points redeemed", paper);
        Assert.DoesNotContain("Mobile", paper);

        // The customer row keeps its place beside the time so every bill has the same shape; on a
        // walk-in it simply has nothing in it.
        var customerLine = paper.Split('\n').Single(l => l.TrimStart().StartsWith("Customer", StringComparison.Ordinal));
        Assert.Matches(@"^Customer\s+Time\s+\d\d:\d\d [AP]M\s*$", customerLine.TrimEnd('\r'));
    }

    /// <summary>A reprint has to say so on its face, or it can be passed off as a second sale.</summary>
    [Fact]
    public void AReprintIsMarkedAsOne()
    {
        var result = CompleteCashSale(BillWith(("Toor Dal 1kg", 189m, 5m)), 189m);

        Assert.DoesNotContain("REPRINT", Composer.Compose(result.Invoice).ToPlainText());
        Assert.Contains("** REPRINT **", Composer.Compose(result.Invoice, isReprint: true).ToPlainText());
    }

    [Fact]
    public void AnInterStateSaleShowsIgstInsteadOfTheSplit()
    {
        var customer = Customers.Add(new Customer { MobileNo = "9876500009", StateCode = "29" });
        var bill = BillWith(("Shampoo 340ml", 299m, 18m));
        bill.SetCustomer(customer);

        var basket = new TenderBasket(bill.Totals.GrandTotal);
        basket.Add(TenderType.Cash, 299m);

        var paper = Composer.Compose(NewCheckout().Complete(Lane, bill, basket).Invoice).ToPlainText();

        Assert.Contains("IGST", paper);
        Assert.DoesNotContain("CGST", paper);
    }

    [Fact]
    public void NoLineOnTheReceiptOverrunsThePaper()
    {
        foreach (var width in new[] { ReceiptBuilder.Width58Mm, ReceiptBuilder.Width80Mm })
        {
            var bill = BillWith(
                ("Premium Organic Cold Pressed Groundnut Oil 5 Litre Tin", 1_299m, 5m),
                ("Toor Dal 1kg", 189m, 5m));

            var result = CompleteCashSale(bill, 1_500m);
            var paper = new ReceiptComposer(Store, width).Compose(result.Invoice).ToPlainText();

            foreach (var line in paper.Split(Environment.NewLine))
                Assert.True(line.Length <= width, $"On {width}-wide paper: '{line}' is {line.Length} characters.");
        }
    }

    [Fact]
    public void TheJobEndsWithACut()
    {
        var bytes = Composer.Compose(CompleteCashSale(BillWith(("Toor Dal 1kg", 189m, 5m)), 189m).Invoice).ToEscPos();

        // GS V is the last command on the wire.
        Assert.Equal(0x1D, bytes[^3]);
        Assert.Equal((byte)'V', bytes[^2]);
    }

    // ---- Printing as part of checkout ---------------------------------------------------------

    [Fact]
    public void CompletingASalePrintsTheReceipt()
    {
        var result = CompleteCashSale(BillWith(("Toor Dal 1kg", 189m, 5m)), 189m);

        Assert.True(result.Print.Succeeded);
        Assert.Single(_printer.Jobs);
        Assert.True(_printer.LastJob.Length > 0);
    }

    /// <summary>
    /// The same rule the drawer follows: a printer that is out of paper must not cost a sale that
    /// has already been paid for.
    /// </summary>
    [Fact]
    public void APrinterFailureIsReportedWithoutLosingTheSale()
    {
        _printer.FailWith = "out of paper";

        var result = CompleteCashSale(BillWith(("Toor Dal 1kg", 189m, 5m)), 189m);

        Assert.Equal(PrintStatus.Failed, result.Print.Status);
        Assert.Equal("out of paper", result.Print.Detail);
        Assert.NotNull(Invoices.FindByInvoiceNo(result.Invoice.InvoiceNo));
    }

    [Fact]
    public void ALaneWithNoPrinterStillCompletesSales()
    {
        var checkout = new CheckoutService(Invoices, Customers, _drawer, null, TimeProvider.System, new NoPrinterService(), Composer);
        var bill = BillWith(("Toor Dal 1kg", 189m, 5m));
        var basket = new TenderBasket(bill.Totals.GrandTotal);
        basket.Add(TenderType.Cash, 189m);

        var result = checkout.Complete(Lane, bill, basket);

        Assert.Equal(PrintStatus.NoPrinterConfigured, result.Print.Status);
        Assert.NotNull(Invoices.FindByInvoiceNo(result.Invoice.InvoiceNo));
    }

    /// <summary>A checkout wired with no composer at all still settles; it just prints nothing.</summary>
    [Fact]
    public void ACheckoutWithNoReceiptComposerStillSettles()
    {
        var checkout = new CheckoutService(Invoices, Customers, _drawer);
        var bill = BillWith(("Toor Dal 1kg", 189m, 5m));
        var basket = new TenderBasket(bill.Totals.GrandTotal);
        basket.Add(TenderType.Cash, 189m);

        var result = checkout.Complete(Lane, bill, basket);

        Assert.Equal(PrintStatus.NoPrinterConfigured, result.Print.Status);
        Assert.NotNull(Invoices.FindByInvoiceNo(result.Invoice.InvoiceNo));
    }

    [Fact]
    public void AReprintCanBeAskedForAfterwards()
    {
        var result = CompleteCashSale(BillWith(("Toor Dal 1kg", 189m, 5m)), 189m);
        _printer.Clear();

        var outcome = NewCheckout().Reprint(Invoices.FindByInvoiceNo(result.Invoice.InvoiceNo)!);

        Assert.True(outcome.Succeeded);
        Assert.Single(_printer.Jobs);
    }

    /// <summary>
    /// Cash opens the drawer and prints. Both peripherals run after the invoice is on disk, so
    /// neither can take the sale down with it.
    /// </summary>
    [Fact]
    public void ACashSaleBothPrintsAndKicks()
    {
        var result = CompleteCashSale(BillWith(("Toor Dal 1kg", 189m, 5m)), 200m);

        Assert.True(result.Print.Succeeded);
        Assert.Equal(DrawerKickResult.Opened, result.Drawer);
        Assert.Equal(1, _drawer.KickCount);
    }

    /// <summary>
    /// The realistic counter wiring: one printer, drawer hanging off its RJ11 port. Both the
    /// receipt and the kick go down the same cable.
    /// </summary>
    [Fact]
    public void APassthroughDrawerAndPrinterShareTheOneConnection()
    {
        var drawer = new PrinterPassthroughDrawerService(_printer);
        var checkout = new CheckoutService(Invoices, Customers, drawer, null, TimeProvider.System, _printer, Composer);

        var bill = BillWith(("Toor Dal 1kg", 189m, 5m));
        var basket = new TenderBasket(bill.Totals.GrandTotal);
        basket.Add(TenderType.Cash, 200m);

        var result = checkout.Complete(Lane, bill, basket);

        Assert.Equal(DrawerKickResult.Opened, result.Drawer);
        Assert.Equal(2, _printer.Jobs.Count);
        Assert.Equal(EscPos.KickDrawer(), _printer.LastJob);
    }
}
