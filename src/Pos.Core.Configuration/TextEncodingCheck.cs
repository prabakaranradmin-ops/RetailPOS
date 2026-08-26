using System.Text;

namespace Pos.Core.Configuration;

/// <summary>
/// Spots text that was saved in the wrong encoding, and works out what it was meant to say.
/// </summary>
/// <remarks>
/// <para>
/// The failure this exists for: a settings file is written as UTF-8 with no byte-order mark, an
/// editor opens it using the machine's ANSI code page instead, and saving it back bakes the
/// misreading into the file. <c>ரவி மளிகை</c> becomes <c>à®°à®µà®¿ à®®à®³à®¿à®•à¯ˆ</c>, and every
/// byte of that is now legitimately encoded — nothing downstream can tell it was ever anything
/// else. The lane prints it on every bill.
/// </para>
/// <para>
/// It is worth catching rather than tolerating because nobody notices. The labels on the receipt
/// are compiled in and stay correct, so the bill looks right apart from the shop's own name, which
/// is the one thing on it a shopkeeper stops reading after the first day.
/// </para>
/// </remarks>
public static class TextEncodingCheck
{
    /// <summary>
    /// Returns what the text was meant to say if it looks like UTF-8 misread as Windows-1252, or
    /// null if it looks like ordinary text.
    /// </summary>
    /// <remarks>
    /// The test is the reverse of the corruption rather than a guess at what mojibake looks like:
    /// put the characters back into the code page that produced them, and see whether the bytes
    /// are valid UTF-8 for something else. Text that was never mangled fails at one step or the
    /// other, so a real name is not "repaired" into nonsense.
    /// </remarks>
    public static string? Repair(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        // Plain ASCII cannot be mojibake, and this is the overwhelmingly common case.
        var hasHighCharacters = false;

        foreach (var c in text)
        {
            if (c > 127)
            {
                hasHighCharacters = true;
                break;
            }
        }

        if (!hasHighCharacters)
            return null;

        // Every character has to exist in the code page that would have produced it. A genuine
        // Tamil or Devanagari string does not, and stops here.
        if (!TryEncodeWindows1252(text, out var bytes))
            return null;

        string repaired;

        try
        {
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            repaired = strictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        // A round trip that changed nothing means the text was fine. One that produced pure ASCII
        // means we have decoded something into less than it was, which is not a repair.
        if (repaired == text || repaired.Length == 0)
            return null;

        foreach (var c in repaired)
        {
            if (c > 127)
                return repaired;
        }

        return null;
    }

    /// <summary>
    /// The 32 characters Windows-1252 puts where Latin-1 has control codes, by their byte.
    /// </summary>
    /// <remarks>
    /// Written out rather than obtained from <see cref="Encoding.GetEncoding(int)"/>, which does
    /// not know code page 1252 on .NET without an extra package registered. Thirty-two entries is
    /// a smaller thing to carry than a dependency, and it cannot fail to be present on a lane.
    /// </remarks>
    private static readonly (char Character, byte Byte)[] Windows1252Extras =
    [
        ('€', 0x80), ('‚', 0x82), ('ƒ', 0x83), ('„', 0x84),
        ('…', 0x85), ('†', 0x86), ('‡', 0x87), ('ˆ', 0x88),
        ('‰', 0x89), ('Š', 0x8A), ('‹', 0x8B), ('Œ', 0x8C),
        ('Ž', 0x8E), ('‘', 0x91), ('’', 0x92), ('“', 0x93),
        ('”', 0x94), ('•', 0x95), ('–', 0x96), ('—', 0x97),
        ('˜', 0x98), ('™', 0x99), ('š', 0x9A), ('›', 0x9B),
        ('œ', 0x9C), ('ž', 0x9E), ('Ÿ', 0x9F),
    ];

    private static bool TryEncodeWindows1252(string text, out byte[] bytes)
    {
        var buffer = new byte[text.Length];

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            // Below 0x80 and from 0xA0 up, Windows-1252 is Latin-1 and the character is its byte.
            // The 0x80-0x9F band is the part that differs.
            if (c < 0x80 || (c >= 0xA0 && c <= 0xFF))
            {
                buffer[i] = (byte)c;
                continue;
            }

            // Five bytes in that band have no character assigned, and Windows passes them through
            // as the matching code point. Mangled Indic text is full of them — the Tamil virama is
            // E0 AF 8D — so refusing them here would miss exactly the cases this exists to catch.
            if (c is '\u0081' or '\u008D' or '\u008F' or '\u0090' or '\u009D')
            {
                buffer[i] = (byte)c;
                continue;
            }

            var mapped = false;

            foreach (var (character, value) in Windows1252Extras)
            {
                if (character != c)
                    continue;

                buffer[i] = value;
                mapped = true;
                break;
            }

            if (!mapped)
            {
                bytes = [];
                return false;
            }
        }

        bytes = buffer;
        return true;
    }

    /// <summary>
    /// Throws if <paramref name="text"/> looks like it was saved in the wrong encoding, naming the
    /// field and saying what it was probably meant to be.
    /// </summary>
    public static void ThrowIfMangled(string? text, string field, string path)
    {
        if (Repair(text) is not { } intended)
            return;

        throw new InvalidOperationException(
            $"The settings file at '{path}' is not saved as UTF-8: {field} reads '{text}', which looks like " +
            $"'{intended}' saved in the wrong encoding. Open it in Notepad, choose Save As, set Encoding to " +
            "\"UTF-8 with BOM\", and save over it. Until then the shop's own name would print as nonsense on " +
            "every bill.");
    }
}
