using System.Windows.Input;
using Pos.Core.Domain;
using Pos.Core.Domain.Printing;
using Pos.Core.Hardware.Printing;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// Every unit the shop can sell in - the metric four and all the traditional ones - rung up at the
/// till with the keyboard, paid for, and printed on the counter bill in Tamil and in English.
/// </summary>
/// <remarks>
/// The unit table is tested on its own in the core; this is the whole way through for each row of
/// it: the line on the screen, the quantity the till will and will not take, the amount, and what
/// the customer reads on the paper - "1.5 முழம்" on a Tamil bill, "1.5 Muzham" on an English one.
/// </remarks>
public class UnitTillTests
{
    private const decimal Price = 40m;

    public static TheoryData<UnitType> EveryUnit()
    {
        var data = new TheoryData<UnitType>();

        foreach (var unit in Units.All)
            data.Add(unit.Type);

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryUnit))]
    public void EveryUnitIsSoldAtTheTillAndPrintedOnTheBill(UnitType unit)
    {
        var info = Units.Of(unit);
        var barcode = Ean13($"8907{(int)unit:D8}");

        using var till = new BillingHarness(
            Catalogue.Item(sku: $"U{(int)unit:D2}", barcode: barcode, name: $"Sold by the {info.Code}", price: Price, gstRate: 5m, unit: unit));

        till.Scan(barcode);

        var line = Assert.Single(till.ViewModel.Lines);
        Assert.Equal(info.Group == UnitGroup.Standard ? Units.ScreenLabel(unit) : info.Tamil, line.UnitLabel);

        // Part of one where the unit allows it - half a padi, a muzham and a half - and never
        // where it does not: a mistyped 1.5 seepu is refused on the line, and the edit stays open.
        till.Press(Key.F3);
        till.ViewModel.EditBuffer = "1.5";
        till.Press(Key.Enter);

        decimal quantity;

        if (info.Fractional)
        {
            quantity = 1.5m;
            Assert.Equal(quantity, line.Quantity);
        }
        else
        {
            Assert.Equal(1m, line.Quantity);
            Assert.Contains("cannot take a fractional quantity", till.ViewModel.StatusMessage);

            quantity = 2m;
            till.ViewModel.EditBuffer = "2";
            till.Press(Key.Enter);
            Assert.Equal(quantity, line.Quantity);
        }

        till.Press(Key.F12);
        till.Press(Key.Enter);
        till.Press(Key.Enter);

        var invoice = till.Invoices.FindLatest(BillingHarness.LaneId)!;
        var sold = Assert.Single(invoice.Sale.Lines);

        Assert.Equal(unit, sold.Unit);
        Assert.Equal(quantity, sold.Quantity);
        Assert.Equal(Price * quantity, sold.LineTotal);

        // The counter bill, as the shop's own bill prints it: the quantity with its unit, in the
        // bill's language.
        var tamil = Compact(ReceiptLanguage.Tamil).Compose(invoice).ToPlainText();
        var english = Compact(ReceiptLanguage.English).Compose(invoice).ToPlainText();

        Assert.Contains(Units.WithQuantity(quantity, unit, ReceiptLanguage.Tamil), tamil);
        Assert.Contains(Units.WithQuantity(quantity, unit, ReceiptLanguage.English), english);
        Assert.Contains((Price * quantity).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), tamil);
    }

    [Fact]
    public void TheTableCoversEveryUnitTheShopCanChoose() =>
        Assert.Equal(Enum.GetValues<UnitType>().Length, EveryUnit().Count<object[]>());

    private static ReceiptComposer Compact(ReceiptLanguage language) =>
        new(BillingHarness.Store, ReceiptBuilder.Width80Mm, language, ReceiptLayout.Compact);

    /// <summary>Twelve digits and the EAN-13 check digit a scanner would read.</summary>
    private static string Ean13(string twelve)
    {
        var sum = 0;

        for (var i = 0; i < 12; i++)
            sum += (twelve[i] - '0') * (i % 2 == 0 ? 1 : 3);

        return twelve + ((10 - sum % 10) % 10);
    }
}
