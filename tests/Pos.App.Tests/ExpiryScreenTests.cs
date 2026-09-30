using System.Windows.Input;
using Pos.App.ViewModels;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// Use-by dates where people see them: the cashier on scanning, and the owner on the Stock tab.
/// </summary>
public class ExpiryScreenTests : IDisposable
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    private readonly TempDatabase _temp = new();

    public void Dispose() => _temp.Dispose();

    private static ExpiryWarning Warning(int daysLeft, decimal? onShelf = 4m) =>
        new(1, "MILK", "Aavin Milk 500ml", UnitType.Each, "A17", Today.AddDays(daysLeft), Today.AddDays(-5), "Murugan Traders", 10m, onShelf, daysLeft);

    [Fact]
    public void TheCashierIsToldToCheckTheDateOnScanning()
    {
        using var till = new BillingHarness(Catalogue.Item(sku: "MILK", barcode: "8901234567890", name: "Aavin Milk 500ml", price: 24m));
        till.ViewModel.ExpiryNote = _ => Expiry.CheckNote([Warning(-2)]);

        till.Scan("8901234567890");

        Assert.Single(till.ViewModel.Lines);
        Assert.Contains("Aavin Milk 500ml added.", till.ViewModel.StatusMessage);
        Assert.Contains("Check the date: a delivery of this expired on", till.ViewModel.StatusMessage);
    }

    /// <summary>The sale goes through whatever the dates say, and a failed read costs the note, not the scan.</summary>
    [Fact]
    public void ADateThatCannotBeReadDoesNotStopTheSale()
    {
        using var till = new BillingHarness(Catalogue.Item(sku: "MILK", barcode: "8901234567890", name: "Aavin Milk 500ml", price: 24m));
        till.ViewModel.ExpiryNote = _ => throw new InvalidOperationException("database is locked");

        till.Scan("8901234567890");

        Assert.Single(till.ViewModel.Lines);
        Assert.Equal("Aavin Milk 500ml added.", till.ViewModel.StatusMessage);
    }

    private OwnerViewModel Owner(Func<IReadOnlyList<ExpiryWarning>>? expiring) => new(
        "L1",
        _ => throw new InvalidOperationException("not read in this test"),
        new StockRepository(_temp.Database),
        TaxMode.Gst,
        isPinSet: false,
        applyTaxMode: _ => null,
        applyPin: _ => null,
        expiring: expiring);

    [Fact]
    public void TheStockTabListsWhatIsNearItsDate()
    {
        var owner = Owner(() => [Warning(-2), Warning(5), Warning(20, onShelf: null)]);

        owner.ShowExpiring = true;

        Assert.False(owner.ShowsStockList);
        Assert.Equal(3, owner.ExpiryWarnings.Count);
        Assert.Contains("3 deliveries within 30 days of their date", owner.ListHeadline);
        Assert.Contains("1 already past it", owner.ListHeadline);
    }

    [Fact]
    public void TheReorderListMentionsDatesToCheck()
    {
        _temp.Items.AddRange([Catalogue.Item(sku: "MILK") with { StockQty = 4m }]);
        var owner = Owner(() => [Warning(5)]);

        owner.LowOnly = false;

        Assert.True(owner.ShowsStockList);
        Assert.Contains("1 delivery is near its date", owner.ListHeadline);
    }

    [Fact]
    public void ALaneWithoutDatesDoesNotOfferTheList()
    {
        var owner = Owner(null);

        Assert.False(owner.CanShowExpiring);
    }

    [Fact]
    public void DatesThatCannotBeReadAreSaidNotThrown()
    {
        var owner = Owner(() => throw new InvalidOperationException("database is locked"));

        owner.ShowExpiring = true;

        Assert.Contains("could not be read: database is locked", owner.ListHeadline);
    }
}
