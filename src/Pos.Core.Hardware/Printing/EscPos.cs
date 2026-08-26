using System.Globalization;
using System.Text;

namespace Pos.Core.Hardware.Printing;

public enum TextAlignment
{
    Left = 0,
    Center = 1,
    Right = 2,
}

public enum CutMode
{
    /// <summary>Severs the paper completely.</summary>
    Full = 0,

    /// <summary>Leaves a small tab so the receipt stays attached until torn off.</summary>
    Partial = 1,
}

/// <summary>
/// ESC/POS command bytes. The command set is a published, vendor-neutral standard implemented by
/// essentially every thermal receipt printer, which is why it is written directly rather than
/// going through a driver.
/// </summary>
/// <remarks>
/// Every method here returns bytes and touches no hardware, so the whole command layer can be
/// asserted byte for byte without a printer attached — which is what the Phase 3 unit tests do.
/// </remarks>
public static class EscPos
{
    public const byte Esc = 0x1B;
    public const byte Gs = 0x1D;
    public const byte Lf = 0x0A;

    /// <summary>ESC @ — resets the printer: clears formatting, empties the buffer.</summary>
    public static byte[] Initialize() => [Esc, (byte)'@'];

    /// <summary>LF — prints the buffer and advances one line.</summary>
    public static byte[] LineFeed() => [Lf];

    /// <summary>ESC d n — advances n lines.</summary>
    public static byte[] Feed(int lines)
    {
        if (lines is < 0 or > 255)
            throw new ArgumentOutOfRangeException(nameof(lines), lines, "A feed is between 0 and 255 lines.");

        return [Esc, (byte)'d', (byte)lines];
    }

    /// <summary>ESC a n — sets justification for the lines that follow.</summary>
    public static byte[] Align(TextAlignment alignment)
    {
        if (!Enum.IsDefined(alignment))
            throw new ArgumentOutOfRangeException(nameof(alignment), alignment, "Unknown alignment.");

        return [Esc, (byte)'a', (byte)alignment];
    }

    /// <summary>ESC E n — emphasised (bold) on or off.</summary>
    public static byte[] Bold(bool on) => [Esc, (byte)'E', on ? (byte)1 : (byte)0];

    /// <summary>ESC - n — underline off, one dot, or two dots.</summary>
    public static byte[] Underline(int dots)
    {
        if (dots is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(dots), dots, "Underline is 0, 1 or 2 dots.");

        return [Esc, (byte)'-', (byte)dots];
    }

    /// <summary>
    /// GS ! n — character size. The low nibble is the height multiplier and the high nibble the
    /// width, each 1 to 8.
    /// </summary>
    public static byte[] TextSize(int widthMultiplier, int heightMultiplier)
    {
        if (widthMultiplier is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(widthMultiplier), widthMultiplier, "Width multiplier is 1 to 8.");

        if (heightMultiplier is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(heightMultiplier), heightMultiplier, "Height multiplier is 1 to 8.");

        var n = (byte)(((widthMultiplier - 1) << 4) | (heightMultiplier - 1));
        return [Gs, (byte)'!', n];
    }

    /// <summary>Back to single width and height.</summary>
    public static byte[] NormalTextSize() => TextSize(1, 1);

    /// <summary>ESC t n — selects the character code table the printer maps bytes through.</summary>
    public static byte[] SelectCodePage(byte codePage) => [Esc, (byte)'t', codePage];

    /// <summary>
    /// GS V m — cuts the paper. The feed before the cut is deliberate: the blade sits above the
    /// print head, so without it the last few lines are still inside the printer when it cuts.
    /// </summary>
    public static byte[] Cut(CutMode mode = CutMode.Partial, int feedBeforeCut = 4)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown cut mode.");

        var command = new List<byte>(8);

        if (feedBeforeCut > 0)
            command.AddRange(Feed(feedBeforeCut));

        // m = 0 full, 1 partial.
        command.AddRange([Gs, (byte)'V', mode == CutMode.Full ? (byte)0 : (byte)1]);

        return [.. command];
    }

    /// <summary>
    /// ESC p m t1 t2 — pulses a drawer connected to the printer's RJ11 port.
    /// </summary>
    /// <param name="pin">
    /// Which connector pin to pulse: 0 for pin 2, 1 for pin 5. Two-drawer setups use both; a single
    /// drawer is almost always on pin 2.
    /// </param>
    /// <param name="onMilliseconds">How long the solenoid is energised.</param>
    /// <param name="offMilliseconds">Rest before the printer will accept another pulse.</param>
    /// <remarks>
    /// The on and off times are sent in units of 2ms, which is what limits them to roughly half a
    /// second. Holding the solenoid much longer than 100ms does not help the drawer open and does
    /// risk cooking the coil, so the range is capped rather than passed through blindly.
    /// </remarks>
    public static byte[] KickDrawer(int pin = 0, int onMilliseconds = 60, int offMilliseconds = 120)
    {
        if (pin is not (0 or 1))
            throw new ArgumentOutOfRangeException(nameof(pin), pin, "The drawer pin is 0 (pin 2) or 1 (pin 5).");

        return [Esc, (byte)'p', (byte)pin, ToPulseUnits(onMilliseconds, nameof(onMilliseconds)), ToPulseUnits(offMilliseconds, nameof(offMilliseconds))];
    }

    private static byte ToPulseUnits(int milliseconds, string parameterName)
    {
        if (milliseconds is < 0 or > 510)
            throw new ArgumentOutOfRangeException(parameterName, milliseconds, "A drawer pulse is between 0 and 510ms.");

        return (byte)(milliseconds / 2);
    }

    /// <summary>
    /// Tallest band sent in one raster command. The parameter itself allows 65,535 rows, but
    /// printers buffer a band before printing it and a firmware given a very tall one commonly
    /// prints garbage or drops it. Every printer handles a band of this height.
    /// </summary>
    public const int MaxRasterBandHeight = 255;

    /// <summary>
    /// GS v 0 — prints a raster bitmap. This is how anything the printer has no font for gets onto
    /// paper: the characters are drawn here and sent as dots, so the printer's own code pages and
    /// glyph set stop mattering entirely.
    /// </summary>
    /// <remarks>
    /// A tall image is split into bands of <see cref="MaxRasterBandHeight"/> rows and sent as
    /// consecutive commands, which prints identically — the head simply prints each band as it
    /// arrives — and keeps every band inside what the smallest printer buffer will take.
    /// </remarks>
    public static byte[] RasterImage(MonochromeBitmap image)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (image.BytesPerRow > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(image), image.Width, "The image is wider than the raster command can describe.");

        var bytes = new List<byte>((image.BytesPerRow * image.Height) + 16);

        for (var top = 0; top < image.Height; top += MaxRasterBandHeight)
        {
            var bottom = Math.Min(top + MaxRasterBandHeight, image.Height);
            var band = top == 0 && bottom == image.Height ? image : image.Slice(top, bottom);

            // GS v 0 m xL xH yL yH — m=0 is normal density in both directions.
            bytes.AddRange([Gs, (byte)'v', (byte)'0', 0]);
            bytes.AddRange([(byte)(band.BytesPerRow & 0xFF), (byte)(band.BytesPerRow >> 8)]);
            bytes.AddRange([(byte)(band.Height & 0xFF), (byte)(band.Height >> 8)]);
            bytes.AddRange(band.Pixels);
        }

        return [.. bytes];
    }

    /// <summary>
    /// Encodes text for the printer, replacing anything the chosen code page cannot represent.
    /// </summary>
    /// <remarks>
    /// Thermal printers carry a handful of single-byte code pages and no font for Indic scripts, so
    /// an item name in Tamil or Devanagari cannot be printed as itself. Substituting a visible '?'
    /// is the honest outcome: the receipt shows something is missing rather than the printer
    /// emitting whatever bytes happen to fall out.
    /// </remarks>
    public static byte[] Text(string? text, Encoding? encoding = null)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        return encoding is null
            ? Encoding.ASCII.GetBytes(Transliterate(text))
            : encoding.GetBytes(text);
    }

    /// <summary>Text followed by a line feed.</summary>
    public static byte[] Line(string? text, Encoding? encoding = null) => [.. Text(text, encoding), Lf];

    /// <summary>
    /// Reduces text to plain ASCII, keeping as much meaning as the characters allow.
    /// </summary>
    /// <remarks>
    /// This exists so the printer's code page stops mattering. PC437, WPC1252 and Latin-1 all agree
    /// exactly on bytes 0-127 and disagree above that, so a receipt built only from ASCII prints
    /// identically whatever the printer happens to be set to — and a lane whose printer was
    /// reconfigured by somebody else still produces correct receipts.
    /// <para>
    /// Accents are folded rather than lost, so "Café" prints as "Cafe" instead of "Caf?". The
    /// rupee sign becomes "Rs." because thermal fonts do not carry a glyph for it. Indic and CJK
    /// text has no ASCII equivalent at all and becomes '?', which is the honest outcome: the
    /// receipt shows something is missing rather than the printer emitting whatever bytes fall out.
    /// A store with product names in Tamil or Devanagari needs a printer with that font and a
    /// code page to match, and that is a hardware decision, not something this can paper over.
    /// </para>
    /// </remarks>
    public static string Transliterate(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var result = new StringBuilder(text.Length);

        // Decomposing separates a letter from its accent, so the accent can be dropped and the
        // letter kept.
        foreach (var character in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;

            if (character < 128)
            {
                result.Append(character);
                continue;
            }

            result.Append(Substitutions.TryGetValue(character, out var replacement) ? replacement : "?");
        }

        return result.ToString();
    }

    /// <summary>
    /// Characters worth spelling out rather than replacing with a question mark. Deliberately
    /// short: these are the ones that turn up in Indian retail product names and in text pasted
    /// out of a spreadsheet.
    /// </summary>
    private static readonly Dictionary<char, string> Substitutions = new()
    {
        ['₹'] = "Rs.",   // ₹
        ['‘'] = "'",     // ‘
        ['’'] = "'",     // ’
        ['“'] = "\"",    // “
        ['”'] = "\"",    // ”
        ['–'] = "-",     // –
        ['—'] = "-",     // —
        ['…'] = "...",   // …
        ['×'] = "x",     // ×
        ['÷'] = "/",     // ÷
        ['°'] = " deg",  // °
        ['½'] = "1/2",   // ½
        ['¼'] = "1/4",   // ¼
        ['¾'] = "3/4",   // ¾
        ['©'] = "(c)",   // ©
        ['®'] = "(R)",   // ®
        ['™'] = "(TM)",  // ™
        [' '] = " ",     // non-breaking space
    };
}
