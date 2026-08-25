namespace Pos.Core.Hardware.Scanning;

public enum Symbology
{
    /// <summary>Not a length or character set this recognises. Still usable, just unverifiable.</summary>
    Unknown = 0,

    Ean13 = 1,
    Ean8 = 2,
    UpcA = 3,
}

/// <summary>
/// Recognises the numeric retail symbologies and checks their check digits.
/// </summary>
/// <remarks>
/// Every one of these carries a check digit computed from the others, so a misread is detectable
/// rather than merely unlikely. Scanners do verify it themselves, but a barcode can also reach the
/// till by being typed in by hand from a smudged label — and that is the path where a transposed
/// pair of digits happily matches a completely different product. Checking costs a few
/// microseconds and turns a wrong item on the bill into a message asking the cashier to try again.
/// <para>
/// A code that is not one of these lengths is reported as <see cref="Symbology.Unknown"/> and
/// treated as valid. Stores use internal codes and other symbologies with no check digit, and
/// refusing them would be worse than not verifying them.
/// </para>
/// </remarks>
public static class Barcode
{
    public static Symbology Identify(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return Symbology.Unknown;

        var digits = code.Trim();

        if (!IsAllDigits(digits))
            return Symbology.Unknown;

        return digits.Length switch
        {
            13 => Symbology.Ean13,
            12 => Symbology.UpcA,
            8 => Symbology.Ean8,
            _ => Symbology.Unknown,
        };
    }

    /// <summary>
    /// True when the code's check digit agrees with the rest of it, or when the symbology has no
    /// check digit to test.
    /// </summary>
    public static bool IsValid(string? code)
    {
        var symbology = Identify(code);

        if (symbology == Symbology.Unknown)
            return !string.IsNullOrWhiteSpace(code);

        var digits = code!.Trim();

        return CheckDigit(digits[..^1]) == digits[^1] - '0';
    }

    /// <summary>
    /// The check digit for a body of digits, by the modulo-10 rule EAN and UPC share: weight the
    /// digits 3 and 1 alternately from the right, then take what is needed to reach a multiple of
    /// ten.
    /// </summary>
    public static int CheckDigit(string body)
    {
        ArgumentException.ThrowIfNullOrEmpty(body);

        if (!IsAllDigits(body))
            throw new ArgumentException("A barcode body is digits only.", nameof(body));

        var sum = 0;
        var weight = 3;

        for (var i = body.Length - 1; i >= 0; i--)
        {
            sum += (body[i] - '0') * weight;
            weight = weight == 3 ? 1 : 3;
        }

        return (10 - (sum % 10)) % 10;
    }

    /// <summary>Appends the correct check digit, for generating test data and internal labels.</summary>
    public static string WithCheckDigit(string body) => body + CheckDigit(body);

    private static bool IsAllDigits(string text)
    {
        foreach (var character in text)
        {
            if (character is < '0' or > '9')
                return false;
        }

        return true;
    }
}
