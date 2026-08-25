using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pos.App;

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

    public TimeSpan SearchDebounce => TimeSpan.FromMilliseconds(SearchDebounceMs);

    public TimeSpan ScannerMaxKeystrokeGap => TimeSpan.FromMilliseconds(ScannerMaxKeystrokeGapMs);

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

        return settings;
    }

    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
}
