using Pos.Core.Analytics;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Domain.Printing;
using Pos.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Pos.Core.Tests;

/// <summary>
/// Buying: suppliers, their bills, what the shop owes them, and what that does to the shelf, the
/// cost prices, the drawer and the GST return. Figures are chosen to be worked on paper: 100 at 5%
/// is 105, ten of them 1,050.
/// </summary>
public class PurchaseTests(ITestOutputHelper output) : IDisposable
{
    private const string Lane = "L1";

    /// <summary>A real GSTIN whose check character is right: a Maharashtra dealer.</summary>
    private const string MaharashtraGstin = "27AAPFU0939F1ZV";

    /// <summary>A real GSTIN whose check character is right: a Tamil Nadu dealer.</summary>
    private const string TamilNaduGstin = "33AEIPH7795F1Z9";

    private readonly TempDatabase _temp = new();

    public void Dispose() => _temp.Dispose();

    private PurchaseRepository Purchases => new(_temp.Database);

    private static readonly DateTimeOffset At = new(2026, 9, 12, 10, 0, 0, TimeSpan.FromHours(5.5));

    private Item Load(string sku, decimal? stock, decimal price = 150m, UnitType unit = UnitType.Each)
    {
        _temp.Items.UpsertRange([Catalogue.Item(sku: sku, name: $"Item {sku}", price: price, unit: unit) with { StockQty = stock }]);
        return _temp.Items.FindBySku(sku)!;
    }

    private Supplier Wholesaler(string name = "Murugan Traders", string? gstin = TamilNaduGstin, bool chargesGst = true) =>
        Purchases.AddSupplier(new Supplier(0, name, "9443012345", gstin, "33", chargesGst));

    private static PurchaseBill Bill(Supplier supplier, string billNo, params PurchaseLine[] lines) =>
        new(supplier, billNo, new DateOnly(2026, 9, 10), lines, InterState: supplier.StateCode != "33");

    private static PurchaseLine Line(Item item, decimal quantity, decimal rate, decimal gst = 5m, bool interState = false, bool chargesGst = true, decimal discount = 0m) =>
        PurchaseLine.Price(new PurchaseLineEntry(item, quantity, rate, gst, discount), interState, chargesGst);

    // ---- GSTIN -----------------------------------------------------------------------------------

    [Theory]
    [InlineData(MaharashtraGstin)]
    [InlineData(TamilNaduGstin)]
    [InlineData(" 27aapfu0939f1zv ")]
    public void ARealGstinIsAccepted(string gstin) => Assert.Null(Gstin.Problem(gstin));

    [Theory]
    [InlineData("27AAPFU0939F1ZW", "mistyped")]   // one character off
    [InlineData("27AAPFU0939F1Z", "fifteen")]     // one short
    [InlineData("99AAPFU0939F1ZV", "state code")] // no such state
    [InlineData("27AAPF10939F1ZV", "PAN")]        // not the shape of a PAN
    [InlineData("27AAPFU0939F1Z-", "letters and digits")]
    public void AWrongGstinSaysWhatIsWrong(string gstin, string says) =>
        Assert.Contains(says, Gstin.Problem(gstin));

    // ---- Pricing a line --------------------------------------------------------------------------

    [Fact]
    public void ALineIsTaxedOnTopOfTheRateAsTheWholesalerBillsIt()
    {
        var line = Line(Load("DAL", 0m), 10m, 100m);

        Assert.Equal(1_000.00m, line.TaxableValue);
        Assert.Equal(25.00m, line.Cgst);
        Assert.Equal(25.00m, line.Sgst);
        Assert.Equal(0m, line.Igst);
        Assert.Equal(1_050.00m, line.LineTotal);
        Assert.Equal(105.00m, line.UnitCost);
    }

    [Fact]
    public void ALineFromAnotherStateCarriesIgst()
    {
        var line = Line(Load("DAL", 0m), 10m, 100m, interState: true);

        Assert.Equal(0m, line.Cgst);
        Assert.Equal(50.00m, line.Igst);
        Assert.Equal(1_050.00m, line.LineTotal);
    }

    [Fact]
    public void ADiscountComesOffBeforeTax()
    {
        var line = Line(Load("DAL", 0m), 10m, 100m, discount: 50m);

        Assert.Equal(950.00m, line.TaxableValue);
        Assert.Equal(23.75m, line.Cgst);
        Assert.Equal(23.75m, line.Sgst);
        Assert.Equal(997.50m, line.LineTotal);
    }

    [Fact]
    public void ASupplierWhoDoesNotChargeGstChargesNone()
    {
        var line = Line(Load("DAL", 0m), 10m, 100m, chargesGst: false);

        Assert.Equal(0m, line.GstRate);
        Assert.Equal(0m, line.Tax);
        Assert.Equal(1_000.00m, line.LineTotal);
    }

    [Fact]
    public void ThingsSoldWholeArriveWhole()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Line(Load("SOAP", 0m), 2.5m, 30m));

        Assert.Equal(2.5m, Line(Load("SUGAR", 0m, unit: UnitType.Kilogram), 2.5m, 40m).Quantity);
    }

    [Fact]
    public void ThePrintedTotalIsMatchedWithARoundOffOfARupeeAtMost()
    {
        var supplier = Wholesaler();
        var bill = Bill(supplier, "MT/101", Line(Load("DAL", 0m), 10m, 100m));

        var (matched, problem) = bill.MatchPrinted(1_050.40m);
        Assert.Null(problem);
        Assert.Equal(0.40m, matched!.RoundOff);
        Assert.Equal(1_050.40m, matched.Total);

        var (none, tooFar) = bill.MatchPrinted(1_052.00m);
        Assert.Null(none);
        Assert.Contains("more than a round-off", tooFar);
    }

    // ---- Suppliers -------------------------------------------------------------------------------

    [Fact]
    public void ASuppliersStateComesFromTheirGstin()
    {
        var supplier = Purchases.AddSupplier(new Supplier(0, "Mumbai Dry Fruits", null, "27aapfu0939f1zv", "33", true));

        Assert.Equal(MaharashtraGstin, supplier.Gstin);
        Assert.Equal("27", supplier.StateCode);
        Assert.True(supplier.ChargesGst);
    }

    [Fact]
    public void ASupplierWithNoGstinCannotChargeGst()
    {
        var supplier = Purchases.AddSupplier(new Supplier(0, "Kannan Vegetables", null, null, "33", ChargesGst: true));

        Assert.False(supplier.ChargesGst);
    }

    [Fact]
    public void ASecondSupplierWithTheSameNameOrGstinIsRefused()
    {
        Wholesaler();

        Assert.Throws<InvalidOperationException>(() => Wholesaler());
        Assert.Throws<InvalidOperationException>(() => Wholesaler(name: "Another Name"));
        Assert.Throws<ArgumentException>(() => Purchases.AddSupplier(new Supplier(0, "Bad", null, "27AAPFU0939F1ZW", "27", true)));
    }

    // ---- Recording a bill ------------------------------------------------------------------------

    [Fact]
    public void ABillRaisesTheShelfAndTheCostPrice()
    {
        var dal = Load("DAL", stock: 4m, price: 150m);
        var supplier = Wholesaler();

        var recorded = Purchases.Record(Bill(supplier, "MT/101", Line(dal, 10m, 100m)), Lane, At, "Murugan");

        var after = _temp.Items.FindBySku("DAL")!;
        Assert.Equal(14m, after.StockQty);
        Assert.Equal(14m, after.FullLevel);
        Assert.Equal(105.00m, after.CostPrice);
        Assert.Equal(1, recorded.CountsMoved);
        Assert.Empty(recorded.NotCounted);
        Assert.Empty(recorded.CostAboveSellingPrice);

        var movement = new StockRepository(_temp.Database).History(dal.Id).First();
        Assert.Equal(StockReason.Purchase, movement.Reason);
        Assert.Equal("Murugan Traders bill MT/101", movement.Reference);
    }

    [Fact]
    public void AnItemNobodyCountsIsNotGivenACountByABill()
    {
        var sugar = Load("SUGAR", stock: null, unit: UnitType.Kilogram);

        var recorded = Purchases.Record(Bill(Wholesaler(), "MT/102", Line(sugar, 50m, 40m)), Lane, At, null);

        Assert.Null(_temp.Items.FindBySku("SUGAR")!.StockQty);
        Assert.Equal("Item SUGAR", Assert.Single(recorded.NotCounted));
    }

    [Fact]
    public void ACostAboveTheSellingPriceIsPointedOut()
    {
        var dal = Load("DAL", stock: 0m, price: 100m);

        var recorded = Purchases.Record(Bill(Wholesaler(), "MT/103", Line(dal, 10m, 100m)), Lane, At, null);

        Assert.Contains("costs 105.00, sells at 100.00", Assert.Single(recorded.CostAboveSellingPrice));
    }

    [Fact]
    public void TheSameBillEnteredTwiceIsRefused()
    {
        var dal = Load("DAL", stock: 0m);
        var supplier = Wholesaler();

        Purchases.Record(Bill(supplier, "MT/104", Line(dal, 10m, 100m)), Lane, At, null);

        var refused = Assert.Throws<InvalidOperationException>(() =>
            Purchases.Record(Bill(supplier, "mt/104", Line(dal, 10m, 100m)), Lane, At, null));

        Assert.Contains("already entered", refused.Message);
        Assert.Equal(10m, _temp.Items.FindBySku("DAL")!.StockQty);
    }

    /// <summary>
    /// A bill entered wrongly is cancelled, not deleted: it stops being owed, the shelf gives back
    /// exactly what it put on, and the bill can then be entered again properly.
    /// </summary>
    [Fact]
    public void ACancelledBillGivesBackItsStockAndCanBeEnteredAgain()
    {
        var dal = Load("DAL", stock: 4m);
        var supplier = Wholesaler();

        var first = Purchases.Record(Bill(supplier, "MT/105", Line(dal, 10m, 100m)), Lane, At, null);
        Purchases.Void(first.PurchaseId, "typed 10 for 1", Lane, At.AddMinutes(5));

        Assert.Equal(4m, _temp.Items.FindBySku("DAL")!.StockQty);
        Assert.Equal(0m, Purchases.Owed(supplier.Id));
        Assert.True(Purchases.Recent().Single().IsVoided);

        Purchases.Record(Bill(supplier, "MT/105", Line(dal, 1m, 100m)), Lane, At.AddMinutes(10), null);

        Assert.Equal(5m, _temp.Items.FindBySku("DAL")!.StockQty);
        Assert.Equal(105.00m, Purchases.Owed(supplier.Id));
        Assert.Throws<InvalidOperationException>(() => Purchases.Void(first.PurchaseId, "again", Lane, At));
    }

    [Fact]
    public void ACancellationNeedsAReason()
    {
        var recorded = Purchases.Record(Bill(Wholesaler(), "MT/106", Line(Load("DAL", 0m), 1m, 100m)), Lane, At, null);

        Assert.Throws<ArgumentException>(() => Purchases.Void(recorded.PurchaseId, " ", Lane, At));
    }

    // ---- What is owed ----------------------------------------------------------------------------

    [Fact]
    public void WhatIsOwedIsTheBillsLessThePayments()
    {
        var dal = Load("DAL", 0m);
        var supplier = Wholesaler();

        var bill = Bill(supplier, "MT/107", Line(dal, 10m, 100m)).MatchPrinted(1_050.40m).Bill!;
        Purchases.Record(bill, Lane, At, null);
        Assert.Equal(1_050.40m, Purchases.Owed(supplier.Id));

        Purchases.Pay(supplier.Id, 500m, SupplierPaymentMethod.Upi, "UPI 4455", Lane, At.AddHours(1), null);
        Assert.Equal(550.40m, Purchases.Owed(supplier.Id));

        var tooMuch = Assert.Throws<InvalidOperationException>(() =>
            Purchases.Pay(supplier.Id, 600m, SupplierPaymentMethod.Cheque, null, Lane, At, null));
        Assert.Contains("550.40", tooMuch.Message);

        var history = Purchases.History(supplier.Id);
        Assert.Equal("Paid, UPI (UPI 4455)", history[0].Description);
        Assert.Equal(-500m, history[0].Change);
        Assert.Equal(550.40m, history[0].BalanceAfter);
        Assert.Equal("Bill MT/107", history[1].Description);
        Assert.Equal(1_050.40m, history[1].BalanceAfter);
    }

    [Fact]
    public void TheListOfWhoIsOwedPutsTheMostFirst()
    {
        var dal = Load("DAL", 0m);
        var small = Wholesaler("Small Supplier", gstin: null);
        var big = Wholesaler("Big Supplier");
        Wholesaler("Paid Up", gstin: MaharashtraGstin);

        Purchases.Record(Bill(small, "S1", Line(dal, 1m, 100m, chargesGst: false)), Lane, At, null);
        Purchases.Record(Bill(big, "B1", Line(dal, 10m, 100m)), Lane, At, null);

        var owing = Purchases.Balances(owingOnly: true);

        Assert.Equal(["Big Supplier", "Small Supplier"], owing.Select(b => b.Supplier.Name));
        Assert.Equal(1_050m, owing[0].Owed);
        Assert.Equal(new DateOnly(2026, 9, 10), owing[0].LastBill);
        Assert.Equal(3, Purchases.Balances().Count);
    }

    // ---- The drawer ------------------------------------------------------------------------------

    /// <summary>
    /// A supplier paid out of the till is cash the drawer no longer holds. The day-end report takes
    /// it off what should be counted, says so on its own line, and a reprint still works out the
    /// change given the way the night's report did.
    /// </summary>
    [Fact]
    public void PayingASupplierFromTheTillComesOffTheDrawer()
    {
        var dal = Load("DAL", 0m, price: 420m);
        var supplier = Wholesaler();

        var lines = new[] { InvoiceLine.Rehydrate(dal.Id, dal.Name, "0713", null, null, UnitType.Each, 420m, 420m, true, 5m, 1m, 0m, false) };
        var totals = InvoiceTotals.From(lines);
        new InvoiceRepository(_temp.Database).Save(new SaleDraft(Lane, At, null, lines, totals,
            [new Tender(TenderType.Cash, 500m)], 80m, 0, 0, null));

        Purchases.Record(Bill(supplier, "MT/108", Line(dal, 10m, 100m)), Lane, At, null);
        Purchases.Pay(supplier.Id, 300m, SupplierPaymentMethod.DrawerCash, null, Lane, At.AddHours(1), "Murugan");
        Purchases.Pay(supplier.Id, 200m, SupplierPaymentMethod.Upi, null, Lane, At.AddHours(1), "Murugan");

        var closes = new DayCloseRepository(_temp.Database, new HeldBillRepository(_temp.Database));
        var closed = closes.Close(Lane, At.AddHours(8));

        Assert.Equal(120.00m, closed.CashExpected);
        Assert.Equal(300.00m, closed.CashPaidOut);
        var movement = Assert.Single(closed.DrawerMovementTotals);
        Assert.Equal(("SupplierPayment", 1, -300.00m), (movement.Kind, movement.Count, movement.Amount));

        var reprinted = closes.FindById(closed.Id)!;
        Assert.Equal(80.00m, reprinted.ChangeGiven);
        Assert.Equal(300.00m, reprinted.CashPaidOut);

        var paper = new ZReportComposer(new StoreProfile { Name = "Test Shop" }).Compose(reprinted).ToPlainText();
        output.WriteLine(paper);
        Assert.Contains("Paid to suppliers (1)", paper);
        Assert.Contains("-300.00", paper);

        // Stamped by that close, so tomorrow's does not count it again.
        Assert.Equal(0m, closes.Preview(Lane, At.AddDays(1)).CashPaidOut);
    }

    [Fact]
    public void ADayWithNoSalesButASupplierPaidStillReportsTheDrawer()
    {
        var supplier = Wholesaler();
        Purchases.Record(Bill(supplier, "MT/109", Line(Load("DAL", 0m), 10m, 100m)), Lane, At, null);
        Purchases.Pay(supplier.Id, 250m, SupplierPaymentMethod.DrawerCash, null, Lane, At, null);

        var preview = new DayCloseRepository(_temp.Database, new HeldBillRepository(_temp.Database)).Preview(Lane, At.AddHours(1));

        Assert.True(preview.TookNothing);
        Assert.True(preview.MovedMoneyWithoutSales);
        Assert.Equal(-250.00m, preview.CashExpected);

        var paper = new ZReportComposer(new StoreProfile { Name = "Test Shop" }).Compose(preview).ToPlainText();
        Assert.Contains("Paid to suppliers (1)", paper);
    }

    // ---- The GST return --------------------------------------------------------------------------

    [Fact]
    public void TheMonthsPurchasesGiveTheInputTaxToClaim()
    {
        var dal = Load("DAL", 0m);
        var soap = Load("SOAP", 0m);

        var local = Wholesaler();
        var mumbai = Purchases.AddSupplier(new Supplier(0, "Mumbai Dry Fruits", null, MaharashtraGstin, "27", true));
        var farmer = Wholesaler("Kannan Vegetables", gstin: null);

        Purchases.Record(Bill(local, "MT/110", Line(dal, 10m, 100m), Line(soap, 10m, 50m, gst: 18m)), Lane, At, null);
        Purchases.Record(Bill(mumbai, "MD/9", Line(dal, 10m, 200m, interState: true)), Lane, At, null);
        Purchases.Record(Bill(farmer, "K1", Line(dal, 10m, 30m, chargesGst: false)), Lane, At, null);

        var cancelled = Purchases.Record(Bill(local, "MT/111", Line(dal, 5m, 100m)), Lane, At, null);
        Purchases.Void(cancelled.PurchaseId, "wrong supplier", Lane, At);

        Purchases.Record(Bill(local, "MT/112", Line(dal, 5m, 100m)) with { BillDate = new DateOnly(2026, 8, 31) }, Lane, At, null);

        var d = new GstReturnQuery(_temp.Database).Gather(Lane, new DateOnly(2026, 9, 1), "33");
        output.WriteLine(GstReturnFiles.PurchaseRegister(d));

        Assert.Equal(3, d.Purchases.Count);

        // 5%: 1,000 locally (25 + 25) and 2,000 from Mumbai (100 IGST). 18%: 500 of soap (45 + 45).
        Assert.Equal(
            [
                new GstInputRow(5m, 3_000.00m, 100.00m, 25.00m, 25.00m),
                new GstInputRow(18m, 500.00m, 0m, 45.00m, 45.00m),
            ],
            d.Inputs);
        Assert.Equal(240.00m, d.InputTax);

        Assert.Contains(d.Warnings, w => w.Contains("do not charge GST") && w.Contains("300.00"));
        Assert.Contains("27AAPFU0939F1ZV,Mumbai Dry Fruits,MD/9,10-09-2026,2000.00,100.00,0.00,0.00,2100.00,Yes", GstReturnFiles.PurchaseRegister(d));
        Assert.Contains(",Kannan Vegetables,K1,10-09-2026,300.00,0.00,0.00,0.00,300.00,No", GstReturnFiles.PurchaseRegister(d));
    }
}
