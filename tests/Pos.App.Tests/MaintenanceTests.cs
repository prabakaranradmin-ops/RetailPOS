using System.IO;
using Pos.App.ViewModels;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Domain.Printing;
using Pos.Core.Hardware.Printing;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// The lane's upkeep, driven from the owner's screen instead of a command prompt: backups, the
/// database's health, restoring, and the day-end reports already taken.
/// </summary>
/// <remarks>
/// The rule these hold down is the same one the catalogue screen holds: this is not a softer route.
/// Every action goes through the class the <c>pos</c> tool goes through, so a damaged snapshot is
/// refused here exactly as it is refused there, and compacting a database nobody has checked is
/// refused in both places.
/// </remarks>
public class MaintenanceTests : IDisposable
{
    private const string Lane = "L1";
    private const string HomeState = "33";

    private readonly TempDatabase _temp = new();
    private readonly List<ReceiptBuilder> _printed = [];

    private bool _printerConfigured = true;

    public void Dispose() => _temp.Dispose();

    private string DataDirectory => Path.GetDirectoryName(_temp.Database.DatabasePath)!;

    private string BackupDirectory => Path.Combine(DataDirectory, "backups");

    private HeldBillRepository Held => new(_temp.Database);

    private DayCloseRepository Closes => new(_temp.Database, Held);

    private static readonly StoreProfile Store = new() { Name = "Sri Lakshmi Stores", Gstin = "33AABCS1429B1ZX" };

    private MaintenanceViewModel Screen() => new(
        _temp.Database,
        DataDirectory,
        Closes,
        new ZReportComposer(Store, 48, ReceiptLanguage.English, TaxMode.Gst),
        Lane,
        print: report =>
        {
            if (!_printerConfigured)
                return new PrintOutcome(PrintStatus.NoPrinterConfigured, "This lane has no printer configured.");

            _printed.Add(report);
            return PrintOutcome.Printed(report.ToEscPos().Length);
        },

        // Tests have no dispatcher, so "back to the UI thread" is "right here".
        post: action => action());

    private void SeedCatalogue() => _temp.Items.AddRange(
    [
        Catalogue.Item(sku: "DAL001", barcode: "8901234567890", name: "Toor Dal 1kg", price: 189m, gstRate: 5m),
    ]);

    /// <summary>Rings up and settles one sale.</summary>
    private void Sell()
    {
        var bill = new InvoiceEngine(HomeState);
        bill.AddItem(_temp.Items.FindByBarcode("8901234567890")!);

        var basket = new TenderBasket(bill.Totals.GrandTotal);
        basket.Add(TenderType.Cash, bill.Totals.GrandTotal);

        new CheckoutService(
            new InvoiceRepository(_temp.Database),
            new CustomerRepository(_temp.Database),
            new RecordingDrawerService()).Complete(Lane, bill, basket);
    }

    // ---- Backing up ------------------------------------------------------------------------------

    [Fact]
    public async Task ABackupWritesAVerifiedSnapshotAndListsIt()
    {
        var screen = Screen();

        Assert.Empty(screen.Snapshots);

        await screen.Backup();

        Assert.Single(screen.Snapshots);
        Assert.True(File.Exists(screen.Snapshots[0].Path));
        Assert.Contains("verified", screen.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OlderSnapshotsArePrunedToWhatWasAskedFor()
    {
        var screen = Screen();
        screen.Keep = 2;

        for (var i = 0; i < 3; i++)
            await screen.Backup();

        Assert.Equal(2, screen.Snapshots.Count);
    }

    /// <summary>Keeping none would mean a backup that deletes itself.</summary>
    [Fact]
    public void AtLeastOneSnapshotIsAlwaysKept()
    {
        var screen = Screen();
        screen.Keep = 0;

        Assert.Equal(1, screen.Keep);
    }

    [Fact]
    public async Task TheNewestSnapshotIsListedFirst()
    {
        var screen = Screen();

        await screen.Backup();
        await screen.Backup();

        Assert.Equal(2, screen.Snapshots.Count);
        Assert.True(screen.Snapshots[0].TakenAt >= screen.Snapshots[1].TakenAt);
    }

    // ---- Checking and compacting -----------------------------------------------------------------

    [Fact]
    public async Task AHealthyDatabaseChecksOutSound()
    {
        var screen = Screen();

        await screen.Check();

        Assert.True(screen.IsHealthy);
        Assert.Contains("sound", screen.Summary, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Compacting rewrites every page, which on a damaged file is the surest way to finish it off.
    /// The command refuses it and so does the screen.
    /// </summary>
    [Fact]
    public void CompactingIsNotOfferedUntilACheckComesBackClean()
    {
        var screen = Screen();

        Assert.False(screen.IsHealthy);
        Assert.False(screen.CanCompact);
    }

    [Fact]
    public async Task CompactingIsOfferedOnceTheCheckIsClean()
    {
        var screen = Screen();

        await screen.Check();

        Assert.True(screen.CanCompact);

        await screen.Compact();

        Assert.Contains("Compacted", screen.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(_temp.Database.DatabasePath));
    }

    [Fact]
    public async Task CompactingWithoutACheckIsRefusedRatherThanDone()
    {
        var screen = Screen();

        await screen.Compact();

        Assert.Contains("Check the database first", screen.Summary, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Restoring -------------------------------------------------------------------------------

    [Fact]
    public async Task ARestoreIsNotArmedUntilTheSnapshotsDateIsTyped()
    {
        var screen = Screen();
        await screen.Backup();

        screen.SelectedSnapshot = screen.Snapshots[0];

        Assert.False(screen.CanRestore);

        screen.TypedConfirmation = "not the date";
        Assert.False(screen.CanRestore);

        screen.TypedConfirmation = screen.Snapshots[0].Confirmation;
        Assert.True(screen.CanRestore);
    }

    /// <summary>
    /// A confirmation belongs to the snapshot it was typed against. Otherwise a phrase typed for
    /// yesterday's file would arm a restore of last month's.
    /// </summary>
    [Fact]
    public async Task PickingADifferentSnapshotHasToBeConfirmedAgain()
    {
        var screen = Screen();
        await screen.Backup();
        await screen.Backup();

        screen.SelectedSnapshot = screen.Snapshots[0];
        screen.TypedConfirmation = screen.Snapshots[0].Confirmation;

        Assert.True(screen.CanRestore);

        screen.SelectedSnapshot = screen.Snapshots[1];

        Assert.Equal(string.Empty, screen.TypedConfirmation);
        Assert.False(screen.CanRestore);
    }

    [Fact]
    public async Task RestoringPutsTheSnapshotBackAndLosesWhatCameAfterIt()
    {
        SeedCatalogue();

        var screen = Screen();
        await screen.Backup();

        // Sold after the snapshot was taken, so restoring has to lose it.
        Sell();
        Assert.NotNull(new InvoiceRepository(_temp.Database).FindLatest(Lane));

        screen.SelectedSnapshot = screen.Snapshots[0];
        screen.TypedConfirmation = screen.Snapshots[0].Confirmation;

        await screen.Restore();

        Assert.True(screen.Summary.StartsWith("Restored", StringComparison.Ordinal),
            $"Summary was: {screen.Summary}\nLog: {string.Join(" | ", screen.Log)}");

        Assert.Null(new InvoiceRepository(_temp.Database).FindLatest(Lane));
    }

    [Fact]
    public async Task ADamagedSnapshotIsRefusedAndNothingIsChanged()
    {
        SeedCatalogue();
        Sell();

        Directory.CreateDirectory(BackupDirectory);

        // Named so it is read as a snapshot, but it is not a database.
        var damaged = Path.Combine(BackupDirectory, "pos-20200101-090000.db");
        await File.WriteAllTextAsync(damaged, "this is not a database");

        var screen = Screen();
        screen.SelectedSnapshot = screen.Snapshots.Single(s => s.Path == damaged);
        screen.TypedConfirmation = screen.SelectedSnapshot.Confirmation;

        await screen.Restore();

        Assert.True(screen.Summary.Contains("not usable", StringComparison.OrdinalIgnoreCase),
            $"Summary was: {screen.Summary}\nLog: {string.Join(" | ", screen.Log)}");

        // The sale is exactly where it was.
        Assert.NotNull(new InvoiceRepository(_temp.Database).FindLatest(Lane));
    }

    [Fact]
    public async Task RestoringWithoutPickingAnythingIsRefused()
    {
        var screen = Screen();

        await screen.Restore();

        Assert.Contains("Pick a snapshot", screen.Summary, StringComparison.OrdinalIgnoreCase);
    }

    // ---- The day-end reports already taken -------------------------------------------------------

    [Fact]
    public void ALaneThatHasNeverClosedShowsNoReports()
    {
        Assert.Empty(Screen().Closes);
    }

    [Fact]
    public void ClosedDaysAreListed()
    {
        SeedCatalogue();
        Sell();
        Closes.Close(Lane, DateTimeOffset.Now);

        var screen = Screen();

        Assert.Single(screen.Closes);
        Assert.Equal(1, screen.Closes[0].InvoiceCount);
    }

    [Fact]
    public async Task APastReportCanBeReadWithoutPrintingAnything()
    {
        SeedCatalogue();
        Sell();
        Closes.Close(Lane, DateTimeOffset.Now);

        var screen = Screen();
        screen.SelectedClose = screen.Closes[0];

        await screen.ShowReport();

        Assert.True(screen.HasReport);
        Assert.Contains("Sri Lakshmi Stores", screen.ReportText);
        Assert.Empty(_printed);
    }

    /// <summary>A duplicate that did not say so is the one an inspector would ask about.</summary>
    [Fact]
    public async Task ADuplicateIsPrintedAndMarkedAsAReprint()
    {
        SeedCatalogue();
        Sell();
        Closes.Close(Lane, DateTimeOffset.Now);

        var screen = Screen();
        screen.SelectedClose = screen.Closes[0];

        await screen.Reprint();

        Assert.Single(_printed);
        Assert.Contains("REPRINT", _printed[0].ToPlainText(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("REPRINT", screen.ReportText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ALaneWithNoPrinterSaysSoRatherThanThrowing()
    {
        SeedCatalogue();
        Sell();
        Closes.Close(Lane, DateTimeOffset.Now);

        _printerConfigured = false;

        var screen = Screen();
        screen.SelectedClose = screen.Closes[0];

        await screen.Reprint();

        Assert.Contains("Did not print", screen.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_printed);
    }

    [Fact]
    public async Task ReadingAReportNeedsOnePicked()
    {
        var screen = Screen();

        await screen.ShowReport();

        Assert.Contains("Pick a report", screen.Summary, StringComparison.OrdinalIgnoreCase);
    }

    // ---- The panel shows one thing at a time -----------------------------------------------------

    /// <summary>
    /// A report left on screen while a backup runs beneath it reads as though the backup produced it.
    /// </summary>
    [Fact]
    public async Task RunningSomethingElseClearsTheReportAndGoesBackToTheLog()
    {
        SeedCatalogue();
        Sell();
        Closes.Close(Lane, DateTimeOffset.Now);

        var screen = Screen();
        screen.SelectedClose = screen.Closes[0];

        await screen.ShowReport();
        Assert.True(screen.HasReport);
        Assert.False(screen.ShowsLog);

        await screen.Backup();

        Assert.False(screen.HasReport);
        Assert.True(screen.ShowsLog);
    }

    [Fact]
    public async Task EverythingIsUsableAgainOnceAJobFinishes()
    {
        var screen = Screen();

        await screen.Backup();

        Assert.False(screen.IsBusy);
        Assert.True(screen.CanRun);
    }
}
