using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pos.Core.Domain.Printing;
using Pos.Core.Loyalty;

namespace Pos.Core.Configuration;

/// <summary>
/// Per-installation settings. These are properties of the lane and the outlet, not of the build,
/// so they live in a file next to the database rather than in code.
/// </summary>
public sealed class PosSettings
{
    /// <summary>
    /// Identifies this till. It is baked into every invoice number, which is what lets several
    /// lanes generate numbers independently with nothing coordinating them (ARCHITECTURE.md 6).
    /// </summary>
    [JsonPropertyName("laneId")]
    public string LaneId { get; set; } = "L1";

    /// <summary>
    /// GST state code of the outlet. Compared against the customer's to choose CGST/SGST or IGST.
    /// </summary>
    [JsonPropertyName("outletStateCode")]
    public string OutletStateCode { get; set; } = "33";

    /// <summary>Debounce window for typed search, in milliseconds (SRS 2.1).</summary>
    [JsonPropertyName("searchDebounceMs")]
    public int SearchDebounceMs { get; set; } = 150;

    /// <summary>
    /// Largest gap between keystrokes still counted as one scanner burst, in milliseconds. Depends
    /// on the scanner's polling behaviour, so it is tunable per site (ARCHITECTURE.md 4).
    /// </summary>
    [JsonPropertyName("scannerMaxKeystrokeGapMs")]
    public int ScannerMaxKeystrokeGapMs { get; set; } = 30;

    /// <summary>Most of a bill that may be settled with points, as a percentage (SRS section 4).</summary>
    [JsonPropertyName("loyaltyRedemptionCapPercent")]
    public decimal LoyaltyRedemptionCapPercent { get; set; } = 30m;

    /// <summary>What one point is worth when redeemed.</summary>
    [JsonPropertyName("loyaltyRupeesPerPoint")]
    public decimal LoyaltyRupeesPerPoint { get; set; } = 0.50m;

    /// <summary>Spend needed to earn one point, on the net bill after any redemption.</summary>
    [JsonPropertyName("loyaltyRupeesPerPointEarned")]
    public decimal LoyaltyRupeesPerPointEarned { get; set; } = 50m;

    /// <summary>
    /// Who is on this till by default. Recorded against every sale so a Z-report can attribute
    /// takings, and a drawer difference can be traced to a shift.
    /// </summary>
    /// <remarks>
    /// Deliberately a name in a file rather than a login. A pilot lane with one operator should not
    /// have to sign in, and a shared shop password is worse than no password at all — it looks like
    /// access control and attributes nothing. Where shifts change, the cashier sets their name at
    /// the till and it lasts until somebody changes it again.
    /// </remarks>
    [JsonPropertyName("defaultCashierName")]
    public string? DefaultCashierName { get; set; }

    /// <summary>What goes at the top of the receipt.</summary>
    [JsonPropertyName("store")]
    public StoreProfileSettings Store { get; set; } = new();

    /// <summary>How this lane composes its invoice numbers.</summary>
    [JsonPropertyName("invoiceNumber")]
    public InvoiceNumberSettings InvoiceNumber { get; set; } = new();

    /// <summary>
    /// Which language the receipt's labels print in. The figures, the item names and the invoice
    /// number are unaffected — only the words around them change.
    /// </summary>
    /// <remarks>
    /// Defaults to English because that prints on any thermal printer with no rasterising at all.
    /// A lane set to Tamil draws those labels as dots instead, which needs
    /// <c>hardware.printerRasterMode</c> left at its default and a Tamil-capable font on the
    /// machine — every Windows build since 8 has one.
    /// </remarks>
    [JsonPropertyName("receiptLanguage")]
    public ReceiptLanguage ReceiptLanguage { get; set; } = ReceiptLanguage.English;

    /// <summary>Which peripherals this lane has, and how they are attached.</summary>
    [JsonPropertyName("hardware")]
    public HardwareSettings Hardware { get; set; } = new();

    /// <summary>
    /// Who may see what. Empty by default: a one-owner shop should not have to unlock its own
    /// figures, and a lock nobody asked for is a lock somebody writes on a sticky note.
    /// </summary>
    [JsonPropertyName("security")]
    public SecuritySettings Security { get; set; } = new();

    public TimeSpan SearchDebounce => TimeSpan.FromMilliseconds(SearchDebounceMs);

    public TimeSpan ScannerMaxKeystrokeGap => TimeSpan.FromMilliseconds(ScannerMaxKeystrokeGapMs);

    public LoyaltyRules LoyaltyRules =>
        new(LoyaltyRedemptionCapPercent, LoyaltyRupeesPerPoint, LoyaltyRupeesPerPointEarned);

    /// <summary>
    /// Reads the settings file, falling back to defaults when it is absent. A malformed file is an
    /// error rather than a silent fallback: running a lane under the wrong lane id would mint
    /// invoice numbers that collide with another till's.
    /// </summary>
    public static PosSettings LoadOrDefault(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
            return new PosSettings();

        PosSettings? settings;

        try
        {
            settings = JsonSerializer.Deserialize<PosSettings>(File.ReadAllText(path), Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"The settings file at '{path}' is not valid JSON: {ex.Message}", ex);
        }

        if (settings is null)
            return new PosSettings();

        if (string.IsNullOrWhiteSpace(settings.LaneId))
            throw new InvalidOperationException($"The settings file at '{path}' has an empty lane id.");

        if (string.IsNullOrWhiteSpace(settings.OutletStateCode))
            throw new InvalidOperationException($"The settings file at '{path}' has an empty outlet state code.");

        if (settings.SearchDebounceMs < 0 || settings.ScannerMaxKeystrokeGapMs <= 0)
            throw new InvalidOperationException($"The settings file at '{path}' has a non-positive timing value.");

        // Surfaces an unworkable loyalty scheme here rather than at the moment a cashier tries to
        // redeem against it.
        try
        {
            settings.LoyaltyRules.Validate();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new InvalidOperationException($"The settings file at '{path}' has an unworkable loyalty scheme: {ex.Message}", ex);
        }

        // Text that was saved in the wrong encoding reads as valid JSON and valid UTF-8, so nothing
        // else here can catch it. It has to be caught before the lane opens, because what it
        // corrupts is the store's own identity on every invoice it issues.
        TextEncodingCheck.ThrowIfMangled(settings.Store.Name, "the store name", path);
        TextEncodingCheck.ThrowIfMangled(settings.Store.AddressLine1, "the first address line", path);
        TextEncodingCheck.ThrowIfMangled(settings.Store.AddressLine2, "the second address line", path);
        TextEncodingCheck.ThrowIfMangled(settings.Store.FooterMessage, "the footer message", path);

        // A template that was copied but not finished. This is checked before the prefix below,
        // because "CHANGEME" is a structurally valid prefix and would otherwise sail through.
        PlaceholderCheck.ThrowIfAnyRemain(settings, path);

        // An unusable invoice prefix has to stop the lane starting. Discovering it at the first
        // sale would mean the shop's first bill of the day carries a number nobody can file.
        try
        {
            settings.InvoiceNumber.ToFormat().Validate();
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            throw new InvalidOperationException($"The settings file at '{path}' has an unusable invoice number format: {ex.Message}", ex);
        }

        // Likewise for the peripherals. A lane told to kick a drawer on a serial port it does not
        // name should say so at startup, not the first time a cashier takes cash.
        try
        {
            settings.Hardware.Validate();
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"The settings file at '{path}' has an unworkable hardware setup: {ex.Message}", ex);
        }

        return settings;
    }

    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // With the byte-order mark, not without. A shopkeeper edits this file in Notepad, and
        // Notepad decides how to read a file it is given: with the mark it reads UTF-8 and saves
        // UTF-8, and a Tamil shop name survives being edited. Without it, older builds fall back to
        // the machine's ANSI code page, show the name as mojibake, and save that back — which is
        // how a shop ends up printing 'à®°à®µà®¿' where its own name should be.
        File.WriteAllText(path, JsonSerializer.Serialize(this, Options), Utf8WithBom);
    }

    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,

        // Enums read and write as their names. A shopkeeper editing this file should see
        // "drawerConnection": "Printer", not a 1 they have to look up.
        Converters = { new JsonStringEnumConverter(allowIntegerValues: true) },
    };
}
