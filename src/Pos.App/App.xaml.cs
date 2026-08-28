using System.IO;
using System.Windows;
using Pos.App.Input;
using Pos.App.ViewModels;
using Pos.App.Views;
using Pos.Core.Analytics;
using Pos.Core.Configuration;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Domain.Printing;
using Pos.Core.Hardware.Printing;
using Pos.Core.Hardware.Windows;
using Pos.Core.Logging;

namespace Pos.App;

public partial class App : Application
{
    private FileLog? _log;

    /// <summary>
    /// Everything this lane owns — database, settings, keymap — lives under one local folder.
    /// Nothing is fetched from a server, at startup or afterwards.
    /// </summary>
    public static string DataDirectory { get; private set; } = DefaultDataDirectory;

    private static string DefaultDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RetailPOS");

    /// <summary>
    /// Where this run keeps its data: <c>--data &lt;path&gt;</c>, else the lane's own folder.
    /// </summary>
    /// <remarks>
    /// The same switch the <c>pos</c> tool takes, and it exists for the same reason. A test run
    /// that had to share a folder with a real lane could only be made safe by moving that lane's
    /// database out of the way and putting it back afterwards — which is a data-loss bug waiting
    /// for the run to be interrupted. Pointing the run somewhere else cannot go wrong.
    /// </remarks>
    internal static string ResolveDataDirectory(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--data", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                return Path.GetFullPath(args[i + 1]);
            }
        }

        return DefaultDataDirectory;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DataDirectory = ResolveDataDirectory(e.Args);
        Directory.CreateDirectory(DataDirectory);

        _log = new FileLog(Path.Combine(DataDirectory, "logs"));

        // Nothing that reaches here has anywhere else to go. Without this an unhandled fault takes
        // the till down mid-queue leaving no trace of why, which is the one failure a pilot cannot
        // afford to lose.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            _log.Error("fatal", "unhandled exception", e.ExceptionObject as Exception);

        DispatcherUnhandledException += (_, e) =>
            _log.Error("fatal", "unhandled exception on the UI thread", e.Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
            _log.Error("fatal", "unobserved task exception", e.Exception);

        var settings = PosSettings.LoadOrDefault(Path.Combine(DataDirectory, "settings.json"));
        var keymap = Keymap.LoadOrDefault(Path.Combine(DataDirectory, "keymap.json"));

        var database = new PosDatabase(Path.Combine(DataDirectory, "pos.db"));
        database.EnsureMigrated();

        _log.Info("startup", $"lane {settings.LaneId}, state {settings.OutletStateCode}, data at {DataDirectory}");

        var customers = new CustomerRepository(database);
        var invoices = new InvoiceRepository(database, settings.InvoiceNumber.ToFormat());
        var heldBills = new HeldBillRepository(database);

        // Built from the lane's settings. A peripheral that is not configured yields the honest
        // "none" implementation, so a lane with no printer or no drawer still bills.
        var printer = PeripheralFactory.CreatePrinter(settings.Hardware, CreateRasterizer(settings));
        var drawer = PeripheralFactory.CreateDrawer(settings.Hardware, printer);

        _log.Info("hardware", $"printer: {printer.Name} at {printer.PaperWidthChars} chars, drawer: {drawer.Name}, scale: {settings.Hardware.ScalePort ?? "none"}");

        // Read at the moment a sale completes, so a shift change part way through a bill still
        // attributes it to whoever finished it.
        BillingViewModel? viewModelRef = null;

        var checkout = new CheckoutService(
            invoices,
            customers,
            drawer,
            settings.LoyaltyRules,
            TimeProvider.System,
            printer,
            new ReceiptComposer(settings.Store.ToProfile(), printer.PaperWidthChars, settings.ReceiptLanguage),
            _log,
            () => viewModelRef?.CashierName,
            new StockRepository(database));

        var dayClose = new DayCloseService(
            new DayCloseRepository(database, heldBills),
            new ZReportComposer(settings.Store.ToProfile(), printer.PaperWidthChars, settings.ReceiptLanguage, settings.TaxMode),
            printer,
            new DatabaseBackupService(new DatabaseBackup(database, Path.Combine(DataDirectory, "backups")), log: _log),
            clock: null,
            stock: new StockRepository(database));

        var viewModel = new BillingViewModel(
            new InvoiceEngine(settings.OutletStateCode, settings.TaxMode),
            new ItemRepository(database),
            heldBills,
            customers,
            checkout,
            settings.LaneId,
            new DispatcherDelayScheduler(Dispatcher),
            new SystemClock(),
            settings.SearchDebounce,
            settings.ScannerMaxKeystrokeGap,
            invoices: invoices,
            dayClose: dayClose,
            cashierName: settings.DefaultCashierName);

        viewModelRef = viewModel;

        var billingView = new MainBillingView(viewModel, keymap, settings);

        // The owner's screen, built fresh each time it is opened so its figures are current. The
        // PIN is checked here rather than inside the window, so a refused attempt never gets far
        // enough to read anything.
        billingView.OwnerViewFactory = () =>
            PinPrompt.Passes(billingView, settings.Security)
                ? new OwnerView(BuildOwnerViewModel(settings, database, viewModel))
                : null;

        MainWindow = billingView;
        MainWindow.Show();

        _log.Info("startup", $"till ready, cashier {viewModel.CashierLabel}");
    }

    /// <summary>
    /// Wires the owner's screen to the lane: where its figures come from, and what its two settings
    /// actually change.
    /// </summary>
    /// <remarks>
    /// The tax mode is applied through the billing view model rather than written straight to the
    /// file, because the engine holding the open bill has to agree with what the file says. Writing
    /// only the file would leave the till issuing one kind of document and the settings claiming
    /// another until somebody restarted it.
    /// </remarks>
    private OwnerViewModel BuildOwnerViewModel(PosSettings settings, PosDatabase database, BillingViewModel billing)
    {
        var settingsPath = Path.Combine(DataDirectory, "settings.json");
        var stock = new StockRepository(database);

        return new OwnerViewModel(
            settings.LaneId,
            days =>
            {
                var to = System.DateTimeOffset.Now;
                var from = new DateTimeOffset(to.Date.AddDays(-(days - 1)), to.Offset);

                return new DashboardQuery(database).Gather(settings.LaneId, from, to, topItems: 10);
            },
            stock,
            settings.TaxMode,
            settings.Security.DashboardIsLocked,

            applyTaxMode: mode =>
            {
                // The engine first: it is the one that can refuse, because a bill may be open.
                if (billing.TrySetTaxMode(mode) is { } refused)
                    return refused;

                try
                {
                    settings.TaxMode = mode;
                    SettingsFile.SetTaxMode(settingsPath, mode);
                }
                catch (Exception ex)
                {
                    _log?.Error("settings", "could not write the tax mode", ex);
                    return $"Changed for this session, but it could not be saved: {ex.Message}";
                }

                _log?.Info("settings", $"tax mode set to {mode}");
                return null;
            },

            applyPin: credential =>
            {
                try
                {
                    SettingsFile.SetDashboardPin(settingsPath, credential);
                    settings.Security.DashboardPin = credential;
                }
                catch (Exception ex)
                {
                    _log?.Error("settings", "could not write the dashboard PIN", ex);
                    return $"Could not save it: {ex.Message}";
                }

                _log?.Info("settings", credential is null ? "dashboard PIN cleared" : "dashboard PIN set");
                return null;
            });
    }

    /// <summary>
    /// Builds the text rasteriser, or returns null if the machine cannot supply one.
    /// </summary>
    /// <remarks>
    /// A font engine that will not start must not stop a till from opening. Losing it costs the
    /// Tamil on the receipt, which is a bad receipt; refusing to run costs the shop its counter.
    /// </remarks>
    private ITextRasterizer? CreateRasterizer(PosSettings settings)
    {
        if (settings.Hardware.PrinterRasterMode == RasterMode.Never)
            return null;

        try
        {
            var size = settings.Hardware.ReceiptFontSizeDots > 0
                ? (float)settings.Hardware.ReceiptFontSizeDots
                : GdiTextRasterizer.DefaultEmSizeDots;

            var rasterizer = new GdiTextRasterizer(settings.Hardware.ReceiptFontFamily, size);
            _log?.Info("hardware", $"receipt text drawn in {rasterizer.FontFamily} at {size} dots, mode {settings.Hardware.PrinterRasterMode}");
            return rasterizer;
        }
        catch (Exception ex)
        {
            _log?.Error("hardware", "could not start the receipt text renderer; receipts will print in ASCII only", ex);
            return null;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.Info("shutdown", $"till closing with exit code {e.ApplicationExitCode}");
        _log?.Dispose();
        base.OnExit(e);
    }
}
