using System.Text.Json.Serialization;

namespace Pos.Core.Configuration;

public enum DrawerConnection
{
    /// <summary>No drawer on this lane.</summary>
    None = 0,

    /// <summary>Wired into the receipt printer's RJ11 port. How nearly every counter is built.</summary>
    Printer = 1,

    /// <summary>On its own serial line.</summary>
    Serial = 2,
}

/// <summary>
/// Which peripherals this lane has and how they are attached. Every field is optional; a lane with
/// none of them set still bills, it just cannot print, kick or weigh.
/// </summary>
public sealed class HardwareSettings
{
    /// <summary>
    /// Windows printer name for the receipt printer. Empty means the lane has no printer.
    /// </summary>
    [JsonPropertyName("printerName")]
    public string? PrinterName { get; set; }

    /// <summary>Characters per line: 48 for 80mm paper, 32 for 58mm.</summary>
    [JsonPropertyName("printerPaperWidthChars")]
    public int PrinterPaperWidthChars { get; set; } = 48;

    /// <summary>
    /// Writes receipt jobs to this file instead of a printer. For a lane being set up before its
    /// printer arrives, and for capturing the byte stream when a printer misbehaves.
    /// </summary>
    [JsonPropertyName("printerOutputFile")]
    public string? PrinterOutputFile { get; set; }

    [JsonPropertyName("drawerConnection")]
    public DrawerConnection DrawerConnection { get; set; } = DrawerConnection.Printer;

    /// <summary>Serial port for the drawer, when it is not on the printer.</summary>
    [JsonPropertyName("drawerPort")]
    public string? DrawerPort { get; set; }

    /// <summary>0 for RJ11 pin 2, 1 for pin 5. A single drawer is almost always on pin 2.</summary>
    [JsonPropertyName("drawerPin")]
    public int DrawerPin { get; set; }

    [JsonPropertyName("drawerPulseOnMs")]
    public int DrawerPulseOnMs { get; set; } = 60;

    [JsonPropertyName("drawerPulseOffMs")]
    public int DrawerPulseOffMs { get; set; } = 120;

    /// <summary>
    /// Serial port for the scanner. Left empty for the usual case, where the scanner presents as a
    /// keyboard and the UI recognises its bursts by timing instead.
    /// </summary>
    [JsonPropertyName("scannerPort")]
    public string? ScannerPort { get; set; }

    [JsonPropertyName("scannerBaudRate")]
    public int ScannerBaudRate { get; set; } = 9600;

    /// <summary>Serial port for the counter scale. Empty means the lane has no scale.</summary>
    [JsonPropertyName("scalePort")]
    public string? ScalePort { get; set; }

    [JsonPropertyName("scaleBaudRate")]
    public int ScaleBaudRate { get; set; } = 9600;

    public void Validate()
    {
        if (PrinterPaperWidthChars < 16)
            throw new ArgumentOutOfRangeException(nameof(PrinterPaperWidthChars), PrinterPaperWidthChars, "Paper narrower than 16 characters cannot hold a line and a price.");

        if (!Enum.IsDefined(DrawerConnection))
            throw new ArgumentOutOfRangeException(nameof(DrawerConnection), DrawerConnection, "Unknown drawer connection.");

        if (DrawerConnection == DrawerConnection.Serial && string.IsNullOrWhiteSpace(DrawerPort))
            throw new ArgumentException("A serial drawer needs a port.", nameof(DrawerPort));

        if (DrawerPin is not (0 or 1))
            throw new ArgumentOutOfRangeException(nameof(DrawerPin), DrawerPin, "The drawer pin is 0 (pin 2) or 1 (pin 5).");

        if (DrawerPulseOnMs is < 0 or > 510 || DrawerPulseOffMs is < 0 or > 510)
            throw new ArgumentOutOfRangeException(nameof(DrawerPulseOnMs), "A drawer pulse is between 0 and 510ms.");
    }
}
