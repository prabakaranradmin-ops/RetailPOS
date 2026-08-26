using Pos.Core.Hardware.Printing;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// The bitmap format and the command that carries it. Both are asserted byte for byte, because a
/// raster image that is off by one bit does not look slightly wrong — it shears, and every line
/// after it comes out as noise.
/// </summary>
public class RasterImageTests
{
    // ---- Packing -------------------------------------------------------------------------------

    [Fact]
    public void TheLeftmostPixelIsTheMostSignificantBit()
    {
        var image = new MonochromeBitmap(8, 1);
        image[0, 0] = true;

        Assert.Equal(0x80, image.Pixels[0]);

        image[0, 0] = false;
        image[7, 0] = true;

        Assert.Equal(0x01, image.Pixels[0]);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(8, 1)]
    [InlineData(9, 2)]
    [InlineData(384, 48)]
    [InlineData(576, 72)]
    public void RowsArePaddedToWholeBytes(int width, int expectedBytesPerRow)
    {
        var image = new MonochromeBitmap(width, 3);

        Assert.Equal(expectedBytesPerRow, image.BytesPerRow);
        Assert.Equal(expectedBytesPerRow * 3, image.Pixels.Length);
    }

    [Fact]
    public void PixelsOutsideTheImageAreIgnoredRatherThanThrowing()
    {
        var image = new MonochromeBitmap(8, 2);

        // Glyphs get clipped by the edge of the paper all the time; that is not an error, and a
        // receipt must not fail to print because a name was one dot too long.
        image[-1, 0] = true;
        image[8, 0] = true;
        image[0, -1] = true;
        image[0, 2] = true;

        Assert.True(image.IsBlank());
        Assert.False(image[-1, 0]);
        Assert.False(image[99, 99]);
    }

    [Fact]
    public void InkIsCountedAndBlanknessIsReported()
    {
        var image = new MonochromeBitmap(16, 2);
        Assert.True(image.IsBlank());
        Assert.Equal(0, image.InkedPixels());

        image[0, 0] = true;
        image[15, 1] = true;

        Assert.False(image.IsBlank());
        Assert.Equal(2, image.InkedPixels());
    }

    [Fact]
    public void ASliceKeepsTheRowsItWasAskedFor()
    {
        var image = new MonochromeBitmap(8, 4);
        image[3, 2] = true;

        var slice = image.Slice(2, 4);

        Assert.Equal(2, slice.Height);
        Assert.True(slice[3, 0]);
        Assert.False(slice[3, 1]);
    }

    // ---- The command ---------------------------------------------------------------------------

    [Fact]
    public void TheRasterCommandCarriesTheImageDimensionsAndItsPixels()
    {
        var image = new MonochromeBitmap(16, 2);
        image[0, 0] = true;
        image[15, 1] = true;

        var bytes = EscPos.RasterImage(image);

        // GS v 0 m xL xH yL yH, then the pixels.
        Assert.Equal([EscPos.Gs, (byte)'v', (byte)'0', 0, 2, 0, 2, 0, 0x80, 0x00, 0x00, 0x01], bytes);
    }

    [Fact]
    public void WidthAndHeightAreSentLowByteFirst()
    {
        var bytes = EscPos.RasterImage(new MonochromeBitmap(576, 300));

        // 72 bytes per row, and a 300-row image split into bands of 255 and 45.
        Assert.Equal(72, bytes[4] | (bytes[5] << 8));
        Assert.Equal(255, bytes[6] | (bytes[7] << 8));
    }

    /// <summary>
    /// A tall image is split into bands. Printers buffer a band before printing it, and firmware
    /// handed a very tall one commonly prints garbage or drops it altogether.
    /// </summary>
    [Fact]
    public void ATallImageIsSentAsSeveralBandsThatAddUpToIt()
    {
        const int height = 600;
        var image = new MonochromeBitmap(64, height);

        for (var y = 0; y < height; y++)
            image[y % 64, y] = true;

        var bytes = EscPos.RasterImage(image);

        var bands = new List<int>();
        var offset = 0;

        while (offset < bytes.Length)
        {
            Assert.Equal(EscPos.Gs, bytes[offset]);
            Assert.Equal((byte)'v', bytes[offset + 1]);
            Assert.Equal((byte)'0', bytes[offset + 2]);

            var bytesPerRow = bytes[offset + 4] | (bytes[offset + 5] << 8);
            var bandHeight = bytes[offset + 6] | (bytes[offset + 7] << 8);

            bands.Add(bandHeight);
            offset += 8 + (bytesPerRow * bandHeight);
        }

        Assert.Equal(bytes.Length, offset);
        Assert.Equal([255, 255, 90], bands);
        Assert.Equal(height, bands.Sum());
    }

    [Fact]
    public void AnImageExactlyOneBandTallIsSentAsOneCommand()
    {
        var bytes = EscPos.RasterImage(new MonochromeBitmap(8, EscPos.MaxRasterBandHeight));

        Assert.Equal(8 + EscPos.MaxRasterBandHeight, bytes.Length);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-4, 4)]
    public void AnImageWithNoAreaIsRefused(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MonochromeBitmap(width, height));
    }
}
