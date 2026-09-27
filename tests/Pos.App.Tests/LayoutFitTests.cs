using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pos.App.Input;
using Pos.App.ViewModels;
using Pos.App.Views;
using Pos.Core.Configuration;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Domain.Catalogue;
using Pos.Core.Domain.Printing;
using Pos.Core.Hardware.Printing;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// Everything fits, and everything can be reached, on the screen a shop actually has.
/// </summary>
/// <remarks>
/// <para>
/// 1366x768 is not an arbitrary size. It is the panel on a cheap till, and it is where this has
/// gone wrong twice: a fixed-width box pushed the catalogue's Browse button off the right-hand edge
/// on exactly this screen, and a button clipped at the foot of a card was found by looking at a
/// screenshot rather than by any test.
/// </para>
/// <para>
/// A control that has scrolled below the fold is still reachable. A control pushed off the side is
/// not — nothing horizontal scrolls on these screens by design — so that is what these assert. The
/// check is deliberately narrow: a blanket "nothing anywhere is clipped" over a whole visual tree
/// fires on every deliberately trimmed label, and a noisy test is one somebody turns off.
/// </para>
/// </remarks>
public class LayoutFitTests : IDisposable
{
    /// <summary>The panel on a cheap till, and the smallest screen this has to work on.</summary>
    private const double TillWidth = 1366;

    private const double TillHeight = 768;

    /// <summary>A pixel of slack, for a layout that rounds against itself.</summary>
    private const double Slack = 1.0;

    private readonly TempDatabase _temp = new();

    public void Dispose() => _temp.Dispose();

    private static PosSettings Settings() => new()
    {
        LaneId = "L1",
        OutletStateCode = "33",
        Store = { Name = "Sri Lakshmi Stores", Gstin = "33AABCS1429B1ZX" },
    };

    // ---- What "fits" means -----------------------------------------------------------------------

    /// <summary>
    /// Every button is inside the window, the whole way across.
    /// </summary>
    /// <remarks>
    /// Buttons rather than everything on the screen: a button that cannot be reached is a feature
    /// that cannot be used, which is the fault worth failing a build over. A label half off the edge
    /// is untidy; a Browse button half off the edge means nobody can load a catalogue.
    /// </remarks>
    private static void AssertEveryButtonIsReachable(Window window, string where)
    {
        var offscreen = new List<string>();
        var invisible = new List<string>();

        foreach (var button in Wpf.Descendants<Button>(window))
        {
            if (!button.IsVisible || button.ActualWidth == 0)
                continue;

            var label = Describe(button);

            if (button.ActualWidth < 1 || button.ActualHeight < 1)
            {
                invisible.Add(label);
                continue;
            }

            var origin = button.TransformToAncestor(window).Transform(new Point(0, 0));
            var right = origin.X + button.ActualWidth;

            if (origin.X < -Slack || right > window.ActualWidth + Slack)
            {
                offscreen.Add(
                    $"{label} spans {origin.X:N0} to {right:N0}, outside a window {window.ActualWidth:N0} wide");
            }
        }

        Assert.True(offscreen.Count == 0,
            $"On {where} at {TillWidth}x{TillHeight}, these buttons are off the side of the window "
            + $"and cannot be clicked:\n  {string.Join("\n  ", offscreen)}");

        Assert.True(invisible.Count == 0,
            $"On {where}, these buttons laid out with no size:\n  {string.Join("\n  ", invisible)}");
    }

    /// <summary>
    /// No button label is cut off by the button it sits on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The other half of the same fault: a control given a fixed width does not push its neighbours
    /// along, it clips its own content, so every neighbour stays perfectly in bounds while the label
    /// reads "Brows".
    /// </para>
    /// <para>
    /// Measured against the text itself rather than against <c>DesiredSize</c>. Measure clamps
    /// DesiredSize to the width that was on offer, so a squeezed control reports wanting exactly
    /// what it was given and a check written that way passes on a screen that is visibly wrong —
    /// which is what the first draft of this did. Laying the string out with the button's own
    /// typeface and comparing that is the only figure that answers the question.
    /// </para>
    /// <para>
    /// Buttons only. A great many labels elsewhere are trimmed deliberately, and an item name ending
    /// in an ellipsis is the design working rather than failing.
    /// </para>
    /// </remarks>
    private static void AssertNoButtonLabelIsCutOff(Window window, string where)
    {
        var clipped = new List<string>();

        foreach (var button in Wpf.Descendants<Button>(window))
        {
            if (!button.IsVisible || button.ActualWidth < 1)
                continue;

            foreach (var label in Wpf.Descendants<TextBlock>(button))
            {
                if (!label.IsVisible || string.IsNullOrWhiteSpace(label.Text))
                    continue;

                // Where the label actually sits inside its button. Not the label's own ActualWidth:
                // a text block handed more room than its parent has keeps its full width and is
                // clipped by the border painting over it, so it reports itself perfectly happy
                // while reading "Brows". Its position relative to the button is what shows that.
                var left = label.TransformToAncestor(button).Transform(new Point(0, 0)).X;
                var right = left + label.ActualWidth;

                if (left < -Slack || right > button.ActualWidth + Slack)
                {
                    clipped.Add(
                        $"\"{label.Text}\" spans {left:N0} to {right:N0} inside a button only "
                        + $"{button.ActualWidth:N0}px wide");
                }
            }
        }

        Assert.True(clipped.Count == 0,
            $"On {where} at {TillWidth}x{TillHeight}, these button labels are cut off:\n  "
            + string.Join("\n  ", clipped));
    }

    /// <summary>Nothing needs sideways scrolling, because nothing on these screens scrolls sideways.</summary>
    private static void AssertNothingOverflowsSideways(Window window, string where)
    {
        var overflowing = new List<string>();

        foreach (var scroller in Wpf.Descendants<ScrollViewer>(window))
        {
            if (!scroller.IsVisible || scroller.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled)
                continue;

            if (scroller.ExtentWidth > scroller.ViewportWidth + Slack)
            {
                overflowing.Add(
                    $"a scroll area holding {scroller.ExtentWidth:N0}px of content in {scroller.ViewportWidth:N0}px");
            }
        }

        Assert.True(overflowing.Count == 0,
            $"On {where} at {TillWidth}x{TillHeight}, content runs off the side:\n  "
            + string.Join("\n  ", overflowing));
    }

    private static string Describe(Button button) =>
        button.Content as string
        ?? button.Name
        ?? (button.Content?.ToString() is { Length: > 0 } text ? text : "an unnamed button");

    // ---- The billing screen ----------------------------------------------------------------------

    [Fact]
    public void TheBillingScreenFitsATillPanel()
    {
        using var harness = new BillingHarness(
            Catalogue.Item(sku: "DAL001", barcode: "8901234567890", name: "Toor Dal 1kg", price: 189m));

        Wpf.Run(() =>
        {
            var window = new MainBillingView(harness.ViewModel, Keymap.Default, Settings());

            try
            {
                Wpf.LayOut(window, TillWidth, TillHeight);

                AssertEveryButtonIsReachable(window, "the billing screen");
                AssertNoButtonLabelIsCutOff(window, "the billing screen");
                AssertNothingOverflowsSideways(window, "the billing screen");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// With a bill on it. An empty grid fits anywhere; the columns only reach their real widths once
    /// there are figures in them, and a long product name is what pushes the Total column off.
    /// </summary>
    [Fact]
    public void TheBillingScreenStillFitsWithALongNameOnTheBill()
    {
        using var harness = new BillingHarness(
            Catalogue.Item(
                sku: "OIL001",
                barcode: "8901234567913",
                name: "Premium Organic Cold Pressed Groundnut Oil 5 Litre Tin",
                price: 1299m));

        harness.ViewModel.SearchText = "8901234567913";

        Wpf.Run(() =>
        {
            var window = new MainBillingView(harness.ViewModel, Keymap.Default, Settings());

            try
            {
                Wpf.LayOut(window, TillWidth, TillHeight);

                AssertEveryButtonIsReachable(window, "the billing screen with a long item name");
                AssertNoButtonLabelIsCutOff(window, "the billing screen with a long item name");
                AssertNothingOverflowsSideways(window, "the billing screen with a long item name");
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ---- The owner's screen ----------------------------------------------------------------------

    /// <summary>
    /// Every tab, not just the one that opens.
    /// </summary>
    /// <remarks>
    /// A TabControl builds a tab's visual tree the first time it is shown, so checking only the tab
    /// the window opens on would report that the other five fit perfectly while being empty. Each is
    /// selected in turn and laid out for real. This is the check that would have caught the Browse
    /// button, which was on the third tab.
    /// </remarks>
    [Fact]
    public void EveryTabOfTheOwnersScreenFitsATillPanel()
    {
        Wpf.Run(() =>
        {
            var window = BuildOwnerView();

            try
            {
                Wpf.LayOut(window, TillWidth, TillHeight);

                var tabs = Wpf.Descendants<TabControl>(window).First();

                for (var i = 0; i < tabs.Items.Count; i++)
                {
                    tabs.SelectedIndex = i;
                    window.UpdateLayout();

                    var name = (tabs.Items[i] as TabItem)?.Header?.ToString() ?? $"tab {i + 1}";

                    AssertEveryButtonIsReachable(window, $"the owner's screen, {name}");
                    AssertNoButtonLabelIsCutOff(window, $"the owner's screen, {name}");
                    AssertNothingOverflowsSideways(window, $"the owner's screen, {name}");
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void TheOwnersScreenOpensOnSixTabs()
    {
        Wpf.Run(() =>
        {
            var window = BuildOwnerView();

            try
            {
                Wpf.LayOut(window, TillWidth, TillHeight);

                var tabs = Wpf.Descendants<TabControl>(window).First();

                Assert.Equal(6, tabs.Items.Count);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private OwnerView BuildOwnerView()
    {
        var settings = Settings();
        var items = new ItemRepository(_temp.Database);
        var stock = new StockRepository(_temp.Database);
        var held = new HeldBillRepository(_temp.Database);

        var owner = new OwnerViewModel(
            settings.LaneId,
            _ => new Pos.Core.Analytics.DashboardQuery(_temp.Database).Gather(
                settings.LaneId, DateTimeOffset.Now.AddDays(-29), DateTimeOffset.Now),
            stock,
            TaxMode.Gst,
            isPinSet: false,
            applyTaxMode: _ => null,
            applyPin: _ => null,
            saveWebPage: (_, _) => null);

        var maintenance = new MaintenanceViewModel(
            _temp.Database,
            Path.GetDirectoryName(_temp.Database.DatabasePath)!,
            new DayCloseRepository(_temp.Database, held),
            new ZReportComposer(BillingHarness.Store, 48, ReceiptLanguage.English, TaxMode.Gst),
            settings.LaneId,
            print: _ => PrintOutcome.NotConfigured(),
            post: action => action());

        return new OwnerView(
            owner,
            new CatalogueImportViewModel(items),
            new HardwareViewModel(settings, rasterizer: null, confirm: _ => true, post: action => action()),
            new NewItemViewModel(items, new HsnSuggester(query => items.Search(query))),
            maintenance);
    }
}
