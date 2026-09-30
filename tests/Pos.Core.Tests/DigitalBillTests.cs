using Pos.Core.Analytics;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Domain.Printing;
using Pos.Core.Hardware.Printing;
using Pos.Core.Loyalty;
using Pos.TestSupport;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// Digital bills: a settled bill as the WhatsApp message a customer gets instead of paper, and as a
/// full A4 invoice - each carrying what the paper carries, read from the same stored lines.
/// </summary>
public class DigitalBillTests : IDisposable
{
    private readonly TempDatabase _temp = new();

    public void Dispose() => _temp.Dispose();

    private static readonly StoreProfile Store = new()
    {
        Name = "Sri Murugan Stores",
        AddressLine1 = "12 North Street",
        AddressLine2 = "Thanjavur",
        Gstin = "33AEIPH7795F1Z9",
        FooterMessage = "Thank you, visit again",
    };

    /// <summary>A dal and half a kilo of sugar for Lakshmi, 400.50 rounded to 400, paid in cash with 100 back.</summary>
    private SettledInvoice Sale(TaxMode mode = TaxMode.Gst, LoopbackPrinterService? printer = null, bool print = true, string stateCode = "33")
    {
        _temp.Items.UpsertRange(
        [
            Catalogue.Item(sku: "DAL001", name: "Toor Dal 1kg", price: 189m, gstRate: 5m, hsn: "0713"),
            Catalogue.Item(sku: "SUG001", name: "Sugar Loose", price: 45m, gstRate: 5m, hsn: "1701", unit: UnitType.Kilogram),
        ]);

        var customers = new CustomerRepository(_temp.Database);
        var lakshmi = customers.Add(new Customer { MobileNo = "9500012345", Name = "Lakshmi", StateCode = stateCode, LoyaltyBalance = 100 });

        var bill = new InvoiceEngine("33", mode, roundToRupee: true);
        bill.AddItem(_temp.Items.FindBySku("DAL001")!, 2m);
        bill.AddItem(_temp.Items.FindBySku("SUG001")!, 0.5m);
        bill.SetCustomer(lakshmi);

        var basket = new TenderBasket(bill.Totals.AmountPayable);
        basket.Add(TenderType.Cash, 500m);

        var checkout = new CheckoutService(
            new InvoiceRepository(_temp.Database), customers, new RecordingDrawerService(),
            LoyaltyRules.Default, TimeProvider.System, printer, new ReceiptComposer(Store));

        return checkout.Complete("L1", bill, basket, printReceipt: print).Invoice;
    }

    // ---- The message -----------------------------------------------------------------------------

    [Fact]
    public void TheMessageCarriesWhatThePaperDoes()
    {
        var invoice = Sale();

        var text = DigitalBill.Text(invoice, Store);

        Assert.StartsWith("*Sri Murugan Stores*\n12 North Street, Thanjavur\nGSTIN 33AEIPH7795F1Z9\n*TAX INVOICE* " + invoice.InvoiceNo + "\n", text);
        Assert.Contains("Customer: Lakshmi (9500012345)\n", text);
        Assert.Contains("Toor Dal 1kg\n  2 Pcs x 189.00 = 378.00\n  HSN 0713, GST 5%\n", text);
        Assert.Contains("Sugar Loose\n  0.5 Kg x 45.00 = 22.50\n  HSN 1701, GST 5%\n", text);
        Assert.Contains($"Taxable value: {invoice.Sale.Totals.SubtotalTaxable:N2}\n", text);
        Assert.Contains("GST 5% on ", text);
        Assert.Contains("Round off: -0.50\n", text);
        Assert.Contains("*Total: Rs 400.00*\n", text);
        Assert.Contains("Paid: Cash 500.00; change 100.00\n", text);
        Assert.EndsWith("Thank you, visit again", text);
    }

    [Fact]
    public void ABillOfSupplyCarriesNoTaxAndTheDeclaration()
    {
        var text = DigitalBill.Text(Sale(TaxMode.Composition), Store);

        Assert.Contains("*BILL OF SUPPLY*", text);
        Assert.DoesNotContain("GST 5%", text);
        Assert.DoesNotContain("Taxable value", text);
        Assert.Contains(CompositionDeclaration.Text, text);
    }

    // ---- WhatsApp --------------------------------------------------------------------------------

    [Theory]
    [InlineData("9500012345", "919500012345")]
    [InlineData("09500012345", "919500012345")]
    [InlineData("919500012345", "919500012345")]
    [InlineData("+91 95000 12345", "919500012345")]
    public void AnIndianMobileIsGivenItsCountryCode(string mobile, string phone) =>
        Assert.StartsWith($"whatsapp://send?phone={phone}&text=", DigitalBill.WhatsAppLink(mobile, "hi"));

    [Theory]
    [InlineData("12345")]
    [InlineData("")]
    [InlineData("449500012345")]
    public void ANumberWhatsAppCannotBeOpenedAtIsNot(string mobile) => Assert.Null(DigitalBill.WhatsAppLink(mobile, "hi"));

    [Fact]
    public void TheBillTravelsInTheLinkEncoded() =>
        Assert.Equal("whatsapp://send?phone=919500012345&text=%2ATotal%3A%20Rs%20400.00%2A%0ATamil%20%E0%AE%A8", DigitalBill.WhatsAppLink("9500012345", "*Total: Rs 400.00*\nTamil ந"));

    // ---- No paper --------------------------------------------------------------------------------

    /// <summary>Taken digitally: the same invoice, numbered and stored, with nothing sent to the printer.</summary>
    [Fact]
    public void ASaleTakenDigitallyPrintsNothingAndIsStillTheInvoice()
    {
        var printer = new LoopbackPrinterService();

        var invoice = Sale(printer: printer, print: false);

        Assert.Empty(printer.Jobs);
        Assert.NotNull(new InvoiceRepository(_temp.Database).FindByInvoiceNo(invoice.InvoiceNo));
    }

    [Fact]
    public void ASaleOnPaperStillPrints()
    {
        var printer = new LoopbackPrinterService();

        Sale(printer: printer);

        Assert.Single(printer.Jobs);
    }

    // ---- The A4 invoice --------------------------------------------------------------------------

    [Fact]
    public void TheA4InvoiceHasTheLinesTheTaxAndTheTotalInWords()
    {
        var invoice = Sale();

        var page = InvoicePage.Render(invoice, Store, "33");

        Assert.Contains("TAX INVOICE", page);
        Assert.Contains(invoice.InvoiceNo, page);
        Assert.Contains("33AEIPH7795F1Z9", page);
        Assert.Contains("33-Tamil Nadu", page);
        Assert.Contains("<th class=\"num\">CGST</th><th class=\"num\">SGST</th>", page);
        Assert.Contains("HSN 0713", page);
        Assert.Contains("Tax by HSN and rate", page);
        Assert.Contains("Rupees Four Hundred Only", page);
        Assert.Contains("Rs 400.00", page);
        Assert.DoesNotContain(">COPY<", page);
    }

    [Fact]
    public void AnInterStateInvoiceShowsIgstAndTheirState()
    {
        var page = InvoicePage.Render(Sale(stateCode: "29"), Store, "33");

        Assert.Contains("<th class=\"num\">IGST</th>", page);
        Assert.Contains("29-Karnataka", page);
        Assert.Contains("Inter-state: IGST", page);
    }

    [Fact]
    public void ABillOfSupplyPageHasNoTaxColumns()
    {
        var page = InvoicePage.Render(Sale(TaxMode.Composition), Store, "33", isCopy: true);

        Assert.Contains("BILL OF SUPPLY", page);
        Assert.Contains(">COPY<", page);
        Assert.DoesNotContain("<th class=\"num\">CGST</th>", page);
        Assert.DoesNotContain("Tax by HSN and rate", page);
    }

    // ---- In words --------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, "Rupees Zero Only")]
    [InlineData(19, "Rupees Nineteen Only")]
    [InlineData(90, "Rupees Ninety Only")]
    [InlineData(400, "Rupees Four Hundred Only")]
    [InlineData(400.50, "Rupees Four Hundred and Fifty Paise Only")]
    [InlineData(1001, "Rupees One Thousand One Only")]
    [InlineData(100000, "Rupees One Lakh Only")]
    [InlineData(12345678.09, "Rupees One Crore Twenty Three Lakh Forty Five Thousand Six Hundred Seventy Eight and Nine Paise Only")]
    public void AnAmountIsWrittenTheIndianWay(double amount, string words) =>
        Assert.Equal(words, AmountInWords.Rupees((decimal)amount));

    [Fact]
    public void ANegativeAmountIsNotWritten() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => AmountInWords.Rupees(-1m));
}
