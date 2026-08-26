using Pos.Core.Configuration;
using Pos.Core.Data;
using Pos.Core.Domain.Import;
using Pos.Core.Domain.Printing;
using Pos.Core.Domain;
using Pos.Core.Hardware.Printing;
using Pos.Core.Logging;
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

// The tool writes to the same log as the till, so a lane's history reads as one story rather than
// two — a restore or a void shows up alongside the sales around it.
using var log = new FileLog(Path.Combine(dataDirectory, "logs"));
log.Info("tool", $"pos {string.Join(' ', args)}");

var checks = new HardwareChecks(settings, Console.Out, Console.In);
var window = ParseWindow(args) ?? TimeSpan.FromSeconds(10);

switch (command)
{
    case "list-ports":
        checks.ListPorts();
        return 0;

    case "import-items":
    {
        var file = ParseStringOption(args, "--file");

        if (file is null)
        {
            Console.Error.WriteLine("import-items needs --file <path>.");
            return 2;
        }

        if (!File.Exists(file))
        {
            Console.Error.WriteLine($"No such file: {file}");
            return 2;
        }

        var database = new PosDatabase(Path.Combine(dataDirectory, "pos.db"));
        database.EnsureMigrated();

        var items = new ItemRepository(database);
        var before = items.Count();
        var updating = flags.Contains("--update");
        var dryRun = flags.Contains("--dry-run");

        Console.WriteLine();
        Console.WriteLine($"Importing {file}");
        Console.WriteLine($"  Catalogue holds {before:N0} items");
        Console.WriteLine($"  Mode: {(dryRun ? "dry run, nothing will be written" : updating ? "insert new and update existing" : "insert only")}");

        using var reader = ItemCsvParser.OpenText(file);
        var result = new ItemImporter(items).Import(reader, updating, dryRun);

        Console.WriteLine($"  Rows read: {result.RowsRead:N0}");

        if (!result.IsClean)
        {
            // Every problem at once. A shopkeeper fixing a spreadsheet wants the whole list, not
            // the first line that failed.
            Console.WriteLine();
            Console.WriteLine($"  {result.Problems.Count:N0} problem(s) — nothing was imported:");
            Console.WriteLine();

            const int shown = 50;

            foreach (var problem in result.Problems.Take(shown))
                Console.WriteLine($"    {problem}");

            if (result.Problems.Count > shown)
                Console.WriteLine($"    ... and {result.Problems.Count - shown:N0} more.");

            Console.WriteLine();
            Console.WriteLine("  Fix the file and run again. The catalogue is unchanged.");
            return 1;
        }

        if (dryRun)
        {
            Console.WriteLine($"  Would insert {result.Inserted:N0} and update {result.Updated:N0}. Nothing written.");
            return 0;
        }

        Console.WriteLine($"  Inserted {result.Inserted:N0}, updated {result.Updated:N0}.");
        Console.WriteLine($"  Catalogue now holds {items.Count():N0} items.");
        return 0;
    }

    case "backup-db":
    {
        var databasePath = Path.Combine(dataDirectory, "pos.db");

        if (!File.Exists(databasePath))
        {
            Console.Error.WriteLine($"No database at {databasePath}.");
            return 2;
        }

        var backup = new DatabaseBackup(new PosDatabase(databasePath), Path.Combine(dataDirectory, "backups"));
        var keep = ParseIntOption(args, "--keep") ?? DatabaseBackup.DefaultKeep;

        Console.WriteLine();
        Console.WriteLine($"Backing up {databasePath}");

        var result = backup.Create(DateTimeOffset.Now, keep);

        foreach (var problem in result.Problems)
            Console.WriteLine($"  {problem}");

        if (!result.Succeeded)
            return 1;

        Console.WriteLine($"  Wrote {result.Path}");
        Console.WriteLine($"  {result.Bytes / 1024:N0} KB, verified.");

        if (result.Pruned.Count > 0)
            Console.WriteLine($"  Removed {result.Pruned.Count} older snapshot(s), keeping {keep}.");

        Console.WriteLine($"  {backup.Existing().Count} snapshot(s) on hand.");
        return 0;
    }

    case "close-day":
    {
        var databasePath = Path.Combine(dataDirectory, "pos.db");
        var database = new PosDatabase(databasePath);
        database.EnsureMigrated();

        var heldBills = new HeldBillRepository(database);
        var closes = new DayCloseRepository(database, heldBills);
        var composer = new ZReportComposer(settings.Store.ToProfile(), settings.Hardware.PrinterPaperWidthChars);

        // Show it before committing to it. A Z-report cannot be taken back.
        var preview = closes.Preview(settings.LaneId, DateTimeOffset.Now);

        Console.WriteLine();
        Console.WriteLine(composer.Compose(preview).ToPlainText());

        if (flags.Contains("--preview"))
            return 0;

        if (preview.TookNothing && !flags.Contains("--force"))
        {
            Console.WriteLine("Nothing has been sold since the last close. Pass --force to close anyway.");
            return 0;
        }

        if (!flags.Contains("--yes"))
        {
            Console.Write("Close the day? This cannot be undone. [y/N] ");
            var answer = Console.ReadLine();

            if (answer is null || !answer.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Left open.");
                return 0;
            }
        }

        var closed = closes.Close(settings.LaneId, DateTimeOffset.Now);
        Console.WriteLine($"Closed. Report no {closed.Id}, {closed.InvoiceCount} invoice(s), net {closed.NetSales:N2}.");

        // The day's books are worth a snapshot before anyone goes home.
        var backup = new DatabaseBackup(database, Path.Combine(dataDirectory, "backups")).Create(DateTimeOffset.Now);

        Console.WriteLine(backup.Succeeded
            ? $"Backed up to {backup.Path} ({backup.Bytes / 1024:N0} KB, verified)."
            : $"BACKUP FAILED: {string.Join("; ", backup.Problems)}");

        var printer = PeripheralFactory.CreatePrinter(settings.Hardware);

        if (printer.IsConfigured)
        {
            var outcome = printer.Print(composer.Compose(closed).ToEscPos());
            Console.WriteLine(outcome.Succeeded ? "Report printed." : $"Report did not print: {outcome.Detail}");
        }

        return backup.Succeeded ? 0 : 1;
    }

    case "restore-db":
    {
        var snapshot = ParseStringOption(args, "--from");

        if (snapshot is null)
        {
            Console.Error.WriteLine("restore-db needs --from <snapshot path>.");
            Console.Error.WriteLine($"Snapshots live in {Path.Combine(dataDirectory, "backups")}.");
            return 2;
        }

        var livePath = Path.Combine(dataDirectory, "pos.db");
        var restore = new DatabaseRestore(livePath);

        Console.WriteLine();
        Console.WriteLine($"Restoring   {livePath}");
        Console.WriteLine($"       from {snapshot}");

        var inspection = restore.Inspect(snapshot);

        if (!inspection.IsHealthy)
        {
            Console.Error.WriteLine($"  The snapshot is not usable: {inspection}");
            Console.Error.WriteLine("  Nothing was changed. Try an older snapshot.");
            return 1;
        }

        Console.WriteLine("  Snapshot checked and sound.");

        if (DatabaseBackup.TimestampOf(snapshot) is { } takenAt)
            Console.WriteLine($"  Taken {takenAt:dd MMM yyyy HH:mm}. Everything sold since then will be gone.");

        if (!flags.Contains("--yes"))
        {
            Console.WriteLine();
            Console.WriteLine("  Close the till before restoring.");
            Console.Write("  Restore now? [y/N] ");

            var answer = Console.ReadLine();

            if (answer is null || !answer.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("  Left alone.");
                return 0;
            }
        }

        var result = restore.Restore(snapshot, DateTimeOffset.Now);

        Console.WriteLine($"  {result.Detail}");

        if (result.MovedAsidePath is { } aside)
            Console.WriteLine($"  The previous database was kept at {aside} — it is not deleted.");

        return result.Succeeded ? 0 : 1;
    }

    case "void-invoice":
    {
        var number = ParseStringOption(args, "--invoice");

        if (number is null)
        {
            Console.Error.WriteLine("void-invoice needs --invoice <number>.");
            return 2;
        }

        var database = new PosDatabase(Path.Combine(dataDirectory, "pos.db"));
        database.EnsureMigrated();

        var invoices = new InvoiceRepository(database);
        var existing = invoices.FindByInvoiceNo(number);

        if (existing is null)
        {
            Console.Error.WriteLine($"There is no invoice numbered {number}.");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine($"  {existing.InvoiceNo}  {existing.Sale.CreatedAt:dd MMM yyyy HH:mm}  {existing.GrandTotal:N2}");
        Console.WriteLine($"  {existing.Sale.Lines.Count} line(s), {existing.Sale.Payments.Count} payment(s)");

        if (existing.IsVoided)
        {
            Console.Error.WriteLine($"  Already voided at {existing.VoidedAt:dd MMM yyyy HH:mm}.");
            return 1;
        }

        if (invoices.IsReported(number))
        {
            Console.Error.WriteLine("  This invoice has already appeared on a day-end report and cannot be voided.");
            Console.Error.WriteLine("  A closed day is corrected with a credit note, not by changing a figure that has been filed.");
            return 1;
        }

        if (!flags.Contains("--yes"))
        {
            Console.Write("  Void this sale? [y/N] ");
            var answer = Console.ReadLine();

            if (answer is null || !answer.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("  Left alone.");
                return 0;
            }
        }

        var checkout = new CheckoutService(
            invoices,
            new CustomerRepository(database),
            PeripheralFactory.CreateDrawer(settings.Hardware, PeripheralFactory.CreatePrinter(settings.Hardware)),
            settings.LoyaltyRules,
            TimeProvider.System,
            log: log);

        var voided = checkout.VoidSale(number, ParseStringOption(args, "--reason"));

        Console.WriteLine($"  {voided.Invoice.InvoiceNo} voided. It stays in the books, marked cancelled, and is left out of takings.");

        if (voided.LoyaltyReversed)
            Console.WriteLine($"  Loyalty points put back — balance is now {voided.NewLoyaltyBalance}.");

        return 0;
    }

    case "check-db":
    {
        var databasePath = Path.Combine(dataDirectory, "pos.db");

        if (!File.Exists(databasePath))
        {
            Console.Error.WriteLine($"No database at {databasePath}.");
            return 2;
        }

        var database = new PosDatabase(databasePath);
        var thorough = !flags.Contains("--quick");

        Console.WriteLine();
        Console.WriteLine($"Checking {databasePath}");
        Console.WriteLine($"  {new FileInfo(databasePath).Length / 1024:N0} KB, {(thorough ? "full" : "quick")} check");

        var report = database.CheckIntegrity(thorough);

        if (report.IsHealthy)
        {
            Console.WriteLine("  No problems found.");
        }
        else
        {
            Console.WriteLine("  PROBLEMS FOUND:");

            foreach (var problem in report.Problems)
                Console.WriteLine($"    {problem}");

            // Deliberately not offering to repair. A damaged till database is the shop's book of
            // account, and the right first move is a copy of the file and a look at the backup,
            // not a tool that rewrites it.
            Console.WriteLine();
            Console.WriteLine("  Take a copy of the file before doing anything else, then restore from backup.");
        }

        if (report.IsHealthy && flags.Contains("--vacuum"))
        {
            Console.WriteLine("  Compacting...");
            database.Vacuum();
            Console.WriteLine($"  Now {new FileInfo(databasePath).Length / 1024:N0} KB.");
        }

        return report.IsHealthy ? 0 : 1;
    }

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

static string? ParseStringOption(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }

    return null;
}

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

          pos import-items --file <path> [--update] [--dry-run]
              Loads a catalogue CSV. Columns: sku, barcode, name, hsn_code,
              unit (Pcs/Kg), mrp, selling_price, gst_rate, is_weighed — in any
              order. Nothing is written unless the whole file is clean, so a
              rejected import leaves the catalogue exactly as it was.
              --update changes items already in the catalogue instead of
              rejecting them, which is what a price revision needs.

          pos close-day [--preview] [--yes] [--force]
              Prints the lane's Z-report and closes the day. Shows the report
              first, because a close cannot be undone. Takes a verified backup
              as part of closing.

          pos backup-db [--keep N]
              Takes a verified snapshot into the lane's backups folder, keeping
              the most recent N (default 30). Does not block anyone billing.

          pos void-invoice --invoice <number> [--reason <text>] [--yes]
              Cancels a sale. The record stays and the number stays used; the
              takings and the tax do not. Loyalty points are put back. Refused
              once the invoice has been on a day-end report — a closed day is
              corrected with a credit note.

          pos restore-db --from <snapshot> [--yes]
              Puts a snapshot back as the live database. Checks it first, and
              renames the database it replaces rather than deleting it.
              Everything sold since the snapshot was taken will be gone.

          pos check-db [--quick] [--vacuum]
              Checks the lane's database for damage. Run it before a trading
              day, not after a problem: corruption on a page nobody has read
              is silent until someone reads it. --vacuum compacts the file
              afterwards, and only if the check passed.

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
