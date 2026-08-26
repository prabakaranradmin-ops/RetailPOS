using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Domain.Printing;
using Pos.Core.Hardware.Drawer;
using Pos.Core.Hardware.Printing;
using Pos.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Pos.Core.Tests;

/// <summary>
/// The same bill printed in Tamil and in English.
/// </summary>
/// <remarks>
/// The labels are the only thing that changes. Every figure, every item name and the invoice number
/// itself have to be identical in both, because a bill that says one thing in one language and
/// another in the other is not a translation problem, it is a wrong bill. Most of these tests
/// therefore assert the two receipts against each other rather than against a fixture.
/// </remarks>
public class TamilReceiptTests(ITestOutputHelper output) : IDisposable
{
    private const string Lane = "L1";
    private const string HomeState = "33";

    private readonly TempDatabase _temp = new();
    private readonly RecordingDrawerService _drawer = new();
    private readonly LoopbackPrinterService _printer = new();

    private static readonly StoreProfile Store = new()
    {
        Name = "ரவி மளிகை",
        AddressLine1 = "No. 3/324, Main Road,",
        AddressLine2 = "Sengipatti, Thanjavur - 613501",
        Gstin = "33AEIPH7795F1Z9",
        FssaiNumber = "12426020000127",
        CustomerCarePhone = "9080678177",
        FooterMessage = "நன்றி, மீண்டும் வருக",
        CurrencyPrefix = "Rs:",
    };

    public void Dispose() => _temp.Dispose();

    private CheckoutService NewCheckout() => new(
        new InvoiceRepository(_temp.Database, new InvoiceNumberFormat { StorePrefix = "RM", IncludeLaneSegment = false }),
        new CustomerRepository(_temp.Database),
        _drawer,
        null,
        TimeProvider.System,
        _printer,
        new ReceiptComposer(Store, ReceiptBuilder.Width80Mm, ReceiptLanguage.Tamil));

    private SettledInvoice Sale(params (string Name, decimal Price, decimal Gst, decimal Qty)[] items)
    {
        var bill = new InvoiceEngine(HomeState);
        var id = 1;

        foreach (var (name, price, gst, quantity) in items)
        {
            bill.AddItem(Catalogue.Item(id: id, sku: $"SKU{id:D4}", barcode: $"890{id:D10}", name: name, price: price, gstRate: gst));
            bill.SetQuantity(id - 1, quantity);
            id++;
        }

        var basket = new TenderBasket(bill.Totals.GrandTotal);
        basket.Add(TenderType.Upi, bill.Totals.GrandTotal);

        return NewCheckout().Complete(Lane, bill, basket).Invoice;
    }

    private static string Paper(SettledInvoice invoice, ReceiptLanguage language) =>
        new ReceiptComposer(Store, ReceiptBuilder.Width80Mm, language).Compose(invoice).ToPlainText();

    private SettledInvoice TheBillFromTheShop() => Sale(
        ("CUTTING BASMATHI BULK", 95m, 5m, 5m),
        ("AAG PALM OIL 800 G", 135m, 5m, 2m),
        ("GOLD WINNER VANASPATHI", 85m, 5m, 2m));

    // ---- The labels a Tamil grocery bill carries ------------------------------------------------

    [Theory]
    [InlineData("பில் நம்பர்")]
    [InlineData("தேதி")]
    [InlineData("கஸ்டமர்")]
    [InlineData("நேரம்")]
    [InlineData("பொருளின் பெயர்")]
    [InlineData("விலை")]
    [InlineData("அளவு")]
    [InlineData("தொகை")]
    [InlineData("மொத்தம்")]
    public void EveryTamilLabelIsOnTheBill(string label)
    {
        var paper = Paper(TheBillFromTheShop(), ReceiptLanguage.Tamil);
        output.WriteLine(paper);

        Assert.Contains(label, paper);
    }

    [Fact]
    public void TheSavingsAndPointsLinesUseTheWordingAShopperExpects()
    {
        var customers = new CustomerRepository(_temp.Database);
        var customer = customers.Add(new Customer { MobileNo = "9876543210", Name = "ARUN", StateCode = HomeState, LoyaltyBalance = 37 });

        var bill = new InvoiceEngine(HomeState);
        bill.AddItem(Catalogue.Item(id: 1, sku: "SKU1", barcode: "8901234567890", name: "CUTTING BASMATHI BULK", price: 95m, gstRate: 5m));
        bill.SetQuantity(0, 5m);
        bill.SetDiscount(0, 156m);
        bill.SetCustomer(customer);

        var basket = new TenderBasket(bill.Totals.GrandTotal);
        basket.Add(TenderType.Upi, bill.Totals.GrandTotal);

        var paper = Paper(NewCheckout().Complete(Lane, bill, basket).Invoice, ReceiptLanguage.Tamil);
        output.WriteLine(paper);

        Assert.Contains("இன்றைய சேமிப்பு : 156.00", paper);
        Assert.Contains("இதுவரை பெற்ற மொத்த புள்ளிகள் : ", paper);
    }

    [Fact]
    public void TheFourWayTenderBlockIsPrintedWithTheTotalBesideIt()
    {
        var invoice = TheBillFromTheShop();
        var total = invoice.Sale.Totals.GrandTotal.ToString("N2", System.Globalization.CultureInfo.InvariantCulture);
        var paper = Paper(invoice, ReceiptLanguage.Tamil);

        var first = paper.Split('\n').Single(l => l.Contains("Cash") && l.Contains("UPI"));
        var second = paper.Split('\n').Single(l => l.Contains("Card") && l.Contains("Credit"));

        // 95x5 + 135x2 + 85x2. The whole bill went on UPI, so that is the only figure that is not
        // zero, and it is the one the total beside it has to equal.
        Assert.Equal("915.00", total);
        Assert.Contains(total, first);
        Assert.Contains("மொத்தம்", first);
        Assert.Contains("0.00", second);
        Assert.Contains($"Rs: {total}", second);
    }

    /// <summary>
    /// "TAX INVOICE" stays in English. It is the phrase the GST rules use and the one an inspector
    /// looks for, so it is not a label to localise.
    /// </summary>
    [Fact]
    public void TheTaxInvoiceHeadingAndTheTenderNamesStayInEnglish()
    {
        var paper = Paper(TheBillFromTheShop(), ReceiptLanguage.Tamil);

        Assert.Contains("TAX INVOICE", paper);
        Assert.Contains("Cash", paper);
        Assert.Contains("UPI", paper);
        Assert.Contains("Card", paper);
        Assert.Contains("Credit", paper);
        Assert.Contains("CGST", paper);
        Assert.Contains("SGST", paper);
    }

    // ---- The two languages against each other ----------------------------------------------------

    [Fact]
    public void EnglishAndTamilPrintTheSameFigures()
    {
        var invoice = TheBillFromTheShop();

        var tamil = Paper(invoice, ReceiptLanguage.Tamil);
        var english = Paper(invoice, ReceiptLanguage.English);

        foreach (var figure in new[] { "915.00", "475.00", "270.00", "170.00", "95.00", "135.00", "85.00" })
        {
            Assert.Contains(figure, tamil);
            Assert.Contains(figure, english);
        }

        // The invoice number, the licences and the item names are the shop's data, not labels.
        // Grocery names run past the 20-character description column and wrap; what matters is that
        // they wrap to the same place in both, since the column is measured the same way either way.
        foreach (var shared in new[] { invoice.InvoiceNo, "GSTIN 33AEIPH7795F1Z9", "FSSAI No 12426020000127", "CUTTING BASMATHI", "GOLD WINNER", "AAG PALM OIL 800 G" })
        {
            Assert.Contains(shared, tamil);
            Assert.Contains(shared, english);
        }

        // Nothing was lost to the wrap: the tail of each name is on the receipt too.
        foreach (var tail in new[] { "BULK", "VANASPATHI" })
        {
            Assert.Contains(tail, tamil);
            Assert.Contains(tail, english);
        }
    }

    [Fact]
    public void TheEnglishBillCarriesNoTamilAndNeedsNoDrawing()
    {
        var receipt = new ReceiptComposer(Store with { Name = "Ravi Maligai", FooterMessage = "Thank you" }, ReceiptBuilder.Width80Mm)
            .Compose(TheBillFromTheShop());

        var rasterizer = new RecordingTextRasterizer();
        receipt.ToEscPos(raster: new RasterOptions(rasterizer, RasterOptions.Dots80Mm, RasterMode.Auto));

        Assert.Empty(rasterizer.Runs);
    }

    /// <summary>
    /// A Tamil bill has to be drawn or it prints as rows of '?'. This is the check that the labels
    /// and the renderer are actually joined up.
    /// </summary>
    [Fact]
    public void TheTamilBillIsDrawnRatherThanSentAsCharacters()
    {
        var receipt = new ReceiptComposer(Store, ReceiptBuilder.Width80Mm, ReceiptLanguage.Tamil)
            .Compose(TheBillFromTheShop());

        var rasterizer = new RecordingTextRasterizer();
        receipt.ToEscPos(raster: new RasterOptions(rasterizer, RasterOptions.Dots80Mm, RasterMode.Auto));

        var drawn = rasterizer.Runs.Select(r => r.Text).ToList();

        Assert.Contains("ரவி மளிகை", drawn);
        Assert.Contains("பில் நம்பர்", drawn);
        Assert.Contains("பொருளின் பெயர்", drawn);
        Assert.Contains("மொத்தம்", drawn);

        // The English item names went down the character path, where they belong.
        Assert.DoesNotContain("CUTTING BASMATHI BULK", drawn);
    }

    // ---- Layout, in both languages ---------------------------------------------------------------

    [Theory]
    [InlineData(ReceiptLanguage.English, ReceiptBuilder.Width80Mm)]
    [InlineData(ReceiptLanguage.English, ReceiptBuilder.Width58Mm)]
    [InlineData(ReceiptLanguage.Tamil, ReceiptBuilder.Width80Mm)]
    [InlineData(ReceiptLanguage.Tamil, ReceiptBuilder.Width58Mm)]
    public void NoLineOverrunsThePaperInEitherLanguage(ReceiptLanguage language, int width)
    {
        var invoice = Sale(
            ("Premium Organic Cold Pressed Groundnut Oil 5 Litre Tin", 1_299m, 5m, 1m),
            ("CUTTING BASMATHI BULK", 95m, 5m, 5m));

        var paper = new ReceiptComposer(Store, width, language).Compose(invoice).ToPlainText();
        output.WriteLine(paper);

        foreach (var line in paper.Split('\n'))
            Assert.True(line.TrimEnd('\r').Length <= width, $"'{line}' is {line.TrimEnd('\r').Length} of {width} characters.");
    }

    /// <summary>
    /// On 58mm paper five cells would leave six characters each, and a figure truncated to six
    /// characters is a wrong figure. The paired blocks go one per line instead.
    /// </summary>
    [Theory]
    [InlineData(ReceiptLanguage.English)]
    [InlineData(ReceiptLanguage.Tamil)]
    public void NarrowPaperStacksTheTendersRatherThanSqueezingThem(ReceiptLanguage language)
    {
        var paper = new ReceiptComposer(Store, ReceiptBuilder.Width58Mm, language).Compose(TheBillFromTheShop()).ToPlainText();
        var lines = paper.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        Assert.DoesNotContain(lines, l => l.Contains("Cash") && l.Contains("UPI"));

        foreach (var tender in new[] { "Cash", "UPI", "Card", "Credit" })
            Assert.Contains(lines, l => l.TrimStart().StartsWith(tender, StringComparison.Ordinal));

        Assert.Contains("915.00", paper);
    }

    [Theory]
    [InlineData(ReceiptLanguage.English)]
    [InlineData(ReceiptLanguage.Tamil)]
    public void TheTendersAndThePointsAddUpToTheTotalOnTheBill(ReceiptLanguage language)
    {
        var customers = new CustomerRepository(_temp.Database);
        var customer = customers.Add(new Customer { MobileNo = "9876543210", Name = "ARUN", StateCode = HomeState, LoyaltyBalance = 5_000 });

        var bill = new InvoiceEngine(HomeState);
        bill.AddItem(Catalogue.Item(id: 1, sku: "SKU1", barcode: "8901234567890", name: "CUTTING BASMATHI BULK", price: 95m, gstRate: 5m));
        bill.SetQuantity(0, 5m);
        bill.SetCustomer(customer);

        var checkout = NewCheckout();
        var redemption = checkout.QuoteRedemption(bill.Totals.GrandTotal, customer);

        var basket = new TenderBasket(bill.Totals.GrandTotal);
        basket.Add(TenderType.LoyaltyPoints, redemption.Value, $"{redemption.Points} points");
        basket.Add(TenderType.Upi, bill.Totals.GrandTotal - redemption.Value);

        var invoice = checkout.Complete(Lane, bill, basket, redemption.Points).Invoice;
        var paper = Paper(invoice, language);
        output.WriteLine(paper);

        // Points settle a bill like any other tender, so they have to appear in the block or the
        // four figures will not come to the total printed beside them.
        Assert.Contains(redemption.Value.ToString("N2", System.Globalization.CultureInfo.InvariantCulture), paper);
        Assert.Contains((bill.Totals.GrandTotal - redemption.Value).ToString("N2", System.Globalization.CultureInfo.InvariantCulture), paper);
        Assert.Contains($"Rs: {invoice.Sale.Totals.GrandTotal:N2}", paper);
    }

    /// <summary>
    /// Redeeming points must leave the bill's tax exactly as it was — they are a way of paying, not
    /// a discount. Asserted on the printed receipt as well as in the engine, because the receipt is
    /// what a customer and an auditor actually read.
    /// </summary>
    [Fact]
    public void PointsDoNotChangeTheTaxPrintedOnTheBill()
    {
        var customers = new CustomerRepository(_temp.Database);
        var customer = customers.Add(new Customer { MobileNo = "9876543211", Name = "ARUN", StateCode = HomeState, LoyaltyBalance = 5_000 });

        string TaxLines(bool withPoints)
        {
            var bill = new InvoiceEngine(HomeState);
            bill.AddItem(Catalogue.Item(id: 1, sku: "SKU1", barcode: "8901234567890", name: "CUTTING BASMATHI BULK", price: 95m, gstRate: 5m));
            bill.SetQuantity(0, 5m);

            var basket = new TenderBasket(bill.Totals.GrandTotal);
            var points = 0;

            if (withPoints)
            {
                bill.SetCustomer(customer);
                var quote = NewCheckout().QuoteRedemption(bill.Totals.GrandTotal, customer);
                points = quote.Points;
                basket.Add(TenderType.LoyaltyPoints, quote.Value, $"{quote.Points} points");
                basket.Add(TenderType.Upi, bill.Totals.GrandTotal - quote.Value);
            }
            else
            {
                basket.Add(TenderType.Upi, bill.Totals.GrandTotal);
            }

            var paper = Paper(NewCheckout().Complete(Lane, bill, basket, points).Invoice, ReceiptLanguage.Tamil);

            return string.Join('\n', paper.Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => l.StartsWith("CGST", StringComparison.Ordinal)
                         || l.StartsWith("SGST", StringComparison.Ordinal)
                         || l.StartsWith("வரிக்குரிய தொகை", StringComparison.Ordinal)));
        }

        Assert.Equal(TaxLines(withPoints: false), TaxLines(withPoints: true));
    }
}
