using Pos.Core.Configuration;
using Pos.Core.Domain.Printing;
using Pos.Diagnostics;

// `pos` — the lane's diagnostic tool. Separate from the till on purpose: checking a peripheral
// means printing test pages and firing drawers, which is not something to expose inside the
// billing screen where a cashier can reach it mid-sale.

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
var flags = args.Skip(1).Select(a => a.ToLowerInvariant()).ToHashSet();

var dataDirectory = ResolveDataDirectory(args);
var settingsPath = Path.Combine(dataDirectory, "settings.json");

if (command is "help" or "--help" or "-h" or "/?")
{
    WriteHelp();
    return 0;
}

PosSettings settings;

try
{
    settings = PosSettings.LoadOrDefault(settingsPath);
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

Console.WriteLine($"RetailPOS diagnostics — lane {settings.LaneId}");
Console.WriteLine($"Settings: {(File.Exists(settingsPath) ? settingsPath : "defaults (no settings file found)")}");

var checks = new HardwareChecks(settings, Console.Out, Console.In);
var window = ParseWindow(args) ?? TimeSpan.FromSeconds(10);

switch (command)
{
    case "list-ports":
        checks.ListPorts();
        return 0;

    case "receipt-preview":
    {
        // Renders the sample receipt as text without touching a printer, which is how the layout
        // gets checked on a bench or against a different paper width.
        var width = ParseWidth(args) ?? settings.Hardware.PrinterPaperWidthChars;
        var receipt = new ReceiptComposer(settings.Store.ToProfile(), width)
            .Compose(SampleInvoice.Build(settings.LaneId));

        Console.WriteLine();
        Console.WriteLine(receipt.ToPlainText());
        Console.WriteLine($"({receipt.ToEscPos().Length} bytes of ESC/POS at {width} characters wide)");
        return 0;
    }

    case "test-hardware":
    {
        var all = flags.Count == 0 || flags.Contains("--all");
        var results = new List<(string Peripheral, CheckResult Result)>();

        if (all || flags.Contains("--printer"))
            results.Add(("Printer", checks.Printer()));

        if (all || flags.Contains("--drawer"))
            results.Add(("Cash drawer", checks.Drawer()));

        if (all || flags.Contains("--scanner"))
            results.Add(("Scanner", checks.Scanner(window)));

        if (all || flags.Contains("--scale"))
            results.Add(("Scale", checks.Scale(window)));

        if (results.Count == 0)
        {
            Console.Error.WriteLine("Nothing selected. Pass --printer, --drawer, --scanner, --scale, or nothing for all.");
            return 2;
        }

        Console.WriteLine();
        Console.WriteLine("Summary");
        Console.WriteLine("-------");

        foreach (var (peripheral, result) in results)
            Console.WriteLine($"  {peripheral,-14} {Describe(result)}");

        // A peripheral that is not configured is not a failure — plenty of lanes have no scale.
        var failed = results.Count(r => r.Result == CheckResult.Failed);

        Console.WriteLine();
        Console.WriteLine(failed == 0
            ? "All configured peripherals passed."
            : $"{failed} peripheral(s) failed.");

        return failed == 0 ? 0 : 1;
    }

    default:
        Console.Error.WriteLine($"Unknown command '{command}'.");
        Console.Error.WriteLine();
        WriteHelp();
        return 2;
}

static string Describe(CheckResult result) => result switch
{
    CheckResult.Passed => "passed",
    CheckResult.Failed => "FAILED",
    CheckResult.NotConfigured => "not configured — skipped",
    _ => "needs a person to confirm",
};

static TimeSpan? ParseWindow(string[] args) => ParseIntOption(args, "--seconds") is { } seconds
    ? TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 300))
    : null;

static int? ParseWidth(string[] args) => ParseIntOption(args, "--width");

static int? ParseIntOption(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out var value))
            return value;
    }

    return null;
}

static string ResolveDataDirectory(string[] args)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i].Equals("--data", StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }

    return Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RetailPOS");
}

static void WriteHelp()
{
    Console.WriteLine("""
        RetailPOS lane diagnostics

          pos test-hardware [--printer] [--drawer] [--scanner] [--scale]
              Checks the lane's peripherals. With no flags it checks all of them.
              The printer and drawer checks ask you to confirm what physically
              happened, because no software can see paper come out of a printer.

          pos receipt-preview [--width N]
              Renders a sample receipt as text. Touches no hardware, so it works
              on a bench and against any paper width.

          pos list-ports
              Lists the serial ports this machine can see.

        Options

          --seconds N    How long the scanner and scale checks listen. Default 10.
          --width N      Characters per line for receipt-preview. Default: the
                         configured printer width.
          --data PATH    Where settings.json lives. Defaults to the lane's own
                         folder under LocalApplicationData.

        Exit codes: 0 all good, 1 a peripheral failed, 2 bad usage or settings.
        """);
}
