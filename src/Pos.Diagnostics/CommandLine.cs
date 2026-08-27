namespace Pos.Diagnostics;

/// <summary>
/// Checking that a command was given options it actually takes.
/// </summary>
/// <remarks>
/// Anything unrecognised used to fall through to whatever the command does by default. On most
/// commands that is merely confusing. On <c>close-day</c> it was dangerous: <c>--lst</c> instead of
/// <c>--list</c> became an interactive close of the day, and with <c>--yes</c> alongside it would
/// have closed one outright. A day cannot be un-closed, so a typo must stop the command rather than
/// change what it means.
/// </remarks>
public static class CommandLine
{
    /// <summary>Taken by every command — it is how a run is pointed at a lane.</summary>
    private static readonly string[] ValuedEverywhere = ["--data"];

    /// <summary>
    /// The options each command takes: the ones that stand alone, and the ones followed by a value.
    /// </summary>
    /// <remarks>
    /// Written out rather than derived from the parsing below, because the list is short and the
    /// cost of getting it wrong is asymmetric. A missing entry refuses something legitimate and is
    /// noticed the first time anyone runs it; the alternative — accepting everything — is the
    /// failure that stayed invisible until it closed a day.
    /// </remarks>
    private static (string[] Standalone, string[] Valued) OptionsFor(string command) => command switch
    {
        "list-ports" => ([], []),
        "import-items" => (["--update", "--dry-run"], ["--file"]),
        "backup-db" => ([], ["--keep"]),
        "close-day" => (["--preview", "--yes", "--force", "--list", "--show", "--reprint"], ["--id", "--limit"]),
        "restore-db" => (["--yes"], ["--from"]),
        "void-invoice" => (["--yes"], ["--invoice", "--reason"]),
        "check-db" => (["--quick", "--vacuum"], []),
        "dashboard" => ([], ["--days", "--top", "--out"]),
        "dashboard-pin" => (["--clear"], []),
        "receipt-preview" => ([], ["--width", "--png"]),
        "test-hardware" => (["--printer", "--drawer", "--scanner", "--scale"], ["--seconds"]),
        _ => ([], []),
    };

    /// <summary>Whether this is a command whose options are known well enough to police.</summary>
    public static bool IsKnownCommand(string command)
    {
        var (standalone, valued) = OptionsFor(command);
        return standalone.Length > 0 || valued.Length > 0 || command == "list-ports";
    }

    /// <summary>
    /// The first option <paramref name="command"/> does not take, or null if they are all fine.
    /// </summary>
    /// <param name="args">The whole command line, command word included.</param>
    public static string? UnknownOption(string[] args, string command)
    {
        if (!IsKnownCommand(command))
            return null;

        var (standalone, valued) = OptionsFor(command);

        var takesValue = valued.Concat(ValuedEverywhere).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var standsAlone = standalone.ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var i = 1; i < args.Length; i++)
        {
            var token = args[i];

            if (!token.StartsWith("--", StringComparison.Ordinal))
                continue;

            // The token after a valued option is its value, and is skipped rather than examined —
            // a path or a reason is perfectly entitled to start with two dashes.
            if (takesValue.Contains(token))
            {
                i++;
                continue;
            }

            if (!standsAlone.Contains(token))
                return token;
        }

        return null;
    }
}
