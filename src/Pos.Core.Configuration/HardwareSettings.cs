using System.Text.Json.Serialization;
using Pos.Core.Hardware.Printing;

namespace Pos.Core.Configuration;

public enum ScaleProtocol
{
    /// <summary>Try the known protocols and use whichever the scale answers in.</summary>
    Auto = 0,

    /// <summary>Comma-separated and CR/LF terminated: <c>ST,GS,+  1.234kg</c>. Essae, Contech.</summary>
    Line = 1,

    /// <summary>STX-framed with a block check character. Toledo, CAS.</summary>
    StxEtx = 2,
}

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

    /// <summary>
    /// Print head width in dots: 576 for 80mm at the usual 203dpi, 384 for 58mm. Zero derives it
    /// from <see cref="PrinterPaperWidthChars"/>, which is right for every printer that has not
    /// been reconfigured to an unusual font.
    /// </summary>
    [JsonPropertyName("printerPaperWidthDots")]
    public int PrinterPaperWidthDots { get; set; }

    /// <summary>
    /// When to draw text as dots instead of sending it as characters. <c>Auto</c> draws only the
    /// lines a printer has no glyphs for, which is what puts Tamil on paper while leaving the
    /// English crisp and cheap.
    /// </summary>
    [JsonPropertyName("printerRasterMode")]
    public RasterMode PrinterRasterMode { get; set; } = RasterMode.Auto;

    /// <summary>
    /// Font used for drawn text. Empty picks the best installed of Nirmala UI, Latha, Arial
    /// Unicode MS and Segoe UI — the first of those carries Tamil and Latin in one face.
    /// </summary>
    [JsonPropertyName("receiptFontFamily")]
    public string? ReceiptFontFamily { get; set; }

    /// <summary>Em size in printer dots for drawn text. Zero uses the default, which is sized to match the printer's own font.</summary>
    [JsonPropertyName("receiptFontSizeDots")]
    public double ReceiptFontSizeDots { get; set; }

    /// <summary>The dot width to lay drawn text out against, derived when not stated outright.</summary>
    public int EffectivePaperWidthDots =>
        PrinterPaperWidthDots > 0 ? PrinterPaperWidthDots : RasterOptions.DotsForCharacterWidth(PrinterPaperWidthChars);

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

    /// <summary>
    /// Which protocol the scale speaks. "Auto" tries the ones in the field and latches onto
    /// whichever answers, which is usually easier than finding the setting in a service menu.
    /// </summary>
    [JsonPropertyName("scaleProtocol")]
    public ScaleProtocol ScaleProtocol { get; set; } = ScaleProtocol.Auto;

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
