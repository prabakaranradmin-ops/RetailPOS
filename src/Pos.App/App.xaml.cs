using System.IO;
using System.Windows;
using Pos.App.Input;
using Pos.App.ViewModels;
using Pos.App.Views;
using Pos.Core.Data;
using Pos.Core.Domain;

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

        var viewModel = new BillingViewModel(
            new InvoiceEngine(settings.OutletStateCode),
            new ItemRepository(database),
            new DispatcherDelayScheduler(Dispatcher),
            new SystemClock(),
            settings.SearchDebounce,
            settings.ScannerMaxKeystrokeGap);

        MainWindow = new MainBillingView(viewModel, keymap, settings);
        MainWindow.Show();
    }
}
