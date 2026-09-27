using Pos.App.ViewModels;
using Pos.Core.Analytics;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// The owner's view of credit: what the shop is owed in all, who owes it, and one customer's khata.
/// </summary>
public class OwnerKhataTests : IDisposable
{
    private const string Lane = "L1";

    private readonly TempDatabase _temp = new();

    public void Dispose() => _temp.Dispose();

    private CustomerRepository Customers => new(_temp.Database);

    private CreditRepository Credit => new(_temp.Database);

    private CustomersViewModel Screen() => new(new CustomerQuery(_temp.Database), Customers, Credit);

    private Customer Known(string mobile, string name) =>
        Customers.Add(new Customer { MobileNo = mobile, Name = name });

    /// <summary>A sale of the given value, all of it on the customer's khata.</summary>
    private void OnCredit(Customer customer, decimal price)
    {
        var sku = $"SKU{Guid.NewGuid():N}"[..12];
        _temp.Items.UpsertRange([Catalogue.Item(sku: sku, name: "Toor Dal 1kg", price: price)]);

        var bill = new InvoiceEngine("33");
        bill.AddItem(_temp.Items.FindBySku(sku)!);
        bill.SetCustomer(customer);

        var basket = new TenderBasket(bill.Totals.AmountPayable);
        basket.Add(TenderType.StoreCredit, bill.Totals.AmountPayable);

        new CheckoutService(new InvoiceRepository(_temp.Database), Customers, new RecordingDrawerService())
            .Complete(Lane, bill, basket);
    }

    [Fact]
    public void TheListSaysWhatTheShopIsOwedInAll()
    {
        OnCredit(Known("9000000001", "Lakshmi"), 300m);
        OnCredit(Known("9000000002", "Ravi"), 200m);

        var screen = Screen();
        screen.Search();

        Assert.Equal("500.00 owed to the shop by 2 customer(s).", screen.TotalOwedLine);
    }

    [Fact]
    public void AShopOwedNothingSaysSo()
    {
        Known("9000000001", "Lakshmi");

        var screen = Screen();
        screen.Search();

        Assert.Equal("Nobody owes the shop anything on credit.", screen.TotalOwedLine);
    }

    /// <summary>The end-of-month list: only those who owe, most first.</summary>
    [Fact]
    public void OnlyOwingListsWhoOwesMostFirst()
    {
        OnCredit(Known("9000000001", "Small"), 100m);
        OnCredit(Known("9000000002", "Big"), 400m);
        Known("9000000003", "Owes nothing");

        var screen = Screen();
        screen.OnlyOwing = true;

        Assert.Equal("WHO OWES WHAT", screen.ResultsHeading);
        Assert.Equal(["Big", "Small"], screen.Results.Select(r => r.Label));
        Assert.Equal(400m, screen.Results[0].Owed);
    }

    [Fact]
    public void PickingSomebodyShowsWhatTheyOweAndTheirKhata()
    {
        var lakshmi = Known("9000000001", "Lakshmi");
        OnCredit(lakshmi, 300m);
        Credit.Collect(lakshmi.Id, 100m, TenderType.Cash, Lane, DateTimeOffset.Now.AddSeconds(1), null);

        var screen = Screen();
        screen.Search();
        screen.Selected = screen.Results[0];

        Assert.True(screen.OwesAnything);
        Assert.Equal("Owes 200.00 on credit", screen.Owes);
        Assert.True(screen.HasKhata);
        Assert.Equal(2, screen.Khata.Count);
        Assert.Equal(200m, screen.Khata[0].BalanceAfter);
    }

    /// <summary>Forgetting must not forgive: the screen says why it will not.</summary>
    [Fact]
    public void ACustomerWhoOwesCannotBeForgottenFromTheScreen()
    {
        OnCredit(Known("9000000001", "Lakshmi"), 300m);

        var screen = Screen();
        screen.Search();
        screen.Selected = screen.Results[0];

        var problem = screen.Forget();

        Assert.NotNull(problem);
        Assert.Contains("owe 300.00", problem);
        Assert.Single(screen.Results);
    }
}
