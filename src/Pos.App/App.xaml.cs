using System.IO;
using System.Windows;
using Pos.App.Input;
using Pos.App.ViewModels;
using Pos.App.Views;
using Pos.Core.Configuration;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Domain.Printing;
using Pos.Core.Logging;

namespace Pos.App;

public partial class App : Application
{
    private FileLog? _log;

    /// <summary>
    /// Everything this lane owns — database, settings, keymap — lives under one local folder.
    /// Nothing is fetched from a server, at startup or afterwards.
    /// </summary>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RetailPOS");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
        var invoices = new InvoiceRepository(database);
        var heldBills = new HeldBillRepository(database);

        // Built from the lane's settings. A peripheral that is not configured yields the honest
        // "none" implementation, so a lane with no printer or no drawer still bills.
        var printer = PeripheralFactory.CreatePrinter(settings.Hardware);
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
            new ReceiptComposer(settings.Store.ToProfile(), printer.PaperWidthChars),
            _log,
            () => viewModelRef?.CashierName);

        var dayClose = new DayCloseService(
            new DayCloseRepository(database, heldBills),
            new ZReportComposer(settings.Store.ToProfile(), printer.PaperWidthChars),
            printer,
            new DatabaseBackupService(new DatabaseBackup(database, Path.Combine(DataDirectory, "backups")), log: _log));

        var viewModel = new BillingViewModel(
            new InvoiceEngine(settings.OutletStateCode),
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

        MainWindow = new MainBillingView(viewModel, keymap, settings);
        MainWindow.Show();

        _log.Info("startup", $"till ready, cashier {viewModel.CashierLabel}");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.Info("shutdown", $"till closing with exit code {e.ApplicationExitCode}");
        _log?.Dispose();
        base.OnExit(e);
    }
}
