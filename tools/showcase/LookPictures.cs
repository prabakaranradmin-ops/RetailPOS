using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pos.App;
using Pos.App.Tests;
using Pos.App.ViewModels;
using Pos.App.Views;
using Pos.Core.Configuration;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Domain.Catalogue;
using Pos.Core.Domain.Printing;
using Pos.Core.Hardware.Printing;
using Pos.TestSupport;

namespace Pos.Showcase;

/// <summary>
/// The till and the owner's screen in each of the four looks - morning, noon, evening and night -
/// so a shop can see them side by side before choosing.
/// </summary>
/// <remarks>
/// One shop's day, drawn four times: a few bills paid so the owner's figures have something in
/// them, and one more on the till, half rung. Only the look changes between the pictures.
/// </remarks>
internal static class LookPictures
{
    private static readonly Item[] Shelf =
    [
        Catalogue.Item(sku: "DAL01", barcode: "8907000000017", name: "Toor Dal 1kg", price: 189m, gstRate: 5m, hsn: "0713"),
        Catalogue.Item(sku: "OIL01", barcode: "8907000000024", name: "Sunflower Oil 1L", price: 165m, gstRate: 5m, hsn: "1512"),
        Catalogue.Item(sku: "RIC01", barcode: "8907000000031", name: "Ponni Rice 5kg", price: 349m, gstRate: 5m, hsn: "1006"),
        Catalogue.Item(sku: "SOP01", barcode: "8907000000048", name: "Bath Soap 100g", price: 38m, gstRate: 18m, hsn: "3401"),
        Catalogue.Item(sku: "TEA01", barcode: "8907000000055", name: "Tea Powder 250g", price: 140m, gstRate: 5m, hsn: "0902"),
        Catalogue.Item(sku: "BIS01", barcode: "8907000000062", name: "Glucose Biscuits", price: 10m, gstRate: 18m, hsn: "1905"),
    ];

    public static void Draw(string output)
    {
        var folder = Directory.CreateDirectory(Path.Combine(output, "looks")).FullName;

        using var till = new BillingHarness(Shelf);
        Cashier(till, "Murugan");

        // A morning's trade, so the figures are not empty.
        foreach (var bill in new[] { new[] { 0, 1, 2 }, new[] { 3, 3, 4 }, new[] { 2, 5, 5, 5, 1 }, new[] { 0, 4 } })
        {
            foreach (var i in bill)
                till.Scan(Shelf[i].Barcode!);

            till.Press(Key.F12);
            till.Press(Key.Enter);
            till.Press(Key.Enter);
        }

        // And the bill on the counter now.
        foreach (var i in new[] { 0, 1, 3, 5, 5 })
            till.Scan(Shelf[i].Barcode!);

        try
        {
            foreach (var look in new[] { ScreenTheme.Morning, ScreenTheme.Noon, ScreenTheme.Evening, ScreenTheme.Night })
            {
                Looks.Apply(look);
                var name = look.ToString().ToLowerInvariant();

                Program.Shoot(till, Path.Combine(folder, $"till-{name}.png"));
                Program.Snap(Owner(till, look), Path.Combine(folder, $"owner-{name}.png"));
                Program.Snap(Owner(till, look), Path.Combine(folder, $"owner-settings-{name}.png"), ready: ShowTheLookCard);

                Console.WriteLine($"Drew the {name} look.");
            }
        }
        finally
        {
            Looks.Apply(ScreenTheme.Night);
        }
    }

    /// <summary>The Settings tab, scrolled to where the look is chosen.</summary>
    private static void ShowTheLookCard(Window owner)
    {
        var tabs = FindTabs(owner);

        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => (string)t.Header == "Settings");
        owner.UpdateLayout();
        Program.Settle(owner);

        if (owner.FindName("LookCard") is FrameworkElement card)
        {
            card.BringIntoView();
            owner.UpdateLayout();
        }
    }

    private static TabControl FindTabs(DependencyObject root) =>
        Find(root) ?? throw new InvalidOperationException("The owner's screen has no tabs.");

    private static TabControl? Find(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);

            if (child is TabControl tabs)
                return tabs;

            if (Find(child) is { } deeper)
                return deeper;
        }

        return null;
    }

    private static void Cashier(BillingHarness till, string name)
    {
        till.Press(Key.U, ModifierKeys.Control);
        till.ViewModel.EditBuffer = name;
        till.Press(Key.Enter);
    }

    /// <summary>The owner's screen over the till's own database, wired as the lane wires it.</summary>
    private static OwnerView Owner(BillingHarness till, ScreenTheme look)
    {
        var database = till.Database;
        var lane = BillingHarness.LaneId;
        var items = till.Items;
        var settings = new PosSettings { LaneId = lane, OutletStateCode = BillingHarness.OutletStateCode, Store = { Name = BillingHarness.Store.Name } };

        var owner = new OwnerViewModel(
            lane,
            days =>
            {
                var to = DateTimeOffset.Now;
                return new Pos.Core.Analytics.DashboardQuery(database).Gather(lane, new DateTimeOffset(to.Date.AddDays(-(days - 1)), to.Offset), to);
            },
            till.Stock,
            TaxMode.Gst,
            isPinSet: false,
            applyTaxMode: _ => null,
            applyPin: _ => null,
            saveWebPage: (_, _) => null,
            receiptLayout: ReceiptLayout.Compact,
            applyReceiptLayout: _ => null,
            upiId: "srilakshmi.stores@okaxis",
            applyUpiId: _ => null,
            screenTheme: look,
            applyScreenTheme: _ => null);

        return new OwnerView(
            owner,
            new CatalogueImportViewModel(items),
            new HardwareViewModel(settings, rasterizer: null, confirm: _ => true, post: action => action()),
            new NewItemViewModel(items, new HsnSuggester(query => items.Search(query))),
            new MaintenanceViewModel(
                database,
                Path.GetDirectoryName(database.DatabasePath)!,
                till.DayCloses,
                new ZReportComposer(BillingHarness.Store, ReceiptBuilder.Width80Mm, ReceiptLanguage.English, TaxMode.Gst),
                lane,
                print: _ => PrintOutcome.NotConfigured(),
                post: action => action()),
            new CustomersViewModel(new Pos.Core.Analytics.CustomerQuery(database), till.Customers),
            new GstReturnViewModel(
                month => new Pos.Core.Analytics.GstReturnQuery(database).Gather(lane, month, BillingHarness.OutletStateCode),
                (_, _) => []),
            new PurchasesViewModel(
                new PurchaseRepository(database),
                query => items.Search(query),
                code => items.FindByBarcode(code) ?? items.FindBySku(code),
                lane,
                BillingHarness.OutletStateCode),
            new PricesViewModel(new PriceRepository(database), _ => PrintOutcome.NotConfigured(), BillingHarness.Store.Name),
            new OrdersViewModel(
                cover => new Pos.Core.Analytics.OrderListQuery(database).Gather(cover),
                _ => { },
                BillingHarness.Store.Name),
            new OffersViewModel(new OfferRepository(database), items.Skus, items.Categories));
    }
}
