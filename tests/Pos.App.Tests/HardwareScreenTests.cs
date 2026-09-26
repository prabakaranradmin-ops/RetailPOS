using System.IO;
using Pos.App.ViewModels;
using Pos.Core.Configuration;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// The peripheral checks and the bill preview as the owner's screen drives them.
/// </summary>
/// <remarks>
/// The checks themselves belong to <see cref="PeripheralCheck"/> and are shared with the <c>pos</c>
/// tool. What is tested here is the part the window owns: that a lane with nothing plugged in says
/// so instead of failing, that a check cannot be started twice at once, that a bill can be composed
/// with no hardware present at all, and that nothing a peripheral does is allowed to throw out into
/// a till that has to carry on selling.
///
/// Nothing here touches a real device. A lane with no printer, drawer or scale configured is the
/// honest way to exercise these paths on a build machine, and it is also a real lane — a counter
/// being set up before its hardware arrives.
/// </remarks>
public class HardwareScreenTests
{
    private static PosSettings BareLane() => new()
    {
        LaneId = "L1",
        OutletStateCode = "33",
    };

    private static HardwareViewModel Screen(
        PosSettings? settings = null,
        Func<string, bool>? confirm = null) =>
        new(
            settings ?? BareLane(),
            rasterizer: null,

            // Nothing should reach this on a lane with no hardware. If something does, saying yes
            // would quietly pass a check nobody answered.
            confirm ?? (_ => throw new InvalidOperationException("The operator was asked about hardware that is not configured.")),

            // The dispatcher's job, done inline: the tests have no UI thread to marshal onto.
            post: action => action());

    // ---- A lane with nothing plugged in ----------------------------------------------------------

    /// <summary>
    /// Not configured is not the same as broken, and a counter waiting for its printer to arrive
    /// must not be told its lane has failed.
    /// </summary>
    [Fact]
    public async Task APeripheralThatIsNotSetUpIsReportedAsSuchRatherThanAsAFailure()
    {
        var screen = Screen();

        await screen.CheckPrinter();

        Assert.Contains("nothing set up", screen.Summary);
        Assert.DoesNotContain("FAILED", screen.Summary);
        Assert.Contains(screen.Log, line => line.Contains("No printer is set up"));
    }

    [Fact]
    public async Task AndTheSameForTheDrawerAndTheScale()
    {
        var screen = Screen();

        await screen.CheckDrawer();
        Assert.Contains("nothing set up", screen.Summary);

        await screen.CheckScale();
        Assert.Contains("nothing set up", screen.Summary);
    }

    /// <summary>
    /// Listing ports is neither a pass nor a fail — it is what you look at when a port is wrong,
    /// and a machine with no serial ports is an ordinary laptop rather than a fault.
    /// </summary>
    [Fact]
    public async Task ListingPortsReportsWhatIsThereWithoutJudgingIt()
    {
        var screen = Screen();

        await screen.ListPorts();

        Assert.Contains("Serial ports", screen.Log);
        Assert.DoesNotContain("FAILED", screen.Summary);
        Assert.DoesNotContain("passed", screen.Summary);
    }

    [Fact]
    public async Task EachRunStartsFromAnEmptyLogRatherThanStackingOnTheLastOne()
    {
        var screen = Screen();

        await screen.CheckPrinter();
        var first = screen.Log.Count;

        await screen.CheckPrinter();

        Assert.Equal(first, screen.Log.Count);
    }

    // ---- The bill, with no hardware at all -------------------------------------------------------

    /// <summary>
    /// The whole point of the preview: a shopkeeper checks their bill layout before the shop opens,
    /// on a lane whose printer may not even have been delivered.
    /// </summary>
    [Fact]
    public void TheBillCanBeComposedWithNoPrinterPresent()
    {
        var settings = BareLane();
        settings.Store.Name = "Ravi Stores";
        settings.Store.Gstin = "33AEIPH7795F1Z9";

        var screen = Screen(settings);
        screen.ShowPreview();

        Assert.Contains("Ravi Stores", screen.PreviewText);
        Assert.Contains("33AEIPH7795F1Z9", screen.PreviewText);

        // The sample exercises the layout rather than the easy parts of it: several slabs, a
        // discount, a weighed line and a split tender.
        Assert.Contains("Toor Dal 1kg", screen.PreviewText);
        Assert.Contains("Sugar Loose", screen.PreviewText);
    }

    /// <summary>
    /// Narrow paper is a different layout, not the same one clipped, and the whole reason to
    /// preview against a width the lane does not have is to see that before buying the printer.
    /// </summary>
    [Fact]
    public void ItCanBePreviewedAgainstAPaperWidthTheLaneDoesNotHave()
    {
        var screen = Screen();

        screen.ShowPreview(48);
        var wide = screen.PreviewText;

        screen.ShowPreview(32);
        var narrow = screen.PreviewText;

        Assert.NotEqual(wide, narrow);
        Assert.All(narrow.Split('\n'), line => Assert.True(line.TrimEnd().Length <= 32, $"'{line}' is wider than 32 characters."));
    }

    /// <summary>
    /// A lane that rounds must preview the bill it will actually issue, round-off line and all.
    /// Previewing the unrounded one would show the shopkeeper a document they never hand over.
    /// </summary>
    [Fact]
    public void ARoundingLanePreviewsTheRoundOff()
    {
        var rounding = BareLane();
        rounding.RoundOffToRupee = true;

        var plain = BareLane();
        plain.RoundOffToRupee = false;

        var withRounding = Screen(rounding);
        withRounding.ShowPreview();

        var without = Screen(plain);
        without.ShowPreview();

        Assert.Contains("Round off", withRounding.PreviewText);
        Assert.DoesNotContain("Round off", without.PreviewText);
    }

    /// <summary>
    /// Drawing needs a text renderer. A lane without one is told so plainly rather than being left
    /// with a button that appears to do nothing.
    /// </summary>
    [Fact]
    public void DrawingTheBillWithNoRendererSaysSoRatherThanFailingSilently()
    {
        var screen = Screen();

        screen.RenderPreviewImage(Path.Combine(Path.GetTempPath(), "pos-tests"));

        Assert.Null(screen.PreviewImagePath);
        Assert.False(screen.ShowsPreviewImage);
        Assert.Contains("no text renderer", screen.Summary);
    }

    // ---- Which pane is showing -------------------------------------------------------------------

    /// <summary>
    /// One at a time. The log, the composed bill and the drawn dots answer different questions, and
    /// stacking them leaves the operator reading one under another.
    /// </summary>
    [Fact]
    public async Task ThePanelShowsOneThingAtATime()
    {
        var screen = Screen();

        Assert.True(screen.ShowsLog);

        screen.ShowPreview();

        Assert.True(screen.ShowsPreviewText);
        Assert.False(screen.ShowsLog);

        await screen.ListPorts();

        Assert.True(screen.ShowsLog);
        Assert.False(screen.ShowsPreviewText);
    }

    // ---- Nothing may take the till down ----------------------------------------------------------

    /// <summary>
    /// A wedge scanner types into whatever has focus, so with an empty box there is nothing to
    /// judge. That is a prompt, not a failure.
    /// </summary>
    [Fact]
    public async Task AScannerCheckWithNothingScannedAsksForAScanRatherThanFailing()
    {
        var screen = Screen();
        screen.ScannedCode = string.Empty;

        await screen.CheckScanner();

        Assert.DoesNotContain("FAILED", screen.Summary);
    }

    [Fact]
    public async Task AChecksOwnFaultBecomesALineRatherThanAnException()
    {
        // A lane pointed at a serial port that is not there: opening it throws inside the check,
        // and the screen has to survive that because the counter behind it is still selling.
        var settings = BareLane();
        settings.Hardware.ScalePort = "COM199";

        var screen = Screen(settings);

        await screen.CheckScale();

        Assert.Contains("FAILED", screen.Summary);
        Assert.Contains(screen.Log, line => line.Contains("FAILED"));
    }
}
