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
/// from the real file in <c>Themes\</c> rather than restated here, so changing a brush is answerable
/// by these tests rather than by somebody noticing later.
/// </para>
/// <para>
/// Every look is held to every check: the classes at the foot of this file run the whole suite once
/// per palette. A light look is not excused a bar because it is light, nor a dark one because it was
/// here first.
/// </para>
/// </remarks>
public abstract class ThemeContrastTests(string look)
{
    private static readonly Dictionary<string, XDocument> Documents = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Dictionary<string, string>> Palettes = new(StringComparer.Ordinal);

    private Dictionary<string, string> Palette => PaletteOf(look);

    /// <summary>The backgrounds a row of the bill grid can be drawn on.</summary>
    /// <remarks>
    /// All three, every time. The fault this suite was written for only appeared on
    /// <c>RowActive</c>, so a check that tried the ordinary row background and stopped would have
    /// passed while the screen was unreadable.
    /// </remarks>
    public static TheoryData<string> GridBackgrounds() => ["SurfaceRaised", "RowAlt", "RowActive"];

    internal static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    internal static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>A look's palette file, as copied beside the tests, read once.</summary>
    internal static XDocument DocumentOf(string look)
    {
        lock (Documents)
        {
            if (Documents.TryGetValue(look, out var known))
                return known;

            var path = Path.Combine(AppContext.BaseDirectory, "Themes", look + ".xaml");

            Assert.True(File.Exists(path),
                $"Themes\\{look}.xaml was not copied beside the tests (looked in {AppContext.BaseDirectory}). "
                + "The palette is read from the real file rather than restated here.");

            return Documents[look] = XDocument.Load(path);
        }
    }

    /// <summary>Every plain colour a look defines, by name.</summary>
    internal static Dictionary<string, string> PaletteOf(string look)
    {
        lock (Palettes)
        {
            if (Palettes.TryGetValue(look, out var known))
                return known;

            return Palettes[look] = DocumentOf(look)
                .Descendants(Presentation + "SolidColorBrush")
                .Where(b => b.Attribute(Xaml + "Key") is not null && b.Attribute("Color") is not null)
                .ToDictionary(
                    b => b.Attribute(Xaml + "Key")!.Value,
                    b => b.Attribute("Color")!.Value,
                    StringComparer.Ordinal);
        }
    }

    private string Colour(string key)
    {
        Assert.True(Palette.ContainsKey(key), $"{look}.xaml has no brush called '{key}'.");
        return Palette[key];
    }

    private void AssertReadable(string foreground, string background, double bar = Wcag.NormalText)
    {
        var ratio = Wcag.Ratio(Colour(foreground), Colour(background));

        Assert.True(ratio >= bar,
            $"In the {look} look, {foreground} on {background} is {ratio:N2}:1, below the {bar:N1}:1 that "
            + $"text this size needs to stay readable. {foreground} is {Colour(foreground)}, {background} is "
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
            Assert.True(Palette.ContainsKey(key), $"{look}.xaml no longer defines '{key}'.");
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
    /// <remarks>
    /// Measured as contrast with the page rather than as lightness, so the rule means the same thing
    /// on a light look, where the loudest ink is the darkest, as on a dark one.
    /// </remarks>
    [Fact]
    public void TheThreeInksAreStillTellableApart()
    {
        var ink = Wcag.Ratio(Colour("Ink"), Colour("Surface"));
        var muted = Wcag.Ratio(Colour("InkMuted"), Colour("Surface"));
        var dim = Wcag.Ratio(Colour("InkDim"), Colour("Surface"));

        Assert.True(ink > muted, $"In the {look} look, Ink must stand out from the page more than InkMuted.");
        Assert.True(muted > dim, $"In the {look} look, InkMuted must stand out from the page more than InkDim.");

        // Raising InkDim to meet the readability bar narrowed this gap on purpose. It is checked so
        // that the next raise is a decision rather than an accident that quietly merges the two.
        var a = Wcag.Luminance(Colour("InkMuted"));
        var b = Wcag.Luminance(Colour("InkDim"));
        var apart = Math.Max(a, b) / Math.Min(a, b);

        Assert.True(apart >= 1.15,
            $"In the {look} look, InkMuted and InkDim are within {apart:N2}x of each other. Two inks "
            + "that read as one are one ink with extra steps.");
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

    // ---- The richer look: cards, fields, messages, charts ----------------------------------------

    /// <summary>The colours a gradient brush in the theme runs through, stop by stop.</summary>
    /// <remarks>
    /// A card's fill is a gradient, so text on a card is checked against each end of it: whatever
    /// is readable on both ends is readable everywhere between.
    /// </remarks>
    private IReadOnlyList<string> Stops(string key)
    {
        var brush = DocumentOf(look)
            .Descendants()
            .FirstOrDefault(e => e.Name.LocalName.EndsWith("GradientBrush", StringComparison.Ordinal)
                                 && (string?)e.Attribute(Xaml + "Key") == key);

        Assert.True(brush is not null, $"{look}.xaml has no gradient called '{key}'.");

        return brush!.Descendants(Presentation + "GradientStop").Select(s => s.Attribute("Color")!.Value).ToList();
    }

    private void AssertReadableOnGradient(string foreground, string gradient, double bar = Wcag.NormalText)
    {
        foreach (var stop in Stops(gradient))
        {
            var ratio = Wcag.Ratio(Colour(foreground), stop);

            Assert.True(ratio >= bar,
                $"In the {look} look, {foreground} on {gradient} is {ratio:N2}:1 where the gradient is {stop}, below {bar:N1}:1.");
        }
    }

    /// <summary>
    /// The edge of a box you type in, against everything a box sits on.
    /// </summary>
    /// <remarks>
    /// 3:1 is WCAG's bar for the parts of a control you need to see to use it (1.4.11). The edge
    /// used to be Line, #FF232B38, which is 1.3:1 on the page - an empty amount box in the payment
    /// pane was a dark patch on a dark card, and a cashier had to know where to type.
    /// </remarks>
    [Theory]
    [InlineData("FieldFill")]
    [InlineData("Surface")]
    [InlineData("SurfaceRaised")]
    public void ABoxToTypeInCanBeSeen(string background) =>
        AssertReadable("FieldBorder", background, bar: 3.0);

    [Fact]
    public void ABoxToTypeInCanBeSeenOnACard() =>
        AssertReadableOnGradient("FieldBorder", "CardFill", bar: 3.0);

    /// <summary>Each kind of message, in its own colour on its own tint.</summary>
    [Theory]
    [InlineData("Accent", "InfoSoft")]
    [InlineData("Done", "DoneSoft")]
    [InlineData("Warning", "WarningSoft")]
    [InlineData("Danger", "DangerSoft")]
    public void EveryKindOfMessageIsReadableOnItsBar(string ink, string bar) =>
        AssertReadable(ink, bar);

    /// <summary>
    /// The kinds are told apart by more than hue: "refused" and "done" differ in lightness as well,
    /// for the one cashier in twelve who sees red and green alike. The icon carries the rest.
    /// </summary>
    [Fact]
    public void RefusedAndDoneAreNotTheSameLightness() =>
        Assert.True(Wcag.Ratio(Colour("Done"), Colour("Danger")) >= 1.2,
            $"Done and Danger are {Wcag.Ratio(Colour("Done"), Colour("Danger")):N2}:1 apart in lightness.");

    /// <summary>Money saved, beside the total and down the discount column.</summary>
    [Theory]
    [MemberData(nameof(GridBackgrounds))]
    public void MoneySavedIsLegibleOnEveryRow(string background) =>
        AssertReadable("Saving", background);

    /// <summary>The three inks on a card, which is where most text now sits.</summary>
    [Theory]
    [InlineData("Ink")]
    [InlineData("InkMuted")]
    [InlineData("InkDim")]
    [InlineData("Accent")]
    [InlineData("Positive")]
    [InlineData("Saving")]
    public void TextIsReadableOnACard(string ink) =>
        AssertReadableOnGradient(ink, "CardFill");

    /// <summary>The grand total, in its glowing card.</summary>
    [Fact]
    public void TheTotalIsReadableOnItsCard() =>
        AssertReadableOnGradient("Positive", "TotalFill");

    /// <summary>"Pay &amp; Print": dark ink on the green, the way round that clears the bar.</summary>
    [Fact]
    public void ThePayButtonIsReadable() =>
        AssertReadableOnGradient("OnAccent", "PayFill");

    /// <summary>
    /// Every series colour on a chart, against the card the chart is drawn on.
    /// </summary>
    /// <remarks>
    /// 3:1, the bar for a graphic: a bar, a line or a slice has to be seen against its background
    /// to be read at all, and the same colours colour the legend's text.
    /// </remarks>
    [Theory]
    [InlineData("ChartColour0")]
    [InlineData("ChartColour1")]
    [InlineData("ChartColour2")]
    [InlineData("ChartColour3")]
    [InlineData("ChartColour4")]
    [InlineData("ChartColour5")]
    [InlineData("ChartColour6")]
    public void EverySeriesStandsOutOnItsCard(string series) =>
        AssertReadableOnGradient(series, "CardFill", bar: 3.0);

    /// <summary>The tooltip's figures, on the tooltip.</summary>
    [Theory]
    [InlineData("Ink")]
    [InlineData("InkMuted")]
    public void TheTooltipIsReadable(string ink) =>
        AssertReadable(ink, "ChartTooltip");

    /// <summary>Gridlines are there to be glanced along, not read: seen, but well short of the data.</summary>
    [Fact]
    public void GridlinesStayBehindTheData()
    {
        var grid = Wcag.Ratio(Colour("ChartGrid"), Stops("CardFill")[0]);
        var faintest = new[] { "ChartColour0", "ChartColour1", "ChartColour2", "ChartColour3", "ChartColour4", "ChartColour5", "ChartColour6" }
            .Min(k => Wcag.Ratio(Colour(k), Stops("CardFill")[0]));

        Assert.True(grid < 1.5, $"In the {look} look, gridlines are {grid:N2}:1 on the card - loud enough to compete with the data.");
        Assert.True(faintest > 2 * grid, $"In the {look} look, the faintest series is only {faintest:N2}:1 against gridlines at {grid:N2}:1.");
    }

    /// <summary>The label on the one button a pane or a dialog is for, resting and under the pointer.</summary>
    [Theory]
    [InlineData("Accent")]
    [InlineData("AccentHover")]
    public void ThePrimaryButtonIsReadable(string face) =>
        AssertReadable("OnAccent", face);

    /// <summary>The bars in the owner's ranked lists, on the card they sit on: a graphic, so 3:1.</summary>
    [Fact]
    public void TheRankedBarsShowOnTheirCard()
    {
        foreach (var bar in Stops("RankFill"))
        {
            foreach (var card in Stops("CardFill"))
            {
                var ratio = Wcag.Ratio(bar, card);
                Assert.True(ratio >= 3.0, $"In the {look} look, a ranked bar ({bar}) is {ratio:N2}:1 on the card ({card}).");
            }
        }
    }

    /// <summary>
    /// "Pay &amp; Print" stays the heaviest thing on the till: on a dark look it glows, on a light one
    /// it goes deep, and either way it stands out from the page it sits on.
    /// </summary>
    [Fact]
    public void ThePayButtonStandsOutFromThePage() =>
        Assert.All(Stops("PayFill"), stop =>
        {
            var ratio = Wcag.Ratio(stop, Colour("Surface"));
            Assert.True(ratio >= 3.0, $"In the {look} look, the Pay key is {ratio:N2}:1 against the page where it is {stop}.");
        });
}

/// <summary>The till's own look, dark, for after sunset.</summary>
public sealed class NightContrastTests() : ThemeContrastTests("Night");

/// <summary>Dim slate, for dusk.</summary>
public sealed class EveningContrastTests() : ThemeContrastTests("Evening");

/// <summary>Light, for a shop open to the daylight.</summary>
public sealed class MorningContrastTests() : ThemeContrastTests("Morning");

/// <summary>The brightest, for sunlight on the screen.</summary>
public sealed class NoonContrastTests() : ThemeContrastTests("Noon");
