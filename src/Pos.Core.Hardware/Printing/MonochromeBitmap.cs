namespace Pos.Core.Hardware.Printing;

/// <summary>
/// A one-bit-per-pixel image in the packing a thermal printer's raster command expects: rows top to
/// bottom, each row padded to a whole number of bytes, and within a byte the most significant bit
/// is the leftmost pixel. A set bit is a black dot.
/// </summary>
/// <remarks>
/// This is deliberately a plain data type with no drawing on it. Producing the pixels needs a font
/// engine and therefore a platform; consuming them needs neither, so the command layer, the layout
/// and every test that checks where ink lands stay portable and stay fast.
/// </remarks>
public sealed class MonochromeBitmap
{
    private readonly byte[] _rows;

    public MonochromeBitmap(int width, int height)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), width, "An image needs a positive width.");

        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height), height, "An image needs a positive height.");

        Width = width;
        Height = height;
        BytesPerRow = (width + 7) / 8;
        _rows = new byte[BytesPerRow * height];
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Row stride, in bytes. A 576-dot row is 72 bytes.</summary>
    public int BytesPerRow { get; }

    /// <summary>The packed pixels, ready to follow a raster command.</summary>
    public ReadOnlySpan<byte> Pixels => _rows;

    public bool this[int x, int y]
    {
        get
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
                return false;

            return (_rows[(y * BytesPerRow) + (x >> 3)] & (0x80 >> (x & 7))) != 0;
        }
        set
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
                return;

            var index = (y * BytesPerRow) + (x >> 3);
            var mask = (byte)(0x80 >> (x & 7));

            if (value)
                _rows[index] |= mask;
            else
                _rows[index] &= (byte)~mask;
        }
    }

    /// <summary>True when nothing was ever drawn — a blank strip that need not be sent.</summary>
    public bool IsBlank()
    {
        foreach (var b in _rows)
        {
            if (b != 0)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Number of set pixels. Used by the tests to prove a script actually rasterised, because a
    /// missing font produces a bitmap that is structurally perfect and completely empty.
    /// </summary>
    public int InkedPixels()
    {
        var total = 0;

        foreach (var b in _rows)
            total += System.Numerics.BitOperations.PopCount((uint)b);

        return total;
    }

    /// <summary>
    /// Copies rows <paramref name="top"/> (inclusive) to <paramref name="bottom"/> (exclusive) into
    /// a new image, for splitting a tall strip into bands a printer will accept.
    /// </summary>
    public MonochromeBitmap Slice(int top, int bottom)
    {
        if (top < 0 || bottom > Height || bottom <= top)
            throw new ArgumentOutOfRangeException(nameof(top), $"Rows {top}..{bottom} are not inside an image {Height} rows tall.");

        var slice = new MonochromeBitmap(Width, bottom - top);
        Array.Copy(_rows, top * BytesPerRow, slice._rows, 0, (bottom - top) * BytesPerRow);
        return slice;
    }
}
