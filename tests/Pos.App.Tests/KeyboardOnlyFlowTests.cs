using System.Windows.Input;
using Pos.App.Input;
using Pos.App.ViewModels;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// The Phase 2 gate: every core action — search, navigate the grid, edit quantity and discount,
/// hold and recall, delete a line — reachable from the keyboard alone (SRS UR-03).
/// </summary>
/// <remarks>
/// Every one of these drives the till through <see cref="KeyboardRouter"/> with the shipped
/// keymap. Nothing calls a view model method directly, so a regression that leaves an action
/// working but unbound still fails here.
/// </remarks>
public class KeyboardOnlyFlowTests
{
    private static Item Dal => Catalogue.Item(sku: "DAL001", barcode: "8901234567890", name: "Toor Dal 1kg", price: 100m, gstRate: 5m);
    private static Item Rice => Catalogue.Item(sku: "RICE01", barcode: "8901234567891", name: "Basmati Rice 5kg", price: 249m, gstRate: 5m);
    private static Item Oil => Catalogue.Item(sku: "OIL001", barcode: "8901234567892", name: "Sunflower Oil 1L", price: 118m, gstRate: 18m);
    private static Item Sugar => Catalogue.Item(sku: "SUG001", barcode: "8901234567893", name: "Sugar Loose", price: 45m, gstRate: 5m, unit: UnitType.Kilogram);

    private static BillingHarness Till() => new(Dal, Rice, Oil, Sugar);

    // ---- Scanning ----------------------------------------------------------------------------

    [Fact]
    public void ScanningABarcodeAddsTheLineAndClearsTheBox()
    {
        using var till = Till();

        till.Scan("8901234567890");

        Assert.Single(till.ViewModel.Lines);
        Assert.Equal("Toor Dal 1kg", till.ViewModel.Lines[0].Name);
        Assert.Equal(string.Empty, till.ViewModel.SearchText);
        Assert.Equal(100.00m, till.ViewModel.GrandTotal);
    }

    [Fact]
    public void ScanningSeveralItemsBuildsTheBill()
    {
        using var till = Till();

        till.Scan("8901234567890");
        till.Scan("8901234567891");
        till.Scan("8901234567892");

        Assert.Equal(3, till.ViewModel.Lines.Count);
        Assert.Equal(467.00m, till.ViewModel.GrandTotal);
    }

    /// <summary>
    /// Classification is a timing heuristic. A burst that looks like a scan but matches no barcode
    /// must fall through to the ordinary search rather than being dropped.
    /// </summary>
    [Fact]
    public void ABurstThatMatchesNoBarcodeFallsBackToSearch()
    {
        using var till = Till();

        till.Scan("Basmati");

        Assert.Single(till.ViewModel.Lines);
        Assert.Equal("Basmati Rice 5kg", till.ViewModel.Lines[0].Name);
    }

    [Fact]
    public void ScanningSomethingUnknownLeavesTheBillAloneAndSaysSo()
    {
        using var till = Till();

        till.Scan("8909999999999");

        Assert.Empty(till.ViewModel.Lines);
        Assert.Contains("No item matches", till.ViewModel.StatusMessage);
    }

    // ---- Typed search ------------------------------------------------------------------------

    [Fact]
    public void TypedSearchDoesNotQueryUntilTheCashierPauses()
    {
        using var till = Till();

        till.Type("Rice", BillingHarness.HumanGapMs);

        Assert.Empty(till.ViewModel.SearchResults);

        till.Scheduler.Advance(BillingHarness.DebounceMs);

        Assert.Single(till.ViewModel.SearchResults);
    }

    [Fact]
    public void AUniqueTypedMatchIsAddedOnCommit()
    {
        using var till = Till();

        till.TypeAndWait("Basmati");
        till.Press(Key.Enter);

        Assert.Single(till.ViewModel.Lines);
        Assert.Equal("Basmati Rice 5kg", till.ViewModel.Lines[0].Name);
    }

    [Fact]
    public void ArrowsChooseFromSeveralMatchesAndCommitAddsTheHighlightedOne()
    {
        using var till = Till();

        // Three items carry a number in the name; "1" matches Dal 1kg, Oil 1L and Rice 5kg's
        // barcode-free name is excluded, so use a term that genuinely matches more than one.
        till.TypeAndWait("o");

        Assert.True(till.ViewModel.SearchResults.Count > 1);

        var second = till.ViewModel.SearchResults[1].Name;
        till.Press(Key.Down);
        till.Press(Key.Enter);

        Assert.Single(till.ViewModel.Lines);
        Assert.Equal(second, till.ViewModel.Lines[0].Name);
    }

    [Fact]
    public void EscapeClearsTheSearchBox()
    {
        using var till = Till();

        till.TypeAndWait("Rice");
        Assert.NotEmpty(till.ViewModel.SearchResults);

        till.Press(Key.Escape);

        Assert.Empty(till.ViewModel.SearchResults);
        Assert.Equal(string.Empty, till.ViewModel.SearchText);
    }

    // ---- Quantity ----------------------------------------------------------------------------

    [Fact]
    public void PlusAndMinusStepTheSelectedLine()
    {
        using var till = Till();
        till.Scan("8901234567890");

        till.Press(Key.Add);
        till.Press(Key.Add);
        Assert.Equal(3m, till.ViewModel.Lines[0].Quantity);
        Assert.Equal(300.00m, till.ViewModel.GrandTotal);

        till.Press(Key.Subtract);
        Assert.Equal(2m, till.ViewModel.Lines[0].Quantity);
    }

    [Fact]
    public void SteppingBelowOneRemovesTheLine()
    {
        using var till = Till();
        till.Scan("8901234567890");

        till.Press(Key.Subtract);

        Assert.Empty(till.ViewModel.Lines);
        Assert.Equal(0m, till.ViewModel.GrandTotal);
    }

    /// <summary>Nudging loose goods by a whole kilo is never what the cashier meant.</summary>
    [Fact]
    public void WeighedGoodsStepBySmallerAmountsThanPieceGoods()
    {
        using var till = Till();
        till.Scan("8901234567893");

        till.Press(Key.Add);

        Assert.Equal(1.1m, till.ViewModel.Lines[0].Quantity);
    }

    [Fact]
    public void QuantityIsEditedByKeyboard()
    {
        using var till = Till();
        till.Scan("8901234567890");

        till.Press(Key.F3);
        Assert.True(till.ViewModel.IsEditing);
        Assert.Equal(EditableColumn.Quantity, till.ViewModel.EditingColumn);

        till.ViewModel.EditBuffer = "12";
        till.Press(Key.Enter);

        Assert.False(till.ViewModel.IsEditing);
        Assert.Equal(12m, till.ViewModel.Lines[0].Quantity);
        Assert.Equal(1200.00m, till.ViewModel.GrandTotal);
    }

    [Fact]
    public void EscapeAbandonsAnEditAndLeavesTheLineAlone()
    {
        using var till = Till();
        till.Scan("8901234567890");

        till.Press(Key.F3);
        till.ViewModel.EditBuffer = "99";
        till.Press(Key.Escape);

        Assert.False(till.ViewModel.IsEditing);
        Assert.Equal(1m, till.ViewModel.Lines[0].Quantity);
    }

    /// <summary>
    /// A rejected figure must leave the editor open so the cashier can correct it in place, rather
    /// than silently reverting and leaving them wondering what happened.
    /// </summary>
    [Theory]
    [InlineData("banana")]
    [InlineData("0")]
    [InlineData("-3")]
    public void AnUnusableQuantityKeepsTheEditorOpen(string entered)
    {
        using var till = Till();
        till.Scan("8901234567890");

        till.Press(Key.F3);
        till.ViewModel.EditBuffer = entered;
        till.Press(Key.Enter);

        Assert.True(till.ViewModel.IsEditing);
        Assert.NotEqual(string.Empty, till.ViewModel.StatusMessage);
        Assert.Equal(1m, till.ViewModel.Lines[0].Quantity);
    }

    [Fact]
    public void APieceItemRefusesAFractionalQuantity()
    {
        using var till = Till();
        till.Scan("8901234567890");

        till.Press(Key.F3);
        till.ViewModel.EditBuffer = "1.5";
        till.Press(Key.Enter);

        Assert.True(till.ViewModel.IsEditing);
        Assert.Equal(1m, till.ViewModel.Lines[0].Quantity);
    }

    [Fact]
    public void AWeighedItemAcceptsAFractionalQuantity()
    {
        using var till = Till();
        till.Scan("8901234567893");

        till.Press(Key.F3);
        till.ViewModel.EditBuffer = "2.75";
        till.Press(Key.Enter);

        Assert.False(till.ViewModel.IsEditing);
        Assert.Equal(2.75m, till.ViewModel.Lines[0].Quantity);
    }

    // ---- Discount ----------------------------------------------------------------------------

    [Fact]
    public void DiscountIsEditedByKeyboard()
    {
        using var till = Till();
        till.Scan("8901234567891");

        till.Press(Key.F4);
        Assert.Equal(EditableColumn.Discount, till.ViewModel.EditingColumn);

        till.ViewModel.EditBuffer = "49";
        till.Press(Key.Enter);

        Assert.Equal(49m, till.ViewModel.Lines[0].Discount);
        Assert.Equal(200.00m, till.ViewModel.GrandTotal);
    }

    [Fact]
    public void ADiscountLargerThanTheLineIsRefused()
    {
        using var till = Till();
        till.Scan("8901234567890");

        till.Press(Key.F4);
        till.ViewModel.EditBuffer = "500";
        till.Press(Key.Enter);

        Assert.True(till.ViewModel.IsEditing);
        Assert.Equal(0m, till.ViewModel.Lines[0].Discount);
    }

    // ---- Grid navigation and deletion --------------------------------------------------------

    [Fact]
    public void ArrowsWalkTheBillWhenTheSearchBoxIsQuiet()
    {
        using var till = Till();
        till.Scan("8901234567890");
        till.Scan("8901234567891");
        till.Scan("8901234567892");

        Assert.Equal(2, till.ViewModel.SelectedLineIndex);

        till.Press(Key.Up);
        Assert.Equal(1, till.ViewModel.SelectedLineIndex);

        till.Press(Key.Up);
        Assert.Equal(0, till.ViewModel.SelectedLineIndex);

        // Already at the top: stays put rather than going out of range.
        till.Press(Key.Up);
        Assert.Equal(0, till.ViewModel.SelectedLineIndex);

        till.Press(Key.Down);
        Assert.Equal(1, till.ViewModel.SelectedLineIndex);
    }

    [Fact]
    public void DeleteRemovesTheSelectedLine()
    {
        using var till = Till();
        till.Scan("8901234567890");
        till.Scan("8901234567891");

        till.Press(Key.Up);
        till.Press(Key.Delete);

        Assert.Single(till.ViewModel.Lines);
        Assert.Equal("Basmati Rice 5kg", till.ViewModel.Lines[0].Name);
    }

    [Fact]
    public void DeletingTheLastLineLeavesNothingSelected()
    {
        using var till = Till();
        till.Scan("8901234567890");

        till.Press(Key.Delete);

        Assert.Empty(till.ViewModel.Lines);
        Assert.Equal(-1, till.ViewModel.SelectedLineIndex);
        Assert.Null(till.ViewModel.SelectedLine);
    }

    [Fact]
    public void EditingWithNothingSelectedSaysSoRatherThanThrowing()
    {
        using var till = Till();

        till.Press(Key.F3);
        Assert.False(till.ViewModel.IsEditing);
        Assert.Contains("Select a line", till.ViewModel.StatusMessage);

        till.Press(Key.Delete);
        Assert.Contains("Select a line", till.ViewModel.StatusMessage);

        till.Press(Key.Add);
        Assert.Contains("Select a line", till.ViewModel.StatusMessage);
    }

    // ---- Hold and recall ---------------------------------------------------------------------

    [Fact]
    public void HoldingParksTheBillAndClearsTheScreen()
    {
        using var till = Till();
        till.Scan("8901234567890");
        till.Scan("8901234567891");

        till.Press(Key.F5);

        Assert.Empty(till.ViewModel.Lines);
        Assert.Equal(0m, till.ViewModel.GrandTotal);
        Assert.Single(till.ViewModel.HeldBills);
        Assert.Equal(2, till.ViewModel.HeldBills[0].ItemCount);
    }

    /// <summary>
    /// SRS 2.5: recalling restores the exact line state, discounts included.
    /// </summary>
    [Fact]
    public void RecallingRestoresTheExactBill()
    {
        using var till = Till();
        till.Scan("8901234567891");
        till.Press(Key.F4);
        till.ViewModel.EditBuffer = "49";
        till.Press(Key.Enter);

        till.Scan("8901234567893");
        till.Press(Key.F3);
        till.ViewModel.EditBuffer = "2.5";
        till.Press(Key.Enter);

        var parkedTotal = till.ViewModel.GrandTotal;

        till.Press(Key.F5);
        Assert.Empty(till.ViewModel.Lines);

        till.Press(Key.F6);
        Assert.True(till.ViewModel.IsRecalling);

        till.Press(Key.Enter);

        Assert.False(till.ViewModel.IsRecalling);
        Assert.Equal(2, till.ViewModel.Lines.Count);
        Assert.Equal(parkedTotal, till.ViewModel.GrandTotal);
        Assert.Equal(49m, till.ViewModel.Lines[0].Discount);
        Assert.Equal(2.5m, till.ViewModel.Lines[1].Quantity);
        Assert.Empty(till.ViewModel.HeldBills);
    }

    /// <summary>
    /// The recall list puts the most recently parked bill first, since that is the one most likely
    /// to be wanted back, and the arrows walk from there to the older ones.
    /// </summary>
    [Fact]
    public void ArrowsChooseWhichParkedBillToRecall()
    {
        using var till = Till();

        // Parked first: one line at 100.00.
        till.Scan("8901234567890");
        till.Press(Key.F5);

        // Parked second: two lines at 367.00.
        till.Scan("8901234567891");
        till.Scan("8901234567892");
        till.Press(Key.F5);

        Assert.Equal(2, till.ViewModel.HeldBills.Count);

        till.Press(Key.F6);

        Assert.Equal(367.00m, till.ViewModel.HeldBills[0].GrandTotal);
        Assert.Equal(100.00m, till.ViewModel.HeldBills[1].GrandTotal);

        // Down moves off the newest and onto the bill parked before it.
        till.Press(Key.Down);
        till.Press(Key.Enter);

        Assert.Single(till.ViewModel.Lines);
        Assert.Equal(100.00m, till.ViewModel.GrandTotal);

        // The bill not chosen is still parked.
        Assert.Single(till.ViewModel.HeldBills);
        Assert.Equal(367.00m, till.ViewModel.HeldBills[0].GrandTotal);
    }

    [Fact]
    public void CommittingStraightAwayRecallsTheMostRecentlyParkedBill()
    {
        using var till = Till();

        till.Scan("8901234567890");
        till.Press(Key.F5);

        till.Scan("8901234567891");
        till.Scan("8901234567892");
        till.Press(Key.F5);

        till.Press(Key.F6);
        till.Press(Key.Enter);

        Assert.Equal(2, till.ViewModel.Lines.Count);
        Assert.Equal(367.00m, till.ViewModel.GrandTotal);
    }

    [Fact]
    public void RecallIsAbandonedWithEscape()
    {
        using var till = Till();
        till.Scan("8901234567890");
        till.Press(Key.F5);

        till.Press(Key.F6);
        Assert.True(till.ViewModel.IsRecalling);

        till.Press(Key.Escape);

        Assert.False(till.ViewModel.IsRecalling);
        Assert.Single(till.ViewModel.HeldBills);
    }

    [Fact]
    public void RecallingOntoAnUnfinishedBillIsRefused()
    {
        using var till = Till();
        till.Scan("8901234567890");
        till.Press(Key.F5);

        till.Scan("8901234567891");
        till.Press(Key.F6);
        till.Press(Key.Enter);

        Assert.Single(till.ViewModel.Lines);
        Assert.Contains("Park or discard", till.ViewModel.StatusMessage);
        Assert.Single(till.ViewModel.HeldBills);
    }

    [Fact]
    public void HoldingAnEmptyBillDoesNothing()
    {
        using var till = Till();

        till.Press(Key.F5);

        Assert.Empty(till.ViewModel.HeldBills);
        Assert.Contains("Nothing to hold", till.ViewModel.StatusMessage);
    }

    [Fact]
    public void RecallWithNothingParkedSaysSo()
    {
        using var till = Till();

        till.Press(Key.F6);

        Assert.False(till.ViewModel.IsRecalling);
        Assert.Contains("No parked bills", till.ViewModel.StatusMessage);
    }

    // ---- New bill ----------------------------------------------------------------------------

    /// <summary>One stray keypress must not throw away a sale in progress.</summary>
    [Fact]
    public void DiscardingABillTakesTwoPresses()
    {
        using var till = Till();
        till.Scan("8901234567890");

        till.Press(Key.N, ModifierKeys.Control);
        Assert.Single(till.ViewModel.Lines);
        Assert.Contains("again", till.ViewModel.StatusMessage);

        till.Press(Key.N, ModifierKeys.Control);
        Assert.Empty(till.ViewModel.Lines);
    }

    [Fact]
    public void AnyOtherActionCancelsAPendingDiscard()
    {
        using var till = Till();
        till.Scan("8901234567890");

        till.Press(Key.N, ModifierKeys.Control);
        till.Press(Key.Down);
        till.Press(Key.N, ModifierKeys.Control);

        Assert.Single(till.ViewModel.Lines);
    }

    // ---- Coverage of the keymap itself -------------------------------------------------------

    [Fact]
    public void EveryActionHasAGestureAndTheRouterHandlesIt()
    {
        using var till = Till();

        foreach (var action in Enum.GetValues<PosAction>())
        {
            var gesture = Assert.Single(Keymap.Default.GesturesFor(action).Take(1));
            Assert.True(
                till.Press(gesture.Key, gesture.Modifiers),
                $"{action} is bound to {gesture} but the router did not handle it.");
        }
    }

    [Fact]
    public void AnUnboundKeyIsLeftForOrdinaryTyping()
    {
        using var till = Till();

        Assert.False(till.Press(Key.A));
        Assert.False(till.Press(Key.F11));
    }

    // ---- The whole checkout ------------------------------------------------------------------

    /// <summary>
    /// A complete transaction end to end, keyboard only: scan, search, adjust, discount, park,
    /// serve someone else, recall, correct, and check the GST totals reconcile.
    /// </summary>
    [Fact]
    public void ACompleteCheckoutRunsFromTheKeyboardAlone()
    {
        using var till = Till();
        var vm = till.ViewModel;

        // Customer one: scan two items, take three of the first.
        till.Scan("8901234567890");
        till.Press(Key.Add);
        till.Press(Key.Add);
        till.Scan("8901234567891");

        Assert.Equal(2, vm.Lines.Count);
        Assert.Equal(549.00m, vm.GrandTotal);

        // They have forgotten their wallet: park the bill.
        till.Press(Key.F5);
        Assert.Empty(vm.Lines);

        // Customer two: find an item by typing, discount it, then remove it entirely.
        till.TypeAndWait("Sunflower");
        till.Press(Key.Enter);
        Assert.Single(vm.Lines);

        till.Press(Key.F4);
        vm.EditBuffer = "18";
        till.Press(Key.Enter);
        Assert.Equal(100.00m, vm.GrandTotal);

        till.Press(Key.Delete);
        Assert.Empty(vm.Lines);

        // Customer one is back: recall and finish the bill.
        till.Press(Key.F6);
        till.Press(Key.Enter);

        Assert.Equal(2, vm.Lines.Count);
        Assert.Equal(549.00m, vm.GrandTotal);

        // Correct the quantity, then check the totals still reconcile line by line.
        till.Press(Key.Up);
        till.Press(Key.F3);
        vm.EditBuffer = "2";
        till.Press(Key.Enter);

        var totals = vm.Totals;
        Assert.Equal(449.00m, totals.GrandTotal);
        Assert.Equal(totals.GrandTotal, vm.Lines.Sum(line => line.LineTotal));
        Assert.Equal(totals.TotalCgst, totals.TotalSgst);
        Assert.Equal(0m, totals.TotalIgst);
        Assert.Equal(totals.SubtotalTaxable + totals.TotalTax, totals.GrandTotal);
    }
}
