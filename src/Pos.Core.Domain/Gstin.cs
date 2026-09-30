namespace Pos.Core.Domain;

/// <summary>
/// Checking a GSTIN before it is saved against a supplier or a customer.
/// </summary>
/// <remarks>
/// A GSTIN is fifteen characters: the state code, the holder's PAN, an entity number, a Z, and a
/// check character worked out from the first fourteen. The check character catches a mistyped or
/// transposed character, which is how a wrong GSTIN usually arrives — and a wrong GSTIN on a
/// purchase is input tax the portal will not match.
/// </remarks>
public static class Gstin
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    /// <summary>Upper-cased and trimmed, the form it is stored and compared in.</summary>
    public static string Normalise(string gstin) => gstin.Trim().ToUpperInvariant();

    /// <summary>Why this is not a GSTIN, or null when it is one.</summary>
    public static string? Problem(string? gstin)
    {
        if (string.IsNullOrWhiteSpace(gstin))
            return "A GSTIN has fifteen characters.";

        var value = Normalise(gstin);

        if (value.Length != 15)
            return $"A GSTIN has fifteen characters; '{value}' has {value.Length}.";

        if (value.Any(c => Alphabet.IndexOf(c) < 0))
            return "A GSTIN is letters and digits only.";

        if (GstStates.Name(value[..2]) is null)
            return $"'{value[..2]}' is not a GST state code.";

        // The PAN inside it: five letters, four digits, a letter.
        var pan = value[2..12];

        if (!pan[..5].All(char.IsLetter) || !pan[5..9].All(char.IsDigit) || !char.IsLetter(pan[9]))
            return $"'{pan}' in the middle is not the shape of a PAN.";

        if (CheckCharacter(value[..14]) != value[14])
            return "The last character does not match the rest - a character is mistyped or two are swapped.";

        return null;
    }

    public static bool IsValid(string? gstin) => Problem(gstin) is null;

    /// <summary>The state a GSTIN was issued in: its first two characters.</summary>
    public static string StateCode(string gstin) => Normalise(gstin)[..2];

    /// <summary>
    /// The check character for the first fourteen: each character's value doubled at every second
    /// position, the base-36 digits of each product summed, and the result taken from 36.
    /// </summary>
    public static char CheckCharacter(string first14)
    {
        var sum = 0;

        for (var i = 0; i < 14; i++)
        {
            var product = Alphabet.IndexOf(first14[i]) * (i % 2 == 0 ? 1 : 2);
            sum += (product / 36) + (product % 36);
        }

        return Alphabet[(36 - (sum % 36)) % 36];
    }
}
