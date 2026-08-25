using System.Text.Json.Serialization;
using Pos.Core.Domain.Printing;
using Pos.Core.Hardware.Drawer;
using Pos.Core.Hardware.Printing;
using Pos.Core.Hardware.Scanning;
using Pos.Core.Hardware.Serial;
using Pos.Core.Hardware.Weighing;

namespace Pos.Core.Configuration;

/// <summary>The store's details, in the form the settings file holds them.</summary>
public sealed class StoreProfileSettings
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "RetailPOS";

    [JsonPropertyName("addressLine1")]
    public string? AddressLine1 { get; set; }

    [JsonPropertyName("addressLine2")]
    public string? AddressLine2 { get; set; }

    [JsonPropertyName("phone")]
    public string? Phone { get; set; }

    [JsonPropertyName("gstin")]
    public string? Gstin { get; set; }

    [JsonPropertyName("footerMessage")]
    public string? FooterMessage { get; set; }

    [JsonPropertyName("currencyPrefix")]
    public string CurrencyPrefix { get; set; } = "Rs.";

    public StoreProfile ToProfile() => new()
    {
        Name = Name,
        AddressLine1 = AddressLine1,
        AddressLine2 = AddressLine2,
        Phone = Phone,
        Gstin = Gstin,
        FooterMessage = FooterMessage,
        CurrencyPrefix = CurrencyPrefix,
    };
}

/// <summary>
/// Builds the peripheral services a lane's settings describe.
/// </summary>
/// <remarks>
/// One place decides what a lane's hardware is, so the billing screen and the diagnostics tool
/// cannot disagree about it. A peripheral that is not configured yields the honest "none"
/// implementation rather than null, so nothing downstream has to null-check its way through a
/// sale.
/// </remarks>
public static class PeripheralFactory
{
    public static IPrinterService CreatePrinter(HardwareSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!string.IsNullOrWhiteSpace(settings.PrinterOutputFile))
            return new FilePrinterService(settings.PrinterOutputFile, settings.PrinterPaperWidthChars);

        if (string.IsNullOrWhiteSpace(settings.PrinterName))
            return new NoPrinterService(settings.PrinterPaperWidthChars);

        return OperatingSystem.IsWindows()
            ? new RawSpoolPrinterService(settings.PrinterName, settings.PrinterPaperWidthChars)
            : new NoPrinterService(settings.PrinterPaperWidthChars);
    }

    public static IDrawerService CreateDrawer(HardwareSettings settings, IPrinterService printer)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(printer);

        return settings.DrawerConnection switch
        {
            DrawerConnection.Printer => new PrinterPassthroughDrawerService(
                printer, settings.DrawerPin, settings.DrawerPulseOnMs, settings.DrawerPulseOffMs),

            DrawerConnection.Serial when !string.IsNullOrWhiteSpace(settings.DrawerPort) => new SerialDrawerService(
                new SystemSerialPort(new SerialPortSettings(settings.DrawerPort)),
                settings.DrawerPin,
                settings.DrawerPulseOnMs,
                settings.DrawerPulseOffMs),

            _ => new NoDrawerService(),
        };
    }

    /// <summary>
    /// The scanner. An empty port is the usual case, not a misconfiguration: most scanners present
    /// as a keyboard, and those bursts reach the till through the UI rather than a serial line.
    /// </summary>
    public static IScannerService CreateScanner(HardwareSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return string.IsNullOrWhiteSpace(settings.ScannerPort)
            ? new KeyboardWedgeScannerService()
            : new SerialScannerService(new SystemSerialPort(new SerialPortSettings(settings.ScannerPort, settings.ScannerBaudRate)));
    }

    public static IScaleService CreateScale(HardwareSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return string.IsNullOrWhiteSpace(settings.ScalePort)
            ? new NoScaleService()
            : new SerialScaleService(new SystemSerialPort(new SerialPortSettings(settings.ScalePort, settings.ScaleBaudRate)));
    }
}
