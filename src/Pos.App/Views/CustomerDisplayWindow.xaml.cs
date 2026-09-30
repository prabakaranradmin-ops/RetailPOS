using System.Windows;
using Pos.App.ViewModels;

namespace Pos.App.Views;

/// <summary>
/// The screen that faces the customer, on the second monitor.
/// </summary>
/// <remarks>
/// Shown without activation and never focused, so opening it cannot take a keystroke meant for the
/// till. It places itself on whichever monitor is not the primary one, filling it.
/// </remarks>
public partial class CustomerDisplayWindow : Window
{
    public CustomerDisplayWindow(CustomerDisplayViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        DataContext = viewModel;
        Focusable = false;
    }

    /// <summary>
    /// The area of the second monitor, or null when there is only one. WPF sees the monitors as one
    /// virtual screen: anything outside the primary's width is the other one.
    /// </summary>
    public static Rect? SecondScreen()
    {
        var primaryWidth = SystemParameters.PrimaryScreenWidth;
        var virtualWidth = SystemParameters.VirtualScreenWidth;

        if (virtualWidth <= primaryWidth + 1)
            return null;

        var left = SystemParameters.VirtualScreenLeft < 0 ? SystemParameters.VirtualScreenLeft : primaryWidth;

        return new Rect(left, SystemParameters.VirtualScreenTop, virtualWidth - primaryWidth, SystemParameters.VirtualScreenHeight);
    }

    /// <summary>Puts the window on the second monitor and fills it. False when there is none.</summary>
    public bool ShowOnSecondScreen()
    {
        if (SecondScreen() is not { } area)
            return false;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = area.Left;
        Top = area.Top;
        Width = area.Width;
        Height = area.Height;

        Show();

        // Maximised once it is on that monitor, so it fills that one rather than the primary.
        WindowState = WindowState.Maximized;
        return true;
    }
}
