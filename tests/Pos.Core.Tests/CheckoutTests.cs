using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Hardware.Drawer;
using Pos.Core.Loyalty;
using Pos.TestSupport;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// Settlement end to end, against a real database: tender the bill, apply loyalty, write the
/// invoice down, kick the drawer.
/// </summary>
public class CheckoutTests : IDisposable
{
    private const string Lane = "L1";
    private const string HomeState = "33";

    private readonly TempDatabase _temp = new();
    private readonly RecordingDrawerService _drawer = new();

    private InvoiceRepository Invoices => new(_temp.Database);
    private CustomerRepository Customers => new(_temp.Database);

    private CheckoutService NewCheckout(LoyaltyRules? rules = null) =>
        new(Invoices, Customers, _drawer, rules, TimeProvider.System);

    private InvoiceEngine BillWith(params (decimal Price, decimal Gst)[] items)
    {
        var bill = new InvoiceEngine(HomeState);
        var id = 1;

        foreach (var (price, gst) in items)
        {
            bill.AddItem(Catalogue.Item(
                id: id,
                sku: $"SKU{id:D4}",
                barcode: $"890{id:D10}",
                name: $"Item {id}",
                price: price,
                gstRate: gst));
            id++;
        }

        return bill;
    }

    public void Dispose() => _temp.Dispose();

    // ---- The straightforward sale ------------------------------------------------------------

    [Fact]
    public void ACashSaleIsNumberedSavedAndOpensTheDrawer()
    {
        var bill = BillWith((189m, 5m), (249m, 5m));
        var basket = new TenderBasket(bill.Totals.GrandTotal);
        basket.Add(TenderType.Cash, 500.00m);

        var result = NewCheckout().Complete(Lane, bill, basket);

        Assert.Equal($"{Lane}-{DateTimeOffset.Now.Year}-000001", result.Invoice.InvoiceNo);
        Assert.Equal(438.00m, result.Invoice.GrandTotal);
        Assert.Equal(62.00m, result.ChangeDue);
        Assert.Equal(1, _drawer.KickCount);
        Assert.Equal(DrawerKickResult.Opened, result.Drawer);
    }

    [Fact]
    public void ACardOnlySaleLeavesTheDrawerShut()
    {
        var bill = BillWith((189m, 5m));
        var basket = new TenderBasket(bill.Totals.GrandTotal);
        basket.Add(TenderType.Card, 189.00m, "AUTH 88213");

        var result = NewCheckout().Complete(Lane, bill, basket);

        Assert.Equal(0, _drawer.KickCount);
        Assert.Equal(DrawerKickResult.NoDrawerAttached, result.Drawer);
    }

    /// <summary>
    /// Cash as part of a split still means notes going into the drawer and change coming out.
    /// </summary>
    [Fact]
    public void CashInASplitTenderStillOpensTheDrawer()
    {
        var bill = BillWith((500m, 18m));
        var basket = new TenderBasket(bill.Totals.GrandTotal);
        basket.Add(TenderType.Upi, 400.00m, "UPI/1");
        basket.Add(TenderType.Cash, 100.00m);

        NewCheckout().Complete(Lane, bill, basket);

        Assert.Equal(1, _drawer.KickCount);
    }

    [Fact]
    public void ALaneWithNoDrawerConfiguredNeverKicks()
    {
        _drawer.IsConfigured = false;

        var bill = BillWith((189m, 5m));
        var basket = new TenderBasket(bill.Totals.GrandTotal);
        basket.Add(TenderType.Cash, 189.00m);

        var result = NewCheckout().Complete(Lane, bill, basket);

        Assert.Equal(0, _drawer.KickCount);
        Assert.Equal(DrawerKickResult.NoDrawerAttached, result.Drawer);
    }

    /// <summary>
    /// A drawer that will not open is a problem for the shop, not a reason to lose an invoice the
    /// customer has already paid.
    /// </summary>
    [Fact]
    public void ABrokenDrawerDoesNotCostTheSale()
    {
        _drawer.NextResult = DrawerKickResult.Failed;

        var bill = BillWith((189m, 5m));
        var basket = new TenderBasket(bill.Totals.GrandTotal);
        basket.Add(TenderType.Cash, 189.00m);

        var result = NewCheckout().Complete(Lane, bill, basket);

        Assert.Equal(DrawerKickResult.Failed, result.Drawer);
        Assert.NotNull(Invoices.FindByInvoiceNo(result.Invoice.InvoiceNo));
    }

    // ---- Refusals ----------------------------------------------------------------------------

    [Fact]
    public void AnUnderpaidBillCannotBeSettled()
    {
        var bill = BillWith((500m, 18m));
        var basket = new TenderBasket(bill.Totals.GrandTotal);
        basket.Add(TenderType.Cash, 100.00m);

        var ex = Assert.Throws<InvalidOperationException>(() => NewCheckout().Complete(Lane, bill, basket));
        Assert.Contains("still owed", ex.Message);
    }

    [Fact]
    public void AnEmptyBillCannotBeSettled()
    {
        var bill = new InvoiceEngine(HomeState);
        var basket = new TenderBasket(0m);

        Assert.Throws<InvalidOperationException>(() => NewCheckout().Complete(Lane, bill, basket));
    }

    /// <summary>
    /// Catches the case where a line was edited after the tender screen opened, so the payments
    /// were taken against a total that no longer exists.
    /// </summary>
    [Fact]
    public void PaymentsTakenAgainstAStaleTotalAreRefused()
    {
        var bill = BillWith((500m, 18m));
        var basket = new TenderBasket(bill.Totals.GrandTotal);
        basket.Add(TenderType.Cash, 500.00m);

        bill.SetQuantity(0, 2m);

        var ex = Assert.Throws<InvalidOperationException>(() => NewCheckout().Complete(Lane, bill, basket));
        Assert.Contains("comes to", ex.Message);
    }

    [Fact]
    public void PointsCannotBeRedeemedWithoutACustomer()
    {
        var bill = BillWith((500m, 18m));
        var basket = new TenderBasket(bill.Totals.GrandTotal);
        basket.Add(TenderType.Cash, 500.00m);

        var ex = Assert.Throws<InvalidOperationException>(() => NewCheckout().Complete(Lane, bill, basket, pointsRedeemed: 100));
        Assert.Contains("without a customer", ex.Message);
    }

    // ---- Loyalty as a tender -----------------------------------------------------------------

    /// <summary>
    /// The decision recorded in the plan: points settle the bill, they do not discount it. The
    /// taxable value and the GST split must come out identical to the same sale paid in cash.
    /// </summary>
    [Fact]
    public void RedeemingPointsLeavesEveryLineTaxUntouched()
    {
        var withoutPoints = BillWith((1_000m, 18m)).Totals;

        var customer = Customers.Add(new Customer { MobileNo = "9876500001", StateCode = HomeState, LoyaltyBalance = 5_000 });
        var bill = BillWith((1_000m, 18m));
        bill.SetCustomer(customer);

        var checkout = NewCheckout();
        var redemption = checkout.QuoteRedemption(bill.Totals.GrandTotal, customer);

        Assert.Equal(600, redemption.Points);
        Assert.Equal(300.00m, redemption.Value);

        var basket = new TenderBasket(bill.Totals.GrandTotal);
        basket.Add(TenderType.LoyaltyPoints, redemption.Value, $"{redemption.Points} points");
        basket.Add(TenderType.Cash, 700.00m);

        var result = checkout.Complete(Lane, bill, basket, redemption.Points);

        // Same tax as the cash-only bill: points came off the payment, not off the line.
        Assert.Equal(withoutPoints.SubtotalTaxable, result.Invoice.Sale.Totals.SubtotalTaxable);
        Assert.Equal(withoutPoints.TotalCgst, result.Invoice.Sale.Totals.TotalCgst);
        Assert.Equal(withoutPoints.TotalSgst, result.Invoice.Sale.Totals.TotalSgst);
        Assert.Equal(1_000.00m, result.Invoice.GrandTotal);

        // Accrual on the ₹700 net bill, not the ₹1,000 gross.
        Assert.Equal(600, result.PointsRedeemed);
        Assert.Equal(14, result.PointsEarned);
        Assert.Equal(4_414, result.NewLoyaltyBalance);
    }

    [Fact]
    public void TheNewBalanceIsWrittenBackToTheCustomer()
    {
        var customer = Customers.Add(new Customer { MobileNo = "9876500002", StateCode = HomeState, LoyaltyBalance = 1_000 });
        var bill = BillWith((1_000m, 18m));
        bill.SetCustomer(customer);

        var checkout = NewCheckout();
        var redemption = checkout.Redeem(bill.Totals.GrandTotal, customer, 600);

        var basket = new TenderBasket(bill.Totals.GrandTotal);
        basket.Add(TenderType.LoyaltyPoints, redemption.Value, $"{redemption.Points} points");
        basket.Add(TenderType.Cash, 700.00m);

        checkout.Complete(Lane, bill, basket, redemption.Points);

        Assert.Equal(414, Customers.FindByMobile("9876500002")!.LoyaltyBalance);
    }

    [Fact]
    public void ACustomerWhoRedeemsNothingStillEarnsOnTheWholeBill()
    {
        var customer = Customers.Add(new Customer { MobileNo = "9876500003", StateCode = HomeState, LoyaltyBalance = 0 });
        var bill = BillWith((1_000m, 18m));
        bill.SetCustomer(customer);

        var basket = new TenderBasket(bill.Totals.GrandTotal);
        basket.Add(TenderType.Cash, 1_000.00m);

        var result = NewCheckout().Complete(Lane, bill, basket);

        Assert.Equal(0, result.PointsRedeemed);
        Assert.Equal(20, result.PointsEarned);
        Assert.Equal(20, result.NewLoyaltyBalance);
    }

    [Fact]
    public void AWalkInEarnsNothingAndHasNoBalance()
    {
        var bill = BillWith((1_000m, 18m));
        var basket = new TenderBasket(bill.Totals.GrandTotal);
        basket.Add(TenderType.Cash, 1_000.00m);

        var result = NewCheckout().Complete(Lane, bill, basket);

        Assert.Equal(0, result.PointsEarned);
        Assert.Null(result.NewLoyaltyBalance);
    }

    [Fact]
    public void TheSchemeParametersAreConfigurablePerInstallation()
    {
        var rules = new LoyaltyRules(RedemptionCapPercent: 50m, RupeesPerPoint: 1m, RupeesPerPointEarned: 100m);
        var customer = Customers.Add(new Customer { MobileNo = "9876500004", StateCode = HomeState, LoyaltyBalance = 5_000 });
        var bill = BillWith((1_000m, 18m));
        bill.SetCustomer(customer);

        var checkout = NewCheckout(rules);
        var redemption = checkout.QuoteRedemption(bill.Totals.GrandTotal, customer);

        Assert.Equal(500, redemption.Points);
        Assert.Equal(500.00m, redemption.Value);

        var basket = new TenderBasket(bill.Totals.GrandTotal);
        basket.Add(TenderType.LoyaltyPoints, redemption.Value, $"{redemption.Points} points");
        basket.Add(TenderType.Cash, 500.00m);

        var result = checkout.Complete(Lane, bill, basket, redemption.Points);

        Assert.Equal(5, result.PointsEarned);
    }
}
