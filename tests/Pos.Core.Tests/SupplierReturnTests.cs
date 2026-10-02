using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// Goods sent back to a supplier on a debit note: priced as their bill charged, off the shelf, off
/// what the shop owes them, and adding up to the bill to the paisa however they go back.
/// </summary>
/// <remarks>Figures that work on paper: 100 at 5% is 105, ten of them 1,050.</remarks>
public class SupplierReturnTests : IDisposable
{
    private const string Lane = "L1";
    private const string TamilNaduGstin = "33AEIPH7795F1Z9";

    private static readonly DateTimeOffset At = new(2026, 9, 12, 10, 0, 0, TimeSpan.FromHours(5.5));

    private readonly TempDatabase _temp = new();

    public void Dispose() => _temp.Dispose();

    private PurchaseRepository Purchases => new(_temp.Database);

    private Item Load(string sku, decimal? stock)
    {
        _temp.Items.UpsertRange([Catalogue.Item(sku: sku, name: $"Item {sku}", price: 150m) with { StockQty = stock }]);
        return _temp.Items.FindBySku(sku)!;
    }

    private Supplier Wholesaler() =>
        Purchases.AddSupplier(new Supplier(0, "Murugan Traders", "9443012345", TamilNaduGstin, "33", true));

    private static PurchaseLine Line(Item item, decimal quantity, decimal rate) =>
        PurchaseLine.Price(new PurchaseLineEntry(item, quantity, rate, 5m, 0m), interState: false, chargesGst: true);

    /// <summary>Ten dal at 100 + 5% and three oil at 33.33 + 5%, on bill B-1.</summary>
    private (Supplier Supplier, long PurchaseId, Item Dal, Item Oil) Delivered(decimal? dalStock = 0m, decimal? oilStock = null)
    {
        var supplier = Wholesaler();
        var dal = Load("DAL", dalStock);
        var oil = Load("OIL", oilStock);

        var recorded = Purchases.Record(
            new PurchaseBill(supplier, "B-1", new DateOnly(2026, 9, 10), [Line(dal, 10m, 100m), Line(oil, 3m, 33.33m)], InterState: false),
            Lane, At, "Murugan");

        return (supplier, recorded.PurchaseId, dal, oil);
    }

    [Fact]
    public void EveryLineOfTheBillCanGoBackUntilSomeHas()
    {
        var (_, id, _, _) = Delivered();

        var lines = Purchases.Returnable(id);

        Assert.Equal(2, lines.Count);
        Assert.Equal(10m, lines[0].Left);
        Assert.Equal(0m, lines[0].SentBack);
        Assert.Equal(3m, lines[1].Bought);
    }

    [Fact]
    public void GoodsGoBackPricedAsTheBillChargedForThem()
    {
        var (supplier, id, dal, _) = Delivered();
        var owedBefore = Purchases.Owed(supplier.Id);

        var note = Purchases.SendBack(id, [new SupplierReturnPick(1, 2m)], "expired", Lane, At.AddDays(3));

        Assert.Equal("DN/26-27/L1-1", note.Number);
        Assert.Equal(200.00m, note.TaxableValue);
        Assert.Equal(10.00m, note.Tax);
        Assert.Equal(210.00m, note.Total);
        Assert.Equal("Murugan Traders", note.SupplierName);
        Assert.Equal("B-1", note.BillNo);

        // Off what is owed, and off the counted shelf: ten came in, two went back.
        Assert.Equal(owedBefore - 210.00m, Purchases.Owed(supplier.Id));
        Assert.Equal(8m, _temp.Items.FindBySku("DAL")!.StockQty);
        Assert.Equal(2m, Purchases.Returnable(id)[0].SentBack);
        Assert.Empty(note.NotCounted);
        _ = dal;
    }

    /// <summary>
    /// A line sent back in parts adds up to the bill to the paisa: the last part takes exactly what
    /// is left, however the parts rounded.
    /// </summary>
    [Fact]
    public void ALineSentBackInPartsAddsUpToTheBillToThePaisa()
    {
        var (supplier, id, _, _) = Delivered();
        var bill = Purchases.Lines(id)[1];

        var first = Purchases.SendBack(id, [new SupplierReturnPick(2, 1m)], "damaged", Lane, At);
        var second = Purchases.SendBack(id, [new SupplierReturnPick(2, 1m)], "damaged", Lane, At);
        var last = Purchases.SendBack(id, [new SupplierReturnPick(2, 1m)], "damaged", Lane, At);

        Assert.Equal(bill.LineTotal, first.Total + second.Total + last.Total);
        Assert.Equal(bill.TaxableValue, first.TaxableValue + second.TaxableValue + last.TaxableValue);
        Assert.Equal(["DN/26-27/L1-1", "DN/26-27/L1-2", "DN/26-27/L1-3"], new[] { first.Number, second.Number, last.Number });
        Assert.Equal(0m, Purchases.Returnable(id)[1].Left);
    }

    [Fact]
    public void MoreThanCameCannotGoBack()
    {
        var (_, id, _, _) = Delivered();
        Purchases.SendBack(id, [new SupplierReturnPick(1, 7m)], "expired", Lane, At);

        var refused = Assert.Throws<InvalidOperationException>(() => Purchases.SendBack(id, [new SupplierReturnPick(1, 4m)], "expired", Lane, At));

        Assert.Contains("3 is left to send back of the 10", refused.Message);
    }

    [Theory]
    [InlineData(" ", "Say why")]
    [InlineData("expired", "Pick what is going back")]
    public void ADebitNoteNeedsAReasonAndSomethingOnIt(string reason, string said)
    {
        var (_, id, _, _) = Delivered();
        var picks = said.StartsWith("Pick", StringComparison.Ordinal) ? Array.Empty<SupplierReturnPick>() : [new SupplierReturnPick(1, 1m)];

        Assert.Contains(said, Assert.Throws<ArgumentException>(() => Purchases.SendBack(id, picks, reason, Lane, At)).Message);
    }

    /// <summary>An item nobody counts goes back on the note; the shelf it does not have is left alone.</summary>
    [Fact]
    public void AnUncountedItemGoesBackWithoutAShelfToTakeItOff()
    {
        var (_, id, _, _) = Delivered(oilStock: null);

        var note = Purchases.SendBack(id, [new SupplierReturnPick(2, 1m)], "leaking", Lane, At);

        Assert.Equal(["Item OIL"], note.NotCounted);
        Assert.Null(_temp.Items.FindBySku("OIL")!.StockQty);
    }

    [Fact]
    public void TheSuppliersAccountShowsTheDebitNote()
    {
        var (supplier, id, _, _) = Delivered();

        Purchases.SendBack(id, [new SupplierReturnPick(1, 2m)], "expired", Lane, At.AddDays(1));

        var latest = Purchases.History(supplier.Id)[0];
        Assert.Equal("Sent back, debit note DN/26-27/L1-1", latest.Description);
        Assert.Equal(-210.00m, latest.Change);
    }

    /// <summary>The supplier holds the debit note too, so the bill under it cannot be cancelled.</summary>
    [Fact]
    public void ABillWithGoodsSentBackCannotBeCancelled()
    {
        var (_, id, _, _) = Delivered();
        Purchases.SendBack(id, [new SupplierReturnPick(1, 1m)], "expired", Lane, At);

        var refused = Assert.Throws<InvalidOperationException>(() => Purchases.Void(id, "entered twice", Lane, At));

        Assert.Contains("DN/26-27/L1-1", refused.Message);
    }

    [Fact]
    public void NothingOnACancelledBillCanGoBack()
    {
        var (_, id, _, _) = Delivered();
        Purchases.Void(id, "entered twice", Lane, At);

        Assert.Throws<InvalidOperationException>(() => Purchases.SendBack(id, [new SupplierReturnPick(1, 1m)], "expired", Lane, At));
    }

    /// <summary>
    /// The month's GST return lists the debit note, with the input tax on it to come off the claim,
    /// and writes it as a file of its own for the accountant.
    /// </summary>
    [Fact]
    public void TheGstReturnTakesTheInputTaxOnWhatWentBack()
    {
        var (_, id, _, _) = Delivered();
        Purchases.SendBack(id, [new SupplierReturnPick(1, 2m)], "expired", Lane, At.AddDays(3));

        var data = new Pos.Core.Analytics.GstReturnQuery(_temp.Database).Gather(Lane, new DateOnly(2026, 9, 1), "33");

        var note = Assert.Single(data.SentBack);
        Assert.Equal("DN/26-27/L1-1", note.Number);
        Assert.Equal("B-1", note.BillNo);
        Assert.Equal(10.00m, note.Tax);
        Assert.Equal(10.00m, data.InputTaxSentBack);
        Assert.Contains(data.Warnings, w => w.Contains("1 debit note this month", StringComparison.Ordinal) && w.Contains("Rs 10.00 of input tax", StringComparison.Ordinal));

        var csv = Pos.Core.Analytics.GstReturnFiles.DebitNotes(data);
        Assert.Contains("DN/26-27/L1-1", csv);
        Assert.Contains("200.00,0.00,5.00,5.00,210.00,10.00", csv);
    }

    /// <summary>A batch sent back because it is near its date stops being warned about.</summary>
    [Fact]
    public void ABatchSentBackIsNoLongerWarnedAboutForExpiry()
    {
        var supplier = Wholesaler();
        var milk = Load("MILK", 0m);
        var today = new DateOnly(2026, 10, 2);

        var expiring = PurchaseLine.Price(new PurchaseLineEntry(milk, 5m, 20m, 5m, 0m), false, true) with { ExpiryDate = today.AddDays(2), BatchNo = "M-1" };
        var recorded = Purchases.Record(new PurchaseBill(supplier, "B-9", today.AddDays(-5), [expiring], InterState: false), Lane, At, null);

        Assert.NotEmpty(new ExpiryRepository(_temp.Database).Expiring(today));

        Purchases.SendBack(recorded.PurchaseId, [new SupplierReturnPick(1, 5m)], "near its date", Lane, At);

        Assert.Empty(new ExpiryRepository(_temp.Database).Expiring(today));
    }
}
