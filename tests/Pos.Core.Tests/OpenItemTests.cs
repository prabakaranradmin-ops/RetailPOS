using Pos.Core.Analytics;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// An item not in the catalogue, sold as a line typed in at the till: the rules for what may be
/// typed, the tax on it, what it leaves alone, and the owner's list of them.
/// </summary>
public class OpenItemTests : IDisposable
{
    private const string Lane = "L1";
    private const string Home = "33";

    private readonly TempDatabase _temp = new();

    public void Dispose() => _temp.Dispose();

    private InvoiceRepository Invoices => new(_temp.Database);

    private OpenItemRepository Open => new(_temp.Database);

    private SettledInvoice Sell(string? cashier, params Item[] items)
    {
        var bill = new InvoiceEngine(Home);

        foreach (var item in items)
            bill.AddItem(item);

        var basket = new TenderBasket(bill.Totals.AmountPayable);
        basket.Add(TenderType.Cash, bill.Totals.AmountPayable);

        return new CheckoutService(Invoices, new CustomerRepository(_temp.Database), new RecordingDrawerService(),
            cashier: () => cashier, stock: new StockRepository(_temp.Database)).Complete(Lane, bill, basket).Invoice;
    }

    private static Item Coil(string name = "Mosquito coil", decimal price = 45m, decimal rate = 18m, string? hsn = null, string? barcode = null) =>
        OpenItem.For(name, price, rate, hsn, barcode);

    private long Movements()
    {
        using var connection = _temp.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM stock_movements;";
        return (long)command.ExecuteScalar()!;
    }

    // ---- What may be typed ----------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("x")]
    [InlineData(null)]
    public void ANameOfLessThanTwoLettersIsRefused(string? name) => Assert.NotNull(OpenItem.NameProblem(name));

    [Fact]
    public void ANameLongerThanABillPrintsIsRefused()
    {
        Assert.Null(OpenItem.NameProblem(new string('a', OpenItem.MaxNameLength)));
        Assert.Contains("61 letters", OpenItem.NameProblem(new string('a', OpenItem.MaxNameLength + 1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void APriceOfNothingOrLessIsRefused(int price) => Assert.Equal("The price has to be more than nothing.", OpenItem.PriceProblem(price));

    [Fact]
    public void APriceInFractionsOfAPaisaIsRefused() => Assert.NotNull(OpenItem.PriceProblem(12.345m));

    [Fact]
    public void APriceAboveTheLimitIsRefused()
    {
        Assert.Null(OpenItem.PriceProblem(OpenItem.MaxPrice));
        Assert.Equal("More than ₹1,00,000 for something not in the catalogue. Add it to the catalogue first.", OpenItem.PriceProblem(OpenItem.MaxPrice + 0.01m));
    }

    [Fact]
    public void ARateThatIsNotASlabIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => OpenItem.For("Mosquito coil", 45m, 10m));

    [Fact]
    public void ABadNameOrPriceIsRefusedWhenTheItemIsMade()
    {
        Assert.Throws<ArgumentException>(() => OpenItem.For("x", 45m, 18m));
        Assert.Throws<ArgumentException>(() => OpenItem.For("Mosquito coil", 0m, 18m));
    }

    [Fact]
    public void TheItemIsSoldAsTypedWithTheTaxInsideThePrice()
    {
        var coil = Coil(name: "  Mosquito coil  ", hsn: " 3808 ", barcode: "8901234599990");

        Assert.Equal(OpenItem.ItemId, coil.Id);
        Assert.Equal("Mosquito coil", coil.Name);
        Assert.Equal("3808", coil.HsnCode);
        Assert.Equal("8901234599990", coil.Barcode);
        Assert.Equal(45m, coil.Mrp);
        Assert.Equal(45m, coil.SellPrice);
        Assert.Equal(18m, coil.GstRate);
        Assert.True(coil.IsTaxInclusive);
        Assert.Equal(UnitType.Each, coil.UnitType);
        Assert.Null(coil.StockQty);
        Assert.Null(coil.Category);
    }

    [Fact]
    public void NoHsnCodeIsEmptyAndNoBarcodeIsNull()
    {
        var coil = Coil();

        Assert.Equal(string.Empty, coil.HsnCode);
        Assert.Null(coil.Barcode);
    }

    [Theory]
    [InlineData("89012345", true)]
    [InlineData("8901234599990", true)]
    [InlineData("12345678901234", true)]
    [InlineData("1234567", false)]
    [InlineData("123456789012345", false)]
    [InlineData("mosquito coil", false)]
    [InlineData("8901234 99990", false)]
    [InlineData(null, false)]
    public void OnlyEightToFourteenDigitsReadAsABarcode(string? text, bool barcode) =>
        Assert.Equal(barcode, OpenItem.LooksLikeBarcode(text));

    [Fact]
    public void TheSlabsOfferedAreThoseInForce() => Assert.Equal([0m, 5m, 18m, 40m], OpenItem.Slabs);

    // ---- The tax on it --------------------------------------------------------------------------

    [Fact]
    public void ItIsTaxedLikeAnyOtherLine()
    {
        // ₹118 at 18% inclusive: exactly ₹100 before tax, ₹9 each of CGST and SGST.
        var bill = new InvoiceEngine(Home);
        var line = bill.AddItem(Coil(price: 118m));

        Assert.True(line.IsOpen());
        Assert.Equal(100.0000m, line.Tax.TaxableValue);
        Assert.Equal(18.0000m, line.Tax.TotalTax);
        Assert.Equal(9.00m, line.Tax.Cgst);
        Assert.Equal(9.00m, line.Tax.Sgst);
        Assert.Equal(118.00m, line.LineTotal);

        // And to the paisa as the same thing would be from the catalogue.
        var catalogued = new InvoiceEngine(Home).AddItem(Catalogue.Item(id: 7, name: "Mosquito coil", price: 45m, gstRate: 18m));
        var open = new InvoiceEngine(Home).AddItem(Coil());

        Assert.Equal(catalogued.Tax.TaxableValue, open.Tax.TaxableValue);
        Assert.Equal(catalogued.Tax.Cgst, open.Tax.Cgst);
        Assert.Equal(catalogued.LineTotal, open.LineTotal);
        Assert.False(catalogued.IsOpen());
    }

    // ---- What it leaves alone -------------------------------------------------------------------

    [Fact]
    public void ItIsSavedWithTheBillAndTakesNothingOffAnyShelf()
    {
        _temp.Items.AddRange([Catalogue.Item(sku: "DAL001", barcode: "8901234567890", name: "Toor Dal 1kg", price: 189m) with { StockQty = 10m }]);
        var dal = _temp.Items.FindBySku("DAL001")!;

        var sale = Sell("Murugan", dal, Coil(hsn: "3808", barcode: "8901234599990"));

        var saved = Invoices.FindByInvoiceNo(sale.InvoiceNo)!;
        var open = Assert.Single(saved.Sale.Lines, l => l.IsOpen());
        Assert.Equal("Mosquito coil", open.NameSnapshot);
        Assert.Equal("3808", open.HsnSnapshot);
        Assert.Equal("8901234599990", open.BarcodeSnapshot);
        Assert.Equal(45m, open.UnitPrice);

        // The dal came off its shelf; the coil had none to come off.
        Assert.Equal(9m, _temp.Items.FindBySku("DAL001")!.StockQty);
        Assert.Equal(1, Movements());
    }

    [Fact]
    public void AVoidPutsNothingBackForIt()
    {
        var sale = Sell("Murugan", Coil());

        new CheckoutService(Invoices, new CustomerRepository(_temp.Database), new RecordingDrawerService(),
            stock: new StockRepository(_temp.Database)).VoidSale(sale.InvoiceNo);

        Assert.Equal(0, Movements());
        Assert.True(Invoices.FindByInvoiceNo(sale.InvoiceNo)!.IsVoided);
    }

    [Fact]
    public void AHeldBillKeepsItAsItWas()
    {
        var held = new HeldBillRepository(_temp.Database);
        var line = InvoiceLine.FromItem(Coil(hsn: "3808", barcode: "8901234599990"), 2m);

        held.Park(Lane, "H001", DateTimeOffset.Now, customer: null, [line]);
        var back = Assert.Single(held.Recall(Lane, "H001")!.Lines);

        Assert.True(back.IsOpen());
        Assert.Equal("Mosquito coil", back.NameSnapshot);
        Assert.Equal("3808", back.HsnSnapshot);
        Assert.Equal("8901234599990", back.BarcodeSnapshot);
        Assert.Equal(45m, back.UnitPrice);
        Assert.Equal(18m, back.GstRate);
        Assert.Equal(2m, back.Quantity);
    }

    [Fact]
    public void ItCanComeBackOnACreditNoteWithNothingPutOnAShelf()
    {
        var sale = Sell("Murugan", Coil());
        var notes = new CreditNoteRepository(_temp.Database);

        var draft = CreditNoteDraft.Build(notes.Returnable(sale.InvoiceNo)!, [(1, 1m, true)], TenderType.Cash, "did not work", roundToRupee: false);
        var note = notes.Issue(draft, Lane, DateTimeOffset.Now, "Murugan");

        Assert.Equal(45m, note.Refunded);
        Assert.Equal("Mosquito coil", Assert.Single(note.Lines).Name);
        Assert.Equal(0, Movements());
    }

    // ---- The owner's list -----------------------------------------------------------------------

    [Fact]
    public void NothingIsWaitingWhenEverythingCameFromTheCatalogue()
    {
        _temp.Items.AddRange([Catalogue.Item(sku: "DAL001", name: "Toor Dal 1kg", price: 189m)]);
        Sell("Murugan", _temp.Items.FindBySku("DAL001")!);

        Assert.Empty(Open.Waiting());
    }

    [Fact]
    public void EachSaleWaitsWithWhatTheCashierTypedAndWhoTheyWere()
    {
        var sale = Sell("Murugan", Coil(hsn: "3808", barcode: "8901234599990"));

        var waiting = Assert.Single(Open.Waiting());

        Assert.Equal(sale.InvoiceNo, waiting.InvoiceNo);
        Assert.Equal("Mosquito coil", waiting.Name);
        Assert.Equal("8901234599990", waiting.Barcode);
        Assert.Equal(45m, waiting.Price);
        Assert.Equal(18m, waiting.GstRate);
        Assert.Equal("3808", waiting.Hsn);
        Assert.Equal(1m, waiting.Quantity);
        Assert.Equal("Murugan", waiting.Cashier);
    }

    [Fact]
    public void TheNewestWaitsFirst()
    {
        Sell(null, Coil(name: "Mosquito coil"));
        Sell(null, Coil(name: "Agarbathi"));

        Assert.Equal(["Agarbathi", "Mosquito coil"], Open.Waiting().Select(w => w.Name));
    }

    [Fact]
    public void AVoidedSaleIsNotWaiting()
    {
        var sale = Sell("Murugan", Coil());
        new CheckoutService(Invoices, new CustomerRepository(_temp.Database), new RecordingDrawerService()).VoidSale(sale.InvoiceNo);

        Assert.Empty(Open.Waiting());
    }

    [Fact]
    public void DealtWithTakesItOffTheListAndTheFirstWordStands()
    {
        Sell(null, Coil(name: "Mosquito coil"));
        Sell(null, Coil(name: "Agarbathi"));
        var coil = Open.Waiting().Single(w => w.Name == "Mosquito coil");

        Open.DealtWith([coil.LineId], DateTimeOffset.Now, "COIL10");
        Open.DealtWith([coil.LineId], DateTimeOffset.Now, addedAs: null);

        Assert.Equal("Agarbathi", Assert.Single(Open.Waiting()).Name);
        Assert.Equal("COIL10", AddedAs(coil.LineId));
    }

    [Fact]
    public void LeftOutIsDealtWithAsNothingAdded()
    {
        Sell(null, Coil());
        var coil = Assert.Single(Open.Waiting());

        Open.DealtWith([coil.LineId], DateTimeOffset.Now, addedAs: "  ");

        Assert.Empty(Open.Waiting());
        Assert.Null(AddedAs(coil.LineId));
    }

    [Fact]
    public void ACatalogueLineCannotBeDealtWith()
    {
        _temp.Items.AddRange([Catalogue.Item(sku: "DAL001", name: "Toor Dal 1kg", price: 189m)]);
        var sale = Sell(null, _temp.Items.FindBySku("DAL001")!);
        var lineId = LineIds(sale.InvoiceNo).Single();

        Open.DealtWith([lineId, 99_999], DateTimeOffset.Now, "DAL001");

        using var connection = _temp.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM open_item_reviews;";
        Assert.Equal(0L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void NothingToDealWithDoesNothing() => Open.DealtWith([], DateTimeOffset.Now, "X");

    // ---- Grouped for the owner ------------------------------------------------------------------

    private static OpenItemSale At(long id, int minutes, string name, string? barcode = null, string? cashier = null, decimal quantity = 1m) =>
        new(id, $"SLS/26-27/L1-{id}", new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.FromHours(5.5)).AddMinutes(minutes),
            name, barcode, 45m, 18m, string.Empty, quantity, cashier);

    [Fact]
    public void TheSameNameHoweverTypedIsOneThing()
    {
        var groups = OpenItemGroup.Of([At(1, 0, "Mosquito coil", cashier: "Murugan"), At(2, 5, "MOSQUITO   coil ", cashier: "Lakshmi", quantity: 2m), At(3, 3, "Agarbathi")]);

        Assert.Equal(2, groups.Count);

        var coil = groups[0];
        Assert.Equal(2, coil.Times);
        Assert.Equal(3m, coil.Quantity);
        Assert.Equal(2, coil.Latest.LineId);
        Assert.Equal([2L, 1L], coil.LineIds);
        Assert.Equal(["Lakshmi", "Murugan"], coil.Cashiers);

        Assert.Equal("Agarbathi", groups[1].Latest.Name);
    }

    [Fact]
    public void TheSameBarcodeIsOneThingWhateverItWasCalled()
    {
        var groups = OpenItemGroup.Of([At(1, 0, "Coil", barcode: "8901234599990"), At(2, 5, "Mosquito coil", barcode: "8901234599990"), At(3, 9, "Coil")]);

        Assert.Equal(2, groups.Count);
        Assert.Equal("Coil", groups[0].Latest.Name);
        Assert.Equal(1, groups[0].Times);
        Assert.Equal("Mosquito coil", groups[1].Latest.Name);
        Assert.Equal(2, groups[1].Times);
    }

    [Fact]
    public void ACashierNamedTwiceIsNamedOnceAndNobodyIsNotNamed()
    {
        var group = Assert.Single(OpenItemGroup.Of([At(1, 0, "Coil", cashier: "Murugan"), At(2, 5, "Coil", cashier: "murugan"), At(3, 9, "Coil")]));

        Assert.Equal(["murugan"], group.Cashiers);
    }

    // ---- The GST return -------------------------------------------------------------------------

    [Fact]
    public void TheReturnSaysWhatALineWithNoHsnCodeIs()
    {
        _temp.Items.AddRange([Catalogue.Item(sku: "DAL001", name: "Toor Dal 1kg", price: 105m, hsn: "0713")]);
        Sell(null, _temp.Items.FindBySku("DAL001")!, Coil(price: 118m));

        var data = new GstReturnQuery(_temp.Database).Gather(Lane, new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1), Home);

        Assert.Contains(data.Warnings, w => w.StartsWith("1 line was sold with no HSN code, worth Rs 118.00: items not in the catalogue", StringComparison.Ordinal));
        Assert.DoesNotContain(data.Warnings, w => w.Contains("shorter than four digits"));

        // In the figures like any other line: ₹100 taxable at 18%, under a blank code.
        Assert.Equal(100m, data.RateWise.Single(r => r.Rate == 18m).TaxableValue);
        Assert.Equal(100m, data.Hsn.Single(h => h.Hsn.Length == 0).TaxableValue);
    }

    [Fact]
    public void AShortCodeIsStillCalledShort()
    {
        _temp.Items.AddRange([Catalogue.Item(sku: "DAL001", name: "Toor Dal 1kg", price: 105m, hsn: "07")]);
        Sell(null, _temp.Items.FindBySku("DAL001")!);

        var data = new GstReturnQuery(_temp.Database).Gather(Lane, new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1), Home);

        Assert.Contains(data.Warnings, w => w.StartsWith("An HSN code shorter than four digits: 07.", StringComparison.Ordinal));
        Assert.DoesNotContain(data.Warnings, w => w.Contains("no HSN code"));
    }

    private string? AddedAs(long lineId)
    {
        using var connection = _temp.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT added_as FROM open_item_reviews WHERE invoice_line_id = $id;";
        command.Parameters.AddWithValue("$id", lineId);
        return command.ExecuteScalar() is string sku ? sku : null;
    }

    private List<long> LineIds(string invoiceNo)
    {
        using var connection = _temp.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT l.id FROM invoice_lines l JOIN invoices i ON i.id = l.invoice_id WHERE i.invoice_no = $no;";
        command.Parameters.AddWithValue("$no", invoiceNo);

        var ids = new List<long>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
            ids.Add(reader.GetInt64(0));

        return ids;
    }
}
