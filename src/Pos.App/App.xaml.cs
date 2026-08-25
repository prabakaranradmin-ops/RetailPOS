using System.IO;
using System.Windows;
using Pos.App.Input;
using Pos.App.ViewModels;
using Pos.App.Views;
using Pos.Core.Configuration;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Domain.Printing;

namespace Pos.App;

public partial class App : Application
{
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

        var settings = PosSettings.LoadOrDefault(Path.Combine(DataDirectory, "settings.json"));
        var keymap = Keymap.LoadOrDefault(Path.Combine(DataDirectory, "keymap.json"));

        var database = new PosDatabase(Path.Combine(DataDirectory, "pos.db"));
        database.EnsureMigrated();

        var customers = new CustomerRepository(database);

        // Built from the lane's settings. A peripheral that is not configured yields the honest
        // "none" implementation, so a lane with no printer or no drawer still bills.
        var printer = PeripheralFactory.CreatePrinter(settings.Hardware);
        var drawer = PeripheralFactory.CreateDrawer(settings.Hardware, printer);

        var checkout = new CheckoutService(
            new InvoiceRepository(database),
            customers,
            drawer,
            settings.LoyaltyRules,
            TimeProvider.System,
            printer,
            new ReceiptComposer(settings.Store.ToProfile(), printer.PaperWidthChars));

        var viewModel = new BillingViewModel(
            new InvoiceEngine(settings.OutletStateCode),
            new ItemRepository(database),
            new HeldBillRepository(database),
            customers,
            checkout,
            settings.LaneId,
            new DispatcherDelayScheduler(Dispatcher),
            new SystemClock(),
            settings.SearchDebounce,
            settings.ScannerMaxKeystrokeGap);

        MainWindow = new MainBillingView(viewModel, keymap, settings);
        MainWindow.Show();
    }
}
