using System.IO;
using Pos.Core.Configuration;
using Pos.Core.Domain;
using Pos.Core.Domain.Printing;
using Pos.Core.Hardware.Printing;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// The UPI request the till's code carries: the shop's UPI ID and name, and the amount to the paisa,
/// so the customer approves exactly what the bill says rather than typing it.
/// </summary>
public class UpiTests : IDisposable
{
    private static readonly UpiPayee Murugan = new("murugan.stores@okaxis", "Sri Murugan Stores");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "posupi-" + Guid.NewGuid().ToString("N"));

    public UpiTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    // ---- The link --------------------------------------------------------------------------------

    [Fact]
    public void ALinkCarriesThePayeeAndTheExactAmount() =>
        Assert.Equal(
            "upi://pay?pa=murugan.stores@okaxis&pn=Sri%20Murugan%20Stores&am=400.50&cu=INR",
            UpiLink.For(Murugan, 400.5m));

    [Theory]
    [InlineData(1, "1.00")]
    [InlineData(0.5, "0.50")]
    [InlineData(1234567.8, "1234567.80")]
    [InlineData(99.995, "100.00")]
    [InlineData(99.985, "99.98")]
    public void TheAmountIsRupeesToThePaisaWithAPoint(double amount, string written) =>
        Assert.EndsWith($"&am={written}&cu=INR", UpiLink.For(Murugan, (decimal)amount));

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public void NothingOrLessIsNotAskedFor(double amount) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => UpiLink.For(Murugan, (decimal)amount));

    /// <summary>Tamil and symbols in the name travel percent-encoded, as UTF-8.</summary>
    [Fact]
    public void TheNameIsEncodedForALink()
    {
        var link = UpiLink.For(new UpiPayee("ravi.maligai@okicici", "ரவி & Sons"), 10m);

        Assert.Contains("&pn=%E0%AE%B0%E0%AE%B5%E0%AE%BF%20%26%20Sons&", link);
    }

    [Fact]
    public void ANoteIsSentShortEnoughForTheApps()
    {
        var link = UpiLink.For(Murugan, 10m, note: new string('x', 80));

        Assert.Contains($"&tn={new string('x', UpiLink.MostNote)}&am=", link);
    }

    /// <summary>A personal UPI ID is sent no reference: some apps refuse a payment to one that has one.</summary>
    [Fact]
    public void AReferenceGoesOnlyToAMerchantUpiId()
    {
        Assert.DoesNotContain("&tr=", UpiLink.For(Murugan, 10m, reference: "L1260929"));

        var merchant = Murugan with { MerchantCode = "5411" };
        Assert.Equal(
            "upi://pay?pa=murugan.stores@okaxis&pn=Sri%20Murugan%20Stores&mc=5411&tr=L1260929&am=10.00&cu=INR",
            UpiLink.For(merchant, 10m, reference: "L1260929"));
    }

    // ---- UPI IDs ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("murugan.stores@okaxis")]
    [InlineData("9876543210@ybl")]
    [InlineData("paytmqr28100505010111abcd@paytm")]
    [InlineData("shop-1_a@icici")]
    public void AUpiIdIsANameThenAtThenTheBank(string id) => Assert.Null(UpiPayee.Problem(id));

    [Theory]
    [InlineData("")]
    [InlineData("murugan")]
    [InlineData("@okaxis")]
    [InlineData("murugan@")]
    [InlineData("murugan stores@okaxis")]
    [InlineData("murugan@ok axis")]
    [InlineData("murugan@9bank")]
    [InlineData("9876543210")]
    public void AnythingElseIsNot(string id) => Assert.NotNull(UpiPayee.Problem(id));

    [Theory]
    [InlineData(null, true)]
    [InlineData("5411", true)]
    [InlineData("541", false)]
    [InlineData("grocery", false)]
    public void AMerchantCodeIsFourDigits(string? code, bool fine) =>
        Assert.Equal(fine, UpiPayee.MerchantCodeProblem(code) is null);

    // ---- Settings --------------------------------------------------------------------------------

    [Fact]
    public void ThePayeeIsNamedAfterTheStoreUnlessTheSettingsSayOtherwise()
    {
        Assert.Null(new UpiSettings().ToPayee("Sri Murugan Stores"));

        Assert.Equal(new UpiPayee("murugan.stores@okaxis", "Sri Murugan Stores"),
            new UpiSettings { Id = " murugan.stores@okaxis " }.ToPayee("Sri Murugan Stores"));

        Assert.Equal(new UpiPayee("murugan.stores@okaxis", "Murugan Traders", "5411"),
            new UpiSettings { Id = "murugan.stores@okaxis", Name = "Murugan Traders", MerchantCode = "5411" }.ToPayee("Sri Murugan Stores"));
    }

    [Fact]
    public void AUpiIdThatCannotBeRightStopsTheLaneStarting()
    {
        File.WriteAllText(SettingsPath, """{ "upi": { "id": "murugan stores" } }""");

        var refused = Assert.Throws<InvalidOperationException>(() => PosSettings.LoadOrDefault(SettingsPath));
        Assert.Contains("upi section", refused.Message);
    }

    [Fact]
    public void TheUpiIdIsSavedWithoutDisturbingTheRestOfTheFile()
    {
        File.WriteAllText(SettingsPath, """{ "laneId": "L7", "upi": { "name": "Murugan Traders" } }""");

        SettingsFile.SetUpiId(SettingsPath, " murugan.stores@okaxis ");

        var settings = PosSettings.LoadOrDefault(SettingsPath);
        Assert.Equal("L7", settings.LaneId);
        Assert.Equal("murugan.stores@okaxis", settings.Upi.Id);
        Assert.Equal("Murugan Traders", settings.Upi.Name);
    }

    /// <summary>Taking the ID out turns the code off, and leaves the name for when it comes back.</summary>
    [Fact]
    public void TakingTheUpiIdOutTurnsTheCodeOff()
    {
        File.WriteAllText(SettingsPath, """{ "upi": { "id": "murugan.stores@okaxis", "name": "Murugan Traders" } }""");

        SettingsFile.SetUpiId(SettingsPath, "  ");

        var settings = PosSettings.LoadOrDefault(SettingsPath);
        Assert.False(settings.Upi.IsSet);
        Assert.Null(settings.Upi.ToPayee("Sri Murugan Stores"));
        Assert.Equal("Murugan Traders", settings.Upi.Name);
    }

    [Fact]
    public void AWrongUpiIdIsNotSaved()
    {
        Assert.Throws<ArgumentException>(() => SettingsFile.SetUpiId(SettingsPath, "not an id"));
        Assert.False(File.Exists(SettingsPath));
    }

    // ---- The slip --------------------------------------------------------------------------------

    private static readonly StoreProfile Store = new() { Name = "Sri Murugan Stores" };

    [Fact]
    public void TheSlipSaysWhatToPayAndThatItIsNotABill()
    {
        var link = UpiLink.For(Murugan, 400.5m);

        var slip = new ReceiptComposer(Store).ComposeUpiSlip(Murugan, 400.5m, link).ToPlainText();

        Assert.Contains("SCAN TO PAY BY UPI", slip);
        Assert.Contains("Rs 400.50", slip);
        Assert.Contains("[QR]", slip);
        Assert.Contains("murugan.stores@okaxis", slip);
        Assert.Contains("Not a bill. Your bill prints once it is paid.", slip);
        Assert.DoesNotContain("TAX INVOICE", slip);
    }

    [Fact]
    public void TheSlipIsInTamilOnATamilLane()
    {
        var slip = new ReceiptComposer(Store, language: ReceiptLanguage.Tamil)
            .ComposeUpiSlip(Murugan, 10m, UpiLink.For(Murugan, 10m)).ToPlainText();

        Assert.Contains("ஸ்கேன் செய்து UPI மூலம் செலுத்தவும்", slip);
        Assert.Contains("இது பில் அல்ல", slip);
    }

    [Fact]
    public void TheSlipGoesToThePrinterWithItsCode()
    {
        var link = UpiLink.For(Murugan, 400.5m);

        var bytes = new ReceiptComposer(Store, ReceiptBuilder.Width58Mm).ComposeUpiSlip(Murugan, 400.5m, link).ToEscPos();

        // GS v 0 at 58mm: 48 bytes (384 dots) a row.
        Assert.Contains(Enumerable.Range(0, bytes.Length - 5), i =>
            bytes[i] == 0x1D && bytes[i + 1] == (byte)'v' && bytes[i + 2] == (byte)'0' && bytes[i + 4] == 48 && bytes[i + 5] == 0);
    }
}
