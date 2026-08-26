using System.Text.Json.Serialization;
using Pos.Core.Domain;
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

    /// <summary>The outlet's FSSAI licence number, which a shop selling food has to display.</summary>
    [JsonPropertyName("fssaiNumber")]
    public string? FssaiNumber { get; set; }

    /// <summary>
    /// The number printed for a customer to ring about a bill. When set it replaces
    /// <see cref="Phone"/> on the receipt rather than joining it, so the bill carries one number.
    /// </summary>
    [JsonPropertyName("customerCarePhone")]
    public string? CustomerCarePhone { get; set; }

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
        FssaiNumber = FssaiNumber,
        CustomerCarePhone = CustomerCarePhone,
        FooterMessage = FooterMessage,
        CurrencyPrefix = CurrencyPrefix,
    };
}

/// <summary>How this lane composes invoice numbers, in the form the settings file holds it.</summary>
public sealed class InvoiceNumberSettings
{
    /// <summary>The shop's prefix — the <c>RM</c> of <c>RM/26-27/11358</c>.</summary>
    [JsonPropertyName("storePrefix")]
    public string StorePrefix { get; set; } = "INV";

    /// <summary>
    /// Whether the lane id appears before the sequence. Leave this on unless the shop has exactly
    /// one till: each lane mints its own 1, 2, 3…, so without it two tills issue the same numbers
    /// and there is no server to notice.
    /// </summary>
    [JsonPropertyName("includeLaneSegment")]
    public bool IncludeLaneSegment { get; set; } = true;

    /// <summary>Zero-padding on the sequence. Zero prints it as-is, as a counter bill does.</summary>
    [JsonPropertyName("sequencePadding")]
    public int SequencePadding { get; set; }

    public InvoiceNumberFormat ToFormat() => new()
    {
        StorePrefix = StorePrefix,
        IncludeLaneSegment = IncludeLaneSegment,
        SequencePadding = SequencePadding,
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
    /// <param name="rasterizer">
    /// Draws the text the printer has no glyphs for. Supplied by the caller rather than built here
    /// because a font engine is a platform dependency, and this assembly is deliberately not one.
    /// Null leaves the lane on the character path, which is correct for a shop printing English.
    /// </param>
    public static IPrinterService CreatePrinter(HardwareSettings settings, ITextRasterizer? rasterizer = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var raster = rasterizer is null || settings.PrinterRasterMode == RasterMode.Never
            ? null
            : new RasterOptions(rasterizer, settings.EffectivePaperWidthDots, settings.PrinterRasterMode);

        if (!string.IsNullOrWhiteSpace(settings.PrinterOutputFile))
            return new FilePrinterService(settings.PrinterOutputFile, settings.PrinterPaperWidthChars, raster);

        if (string.IsNullOrWhiteSpace(settings.PrinterName))
            return new NoPrinterService(settings.PrinterPaperWidthChars);

        return OperatingSystem.IsWindows()
            ? new RawSpoolPrinterService(settings.PrinterName, settings.PrinterPaperWidthChars, raster)
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
            : new SerialScaleService(
                new SystemSerialPort(new SerialPortSettings(settings.ScalePort, settings.ScaleBaudRate)),
                CreateWeightReader(settings.ScaleProtocol));
    }

    public static IWeightFrameReader CreateWeightReader(ScaleProtocol protocol) => protocol switch
    {
        ScaleProtocol.Line => new LineWeightFrameReader(),
        ScaleProtocol.StxEtx => new StxEtxWeightFrameReader(),
        _ => new AutoDetectingWeightFrameReader(),
    };
}
