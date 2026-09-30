using System.IO;
using System.Windows.Input;
using Pos.App.ViewModels;
using Pos.Core.Hardware.Display;
using Pos.Core.Hardware.Serial;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// The customer's side of the counter: the second screen and the pole display following the till,
/// from welcome, through the bill and the payment, to thanks and the change.
/// </summary>
public class CustomerDisplayTests
{
    /// <summary>A pole display that keeps what it was told to show.</summary>
    private sealed class RecordingPole : IPoleDisplay
    {
        public List<(string Top, string Bottom)> Shown { get; } = [];

        public bool IsConfigured => true;

        public string Name => "recording pole";

        public int Width => 20;

        public bool Show(string top, string bottom)
        {
            Shown.Add((top, bottom));
            return true;
        }
    }

    private static BillingHarness Till() => new(
        Catalogue.Item(sku: "DAL001", barcode: "8901234567890", name: "Toor Dal 1kg", price: 189m),
        Catalogue.Item(sku: "SUG001", barcode: "8901234567906", name: "Sugar Loose", price: 45m));

    [Fact]
    public void AnEmptyTillWelcomes()
    {
        using var till = Till();
        var pole = new RecordingPole();
        using var display = new CustomerDisplayViewModel(till.ViewModel, "Sri Murugan Stores", pole);

        Assert.Equal(CustomerDisplayState.Welcome, display.State);
        Assert.Equal(("Sri Murugan Stores", "WELCOME"), pole.Shown[^1]);
    }

    [Fact]
    public void EachItemShowsAsItIsRungUpWithTheTotal()
    {
        using var till = Till();
        var pole = new RecordingPole();
        using var display = new CustomerDisplayViewModel(till.ViewModel, "Sri Murugan Stores", pole);

        till.Scan("8901234567890");
        till.Scan("8901234567906");

        Assert.Equal(CustomerDisplayState.Bill, display.State);
        Assert.Equal(2, display.Lines.Count);
        Assert.Equal("Sugar Loose", display.LastItem);
        Assert.Equal("234.00", display.Total);
        Assert.Equal(("Sugar Loose    45.00", "TOTAL         234.00"), pole.Shown[^1]);
    }

    /// <summary>Paying: what is left while it is being paid, and the change once it is over-paid.</summary>
    [Fact]
    public void PayingShowsTheBalanceThenTheChange()
    {
        using var till = Till();
        var pole = new RecordingPole();
        using var display = new CustomerDisplayViewModel(till.ViewModel, "Sri Murugan Stores", pole);

        till.Scan("8901234567890");
        till.Press(Key.F12);
        Assert.Equal(CustomerDisplayState.Paying, display.State);

        till.ViewModel.EditBuffer = "100";
        till.Press(Key.Enter);
        Assert.Equal("89.00", display.Balance);
        Assert.Equal(("TOTAL         189.00", "BALANCE        89.00"), pole.Shown[^1]);

        till.ViewModel.EditBuffer = "100";
        till.Press(Key.Enter);
        Assert.Equal("11.00", display.Change);
    }

    [Fact]
    public void ASettledSaleThanksWithTheChangeUntilTheNextBillStarts()
    {
        using var till = Till();
        var pole = new RecordingPole();
        using var display = new CustomerDisplayViewModel(till.ViewModel, "Sri Murugan Stores", pole);

        till.Scan("8901234567890");
        till.Press(Key.F12);
        till.ViewModel.EditBuffer = "200";
        till.Press(Key.Enter);
        till.Press(Key.Enter);

        Assert.Equal(CustomerDisplayState.Thanks, display.State);
        Assert.Equal("11.00", display.Change);
        Assert.Equal(("CHANGE         11.00", "THANK YOU"), pole.Shown[^1]);

        till.Scan("8901234567906");
        Assert.Equal(CustomerDisplayState.Bill, display.State);
    }

    /// <summary>
    /// The screen faces the queue: the customer by first name only, never their mobile number, and
    /// their points only once the sale has changed them.
    /// </summary>
    [Fact]
    public void TheQueueSeesAFirstNameAndPointsOnlyWhenTheyChange()
    {
        using var till = Till();
        using var display = new CustomerDisplayViewModel(till.ViewModel, "Sri Murugan Stores");
        till.AddCustomer("9500012345", loyaltyBalance: 40, name: "Lakshmi Narayanan Subramaniam");

        till.Press(Key.F7);
        till.ViewModel.EditBuffer = "9500012345";
        till.Press(Key.Enter);
        till.Scan("8901234567890");

        Assert.Equal("Lakshmi", display.Customer);

        till.Press(Key.F12);
        till.Press(Key.Enter);
        till.Press(Key.Enter);

        Assert.Equal(CustomerDisplayState.Thanks, display.State);
        Assert.StartsWith("Lakshmi  ·  3 points earned, 43 in all", display.Customer);
    }

    [Fact]
    public void ACustomerWithNoNameIsNotShownByTheirNumber()
    {
        using var till = Till();
        using var display = new CustomerDisplayViewModel(till.ViewModel, "Sri Murugan Stores");
        till.AddCustomer("9500012345");

        till.Press(Key.F7);
        till.ViewModel.EditBuffer = "9500012345";
        till.Press(Key.Enter);
        till.Scan("8901234567890");

        Assert.True(till.ViewModel.HasCustomer);
        Assert.Equal(string.Empty, display.Customer);
    }

    /// <summary>A Tamil lane's customer display is in Tamil, in the receipt's own words where it has them.</summary>
    [Fact]
    public void ATamilLanesScreenIsInTamil()
    {
        using var till = Till();
        using var display = new CustomerDisplayViewModel(till.ViewModel, "Sri Murugan Stores", language: Pos.Core.Domain.Printing.ReceiptLanguage.Tamil);

        Assert.Equal("மொத்தம்", display.Words.Total);
        Assert.Equal("வணக்கம்", display.Words.Welcome);
        Assert.All(
            typeof(CustomerDisplayWords).GetProperties().Select(p => (string)p.GetValue(display.Words)!),
            word => Assert.Contains(word, c => c is >= '஀' and <= '௿'));
    }

    [Fact]
    public void ThePoleIsNotRewrittenWhenNothingOnItChanged()
    {
        using var till = Till();
        var pole = new RecordingPole();
        using var display = new CustomerDisplayViewModel(till.ViewModel, "Sri Murugan Stores", pole);

        var before = pole.Shown.Count;
        till.ViewModel.SearchText = "sug";

        Assert.Equal(before, pole.Shown.Count);
    }

    // ---- The pole display itself -----------------------------------------------------------------

    [Fact]
    public void ThePoleIsClearedThenGivenTwoPaddedLines()
    {
        var port = new FakeSerialPort("COM4");
        var pole = new SerialPoleDisplay(port);

        Assert.True(pole.Show("Sugar Loose    45.00", "TOTAL"));

        var bytes = port.Written.ToArray();
        Assert.Equal(0x0C, bytes[0]);
        Assert.Equal("Sugar Loose    45.00", System.Text.Encoding.ASCII.GetString(bytes, 1, 20));
        Assert.Equal(new byte[] { 0x1F, 0x24, 0x01, 0x02 }, bytes[21..25]);
        Assert.Equal("TOTAL               ", System.Text.Encoding.ASCII.GetString(bytes, 25, 20));
    }

    [Fact]
    public void ALongLineIsCutToTheDisplay() =>
        Assert.Equal(20, new SerialPoleDisplay(new FakeSerialPort()).Frame("Premium Organic Cold Pressed Groundnut Oil", "")[1..21].Length);

    /// <summary>A display that has gone away is a display showing nothing, not a failed sale.</summary>
    [Fact]
    public void AnUnpluggedPoleIsReportedNotThrown()
    {
        var port = new FakeSerialPort { FailWith = new IOException("the device is not connected") };

        Assert.False(new SerialPoleDisplay(port).Show("TOTAL", "189.00"));
    }

    [Fact]
    public void APairKeepsTheFigureAndCutsTheLabel() =>
        Assert.Equal("Premium Organ 1299.00", SerialPoleDisplay.Pair("Premium Organic Cold Pressed", "1299.00", 21));
}
