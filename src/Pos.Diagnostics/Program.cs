using System.Text;
using Pos.Core.Analytics;
using Pos.Core.Configuration;
using Pos.Core.Data;
using Pos.Core.Domain.Import;
using Pos.Core.Domain.Printing;
using Pos.Core.Domain;
using Pos.Core.Hardware.Printing;
using Pos.Core.Hardware.Windows;
using Pos.Core.Logging;
using Pos.Diagnostics;

// `pos` — the lane's diagnostic tool. Separate from the till on purpose: checking a peripheral
// means printing test pages and firing drawers, which is not something to expose inside the
// billing screen where a cashier can reach it mid-sale.

// Say what encoding the output is in, rather than inheriting whatever code page the console
// happens to be on. A Z-report with Tamil headings is something a shopkeeper reasonably pipes to a
// file or sends to whoever supports the lane, and without this it arrives as question marks or as
// mojibake depending on which way it was read. Wrapped because a process with no console attached
// cannot set it, and that must not stop the tool running.
try
{
    Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}
catch (System.IO.IOException)
{
}

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
var flags = args.Skip(1).Select(a => a.ToLowerInvariant()).ToHashSet();

var dataDirectory = ResolveDataDirectory(args);
var settingsPath = Path.Combine(dataDirectory, "settings.json");

if (command is "help" or "--help" or "-h" or "/?")
{
    WriteHelp();
    return 0;
}

// A mistyped option must stop the command rather than quietly change what it means — see
// CommandLine for what that cost. Checked before the settings are even read, so a typo is refused
// on any lane rather than only on a lane that is set up correctly.
if (CommandLine.UnknownOption(args, command) is { } offending)
{
    Console.Error.WriteLine($"'{offending}' is not an option for '{command}'.");
    Console.Error.WriteLine();
    Console.Error.WriteLine($"Run 'pos help' to see what '{command}' takes.");
    return 2;
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

// Draws the labels the printer has no glyphs for. Shared by every command that produces a receipt,
// so what the tool prints is byte for byte what the till would have printed.
var rasterizer = CreateRasterizer(settings, log);
using var rasterizerLifetime = rasterizer as IDisposable;

var checks = new HardwareChecks(settings, Console.Out, Console.In, rasterizer);
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
        var composer = new ZReportComposer(settings.Store.ToProfile(), settings.Hardware.PrinterPaperWidthChars, settings.ReceiptLanguage);

        // Looking at a report that has already been taken, rather than taking a new one. Every
        // close is stored — the figures, the tenders, who was on the till — and until these three
        // existed the printed sheet was the only way to see any of it. A jammed printer at closing
        // time, or a sheet that goes missing, should not put a day's takings out of reach.
        if (flags.Contains("--list"))
        {
            var entries = closes.List(settings.LaneId, ParseIntOption(args, "--limit") ?? 30);

            Console.WriteLine();

            if (entries.Count == 0)
            {
                Console.WriteLine($"Lane {settings.LaneId} has not closed a day yet.");
                return 0;
            }

            Console.WriteLine($"  {"No",5}  {"Closed",-17}  {"Bills",7}  {"Net sales",13}  {"Cash",13}");

            // Grouped the way the report itself groups, not the way this machine's locale would.
            // A listing that says 2,06,625.29 beside a report that says 206,625.29 makes somebody
            // stop and check whether they are looking at the same figure.
            var invariant = System.Globalization.CultureInfo.InvariantCulture;

            foreach (var entry in entries)
            {
                Console.WriteLine(string.Format(invariant,
                    "  {0,5}  {1:dd-MM-yyyy HH:mm}  {2,7:N0}  {3,13:N2}  {4,13:N2}",
                    entry.Id, entry.ClosedAt, entry.InvoiceCount, entry.NetSales, entry.CashExpected));
            }

            Console.WriteLine();
            Console.WriteLine("  pos close-day --show --id <no>      read one on screen");
            Console.WriteLine("  pos close-day --reprint --id <no>   print a duplicate");
            return 0;
        }

        if (flags.Contains("--show") || flags.Contains("--reprint"))
        {
            var wanted = ParseIntOption(args, "--id");

            var report = wanted is { } id
                ? closes.FindById(id)
                : closes.FindLatest(settings.LaneId);

            if (report is null)
            {
                Console.Error.WriteLine(wanted is { } missing
                    ? $"There is no day-end report numbered {missing}."
                    : $"Lane {settings.LaneId} has not closed a day yet.");
                return 2;
            }

            var isReprint = flags.Contains("--reprint");

            Console.WriteLine();
            Console.WriteLine(composer.Compose(report, isReprint).ToPlainText());

            if (!isReprint)
                return 0;

            var toPrinter = PeripheralFactory.CreatePrinter(settings.Hardware, rasterizer);

            if (!toPrinter.IsConfigured)
            {
                Console.Error.WriteLine("This lane has no printer configured, so there is nothing to print to.");
                return 2;
            }

            var duplicate = toPrinter.Print(composer.Compose(report, isReprint: true).ToEscPos(raster: toPrinter.Raster));

            Console.WriteLine(duplicate.Succeeded
                ? $"Duplicate of report {report.Id} printed, marked as a reprint."
                : $"Did not print: {duplicate.Detail}");

            log.Info("tool", $"reprinted day-end report {report.Id}");
            return duplicate.Succeeded ? 0 : 1;
        }

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

        var printer = PeripheralFactory.CreatePrinter(settings.Hardware, rasterizer);

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

        var invoices = new InvoiceRepository(database, settings.InvoiceNumber.ToFormat());
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
            PeripheralFactory.CreateDrawer(settings.Hardware, PeripheralFactory.CreatePrinter(settings.Hardware, rasterizer)),
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

    case "dashboard":
    {
        // Turnover, margins, cost prices and best sellers — the figures an owner does not
        // necessarily want read off the counter screen. Locked only if the shop asked for it.
        if (!Unlock(settings.Security, log))
            return 2;

        // Read-only, and on its own connection. SQLite in WAL mode lets this run while the till is
        // billing, so a shopkeeper can look at the day's figures from the back room at four o'clock
        // without a cashier noticing.
        var days = Math.Clamp(ParseIntOption(args, "--days") ?? 30, 1, 3650);
        var to = DateTimeOffset.Now;
        var from = to.Date.AddDays(-(days - 1));

        var database = new PosDatabase(Path.Combine(dataDirectory, "pos.db"));
        database.EnsureMigrated();

        var data = new DashboardQuery(database).Gather(
            settings.LaneId,
            new DateTimeOffset(from, to.Offset),
            to,
            Math.Clamp(ParseIntOption(args, "--top") ?? 10, 1, 100));

        var outPath = ParseStringOption(args, "--out") ?? Path.Combine(dataDirectory, "dashboard.html");
        outPath = Path.GetFullPath(outPath);

        var directory = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(outPath, DashboardPage.Render(data, settings.Store.Name), new UTF8Encoding(true));

        // Grouped the way the page itself groups figures, rather than the way this machine happens
        // to be set up — otherwise the same command prints 2,06,625.29 on one till and 206,625.29
        // on the next, and the summary disagrees with the page it just wrote.
        var indian = System.Globalization.CultureInfo.GetCultureInfo("en-IN");

        Console.WriteLine();
        Console.WriteLine($"  Window        : {days} days to {to.ToString("dd MMM yyyy", indian)}");
        Console.WriteLine($"  Bills         : {data.Range.Bills.ToString("N0", indian)}");
        Console.WriteLine($"  Net sales     : {data.Range.NetSales.ToString("N2", indian)}");
        Console.WriteLine($"  Read in       : {data.Elapsed.TotalMilliseconds.ToString("N0", indian)} ms");
        Console.WriteLine();
        Console.WriteLine($"Saved to {outPath}");

        // The lock is on the command, and it cannot follow the page out of it. Saying so is the
        // difference between an owner who leaves it in the lane folder and one who does not.
        if (settings.Security.DashboardIsLocked)
        {
            Console.WriteLine();
            Console.WriteLine("  That file is not protected — anyone who can use this computer can");
            Console.WriteLine("  open it. Use --out to write it somewhere private, and delete it");
            Console.WriteLine("  when you are done.");
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("  Anyone who can use this computer can run this. To require a PIN:");
            Console.WriteLine("    pos dashboard-pin");
        }

        log.Info("tool", $"dashboard over {days} days: {data.Range.Bills} bills, read in {data.Elapsed.TotalMilliseconds:N0} ms");

        return 0;
    }

    case "dashboard-pin":
    {
        var clearing = flags.Contains("--clear");

        // Changing or removing the lock requires the current PIN. Without this the lock would be
        // decorative: anybody shut out by it could simply clear it and run the dashboard.
        if (settings.Security.DashboardIsLocked && !Unlock(settings.Security, log, "Current PIN: "))
            return 2;

        if (clearing)
        {
            if (!settings.Security.DashboardIsLocked)
            {
                Console.WriteLine();
                Console.WriteLine("The dashboard is not locked, so there is nothing to clear.");
                return 0;
            }

            SettingsFile.SetDashboardPin(settingsPath, null);
            log.Info("tool", "dashboard PIN cleared");

            Console.WriteLine();
            Console.WriteLine("The dashboard PIN has been removed. Anyone who can use this computer");
            Console.WriteLine("can now run `pos dashboard`.");
            return 0;
        }

        var chosen = ReadSecret("New PIN: ");

        if (chosen is null)
        {
            Console.Error.WriteLine("Nothing was entered. The PIN is unchanged.");
            return 2;
        }

        if (DashboardLock.Rejection(chosen) is { } why)
        {
            Console.Error.WriteLine(why);
            return 2;
        }

        // Typed twice because it is never echoed and there is no way to recover it — a mistyped PIN
        // would lock the owner out of their own figures until they hand-edited settings.json.
        if (ReadSecret("Again:   ") != chosen)
        {
            Console.Error.WriteLine("Those did not match. The PIN is unchanged.");
            return 2;
        }

        SettingsFile.SetDashboardPin(settingsPath, DashboardLock.Create(chosen));
        log.Info("tool", "dashboard PIN set");

        Console.WriteLine();
        Console.WriteLine($"Set. `pos dashboard` will ask for it from now on, on this lane.");
        Console.WriteLine();
        Console.WriteLine("  This keeps somebody from idly reading the shop's figures. It is not a");
        Console.WriteLine("  safe: whoever can log in to this computer can still open pos.db with");
        Console.WriteLine("  other software. Real separation needs a second Windows account —");
        Console.WriteLine("  SETTINGS.md explains how.");

        return 0;
    }

    case "receipt-preview":
    {
        // Renders the sample receipt as text without touching a printer, which is how the layout
        // gets checked on a bench or against a different paper width.
        var width = ParseWidth(args) ?? settings.Hardware.PrinterPaperWidthChars;
        var receipt = new ReceiptComposer(settings.Store.ToProfile(), width, settings.ReceiptLanguage)
            .Compose(SampleInvoice.Build(settings.LaneId, settings.InvoiceNumber.ToFormat()));

        Console.WriteLine();
        Console.WriteLine(receipt.ToPlainText());

        var raster = rasterizer is null || settings.Hardware.PrinterRasterMode == RasterMode.Never
            ? null
            : new RasterOptions(rasterizer, settings.Hardware.EffectivePaperWidthDots, settings.Hardware.PrinterRasterMode);

        Console.WriteLine($"({receipt.ToEscPos(raster: raster).Length} bytes of ESC/POS at {width} characters wide)");

        if (settings.ReceiptLanguage != ReceiptLanguage.English && raster is null)
            Console.WriteLine("WARNING: this lane prints Tamil labels but has no text renderer, so they will print as '?'.");

        // The text preview above counts characters, which says nothing about how Tamil will
        // actually come out. This renders the dots the printer would burn and saves them as an
        // image, so the layout can be looked at on a bench with no printer and no paper.
        if (ParseStringOption(args, "--png") is { } pngPath)
        {
            if (raster is null)
            {
                Console.Error.WriteLine("Nothing to draw: this lane has no text renderer, or rasterising is switched off.");
                return 2;
            }

            var pixels = receipt.ToBitmap(raster);
            ReceiptImage.SavePng(pixels, pngPath);
            Console.WriteLine($"Saved {pixels.Width}x{pixels.Height} dots to {pngPath}.");
        }

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

/// <summary>
/// Builds the text rasteriser, or null when the machine cannot supply one. A missing font engine
/// costs the Tamil on a receipt; it must not stop the tool running, because the commands that
/// matter most when something is wrong are the ones that touch no printer at all.
/// </summary>
static ITextRasterizer? CreateRasterizer(PosSettings settings, FileLog log)
{
    if (settings.Hardware.PrinterRasterMode == RasterMode.Never)
        return null;

    try
    {
        var size = settings.Hardware.ReceiptFontSizeDots > 0
            ? (float)settings.Hardware.ReceiptFontSizeDots
            : GdiTextRasterizer.DefaultEmSizeDots;

        return new GdiTextRasterizer(settings.Hardware.ReceiptFontFamily, size);
    }
    catch (Exception ex)
    {
        log.Error("tool", "could not start the receipt text renderer", ex);
        Console.Error.WriteLine($"Text rendering unavailable: {ex.Message}");
        return null;
    }
}

/// <summary>
/// Asks for the dashboard PIN, if this lane has one. True when the caller may proceed.
/// </summary>
/// <remarks>
/// Three attempts, then the command stops. A new run starts a fresh three, which is why the count
/// is not the real protection — the cost of each guess is (see <see cref="DashboardLock"/>). Three
/// is here so somebody standing at the counter cannot sit and try.
/// </remarks>
static bool Unlock(SecuritySettings security, FileLog log, string prompt = "PIN: ")
{
    if (!security.DashboardIsLocked)
        return true;

    const int attempts = 3;

    for (var attempt = 1; attempt <= attempts; attempt++)
    {
        var entered = ReadSecret(prompt);

        // No console and nothing piped in. Refusing beats waiting forever for a person who is not
        // there — this runs from scheduled scripts as well as from a keyboard.
        if (entered is null)
        {
            Console.Error.WriteLine("The dashboard needs a PIN, and there was nothing to read it from.");
            return false;
        }

        if (DashboardLock.Verify(entered, security.DashboardPin))
            return true;

        log.Warn("tool", $"dashboard PIN refused (attempt {attempt} of {attempts})");

        if (attempt < attempts)
            Console.Error.WriteLine($"That is not the PIN. {attempts - attempt} left.");
    }

    Console.Error.WriteLine("That is not the PIN.");
    return false;
}

/// <summary>
/// Reads a line without echoing it. Null when there was nothing to read.
/// </summary>
static string? ReadSecret(string prompt)
{
    Console.Write(prompt);

    // Piped or redirected input has no console to mask, and ReadKey would throw. Reading the line
    // plainly is right here: what protects a piped PIN is the pipe, not this.
    if (Console.IsInputRedirected)
    {
        var piped = Console.ReadLine();
        Console.WriteLine();
        return string.IsNullOrEmpty(piped) ? null : piped;
    }

    var typed = new System.Text.StringBuilder();

    while (true)
    {
        ConsoleKeyInfo key;

        try
        {
            key = Console.ReadKey(intercept: true);
        }
        catch (InvalidOperationException)
        {
            // No console to read keys from — a scheduled task, or a service. Nothing is going to
            // arrive, so say so rather than looping on an exception forever.
            Console.WriteLine();
            return null;
        }

        switch (key.Key)
        {
            case ConsoleKey.Enter:
                Console.WriteLine();
                return typed.Length == 0 ? null : typed.ToString();

            case ConsoleKey.Escape:
                Console.WriteLine();
                return null;

            case ConsoleKey.Backspace:
                if (typed.Length > 0)
                    typed.Length--;
                break;

            default:
                // Control characters are not PIN material; anything printable is, including
                // letters and punctuation, because nothing here requires it to be digits.
                if (!char.IsControl(key.KeyChar))
                    typed.Append(key.KeyChar);
                break;
        }
    }
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

          pos dashboard [--days N] [--top N] [--out <path>]
              The shop's figures as one HTML page: takings, the hourly rush,
              what sells, how customers paid, and GST by slab. Reads the books
              without writing to them, so it can be run while the till is busy.
              Asks for a PIN first if one has been set.

          pos dashboard-pin [--clear]
              Require a PIN before the dashboard will run, so a cashier cannot
              read the shop's turnover and margins. Asks for the current PIN
              before changing or clearing one. Keeps somebody out of the
              command; it does not encrypt the database — see SETTINGS.md.
              Defaults to the last 30 days.

          pos receipt-preview [--width N] [--png <path>]
              Renders a sample receipt as text. Touches no hardware, so it works
              on a bench and against any paper width. --png saves the dots the
              printer would actually burn, which is the only way to check that
              Tamil came out right without using a roll of paper.

          pos import-items --file <path> [--update] [--dry-run]
              Loads a catalogue CSV. Columns: sku, barcode, name, hsn_code,
              unit (Pcs/Kg), mrp, selling_price, gst_rate, is_weighed — in any
              order. Nothing is written unless the whole file is clean, so a
              rejected import leaves the catalogue exactly as it was.
              --update changes items already in the catalogue instead of
              rejecting them, which is what a price revision needs.

          pos close-day --list [--limit N]
              The day-end reports this lane has taken, most recent first.

          pos close-day --show [--id <no>]
              Reads one back on screen, no paper. Defaults to the last one.

          pos close-day --reprint [--id <no>]
              Prints a duplicate, marked as a reprint. For a sheet that was
              lost, or a printer that jammed at closing time.

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
