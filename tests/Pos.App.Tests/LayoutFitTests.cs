using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    /// <summary>
    /// What a maximised window gets of a 768-high screen once the taskbar and the title bar have
    /// theirs. Laid out at this size exactly (<see cref="Wpf.LayOutAt"/>), not at the size of the
    /// screen the tests happen to run on.
    /// </summary>
    private const double TillHeight = 698;

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
            var width = AreaWidth(window);

            if (origin.X < -Slack || right > width + Slack)
            {
                offscreen.Add(
                    $"{label} spans {origin.X:N0} to {right:N0}, outside a window {width:N0} wide");
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

    /// <summary>
    /// No key in the footer is hidden under Pay &amp; Print.
    /// </summary>
    /// <remarks>
    /// The one fault the other checks here could not see: the pills are not buttons, and they did
    /// not run off the side - they ran underneath. Pills and Pay shared one grid cell, so adding F8
    /// pushed "Close day" behind the Pay pill on a 1920 screen, found in an acceptance screenshot.
    /// Overlap is checked with a pixel of slack so two pills that merely touch are not a failure.
    /// </remarks>
    private static void AssertNoKeyIsHiddenUnderPay(Window window, string where)
    {
        var pills = (ItemsControl)window.FindName("KeyPills");
        var pay = (FrameworkElement)window.FindName("PayPill");

        Assert.NotNull(pills);
        Assert.NotNull(pay);

        static Rect Bounds(FrameworkElement element, Visual root) =>
            element.TransformToAncestor(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

        var payBounds = Bounds(pay, window);
        var hidden = new List<string>();

        for (var i = 0; i < pills.Items.Count; i++)
        {
            if (pills.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement pill || !pill.IsVisible)
                continue;

            var bounds = Bounds(pill, window);

            var overlaps = bounds.Right > payBounds.Left + Slack && bounds.Left < payBounds.Right - Slack
                && bounds.Bottom > payBounds.Top + Slack && bounds.Top < payBounds.Bottom - Slack;

            if (overlaps)
                hidden.Add($"{pills.Items[i]} spans {bounds.Left:N0} to {bounds.Right:N0}; Pay starts at {payBounds.Left:N0}");
        }

        Assert.True(hidden.Count == 0,
            $"On {where} at {TillWidth}x{TillHeight}, these keys are hidden under Pay & Print:\n  "
            + string.Join("\n  ", hidden));
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

    /// <summary>
    /// How wide the window's content was laid out: the size <see cref="Wpf.LayOutAt"/> pinned it to,
    /// which on a smaller test screen is wider than the window itself.
    /// </summary>
    private static double AreaWidth(Window window) =>
        window.Content is FrameworkElement root && !double.IsNaN(root.Width)
            ? root.Width + root.Margin.Left + root.Margin.Right
            : window.ActualWidth;

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
                Wpf.LayOutAt(window, TillWidth, TillHeight);

                AssertEveryButtonIsReachable(window, "the billing screen");
                AssertNoButtonLabelIsCutOff(window, "the billing screen");
                AssertNoKeyIsHiddenUnderPay(window, "the billing screen");
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
                Wpf.LayOutAt(window, TillWidth, TillHeight);

                AssertEveryButtonIsReachable(window, "the billing screen with a long item name");
                AssertNoButtonLabelIsCutOff(window, "the billing screen with a long item name");
                AssertNoKeyIsHiddenUnderPay(window, "the billing screen with a long item name");
                AssertNothingOverflowsSideways(window, "the billing screen with a long item name");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// The return pane open over a real bill with a long name on it, part picked and a line marked
    /// damaged - the widest the pane's rows get.
    /// </summary>
    [Fact]
    public void TheReturnPaneFitsWithABillOnIt()
    {
        using var harness = new BillingHarness(
            Catalogue.Item(sku: "OIL001", barcode: "8901234567913", name: "Premium Organic Cold Pressed Groundnut Oil 5 Litre Tin", price: 1299m),
            Catalogue.Item(sku: "DAL001", barcode: "8901234567890", name: "Toor Dal 1kg", price: 189m));

        harness.Scan("8901234567913");
        harness.Scan("8901234567890");
        harness.Press(Key.F12);
        harness.Press(Key.Enter);
        harness.Press(Key.Enter);

        harness.Press(Key.F9);
        harness.Press(Key.Enter);
        harness.ViewModel.EditBuffer = "1d";
        harness.Press(Key.Enter);

        Assert.True(harness.ViewModel.IsReturning);

        Wpf.Run(() =>
        {
            var window = new MainBillingView(harness.ViewModel, Keymap.Default, Settings());

            try
            {
                Wpf.LayOutAt(window, TillWidth, TillHeight);

                AssertNoKeyIsHiddenUnderPay(window, "the billing screen with the return pane open");
                AssertNothingOverflowsSideways(window, "the billing screen with the return pane open");

                var pane = (FrameworkElement)window.FindName("ReturnList");
                Assert.True(pane.IsVisible);
                Assert.True(pane.ActualWidth <= TillWidth);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>All twenty-four quick keys, with names too long for a tile.</summary>
    [Fact]
    public void TheQuickKeysFitWithEveryKeyTaken()
    {
        var loose = Enumerable.Range(1, 30)
            .Select(i => Catalogue.Item(sku: $"LOOSE{i:D2}", name: $"Country tomato, hybrid, from the Oddanchatram market lot {i}", price: 30m + i, unit: UnitType.Kilogram))
            .ToArray();

        using var harness = new BillingHarness(loose);
        harness.Press(Key.F11);

        Assert.Equal(24, harness.ViewModel.QuickKeyTiles.Count);

        Wpf.Run(() =>
        {
            var window = new MainBillingView(harness.ViewModel, Keymap.Default, Settings());

            try
            {
                Wpf.LayOutAt(window, TillWidth, TillHeight);

                AssertNothingOverflowsSideways(window, "the billing screen with the quick keys open");

                var box = (FrameworkElement)window.FindName("QuickKeyBox");
                var top = box.TranslatePoint(new Point(0, 0), window).Y;
                Assert.True(box.IsVisible && top > 0, $"the quick key box starts at {top:0}, off the top of the screen");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>The cash pane at its tallest: an expense, with every category listed under the box.</summary>
    [Fact]
    public void TheCashPaneFitsWithTheCategoriesOpen()
    {
        using var harness = new BillingHarness();

        harness.Press(Key.M, ModifierKeys.Control);
        harness.Press(Key.Down);
        harness.ViewModel.EditBuffer = "120";
        harness.Press(Key.Enter);

        Assert.True(harness.ViewModel.IsChoosingExpenseCategory);

        Wpf.Run(() =>
        {
            var window = new MainBillingView(harness.ViewModel, Keymap.Default, Settings());

            try
            {
                Wpf.LayOutAt(window, TillWidth, TillHeight);

                AssertNoKeyIsHiddenUnderPay(window, "the billing screen with the cash pane open");
                AssertNothingOverflowsSideways(window, "the billing screen with the cash pane open");

                var box = (FrameworkElement)window.FindName("DrawerBox");
                var bottom = box.TranslatePoint(new Point(0, box.ActualHeight), window).Y;
                Assert.True(box.IsVisible && bottom < TillHeight, $"the cash box ends at {bottom:0} of {TillHeight}");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// A long WhatsApp list pasted into the order box: the box scrolls inside itself rather than
    /// pushing the pane off the screen.
    /// </summary>
    [Fact]
    public void TheOrderPaneFitsWithALongMessagePasted()
    {
        using var harness = new BillingHarness();

        harness.Press(Key.O, ModifierKeys.Control);
        harness.ViewModel.EditBuffer = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"{i}. Premium organic cold pressed groundnut oil 5 litre tin x {i}"));

        Assert.True(harness.ViewModel.IsTakingOrder);

        Wpf.Run(() =>
        {
            var window = new MainBillingView(harness.ViewModel, Keymap.Default, Settings());

            try
            {
                Wpf.LayOutAt(window, TillWidth, TillHeight);

                AssertNothingOverflowsSideways(window, "the billing screen with the order pane open");

                var box = (FrameworkElement)window.FindName("OrderBox");
                var top = box.TranslatePoint(new Point(0, 0), window).Y;
                var bottom = box.TranslatePoint(new Point(0, box.ActualHeight), window).Y;
                Assert.True(box.IsVisible && top > 0 && bottom < TillHeight, $"the order box runs from {top:0} to {bottom:0} of {TillHeight}");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>The business pane, at the address with a long one typed in.</summary>
    [Fact]
    public void TheBusinessPaneFits()
    {
        using var harness = new BillingHarness();
        harness.AddCustomer("9800011122", name: "Sri Venkateswara Wholesale Provisions and General Merchants");
        harness.Press(Key.F7);
        harness.ViewModel.EditBuffer = "9800011122";
        harness.Press(Key.Enter);
        harness.Press(Key.G, ModifierKeys.Control);
        harness.ViewModel.EditBuffer = "29AABCK1234M1ZG";
        harness.Press(Key.Enter);
        harness.ViewModel.EditBuffer = "No. 1234, 5th Cross, 12th Main, Industrial Suburb, Rajajinagar, Bengaluru, Karnataka 560010";

        Assert.True(harness.ViewModel.IsSettingBusiness);

        Wpf.Run(() =>
        {
            var window = new MainBillingView(harness.ViewModel, Keymap.Default, Settings());

            try
            {
                Wpf.LayOutAt(window, TillWidth, TillHeight);

                AssertNothingOverflowsSideways(window, "the billing screen with the business pane open");

                var box = (FrameworkElement)window.FindName("BusinessBox");
                var bottom = box.TranslatePoint(new Point(0, box.ActualHeight), window).Y;
                Assert.True(box.IsVisible && bottom < TillHeight, $"the business box ends at {bottom:0} of {TillHeight}");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>F8 at its tallest: the customer picked, paying back by UPI, the code and the keys under it.</summary>
    /// <remarks>
    /// The sale before it went on the khata, so the last sale's note is up as well as the message
    /// bar. With the code stacked under the tenders the card was taller than the room above those
    /// two, and its keys were behind them; only the code itself was being checked.
    /// </remarks>
    [Fact]
    public void TheKhataPaymentPaneFitsWithTheUpiCode()
    {
        using var harness = new BillingHarness(
            Catalogue.Item(sku: "DAL001", barcode: "8901234567890", name: "Toor Dal 1kg", price: 189m));

        harness.ViewModel.Upi = new UpiPayee("sri.lakshmi.stores.main.road@okhdfcbank", "Sri Lakshmi Stores, Main Road, Tirunelveli");
        harness.AddCustomer("9500012345", name: "Lakshmi Narayanan Subramaniam");
        harness.Press(Key.F7);
        harness.ViewModel.EditBuffer = "9500012345";
        harness.Press(Key.Enter);
        harness.Scan("8901234567890");
        harness.Press(Key.F12);
        harness.Press(Key.Down);
        harness.Press(Key.Down);
        harness.Press(Key.Down);
        harness.Press(Key.Enter);
        harness.Press(Key.Enter);

        harness.Press(Key.F8);
        harness.ViewModel.EditBuffer = "9500012345";
        harness.Press(Key.Enter);
        harness.Press(Key.Down);

        Assert.True(harness.ViewModel.ShowsUpiQr);
        Assert.True(harness.ViewModel.ShowsStandingNote);

        Wpf.Run(() =>
        {
            var window = new MainBillingView(harness.ViewModel, Keymap.Default, Settings());

            try
            {
                Wpf.LayOutAt(window, TillWidth, TillHeight);
                Wpf.Settle(window);

                AssertNothingOverflowsSideways(window, "the billing screen with the khata payment open");

                var code = (QrCodeView)window.FindName("CollectUpiCode");
                var top = code.TranslatePoint(new Point(0, 0), window).Y;
                var bottom = code.TranslatePoint(new Point(0, code.ActualHeight), window).Y;

                Assert.True(code.IsVisible && top > 0 && bottom < TillHeight, $"the khata UPI code runs from {top:0} to {bottom:0} of {TillHeight}");
                Assert.NotNull(code.Code);

                var pane = (FrameworkElement)window.FindName("CollectPane");
                var note = (FrameworkElement)window.FindName("StandingNotePanel");
                var paneBottom = pane.TranslatePoint(new Point(0, pane.ActualHeight), window).Y;
                var noteTop = note.TranslatePoint(new Point(0, 0), window).Y;

                Assert.True(note.IsVisible, "the last sale's note is not showing, so there is nothing to be under");
                Assert.True(paneBottom <= noteTop + Slack, $"the khata pane ends at {paneBottom:0}, under the last sale's note at {noteTop:0}");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>The payment pane at its tallest: two payments taken, and the UPI code for the rest.</summary>
    [Fact]
    public void ThePaymentPaneFitsWithTheUpiCode()
    {
        using var harness = new BillingHarness(
            Catalogue.Item(sku: "OIL001", barcode: "8901234567913", name: "Premium Organic Cold Pressed Groundnut Oil 5 Litre Tin", price: 1299m));

        harness.ViewModel.Upi = new UpiPayee("sri.lakshmi.stores.main.road@okhdfcbank", "Sri Lakshmi Stores, Main Road, Tirunelveli");
        harness.ViewModel.Store = BillingHarness.Store;
        harness.AddCustomer("9500012345", name: "Lakshmi Narayanan Subramaniam");
        harness.Press(Key.F7);
        harness.ViewModel.EditBuffer = "9500012345";
        harness.Press(Key.Enter);
        harness.Scan("8901234567913");
        harness.Press(Key.F12);

        // And taken on the phone: the banner saying so is in the pane too.
        harness.Press(Key.W, ModifierKeys.Control);
        Assert.True(harness.ViewModel.IsPaperless);

        harness.ViewModel.EditBuffer = "100";
        harness.Press(Key.Enter);
        harness.Press(Key.Down);
        harness.ViewModel.EditBuffer = "100";
        harness.Press(Key.Enter);
        harness.Press(Key.Down);

        Assert.True(harness.ViewModel.ShowsUpiQr);

        Wpf.Run(() =>
        {
            var window = new MainBillingView(harness.ViewModel, Keymap.Default, Settings());

            try
            {
                Wpf.LayOutAt(window, TillWidth, TillHeight);

                AssertNothingOverflowsSideways(window, "the billing screen with the UPI code open");

                var code = (QrCodeView)window.FindName("UpiCode");
                var top = code.TranslatePoint(new Point(0, 0), window).Y;
                var bottom = code.TranslatePoint(new Point(0, code.ActualHeight), window).Y;

                Assert.True(code.IsVisible && top > 0 && bottom < TillHeight, $"the UPI code runs from {top:0} to {bottom:0} of {TillHeight}");
                Assert.NotNull(code.Code);
                Assert.True(code.ActualWidth >= 180, $"the UPI code is only {code.ActualWidth:0} wide");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// "Still due" and "Change" are clear of the message bar, with the pane at its tallest.
    /// </summary>
    /// <remarks>
    /// The bar is drawn over the panes so it can be read while one is open, and the payment pane,
    /// centred on the whole screen, had its two totals behind it - the figures the cashier reads
    /// out, hidden by the message telling them to take the payment.
    /// </remarks>
    [Fact]
    public void ThePaymentFiguresAreNotUnderTheMessageBar()
    {
        using var harness = new BillingHarness(
            Catalogue.Item(sku: "OIL001", barcode: "8901234567913", name: "Premium Organic Cold Pressed Groundnut Oil 5 Litre Tin", price: 1299m));

        harness.ViewModel.Upi = new UpiPayee("sri.lakshmi.stores.main.road@okhdfcbank", "Sri Lakshmi Stores, Main Road, Tirunelveli");
        harness.ViewModel.Store = BillingHarness.Store;
        harness.Scan("8901234567913");
        harness.Press(Key.F12);
        harness.Press(Key.W, ModifierKeys.Control);
        harness.ViewModel.EditBuffer = "100";
        harness.Press(Key.Enter);
        harness.Press(Key.Down);
        harness.ViewModel.EditBuffer = "100";
        harness.Press(Key.Enter);
        harness.Press(Key.Down);

        Assert.True(harness.ViewModel.ShowsUpiQr);
        Assert.False(string.IsNullOrEmpty(harness.ViewModel.StatusMessage));

        Wpf.Run(() =>
        {
            var window = new MainBillingView(harness.ViewModel, Keymap.Default, Settings());

            try
            {
                Wpf.LayOutAt(window, TillWidth, TillHeight);
                Wpf.Settle(window);

                var totals = (FrameworkElement)window.FindName("PaneTotals");
                var bar = (FrameworkElement)window.FindName("MessageStrip");
                var code = (FrameworkElement)window.FindName("UpiPanel");

                var totalsBottom = totals.TranslatePoint(new Point(0, totals.ActualHeight), window).Y;
                var codeBottom = code.TranslatePoint(new Point(0, code.ActualHeight), window).Y;
                var barTop = bar.TranslatePoint(new Point(0, 0), window).Y;

                Assert.True(bar.IsVisible, "the message bar is not showing, so there is nothing to be under");
                Assert.True(totalsBottom <= barTop + Slack, $"the totals end at {totalsBottom:0}, under the message bar at {barTop:0}");
                Assert.True(codeBottom <= barTop + Slack, $"the UPI code ends at {codeBottom:0}, under the message bar at {barTop:0}");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// The bill's columns reach the edge of the grid, and the item name has the room the codes
    /// leave when there is no space for them.
    /// </summary>
    [Fact]
    public void TheBillsColumnsFillTheGrid()
    {
        using var harness = new BillingHarness(
            Catalogue.Item(sku: "OIL001", barcode: "8901234567913", name: "Premium Organic Cold Pressed Groundnut Oil 5 Litre Tin", price: 1299m));

        harness.Scan("8901234567913");

        Wpf.Run(() =>
        {
            var window = new MainBillingView(harness.ViewModel, Keymap.Default, Settings());

            try
            {
                Wpf.LayOutAt(window, TillWidth, TillHeight);
                Wpf.Settle(window);

                var grid = (DataGrid)window.FindName("LineGrid");
                var shown = grid.Columns.Where(c => c.Visibility == Visibility.Visible).Sum(c => c.ActualWidth);
                var hsn = grid.Columns.Single(c => Equals(c.Header, "HSN"));
                var item = grid.Columns.Single(c => Equals(c.Header, "Item"));

                Assert.Equal(grid.ActualWidth >= MainBillingView.ReferenceColumnsMinWidth, hsn.Visibility == Visibility.Visible);
                Assert.True(grid.ActualWidth - shown < 24, $"the columns come to {shown:0} of a grid {grid.ActualWidth:0} wide");
                Assert.True(item.ActualWidth > item.MinWidth, $"the item name is squeezed to {item.ActualWidth:0}");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// On a 1024 × 768 counter all-in-one the line total is still whole: the columns that are
    /// only reference - HSN, barcode, the rate before tax - make way for it.
    /// </summary>
    /// <remarks>
    /// The grid gets about 600 units there, and the columns' floors used to come to 890: Rate,
    /// Disc and Total fell off the right-hand edge, with nothing to scroll them back.
    /// </remarks>
    [Fact]
    public void TheLineTotalIsWholeOnA1024Screen()
    {
        using var harness = new BillingHarness(
            Catalogue.Item(sku: "OIL001", barcode: "8901234567913", name: "Premium Organic Cold Pressed Groundnut Oil 5 Litre Tin", price: 1299m));

        harness.Scan("8901234567913");
        harness.Press(Key.F4);
        harness.ViewModel.EditBuffer = "99";
        harness.Press(Key.Enter);

        Wpf.Run(() =>
        {
            var window = new MainBillingView(harness.ViewModel, Keymap.Default, Settings());

            try
            {
                Wpf.LayOutAt(window, 1024, 698);

                var grid = (DataGrid)window.FindName("LineGrid");
                var visible = grid.Columns.Where(c => c.Visibility == Visibility.Visible).ToList();
                var total = grid.Columns.Single(c => Equals(c.Header, "Total"));

                Assert.DoesNotContain(visible, c => Equals(c.Header, "Before GST") || Equals(c.Header, "HSN") || Equals(c.Header, "Barcode"));
                Assert.True(visible.Sum(c => c.ActualWidth) <= grid.ActualWidth + Slack,
                    $"the columns come to {visible.Sum(c => c.ActualWidth):0} of a grid {grid.ActualWidth:0} wide");
                Assert.True(total.ActualWidth >= total.MinWidth, $"Total is {total.ActualWidth:0} wide");

                AssertNothingOverflowsSideways(window, "the billing screen at 1024 × 768");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// The customer's screen, at the smallest monitor a shop puts there: the whole code and its
    /// amount on screen, large enough to scan from across the counter.
    /// </summary>
    [Fact]
    public void TheCustomersScreenShowsTheWholeUpiCode()
    {
        using var harness = new BillingHarness(
            Catalogue.Item(sku: "DAL001", barcode: "8901234567890", name: "Toor Dal 1kg", price: 189m));

        harness.ViewModel.Upi = new UpiPayee("sri.lakshmi.stores@okaxis", "Sri Lakshmi Stores");
        harness.Scan("8901234567890");
        harness.Press(Key.F12);
        harness.Press(Key.Down);
        harness.Press(Key.Down);

        Wpf.Run(() =>
        {
            using var display = new CustomerDisplayViewModel(harness.ViewModel, "Sri Lakshmi Stores");
            var window = new CustomerDisplayWindow(display);

            try
            {
                Wpf.LayOutAt(window, 1024, 768);

                var code = (QrCodeView)window.FindName("UpiCode");
                var panel = (FrameworkElement)window.FindName("UpiPanel");
                var bottom = panel.TranslatePoint(new Point(0, panel.ActualHeight), window).Y;

                Assert.True(code.IsVisible && code.Code is not null, "the UPI code is not shown");
                Assert.True(bottom <= 768 + Slack, $"the UPI panel ends at {bottom:0} of 768");
                Assert.True(Math.Min(code.ActualWidth, code.ActualHeight) >= 200, $"the UPI code is only {code.ActualWidth:0} by {code.ActualHeight:0}");
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
                Wpf.LayOutAt(window, TillWidth, TillHeight);

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

    /// <summary>
    /// Every box to type in, every list and every table on the owner's screen has a name a screen
    /// reader can say.
    /// </summary>
    /// <remarks>
    /// Without one Narrator calls a box "edit" and a table "data grid", and the owner is left to guess
    /// which of the eight boxes on the purchase form the caret is in. Asked of each control's
    /// automation peer, which is what a screen reader asks: a name set on the control, or a label
    /// that says it is for it, both count.
    /// </remarks>
    [Fact]
    public void EveryBoxListAndTableOnTheOwnersScreenHasAName()
    {
        Wpf.Run(() =>
        {
            var window = BuildOwnerView();

            try
            {
                Wpf.LayOutAt(window, TillWidth, TillHeight);

                var tabs = Wpf.Descendants<TabControl>(window).First();
                var unnamed = new List<string>();

                for (var i = 0; i < tabs.Items.Count; i++)
                {
                    tabs.SelectedIndex = i;
                    window.UpdateLayout();

                    var tab = (tabs.Items[i] as TabItem)?.Header?.ToString() ?? $"tab {i + 1}";

                    foreach (var control in Wpf.Descendants<Control>(window))
                    {
                        if (control is not (TextBox or PasswordBox or ComboBox or ListBox or DataGrid) || !control.IsVisible)
                            continue;

                        var name = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(control)?.GetName();

                        if (string.IsNullOrWhiteSpace(name))
                            unnamed.Add($"{tab}: {control.GetType().Name} {Bound(control)}");
                    }
                }

                Assert.True(unnamed.Count == 0,
                    "These have no name a screen reader can say:\n  " + string.Join("\n  ", unnamed.Distinct()));
            }
            finally
            {
                window.Close();
            }
        });

        static string Bound(Control control)
        {
            var property = control switch
            {
                TextBox => TextBox.TextProperty,
                ComboBox => ComboBox.SelectedItemProperty,
                ItemsControl => ItemsControl.ItemsSourceProperty,
                _ => null,
            };

            var path = property is null ? null : System.Windows.Data.BindingOperations.GetBindingExpression(control, property)?.ParentBinding.Path?.Path;

            return string.IsNullOrEmpty(control.Name) ? $"bound to {path ?? "nothing"}" : control.Name;
        }
    }

    /// <summary>
    /// No two things on screen answer to the same Alt key.
    /// </summary>
    /// <remarks>
    /// Two controls with one access key do not both fire: WPF moves focus between them instead, and
    /// the key the runbook tells the owner to press does nothing. The price sheet was given Alt+P
    /// beside the header's "Save as a web page", and only the acceptance run noticed.
    /// </remarks>
    [Fact]
    public void NoTwoControlsOnATabShareAnAccessKey()
    {
        Wpf.Run(() =>
        {
            var window = BuildOwnerView();

            try
            {
                Wpf.LayOutAt(window, TillWidth, TillHeight);

                var tabs = Wpf.Descendants<TabControl>(window).First();
                var clashes = new List<string>();

                for (var i = 0; i < tabs.Items.Count; i++)
                {
                    tabs.SelectedIndex = i;
                    window.UpdateLayout();

                    var name = (tabs.Items[i] as TabItem)?.Header?.ToString() ?? $"tab {i + 1}";

                    foreach (var group in Wpf.Descendants<AccessText>(window)
                                 .Where(a => a.IsVisible && a.AccessKey != '\0')
                                 .GroupBy(a => char.ToUpperInvariant(a.AccessKey))
                                 .Where(g => g.Count() > 1))
                    {
                        clashes.Add($"{name}: Alt+{group.Key} on {string.Join(" and ", group.Select(a => $"'{a.Text}'"))}");
                    }
                }

                Assert.True(clashes.Count == 0, "These access keys are taken twice:\n  " + string.Join("\n  ", clashes));
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// The sections are a list down the left, one column, every header on screen without scrolling
    /// at 1366x768. A list that ran off the bottom would hide the last sections from anyone who does
    /// not know the Ctrl+number for them.
    /// </summary>
    [Fact]
    public void TheOwnersSectionsAreOneColumnAllOnScreen()
    {
        Wpf.Run(() =>
        {
            var window = BuildOwnerView();

            try
            {
                Wpf.LayOutAt(window, TillWidth, TillHeight);

                var tabs = Wpf.Descendants<TabControl>(window).First();

                Assert.Equal(10, tabs.Items.Count);

                var headers = tabs.Items.Cast<TabItem>().ToList();
                var lefts = headers.Select(item => Math.Round(item.TranslatePoint(new Point(0, 0), window).X)).Distinct().ToList();

                Assert.True(lefts.Count == 1, $"the section headers are in {lefts.Count} columns");

                var content = (FrameworkElement)window.Content;

                foreach (var header in headers)
                {
                    var bottom = header.TranslatePoint(new Point(0, header.ActualHeight), window).Y;
                    Assert.True(bottom <= content.ActualHeight, $"'{header.Header}' ends at {bottom:0}, below the {content.ActualHeight:0} the window shows");
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// The Purchases tab halfway through a delivery: a supplier picked, matches listed for the item
    /// being typed, and lines on the bill with a long name among them.
    /// </summary>
    [Fact]
    public void ThePurchasesTabFitsWithABillHalfEntered()
    {
        var items = new ItemRepository(_temp.Database);
        items.UpsertRange(
        [
            Catalogue.Item(sku: "OIL001", name: "Premium Organic Cold Pressed Groundnut Oil 5 Litre Tin", price: 1299m) with { StockQty = 3m },
            Catalogue.Item(sku: "OIL002", name: "Refined Sunflower Oil 1 Litre Pouch", price: 180m),
        ]);

        new PurchaseRepository(_temp.Database).AddSupplier(new Supplier(0, "Sri Venkateswara Wholesale Provisions and Oil Traders", "9443012345", "33AEIPH7795F1Z9", "33", true));

        Wpf.Run(() =>
        {
            var window = BuildOwnerView();

            try
            {
                Wpf.LayOutAt(window, TillWidth, TillHeight);

                var tabs = Wpf.Descendants<TabControl>(window).First();
                tabs.SelectedIndex = 8;
                window.UpdateLayout();

                var purchases = (PurchasesViewModel)((FrameworkElement)window.FindName("PurchasesTab")).DataContext;
                purchases.SelectedSupplier = purchases.Suppliers.Single();
                purchases.BillNo = "SVW/2026-27/00412";

                purchases.ItemQuery = "OIL001";
                purchases.LineQuantity = "12";
                purchases.LineRate = "1180.50";
                Assert.Null(purchases.AddLine());

                purchases.ItemQuery = "oil";
                window.UpdateLayout();

                Assert.Single(purchases.Lines);
                Assert.NotEmpty(purchases.ItemMatches);

                AssertEveryButtonIsReachable(window, "the Purchases tab with a bill half entered");
                AssertNoButtonLabelIsCutOff(window, "the Purchases tab with a bill half entered");
                AssertNothingOverflowsSideways(window, "the Purchases tab with a bill half entered");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>The GST tab with a month of sales on it, every table filled.</summary>
    [Fact]
    public void TheGstTabFitsWithAMonthOnIt()
    {
        var invoices = new InvoiceRepository(_temp.Database);
        var month = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);

        InvoiceLine[] lines =
        [
            InvoiceLine.Rehydrate(1, "Premium Organic Cold Pressed Groundnut Oil 5 Litre Tin", "1512", null, null, UnitType.Each, 1299m, 1299m, true, 5m, 1m, 0m, false),
            InvoiceLine.Rehydrate(2, "Malligai Poo", "0603", null, null, UnitType.Muzham, 30m, 30m, true, 0m, 2.5m, 0m, false),
        ];

        var totals = InvoiceTotals.From(lines);
        invoices.Save(new SaleDraft("L1", DateTimeOffset.Now, null, lines, totals, [new Tender(TenderType.Cash, totals.AmountPayable)], 0m, 0, 0, null));

        Wpf.Run(() =>
        {
            var window = BuildOwnerView();

            try
            {
                Wpf.LayOutAt(window, TillWidth, TillHeight);

                var tabs = Wpf.Descendants<TabControl>(window).First();
                tabs.SelectedIndex = 7;
                window.UpdateLayout();

                var gst = (GstReturnViewModel)((FrameworkElement)window.FindName("GstTab")).DataContext;
                gst.LaterMonth();
                window.UpdateLayout();

                Assert.Equal(month, gst.Month);
                Assert.Equal(2, gst.Hsn.Count);

                AssertEveryButtonIsReachable(window, "the GST tab with a month on it");
                AssertNoButtonLabelIsCutOff(window, "the GST tab with a month on it");
                AssertNothingOverflowsSideways(window, "the GST tab with a month on it");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// The Customers tab with somebody on it.
    /// </summary>
    /// <remarks>
    /// Opened empty, the tab shows a sentence and nothing else, so "every tab fits" said nothing
    /// about the panel an owner actually uses: two charts side by side, a list of bills, a name box
    /// with its button, and the button that forgets a customer. That panel only exists once a
    /// customer is picked, so one is.
    /// </remarks>
    [Fact]
    public void TheCustomersTabFitsWithACustomerOnIt()
    {
        var items = new ItemRepository(_temp.Database);
        items.UpsertRange([Catalogue.Item(sku: "OIL001", name: "Premium Organic Cold Pressed Groundnut Oil 5 Litre Tin", price: 1299m)]);

        var customers = new CustomerRepository(_temp.Database);
        var lakshmi = customers.Add(new Customer { MobileNo = "9876543210", Name = "Lakshmi Narayanan Venkataraman" });

        var bill = new InvoiceEngine("33");
        bill.AddItem(items.FindBySku("OIL001")!);
        bill.SetCustomer(lakshmi);

        var basket = new TenderBasket(bill.Totals.AmountPayable);
        // On credit, so the khata card and the total owed are on screen too - the widest the tab gets.
        basket.Add(TenderType.StoreCredit, bill.Totals.AmountPayable);

        new CheckoutService(new InvoiceRepository(_temp.Database), customers, new RecordingDrawerService())
            .Complete("L1", bill, basket);

        var screen = new CustomersViewModel(
            new Pos.Core.Analytics.CustomerQuery(_temp.Database), customers, new CreditRepository(_temp.Database));

        Wpf.Run(() =>
        {
            var window = BuildOwnerView(screen);

            try
            {
                Wpf.LayOutAt(window, TillWidth, TillHeight);

                var tabs = Wpf.Descendants<TabControl>(window).First();
                tabs.SelectedIndex = 6;

                screen.Search();
                screen.Selected = screen.Results[0];
                window.UpdateLayout();

                Assert.True(screen.HasSelection);
                Assert.True(screen.HasKhata);

                AssertEveryButtonIsReachable(window, "the Customers tab with a customer on it");
                AssertNoButtonLabelIsCutOff(window, "the Customers tab with a customer on it");
                AssertNothingOverflowsSideways(window, "the Customers tab with a customer on it");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// A bill previewed again starts at its heading. Found by the acceptance run: after reading one
    /// bill to its foot, the next preview opened at the foot too, and the heading of the layout the
    /// owner had just switched to was off the top of the screen.
    /// </summary>
    [Fact]
    public void ANewPreviewOpensAtTheTopOfTheBill()
    {
        Wpf.Run(() =>
        {
            var window = BuildOwnerView();

            try
            {
                Wpf.LayOutAt(window, TillWidth, TillHeight);

                var tabs = Wpf.Descendants<TabControl>(window).First();
                tabs.SelectedIndex = 3;
                window.UpdateLayout();

                var show = Wpf.Descendants<Button>(window).Single(b => b.Content is string s && s.Contains("_bill", StringComparison.Ordinal));
                var scroller = (ScrollViewer)window.FindName("PreviewTextScroll");

                show.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                window.UpdateLayout();

                Assert.True(scroller.ScrollableHeight > 0, "the sample bill should be longer than the screen");

                scroller.ScrollToEnd();
                window.UpdateLayout();
                Assert.True(scroller.VerticalOffset > 0);

                show.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                window.UpdateLayout();

                Assert.Equal(0, scroller.VerticalOffset);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// A day-end report read back on the Maintenance tab gets most of the screen. It used to share
    /// its column with two lists and showed four lines at a time, which is not a report anybody
    /// can read.
    /// </summary>
    [Fact]
    public void AReportReadBackOnMaintenanceGetsMostOfTheScreen()
    {
        Wpf.Run(() =>
        {
            var window = BuildOwnerView();

            try
            {
                Wpf.LayOutAt(window, TillWidth, TillHeight);

                var tabs = Wpf.Descendants<TabControl>(window).First();
                tabs.SelectedIndex = 5;
                window.UpdateLayout();

                var output = (FrameworkElement)window.FindName("MaintenanceOutput");

                Assert.True(output.ActualHeight >= TillHeight * 0.5,
                    $"the report panel is {output.ActualHeight:0} of {TillHeight} pixels tall");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private OwnerView BuildOwnerView(CustomersViewModel? customers = null)
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
            saveWebPage: (_, _) => null,
            receiptLayout: ReceiptLayout.Standard,
            applyReceiptLayout: _ => null,
            upiId: "sri.lakshmi.stores@okaxis",
            applyUpiId: _ => null);

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
            maintenance,
            customers ?? new CustomersViewModel(
                new Pos.Core.Analytics.CustomerQuery(_temp.Database),
                new CustomerRepository(_temp.Database)),
            new GstReturnViewModel(
                month => new Pos.Core.Analytics.GstReturnQuery(_temp.Database).Gather(settings.LaneId, month, "33"),
                (_, _) => []),
            new PurchasesViewModel(
                new PurchaseRepository(_temp.Database),
                query => items.Search(query),
                code => items.FindByBarcode(code) ?? items.FindBySku(code),
                settings.LaneId,
                "33"),
            new PricesViewModel(
                new PriceRepository(_temp.Database),
                _ => PrintOutcome.NotConfigured(),
                BillingHarness.Store.Name),
            new OrdersViewModel(
                cover => new Pos.Core.Analytics.OrderListQuery(_temp.Database).Gather(cover),
                _ => { },
                BillingHarness.Store.Name),
            new OffersViewModel(new OfferRepository(_temp.Database), items.Skus, items.Categories));
    }

    /// <summary>
    /// The Orders tab with something to order from a supplier with a long name, and an item with a
    /// long name on it - the widest the list and the order get.
    /// </summary>
    [Fact]
    public void TheOrdersTabFitsWithAnOrderOnIt()
    {
        var items = new ItemRepository(_temp.Database);
        items.UpsertRange([Catalogue.Item(sku: "OIL001", name: "Premium Organic Cold Pressed Groundnut Oil 5 Litre Tin", price: 1299m) with { StockQty = 0m }]);
        var oil = items.FindBySku("OIL001")!;

        var purchases = new PurchaseRepository(_temp.Database);
        var supplier = purchases.AddSupplier(new Supplier(0, "Sri Venkateswara Wholesale Provisions and Oil Traders", "9443012345", "33AEIPH7795F1Z9", "33", true));
        var line = PurchaseLine.Price(new PurchaseLineEntry(oil, 12m, 1180.50m, 5m, 0m), interState: false, chargesGst: true);
        purchases.Record(new PurchaseBill(supplier, "SVW/1", DateOnly.FromDateTime(DateTime.Today), [line], InterState: false), BillingHarness.LaneId, DateTimeOffset.Now, null);

        // Down to one of the twelve delivered: low, so on the list to fill back up.
        new StockRepository(_temp.Database).Set(oil.Id, 1m, StockReason.Adjust, BillingHarness.LaneId);

        Wpf.Run(() =>
        {
            var window = BuildOwnerView();

            try
            {
                Wpf.LayOutAt(window, TillWidth, TillHeight);

                var tabs = Wpf.Descendants<TabControl>(window).First();
                tabs.SelectedIndex = 9;
                window.UpdateLayout();

                var orders = (OrdersViewModel)((FrameworkElement)window.FindName("OrdersTab")).DataContext;
                orders.Load();
                window.UpdateLayout();

                Assert.NotNull(orders.SelectedSupplier);
                Assert.NotEmpty(orders.Lines);

                AssertEveryButtonIsReachable(window, "the Orders tab with an order on it");
                AssertNoButtonLabelIsCutOff(window, "the Orders tab with an order on it");
                AssertNothingOverflowsSideways(window, "the Orders tab with an order on it");
            }
            finally
            {
                window.Close();
            }
        });
    }
}
