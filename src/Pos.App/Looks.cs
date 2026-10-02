using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Pos.App.Charts;
using Pos.App.Views;
using Pos.Core.Configuration;

namespace Pos.App;

/// <summary>
/// Which look the screens are in - Morning, Noon, Evening or Night - and changing it while every
/// window is open.
/// </summary>
/// <remarks>
/// <para>
/// A look is a palette in <c>Themes\</c>: the same colour names, different colours. One is loaded
/// into the application's resources ahead of <c>Theme.xaml</c>, and every window takes its colours by
/// name as dynamic resources, so swapping the palette repaints the till, the owner's screen and any
/// dialog at once - nothing reopens and no bill in progress is touched.
/// </para>
/// <para>
/// Two things are drawn outside WPF's resources and are told separately: the title bar, which
/// belongs to Windows, and the charts, which draw themselves from the palette and are asked to draw
/// again.
/// </para>
/// </remarks>
public static class Looks
{
    private static DispatcherTimer? _clock;
    private static Func<TimeOnly> _now = () => TimeOnly.FromDateTime(DateTime.Now);

    /// <summary>What the owner chose: one of the four looks, or to follow the time of day.</summary>
    public static ScreenTheme Chosen { get; private set; } = ScreenTheme.Night;

    /// <summary>The look on screen now: always one of the four, never <see cref="ScreenTheme.ByTimeOfDay"/>.</summary>
    public static ScreenTheme Showing { get; private set; } = ScreenTheme.Night;

    /// <summary>
    /// Shows the chosen look, and when the choice is to follow the time of day, keeps checking the
    /// clock so the look changes on its own as the hours pass.
    /// </summary>
    /// <param name="now">The clock, for tests; the machine's own otherwise.</param>
    public static void Follow(ScreenTheme chosen, Func<TimeOnly>? now = null)
    {
        Chosen = Enum.IsDefined(chosen) ? chosen : ScreenTheme.Night;

        if (now is not null)
            _now = now;

        Apply(ScreenThemes.Resolve(Chosen, _now()));

        if (Chosen != ScreenTheme.ByTimeOfDay)
        {
            _clock?.Stop();
            return;
        }

        // Once a minute is often enough: the look changes four times a day, and a minute late at
        // eleven o'clock is not something anybody at a counter will see.
        _clock ??= new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher.CurrentDispatcher);
        _clock.Start();
    }

    /// <summary>Looks at the clock, and changes the look if the hour now calls for another.</summary>
    public static void Tick()
    {
        if (Chosen == ScreenTheme.ByTimeOfDay)
            Apply(ScreenThemes.At(_now()));
    }

    /// <summary>The palette a look is drawn from.</summary>
    public static Uri Palette(ScreenTheme look) =>
        new($"/Pos.App;component/Themes/{(look == ScreenTheme.ByTimeOfDay ? ScreenTheme.Night : look)}.xaml", UriKind.Relative);

    /// <summary>
    /// Puts a look on every open window: its colours, its title bars, and its charts drawn again.
    /// </summary>
    public static void Apply(ScreenTheme look)
    {
        if (look == ScreenTheme.ByTimeOfDay)
            look = ScreenThemes.At(_now());

        if (Application.Current is not { } app)
            return;

        var merged = app.Resources.MergedDictionaries;
        var at = IndexOfPalette(merged);

        if (at >= 0 && look == Showing)
            return;

        var palette = new ResourceDictionary { Source = Palette(look) };

        if (at >= 0)
            merged[at] = palette;
        else
            merged.Insert(0, palette);

        Showing = look;

        foreach (Window window in app.Windows)
        {
            TitleBar.Paint(window);
            Redraw(window);
        }
    }

    /// <summary>Where the palette sits among the application's dictionaries, or -1 when none is loaded.</summary>
    private static int IndexOfPalette(IList<ResourceDictionary> merged)
    {
        for (var i = 0; i < merged.Count; i++)
        {
            if (merged[i].Source?.OriginalString.Contains("Themes/", StringComparison.OrdinalIgnoreCase) == true)
                return i;
        }

        return -1;
    }

    /// <summary>Asks every chart in a window to draw itself again from the new palette.</summary>
    private static void Redraw(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);

            if (child is ChartSurface or Sparkline)
                ((UIElement)child).InvalidateVisual();

            Redraw(child);
        }
    }
}
