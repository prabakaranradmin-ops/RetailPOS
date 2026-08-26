using Pos.Core.Domain;
using Pos.Core.Domain.Printing;
using Pos.Core.Hardware.Printing;
using Pos.Core.Hardware.Windows;
using Xunit;
using Xunit.Abstractions;

namespace Pos.App.Tests;

/// <summary>
/// Tamil through the real font engine.
/// </summary>
/// <remarks>
/// The layout tests use a rasteriser made of arithmetic, which is right for asserting where text
/// lands but proves nothing about whether the glyphs exist. These use whatever font the machine
/// actually has, because the failure this feature is meant to prevent — Tamil arriving as a row of
/// boxes, or as nothing — looks perfectly healthy to any check that only counts bytes.
/// </remarks>
public class TamilRasterizationTests(ITestOutputHelper output)
{
    private const string ShopName = "ரவி மளிகை";
    private const string ItemHeading = "பொருளின் பெயர்";

    private static MonochromeBitmap Draw(GdiTextRasterizer rasterizer, string text, RasterTextStyle style = default)
    {
        var strip = new MonochromeBitmap(RasterOptions.Dots80Mm, Math.Max(1, rasterizer.LineHeight(style)));
        rasterizer.Draw(strip, text, 0, 0, style);
        return strip;
    }

    [Fact]
    public void AFontThatCanCarryTamilIsFound()
    {
        using var rasterizer = new GdiTextRasterizer();
        output.WriteLine($"resolved font: {rasterizer.FontFamily}");

        Assert.False(string.IsNullOrWhiteSpace(rasterizer.FontFamily));
    }

    [Fact]
    public void AFontThatIsNotInstalledFallsBackRatherThanFailing()
    {
        using var rasterizer = new GdiTextRasterizer("No Such Font On Any Machine");

        Assert.NotEqual("No Such Font On Any Machine", rasterizer.FontFamily);
        Assert.False(Draw(rasterizer, ShopName).IsBlank());
    }

    [Theory]
    [InlineData(ShopName)]
    [InlineData(ItemHeading)]
    [InlineData("இதுவரை பெற்ற மொத்த புள்ளிகள்")]
    [InlineData("மொத்தம்")]
    [InlineData("இன்றைய சேமிப்பு")]
    public void TamilPutsInkOnThePaper(string text)
    {
        using var rasterizer = new GdiTextRasterizer();
        var strip = Draw(rasterizer, text);

        output.WriteLine($"{text}: {rasterizer.Measure(text, default)} dots wide, {strip.InkedPixels()} inked");

        Assert.True(rasterizer.Measure(text, default) > 0);
        Assert.True(strip.InkedPixels() > 20, $"'{text}' drew almost nothing.");
    }

    /// <summary>
    /// The test that a missing font cannot pass. A font with no Tamil renders every character as
    /// the same fallback box, so two different words of the same length would put down identical
    /// ink. Real glyphs do not.
    /// </summary>
    [Fact]
    public void DifferentTamilWordsOfTheSameLengthDrawDifferently()
    {
        using var rasterizer = new GdiTextRasterizer();

        var first = Draw(rasterizer, "ரவி");
        var second = Draw(rasterizer, "மளி");

        Assert.NotEqual(first.InkedPixels(), second.InkedPixels());
        Assert.True(first.InkedPixels() > 0);
        Assert.True(second.InkedPixels() > 0);
    }

    /// <summary>
    /// A Tamil syllable is assembled from several code points, so its width is not its character
    /// count times anything — which is exactly why the character path cannot lay it out.
    /// </summary>
    [Fact]
    public void ASyllableIsWiderThanItsBaseConsonantAlone()
    {
        using var rasterizer = new GdiTextRasterizer();

        var bare = rasterizer.Measure("க", default);
        var withVowelSign = rasterizer.Measure("கெ", default);

        Assert.True(bare > 0);
        Assert.True(withVowelSign > bare, $"'கெ' measured {withVowelSign}, no wider than 'க' at {bare}.");
    }

    [Fact]
    public void EnglishAndTamilShareTheSameLineHeight()
    {
        using var rasterizer = new GdiTextRasterizer();

        // A bilingual bill in which the drawn lines are a different height from each other reads as
        // two receipts stuck together.
        Assert.Equal(rasterizer.LineHeight(default), rasterizer.LineHeight(new RasterTextStyle(Bold: true)));
        Assert.True(rasterizer.LineHeight(default) is > 12 and < 48);
    }

    [Fact]
    public void DoublingTheScaleDoublesTheSize()
    {
        using var rasterizer = new GdiTextRasterizer();

        var single = rasterizer.Measure(ShopName, default);
        var doubleWidth = rasterizer.Measure(ShopName, new RasterTextStyle(WidthMultiplier: 2));
        var doubleBoth = rasterizer.Measure(ShopName, new RasterTextStyle(WidthMultiplier: 2, HeightMultiplier: 2));

        Assert.InRange(doubleWidth, (single * 2) - 4, (single * 2) + 4);
        Assert.InRange(doubleBoth, (single * 2) - 4, (single * 2) + 4);

        // Within a dot or two rather than exactly: the line box is a font metric rounded up, and
        // rounding a doubled measurement is not the same as doubling a rounded one.
        var single26 = rasterizer.LineHeight(default);
        Assert.InRange(rasterizer.LineHeight(new RasterTextStyle(HeightMultiplier: 2)), (single26 * 2) - 2, (single26 * 2) + 2);
    }

    [Fact]
    public void TextIsDrawnWhereItIsAskedForAndClippedAtThePaperEdge()
    {
        using var rasterizer = new GdiTextRasterizer();
        var strip = new MonochromeBitmap(RasterOptions.Dots80Mm, rasterizer.LineHeight(default));

        rasterizer.Draw(strip, ShopName, 200, 0, default);

        var leftmost = Leftmost(strip);
        Assert.True(leftmost >= 200, $"ink started at {leftmost}, left of the requested 200.");

        // Running off the right-hand edge clips rather than throwing or wrapping round.
        rasterizer.Draw(strip, ShopName, RasterOptions.Dots80Mm - 10, 0, default);
        Assert.True(strip.InkedPixels() > 0);
    }

    /// <summary>
    /// Two runs share a line — a label and its figure — so drawing the second must not wipe out the
    /// first. The rasteriser adds ink; it never clears.
    /// </summary>
    [Fact]
    public void DrawingASecondRunKeepsTheFirst()
    {
        using var rasterizer = new GdiTextRasterizer();
        var strip = new MonochromeBitmap(RasterOptions.Dots80Mm, rasterizer.LineHeight(default));

        rasterizer.Draw(strip, "மொத்தம்", 0, 0, default);
        var afterFirst = strip.InkedPixels();

        rasterizer.Draw(strip, "1,184.00", 400, 0, default);

        Assert.True(strip.InkedPixels() > afterFirst);
        Assert.True(Leftmost(strip) < 100);
    }

    [Fact]
    public void EmptyTextDrawsNothingAndMeasuresZero()
    {
        using var rasterizer = new GdiTextRasterizer();

        Assert.Equal(0, rasterizer.Measure("", default));
        Assert.True(Draw(rasterizer, "").IsBlank());
    }

    // ---- The whole bill --------------------------------------------------------------------------

    /// <summary>
    /// End to end with the real font: a Tamil receipt rendered to dots has to be a page of ink of
    /// roughly the right shape, not a tall blank strip.
    /// </summary>
    [Fact]
    public void AWholeTamilReceiptRendersToAPageOfInk()
    {
        using var rasterizer = new GdiTextRasterizer();

        var store = new StoreProfile
        {
            Name = ShopName,
            AddressLine1 = "No. 3/324, Main Road,",
            Gstin = "33AEIPH7795F1Z9",
            FssaiNumber = "12426020000127",
            CustomerCarePhone = "9080678177",
            CurrencyPrefix = "Rs:",
        };

        var receipt = new ReceiptComposer(store, ReceiptBuilder.Width80Mm, ReceiptLanguage.Tamil)
            .Compose(SampleSale());

        var page = receipt.ToBitmap(new RasterOptions(rasterizer, RasterOptions.Dots80Mm, RasterMode.Always));

        output.WriteLine($"{page.Width}x{page.Height} dots, {page.InkedPixels()} inked");

        Assert.Equal(RasterOptions.Dots80Mm, page.Width);
        Assert.True(page.Height > 300, "a receipt this long cannot be that short.");
        Assert.True(page.InkedPixels() > 3_000, "the page came out nearly blank.");

        // Nothing runs off the right-hand edge of the paper.
        Assert.True(Rightmost(page) < RasterOptions.Dots80Mm);
    }

    private static SettledInvoice SampleSale()
    {
        InvoiceLine[] lines =
        [
            InvoiceLine.Rehydrate(1, "CUTTING BASMATHI BULK", "1006", "8901234567890", null, UnitType.Each, 95m, 95m, true, 5m, 5m, 0m, false),
            InvoiceLine.Rehydrate(2, "AAG PALM OIL 800 G", "1511", "8901234567891", null, UnitType.Each, 135m, 135m, true, 5m, 2m, 0m, false),
        ];

        var totals = InvoiceTotals.From(lines);

        var sale = new SaleDraft(
            "L1",
            DateTimeOffset.Now,
            null,
            lines,
            totals,
            [new Tender(TenderType.Upi, totals.GrandTotal)],
            0m,
            0,
            0,
            null);

        return new SettledInvoice(1, "RM/26-27/11358", sale);
    }

    private static int Leftmost(MonochromeBitmap image)
    {
        for (var x = 0; x < image.Width; x++)
        {
            for (var y = 0; y < image.Height; y++)
            {
                if (image[x, y])
                    return x;
            }
        }

        return image.Width;
    }

    private static int Rightmost(MonochromeBitmap image)
    {
        for (var x = image.Width - 1; x >= 0; x--)
        {
            for (var y = 0; y < image.Height; y++)
            {
                if (image[x, y])
                    return x;
            }
        }

        return -1;
    }
}
