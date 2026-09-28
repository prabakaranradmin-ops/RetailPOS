using Pos.Core.Domain;
using Pos.Core.Domain.Printing;
using Pos.Core.Hardware.Printing;
using Xunit;
using Xunit.Abstractions;

namespace Pos.Core.Tests;

/// <summary>
/// The two bill layouts. Different paper, the same invoice: every figure on one is on the other,
/// every line carries its quantity with its unit, and nothing a tax invoice must carry is dropped to
/// make the compact one shorter.
/// </summary>
public class ReceiptLayoutTests(ITestOutputHelper output)
{
    private static readonly StoreProfile Store = new()
    {
        Name = "Sri Murugan Stores",
        Gstin = "33AABCS1429B1ZX",
        FssaiNumber = "12421020000840",
        FooterMessage = "Thank you, visit again",
    };

    private static readonly DateTimeOffset At = new(2026, 9, 22, 20, 30, 0, TimeSpan.FromHours(5.5));

    // ---- The quantity column, on both ----------------------------------------------------------

    [Theory]
    [InlineData(ReceiptLayout.Standard)]
    [InlineData(ReceiptLayout.Compact)]
    public void EveryQuantityPrintsWithItsUnit(ReceiptLayout layout)
    {
        var paper = Print(Bill(), layout);

        Assert.Contains("1 Pcs", paper);
        Assert.Contains("2.75 Kg", paper);
        Assert.Contains("2.5 Muzham", paper);
        Assert.Contains("1 Seepu", paper);
    }

    [Theory]
    [InlineData(ReceiptLayout.Standard)]
    [InlineData(ReceiptLayout.Compact)]
    public void ATamilBillNamesTraditionalUnitsInTamil(ReceiptLayout layout)
    {
        var paper = Print(Bill(), layout, language: ReceiptLanguage.Tamil);

        Assert.Contains("2.5 முழம்", paper);
        Assert.Contains("1 சீப்பு", paper);

        // The metric units stay as the shops' own Tamil bills print them.
        Assert.Contains("1 Pcs", paper);
        Assert.Contains("2.75 Kg", paper);
    }

    /// <summary>A quantity column cut short would print "2.5 Muz", which is not a unit.</summary>
    [Theory]
    [InlineData(ReceiptLayout.Standard, ReceiptBuilder.Width80Mm)]
    [InlineData(ReceiptLayout.Standard, ReceiptBuilder.Width58Mm)]
    [InlineData(ReceiptLayout.Compact, ReceiptBuilder.Width80Mm)]
    [InlineData(ReceiptLayout.Compact, ReceiptBuilder.Width58Mm)]
    public void TheLongestQuantityIsNeverCut(ReceiptLayout layout, int width)
    {
        var bill = Of(Line(9, "Ponni Rice", "1006", 480m, 0m, 12.5m, UnitType.Marakkaal));

        var tamil = Print(bill, layout, width, ReceiptLanguage.Tamil);
        var english = Print(bill, layout, width);

        Assert.Contains("12.5 மரக்கால்", tamil);
        Assert.Contains("12.5 Marakkaal", english);

        // Nor is the amount beside it, which on narrow paper is what gave way first.
        Assert.Contains(Lines(english), l => l.EndsWith("6,000.00", StringComparison.Ordinal));
        Assert.Contains(Lines(tamil), l => l.EndsWith("6,000.00", StringComparison.Ordinal));
        Assert.All(Lines(english), l => Assert.True(l.Length <= width, $"'{l}' runs past the paper"));
    }

    /// <summary>
    /// Three pieces, two and three-quarter kilos and a comb of bananas do not add up to anything, so
    /// the standard bill stops printing a total quantity for them.
    /// </summary>
    [Fact]
    public void AMixedBillPrintsNoTotalQuantity()
    {
        var mixed = Print(Bill(), ReceiptLayout.Standard);
        Assert.Contains("Items: 6", mixed);
        Assert.DoesNotContain("Qty: ", mixed);

        var sameUnit = Print(Of(Line(1, "Toor Dal 1kg", "0713", 189m, 5m), Line(2, "Shampoo", "3305", 299m, 18m, 2m)), ReceiptLayout.Standard);
        Assert.Contains("Qty: 3 Pcs", sameUnit);
    }

    // ---- The compact bill ----------------------------------------------------------------------

    [Fact]
    public void TheCompactBillHasItemQuantityAndAmountButNoRateColumn()
    {
        var paper = Print(Bill(), ReceiptLayout.Compact);
        var heading = Lines(paper).Single(l => l.StartsWith("Item ", StringComparison.Ordinal) && l.Contains("Amount"));

        Assert.Contains("Qty", heading);
        Assert.Contains("Amount", heading);
        Assert.DoesNotContain("Rate", heading);

        // The rate is still on the bill - under the line, where a customer checks a loose line.
        Assert.Contains("(HSN:0603) GST:0%  @30.00", paper);
        Assert.Contains("(HSN:3305) GST:18%  @299.00  less 49.00", paper);
    }

    /// <summary>
    /// Shorter is not lighter. What a tax invoice has to carry is all still there: who issued it,
    /// its number and date, the HSN and rate of each line, and the tax at each slab.
    /// </summary>
    [Fact]
    public void TheCompactBillIsStillAFullTaxInvoice()
    {
        var bill = Bill();
        var paper = Print(bill, ReceiptLayout.Compact);

        Assert.Contains("TAX INVOICE", paper);
        Assert.Contains("GSTIN 33AABCS1429B1ZX", paper);
        Assert.Contains("FSSAI No 12421020000840", paper);
        Assert.Contains("Bill No: RM/26-27/42", paper);
        Assert.Contains("22-09-2026 08:30 PM", paper);
        Assert.Contains("(HSN:0713) GST:5%", paper);
        Assert.Contains("Tax summary", paper);
        Assert.Contains("18%", Lines(paper).SkipWhile(l => !l.StartsWith("Tax summary", StringComparison.Ordinal)).Skip(1).First(l => l.StartsWith("18%", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Read top to bottom, the figures add up: the total is the sum of the amounts printed above it,
    /// the round-off nudges it, and what is left is the Total Amount handed over.
    /// </summary>
    [Fact]
    public void TheCompactTotalsAddUpAsPrinted()
    {
        var bill = Bill(roundToRupee: true);
        var totals = bill.Sale.Totals;
        var paper = Print(bill, ReceiptLayout.Compact);

        Assert.Equal(1_996.75m, totals.GrandTotal);
        Assert.Equal(0.25m, totals.RoundOff);
        Assert.Equal(1_997.00m, totals.AmountPayable);

        Assert.Contains("TOTAL: 1,996.75", paper);
        Assert.Contains("Round off: +0.25", paper);
        Assert.Contains("Total Amount : Rs. 1,997.00", paper);

        // A discount line under a total that is already after discount would read as coming off
        // twice. It is on its line, and in the saving at the foot.
        Assert.DoesNotContain("Discount:", paper);
        Assert.Contains("Today's saving : 49.00", paper);
    }

    [Fact]
    public void TheCompactBillPrintsOnlyTheTendersUsed()
    {
        var paper = Print(Bill(payments: [new Tender(TenderType.Cash, 2_000m)], change: 3.25m), ReceiptLayout.Compact);

        Assert.Contains(Lines(paper), l => l.StartsWith("Cash", StringComparison.Ordinal) && l.EndsWith("2,000.00", StringComparison.Ordinal));
        Assert.Contains(Lines(paper), l => l.StartsWith("Change", StringComparison.Ordinal) && l.EndsWith("3.25", StringComparison.Ordinal));
        Assert.DoesNotContain(Lines(paper), l => l.StartsWith("UPI", StringComparison.Ordinal) || l.StartsWith("Card", StringComparison.Ordinal) || l.StartsWith("Credit", StringComparison.Ordinal));
    }

    [Fact]
    public void AWalkInIsBilledToCash()
    {
        var paper = Print(Bill(walkIn: true), ReceiptLayout.Compact);

        Assert.Contains("Customer: CASH", paper);
    }

    [Fact]
    public void ANamedCustomerIsBilledByNameWithTheirNumber()
    {
        var paper = Print(Bill(), ReceiptLayout.Compact);
        var line = Lines(paper).Single(l => l.StartsWith("Customer:", StringComparison.Ordinal));

        Assert.StartsWith("Customer: Lakshmi", line);
        Assert.EndsWith("9500012345", line);
    }

    /// <summary>Who billed it, on which till, and when - the line a shop reads back for a query.</summary>
    [Fact]
    public void TheCompactBillIsSignedByTheCashierTillAndTime()
    {
        var paper = Print(Bill(cashier: "Murugan"), ReceiptLayout.Compact);

        Assert.Contains("Murugan/T1/22-09-2026 08:30 PM", paper);
    }

    /// <summary>
    /// On 58mm paper the bill number and the date do not fit on one line. Cutting the number to fit
    /// would print a different bill's number, so they go onto two lines instead.
    /// </summary>
    [Fact]
    public void NarrowPaperNeverCutsTheBillNumberOrTheName()
    {
        var bill = Bill(number: "MURUGAN/2026-27/000142");
        var paper = Print(bill, ReceiptLayout.Compact, ReceiptBuilder.Width58Mm);

        Assert.Contains("Bill No: MURUGAN/2026-27/000142", paper);
        Assert.Contains("Customer: Lakshmi", paper);
        Assert.All(Lines(paper), l => Assert.True(l.Length <= ReceiptBuilder.Width58Mm, $"'{l}' runs past the paper"));
    }

    [Fact]
    public void ACompactReprintSaysSo()
    {
        var paper = new ReceiptComposer(Store, layout: ReceiptLayout.Compact).Compose(Bill(), isReprint: true).ToPlainText();

        Assert.Contains("** REPRINT **", paper);
    }

    /// <summary>
    /// A composition dealer's compact bill is still a bill of supply: its heading, its declaration,
    /// no GST rate against any line and no tax block.
    /// </summary>
    [Fact]
    public void ACompactBillOfSupplyShowsNoTax()
    {
        var bill = Bill(taxMode: TaxMode.Composition);
        var paper = Print(bill, ReceiptLayout.Compact);
        output.WriteLine(paper);

        Assert.Contains("BILL OF SUPPLY", paper);
        Assert.DoesNotContain("TAX INVOICE", paper);
        Assert.Contains("(HSN:0713)", paper);
        Assert.DoesNotContain("GST:", paper);
        Assert.DoesNotContain("Tax summary", paper);
        Assert.Contains("composition", paper, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Switching -----------------------------------------------------------------------------

    /// <summary>The owner's choice reaches the till's own composer, so the next bill follows it.</summary>
    [Fact]
    public void TheSameComposerSwitchesLayoutForTheNextBill()
    {
        var composer = new ReceiptComposer(Store);
        var bill = Bill();

        var before = composer.Compose(bill).ToPlainText();
        composer.Layout = ReceiptLayout.Compact;
        var after = composer.Compose(bill).ToPlainText();

        Assert.DoesNotContain("Total Amount", before);
        Assert.Contains("Total Amount", after);
    }

    [Fact]
    public void BothLayoutsChargeTheSameAmount()
    {
        var bill = Bill(roundToRupee: true);

        Assert.Contains("Rs. 1,997.00", Print(bill, ReceiptLayout.Standard));
        Assert.Contains("Rs. 1,997.00", Print(bill, ReceiptLayout.Compact));
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private string Print(
        SettledInvoice bill,
        ReceiptLayout layout,
        int width = ReceiptBuilder.Width80Mm,
        ReceiptLanguage language = ReceiptLanguage.English)
    {
        var paper = new ReceiptComposer(Store, width, language, layout).Compose(bill).ToPlainText();
        output.WriteLine(paper);
        return paper;
    }

    private static string[] Lines(string paper) =>
        paper.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

    private static InvoiceLine Line(
        long id,
        string name,
        string hsn,
        decimal price,
        decimal gst,
        decimal quantity = 1m,
        UnitType unit = UnitType.Each,
        decimal discount = 0m) =>
        InvoiceLine.Rehydrate(id, name, hsn, null, null, unit, price, price, true, gst, quantity, discount, false);

    private static readonly Customer Lakshmi = new() { Id = 7, MobileNo = "9500012345", Name = "Lakshmi", StateCode = "33" };

    private static SettledInvoice Of(params InvoiceLine[] lines) => Bill(lines: lines);

    private static SettledInvoice Bill(
        InvoiceLine[]? lines = null,
        bool roundToRupee = false,
        Tender[]? payments = null,
        decimal change = 0m,
        Customer? customer = null,
        bool walkIn = false,
        string? cashier = null,
        string number = "RM/26-27/42",
        TaxMode taxMode = TaxMode.Gst)
    {
        var rate = taxMode == TaxMode.Composition ? 0m : 1m;

        lines ??=
        [
            Line(1, "Toor Dal 1kg", "0713", 189m, 5m * rate),
            Line(2, "Sugar Loose", "1701", 45m, 5m * rate, 2.75m, UnitType.Kilogram),
            Line(3, "Shampoo 340ml", "3305", 299m, 18m * rate, discount: 49m),
            Line(4, "Groundnut Oil 5L", "1512", 1_299m, 5m * rate),
            Line(5, "Malligai Poo", "0603", 30m, 0m, 2.5m, UnitType.Muzham),
            Line(6, "Poovan Banana", "0803", 60m, 0m, 1m, UnitType.Seepu),
        ];

        var totals = InvoiceTotals.From(lines, roundToRupee);

        var sale = new SaleDraft(
            "T1",
            At,
            walkIn ? null : customer ?? Lakshmi,
            lines,
            totals,
            payments ?? [new Tender(TenderType.Cash, totals.AmountPayable)],
            change,
            0,
            0,
            null,
            cashier,
            taxMode);

        return new SettledInvoice(42, number, sale);
    }
}
