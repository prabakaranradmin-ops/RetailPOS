using System.Windows.Input;
using Pos.App.ViewModels;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// The float, expenses and cash in and out, at the counter with Ctrl+M - driven by keystrokes.
/// </summary>
public class DrawerTillTests
{
    private static BillingHarness Till() => new(
        Catalogue.Item(sku: "DAL001", barcode: "8901234567890", name: "Toor Dal 1kg", price: 189m));

    private static void Open(BillingHarness till)
    {
        Assert.True(till.Press(Key.M, ModifierKeys.Control));
        Assert.True(till.ViewModel.IsUsingDrawer);
    }

    private static void Type(BillingHarness till, string text)
    {
        till.ViewModel.EditBuffer = text;
        till.Press(Key.Enter);
    }

    private static decimal DrawerExpected(BillingHarness till) =>
        till.DayCloses.Preview(BillingHarness.LaneId, DateTimeOffset.Now).CashExpected;

    [Fact]
    public void TheMorningOpensOnTheFloat()
    {
        using var till = Till();
        var kicks = till.Drawer.KickCount;

        Open(till);
        Assert.Equal(DrawerEntryKind.OpeningFloat, till.ViewModel.DrawerKindChoices[till.ViewModel.SelectedDrawerKindIndex]);

        Type(till, "2000");

        Assert.False(till.ViewModel.IsUsingDrawer);
        Assert.Contains("Float of ₹2,000.00 recorded", till.ViewModel.StatusMessage);
        Assert.Equal(kicks + 1, till.Drawer.KickCount);
        Assert.Equal(2_000.00m, DrawerExpected(till));
    }

    /// <summary>Once the float is in, the pane opens on an expense - the commonest thing after it.</summary>
    [Fact]
    public void AnExpenseFromTheDrawerIsPickedFromTheList()
    {
        using var till = Till();
        Open(till);
        Type(till, "2000");

        Open(till);
        Assert.Equal(DrawerEntryKind.ExpenseFromDrawer, till.ViewModel.DrawerKindChoices[till.ViewModel.SelectedDrawerKindIndex]);

        Type(till, "120");
        Assert.Equal(DrawerStage.Category, till.ViewModel.DrawerStage);

        till.Press(Key.Down);
        Type(till, "auto from the market");

        Assert.Contains("Transport and delivery: ₹120.00 paid from the drawer", till.ViewModel.StatusMessage);
        Assert.Equal(1_880.00m, DrawerExpected(till));

        var expense = Assert.Single(till.CashDrawer.Expenses(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddMinutes(1)));
        Assert.Equal("auto from the market", expense.Note);
    }

    [Fact]
    public void AnExpensePaidFromOutsideLeavesTheDrawerAndItsFigureAlone()
    {
        using var till = Till();
        var kicks = till.Drawer.KickCount;

        Open(till);
        till.Press(Key.Down);
        till.Press(Key.Down);
        Type(till, "2400");

        for (var i = 0; i < 3; i++)
            till.Press(Key.Down);

        Type(till, "");

        Assert.Contains("Electricity: ₹2,400.00 recorded, paid from outside the till", till.ViewModel.StatusMessage);
        Assert.Equal(kicks, till.Drawer.KickCount);
        Assert.Equal(0m, DrawerExpected(till));
    }

    [Fact]
    public void CashTakenOutMustSayWhereItWent()
    {
        using var till = Till();
        Open(till);
        Type(till, "3000");

        Open(till);
        for (var i = 0; i < 4; i++)
            till.Press(Key.Down);

        Type(till, "1000");
        Assert.Equal(DrawerStage.Note, till.ViewModel.DrawerStage);

        Type(till, "");
        Assert.True(till.ViewModel.IsUsingDrawer);
        Assert.Contains("where the cash is going", till.ViewModel.StatusMessage);

        Type(till, "to the bank");
        Assert.Contains("1,000.00 taken out of the drawer", till.ViewModel.StatusMessage);
        Assert.Equal(2_000.00m, DrawerExpected(till));
    }

    [Fact]
    public void CashPutInIsAddedToTheDrawer()
    {
        using var till = Till();
        Open(till);
        till.Press(Key.Down);
        till.Press(Key.Down);
        till.Press(Key.Down);

        Assert.Equal(DrawerEntryKind.CashIn, till.ViewModel.DrawerKindChoices[till.ViewModel.SelectedDrawerKindIndex]);

        Type(till, "500");

        Assert.Equal(500.00m, DrawerExpected(till));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    public void AnAmountThatIsNotOneIsRefusedInThePane(string typed)
    {
        using var till = Till();
        Open(till);

        Type(till, typed);

        Assert.True(till.ViewModel.IsUsingDrawer);
        Assert.Equal(DrawerStage.KindAndAmount, till.ViewModel.DrawerStage);
        Assert.Equal(0m, DrawerExpected(till));
    }

    [Fact]
    public void EscapeBacksOutWithoutRecordingAnything()
    {
        using var till = Till();
        Open(till);
        till.Press(Key.Down);
        Type(till, "75");
        Assert.Equal(DrawerStage.Category, till.ViewModel.DrawerStage);

        till.Press(Key.Escape);
        Assert.Equal(DrawerStage.KindAndAmount, till.ViewModel.DrawerStage);
        Assert.Equal("75", till.ViewModel.EditBuffer);

        till.Press(Key.Escape);
        Assert.False(till.ViewModel.IsUsingDrawer);
        Assert.Equal("Nothing recorded.", till.ViewModel.StatusMessage);
        Assert.Empty(till.CashDrawer.Expenses(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddMinutes(1)));
    }

    [Fact]
    public void CashWaitsForTheBillOnScreen()
    {
        using var till = Till();
        till.Scan("8901234567890");

        till.Press(Key.M, ModifierKeys.Control);

        Assert.False(till.ViewModel.IsUsingDrawer);
        Assert.Contains("not part of a sale", till.ViewModel.StatusMessage);
    }

    /// <summary>The drawer figure the close previews is the float plus the takings, less what was paid out.</summary>
    [Fact]
    public void TheClosePreviewCountsTheFloatAndTheExpenses()
    {
        using var till = Till();
        Open(till);
        Type(till, "1000");

        till.Scan("8901234567890");
        till.Press(Key.F12);
        till.Press(Key.Enter);
        till.Press(Key.Enter);

        Open(till);
        Type(till, "40");
        Type(till, "");

        till.Press(Key.F12, ModifierKeys.Shift);

        // The drawer figure is not shown until the drawer is counted.
        Assert.DoesNotContain("1,149", till.ViewModel.StatusMessage);
        Assert.Contains(new DayCloseRow("Paid out of the drawer", "₹40.00"), till.ViewModel.DayCloseRows);

        Type(till, "1149");

        // Counted, then shown: the expense paid out, the drawer figure, and the drawer exactly right.
        Assert.Contains("exactly right", till.ViewModel.StatusMessage);
        Assert.Contains(new DayCloseRow("Cash expected in the drawer", "₹1,149.00"), till.ViewModel.DayCloseRows);
        Assert.Equal(new DayCloseRow("The drawer is", "exactly right", Emphasis: true), till.ViewModel.DayCloseRows[^1]);
    }

    /// <summary>The cash pane's title follows the step, so it says what it is asking.</summary>
    [Fact]
    public void TheCashPaneSaysWhatItIsAskingForNow()
    {
        using var till = Till();
        Open(till);
        Assert.Equal("Cash in and out", till.ViewModel.DrawerTitle);

        till.Press(Key.Down);
        Type(till, "50");

        Assert.Equal("Expense of ₹50.00: what for?", till.ViewModel.DrawerTitle);
    }
}
