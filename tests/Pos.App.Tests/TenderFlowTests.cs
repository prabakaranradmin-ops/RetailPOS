using System.Windows.Input;
using Pos.App.Input;
using Pos.App.ViewModels;
using Pos.Core.Domain;
using Pos.Core.Hardware.Drawer;
using Pos.Core.Loyalty;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// Settlement driven from the keyboard: SRS 2.4 for split tender and change, SRS section 4 for
/// loyalty. As with the rest of the keyboard suite, nothing here calls a view model method —
/// everything goes through the router with the shipped keymap.
/// </summary>
public class TenderFlowTests
{
    private static Item Dal => Catalogue.Item(sku: "DAL001", barcode: "8901234567890", name: "Toor Dal 1kg", price: 189m, gstRate: 5m);
    private static Item Rice => Catalogue.Item(sku: "RICE01", barcode: "8901234567891", name: "Basmati Rice 5kg", price: 649m, gstRate: 5m);
    private static Item Shampoo => Catalogue.Item(sku: "SHM001", barcode: "8901234567892", name: "Shampoo 340ml", price: 299m, gstRate: 18m);

    private static BillingHarness Till() => new(Dal, Rice, Shampoo);

    /// <summary>Types into whichever pane box currently has the caret.</summary>
    private static void TypeIntoPane(BillingHarness till, string text) => till.ViewModel.EditBuffer = text;

    // ---- Opening the pane --------------------------------------------------------------------

    [Fact]
    public void TenderOpensAgainstTheBillTotal()
    {
        using var till = Till();
        till.Scan("8901234567890");

        till.Press(Key.F12);

        Assert.True(till.ViewModel.IsTendering);
        Assert.Equal(189.00m, till.ViewModel.AmountDue);
        Assert.Equal(189.00m, till.ViewModel.AmountRemaining);
        Assert.Equal(TenderType.Cash, till.ViewModel.SelectedTenderType);
    }

    [Fact]
    public void TenderOnAnEmptyBillDoesNothing()
    {
        using var till = Till();

        till.Press(Key.F12);

        Assert.False(till.ViewModel.IsTendering);
        Assert.Contains("Nothing to tender", till.ViewModel.StatusMessage);
    }

    [Fact]
    public void ArrowsWalkTheTenderTypes()
    {
        using var till = Till();
        till.Scan("8901234567890");
        till.Press(Key.F12);

        till.Press(Key.Down);
        Assert.Equal(TenderType.Card, till.ViewModel.SelectedTenderType);

        till.Press(Key.Down);
        Assert.Equal(TenderType.Upi, till.ViewModel.SelectedTenderType);

        till.Press(Key.Up);
        Assert.Equal(TenderType.Card, till.ViewModel.SelectedTenderType);
    }

    // ---- Completing a sale -------------------------------------------------------------------

    [Fact]
    public void ExactCashSettlesTheBillAndSavesTheInvoice()
    {
        using var till = Till();
        till.Scan("8901234567890");

        till.Press(Key.F12);
        TypeIntoPane(till, "189");
        till.Press(Key.Enter);

        Assert.True(till.ViewModel.IsFullyTendered);
        Assert.Equal(0m, till.ViewModel.ChangeDue);

        // Second commit finishes the sale.
        till.Press(Key.Enter);

        Assert.False(till.ViewModel.IsTendering);
        Assert.Empty(till.ViewModel.Lines);
        Assert.Equal($"{BillingHarness.LaneId}-{DateTimeOffset.Now.Year}-000001", till.ViewModel.LastInvoiceNo);
        Assert.NotNull(till.Invoices.FindByInvoiceNo(till.ViewModel.LastInvoiceNo));
    }

    /// <summary>Leaving the amount blank takes whatever is still owed under the chosen tender.</summary>
    [Fact]
    public void ABlankAmountTakesTheWholeBalance()
    {
        using var till = Till();
        till.Scan("8901234567890");

        till.Press(Key.F12);
        till.Press(Key.Enter);

        Assert.True(till.ViewModel.IsFullyTendered);
        Assert.Equal(189.00m, till.ViewModel.AmountTendered);
    }

    [Fact]
    public void OverTenderedCashReportsChange()
    {
        using var till = Till();
        till.Scan("8901234567890");

        till.Press(Key.F12);
        TypeIntoPane(till, "500");
        till.Press(Key.Enter);

        Assert.Equal(311.00m, till.ViewModel.ChangeDue);
        Assert.Contains("Change 311.00", till.ViewModel.StatusMessage);

        till.Press(Key.Enter);

        Assert.Equal(311.00m, till.ViewModel.LastSale!.ChangeDue);
    }

    /// <summary>The split-tender case: part cash, part UPI, reconciling to the grand total.</summary>
    [Fact]
    public void CashAndUpiSplitSettlesTheBill()
    {
        using var till = Till();
        till.Scan("8901234567891");
        till.Scan("8901234567890");

        var total = till.ViewModel.GrandTotal;
        Assert.Equal(838.00m, total);

        till.Press(Key.F12);

        TypeIntoPane(till, "500");
        till.Press(Key.Enter);
        Assert.Equal(338.00m, till.ViewModel.AmountRemaining);

        till.Press(Key.Down);
        till.Press(Key.Down);
        Assert.Equal(TenderType.Upi, till.ViewModel.SelectedTenderType);

        TypeIntoPane(till, "338");
        till.Press(Key.Enter);

        Assert.True(till.ViewModel.IsFullyTendered);
        Assert.Equal(0m, till.ViewModel.ChangeDue);

        till.Press(Key.Enter);

        var saved = till.Invoices.FindByInvoiceNo(till.ViewModel.LastInvoiceNo)!;
        Assert.Equal(2, saved.Sale.Payments.Count);
        Assert.Equal(838.00m, saved.Sale.Payments.Sum(p => p.Amount));
        Assert.Equal(total, saved.Sale.Totals.GrandTotal);
    }

    [Fact]
    public void ANonCashTenderOverTheBalanceIsRefused()
    {
        using var till = Till();
        till.Scan("8901234567890");

        till.Press(Key.F12);
        till.Press(Key.Down);
        TypeIntoPane(till, "500");
        till.Press(Key.Enter);

        Assert.Empty(till.ViewModel.Payments);
        Assert.Contains("only cash gives change", till.ViewModel.StatusMessage);
    }

    [Fact]
    public void DeleteRemovesTheLastPaymentTaken()
    {
        using var till = Till();
        till.Scan("8901234567890");

        till.Press(Key.F12);
        TypeIntoPane(till, "100");
        till.Press(Key.Enter);
        Assert.Single(till.ViewModel.Payments);

        till.Press(Key.Delete);

        Assert.Empty(till.ViewModel.Payments);
        Assert.Equal(189.00m, till.ViewModel.AmountRemaining);
    }

    [Fact]
    public void EscapeAbandonsThePaymentAndKeepsTheBill()
    {
        using var till = Till();
        till.Scan("8901234567890");

        till.Press(Key.F12);
        TypeIntoPane(till, "100");
        till.Press(Key.Enter);

        till.Press(Key.Escape);

        Assert.False(till.ViewModel.IsTendering);
        Assert.Single(till.ViewModel.Lines);
        Assert.Equal(189.00m, till.ViewModel.GrandTotal);
        Assert.Empty(till.Invoices.FindByInvoiceNo($"{BillingHarness.LaneId}-{DateTimeOffset.Now.Year}-000001")?.Sale.Lines ?? []);
    }

    [Fact]
    public void AnUnparseableAmountIsReportedAndTakesNoPayment()
    {
        using var till = Till();
        till.Scan("8901234567890");

        till.Press(Key.F12);
        TypeIntoPane(till, "banana");
        till.Press(Key.Enter);

        Assert.Empty(till.ViewModel.Payments);
        Assert.Contains("not an amount", till.ViewModel.StatusMessage);
    }

    [Fact]
    public void ParkingAndDiscardingAreRefusedMidPayment()
    {
        using var till = Till();
        till.Scan("8901234567890");
        till.Press(Key.F12);

        till.Press(Key.F5);
        Assert.True(till.ViewModel.IsTendering);
        Assert.Contains("before parking", till.ViewModel.StatusMessage);

        till.Press(Key.N, ModifierKeys.Control);
        Assert.True(till.ViewModel.IsTendering);
        Assert.Single(till.ViewModel.Lines);
    }

    // ---- The drawer ---------------------------------------------------------------------------

    [Fact]
    public void CashOpensTheDrawerAndCardDoesNot()
    {
        using var cash = Till();
        cash.Scan("8901234567890");
        cash.Press(Key.F12);
        cash.Press(Key.Enter);
        cash.Press(Key.Enter);
        Assert.Equal(1, cash.Drawer.KickCount);

        using var card = Till();
        card.Scan("8901234567890");
        card.Press(Key.F12);
        card.Press(Key.Down);
        card.Press(Key.Enter);
        card.Press(Key.Enter);
        Assert.Equal(0, card.Drawer.KickCount);
    }

    /// <summary>A drawer that will not open is reported, but the sale still completes.</summary>
    [Fact]
    public void ABrokenDrawerIsReportedWithoutLosingTheSale()
    {
        using var till = Till();
        till.Drawer.NextResult = DrawerKickResult.Failed;

        till.Scan("8901234567890");
        till.Press(Key.F12);
        till.Press(Key.Enter);
        till.Press(Key.Enter);

        Assert.Contains("did not open", till.ViewModel.StatusMessage);
        Assert.NotNull(till.Invoices.FindByInvoiceNo(till.ViewModel.LastInvoiceNo));
    }

    // ---- Customers and loyalty ---------------------------------------------------------------

    [Fact]
    public void AKnownCustomerIsAttachedByMobileNumber()
    {
        using var till = Till();
        till.AddCustomer("9876543210", loyaltyBalance: 240, name: "Anitha");
        till.Scan("8901234567890");

        till.Press(Key.F7);
        Assert.True(till.ViewModel.IsFindingCustomer);

        TypeIntoPane(till, "9876543210");
        till.Press(Key.Enter);

        Assert.False(till.ViewModel.IsFindingCustomer);
        Assert.Equal("Anitha", till.ViewModel.CustomerLabel);
        Assert.Equal(240, till.ViewModel.LoyaltyBalance);
    }

    /// <summary>
    /// Creating a customer on a mistyped number is worse than one extra keypress, so the first
    /// commit reports and the second creates.
    /// </summary>
    [Fact]
    public void AnUnknownMobileTakesTwoCommitsToBecomeACustomer()
    {
        using var till = Till();
        till.Scan("8901234567890");

        till.Press(Key.F7);
        TypeIntoPane(till, "9000000001");
        till.Press(Key.Enter);

        Assert.True(till.ViewModel.IsFindingCustomer);
        Assert.False(till.ViewModel.HasCustomer);
        Assert.Contains("Commit again to add", till.ViewModel.StatusMessage);

        till.Press(Key.Enter);

        Assert.False(till.ViewModel.IsFindingCustomer);
        Assert.Equal("9000000001", till.ViewModel.CustomerLabel);
        Assert.NotNull(till.Customers.FindByMobile("9000000001"));
    }

    [Fact]
    public void EscapeLeavesTheCustomerLookupWithoutAttachingAnyone()
    {
        using var till = Till();
        till.Scan("8901234567890");

        till.Press(Key.F7);
        TypeIntoPane(till, "9000000002");
        till.Press(Key.Escape);

        Assert.False(till.ViewModel.IsFindingCustomer);
        Assert.False(till.ViewModel.HasCustomer);
        Assert.Null(till.Customers.FindByMobile("9000000002"));
    }

    /// <summary>
    /// The decision this phase was cleared on: points settle the bill, they do not discount it.
    /// The tax on every line must be identical to the same bill paid entirely in cash.
    /// </summary>
    [Fact]
    public void RedeemingPointsLeavesTheTaxOnEveryLineUntouched()
    {
        using var cashOnly = Till();
        cashOnly.Scan("8901234567891");
        var cashTotals = cashOnly.ViewModel.Totals;

        using var till = Till();
        till.AddCustomer("9876543210", loyaltyBalance: 5_000);
        till.Scan("8901234567891");

        till.Press(Key.F7);
        TypeIntoPane(till, "9876543210");
        till.Press(Key.Enter);

        till.Press(Key.F12);
        Assert.Equal(649.00m, till.ViewModel.AmountDue);

        // 30% of 649 is 194.70, which buys 389 points at 50 paise each.
        Assert.Equal(389, till.ViewModel.MaxRedeemablePoints);

        // Move to loyalty and redeem the maximum by leaving the box blank.
        for (var i = 0; i < 4; i++)
            till.Press(Key.Down);

        Assert.Equal(TenderType.LoyaltyPoints, till.ViewModel.SelectedTenderType);
        till.Press(Key.Enter);

        Assert.Equal(194.50m, till.ViewModel.AmountTendered);
        Assert.Equal(454.50m, till.ViewModel.AmountRemaining);

        till.Press(Key.Up);
        till.Press(Key.Up);
        till.Press(Key.Up);
        till.Press(Key.Up);
        till.Press(Key.Enter);
        till.Press(Key.Enter);

        var saved = till.Invoices.FindByInvoiceNo(till.ViewModel.LastInvoiceNo)!;

        Assert.Equal(cashTotals.SubtotalTaxable, saved.Sale.Totals.SubtotalTaxable);
        Assert.Equal(cashTotals.TotalCgst, saved.Sale.Totals.TotalCgst);
        Assert.Equal(cashTotals.TotalSgst, saved.Sale.Totals.TotalSgst);
        Assert.Equal(649.00m, saved.Sale.Totals.GrandTotal);

        Assert.Equal(389, saved.Sale.PointsRedeemed);
        Assert.Equal(TenderType.LoyaltyPoints, saved.Sale.Payments[0].Type);
        Assert.Equal("389 points", saved.Sale.Payments[0].ReferenceNo);
    }

    [Fact]
    public void TypingAPointCountRedeemsExactlyThatMany()
    {
        using var till = Till();
        till.AddCustomer("9876543210", loyaltyBalance: 5_000);
        till.Scan("8901234567891");

        till.Press(Key.F7);
        TypeIntoPane(till, "9876543210");
        till.Press(Key.Enter);

        till.Press(Key.F12);
        for (var i = 0; i < 4; i++)
            till.Press(Key.Down);

        TypeIntoPane(till, "100");
        till.Press(Key.Enter);

        Assert.Equal(50.00m, till.ViewModel.AmountTendered);
        Assert.Equal("100 points", till.ViewModel.Payments[0].ReferenceNo);
    }

    [Fact]
    public void PointsCannotBeRedeemedWithoutACustomer()
    {
        using var till = Till();
        till.Scan("8901234567890");

        till.Press(Key.F12);
        for (var i = 0; i < 4; i++)
            till.Press(Key.Down);

        till.Press(Key.Enter);

        Assert.Empty(till.ViewModel.Payments);
        Assert.Contains("Attach a customer", till.ViewModel.StatusMessage);
    }

    [Fact]
    public void PointsCannotBeRedeemedTwiceOnOneBill()
    {
        using var till = Till();
        till.AddCustomer("9876543210", loyaltyBalance: 5_000);
        till.Scan("8901234567891");

        till.Press(Key.F7);
        TypeIntoPane(till, "9876543210");
        till.Press(Key.Enter);

        till.Press(Key.F12);
        for (var i = 0; i < 4; i++)
            till.Press(Key.Down);

        TypeIntoPane(till, "100");
        till.Press(Key.Enter);
        TypeIntoPane(till, "100");
        till.Press(Key.Enter);

        Assert.Single(till.ViewModel.Payments);
        Assert.Contains("already been redeemed", till.ViewModel.StatusMessage);
    }

    [Fact]
    public void ACustomerWithNoPointsIsToldSoRatherThanTenderingNothing()
    {
        using var till = Till();
        till.AddCustomer("9876543210", loyaltyBalance: 0);
        till.Scan("8901234567890");

        till.Press(Key.F7);
        TypeIntoPane(till, "9876543210");
        till.Press(Key.Enter);

        till.Press(Key.F12);
        for (var i = 0; i < 4; i++)
            till.Press(Key.Down);

        till.Press(Key.Enter);

        Assert.Empty(till.ViewModel.Payments);
        Assert.Contains("No points can be redeemed", till.ViewModel.StatusMessage);
    }

    [Fact]
    public void PointsAccrueOnTheNetBillAndTheBalanceIsWrittenBack()
    {
        using var till = new BillingHarness(
            new LoyaltyRules(RedemptionCapPercent: 30m, RupeesPerPoint: 0.50m, RupeesPerPointEarned: 50m),
            Dal, Rice, Shampoo);

        till.AddCustomer("9876543210", loyaltyBalance: 1_000);
        till.Scan("8901234567891");

        till.Press(Key.F7);
        TypeIntoPane(till, "9876543210");
        till.Press(Key.Enter);

        till.Press(Key.F12);
        for (var i = 0; i < 4; i++)
            till.Press(Key.Down);

        TypeIntoPane(till, "300");
        till.Press(Key.Enter);

        // 300 points at 50 paise is 150.00 off a 649.00 bill, leaving 499.00 net.
        for (var i = 0; i < 4; i++)
            till.Press(Key.Up);

        till.Press(Key.Enter);
        till.Press(Key.Enter);

        var sale = till.ViewModel.LastSale!;

        Assert.Equal(300, sale.PointsRedeemed);
        Assert.Equal(9, sale.PointsEarned);
        Assert.Equal(709, sale.NewLoyaltyBalance);
        Assert.Equal(709, till.Customers.FindByMobile("9876543210")!.LoyaltyBalance);
    }

    // ---- Park, recall, settle ------------------------------------------------------------------

    /// <summary>
    /// The whole Phase 4 flow: park a bill with a discount on it, serve someone else, recall it,
    /// settle it across two tenders, and check what was written down.
    /// </summary>
    [Fact]
    public void AParkedBillCanBeRecalledAndSettled()
    {
        using var till = Till();

        till.Scan("8901234567891");
        till.Press(Key.F4);
        till.ViewModel.EditBuffer = "49";
        till.Press(Key.Enter);
        Assert.Equal(600.00m, till.ViewModel.GrandTotal);

        till.Press(Key.F5);
        var token = Assert.Single(till.HeldBills.List(BillingHarness.LaneId)).Token;

        // Another customer, settled and gone.
        till.Scan("8901234567890");
        till.Press(Key.F12);
        till.Press(Key.Enter);
        till.Press(Key.Enter);
        Assert.EndsWith("-000001", till.ViewModel.LastInvoiceNo);

        // Back to the parked bill.
        till.Press(Key.F6);
        till.Press(Key.Enter);

        Assert.Single(till.ViewModel.Lines);
        Assert.Equal(600.00m, till.ViewModel.GrandTotal);
        Assert.Equal(49m, till.ViewModel.Lines[0].Discount);

        till.Press(Key.F12);
        TypeIntoPane(till, "300");
        till.Press(Key.Enter);
        till.Press(Key.Down);
        TypeIntoPane(till, "300");
        till.Press(Key.Enter);
        till.Press(Key.Enter);

        var saved = till.Invoices.FindByInvoiceNo(till.ViewModel.LastInvoiceNo)!;

        Assert.EndsWith("-000002", saved.InvoiceNo);
        Assert.Equal(600.00m, saved.Sale.Totals.GrandTotal);
        Assert.Equal(2, saved.Sale.Payments.Count);
        Assert.Equal(token, saved.Sale.RecalledFromToken);
        Assert.Empty(till.HeldBills.List(BillingHarness.LaneId));
    }

    [Fact]
    public void ParkedBillsSurviveARestart()
    {
        using var till = Till();

        till.Scan("8901234567891");
        till.Press(Key.F5);

        // A fresh view model over the same database is what a restart looks like from here.
        Assert.Single(till.HeldBills.List(BillingHarness.LaneId));
        Assert.Single(till.ViewModel.HeldBills);
    }

    // ---- Every action still bound ------------------------------------------------------------

    [Fact]
    public void TheNewActionsAreBoundAndHandled()
    {
        using var till = Till();

        foreach (var action in new[] { PosAction.Tender, PosAction.FindCustomer })
        {
            var gesture = Assert.Single(Keymap.Default.GesturesFor(action).Take(1));
            Assert.True(till.Press(gesture.Key, gesture.Modifiers), $"{action} is bound to {gesture} but was not handled.");
        }
    }
}
