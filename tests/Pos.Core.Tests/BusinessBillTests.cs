using Pos.Core.Analytics;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Domain.Printing;
using Pos.TestSupport;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// Bills to businesses: a customer with a GSTIN gets a tax invoice to a registered buyer - their
/// name, GSTIN, address and the state the goods went to - taxed by that state, kept as it was issued,
/// and filed bill by bill in the return (B2B), with its returns note by note (CDNR).
/// </summary>
/// <remarks>
/// 210 at 5% is 200 taxable and 10 tax, so every figure below can be worked on paper.
/// </remarks>
public class BusinessBillTests : IDisposable
{
    private const string Lane = "L1";
    private const string Home = "33";

    /// <summary>A Karnataka business: its bills from a Tamil Nadu shop are inter-state, IGST.</summary>
    private const string Kumar = "29AABCK1234M1ZG";

    /// <summary>A Tamil Nadu business: CGST and SGST.</summary>
    private const string Local = "33AAACR5055K1ZE";

    private readonly TempDatabase _temp = new();

    public void Dispose() => _temp.Dispose();

    private CustomerRepository Customers => new(_temp.Database);

    private InvoiceRepository Invoices => new(_temp.Database);

    private static readonly StoreProfile Store = new() { Name = "Sri Murugan Stores", Gstin = "33AEIPH7795F1Z9" };

    private Customer Business(string gstin = Kumar, string mobile = "9800011122", string name = "Kumar Traders", string? address = "12 MG Road, Bengaluru")
    {
        var customer = Customers.Add(new Customer { MobileNo = mobile, Name = name, StateCode = Home });
        return Customers.SetBusiness(customer.Id, gstin, address);
    }

    private Item Dal()
    {
        _temp.Items.UpsertRange([Catalogue.Item(sku: "DAL001", name: "Toor Dal 1kg", price: 210m, gstRate: 5m, hsn: "0713")]);
        return _temp.Items.FindBySku("DAL001")!;
    }

    private SettledInvoice Sell(Customer? customer, decimal quantity = 1m, decimal gstRate = 5m)
    {
        var item = gstRate == 5m ? Dal() : SellNil();
        var bill = new InvoiceEngine(Home);
        bill.AddItem(item, quantity);
        bill.SetCustomer(customer);

        var basket = new TenderBasket(bill.Totals.AmountPayable);
        basket.Add(TenderType.Cash, bill.Totals.AmountPayable);

        return new CheckoutService(Invoices, Customers, new RecordingDrawerService()).Complete(Lane, bill, basket).Invoice;

        Item SellNil()
        {
            _temp.Items.UpsertRange([Catalogue.Item(sku: "RICE01", name: "Rice Loose", price: 60m, gstRate: 0m, hsn: "1006", unit: UnitType.Kilogram)]);
            return _temp.Items.FindBySku("RICE01")!;
        }
    }

    private GstReturnData Return() =>
        new GstReturnQuery(_temp.Database).Gather(Lane, new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1), Home);

    // ---- The customer ------------------------------------------------------------------------------

    [Fact]
    public void AGstinMakesACustomerABusinessInItsState()
    {
        var kumar = Business();

        Assert.True(kumar.IsBusiness);
        Assert.Equal(Kumar, kumar.Gstin);
        Assert.Equal("29", kumar.StateCode);
        Assert.Equal("12 MG Road, Bengaluru", kumar.Address);
        Assert.Equal(kumar.Id, Customers.FindByGstin(Kumar.ToLowerInvariant())!.Id);
        Assert.Contains(Customers.Search("AABCK"), c => c.Id == kumar.Id);
    }

    [Fact]
    public void AGstinThatDoesNotCheckOutIsRefused()
    {
        var customer = Customers.Add(new Customer { MobileNo = "9800011122", StateCode = Home });

        Assert.Throws<ArgumentException>(() => Customers.SetBusiness(customer.Id, "29AABCK1234M1ZH", null));
        Assert.Null(Customers.FindByMobile("9800011122")!.Gstin);
    }

    [Fact]
    public void TwoCustomersCannotShareAGstin()
    {
        Business();
        var other = Customers.Add(new Customer { MobileNo = "9800099999", StateCode = Home });

        var refused = Assert.Throws<InvalidOperationException>(() => Customers.SetBusiness(other.Id, Kumar, null));
        Assert.Contains("already Kumar Traders's", refused.Message);
    }

    [Fact]
    public void TakingTheGstinOffLeavesTheirState()
    {
        var kumar = Business();

        var plain = Customers.SetBusiness(kumar.Id, null, null);

        Assert.False(plain.IsBusiness);
        Assert.Equal("29", plain.StateCode);
    }

    // ---- The bill --------------------------------------------------------------------------------

    [Fact]
    public void ABillToABusinessInAnotherStateIsIgst()
    {
        var sale = Sell(Business(), 2m);
        var line = sale.Sale.Lines[0];

        Assert.True(line.IsInterState);
        Assert.Equal(20m, line.Tax.Igst);
        Assert.Equal(0m, line.Tax.Cgst);
        Assert.Equal(new BusinessBuyer(Kumar, "Kumar Traders", "12 MG Road, Bengaluru"), sale.Sale.Buyer);
    }

    /// <summary>A business that moves does not change the bill it was already given.</summary>
    [Fact]
    public void TheBuyerIsKeptAsItWasOnTheBill()
    {
        var kumar = Business();
        var sale = Sell(kumar);

        Customers.SetBusiness(kumar.Id, Kumar, "44 New Road, Mysuru");
        Customers.Rename(kumar.Id, "Kumar Traders Pvt Ltd");

        var stored = Invoices.FindByInvoiceNo(sale.InvoiceNo)!;
        Assert.Equal("Kumar Traders", stored.Sale.Buyer!.Name);
        Assert.Equal("12 MG Road, Bengaluru", stored.Sale.Buyer.Address);
    }

    [Fact]
    public void ABillToSomebodyWithoutAGstinHasNoBuyer() =>
        Assert.Null(Sell(Customers.Add(new Customer { MobileNo = "9500012345", StateCode = Home })).Sale.Buyer);

    [Fact]
    public void ThePaperSaysWhoItWasToAndWhere()
    {
        var sale = Invoices.FindByInvoiceNo(Sell(Business()).InvoiceNo)!;

        foreach (var layout in new[] { ReceiptLayout.Standard, ReceiptLayout.Compact })
        {
            var text = new ReceiptComposer(Store, layout: layout).Compose(sale).ToPlainText();

            Assert.Contains("Bill to: Kumar Traders", text);
            Assert.Contains($"Buyer GSTIN", text);
            Assert.Contains(Kumar, text);
            Assert.Contains("12 MG Road, Bengaluru", text);
            Assert.Contains("29-Karnataka", text);
        }
    }

    [Fact]
    public void TheWhatsAppBillAndTheA4InvoiceSayItToo()
    {
        var sale = Invoices.FindByInvoiceNo(Sell(Business()).InvoiceNo)!;

        var text = DigitalBill.Text(sale, Store);
        Assert.Contains($"Bill to: Kumar Traders\nBuyer GSTIN {Kumar}\n12 MG Road, Bengaluru\nPlace of supply: 29-Karnataka\n", text);
        Assert.Contains("IGST 10.00", text);

        var page = InvoicePage.Render(sale, Store, Home);
        Assert.Contains($"<b>GSTIN</b> {Kumar}", page);
        Assert.Contains("29-Karnataka", page);
        Assert.Contains("<th class=\"num\">IGST</th>", page);
    }

    // ---- The return ------------------------------------------------------------------------------

    /// <summary>
    /// One bill to a business, one to a customer without a GSTIN. The business bill is in B2B and
    /// nowhere else; the other is in B2CS and nowhere else.
    /// </summary>
    [Fact]
    public void ABillToABusinessIsFiledBillByBill()
    {
        var b2b = Sell(Business(), 2m);
        Sell(null);

        var data = Return();

        var row = Assert.Single(data.B2b);
        Assert.Equal(new GstB2bRow(Kumar, "Kumar Traders", b2b.InvoiceNo, DateOnly.FromDateTime(DateTime.Today), 420m, "29", 5m, 400m, 20m, 0m, 0m), row);
        Assert.Equal(1, data.B2bInvoices);

        var b2cs = Assert.Single(data.RateWise);
        Assert.Equal(200m, b2cs.TaxableValue);
        Assert.Equal("33", b2cs.PlaceOfSupply);

        Assert.Equal(600m, data.TaxableValue);
        Assert.Equal(20m, data.Igst);
        Assert.Equal(10m, data.Cgst + data.Sgst);

        Assert.Equal(400m, Assert.Single(data.HsnB2b).TaxableValue);
        Assert.Equal(200m, Assert.Single(data.Hsn).TaxableValue);
        Assert.Contains(data.Warnings, w => w.Contains("1 bill this month was to businesses"));
    }

    /// <summary>A business bill over the B2CL sum is still B2B: B2CL is for buyers without a GSTIN.</summary>
    [Fact]
    public void ALargeBillToABusinessIsNotB2cl()
    {
        Sell(Business(), 500m);

        var data = Return();

        Assert.Empty(data.LargeInterState);
        Assert.Equal(100_000m, Assert.Single(data.B2b).TaxableValue);
    }

    [Fact]
    public void ReturnsFromABusinessAreListedNoteByNote()
    {
        var sale = Sell(Business(Local), 3m);
        var notes = new CreditNoteRepository(_temp.Database);
        var draft = CreditNoteDraft.Build(notes.Returnable(sale.InvoiceNo)!, [(1, 1m, true)], TenderType.Cash, "damaged", roundToRupee: false);
        var note = notes.Issue(draft, Lane, DateTimeOffset.Now, "Priya");

        var data = Return();

        var row = Assert.Single(data.B2bCreditNotes);
        Assert.Equal(new GstB2bCreditNoteRow(Local, "Kumar Traders", note.Number, DateOnly.FromDateTime(DateTime.Today), sale.InvoiceNo, 210m, "33", 5m, 200m, 0m, 5m, 5m), row);

        // Not netted out of anything else: the B2B row is the whole bill, and B2CS is empty.
        Assert.Equal(600m, Assert.Single(data.B2b).TaxableValue);
        Assert.Empty(data.RateWise);
        Assert.Equal(400m, data.TaxableValue);
        Assert.Equal(400m, Assert.Single(data.HsnB2b).TaxableValue);
    }

    [Fact]
    public void NilRatedSalesToABusinessGoInTheRegisteredRows()
    {
        Sell(Business(Local), 2m, gstRate: 0m);

        var data = Return();

        Assert.Equal(120m, data.NilB2bIntraState);
        Assert.Equal(0m, data.NilIntraState);
        Assert.Empty(data.B2b);
        Assert.Contains("Intra-State supplies to registered persons,120.00", GstReturnFiles.Exempt(data));
    }

    [Fact]
    public void TheFilesCarryTheB2bTables()
    {
        var sale = Sell(Business(), 2m);
        var data = Return();
        var folder = Path.Combine(Path.GetTempPath(), $"gst-b2b-{Guid.NewGuid():N}");

        try
        {
            var written = GstReturnFiles.Write(data, Path.Combine(folder, "gst.html"), "Sri Murugan Stores", Store.Gstin);

            var b2b = File.ReadAllText(Assert.Single(written, p => p.EndsWith("gst-b2b.csv", StringComparison.Ordinal)));
            Assert.Contains($"{Kumar},Kumar Traders,{sale.InvoiceNo},", b2b);
            Assert.Contains(",420.00,29-Karnataka,N,,Regular B2B,,5,400.00,", b2b);
            Assert.Contains(written, p => p.EndsWith("gst-hsn(b2b).csv", StringComparison.Ordinal));
            Assert.Contains("Bills to businesses", File.ReadAllText(written[0]));
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }
}
