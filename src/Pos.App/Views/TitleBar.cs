using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Pos.App.Views;

/// <summary>
/// Asks Windows to draw a window's title bar in the look on screen: dark on a dark look, light on a
/// light one, in the page's own colour, so the frame does not sit as a white stripe across the top
/// of a dark screen or a black one across a light screen.
/// </summary>
/// <remarks>
/// <para>
/// The title bar belongs to Windows, not to the window, so this is a request rather than a style:
/// Windows 10 from 20H1 honours the dark mode, Windows 11 also takes the caption and border colours,
/// and anything older ignores all of it and keeps its usual frame. Nothing here can fail a window
/// that would otherwise have opened.
/// </para>
/// <para>
/// Local to this machine: a call into the desktop window manager, nothing over any network.
/// </para>
/// </remarks>
public static class TitleBar
{
    private const int DarkModeOld = 19;
    private const int DarkMode = 20;
    private const int BorderColour = 34;
    private const int CaptionColour = 35;
    private const int TextColour = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    /// <summary>Paints the title bar when the window gets its handle, or at once if it has one.</summary>
    public static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            Paint(window);
        else
            window.SourceInitialized += (_, _) => Paint(window);
    }

    /// <summary>Paints the title bar in the look on screen now. Called again when the look changes.</summary>
    public static void Paint(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;

            if (handle == IntPtr.Zero)
                return;

            var dark = window.TryFindResource("IsDarkTheme") is not false ? 1 : 0;

            if (DwmSetWindowAttribute(handle, DarkMode, ref dark, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, DarkModeOld, ref dark, sizeof(int));

            // The caption in the page colour, its text in the ink, and the border in the rim the
            // cards use - whichever look those are now.
            var caption = Native(window, "Surface", Color.FromRgb(0x0A, 0x0D, 0x13));
            var text = Native(window, "Ink", Color.FromRgb(0xF8, 0xFA, 0xFC));
            var border = Native(window, "CardEdge", Color.FromRgb(0x1F, 0x29, 0x37));

            DwmSetWindowAttribute(handle, CaptionColour, ref caption, sizeof(int));
            DwmSetWindowAttribute(handle, TextColour, ref text, sizeof(int));
            DwmSetWindowAttribute(handle, BorderColour, ref border, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // A Windows without the window manager's API keeps its ordinary frame.
        }
    }

    /// <summary>A palette colour as Windows takes it: 0x00BBGGRR.</summary>
    private static int Native(Window window, string key, Color fallback)
    {
        var colour = window.TryFindResource(key) is SolidColorBrush brush ? brush.Color : fallback;
        return colour.R | (colour.G << 8) | (colour.B << 16);
    }
}
