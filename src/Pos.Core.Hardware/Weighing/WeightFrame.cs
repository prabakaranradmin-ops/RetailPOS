using System.Globalization;

namespace Pos.Core.Hardware.Weighing;

public enum WeightStability
{
    /// <summary>The reading has settled. Only a settled reading may be priced.</summary>
    Stable = 0,

    /// <summary>Still moving — goods being placed, or a hand still on the pan.</summary>
    Unstable = 1,

    /// <summary>Over the scale's capacity. The number that came with it means nothing.</summary>
    Overload = 2,
}

public enum WeightMode
{
    /// <summary>Everything on the pan, container included.</summary>
    Gross = 0,

    /// <summary>Contents only, with a tare already subtracted by the scale.</summary>
    Net = 1,
}

/// <summary>One reading, as the scale reported it.</summary>
/// <param name="Stability">Whether the number can be trusted yet.</param>
/// <param name="Mode">Whether the scale had already taken off a tare.</param>
/// <param name="Kilograms">The weight, normalised to kilograms whatever unit came over the wire.</param>
public readonly record struct WeightFrame(WeightStability Stability, WeightMode Mode, decimal Kilograms)
{
    public bool IsUsable => Stability == WeightStability.Stable && Kilograms > 0m;
}

/// <summary>
/// Reads the continuous ASCII frames a retail scale streams down its serial line.
/// </summary>
/// <remarks>
/// The frame is the format used by the common Indian counter scales: a status field, a mode field,
/// a signed weight and a unit, comma separated and terminated by CR LF —
/// <c>ST,GS,+  1.234kg</c>. Some models append an XOR checksum as two hex digits
/// (<c>ST,GS,+  1.234kg,3F</c>); the parser validates it when it is there and accepts frames
/// without it, because both are in the field.
/// <para>
/// Everything here is strict on purpose. A scale streams several readings a second, so throwing
/// away a frame that does not parse cleanly costs nothing — the next one arrives in a fraction of
/// a second — while guessing at a malformed frame prices somebody's groceries wrong.
/// </para>
/// </remarks>
public static class WeightFrameParser
{
    public static bool TryParse(string? frame, out WeightFrame reading)
    {
        reading = default;

        if (string.IsNullOrWhiteSpace(frame))
            return false;

        var text = frame.Trim();

        if (!TryStripChecksum(ref text))
            return false;

        var fields = text.Split(',');

        if (fields.Length != 3)
            return false;

        if (!TryParseStability(fields[0].Trim(), out var stability))
            return false;

        if (!TryParseMode(fields[1].Trim(), out var mode))
            return false;

        if (!TryParseWeight(fields[2].Trim(), out var kilograms))
            return false;

        reading = new WeightFrame(stability, mode, kilograms);
        return true;
    }

    /// <summary>
    /// Removes and verifies a trailing checksum if the frame carries one.
    /// </summary>
    /// <returns>False when a checksum is present but does not match the payload.</returns>
    private static bool TryStripChecksum(ref string text)
    {
        var lastComma = text.LastIndexOf(',');

        if (lastComma < 0)
            return true;

        var tail = text[(lastComma + 1)..];

        // A two-hex-digit tail is a checksum; anything else is the weight field of a frame that
        // simply has no checksum, so it is left alone.
        if (tail.Length != 2 || !byte.TryParse(tail, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var declared))
            return true;

        var payload = text[..lastComma];

        if (Checksum(payload) != declared)
            return false;

        text = payload;
        return true;
    }

    /// <summary>XOR of every payload byte, which is the scheme these scales use.</summary>
    public static byte Checksum(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        byte checksum = 0;

        foreach (var character in payload)
            checksum ^= (byte)character;

        return checksum;
    }

    /// <summary>Builds a frame with a valid checksum, for the fake scale and the tests.</summary>
    public static string Format(WeightFrame reading, string unit = "kg", bool withChecksum = false)
    {
        var status = reading.Stability switch
        {
            WeightStability.Stable => "ST",
            WeightStability.Unstable => "US",
            _ => "OL",
        };

        var mode = reading.Mode == WeightMode.Net ? "NT" : "GS";
        var scaled = unit.Equals("g", StringComparison.OrdinalIgnoreCase) ? reading.Kilograms * 1000m : reading.Kilograms;
        var sign = scaled < 0m ? "-" : "+";
        var payload = $"{status},{mode},{sign}{Math.Abs(scaled).ToString("0.000", CultureInfo.InvariantCulture)}{unit}";

        return withChecksum ? $"{payload},{Checksum(payload):X2}" : payload;
    }

    private static bool TryParseStability(string field, out WeightStability stability)
    {
        stability = field.ToUpperInvariant() switch
        {
            "ST" => WeightStability.Stable,
            "US" => WeightStability.Unstable,
            "OL" => WeightStability.Overload,
            _ => (WeightStability)(-1),
        };

        return Enum.IsDefined(stability);
    }

    private static bool TryParseMode(string field, out WeightMode mode)
    {
        mode = field.ToUpperInvariant() switch
        {
            "GS" => WeightMode.Gross,
            "NT" => WeightMode.Net,
            _ => (WeightMode)(-1),
        };

        return Enum.IsDefined(mode);
    }

    /// <summary>
    /// Parses the signed magnitude and its unit, normalising to kilograms. The scale pads the
    /// number with spaces to a fixed width, so those are stripped rather than treated as a
    /// separator.
    /// </summary>
    private static bool TryParseWeight(string field, out decimal kilograms)
    {
        kilograms = 0m;

        var compact = field.Replace(" ", string.Empty, StringComparison.Ordinal);

        if (compact.Length == 0)
            return false;

        var multiplier = 1m;

        if (compact.EndsWith("kg", StringComparison.OrdinalIgnoreCase))
        {
            compact = compact[..^2];
        }
        else if (compact.EndsWith("lb", StringComparison.OrdinalIgnoreCase))
        {
            compact = compact[..^2];
            multiplier = 0.45359237m;
        }
        else if (compact.EndsWith("g", StringComparison.OrdinalIgnoreCase))
        {
            compact = compact[..^1];
            multiplier = 0.001m;
        }
        else
        {
            // A unitless frame is kilograms by convention on these scales.
        }

        if (compact.Length == 0)
            return false;

        var negative = compact[0] == '-';

        if (compact[0] is '+' or '-')
            compact = compact[1..];

        if (!decimal.TryParse(compact, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var magnitude))
            return false;

        kilograms = (negative ? -magnitude : magnitude) * multiplier;
        return true;
    }
}
