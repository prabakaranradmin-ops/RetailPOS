using System.Windows.Input;
using Pos.App.Input;
using Pos.App.ViewModels;
using Pos.App.Views;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// What colour the till's messages come up in, read off what the till actually says.
/// </summary>
/// <remarks>
/// Driven through the till rather than by feeding phrases to <see cref="MessageKinds"/>, because the
/// kind is read from the wording: a test that restated the wording would go on passing after the
/// till's own message had been reworded out from under it.
/// </remarks>
public class MessageKindTests
{
    private const string DalBarcode = "8901234567890";

    private static Item Dal => Catalogue.Item(sku: "DAL001", barcode: DalBarcode, name: "Toor Dal 1kg", price: 189m, gstRate: 5m);

    private static BillingHarness Till() => new(Dal);

    [Fact]
    public void NothingToSayIsNotAnAlarm()
    {
        Assert.Equal(MessageKind.Info, MessageKinds.Classify(null));
        Assert.Equal(MessageKind.Info, MessageKinds.Classify("   "));
    }

    [Fact]
    public void AnItemGoingOnTheBillIsDone()
    {
        using var till = Till();

        till.Scan(DalBarcode);

        Assert.Contains("added.", till.ViewModel.StatusMessage);
        Assert.Equal(MessageKind.Done, till.ViewModel.StatusKind);
    }

    /// <summary>
    /// A scan that matched nothing is red, and the view is told so it can select what was scanned
    /// and sound - the cashier is looking at the goods, not the screen.
    /// </summary>
    [Fact]
    public void AScanThatMatchesNothingIsRefusedAndSaysSoOnce()
    {
        using var till = Till();
        var rejected = 0;
        till.ViewModel.SearchRejected += (_, _) => rejected++;

        till.Scan("8900000000001");

        Assert.Contains("No item matches", till.ViewModel.StatusMessage);
        Assert.Equal(MessageKind.Error, till.ViewModel.StatusKind);
        Assert.Equal(1, rejected);
    }

    [Fact]
    public void AScanThatMatchesIsNotRejected()
    {
        using var till = Till();
        var rejected = 0;
        till.ViewModel.SearchRejected += (_, _) => rejected++;

        till.Scan(DalBarcode);

        Assert.Equal(0, rejected);
    }

    [Fact]
    public void ParkingABillIsDone()
    {
        using var till = Till();
        till.Scan(DalBarcode);

        till.Press(Key.F5);

        Assert.Contains("held as", till.ViewModel.StatusMessage);
        Assert.Equal(MessageKind.Done, till.ViewModel.StatusKind);
    }

    /// <summary>Nothing went wrong, but nothing happened either: amber, not red.</summary>
    [Fact]
    public void RecallingWithNothingParkedIsHeldUpNotRefused()
    {
        using var till = Till();

        till.Press(Key.F6);

        Assert.Equal("No held bills.", till.ViewModel.StatusMessage);
        Assert.Equal(MessageKind.Warning, till.ViewModel.StatusKind);
    }

    [Fact]
    public void AnAmountThatIsNotAnAmountIsRefused()
    {
        using var till = Till();
        till.Scan(DalBarcode);
        till.Press(Key.F12);

        till.ViewModel.EditBuffer = "abc";
        till.Press(Key.Enter);

        Assert.Contains("is not an amount", till.ViewModel.StatusMessage);
        Assert.Equal(MessageKind.Error, till.ViewModel.StatusKind);
    }

    /// <summary>Opening the payment says what to do next: the ordinary colour, not a success.</summary>
    [Fact]
    public void BeingToldWhatToDoNextIsInformation()
    {
        using var till = Till();
        till.Scan(DalBarcode);

        till.Press(Key.F12);

        Assert.Contains("Choose a tender", till.ViewModel.StatusMessage);
        Assert.Equal(MessageKind.Info, till.ViewModel.StatusKind);
    }

    [Fact]
    public void BeingPaidInFullIsDone()
    {
        using var till = Till();
        till.Scan(DalBarcode);
        till.Press(Key.F12);

        till.Press(Key.Enter);

        Assert.Equal("Paid in full. Enter again to finish.", till.ViewModel.StatusMessage);
        Assert.Equal(MessageKind.Done, till.ViewModel.StatusKind);
    }

    /// <summary>
    /// A sale that went through but left something to do by hand is amber: the colour is what makes
    /// somebody read to the end of the sentence.
    /// </summary>
    [Fact]
    public void AProblemOutranksASuccess() =>
        Assert.Equal(MessageKind.Warning,
            MessageKinds.Classify("Bill INV-0001 settled for 189.00. WhatsApp did not open - paste the bill into a message to 9500012345."));

    /// <summary>The failures written in capitals, so nobody misses them, are not shown as done.</summary>
    [Theory]
    [InlineData("INV-1 settled for ₹189.00. THE RECEIPT DID NOT PRINT: out of paper.", MessageKind.Warning)]
    [InlineData("Lakshmi paid ₹100.00 by cash. Nothing more owed. THE DRAWER DID NOT OPEN - use the key.", MessageKind.Warning)]
    [InlineData("Day closed. Report 3: 1 bill. BACKUP FAILED: the disk is full", MessageKind.Error)]
    public void CapitalisedFailuresAreNotSuccesses(string message, MessageKind kind) =>
        Assert.Equal(kind, MessageKinds.Classify(message));

    [Fact]
    public void RefusalsAreRecognisedWhateverTheirCapitalisation() =>
        Assert.Equal(MessageKind.Error, MessageKinds.Classify("Could not read the scale."));

    // ---- The key a message tells the cashier to press -------------------------------------------

    [Fact]
    public void TheConfirmKeyIsCalledEnter() =>
        Assert.Equal("Enter", MainBillingView.CommitKeyName(Keymap.Default));

    /// <summary>
    /// On a lane that confirms with another key, "press Enter" would be an instruction that does
    /// nothing, so the messages name the key the lane actually uses.
    /// </summary>
    [Fact]
    public void ALaneThatConfirmsWithAnotherKeySaysThatKey()
    {
        var keymap = Keymap.Default.WithOverrides(new Dictionary<KeyStroke, PosAction>
        {
            [new(Key.Enter)] = PosAction.MoveDown,
            [new(Key.F10)] = PosAction.Commit,
        });

        Assert.Equal("F10", MainBillingView.CommitKeyName(keymap));

        using var till = Till();
        till.ViewModel.CommitKey = MainBillingView.CommitKeyName(keymap);
        till.Scan(DalBarcode);
        till.Press(Key.F12);

        Assert.EndsWith("then press F10.", till.ViewModel.StatusMessage);
    }

    [Fact]
    public void AConfirmKeyWithAModifierIsWrittenInFull()
    {
        var keymap = Keymap.Default.WithOverrides(new Dictionary<KeyStroke, PosAction>
        {
            [new(Key.Enter)] = PosAction.MoveDown,
            [new(Key.Enter, ModifierKeys.Control)] = PosAction.Commit,
        });

        Assert.Equal("Ctrl+Enter", MainBillingView.CommitKeyName(keymap));
    }
}
