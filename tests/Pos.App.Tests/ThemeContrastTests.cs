using System.Globalization;
using System.IO;
using System.Xml.Linq;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// WCAG relative luminance and contrast, which is the only objective answer to "can somebody read
/// this".
/// </summary>
/// <remarks>
/// The formulae are from WCAG 2.1. It is a web standard, and a till is not a web page — but the
/// question it answers is exactly the one that matters at a counter: whether text stays legible on
/// a cheap panel under overhead lighting, read by somebody who is not leaning in.
/// </remarks>
internal static class Wcag
{
    /// <summary>Normal-size text. Everything in the line grid is 12-14px, so this is the bar.</summary>
    public const double NormalText = 4.5;

    /// <summary>Text at 18.66px bold or 24px plain. Nothing in the grid qualifies.</summary>
    public const double LargeText = 3.0;

    /// <summary>Relative luminance of an #AARRGGBB or #RRGGBB colour.</summary>
    public static double Luminance(string hex)
    {
        var h = hex.TrimStart('#');

        if (h.Length == 8)
            h = h[2..];

        double Channel(int offset)
        {
            var v = int.Parse(h.Substring(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(0) + 0.7152 * Channel(2) + 0.0722 * Channel(4);
    }

    /// <summary>Contrast between two colours, from 1 (identical) to 21 (black on white).</summary>
    public static double Ratio(string foreground, string background)
    {
        var a = Luminance(foreground);
        var b = Luminance(background);
        var ratio = (a + 0.05) / (b + 0.05);

        return ratio < 1 ? 1 / ratio : ratio;
    }
}

/// <summary>
/// Every foreground the till draws, against every background it draws it on.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of a fault no other test in this repository could have caught. Selecting a
/// line in the bill handed the cell WPF's theme highlight-text colour, which dimmed the item name,
/// the quantity and the rate to near-invisible — precisely the three things a cashier reads off a
/// line while a customer waits. Nothing failed, nothing threw, every figure was correct. It was
/// found by looking at a screenshot, which is not a way to find things reliably.
/// </para>
/// <para>
/// Colour is not a matter of taste once it stops being readable, and a ratio is checkable in
/// milliseconds with no window, no dispatcher and no screenshot to go stale. The palette is read
/// from the real <c>Theme.xaml</c> rather than restated here, so changing a brush is answerable by
/// these tests rather than by somebody noticing later.
/// </para>
/// </remarks>
public class ThemeContrastTests
{
    private static readonly Dictionary<string, string> Palette = LoadPalette();

    /// <summary>The backgrounds a row of the bill grid can be drawn on.</summary>
    /// <remarks>
    /// All three, every time. The fault this suite was written for only appeared on
    /// <c>RowActive</c>, so a check that tried the ordinary row background and stopped would have
    /// passed while the screen was unreadable.
    /// </remarks>
    public static TheoryData<string> GridBackgrounds() => ["SurfaceRaised", "RowAlt", "RowActive"];

    private static Dictionary<string, string> LoadPalette()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Theme.xaml");

        Assert.True(File.Exists(path),
            $"Theme.xaml was not copied beside the tests (looked in {AppContext.BaseDirectory}). "
            + "The palette is read from the real theme rather than restated here.");

        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        return XDocument.Load(path)
            .Descendants(presentation + "SolidColorBrush")
            .Where(b => b.Attribute(xaml + "Key") is not null && b.Attribute("Color") is not null)
            .ToDictionary(
                b => b.Attribute(xaml + "Key")!.Value,
                b => b.Attribute("Color")!.Value,
                StringComparer.Ordinal);
    }

    private static string Colour(string key)
    {
        Assert.True(Palette.ContainsKey(key), $"Theme.xaml has no brush called '{key}'.");
        return Palette[key];
    }

    private static void AssertReadable(string foreground, string background, double bar = Wcag.NormalText)
    {
        var ratio = Wcag.Ratio(Colour(foreground), Colour(background));

        Assert.True(ratio >= bar,
            $"{foreground} on {background} is {ratio:N2}:1, below the {bar:N1}:1 that text this size "
            + $"needs to stay readable. {foreground} is {Colour(foreground)}, {background} is "
            + $"{Colour(background)}.");
    }

    // ---- The palette is what the tests think it is -----------------------------------------------

    [Fact]
    public void TheThemeDefinesEveryColourTheseTestsCheck()
    {
        foreach (var key in new[]
                 {
                     "Surface", "SurfaceRaised", "SurfaceHeader", "Ink", "InkMuted", "InkDim",
                     "Accent", "Positive", "Negative", "Line", "RowActive", "RowAlt",
                 })
        {
            Assert.True(Palette.ContainsKey(key), $"Theme.xaml no longer defines '{key}'.");
        }
    }

    // ---- The bill grid ---------------------------------------------------------------------------

    /// <summary>
    /// The item name, the quantity and the rate. These take their colour by inheritance rather than
    /// from an ElementStyle, which is exactly why selecting a row was able to dim them.
    /// </summary>
    [Theory]
    [MemberData(nameof(GridBackgrounds))]
    public void TheFiguresACashierReadsAreLegibleOnEveryRow(string background) =>
        AssertReadable("Ink", background);

    /// <summary>The HSN code, the barcode and the unit — quiet on purpose, readable all the same.</summary>
    [Theory]
    [MemberData(nameof(GridBackgrounds))]
    public void TheCodesAreQuietWithoutBeingUnreadable(string background) =>
        AssertReadable("InkMuted", background);

    /// <summary>
    /// The line number and the MRP.
    /// </summary>
    /// <remarks>
    /// The quietest ink in the theme, and the one that failed this bar. It was #FF64748B, which
    /// measured 3.15:1 on a selected row. That would be arguable for a line number and is not for
    /// the MRP beside it: a printed price a customer may be querying is not decoration.
    /// </remarks>
    [Theory]
    [MemberData(nameof(GridBackgrounds))]
    public void ThePrintedPriceIsReadableEvenThoughItIsQuiet(string background) =>
        AssertReadable("InkDim", background);

    /// <summary>What the line comes to — the figure a customer follows down the screen.</summary>
    [Theory]
    [MemberData(nameof(GridBackgrounds))]
    public void TheLineTotalIsLegible(string background) =>
        AssertReadable("Positive", background);

    /// <summary>
    /// Money off.
    /// </summary>
    /// <remarks>
    /// This measured 4.09:1 on a selected row, so the one figure saying a customer is being charged
    /// less than the shelf price was hardest to read on the row the cashier had selected.
    /// </remarks>
    [Theory]
    [MemberData(nameof(GridBackgrounds))]
    public void MoneyOffIsLegible(string background) =>
        AssertReadable("Negative", background);

    // ---- The rest of the window ------------------------------------------------------------------

    [Fact]
    public void HeadingsAreLegibleOnTheHeaderBar() =>
        AssertReadable("Accent", "SurfaceHeader");

    [Fact]
    public void TheOrdinaryInkIsLegibleOnThePageItself() =>
        AssertReadable("Ink", "Surface");

    [Fact]
    public void LabelsAreLegibleOnThePageItself() =>
        AssertReadable("InkMuted", "Surface");

    // ---- The colours have to stay distinguishable from each other --------------------------------

    /// <summary>
    /// Money taken and money off are never the same colour, and not merely different in name.
    /// </summary>
    /// <remarks>
    /// Red and green at similar luminance are the commonest pair to be confused, and roughly one man
    /// in twelve has some difficulty telling them apart. They carry opposite meanings on a bill, so
    /// they are kept apart by lightness as well as by hue — somebody who cannot see the hue can
    /// still see which figure is which.
    /// </remarks>
    [Fact]
    public void MoneyTakenAndMoneyOffAreNotTheSameLightness()
    {
        var ratio = Wcag.Ratio(Colour("Positive"), Colour("Negative"));

        Assert.True(ratio >= 1.2,
            $"Positive and Negative are within {ratio:N2}:1 of each other in lightness. They mean "
            + "opposite things on a bill and must not rely on hue alone to be told apart.");
    }

    /// <summary>The three inks are three steps, not two steps and a rounding error.</summary>
    [Fact]
    public void TheThreeInksAreStillTellableApart()
    {
        var ink = Wcag.Luminance(Colour("Ink"));
        var muted = Wcag.Luminance(Colour("InkMuted"));
        var dim = Wcag.Luminance(Colour("InkDim"));

        Assert.True(ink > muted, "Ink must be lighter than InkMuted.");
        Assert.True(muted > dim, "InkMuted must be lighter than InkDim.");

        // Raising InkDim to meet the readability bar narrowed this gap on purpose. It is checked so
        // that the next raise is a decision rather than an accident that quietly merges the two.
        Assert.True(muted / dim >= 1.15,
            $"InkMuted and InkDim are within {muted / dim:N2}x of each other. Two inks that read as "
            + "one are one ink with extra steps.");
    }

    // ---- A record of the figures, so a change shows up as a change -------------------------------

    /// <summary>
    /// The whole grid, measured. Not an assertion about any one pair — those are above — but a
    /// printed table, so a change to the palette shows up in the test output with its numbers.
    /// </summary>
    [Fact]
    public void EveryGridPairingClearsTheBar()
    {
        var failures = new List<string>();

        foreach (var foreground in new[] { "Ink", "InkMuted", "InkDim", "Positive", "Negative" })
        {
            foreach (var background in new[] { "SurfaceRaised", "RowAlt", "RowActive" })
            {
                var ratio = Wcag.Ratio(Colour(foreground), Colour(background));

                if (ratio < Wcag.NormalText)
                    failures.Add($"{foreground} on {background}: {ratio:N2}:1");
            }
        }

        Assert.True(failures.Count == 0,
            "These pairings are below 4.5:1 and would be hard to read at a counter:\n  "
            + string.Join("\n  ", failures));
    }
}
