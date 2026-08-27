using Pos.Diagnostics;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// The `pos` tool refusing options a command does not take.
/// </summary>
/// <remarks>
/// This exists because of a specific near-miss. An unrecognised option was simply ignored, so
/// `pos close-day --lst` — one missing letter — fell through to the ordinary close path and offered
/// to close the day, and `pos close-day --yes --lst` would have closed one without asking. A close
/// cannot be undone. The general rule is worth having everywhere, but close-day is why it is here.
/// </remarks>
public class CommandLineTests
{
    // ---- The reason this exists ------------------------------------------------------------------

    [Theory]
    [InlineData("--lst")]
    [InlineData("--liast")]
    [InlineData("--sho")]
    [InlineData("--reprnt")]
    public void AMistypedCloseDayOptionIsRefusedRatherThanTreatedAsAClose(string typo)
    {
        Assert.Equal(typo, CommandLine.UnknownOption(["close-day", typo], "close-day"));
    }

    /// <summary>
    /// The dangerous case: the typo sits next to the option that skips the confirmation.
    /// </summary>
    [Fact]
    public void ATypoAlongsideYesIsStillRefused()
    {
        Assert.Equal("--lst", CommandLine.UnknownOption(["close-day", "--yes", "--lst"], "close-day"));
    }

    /// <summary>The first offending option is named, not the last.</summary>
    [Fact]
    public void TheOptionNamedIsTheFirstOneWrong()
    {
        Assert.Equal("--wrong", CommandLine.UnknownOption(["close-day", "--wrong", "--alsowrong"], "close-day"));
    }

    // ---- Real command lines still work -----------------------------------------------------------

    [Theory]
    // Every option of every command, as the runbook and help text give them.
    [InlineData("list-ports")]
    [InlineData("close-day", "--preview")]
    [InlineData("close-day", "--yes")]
    [InlineData("close-day", "--force")]
    [InlineData("close-day", "--list")]
    [InlineData("close-day", "--list", "--limit", "50")]
    [InlineData("close-day", "--show", "--id", "12")]
    [InlineData("close-day", "--reprint", "--id", "12")]
    [InlineData("import-items", "--file", "catalogue.csv", "--dry-run")]
    [InlineData("import-items", "--file", "catalogue.csv", "--update")]
    [InlineData("backup-db", "--keep", "30")]
    [InlineData("restore-db", "--from", "backups\\pos-20260825.db", "--yes")]
    [InlineData("void-invoice", "--invoice", "RM/26-27/11358", "--reason", "wrong item", "--yes")]
    [InlineData("check-db", "--quick")]
    [InlineData("check-db", "--vacuum")]
    [InlineData("dashboard", "--days", "30", "--top", "10", "--out", "dash.html")]
    [InlineData("dashboard-pin")]
    [InlineData("dashboard-pin", "--clear")]
    [InlineData("receipt-preview", "--width", "32", "--png", "receipt.png")]
    [InlineData("test-hardware", "--printer", "--drawer", "--scanner", "--scale", "--seconds", "20")]
    public void TheCommandLinesTheRunbookTellsPeopleToTypeAreAccepted(params string[] args)
    {
        Assert.Null(CommandLine.UnknownOption(args, args[0]));
    }

    /// <summary>--data points a run at a lane, and every command takes it.</summary>
    [Theory]
    [InlineData("close-day")]
    [InlineData("dashboard")]
    [InlineData("check-db")]
    [InlineData("list-ports")]
    [InlineData("test-hardware")]
    public void EveryCommandTakesData(string command)
    {
        Assert.Null(CommandLine.UnknownOption([command, "--data", "D:\\lane"], command));
    }

    // ---- Values are values, not options ----------------------------------------------------------

    /// <summary>
    /// A value is skipped rather than inspected. A void reason is free text a cashier types, and it
    /// is entitled to begin with two dashes without the command refusing to run.
    /// </summary>
    [Fact]
    public void AValueThatLooksLikeAnOptionIsStillAValue()
    {
        Assert.Null(CommandLine.UnknownOption(
            ["void-invoice", "--invoice", "RM/26-27/11358", "--reason", "--customer changed mind"],
            "void-invoice"));
    }

    [Fact]
    public void APathThatBeginsWithDashesIsStillAPath()
    {
        Assert.Null(CommandLine.UnknownOption(["import-items", "--file", "--odd-name.csv"], "import-items"));
    }

    /// <summary>Positional words are not options and are left to the command to interpret.</summary>
    [Fact]
    public void SomethingThatIsNotAnOptionIsNotChecked()
    {
        Assert.Null(CommandLine.UnknownOption(["close-day", "list"], "close-day"));
    }

    // ---- Wrong command, right option ------------------------------------------------------------

    /// <summary>
    /// An option that is real elsewhere is still wrong here — `--vacuum` on a close would otherwise
    /// have been ignored in exactly the way that started this.
    /// </summary>
    [Fact]
    public void AnOptionBorrowedFromAnotherCommandIsRefused()
    {
        Assert.Equal("--vacuum", CommandLine.UnknownOption(["close-day", "--vacuum"], "close-day"));
        Assert.Equal("--list", CommandLine.UnknownOption(["check-db", "--list"], "check-db"));
        Assert.Equal("--dry-run", CommandLine.UnknownOption(["backup-db", "--dry-run"], "backup-db"));
    }

    [Fact]
    public void ACommandThatTakesNoOptionsAcceptsNone()
    {
        Assert.Equal("--verbose", CommandLine.UnknownOption(["list-ports", "--verbose"], "list-ports"));
    }

    /// <summary>
    /// There is deliberately no --pin. A PIN passed as an option would sit in the shell's history
    /// and in the process list, where the person it is keeping out can read it.
    /// </summary>
    [Fact]
    public void ThePinCannotBePassedOnTheCommandLine()
    {
        Assert.Equal("--pin", CommandLine.UnknownOption(["dashboard", "--pin", "7412"], "dashboard"));
        Assert.Equal("--pin", CommandLine.UnknownOption(["dashboard-pin", "--pin", "7412"], "dashboard-pin"));
    }

    // ---- Not overreaching -------------------------------------------------------------------------

    /// <summary>
    /// An unrecognised command is left alone. It has its own error further on, and complaining about
    /// its options first would explain the wrong problem.
    /// </summary>
    [Fact]
    public void AnUnknownCommandIsNotPolicedForOptions()
    {
        Assert.Null(CommandLine.UnknownOption(["frobnicate", "--anything"], "frobnicate"));
    }

    [Fact]
    public void TheCommandWordItselfIsNeverTreatedAsAnOption()
    {
        // Bare command, and a command word that starts with dashes — neither is an option.
        Assert.Null(CommandLine.UnknownOption(["close-day"], "close-day"));
        Assert.Null(CommandLine.UnknownOption(["--help"], "--help"));
    }

    /// <summary>Options are matched however they were typed.</summary>
    [Fact]
    public void CaseDoesNotDecideWhetherAnOptionIsRecognised()
    {
        Assert.Null(CommandLine.UnknownOption(["close-day", "--LIST"], "close-day"));
        Assert.Null(CommandLine.UnknownOption(["close-day", "--Show", "--Id", "3"], "close-day"));
    }
}
