using Pos.App.ViewModels;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// The Purchases tab, driven the way an owner enters a delivery: pick the supplier, type the bill
/// number, add each line off the paper, check the printed total, save.
/// </summary>
public class PurchasesScreenTests : IDisposable
{
    private const string Lane = "L1";
    private const string TamilNaduGstin = "33AEIPH7795F1Z9";
    private const string MaharashtraGstin = "27AAPFU0939F1ZV";

    private static readonly DateTimeOffset Now = new(2026, 9, 12, 11, 0, 0, TimeSpan.FromHours(5.5));

    private readonly TempDatabase _temp = new();

    public void Dispose() => _temp.Dispose();

    private PurchaseRepository Store => new(_temp.Database);

    private PurchasesViewModel Screen()
    {
        var items = new ItemRepository(_temp.Database);

        var screen = new PurchasesViewModel(
            Store,
            query => items.Search(query),
            code => items.FindByBarcode(code) ?? items.FindBySku(code),
            Lane,
            "33",
            () => "Murugan",
            () => Now);

        screen.Load();
        return screen;
    }

    private Item Stocked(string sku, string barcode, decimal? stock = 5m, decimal price = 150m, decimal gst = 5m)
    {
        _temp.Items.UpsertRange([Catalogue.Item(sku: sku, barcode: barcode, name: $"Item {sku}", price: price, gstRate: gst) with { StockQty = stock }]);
        return _temp.Items.FindBySku(sku)!;
    }

    private static void Pick(PurchasesViewModel screen, string supplierName) =>
        screen.SelectedSupplier = screen.Suppliers.Single(s => s.Supplier.Name == supplierName);

    private static void AddLine(PurchasesViewModel screen, string code, string quantity, string rate)
    {
        screen.ItemQuery = code;
        screen.LineQuantity = quantity;
        screen.LineRate = rate;
        Assert.Null(screen.AddLine());
    }

    [Fact]
    public void ASupplierIsAddedFromTheScreenWithTheirStateTakenFromTheGstin()
    {
        var screen = Screen();

        screen.NewSupplierName = "Mumbai Dry Fruits";
        screen.NewSupplierGstin = MaharashtraGstin;

        Assert.Equal("27", screen.NewSupplierState);
        Assert.Contains("Maharashtra", screen.NewSupplierGstinNote);

        Assert.Null(screen.AddSupplier());

        Assert.Equal("Mumbai Dry Fruits", screen.SelectedSupplier!.Supplier.Name);
        Assert.True(screen.InterState);
        Assert.Contains("claim", screen.Status);
    }

    /// <summary>A greyed "Save the bill" says what it is waiting for, one thing at a time, in order.</summary>
    [Fact]
    public void AGreyedSaveSaysWhatTheBillStillNeeds()
    {
        Stocked("DAL", "8901234567890");
        Store.AddSupplier(new Supplier(0, "Murugan Traders", null, TamilNaduGstin, "33", true));
        var screen = Screen();

        Assert.Equal("Pick the supplier first.", screen.SaveBillBlocker);
        Assert.Equal("Pick the supplier first.", screen.PayBlocker);

        Pick(screen, "Murugan Traders");
        Assert.Equal("Type the bill number from their bill.", screen.SaveBillBlocker);
        Assert.Equal("Nothing is owed to them.", screen.PayBlocker);

        screen.BillNo = "MT/300";
        Assert.Equal("Add the bill's lines.", screen.SaveBillBlocker);

        AddLine(screen, "8901234567890", "2", "100");
        Assert.True(screen.CanSaveBill);
        Assert.Equal(string.Empty, screen.SaveBillBlocker);
    }

    [Fact]
    public void AWrongGstinIsCaughtAsItIsTyped()
    {
        var screen = Screen();

        screen.NewSupplierName = "Typo Traders";
        screen.NewSupplierGstin = "33AEIPH7795F1Z8";

        Assert.Contains("mistyped", screen.NewSupplierGstinNote);
        Assert.NotNull(screen.AddSupplier());
        Assert.Empty(screen.Suppliers);
    }

    /// <summary>The whole of a delivery, from picking the supplier to the shelf going up.</summary>
    [Fact]
    public void ADeliveryIsEnteredAndTheShelfAndTheAccountFollow()
    {
        var dal = Stocked("DAL", "8901234567890", stock: 4m);
        var soap = Stocked("SOAP", "8901234567906", stock: 2m, gst: 18m);
        Store.AddSupplier(new Supplier(0, "Murugan Traders", null, TamilNaduGstin, "33", true));

        var screen = Screen();
        Pick(screen, "Murugan Traders");
        screen.BillNo = "MT/201";

        // A scanned barcode picks the item outright, and brings its GST rate with it.
        screen.ItemQuery = "8901234567890";
        Assert.Equal(dal.Id, screen.LineItem!.Id);
        Assert.Equal("5", screen.LineGst);

        screen.LineQuantity = "10";
        screen.LineRate = "100";
        Assert.Null(screen.AddLine());

        AddLine(screen, "SOAP", "10", "50");

        Assert.Equal(2, screen.Lines.Count);
        Assert.Equal("1,500.00", screen.TaxableLine);
        Assert.Equal("CGST 70.00  ·  SGST 70.00", screen.TaxLine);
        Assert.Equal("1,640.00", screen.LinesTotalLine);

        screen.PrintedTotal = "1640";
        Assert.Contains("Save bill MT/201 from Murugan Traders: 2 lines, 1,640.00", screen.SaveQuestion);
        Assert.Null(screen.SaveBill());

        Assert.Contains("2 shelf counts went up", screen.Status);
        Assert.Equal(14m, _temp.Items.FindBySku("DAL")!.StockQty);
        Assert.Equal(12m, _temp.Items.FindBySku("SOAP")!.StockQty);
        Assert.Equal(1_640m, screen.SelectedSupplier!.Owed);
        Assert.Contains("You owe Murugan Traders 1,640.00", screen.OwedLine);
        Assert.Equal("Bill MT/201", screen.History[0].Description);
        Assert.Equal("MT/201", Assert.Single(screen.Recent).BillNo);

        // The form is ready for the next bill.
        Assert.Empty(screen.Lines);
        Assert.Equal(string.Empty, screen.BillNo);
    }

    [Fact]
    public void APrintedTotalThatTheLinesDoNotReachStopsTheSave()
    {
        Stocked("DAL", "8901234567890");
        Store.AddSupplier(new Supplier(0, "Murugan Traders", null, TamilNaduGstin, "33", true));

        var screen = Screen();
        Pick(screen, "Murugan Traders");
        screen.BillNo = "MT/202";
        AddLine(screen, "DAL", "10", "100");

        screen.PrintedTotal = "1150";

        Assert.Contains("more than a round-off", screen.SaveBill());
        Assert.Empty(Store.Recent());
        Assert.Single(screen.Lines);
    }

    [Fact]
    public void ALineThatIsNotALineIsRefusedAndSaysWhy()
    {
        Stocked("DAL", "8901234567890");
        Store.AddSupplier(new Supplier(0, "Murugan Traders", null, TamilNaduGstin, "33", true));

        var screen = Screen();
        Pick(screen, "Murugan Traders");

        screen.ItemQuery = "DAL";
        screen.LineQuantity = "ten";
        screen.LineRate = "100";
        Assert.Contains("not a quantity", screen.AddLine());

        screen.LineQuantity = "10";
        screen.LineGst = "7";
        Assert.Contains("not a GST rate", screen.AddLine());

        screen.LineGst = "5";
        screen.LineExpiry = "31st Dec";
        Assert.Contains("not a date", screen.AddLine());

        Assert.Empty(screen.Lines);
    }

    [Fact]
    public void ABillDatedInTheFutureIsRefused()
    {
        Stocked("DAL", "8901234567890");
        Store.AddSupplier(new Supplier(0, "Murugan Traders", null, TamilNaduGstin, "33", true));

        var screen = Screen();
        Pick(screen, "Murugan Traders");
        screen.BillNo = "MT/203";
        AddLine(screen, "DAL", "1", "100");
        screen.BillDate = "20-09-2026";

        Assert.Contains("has not happened yet", screen.SaveBill());
    }

    [Fact]
    public void ASupplierWhoChargesNoGstGetsNoGstOnTheLine()
    {
        Stocked("DAL", "8901234567890");
        Store.AddSupplier(new Supplier(0, "Kannan Vegetables", null, null, "33", false));

        var screen = Screen();
        Pick(screen, "Kannan Vegetables");
        screen.ItemQuery = "DAL";

        Assert.Equal("0", screen.LineGst);
        Assert.Contains("charges no GST", screen.SupplierLine);
    }

    [Fact]
    public void PaidOnTheSpotAndPaidLaterAreBothOnTheAccount()
    {
        Stocked("DAL", "8901234567890");
        Store.AddSupplier(new Supplier(0, "Murugan Traders", null, TamilNaduGstin, "33", true));

        var screen = Screen();
        Pick(screen, "Murugan Traders");
        screen.BillNo = "MT/204";
        AddLine(screen, "DAL", "10", "100");
        screen.PaidNow = "50";
        screen.PaidNowMethod = screen.PaymentMethods.Single(m => m.Method == SupplierPaymentMethod.Upi);
        Assert.Null(screen.SaveBill());

        Assert.Contains("Paid 50.00 UPI", screen.Status);
        Assert.Equal(1_000m, screen.SelectedSupplier!.Owed);

        screen.PaymentAmount = "400";
        screen.PaymentMethod = screen.PaymentMethods.Single(m => m.Method == SupplierPaymentMethod.DrawerCash);
        Assert.True(screen.CanPay);
        Assert.Null(screen.Pay());

        Assert.Contains("from the till", screen.Status);
        Assert.Contains("day-end", screen.Status);
        Assert.Equal(600m, screen.SelectedSupplier!.Owed);

        screen.PaymentAmount = "700";
        Assert.Contains("600.00", screen.Pay());
    }

    [Fact]
    public void ABillEnteredWronglyIsCancelledFromTheList()
    {
        Stocked("DAL", "8901234567890", stock: 4m);
        Store.AddSupplier(new Supplier(0, "Murugan Traders", null, TamilNaduGstin, "33", true));

        var screen = Screen();
        Pick(screen, "Murugan Traders");
        screen.BillNo = "MT/205";
        AddLine(screen, "DAL", "10", "100");
        Assert.Null(screen.SaveBill());

        screen.SelectedBill = screen.Recent.Single();
        Assert.False(screen.CanVoid);

        screen.VoidReason = "typed 10 for 1";
        Assert.True(screen.CanVoid);
        Assert.Null(screen.VoidBill());

        Assert.Equal(4m, _temp.Items.FindBySku("DAL")!.StockQty);
        Assert.True(screen.Recent.Single().IsVoided);
        Assert.Equal(0m, screen.SelectedSupplier!.Owed);
    }

    [Fact]
    public void TheListCanShowOnlyWhoIsOwed()
    {
        Stocked("DAL", "8901234567890");
        var owed = Store.AddSupplier(new Supplier(0, "Owed Supplier", null, TamilNaduGstin, "33", true));
        Store.AddSupplier(new Supplier(0, "Settled Supplier", null, null, "33", false));

        var dal = _temp.Items.FindBySku("DAL")!;
        Store.Record(new PurchaseBill(owed, "O1", new DateOnly(2026, 9, 10),
            [PurchaseLine.Price(new PurchaseLineEntry(dal, 1m, 100m, 5m), false, true)], false), Lane, Now, null);

        var screen = Screen();
        Assert.Equal(2, screen.Suppliers.Count);
        Assert.Contains("105.00 owed to 1 supplier", screen.TotalOwedLine);

        screen.OnlyOwed = true;
        Assert.Equal("Owed Supplier", Assert.Single(screen.Suppliers).Supplier.Name);

        screen.OnlyOwed = false;
        screen.SupplierSearch = "sett";
        Assert.Equal("Settled Supplier", Assert.Single(screen.Suppliers).Supplier.Name);
    }

    // ---- Sending goods back ----------------------------------------------------------------------

    /// <summary>Ten dal at 100 and ten soap at 50 on bill MT/301, saved from the screen.</summary>
    private PurchasesViewModel WithADelivery()
    {
        Stocked("DAL", "8901234567890", stock: 0m);
        Stocked("SOAP", "8901234567906", stock: 0m, gst: 18m);
        Store.AddSupplier(new Supplier(0, "Murugan Traders", null, TamilNaduGstin, "33", true));

        var screen = Screen();
        Pick(screen, "Murugan Traders");
        screen.BillNo = "MT/301";
        AddLine(screen, "DAL", "10", "100");
        AddLine(screen, "SOAP", "10", "50");
        screen.PrintedTotal = "1640";
        Assert.Null(screen.SaveBill());

        screen.SelectedBill = screen.Recent.Single();
        return screen;
    }

    [Fact]
    public void PickingABillListsWhatCanGoBack()
    {
        var screen = WithADelivery();

        Assert.True(screen.HasReturnRows);
        Assert.Equal(["Item DAL", "Item SOAP"], screen.ReturnRows.Select(r => r.Name));
        Assert.Equal("10 Pcs", screen.ReturnRows[0].OnTheBill);
        Assert.False(screen.CanSendBack);
        Assert.Equal("Type how many of each are going back.", screen.SendBackBlocker);
    }

    [Fact]
    public void GoodsGoBackOnADebitNoteFromTheScreen()
    {
        var screen = WithADelivery();

        screen.ReturnRows[0].Back = "2";
        Assert.Contains("Say why", screen.SendBackBlocker);

        screen.ReturnReason = "expired";
        Assert.True(screen.CanSendBack);

        Assert.Null(screen.SendBack());

        Assert.Contains("Debit note DN/26-27/L1-1: 1 line going back to Murugan Traders, 210.00 off what the shop owes them - now 1,430.00", screen.Status);
        Assert.Equal(8m, _temp.Items.FindBySku("DAL")!.StockQty);
        Assert.Equal(1_430m, screen.SelectedSupplier!.Owed);
        Assert.Equal("Sent back, debit note DN/26-27/L1-1", screen.History[0].Description);

        // The rows read again: two of the dal gone back, the boxes empty, and the reason cleared.
        Assert.Equal("2", screen.ReturnRows[0].GoneBack);
        Assert.Equal(string.Empty, screen.ReturnRows[0].Back);
        Assert.Equal(string.Empty, screen.ReturnReason);
    }

    [Theory]
    [InlineData("eleven", "is not a number")]
    [InlineData("-1", "less than nothing")]
    [InlineData("11", "only 10 can still go back")]
    public void AQuantityThatCannotGoBackSaysWhy(string typed, string said)
    {
        var screen = WithADelivery();
        screen.ReturnReason = "expired";

        screen.ReturnRows[0].Back = typed;

        Assert.False(screen.CanSendBack);
        Assert.Contains(said, screen.SendBackBlocker);
    }
}
