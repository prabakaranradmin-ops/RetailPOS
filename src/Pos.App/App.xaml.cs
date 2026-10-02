using System.IO;
using System.Text;
using System.Windows;
using Pos.App.Input;
using Pos.App.ViewModels;
using Pos.App.Views;
using Pos.Core.Analytics;
using Pos.Core.Configuration;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Domain.Catalogue;
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

        // The no-tax build never charges tax, whatever the settings file says. A settings file
        // copied from a GST lane must not be able to make this build start issuing tax invoices.
        settings.TaxMode = ProductVariant.Resolve(settings.TaxMode);
        var keymap = Keymap.LoadOrDefault(Path.Combine(DataDirectory, "keymap.json"));

        // The look the owner chose, before the first window opens, so the till never flashes dark
        // on a lane set to a light look.
        Looks.Follow(settings.ScreenTheme);

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

        // Kept, not built inline, so a layout changed on the owner's screen reaches the next bill.
        var receipts = new ReceiptComposer(settings.Store.ToProfile(), printer.PaperWidthChars, settings.ReceiptLanguage, settings.ReceiptLayout);

        var checkout = new CheckoutService(
            invoices,
            customers,
            drawer,
            settings.LoyaltyRules,
            TimeProvider.System,
            printer,
            receipts,
            _log,
            () => viewModelRef?.CashierName,
            new StockRepository(database, () => settings.LowStockPercent));

        var dayClose = new DayCloseService(
            new DayCloseRepository(database, heldBills),
            new ZReportComposer(settings.Store.ToProfile(), printer.PaperWidthChars, settings.ReceiptLanguage, settings.TaxMode),
            printer,
            new DatabaseBackupService(
                new DatabaseBackup(database, Path.Combine(DataDirectory, "backups")),
                log: _log,
                offMachine: new OffMachineCopy(Path.Combine(DataDirectory, "backups"), settings.LaneId)),
            clock: null,
            stock: new StockRepository(database, () => settings.LowStockPercent),
            expiry: new ExpiryRepository(database));

        var viewModel = new BillingViewModel(
            new InvoiceEngine(settings.OutletStateCode, settings.TaxMode, settings.RoundOffToRupee),
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
            // With cashiers set up, nobody is on the till until somebody signs on with their PIN: a
            // name assumed from the file would put sales against a person who never signed on.
            cashierName: settings.Cashiers.Count > 0 ? null : settings.DefaultCashierName,

            // The till's own drawer and printer, and whoever is on the till when the money is
            // handed over - so the day-end report puts a cash repayment on the right shift.
            credit: new CreditService(
                new CreditRepository(database),
                drawer,
                TimeProvider.System,
                printer,
                new ReceiptComposer(settings.Store.ToProfile(), printer.PaperWidthChars, settings.ReceiptLanguage),
                _log,
                () => viewModelRef?.CashierName),

            // A return settles to the rupee exactly as the lane's sales do, so a cash refund is in
            // coins the drawer has.
            returns: new ReturnService(
                new CreditNoteRepository(database),
                drawer,
                TimeProvider.System,
                printer,
                receipts,
                _log,
                () => viewModelRef?.CashierName,
                () => settings.RoundOffToRupee),

            // The float, expenses and cash in or out, on whoever is on the till when it happens.
            cashDrawer: new CashDrawerService(
                new CashDrawerRepository(database),
                drawer,
                TimeProvider.System,
                _log,
                () => viewModelRef?.CashierName));

        viewModelRef = viewModel;
        viewModel.LowStockPercent = settings.LowStockPercent;

        // The cashiers' PINs, the owner's PIN in front of voids and the like, and the record of both.
        // Read from the settings as they stand, so the owner's screen changes them without a restart.
        viewModel.Security = new TillSecurity(settings, new TillEventRepository(database));

        // An order confirmed to the customer is a message the cashier pastes into their own reply.
        viewModel.ShopName = settings.Store.Name;
        viewModel.CopyText = text => System.Windows.Clipboard.SetText(text);

        // Labels from the shop's own weighing scale, read as the item and its weight or price.
        viewModel.ScaleLabels = settings.ScaleBarcode.ToFormat();

        // A UPI code with the exact amount, once the shop has set its UPI ID - on the payment pane,
        // the customer's screen, and on a slip from the till's own printer for Ctrl+Q.
        viewModel.Upi = settings.Upi.ToPayee(settings.Store.Name);

        // Digital bills: WhatsApp on this computer opens at the customer's chat with the bill typed
        // in, for the cashier to send. The till itself sends nothing.
        viewModel.Store = settings.Store.ToProfile();
        viewModel.OpenLink = settings.OpenWhatsApp ? ShellLinks.Open : null;
        viewModel.PrintUpiSlip = (payee, amount, link) =>
        {
            var outcome = printer.Print(receipts.ComposeUpiSlip(payee, amount, link).ToEscPos(raster: printer.Raster));

            return outcome.Status switch
            {
                PrintStatus.Printed => null,
                PrintStatus.NoPrinterConfigured => "no printer is set up on this lane.",
                _ => outcome.Detail,
            };
        };

        // The shop's offers and schemes, worked out on every bill as it is rung up.
        try
        {
            viewModel.Offers = new OfferRepository(database).All();
        }
        catch (Exception ex)
        {
            // A lane that cannot read its offers bills at full price rather than not at all.
            _log.Error("offers", "the offers could not be read; billing without them", ex);
        }

        // A delivery past its use-by date and probably still on the shelf: said on scanning it.
        var expiry = new ExpiryRepository(database);
        viewModel.ExpiryNote = itemId => Expiry.CheckNote(expiry.ExpiringFor(itemId, DateOnly.FromDateTime(DateTime.Today)));

        var billingView = new MainBillingView(viewModel, keymap, settings);

        // The customer's side of the counter: a second monitor and a pole display, when the lane
        // has either. Following the till, never driving it.
        StartCustomerDisplay(viewModel, settings, billingView);

        // The owner's screen, built fresh each time it is opened so its figures are current. The
        // PIN is checked here rather than inside the window, so a refused attempt never gets far
        // enough to read anything.
        billingView.OwnerViewFactory = () =>
        {
            if (!PinPrompt.Passes(billingView, settings.Security))
                return null;

            var items = new ItemRepository(database);

            // Suggestions come off the same index the till searches on, so what the shop is offered
            // is what the shop actually sells. Shared with the list of items the till sold that are
            // not in the catalogue, which fills it in to add one.
            var newItem = new NewItemViewModel(items, new HsnSuggester(query => items.Search(query)));

            return new OwnerView(
                BuildOwnerViewModel(settings, database, viewModel, receipts),
                new CatalogueImportViewModel(items),
                BuildHardwareViewModel(settings),
                newItem,

                BuildMaintenanceViewModel(settings, database, heldBills, printer),

                // The same store the till attaches customers through, so a name saved on the owner's
                // screen is the name the next bill prints.
                // Statements carry the UPI ID as it is now, so one changed on Settings is on the next.
                new CustomersViewModel(new CustomerQuery(database), customers, new CreditRepository(database))
                {
                    ShopName = settings.Store.Name,
                    Upi = settings.Upi.ToPayee(settings.Store.Name),
                    CopyText = text => System.Windows.Clipboard.SetText(text),
                    RenderStatements = statements => KhataStatementPage.Render(statements, settings.Store.ToProfile(), settings.Upi.ToPayee(settings.Store.Name)),
                    RenderBill = invoiceNo => new InvoiceRepository(database, settings.InvoiceNumber.ToFormat()).FindByInvoiceNo(invoiceNo) is { } found
                        ? InvoicePage.Render(found, settings.Store.ToProfile(), settings.OutletStateCode, isCopy: true)
                        : null,
                },

                // The month's return, read from the same books and written where the owner says.
                new GstReturnViewModel(
                    month => new GstReturnQuery(database).Gather(settings.LaneId, month, settings.OutletStateCode),
                    (data, path) => GstReturnFiles.Write(data, path, settings.Store.Name, settings.Store.Gstin))
                {
                    // The month's books for the accountant, under the ledger names they keep.
                    DayBook = (month, path) =>
                    {
                        var book = new DayBookQuery(database).Month(settings.LaneId, month, settings.DayBook);
                        var files = book.HasAnything ? DayBookFiles.Write(book, path) : [];
                        return new DayBookSaved(book.Vouchers.Count, book.Notes, files);
                    },
                },

                // Deliveries come in against the same catalogue the till sells from, and a supplier
                // paid from the drawer is on whoever is on the till at the time.
                new PurchasesViewModel(
                    new PurchaseRepository(database),
                    query => items.Search(query),
                    code => items.FindByBarcode(code) ?? items.FindBySku(code),
                    settings.LaneId,
                    settings.OutletStateCode,
                    () => viewModel.CashierName),

                // Prices in bulk, and the shelf labels they put out of date, printed on the till's
                // own printer in the lane's language.
                new PricesViewModel(
                    new PriceRepository(database),
                    labels => printer.IsConfigured
                        ? printer.Print(new ShelfLabelComposer(printer.PaperWidthChars, settings.ReceiptLanguage, settings.Store.ToProfile().CurrencyPrefix)
                            .Compose(labels).ToEscPos(raster: printer.Raster))
                        : PrintOutcome.NotConfigured(),
                    settings.Store.Name),

                // What to order, from the same shelf counts and purchase bills. Copied to the
                // clipboard for the owner to send from their own phone; the till sends nothing.
                new OrdersViewModel(
                    cover => new OrderListQuery(database, settings.LowStockPercent).Gather(cover),
                    text => System.Windows.Clipboard.SetText(text),
                    settings.Store.Name,
                    settings.OrderCoverDays,
                    days =>
                    {
                        try
                        {
                            settings.OrderCoverDays = days;
                            SettingsFile.SetOrderCoverDays(Path.Combine(DataDirectory, "settings.json"), days);
                            _log?.Info("settings", $"orders now cover {days} days");
                            return null;
                        }
                        catch (Exception ex)
                        {
                            _log?.Error("settings", "could not write the order cover", ex);
                            return ex.Message;
                        }
                    }),

                // Offers and schemes, from the sheet the owner loads. The till is handed the new
                // list at once, so the next change to a bill is priced by it.
                new OffersViewModel(
                    new OfferRepository(database),
                    items.Skus,
                    items.Categories,
                    offers =>
                    {
                        viewModel.Offers = offers;
                        _log?.Info("offers", $"{Plural.Of(offers.Count, "offer")} loaded");
                    }),

                // What the till sold that is not in the catalogue, waiting to be added.
                new OpenItemsViewModel(new OpenItemRepository(database), newItem));
        };

        MainWindow = billingView;
        MainWindow.Show();

        _log.Info("startup", $"till ready, cashier {viewModel.CashierLabel}");
    }

    /// <summary>
    /// Puts the bill in front of the customer, on the second monitor and the pole display the lane
    /// has. Neither is allowed to stop the till: a failure here is logged and billing goes on.
    /// </summary>
    private void StartCustomerDisplay(BillingViewModel billing, PosSettings settings, Window till)
    {
        try
        {
            var pole = PeripheralFactory.CreatePoleDisplay(settings.Hardware);

            if (!settings.Hardware.CustomerScreen && !pole.IsConfigured)
                return;

            var display = new CustomerDisplayViewModel(billing, settings.Store.Name, pole, settings.ReceiptLanguage);

            if (settings.Hardware.CustomerScreen)
            {
                var window = new CustomerDisplayWindow(display);

                if (window.ShowOnSecondScreen())
                    till.Closed += (_, _) => window.Close();
                else
                    _log?.Warn("display", "customerScreen is on but there is no second monitor; the bill is not shown to the customer");
            }

            _log?.Info("display", $"customer display: screen {(settings.Hardware.CustomerScreen ? "on" : "off")}, pole {pole.Name}");
        }
        catch (Exception ex)
        {
            _log?.Error("display", "the customer display could not start", ex);
        }
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
    private OwnerViewModel BuildOwnerViewModel(PosSettings settings, PosDatabase database, BillingViewModel billing, ReceiptComposer receipts)
    {
        var settingsPath = Path.Combine(DataDirectory, "settings.json");
        var stock = new StockRepository(database, () => settings.LowStockPercent);

        var owner = new OwnerViewModel(
            settings.LaneId,
            days =>
            {
                var to = System.DateTimeOffset.Now;
                var from = new DateTimeOffset(to.Date.AddDays(-(days - 1)), to.Offset);

                return new DashboardQuery(database, settings.LowStockPercent).Gather(settings.LaneId, from, to, topItems: 10);
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

            // Rendered by the same page the `pos dashboard` command writes, so a shop that sends one
            // to its accountant sends the same document either way.
            saveWebPage: (days, path) =>
            {
                try
                {
                    var to = DateTimeOffset.Now;
                    var from = new DateTimeOffset(to.Date.AddDays(-(days - 1)), to.Offset);
                    var data = new DashboardQuery(database, settings.LowStockPercent).Gather(settings.LaneId, from, to, topItems: 10);

                    if (Path.GetDirectoryName(path) is { Length: > 0 } folder)
                        Directory.CreateDirectory(folder);

                    File.WriteAllText(path, DashboardPage.Render(data, settings.Store.Name), new UTF8Encoding(true));

                    _log?.Info("owner", $"figures for {days} days saved to {path}");
                    return null;
                }
                catch (Exception ex)
                {
                    _log?.Error("owner", "could not save the figures as a page", ex);
                    return $"Could not save it: {ex.Message}";
                }
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
            },

            // The till's composer and the shared settings both change, so the next bill and the
            // Hardware tab's preview agree with what was just picked.
            receiptLayout: settings.ReceiptLayout,
            applyReceiptLayout: layout =>
            {
                receipts.Layout = layout;
                settings.ReceiptLayout = layout;

                try
                {
                    SettingsFile.SetReceiptLayout(settingsPath, layout);
                }
                catch (Exception ex)
                {
                    _log?.Error("settings", "could not write the receipt layout", ex);
                    return $"Changed for this session, but it could not be saved: {ex.Message}";
                }

                _log?.Info("settings", $"receipt layout set to {layout}");
                return null;
            },

            // Read live by every stock list through the shared settings, and pushed to the till so
            // its "only N left" follows the same rule as the owner's reorder list.
            lowStockPercent: settings.LowStockPercent,
            applyLowStockPercent: percent =>
            {
                settings.LowStockPercent = percent;
                billing.LowStockPercent = percent;

                try
                {
                    SettingsFile.SetLowStockPercent(settingsPath, percent);
                }
                catch (Exception ex)
                {
                    _log?.Error("settings", "could not write the low-stock percentage", ex);
                    return $"Changed for this session, but it could not be saved: {ex.Message}";
                }

                _log?.Info("settings", $"low stock at {percent}% of full");
                return null;
            },

            // Deliveries near their use-by date, from the purchase bills and the shelf count.
            expiring: () => new ExpiryRepository(database).Expiring(DateOnly.FromDateTime(DateTime.Today)),

            // What has stopped selling, and the money on the shelf in it.
            deadStock: () => new DeadStockRepository(database).NotSelling(DateOnly.FromDateTime(DateTime.Today)),

            // The till's codes follow the new ID from the next payment; the file keeps it.
            upiId: settings.Upi.Id,
            applyUpiId: id =>
            {
                settings.Upi.Id = id;
                billing.Upi = settings.Upi.ToPayee(settings.Store.Name);

                try
                {
                    SettingsFile.SetUpiId(settingsPath, id);
                }
                catch (Exception ex)
                {
                    _log?.Error("settings", "could not write the UPI ID", ex);
                    return $"Changed for this session, but it could not be saved: {ex.Message}";
                }

                _log?.Info("settings", id is null ? "UPI code turned off" : $"UPI ID set to {id}");
                return null;
            },

            // Every window changes at once, the till behind this screen included; the file keeps
            // the choice for the next time the lane starts.
            screenTheme: settings.ScreenTheme,
            applyScreenTheme: theme =>
            {
                settings.ScreenTheme = theme;
                Looks.Follow(theme);

                try
                {
                    SettingsFile.SetScreenTheme(settingsPath, theme);
                }
                catch (Exception ex)
                {
                    _log?.Error("settings", "could not write the screen theme", ex);
                    return $"Changed for this session, but it could not be saved: {ex.Message}";
                }

                _log?.Info("settings", $"screens set to the {theme} look");
                return null;
            });

        // The people on the till and what waits for the owner's PIN. Changed in the settings the
        // till reads at each question, so the next sign-on or void follows it without a restart.
        owner.UseTillAccess(
            settings.Cashiers.Select(c => c.Name.Trim()).ToList(),
            addCashier: (name, pin) => SaveCashiers(settingsPath, settings,
                [.. settings.Cashiers, new CashierSettings { Name = name, Pin = DashboardLock.Create(pin) }],
                $"cashier {name} added"),
            removeCashier: name => SaveCashiers(settingsPath, settings,
                settings.Cashiers.Where(c => !string.Equals(c.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)).ToList(),
                $"cashier {name} taken off"),
            settings.Approvals,
            applyApprovals: approvals =>
            {
                try
                {
                    SettingsFile.SetApprovals(settingsPath, approvals);
                }
                catch (Exception ex)
                {
                    _log?.Error("settings", "could not write the approvals", ex);
                    return $"Not changed: it could not be saved. {ex.Message}";
                }

                settings.Approvals = approvals.Copy();
                _log?.Info("settings", "what waits for the owner's PIN was changed");
                return null;
            });

        // A festival against last year's: the dashboard's own gathering, for the two windows.
        owner.UseFestivals((from, to, items) => new DashboardQuery(database).Gather(settings.LaneId, from, to, items));

        return owner;
    }

    /// <summary>
    /// Saves the cashiers, and only once they are saved puts them in front of the till: a cashier
    /// who exists only until a restart would be locked out of their own till the next morning.
    /// </summary>
    private string? SaveCashiers(string settingsPath, PosSettings settings, List<CashierSettings> cashiers, string what)
    {
        try
        {
            SettingsFile.SetCashiers(settingsPath, cashiers);
        }
        catch (Exception ex)
        {
            _log?.Error("settings", "could not write the cashiers", ex);
            return $"Not changed: it could not be saved. {ex.Message}";
        }

        settings.Cashiers = cashiers;
        _log?.Info("settings", what);
        return null;
    }

    /// <summary>
    /// The peripheral checks, wired to dialogs instead of to a console.
    /// </summary>
    /// <remarks>
    /// The rasteriser is built afresh rather than shared with the till's printer. A check that ran
    /// through a different path from a sale would be checking the wrong thing, but it must also not
    /// be able to disturb the one the counter is using mid-queue.
    /// </remarks>
    /// <summary>
    /// Wires the upkeep screen: backups, the database's health, restoring, and the day-end reports
    /// already taken.
    /// </summary>
    /// <remarks>
    /// The printer is the lane's own, the same one the till settles onto, so a duplicate Z-report
    /// comes out of the machine the original did. A lane with no printer configured gets a message
    /// saying so rather than a throw — the rest of the screen still works without one.
    /// </remarks>
    private MaintenanceViewModel BuildMaintenanceViewModel(
        PosSettings settings,
        PosDatabase database,
        HeldBillRepository heldBills,
        IPrinterService printer) =>
        new(
            database,
            DataDirectory,
            new DayCloseRepository(database, heldBills),
            new ZReportComposer(settings.Store.ToProfile(), printer.PaperWidthChars, settings.ReceiptLanguage, settings.TaxMode),
            settings.LaneId,

            print: report => printer.IsConfigured
                ? printer.Print(report.ToEscPos(raster: printer.Raster))
                : new PrintOutcome(PrintStatus.NoPrinterConfigured, "This lane has no printer configured, so there is nothing to print to."),

            post: action => Dispatcher.Invoke(action),
            offMachine: new OffMachineCopy(Path.Combine(DataDirectory, "backups"), settings.LaneId));

    private HardwareViewModel BuildHardwareViewModel(PosSettings settings) =>
        new(
            settings,
            CreateRasterizer(settings),

            // Called from the thread running the check, so it has to come back to the dispatcher
            // before it can put a window up — and it has to block there until the operator answers,
            // because the answer is the check's result.
            confirm: question => Dispatcher.Invoke(() => Views.ConfirmDialog.Ask(
                Current.Windows.OfType<Window>().LastOrDefault(w => w.IsActive) ?? MainWindow,
                "Checking the hardware",
                question,
                "Yes",
                "No")),

            post: action => Dispatcher.Invoke(action));

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
