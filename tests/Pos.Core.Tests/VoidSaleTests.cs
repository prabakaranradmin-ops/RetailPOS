using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Domain.Printing;
using Pos.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Pos.Core.Tests;

/// <summary>
/// Cancelling a sale. The record stays, the number stays used, the takings do not.
/// </summary>
public class VoidSaleTests(ITestOutputHelper output) : IDisposable
{
    private const string Lane = "L1";
    private const string HomeState = "33";

    private readonly TempDatabase _temp = new();
    private readonly RecordingDrawerService _drawer = new();

    public void Dispose() => _temp.Dispose();

    private InvoiceRepository Invoices => new(_temp.Database);
    private CustomerRepository Customers => new(_temp.Database);
    private DayCloseRepository Closes => new(_temp.Database, new HeldBillRepository(_temp.Database));

    private CheckoutService NewCheckout() => new(Invoices, Customers, _drawer);

    private void SeedCatalogue() => _temp.Items.AddRange(
    [
        Catalogue.Item(sku: "DAL001", barcode: "8901234567890", name: "Toor Dal 1kg", price: 189m, gstRate: 5m),
        Catalogue.Item(sku: "RICE01", barcode: "8901234567920", name: "Basmati Rice 5kg", price: 649m, gstRate: 5m),
    ]);

    private SettledInvoice Sell(string barcode = "8901234567890", TenderType tender = TenderType.Cash, Customer? customer = null, int redeemPoints = 0)
    {
        var bill = new InvoiceEngine(HomeState);
        bill.AddItem(_temp.Items.FindByBarcode(barcode)!);

        if (customer is not null)
            bill.SetCustomer(customer);

        var checkout = NewCheckout();
        var total = bill.Totals.GrandTotal;
        var basket = new TenderBasket(total);

        if (redeemPoints > 0)
        {
            var redemption = checkout.Redeem(total, customer, redeemPoints);
            basket.Add(TenderType.LoyaltyPoints, redemption.Value, $"{redemption.Points} points");
            basket.Add(tender, total - redemption.Value);
            return checkout.Complete(Lane, bill, basket, redemption.Points).Invoice;
        }

        basket.Add(tender, total);
        return checkout.Complete(Lane, bill, basket).Invoice;
    }

    // ---- What a void does ----------------------------------------------------------------------

    [Fact]
    public void AVoidedSaleStaysInTheBooksMarkedCancelled()
    {
        SeedCatalogue();
        var sale = Sell();

        var result = NewCheckout().VoidSale(sale.InvoiceNo, "rung up twice");

        Assert.True(result.Invoice.IsVoided);
        Assert.NotNull(result.Invoice.VoidedAt);
        Assert.Equal("rung up twice", result.Invoice.VoidReason);

        // Still there, still readable, still its own number.
        var reloaded = Invoices.FindByInvoiceNo(sale.InvoiceNo);
        Assert.NotNull(reloaded);
        Assert.True(reloaded.IsVoided);
        Assert.Equal(sale.GrandTotal, reloaded.GrandTotal);
        Assert.Single(reloaded.Sale.Lines);
    }

    /// <summary>
    /// A number that vanished is harder to explain than one that is visibly void, and a GST invoice
    /// run has to be unbroken.
    /// </summary>
    [Fact]
    public void TheNumberStaysUsedAndTheRunStaysUnbroken()
    {
        SeedCatalogue();

        var first = Sell();
        NewCheckout().VoidSale(first.InvoiceNo, null);
        var second = Sell();

        Assert.EndsWith("-000001", first.InvoiceNo);
        Assert.EndsWith("-000002", second.InvoiceNo);
    }

    [Fact]
    public void VoidingTwiceIsRefused()
    {
        SeedCatalogue();
        var sale = Sell();

        NewCheckout().VoidSale(sale.InvoiceNo, null);

        var ex = Assert.Throws<InvalidOperationException>(() => NewCheckout().VoidSale(sale.InvoiceNo, null));
        Assert.Contains("already been voided", ex.Message);
    }

    [Fact]
    public void VoidingSomethingThatDoesNotExistIsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => NewCheckout().VoidSale("L9-2026-000404", null));
        Assert.Contains("no invoice numbered", ex.Message);
    }

    /// <summary>Cash taken on the original sale has to come back out of the drawer.</summary>
    [Fact]
    public void VoidingACashSaleOpensTheDrawer()
    {
        SeedCatalogue();
        var sale = Sell(tender: TenderType.Cash);
        var kicksBefore = _drawer.KickCount;

        NewCheckout().VoidSale(sale.InvoiceNo, null);

        Assert.Equal(kicksBefore + 1, _drawer.KickCount);
    }

    [Fact]
    public void VoidingACardSaleLeavesTheDrawerShut()
    {
        SeedCatalogue();
        var sale = Sell(tender: TenderType.Card);
        var kicksBefore = _drawer.KickCount;

        NewCheckout().VoidSale(sale.InvoiceNo, null);

        Assert.Equal(kicksBefore, _drawer.KickCount);
    }

    // ---- Loyalty -------------------------------------------------------------------------------

    /// <summary>
    /// A sale that no longer exists must not have spent or earned anything. A customer whose points
    /// were taken by a mis-keyed bill will notice.
    /// </summary>
    [Fact]
    public void PointsSpentAndEarnedAreBothPutBack()
    {
        SeedCatalogue();

        var customer = Customers.Add(new Customer { MobileNo = "9876543210", StateCode = HomeState, LoyaltyBalance = 1_000 });
        var sale = Sell("8901234567920", customer: customer, redeemPoints: 300);

        var afterSale = Customers.FindByMobile("9876543210")!.LoyaltyBalance;
        Assert.NotEqual(1_000, afterSale);

        var result = NewCheckout().VoidSale(sale.InvoiceNo, null);

        Assert.True(result.LoyaltyReversed);
        Assert.Equal(1_000, result.NewLoyaltyBalance);
        Assert.Equal(1_000, Customers.FindByMobile("9876543210")!.LoyaltyBalance);

        output.WriteLine($"balance 1000 -> {afterSale} -> {result.NewLoyaltyBalance}");
    }

    [Fact]
    public void AWalkInSaleHasNoPointsToPutBack()
    {
        SeedCatalogue();
        var sale = Sell();

        var result = NewCheckout().VoidSale(sale.InvoiceNo, null);

        Assert.False(result.LoyaltyReversed);
        Assert.Null(result.NewLoyaltyBalance);
    }

    /// <summary>A balance can never go negative, however the points have moved since.</summary>
    [Fact]
    public void ReversingNeverDrivesABalanceNegative()
    {
        SeedCatalogue();

        var customer = Customers.Add(new Customer { MobileNo = "9876543210", StateCode = HomeState, LoyaltyBalance = 0 });
        var sale = Sell("8901234567920", customer: customer);

        // The sale earned points; spend them elsewhere before the void.
        Customers.UpdateLoyaltyBalance(customer.Id, 0);
        customer.LoyaltyBalance = 0;

        var result = NewCheckout().VoidSale(sale.InvoiceNo, null);

        Assert.True(result.NewLoyaltyBalance >= 0);
        Assert.True(Customers.FindByMobile("9876543210")!.LoyaltyBalance >= 0);
    }

    // ---- The day-end boundary -------------------------------------------------------------------

    /// <summary>
    /// Once a day is closed its figures have been printed and filed. Changing them afterwards
    /// alters a number somebody has already acted on, and that correction is a credit note.
    /// </summary>
    [Fact]
    public void AnInvoiceAlreadyOnAZReportCannotBeVoided()
    {
        SeedCatalogue();
        var sale = Sell();

        Closes.Close(Lane, DateTimeOffset.Now);

        var ex = Assert.Throws<InvalidOperationException>(() => NewCheckout().VoidSale(sale.InvoiceNo, null));
        Assert.Contains("credit note", ex.Message);

        Assert.False(Invoices.FindByInvoiceNo(sale.InvoiceNo)!.IsVoided);
    }

    [Fact]
    public void AnUnreportedInvoiceCanStillBeVoided()
    {
        SeedCatalogue();

        var first = Sell();
        Closes.Close(Lane, DateTimeOffset.Now);

        var second = Sell();

        Assert.True(Invoices.IsReported(first.InvoiceNo));
        Assert.False(Invoices.IsReported(second.InvoiceNo));

        NewCheckout().VoidSale(second.InvoiceNo, null);
        Assert.True(Invoices.FindByInvoiceNo(second.InvoiceNo)!.IsVoided);
    }

    // ---- The Z-report ---------------------------------------------------------------------------

    /// <summary>Voided sales are not takings and carry no tax.</summary>
    [Fact]
    public void AVoidedSaleIsLeftOutOfTakingsAndTax()
    {
        SeedCatalogue();

        var kept = Sell("8901234567890");
        var voided = Sell("8901234567920");

        NewCheckout().VoidSale(voided.InvoiceNo, null);

        var day = Closes.Close(Lane, DateTimeOffset.Now);

        Assert.Equal(1, day.InvoiceCount);
        Assert.Equal(kept.GrandTotal, day.NetSales);
        Assert.Equal(1, day.VoidedCount);
        Assert.Equal(voided.GrandTotal, day.VoidedValue);
    }

    /// <summary>
    /// A report that simply omitted them could not be reconciled against the invoice run — the
    /// numbers would have gaps with no explanation.
    /// </summary>
    [Fact]
    public void TheVoidedCountAndValueAppearOnTheReport()
    {
        SeedCatalogue();

        Sell("8901234567890");
        var voided = Sell("8901234567920");
        NewCheckout().VoidSale(voided.InvoiceNo, null);

        var day = Closes.Close(Lane, DateTimeOffset.Now);
        var report = new ZReportComposer(new StoreProfile { Name = "Test Stores" }).Compose(day).ToPlainText();

        output.WriteLine(report);

        Assert.Contains("Voided", report);
        Assert.Contains("Invoices voided", report);
        Assert.Contains(voided.GrandTotal.ToString("N2"), report);
        Assert.Contains("Excluded from sales and tax", report);
    }

    /// <summary>A voided sale must not be counted again on the next day's report.</summary>
    [Fact]
    public void AVoidedSaleIsReportedOnceAndNeverAgain()
    {
        SeedCatalogue();

        var voided = Sell();
        NewCheckout().VoidSale(voided.InvoiceNo, null);

        var first = Closes.Close(Lane, DateTimeOffset.Now);
        var second = Closes.Close(Lane, DateTimeOffset.Now.AddMinutes(1));

        Assert.Equal(1, first.VoidedCount);
        Assert.Equal(0, second.VoidedCount);
        Assert.Equal(0m, second.VoidedValue);
    }

    [Fact]
    public void ADayOfOnlyVoidedSalesStillReconciles()
    {
        SeedCatalogue();

        var one = Sell();
        var two = Sell("8901234567920");
        NewCheckout().VoidSale(one.InvoiceNo, null);
        NewCheckout().VoidSale(two.InvoiceNo, null);

        var day = Closes.Close(Lane, DateTimeOffset.Now);

        Assert.True(day.TookNothing);
        Assert.Equal(0m, day.NetSales);
        Assert.Equal(2, day.VoidedCount);
        Assert.Equal(one.GrandTotal + two.GrandTotal, day.VoidedValue);
    }

    [Fact]
    public void TheVoidedTotalsSurviveTheRoundTrip()
    {
        SeedCatalogue();

        Sell("8901234567890");
        var voided = Sell("8901234567920");
        NewCheckout().VoidSale(voided.InvoiceNo, null);

        var closed = Closes.Close(Lane, DateTimeOffset.Now);
        var reloaded = Closes.FindById(closed.Id)!;

        Assert.Equal(closed.VoidedCount, reloaded.VoidedCount);
        Assert.Equal(closed.VoidedValue, reloaded.VoidedValue);
        Assert.Equal(closed.NetSales, reloaded.NetSales);
    }

    /// <summary>The last bill for a reprint should be the last one that still stands.</summary>
    [Fact]
    public void AVoidedSaleIsNotOfferedAsTheLastBill()
    {
        SeedCatalogue();

        var kept = Sell("8901234567890");
        var voided = Sell("8901234567920");
        NewCheckout().VoidSale(voided.InvoiceNo, null);

        Assert.Equal(kept.InvoiceNo, Invoices.FindLatest(Lane)!.InvoiceNo);

        // But it can still be looked up by number, so a duplicate can be shown to a customer.
        Assert.NotNull(Invoices.FindByInvoiceNo(voided.InvoiceNo));
    }
}
