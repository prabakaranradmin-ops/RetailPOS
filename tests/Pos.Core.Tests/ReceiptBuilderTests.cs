using Pos.Core.Hardware.Printing;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// Receipt layout. Asserted as text rather than as bytes, because layout faults are things you can
/// see — a column that does not line up, a name that swallows the price — and a byte array hides
/// exactly those.
/// </summary>
public class ReceiptBuilderTests
{
    private static ReceiptBuilder Narrow() => new(ReceiptBuilder.Width58Mm);

    private static string[] LinesOf(ReceiptBuilder receipt) =>
        receipt.ToPlainText().Split(Environment.NewLine, StringSplitOptions.None)[..^1];

    // ---- Alignment ----------------------------------------------------------------------------

    [Fact]
    public void TextIsLeftAlignedByDefault()
    {
        Assert.Equal("Toor Dal", LinesOf(Narrow().Text("Toor Dal"))[0]);
    }

    [Fact]
    public void CentredTextIsPaddedToTheMiddle()
    {
        var line = LinesOf(Narrow().Text("SHOP", TextAlignment.Center))[0];

        // 32 wide, 4 characters: 14 spaces each side, trailing ones trimmed.
        Assert.Equal(new string(' ', 14) + "SHOP", line);
    }

    [Fact]
    public void RightAlignedTextEndsAtTheMargin()
    {
        var line = LinesOf(Narrow().Text("99.00", TextAlignment.Right))[0];

        Assert.Equal(ReceiptBuilder.Width58Mm, line.Length);
        Assert.EndsWith("99.00", line);
    }

    // ---- Two-column rows ----------------------------------------------------------------------

    [Fact]
    public void ColumnsPutTheFigureHardAgainstTheRightMargin()
    {
        var line = LinesOf(Narrow().Columns("TOTAL", "1,137.00"))[0];

        Assert.Equal(ReceiptBuilder.Width58Mm, line.Length);
        Assert.StartsWith("TOTAL", line);
        Assert.EndsWith("1,137.00", line);
    }

    /// <summary>
    /// When the line is too tight for both, the label gives way. The figure is the part that must
    /// survive: a truncated label is still readable, a truncated amount is a wrong receipt.
    /// </summary>
    [Fact]
    public void AnOverlongLabelIsTruncatedRatherThanPushingTheFigureOff()
    {
        var line = LinesOf(Narrow().Columns(new string('x', 60), "1,137.00"))[0];

        Assert.Equal(ReceiptBuilder.Width58Mm, line.Length);
        Assert.EndsWith("1,137.00", line);
    }

    [Fact]
    public void ARuleFillsTheWidth()
    {
        Assert.Equal(new string('-', ReceiptBuilder.Width58Mm), LinesOf(Narrow().Rule())[0]);
        Assert.Equal(new string('=', ReceiptBuilder.Width58Mm), LinesOf(Narrow().Rule('='))[0]);
    }

    // ---- Item rows ----------------------------------------------------------------------------

    [Fact]
    public void AnItemRowLinesUpItsFigures()
    {
        var receipt = new ReceiptBuilder(ReceiptBuilder.Width80Mm)
            .Row("Toor Dal 1kg", new ColumnValue("1", 6), new ColumnValue("189.00", 9), new ColumnValue("189.00", 10))
            .Row("Sugar Loose", new ColumnValue("2.75", 6), new ColumnValue("45.00", 9), new ColumnValue("123.75", 10));

        var lines = LinesOf(receipt);

        // Both rows end at the same column with their amount, which is what makes a column of
        // figures scannable down the page.
        Assert.Equal(lines[0].Length, lines[1].Length);
        Assert.EndsWith("189.00", lines[0]);
        Assert.EndsWith("123.75", lines[1]);

        // And the quantity column starts at the same place on both.
        Assert.Equal(
            lines[0].LastIndexOf("189.00", StringComparison.Ordinal),
            lines[1].LastIndexOf("123.75", StringComparison.Ordinal));
    }

    /// <summary>
    /// A long item name wraps onto its own line instead of pushing the price off the paper. The
    /// figures stay on the first line, beside the start of the name.
    /// </summary>
    [Fact]
    public void ALongItemNameWrapsAndKeepsItsFiguresOnTheFirstLine()
    {
        var receipt = Narrow().Row(
            "Premium Organic Cold Pressed Groundnut Oil 5 Litre Tin",
            new ColumnValue("1", 5),
            new ColumnValue("1,299.00", 9));

        var lines = LinesOf(receipt);

        Assert.True(lines.Length > 1, "A name this long should have wrapped.");
        Assert.EndsWith("1,299.00", lines[0]);
        Assert.All(lines, line => Assert.True(line.Length <= ReceiptBuilder.Width58Mm, $"'{line}' is wider than the paper."));
    }

    // ---- Wrapping -----------------------------------------------------------------------------

    [Fact]
    public void TextWrapsOnWordBoundaries()
    {
        var lines = ReceiptBuilder.Wrap("the quick brown fox jumps over the lazy dog", 12).ToArray();

        Assert.All(lines, line => Assert.True(line.Length <= 12));
        Assert.Equal("the quick", lines[0]);
        Assert.DoesNotContain(lines, line => line.StartsWith(' ') || line.EndsWith(' '));
    }

    /// <summary>
    /// A single word longer than the paper — a barcode, a run-on product code — has to be broken
    /// hard. Dropping it would silently lose information from the receipt.
    /// </summary>
    [Fact]
    public void AWordLongerThanThePaperIsBrokenRatherThanLost()
    {
        var lines = ReceiptBuilder.Wrap("8901234567890123456789", 10).ToArray();

        Assert.Equal(3, lines.Length);
        Assert.Equal("8901234567890123456789", string.Concat(lines));
    }

    /// <summary>
    /// On 58mm paper the figures do not fit beside a readable name, so the row stacks instead.
    /// Without this the name shreds into four-character fragments — "Toor", "Dal", "1kg" — which
    /// fits the paper and is useless.
    /// </summary>
    [Fact]
    public void OnNarrowPaperTheFiguresMoveBeneathTheNameRatherThanShreddingIt()
    {
        var receipt = Narrow().Row(
            "Toor Dal 1kg",
            new ColumnValue("1", 6),
            new ColumnValue("189.00", 9),
            new ColumnValue("189.00", 10));

        var lines = LinesOf(receipt);

        Assert.Equal("Toor Dal 1kg", lines[0]);
        Assert.EndsWith("189.00", lines[1]);
        Assert.Contains("189.00     189.00", lines[1]);
    }

    [Fact]
    public void ALongNameOnNarrowPaperStillWrapsOnWords()
    {
        var receipt = Narrow().Row(
            "Premium Organic Cold Pressed Groundnut Oil",
            new ColumnValue("1", 6),
            new ColumnValue("1,299.00", 9),
            new ColumnValue("1,299.00", 10));

        var lines = LinesOf(receipt);

        // Whole words, not fragments.
        Assert.StartsWith("Premium Organic", lines[0]);
        Assert.DoesNotContain(lines[..^1], line => line.Length is > 0 and < 4);
        Assert.EndsWith("1,299.00", lines[^1]);
    }

    /// <summary>
    /// A short label fits in the gap beside the figures however narrow the paper is. The tax
    /// summary is rows of "5%" and "18%", and stacking those would be worse, not better.
    /// </summary>
    [Fact]
    public void AShortLabelStaysBesideItsFiguresEvenOnNarrowPaper()
    {
        var receipt = Narrow().Row("5%", new ColumnValue("1,535.00", 12), new ColumnValue("76.75", 10));

        var line = Assert.Single(LinesOf(receipt));

        Assert.StartsWith("5%", line);
        Assert.EndsWith("76.75", line);
    }

    /// <summary>Wide paper keeps the figures beside the name, where they read best.</summary>
    [Fact]
    public void OnWidePaperTheFiguresStayBesideTheName()
    {
        var receipt = new ReceiptBuilder(ReceiptBuilder.Width80Mm).Row(
            "Toor Dal 1kg",
            new ColumnValue("1", 6),
            new ColumnValue("189.00", 9),
            new ColumnValue("189.00", 10));

        var line = Assert.Single(LinesOf(receipt));

        Assert.StartsWith("Toor Dal 1kg", line);
        Assert.EndsWith("189.00", line);
    }

    [Fact]
    public void NoLineEverExceedsThePaperWidth()
    {
        var receipt = new ReceiptBuilder(ReceiptBuilder.Width58Mm)
            .Text("A shop with a really rather long trading name indeed", TextAlignment.Center)
            .Columns("An extremely long label that will not fit", "9,999.00")
            .Row("Another very long product description here", new ColumnValue("10", 4), new ColumnValue("100.00", 8))
            .Rule();

        Assert.All(LinesOf(receipt), line =>
            Assert.True(line.Length <= ReceiptBuilder.Width58Mm, $"'{line}' is {line.Length} wide on {ReceiptBuilder.Width58Mm} paper."));
    }

    /// <summary>Double-width characters take two cells, so a scaled line fits half as much.</summary>
    [Fact]
    public void DoubleWidthTextWrapsAtHalfTheCharacterCount()
    {
        var receipt = Narrow().Text("ABCDEFGHIJKLMNOPQRSTUVWXYZ", widthMultiplier: 2);

        Assert.All(LinesOf(receipt), line => Assert.True(line.Length <= ReceiptBuilder.Width58Mm / 2));
    }

    // ---- Byte output --------------------------------------------------------------------------

    [Fact]
    public void TheJobStartsByResettingThePrinter()
    {
        var bytes = Narrow().Text("hello").ToEscPos();

        Assert.Equal(EscPos.Initialize(), bytes[..2]);
    }

    /// <summary>
    /// Formatting is only re-sent when it actually changes. A receipt that re-declared bold on
    /// every line would triple the job size on a device measured in hundreds of bytes.
    /// </summary>
    [Fact]
    public void ModeChangesAreNotRepeatedOnEveryLine()
    {
        var bytes = Narrow()
            .Text("one", bold: true)
            .Text("two", bold: true)
            .Text("three", bold: true)
            .ToEscPos();

        Assert.Equal(1, CountSequence(bytes, EscPos.Bold(true)));
    }

    [Fact]
    public void EmphasisIsClearedBeforeTheCut()
    {
        var bytes = Narrow().Text("bold", bold: true).Cut().ToEscPos();

        var boldOff = IndexOfSequence(bytes, EscPos.Bold(false));
        var cut = IndexOfSequence(bytes, [0x1D, 0x56]);

        Assert.True(boldOff >= 0, "Bold was never turned off.");
        Assert.True(boldOff < cut, "Bold must be cleared before the cut so the next receipt starts clean.");
    }

    [Fact]
    public void TheDrawerPulseRidesInTheSameJob()
    {
        var bytes = Narrow().Text("receipt").KickDrawer().Cut().ToEscPos();

        Assert.True(IndexOfSequence(bytes, EscPos.KickDrawer()) > 0);
    }

    /// <summary>The kick is not paper, so it leaves no mark on the text preview.</summary>
    [Fact]
    public void TheDrawerPulseDoesNotAppearInThePreview()
    {
        Assert.Equal(
            Narrow().Text("receipt").ToPlainText(),
            Narrow().Text("receipt").KickDrawer().ToPlainText());
    }

    [Fact]
    public void PaperTooNarrowToHoldALineAndAPriceIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReceiptBuilder(8));
    }

    private static int IndexOfSequence(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var match = true;

            for (var j = 0; j < needle.Length && match; j++)
                match = haystack[i + j] == needle[j];

            if (match)
                return i;
        }

        return -1;
    }

    private static int CountSequence(byte[] haystack, byte[] needle)
    {
        var count = 0;

        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var match = true;

            for (var j = 0; j < needle.Length && match; j++)
                match = haystack[i + j] == needle[j];

            if (match)
                count++;
        }

        return count;
    }
}
