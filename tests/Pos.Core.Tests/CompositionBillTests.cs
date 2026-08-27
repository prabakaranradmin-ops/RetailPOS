using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Domain.Printing;
using Pos.Core.Hardware.Drawer;
using Pos.Core.Hardware.Printing;
using Pos.TestSupport;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// A composition dealer's bill of supply.
/// </summary>
/// <remarks>
/// A dealer under the composition scheme is registered and has a GSTIN, but may not collect tax
/// from the customer. So the document is a bill of supply, not a tax invoice, and it carries a
/// declaration saying the dealer is not eligible to collect tax (CGST Rules, rule 5(1)(f)).
///
/// The failure to guard against is not a missing column. It is a bill that still says TAX INVOICE
/// while showing no tax — a document asserting the shop charged GST it is not allowed to charge.
/// </remarks>
public class CompositionBillTests : IDisposable
{
    private const string Lane = "L1";
    private const string HomeState = "33";

    private readonly TempDatabase _temp = new();
    private readonly RecordingDrawerService _drawer = new();

    private static readonly StoreProfile Store = new()
    {
        Name = "Ravi Maligai",
        Gstin = "33AEIPH7795F1Z9",
    };

    public void Dispose() => _temp.Dispose();

    /// <summary>Sells one taxed item under the given mode and returns the stored invoice.</summary>
    private SettledInvoice Sell(TaxMode mode, decimal price = 48m, decimal gstRate = 18m)
    {
        var bill = new InvoiceEngine(HomeState, mode);
        bill.AddItem(Catalogue.Item(id: 1, sku: "SOAP", barcode: "8901234567896",
            name: "Bath Soap 100g", price: price, gstRate: gstRate));

        var basket = new TenderBasket(bill.Totals.GrandTotal);
        basket.Add(TenderType.Cash, bill.Totals.GrandTotal);

        return new CheckoutService(
            new InvoiceRepository(_temp.Database),
            new CustomerRepository(_temp.Database),
            _drawer,
            null,
            TimeProvider.System).Complete(Lane, bill, basket).Invoice;
    }

    private static string Print(SettledInvoice invoice, ReceiptLanguage language = ReceiptLanguage.English, bool isReprint = false) =>
        new ReceiptComposer(Store, ReceiptBuilder.Width80Mm, language).Compose(invoice, isReprint).ToPlainText();

    // ---- The money ------------------------------------------------------------------------------

    /// <summary>
    /// The item is an 18% one. Under composition the shop still may not charge that, so the rate is
    /// dropped when the line is made rather than hidden when it is printed.
    /// </summary>
    [Fact]
    public void NoTaxIsChargedEvenOnAnItemThatCarriesARate()
    {
        var bill = new InvoiceEngine(HomeState, TaxMode.Composition);
        var line = bill.AddItem(Catalogue.Item(id: 1, price: 48m, gstRate: 18m));

        Assert.Equal(0m, line.GstRate);
        Assert.Equal(0m, line.CgstRate);
        Assert.Equal(0m, line.SgstRate);
        Assert.Equal(0m, line.IgstRate);
        Assert.Equal(0m, line.Tax.SplitTax);
    }

    /// <summary>What the customer pays is what is on the shelf, and all of it is the subtotal.</summary>
    [Fact]
    public void ThePriceOnTheShelfIsThePriceAndTheWholeOfIt()
    {
        var bill = new InvoiceEngine(HomeState, TaxMode.Composition);
        bill.AddItem(Catalogue.Item(id: 1, price: 48m, gstRate: 18m));

        var totals = bill.Totals;

        Assert.Equal(48m, totals.GrandTotal);
        Assert.Equal(48m, totals.SubtotalTaxable);
        Assert.Equal(0m, totals.TotalCgst);
        Assert.Equal(0m, totals.TotalSgst);
        Assert.Equal(0m, totals.TotalIgst);
    }

    /// <summary>
    /// The same item on a GST lane still splits out 18%. This is the regression guard: turning the
    /// composition variant on must change nothing for a shop that never asked for it.
    /// </summary>
    [Fact]
    public void AGstLaneIsCompletelyUnaffected()
    {
        var bill = new InvoiceEngine(HomeState);
        var line = bill.AddItem(Catalogue.Item(id: 1, price: 48m, gstRate: 18m));

        Assert.Equal(18m, line.GstRate);
        Assert.Equal(9m, line.CgstRate);
        Assert.Equal(9m, line.SgstRate);
        Assert.True(bill.Totals.TotalCgst > 0m);
        Assert.Equal(48m, bill.Totals.GrandTotal);
    }

    /// <summary>Composition dealers may not supply interstate, and nothing here can produce IGST.</summary>
    [Fact]
    public void AnOutOfStateCustomerStillProducesNoTax()
    {
        var bill = new InvoiceEngine(HomeState, TaxMode.Composition);
        bill.AddItem(Catalogue.Item(id: 1, price: 48m, gstRate: 18m));
        bill.SetCustomer(new Customer { Id = 1, MobileNo = "9840012345", StateCode = "29" });

        Assert.True(bill.IsInterState);
        Assert.Equal(0m, bill.Totals.TotalIgst);
        Assert.Equal(48m, bill.Totals.GrandTotal);
    }

    // ---- The document ---------------------------------------------------------------------------

    [Fact]
    public void TheBillIsCalledABillOfSupplyAndNotATaxInvoice()
    {
        var printed = Print(Sell(TaxMode.Composition));

        Assert.Contains("BILL OF SUPPLY", printed);
        Assert.DoesNotContain("TAX INVOICE", printed);
    }

    /// <summary>
    /// The declaration is 67 characters and the widest paper is 48, so it is wrapped. Comparing
    /// against the receipt with its line breaks and centring collapsed asserts the whole phrase
    /// survived, without pinning where it happens to break.
    /// </summary>
    [Theory]
    [InlineData(ReceiptBuilder.Width80Mm)]
    [InlineData(ReceiptBuilder.Width58Mm)]
    public void TheDeclarationTheRulesRequireIsOnItWholeOnEveryPaperWidth(int width)
    {
        var printed = new ReceiptComposer(Store, width)
            .Compose(Sell(TaxMode.Composition))
            .ToPlainText();

        var flattened = string.Join(' ', printed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        // The half that gets lost to truncation is the half that matters, so this asserts on the
        // whole phrase rather than on its opening words.
        Assert.Contains(CompositionDeclaration.Text, flattened);
    }

    /// <summary>No line of the bill may run off the paper, declaration included.</summary>
    [Theory]
    [InlineData(ReceiptBuilder.Width80Mm)]
    [InlineData(ReceiptBuilder.Width58Mm)]
    public void NothingOverrunsThePaper(int width)
    {
        var printed = new ReceiptComposer(Store, width)
            .Compose(Sell(TaxMode.Composition))
            .ToPlainText();

        foreach (var line in printed.Split('\n'))
            Assert.True(line.TrimEnd('\r').Length <= width, $"'{line.Trim()}' is wider than {width}.");
    }

    /// <summary>
    /// No rate-wise breakup. Every line is at zero, so an unguarded summary would print a tidy "0%"
    /// slab against the full value of the bill — which reads as a shop declaring it charged nothing
    /// on a taxable supply, rather than one not permitted to charge at all.
    /// </summary>
    [Fact]
    public void ThereIsNoTaxBreakupAndNoZeroPercentSlab()
    {
        var printed = Print(Sell(TaxMode.Composition));

        Assert.DoesNotContain("CGST", printed);
        Assert.DoesNotContain("SGST", printed);
        Assert.DoesNotContain("IGST", printed);
        Assert.DoesNotContain("0%", printed);
    }

    [Fact]
    public void ThereIsNoTaxableValueBecauseNothingWasTaxed()
    {
        var printed = Print(Sell(TaxMode.Composition));

        Assert.Contains("Subtotal", printed);
        Assert.DoesNotContain("Taxable", printed);
    }

    [Fact]
    public void TheShopsGstinIsStillPrintedBecauseTheDealerIsRegistered()
    {
        Assert.Contains("GSTIN 33AEIPH7795F1Z9", Print(Sell(TaxMode.Composition)));
    }

    /// <summary>
    /// On a Tamil lane the labels are Tamil, but the two phrases that come from the rules are not.
    /// They are what an inspector looks for, in the words the rules use.
    /// </summary>
    [Fact]
    public void ATamilBillOfSupplyKeepsThePhrasesTheRulesUse()
    {
        var printed = Print(Sell(TaxMode.Composition), ReceiptLanguage.Tamil);

        Assert.Contains("BILL OF SUPPLY", printed);
        Assert.Contains("Composition taxable person", printed);
        Assert.Contains("பில் நம்பர்", printed);
        Assert.DoesNotContain("TAX INVOICE", printed);
    }

    /// <summary>The Tamil subtotal is not the same word as the Tamil total, or the bill says it twice.</summary>
    [Fact]
    public void TheTamilSubtotalIsNotTheTamilTotal()
    {
        Assert.NotEqual(ReceiptLabels.TamilLabels.Total, ReceiptLabels.TamilLabels.Subtotal);
        Assert.Contains(ReceiptLabels.TamilLabels.Subtotal, Print(Sell(TaxMode.Composition), ReceiptLanguage.Tamil));
    }

    [Fact]
    public void AGstLaneStillPrintsATaxInvoiceWithItsBreakup()
    {
        var printed = Print(Sell(TaxMode.Gst));

        Assert.Contains("TAX INVOICE", printed);
        Assert.Contains("CGST", printed);
        Assert.DoesNotContain("BILL OF SUPPLY", printed);
        Assert.DoesNotContain("Composition taxable person", printed);
    }

    // ---- Reading it back ------------------------------------------------------------------------

    [Fact]
    public void TheModeSurvivesBeingWrittenDownAndReadBack()
    {
        var saved = Sell(TaxMode.Composition);
        var reloaded = new InvoiceRepository(_temp.Database).FindByInvoiceNo(saved.InvoiceNo)!;

        Assert.Equal(TaxMode.Composition, reloaded.Sale.TaxMode);
    }

    /// <summary>
    /// The reason the mode is stored on the invoice rather than read from settings.
    ///
    /// A shop crosses the turnover threshold and registers normally. Every bill it issued before
    /// that was a bill of supply and must reprint as one — reading today's setting would reprint it
    /// as a tax invoice showing no tax, a document claiming the shop collected GST it never did.
    /// </summary>
    [Fact]
    public void ABillOfSupplyStillReprintsAsOneAfterTheShopSwitchesToGst()
    {
        var underComposition = Sell(TaxMode.Composition);

        // The shop switches. Settings now say Gst; nothing about the old sale has changed.
        var reloaded = new InvoiceRepository(_temp.Database).FindByInvoiceNo(underComposition.InvoiceNo)!;
        var reprinted = Print(reloaded, isReprint: true);

        Assert.Contains("BILL OF SUPPLY", reprinted);
        Assert.Contains("Composition taxable person", reprinted);
        Assert.Contains("** REPRINT **", reprinted);
        Assert.DoesNotContain("TAX INVOICE", reprinted);
    }

    /// <summary>And the other way round: a tax invoice from before the switch stays a tax invoice.</summary>
    [Fact]
    public void BothKindsCanSitInTheSameBooksAndEachReadsBackAsItself()
    {
        var taxInvoice = Sell(TaxMode.Gst);
        var billOfSupply = Sell(TaxMode.Composition);

        var invoices = new InvoiceRepository(_temp.Database);

        Assert.Contains("TAX INVOICE", Print(invoices.FindByInvoiceNo(taxInvoice.InvoiceNo)!));
        Assert.Contains("BILL OF SUPPLY", Print(invoices.FindByInvoiceNo(billOfSupply.InvoiceNo)!));
    }

    /// <summary>
    /// Every sale taken before the column existed was a tax invoice, and reads back as one.
    /// </summary>
    [Fact]
    public void ASaleWithNothingSaidAboutTheModeIsATaxInvoice()
    {
        Assert.Equal(TaxMode.Gst, new InvoiceEngine(HomeState).TaxMode);
        Assert.Contains("TAX INVOICE", Print(Sell(TaxMode.Gst)));
    }

    // ---- The day-end report ----------------------------------------------------------------------

    private string CloseAndPrintZReport(TaxMode mode)
    {
        Sell(mode);

        var closes = new DayCloseRepository(_temp.Database, new HeldBillRepository(_temp.Database));
        var day = closes.Close(Lane, DateTimeOffset.Now);

        return new ZReportComposer(Store, ReceiptBuilder.Width80Mm, ReceiptLanguage.English, mode)
            .Compose(day)
            .ToPlainText();
    }

    /// <summary>
    /// The day's report has no tax section either. A column of zeroes and a "0%" slab against the
    /// whole day's takings is the same misstatement as on the bill, filed rather than handed over.
    /// </summary>
    [Fact]
    public void ACompositionLanesDayEndReportHasNoTaxSection()
    {
        var report = CloseAndPrintZReport(TaxMode.Composition);

        Assert.DoesNotContain("CGST", report);
        Assert.DoesNotContain("SGST", report);
        Assert.DoesNotContain("0%", report);
        Assert.Contains("Subtotal", report);

        // The takings are still reported in full — this removes the tax breakdown, not the money.
        Assert.Contains("48.00", report);
    }

    [Fact]
    public void AGstLanesDayEndReportStillCarriesItsTaxBreakdown()
    {
        var report = CloseAndPrintZReport(TaxMode.Gst);

        Assert.Contains("CGST", report);
        Assert.Contains("SGST", report);
        Assert.Contains("18%", report);
    }
}
