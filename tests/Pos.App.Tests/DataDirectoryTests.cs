using System.IO;
using Pos.App;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// Which folder a run of the till bills against.
/// </summary>
/// <remarks>
/// Small, and worth pinning anyway. Getting this wrong does not throw or look broken — the till
/// opens perfectly well against the wrong lane and puts real-looking sales into somebody else's
/// books. The acceptance harness depends on it being right, and checks at runtime that a database
/// actually appeared where it asked for one, because a silent fallback here is unrecoverable.
/// </remarks>
public class DataDirectoryTests
{
    private static string Default => Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
        "RetailPOS");

    [Fact]
    public void WithNoArgumentsTheLaneUsesItsOwnFolder()
    {
        Assert.Equal(Default, App.ResolveDataDirectory([]));
    }

    [Fact]
    public void TheDataSwitchPointsTheRunSomewhereElse()
    {
        var chosen = App.ResolveDataDirectory(["--data", @"C:\lanes\bench"]);

        Assert.Equal(Path.GetFullPath(@"C:\lanes\bench"), chosen);
        Assert.NotEqual(Default, chosen);
    }

    [Fact]
    public void ARelativePathIsResolvedSoTheLaneDoesNotMoveWithTheWorkingDirectory()
    {
        var chosen = App.ResolveDataDirectory(["--data", "bench-lane"]);

        Assert.True(Path.IsPathFullyQualified(chosen));
        Assert.EndsWith("bench-lane", chosen);
    }

    [Theory]
    [InlineData("--DATA")]
    [InlineData("--Data")]
    public void TheSwitchIsNotCaseSensitive(string spelling)
    {
        Assert.Equal(Path.GetFullPath(@"C:\lanes\bench"), App.ResolveDataDirectory([spelling, @"C:\lanes\bench"]));
    }

    [Fact]
    public void TheSwitchIsFoundAmongOtherArguments()
    {
        Assert.Equal(
            Path.GetFullPath(@"C:\lanes\bench"),
            App.ResolveDataDirectory(["--verbose", "--data", @"C:\lanes\bench", "--other"]));
    }

    /// <summary>
    /// A switch with nothing after it falls back rather than billing against an empty path. The
    /// operator meant to point the lane somewhere, so this is worth noticing — but a till that
    /// refuses to start at a counter is worse than one that opens where it always has.
    /// </summary>
    [Fact]
    public void ASwitchWithNoPathFallsBackToTheLanesOwnFolder()
    {
        Assert.Equal(Default, App.ResolveDataDirectory(["--data"]));
        Assert.Equal(Default, App.ResolveDataDirectory(["--data", ""]));
        Assert.Equal(Default, App.ResolveDataDirectory(["--data", "   "]));
    }

    [Fact]
    public void AnUnrelatedArgumentIsIgnored()
    {
        Assert.Equal(Default, App.ResolveDataDirectory(["--data-dir", @"C:\nope"]));
    }
}
