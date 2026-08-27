using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Domain.Printing;
using Pos.Core.Hardware.Drawer;
using Pos.Core.Hardware.Printing;
using Pos.TestSupport;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// Reading a day-end report back after it has been taken.
/// </summary>
/// <remarks>
/// Every close was already stored — the figures, the tenders, who was on the till — and until this
/// existed the printed sheet was the only way to see any of it. A printer that jams at closing
/// time, or a sheet that goes missing between the counter and the file, should not put a day's
/// takings out of reach of the shop that took them.
/// </remarks>
public class DayCloseHistoryTests : IDisposable
{
    private const string Lane = "L1";
    private const string OtherLane = "L2";
    private const string HomeState = "33";

    private readonly TempDatabase _temp = new();
    private readonly RecordingDrawerService _drawer = new();

    private static readonly StoreProfile Store = new()
    {
        Name = "Sri Lakshmi Stores",
        Gstin = "33AABCS1429B1ZX",
    };

    public void Dispose() => _temp.Dispose();

    private DayCloseRepository Closes => new(_temp.Database, new HeldBillRepository(_temp.Database));

    /// <summary>Sells one item on a lane and closes the day, returning the stored report.</summary>
    private DayCloseSummary TradeAndClose(decimal price, string lane = Lane)
    {
        var bill = new InvoiceEngine(HomeState);
        bill.AddItem(Catalogue.Item(id: 1, sku: "SKU1", barcode: "8901234567890", name: "Toor Dal", price: price, gstRate: 5m));

        var basket = new TenderBasket(bill.Totals.GrandTotal);
        basket.Add(TenderType.Cash, bill.Totals.GrandTotal);

        new CheckoutService(
            new InvoiceRepository(_temp.Database),
            new CustomerRepository(_temp.Database),
            _drawer,
            null,
            TimeProvider.System).Complete(lane, bill, basket);

        return Closes.Close(lane, DateTimeOffset.Now);
    }

    // ---- Listing --------------------------------------------------------------------------------

    [Fact]
    public void ALaneThatHasNeverClosedListsNothingRatherThanFailing()
    {
        Assert.Empty(Closes.List(Lane));
    }

    [Fact]
    public void TheClosesAreListedMostRecentFirst()
    {
        var first = TradeAndClose(100m);
        var second = TradeAndClose(250m);

        var listed = Closes.List(Lane);

        Assert.Equal(2, listed.Count);
        Assert.Equal(second.Id, listed[0].Id);
        Assert.Equal(first.Id, listed[1].Id);
    }

    [Fact]
    public void EachLineCarriesEnoughToFindTheReportYouMeant()
    {
        var closed = TradeAndClose(189m);
        var entry = Assert.Single(Closes.List(Lane));

        Assert.Equal(closed.Id, entry.Id);
        Assert.Equal(1, entry.InvoiceCount);
        Assert.Equal(189m, entry.NetSales);
        Assert.Equal(189m, entry.CashExpected);
        Assert.Equal(closed.ClosedAt.UtcDateTime, entry.ClosedAt.UtcDateTime, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void AnotherLanesClosesAreNotThisLanesHistory()
    {
        TradeAndClose(100m);
        TradeAndClose(250m, lane: OtherLane);

        Assert.Single(Closes.List(Lane));
        Assert.Single(Closes.List(OtherLane));
        Assert.Equal(250m, Closes.List(OtherLane)[0].NetSales);
    }

    [Fact]
    public void TheListingIsCappedAndTheCapIsSane()
    {
        for (var i = 0; i < 5; i++)
            TradeAndClose(100m + i);

        Assert.Equal(5, Closes.List(Lane).Count);
        Assert.Equal(3, Closes.List(Lane, 3).Count);
        Assert.Single(Closes.List(Lane, 1));

        // A nonsense limit is clamped rather than throwing at a counter, or quietly returning
        // nothing and letting somebody conclude the shop has never closed a day.
        Assert.Single(Closes.List(Lane, 0));
        Assert.Single(Closes.List(Lane, -20));
        Assert.Equal(5, Closes.List(Lane, 100_000).Count);
    }

    // ---- Reading one back ------------------------------------------------------------------------

    /// <summary>
    /// The whole point: a report taken weeks ago comes back with the same figures it printed with.
    /// </summary>
    [Fact]
    public void AStoredReportComesBackWithTheFiguresItWasTakenWith()
    {
        var closed = TradeAndClose(189m);
        var reloaded = Closes.FindById(closed.Id)!;

        Assert.Equal(closed.Id, reloaded.Id);
        Assert.Equal(closed.InvoiceCount, reloaded.InvoiceCount);
        Assert.Equal(closed.NetSales, reloaded.NetSales);
        Assert.Equal(closed.CashExpected, reloaded.CashExpected);
        Assert.Equal(closed.TotalTax, reloaded.TotalTax);
        Assert.Equal(closed.Tenders.Count, reloaded.Tenders.Count);
    }

    [Fact]
    public void AskingForAReportThatDoesNotExistReturnsNothing()
    {
        TradeAndClose(189m);

        Assert.Null(Closes.FindById(9999));
    }

    [Fact]
    public void TheLatestIsTheOneMostRecentlyClosed()
    {
        TradeAndClose(100m);
        var second = TradeAndClose(250m);

        Assert.Equal(second.Id, Closes.FindLatest(Lane)!.Id);
        Assert.Null(Closes.FindLatest("NEVER-TRADED"));
    }

    // ---- Reprinting ------------------------------------------------------------------------------

    /// <summary>
    /// A duplicate has to say so on its face, or it can be filed as a second day's takings.
    /// </summary>
    [Fact]
    public void ADuplicateIsMarkedAsOneAndCarriesTheOriginalFigures()
    {
        var closed = TradeAndClose(189m);
        var stored = Closes.FindById(closed.Id)!;

        var composer = new ZReportComposer(Store);
        var original = composer.Compose(stored).ToPlainText();
        var duplicate = composer.Compose(stored, isReprint: true).ToPlainText();

        Assert.DoesNotContain("REPRINT", original);
        Assert.Contains("** REPRINT **", duplicate);

        // Same day, same figures — only the marking differs.
        foreach (var figure in new[] { "189.00", "Sri Lakshmi Stores", "33AABCS1429B1ZX" })
        {
            Assert.Contains(figure, original);
            Assert.Contains(figure, duplicate);
        }
    }

    [Fact]
    public void ReprintingGoesToThePrinterAndDoesNotCloseAnythingAgain()
    {
        var closed = TradeAndClose(189m);

        var printer = new LoopbackPrinterService();
        var service = new DayCloseService(Closes, new ZReportComposer(Store), printer);

        var outcome = service.Reprint(Closes.FindById(closed.Id)!);

        Assert.True(outcome.Succeeded);
        Assert.Single(printer.Jobs);

        // Still one close. Reading a report is not taking one.
        Assert.Single(Closes.List(Lane));
    }

    /// <summary>
    /// On a Tamil lane the duplicate is in Tamil too — it is the same document, read back.
    /// </summary>
    [Fact]
    public void ATamilLanesDuplicateIsInTamil()
    {
        var closed = TradeAndClose(189m);
        var stored = Closes.FindById(closed.Id)!;

        var tamil = new ZReportComposer(Store, ReceiptBuilder.Width80Mm, ReceiptLanguage.Tamil)
            .Compose(stored, isReprint: true)
            .ToPlainText();

        Assert.Contains("நாள் இறுதி அறிக்கை (Z)", tamil);
        Assert.Contains("** REPRINT **", tamil);
        Assert.Contains("189.00", tamil);
    }
}
