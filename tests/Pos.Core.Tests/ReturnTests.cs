using Pos.Core.Analytics;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Domain.Printing;
using Pos.Core.Hardware.Drawer;
using Pos.TestSupport;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// Returns: goods coming back against a bill, on a credit note, and what that does to the tax, the
/// shelf, the drawer, the khata, the points, the day-end report and the GST return.
/// </summary>
/// <remarks>
/// The figures are worked on paper. ₹100 at 5% inclusive has a taxable value of 95.2381 and tax of
/// 4.76, split 2.38 and 2.38. Three of them: 285.7143, tax 14.29, split 7.14 and 7.15 - so three
/// returns of one, priced afresh each time, would take back 7.14 + 7.14 of SGST where 7.15 was
/// charged. The last return of a line takes exactly what is left instead.
/// </remarks>
public class ReturnTests : IDisposable
{
    private const string Lane = "L1";
    private const string HomeState = "33";

    private readonly TempDatabase _temp = new();
    private readonly RecordingDrawerService _drawer = new();

    public void Dispose() => _temp.Dispose();

    private CreditNoteRepository Notes => new(_temp.Database);

    private CustomerRepository Customers => new(_temp.Database);

    private InvoiceRepository Invoices => new(_temp.Database);

    private StockRepository Stock => new(_temp.Database);

    private Item Load(string sku = "DAL001", decimal price = 100m, decimal gst = 5m, decimal? stock = null, UnitType unit = UnitType.Each)
    {
        _temp.Items.UpsertRange([Catalogue.Item(sku: sku, name: $"Item {sku}", price: price, gstRate: gst, unit: unit) with { StockQty = stock }]);
        return _temp.Items.FindBySku(sku)!;
    }

    private Customer Known(string mobile = "9876543210", string? state = HomeState) =>
        Customers.Add(new Customer { MobileNo = mobile, Name = "Lakshmi", StateCode = state });

    /// <summary>One sale, paid in cash unless a share of it is put on the khata.</summary>
    private SettledInvoice Sell(Item item, decimal quantity = 1m, Customer? customer = null, decimal discount = 0m, decimal onCredit = 0m, bool roundToRupee = false)
    {
        var bill = new InvoiceEngine(HomeState, roundToRupee: roundToRupee);
        bill.AddItem(item, quantity);

        if (discount > 0m)
            bill.SetDiscount(0, discount);

        if (customer is not null)
            bill.SetCustomer(customer);

        var payable = bill.Totals.AmountPayable;
        var basket = new TenderBasket(payable);

        if (onCredit > 0m)
            basket.Add(TenderType.StoreCredit, onCredit);

        if (payable - onCredit > 0m)
            basket.Add(TenderType.Cash, payable - onCredit);

        return new CheckoutService(Invoices, Customers, _drawer, null, TimeProvider.System, stock: Stock)
            .Complete(Lane, bill, basket).Invoice;
    }

    private ReturnableBill Returnable(SettledInvoice invoice) => Notes.Returnable(invoice.InvoiceNo)!;

    private CreditNote Return(
        SettledInvoice invoice,
        decimal quantity,
        TenderType refund = TenderType.Cash,
        bool restock = true,
        int line = 1,
        DateTimeOffset? at = null,
        bool roundToRupee = false)
    {
        var draft = CreditNoteDraft.Build(Returnable(invoice), [(line, quantity, restock)], refund, "changed mind", roundToRupee);
        return Notes.Issue(draft, Lane, at ?? DateTimeOffset.Now, "Priya");
    }

    // ---- Pricing a return ------------------------------------------------------------------------

    [Fact]
    public void APartReturnIsPricedTheWayTheSaleWas()
    {
        var sale = Sell(Load(), 3m);

        var line = CreditNoteLine.Price(Returnable(sale).Lines[0], 1m, restock: true);

        Assert.Equal(95.24m, line.TaxableValue);
        Assert.Equal(2.38m, line.Cgst);
        Assert.Equal(2.38m, line.Sgst);
        Assert.Equal(0m, line.Igst);
        Assert.Equal(100.00m, line.LineTotal);
    }

    /// <summary>
    /// Three returns of one take back exactly what three were sold for: 285.71 taxable, 7.14 CGST
    /// and 7.15 SGST. Priced afresh, the third would have taken 2.38 of SGST and left a paisa of tax
    /// charged on goods that all came back.
    /// </summary>
    [Fact]
    public void ReturnsOfALineInPartsTakeBackExactlyWhatItWasSoldFor()
    {
        var sale = Sell(Load(), 3m);

        var notes = new[] { Return(sale, 1m), Return(sale, 1m), Return(sale, 1m) };
        var last = notes[2].Lines[0];

        Assert.Equal(95.23m, last.TaxableValue);
        Assert.Equal(2.38m, last.Cgst);
        Assert.Equal(2.39m, last.Sgst);
        Assert.Equal(100.00m, last.LineTotal);

        Assert.Equal(285.71m, notes.Sum(n => n.TaxableValue));
        Assert.Equal(7.14m, notes.Sum(n => n.Cgst));
        Assert.Equal(7.15m, notes.Sum(n => n.Sgst));
        Assert.Equal(300.00m, notes.Sum(n => n.Refunded));
    }

    [Fact]
    public void AWholeLineReturnedAtOnceCreditsTheSaleToThePaisa()
    {
        var sale = Sell(Load(), 3m);

        var note = Return(sale, 3m);

        Assert.Equal(285.71m, note.TaxableValue);
        Assert.Equal(7.14m, note.Cgst);
        Assert.Equal(7.15m, note.Sgst);
        Assert.Equal(300.00m, note.Refunded);
    }

    /// <summary>Two of five packets take two fifths of the ₹10 discount with them: 40 less 4 is 36.</summary>
    [Fact]
    public void ADiscountGoesBackInProportion()
    {
        var sale = Sell(Load(price: 20m, gst: 18m), 5m, discount: 10m);

        var line = CreditNoteLine.Price(Returnable(sale).Lines[0], 2m, restock: true);

        Assert.Equal(30.51m, line.TaxableValue);
        Assert.Equal(2.74m, line.Cgst);
        Assert.Equal(2.75m, line.Sgst);
        Assert.Equal(36.00m, line.LineTotal);
    }

    [Fact]
    public void WeighedGoodsComeBackByWeight()
    {
        var sale = Sell(Load(price: 48m, unit: UnitType.Kilogram), 2.75m);

        var line = CreditNoteLine.Price(Returnable(sale).Lines[0], 1.5m, restock: true);

        Assert.Equal(68.57m, line.TaxableValue);
        Assert.Equal(1.71m, line.Cgst);
        Assert.Equal(1.72m, line.Sgst);
        Assert.Equal(72.00m, line.LineTotal);
    }

    [Fact]
    public void ThingsSoldWholeComeBackWhole()
    {
        var sale = Sell(Load(), 3m);

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => CreditNoteLine.Price(Returnable(sale).Lines[0], 1.5m, restock: true));

        Assert.Contains("comes back whole", error.Message);
    }

    [Fact]
    public void ASaleIntoAnotherStateIsReturnedWithItsIgst()
    {
        var sale = Sell(Load(), 3m, customer: Known(state: "29"));

        var line = CreditNoteLine.Price(Returnable(sale).Lines[0], 1m, restock: true);

        Assert.True(line.InterState);
        Assert.Equal(0m, line.Cgst + line.Sgst);
        Assert.Equal(4.76m, line.Igst);
        Assert.Equal(100.00m, line.LineTotal);
    }

    /// <summary>A lane that settles to the rupee refunds to the rupee: 33.33 goes back as 33.00.</summary>
    [Fact]
    public void ALaneThatRoundsRefundsToTheRupee()
    {
        var sale = Sell(Load(price: 33.33m), 3m, roundToRupee: true);

        var draft = CreditNoteDraft.Build(Returnable(sale), [(1, 1m, true)], TenderType.Cash, null, roundToRupee: true);

        Assert.Equal(33.33m, draft.LinesTotal);
        Assert.Equal(-0.33m, draft.RoundOff);
        Assert.Equal(33.00m, draft.Refunded);
    }

    // ---- What can come back ----------------------------------------------------------------------

    [Fact]
    public void NothingComesBackTwice()
    {
        var sale = Sell(Load(), 3m);
        Return(sale, 2m);

        var bill = Returnable(sale);

        Assert.Equal(2m, bill.Lines[0].AlreadyReturned);
        Assert.Equal(1m, bill.Lines[0].Remaining);

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => CreditNoteLine.Price(bill.Lines[0], 2m, restock: true));
        Assert.Contains("Only 1", error.Message);

        Return(sale, 1m);
        Assert.False(Returnable(sale).AnythingLeft);
    }

    /// <summary>
    /// Another lane took goods back off the same bill after this one looked it up. What this draft
    /// was priced against is no longer what is left, so it is refused rather than issued.
    /// </summary>
    [Fact]
    public void AReturnWorkedOutBeforeAnotherIsIssuedIsRefused()
    {
        var sale = Sell(Load(), 3m);
        var stale = CreditNoteDraft.Build(Returnable(sale), [(1, 2m, true)], TenderType.Cash, null, false);

        Return(sale, 2m);

        var error = Assert.Throws<InvalidOperationException>(() => Notes.Issue(stale, Lane, DateTimeOffset.Now, null));

        Assert.Contains("since it was looked up", error.Message);
        Assert.Single(Notes.ForInvoice(sale.InvoiceNo));
    }

    [Fact]
    public void AVoidedBillHasNothingToReturn()
    {
        var sale = Sell(Load(), 3m);
        var draft = CreditNoteDraft.Build(Returnable(sale), [(1, 1m, true)], TenderType.Cash, null, false);

        Invoices.Void(sale.InvoiceNo, DateTimeOffset.Now, "rung up twice");

        Assert.True(Returnable(sale).Invoice.IsVoided);
        Assert.Throws<InvalidOperationException>(() => Notes.Issue(draft, Lane, DateTimeOffset.Now, null));
    }

    /// <summary>
    /// Once goods have come back on a credit note the bill cannot be voided as well: the refund
    /// would come off the day twice.
    /// </summary>
    [Fact]
    public void ABillWithReturnsAgainstItCannotBeVoided()
    {
        var sale = Sell(Load(), 3m);
        Return(sale, 1m);

        var error = Assert.Throws<InvalidOperationException>(() => Invoices.Void(sale.InvoiceNo, DateTimeOffset.Now, null));

        Assert.Contains("credit note", error.Message);
        Assert.False(Invoices.FindByInvoiceNo(sale.InvoiceNo)!.IsVoided);
    }

    [Fact]
    public void AReturnIsRefundedInMoneyNotPoints() =>
        Assert.Throws<ArgumentException>(() =>
            CreditNoteDraft.Build(Returnable(Sell(Load())), [(1, 1m, true)], TenderType.LoyaltyPoints, null, false));

    // ---- Numbering -------------------------------------------------------------------------------

    /// <summary>A series of its own, and the bills' run is left unbroken.</summary>
    [Fact]
    public void CreditNotesNumberInTheirOwnSeries()
    {
        var at = new DateTimeOffset(2026, 9, 12, 10, 0, 0, TimeSpan.FromHours(5.5));
        var sale = Sell(Load(), 3m);

        Assert.Equal("CN/26-27/L1-1", Return(sale, 1m, at: at).Number);
        Assert.Equal("CN/26-27/L1-2", Return(sale, 1m, at: at).Number);

        var next = Sell(Load(), 1m);
        Assert.EndsWith("/L1-2", next.InvoiceNo);
    }

    [Fact]
    public void ACreditNoteReadsBackAsItWasIssued()
    {
        var sale = Sell(Load(), 3m, customer: Known());
        var issued = Return(sale, 1m, restock: false);

        var found = Notes.Find(issued.Number)!;

        Assert.Equal(sale.InvoiceNo, found.InvoiceNo);
        Assert.Equal("changed mind", found.Reason);
        Assert.Equal("Priya", found.CashierName);
        Assert.Equal(TenderType.Cash, found.Refund);
        Assert.Equal("Lakshmi", found.Customer!.Name);
        Assert.False(Assert.Single(found.Lines).Restocked);
        Assert.Equal(100.00m, found.Refunded);
    }

    // ---- The shelf -------------------------------------------------------------------------------

    [Fact]
    public void GoodsFitToSellGoBackOnTheShelf()
    {
        var dal = Load(stock: 10m);
        var sale = Sell(dal, 3m);

        Return(sale, 2m);

        Assert.Equal(9m, _temp.Items.FindBySku("DAL001")!.StockQty);
    }

    [Fact]
    public void DamagedGoodsAreRefundedButNotCountedBackIn()
    {
        var dal = Load(stock: 10m);
        var sale = Sell(dal, 3m);

        Return(sale, 2m, restock: false);

        Assert.Equal(7m, _temp.Items.FindBySku("DAL001")!.StockQty);
    }

    // ---- The drawer and the day ------------------------------------------------------------------

    [Fact]
    public void ACashRefundComesOutOfTheDrawerOnTheDayEndReport()
    {
        var sale = Sell(Load(), 3m);
        Return(sale, 1m);

        var day = new DayCloseRepository(_temp.Database).Preview(Lane, DateTimeOffset.Now);

        Assert.Equal(300.00m, day.NetSales);
        Assert.Equal(200.00m, day.CashExpected);
        Assert.Equal(100.00m, day.CashPaidOut);
        Assert.Equal(1, day.ReturnsCount);
        Assert.Equal(100.00m, day.ReturnsValue);
        Assert.Equal(4.76m, day.ReturnsTax);
        Assert.Equal(200.00m, day.NetAfterReturns);

        var refund = Assert.Single(day.DrawerMovementTotals);
        Assert.Equal(CreditNoteRepository.RefundKind, refund.Kind);
        Assert.Equal(-100.00m, refund.Amount);
    }

    [Fact]
    public void AUpiRefundLeavesTheDrawerAlone()
    {
        var sale = Sell(Load(), 3m);
        Return(sale, 1m, TenderType.Upi);

        var day = new DayCloseRepository(_temp.Database).Preview(Lane, DateTimeOffset.Now);

        Assert.Equal(300.00m, day.CashExpected);
        Assert.Equal(1, day.ReturnsCount);
        Assert.Empty(day.DrawerMovementTotals);
    }

    /// <summary>
    /// Reported once, on the close it fell in, and read back the same from the stored report. A
    /// return against a bill from an earlier day is today's return.
    /// </summary>
    [Fact]
    public void AReturnIsOnExactlyOneDayEndReport()
    {
        var closes = new DayCloseRepository(_temp.Database);
        var sale = Sell(Load(), 3m);

        closes.Close(Lane, DateTimeOffset.Now);
        Return(sale, 1m);

        var today = closes.Close(Lane, DateTimeOffset.Now);
        var again = closes.Preview(Lane, DateTimeOffset.Now);
        var stored = closes.FindById(today.Id)!;

        Assert.True(today.TookNothing);
        Assert.True(today.MovedMoneyWithoutSales);
        Assert.Equal(1, today.ReturnsCount);
        Assert.Equal(-100.00m, today.CashExpected);
        Assert.Equal(0, again.ReturnsCount);

        Assert.Equal(1, stored.ReturnsCount);
        Assert.Equal(100.00m, stored.ReturnsValue);
        Assert.Equal(4.76m, stored.ReturnsTax);
        Assert.Equal(0m, stored.ChangeGiven);
    }

    [Fact]
    public void TheDayEndReportPrintsTheReturns()
    {
        var sale = Sell(Load(), 3m);
        Return(sale, 1m);

        var day = new DayCloseRepository(_temp.Database).Preview(Lane, DateTimeOffset.Now);
        var text = new ZReportComposer(new StoreProfile { Name = "Sri Murugan Stores" }).Compose(day).ToPlainText();

        Assert.Contains("Returns", text);
        Assert.Contains("Refunded on returns (1)", text);
        Assert.Contains("-100.00", text);
        Assert.Contains("Tax reversed", text);
        Assert.Contains("Net after returns", text);
        Assert.Contains("Reconciled", text);
    }

    // ---- The khata and the points ----------------------------------------------------------------

    [Fact]
    public void ARefundToTheKhataComesOffWhatTheyOwe()
    {
        var lakshmi = Known();
        var sale = Sell(Load(), 3m, customer: lakshmi, onCredit: 300m);

        var note = Return(sale, 1m, TenderType.StoreCredit);
        var credit = new CreditRepository(_temp.Database);

        Assert.Equal(200.00m, credit.Balance(lakshmi.Id));
        Assert.Equal(200.00m, Assert.Single(credit.Owing()).Owed);

        var latest = credit.History(lakshmi.Id)[0];
        Assert.Equal($"Goods returned, credit note {note.Number}", latest.Description);
        Assert.Equal(-100.00m, latest.Change);
        Assert.Equal(200.00m, latest.BalanceAfter);

        // Not money in the drawer, and not a repayment.
        var day = new DayCloseRepository(_temp.Database).Preview(Lane, DateTimeOffset.Now);
        Assert.Equal(0m, day.CreditCollected);
        Assert.Equal(0m, day.CashExpected);
    }

    [Fact]
    public void TheKhataIsNotRefundedBelowNothing()
    {
        var lakshmi = Known();
        var sale = Sell(Load(), 3m, customer: lakshmi, onCredit: 100m);

        var error = Assert.Throws<InvalidOperationException>(() => Return(sale, 2m, TenderType.StoreCredit));

        Assert.Contains("owe 100.00", error.Message);
        Assert.Equal(100.00m, new CreditRepository(_temp.Database).Balance(lakshmi.Id));
    }

    [Fact]
    public void AWalkInIsNotRefundedToAKhata()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            CreditNoteDraft.Build(Returnable(Sell(Load())), [(1, 1m, true)], TenderType.StoreCredit, null, false));

        Assert.Contains("customer", error.Message);
    }

    /// <summary>300 earned 6 points at one per ₹50. A third of it back takes 2 of them.</summary>
    [Fact]
    public void PointsEarnedOnReturnedGoodsComeBackOff()
    {
        var lakshmi = Known();
        var sale = Sell(Load(), 3m, customer: lakshmi);

        Assert.Equal(6, sale.Sale.PointsEarned);
        Assert.Equal(6, Customers.FindByMobile(lakshmi.MobileNo)!.LoyaltyBalance);

        var note = Return(sale, 1m);

        Assert.Equal(2, note.PointsReversed);
        Assert.Equal(4, Customers.FindByMobile(lakshmi.MobileNo)!.LoyaltyBalance);
    }

    /// <summary>
    /// 350 earned 7 points. Returned two, two and three at a time: 2, then 2, then the 3 left - not
    /// 2, 2 and 2, which would let the customer keep a point on goods that all came back.
    /// </summary>
    [Fact]
    public void PointsTakenBackInPartsAddUpToThePointsEarned()
    {
        var lakshmi = Known();
        var sale = Sell(Load(price: 50m), 7m, customer: lakshmi);

        Assert.Equal(7, sale.Sale.PointsEarned);

        var taken = new[] { Return(sale, 2m).PointsReversed, Return(sale, 2m).PointsReversed, Return(sale, 3m).PointsReversed };

        Assert.Equal([2, 2, 3], taken);
        Assert.Equal(0, Customers.FindByMobile(lakshmi.MobileNo)!.LoyaltyBalance);
    }

    [Fact]
    public void ACustomerWithReturnsCanStillBeForgotten()
    {
        var lakshmi = Known();
        var sale = Sell(Load(), 3m, customer: lakshmi);
        var note = Return(sale, 1m);

        Assert.True(Customers.Forget(lakshmi.Id) >= 1);
        Assert.Null(Notes.Find(note.Number)!.Customer);
    }

    // ---- The paper -------------------------------------------------------------------------------

    [Fact]
    public void TheCreditNoteSaysWhatItIsAndWhatItReverses()
    {
        var sale = Sell(Load(), 3m, customer: Known());
        var note = Return(sale, 1m, restock: false);

        var text = new ReceiptComposer(new StoreProfile { Name = "Sri Murugan Stores", Gstin = "33AEIPH7795F1Z9" })
            .ComposeCreditNote(note).ToPlainText();

        Assert.Contains("CREDIT NOTE", text);
        Assert.Contains(note.Number, text);
        Assert.Contains($"Against bill: {sale.InvoiceNo}", text);
        Assert.Contains("damaged, not restocked", text);
        Assert.Contains("Tax reversed", text);
        Assert.Contains("4.76", text);
        Assert.Contains("Refund (Cash)", text);
        Assert.Contains("100.00", text);
        Assert.Contains("changed mind", text);
        Assert.DoesNotContain("TAX INVOICE", text);
    }

    [Fact]
    public void TheServiceOpensTheDrawerOnlyForCash()
    {
        var service = new ReturnService(Notes, _drawer);
        var sale = Sell(Load(), 3m);
        var kicksAfterSale = _drawer.KickCount;

        var upi = service.Issue(service.Draft(service.Find(sale.InvoiceNo)!, [(1, 1m, true)], TenderType.Upi, null), Lane);
        Assert.Equal(DrawerKickResult.NoDrawerAttached, upi.Drawer);
        Assert.Equal(kicksAfterSale, _drawer.KickCount);

        var cash = service.Issue(service.Draft(service.Find(sale.InvoiceNo)!, [(1, 1m, true)], TenderType.Cash, null), Lane);
        Assert.Equal(DrawerKickResult.Opened, cash.Drawer);
        Assert.Equal(kicksAfterSale + 1, _drawer.KickCount);
    }

    /// <summary>The owner's figures show what was refunded beside the sales, which stay as billed.</summary>
    [Fact]
    public void TheOwnersFiguresShowTheReturnsBesideTheSales()
    {
        var sale = Sell(Load(), 3m);
        Return(sale, 1m);

        var to = DateTimeOffset.Now.AddMinutes(1);
        var data = new DashboardQuery(_temp.Database).Gather(Lane, to.AddDays(-1), to);

        Assert.Equal(300.00m, data.Range.NetSales);
        Assert.Equal(1, data.Returns.Count);
        Assert.Equal(100.00m, data.Returns.Value);
        Assert.Contains("Goods returned", DashboardPage.Render(data, "Sri Murugan Stores"));
    }

    // ---- The GST return --------------------------------------------------------------------------

    /// <summary>
    /// Sold 3 at 285.71 taxable, 7.14 + 7.15; one back at 95.24, 2.38 + 2.38. The month files 190.47,
    /// 4.76 and 4.77, and two in the HSN summary.
    /// </summary>
    [Fact]
    public void TheMonthsReturnIsNetOfTheGoodsThatCameBack()
    {
        var sale = Sell(Load(), 3m);
        var note = Return(sale, 1m);

        var month = DateOnly.FromDateTime(DateTime.Today);
        var data = new GstReturnQuery(_temp.Database).Gather(Lane, month, HomeState);

        var row = Assert.Single(data.RateWise);
        Assert.Equal(190.47m, row.TaxableValue);
        Assert.Equal(4.76m, row.Cgst);
        Assert.Equal(4.77m, row.Sgst);

        var hsn = Assert.Single(data.Hsn);
        Assert.Equal(2m, hsn.Quantity);
        Assert.Equal(200.00m, hsn.TotalValue);
        Assert.Equal(190.47m, hsn.TaxableValue);

        Assert.Equal(1, data.CreditNotes);
        Assert.Equal(100.00m, data.CreditNotesValue);
        Assert.Contains(data.Warnings, w => w.Contains("credit note", StringComparison.Ordinal));

        var series = Assert.Single(data.Documents, d => d.Nature == GstDocumentSeries.CreditNotes);
        Assert.Equal(note.Number, series.From);
        Assert.Equal(1, series.Total);
        Assert.Contains("Credit Note,CN/", GstReturnFiles.Documents(data));
    }

    [Fact]
    public void ReturnedNilRatedGoodsComeOffTheNilFigure()
    {
        var sale = Sell(Load(sku: "RICE", price: 60m, gst: 0m), 3m);
        Return(sale, 1m);

        var data = new GstReturnQuery(_temp.Database).Gather(Lane, DateOnly.FromDateTime(DateTime.Today), HomeState);

        Assert.Equal(120.00m, data.NilIntraState);
    }
}
