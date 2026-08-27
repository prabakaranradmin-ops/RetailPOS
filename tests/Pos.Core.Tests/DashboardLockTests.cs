using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pos.Core.Configuration;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// Keeping the shop's figures behind a PIN.
/// </summary>
/// <remarks>
/// The dashboard shows turnover, margins, cost prices and what sells — the things an owner may not
/// want read off the counter. These tests cover the lock itself. What the lock cannot do is also
/// worth stating plainly: on a shared Windows login the database is still readable with other
/// software, and that is a deployment problem rather than one this code can solve.
/// </remarks>
public class DashboardLockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "poslock-" + Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public DashboardLockTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    // ---- The PIN itself --------------------------------------------------------------------------

    [Fact]
    public void TheRightPinOpensItAndAWrongOneDoesNot()
    {
        var stored = DashboardLock.Create("7412");

        Assert.True(DashboardLock.Verify("7412", stored));
        Assert.False(DashboardLock.Verify("7413", stored));
        Assert.False(DashboardLock.Verify("741", stored));
        Assert.False(DashboardLock.Verify("74120", stored));
    }

    [Fact]
    public void ThePinIsNeverStored()
    {
        var stored = DashboardLock.Create("7412");
        var written = JsonSerializer.Serialize(stored);

        Assert.DoesNotContain("7412", written);
    }

    /// <summary>
    /// Same PIN, different salt, so two lanes sharing a PIN do not share a hash — and a hash lifted
    /// from one settings file says nothing about another.
    /// </summary>
    [Fact]
    public void TheSamePinStoredTwiceLooksNothingAlike()
    {
        var first = DashboardLock.Create("7412");
        var second = DashboardLock.Create("7412");

        Assert.NotEqual(first.Salt, second.Salt);
        Assert.NotEqual(first.Hash, second.Hash);

        Assert.True(DashboardLock.Verify("7412", first));
        Assert.True(DashboardLock.Verify("7412", second));
    }

    [Fact]
    public void CaseMatters()
    {
        var stored = DashboardLock.Create("Maligai26");

        Assert.True(DashboardLock.Verify("Maligai26", stored));
        Assert.False(DashboardLock.Verify("maligai26", stored));
    }

    [Fact]
    public void APinNeedNotBeDigits()
    {
        var stored = DashboardLock.Create("ravi maligai 26");

        Assert.True(DashboardLock.Verify("ravi maligai 26", stored));
        Assert.False(DashboardLock.Verify("ravi maligai 27", stored));
    }

    [Fact]
    public void NothingOpensAnUnsetLock()
    {
        Assert.False(DashboardLock.Verify("7412", null));
        Assert.False(DashboardLock.Verify("7412", new PinCredential()));
        Assert.False(DashboardLock.Verify(null, DashboardLock.Create("7412")));
        Assert.False(DashboardLock.Verify("", DashboardLock.Create("7412")));
    }

    /// <summary>
    /// A hand-edited or truncated credential refuses everything rather than throwing — and, more to
    /// the point, rather than accepting. A damaged file must not open the thing it was locking.
    /// </summary>
    [Theory]
    [InlineData("not base64 at all", "also not")]
    [InlineData("", "")]
    [InlineData("////", "")]
    public void ADamagedCredentialRefusesRatherThanFailingOpen(string salt, string hash)
    {
        var damaged = new PinCredential { Salt = salt, Hash = hash, Iterations = DashboardLock.Iterations };

        Assert.False(DashboardLock.Verify("7412", damaged));
        Assert.False(DashboardLock.Verify("", damaged));
    }

    [Fact]
    public void ACredentialClaimingNoWorkRefusesRatherThanThrowing()
    {
        var real = DashboardLock.Create("7412");
        var tampered = new PinCredential { Salt = real.Salt, Hash = real.Hash, Iterations = 0 };

        Assert.False(DashboardLock.Verify("7412", tampered));
    }

    /// <summary>
    /// The iteration count is the only thing making a guess cost anything, since the salt and hash
    /// sit in a file the person being kept out can read. It is not a number to quietly lower.
    /// </summary>
    [Fact]
    public void EachGuessIsMadeExpensiveOnPurpose()
    {
        Assert.True(DashboardLock.Iterations >= 200_000);
        Assert.Equal(DashboardLock.Iterations, DashboardLock.Create("7412").Iterations);
    }

    // ---- Which PINs are allowed ------------------------------------------------------------------

    [Theory]
    [InlineData("7412")]
    [InlineData("90210")]
    [InlineData("Maligai26")]
    [InlineData("ravi maligai 26")]
    public void AReasonablePinIsAccepted(string pin)
    {
        Assert.Null(DashboardLock.Rejection(pin));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12")]
    [InlineData("999")]
    public void AnEmptyOrShortPinIsRefused(string? pin)
    {
        Assert.NotNull(DashboardLock.Rejection(pin));
    }

    /// <summary>
    /// A lock set to 0000 is worse than no lock: the owner believes the figures are private, and
    /// anybody opens them first go.
    /// </summary>
    [Theory]
    [InlineData("0000")]
    [InlineData("1111")]
    [InlineData("aaaa")]
    [InlineData("1234")]
    [InlineData("4321")]
    [InlineData("123456")]
    [InlineData("9876")]
    [InlineData("abcd")]
    public void AnObviousPinIsRefused(string pin)
    {
        Assert.NotNull(DashboardLock.Rejection(pin));
    }

    /// <summary>Refusing runs must not refuse anything that merely contains one.</summary>
    [Theory]
    [InlineData("1235")]
    [InlineData("2341")]
    [InlineData("91234")]
    [InlineData("12345a")]
    public void APinThatOnlyLooksLikeARunIsAccepted(string pin)
    {
        Assert.Null(DashboardLock.Rejection(pin));
    }

    [Fact]
    public void APinPaddedWithSpacesIsRefusedRatherThanSilentlyTrimmed()
    {
        // Silently trimming would mean the PIN the owner typed is not the PIN that was stored.
        Assert.NotNull(DashboardLock.Rejection(" 7412"));
        Assert.NotNull(DashboardLock.Rejection("7412 "));
    }

    [Fact]
    public void APinTheRulesRefuseCannotBeStoredAnyway()
    {
        Assert.Throws<ArgumentException>(() => DashboardLock.Create("1234"));
        Assert.Throws<ArgumentException>(() => DashboardLock.Create("99"));
    }

    // ---- Whether the lane is locked --------------------------------------------------------------

    [Fact]
    public void ALaneWithNoPinIsOpen()
    {
        Assert.False(new SecuritySettings().DashboardIsLocked);
        Assert.False(new PosSettings().Security.DashboardIsLocked);
        Assert.False(new SecuritySettings { DashboardPin = new PinCredential() }.DashboardIsLocked);
    }

    [Fact]
    public void ALaneWithAPinIsLocked()
    {
        Assert.True(new SecuritySettings { DashboardPin = DashboardLock.Create("7412") }.DashboardIsLocked);
    }

    // ---- Writing it into the settings file -------------------------------------------------------

    [Fact]
    public void SettingAPinOnALaneThatHasNoSettingsFileWritesOne()
    {
        SettingsFile.SetDashboardPin(SettingsPath, DashboardLock.Create("7412"));

        var reloaded = PosSettings.LoadOrDefault(SettingsPath);

        Assert.True(reloaded.Security.DashboardIsLocked);
        Assert.True(DashboardLock.Verify("7412", reloaded.Security.DashboardPin));
        Assert.Equal("L1", reloaded.LaneId);
    }

    /// <summary>
    /// The command was asked to set a PIN, so it sets a PIN. Everything else in the file — including
    /// anything this build does not know about — is left exactly as the shopkeeper wrote it.
    /// </summary>
    [Fact]
    public void SettingAPinDisturbsNothingElseInTheFile()
    {
        File.WriteAllText(SettingsPath, """
            {
              "laneId": "L4",
              "outletStateCode": "29",
              "store": { "name": "ரவி மளிகை" },
              "somethingThisBuildHasNeverHeardOf": { "keep": [1, 2, 3] }
            }
            """, new UTF8Encoding(true));

        SettingsFile.SetDashboardPin(SettingsPath, DashboardLock.Create("7412"));

        var root = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();

        Assert.Equal("L4", (string?)root["laneId"]);
        Assert.Equal("29", (string?)root["outletStateCode"]);
        Assert.Equal("ரவி மளிகை", (string?)root["store"]!["name"]);
        Assert.Equal(3, root["somethingThisBuildHasNeverHeardOf"]!["keep"]!.AsArray().Count);
        Assert.True(DashboardLock.Verify("7412", PosSettings.LoadOrDefault(SettingsPath).Security.DashboardPin));
    }

    [Fact]
    public void TheSettingsFileKeepsItsByteOrderMark()
    {
        SettingsFile.SetDashboardPin(SettingsPath, DashboardLock.Create("7412"));

        var first = File.ReadAllBytes(SettingsPath).Take(3).ToArray();

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, first);
    }

    [Fact]
    public void ClearingTakesTheWholeSectionOutAgain()
    {
        SettingsFile.SetDashboardPin(SettingsPath, DashboardLock.Create("7412"));
        SettingsFile.SetDashboardPin(SettingsPath, null);

        var reloaded = PosSettings.LoadOrDefault(SettingsPath);
        Assert.False(reloaded.Security.DashboardIsLocked);

        // No hollow "security": {} left behind for somebody to puzzle over.
        var root = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
        Assert.False(root.ContainsKey("security"));
    }

    [Fact]
    public void ClearingALaneThatWasNeverLockedIsHarmless()
    {
        File.WriteAllText(SettingsPath, """{ "laneId": "L1" }""", new UTF8Encoding(true));

        SettingsFile.SetDashboardPin(SettingsPath, null);

        Assert.False(PosSettings.LoadOrDefault(SettingsPath).Security.DashboardIsLocked);
        Assert.Equal("L1", PosSettings.LoadOrDefault(SettingsPath).LaneId);
    }

    [Fact]
    public void ChangingThePinReplacesTheOldOne()
    {
        SettingsFile.SetDashboardPin(SettingsPath, DashboardLock.Create("7412"));
        SettingsFile.SetDashboardPin(SettingsPath, DashboardLock.Create("8523"));

        var reloaded = PosSettings.LoadOrDefault(SettingsPath).Security.DashboardPin;

        Assert.True(DashboardLock.Verify("8523", reloaded));
        Assert.False(DashboardLock.Verify("7412", reloaded));
    }

    /// <summary>
    /// A lane whose settings predate this feature keeps working, unlocked. Nobody's till stops
    /// because a version added a lock they never asked for.
    /// </summary>
    [Fact]
    public void SettingsWrittenBeforeThisExistedStillLoad()
    {
        File.WriteAllText(SettingsPath, """
            { "laneId": "L1", "outletStateCode": "33", "store": { "name": "Ravi Maligai" } }
            """, new UTF8Encoding(true));

        var reloaded = PosSettings.LoadOrDefault(SettingsPath);

        Assert.False(reloaded.Security.DashboardIsLocked);
        Assert.Equal("Ravi Maligai", reloaded.Store.Name);
    }
}
