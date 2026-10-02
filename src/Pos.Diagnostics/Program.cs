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

// The no-tax build never charges tax, whatever the settings file says — so a preview or a
// reprint from this tool shows the same document the till would actually issue.
settings.TaxMode = ProductVariant.Resolve(settings.TaxMode);

Console.WriteLine($"RetailPOS diagnostics — lane {settings.LaneId}");
Console.WriteLine($"Settings: {(File.Exists(settingsPath) ? settingsPath : "defaults (no settings file found)")}");

if (ProductVariant.ChargesNoTax)
    Console.WriteLine("Build: no-tax — this lane issues a BILL OF SUPPLY.");

// The tool writes to the same log as the till, so a lane's history reads as one story rather than
// two — a restore or a void shows up alongside the sales around it.
using var log = new FileLog(Path.Combine(dataDirectory, "logs"));
log.Info("tool", $"pos {string.Join(' ', args)}");

// Draws the labels the printer has no glyphs for. Shared by every command that produces a receipt,
// so what the tool prints is byte for byte what the till would have printed.
var rasterizer = CreateRasterizer(settings, log);
using var rasterizerLifetime = rasterizer as IDisposable;

// The same checks the owner's screen runs, wired to a console instead of to dialogs. One
// implementation, so a lane signed off from a window is the lane the sign-off sheet describes.
var checks = new PeripheralCheck(
    settings,
    report: line => Console.WriteLine(line.Length == 0 ? string.Empty : "  " + line),
    confirm: question =>
    {
        Console.Write($"  {question} [y/N] ");
        var answer = Console.ReadLine();

        return answer is not null && answer.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);
    },
    rasterizer);
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
            Console.WriteLine($"  {Plural.Of(result.Problems.Count, "problem")} — nothing was imported:");
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
            Console.WriteLine($"  Removed {Plural.Of(result.Pruned.Count, "older snapshot")}, keeping {keep}.");

        Console.WriteLine($"  {Plural.Of(backup.Existing().Count, "snapshot")} on hand.");
        return 0;
    }

    case "close-day":
    {
        var databasePath = Path.Combine(dataDirectory, "pos.db");
        var database = new PosDatabase(databasePath);
        database.EnsureMigrated();

        var heldBills = new HeldBillRepository(database);
        var closes = new DayCloseRepository(database, heldBills);
        var composer = new ZReportComposer(settings.Store.ToProfile(), settings.Hardware.PrinterPaperWidthChars, settings.ReceiptLanguage, settings.TaxMode);

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

            Console.WriteLine($"  {"No",5}  {"Closed",-17}  {"Bills",7}  {"Net sales",13}  {"Cash",13}  {"Counted",13}  {"Over/short",11}");

            // Grouped the way the report itself groups, not the way this machine's locale would.
            // A listing that says 2,06,625.29 beside a report that says 206,625.29 makes somebody
            // stop and check whether they are looking at the same figure.
            var invariant = System.Globalization.CultureInfo.InvariantCulture;

            foreach (var entry in entries)
            {
                Console.WriteLine(string.Format(invariant,
                    "  {0,5}  {1:dd-MM-yyyy HH:mm}  {2,7:N0}  {3,13:N2}  {4,13:N2}  {5,13}  {6,11}",
                    entry.Id, entry.ClosedAt, entry.InvoiceCount, entry.NetSales, entry.CashExpected,
                    entry.CashCounted is { } count ? count.ToString("N2", invariant) : "not counted",
                    entry.CashDifference is { } difference ? difference.ToString("+0.00;-0.00;0.00", invariant) : string.Empty));
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

        // Credit paid back is money to report even on a day with no sales, so it does not need
        // --force: that flag is for closing a day with nothing in it at all.
        if (preview.TookNothing && !preview.MovedMoneyWithoutSales && !flags.Contains("--force"))
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

        // What was counted in the drawer, for a close made from here: the till asks for it before
        // showing the drawer figure; a script passes it.
        var counted = ParseAmountOption(args, "--counted");

        if (counted is < 0m)
        {
            Console.Error.WriteLine("--counted is what is in the drawer, which cannot be less than nothing.");
            return 2;
        }

        var closed = closes.Close(settings.LaneId, DateTimeOffset.Now, counted, counted is null ? null : "pos tool");
        Console.WriteLine($"Closed. Report no {closed.Id}, {Plural.Of(closed.InvoiceCount, "invoice")}, net {closed.NetSales:N2}.");

        if (closed.CashDifference is { } over)
        {
            Console.WriteLine(over switch
            {
                0m => "The drawer counted exactly right.",
                > 0m => $"The drawer is over by {over:N2}.",
                _ => $"The drawer is short by {-over:N2}.",
            });
        }

        // What to reorder, gathered now and printed at the foot of this report only. A reprint
        // months later must not carry today's shelves under last spring's takings.
        var lowStock = new StockRepository(database, () => settings.LowStockPercent).ListLow(50);
        var dates = new ExpiryRepository(database).Expiring(DateOnly.FromDateTime(DateTime.Today)).Where(w => w.DaysLeft <= 7).ToList();

        // The day's books are worth a snapshot before anyone goes home.
        var backup = new DatabaseBackup(database, Path.Combine(dataDirectory, "backups")).Create(DateTimeOffset.Now);

        Console.WriteLine(backup.Succeeded
            ? $"Backed up to {backup.Path} ({backup.Bytes / 1024:N0} KB, verified)."
            : $"BACKUP FAILED: {string.Join("; ", backup.Problems)}");

        var printer = PeripheralFactory.CreatePrinter(settings.Hardware, rasterizer);

        if (printer.IsConfigured)
        {
            var outcome = printer.Print(composer.Compose(closed, isReprint: false, lowStock, dates).ToEscPos());
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
        Console.WriteLine($"  {Plural.Of(existing.Sale.Lines.Count, "line")}, {Plural.Of(existing.Sale.Payments.Count, "payment")}");

        // Whose sale it was, so a void is confirmed against the right customer and a bill that has
        // loyalty points on it is recognised as one before the points are taken back.
        if (existing.Sale.Customer is { } customer)
        {
            Console.WriteLine(customer.Name is { Length: > 0 } name
                ? $"  for {name}, {customer.MobileNo}"
                : $"  for {customer.MobileNo}");
        }

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
            log: log,

            // So a void here puts the goods back on the shelf count, exactly as one at the till does.
            stock: new StockRepository(database, () => settings.LowStockPercent));

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

        var data = new DashboardQuery(database, settings.LowStockPercent).Gather(
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

        if (data.Returns.Count > 0)
            Console.WriteLine($"  Returns       : {Plural.Of(data.Returns.Count, "credit note")}, {data.Returns.Value.ToString("N2", indian)} refunded");

        if (data.Expenses.Count > 0)
            Console.WriteLine($"  Expenses      : {data.ExpensesTotal.ToString("N2", indian)}");

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

    case "gst-return":
    {
        // The shop's turnover, so behind the same lock as the dashboard.
        if (!Unlock(settings.Security, log))
            return 2;

        // Last month by default: a return is filed for a month that has finished.
        var monthText = ParseStringOption(args, "--month");
        DateOnly month;

        if (monthText is null)
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            month = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
        }
        else if (!DateOnly.TryParseExact(monthText + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                     System.Globalization.DateTimeStyles.None, out month))
        {
            Console.Error.WriteLine($"'{monthText}' is not a month. Write it as 2026-09.");
            return 2;
        }

        var database = new PosDatabase(Path.Combine(dataDirectory, "pos.db"));
        database.EnsureMigrated();

        var data = new GstReturnQuery(database).Gather(settings.LaneId, month, settings.OutletStateCode);

        var outPath = Path.GetFullPath(ParseStringOption(args, "--out")
            ?? Path.Combine(dataDirectory, "gst", GstReturnFiles.Stem(data) + ".html"));

        var files = GstReturnFiles.Write(data, outPath, settings.Store.Name, settings.Store.Gstin);
        var indian = System.Globalization.CultureInfo.GetCultureInfo("en-IN");

        Console.WriteLine();
        Console.WriteLine($"  Month          : {month.ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture)}, lane {settings.LaneId}");
        Console.WriteLine($"  Tax invoices   : {data.TaxInvoices.ToString("N0", indian)}");
        Console.WriteLine($"  Taxable value  : {data.TaxableValue.ToString("N2", indian)}");
        Console.WriteLine($"  CGST           : {data.Cgst.ToString("N2", indian)}");
        Console.WriteLine($"  SGST           : {data.Sgst.ToString("N2", indian)}");

        if (data.Igst != 0m)
            Console.WriteLine($"  IGST           : {data.Igst.ToString("N2", indian)}");

        Console.WriteLine($"  Nil rated      : {data.NilRated.ToString("N2", indian)}");
        Console.WriteLine($"  HSN codes      : {data.Hsn.Count}");

        if (data.CreditNotes > 0)
            Console.WriteLine($"  Returns        : {Plural.Of(data.CreditNotes, "credit note")}, {data.CreditNotesValue.ToString("N2", indian)}, netted out");

        foreach (var run in data.Documents)
        {
            Console.WriteLine(run.Nature == GstDocumentSeries.CreditNotes
                ? $"  Credit notes   : {run.From} to {run.To}, {run.Total} issued"
                : $"  Bills          : {run.From} to {run.To}, {run.Total} issued, {run.Cancelled} cancelled");
        }

        foreach (var warning in data.Warnings)
        {
            Console.WriteLine();
            Console.WriteLine($"  NOTE: {warning}");
        }

        Console.WriteLine();

        foreach (var file in files)
            Console.WriteLine($"Saved {file}");

        log.Info("tool", $"GST return for {month:yyyy-MM}: {data.TaxInvoices} invoices, taxable {data.TaxableValue:0.00}, {files.Count} files");

        return 0;
    }

    case "dead-stock":
    {
        // Counted items on the shelf that have stopped selling: the Stock tab's "Not selling".
        var days = ParseIntOption(args, "--days") ?? DeadStock.Days;

        if (!DeadStock.IsValidDays(days))
        {
            Console.Error.WriteLine($"Dead stock is not sold for 14 to 365 days, not {days}.");
            return 2;
        }

        var database = new PosDatabase(Path.Combine(dataDirectory, "pos.db"));
        database.EnsureMigrated();

        var items = new DeadStockRepository(database).NotSelling(DateOnly.FromDateTime(DateTime.Today), days);
        var indian = System.Globalization.CultureInfo.GetCultureInfo("en-IN");

        Console.WriteLine();

        if (items.Count == 0)
        {
            Console.WriteLine($"  Everything counted on the shelf has sold in the last {days} days.");
            return 0;
        }

        foreach (var item in items)
        {
            var name = item.Name.Length > 30 ? item.Name[..29] + "…" : item.Name;
            var sold = item.LastSold is { } last ? last.ToString("dd-MM-yyyy", System.Globalization.CultureInfo.InvariantCulture) : "never";
            var tied = item.TiedUp?.ToString("N2", indian) ?? "—";

            Console.WriteLine($"  {name,-32}{item.Have.ToString("0.###", indian),8}  {sold,-11}{tied,12}  {item.Advice}");
        }

        Console.WriteLine();
        Console.WriteLine($"  {Plural.Of(items.Count, "item")} not sold in {days} days, {items.Sum(i => i.TiedUp ?? 0m).ToString("N2", indian)} tied up at cost.");
        return 0;
    }

    case "offers":
    {
        // The shop's offers: what runs and what it gave, the sheet that sets them, and a trial bill.
        var database = new PosDatabase(Path.Combine(dataDirectory, "pos.db"));
        database.EnsureMigrated();

        var offerStore = new OfferRepository(database);
        var items = new ItemRepository(database);
        var now = DateTimeOffset.Now;
        var today = DateOnly.FromDateTime(now.DateTime);
        var indian = System.Globalization.CultureInfo.GetCultureInfo("en-IN");

        if (ParseStringOption(args, "--sheet") is { } sheetPath)
        {
            File.WriteAllText(sheetPath, OfferSheet.Write(offerStore.All()), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Console.WriteLine($"Saved the offers sheet to {sheetPath}.");
            return 0;
        }

        if (ParseStringOption(args, "--load") is { } loadPath)
        {
            OfferSheetPlan plan;

            using (var reader = ItemCsvParser.OpenText(loadPath))
                plan = OfferSheet.Read(reader, items.Skus(), items.Categories());

            if (!plan.IsClean)
            {
                foreach (var problem in plan.Problems)
                    Console.Error.WriteLine($"  Line {problem.Line}, {problem.Column}: {problem.Problem}");

                Console.Error.WriteLine("Nothing was changed.");
                return 1;
            }

            foreach (var warning in plan.Warnings)
                Console.WriteLine($"  NOTE: {warning}");

            if (!flags.Contains("--yes"))
            {
                Console.WriteLine($"The sheet lists {Plural.Of(plan.Offers.Count, "offer")}, replacing the {offerStore.All().Count} there are now. Add --yes to load it.");
                return 0;
            }

            offerStore.ReplaceAll(plan.Offers, now);
            log.Info("tool", $"{Plural.Of(plan.Offers.Count, "offer")} loaded from {loadPath}");
            Console.WriteLine($"Loaded {Plural.Of(plan.Offers.Count, "offer")}. The till uses them from its next start, or at once when loaded on the owner's screen.");
            return 0;
        }

        var offers = offerStore.All();

        if (ParseStringOption(args, "--try") is { } basket)
        {
            // "DAL001:3 SUG001:1.25": a bill of these, priced with the offers as the till would.
            var bill = new InvoiceEngine(settings.OutletStateCode, settings.TaxMode, settings.RoundOffToRupee);

            foreach (var part in basket.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries))
            {
                var pieces = part.Split(':');

                if (items.FindBySku(pieces[0]) is not { } item)
                {
                    Console.Error.WriteLine($"No item has SKU {pieces[0]}.");
                    return 1;
                }

                var quantity = pieces.Length > 1 && decimal.TryParse(pieces[1], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var q) ? q : 1m;
                bill.AddItem(item, quantity);
            }

            foreach (var given in OfferEngine.Work(bill.Lines, offers, today))
                bill.ApplyOffer(given.Index, given.Discount, given.OfferName);

            Console.WriteLine();

            foreach (var line in bill.Lines)
            {
                var name = line.NameSnapshot.Length > 26 ? line.NameSnapshot[..25] + "…" : line.NameSnapshot;
                var off = line.Discount > 0m ? "-" + line.Discount.ToString("N2", indian) : string.Empty;
                Console.WriteLine($"  {name,-27}{line.Quantity.ToString("0.###", indian),7}{line.Gross.ToString("N2", indian),11}{off,11}{line.LineTotal.ToString("N2", indian),11}  {line.OfferName}");
            }

            var totals = bill.Totals;
            Console.WriteLine();
            Console.WriteLine($"  Offers give {totals.TotalDiscount.ToString("N2", indian)}; the bill comes to {totals.AmountPayable.ToString("N2", indian)}.");
            return 0;
        }

        Console.WriteLine();

        if (offers.Count == 0)
        {
            Console.WriteLine("  No offers. `pos offers --sheet offers.csv` saves a sheet with an example of each kind.");
            return 0;
        }

        var givenByOffer = offerStore.Given(now.AddDays(-30), now.AddMinutes(1));

        foreach (var offer in offers)
        {
            var state = offer.Sku is not null && offer.ItemId is null ? $"gives nothing: no item has SKU {offer.Sku}"
                : offer.IsOn(today) ? "on today" : "not today";
            var uses = givenByOffer.Where(u => u.OfferName.Split(" + ").Contains(offer.Name, StringComparer.OrdinalIgnoreCase)).ToList();

            Console.WriteLine($"  {offer.Name}");
            Console.WriteLine($"      {offer.Describe()}; {offer.When()}; {state}");

            if (uses.Count > 0)
                Console.WriteLine($"      gave {uses.Sum(u => u.Given).ToString("N2", indian)} on {Plural.Of(uses.Sum(u => u.Bills), "bill")} in 30 days");
        }

        return 0;
    }

    case "bill":
    {
        // A bill as the customer gets it on WhatsApp, and - with --out - as a full A4 invoice.
        var database = new PosDatabase(Path.Combine(dataDirectory, "pos.db"));
        database.EnsureMigrated();

        var invoices = new InvoiceRepository(database, settings.InvoiceNumber.ToFormat());
        var number = ParseStringOption(args, "--no");
        var invoice = number is null ? invoices.FindLatest(settings.LaneId) : invoices.FindByInvoiceNo(number.Trim());

        if (invoice is null)
        {
            Console.Error.WriteLine(number is null ? "This lane has not billed anything yet." : $"No bill numbered {number.Trim()}.");
            return 1;
        }

        var store = settings.Store.ToProfile();

        Console.WriteLine();
        Console.WriteLine(DigitalBill.Text(invoice, store));
        Console.WriteLine();

        if (ParseStringOption(args, "--out") is { } outPath)
        {
            File.WriteAllText(outPath, InvoicePage.Render(invoice, store, settings.OutletStateCode, isCopy: true), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Console.WriteLine($"Saved {invoice.InvoiceNo} as an A4 invoice to {outPath}.");
        }

        return 0;
    }

    case "statement":
    {
        // A customer's khata statement - since they last owed nothing, or between two days - as the
        // till prints it with Ctrl+K; or, with --owing, one for everybody who owes, as a page.
        var database = new PosDatabase(Path.Combine(dataDirectory, "pos.db"));
        database.EnsureMigrated();

        var credit = new CreditRepository(database);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var upi = settings.Upi.ToPayee(settings.Store.Name);

        DateOnly? ParseDay(string option)
        {
            var text = ParseStringOption(args, option);

            if (text is null)
                return null;

            if (DateOnly.TryParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day))
                return day;

            throw new FormatException($"{option} is a day written 2026-09-01, not '{text}'.");
        }

        DateOnly? from, to;

        try
        {
            from = ParseDay("--from");
            to = ParseDay("--to");
        }
        catch (FormatException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        if (from is { } f && to is { } t && t < f)
        {
            Console.Error.WriteLine($"--to ({t:yyyy-MM-dd}) is before --from ({f:yyyy-MM-dd}).");
            return 2;
        }

        KhataStatement For(Customer customer)
        {
            var ledger = credit.Ledger(customer.Id);

            return from is null && to is null
                ? KhataStatement.SinceLastClear(customer, ledger, today)
                : KhataStatement.Build(customer, ledger, from ?? DateOnly.MinValue, to ?? today);
        }

        var statements = new List<KhataStatement>();

        if (flags.Contains("--owing"))
        {
            foreach (var owing in credit.Owing(10_000))
                statements.Add(For(new Customer { Id = owing.CustomerId, MobileNo = owing.MobileNo, Name = owing.Name }));

            if (statements.Count == 0)
            {
                Console.WriteLine("Nobody owes anything on credit.");
                return 0;
            }
        }
        else
        {
            if (ParseStringOption(args, "--mobile") is not { } mobile)
            {
                Console.Error.WriteLine("Whose statement? --mobile and their number, or --owing for everybody who owes.");
                return 2;
            }

            if (new CustomerRepository(database).FindByMobile(mobile.Trim()) is not { } customer)
            {
                Console.Error.WriteLine($"No customer has the number {mobile.Trim()}.");
                return 1;
            }

            statements.Add(For(customer));
        }

        var composer = new ReceiptComposer(settings.Store.ToProfile(), settings.Hardware.PrinterPaperWidthChars, settings.ReceiptLanguage);

        foreach (var statement in statements)
        {
            Console.WriteLine();
            Console.WriteLine(composer.ComposeKhataStatement(statement, upi).ToPlainText());
        }

        if (ParseStringOption(args, "--out") is { } outPath)
        {
            File.WriteAllText(outPath, KhataStatementPage.Render(statements, settings.Store.ToProfile(), upi), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Console.WriteLine($"Saved {Plural.Of(statements.Count, "statement")}, one a page, to {outPath}.");
        }

        if (flags.Contains("--print"))
        {
            var printer = PeripheralFactory.CreatePrinter(settings.Hardware, rasterizer);

            if (!printer.IsConfigured)
            {
                Console.Error.WriteLine("No printer is set up on this lane.");
                return 1;
            }

            foreach (var statement in statements)
            {
                var outcome = printer.Print(composer.ComposeKhataStatement(statement, upi).ToEscPos(raster: printer.Raster));

                if (!outcome.Succeeded)
                {
                    Console.Error.WriteLine($"A statement did not print: {outcome.Detail}");
                    return 1;
                }
            }

            Console.WriteLine($"Printed {Plural.Of(statements.Count, "statement")}.");
        }

        log.Info("tool", $"{Plural.Of(statements.Count, "khata statement")}");
        return 0;
    }

    case "upi":
    {
        // The code the till shows when a customer pays by UPI, for an amount given here: to check
        // the shop's UPI ID with a real phone before the first customer does.
        if (settings.Upi.ToPayee(settings.Store.Name) is not { } payee)
        {
            Console.Error.WriteLine("No UPI ID is set for this lane. The owner sets it in Settings (Ctrl+D), or \"upi\": { \"id\": \"...\" } in settings.json.");
            return 2;
        }

        var amountText = ParseStringOption(args, "--amount");

        if (amountText is null
            || !decimal.TryParse(amountText, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var amount)
            || amount <= 0m || decimal.Round(amount, 2) != amount)
        {
            Console.Error.WriteLine($"--amount is the rupees to ask for, such as 400.50, not '{amountText}'.");
            return 2;
        }

        var link = UpiLink.For(payee, amount);
        var width = ParseWidth(args) ?? settings.Hardware.PrinterPaperWidthChars;
        var slip = new ReceiptComposer(settings.Store.ToProfile(), width, settings.ReceiptLanguage).ComposeUpiSlip(payee, amount, link);
        var code = QrCode.Encode(link);

        Console.WriteLine();
        Console.WriteLine($"  Pay to   : {payee.Name} ({payee.Id})");
        Console.WriteLine($"  Amount   : {amount.ToString("N2", System.Globalization.CultureInfo.InvariantCulture)}");
        Console.WriteLine($"  Link     : {link}");
        Console.WriteLine($"  QR code  : version {code.Version}, {code.Size} x {code.Size} modules");
        Console.WriteLine();

        if (ParseStringOption(args, "--png") is { } pngPath)
        {
            var raster = rasterizer is null
                ? null
                : new RasterOptions(rasterizer, settings.Hardware.EffectivePaperWidthDots, settings.Hardware.PrinterRasterMode);

            if (raster is null)
            {
                Console.Error.WriteLine("Nothing to draw the slip's words with: this lane has no text renderer.");
                return 2;
            }

            var pixels = slip.ToBitmap(raster);
            ReceiptImage.SavePng(pixels, pngPath);
            Console.WriteLine($"Saved the slip, {pixels.Width}x{pixels.Height} dots, to {pngPath}. Scan it off the screen with a phone to check.");
        }

        if (flags.Contains("--print"))
        {
            var printer = PeripheralFactory.CreatePrinter(settings.Hardware, rasterizer);

            if (!printer.IsConfigured)
            {
                Console.Error.WriteLine("No printer is set up on this lane.");
                return 1;
            }

            var outcome = printer.Print(slip.ToEscPos(raster: printer.Raster));

            if (!outcome.Succeeded)
            {
                Console.Error.WriteLine($"The slip did not print: {outcome.Detail}");
                return 1;
            }

            Console.WriteLine("Printed. Scan it with a phone: the app should show the shop's name and this amount. Do not pay it.");
        }

        log.Info("tool", $"UPI code for {amount:0.00} to {payee.Id}");
        return 0;
    }

    case "expiring":
    {
        // Deliveries within a month of their use-by date and probably still on the shelf: the same
        // list as the Stock tab's "Near its date".
        var database = new PosDatabase(Path.Combine(dataDirectory, "pos.db"));
        database.EnsureMigrated();

        var warnings = new ExpiryRepository(database).Expiring(DateOnly.FromDateTime(DateTime.Today));
        var indian = System.Globalization.CultureInfo.GetCultureInfo("en-IN");

        Console.WriteLine();

        if (warnings.Count == 0)
        {
            Console.WriteLine($"  Nothing on the shelf is within {Expiry.WarnDays} days of its date, as far as the purchase bills say.");
            return 0;
        }

        foreach (var warning in warnings)
        {
            var name = warning.Name.Length > 30 ? warning.Name[..29] + "…" : warning.Name;
            var shelf = warning.LikelyOnShelf is { } left ? $"{left.ToString("0.###", indian)} likely on the shelf" : "not counted";

            Console.WriteLine($"  {name,-32}{warning.Expires:dd-MM-yyyy}  {Plural.Of(warning.DaysLeft, "day"),9}  {shelf,-26}{warning.Advice}");
        }

        Console.WriteLine();
        Console.WriteLine($"  {warnings.Count} deliver{(warnings.Count == 1 ? "y" : "ies")}, {warnings.Count(w => w.IsExpired)} past the date.");
        return 0;
    }

    case "order-list":
    {
        // What to order, from whom: the same list as the owner's Orders tab.
        var cover = ParseIntOption(args, "--cover") ?? settings.OrderCoverDays;

        if (!Reorder.IsValidCoverDays(cover))
        {
            Console.Error.WriteLine($"An order covers between 1 and 120 days, not {cover}.");
            return 2;
        }

        var database = new PosDatabase(Path.Combine(dataDirectory, "pos.db"));
        database.EnsureMigrated();

        var list = new OrderListQuery(database, settings.LowStockPercent).Gather(cover);
        var indian = System.Globalization.CultureInfo.GetCultureInfo("en-IN");

        Console.WriteLine();

        if (list.IsEmpty)
        {
            Console.WriteLine($"  Nothing needs ordering: every counted item lasts {cover} days or more at the rate it sells.");
            return 0;
        }

        Console.WriteLine($"  To last {cover} days, at the rate each sold over the last {Plural.Of(list.DaysMeasured, "day")}.");

        foreach (var supplier in list.Suppliers)
        {
            Console.WriteLine();
            Console.WriteLine($"  {supplier.Supplier}{(supplier.Phone is { Length: > 0 } phone ? $"  ({phone})" : "")}");

            foreach (var line in supplier.Lines)
            {
                var name = line.Name.Length > 30 ? line.Name[..29] + "…" : line.Name;
                var days = line.DaysLeft is { } left ? $"{left.ToString("0.#", indian)} days left" : "not selling";

                Console.WriteLine($"    {name,-32}{line.Order.ToString("0.###", indian),8} {Units.ScreenLabel(line.Unit),-8}{days}");
            }
        }

        if (ParseStringOption(args, "--out") is { } outPath)
        {
            OrderListFiles.Write(list, Path.GetFullPath(outPath));
            Console.WriteLine();
            Console.WriteLine($"Saved to {Path.GetFullPath(outPath)}");
        }

        return 0;
    }

    case "price-sheet":
    {
        // Prices in bulk, the same sheet as the Catalogue tab: --out writes one to fill in, --load
        // reads it back and changes only prices.
        var database = new PosDatabase(Path.Combine(dataDirectory, "pos.db"));
        database.EnsureMigrated();
        var prices = new PriceRepository(database);

        if (ParseStringOption(args, "--out") is { } outPath)
        {
            var items = prices.PriceSheet();
            File.WriteAllText(Path.GetFullPath(outPath), PriceSheet.Write(items), new System.Text.UTF8Encoding(true));
            Console.WriteLine($"Saved a price sheet of {Plural.Of(items.Count, "item")} to {Path.GetFullPath(outPath)}.");
            return 0;
        }

        if (ParseStringOption(args, "--load") is not { } loadPath)
        {
            Console.Error.WriteLine("pos price-sheet --out <file.csv>     save a sheet to fill in");
            Console.Error.WriteLine("pos price-sheet --load <file.csv>    load a filled-in one back");
            return 2;
        }

        PriceSheetPlan plan;

        using (var reader = ItemCsvParser.OpenText(Path.GetFullPath(loadPath)))
            plan = PriceSheet.Read(reader, prices.PriceSheet());

        Console.WriteLine();

        if (!plan.IsClean)
        {
            foreach (var problem in plan.Problems)
                Console.WriteLine($"  Line {problem.Line}, {problem.Column}: {problem.Problem}");

            Console.WriteLine();
            Console.WriteLine("Nothing was changed.");
            return 1;
        }

        foreach (var change in plan.Changes)
            Console.WriteLine($"  {change.Sku,-16}{change.Name,-32} {change.OldPrice,10:0.00} -> {change.NewPrice,10:0.00}   MRP {change.NewMrp:0.00}");

        foreach (var warning in plan.Warnings)
            Console.WriteLine($"  NOTE: {warning}");

        if (plan.Changes.Count == 0)
        {
            Console.WriteLine("  Nothing in that sheet changes a price.");
            return 0;
        }

        if (!flags.Contains("--yes"))
        {
            Console.Write($"Change {Plural.Of(plan.Changes.Count, "price")}? [y/N] ");
            var answer = Console.ReadLine();

            if (answer is null || !answer.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Left as they were.");
                return 0;
            }
        }

        var changed = prices.Apply(plan.Changes);
        Console.WriteLine(changed == 1
            ? "1 price changed. Its shelf label is due: pos labels."
            : $"{Plural.Of(changed, "price")} changed. Their shelf labels are due: pos labels.");
        log.Info("prices", $"{Plural.Of(changed, "price")} changed from a price sheet");
        return 0;
    }

    case "labels":
    {
        // The shelf labels that are out of date, or every one with --all.
        var database = new PosDatabase(Path.Combine(dataDirectory, "pos.db"));
        database.EnsureMigrated();
        var prices = new PriceRepository(database);
        var labels = flags.Contains("--all") ? prices.AllLabels() : prices.LabelsDue();

        Console.WriteLine();

        if (labels.Count == 0)
        {
            Console.WriteLine("  Every shelf label is up to date.");
            return 0;
        }

        foreach (var label in labels)
            Console.WriteLine($"  {label.Sku,-16}{label.Name,-32} {label.Price,10:0.00} / {Units.ScreenLabel(label.Unit)}   MRP {label.Mrp:0.00}");

        Console.WriteLine();
        Console.WriteLine($"  {Plural.Of(labels.Count, "label")}{(flags.Contains("--all") ? "" : " due")}.");

        var done = false;

        if (ParseStringOption(args, "--out") is { } pagePath)
        {
            File.WriteAllText(Path.GetFullPath(pagePath), ShelfLabelPage.Render(labels, settings.Store.Name), new System.Text.UTF8Encoding(true));
            Console.WriteLine($"Saved to {Path.GetFullPath(pagePath)}: print it on A4 at actual size.");
            done = true;
        }

        if (flags.Contains("--print"))
        {
            var labelPrinter = PeripheralFactory.CreatePrinter(settings.Hardware, rasterizer);

            if (!labelPrinter.IsConfigured)
            {
                Console.Error.WriteLine("This lane has no printer configured. Use --out to save a page instead.");
                return 2;
            }

            var outcome = labelPrinter.Print(new ShelfLabelComposer(labelPrinter.PaperWidthChars, settings.ReceiptLanguage, settings.Store.ToProfile().CurrencyPrefix)
                .Compose(labels).ToEscPos(raster: labelPrinter.Raster));

            Console.WriteLine(outcome.Succeeded ? $"Printed {Plural.Of(labels.Count, "label")}." : $"Did not print: {outcome.Detail}");

            if (!outcome.Succeeded)
                return 1;

            done = true;
        }

        // Only what reached paper or a page counts as done; a listing on its own leaves them due.
        if (done && !flags.Contains("--all"))
            prices.MarkLabelled(labels.Select(l => l.ItemId), DateTimeOffset.Now);

        return 0;
    }

    case "credit-note":
    {
        // Reading back a return: on screen, or a duplicate on the printer. The credit note is the
        // customer's proof that goods went back and money came to them, so it has to be reachable
        // after the slip has gone.
        var number = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));

        if (string.IsNullOrWhiteSpace(number))
        {
            Console.Error.WriteLine("Which credit note? pos credit-note CN/26-27/T1-1 [--reprint]");
            return 2;
        }

        var database = new PosDatabase(Path.Combine(dataDirectory, "pos.db"));
        database.EnsureMigrated();

        var note = new CreditNoteRepository(database).Find(number);

        if (note is null)
        {
            Console.Error.WriteLine($"There is no credit note {number}.");
            return 2;
        }

        var composer = new ReceiptComposer(settings.Store.ToProfile(), settings.Hardware.PrinterPaperWidthChars, settings.ReceiptLanguage);
        var isReprint = flags.Contains("--reprint");

        Console.WriteLine();
        Console.WriteLine(composer.ComposeCreditNote(note, isReprint).ToPlainText());

        if (!isReprint)
            return 0;

        var toPrinter = PeripheralFactory.CreatePrinter(settings.Hardware, rasterizer);

        if (!toPrinter.IsConfigured)
        {
            Console.Error.WriteLine("This lane has no printer configured, so there is nothing to print to.");
            return 2;
        }

        var duplicate = toPrinter.Print(composer.ComposeCreditNote(note, isReprint: true).ToEscPos(raster: toPrinter.Raster));

        Console.WriteLine(duplicate.Succeeded ? $"Duplicate of {note.Number} printed, marked as a reprint." : $"Did not print: {duplicate.Detail}");
        log.Info("tool", $"reprinted credit note {note.Number}");
        return duplicate.Succeeded ? 0 : 1;
    }

    case "stock":
    {
        var database = new PosDatabase(Path.Combine(dataDirectory, "pos.db"));
        database.EnsureMigrated();

        var stock = new StockRepository(database, () => settings.LowStockPercent);
        var items = new ItemRepository(database);
        var indian = System.Globalization.CultureInfo.GetCultureInfo("en-IN");

        // Correcting a count by hand: a delivery arrived, something broke, somebody recounted.
        if (flags.Contains("--set"))
        {
            var sku = ParseStringOption(args, "--sku");
            var quantityText = ParseStringOption(args, "--qty");

            if (string.IsNullOrWhiteSpace(sku) || string.IsNullOrWhiteSpace(quantityText))
            {
                Console.Error.WriteLine("Setting a count needs both --sku and --qty.");
                Console.Error.WriteLine("  pos stock --set --sku RICE5KG --qty 24 --reason \"delivery\"");
                return 2;
            }

            if (!decimal.TryParse(quantityText, System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var quantity) || quantity < 0m)
            {
                Console.Error.WriteLine($"'{quantityText}' is not a quantity.");
                return 2;
            }

            var item = items.FindBySku(sku);

            if (item is null)
            {
                Console.Error.WriteLine($"There is no active item with SKU '{sku}'.");
                return 2;
            }

            // An item the catalogue never gave a count to has nothing to move. Saying so beats
            // silently doing nothing and letting somebody believe the shelf is now recorded.
            if (!item.IsStockTracked)
            {
                Console.Error.WriteLine($"{item.Name} is not counted. Add a stock_qty column to the catalogue and re-import to start counting it.");
                return 2;
            }

            var before = item.StockQty!.Value;
            var after = stock.Set(item.Id, quantity, StockReason.Adjust, settings.LaneId, ParseStringOption(args, "--reason"));

            Console.WriteLine();
            Console.WriteLine($"  {item.Name}");
            Console.WriteLine($"  {before.ToString("0.###", indian)}  ->  {after?.ToString("0.###", indian)}");
            log.Info("stock", $"{item.Sku} set to {quantity} (was {before})");

            return 0;
        }

        var low = flags.Contains("--low");
        var levels = low
            ? stock.ListLow(ParseIntOption(args, "--limit") ?? 200)
            : stock.List(ParseIntOption(args, "--limit") ?? 200);

        Console.WriteLine();

        if (levels.Count == 0)
        {
            Console.WriteLine(low
                ? $"  Nothing is low - nothing is at its reorder level or down to {settings.LowStockPercent:0.##}% of full."
                : "  No item in this catalogue is counted. Load a stock sheet or a stock_qty column to start.");
            return 0;
        }

        Console.WriteLine($"  {"SKU",-16}{"Item",-32}{"Have",8}{"Full",8}{"Warns at",10}{"To order",10}");
        Console.WriteLine("  " + new string('-', 84));

        foreach (var level in levels)
        {
            var name = level.Name.Length > 30 ? level.Name[..29] + "…" : level.Name;

            Console.WriteLine(
                $"  {level.Sku,-16}{name,-32}" +
                $"{level.Quantity.ToString("0.###", indian),8}" +
                $"{(level.FullLevel?.ToString("0.###", indian) ?? "—"),8}" +
                $"{(level.WarnAt?.ToString("0.###", indian) ?? "—"),10}" +
                $"{(level.ToOrder?.ToString("0.###", indian) ?? ""),10}" +
                (level.IsOut ? "  OUT" : level.IsLow ? "  LOW" : string.Empty));
        }

        Console.WriteLine();
        Console.WriteLine($"  {Plural.Of(levels.Count, "item")}{(low ? $" low - at the reorder level, or down to {settings.LowStockPercent:0.##}% of full" : " counted")}.");

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
        Console.WriteLine("  SETTINGS.html explains how.");

        return 0;
    }

    case "receipt-preview":
    {
        // Renders the sample receipt as text without touching a printer, which is how the layout
        // gets checked on a bench or against a different paper width.
        var width = ParseWidth(args) ?? settings.Hardware.PrinterPaperWidthChars;

        // Either layout can be looked at before the owner switches to it. The lane's own setting is
        // the default, so the plain command still shows what this lane prints.
        if (ParseStringOption(args, "--layout") is { } layoutName)
        {
            if (!Enum.TryParse<ReceiptLayout>(layoutName, ignoreCase: true, out var layout) || !Enum.IsDefined(layout))
            {
                Console.Error.WriteLine($"'{layoutName}' is not a layout. Use standard or compact.");
                return 2;
            }

            settings.ReceiptLayout = layout;
        }

        var receipt = checks.Preview(width);

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
        {
            // A keyboard-emulation scanner types into whatever has focus, so at a console it is
            // simply read as a line before the check is asked to judge it.
            string? typed = null;

            if (checks.ScannerTypesLikeAKeyboard)
            {
                Console.WriteLine();
                Console.WriteLine("  This scanner types like a keyboard. Scan an item now, or press Enter to skip.");
                Console.Write("  > ");
                typed = Console.ReadLine();
            }

            results.Add(("Scanner", checks.Scanner(window, typed)));
        }

        if (all || flags.Contains("--scale"))
            results.Add(("Scale", checks.Scale(window)));

        if (all || flags.Contains("--pole"))
            results.Add(("Pole display", checks.PoleDisplay()));

        if (results.Count == 0)
        {
            Console.Error.WriteLine("Nothing selected. Pass --printer, --drawer, --scanner, --scale, --pole, or nothing for all.");
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
            : $"{Plural.Of(failed, "peripheral")} failed.");

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

/// <summary>An amount after an option, read the invariant way: 1234.50, never 1.234,50.</summary>
static decimal? ParseAmountOption(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)
            && decimal.TryParse(args[i + 1], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }
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

          pos test-hardware [--printer] [--drawer] [--scanner] [--scale] [--pole]
              Checks the lane's peripherals. With no flags it checks all of them.
              The printer and drawer checks ask you to confirm what physically
              happened, because no software can see paper come out of a printer.

          pos dashboard [--days N] [--top N] [--out <path>]
              The shop's figures as one HTML page: takings, the hourly rush,
              what sells, how customers paid, and — where tax was charged —
              GST by slab. Reads the books without writing to them, so it can
              be run while the till is busy.
              Defaults to the last 30 days. Asks for a PIN first if one
              has been set.

          pos stock [--low] [--limit N]
              What is left on the shelf, most depleted first. --low lists only
              what is at or below its reorder level, which is the list to
              order against. Items with no stock_qty in the catalogue are not
              counted and never appear.

          pos stock --set --sku <sku> --qty <n> [--reason "..."]
              Corrects a count by hand after a delivery, a breakage or a
              recount. The change and the reason are kept, so a count that
              stops matching the shelf can be traced back to where it went.

          pos dashboard-pin [--clear]
              Require a PIN before the dashboard will run, so a cashier cannot
              read the shop's turnover and margins. Asks for the current PIN
              before changing or clearing one. Keeps somebody out of the
              command; it does not encrypt the database — see SETTINGS.html.

          pos gst-return [--month 2026-09] [--out <file.html>]
              The month's figures for the GST return: sales by rate and place
              of supply, nil-rated sales, the HSN summary and the bill numbers
              issued. Writes a page to read and CSV files in the layout of the
              GST offline tool. Defaults to last month. Behind the dashboard
              PIN when one is set. Goods returned on credit notes in the month
              are taken off the figures.

          pos credit-note <number> [--reprint]
              Shows a credit note - goods taken back at the till with F9 - as
              it printed. --reprint prints a duplicate marked as a reprint.

          pos price-sheet --out <file.csv> | --load <file.csv> [--yes]
              Prices in bulk. --out saves every item with its prices and two
              empty columns, new_mrp and new_selling_price; --load reads the
              filled-in sheet back, says what it will change, and changes only
              prices. A price above its MRP is refused; one below cost is named.

          pos labels [--all] [--print] [--out <page.html>]
              Shelf labels for every item whose price changed, or which is new,
              since its label was printed (--all for every item). --print
              sends them to the till's printer, --out saves an A4 page. Either
              marks them done.

          pos offers [--sheet <file.csv>] [--load <file.csv> [--yes]] [--try "SKU:qty ..."]
              The shop's offers and schemes, and what each gave in 30 days.
              --sheet saves the offers sheet (examples of each kind when there
              are none); --load checks a filled-in one, and with --yes loads it,
              replacing every offer. --try prices a bill of those items with
              the offers, as the till would: "DAL001:3,SUG001:2", or spaces
              inside quotes.

          pos bill [--no <invoice number>] [--out <file.html>]
              A bill as the customer gets it on WhatsApp - the last one, or the
              one numbered. --out saves it as a full A4 tax invoice.

          pos statement --mobile <number> [--from 2026-09-01] [--to 2026-09-30]
                        [--print] [--out <file.html>]
          pos statement --owing [--print] [--out <file.html>]
              A customer's khata statement: everything since they last owed
              nothing, or the days given, with what they owe now, how old it
              is, and a UPI code for it. --owing does everybody who owes, one
              a page. The till prints the same with Ctrl+K.

          pos upi --amount 400.50 [--print] [--png <path>]
              The UPI link and QR code the till shows for that amount, to check
              the shop's UPI ID with a real phone before a customer does.
              --print prints the scan-to-pay slip, --png saves it as a picture.

          pos dead-stock [--days 60]
              Counted items on the shelf not sold for 60 days (or --days),
              most money tied up at cost first, with what to do about each.

          pos expiring
              Deliveries within a month of the use-by date on their purchase
              bill and probably still on the shelf, soonest first - worked
              out from the count, newest deliveries on the shelf first.

          pos order-list [--cover 14] [--out <file.csv>]
              What to order and from whom: enough of each counted item to
              last the cover at the rate it sold over the last four weeks,
              grouped by the supplier it was last bought from. --out saves
              the list as a spreadsheet.

          pos receipt-preview [--width N] [--png <path>] [--layout standard|compact]
              Renders a sample receipt as text. Touches no hardware, so it works
              on a bench and against any paper width. --png saves the dots the
              printer would actually burn, which is the only way to check that
              Tamil came out right without using a roll of paper. --layout
              shows the other bill layout without switching the lane to it.

          pos import-items --file <path> [--update] [--dry-run]
              Loads a catalogue CSV. Required columns: sku, barcode, name,
              hsn_code, unit (Pcs, Kg, L, m, or a Tamil unit such as Seepu,
              Kattu, Padi, Muzham), mrp, selling_price, gst_rate,
              is_weighed — in any order. Optional: category, cost_price,
              stock_qty, reorder_level. Nothing is written unless the whole
              file is clean, so a rejected import leaves the catalogue
              exactly as it was.
              --update changes items already in the catalogue instead of
              rejecting them, which is what a price revision needs.

          pos close-day --list [--limit N]
              The day-end reports this lane has taken, most recent first.

          pos close-day --show [--id <no>]
              Reads one back on screen, no paper. Defaults to the last one.

          pos close-day --reprint [--id <no>]
              Prints a duplicate, marked as a reprint. For a sheet that was
              lost, or a printer that jammed at closing time.

          pos close-day [--preview] [--yes] [--force] [--counted <amount>]
              Prints the lane's Z-report and closes the day. Shows the report
              first, because a close cannot be undone. Takes a verified backup
              as part of closing. --counted is the cash counted in the drawer,
              kept with the close and printed with whether it is over or short.

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
