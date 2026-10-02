using System.Windows.Input;
using Pos.App.ViewModels;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// Counting the drawer at the close: typed before the till says what it expects, then the
/// difference, then the count kept with the close and printed on the report.
/// </summary>
public class CountAtCloseTests
{
    private const string Dal = "8901234567890";

    private static BillingHarness Till()
    {
        var till = new BillingHarness(Catalogue.Item(sku: "DAL001", barcode: Dal, name: "Toor Dal 1kg", price: 189m));

        till.Press(Key.U, ModifierKeys.Control);
        Type(till, "Murugan");

        return till;
    }

    /// <summary>One dal, paid in cash: ₹189.00 expected in the drawer.</summary>
    private static void Sell(BillingHarness till)
    {
        till.Scan(Dal);
        till.Press(Key.F12);
        till.Press(Key.Enter);
        till.Press(Key.Enter);
    }

    private static void Type(BillingHarness till, string text)
    {
        till.ViewModel.EditBuffer = text;
        till.Press(Key.Enter);
    }

    private static void OpenClose(BillingHarness till) => till.Press(Key.F12, ModifierKeys.Shift);

    [Fact]
    public void TheExpectedCashIsNotShownUntilTheDrawerIsCounted()
    {
        using var till = Till();
        Sell(till);

        OpenClose(till);

        Assert.True(till.ViewModel.IsCountingDrawer);
        Assert.DoesNotContain("189", till.ViewModel.StatusMessage);
        Assert.Contains("Count the cash in the drawer", till.ViewModel.StatusMessage);
        Assert.DoesNotContain(till.ViewModel.DayCloseRows, r => r.Label == "Cash expected in the drawer");
        Assert.Equal(new DayCloseRow("Cash in the drawer", "count it first", Emphasis: true), till.ViewModel.DayCloseRows[^1]);
        Assert.Contains("Count the drawer", till.ViewModel.DayCloseKeys);
    }

    [Theory]
    [InlineData("189", "exactly right", "The drawer is", "exactly right")]
    [InlineData("200", "over by ₹11.00", "The drawer is over by", "₹11.00")]
    [InlineData("180", "short by ₹9.00", "The drawer is short by", "₹9.00")]
    public void TheCountShowsWhetherTheDrawerIsOverOrShort(string counted, string said, string label, string value)
    {
        using var till = Till();
        Sell(till);
        OpenClose(till);

        Type(till, counted);

        Assert.False(till.ViewModel.IsCountingDrawer);
        Assert.Contains(said, till.ViewModel.StatusMessage);
        Assert.Contains(new DayCloseRow("Cash expected in the drawer", "₹189.00"), till.ViewModel.DayCloseRows);
        Assert.Equal(new DayCloseRow(label, value, Emphasis: true), till.ViewModel.DayCloseRows[^1]);
        Assert.Contains("again closes the day", till.ViewModel.DayCloseKeys);
    }

    /// <summary>A recount replaces the first count: the last figure typed is the one closed on.</summary>
    [Fact]
    public void TheDrawerCanBeCountedAgain()
    {
        using var till = Till();
        Sell(till);
        OpenClose(till);

        Type(till, "180");
        Type(till, "189");

        Assert.Contains("exactly right", till.ViewModel.StatusMessage);

        OpenClose(till);

        Assert.Equal(189m, till.DayCloses.FindLatest(BillingHarness.LaneId)!.CashCounted);
    }

    [Fact]
    public void TheCountIsKeptWithTheCloseAndSaidAfterIt()
    {
        using var till = Till();
        Sell(till);
        OpenClose(till);
        Type(till, "180");

        OpenClose(till);

        var day = till.DayCloses.FindLatest(BillingHarness.LaneId)!;
        Assert.Equal(180m, day.CashCounted);
        Assert.Equal("Murugan", day.CountedBy);
        Assert.Equal(-9m, day.CashDifference);

        Assert.Contains("₹189.00 expected in the drawer, short by ₹9.00.", till.ViewModel.StatusMessage);
    }

    /// <summary>The count goes on the paper too, with the difference in words.</summary>
    [Fact]
    public void TheReportPrintsTheCountAndTheDifference()
    {
        using var till = Till();
        Sell(till);
        till.Printer.Clear();

        OpenClose(till);
        Type(till, "180");
        OpenClose(till);

        var paper = System.Text.Encoding.Latin1.GetString(Assert.Single(till.Printer.Jobs));
        Assert.Contains("Cash counted", paper);
        Assert.Contains("Counted by", paper);
        Assert.Contains("SHORT BY", paper);
        Assert.Contains("9.00", paper);
    }

    /// <summary>A close without a count still closes, and says it was not counted, on screen and on paper.</summary>
    [Fact]
    public void ADayClosedWithoutACountSaysSo()
    {
        using var till = Till();
        Sell(till);
        till.Printer.Clear();

        OpenClose(till);
        OpenClose(till);

        var day = till.DayCloses.FindLatest(BillingHarness.LaneId)!;
        Assert.Null(day.CashCounted);
        Assert.Null(day.CountedBy);
        Assert.Contains("not counted.", till.ViewModel.StatusMessage);
        Assert.Contains("Not counted at the close", System.Text.Encoding.Latin1.GetString(Assert.Single(till.Printer.Jobs)));
    }

    /// <summary>Backing out of the close forgets the count: the next close is counted afresh.</summary>
    [Fact]
    public void BackingOutForgetsTheCount()
    {
        using var till = Till();
        Sell(till);
        OpenClose(till);
        Type(till, "189");

        till.Press(Key.Escape);
        OpenClose(till);

        Assert.True(till.ViewModel.IsCountingDrawer);
        Assert.DoesNotContain(till.ViewModel.DayCloseRows, r => r.Label == "Cash expected in the drawer");
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-50")]
    public void ACountThatIsNotAnAmountIsRefused(string typed)
    {
        using var till = Till();
        Sell(till);
        OpenClose(till);

        Type(till, typed);

        Assert.True(till.ViewModel.IsCountingDrawer);
        Assert.Contains("is not an amount of cash", till.ViewModel.StatusMessage);
    }

    /// <summary>Enter with nothing typed says what to do, and closes nothing.</summary>
    [Fact]
    public void EnterWithNothingTypedSaysWhatToDo()
    {
        using var till = Till();
        Sell(till);
        OpenClose(till);

        till.Press(Key.Enter);

        Assert.True(till.ViewModel.IsConfirmingDayClose);
        Assert.Contains("close without a count", till.ViewModel.StatusMessage);
        Assert.Null(till.DayCloses.FindLatest(BillingHarness.LaneId));
    }
}
