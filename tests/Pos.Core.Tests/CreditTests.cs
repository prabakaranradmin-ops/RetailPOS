using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Domain.Printing;
using Pos.TestSupport;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// Customer credit (khata): buying on credit, what is owed, and paying it back.
/// </summary>
/// <remarks>
/// What a customer owes is never stored; every figure here is read back from the bills and the
/// repayments. These tests hold the rules that keep that honest: a debt needs somebody to owe it,
/// a void takes it back off, a repayment cannot overshoot, and a customer who owes cannot be
/// forgotten out of it.
/// </remarks>
public class CreditTests : IDisposable
{
    private const string Lane = "L1";
    private const string HomeState = "33";

    private readonly TempDatabase _temp = new();

    public void Dispose() => _temp.Dispose();

    private CreditRepository Credit => new(_temp.Database);

    private CustomerRepository Customers => new(_temp.Database);

    private Customer Known(string mobile = "9876543210", string? name = "Lakshmi") =>
        Customers.Add(new Customer { MobileNo = mobile, Name = name });

    private Item Stocked(decimal price = 100m)
    {
        _temp.Items.UpsertRange([Catalogue.Item(sku: "DAL001", name: "Toor Dal 1kg", price: price)]);
        return _temp.Items.FindBySku("DAL001")!;
    }

    private CheckoutService Checkout() => new(
        new InvoiceRepository(_temp.Database),
        new CustomerRepository(_temp.Database),
        new RecordingDrawerService());

    /// <summary>One sale, paid partly or wholly on store credit.</summary>
    private SettledInvoice Sell(Customer? customer, decimal onCredit, decimal inCash = 0m, int quantity = 1)
    {
        var bill = new InvoiceEngine(HomeState);
        bill.AddItem(Stocked());

        if (quantity != 1)
            bill.SetQuantity(0, quantity);

        if (customer is not null)
            bill.SetCustomer(customer);

        var basket = new TenderBasket(bill.Totals.AmountPayable);

        if (inCash > 0m)
            basket.Add(TenderType.Cash, inCash);

        if (onCredit > 0m)
            basket.Add(TenderType.StoreCredit, onCredit);

        return Checkout().Complete(Lane, bill, basket).Invoice;
    }

    // ---- Owing ------------------------------------------------------------------------------------

    [Fact]
    public void BuyingOnCreditMakesTheCustomerOweIt()
    {
        var lakshmi = Known();

        Sell(lakshmi, onCredit: 100m);

        Assert.Equal(100m, Credit.Balance(lakshmi.Id));
    }

    /// <summary>Half now, half on the khata: only the half on credit is owed.</summary>
    [Fact]
    public void OnlyThePartPaidOnCreditIsOwed()
    {
        var lakshmi = Known();

        Sell(lakshmi, onCredit: 150m, inCash: 50m, quantity: 2);

        Assert.Equal(150m, Credit.Balance(lakshmi.Id));
    }

    /// <summary>
    /// A debt needs somebody to owe it. Refused in checkout itself, so no screen and no command
    /// can create a credit sale for a walk-in.
    /// </summary>
    [Fact]
    public void AWalkInCannotBuyOnCredit()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Sell(customer: null, onCredit: 100m));

        Assert.Contains("needs a customer", error.Message);
        Assert.Null(new InvoiceRepository(_temp.Database).FindLatest(Lane));
    }

    /// <summary>A cancelled sale was never bought, on credit or otherwise.</summary>
    [Fact]
    public void VoidingACreditSaleTakesItBackOff()
    {
        var lakshmi = Known();

        Sell(lakshmi, onCredit: 100m);
        var mistake = Sell(lakshmi, onCredit: 100m);

        Checkout().VoidSale(mistake.InvoiceNo, reason: "rung up twice");

        Assert.Equal(100m, Credit.Balance(lakshmi.Id));
    }

    // ---- Paying back ------------------------------------------------------------------------------

    [Fact]
    public void ARepaymentComesOffWhatIsOwed()
    {
        var lakshmi = Known();
        Sell(lakshmi, onCredit: 300m, quantity: 3);

        var payment = Credit.Collect(lakshmi.Id, 120m, TenderType.Cash, Lane, DateTimeOffset.Now, "Priya");

        Assert.Equal(120m, payment.Amount);
        Assert.Equal(180m, Credit.Balance(lakshmi.Id));
    }

    [Fact]
    public void ThePaymentCanBeUpiOrCard()
    {
        var lakshmi = Known();
        Sell(lakshmi, onCredit: 200m, quantity: 2);

        Credit.Collect(lakshmi.Id, 100m, TenderType.Upi, Lane, DateTimeOffset.Now, null);
        Credit.Collect(lakshmi.Id, 100m, TenderType.Card, Lane, DateTimeOffset.Now, null);

        Assert.Equal(0m, Credit.Balance(lakshmi.Id));
    }

    /// <summary>
    /// More than is owed would leave the shop in the customer's debt - an advance, which is a
    /// different thing and not one to create by mistyping an amount.
    /// </summary>
    [Fact]
    public void TakingMoreThanIsOwedIsRefused()
    {
        var lakshmi = Known();
        Sell(lakshmi, onCredit: 100m);

        var error = Assert.Throws<InvalidOperationException>(
            () => Credit.Collect(lakshmi.Id, 150m, TenderType.Cash, Lane, DateTimeOffset.Now, null));

        Assert.Contains("owe 100.00", error.Message);
        Assert.Equal(100m, Credit.Balance(lakshmi.Id));
    }

    [Fact]
    public void NothingCanBeTakenFromSomebodyWhoOwesNothing()
    {
        var lakshmi = Known();

        Assert.Throws<InvalidOperationException>(
            () => Credit.Collect(lakshmi.Id, 10m, TenderType.Cash, Lane, DateTimeOffset.Now, null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ARepaymentHasToBeMoreThanNothing(int amount)
    {
        var lakshmi = Known();
        Sell(lakshmi, onCredit: 100m);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => Credit.Collect(lakshmi.Id, amount, TenderType.Cash, Lane, DateTimeOffset.Now, null));
    }

    [Fact]
    public void ARepaymentCannotBeFinerThanAPaisa()
    {
        var lakshmi = Known();
        Sell(lakshmi, onCredit: 100m);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => Credit.Collect(lakshmi.Id, 10.005m, TenderType.Cash, Lane, DateTimeOffset.Now, null));
    }

    /// <summary>A debt paid with more credit is the same debt; one paid in points is the shop paying itself.</summary>
    [Theory]
    [InlineData(TenderType.StoreCredit)]
    [InlineData(TenderType.LoyaltyPoints)]
    public void ARepaymentIsTakenInMoney(TenderType tender)
    {
        var lakshmi = Known();
        Sell(lakshmi, onCredit: 100m);

        Assert.Throws<ArgumentException>(
            () => Credit.Collect(lakshmi.Id, 50m, tender, Lane, DateTimeOffset.Now, null));
    }

    // ---- The khata --------------------------------------------------------------------------------

    [Fact]
    public void TheHistoryListsPurchasesAndPaymentsWithTheBalanceAfterEach()
    {
        var lakshmi = Known();
        var bill = Sell(lakshmi, onCredit: 200m, quantity: 2);
        Credit.Collect(lakshmi.Id, 50m, TenderType.Cash, Lane, DateTimeOffset.Now.AddSeconds(1), null);

        var history = Credit.History(lakshmi.Id);

        // Newest first.
        Assert.Equal(2, history.Count);
        Assert.Equal(-50m, history[0].Change);
        Assert.Equal(150m, history[0].BalanceAfter);
        Assert.Contains("cash", history[0].Description);

        Assert.Equal(200m, history[1].Change);
        Assert.Equal(200m, history[1].BalanceAfter);
        Assert.Contains(bill.InvoiceNo, history[1].Description);
    }

    [Fact]
    public void WhoOwesWhatListsOnlyThoseWhoOweMostFirst()
    {
        var small = Known("9000000001", "Occasional");
        var big = Known("9000000002", "Regular");
        var settled = Known("9000000003", "Settled");
        Known("9000000004", "Never on credit");

        Sell(small, onCredit: 100m);
        Sell(big, onCredit: 300m, quantity: 3);
        Sell(settled, onCredit: 100m);
        Credit.Collect(settled.Id, 100m, TenderType.Cash, Lane, DateTimeOffset.Now, null);

        var owing = Credit.Owing();

        Assert.Equal(["Regular", "Occasional"], owing.Select(o => o.Label));
        Assert.Equal(300m, owing[0].Owed);
    }

    // ---- The day-end report -----------------------------------------------------------------------

    private DayCloseRepository Closes => new(_temp.Database, new HeldBillRepository(_temp.Database));

    private static string Print(DayCloseSummary day) =>
        new ZReportComposer(
                new StoreProfile { Name = "Sri Lakshmi Stores", Gstin = "33AABCS1429B1ZX" }, 48,
                ReceiptLanguage.English, TaxMode.Gst)
            .Compose(day).ToPlainText();

    /// <summary>
    /// Cash paid back is in the drawer, so it is in what the drawer should hold. It is not a sale,
    /// so it is in nothing else - and the report still reconciles.
    /// </summary>
    [Fact]
    public void ACashRepaymentIsInTheDrawerButNotInSales()
    {
        var lakshmi = Known();
        Sell(lakshmi, onCredit: 100m);
        Closes.Close(Lane, DateTimeOffset.Now);

        // A later day: one cash sale, and 60 paid back on the khata.
        Sell(Known("9000000009", "Walk-in with a number"), onCredit: 0m, inCash: 100m);
        Credit.Collect(lakshmi.Id, 60m, TenderType.Cash, Lane, DateTimeOffset.Now, null);

        var day = Closes.Close(Lane, DateTimeOffset.Now);

        Assert.Equal(100m, day.NetSales);
        Assert.Equal(160m, day.CashExpected);
        Assert.Equal(60m, day.CreditCollected);
        Assert.Equal(60m, day.CreditCollectedCash);

        var printed = Print(day);
        Assert.Contains("Credit collected (1)", printed);
        Assert.Contains("Credit collected in cash", printed);
        Assert.Contains("Reconciled", printed);
    }

    /// <summary>Paid back by UPI: collected, but the bank's, not the drawer's.</summary>
    [Fact]
    public void AUpiRepaymentIsCollectedButNotInTheDrawer()
    {
        var lakshmi = Known();
        Sell(lakshmi, onCredit: 100m);
        Closes.Close(Lane, DateTimeOffset.Now);

        Credit.Collect(lakshmi.Id, 100m, TenderType.Upi, Lane, DateTimeOffset.Now, null);

        var day = Closes.Close(Lane, DateTimeOffset.Now);

        Assert.Equal(100m, day.CreditCollected);
        Assert.Equal(0m, day.CreditCollectedCash);
        Assert.Equal(100m, day.CreditCollectedToBank);
        Assert.Equal(0m, day.CashExpected);
    }

    /// <summary>A repayment belongs to one Z-report. Closing again must not count it twice.</summary>
    [Fact]
    public void ARepaymentIsOnExactlyOneReport()
    {
        var lakshmi = Known();
        Sell(lakshmi, onCredit: 100m);
        Credit.Collect(lakshmi.Id, 40m, TenderType.Cash, Lane, DateTimeOffset.Now, null);

        var first = Closes.Close(Lane, DateTimeOffset.Now);
        var second = Closes.Close(Lane, DateTimeOffset.Now);

        Assert.Equal(40m, first.CreditCollected);
        Assert.Equal(0m, second.CreditCollected);
    }

    /// <summary>
    /// "No sales" is not "no money". A day on which somebody only came in to settle their khata
    /// still has cash to count, and the report has to say so rather than print "no sales" alone.
    /// </summary>
    [Fact]
    public void ADayWithOnlyRepaymentsStillReportsTheCash()
    {
        var lakshmi = Known();
        Sell(lakshmi, onCredit: 100m);
        Closes.Close(Lane, DateTimeOffset.Now);

        Credit.Collect(lakshmi.Id, 100m, TenderType.Cash, Lane, DateTimeOffset.Now, null);

        var preview = Closes.Preview(Lane, DateTimeOffset.Now);
        Assert.True(preview.TookNothing);
        Assert.True(preview.CollectedCredit);

        var printed = Print(Closes.Close(Lane, DateTimeOffset.Now));

        Assert.Contains("NO SALES IN THIS PERIOD", printed);
        Assert.Contains("CASH IN DRAWER SHOULD BE", printed);
        Assert.Contains("Credit collected (1)", printed);
    }

    /// <summary>
    /// A reprint says what the report said on the night. Change is not stored - it is worked back
    /// from the drawer figure - so the cash repayment has to come off first, or a reprint would show
    /// it as negative change.
    /// </summary>
    [Fact]
    public void AReprintedReportShowsTheSameChangeAndCollections()
    {
        var lakshmi = Known();
        Sell(lakshmi, onCredit: 100m);
        Closes.Close(Lane, DateTimeOffset.Now);

        Sell(Known("9000000009", "Cash buyer"), onCredit: 0m, inCash: 100m);
        Credit.Collect(lakshmi.Id, 70m, TenderType.Cash, Lane, DateTimeOffset.Now, null);
        var night = Closes.Close(Lane, DateTimeOffset.Now);

        var reprint = Closes.FindById(night.Id)!;

        Assert.Equal(night.ChangeGiven, reprint.ChangeGiven);
        Assert.Equal(night.CashExpected, reprint.CashExpected);
        Assert.Equal(70m, reprint.CreditCollected);
        Assert.Equal(1, reprint.CreditCollectedCount);
    }

    /// <summary>
    /// The by-cashier split says whose shift a drawer difference is on. Cash one of them took back on
    /// credit is in the drawer on their shift, so it is in what they hold.
    /// </summary>
    [Fact]
    public void CashTakenBackCountsTowardsTheCashierWhoTookIt()
    {
        var lakshmi = Known();
        Sell(lakshmi, onCredit: 100m);
        Closes.Close(Lane, DateTimeOffset.Now);

        Credit.Collect(lakshmi.Id, 50m, TenderType.Cash, Lane, DateTimeOffset.Now, "Priya");

        var day = Closes.Close(Lane, DateTimeOffset.Now);

        var priya = Assert.Single(day.CashierTotals, c => c.Name == "Priya");
        Assert.Equal(50m, priya.CashHeld);
        Assert.Equal(day.CashExpected, day.CashierTotals.Sum(c => c.CashHeld));
    }

    // ---- Forgetting -------------------------------------------------------------------------------

    /// <summary>Forgetting a customer must not forgive their debt by deleting who it belongs to.</summary>
    [Fact]
    public void ACustomerWhoOwesCannotBeForgotten()
    {
        var lakshmi = Known();
        Sell(lakshmi, onCredit: 100m);

        var error = Assert.Throws<InvalidOperationException>(() => Customers.Forget(lakshmi.Id));

        Assert.Contains("owe 100.00", error.Message);
        Assert.NotNull(Customers.FindByMobile("9876543210"));
    }

    /// <summary>
    /// Once settled they can be forgotten, and the money they paid back stays in the books: it was
    /// received, and the day it was received on still has to reconcile.
    /// </summary>
    [Fact]
    public void OnceSettledTheyCanBeForgottenAndTheRepaymentStays()
    {
        var lakshmi = Known();
        Sell(lakshmi, onCredit: 100m);
        Credit.Collect(lakshmi.Id, 100m, TenderType.Cash, Lane, DateTimeOffset.Now, null);

        Customers.Forget(lakshmi.Id);

        Assert.Null(Customers.FindByMobile("9876543210"));

        using var connection = _temp.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), SUM(customer_id IS NULL) FROM credit_payments;";
        using var reader = command.ExecuteReader();
        reader.Read();

        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(1, reader.GetInt32(1));
    }

    /// <summary>
    /// A credit sale voided after it was paid back leaves the shop owing the customer. That is not
    /// a debt to list, and nothing more can be taken - but it is still not a reason to forget them.
    /// </summary>
    [Fact]
    public void WhenTheShopOwesTheCustomerNothingCanBeTakenAndTheyAreNotForgotten()
    {
        var lakshmi = Known();
        var sale = Sell(lakshmi, onCredit: 100m);
        Credit.Collect(lakshmi.Id, 100m, TenderType.Cash, Lane, DateTimeOffset.Now, null);
        Checkout().VoidSale(sale.InvoiceNo, reason: "returned");

        Assert.Equal(-100m, Credit.Balance(lakshmi.Id));
        Assert.Empty(Credit.Owing());
        Assert.Throws<InvalidOperationException>(
            () => Credit.Collect(lakshmi.Id, 10m, TenderType.Cash, Lane, DateTimeOffset.Now, null));

        var error = Assert.Throws<InvalidOperationException>(() => Customers.Forget(lakshmi.Id));
        Assert.Contains("shop owes them", error.Message);
    }
}
