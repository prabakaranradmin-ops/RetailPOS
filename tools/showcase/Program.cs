using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Pos.App.Input;
using Pos.App.Tests;
using Pos.App.Views;
using Pos.Core.Configuration;
using Pos.Core.Domain;
using Pos.Core.Domain.Printing;
using Pos.Core.Hardware.Printing;
using Pos.Core.Hardware.Windows;
using Pos.TestSupport;

namespace Pos.Showcase;

/// <summary>
/// Draws the pictures the showcase is made from: the till with a bill in every unit a shop sells in,
/// and every document the lane prints, as the dots the printer would burn.
/// </summary>
/// <remarks>
/// Every bill is rung up on the real till with keys, over a real database, and printed by the same
/// composers the lane uses, in the counter-bill layout (item, quantity with its unit, amount, and
/// the HSN and rate under each line). The shop is made up, and so is every number on it: these
/// pictures are shown to other shops.
/// </remarks>
internal static class Program
{
    private const double ScreenWidth = 1600;
    private const double ScreenHeight = 900;
    private const double Scale = 1.2;
    private const double Gutter = 24;
    private const int PaperChars = ReceiptBuilder.Width80Mm;
    private const int PaperDots = 576;
    private const string Lane = BillingHarness.LaneId;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static readonly StoreProfile Shop = new()
    {
        Name = "ஸ்ரீ லட்சுமி ஸ்டோர்ஸ்",
        AddressLine1 = "12, பெரிய தெரு",
        AddressLine2 = "திருநெல்வேலி - 627001",
        Gstin = "33AABCS1429B1ZX",
        FssaiNumber = "12345678901234",
        FooterMessage = "நன்றி, மீண்டும் வருக!",
    };

    private static readonly UpiPayee Upi = new("srilakshmi.stores@okaxis", "Sri Lakshmi Stores");

    private sealed record Line(string Name, string Hsn, decimal Gst, decimal Price, UnitType Unit, decimal Quantity);

    private sealed record Group(string Key, string? Customer, Line[] Lines);

    /// <summary>A bill for each family of units, with things a Tamil Nadu shop really sells in them.</summary>
    private static readonly Group[] Groups =
    [
        new("standard", null,
        [
            new("துவரம் பருப்பு 1kg", "0713", 5m, 189m, UnitType.Each, 2m),
            new("சர்க்கரை", "1701", 5m, 45m, UnitType.Kilogram, 1.25m),
            new("கடலை எண்ணெய்", "1508", 5m, 210m, UnitType.Litre, 2m),
            new("நைலான் கயிறு", "5607", 12m, 15m, UnitType.Metre, 3m),
        ]),
        new("bunches", null,
        [
            new("பூவன் வாழைப்பழம்", "0803", 0m, 60m, UnitType.Seepu, 2m),
            new("நேந்திரம் வாழைத்தார்", "0803", 0m, 450m, UnitType.Thaar, 1m),
            new("கருவேப்பிலை", "0709", 0m, 10m, UnitType.Kothu, 2m),
            new("தேங்காய் குலை", "0801", 0m, 320m, UnitType.Kulai, 1m),
            new("அரைக்கீரை", "0709", 0m, 15m, UnitType.Kattu, 3m),
            new("புதினா", "0709", 0m, 10m, UnitType.Pidi, 2m),
            new("தேங்காய்", "0801", 0m, 35m, UnitType.Mattai, 2m),
        ]),
        new("heaps", null,
        [
            new("தக்காளி", "0702", 0m, 20m, UnitType.Kooru, 2m),
            new("மாம்பழம்", "0804", 0m, 600m, UnitType.Koodai, 1m),
            new("பலாச்சுளை", "0810", 0m, 5m, UnitType.Sulai, 10m),
            new("பூண்டு", "0703", 0m, 1m, UnitType.Pal, 20m),
            new("பூசணிக்காய்", "0709", 0m, 80m, UnitType.Muzhu, 1m),
            new("தர்பூசணி", "0807", 0m, 20m, UnitType.Keetru, 2m),
            new("ரப்பர் செருப்பு", "6402", 5m, 120m, UnitType.Jodi, 1m),
            new("வெற்றிலை", "1404", 0m, 60m, UnitType.Kavuli, 1m),
            new("வெற்றிலை", "1404", 0m, 35m, UnitType.Suvadu, 1m),
            new("வெற்றிலை", "1404", 0m, 20m, UnitType.Adukku, 1m),
        ]),
        new("packets", null,
        [
            new("2ரூ ஷாம்பு", "3305", 5m, 28m, UnitType.Saram, 1m),
            new("காபி சாஷே", "2101", 5m, 50m, UnitType.Attai, 1m),
            new("மிளகு", "0904", 5m, 20m, UnitType.Pottalam, 2m),
            new("பெருங்காயம்", "1301", 5m, 2m, UnitType.Sittigai, 5m),
            new("நெய்", "0405", 5m, 1m, UnitType.Thuli, 10m),
        ]),
        new("grain", null,
        [
            new("பொன்னி அரிசி", "1006", 0m, 12m, UnitType.Aazhakku, 1.5m),
            new("உளுந்து", "0713", 0m, 30m, UnitType.Uzhakku, 1m),
            new("இட்லி அரிசி", "1006", 0m, 70m, UnitType.Padi, 2.5m),
            new("கம்பு", "1008", 0m, 40m, UnitType.AraiPadi, 1m),
            new("நெல்", "1006", 0m, 450m, UnitType.Marakkaal, 1.5m),
            new("நெல்", "1006", 0m, 5200m, UnitType.Kalam, 1m),
            new("அரிசி மூட்டை 25kg", "1006", 5m, 1450m, UnitType.Moottai, 1m),
            new("வெல்லம்", "1701", 0m, 90m, UnitType.Veesai, 1.5m),
            new("புளி", "0813", 0m, 1200m, UnitType.Thulaam, 0.5m),
        ]),
        new("flowers", "லட்சுமி",
        [
            new("மல்லிகைப் பூ", "0603", 0m, 30m, UnitType.Muzham, 2.5m),
            new("முல்லைப் பூ", "0603", 0m, 15m, UnitType.Saan, 3m),
            new("கனகாம்பரம்", "0603", 0m, 100m, UnitType.Maaru, 1m),
            new("ரோஜா பந்து", "0603", 0m, 80m, UnitType.Panthu, 1m),
        ]),
    ];

    [STAThread]
    private static int Main(string[] args)
    {
        // The theme the tests load: the till's own styles, from the till's own assembly.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Pos.App;component/Theme.xaml", UriKind.RelativeOrAbsolute),
        });

        // "video <acceptance screenshots> [<showcase folder>] [<recordings>]": the tours, in Tamil and
        // in English, made from the acceptance run's screenshots, the pictures drawn below, and the
        // voice recordings (tools\showcase\recordings unless another folder is given).
        if (args.Length >= 2 && args[0] == "video")
        {
            Video.Make(
                Path.GetFullPath(args[1]),
                Path.GetFullPath(args.Length > 2 ? args[2] : Path.Combine("artifacts", "showcase")),
                Path.GetFullPath(args.Length > 3 ? args[3] : Path.Combine("tools", "showcase", "recordings")));
            return 0;
        }

        var output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine("artifacts", "showcase"));
        var screens = Directory.CreateDirectory(Path.Combine(output, "till")).FullName;
        var paper = Directory.CreateDirectory(Path.Combine(output, "bills")).FullName;

        using var rasterizer = new GdiTextRasterizer();
        var raster = new RasterOptions(rasterizer, PaperDots, RasterMode.Auto);

        foreach (var group in Groups)
            BillAGroup(group, screens, paper, raster);

        TheCounterBill(paper, raster);
        TheDay(paper, raster);
        Slips(paper, raster);
        WriteUnits(output);

        Console.WriteLine($"Written to {output}");
        return 0;
    }

    // ---- A bill in each family of units --------------------------------------------------------

    private static void BillAGroup(Group group, string screens, string paper, RasterOptions raster)
    {
        var items = group.Lines
            .Select((line, i) => Catalogue.Item(
                sku: $"{group.Key[..3].ToUpperInvariant()}{i:D2}",
                barcode: Ean13($"8907{Array.IndexOf(Groups, group):D2}{i:D6}"),
                name: line.Name,
                price: line.Price,
                gstRate: line.Gst,
                hsn: line.Hsn,
                unit: line.Unit))
            .ToArray();

        using var till = new BillingHarness(items);
        Cashier(till, "Murugan");

        if (group.Customer is { } name)
        {
            till.AddCustomer("9500012345", name: name);
            till.Press(Key.F7);
            till.ViewModel.EditBuffer = "9500012345";
            till.Press(Key.Enter);
        }

        for (var i = 0; i < items.Length; i++)
            Ring(till, items[i].Barcode!, group.Lines[i].Quantity);

        Shoot(till, Path.Combine(screens, $"units-{group.Key}.png"));

        Pay(till);
        var invoice = till.Invoices.FindLatest(Lane)!;

        Print(Counter(ReceiptLanguage.Tamil).Compose(invoice), raster, Path.Combine(paper, $"units-{group.Key}.png"));

        if (group.Key == "standard")
            Print(Counter(ReceiptLanguage.English).Compose(invoice), raster, Path.Combine(paper, "units-standard-english.png"));
    }

    // ---- The counter bill, as the shop's own bill reads --------------------------------------

    /// <summary>
    /// A small bill in the layout a Tamil Nadu counter bill has - three things, one in a strip of
    /// sachets - settled to the rupee, in the counter layout and in the full tax invoice beside it.
    /// </summary>
    private static void TheCounterBill(string paper, RasterOptions raster)
    {
        InvoiceLine[] lines =
        [
            Rehydrate(1, "2ரூ ஷாம்பு", "3305", 5m, 28m, UnitType.Saram, 1m),
            Rehydrate(2, "40ரூ டிடர்ஜென்ட் சோப்", "3401", 5m, 38m, UnitType.Each, 1m),
            Rehydrate(3, "10ரூ குளியல் சோப்", "3401", 5m, 9.5m, UnitType.Each, 3m),
        ];

        var invoice = Settled(lines, TaxMode.Gst, cashier: "MURUGAN", sequence: 69425);

        Print(Counter(ReceiptLanguage.Tamil).Compose(invoice), raster, Path.Combine(paper, "counter-bill.png"));
        Print(Full(ReceiptLanguage.Tamil).Compose(invoice), raster, Path.Combine(paper, "tax-invoice-full.png"));
        Print(Full(ReceiptLanguage.English).Compose(invoice), raster, Path.Combine(paper, "tax-invoice-full-english.png"));
        Print(Counter(ReceiptLanguage.Tamil).Compose(invoice, isReprint: true), raster, Path.Combine(paper, "reprint.png"));

        // The same goods from a shop in the composition scheme: a bill of supply, no tax on it, and
        // the declaration the rules require.
        InvoiceLine[] untaxed =
        [
            Rehydrate(1, "2ரூ ஷாம்பு", "3305", 0m, 28m, UnitType.Saram, 1m),
            Rehydrate(2, "40ரூ டிடர்ஜென்ட் சோப்", "3401", 0m, 38m, UnitType.Each, 1m),
            Rehydrate(3, "10ரூ குளியல் சோப்", "3401", 0m, 9.5m, UnitType.Each, 3m),
        ];

        Print(Counter(ReceiptLanguage.Tamil).Compose(Settled(untaxed, TaxMode.Composition, "MURUGAN", 1204)), raster, Path.Combine(paper, "bill-of-supply.png"));
    }

    // ---- A day at the counter: a sale, a khata, a return, a payment, the close ----------------

    private static void TheDay(string paper, RasterOptions raster)
    {
        Item[] stock =
        [
            Catalogue.Item(sku: "DAL01", barcode: Ean13("890799000001"), name: "துவரம் பருப்பு 1kg", price: 189m, gstRate: 5m, hsn: "0713"),
            Catalogue.Item(sku: "SUG01", barcode: Ean13("890799000002"), name: "சர்க்கரை", price: 45m, gstRate: 5m, hsn: "1701", unit: UnitType.Kilogram),
            Catalogue.Item(sku: "JAS01", barcode: Ean13("890799000003"), name: "மல்லிகைப் பூ", price: 30m, gstRate: 0m, hsn: "0603", unit: UnitType.Muzham),
        ];

        using var day = new BillingHarness(stock);
        Cashier(day, "Murugan");
        var lakshmi = day.AddCustomer("9500012345", name: "லட்சுமி");

        // A cash sale.
        Ring(day, stock[0].Barcode!, 1m);
        Ring(day, stock[1].Barcode!, 1.5m);
        Ring(day, stock[2].Barcode!, 2m);
        Pay(day);
        var first = day.Invoices.FindLatest(Lane)!;

        // Two packets of dal on Lakshmi's khata.
        day.Press(Key.F7);
        day.ViewModel.EditBuffer = lakshmi.MobileNo;
        day.Press(Key.Enter);
        Ring(day, stock[0].Barcode!, 2m);
        day.Press(Key.F12);
        day.Press(Key.Down);
        day.Press(Key.Down);
        day.Press(Key.Down);
        day.Press(Key.Enter);
        day.Press(Key.Enter);
        var onKhata = day.Invoices.FindLatest(Lane)!;

        // One packet of dal back from the first bill, refunded in cash.
        day.Press(Key.F9);
        day.ViewModel.EditBuffer = first.InvoiceNo;
        day.Press(Key.Enter);
        day.ViewModel.EditBuffer = "1";
        day.Press(Key.Enter);
        day.Press(Key.Enter);
        day.ViewModel.EditBuffer = "பாக்கெட் கிழிந்தது";
        day.Press(Key.Enter);
        var note = day.Returns.Find(day.Returns.ForInvoice(first.InvoiceNo).Single())!;

        // Lakshmi pays back part of what she owes.
        var payment = day.Credit.Collect(lakshmi.Id, 200m, TenderType.Cash, Lane, DateTimeOffset.Now, "Murugan");
        var stillOwed = day.Credit.Balance(lakshmi.Id);
        var statement = KhataStatement.SinceLastClear(lakshmi, day.Credit.Ledger(lakshmi.Id), DateOnly.FromDateTime(DateTime.Today));

        // Shift+F12 shows the day; Shift+F12 again closes it.
        day.Press(Key.F12, ModifierKeys.Shift);
        day.Press(Key.F12, ModifierKeys.Shift);
        var close = day.DayCloses.FindLatest(Lane)!;

        Print(Counter(ReceiptLanguage.Tamil).Compose(onKhata), raster, Path.Combine(paper, "khata-sale.png"));
        Print(Counter(ReceiptLanguage.Tamil).ComposeCreditNote(note), raster, Path.Combine(paper, "credit-note.png"));
        Print(Counter(ReceiptLanguage.Tamil).ComposeCollection(lakshmi, payment, stillOwed), raster, Path.Combine(paper, "khata-payment.png"));
        Print(Counter(ReceiptLanguage.Tamil).ComposeKhataStatement(statement, Upi), raster, Path.Combine(paper, "khata-statement.png"));
        Print(new ZReportComposer(Shop, PaperChars, ReceiptLanguage.Tamil).Compose(close), raster, Path.Combine(paper, "day-end.png"));
        Print(new ZReportComposer(Shop, PaperChars, ReceiptLanguage.English).Compose(close), raster, Path.Combine(paper, "day-end-english.png"));
    }

    // ---- The slips: the UPI code, and shelf labels -------------------------------------------

    private static void Slips(string paper, RasterOptions raster)
    {
        Print(Counter(ReceiptLanguage.Tamil).ComposeUpiSlip(Upi, 94m, UpiLink.For(Upi, 94m)), raster, Path.Combine(paper, "upi-slip.png"));

        ShelfLabel[] labels =
        [
            new(1, "SHP01", "ஷாம்பு 340ml", "8901234567897", UnitType.Each, 299m, 289m),
            new(2, "RIC01", "பொன்னி அரிசி", null, UnitType.Padi, 80m, 70m),
            new(3, "JAS01", "மல்லிகைப் பூ", null, UnitType.Muzham, 30m, 30m),
        ];

        Print(new ShelfLabelComposer(PaperChars, ReceiptLanguage.Tamil).Compose(labels), raster, Path.Combine(paper, "shelf-labels.png"));
    }

    // ---- Every unit, for the table on the page -----------------------------------------------

    private static void WriteUnits(string output)
    {
        var rows = Units.All.Select(u => new
        {
            code = u.Code,
            tamil = u.Tamil,
            fractional = u.Fractional,
            group = u.Group.ToString(),
            meaning = u.Meaning,
        });

        var json = JsonSerializer.Serialize(rows, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        File.WriteAllText(Path.Combine(output, "units.json"), json, new System.Text.UTF8Encoding(false));
    }

    // ---- At the till ---------------------------------------------------------------------------

    private static void Cashier(BillingHarness till, string name)
    {
        till.Press(Key.U, ModifierKeys.Control);
        till.ViewModel.EditBuffer = name;
        till.Press(Key.Enter);
    }

    /// <summary>Scans an item, and keys its quantity with F3 when it is not one.</summary>
    private static void Ring(BillingHarness till, string barcode, decimal quantity)
    {
        till.Scan(barcode);

        if (quantity == 1m)
            return;

        till.Press(Key.F3);
        till.ViewModel.EditBuffer = quantity.ToString("0.###", Invariant);
        till.Press(Key.Enter);
    }

    /// <summary>F12, cash for the whole bill, and Enter again to finish.</summary>
    private static void Pay(BillingHarness till)
    {
        till.Press(Key.F12);
        till.Press(Key.Enter);
        till.Press(Key.Enter);
    }

    /// <summary>
    /// The billing screen as it stands, as a 1600 × 900 shop monitor shows it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shown far off the desktop and never activated, so it lays out for real - a tab or a grid
    /// builds its rows only once it is on a presentation source - without taking the keyboard from
    /// whoever is using the machine.
    /// </para>
    /// <para>
    /// Windows will not make a window bigger than the screen it is on, and the part of the till past
    /// that edge was clipped away. So the window keeps whatever size it is allowed, and the till
    /// inside it is laid out at the monitor's size and shrunk to fit; the picture is then drawn from
    /// the laid-out till at full size.
    /// </para>
    /// </remarks>
    private static void Shoot(BillingHarness till, string path)
    {
        var settings = new PosSettings
        {
            LaneId = Lane,
            OutletStateCode = BillingHarness.OutletStateCode,
            Store = { Name = Shop.Name, Gstin = Shop.Gstin },
        };

        var window = new MainBillingView(till.ViewModel, Keymap.Default, settings)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowState = WindowState.Normal,
            Left = -20000,
            Top = -20000,
            ShowInTaskbar = false,
            ShowActivated = false,
            Width = ScreenWidth,
            Height = ScreenHeight,
        };

        try
        {
            window.Show();
            window.UpdateLayout();

            var root = (FrameworkElement)window.Content;
            var allowedWidth = root.ActualWidth + root.Margin.Left + root.Margin.Right;
            var allowedHeight = root.ActualHeight + root.Margin.Top + root.Margin.Bottom;
            var shrink = Math.Min(1.0, Math.Min(allowedWidth / ScreenWidth, allowedHeight / ScreenHeight));

            root.LayoutTransform = new ScaleTransform(shrink, shrink);
            root.Width = ScreenWidth - root.Margin.Left - root.Margin.Right;
            root.Height = ScreenHeight - root.Margin.Top - root.Margin.Bottom;
            window.UpdateLayout();
            Settle(window);
            Settle(window);

            // A little room on the right: text measured at the shrunk size comes out a hair wider
            // than it was measured, and the header's last word ran off the edge.
            const double canvasWidth = ScreenWidth + Gutter;
            var pixelsWide = (int)Math.Ceiling(canvasWidth * Scale);
            var pixelsHigh = (int)Math.Ceiling(ScreenHeight * Scale);

            // The till as laid out and shrunk, drawn at a resolution that undoes the shrink: it is
            // shapes and text, so it comes out as sharp as if it had never been made smaller.
            var dpi = 96 * Scale / shrink;
            var drawn = new RenderTargetBitmap(pixelsWide, pixelsHigh, dpi, dpi, PixelFormats.Pbgra32);
            drawn.Render(root);

            // On the window's own background, which the till's panels sit on.
            var picture = new DrawingVisual();

            using (var context = picture.RenderOpen())
            {
                context.DrawRectangle(window.Background ?? Brushes.Black, null, new Rect(0, 0, canvasWidth, ScreenHeight));
                context.DrawImage(drawn, new Rect(0, 0, canvasWidth, ScreenHeight));
            }

            var bitmap = new RenderTargetBitmap(pixelsWide, pixelsHigh, 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
            bitmap.Render(picture);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            using var file = File.Create(path);
            encoder.Save(file);
        }
        finally
        {
            window.Close();
        }
    }

    private static void Settle(Window window)
    {
        var frame = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
        window.UpdateLayout();
    }

    // ---- On paper --------------------------------------------------------------------------------

    /// <summary>The counter bill: item, quantity with its unit, amount, and the HSN and rate under each line.</summary>
    private static ReceiptComposer Counter(ReceiptLanguage language) => new(Shop, PaperChars, language, ReceiptLayout.Compact);

    /// <summary>The full tax invoice, with its rate and tax columns.</summary>
    private static ReceiptComposer Full(ReceiptLanguage language) => new(Shop, PaperChars, language, ReceiptLayout.Standard);

    /// <summary>Saves the dots the printer would burn, the same way the lane's own preview does.</summary>
    private static void Print(ReceiptBuilder receipt, RasterOptions raster, string path)
    {
        ReceiptImage.SavePng(receipt.ToBitmap(raster), path);
        Console.WriteLine($"  {Path.GetFileName(path)}");
    }

    private static InvoiceLine Rehydrate(long id, string name, string hsn, decimal gst, decimal price, UnitType unit, decimal quantity) =>
        InvoiceLine.Rehydrate(id, name, hsn, null, null, unit, price, price, true, gst, quantity, 0m, false);

    /// <summary>A cash sale, settled to the rupee as a counter bill is.</summary>
    private static SettledInvoice Settled(InvoiceLine[] lines, TaxMode mode, string cashier, long sequence)
    {
        var totals = InvoiceTotals.From(lines, roundToRupee: true);
        var when = DateTimeOffset.Now;

        var sale = new SaleDraft(
            "T1",
            when,
            null,
            lines,
            totals,
            [new Tender(TenderType.Cash, totals.AmountPayable)],
            ChangeDue: 0m,
            PointsRedeemed: 0,
            PointsEarned: 0,
            RecalledFromToken: null,
            CashierName: cashier,
            TaxMode: mode);

        return new SettledInvoice(0, InvoiceNumberFormat.Default.Format("T1", FiscalYear.For(when), sequence), sale);
    }

    /// <summary>Twelve digits and the EAN-13 check digit a scanner would read.</summary>
    private static string Ean13(string twelve)
    {
        var sum = 0;

        for (var i = 0; i < 12; i++)
            sum += (twelve[i] - '0') * (i % 2 == 0 ? 1 : 3);

        return twelve + ((10 - sum % 10) % 10);
    }
}
