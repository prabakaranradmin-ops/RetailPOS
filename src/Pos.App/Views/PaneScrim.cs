using System.Windows;
using System.Windows.Controls;

namespace Pos.App.Views;

/// <summary>
/// The dimmed layer under a pane, which places the pane's card above the message bar.
/// </summary>
/// <remarks>
/// <para>
/// The message bar is drawn over the panes, so that what a pane has just done - "statement
/// printed", "not an amount" - can be read while the pane is still open; under the dimming it was
/// 1.18:1. But a card centred on the whole screen then had its foot under the bar: on a 1366×768
/// till the payment pane's "Still due" and "Change", the two figures the cashier reads out, were
/// behind the message telling them to take the payment.
/// </para>
/// <para>
/// So the card is centred in the space above the bar instead. A card too tall for that space - a
/// long list of loose items on a short screen - goes to the top instead, which leaves as little of
/// it under the bar as the screen allows.
/// </para>
/// </remarks>
public sealed class PaneScrim : Border
{
    /// <summary>The gap kept between the card and the top of the screen, or the bar.</summary>
    private const double Gap = 12;

    public static readonly DependencyProperty BottomInsetProperty = DependencyProperty.Register(
        nameof(BottomInset), typeof(double), typeof(PaneScrim),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsArrange));

    /// <summary>How much of the foot of the screen the card stays off: the message bar and the keys.</summary>
    public double BottomInset
    {
        get => (double)GetValue(BottomInsetProperty);
        set => SetValue(BottomInsetProperty, value);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is null)
            return finalSize;

        var height = Math.Min(Child.DesiredSize.Height, finalSize.Height);
        var above = finalSize.Height - Math.Max(0, BottomInset);

        var top = height + 2 * Gap <= above
            ? (above - height) / 2
            : Math.Max(0, Math.Min(Gap, finalSize.Height - height));

        Child.Arrange(new Rect(0, top, finalSize.Width, height));
        return finalSize;
    }
}
