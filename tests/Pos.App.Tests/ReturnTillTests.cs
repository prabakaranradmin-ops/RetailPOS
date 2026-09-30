using System.Text;
using System.Windows.Input;
using Pos.App.ViewModels;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// Goods coming back at the counter with F9 - the bill, the goods, the refund - driven by keystrokes,
/// the way a cashier does it.
/// </summary>
public class ReturnTillTests
{
    private const string Dal = "8901234567890";
    private const string Rice = "8901234567891";

    private static BillingHarness Till() => new(
        Catalogue.Item(sku: "DAL001", barcode: Dal, name: "Toor Dal 1kg", price: 100m),
        Catalogue.Item(sku: "RICE5", barcode: Rice, name: "Ponni Rice 5kg", price: 400m));

    /// <summary>Three dal and a bag of rice, paid in cash.</summary>
    private static SettledInvoice SellAndPay(BillingHarness till, string? mobile = null, bool onCredit = false)
    {
        if (mobile is not null)
        {
            till.Press(Key.F7);
            till.ViewModel.EditBuffer = mobile;
            till.Press(Key.Enter);
        }

        till.Scan(Dal);
        till.Press(Key.F3);
        till.ViewModel.EditBuffer = "3";
        till.Press(Key.Enter);
        till.Scan(Rice);

        Assert.Equal(2, till.ViewModel.Lines.Count);

        till.Press(Key.F12);

        if (onCredit)
        {
            till.Press(Key.Down);
            till.Press(Key.Down);
            till.Press(Key.Down);
        }

        till.Press(Key.Enter);
        till.Press(Key.Enter);

        return till.Invoices.FindLatest(BillingHarness.LaneId)!;
    }

    private static void Type(BillingHarness till, string text)
    {
        till.ViewModel.EditBuffer = text;
        till.Press(Key.Enter);
    }

    [Fact]
    public void F9OpensTheReturnAndEnterFindsTheLastBill()
    {
        using var till = Till();
        var sale = SellAndPay(till);

        Assert.True(till.Press(Key.F9));
        Assert.True(till.ViewModel.IsReturning);
        Assert.Equal(ReturnStage.FindBill, till.ViewModel.ReturnStage);

        till.Press(Key.Enter);

        Assert.Equal(ReturnStage.PickGoods, till.ViewModel.ReturnStage);
        Assert.Contains(sale.InvoiceNo, till.ViewModel.ReturnBillLabel);
        Assert.Equal(2, till.ViewModel.ReturnLines.Count);
        Assert.Equal(0, till.ViewModel.SelectedReturnLineIndex);
    }

    [Fact]
    public void ABillIsFoundByItsNumber()
    {
        using var till = Till();
        var first = SellAndPay(till);
        SellAndPay(till);

        till.Press(Key.F9);
        Type(till, first.InvoiceNo);

        Assert.Contains(first.InvoiceNo, till.ViewModel.ReturnBillLabel);
    }

    [Fact]
    public void ANumberThatIsNoBillIsSaid()
    {
        using var till = Till();
        SellAndPay(till);

        till.Press(Key.F9);
        Type(till, "INV/26-27/L1-999");

        Assert.Equal(ReturnStage.FindBill, till.ViewModel.ReturnStage);
        Assert.Contains("No bill", till.ViewModel.StatusMessage);
    }

    /// <summary>
    /// One dal back for cash, the whole way through on the keyboard: the credit note is issued, the
    /// drawer opens, the note prints, and the till is back to billing.
    /// </summary>
    [Fact]
    public void OneItemBackForCashEndToEnd()
    {
        using var till = Till();
        var sale = SellAndPay(till);
        var kicks = till.Drawer.KickCount;

        till.Press(Key.F9);
        till.Press(Key.Enter);          // the last bill
        Type(till, "1");                // one dal
        Assert.Equal(1m, till.ViewModel.ReturnLines[0].Returning);
        Assert.Equal(100.00m, till.ViewModel.ReturnLines[0].Refund);
        Assert.Contains("Refund ₹100.00", till.ViewModel.ReturnSummary);
        Assert.Equal(1, till.ViewModel.SelectedReturnLineIndex);

        till.Press(Key.Enter);          // done picking
        Assert.Equal(ReturnStage.Refund, till.ViewModel.ReturnStage);
        Assert.Equal(TenderType.Cash, till.ViewModel.RefundOptions[till.ViewModel.SelectedRefundIndex].Type);

        Type(till, "wrong brand");      // reason, and refund

        Assert.False(till.ViewModel.IsReturning);
        Assert.Contains("hand back ₹100.00 in cash", till.ViewModel.StatusMessage);
        Assert.Equal(kicks + 1, till.Drawer.KickCount);

        var number = Assert.Single(till.Returns.ForInvoice(sale.InvoiceNo));
        var note = till.Returns.Find(number)!;
        Assert.Equal("wrong brand", note.Reason);
        Assert.Equal(100.00m, note.Refunded);

        var paper = Encoding.Latin1.GetString(till.Printer.LastJob);
        Assert.Contains("CREDIT NOTE", paper);
        Assert.Contains(sale.InvoiceNo, paper);
    }

    [Fact]
    public void AStarBringsBackTheWholeBill()
    {
        using var till = Till();
        SellAndPay(till);

        till.Press(Key.F9);
        till.Press(Key.Enter);
        Type(till, "*");

        Assert.Equal(3m, till.ViewModel.ReturnLines[0].Returning);
        Assert.Equal(1m, till.ViewModel.ReturnLines[1].Returning);
        Assert.Contains("Refund ₹700.00", till.ViewModel.ReturnSummary);
    }

    /// <summary>"1d" on the rice: refunded, and marked as not going back on the shelf.</summary>
    [Fact]
    public void ADAfterTheQuantityMarksItDamaged()
    {
        using var till = Till();
        SellAndPay(till);

        till.Press(Key.F9);
        till.Press(Key.Enter);
        till.Press(Key.Down);
        Type(till, "1d");

        Assert.True(till.ViewModel.ReturnLines[1].Damaged);
        Assert.Equal(400.00m, till.ViewModel.ReturnLines[1].Refund);

        till.Press(Key.Enter);
        till.Press(Key.Enter);

        var note = till.Returns.Find(till.Returns.ForInvoice(till.Invoices.FindLatest(BillingHarness.LaneId)!.InvoiceNo)[0])!;
        Assert.False(Assert.Single(note.Lines).Restocked);
    }

    [Fact]
    public void MoreThanWasSoldIsRefusedOnTheLine()
    {
        using var till = Till();
        SellAndPay(till);

        till.Press(Key.F9);
        till.Press(Key.Enter);
        Type(till, "4");

        Assert.Equal(0m, till.ViewModel.ReturnLines[0].Returning);
        Assert.Contains("Only 3", till.ViewModel.StatusMessage);
    }

    [Fact]
    public void DeleteTakesALineBackOffTheReturn()
    {
        using var till = Till();
        SellAndPay(till);

        till.Press(Key.F9);
        till.Press(Key.Enter);
        Type(till, "2");
        till.Press(Key.Up);
        till.Press(Key.Delete);

        Assert.False(till.ViewModel.ReturnLines[0].IsPicked);
        Assert.Contains("not coming back", till.ViewModel.StatusMessage);
    }

    [Fact]
    public void EnterWithNothingPickedDoesNotMoveOn()
    {
        using var till = Till();
        SellAndPay(till);

        till.Press(Key.F9);
        till.Press(Key.Enter);
        till.Press(Key.Enter);

        Assert.Equal(ReturnStage.PickGoods, till.ViewModel.ReturnStage);
        Assert.Contains("Nothing is coming back", till.ViewModel.StatusMessage);
    }

    /// <summary>Escape steps back one stage at a time, and nothing is issued on the way out.</summary>
    [Fact]
    public void EscapeBacksOutAStageAtATime()
    {
        using var till = Till();
        var sale = SellAndPay(till);

        till.Press(Key.F9);
        till.Press(Key.Enter);
        Type(till, "1");
        till.Press(Key.Enter);
        Assert.Equal(ReturnStage.Refund, till.ViewModel.ReturnStage);

        till.Press(Key.Escape);
        Assert.Equal(ReturnStage.PickGoods, till.ViewModel.ReturnStage);
        Assert.Equal(1m, till.ViewModel.ReturnLines[0].Returning);

        till.Press(Key.Escape);
        Assert.Equal(ReturnStage.FindBill, till.ViewModel.ReturnStage);

        till.Press(Key.Escape);
        Assert.False(till.ViewModel.IsReturning);
        Assert.Empty(till.Returns.ForInvoice(sale.InvoiceNo));
    }

    [Fact]
    public void AReturnWaitsForTheBillOnScreen()
    {
        using var till = Till();
        till.Scan(Dal);

        till.Press(Key.F9);

        Assert.False(till.ViewModel.IsReturning);
        Assert.Contains("a return is its own document", till.ViewModel.StatusMessage);
    }

    [Fact]
    public void AWalkInIsOfferedMoneyOnly()
    {
        using var till = Till();
        SellAndPay(till);

        till.Press(Key.F9);
        till.Press(Key.Enter);

        Assert.DoesNotContain(till.ViewModel.RefundOptions, o => o.Type == TenderType.StoreCredit);
    }

    /// <summary>
    /// A customer who bought on the khata has the refund taken off it: arrow down past cash, UPI and
    /// card to the khata, and what they owe drops by the refund.
    /// </summary>
    [Fact]
    public void ACreditCustomersReturnComesOffTheirKhata()
    {
        using var till = Till();
        var lakshmi = till.AddCustomer("9876543210", name: "Lakshmi");
        SellAndPay(till, "9876543210", onCredit: true);
        Assert.Equal(700.00m, till.Credit.Balance(lakshmi.Id));

        till.Press(Key.F9);
        till.Press(Key.Enter);
        Type(till, "1");
        till.Press(Key.Enter);

        till.Press(Key.Down);
        till.Press(Key.Down);
        till.Press(Key.Down);
        Assert.Equal(TenderType.StoreCredit, till.ViewModel.RefundOptions[till.ViewModel.SelectedRefundIndex].Type);

        till.Press(Key.Enter);

        Assert.Contains("taken off their khata", till.ViewModel.StatusMessage);
        Assert.Equal(600.00m, till.Credit.Balance(lakshmi.Id));
    }

    [Fact]
    public void ABillReturnedInFullHasNothingLeftToReturn()
    {
        using var till = Till();
        SellAndPay(till);

        till.Press(Key.F9);
        till.Press(Key.Enter);
        Type(till, "*");
        till.Press(Key.Enter);
        till.Press(Key.Enter);

        till.Press(Key.F9);
        till.Press(Key.Enter);

        Assert.Equal(ReturnStage.FindBill, till.ViewModel.ReturnStage);
        Assert.Contains("already come back", till.ViewModel.StatusMessage);
    }
}
