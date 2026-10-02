using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Domain.Printing;
using Pos.TestSupport;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// The cash counted at the close: kept with the close, read back, listed, and printed with the
/// difference in words.
/// </summary>
public class CashCountTests : IDisposable
{
    private const string Lane = "L1";

    private readonly TempDatabase _temp = new();
    private static readonly StoreProfile Store = new() { Name = "Sri Lakshmi Stores", Gstin = "33AABCS1429B1ZX" };
    private static readonly DateTimeOffset Evening = new(2026, 10, 2, 21, 0, 0, TimeSpan.FromHours(5.5));

    public void Dispose() => _temp.Dispose();

    private DayCloseRepository Closes => new(_temp.Database, new HeldBillRepository(_temp.Database));

    /// <summary>One ₹189 dal for cash.</summary>
    private void Sell()
    {
        _temp.Items.AddRange([Catalogue.Item(sku: $"D{Guid.NewGuid():N}"[..10], barcode: null, name: "Toor Dal 1kg", price: 189m, gstRate: 5m)]);

        var bill = new InvoiceEngine("33");
        bill.AddItem(_temp.Items.Search("Toor Dal").First());

        var basket = new TenderBasket(bill.Totals.GrandTotal);
        basket.Add(TenderType.Cash, bill.Totals.GrandTotal);

        new CheckoutService(new InvoiceRepository(_temp.Database), new CustomerRepository(_temp.Database), new RecordingDrawerService())
            .Complete(Lane, bill, basket);
    }

    [Fact]
    public void TheCountIsKeptWithTheCloseAndReadBack()
    {
        Sell();

        var closed = Closes.Close(Lane, Evening, cashCounted: 180m, countedBy: " Murugan ");

        Assert.Equal(180m, closed.CashCounted);
        Assert.Equal("Murugan", closed.CountedBy);
        Assert.Equal(-9m, closed.CashDifference);

        var read = Closes.FindById(closed.Id)!;
        Assert.Equal(180m, read.CashCounted);
        Assert.Equal("Murugan", read.CountedBy);
        Assert.Equal(-9m, read.CashDifference);
    }

    [Fact]
    public void TheListOfClosesCarriesEachCount()
    {
        Sell();
        Closes.Close(Lane, Evening, cashCounted: 200m, countedBy: "Lakshmi");
        Sell();
        Closes.Close(Lane, Evening.AddDays(1));

        var list = Closes.List(Lane);

        Assert.Null(list[0].CashCounted);
        Assert.Null(list[0].CashDifference);
        Assert.Equal(200m, list[1].CashCounted);
        Assert.Equal(11m, list[1].CashDifference);
        Assert.Equal("Lakshmi", list[1].CountedBy);
    }

    /// <summary>Nobody is named as having counted a drawer nobody counted.</summary>
    [Fact]
    public void AClosedWithoutACountNamesNobodyAsCounting()
    {
        Sell();

        var closed = Closes.Close(Lane, Evening, cashCounted: null, countedBy: "Murugan");

        Assert.Null(closed.CashCounted);
        Assert.Null(closed.CountedBy);
    }

    [Fact]
    public void ACountBelowNothingIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Closes.Close(Lane, Evening, cashCounted: -1m));

    [Theory]
    [InlineData(189, "COUNTED: EXACTLY RIGHT")]
    [InlineData(200, "OVER BY")]
    [InlineData(150, "SHORT BY")]
    public void TheReportSaysWhetherTheDrawerWasOverOrShort(decimal counted, string said)
    {
        Sell();
        var day = Closes.Close(Lane, Evening, counted, "Murugan");

        var paper = new ZReportComposer(Store, 48, ReceiptLanguage.English, TaxMode.Gst).Compose(day).ToPlainText();

        Assert.Contains("Cash counted", paper);
        Assert.Contains("Murugan", paper);
        Assert.Contains(said, paper);
        Assert.DoesNotContain("Not counted", paper);
    }

    /// <summary>A reprint months later still shows the count: it belongs to the close, not to the evening.</summary>
    [Fact]
    public void AReprintKeepsTheCount()
    {
        Sell();
        var day = Closes.Close(Lane, Evening, 150m, "Murugan");

        var reprint = new ZReportComposer(Store, 48, ReceiptLanguage.English, TaxMode.Gst)
            .Compose(Closes.FindById(day.Id)!, isReprint: true).ToPlainText();

        Assert.Contains("SHORT BY", reprint);
        Assert.Contains("39.00", reprint);
    }

    [Fact]
    public void AnUncountedCloseIsPrintedAsSuch()
    {
        Sell();
        var day = Closes.Close(Lane, Evening);

        var paper = new ZReportComposer(Store, 48, ReceiptLanguage.English, TaxMode.Gst).Compose(day).ToPlainText();

        Assert.Contains("Not counted at the close", paper);
        Assert.DoesNotContain("Cash counted", paper);
    }

    /// <summary>The Tamil report prints the count too, in Tamil, with the same figures.</summary>
    [Fact]
    public void TheTamilReportPrintsTheCount()
    {
        Sell();
        var day = Closes.Close(Lane, Evening, 150m, "Murugan");

        var paper = new ZReportComposer(Store, 48, ReceiptLanguage.Tamil, TaxMode.Gst).Compose(day).ToPlainText();

        Assert.Contains("எண்ணிய ரொக்கம்", paper);
        Assert.Contains("குறைவு", paper);
        Assert.Contains("39.00", paper);
        Assert.Contains("150.00", paper);
    }
}
