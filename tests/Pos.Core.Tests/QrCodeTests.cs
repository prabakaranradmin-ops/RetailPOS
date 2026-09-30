using Pos.Core.Hardware.Printing;
using Pos.TestSupport;
using Xunit;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace Pos.Core.Tests;

/// <summary>
/// The till's own QR codes. The proof they are right is not that the modules look like a QR code:
/// it is that an independent reader, knowing nothing of how they were made, reads back exactly what
/// was put in - at every size, under every mask, off the dots the printer is sent.
/// </summary>
public class QrCodeTests
{
    // ---- The parts, against the standard ------------------------------------------------------

    /// <summary>"HELLO WORLD" at version 1-M: the worked example in every account of the standard.</summary>
    [Fact]
    public void TheCheckBytesMatchTheWorkedExample()
    {
        byte[] data = [32, 91, 11, 120, 209, 114, 220, 77, 67, 64, 236, 17, 236, 17, 236, 17];

        var check = ReedSolomon.Remainder(data, ReedSolomon.Generator(10));

        Assert.Equal([196, 35, 39, 119, 235, 215, 231, 226, 93, 23], check);
    }

    /// <summary>
    /// The block table and the symbol's geometry are worked out separately; they must agree to the
    /// bit, leaving only the remainder bits the standard sets for each version.
    /// </summary>
    [Theory]
    [MemberData(nameof(Versions))]
    public void TheBlockTableFillsTheSymbolExactly(int version)
    {
        var left = QrCode.DataModules(version) - (QrCode.TotalCodewords(version) * 8);

        Assert.Equal(version switch { 1 => 0, <= 6 => 7, <= 13 => 0, _ => 3 }, left);
    }

    public static TheoryData<int> Versions() => [.. Enumerable.Range(1, 20)];

    /// <summary>Level M's format information for each mask, as the standard tabulates it.</summary>
    [Fact]
    public void TheFormatInformationIsTheStandardsForEveryMask()
    {
        int[] levelM = [0x5412, 0x5125, 0x5E7C, 0x5B4B, 0x45F9, 0x40CE, 0x4F97, 0x4AA0];
        var seen = new HashSet<int>();

        foreach (var code in CodesUntilEveryMaskIsUsed())
        {
            Assert.Equal(levelM[code.Mask], FormatBits(code));
            seen.Add(code.Mask);
        }

        Assert.Equal(8, seen.Count);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(14, 1)]
    [InlineData(15, 2)]
    [InlineData(106, 6)]
    [InlineData(107, 7)]
    [InlineData(213, 10)]
    [InlineData(214, 11)]
    [InlineData(666, 20)]
    public void TheSmallestVersionThatHoldsItIsUsed(int length, int version)
    {
        Assert.Equal(version, QrCode.Encode(Text(length)).Version);
    }

    [Fact]
    public void MoreThanACodeHoldsIsRefused()
    {
        var refused = Assert.Throws<ArgumentException>(() => QrCode.Encode(Text(QrCode.MostBytes + 1)));
        Assert.Contains("more than a QR code here holds", refused.Message);
    }

    [Fact]
    public void NothingIsRefused() => Assert.ThrowsAny<ArgumentException>(() => QrCode.Encode(string.Empty));

    // ---- Read back by somebody else's reader -------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(60)]
    [InlineData(106)]
    [InlineData(107)]
    [InlineData(150)]
    [InlineData(213)]
    [InlineData(300)]
    [InlineData(450)]
    [InlineData(666)]
    public void ACodeOfEverySizeIsReadBack(int length)
    {
        var text = Text(length);

        Assert.Equal(text, Read(QrCode.Encode(text)));
    }

    /// <summary>Each of the eight masks is applied and recorded correctly, not just the common ones.</summary>
    [Fact]
    public void ACodeUnderEveryMaskIsReadBack()
    {
        foreach (var (code, text) in CodesUntilEveryMaskIsUsed(withText: true))
            Assert.Equal(text, Read(code));
    }

    [Fact]
    public void AUpiLinkIsReadBackExactly()
    {
        const string link = "upi://pay?pa=murugan.stores@okaxis&pn=Sri%20Murugan%20Stores&am=400.50&cu=INR";

        Assert.Equal(link, Read(QrCode.Encode(link)));
    }

    [Fact]
    public void TamilIsCarriedAsUtf8() =>
        Assert.Equal("ரவி மளிகை - 400.50", Read(QrCode.Encode("ரவி மளிகை - 400.50")));

    /// <summary>The dots sent to an 80mm printer, not just the modules: what the customer scans.</summary>
    [Theory]
    [InlineData(576)]
    [InlineData(384)]
    public void TheDotsForThePrinterAreReadBack(int paperWidthDots)
    {
        const string link = "upi://pay?pa=murugan.stores@okaxis&pn=Sri%20Murugan%20Stores&am=1234.00&cu=INR";
        var code = QrCode.Encode(link);
        var dots = code.DotsPerModuleFor(paperWidthDots);

        var image = code.Draw(paperWidthDots, dots);

        Assert.InRange(dots, 3, 8);
        Assert.Equal(paperWidthDots, image.Width);
        Assert.Equal(link, Read(image));
    }

    // ---- On the receipt ------------------------------------------------------------------------

    [Fact]
    public void AReceiptPrintsTheCodeAsDotsWhateverTheLanePrintsTextAs()
    {
        var receipt = new ReceiptBuilder().Qr("upi://pay?pa=a1@okaxis&am=10.00&cu=INR");

        var bytes = receipt.ToEscPos();

        // GS v 0: a raster image, 72 bytes (576 dots) a row.
        var at = IndexOf(bytes, [0x1D, (byte)'v', (byte)'0', 0x00, 72, 0]);
        Assert.True(at >= 0, "no raster image was sent for the code");
    }

    [Fact]
    public void TheTextPreviewSaysWhatTheCodeCarries()
    {
        var text = new ReceiptBuilder(32).Qr("upi://pay?pa=a1@okaxis&am=10.00&cu=INR").ToPlainText();

        Assert.Contains("[QR]", text);
        Assert.Contains("upi://pay?pa=a1@okaxis&am=10.00", text.Replace("\r", string.Empty).Replace("\n", string.Empty));
    }

    /// <summary>The preview of a whole slip carries the real code, not a picture of where it goes.</summary>
    [Fact]
    public void TheDrawnReceiptCarriesTheRealCode()
    {
        const string link = "upi://pay?pa=a1@okaxis&am=10.00&cu=INR";
        var raster = new RasterOptions(new RecordingTextRasterizer(), 576);

        var page = new ReceiptBuilder().Text("SCAN TO PAY").Qr(link).Text("Not a bill").ToBitmap(raster);

        Assert.Equal(link, Read(page));
    }

    // ---- Helpers -------------------------------------------------------------------------------

    /// <summary>Text of a given length, varied enough that the masks come out different.</summary>
    private static string Text(int length) =>
        string.Concat(Enumerable.Range(0, length).Select(i => (char)('!' + ((i * 37) + (length * 11)) % 90)));

    private static IEnumerable<QrCode> CodesUntilEveryMaskIsUsed() =>
        CodesUntilEveryMaskIsUsed(withText: false).Select(pair => pair.Code);

    /// <summary>
    /// A code under each of the eight masks, forced: the penalty rules rarely choose some of them
    /// (mask 1's stripes run long), but a reader must be able to take any of them off.
    /// </summary>
    private static IEnumerable<(QrCode Code, string Text)> CodesUntilEveryMaskIsUsed(bool withText)
    {
        for (var mask = 0; mask < 8; mask++)
        {
            var text = $"upi://pay?pa=shop{mask}@okaxis&am={mask + 1}.50&cu=INR";
            var code = QrCode.Encode(System.Text.Encoding.UTF8.GetBytes(text), mask);

            Assert.Equal(mask, code.Mask);
            yield return (code, text);
        }
    }

    /// <summary>Left to itself, the encoder chooses a mask by the penalty rules - and varies it.</summary>
    [Fact]
    public void TheMaskIsChosenPerCode()
    {
        var masks = Enumerable.Range(1, 200)
            .Select(n => QrCode.Encode($"upi://pay?pa=shop{n}@okaxis&am={n}.00&cu=INR").Mask)
            .ToHashSet();

        Assert.True(masks.Count >= 4, $"only masks {string.Join(", ", masks)} were ever chosen");
    }

    /// <summary>The fifteen format bits as laid beside the top-left finder, bit 0 first.</summary>
    private static int FormatBits(QrCode code)
    {
        var bits = 0;

        void Take(int x, int y, int index)
        {
            if (code[x, y])
                bits |= 1 << index;
        }

        for (var i = 0; i <= 5; i++)
            Take(8, i, i);

        Take(8, 7, 6);
        Take(8, 8, 7);
        Take(7, 8, 8);

        for (var i = 9; i < 15; i++)
            Take(14 - i, 8, i);

        return bits;
    }

    private static string? Read(QrCode code)
    {
        const int scale = 4;
        const int quiet = 4;
        var side = (code.Size + (2 * quiet)) * scale;
        var pixels = new byte[side * side];

        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
                pixels[(y * side) + x] = code[(x / scale) - quiet, (y / scale) - quiet] ? (byte)0 : (byte)255;
        }

        return Read(pixels, side, side);
    }

    private static string? Read(MonochromeBitmap image)
    {
        var pixels = new byte[image.Width * image.Height];

        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
                pixels[(y * image.Width) + x] = image[x, y] ? (byte)0 : (byte)255;
        }

        return Read(pixels, image.Width, image.Height);
    }

    private static string? Read(byte[] pixels, int width, int height)
    {
        var source = new RGBLuminanceSource(pixels, width, height, RGBLuminanceSource.BitmapFormat.Gray8);
        var hints = new Dictionary<DecodeHintType, object>
        {
            [DecodeHintType.CHARACTER_SET] = "UTF-8",
            [DecodeHintType.TRY_HARDER] = true,
        };

        return new QRCodeReader().decode(new BinaryBitmap(new HybridBinarizer(source)), hints)?.Text;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                return i;
        }

        return -1;
    }
}
