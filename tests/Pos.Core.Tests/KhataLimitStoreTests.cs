using Pos.Core.Analytics;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// A customer's khata limit kept on their record, when they last paid back, and a sale past the
/// limit in the owner's exceptions.
/// </summary>
public class KhataLimitStoreTests : IDisposable
{
    private readonly TempDatabase _temp = new();

    public void Dispose() => _temp.Dispose();

    private CustomerRepository Customers => new(_temp.Database);

    private Customer Lakshmi() => Customers.FindByMobile("9500012345") ?? Customers.Add(new Customer { MobileNo = "9500012345", Name = "Lakshmi" });

    [Fact]
    public void ANewCustomerHasNoLimit() => Assert.Null(Lakshmi().CreditLimit);

    [Fact]
    public void TheLimitIsKeptAndReadBackAndCanBeTakenOff()
    {
        var lakshmi = Lakshmi();

        Customers.SetCreditLimit(lakshmi.Id, 2_500.50m);
        Assert.Equal(2_500.50m, Customers.FindByMobile(lakshmi.MobileNo)!.CreditLimit);

        // Found by search as well, which is how the till finds them.
        Assert.Equal(2_500.50m, Customers.Search("Lak").Single().CreditLimit);

        Customers.SetCreditLimit(lakshmi.Id, null);
        Assert.Null(Customers.FindByMobile(lakshmi.MobileNo)!.CreditLimit);
    }

    [Fact]
    public void ALimitBelowNothingIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Customers.SetCreditLimit(Lakshmi().Id, -1m));

    [Fact]
    public void ALimitInFractionsOfAPaisaIsRefused() =>
        Assert.Throws<ArgumentException>(() => Customers.SetCreditLimit(Lakshmi().Id, 100.005m));

    [Fact]
    public void ACustomerNoLongerOnFileIsSaidSo() =>
        Assert.Throws<InvalidOperationException>(() => Customers.SetCreditLimit(99_999, 100m));

    // ---- When they last paid ---------------------------------------------------------------------

    [Fact]
    public void SomebodyWhoHasNeverPaidBackHasNoLastPayment() =>
        Assert.Null(new CreditRepository(_temp.Database).LastPaid(Lakshmi().Id));

    [Fact]
    public void TheLastPaymentIsTheNewest()
    {
        var lakshmi = Lakshmi();
        _temp.Items.AddRange([Catalogue.Item(sku: "DAL001", barcode: "8901234567890", name: "Toor Dal 1kg", price: 189m)]);

        var bill = new InvoiceEngine("33");
        bill.AddItem(_temp.Items.FindBySku("DAL001")!);
        bill.SetCustomer(lakshmi);
        var basket = new TenderBasket(bill.Totals.GrandTotal);
        basket.Add(TenderType.StoreCredit, bill.Totals.GrandTotal);
        new CheckoutService(new InvoiceRepository(_temp.Database), Customers, new RecordingDrawerService()).Complete("L1", bill, basket);

        var credit = new CreditRepository(_temp.Database);
        var first = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.FromHours(5.5));
        var second = first.AddDays(12);

        credit.Collect(lakshmi.Id, 50m, TenderType.Cash, "L1", second, "Murugan");
        credit.Collect(lakshmi.Id, 20m, TenderType.Upi, "L1", first, "Murugan");

        Assert.Equal(second, credit.LastPaid(lakshmi.Id));
    }

    // ---- In the owner's exceptions ---------------------------------------------------------------

    [Fact]
    public void ASalePastTheLimitIsCountedInTheExceptions()
    {
        var now = DateTimeOffset.Now;
        new TillEventRepository(_temp.Database).Record("L1", now, TillEventKind.OverKhataLimit, "Murugan", "Lakshmi", 189m, approved: true,
            detail: "Lakshmi owes ₹378.00 against a limit of ₹300.00");

        var murugan = Assert.Single(new DashboardQuery(_temp.Database).Gather("L1", now.AddDays(-1), now.AddDays(1)).Exceptions.ByCashier);

        Assert.Equal(1, murugan.OverKhataLimit);
        Assert.Equal(189m, murugan.OverKhataLimitValue);
        Assert.Equal(1, murugan.Total);
    }
}
