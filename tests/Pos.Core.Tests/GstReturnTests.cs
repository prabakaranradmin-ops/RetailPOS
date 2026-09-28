using System.Text;
using Pos.Core.Analytics;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Pos.Core.Tests;

/// <summary>
/// The month's figures for the GST return, asserted to the paisa against bills built by hand.
/// </summary>
/// <remarks>
/// Prices are chosen so every figure can be worked on paper: 210 at 5% is 200 taxable and 10 tax,
/// 118 at 18% is 100 and 18, 140 at 40% is 100 and 40. A return is the one document a shop signs
/// its name to, so nothing here is "approximately".
/// </remarks>
public class GstReturnTests(ITestOutputHelper output) : IDisposable
{
    private const string Lane = "L1";
    private const string Home = "33";

    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);
    private static readonly DateOnly September = new(2026, 9, 1);

    private readonly TempDatabase _temp = new();

    public void Dispose() => _temp.Dispose();

    private InvoiceRepository Invoices => new(_temp.Database);

    private GstReturnData Gather(DateOnly? month = null) =>
        new GstReturnQuery(_temp.Database).Gather(Lane, month ?? September, Home);

    private static InvoiceLine Line(
        string name,
        string hsn,
        decimal price,
        decimal gst,
        decimal quantity = 1m,
        UnitType unit = UnitType.Each,
        bool interState = false) =>
        InvoiceLine.Rehydrate(1, name, hsn, null, null, unit, price, price, true, gst, quantity, 0m, interState);

    private static InvoiceLine ToorDal(decimal quantity = 1m, bool interState = false) => Line("Toor Dal 1kg", "0713", 210m, 5m, quantity, interState: interState);
    private static InvoiceLine Detergent() => Line("Detergent Powder 1kg", "3402", 118m, 18m);
    private static InvoiceLine Cola() => Line("Cola 600ml", "2202", 140m, 40m);
    private static InvoiceLine Sugar() => Line("Sugar Loose", "1701", 42m, 5m, 2.5m, UnitType.Kilogram);
    private static InvoiceLine Jasmine() => Line("Malligai Poo", "0603", 30m, 0m, 2.5m, UnitType.Muzham);
    private static InvoiceLine Banana() => Line("Poovan Banana", "0803", 60m, 0m, 1m, UnitType.Seepu);

    private SettledInvoice Sell(
        DateTimeOffset at,
        InvoiceLine[] lines,
        Customer? customer = null,
        string lane = Lane,
        TaxMode taxMode = TaxMode.Gst)
    {
        var totals = InvoiceTotals.From(lines);

        return Invoices.Save(new SaleDraft(
            lane,
            at,
            customer,
            lines,
            totals,
            [new Tender(TenderType.Cash, totals.AmountPayable)],
            0m,
            0,
            0,
            null,
            TaxMode: taxMode));
    }

    private static DateTimeOffset On(int month, int day, int hour = 11, int minute = 0) => new(2026, month, day, hour, minute, 0, Ist);

    /// <summary>Two ordinary September bills and one cancelled one, with sales either side of the month.</summary>
    private void TradeSeptember()
    {
        Sell(On(8, 31, 23, 59), [ToorDal()]);
        Sell(On(9, 5), [ToorDal(2m), Detergent(), Jasmine()]);
        Sell(On(9, 20), [Sugar(), Cola(), Banana(), ToorDal()]);

        var cancelled = Sell(On(9, 21), [Detergent()]);
        Invoices.Void(cancelled.InvoiceNo, On(9, 21, 12), "rung up twice");

        Sell(On(10, 1, 0, 0), [ToorDal()]);
    }

    // ---- Sales by rate ---------------------------------------------------------------------------

    [Fact]
    public void TaxableSalesAreTotalledByRateToThePaisa()
    {
        TradeSeptember();

        var d = Gather();
        output.WriteLine(GstReturnFiles.B2cs(d));

        Assert.Equal(
            [
                new GstRateRow(Home, 5m, 700.00m, 17.50m, 17.50m, 0m),
                new GstRateRow(Home, 18m, 100.00m, 9.00m, 9.00m, 0m),
                new GstRateRow(Home, 40m, 100.00m, 20.00m, 20.00m, 0m),
            ],
            d.RateWise);

        Assert.Equal(900.00m, d.TaxableValue);
        Assert.Equal(46.50m, d.Cgst);
        Assert.Equal(46.50m, d.Sgst);
        Assert.Equal(0m, d.Igst);
        Assert.Equal(2, d.TaxInvoices);
    }

    [Fact]
    public void WhatWasSoldAtZeroIsNilRatedNotInTheRates()
    {
        TradeSeptember();

        var d = Gather();

        Assert.Equal(135.00m, d.NilIntraState);
        Assert.Equal(0m, d.NilInterState);
        Assert.DoesNotContain(d.RateWise, r => r.Rate == 0m);
        Assert.Contains(d.Warnings, w => w.Contains("exempted column"));
    }

    /// <summary>
    /// The first minute of the month is in it and the first minute of the next is not. The owner's
    /// figures once dropped the first day of every window over how a date was compared.
    /// </summary>
    [Fact]
    public void TheMonthRunsFromItsFirstMinuteToItsLast()
    {
        Sell(On(8, 31, 23, 59), [ToorDal()]);
        Sell(On(9, 1, 0, 0), [ToorDal()]);
        Sell(On(9, 30, 23, 59), [ToorDal()]);
        Sell(On(10, 1, 0, 0), [ToorDal()]);

        var d = Gather();

        Assert.Equal(2, d.TaxInvoices);
        Assert.Equal(400.00m, d.TaxableValue);
    }

    [Fact]
    public void AnotherLanesBillsAreNotThisLanesReturn()
    {
        Sell(On(9, 5), [ToorDal()]);
        Sell(On(9, 5), [ToorDal(3m)], lane: "L2");

        var d = Gather();

        Assert.Equal(1, d.TaxInvoices);
        Assert.Equal(200.00m, d.TaxableValue);
    }

    // ---- Into another state ----------------------------------------------------------------------

    [Fact]
    public void AnInterStateSaleIsFiledUnderItsPlaceOfSupplyWithIgst()
    {
        var customer = new CustomerRepository(_temp.Database).Add(new Customer { MobileNo = "9800000001", Name = "Ravi", StateCode = "29" });

        Sell(On(9, 5), [ToorDal()]);
        Sell(On(9, 6), [ToorDal(interState: true)], customer);

        var d = Gather();

        Assert.Contains(new GstRateRow("29", 5m, 200.00m, 0m, 0m, 10.00m), d.RateWise);
        Assert.Contains(new GstRateRow(Home, 5m, 200.00m, 5.00m, 5.00m, 0m), d.RateWise);
        Assert.Equal(10.00m, d.Igst);
        Assert.Contains("29-Karnataka", GstReturnFiles.B2cs(d));
    }

    /// <summary>
    /// A bill of more than a lakh into another state is listed on its own, not folded into the
    /// totals. It is still in the HSN summary, which covers everything.
    /// </summary>
    [Fact]
    public void ALargeInterStateBillIsListedOnItsOwn()
    {
        var customer = new CustomerRepository(_temp.Database).Add(new Customer { MobileNo = "9800000002", StateCode = "32" });

        var big = Sell(On(9, 8), [ToorDal(500m, interState: true)], customer);

        var d = Gather();

        var row = Assert.Single(d.LargeInterState);
        Assert.Equal(big.InvoiceNo, row.InvoiceNo);
        Assert.Equal(105_000.00m, row.InvoiceValue);
        Assert.Equal("32", row.PlaceOfSupply);
        Assert.Equal(100_000.00m, row.TaxableValue);
        Assert.Equal(5_000.00m, row.Igst);

        Assert.Empty(d.RateWise);
        Assert.Equal(100_000.00m, d.TaxableValue);
        Assert.Equal(500m, Assert.Single(d.Hsn).Quantity);
        Assert.Contains("32-Kerala", GstReturnFiles.B2cl(d));
    }

    /// <summary>A lakh exactly is not more than a lakh.</summary>
    [Fact]
    public void ABillOfExactlyALakhStaysInTheTotals()
    {
        var customer = new CustomerRepository(_temp.Database).Add(new Customer { MobileNo = "9800000003", StateCode = "32" });

        Sell(On(9, 8), [Line("Bulk Rice", "1006", 200m, 5m, 500m, interState: true)], customer);

        var d = Gather();

        Assert.Empty(d.LargeInterState);
        Assert.Single(d.RateWise);
    }

    [Fact]
    public void AnInterStateSaleToAForgottenCustomerIsFlaggedNotLost()
    {
        var customers = new CustomerRepository(_temp.Database);
        var customer = customers.Add(new Customer { MobileNo = "9800000004", StateCode = "29" });

        Sell(On(9, 6), [ToorDal(interState: true)], customer);
        customers.Forget(customer.Id);

        var d = Gather();

        Assert.Contains(new GstRateRow("", 5m, 200.00m, 0m, 0m, 10.00m), d.RateWise);
        Assert.Contains(d.Warnings, w => w.Contains("state is no longer on record"));
    }

    // ---- The HSN summary -------------------------------------------------------------------------

    [Fact]
    public void TheHsnSummaryCoversEveryCodeInTheUnitItWasSoldIn()
    {
        TradeSeptember();

        var d = Gather();
        output.WriteLine(GstReturnFiles.HsnSummary(d));

        Assert.Equal(
            [
                new GstHsnRow("0603", "Malligai Poo", Uqc.Others, 0m, 2.5m, 75.00m, 75.00m, 0m, 0m, 0m),
                new GstHsnRow("0713", "Toor Dal 1kg", Uqc.Pieces, 5m, 3m, 630.00m, 600.00m, 0m, 15.00m, 15.00m),
                new GstHsnRow("0803", "Poovan Banana", Uqc.Bunches, 0m, 1m, 60.00m, 60.00m, 0m, 0m, 0m),
                new GstHsnRow("1701", "Sugar Loose", Uqc.Kilograms, 5m, 2.5m, 105.00m, 100.00m, 0m, 2.50m, 2.50m),
                new GstHsnRow("2202", "Cola 600ml", Uqc.Pieces, 40m, 1m, 140.00m, 100.00m, 0m, 20.00m, 20.00m),
                new GstHsnRow("3402", "Detergent Powder 1kg", Uqc.Pieces, 18m, 1m, 118.00m, 100.00m, 0m, 9.00m, 9.00m),
            ],
            d.Hsn);
    }

    /// <summary>
    /// The HSN summary and the rate-wise tables describe the same sales two ways, so they must add
    /// up to the same thing. If they did not, one of them would be a wrong return.
    /// </summary>
    [Fact]
    public void TheHsnSummaryAgreesWithTheRatesAndTheNilRated()
    {
        TradeSeptember();

        var d = Gather();

        Assert.Equal(d.TaxableValue + d.NilRated, d.Hsn.Sum(h => h.TaxableValue));
        Assert.Equal(d.Cgst, d.Hsn.Sum(h => h.Cgst));
        Assert.Equal(d.Sgst, d.Hsn.Sum(h => h.Sgst));
        Assert.Equal(d.TaxableValue + d.NilRated + d.Tax, d.Hsn.Sum(h => h.TotalValue));
    }

    [Fact]
    public void ACodeSoldUnderSeveralNamesIsDescribedByTheOneThatSoldMost()
    {
        Sell(On(9, 5), [Line("Toor Dal 500g", "0713", 105m, 5m), Line("Toor Dal 1kg", "0713", 210m, 5m, 3m), Line("Moong Dal 1kg", "0713", 210m, 5m)]);

        var row = Assert.Single(Gather().Hsn);

        Assert.Equal("Toor Dal 1kg and 2 more", row.Description);
        Assert.Equal(5m, row.Quantity);
    }

    /// <summary>
    /// Quantities are summed in exact thousandths. Three hundred lines of 0.333 kg summed as floating
    /// point come to 99.89999..., and the HSN summary files a quantity.
    /// </summary>
    [Fact]
    public void WeighedQuantitiesAddUpExactly()
    {
        var lines = Enumerable.Range(0, 300)
            .Select(_ => Line("Sugar Loose", "1701", 42m, 5m, 0.333m, UnitType.Kilogram))
            .ToArray();

        Sell(On(9, 5), lines);

        Assert.Equal(99.900m, Assert.Single(Gather().Hsn).Quantity);
    }

    [Fact]
    public void AShortHsnCodeIsFlagged()
    {
        Sell(On(9, 5), [Line("Loose Item", "07", 210m, 5m)]);

        Assert.Contains(Gather().Warnings, w => w.Contains("shorter than four digits") && w.Contains("07"));
    }

    // ---- Documents issued ------------------------------------------------------------------------

    [Fact]
    public void TheBillNumbersIssuedAreCountedWithTheCancelledOnes()
    {
        TradeSeptember();

        var d = Gather();
        var run = Assert.Single(d.Documents);

        // The August bill took number 1; September's three are 2 to 4, one of them cancelled.
        Assert.EndsWith("-2", run.From);
        Assert.EndsWith("-4", run.To);
        Assert.Equal(3, run.Total);
        Assert.Equal(1, run.Cancelled);
        Assert.DoesNotContain(d.Warnings, w => w.Contains("span"));
    }

    [Fact]
    public void AGapInTheBillNumbersIsFlagged()
    {
        Sell(On(9, 5), [ToorDal()]);
        var second = Sell(On(9, 6), [ToorDal()]);

        using (var connection = _temp.Database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE invoices SET invoice_no = $no WHERE id = $id;";
            command.Parameters.AddWithValue("$no", second.InvoiceNo[..second.InvoiceNo.LastIndexOf('-')] + "-9");
            command.Parameters.AddWithValue("$id", second.Id);
            command.ExecuteNonQuery();
        }

        Assert.Contains(Gather().Warnings, w => w.Contains("span 9 numbers but 2 bills"));
    }

    // ---- What is not in it -----------------------------------------------------------------------

    /// <summary>
    /// A bill of supply is not a GSTR-1 supply. A month that changed scheme part way through keeps
    /// them apart and says so, rather than filing a composition bill as a taxable sale.
    /// </summary>
    [Fact]
    public void BillsOfSupplyAreKeptOutAndCounted()
    {
        Sell(On(9, 5), [ToorDal()]);
        Sell(On(9, 25), [Line("Toor Dal 1kg", "0713", 210m, 0m)], taxMode: TaxMode.Composition);

        var d = Gather();

        Assert.Equal(1, d.TaxInvoices);
        Assert.Equal(1, d.BillsOfSupply);
        Assert.Equal(210.00m, d.BillsOfSupplyValue);
        Assert.Equal(200.00m, d.TaxableValue);
        Assert.Equal(0m, d.NilRated);
        Assert.Contains(d.Warnings, w => w.Contains("CMP-08"));
    }

    [Fact]
    public void AMonthWithNoSalesIsEmptyNotAnError()
    {
        var d = Gather();

        Assert.False(d.HasAnything);
        Assert.Empty(d.RateWise);
        Assert.Empty(d.Hsn);
        Assert.Empty(d.Documents);
        Assert.Equal(0m, d.TaxableValue);
    }

    // ---- The files -------------------------------------------------------------------------------

    [Fact]
    public void TheCsvFilesUseTheOfflineToolsHeadings()
    {
        TradeSeptember();

        var d = Gather();

        Assert.StartsWith("Type,Place Of Supply,Rate,Applicable % of Tax Rate,Taxable Value,Cess Amount,E-Commerce GSTIN", GstReturnFiles.B2cs(d));
        Assert.Contains("OE,33-Tamil Nadu,5,,700.00,,", GstReturnFiles.B2cs(d));
        Assert.Contains("OE,33-Tamil Nadu,40,,100.00,,", GstReturnFiles.B2cs(d));

        Assert.StartsWith("Description,Nil Rated Supplies,Exempted(other than nil rated/non GST supply),Non-GST supplies", GstReturnFiles.Exempt(d));
        Assert.Contains("Intra-State supplies to unregistered persons,135.00,0.00,0.00", GstReturnFiles.Exempt(d));

        Assert.StartsWith("HSN,Description,UQC,Total Quantity,Total Value,Rate,Taxable Value,Integrated Tax Amount,Central Tax Amount,State/UT Tax Amount,Cess Amount", GstReturnFiles.HsnSummary(d));
        Assert.Contains("0713,Toor Dal 1kg,PCS-PIECES,3,630.00,5,600.00,0.00,15.00,15.00,", GstReturnFiles.HsnSummary(d));
        Assert.Contains("0603,Malligai Poo,OTH-OTHERS,2.5,75.00,0,75.00,0.00,0.00,0.00,", GstReturnFiles.HsnSummary(d));

        Assert.StartsWith("Nature of Document,Sr. No. From,Sr. No. To,Total Number,Cancelled", GstReturnFiles.Documents(d));
        Assert.Contains("Invoices for outward supply,", GstReturnFiles.Documents(d));
    }

    /// <summary>A comma inside a name must not shift every column after it.</summary>
    [Fact]
    public void ANameWithACommaStaysOneField()
    {
        Sell(On(9, 5), [Line("Rice, Ponni", "1006", 210m, 5m)]);

        Assert.Contains("1006,\"Rice, Ponni\",PCS-PIECES,", GstReturnFiles.HsnSummary(Gather()));
    }

    [Fact]
    public void ThePageAndTheCsvsAreWrittenTogether()
    {
        TradeSeptember();

        var d = Gather();
        var folder = Path.Combine(Path.GetTempPath(), "gst-" + Guid.NewGuid().ToString("N"));

        try
        {
            var page = Path.Combine(folder, GstReturnFiles.Stem(d) + ".html");
            var files = GstReturnFiles.Write(d, page, "ரவி மளிகை", "33AABCS1429B1ZX");

            Assert.Equal(page, files[0]);
            Assert.Equal("gst-L1-2026-09", GstReturnFiles.Stem(d));
            Assert.All(files, f => Assert.True(File.Exists(f), f));
            Assert.Contains(files, f => f.EndsWith("-b2cs.csv", StringComparison.Ordinal));
            Assert.Contains(files, f => f.EndsWith("-hsn(b2c).csv", StringComparison.Ordinal));

            // No large inter-state bills, so no file for them.
            Assert.DoesNotContain(files, f => f.EndsWith("-b2cl.csv", StringComparison.Ordinal));

            // With a byte-order mark, so Excel reads a Tamil shop name as Tamil.
            Assert.All(files, f => Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(f).Take(3).ToArray()));

            var html = File.ReadAllText(page, Encoding.UTF8);
            Assert.Contains("ரவி மளிகை", html);
            Assert.Contains("GSTIN 33AABCS1429B1ZX", html);
            Assert.Contains("September 2026", html);
            Assert.Contains("Rs 900.00", html);
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    // ---- Reference data --------------------------------------------------------------------------

    [Theory]
    [InlineData("33", "33-Tamil Nadu")]
    [InlineData("29", "29-Karnataka")]
    [InlineData("3", "03-Punjab")]
    [InlineData("97", "97-Other Territory")]
    [InlineData("99", "99")]
    public void AStateIsWrittenAsCodeAndName(string code, string expected) =>
        Assert.Equal(expected, GstStates.Label(code));

    [Fact]
    public void EveryUnitIsReportedUnderARealQuantityCode()
    {
        string[] codes = [Uqc.Pieces, Uqc.Kilograms, Uqc.Litres, Uqc.Metres, Uqc.Numbers, Uqc.Bunches, Uqc.Bundles, Uqc.Bags, Uqc.Pairs, Uqc.Packs, Uqc.Others];

        Assert.All(Enum.GetValues<UnitType>(), unit => Assert.Contains(Uqc.For(unit), codes));
        Assert.Equal(Uqc.Bunches, Uqc.For(UnitType.Seepu));
        Assert.Equal(Uqc.Bundles, Uqc.For(UnitType.Kattu));
        Assert.Equal(Uqc.Others, Uqc.For(UnitType.Padi));
        Assert.Equal(Uqc.Pairs, Uqc.For(UnitType.Jodi));
    }
}
