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

        Assert.Equal("₹500.00 owed to the shop by 2 customers.", screen.TotalOwedLine);
    }

    [Fact]
    public void AShopOwedNothingSaysSo()
    {
        Known("9000000001", "Lakshmi");

        var screen = Screen();
        screen.Search();

        Assert.Equal("Nobody owes the shop anything on the khata.", screen.TotalOwedLine);
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
        Assert.Equal("Owes ₹200.00 on the khata", screen.Owes);
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

    // ---- Statements --------------------------------------------------------------------------------

    private CustomersViewModel StatementScreen(List<string> copied)
    {
        var screen = Screen();
        screen.ShopName = "Sri Murugan Stores";
        screen.Upi = new UpiPayee("murugan.stores@okaxis", "Sri Murugan Stores");
        screen.CopyText = copied.Add;
        screen.RenderStatements = statements => KhataStatementPage.Render(statements, new() { Name = "Sri Murugan Stores" }, screen.Upi);
        return screen;
    }

    [Fact]
    public void AStatementIsCopiedToSendOnWhatsApp()
    {
        var lakshmi = Known("9000000001", "Lakshmi");
        OnCredit(lakshmi, 300m);
        Credit.Collect(lakshmi.Id, 100m, TenderType.Cash, Lane, DateTimeOffset.Now.AddSeconds(1), null);
        var copied = new List<string>();

        var screen = StatementScreen(copied);
        screen.Search();
        screen.Selected = screen.Results[0];

        Assert.Null(screen.CopyStatement());

        var message = Assert.Single(copied);
        Assert.StartsWith("Sri Murugan Stores: khata for Lakshmi", message);
        Assert.Contains("Owed now: Rs 200.00", message);
        Assert.Contains("Pay by UPI to murugan.stores@okaxis", message);
        Assert.Contains("200.00 owed. Paste it into WhatsApp", screen.Status);
    }

    [Fact]
    public void AStatementIsLaidOutAsAPage()
    {
        OnCredit(Known("9000000001", "Lakshmi"), 300m);
        var screen = StatementScreen([]);
        screen.Search();
        screen.Selected = screen.Results[0];

        var page = screen.StatementPage();

        Assert.NotNull(page);
        Assert.Contains("KHATA STATEMENT", page);
        Assert.Contains("Lakshmi", page);
        Assert.Contains("Rs 300.00", page);
    }

    /// <summary>The month-end round: everybody who owes, a page each, nobody who does not.</summary>
    [Fact]
    public void EveryoneWhoOwesGetsAPage()
    {
        OnCredit(Known("9000000001", "Lakshmi"), 300m);
        OnCredit(Known("9000000002", "Ravi"), 200m);
        Known("9000000003", "Owes nothing");
        var screen = StatementScreen([]);

        var page = screen.EveryoneOwingPage();

        Assert.NotNull(page);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(page, "<section class=\"statement\">").Count);
        Assert.DoesNotContain("Owes nothing", page);
    }

    [Fact]
    public void SomebodyWhoOwesNothingHasNoStatement()
    {
        var lakshmi = Known("9000000001", "Lakshmi");
        OnCredit(lakshmi, 300m);
        Credit.Collect(lakshmi.Id, 300m, TenderType.Cash, Lane, DateTimeOffset.Now.AddSeconds(1), null);
        var copied = new List<string>();
        var screen = StatementScreen(copied);
        screen.Search();
        screen.Selected = screen.Results[0];

        Assert.NotNull(screen.CopyStatement());

        Assert.Empty(copied);
        Assert.Contains("owes nothing on the khata", screen.Status);
    }

    [Fact]
    public void WithNobodyOwingThereAreNoStatementsToSave()
    {
        Known("9000000001", "Lakshmi");
        var screen = StatementScreen([]);

        Assert.Null(screen.EveryoneOwingPage());
        Assert.Equal("Nobody owes anything on the khata.", screen.Status);
    }

    // ---- How old the khata is ----------------------------------------------------------------------

    [Fact]
    public void TheListOfThoseWhoOweSaysHowOldItIs()
    {
        OnCredit(Known("9000000001", "Lakshmi"), 300m);
        OnCredit(Known("9000000002", "Ravi"), 200m);

        var screen = Screen();
        screen.OnlyOwing = true;

        // Bought today: all of it under 30 days, and nobody has waited a day yet.
        Assert.Equal("How old: ₹500.00 under 30 days · ₹0.00 31 to 60 · ₹0.00 61 to 90 · ₹0.00 over 90.", screen.AgeingLine);
        Assert.All(screen.Results, r => Assert.Equal(0, r.DaysWaiting));

        screen.OnlyOwing = false;

        Assert.Equal(string.Empty, screen.AgeingLine);
        Assert.False(screen.HasAgeingLine);
    }

    [Fact]
    public void AReminderIsCopiedForSomebodyWhoOwes()
    {
        var lakshmi = Known("9000000001", "Lakshmi");
        OnCredit(lakshmi, 300m);

        string? copied = null;
        var screen = Picked(lakshmi);
        screen.ShopName = "Sri Lakshmi Stores";
        screen.CopyText = text => copied = text;

        Assert.Null(screen.CopyReminder());

        Assert.StartsWith("Dear Lakshmi, a reminder from Sri Lakshmi Stores: Rs 300.00 is due on your khata", copied);
        Assert.Contains("A reminder to Lakshmi is on the clipboard", screen.Status);
    }

    [Fact]
    public void NobodyIsRemindedOfNothing()
    {
        var ravi = Known("9000000002", "Ravi");
        string? copied = null;
        var screen = Picked(ravi);
        screen.CopyText = text => copied = text;

        Assert.NotNull(screen.CopyReminder());
        Assert.Null(copied);
    }

    // ---- The khata limit ---------------------------------------------------------------------------

    private CustomersViewModel Picked(Customer customer)
    {
        var screen = Screen();
        screen.SearchText = customer.Name!;
        screen.Search();
        screen.Select(customer.Id);
        return screen;
    }

    [Fact]
    public void TheOwnerSetsALimitAndTheCustomerShowsIt()
    {
        var lakshmi = Known("9000000001", "Lakshmi");
        OnCredit(lakshmi, 300m);
        var screen = Picked(lakshmi);

        Assert.Equal(string.Empty, screen.EditLimit);
        Assert.Equal("No khata limit · nothing paid back yet", screen.KhataNote);

        screen.EditLimit = "₹2,000";

        Assert.Null(screen.SaveLimit());
        Assert.Equal(2_000m, Customers.FindByMobile(lakshmi.MobileNo)!.CreditLimit);
        Assert.Equal("2000", screen.EditLimit);
        Assert.Equal("Khata limit ₹2,000.00 · nothing paid back yet", screen.KhataNote);
        Assert.Contains("may owe up to ₹2,000.00", screen.Status);
    }

    [Fact]
    public void AnEmptyBoxTakesTheLimitOff()
    {
        var lakshmi = Known("9000000001", "Lakshmi");
        Customers.SetCreditLimit(lakshmi.Id, 500m);
        var screen = Picked(lakshmi);

        Assert.Equal("500", screen.EditLimit);

        screen.EditLimit = "  ";

        Assert.Null(screen.SaveLimit());
        Assert.Null(Customers.FindByMobile(lakshmi.MobileNo)!.CreditLimit);
        Assert.Contains("no khata limit", screen.Status);
    }

    [Theory]
    [InlineData("lots")]
    [InlineData("-100")]
    [InlineData("10.005")]
    public void ALimitThatIsNotAnAmountIsNotSaved(string typed)
    {
        var lakshmi = Known("9000000001", "Lakshmi");
        var screen = Picked(lakshmi);

        screen.EditLimit = typed;

        Assert.NotNull(screen.SaveLimit());
        Assert.Null(Customers.FindByMobile(lakshmi.MobileNo)!.CreditLimit);
    }

    [Fact]
    public void TheNoteSaysWhenTheyLastPaid()
    {
        var lakshmi = Known("9000000001", "Lakshmi");
        OnCredit(lakshmi, 300m);
        Credit.Collect(lakshmi.Id, 100m, TenderType.Cash, Lane, new DateTimeOffset(2026, 9, 20, 11, 0, 0, TimeSpan.FromHours(5.5)), "Murugan");

        Assert.Equal("No khata limit · last paid 20 Sep 2026", Picked(lakshmi).KhataNote);
    }
}
