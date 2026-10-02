using System.Text;
using System.Text.RegularExpressions;

namespace Pos.Core.Domain.Catalogue;

/// <summary>
/// How a name sounds, written so that the ways a Tamil word is spelled in English - and the word in
/// Tamil script itself - come out the same.
/// </summary>
/// <remarks>
/// <para>
/// A cashier asked for "paruppu" types paruppu, or baruppu, or பருப்பு, and the catalogue may say
/// any of them, or "Toor Dal" with the Tamil name beside it. Tamil writes one letter where English
/// spellings choose between two - க is k or g, த is t, th, d or dh, ச is s, ch or j - and doubles
/// letters English spellings often do not. So both the name and what is typed are folded the same
/// way, and compared folded:
/// </para>
/// <list type="bullet">
///   <item>Tamil script is written out in Latin letters first.</item>
///   <item>zh is l, ch, sh and j are s, and an h after a consonant goes: th is t, dh is d.</item>
///   <item>g is k, d is t, b is p, w is v, f is p, z is s, c is k.</item>
///   <item>ee is i and oo is u, as English spellings use them; doubled letters are single.</item>
///   <item>A word ending -ey, -ei or -ay ends -ai; one starting ye- starts e-.</item>
/// </list>
/// <para>
/// It is a key for matching, never shown. It errs towards finding too much rather than too little:
/// the till lists what matched last, after every exact match, and the cashier picks.
/// </para>
/// </remarks>
public static partial class SoundKey
{
    /// <summary>Shorter than this, a folded word matches too much of the catalogue to be worth asking.</summary>
    public const int MinLength = 3;

    /// <summary>The folded form of a name or of what was typed. Empty for nothing.</summary>
    public static string Of(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var latin = Transliterate(text.Normalize(NormalizationForm.FormC)).ToLowerInvariant();
        var words = NotLetterOrDigit().Replace(latin, " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return string.Join(' ', words.Select(Fold).Where(w => w.Length > 0));
    }

    private static string Fold(string word)
    {
        var folded = word
            .Replace("zh", "l", StringComparison.Ordinal)
            .Replace("ch", "s", StringComparison.Ordinal)
            .Replace("sh", "s", StringComparison.Ordinal);

        folded = AspiratedH().Replace(folded, "$1");

        folded = folded
            .Replace("ee", "i", StringComparison.Ordinal)
            .Replace("ii", "i", StringComparison.Ordinal)
            .Replace("oo", "u", StringComparison.Ordinal)
            .Replace("uu", "u", StringComparison.Ordinal);

        var letters = new StringBuilder(folded.Length + 2);

        foreach (var c in folded)
        {
            letters.Append(c switch
            {
                'g' => "k",
                'd' => "t",
                'b' => "p",
                'j' => "s",
                'z' => "s",
                'w' => "v",
                'c' => "k",
                'q' => "k",
                'f' => "p",
                'x' => "ks",
                _ => c.ToString(),
            });
        }

        folded = Doubled().Replace(letters.ToString(), "$1");

        if (folded.Length > 2 && folded.StartsWith("ye", StringComparison.Ordinal))
            folded = folded[1..];

        if (folded.Length > 2 && (folded.EndsWith("ey", StringComparison.Ordinal)
                                  || folded.EndsWith("ei", StringComparison.Ordinal)
                                  || folded.EndsWith("ay", StringComparison.Ordinal)))
            folded = folded[..^2] + "ai";

        return folded;
    }

    // ---- Tamil script into Latin letters ------------------------------------------------------

    private const char Pulli = '்';
    private const char Aytham = 'ஃ';

    /// <summary>
    /// The vowels standing alone. The long e and o are written single: an English spelling's ee and
    /// oo are i and u, and the two must not be confused when folded.
    /// </summary>
    private static readonly Dictionary<char, string> Vowels = new()
    {
        ['அ'] = "a", ['ஆ'] = "aa", ['இ'] = "i", ['ஈ'] = "ii",
        ['உ'] = "u", ['ஊ'] = "uu", ['எ'] = "e", ['ஏ'] = "e",
        ['ஐ'] = "ai", ['ஒ'] = "o", ['ஓ'] = "o", ['ஔ'] = "au",
    };

    /// <summary>The vowel signs on a consonant, written as the vowels are.</summary>
    private static readonly Dictionary<char, string> Signs = new()
    {
        ['ா'] = "aa", ['ி'] = "i", ['ீ'] = "ii", ['ு'] = "u",
        ['ூ'] = "uu", ['ெ'] = "e", ['ே'] = "e", ['ை'] = "ai",
        ['ொ'] = "o", ['ோ'] = "o", ['ௌ'] = "au",
    };

    /// <summary>The consonants, each with the a it carries unless a sign or the pulli says otherwise.</summary>
    private static readonly Dictionary<char, string> Consonants = new()
    {
        ['க'] = "k", ['ங'] = "ng", ['ச'] = "s", ['ஜ'] = "j", ['ஞ'] = "nj",
        ['ட'] = "t", ['ண'] = "n", ['த'] = "th", ['ந'] = "n", ['ன'] = "n",
        ['ப'] = "p", ['ம'] = "m", ['ய'] = "y", ['ர'] = "r", ['ற'] = "r",
        ['ல'] = "l", ['ள'] = "l", ['ழ'] = "zh", ['வ'] = "v", ['ஶ'] = "sh",
        ['ஷ'] = "sh", ['ஸ'] = "s", ['ஹ'] = "h",
    };

    private static bool IsTamil(char c) => c is >= '஀' and <= '௿';

    private static string Transliterate(string text)
    {
        if (!text.Any(IsTamil))
            return text;

        var latin = new StringBuilder(text.Length * 2);

        // A consonant just written, still waiting to hear whether it carries its a.
        var waiting = false;

        foreach (var c in text)
        {
            if (Consonants.TryGetValue(c, out var consonant))
            {
                if (waiting)
                    latin.Append('a');

                latin.Append(consonant);
                waiting = true;
                continue;
            }

            if (Signs.TryGetValue(c, out var sign))
            {
                latin.Append(sign);
                waiting = false;
                continue;
            }

            if (c == Pulli)
            {
                waiting = false;
                continue;
            }

            if (waiting)
            {
                latin.Append('a');
                waiting = false;
            }

            if (Vowels.TryGetValue(c, out var vowel))
                latin.Append(vowel);
            else if (c == Aytham)
                latin.Append('h');
            else if (c is >= '௦' and <= '௯')
                latin.Append((char)('0' + (c - '௦')));
            else if (!IsTamil(c))
                latin.Append(c);
        }

        if (waiting)
            latin.Append('a');

        return latin.ToString();
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotLetterOrDigit();

    [GeneratedRegex("([bcdfgjkpqrstvxz])h")]
    private static partial Regex AspiratedH();

    [GeneratedRegex(@"(.)\1+")]
    private static partial Regex Doubled();
}
