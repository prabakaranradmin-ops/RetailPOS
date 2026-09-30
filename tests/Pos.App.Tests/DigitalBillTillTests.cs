using System.Windows.Input;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// Ctrl+W: the bill on the customer's phone. WhatsApp on the till's computer opens at their chat with
/// the bill typed in - instead of paper while paying, or afterwards - or the bill goes on the
/// clipboard when it cannot.
/// </summary>
public class DigitalBillTillTests
{
    private sealed class Links
    {
        public List<string> Opened { get; } = [];

        public bool Works { get; set; } = true;

        public bool Open(string link)
        {
            Opened.Add(link);
            return Works;
        }
    }

    private static BillingHarness Till(Links links, List<string> copied)
    {
        var till = new BillingHarness(
            Catalogue.Item(sku: "DAL001", barcode: "8901234567890", name: "Toor Dal 1kg", price: 189m));

        till.ViewModel.Store = BillingHarness.Store;
        till.ViewModel.OpenLink = links.Open;
        till.ViewModel.CopyText = copied.Add;
        return till;
    }

    private static void AttachLakshmi(BillingHarness till)
    {
        till.AddCustomer("9500012345", name: "Lakshmi");
        till.Press(Key.F7);
        till.ViewModel.EditBuffer = "9500012345";
        till.Press(Key.Enter);
    }

    private static void PayCash(BillingHarness till)
    {
        till.Press(Key.Enter);
        till.Press(Key.Enter);
    }

    [Fact]
    public void AfterASaleCtrlWOpensWhatsAppAtTheirChat()
    {
        var links = new Links();
        using var till = Till(links, []);
        AttachLakshmi(till);
        till.Scan("8901234567890");
        till.Press(Key.F12);
        PayCash(till);

        Assert.True(till.Press(Key.W, ModifierKeys.Control));

        var link = Assert.Single(links.Opened);
        Assert.StartsWith("whatsapp://send?phone=919500012345&text=", link);
        Assert.Contains(Uri.EscapeDataString(till.ViewModel.LastInvoiceNo), link);
        Assert.Contains("is in WhatsApp for Lakshmi: press Enter there to send it", till.ViewModel.StatusMessage);
    }

    [Fact]
    public void WithoutWhatsAppTheBillGoesOnTheClipboard()
    {
        var links = new Links { Works = false };
        var copied = new List<string>();
        using var till = Till(links, copied);
        AttachLakshmi(till);
        till.Scan("8901234567890");
        till.Press(Key.F12);
        PayCash(till);

        till.Press(Key.W, ModifierKeys.Control);

        Assert.Contains("*TAX INVOICE*", Assert.Single(copied));
        Assert.Contains("WhatsApp did not open on this computer, so paste it into a message to 9500012345", till.ViewModel.StatusMessage);
    }

    [Fact]
    public void AWalkInsBillGoesOnTheClipboard()
    {
        var links = new Links();
        var copied = new List<string>();
        using var till = Till(links, copied);
        till.Scan("8901234567890");
        till.Press(Key.F12);
        PayCash(till);

        till.Press(Key.W, ModifierKeys.Control);

        Assert.Empty(links.Opened);
        Assert.Single(copied);
        Assert.Contains("There is no number on the bill", till.ViewModel.StatusMessage);
    }

    // ---- Instead of paper ------------------------------------------------------------------------

    [Fact]
    public void TakenDigitallyTheBillPrintsNothingAndGoesToWhatsApp()
    {
        var links = new Links();
        using var till = Till(links, []);
        AttachLakshmi(till);
        till.Scan("8901234567890");
        till.Press(Key.F12);
        var printed = till.Printer.Jobs.Count;

        till.Press(Key.W, ModifierKeys.Control);
        Assert.True(till.ViewModel.IsPaperless);
        Assert.Contains("No paper: the bill goes to Lakshmi's WhatsApp", till.ViewModel.PaperlessLine);

        PayCash(till);

        Assert.Equal(printed, till.Printer.Jobs.Count);
        Assert.Single(links.Opened);
        Assert.Contains("settled for ₹189.00", till.ViewModel.StatusMessage);
        Assert.Contains("is in WhatsApp for Lakshmi", till.ViewModel.StatusMessage);
        Assert.False(till.ViewModel.IsPaperless);
    }

    [Fact]
    public void CtrlWAgainPrintsItAfterAll()
    {
        var links = new Links();
        using var till = Till(links, []);
        AttachLakshmi(till);
        till.Scan("8901234567890");
        till.Press(Key.F12);
        var printed = till.Printer.Jobs.Count;

        till.Press(Key.W, ModifierKeys.Control);
        till.Press(Key.W, ModifierKeys.Control);
        PayCash(till);

        Assert.Equal(printed + 1, till.Printer.Jobs.Count);
        Assert.Empty(links.Opened);
    }

    [Fact]
    public void ABillWithNoNumberCannotGoWithoutPaper()
    {
        using var till = Till(new Links(), []);
        till.Scan("8901234567890");
        till.Press(Key.F12);

        till.Press(Key.W, ModifierKeys.Control);

        Assert.False(till.ViewModel.IsPaperless);
        Assert.Contains("needs the customer's number", till.ViewModel.StatusMessage);
    }

    /// <summary>Abandoning the payment forgets the choice: the next attempt prints unless asked again.</summary>
    [Fact]
    public void AbandoningThePaymentForgetsTheChoice()
    {
        using var till = Till(new Links(), []);
        AttachLakshmi(till);
        till.Scan("8901234567890");
        till.Press(Key.F12);
        till.Press(Key.W, ModifierKeys.Control);

        till.Press(Key.Escape);
        till.Press(Key.F12);

        Assert.False(till.ViewModel.IsPaperless);
    }

    // ---- An older bill ---------------------------------------------------------------------------

    [Fact]
    public void InCtrlPTheBillFoundIsSent()
    {
        var links = new Links();
        using var till = Till(links, []);
        AttachLakshmi(till);
        till.Scan("8901234567890");
        till.Press(Key.F12);
        PayCash(till);
        var first = till.ViewModel.LastInvoiceNo;

        till.Scan("8901234567890");
        till.Press(Key.F12);
        PayCash(till);

        till.Press(Key.P, ModifierKeys.Control);
        till.ViewModel.EditBuffer = first;
        till.Press(Key.W, ModifierKeys.Control);

        Assert.Contains(Uri.EscapeDataString(first), Assert.Single(links.Opened));
        Assert.StartsWith(first, till.ViewModel.StatusMessage);
        Assert.False(till.ViewModel.IsReprinting);
    }

    [Fact]
    public void CtrlWWithNothingToSendSaysWhen()
    {
        using var till = Till(new Links(), []);

        till.Press(Key.W, ModifierKeys.Control);

        Assert.Contains("Ctrl+W sends a bill to the customer's WhatsApp", till.ViewModel.StatusMessage);
    }
}
