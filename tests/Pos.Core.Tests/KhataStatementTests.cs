using Pos.Core.Analytics;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Domain.Printing;
using Pos.TestSupport;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// Khata statements: what a customer owed at the start, every bill, payment and return with the
/// balance after it, what they owe at the end and how old it is - always closing on the balance the
/// till shows, because it is read from the same books.
/// </summary>
public class KhataStatementTests : IDisposable
{
    private static readonly TimeSpan India = TimeSpan.FromHours(5.5);
    private static readonly DateOnly Today = new(2026, 9, 29);
    private static readonly Customer Lakshmi = new() { Id = 7, MobileNo = "9500012345", Name = "Lakshmi" };

    private readonly TempDatabase _temp = new();

    public void Dispose() => _temp.Dispose();

    private static DateTimeOffset On(DateOnly day, int hour = 10) => new(day.ToDateTime(new TimeOnly(hour, 0)), India);

    private static KhataEntry Bought(int daysAgo, decimal amount, string bill = "RM/26-27/1", int hour = 10) =>
        new(On(Today.AddDays(-daysAgo), hour), KhataEntryKind.Bought, bill, amount);

    private static KhataEntry Paid(int daysAgo, decimal amount, TenderType tender = TenderType.Cash, int hour = 10) =>
        new(On(Today.AddDays(-daysAgo), hour), KhataEntryKind.Paid, string.Empty, amount, tender);

    private static KhataEntry Returned(int daysAgo, decimal amount, string note = "CN/26-27/L1-1") =>
        new(On(Today.AddDays(-daysAgo)), KhataEntryKind.Returned, note, amount);

    // ---- The figures -----------------------------------------------------------------------------

    [Fact]
    public void AStatementOpensOnWhatWasOwedAndWalksTheBalanceForward()
    {
        KhataEntry[] ledger =
        [
            Bought(40, 500m, "RM/26-27/10"),
            Paid(35, 200m),
            Bought(20, 189m, "RM/26-27/20"),
            Returned(15, 45.50m),
            Paid(5, 100m, TenderType.Upi),
        ];

        var statement = KhataStatement.Build(Lakshmi, ledger, Today.AddDays(-30), Today);

        Assert.Equal(300m, statement.Opening);
        Assert.Equal([489m, 443.50m, 343.50m], statement.Lines.Select(l => l.BalanceAfter));
        Assert.Equal(343.50m, statement.Closing);
        Assert.Equal(189m, statement.Bought);
        Assert.Equal(1, statement.Bills);
        Assert.Equal(100m, statement.Paid);
        Assert.Equal(45.50m, statement.Returned);
        Assert.Equal(TenderType.Upi, statement.LastPayment!.Tender);
    }

    /// <summary>Bought and paid in the same instant: the purchase first, so the balance never dips below nothing.</summary>
    [Fact]
    public void APurchaseComesBeforeAPaymentMadeAtTheSameMoment()
    {
        var statement = KhataStatement.Build(Lakshmi, [Paid(0, 100m), Bought(0, 100m)], Today, Today);

        Assert.Equal([100m, 0m], statement.Lines.Select(l => l.BalanceAfter));
    }

    [Fact]
    public void AStatementCannotEndBeforeItStarts() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => KhataStatement.Build(Lakshmi, [], Today, Today.AddDays(-1)));

    // ---- How old it is -----------------------------------------------------------------------------

    /// <summary>
    /// 1,100 bought over three months, 600 paid: the payments cleared the oldest, so the 500 owed is
    /// the newest bills - 100 from 10 days ago, 200 from 40, and 200 of the 300 from 70.
    /// </summary>
    [Fact]
    public void WhatIsOwedIsTheNewestBillsPaymentsClearTheOldest()
    {
        KhataEntry[] ledger = [Bought(100, 500m), Bought(70, 300m), Bought(40, 200m), Bought(10, 100m), Paid(5, 600m)];

        var ageing = KhataStatement.Build(Lakshmi, ledger, Today.AddDays(-120), Today).Ageing;

        Assert.Equal(100m, ageing.UpTo30Days);
        Assert.Equal(200m, ageing.Days31To60);
        Assert.Equal(200m, ageing.Days61To90);
        Assert.Equal(0m, ageing.Over90Days);
        Assert.Equal(500m, ageing.Total);
        Assert.Equal(Today.AddDays(-70), ageing.OldestUnpaid);
        Assert.Equal(70, ageing.DaysWaiting(Today));
    }

    [Fact]
    public void NothingOwedIsNotAged()
    {
        var statement = KhataStatement.Build(Lakshmi, [Bought(40, 100m), Paid(5, 100m)], Today.AddDays(-60), Today);

        Assert.Equal(KhataAgeing.Nothing, statement.Ageing);
        Assert.False(statement.OwesAnything);
    }

    // ---- Since they last owed nothing ------------------------------------------------------------

    [Fact]
    public void TheCounterStatementStartsAfterTheKhataWasLastClear()
    {
        KhataEntry[] ledger = [Bought(30, 100m), Paid(25, 100m), Bought(10, 50m), Bought(3, 30m)];

        var statement = KhataStatement.SinceLastClear(Lakshmi, ledger, Today);

        Assert.Equal(Today.AddDays(-10), statement.From);
        Assert.Equal(0m, statement.Opening);
        Assert.Equal(2, statement.Lines.Count);
        Assert.Equal(80m, statement.Closing);
    }

    [Fact]
    public void AKhataNeverClearedStartsAtItsFirstBill()
    {
        var statement = KhataStatement.SinceLastClear(Lakshmi, [Bought(30, 100m), Paid(25, 40m)], Today);

        Assert.Equal(Today.AddDays(-30), statement.From);
        Assert.Equal(60m, statement.Closing);
    }

    /// <summary>Years of khata do not make a metre of paper: the oldest lines go into the opening balance.</summary>
    [Fact]
    public void ALongKhataIsCutToTheNewestLines()
    {
        var ledger = Enumerable.Range(1, 70).Select(i => Bought(daysAgo: 71 - i, amount: 10m, bill: $"B{i}")).ToList();

        var statement = KhataStatement.SinceLastClear(Lakshmi, ledger, Today, mostLines: 60);

        Assert.Equal(60, statement.Lines.Count);
        Assert.Equal(100m, statement.Opening);
        Assert.Equal(700m, statement.Closing);
        Assert.Equal("B11", statement.Lines[0].Entry.Reference);
    }

    /// <summary>Bought and paid off the same day: clear, so nothing - not today's lines adding up to nothing.</summary>
    [Fact]
    public void AKhataClearedTodayHasNoLinesEvenForTodaysBills()
    {
        var statement = KhataStatement.SinceLastClear(Lakshmi, [Bought(0, 100m, hour: 9), Paid(0, 100m, hour: 11)], Today);

        Assert.Empty(statement.Lines);
        Assert.Equal(0m, statement.Closing);
        Assert.NotNull(statement.LastPayment);
    }

    [Fact]
    public void AKhataThatIsClearHasNoLines()
    {
        var statement = KhataStatement.SinceLastClear(Lakshmi, [Bought(30, 100m), Paid(2, 100m)], Today);

        Assert.Empty(statement.Lines);
        Assert.Equal(0m, statement.Closing);
    }

    // ---- The message -----------------------------------------------------------------------------

    [Fact]
    public void TheMessageSaysWhatIsOwedAndHowToPay()
    {
        var statement = KhataStatement.Build(Lakshmi, [Bought(40, 1500m), Paid(20, 500m), Bought(5, 189m)], Today.AddDays(-30), Today);

        var message = statement.Message("Sri Murugan Stores", new UpiPayee("murugan.stores@okaxis", "Sri Murugan Stores"));

        Assert.Equal(
            "Sri Murugan Stores: khata for Lakshmi, 30-08-2026 to 29-09-2026.\n"
            + "Owed on 30-08-2026: Rs 1,500.00\n"
            + "Bought on khata: Rs 189.00 (1 bill)\n"
            + "Paid: Rs 500.00\n"
            + "Owed now: Rs 1,189.00\n"
            + "Oldest unpaid bill: 20-08-2026\n"
            + "Pay by UPI to murugan.stores@okaxis (Sri Murugan Stores).\n"
            + "Thank you.",
            message);
    }

    // ---- From the books ------------------------------------------------------------------------

    [Fact]
    public void TheLedgerIsTheBooksAndTheStatementClosesOnTheBalance()
    {
        var customer = new CustomerRepository(_temp.Database).Add(new Customer { MobileNo = "9500012345", Name = "Lakshmi" });
        _temp.Items.UpsertRange([Catalogue.Item(sku: "DAL001", name: "Toor Dal 1kg", price: 189m)]);
        var dal = _temp.Items.FindBySku("DAL001")!;
        var invoices = new InvoiceRepository(_temp.Database);
        var checkout = new CheckoutService(invoices, new CustomerRepository(_temp.Database), new RecordingDrawerService());

        SettledInvoice SellOnCredit()
        {
            var bill = new InvoiceEngine("33");
            bill.AddItem(dal);
            bill.SetCustomer(customer);
            var basket = new TenderBasket(bill.Totals.AmountPayable);
            basket.Add(TenderType.StoreCredit, bill.Totals.AmountPayable);
            return checkout.Complete("L1", bill, basket).Invoice;
        }

        var first = SellOnCredit();
        var voided = SellOnCredit();
        invoices.Void(voided.InvoiceNo, DateTimeOffset.Now, "rung up twice");
        var credit = new CreditRepository(_temp.Database);
        credit.Collect(customer.Id, 89m, TenderType.Upi, "L1", DateTimeOffset.Now, null);

        var ledger = credit.Ledger(customer.Id);

        Assert.Equal([KhataEntryKind.Bought, KhataEntryKind.Paid], ledger.Select(e => e.Kind));
        Assert.Equal(first.InvoiceNo, ledger[0].Reference);
        Assert.Equal(189m, ledger[0].Amount);
        Assert.Equal(TenderType.Upi, ledger[1].Tender);

        var statement = KhataStatement.SinceLastClear(customer, ledger, DateOnly.FromDateTime(DateTime.Today));
        Assert.Equal(credit.Balance(customer.Id), statement.Closing);
        Assert.Equal(100m, statement.Closing);
    }

    // ---- On paper ------------------------------------------------------------------------------

    private static readonly StoreProfile Store = new() { Name = "Sri Murugan Stores" };

    private static KhataStatement Typical() =>
        KhataStatement.Build(Lakshmi, [Bought(40, 1500m, "RM/26-27/10"), Paid(20, 500m, TenderType.Upi), Bought(5, 189m, "RM/26-27/20")], Today.AddDays(-30), Today);

    [Fact]
    public void ThePrintedStatementCarriesEveryLineAndWhatIsOwed()
    {
        var text = new ReceiptComposer(Store).ComposeKhataStatement(Typical()).ToPlainText();

        Assert.Contains("KHATA STATEMENT", text);
        Assert.Contains("30-08-2026 - 29-09-2026", text);
        Assert.Contains("Owed at the start", text);
        Assert.Contains("09-09 Paid, UPI", text);
        Assert.Contains("24-09 Bill RM/26-27/20", text);
        Assert.Contains("+189.00", text);
        Assert.Contains("-500.00", text);
        Assert.Contains("OWED NOW", text);
        Assert.Contains("1,189.00", text);
        Assert.Contains("Oldest unpaid bill", text);
        Assert.Contains("20-08-2026 (40 days)", text);
        Assert.Contains("Not a bill.", text);
        Assert.DoesNotContain("[QR]", text);
    }

    [Fact]
    public void WithTheShopsUpiIdTheStatementEndsWithACodeForWhatIsOwed()
    {
        var upi = new UpiPayee("murugan.stores@okaxis", "Sri Murugan Stores");

        var text = new ReceiptComposer(Store).ComposeKhataStatement(Typical(), upi).ToPlainText();
        var flat = text.Replace("\r", string.Empty).Replace("\n", string.Empty);

        Assert.Contains("SCAN TO PAY BY UPI", text);
        Assert.Contains("Rs 1,189.00", text);
        Assert.Contains("upi://pay?pa=murugan.stores@okaxis&pn=Sri%20Murugan%20Stores&tn=Khata%209500012345&am=1189.00&cu=INR", flat);
    }

    [Fact]
    public void AClearKhataSaysNothingIsOwedAndAsksForNothing()
    {
        var clear = KhataStatement.Build(Lakshmi, [Bought(40, 100m), Paid(5, 100m)], Today.AddDays(-60), Today);

        var text = new ReceiptComposer(Store).ComposeKhataStatement(clear, new UpiPayee("murugan.stores@okaxis", "Shop")).ToPlainText();

        Assert.Contains("Nothing is owed. Thank you.", text);
        Assert.DoesNotContain("[QR]", text);
    }

    [Fact]
    public void TheStatementIsInTamilOnATamilLane()
    {
        var text = new ReceiptComposer(Store, language: ReceiptLanguage.Tamil).ComposeKhataStatement(Typical()).ToPlainText();

        Assert.Contains("கடன் கணக்கு அறிக்கை", text);
        Assert.Contains("இப்போது நிலுவை", text);
    }

    // ---- As a page -------------------------------------------------------------------------------

    [Fact]
    public void TheStatementPageHasOnePagePerCustomerAndTheCode()
    {
        var ravi = new Customer { Id = 8, MobileNo = "9500054321", Name = "Ravi" };
        var second = KhataStatement.Build(ravi, [Bought(3, 45m)], Today.AddDays(-30), Today);
        var upi = new UpiPayee("murugan.stores@okaxis", "Sri Murugan Stores");

        var page = KhataStatementPage.Render([Typical(), second], Store, upi);

        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(page, "<section class=\"statement\">").Count);
        Assert.Contains("Lakshmi", page);
        Assert.Contains("Ravi", page);
        Assert.Contains("Rs 1,189.00", page);
        Assert.Contains("Paid, UPI", page);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(page, "aria-label=\"UPI code\"").Count);
    }

    [Fact]
    public void TheCodeOnThePageIsTheModulesOfTheRealCode()
    {
        const string link = "upi://pay?pa=a1@okaxis&am=10.00&cu=INR";
        var code = Pos.Core.Hardware.Printing.QrCode.Encode(link);

        var svg = KhataStatementPage.QrSvg(link, "30mm");

        Assert.Contains($"viewBox=\"0 0 {code.Size + 8} {code.Size + 8}\"", svg);

        // Every dark module is covered by exactly one run of the path.
        var dark = 0;

        for (var y = 0; y < code.Size; y++)
        {
            for (var x = 0; x < code.Size; x++)
            {
                if (code[x, y])
                    dark++;
            }
        }

        var covered = System.Text.RegularExpressions.Regex.Matches(svg, @"M\d+ \d+h(\d+)").Sum(m => int.Parse(m.Groups[1].Value));
        Assert.Equal(dark, covered);
    }
}
