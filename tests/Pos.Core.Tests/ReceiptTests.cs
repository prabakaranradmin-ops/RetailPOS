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
        Assert.Contains("GSTIN: 33AABCS1429B1ZX", paper);
        Assert.Contains("TAX INVOICE", paper);
        Assert.Contains(result.Invoice.InvoiceNo, paper);
        Assert.Contains("Toor Dal 1kg", paper);
        Assert.Contains("Shampoo 340ml", paper);
        Assert.Contains("HSN 0713", paper);
        Assert.Contains("CGST", paper);
        Assert.Contains("SGST", paper);
        Assert.Contains("TOTAL Rs.", paper);
        Assert.Contains("488.00", paper);
        Assert.Contains("Thank you, please visit again", paper);
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
        Assert.Contains("Reward points", paper);
        Assert.Contains("Redeemed", paper);
        Assert.Contains("Earned", paper);
        Assert.Contains("Loyalty points", paper);
    }

    [Fact]
    public void AWalkInReceiptHasNoCustomerOrLoyaltySection()
    {
        var paper = Composer.Compose(CompleteCashSale(BillWith(("Toor Dal 1kg", 189m, 5m)), 189m).Invoice).ToPlainText();

        Assert.DoesNotContain("Reward points", paper);
        Assert.DoesNotContain("Customer", paper);
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
