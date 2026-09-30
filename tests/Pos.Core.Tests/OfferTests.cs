using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Domain.Import;
using Pos.Core.Domain.Printing;
using Pos.TestSupport;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// Offers and schemes: each comes out as a discount on the lines it applies to, to the paisa, so the
/// GST is worked out on what the customer paid - and a discount given by hand is never touched.
/// </summary>
public class OfferTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 9, 29); // a Tuesday

    private static readonly Item Soap = Catalogue.Item(id: 1, sku: "SOAP75", name: "Bath Soap 75g", price: 20m, gstRate: 18m, hsn: "3401");
    private static readonly Item Dal = Catalogue.Item(id: 2, sku: "DAL001", name: "Toor Dal 1kg", price: 189m);
    private static readonly Item Sugar = Catalogue.Item(id: 3, sku: "SUG001", name: "Sugar Loose", price: 45m, unit: UnitType.Kilogram, hsn: "1701") with { Category = "Staples" };
    private static readonly Item Oil = Catalogue.Item(id: 4, sku: "OIL001", name: "Groundnut Oil 1L", price: 150m) with { Category = "Oils" };
    private static readonly Item Biscuit = Catalogue.Item(id: 5, sku: "BISC01", name: "Biscuits", price: 40m);

    private readonly TempDatabase _temp = new();

    public void Dispose() => _temp.Dispose();

    private static Offer BuyTwoGetOne => new() { Name = "Buy 2 soaps get 1", Kind = OfferKind.BuyGet, Sku = "SOAP75", ItemId = 1, Buy = 2, Get = 1 };

    private static Offer TenOffOils => new() { Name = "10% off oils", Kind = OfferKind.Percent, Category = "Oils", Percent = 10m };

    private static Offer FiftyOffAThousand => new() { Name = "50 off 1000", Kind = OfferKind.BillAmount, Amount = 50m, MinBill = 1000m };

    private static InvoiceEngine Bill(params (Item Item, decimal Quantity)[] lines)
    {
        var bill = new InvoiceEngine("33");

        foreach (var (item, quantity) in lines)
            bill.AddItem(item, quantity);

        return bill;
    }

    /// <summary>What the till does: works the offers out, and applies them.</summary>
    private static InvoiceEngine Priced(InvoiceEngine bill, params Offer[] offers)
    {
        foreach (var given in OfferEngine.Work(bill.Lines, offers, Today))
            bill.ApplyOffer(given.Index, given.Discount, given.OfferName);

        return bill;
    }

    // ---- Buy some, get some free -------------------------------------------------------------

    [Theory]
    [InlineData(2, 0)]
    [InlineData(3, 20)]
    [InlineData(5, 20)]
    [InlineData(6, 40)]
    public void BuyTwoGetOneGivesEveryThirdFree(int soaps, int off)
    {
        var line = Priced(Bill((Soap, soaps)), BuyTwoGetOne).Lines[0];

        Assert.Equal(off, line.Discount);
        Assert.Equal(off > 0 ? "Buy 2 soaps get 1" : null, line.OfferName);
    }

    /// <summary>
    /// The free soap is taxed on nothing: 40.00 charged at 18% is 33.8983 taxable and 6.1017 tax,
    /// split 3.05 CGST and 3.05 SGST.
    /// </summary>
    [Fact]
    public void TheTaxIsOnWhatWasCharged()
    {
        var line = Priced(Bill((Soap, 3m)), BuyTwoGetOne).Lines[0];

        Assert.Equal(40.00m, line.Tax.Gross);
        Assert.Equal(40.00m, line.LineTotal);
        Assert.Equal(33.8983m, line.Tax.TaxableValue);
        Assert.Equal(6.1017m, line.Tax.TotalTax);
        Assert.Equal(3.05m, line.Tax.Cgst);
        Assert.Equal(3.05m, line.Tax.Sgst);
    }

    /// <summary>Scanned one at a time, the third soap is the free one.</summary>
    [Fact]
    public void SoapsOnSeparateLinesMakeTheLastOneFree()
    {
        var lines = Priced(Bill((Soap, 1m), (Soap, 1m), (Soap, 1m)), BuyTwoGetOne).Lines;

        Assert.Equal([0m, 0m, 20m], lines.Select(l => l.Discount));
    }

    [Fact]
    public void BuyGetIsForWholePiecesNotWeights()
    {
        var byWeight = BuyTwoGetOne with { Sku = "SUG001", ItemId = 3 };

        Assert.Equal(0m, Priced(Bill((Sugar, 3m)), byWeight).Lines[0].Discount);
    }

    [Fact]
    public void TakingTheThirdOffTakesTheFreeOneBack()
    {
        var bill = Priced(Bill((Soap, 3m)), BuyTwoGetOne);

        bill.SetQuantity(0, 2m);
        Priced(bill, BuyTwoGetOne);

        Assert.Equal(0m, bill.Lines[0].Discount);
        Assert.Null(bill.Lines[0].OfferName);
    }

    // ---- A share off, and so many for a price ------------------------------------------------

    [Fact]
    public void APercentageOffADepartment()
    {
        var bill = Priced(Bill((Oil, 2m), (Dal, 1m)), TenOffOils);

        Assert.Equal(30m, bill.Lines[0].Discount);
        Assert.Equal(0m, bill.Lines[1].Discount);
    }

    /// <summary>1.25 kg at 45 is 56.25; 7.5% of it is 4.21875, and to the paisa 4.22.</summary>
    [Fact]
    public void APercentageIsToThePaisa()
    {
        var offer = new Offer { Name = "Sugar 7.5%", Kind = OfferKind.Percent, Sku = "SUG001", ItemId = 3, Percent = 7.5m };

        Assert.Equal(4.22m, Priced(Bill((Sugar, 1.25m)), offer).Lines[0].Discount);
    }

    /// <summary>Seven biscuits at 40, three for 100: two threes at 20 off each, and one at full price.</summary>
    [Fact]
    public void SoManyForAPrice()
    {
        var offer = new Offer { Name = "3 for 100", Kind = OfferKind.MultiPrice, Sku = "BISC01", ItemId = 5, Buy = 3, Price = 100m };

        var line = Priced(Bill((Biscuit, 7m)), offer).Lines[0];

        Assert.Equal(40m, line.Discount);
        Assert.Equal(240m, line.LineTotal);
    }

    [Fact]
    public void TheOfferThatGivesTheMostWins()
    {
        var tenPercent = new Offer { Name = "10% off soap", Kind = OfferKind.Percent, Sku = "SOAP75", ItemId = 1, Percent = 10m };

        var line = Priced(Bill((Soap, 3m)), tenPercent, BuyTwoGetOne).Lines[0];

        Assert.Equal(20m, line.Discount);
        Assert.Equal("Buy 2 soaps get 1", line.OfferName);
    }

    // ---- A discount given by hand ------------------------------------------------------------

    [Fact]
    public void ADiscountGivenByHandIsNeverTouched()
    {
        var bill = Bill((Soap, 3m));
        bill.SetDiscount(0, 5m);

        Priced(bill, BuyTwoGetOne);

        Assert.Equal(5m, bill.Lines[0].Discount);
        Assert.Null(bill.Lines[0].OfferName);
        Assert.Throws<InvalidOperationException>(() => bill.ApplyOffer(0, 20m, "Buy 2 soaps get 1"));
    }

    [Fact]
    public void SettingTheHandDiscountBackToNothingGivesTheLineBackToTheOffers()
    {
        var bill = Bill((Soap, 3m));
        bill.SetDiscount(0, 5m);
        bill.SetDiscount(0, 0m);

        Assert.Equal(20m, Priced(bill, BuyTwoGetOne).Lines[0].Discount);
    }

    // ---- The bill ----------------------------------------------------------------------------

    /// <summary>
    /// 1,095 on the bill, 50 off: spread by what each line comes to, 43.15 on the dal's 945 and 6.85 on
    /// the oil's 150 - to the paisa, adding to exactly 50.
    /// </summary>
    [Fact]
    public void MoneyOffTheBillIsSpreadAcrossItsLines()
    {
        var bill = Priced(Bill((Dal, 5m), (Oil, 1m)), FiftyOffAThousand);

        Assert.Equal([43.15m, 6.85m], bill.Lines.Select(l => l.Discount));
        Assert.Equal(1045m, bill.Totals.AmountPayable);
    }

    /// <summary>The sum is what the bill comes to after its item offers: seven oils, 1,050 less 10%, is 945.</summary>
    [Fact]
    public void TheBillSumIsAfterItemOffers() =>
        Assert.Equal(105m, Priced(Bill((Oil, 7m)), TenOffOils, FiftyOffAThousand).Lines[0].Discount);

    [Fact]
    public void ABillUnderTheSumGetsNothing() =>
        Assert.All(Priced(Bill((Dal, 5m)), FiftyOffAThousand).Lines, l => Assert.Equal(0m, l.Discount));

    [Fact]
    public void AShareOffTheBill()
    {
        var offer = new Offer { Name = "5% over 1000", Kind = OfferKind.BillPercent, Percent = 5m, MinBill = 1000m };

        var bill = Priced(Bill((Dal, 5m), (Oil, 1m)), offer);

        Assert.Equal(54.75m, bill.Lines.Sum(l => l.Discount));
    }

    /// <summary>
    /// A line with an offer of its own takes its share of the bill offer too, and is named with both.
    /// Eight oils are 1,200, less 10% is 1,080: over the thousand, so 50 more.
    /// </summary>
    [Fact]
    public void AnItemOfferAndABillOfferOnOneLineAreNamedTogether()
    {
        var bill = Priced(Bill((Oil, 8m)), TenOffOils, FiftyOffAThousand);

        Assert.Equal(170m, bill.Lines[0].Discount);
        Assert.Equal("10% off oils + 50 off 1000", bill.Lines[0].OfferName);
    }

    /// <summary>A bill offer is spread over the lines not discounted by hand; the hand discount stays.</summary>
    [Fact]
    public void ABillOfferGoesAroundAHandDiscount()
    {
        var bill = Bill((Dal, 5m), (Oil, 1m));
        bill.SetDiscount(1, 10m);

        Priced(bill, FiftyOffAThousand);

        Assert.Equal(50m, bill.Lines[0].Discount);
        Assert.Equal(10m, bill.Lines[1].Discount);
    }

    // ---- A free item with a big bill ---------------------------------------------------------

    private static Offer FreeSugar => new() { Name = "Free sugar over 500", Kind = OfferKind.FreeItem, Sku = "SUG001", ItemId = 3, FreeQuantity = 1m, MinBill = 500m };

    [Fact]
    public void AFreeItemComesWithABigEnoughBill()
    {
        var bill = Priced(Bill((Dal, 3m), (Sugar, 2m)), FreeSugar);

        Assert.Equal(45m, bill.Lines[1].Discount);
        Assert.Equal(45m, bill.Lines[1].LineTotal);
    }

    /// <summary>The gift does not count towards the bill it is a gift for.</summary>
    [Fact]
    public void TheFreeItemDoesNotQualifyItself()
    {
        var bill = Priced(Bill((Dal, 2m), (Sugar, 4m)), FreeSugar);

        Assert.Equal(0m, bill.Lines[1].Discount);
    }

    // ---- When -------------------------------------------------------------------------------

    [Fact]
    public void AnOfferRunsOnlyOnItsDaysAndDates()
    {
        var ended = BuyTwoGetOne with { To = Today.AddDays(-1) };
        var notYet = BuyTwoGetOne with { From = Today.AddDays(1) };
        var wednesdays = BuyTwoGetOne with { Days = [DayOfWeek.Wednesday] };
        var tuesdays = BuyTwoGetOne with { Days = [DayOfWeek.Tuesday] };

        Assert.Equal(0m, Priced(Bill((Soap, 3m)), ended).Lines[0].Discount);
        Assert.Equal(0m, Priced(Bill((Soap, 3m)), notYet).Lines[0].Discount);
        Assert.Equal(0m, Priced(Bill((Soap, 3m)), wednesdays).Lines[0].Discount);
        Assert.Equal(20m, Priced(Bill((Soap, 3m)), tuesdays).Lines[0].Discount);
    }

    [Fact]
    public void AnOfferOnAnItemTheShopDoesNotHaveGivesNothing() =>
        Assert.Equal(0m, Priced(Bill((Soap, 3m)), BuyTwoGetOne with { ItemId = null }).Lines[0].Discount);

    [Theory]
    [InlineData(OfferKind.BuyGet, 0, 1, 0, 0, "Buy and get")]
    [InlineData(OfferKind.Percent, 0, 0, 150, 0, "more than 0 and less than 100")]
    [InlineData(OfferKind.MultiPrice, 1, 0, 0, 100, "2 or more")]
    [InlineData(OfferKind.BillAmount, 0, 0, 0, 0, "money off is more than nothing")]
    public void AnOfferThatCannotBeRightSaysWhy(OfferKind kind, int buy, int get, double percent, double price, string said)
    {
        var offer = new Offer { Name = "x", Kind = kind, Sku = "SOAP75", Buy = buy, Get = get, Percent = (decimal)percent, Price = (decimal)price, MinBill = 100m };

        Assert.Contains(said, offer.Problem());
    }

    // ---- The sheet -------------------------------------------------------------------------------

    private static readonly IReadOnlySet<string> Skus = new HashSet<string>(["SOAP75", "DAL001", "SUG001", "BISC01"], StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlySet<string> Categories = new HashSet<string>(["Staples", "Oils"], StringComparer.OrdinalIgnoreCase);

    private static OfferSheetPlan Read(string csv) => OfferSheet.Read(new StringReader(csv), Skus, Categories);

    [Fact]
    public void AFreshSheetCarriesExamplesThatLoadNothing()
    {
        var sheet = OfferSheet.Write([]);

        Assert.Contains("# Buy 2 soaps get 1,BuyGet", sheet);
        var plan = Read(sheet);
        Assert.True(plan.IsClean);
        Assert.Empty(plan.Offers);
    }

    [Fact]
    public void ASheetReadsEachKind()
    {
        var plan = Read("""
            name,kind,sku,category,buy,get,percent,amount,price,min_bill,free_qty,from,to,days
            Buy 2 soaps get 1,buy-get,SOAP75,,2,1,,,,,,2026-10-01,31-10-2026,
            10% off staples,Percent,,Staples,,,10%,,,,,,,
            Biscuits 3 for 100,MultiPrice,BISC01,,3,,,,100,,,,,
            50 off 1000,BillAmount,,,,,,50,,"1,000",,,,
            Wednesday 5%,BillPercent,,,,,5,,,500,,,,Wed Sat
            Free sugar,FreeItem,SUG001,,,,,,,2000,1,,,
            """);

        Assert.True(plan.IsClean, string.Join("; ", plan.Problems.Select(p => $"{p.Line} {p.Column} {p.Problem}")));
        Assert.Equal(6, plan.Offers.Count);
        Assert.Equal(new DateOnly(2026, 10, 31), plan.Offers[0].To);
        Assert.Equal(10m, plan.Offers[1].Percent);
        Assert.Equal(1000m, plan.Offers[3].MinBill);
        Assert.Equal([DayOfWeek.Wednesday, DayOfWeek.Saturday], plan.Offers[4].Days);
    }

    [Theory]
    [InlineData("x,Discount,SOAP75,,,,,,,,,,,", "kind", "not a kind of offer")]
    [InlineData("x,BuyGet,NOPE,,2,1,,,,,,,,", "sku", "No item in the catalogue has SKU 'NOPE'")]
    [InlineData("x,BuyGet,SOAP75,,2,1,,,,,,2026-10-31,2026-10-01,", "to", "ends")]
    [InlineData("x,BuyGet,SOAP75,,2,1,,,,,,31/31/2026,,", "from", "not a date")]
    [InlineData("x,Percent,,Staples,,,150,,,,,,,", "percent", "less than 100")]
    [InlineData("x,BillAmount,,,,,,50,,,,,,", "min_bill", "smallest bill")]
    [InlineData("x,BuyGet,SOAP75,,2,1,,,,,,,,Funday", "days", "not a day")]
    public void AWrongRowIsRefusedWithWhereAndWhy(string row, string column, string said)
    {
        var plan = Read($"name,kind,sku,category,buy,get,percent,amount,price,min_bill,free_qty,from,to,days\n{row}");

        var problem = Assert.Single(plan.Problems);
        Assert.Equal(2, problem.Line);
        Assert.Equal(column, problem.Column);
        Assert.Contains(said, problem.Problem);
        Assert.Empty(plan.Offers);
    }

    [Fact]
    public void TwoOffersCannotShareAName()
    {
        var plan = Read("name,kind,sku,buy,get\nSoap deal,BuyGet,SOAP75,2,1\nsoap deal,BuyGet,SOAP75,3,1");

        Assert.Contains("already the name on line 2", Assert.Single(plan.Problems).Problem);
    }

    [Fact]
    public void ADepartmentWithNothingInItIsNamedNotRefused()
    {
        var plan = Read("name,kind,category,percent\nFruit 5%,Percent,Fruit,5");

        Assert.True(plan.IsClean);
        Assert.Contains("nothing in the catalogue is in 'Fruit'", Assert.Single(plan.Warnings));
    }

    [Fact]
    public void TheSheetSavedIsTheSheetLoaded()
    {
        Offer[] offers =
        [
            BuyTwoGetOne with { ItemId = null, From = new DateOnly(2026, 10, 1), Days = [DayOfWeek.Wednesday] },
            TenOffOils with { Category = "Staples" },
            FiftyOffAThousand,
        ];

        var plan = Read(OfferSheet.Write(offers));

        Assert.True(plan.IsClean);
        Assert.Equal(offers.Select(o => o.Describe()), plan.Offers.Select(o => o.Describe()));
        Assert.Equal(offers.Select(o => o.When()), plan.Offers.Select(o => o.When()));
    }

    // ---- Kept, and on the bill -----------------------------------------------------------------

    [Fact]
    public void OffersAreKeptAndTheirItemsFound()
    {
        _temp.Items.UpsertRange([Soap with { Id = 0 }]);
        var store = new OfferRepository(_temp.Database);

        store.ReplaceAll([BuyTwoGetOne with { ItemId = null, Days = [DayOfWeek.Friday] }, TenOffOils, FiftyOffAThousand], DateTimeOffset.Now);

        var kept = store.All();
        Assert.Equal(["10% off oils", "50 off 1000", "Buy 2 soaps get 1"], kept.Select(o => o.Name));
        Assert.Equal(_temp.Items.FindBySku("SOAP75")!.Id, kept[2].ItemId);
        Assert.Equal([DayOfWeek.Friday], kept[2].Days);

        store.ReplaceAll([FiftyOffAThousand], DateTimeOffset.Now);
        Assert.Equal("50 off 1000", Assert.Single(store.All()).Name);
    }

    /// <summary>The offer's name goes on the stored line, the paper and the figures of what it gave.</summary>
    [Fact]
    public void AnOfferedLineIsStoredPrintedAndCounted()
    {
        _temp.Items.UpsertRange([Soap with { Id = 0 }]);
        var soap = _temp.Items.FindBySku("SOAP75")!;
        var offer = BuyTwoGetOne with { ItemId = soap.Id };
        var invoices = new InvoiceRepository(_temp.Database);
        var receipts = new ReceiptComposer(new StoreProfile { Name = "Sri Murugan Stores" });

        var bill = Priced(Bill((soap, 3m)), offer);
        var basket = new TenderBasket(bill.Totals.AmountPayable);
        basket.Add(TenderType.Cash, bill.Totals.AmountPayable);
        var sale = new CheckoutService(invoices, new CustomerRepository(_temp.Database), new RecordingDrawerService()).Complete("L1", bill, basket).Invoice;

        var stored = invoices.FindByInvoiceNo(sale.InvoiceNo)!;
        Assert.Equal("Buy 2 soaps get 1", stored.Sale.Lines[0].OfferName);
        Assert.Contains("Offer: Buy 2 soaps get 1", receipts.Compose(stored).ToPlainText());
        Assert.Contains("  Offer: Buy 2 soaps get 1\n", DigitalBill.Text(stored, new StoreProfile { Name = "Sri Murugan Stores" }));

        var given = Assert.Single(new OfferRepository(_temp.Database).Given(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1)));
        Assert.Equal(new OfferUse("Buy 2 soaps get 1", 1, 20m), given);
    }

    [Fact]
    public void AParkedBillKeepsItsOffers()
    {
        var held = new HeldBillRepository(_temp.Database);
        var lines = Priced(Bill((Soap, 3m)), BuyTwoGetOne).SnapshotLines();

        held.Park("L1", "H001", DateTimeOffset.Now, null, lines);

        Assert.Equal("Buy 2 soaps get 1", held.Recall("L1", "H001")!.Lines[0].OfferName);
    }
}
