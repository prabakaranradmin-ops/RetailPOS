using Pos.Core.Analytics;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Pos.Core.Tests;

/// <summary>
/// What the dashboard says about a shop, and what it refuses to count.
/// </summary>
/// <remarks>
/// The figures here are asserted to the paise against a hand-built day of trading. That is the
/// point of the whole exercise: a page of charts is only worth looking at if the numbers under it
/// are the same numbers the Z-report and the invoices carry, and a chart that is a little bit wrong
/// is worse than no chart, because somebody will act on it.
/// </remarks>
public class DashboardTests(ITestOutputHelper output) : IDisposable
{
    private const string Lane = "L1";
    private const string OtherLane = "L2";
    private const string HomeState = "33";

    private readonly TempDatabase _temp = new();
    private readonly DateTimeOffset _today = new(2026, 8, 26, 0, 0, 0, TimeSpan.FromHours(5.5));

    public void Dispose() => _temp.Dispose();

    private InvoiceRepository Invoices => new(_temp.Database);

    private DashboardData Gather(int days = 30) =>
        new DashboardQuery(_temp.Database).Gather(Lane, _today.AddDays(-days), _today.AddDays(1));

    /// <summary>One sale, at a stated hour, with stated line prices.</summary>
    private SettledInvoice Sell(
        int hour,
        (string Name, decimal Price, decimal Gst, decimal Qty)[] items,
        TenderType tender = TenderType.Cash,
        Customer? customer = null,
        int daysAgo = 0,
        decimal discount = 0m,
        int pointsEarned = 0,
        int pointsRedeemed = 0,
        string lane = Lane,
        DateTimeOffset? at = null,
        decimal? handedOver = null)
    {
        // A fractional quantity means the thing is weighed — the domain refuses 2.75 of something
        // counted in pieces, and rightly so.
        var lines = items.Select((i, index) => InvoiceLine.Rehydrate(
            index + 1, i.Name, "0713", $"890{index:D10}", null,
            i.Qty == decimal.Truncate(i.Qty) ? UnitType.Each : UnitType.Kilogram,
            i.Price, i.Price, true, i.Gst, i.Qty, index == 0 ? discount : 0m, false)).ToArray();

        var totals = InvoiceTotals.From(lines);

        var paid = handedOver ?? totals.GrandTotal;

        var sale = new SaleDraft(
            lane,
            at ?? _today.AddDays(-daysAgo).AddHours(hour),
            customer,
            lines,
            totals,
            [new Tender(tender, paid)],
            ChangeDue: paid - totals.GrandTotal,
            PointsRedeemed: pointsRedeemed,
            PointsEarned: pointsEarned,
            RecalledFromToken: null);

        return Invoices.Save(sale);
    }

    // ---- The figures agree with the books ------------------------------------------------------

    [Fact]
    public void TheTakingsMatchTheInvoicesToThePaise()
    {
        Sell(9, [("Toor Dal", 189m, 5m, 1m)]);
        Sell(10, [("Sugar", 45m, 5m, 2.75m)]);
        Sell(18, [("Shampoo", 299m, 18m, 1m)]);

        var d = Gather();

        // 189.00 + 123.75 + 299.00
        Assert.Equal(3, d.Range.Bills);
        Assert.Equal(611.75m, d.Range.NetSales);
        Assert.Equal(0m, d.Range.Discount);
        Assert.Equal(611.75m, d.Range.GrossSales);
        Assert.Equal(decimal.Round(611.75m / 3, 2), d.Range.AverageBasket);
    }

    /// <summary>
    /// Amounts are stored as text and SQLite has no decimal type. Summing them through floating
    /// point drifts, and a GST figure that is a few paise out is one somebody files a return on.
    /// Two hundred awkward thirds is enough for a float to show it.
    /// </summary>
    [Fact]
    public void ManyAwkwardAmountsStillSumExactly()
    {
        for (var i = 0; i < 200; i++)
            Sell(10, [("Odd Item", 33.33m, 5m, 1m)]);

        var d = Gather();

        Assert.Equal(200, d.Range.Bills);
        Assert.Equal(6666.00m, d.Range.NetSales);
    }

    [Fact]
    public void TheGstBreakupMatchesWhatTheLinesWereCharged()
    {
        Sell(9, [("Toor Dal", 189m, 5m, 1m), ("Shampoo", 299m, 18m, 1m)]);

        var d = Gather();

        Assert.Equal(2, d.GstSlabs.Count);

        var five = d.GstSlabs.Single(s => s.Rate == 5m);
        var eighteen = d.GstSlabs.Single(s => s.Rate == 18m);

        // The tax on the page has to be the tax on the invoice, not a re-derivation of it.
        Assert.Equal(189m, five.TaxableValue + five.TotalTax);
        Assert.Equal(299m, eighteen.TaxableValue + eighteen.TotalTax);
        Assert.Equal(five.Cgst, five.Sgst);
        Assert.Equal(0m, five.Igst);

        Assert.Equal(d.Range.Tax, d.GstSlabs.Sum(s => s.TotalTax));
    }

    [Fact]
    public void DiscountsSeparateGrossFromNet()
    {
        Sell(9, [("Shampoo", 299m, 18m, 1m)], discount: 49m);

        var d = Gather();

        Assert.Equal(250m, d.Range.NetSales);
        Assert.Equal(49m, d.Range.Discount);
        Assert.Equal(299m, d.Range.GrossSales);
    }

    // ---- What must not be counted --------------------------------------------------------------

    /// <summary>
    /// A voided bill keeps its row and its number, which is what makes the invoice run auditable.
    /// It is not takings, and must not reach a single figure on the page except its own.
    /// </summary>
    [Fact]
    public void AVoidedBillIsCountedOnlyAsAVoid()
    {
        Sell(9, [("Toor Dal", 189m, 5m, 1m)]);
        var mistake = Sell(10, [("Basmati Rice", 649m, 5m, 1m)]);

        new CheckoutService(Invoices, new CustomerRepository(_temp.Database), new RecordingDrawerService(), null, TimeProvider.System)
            .VoidSale(mistake.InvoiceNo, "rung up twice");

        var d = Gather();
        output.WriteLine($"net {d.Range.NetSales}, voided {d.Voids.Count} worth {d.Voids.Value}");

        Assert.Equal(1, d.Range.Bills);
        Assert.Equal(189m, d.Range.NetSales);

        Assert.Equal(1, d.Voids.Count);
        Assert.Equal(649m, d.Voids.Value);

        // And nowhere else: not in the hourly chart, not in the items, not in the tax.
        Assert.Equal(189m, d.Hourly.Sum(h => h.NetSales));
        Assert.DoesNotContain(d.TopItems, i => i.Name == "Basmati Rice");
        Assert.Equal(189m, d.GstSlabs.Sum(s => s.TaxableValue + s.TotalTax));
    }

    [Fact]
    public void AnotherLanesTradingIsNotThisLanesTakings()
    {
        Sell(9, [("Toor Dal", 189m, 5m, 1m)]);
        Sell(9, [("Basmati Rice", 649m, 5m, 1m)], lane: OtherLane);

        var d = Gather();

        Assert.Equal(1, d.Range.Bills);
        Assert.Equal(189m, d.Range.NetSales);
    }

    [Fact]
    public void SalesOutsideTheWindowAreNotCounted()
    {
        Sell(9, [("Toor Dal", 189m, 5m, 1m)], daysAgo: 2);
        Sell(9, [("Basmati Rice", 649m, 5m, 1m)], daysAgo: 40);

        var d = Gather(days: 7);

        Assert.Equal(1, d.Range.Bills);
        Assert.Equal(189m, d.Range.NetSales);
    }

    [Fact]
    public void AShopThatHasNotTradedReportsZerosRatherThanFailing()
    {
        var d = Gather();

        Assert.Equal(0, d.Range.Bills);
        Assert.Equal(0m, d.Range.NetSales);
        Assert.Equal(0m, d.Range.AverageBasket);
        Assert.Empty(d.TopItems);
        Assert.Empty(d.GstSlabs);
        Assert.Empty(d.Tenders);

        // The charts still have their shape, so the page renders as an empty shop rather than a gap.
        Assert.Equal(24, d.Hourly.Count);
        Assert.Equal(7 * 12, d.WeekdayByHour.Count);
    }

    // ---- The shape of the day ------------------------------------------------------------------

    [Fact]
    public void TheHourlyChartPutsTakingsInTheHourTheyHappened()
    {
        Sell(9, [("Toor Dal", 189m, 5m, 1m)]);
        Sell(9, [("Toor Dal", 189m, 5m, 1m)]);
        Sell(19, [("Basmati Rice", 649m, 5m, 1m)]);

        var d = Gather();

        Assert.Equal(24, d.Hourly.Count);
        Assert.Equal(378m, d.Hourly.Single(h => h.Hour == 9).NetSales);
        Assert.Equal(2, d.Hourly.Single(h => h.Hour == 9).Bills);
        Assert.Equal(649m, d.Hourly.Single(h => h.Hour == 19).NetSales);
        Assert.Equal(0m, d.Hourly.Single(h => h.Hour == 3).NetSales);
    }

    [Fact]
    public void EveryDayInTheWindowIsOnTheTrendEvenTheClosedOnes()
    {
        Sell(9, [("Toor Dal", 189m, 5m, 1m)], daysAgo: 1);

        var d = Gather(days: 7);

        Assert.Equal(9, d.Daily.Count);
        Assert.Equal(189m, d.Daily.Single(p => p.Date == DateOnly.FromDateTime(_today.AddDays(-1).Date)).NetSales);
        Assert.Contains(d.Daily, p => p.Bills == 0);
    }

    [Fact]
    public void TheWeekdayGridPutsASaleInItsOwnTwoHourBand()
    {
        // 26 August 2026 is a Wednesday.
        Sell(15, [("Toor Dal", 189m, 5m, 1m)]);

        var d = Gather();
        var cell = d.WeekdayByHour.Single(c => c.Weekday == 3 && c.HourBand == 14);

        Assert.Equal(1, cell.Bills);
        Assert.Equal(189m, cell.NetSales);
    }

    [Fact]
    public void TopItemsAreRankedByWhatTheyBroughtInNotByHowManyMoved()
    {
        Sell(9, [("Chewing Gum", 5m, 18m, 40m)]);      // 200.00 over 40 units
        Sell(10, [("Basmati Rice", 649m, 5m, 1m)]);    // 649.00 over 1

        var d = Gather();

        Assert.Equal("Basmati Rice", d.TopItems[0].Name);
        Assert.Equal(649m, d.TopItems[0].NetSales);
        Assert.Equal("Chewing Gum", d.TopItems[1].Name);
        Assert.Equal(40m, d.TopItems[1].Quantity);
    }

    [Fact]
    public void TheTenderSplitSaysWhatIsInTheDrawerAndWhatIsInTheBank()
    {
        Sell(9, [("Toor Dal", 189m, 5m, 1m)], TenderType.Cash);
        Sell(10, [("Toor Dal", 189m, 5m, 1m)], TenderType.Upi);
        Sell(11, [("Toor Dal", 189m, 5m, 1m)], TenderType.Card);

        var d = Gather();

        Assert.Equal(189m, d.Range.Cash);
        Assert.Equal(378m, d.Range.Bank);
        Assert.Equal(189m, d.Range.CashInDrawer);
        Assert.Equal(567m, d.Tenders.Sum(t => t.Amount));
    }

    /// <summary>
    /// A sale on the first day of the window is in the window.
    /// </summary>
    /// <remarks>
    /// The window used to be bound as an "O"-format string - "2026-08-19T00:00:00.0000000+05:30" -
    /// against created_at, which the driver writes as "2026-08-19 10:00:00+05:30". A space sorts
    /// before a T, so every sale on the window's first day compared as earlier than its own midnight
    /// and was dropped: the first day of every 7, 30 and 90-day view was missing from every figure.
    /// </remarks>
    [Fact]
    public void ASaleOnTheFirstDayOfTheWindowIsCounted()
    {
        // Gather(7) runs from midnight seven days back; this sale is mid-morning that same day.
        Sell(10, [("Toor Dal", 189m, 5m, 1m)], daysAgo: 7);

        var d = Gather(7);

        Assert.Equal(1, d.Range.Bills);
        Assert.Equal(189m, d.Range.NetSales);
        Assert.Equal(189m, d.Range.Cash);
        Assert.Single(d.TopItems);
    }

    /// <summary>
    /// Today's cash in the drawer is what was taken today less today's change - not zero less it.
    /// </summary>
    /// <remarks>
    /// Today's figures used to be folded without a tender split, as not worth a second query. The
    /// change handed back was still counted, so the page's "cash in drawer" for today came out
    /// negative on any day the till had given change: take 200 for a 189 bill and it said -11.00.
    /// Timed at the real clock, because "today" is the machine's today.
    /// </remarks>
    [Fact]
    public void TodaysCashInTheDrawerIsTakenLessChangeNotANegativeNumber()
    {
        var now = DateTimeOffset.Now;

        Sell(0, [("Toor Dal", 189m, 5m, 1m)], TenderType.Cash, at: now.AddSeconds(-5), handedOver: 200m);
        Sell(0, [("Toor Dal", 189m, 5m, 1m)], TenderType.Upi, at: now.AddSeconds(-4));

        var d = new DashboardQuery(_temp.Database).Gather(Lane, now.AddDays(-1), now.AddMinutes(1));

        Assert.Equal(200m, d.Today.Cash);
        Assert.Equal(11m, d.Today.ChangeGiven);
        Assert.Equal(189m, d.Today.CashInDrawer);
        Assert.Equal(189m, d.Today.Bank);
    }

    /// <summary>
    /// Money owed and money given away are not money in the bank.
    /// </summary>
    /// <remarks>
    /// "Digital" used to be every tender that was not cash, and the owner's screen labelled it
    /// "what should reach the bank". That counted store credit — which the customer still owes —
    /// and loyalty points redeemed — which the shop gave away — as money on its way to the bank
    /// account. An owner reconciling a bank statement against it would come up short by exactly
    /// the credit and the points, and go looking for money that was never going to arrive.
    /// </remarks>
    [Fact]
    public void CreditAndPointsAreNotCountedAsMoneyInTheBank()
    {
        Sell(9, [("Toor Dal", 189m, 5m, 1m)], TenderType.Cash);
        Sell(10, [("Toor Dal", 189m, 5m, 1m)], TenderType.Upi);
        Sell(11, [("Toor Dal", 189m, 5m, 1m)], TenderType.Card);
        Sell(12, [("Toor Dal", 189m, 5m, 1m)], TenderType.StoreCredit);
        Sell(13, [("Toor Dal", 189m, 5m, 1m)], TenderType.LoyaltyPoints);

        var d = Gather();

        Assert.Equal(189m, d.Range.Cash);
        Assert.Equal(378m, d.Range.Bank);
        Assert.Equal(189m, d.Range.Credit);
        Assert.Equal(189m, d.Range.PointsRedeemed);

        // Every rupee of takings is in exactly one of the four places, and none is counted twice.
        // Cash kept, not cash taken: change handed back was never takings.
        Assert.Equal(d.Range.NetSales, d.Range.CashInDrawer + d.Range.Bank + d.Range.Credit + d.Range.PointsRedeemed);
    }

    // ---- Customers -----------------------------------------------------------------------------

    [Fact]
    public void KnownCustomersAreToldApartFromWalkIns()
    {
        var customers = new CustomerRepository(_temp.Database);
        var anitha = customers.Add(new Customer { MobileNo = "9876543210", Name = "Anitha", StateCode = HomeState });
        var kumar = customers.Add(new Customer { MobileNo = "9876543211", Name = "Kumar", StateCode = HomeState });

        Sell(9, [("Toor Dal", 189m, 5m, 1m)], customer: anitha, pointsEarned: 3);
        Sell(10, [("Toor Dal", 189m, 5m, 1m)], customer: anitha, pointsEarned: 3, daysAgo: 1);
        Sell(11, [("Toor Dal", 189m, 5m, 1m)], customer: kumar, pointsEarned: 3);
        Sell(12, [("Toor Dal", 189m, 5m, 1m)]);

        var d = Gather();

        Assert.Equal(3, d.Customers.IdentifiedBills);
        Assert.Equal(567m, d.Customers.IdentifiedSales);
        Assert.Equal(1, d.Customers.WalkInBills);
        Assert.Equal(189m, d.Customers.WalkInSales);

        Assert.Equal(2, d.Customers.DistinctCustomers);
        Assert.Equal(1, d.Customers.ReturningCustomers);
    }

    [Fact]
    public void PointsEarnedAndRedeemedAreTrackedWithWhatIsStillOwed()
    {
        var customers = new CustomerRepository(_temp.Database);
        var anitha = customers.Add(new Customer { MobileNo = "9876543210", Name = "Anitha", StateCode = HomeState, LoyaltyBalance = 412 });

        Sell(9, [("Toor Dal", 189m, 5m, 1m)], customer: anitha, pointsEarned: 3, pointsRedeemed: 100);

        var d = Gather();

        Assert.Equal(3, d.Points.Earned);
        Assert.Equal(100, d.Points.Redeemed);
        Assert.Equal(412, d.Points.OutstandingBalance);
        Assert.Single(d.Points.Daily);
    }

    // ---- The page ------------------------------------------------------------------------------

    [Fact]
    public void ThePageRendersAsOneSelfContainedFile()
    {
        Sell(9, [("Toor Dal", 189m, 5m, 1m)], TenderType.Upi);

        var html = DashboardPage.Render(Gather(), "ரவி மளிகை");

        Assert.StartsWith("<!doctype html>", html);
        Assert.EndsWith("</html>", html);
        Assert.Contains("ரவி மளிகை", html);
        Assert.Contains("Toor Dal", html);

        // Nothing fetched from anywhere: a shop's figures must not need the internet to be read,
        // and the lane has none.
        Assert.DoesNotContain("http://", html);
        Assert.DoesNotContain("https://", html);
        Assert.DoesNotContain("<script", html);
    }

    [Fact]
    public void AShopNameWithMarkupInItCannotBreakThePage()
    {
        var html = DashboardPage.Render(Gather(), "<script>alert('x')</script>");

        Assert.DoesNotContain("<script>alert", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void AWindowThatEndsBeforeItStartsIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DashboardQuery(_temp.Database).Gather(Lane, _today, _today.AddDays(-1)));
    }
}
