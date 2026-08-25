using Pos.Core.Configuration;
using Pos.Core.Hardware.Drawer;
using Pos.Core.Hardware.Printing;
using Pos.Core.Hardware.Scanning;
using Pos.Core.Hardware.Weighing;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// The lane's settings file, and what it builds. One place decides what a lane's hardware is, so
/// the billing screen and the diagnostics tool cannot disagree about it.
/// </summary>
public class ConfigurationTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "pos-settings-tests", Guid.NewGuid().ToString("N"));

    public ConfigurationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Write(string name, string json)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, json);
        return path;
    }

    // ---- Settings -----------------------------------------------------------------------------

    [Fact]
    public void AMissingFileYieldsWorkingDefaults()
    {
        var settings = PosSettings.LoadOrDefault(Path.Combine(_directory, "absent.json"));

        Assert.Equal("L1", settings.LaneId);
        Assert.Equal(48, settings.Hardware.PrinterPaperWidthChars);
        Assert.Equal(DrawerConnection.Printer, settings.Hardware.DrawerConnection);
    }

    [Fact]
    public void TheStoreAndHardwareSectionsAreRead()
    {
        var path = Write("settings.json", """
            {
              "laneId": "COUNTER-2",
              "outletStateCode": "29",
              "store": { "name": "Sri Lakshmi Stores", "gstin": "33AABCS1429B1ZX" },
              "hardware": { "printerName": "POS-80", "printerPaperWidthChars": 32, "scalePort": "COM3" }
            }
            """);

        var settings = PosSettings.LoadOrDefault(path);

        Assert.Equal("COUNTER-2", settings.LaneId);
        Assert.Equal("29", settings.OutletStateCode);
        Assert.Equal("Sri Lakshmi Stores", settings.Store.Name);
        Assert.Equal("33AABCS1429B1ZX", settings.Store.ToProfile().Gstin);
        Assert.Equal("POS-80", settings.Hardware.PrinterName);
        Assert.Equal(32, settings.Hardware.PrinterPaperWidthChars);
        Assert.Equal("COM3", settings.Hardware.ScalePort);
    }

    /// <summary>
    /// A shopkeeper editing this file should see a name, not a number they have to look up.
    /// </summary>
    [Theory]
    [InlineData("\"None\"", DrawerConnection.None)]
    [InlineData("\"Printer\"", DrawerConnection.Printer)]
    [InlineData("\"Serial\"", DrawerConnection.Serial)]
    public void TheDrawerConnectionIsWrittenByName(string json, DrawerConnection expected)
    {
        var path = Write("settings.json", $$"""
            { "hardware": { "drawerConnection": {{json}}, "drawerPort": "COM2" } }
            """);

        Assert.Equal(expected, PosSettings.LoadOrDefault(path).Hardware.DrawerConnection);
    }

    [Fact]
    public void SettingsRoundTripThroughTheFile()
    {
        var original = new PosSettings
        {
            LaneId = "L9",
            OutletStateCode = "27",
            Store = { Name = "Test Stores", Gstin = "27AAAAA0000A1Z5" },
            Hardware = { PrinterName = "POS-58", PrinterPaperWidthChars = 32, DrawerConnection = DrawerConnection.Serial, DrawerPort = "COM4" },
        };

        var path = Path.Combine(_directory, "round-trip.json");
        original.Save(path);

        var reloaded = PosSettings.LoadOrDefault(path);

        Assert.Equal("L9", reloaded.LaneId);
        Assert.Equal("Test Stores", reloaded.Store.Name);
        Assert.Equal(DrawerConnection.Serial, reloaded.Hardware.DrawerConnection);
        Assert.Equal("COM4", reloaded.Hardware.DrawerPort);
    }

    /// <summary>
    /// A lane told to kick a drawer on a port it does not name should say so at startup, not the
    /// first time a cashier takes cash.
    /// </summary>
    [Fact]
    public void ASerialDrawerWithNoPortIsRefusedAtStartup()
    {
        var path = Write("settings.json", """
            { "hardware": { "drawerConnection": "Serial" } }
            """);

        var ex = Assert.Throws<InvalidOperationException>(() => PosSettings.LoadOrDefault(path));
        Assert.Contains("hardware setup", ex.Message);
    }

    [Theory]
    [InlineData("""{ "hardware": { "printerPaperWidthChars": 8 } }""")]
    [InlineData("""{ "hardware": { "drawerPin": 7 } }""")]
    [InlineData("""{ "hardware": { "drawerPulseOnMs": 5000 } }""")]
    public void AnUnworkableHardwareSetupIsRefused(string json)
    {
        var path = Write("settings.json", json);

        Assert.Throws<InvalidOperationException>(() => PosSettings.LoadOrDefault(path));
    }

    [Fact]
    public void AMalformedFileFailsLoudlyAndNamesTheFile()
    {
        var path = Write("broken.json", "{ not json");

        var ex = Assert.Throws<InvalidOperationException>(() => PosSettings.LoadOrDefault(path));
        Assert.Contains(path, ex.Message);
    }

    // ---- What the settings build --------------------------------------------------------------

    [Fact]
    public void ALaneWithNoPrinterNamedGetsTheHonestNoPrinter()
    {
        var printer = PeripheralFactory.CreatePrinter(new HardwareSettings());

        Assert.IsType<NoPrinterService>(printer);
        Assert.False(printer.IsConfigured);
    }

    /// <summary>
    /// An output file wins over a printer name, so a lane being set up before its printer arrives
    /// can capture receipts without pretending to print them.
    /// </summary>
    [Fact]
    public void AnOutputFileTakesPrecedenceOverAPrinterName()
    {
        var printer = PeripheralFactory.CreatePrinter(new HardwareSettings
        {
            PrinterName = "POS-80",
            PrinterOutputFile = Path.Combine(_directory, "receipts.escpos"),
        });

        Assert.IsType<FilePrinterService>(printer);
        Assert.True(printer.IsConfigured);
    }

    [Fact]
    public void ThePaperWidthReachesThePrinter()
    {
        var printer = PeripheralFactory.CreatePrinter(new HardwareSettings { PrinterPaperWidthChars = 32 });

        Assert.Equal(32, printer.PaperWidthChars);
    }

    [Fact]
    public void ADrawerOnThePrinterIsBuiltAsAPassthrough()
    {
        var printer = new LoopbackPrinterService();
        var drawer = PeripheralFactory.CreateDrawer(new HardwareSettings { DrawerConnection = DrawerConnection.Printer }, printer);

        Assert.IsType<PrinterPassthroughDrawerService>(drawer);

        drawer.Kick();
        Assert.Equal(EscPos.KickDrawer(), printer.LastJob);
    }

    [Fact]
    public void TheConfiguredPulseTimingsReachTheDrawer()
    {
        var printer = new LoopbackPrinterService();
        var drawer = PeripheralFactory.CreateDrawer(
            new HardwareSettings { DrawerPin = 1, DrawerPulseOnMs = 100, DrawerPulseOffMs = 200 },
            printer);

        drawer.Kick();

        Assert.Equal(EscPos.KickDrawer(pin: 1, onMilliseconds: 100, offMilliseconds: 200), printer.LastJob);
    }

    [Fact]
    public void ALaneWithNoDrawerGetsTheHonestNoDrawer()
    {
        var drawer = PeripheralFactory.CreateDrawer(new HardwareSettings { DrawerConnection = DrawerConnection.None }, new LoopbackPrinterService());

        Assert.IsType<NoDrawerService>(drawer);
        Assert.False(drawer.IsConfigured);
    }

    /// <summary>
    /// No scanner port is the usual case, not a misconfiguration: most scanners present as a
    /// keyboard, and those bursts reach the till through the UI instead.
    /// </summary>
    [Fact]
    public void NoScannerPortMeansTheKeyboardWedgePath()
    {
        Assert.IsType<KeyboardWedgeScannerService>(PeripheralFactory.CreateScanner(new HardwareSettings()));
    }

    [Fact]
    public void ALaneWithNoScaleGetsTheHonestNoScale()
    {
        using var scale = PeripheralFactory.CreateScale(new HardwareSettings());

        Assert.IsType<NoScaleService>(scale);
        Assert.False(scale.IsConfigured);
    }
}
