using System.Text;
using System.Xml.Linq;
using Pos.Core.Analytics;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// The day book for the accountant: every bill, return, khata repayment, purchase, payment, debit
/// note, expense and cash moved, as vouchers that balance to the paisa, and the files they are
/// written to.
/// </summary>
/// <remarks>Figures that work on paper: ₹105 at 5% is ₹100 and ₹2.50 each of CGST and SGST; ₹118 at 18% is ₹100 and ₹9 each.</remarks>
public class DayBookTests : IDisposable
{
    private const string Lane = "L1";
    private const string Home = "33";
    private const string TamilNaduGstin = "33AEIPH7795F1Z9";

    private static readonly DateTimeOffset In = new(2026, 9, 12, 10, 0, 0, TimeSpan.FromHours(5.5));
    private static readonly DateOnly September = new(2026, 9, 1);

    private readonly TempDatabase _temp = new();

    public void Dispose() => _temp.Dispose();

    private sealed class At(DateTimeOffset moment) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => moment.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromHours(5.5), "IST", "IST");
    }

    private CustomerRepository Customers => new(_temp.Database);

    private PurchaseRepository Purchases => new(_temp.Database);

    private DayBookData Book(DayBookLedgers? ledgers = null, string lane = Lane) =>
        new DayBookQuery(_temp.Database).Month(lane, September, ledgers ?? new DayBookLedgers());

    private Item Item(string sku, string name, decimal price, decimal rate)
    {
        _temp.Items.UpsertRange([Catalogue.Item(sku: sku, name: name, price: price, gstRate: rate)]);
        return _temp.Items.FindBySku(sku)!;
    }

    private Item Dal => Item("DAL", "Toor Dal 1kg", 105m, 5m);
    private Item Oil => Item("OIL", "Groundnut Oil 1L", 118m, 18m);

    private Customer Lakshmi(string? state = null) =>
        Customers.FindByMobile("9500012345") ?? Customers.Add(new Customer { MobileNo = "9500012345", Name = "Lakshmi", StateCode = state });

    /// <summary>A bill, paid as given, on a lane and at a moment.</summary>
    private SettledInvoice Sell(IEnumerable<Item> items, IEnumerable<(TenderType Type, decimal Amount)> paid, Customer? customer = null,
        DateTimeOffset? at = null, string lane = Lane, TaxMode mode = TaxMode.Gst, bool roundToRupee = false)
    {
        var bill = new InvoiceEngine(Home, mode, roundToRupee);

        foreach (var item in items)
            bill.AddItem(item);

        bill.SetCustomer(customer);

        var basket = new TenderBasket(bill.Totals.AmountPayable);

        foreach (var (type, amount) in paid)
            basket.Add(type, amount);

        return new CheckoutService(new InvoiceRepository(_temp.Database), Customers, new RecordingDrawerService(), clock: new At(at ?? In))
            .Complete(lane, bill, basket).Invoice;
    }

    private static void Balanced(DayBookVoucher voucher) =>
        Assert.True(voucher.IsBalanced, $"{voucher.Kind} {voucher.Number}: debits {voucher.Debits}, credits {voucher.Credits}");

    private static (string Ledger, LedgerGroup Group, decimal Debit, decimal Credit)[] Entries(DayBookVoucher voucher) =>
        [.. voucher.Entries.Select(e => (e.Ledger, e.Group, e.Debit, e.Credit))];

    // ---- Bills ----------------------------------------------------------------------------------

    [Fact]
    public void ACashBillIsTheCashTakenAgainstEachRatesSalesAndItsTax()
    {
        // ₹223, paid with a ₹500 note: ₹277 change, so ₹223 into the cash.
        var sale = Sell([Dal, Oil], [(TenderType.Cash, 500m)]);

        var voucher = Assert.Single(Book().Vouchers);

        Assert.Equal(VoucherKind.Sales, voucher.Kind);
        Assert.Equal(sale.InvoiceNo, voucher.Number);
        Assert.Equal(new DateOnly(2026, 9, 12), voucher.Date);
        Assert.Null(voucher.Party);
        Assert.Equal(
        [
            ("Cash", LedgerGroup.CashInHand, 223.00m, 0m),
            ("Sales @ 5%", LedgerGroup.SalesAccounts, 0m, 100.00m),
            ("Output CGST", LedgerGroup.DutiesAndTaxes, 0m, 11.50m),
            ("Output SGST", LedgerGroup.DutiesAndTaxes, 0m, 11.50m),
            ("Sales @ 18%", LedgerGroup.SalesAccounts, 0m, 100.00m),
        ], Entries(voucher));
        Balanced(voucher);
    }

    [Fact]
    public void ABillPaidSeveralWaysDebitsEachAndTheKhataIsTheCustomersOwnLedger()
    {
        Sell([Dal, Oil], [(TenderType.Upi, 100m), (TenderType.Card, 23m), (TenderType.StoreCredit, 100m)], Lakshmi());

        var voucher = Assert.Single(Book().Vouchers);

        Assert.Equal("Lakshmi (9500012345)", voucher.Party);
        Assert.Equal(
        [
            ("Card collections", LedgerGroup.BankAccounts, 23.00m, 0m),
            ("UPI collections", LedgerGroup.BankAccounts, 100.00m, 0m),
            ("Lakshmi (9500012345)", LedgerGroup.SundryDebtors, 100.00m, 0m),
        ], Entries(voucher)[..3]);
        Balanced(voucher);
    }

    [Fact]
    public void LoyaltyPointsSpentAreADebitOfTheirOwn()
    {
        var lakshmi = Lakshmi();
        Customers.UpdateLoyaltyBalance(lakshmi.Id, 500);

        Sell([Dal], [(TenderType.LoyaltyPoints, 5m), (TenderType.Cash, 100m)], Customers.FindByMobile("9500012345"));

        var voucher = Assert.Single(Book().Vouchers);

        Assert.Contains(("Loyalty points redeemed", LedgerGroup.IndirectExpenses, 5.00m, 0m), Entries(voucher));
        Assert.Contains(("Cash", LedgerGroup.CashInHand, 100.00m, 0m), Entries(voucher));
        Balanced(voucher);
    }

    [Fact]
    public void ABillToAnotherStateIsInterStateSalesAndIgst()
    {
        Sell([Dal], [(TenderType.Upi, 105m)], Lakshmi(state: "29"));

        Assert.Equal(
        [
            ("UPI collections", LedgerGroup.BankAccounts, 105.00m, 0m),
            ("Sales inter-state @ 5%", LedgerGroup.SalesAccounts, 0m, 100.00m),
            ("Output IGST", LedgerGroup.DutiesAndTaxes, 0m, 5.00m),
        ], Entries(Assert.Single(Book().Vouchers)));
    }

    [Theory]
    [InlineData(45.40, 45, 0.40, 0)]
    [InlineData(45.60, 46, 0, 0.40)]
    public void ARoundOffIsOnItsOwnLedgerOnWhicheverSideItFalls(double price, int paid, double debit, double credit)
    {
        // Nil rated, so the figures are the price and nothing else.
        var rice = Item("RICE", "Ponni Rice Loose", (decimal)price, 0m);

        Sell([rice], [(TenderType.Cash, paid)], roundToRupee: true);

        var voucher = Assert.Single(Book().Vouchers);

        Assert.Equal(("Cash", LedgerGroup.CashInHand, (decimal)paid, 0m), Entries(voucher)[0]);
        Assert.Contains(("Sales @ 0%", LedgerGroup.SalesAccounts, 0m, (decimal)price), Entries(voucher));
        Assert.Contains(("Round off", LedgerGroup.IndirectExpenses, (decimal)debit, (decimal)credit), Entries(voucher));
        Balanced(voucher);
    }

    [Fact]
    public void ABillOfSupplyIsSalesWithNoTax()
    {
        Sell([Dal], [(TenderType.Cash, 105m)], mode: TaxMode.Composition);

        Assert.Equal(
        [
            ("Cash", LedgerGroup.CashInHand, 105.00m, 0m),
            ("Sales, bill of supply", LedgerGroup.SalesAccounts, 0m, 105.00m),
        ], Entries(Assert.Single(Book().Vouchers)));
    }

    [Fact]
    public void AVoidedBillIsNotInIt()
    {
        var sale = Sell([Dal], [(TenderType.Cash, 105m)]);
        new CheckoutService(new InvoiceRepository(_temp.Database), Customers, new RecordingDrawerService(), clock: new At(In)).VoidSale(sale.InvoiceNo);

        Assert.Empty(Book().Vouchers);
    }

    [Fact]
    public void OnlyThisLaneAndThisMonth()
    {
        Sell([Dal], [(TenderType.Cash, 105m)], at: new DateTimeOffset(2026, 8, 31, 23, 59, 0, TimeSpan.FromHours(5.5)));
        Sell([Dal], [(TenderType.Cash, 105m)], at: new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(5.5)));
        Sell([Dal], [(TenderType.Cash, 105m)], lane: "L2");
        var mine = Sell([Dal], [(TenderType.Cash, 105m)], at: new DateTimeOffset(2026, 9, 30, 23, 59, 0, TimeSpan.FromHours(5.5)));

        Assert.Equal([mine.InvoiceNo], Book().Vouchers.Select(v => v.Number));
    }

    [Fact]
    public void ABillWhosePaymentsDoNotMatchItsLinesIsPutRightOnRoundOffAndNamed()
    {
        var sale = Sell([Dal], [(TenderType.Cash, 105m)]);

        // Not something the till writes; something an old or mended book might hold.
        using (var connection = _temp.Database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE payments SET amount = '105.02';";
            command.ExecuteNonQuery();
        }

        var book = Book();
        var voucher = Assert.Single(book.Vouchers);

        Balanced(voucher);
        Assert.Contains(("Round off", LedgerGroup.IndirectExpenses, 0m, 0.02m), Entries(voucher));
        Assert.Equal([$"Bill {sale.InvoiceNo} did not balance by Rs 0.02; the difference is on Round off."], book.Notes);
    }

    // ---- Returns and the khata ------------------------------------------------------------------

    [Fact]
    public void GoodsBackAreTheBillTurnedRoundAgainstHowTheMoneyWentBack()
    {
        var sale = Sell([Dal, Oil], [(TenderType.Cash, 223m)]);
        var notes = new CreditNoteRepository(_temp.Database);
        var draft = CreditNoteDraft.Build(notes.Returnable(sale.InvoiceNo)!, [(2, 1m, true)], TenderType.Cash, "leaking", roundToRupee: false);
        var note = notes.Issue(draft, Lane, In.AddDays(1), "Murugan");

        var voucher = Book().Vouchers.Single(v => v.Kind == VoucherKind.CreditNote);

        Assert.Equal(note.Number, voucher.Number);
        Assert.Equal($"Goods back against {sale.InvoiceNo}: leaking", voucher.Narration);
        Assert.Equal(
        [
            ("Sales @ 18%", LedgerGroup.SalesAccounts, 100.00m, 0m),
            ("Output CGST", LedgerGroup.DutiesAndTaxes, 9.00m, 0m),
            ("Output SGST", LedgerGroup.DutiesAndTaxes, 9.00m, 0m),
            ("Cash", LedgerGroup.CashInHand, 0m, 118.00m),
        ], Entries(voucher));
    }

    [Fact]
    public void GoodsBackOffTheKhataComeOffTheCustomersLedger()
    {
        var sale = Sell([Dal], [(TenderType.StoreCredit, 105m)], Lakshmi());
        var notes = new CreditNoteRepository(_temp.Database);
        notes.Issue(CreditNoteDraft.Build(notes.Returnable(sale.InvoiceNo)!, [(1, 1m, true)], TenderType.StoreCredit, "wrong one", roundToRupee: false), Lane, In, "Murugan");

        var voucher = Book().Vouchers.Single(v => v.Kind == VoucherKind.CreditNote);

        Assert.Equal(("Lakshmi (9500012345)", LedgerGroup.SundryDebtors, 0m, 105.00m), Entries(voucher)[^1]);
    }

    [Fact]
    public void MoneyPaidBackOnTheKhataIsAReceiptFromTheCustomer()
    {
        var lakshmi = Lakshmi();
        Sell([Dal], [(TenderType.StoreCredit, 105m)], lakshmi);
        var paid = new CreditRepository(_temp.Database).Collect(lakshmi.Id, 50m, TenderType.Upi, Lane, In.AddDays(2), "Murugan");

        var voucher = Book().Vouchers.Single(v => v.Kind == VoucherKind.Receipt);

        Assert.Equal($"KR-{paid.Id}", voucher.Number);
        Assert.Equal(new DateOnly(2026, 9, 14), voucher.Date);
        Assert.Equal(
        [
            ("UPI collections", LedgerGroup.BankAccounts, 50.00m, 0m),
            ("Lakshmi (9500012345)", LedgerGroup.SundryDebtors, 0m, 50.00m),
        ], Entries(voucher));
    }

    // ---- The supplier's side --------------------------------------------------------------------

    private Supplier Wholesaler() =>
        Purchases.AddSupplier(new Supplier(0, "Murugan Traders", "9443012345", TamilNaduGstin, "33", true));

    private static PurchaseLine Line(Item item, decimal quantity, decimal rate, decimal gst) =>
        PurchaseLine.Price(new PurchaseLineEntry(item, quantity, rate, gst, 0m), interState: false, chargesGst: true);

    private long Delivery(Supplier supplier, decimal roundOff = 0m, params PurchaseLine[] lines) =>
        Purchases.Record(new PurchaseBill(supplier, "B-1", new DateOnly(2026, 9, 10), lines, InterState: false, RoundOff: roundOff), Lane, In, "Murugan").PurchaseId;

    [Fact]
    public void ASuppliersBillIsEachRatesPurchasesAndTheInputTaxAgainstWhatIsOwed()
    {
        var supplier = Wholesaler();
        Delivery(supplier, 0m, Line(Dal, 10m, 100m, 5m), Line(Oil, 4m, 50m, 18m));

        var voucher = Assert.Single(Book().Vouchers);

        Assert.Equal(VoucherKind.Purchase, voucher.Kind);
        Assert.Equal("B-1", voucher.Number);
        Assert.Equal(new DateOnly(2026, 9, 10), voucher.Date);
        Assert.Equal("Murugan Traders", voucher.Party);
        Assert.Equal(
        [
            ("Purchases @ 5%", LedgerGroup.PurchaseAccounts, 1000.00m, 0m),
            ("Input CGST", LedgerGroup.DutiesAndTaxes, 43.00m, 0m),
            ("Input SGST", LedgerGroup.DutiesAndTaxes, 43.00m, 0m),
            ("Purchases @ 18%", LedgerGroup.PurchaseAccounts, 200.00m, 0m),
            ("Murugan Traders", LedgerGroup.SundryCreditors, 0m, 1286.00m),
        ], Entries(voucher));
    }

    [Fact]
    public void TheSuppliersRoundOffIsADebitWhenItCostsTheShopMore()
    {
        // Three at 33.33 and 5%: 99.99 and 5.00 is 104.99, printed as 105.00.
        Delivery(Wholesaler(), 0.01m, Line(Oil, 3m, 33.33m, 5m));

        var voucher = Assert.Single(Book().Vouchers);

        Assert.Contains(("Round off", LedgerGroup.IndirectExpenses, 0.01m, 0m), Entries(voucher));
        Assert.Contains(("Murugan Traders", LedgerGroup.SundryCreditors, 0m, 105.00m), Entries(voucher));
        Balanced(voucher);
    }

    [Theory]
    [InlineData(SupplierPaymentMethod.DrawerCash, "Cash")]
    [InlineData(SupplierPaymentMethod.OtherCash, "Cash")]
    [InlineData(SupplierPaymentMethod.Upi, "Bank")]
    [InlineData(SupplierPaymentMethod.Cheque, "Bank")]
    public void PayingASupplierIsAPaymentFromCashOrTheBank(SupplierPaymentMethod method, string from)
    {
        var supplier = Wholesaler();
        Delivery(supplier, 0m, Line(Dal, 10m, 100m, 5m));
        var paid = Purchases.Pay(supplier.Id, 500m, method, "UTR 4411", Lane, In, "Murugan");

        var voucher = Book().Vouchers.Single(v => v.Kind == VoucherKind.Payment);

        Assert.Equal(VoucherKind.Payment, voucher.Kind);
        Assert.Equal($"SP-{paid.Id}", voucher.Number);
        Assert.Equal("Paid to Murugan Traders, UTR 4411", voucher.Narration);
        Assert.Equal(("Murugan Traders", LedgerGroup.SundryCreditors, 500.00m, 0m), Entries(voucher)[0]);
        Assert.Equal(from, Entries(voucher)[1].Ledger);
        Assert.Equal(500.00m, Entries(voucher)[1].Credit);
    }

    [Fact]
    public void GoodsSentBackAreADebitNoteOffWhatIsOwedAndTheInputTax()
    {
        var supplier = Wholesaler();
        var id = Delivery(supplier, 0m, Line(Dal, 10m, 100m, 5m));
        var note = Purchases.SendBack(id, [new SupplierReturnPick(1, 2m)], "expired", Lane, In.AddDays(3));

        var voucher = Book().Vouchers.Single(v => v.Kind == VoucherKind.DebitNote);

        Assert.Equal(note.Number, voucher.Number);
        Assert.Equal(
        [
            ("Murugan Traders", LedgerGroup.SundryCreditors, 210.00m, 0m),
            ("Purchases @ 5%", LedgerGroup.PurchaseAccounts, 0m, 200.00m),
            ("Input CGST", LedgerGroup.DutiesAndTaxes, 0m, 5.00m),
            ("Input SGST", LedgerGroup.DutiesAndTaxes, 0m, 5.00m),
        ], Entries(voucher));
    }

    // ---- Spending and the drawer ------------------------------------------------------------------

    private void Drawer(DrawerEntry entry, DateTimeOffset? at = null) =>
        new CashDrawerRepository(_temp.Database).Record(entry, Lane, at ?? In, "Murugan");

    [Fact]
    public void AnExpenseIsAPaymentFromTheDrawerOrTheBankUnderItsOwnName()
    {
        Drawer(new DrawerEntry(DrawerEntryKind.ExpenseFromDrawer, 40m, "Tea and snacks", "staff tea"));
        Drawer(new DrawerEntry(DrawerEntryKind.ExpenseFromOutside, 1_200m, "Electricity"));

        var vouchers = Book().Vouchers;

        Assert.All(vouchers, v => Assert.Equal(VoucherKind.Payment, v.Kind));
        Assert.Equal(
        [
            ("Tea and snacks", LedgerGroup.IndirectExpenses, 40.00m, 0m),
            ("Cash", LedgerGroup.CashInHand, 0m, 40.00m),
        ], Entries(vouchers.Single(v => v.Narration == "Tea and snacks: staff tea")));
        Assert.Equal(
        [
            ("Electricity", LedgerGroup.IndirectExpenses, 1_200.00m, 0m),
            ("Bank", LedgerGroup.BankAccounts, 0m, 1_200.00m),
        ], Entries(vouchers.Single(v => v.Narration == "Electricity")));
    }

    [Fact]
    public void CashTakenOutOrPutInIsAContraForTheAccountantToPlaceAndTheFloatIsNotInIt()
    {
        Drawer(new DrawerEntry(DrawerEntryKind.OpeningFloat, 1_000m));
        Drawer(new DrawerEntry(DrawerEntryKind.CashOut, 2_000m, Note: "to the bank"));
        Drawer(new DrawerEntry(DrawerEntryKind.CashIn, 500m, Note: "change from the bank"));

        var vouchers = Book().Vouchers;

        Assert.Equal(2, vouchers.Count);
        Assert.Equal(
        [
            ("Cash taken out of the till", LedgerGroup.Suspense, 2_000.00m, 0m),
            ("Cash", LedgerGroup.CashInHand, 0m, 2_000.00m),
        ], Entries(vouchers.Single(v => v.Narration == "Cash taken out of the till: to the bank")));
        Assert.Equal(
        [
            ("Cash", LedgerGroup.CashInHand, 500.00m, 0m),
            ("Cash put into the till", LedgerGroup.Suspense, 0m, 500.00m),
        ], Entries(vouchers.Single(v => v.Narration == "Cash put into the till: change from the bank")));
    }

    [Fact]
    public void ARefundOrASupplierPaidFromTheTillIsNotCountedTwice()
    {
        // Each moves the drawer, and each is already a voucher of its own.
        var sale = Sell([Dal], [(TenderType.Cash, 105m)]);
        var notes = new CreditNoteRepository(_temp.Database);
        notes.Issue(CreditNoteDraft.Build(notes.Returnable(sale.InvoiceNo)!, [(1, 1m, true)], TenderType.Cash, "damaged", roundToRupee: false), Lane, In, "Murugan");
        var supplier = Wholesaler();
        Delivery(supplier, 0m, Line(Dal, 10m, 100m, 5m));
        Purchases.Pay(supplier.Id, 300m, SupplierPaymentMethod.DrawerCash, null, Lane, In, "Murugan");

        var book = Book();

        Assert.Equal([VoucherKind.Sales, VoucherKind.CreditNote, VoucherKind.Purchase, VoucherKind.Payment], book.Vouchers.Select(v => v.Kind).Order());
    }

    // ---- The month ------------------------------------------------------------------------------

    [Fact]
    public void AMonthOfEverythingBalancesVoucherByVoucherInDateOrder()
    {
        var lakshmi = Lakshmi();
        var supplier = Wholesaler();

        Sell([Dal, Oil], [(TenderType.Cash, 500m)], at: In.AddDays(-5));
        // ₹223 on the khata, ₹18 of it paid back, and the oil returned off what is left.
        var credit = Sell([Dal, Oil], [(TenderType.StoreCredit, 223m)], lakshmi, at: In.AddDays(-4));
        new CreditRepository(_temp.Database).Collect(lakshmi.Id, 18m, TenderType.Cash, Lane, In.AddDays(-3), "Murugan");
        var notes = new CreditNoteRepository(_temp.Database);
        notes.Issue(CreditNoteDraft.Build(notes.Returnable(credit.InvoiceNo)!, [(2, 1m, true)], TenderType.StoreCredit, "spilt", roundToRupee: false), Lane, In.AddDays(-2), "Murugan");
        var id = Delivery(supplier, 0.01m, Line(Dal, 10m, 100m, 5m), Line(Oil, 3m, 33.33m, 5m));
        Purchases.Pay(supplier.Id, 500m, SupplierPaymentMethod.Bank, null, Lane, In.AddDays(1), "Murugan");
        Purchases.SendBack(id, [new SupplierReturnPick(2, 1m)], "leaking", Lane, In.AddDays(2));
        Drawer(new DrawerEntry(DrawerEntryKind.ExpenseFromDrawer, 40m, "Tea and snacks"), In.AddDays(3));
        Drawer(new DrawerEntry(DrawerEntryKind.CashOut, 200m, Note: "home"), In.AddDays(4));

        var book = Book();

        Assert.Equal(9, book.Vouchers.Count);
        Assert.All(book.Vouchers, Balanced);
        Assert.Empty(book.Notes);
        Assert.Equal(book.Vouchers.OrderBy(v => v.Date).Select(v => v.Date), book.Vouchers.Select(v => v.Date));
        Assert.Equal(1, book.Count(VoucherKind.Receipt));
        Assert.Equal(2, book.Count(VoucherKind.Payment));
    }

    [Fact]
    public void TheAccountantsOwnLedgerNamesAreUsed()
    {
        Sell([Oil], [(TenderType.Cash, 118m)]);

        var voucher = Assert.Single(Book(new DayBookLedgers { Cash = "Cash in Shop", Sales = "Retail Sales", OutputCgst = "CGST Payable", OutputSgst = "SGST Payable" }).Vouchers);

        Assert.Equal(["Cash in Shop", "Retail Sales @ 18%", "CGST Payable", "SGST Payable"], voucher.Entries.Select(e => e.Ledger));
    }

    [Fact]
    public void ANothingMonthHasNothing()
    {
        var book = Book();

        Assert.False(book.HasAnything);
        Assert.Empty(book.Notes);
    }

    [Fact]
    public void AnEmptyOrBackwardWindowIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DayBookQuery(_temp.Database).Gather(Lane, September, September, new DayBookLedgers()));

    // ---- The ledger names -----------------------------------------------------------------------

    [Fact]
    public void TheDefaultNamesWillDo() => Assert.Null(new DayBookLedgers().Problem());

    [Fact]
    public void ANameLeftEmptyIsRefused() =>
        Assert.Equal("cash has no ledger name.", new DayBookLedgers { Cash = " " }.Problem());

    [Fact]
    public void TwoTheSameAreRefused() =>
        Assert.Equal("card and upi have the same ledger name, 'Bank collections'.", new DayBookLedgers { Card = "Bank collections", Upi = "bank collections" }.Problem());

    [Fact]
    public void PaidOutsideMayBeTheBank() =>
        Assert.Null(new DayBookLedgers { ExpensesPaidOutside = "Bank" }.Problem());

    private static string SettingsFile(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), "pos-tests", Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void TheNamesAreReadFromTheSettingsFileAndTheRestKeepTheirs()
    {
        var path = SettingsFile("""{ "dayBook": { "cash": "Cash in Shop", "sales": "Retail Sales" } }""");

        var ledgers = Pos.Core.Configuration.PosSettings.LoadOrDefault(path).DayBook;

        Assert.Equal("Cash in Shop", ledgers.Cash);
        Assert.Equal("Retail Sales @ 5%", ledgers.SalesAt(5m, interState: false));
        Assert.Equal("UPI collections", ledgers.Upi);
        File.Delete(path);
    }

    [Fact]
    public void ASettingsFileWithoutTheSectionHasTheDefaults()
    {
        var path = SettingsFile("""{ "laneId": "L1" }""");

        Assert.Null(Pos.Core.Configuration.PosSettings.LoadOrDefault(path).DayBook.Problem());
        Assert.Equal("Cash", Pos.Core.Configuration.PosSettings.LoadOrDefault(path).DayBook.Cash);
        File.Delete(path);
    }

    [Fact]
    public void AnUnusableSectionStopsTheLaneSayingWhy()
    {
        var path = SettingsFile("""{ "dayBook": { "card": "Bank collections", "upi": "Bank collections" } }""");

        var refused = Assert.Throws<InvalidOperationException>(() => Pos.Core.Configuration.PosSettings.LoadOrDefault(path));

        Assert.Contains("dayBook section", refused.Message);
        Assert.Contains("card and upi have the same ledger name", refused.Message);
        File.Delete(path);
    }

    // ---- The files ------------------------------------------------------------------------------

    private DayBookData OneBill()
    {
        Sell([Dal, Oil], [(TenderType.Cash, 500m)]);
        return Book();
    }

    [Fact]
    public void TheCsvIsOneRowPerEntryWithTheVoucherOnEachRow()
    {
        var book = OneBill();
        var no = book.Vouchers[0].Number;

        Assert.Equal(
            "Date,Voucher type,Voucher no,Party,Ledger,Group,Debit,Credit,Narration\n" +
            $"12-09-2026,Sales,{no},,Cash,Cash-in-Hand,223.00,,Bill {no}\n" +
            $"12-09-2026,Sales,{no},,Sales @ 5%,Sales Accounts,,100.00,Bill {no}\n" +
            $"12-09-2026,Sales,{no},,Output CGST,Duties & Taxes,,11.50,Bill {no}\n" +
            $"12-09-2026,Sales,{no},,Output SGST,Duties & Taxes,,11.50,Bill {no}\n" +
            $"12-09-2026,Sales,{no},,Sales @ 18%,Sales Accounts,,100.00,Bill {no}\n",
            DayBookFiles.Csv(book).Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void TallyVouchersCarryEachEntryWithDebitsNegativeAndAddUpToNothing()
    {
        var book = OneBill();

        var xml = XDocument.Parse(DayBookFiles.TallyVouchers(book));

        Assert.Equal("Import Data", xml.Root!.Element("HEADER")!.Element("TALLYREQUEST")!.Value);
        Assert.Equal("Vouchers", xml.Descendants("REPORTNAME").Single().Value);

        var voucher = xml.Descendants("VOUCHER").Single();
        Assert.Equal("Sales", voucher.Attribute("VCHTYPE")!.Value);
        Assert.Equal("20260912", voucher.Element("DATE")!.Value);
        Assert.Equal(book.Vouchers[0].Number, voucher.Element("VOUCHERNUMBER")!.Value);

        var entries = voucher.Elements("ALLLEDGERENTRIES.LIST").ToList();
        Assert.Equal(5, entries.Count);
        Assert.Equal("Cash", entries[0].Element("LEDGERNAME")!.Value);
        Assert.Equal("Yes", entries[0].Element("ISDEEMEDPOSITIVE")!.Value);
        Assert.Equal("-223.00", entries[0].Element("AMOUNT")!.Value);
        Assert.Equal("No", entries[1].Element("ISDEEMEDPOSITIVE")!.Value);
        Assert.Equal("100.00", entries[1].Element("AMOUNT")!.Value);
        Assert.Equal(0m, entries.Sum(e => decimal.Parse(e.Element("AMOUNT")!.Value, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void TallyLedgersAreEachNamedOnceUnderItsGroup()
    {
        Sell([Dal], [(TenderType.StoreCredit, 105m)], Lakshmi());

        // A supplier with an ampersand in the name, who charges no GST, paid from the bank.
        var raja = Purchases.AddSupplier(new Supplier(0, "Raja & Sons", "9443000000", null, "33", false));
        var line = PurchaseLine.Price(new PurchaseLineEntry(Dal, 2m, 90m, 0m, 0m), interState: false, chargesGst: false);
        Purchases.Record(new PurchaseBill(raja, "R-7", new DateOnly(2026, 9, 11), [line], InterState: false), Lane, In, "Murugan");
        Purchases.Pay(raja.Id, 100m, SupplierPaymentMethod.Bank, null, Lane, In, "Murugan");

        var xml = XDocument.Parse(DayBookFiles.TallyLedgers(Book()));

        Assert.Equal("All Masters", xml.Descendants("REPORTNAME").Single().Value);

        var ledgers = xml.Descendants("LEDGER").ToDictionary(l => l.Attribute("NAME")!.Value, l => l.Element("PARENT")!.Value);

        Assert.Equal("Sundry Debtors", ledgers["Lakshmi (9500012345)"]);
        Assert.Equal("Sundry Creditors", ledgers["Raja & Sons"]);
        Assert.Equal("Purchase Accounts", ledgers["Purchases, no GST"]);
        Assert.Equal("Bank Accounts", ledgers["Bank"]);
        Assert.Equal("Sales Accounts", ledgers["Sales @ 5%"]);
        Assert.Equal("Duties & Taxes", ledgers["Output CGST"]);
        Assert.Equal(ledgers.Count, xml.Descendants("LEDGER").Count());
    }

    [Fact]
    public void TheThreeFilesAreWrittenBesideEachOtherReadableInExcel()
    {
        var book = OneBill();
        var folder = Path.Combine(Path.GetTempPath(), "pos-tests", Guid.NewGuid().ToString("N"));

        try
        {
            var files = DayBookFiles.Write(book, Path.Combine(folder, "daybook-L1-2026-09.csv"));

            Assert.Equal(
                ["daybook-L1-2026-09.csv", "daybook-L1-2026-09-tally-ledgers.xml", "daybook-L1-2026-09-tally-vouchers.xml"],
                files.Select(Path.GetFileName));

            foreach (var file in files)
                Assert.Equal(Encoding.UTF8.Preamble.ToArray(), File.ReadAllBytes(file)[..3]);

            Assert.Equal("daybook-L1-2026-09", DayBookFiles.Stem(book));
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }
}
