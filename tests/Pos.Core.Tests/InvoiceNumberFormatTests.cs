using Pos.Core.Domain;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// The shape of an invoice number, and the year it is filed under.
/// </summary>
/// <remarks>
/// Both matter more than they look. A number cannot be changed once a bill has been handed over, so
/// the format has to be right before the shop's first sale, and the financial year is what a GST
/// return is filed against — a sequence restarting on 1 January restarts in the middle of it.
/// </remarks>
public class InvoiceNumberFormatTests
{
    // ---- The financial year --------------------------------------------------------------------

    [Theory]
    [InlineData(2026, 4, 1, 2026, "26-27")]   // the day it opens
    [InlineData(2026, 8, 21, 2026, "26-27")]
    [InlineData(2026, 12, 31, 2026, "26-27")]
    [InlineData(2027, 1, 1, 2026, "26-27")]   // January is still the year that opened in April
    [InlineData(2027, 3, 31, 2026, "26-27")]  // the last day of it
    [InlineData(2027, 4, 1, 2027, "27-28")]   // the next morning
    [InlineData(1999, 5, 5, 1999, "99-00")]   // the label wraps at the century
    [InlineData(2000, 2, 2, 1999, "99-00")]
    public void TheYearRunsFromAprilToMarch(int year, int month, int day, int expectedStart, string expectedLabel)
    {
        var moment = new DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.FromHours(5.5));
        var fiscal = FiscalYear.For(moment);

        Assert.Equal(expectedStart, fiscal.StartYear);
        Assert.Equal(expectedStart + 1, fiscal.EndYear);
        Assert.Equal(expectedLabel, fiscal.ShortLabel);
        Assert.Equal($"{expectedStart}-{expectedStart + 1}", fiscal.LongLabel);
    }

    /// <summary>
    /// The offset is part of the instant. A bill rung up just after midnight on 1 April in India is
    /// an April bill, and it belongs to the financial year that opens that morning — even though
    /// the same instant is still 31 March in UTC.
    /// </summary>
    [Fact]
    public void TheYearIsReadFromTheLocalMomentNotFromUtc()
    {
        var justAfterMidnight = new DateTimeOffset(2027, 4, 1, 0, 30, 0, TimeSpan.FromHours(5.5));

        Assert.Equal(3, justAfterMidnight.ToUniversalTime().Month);
        Assert.Equal(2027, FiscalYear.For(justAfterMidnight).StartYear);
        Assert.Equal("27-28", FiscalYear.For(justAfterMidnight).ShortLabel);
    }

    // ---- The number --------------------------------------------------------------------------

    [Fact]
    public void TheDefaultCarriesTheLaneSoTwoTillsCannotCollide()
    {
        var format = InvoiceNumberFormat.Default;

        Assert.True(format.IncludeLaneSegment);
        Assert.Equal("INV/26-27/L1-1", format.Format("L1", new FiscalYear(2026), 1));
        Assert.Equal("INV/26-27/L2-1", format.Format("L2", new FiscalYear(2026), 1));
    }

    [Fact]
    public void AOneTillShopCanDropTheLaneSegment()
    {
        var format = new InvoiceNumberFormat { StorePrefix = "RM", IncludeLaneSegment = false };

        Assert.Equal("RM/26-27/11358", format.Format("L1", new FiscalYear(2026), 11358));
    }

    [Theory]
    [InlineData(0, "RM/26-27/7")]
    [InlineData(1, "RM/26-27/7")]
    [InlineData(4, "RM/26-27/0007")]
    [InlineData(6, "RM/26-27/000007")]
    public void TheSequenceIsPaddedToWhateverWidthTheShopWants(int padding, string expected)
    {
        var format = new InvoiceNumberFormat
        {
            StorePrefix = "RM",
            IncludeLaneSegment = false,
            SequencePadding = padding,
        };

        Assert.Equal(expected, format.Format("L1", new FiscalYear(2026), 7));
    }

    [Fact]
    public void ALongSequenceIsNeverTruncatedByItsPadding()
    {
        var format = new InvoiceNumberFormat { StorePrefix = "RM", IncludeLaneSegment = false, SequencePadding = 3 };

        Assert.Equal("RM/26-27/123456", format.Format("L1", new FiscalYear(2026), 123456));
    }

    // ---- What is refused -----------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("RM/2")]
    [InlineData("RM 2")]
    [InlineData("RM\\2")]
    public void APrefixThatWouldMakeTheNumberAmbiguousIsRefused(string prefix)
    {
        var format = new InvoiceNumberFormat { StorePrefix = prefix };

        Assert.Throws<ArgumentException>(format.Validate);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(13)]
    public void AnUnworkablePaddingIsRefused(int padding)
    {
        var format = new InvoiceNumberFormat { SequencePadding = padding };

        Assert.Throws<ArgumentOutOfRangeException>(format.Validate);
    }

    [Fact]
    public void AReasonablePrefixIsAccepted()
    {
        foreach (var prefix in new[] { "RM", "INV", "SLS-A", "ரவி" })
            new InvoiceNumberFormat { StorePrefix = prefix }.Validate();
    }
}
