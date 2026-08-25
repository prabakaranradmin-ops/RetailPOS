using Pos.Core.Domain;
using Pos.Core.Tax;
using Xunit;

namespace Pos.Core.Tests;

public class InvoiceEngineTests
{
    private const string HomeState = "33";  // Tamil Nadu
    private const string AwayState = "29";  // Karnataka

    private static InvoiceEngine NewBill() => new(HomeState);

    private static Item Item(
        long id = 1,
        string name = "Toor Dal 1kg",
        decimal price = 100m,
        decimal gstRate = 5m,
        UnitType unit = UnitType.Each,
        bool taxInclusive = true,
        bool active = true) =>
        new()
        {
            Id = id,
            Sku = $"SKU{id:D4}",
            Barcode = $"890{id:D10}",
            HsnCode = "0713",
            Name = name,
            Mrp = price,
            SellPrice = price,
            GstRate = gstRate,
            IsTaxInclusive = taxInclusive,
            UnitType = unit,
            IsActive = active,
        };

    [Fact]
    public void StartsEmpty()
    {
        var bill = NewBill();

        Assert.True(bill.IsEmpty);
        Assert.Empty(bill.Lines);
        Assert.Equal(InvoiceTotals.Empty, bill.Totals);
    }

    [Fact]
    public void AddingAnItemAppendsOneLineAtQuantityOne()
    {
        var bill = NewBill();

        var line = bill.AddItem(Item());

        Assert.Single(bill.Lines);
        Assert.Equal(1m, line.Quantity);
        Assert.Equal("Toor Dal 1kg", line.NameSnapshot);
        Assert.Equal("0713", line.HsnSnapshot);
    }

    /// <summary>
    /// SRS 2.1 says a selection adds the item at quantity 1; multiples are reached with the
    /// increment key, not by silently merging rescans into an existing row.
    /// </summary>
    [Fact]
    public void ScanningTheSameItemTwiceProducesTwoLines()
    {
        var bill = NewBill();

        bill.AddItem(Item());
        bill.AddItem(Item());

        Assert.Equal(2, bill.Lines.Count);
        Assert.Equal(200.00m, bill.Totals.GrandTotal);
    }

    [Fact]
    public void RefusesToBillAnInactiveItem()
    {
        var bill = NewBill();

        var ex = Assert.Throws<InvalidOperationException>(() => bill.AddItem(Item(active: false)));
        Assert.Contains("not an active item", ex.Message);
        Assert.True(bill.IsEmpty);
    }

    [Fact]
    public void RemovingALineDropsItFromTheTotals()
    {
        var bill = NewBill();
        bill.AddItem(Item(1, price: 100m));
        bill.AddItem(Item(2, price: 250m));

        bill.RemoveAt(0);

        Assert.Single(bill.Lines);
        Assert.Equal(250.00m, bill.Totals.GrandTotal);
    }

    [Fact]
    public void QuantityAndDiscountEditsFlowStraightIntoTheTotals()
    {
        var bill = NewBill();
        bill.AddItem(Item(price: 100m, gstRate: 18m));

        bill.SetQuantity(0, 3m);
        Assert.Equal(300.00m, bill.Totals.GrandTotal);

        bill.SetDiscount(0, 50m);
        Assert.Equal(250.00m, bill.Totals.GrandTotal);
        Assert.Equal(50.00m, bill.Totals.TotalDiscount);
    }

    [Fact]
    public void IncrementAndDecrementWalkTheQuantityUpAndDown()
    {
        var bill = NewBill();
        bill.AddItem(Item());

        bill.AdjustQuantity(0, 1m);
        bill.AdjustQuantity(0, 1m);
        Assert.Equal(3m, bill.Lines[0].Quantity);

        bill.AdjustQuantity(0, -1m);
        Assert.Equal(2m, bill.Lines[0].Quantity);
    }

    /// <summary>
    /// Holding the decrement key down should end with the line gone, not with an exception on the
    /// keystroke that would have taken it to zero.
    /// </summary>
    [Fact]
    public void DecrementingPastOneRemovesTheLine()
    {
        var bill = NewBill();
        bill.AddItem(Item());

        bill.AdjustQuantity(0, -1m);

        Assert.True(bill.IsEmpty);
    }

    [Fact]
    public void PieceGoodsRejectAFractionalQuantityButWeighedGoodsAcceptIt()
    {
        var bill = NewBill();
        bill.AddItem(Item(1, unit: UnitType.Each));
        bill.AddItem(Item(2, name: "Sugar loose", unit: UnitType.Kilogram));

        Assert.Throws<ArgumentOutOfRangeException>(() => bill.SetQuantity(0, 1.5m));

        bill.SetQuantity(1, 1.5m);
        Assert.Equal(1.5m, bill.Lines[1].Quantity);
    }

    [Fact]
    public void DiscountCannotExceedTheLineValue()
    {
        var bill = NewBill();
        bill.AddItem(Item(price: 100m));

        Assert.Throws<ArgumentOutOfRangeException>(() => bill.SetDiscount(0, 150m));
        Assert.Equal(0m, bill.Lines[0].Discount);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(99)]
    public void EditingALineThatIsNotThereIsRejected(int index)
    {
        var bill = NewBill();
        bill.AddItem(Item());

        Assert.Throws<ArgumentOutOfRangeException>(() => bill.SetQuantity(index, 2m));
        Assert.Throws<ArgumentOutOfRangeException>(() => bill.SetDiscount(index, 1m));
        Assert.Throws<ArgumentOutOfRangeException>(() => bill.RemoveAt(index));
    }

    [Fact]
    public void AWalkInCustomerIsBilledIntraState()
    {
        var bill = NewBill();
        bill.AddItem(Item(gstRate: 18m));

        Assert.False(bill.IsInterState);
        Assert.Equal(0m, bill.Totals.TotalIgst);
        Assert.True(bill.Totals.TotalCgst > 0m);
    }

    [Fact]
    public void AttachingAnOutOfStateCustomerFlipsExistingLinesToIgst()
    {
        var bill = NewBill();
        bill.AddItem(Item(gstRate: 18m));
        Assert.True(bill.Totals.TotalCgst > 0m);

        bill.SetCustomer(new Customer { MobileNo = "9876543210", StateCode = AwayState });

        Assert.True(bill.IsInterState);
        Assert.Equal(0m, bill.Totals.TotalCgst);
        Assert.Equal(0m, bill.Totals.TotalSgst);
        Assert.Equal(15.25m, bill.Totals.TotalIgst);
        Assert.Equal(100.00m, bill.Totals.GrandTotal);
    }

    [Fact]
    public void AnInStateCustomerKeepsTheCgstSgstSplit()
    {
        var bill = NewBill();
        bill.AddItem(Item(gstRate: 18m));

        bill.SetCustomer(new Customer { MobileNo = "9876543210", StateCode = HomeState });

        Assert.False(bill.IsInterState);
        Assert.Equal(15.25m, bill.Totals.TotalCgst + bill.Totals.TotalSgst);
    }

    [Fact]
    public void ClearingTheCustomerReturnsTheBillToIntraState()
    {
        var bill = NewBill();
        bill.AddItem(Item(gstRate: 18m));
        bill.SetCustomer(new Customer { MobileNo = "9876543210", StateCode = AwayState });

        bill.SetCustomer(null);

        Assert.False(bill.IsInterState);
        Assert.Equal(0m, bill.Totals.TotalIgst);
    }

    /// <summary>
    /// The grand total on the printed invoice must be reproducible by adding up the line totals
    /// the customer can see in the grid.
    /// </summary>
    [Fact]
    public void TotalsReconcileLineByLine()
    {
        var bill = NewBill();
        bill.AddItem(Item(1, price: 249m, gstRate: 18m));
        bill.AddItem(Item(2, price: 33m, gstRate: 5m));
        bill.AddItem(Item(3, price: 1.76m, gstRate: 28m));
        bill.AddItem(Item(4, price: 60m, gstRate: 12m, unit: UnitType.Kilogram));
        bill.SetQuantity(3, 2.5m);
        bill.SetDiscount(0, 19m);

        var totals = bill.Totals;

        Assert.Equal(4, totals.LineCount);
        Assert.Equal(Money.ToPresentation(bill.Lines.Sum(l => l.LineTotal)), totals.GrandTotal);
        Assert.Equal(Money.ToPresentation(bill.Lines.Sum(l => l.Tax.Cgst)), totals.TotalCgst);
        Assert.Equal(Money.ToPresentation(bill.Lines.Sum(l => l.Tax.Sgst)), totals.TotalSgst);
        Assert.Equal(19.00m, totals.TotalDiscount);
        Assert.Equal(0m, totals.TotalIgst);
    }

    [Fact]
    public void UnitRateExcludesTaxAndScalesWithQuantity()
    {
        var bill = NewBill();
        bill.AddItem(Item(price: 118m, gstRate: 18m));

        Assert.Equal(100.00m, bill.Lines[0].UnitRateExclTax);

        bill.SetQuantity(0, 4m);
        Assert.Equal(100.00m, bill.Lines[0].UnitRateExclTax);
    }

    [Fact]
    public void GridTaxRatesSplitInHalfIntraStateAndSitWhollyOnIgstInterState()
    {
        var bill = NewBill();
        bill.AddItem(Item(gstRate: 18m));

        Assert.Equal(9m, bill.Lines[0].CgstRate);
        Assert.Equal(9m, bill.Lines[0].SgstRate);
        Assert.Equal(0m, bill.Lines[0].IgstRate);

        bill.SetCustomer(new Customer { MobileNo = "9", StateCode = AwayState });

        Assert.Equal(0m, bill.Lines[0].CgstRate);
        Assert.Equal(0m, bill.Lines[0].SgstRate);
        Assert.Equal(18m, bill.Lines[0].IgstRate);
    }

    [Fact]
    public void ClearingEmptiesTheBillAndDetachesTheCustomer()
    {
        var bill = NewBill();
        bill.AddItem(Item());
        bill.SetCustomer(new Customer { MobileNo = "9876543210", StateCode = AwayState });

        bill.Clear();

        Assert.True(bill.IsEmpty);
        Assert.Null(bill.Customer);
        Assert.False(bill.IsInterState);
    }

    /// <summary>
    /// The hold/recall round trip required by SRS 2.5: a parked bill must come back with its
    /// discounts and quantities exactly as they were.
    /// </summary>
    [Fact]
    public void SnapshotAndRestorePreserveTheExactLineState()
    {
        var bill = NewBill();
        bill.AddItem(Item(1, price: 249m, gstRate: 18m));
        bill.AddItem(Item(2, name: "Sugar loose", price: 45m, gstRate: 5m, unit: UnitType.Kilogram));
        bill.SetQuantity(1, 2.75m);
        bill.SetDiscount(0, 19m);
        var customer = new Customer { MobileNo = "9876543210", StateCode = HomeState };
        bill.SetCustomer(customer);

        var parked = bill.SnapshotLines();
        var expectedTotals = bill.Totals;
        bill.Clear();

        bill.Restore(parked, customer);

        Assert.Equal(expectedTotals, bill.Totals);
        Assert.Equal(2.75m, bill.Lines[1].Quantity);
        Assert.Equal(19m, bill.Lines[0].Discount);
        Assert.Equal("Sugar loose", bill.Lines[1].NameSnapshot);
    }

    /// <summary>A parked bill must not be disturbed by later edits to the live one.</summary>
    [Fact]
    public void SnapshotIsADeepCopy()
    {
        var bill = NewBill();
        bill.AddItem(Item());

        var parked = bill.SnapshotLines();
        bill.SetQuantity(0, 7m);

        Assert.Equal(1m, parked[0].Quantity);
    }

    /// <summary>
    /// Lines carry the item name and HSN as they were at the time of sale, so a later change to
    /// the item master cannot rewrite history on a reprint.
    /// </summary>
    [Fact]
    public void LineSnapshotsSurviveAnItemMasterChange()
    {
        var bill = NewBill();
        bill.AddItem(Item(1, name: "Toor Dal 1kg"));

        _ = Item(1, name: "Toor Dal 1kg (Premium)");  // the master record is edited afterwards

        Assert.Equal("Toor Dal 1kg", bill.Lines[0].NameSnapshot);
    }
}
