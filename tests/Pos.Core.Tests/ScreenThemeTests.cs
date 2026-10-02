using System.IO;
using Pos.Core.Configuration;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// How the screens look: which look suits which hour, and the owner's choice kept in the settings
/// file without touching anything else in it.
/// </summary>
public class ScreenThemeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "poslook-" + Guid.NewGuid().ToString("N"));

    public ScreenThemeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    // ---- The hours -------------------------------------------------------------------------------

    /// <summary>Each change of look, a minute either side of it.</summary>
    [Theory]
    [InlineData("00:00", ScreenTheme.Night)]
    [InlineData("05:59", ScreenTheme.Night)]
    [InlineData("06:00", ScreenTheme.Morning)]
    [InlineData("10:59", ScreenTheme.Morning)]
    [InlineData("11:00", ScreenTheme.Noon)]
    [InlineData("15:59", ScreenTheme.Noon)]
    [InlineData("16:00", ScreenTheme.Evening)]
    [InlineData("18:59", ScreenTheme.Evening)]
    [InlineData("19:00", ScreenTheme.Night)]
    [InlineData("23:59", ScreenTheme.Night)]
    public void EachHourHasItsLook(string time, ScreenTheme expected) =>
        Assert.Equal(expected, ScreenThemes.At(TimeOnly.Parse(time, System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>A look chosen outright stays, whatever the hour; only following the clock follows it.</summary>
    [Theory]
    [InlineData(ScreenTheme.Night, "12:00", ScreenTheme.Night)]
    [InlineData(ScreenTheme.Noon, "22:00", ScreenTheme.Noon)]
    [InlineData(ScreenTheme.Morning, "17:00", ScreenTheme.Morning)]
    [InlineData(ScreenTheme.Evening, "07:00", ScreenTheme.Evening)]
    [InlineData(ScreenTheme.ByTimeOfDay, "07:00", ScreenTheme.Morning)]
    [InlineData(ScreenTheme.ByTimeOfDay, "12:00", ScreenTheme.Noon)]
    [InlineData(ScreenTheme.ByTimeOfDay, "17:30", ScreenTheme.Evening)]
    [InlineData(ScreenTheme.ByTimeOfDay, "21:00", ScreenTheme.Night)]
    public void AChosenLookStaysAndTheClockIsOnlyFollowedWhenAskedTo(ScreenTheme chosen, string time, ScreenTheme expected) =>
        Assert.Equal(expected, ScreenThemes.Resolve(chosen, TimeOnly.Parse(time, System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>Following the clock always lands on one of the four looks, never on itself.</summary>
    [Fact]
    public void EveryMinuteOfTheDayHasOneOfTheFourLooks()
    {
        for (var minute = 0; minute < 24 * 60; minute++)
        {
            var look = ScreenThemes.At(TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(minute)));
            Assert.NotEqual(ScreenTheme.ByTimeOfDay, look);
            Assert.True(Enum.IsDefined(look));
        }
    }

    // ---- The settings file -----------------------------------------------------------------------

    [Fact]
    public void ALaneOpensInTheNightLookUntilTheOwnerChoosesAnother()
    {
        Assert.Equal(ScreenTheme.Night, new PosSettings().ScreenTheme);

        File.WriteAllText(SettingsPath, """{ "laneId": "L2" }""");
        Assert.Equal(ScreenTheme.Night, PosSettings.LoadOrDefault(SettingsPath).ScreenTheme);
    }

    [Theory]
    [InlineData(ScreenTheme.Morning)]
    [InlineData(ScreenTheme.Noon)]
    [InlineData(ScreenTheme.Evening)]
    [InlineData(ScreenTheme.Night)]
    [InlineData(ScreenTheme.ByTimeOfDay)]
    public void TheLookIsSavedWithoutDisturbingTheRestOfTheFile(ScreenTheme look)
    {
        File.WriteAllText(SettingsPath, """{ "laneId": "L7", "receiptLayout": "Compact" }""");

        SettingsFile.SetScreenTheme(SettingsPath, look);

        var settings = PosSettings.LoadOrDefault(SettingsPath);
        Assert.Equal(look, settings.ScreenTheme);
        Assert.Equal("L7", settings.LaneId);
        Assert.Equal(Pos.Core.Domain.Printing.ReceiptLayout.Compact, settings.ReceiptLayout);

        // By name, so somebody opening the file can read it.
        Assert.Contains($"\"screenTheme\": \"{look}\"", File.ReadAllText(SettingsPath));
    }

    /// <summary>
    /// A misspelt look, or one from a later build, is only a colour: the lane still starts, in the
    /// night look, rather than refusing to bill over it.
    /// </summary>
    [Theory]
    [InlineData("\"Dusk\"")]
    [InlineData("\"\"")]
    [InlineData("9")]
    [InlineData("-1")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{ \"name\": \"Noon\" }")]
    public void ALookThisBuildDoesNotKnowOpensInTheNightLook(string value)
    {
        File.WriteAllText(SettingsPath, $$"""{ "laneId": "L3", "screenTheme": {{value}}, "lowStockPercent": 15 }""");

        var settings = PosSettings.LoadOrDefault(SettingsPath);

        Assert.Equal(ScreenTheme.Night, settings.ScreenTheme);
        Assert.Equal("L3", settings.LaneId);
        Assert.Equal(15m, settings.LowStockPercent);
    }

    [Theory]
    [InlineData("\"noon\"", ScreenTheme.Noon)]
    [InlineData("\"BYTIMEOFDAY\"", ScreenTheme.ByTimeOfDay)]
    [InlineData("2", ScreenTheme.Morning)]
    public void ALookWrittenByHandIsReadAsMeant(string value, ScreenTheme expected)
    {
        File.WriteAllText(SettingsPath, $$"""{ "screenTheme": {{value}} }""");

        Assert.Equal(expected, PosSettings.LoadOrDefault(SettingsPath).ScreenTheme);
    }

    [Fact]
    public void ALookThisBuildDoesNotHaveIsNotSaved()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SettingsFile.SetScreenTheme(SettingsPath, (ScreenTheme)9));
        Assert.False(File.Exists(SettingsPath));
    }
}
