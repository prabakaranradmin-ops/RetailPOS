using System.Text;

namespace Pos.Core.Hardware.Printing;

/// <summary>
/// A QR code, worked out here from ISO/IEC 18004 rather than asked of the printer, so the same
/// symbol is printed, previewed and shown on screen.
/// </summary>
/// <remarks>
/// <para>
/// Only what a till needs: byte mode, error correction level M (about 15% of the symbol can be
/// smudged or torn and it still reads), versions 1 to 20. A UPI payment link is well under a
/// version 10; version 20 takes 666 bytes.
/// </para>
/// <para>
/// Printers can draw a QR code themselves (<c>GS ( k</c>), but not every printer a shop buys can, and
/// the ones that can draw them at their own size. Drawing it here means one symbol everywhere, and a
/// preview that is the code the customer will scan, not a picture of where it would go.
/// </para>
/// <para>
/// Built in four steps, each in the standard's own terms: the data is written as a bit stream and
/// padded to the version's capacity; it is split into blocks, each given its Reed-Solomon check
/// bytes, and the blocks interleaved; the fixed patterns are laid down and the codewords placed in
/// the zig-zag the standard sets; and of the eight masks, the one that leaves the fewest confusing
/// features is applied, with the format information that tells a reader which.
/// </para>
/// </remarks>
public sealed class QrCode
{
    /// <summary>The most bytes a code here can carry: version 20 at level M.</summary>
    public const int MostBytes = 666;

    /// <summary>
    /// Level M, per version 1 to 20: check bytes per block, then the two groups of blocks as
    /// (count, data bytes each). ISO/IEC 18004 Table 9.
    /// </summary>
    private static readonly (int Check, int Blocks1, int Data1, int Blocks2, int Data2)[] LevelM =
    [
        (10, 1, 16, 0, 0),
        (16, 1, 28, 0, 0),
        (26, 1, 44, 0, 0),
        (18, 2, 32, 0, 0),
        (24, 2, 43, 0, 0),
        (16, 4, 27, 0, 0),
        (18, 4, 31, 0, 0),
        (22, 2, 38, 2, 39),
        (22, 3, 36, 2, 37),
        (26, 4, 43, 1, 44),
        (30, 1, 50, 4, 51),
        (22, 6, 36, 2, 37),
        (22, 8, 37, 1, 38),
        (24, 4, 40, 5, 41),
        (24, 5, 41, 5, 42),
        (28, 7, 45, 3, 46),
        (28, 10, 46, 1, 47),
        (26, 9, 43, 4, 44),
        (26, 3, 44, 11, 45),
        (26, 3, 41, 13, 42),
    ];

    /// <summary>Centres of the alignment patterns, per version 1 to 20. ISO/IEC 18004 Annex E.</summary>
    private static readonly int[][] AlignmentCentres =
    [
        [],
        [6, 18],
        [6, 22],
        [6, 26],
        [6, 30],
        [6, 34],
        [6, 22, 38],
        [6, 24, 42],
        [6, 26, 46],
        [6, 28, 50],
        [6, 30, 54],
        [6, 32, 58],
        [6, 34, 62],
        [6, 26, 46, 66],
        [6, 26, 48, 70],
        [6, 26, 50, 74],
        [6, 30, 54, 78],
        [6, 30, 56, 82],
        [6, 30, 58, 86],
        [6, 34, 62, 90],
    ];

    private readonly bool[,] _dark;

    private QrCode(int version, int mask, bool[,] dark)
    {
        Version = version;
        Mask = mask;
        _dark = dark;
    }

    /// <summary>1 to 20: how big the symbol had to be for what it carries.</summary>
    public int Version { get; }

    /// <summary>Modules along a side, not counting the quiet zone: 21 for version 1, 4 more a version.</summary>
    public int Size => SizeOf(Version);

    /// <summary>Which of the eight masks was applied, 0 to 7.</summary>
    public int Mask { get; }

    /// <summary>True for a dark module. Outside the symbol - the quiet zone - is light.</summary>
    public bool this[int x, int y] => (uint)x < (uint)Size && (uint)y < (uint)Size && _dark[y, x];

    /// <summary>The code for some text, as UTF-8.</summary>
    /// <exception cref="ArgumentException">The text is empty, or longer than <see cref="MostBytes"/>.</exception>
    public static QrCode Encode(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        return Encode(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>The code for some bytes, in the smallest version that holds them.</summary>
    public static QrCode Encode(byte[] data) => Encode(data, mask: null);

    /// <summary>
    /// The code with a given mask rather than the best one: for proving each of the eight is applied
    /// and recorded correctly, including those the penalty rules rarely choose.
    /// </summary>
    internal static QrCode Encode(byte[] data, int? mask)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length == 0)
            throw new ArgumentException("There is nothing to put in the code.", nameof(data));

        var version = 1;

        while (version <= LevelM.Length && ByteCapacity(version) < data.Length)
            version++;

        if (version > LevelM.Length)
            throw new ArgumentException($"{data.Length} bytes is more than a QR code here holds ({MostBytes}).", nameof(data));

        var codewords = Interleave(version, Pad(version, data));

        return Lay(version, codewords, mask);
    }

    /// <summary>The most bytes a version holds at level M, in byte mode.</summary>
    public static int ByteCapacity(int version)
    {
        var bits = DataCodewords(version) * 8;

        // Four bits of mode, then the count: eight bits wide to version 9, sixteen after.
        return (bits - 4 - CountBits(version)) / 8;
    }

    /// <summary>
    /// Draws the code as dots for the printer: centred across the paper, each module a square of
    /// <paramref name="dotsPerModule"/>, with the quiet zone above and below. The paper to either
    /// side is the quiet zone there.
    /// </summary>
    public MonochromeBitmap Draw(int paperWidthDots, int dotsPerModule)
    {
        if (dotsPerModule < 1)
            throw new ArgumentOutOfRangeException(nameof(dotsPerModule), dotsPerModule, "A module is at least one dot.");

        const int quiet = 4;
        var side = Size * dotsPerModule;
        var width = Math.Max(paperWidthDots, side);
        var image = new MonochromeBitmap(width, side + (2 * quiet * dotsPerModule));
        var left = (width - side) / 2;
        var top = quiet * dotsPerModule;

        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                if (!_dark[y, x])
                    continue;

                for (var dy = 0; dy < dotsPerModule; dy++)
                {
                    for (var dx = 0; dx < dotsPerModule; dx++)
                        image[left + (x * dotsPerModule) + dx, top + (y * dotsPerModule) + dy] = true;
                }
            }
        }

        return image;
    }

    /// <summary>
    /// The largest square module that keeps the code, with its quiet zone, inside a share of the
    /// paper - big enough for a phone at arm's length, small enough not to waste a roll.
    /// </summary>
    public int DotsPerModuleFor(int paperWidthDots, double share = 0.65)
    {
        var fit = (int)(paperWidthDots * share / (Size + 8));
        return Math.Clamp(fit, 1, 8);
    }

    // ---- The data --------------------------------------------------------------------------------

    private static int SizeOf(int version) => 17 + (4 * version);

    private static int CountBits(int version) => version <= 9 ? 8 : 16;

    private static int DataCodewords(int version)
    {
        var (_, blocks1, data1, blocks2, data2) = LevelM[version - 1];
        return (blocks1 * data1) + (blocks2 * data2);
    }

    /// <summary>Every codeword the symbol holds, data and check alike.</summary>
    internal static int TotalCodewords(int version)
    {
        var (check, blocks1, _, blocks2, _) = LevelM[version - 1];
        return DataCodewords(version) + (check * (blocks1 + blocks2));
    }

    /// <summary>
    /// The modules left for codewords once the fixed patterns are drawn, worked out from the
    /// symbol's geometry rather than looked up - a check on the table above.
    /// </summary>
    internal static int DataModules(int version)
    {
        var size = SizeOf(version);
        var total = size * size;

        total -= 3 * 8 * 8;                 // three finders with their separators
        total -= 2 * (size - 16);           // the two timing patterns, between the separators
        total -= (2 * 15) + 1;              // format information twice, and the dark module

        var centres = AlignmentCentres[version - 1].Length;

        if (centres > 0)
        {
            // Every pairing of centres but the three that fall on a finder.
            var patterns = (centres * centres) - 3;
            total -= patterns * 25;

            // Those in line with the timing patterns cross them, and the crossing was counted twice.
            total += 2 * (centres - 2) * 5;
        }

        if (version >= 7)
            total -= 2 * 18;                // version information twice

        return total;
    }

    /// <summary>Mode, count, the bytes, a terminator, then padding to the version's data capacity.</summary>
    private static byte[] Pad(int version, byte[] data)
    {
        var capacity = DataCodewords(version);
        var bits = new BitStream(capacity * 8);

        bits.Write(0b0100, 4);
        bits.Write(data.Length, CountBits(version));

        foreach (var b in data)
            bits.Write(b, 8);

        bits.Write(0, Math.Min(4, (capacity * 8) - bits.Length));
        bits.Write(0, (8 - (bits.Length % 8)) % 8);

        var bytes = bits.ToBytes();
        var padded = new byte[capacity];
        Array.Copy(bytes, padded, bytes.Length);

        // The two pad bytes the standard sets, alternating, for whatever room is left.
        for (var i = bytes.Length; i < capacity; i++)
            padded[i] = (i - bytes.Length) % 2 == 0 ? (byte)0xEC : (byte)0x11;

        return padded;
    }

    /// <summary>Splits the data into its blocks, adds each its check bytes, and interleaves them.</summary>
    private static byte[] Interleave(int version, byte[] data)
    {
        var (check, blocks1, data1, blocks2, data2) = LevelM[version - 1];
        var generator = ReedSolomon.Generator(check);
        var blocks = new List<(byte[] Data, byte[] Check)>(blocks1 + blocks2);
        var offset = 0;

        for (var i = 0; i < blocks1 + blocks2; i++)
        {
            var length = i < blocks1 ? data1 : data2;
            var block = data[offset..(offset + length)];
            offset += length;

            blocks.Add((block, ReedSolomon.Remainder(block, generator)));
        }

        var result = new List<byte>(TotalCodewords(version));
        var longest = Math.Max(data1, data2);

        for (var i = 0; i < longest; i++)
        {
            foreach (var (block, _) in blocks)
            {
                if (i < block.Length)
                    result.Add(block[i]);
            }
        }

        for (var i = 0; i < check; i++)
        {
            foreach (var (_, checkBytes) in blocks)
                result.Add(checkBytes[i]);
        }

        return [.. result];
    }

    // ---- The symbol ------------------------------------------------------------------------------

    private static QrCode Lay(int version, byte[] codewords, int? forcedMask)
    {
        var size = SizeOf(version);
        var dark = new bool[size, size];
        var reserved = new bool[size, size];

        void Fix(int x, int y, bool on)
        {
            dark[y, x] = on;
            reserved[y, x] = true;
        }

        // Timing first: the finders and alignment patterns overwrite the ends of it.
        for (var i = 0; i < size; i++)
        {
            Fix(6, i, i % 2 == 0);
            Fix(i, 6, i % 2 == 0);
        }

        // The three finders, each with its light separator.
        foreach (var (cx, cy) in new[] { (3, 3), (size - 4, 3), (3, size - 4) })
        {
            for (var dy = -4; dy <= 4; dy++)
            {
                for (var dx = -4; dx <= 4; dx++)
                {
                    var x = cx + dx;
                    var y = cy + dy;

                    if (x < 0 || y < 0 || x >= size || y >= size)
                        continue;

                    var ring = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    Fix(x, y, ring != 2 && ring != 4);
                }
            }
        }

        // Alignment patterns at every pairing of centres, except over a finder.
        var centres = AlignmentCentres[version - 1];
        var last = centres.Length - 1;

        for (var i = 0; i < centres.Length; i++)
        {
            for (var j = 0; j < centres.Length; j++)
            {
                if ((i == 0 && j == 0) || (i == 0 && j == last) || (i == last && j == 0))
                    continue;

                for (var dy = -2; dy <= 2; dy++)
                {
                    for (var dx = -2; dx <= 2; dx++)
                        Fix(centres[i] + dx, centres[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
                }
            }
        }

        // Room for the format information, written once the mask is chosen, and the dark module.
        PlaceFormat(dark, reserved, size, 0);
        Fix(8, size - 8, true);

        if (version >= 7)
            PlaceVersion(dark, reserved, size, version);

        PlaceCodewords(dark, reserved, size, codewords);

        // Each mask tried, and the one with the lowest penalty kept.
        var best = -1;
        var bestPenalty = int.MaxValue;

        for (var mask = 0; mask < 8; mask++)
        {
            ApplyMask(dark, reserved, size, mask);
            PlaceFormat(dark, reserved, size, mask);

            var penalty = Penalty(dark, size);

            if (penalty < bestPenalty)
            {
                best = mask;
                bestPenalty = penalty;
            }

            // Masking is its own inverse: applying it again takes it off.
            ApplyMask(dark, reserved, size, mask);
        }

        if (forcedMask is { } chosen)
            best = chosen;

        ApplyMask(dark, reserved, size, best);
        PlaceFormat(dark, reserved, size, best);

        return new QrCode(version, best, dark);
    }

    /// <summary>
    /// Level and mask, five bits, with ten BCH check bits and the standard's XOR pattern - so a
    /// reader can tell which mask to take off. Two copies, around the finders.
    /// </summary>
    private static void PlaceFormat(bool[,] dark, bool[,] reserved, int size, int mask)
    {
        // Level M is 00, so the five bits are the mask alone.
        var data = mask;
        var check = data << 10;

        for (var bit = 14; bit >= 10; bit--)
        {
            if ((check & (1 << bit)) != 0)
                check ^= 0x537 << (bit - 10);
        }

        var bits = ((data << 10) | check) ^ 0x5412;

        void Put(int x, int y, int index)
        {
            dark[y, x] = ((bits >> index) & 1) != 0;
            reserved[y, x] = true;
        }

        // Beside the top-left finder: up column 8 from the top, stepping over the timing row, then
        // along row 8 back to the left edge.
        for (var i = 0; i <= 5; i++)
            Put(8, i, i);

        Put(8, 7, 6);
        Put(8, 8, 7);
        Put(7, 8, 8);

        for (var i = 9; i < 15; i++)
            Put(14 - i, 8, i);

        // The second copy: along row 8 under the top-right finder, and up column 8 beside the
        // bottom-left one.
        for (var i = 0; i < 8; i++)
            Put(size - 1 - i, 8, i);

        for (var i = 8; i < 15; i++)
            Put(8, size - 15 + i, i);
    }

    /// <summary>From version 7: the version number, six bits with twelve Golay check bits, twice.</summary>
    private static void PlaceVersion(bool[,] dark, bool[,] reserved, int size, int version)
    {
        var check = version << 12;

        for (var bit = 17; bit >= 12; bit--)
        {
            if ((check & (1 << bit)) != 0)
                check ^= 0x1F25 << (bit - 12);
        }

        var bits = (version << 12) | check;

        for (var i = 0; i < 18; i++)
        {
            var on = ((bits >> i) & 1) != 0;
            var across = size - 11 + (i % 3);
            var down = i / 3;

            // A block of three by six above the bottom-left finder, and its mirror left of the
            // top-right one.
            dark[down, across] = on;
            reserved[down, across] = true;
            dark[across, down] = on;
            reserved[across, down] = true;
        }
    }

    /// <summary>
    /// The codewords, most significant bit first, in two-module-wide columns from the bottom right,
    /// up one and down the next, stepping over the vertical timing pattern and every fixed module.
    /// </summary>
    private static void PlaceCodewords(bool[,] dark, bool[,] reserved, int size, byte[] codewords)
    {
        var index = 0;
        var total = codewords.Length * 8;
        var upward = true;

        for (var right = size - 1; right >= 1; right -= 2)
        {
            // Column 6 is the timing pattern: the pair to its left starts at 5.
            if (right == 6)
                right = 5;

            for (var step = 0; step < size; step++)
            {
                var y = upward ? size - 1 - step : step;

                for (var side = 0; side < 2; side++)
                {
                    var x = right - side;

                    if (reserved[y, x])
                        continue;

                    // Past the last codeword are the remainder bits, which are light.
                    dark[y, x] = index < total && ((codewords[index >> 3] >> (7 - (index & 7))) & 1) != 0;
                    index++;
                }
            }

            upward = !upward;
        }
    }

    private static void ApplyMask(bool[,] dark, bool[,] reserved, int size, int mask)
    {
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (reserved[y, x])
                    continue;

                var flip = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => ((x / 3) + (y / 2)) % 2 == 0,
                    5 => ((x * y) % 2) + ((x * y) % 3) == 0,
                    6 => (((x * y) % 2) + ((x * y) % 3)) % 2 == 0,
                    _ => (((x + y) % 2) + ((x * y) % 3)) % 2 == 0,
                };

                if (flip)
                    dark[y, x] = !dark[y, x];
            }
        }
    }

    /// <summary>
    /// The standard's four penalties for a masked symbol: long runs of one colour, two-by-two
    /// blocks, anything that looks like a finder, and too much dark or light overall.
    /// </summary>
    internal static int Penalty(bool[,] dark, int size)
    {
        var penalty = 0;

        for (var line = 0; line < size; line++)
        {
            penalty += RunPenalty(i => dark[line, i], size);
            penalty += RunPenalty(i => dark[i, line], size);
            penalty += FinderLikePenalty(i => dark[line, i], size);
            penalty += FinderLikePenalty(i => dark[i, line], size);
        }

        for (var y = 0; y < size - 1; y++)
        {
            for (var x = 0; x < size - 1; x++)
            {
                var colour = dark[y, x];

                if (dark[y, x + 1] == colour && dark[y + 1, x] == colour && dark[y + 1, x + 1] == colour)
                    penalty += 3;
            }
        }

        var darkCount = 0;

        foreach (var module in dark)
        {
            if (module)
                darkCount++;
        }

        var percent = darkCount * 100 / (size * size);
        penalty += Math.Abs(percent - 50) / 5 * 10;

        return penalty;
    }

    private static int RunPenalty(Func<int, bool> at, int size)
    {
        var penalty = 0;
        var run = 1;

        for (var i = 1; i <= size; i++)
        {
            if (i < size && at(i) == at(i - 1))
            {
                run++;
                continue;
            }

            if (run >= 5)
                penalty += 3 + (run - 5);

            run = 1;
        }

        return penalty;
    }

    /// <summary>Dark-light-dark-dark-dark-light-dark with four light modules before or after it.</summary>
    private static int FinderLikePenalty(Func<int, bool> at, int size)
    {
        ReadOnlySpan<bool> core = [true, false, true, true, true, false, true];
        var penalty = 0;

        for (var start = 0; start + 7 <= size; start++)
        {
            var matches = true;

            for (var k = 0; k < 7 && matches; k++)
                matches = at(start + k) == core[k];

            if (!matches)
                continue;

            // Light beyond the edge counts as light: that is the quiet zone.
            bool LightRun(int from, int to)
            {
                for (var i = from; i < to; i++)
                {
                    if (i >= 0 && i < size && at(i))
                        return false;
                }

                return true;
            }

            if (LightRun(start - 4, start) || LightRun(start + 7, start + 11))
                penalty += 40;
        }

        return penalty;
    }

    /// <summary>Bits written most significant first, into as many bytes as they fill.</summary>
    private sealed class BitStream(int capacityBits)
    {
        private readonly List<bool> _bits = new(capacityBits);

        public int Length => _bits.Count;

        public void Write(int value, int count)
        {
            for (var i = count - 1; i >= 0; i--)
                _bits.Add(((value >> i) & 1) != 0);
        }

        public byte[] ToBytes()
        {
            var bytes = new byte[(_bits.Count + 7) / 8];

            for (var i = 0; i < _bits.Count; i++)
            {
                if (_bits[i])
                    bytes[i >> 3] |= (byte)(0x80 >> (i & 7));
            }

            return bytes;
        }
    }
}

/// <summary>
/// Reed-Solomon check bytes over GF(2^8) with the field polynomial QR codes use,
/// x^8 + x^4 + x^3 + x^2 + 1.
/// </summary>
internal static class ReedSolomon
{
    private static readonly byte[] Exp = new byte[512];
    private static readonly byte[] Log = new byte[256];

    static ReedSolomon()
    {
        var value = 1;

        for (var power = 0; power < 255; power++)
        {
            Exp[power] = (byte)value;
            Log[value] = (byte)power;

            value <<= 1;

            if (value >= 256)
                value ^= 0x11D;
        }

        // Doubled, so a product of two logarithms never needs reducing.
        for (var power = 255; power < 512; power++)
            Exp[power] = Exp[power - 255];
    }

    public static byte Multiply(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];

    /// <summary>
    /// (x - a^0)(x - a^1)...(x - a^(degree-1)), highest power first. Its leading coefficient is 1.
    /// </summary>
    public static byte[] Generator(int degree)
    {
        byte[] polynomial = [1];

        for (var i = 0; i < degree; i++)
        {
            var root = Exp[i];
            var next = new byte[polynomial.Length + 1];

            for (var j = 0; j < polynomial.Length; j++)
            {
                next[j] ^= polynomial[j];
                next[j + 1] ^= Multiply(polynomial[j], root);
            }

            polynomial = next;
        }

        return polynomial;
    }

    /// <summary>The remainder of the data, shifted up by the generator's degree, divided by the generator.</summary>
    public static byte[] Remainder(byte[] data, byte[] generator)
    {
        var degree = generator.Length - 1;
        var remainder = new byte[degree];

        foreach (var value in data)
        {
            var factor = (byte)(value ^ remainder[0]);

            Array.Copy(remainder, 1, remainder, 0, degree - 1);
            remainder[degree - 1] = 0;

            for (var j = 0; j < degree; j++)
                remainder[j] ^= Multiply(generator[j + 1], factor);
        }

        return remainder;
    }
}
