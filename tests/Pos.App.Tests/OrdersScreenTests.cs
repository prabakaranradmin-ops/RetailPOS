using System.IO;
using Pos.App.ViewModels;
using Pos.Core.Analytics;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// The Orders tab, the way an owner uses it: look at who there is something to order from, copy one
/// supplier's order to send, change how long an order lasts, save the whole list.
/// </summary>
public class OrdersScreenTests : IDisposable
{
    private const string Lane = "L1";
    private static readonly DateOnly Today = new(2026, 9, 29);

    private readonly TempDatabase _temp = new();
    private readonly List<string> _copied = [];
    private readonly List<int> _kept = [];

    public void Dispose() => _temp.Dispose();

    private OrdersViewModel Screen(int cover = 14, Func<int, string?>? keep = null)
    {
        var screen = new OrdersViewModel(
            days => new OrderListQuery(_temp.Database).Gather(days),
            _copied.Add,
            "Sri Murugan Stores",
            cover,
            keep ?? (days => { _kept.Add(days); return null; }),
            () => Today);

        screen.Load();
        return screen;
    }

    /// <summary>Dal from Murugan Traders selling four a day with sixteen left; salt never bought on a bill.</summary>
    private void Stock()
    {
        _temp.Items.UpsertRange(
        [
            Catalogue.Item(sku: "DAL", name: "Toor Dal 1kg", price: 100m) with { StockQty = 10m },
            Catalogue.Item(sku: "SALT", name: "Crystal Salt 1kg", price: 20m) with { StockQty = 1m },
        ]);

        var dal = _temp.Items.FindBySku("DAL")!;
        var salt = _temp.Items.FindBySku("SALT")!;

        var purchases = new PurchaseRepository(_temp.Database);
        var murugan = purchases.AddSupplier(new Supplier(0, "Murugan Traders", "9443012345", "33AEIPH7795F1Z9", "33", true));
        var line = PurchaseLine.Price(new PurchaseLineEntry(dal, 10m, 100m, 5m, 0m), interState: false, chargesGst: true);
        purchases.Record(new PurchaseBill(murugan, "MT/1", DateOnly.FromDateTime(DateTime.Today), [line], InterState: false), Lane, DateTimeOffset.Now, null);

        Sell(dal, 4m);
        Sell(salt, 1m);
    }

    private void Sell(Item item, decimal quantity)
    {
        var bill = new InvoiceEngine("33");
        bill.AddItem(item, quantity);

        var basket = new TenderBasket(bill.Totals.AmountPayable);
        basket.Add(TenderType.Cash, bill.Totals.AmountPayable);

        new CheckoutService(new InvoiceRepository(_temp.Database), new CustomerRepository(_temp.Database),
                new RecordingDrawerService(), null, TimeProvider.System, stock: new StockRepository(_temp.Database))
            .Complete(Lane, bill, basket);
    }

    [Fact]
    public void ItOpensOnTheSupplierWithTheMostUrgentOrder()
    {
        Stock();
        var screen = Screen();

        Assert.Equal(["Murugan Traders", SupplierOrder.NoSupplier], screen.Suppliers.Select(s => s.Supplier));
        Assert.Equal("Murugan Traders", screen.SelectedSupplier!.Supplier);
        Assert.Equal("Murugan Traders  ·  9443012345", screen.SupplierHeading);
        Assert.Equal(40m, Assert.Single(screen.Lines).Order);
        Assert.Contains("2 items to order from 2 suppliers, to last 14 days", screen.Headline);
        Assert.Equal("About ₹4,000.00 at the rates last paid, before tax.", screen.EstimateLine);
    }

    [Fact]
    public void PickingAnotherSupplierShowsTheirOrder()
    {
        Stock();
        var screen = Screen();

        screen.SelectedSupplier = screen.Suppliers[1];

        Assert.Equal("Crystal Salt 1kg", Assert.Single(screen.Lines).Name);
        Assert.StartsWith("No cost to show", screen.EstimateLine);
    }

    [Fact]
    public void CopyPutsTheOrderOnTheClipboardAsAMessage()
    {
        Stock();
        var screen = Screen();

        Assert.Null(screen.Copy());

        var message = Assert.Single(_copied);
        Assert.StartsWith("Order from Sri Murugan Stores - 29 Sep 2026", message);
        Assert.Contains("1. Toor Dal 1kg - 40 pc", message);
        Assert.Contains("Copied the order for Murugan Traders", screen.Status);
    }

    [Fact]
    public void AClipboardHeldByAnotherProgramIsSaidNotThrown()
    {
        Stock();
        var screen = new OrdersViewModel(
            days => new OrderListQuery(_temp.Database).Gather(days),
            _ => throw new InvalidOperationException("OpenClipboard failed"),
            "Sri Murugan Stores",
            today: () => Today);
        screen.Load();

        Assert.Contains("Could not copy it", screen.Copy());
    }

    /// <summary>Four weeks instead of two: the dal order goes from 40 to 96, and the choice is kept.</summary>
    [Fact]
    public void ALongerCoverIsWorkedOutAndKept()
    {
        Stock();
        var screen = Screen();

        screen.CoverDaysText = "28";
        Assert.Null(screen.ApplyCoverDays());

        Assert.Equal(28, screen.CoverDays);
        Assert.Equal(96m, screen.Lines.Single().Order);
        Assert.Equal([28], _kept);
        Assert.Equal("Murugan Traders", screen.SelectedSupplier!.Supplier);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("121")]
    [InlineData("two weeks")]
    [InlineData("-3")]
    public void ACoverThatIsNotADayCountIsRefused(string typed)
    {
        Stock();
        var screen = Screen();

        screen.CoverDaysText = typed;

        Assert.Contains("not a number of days", screen.ApplyCoverDays());
        Assert.Equal(14, screen.CoverDays);
        Assert.Empty(_kept);
    }

    [Fact]
    public void ACoverThatCouldNotBeKeptStillAppliesAndSaysSo()
    {
        Stock();
        var screen = Screen(keep: _ => "the settings file is read-only");

        screen.CoverDaysText = "21";
        screen.ApplyCoverDays();

        Assert.Equal(21, screen.CoverDays);
        Assert.Contains("could not be kept", screen.Status);
    }

    [Fact]
    public void NothingToOrderIsSaidAndThereIsNothingToSaveOrCopy()
    {
        _temp.Items.UpsertRange([Catalogue.Item(sku: "DAL", name: "Toor Dal 1kg") with { StockQty = 500m }]);
        var screen = Screen();

        Assert.Empty(screen.Suppliers);
        Assert.Contains("Nothing needs ordering", screen.Headline);
        Assert.False(screen.CanSave);
        Assert.False(screen.CanCopy);
        Assert.Equal("Pick a supplier on the left.", screen.SupplierHeading);
    }

    [Fact]
    public void TheWholeListIsSavedAsASpreadsheet()
    {
        Stock();
        var screen = Screen();
        var path = Path.Combine(Path.GetDirectoryName(_temp.Database.DatabasePath)!, screen.SuggestedFileName);

        Assert.Equal("order-list-2026-09-29.csv", screen.SuggestedFileName);
        Assert.Null(screen.Save(path));

        var rows = File.ReadAllLines(path);
        Assert.Equal(3, rows.Length);
        Assert.Contains("Saved 2 items for 2 suppliers", screen.Status);
    }

    [Fact]
    public void AListThatCannotBeReadIsSaidNotThrown()
    {
        var screen = new OrdersViewModel(_ => throw new InvalidOperationException("database is locked"), _ => { }, "Shop");

        screen.Load();

        Assert.Contains("could not be read: database is locked", screen.Status);
        Assert.Empty(screen.Suppliers);
    }
}
