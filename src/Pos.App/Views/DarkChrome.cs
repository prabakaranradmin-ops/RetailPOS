using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Pos.App.Views;

/// <summary>
/// Asks Windows to draw a window's title bar dark, in the page's own colour, so the frame does not
/// sit as a white stripe across the top of a dark screen.
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
public static class DarkChrome
{
    private const int DarkModeOld = 19;
    private const int DarkMode = 20;
    private const int BorderColour = 34;
    private const int CaptionColour = 35;
    private const int TextColour = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    /// <summary>Applies the dark frame when the window gets its handle, or at once if it has one.</summary>
    public static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            Paint(window);
        else
            window.SourceInitialized += (_, _) => Paint(window);
    }

    private static void Paint(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;

            if (handle == IntPtr.Zero)
                return;

            var on = 1;

            if (DwmSetWindowAttribute(handle, DarkMode, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, DarkModeOld, ref on, sizeof(int));

            // Windows takes these as 0x00BBGGRR. The caption in the page colour (#0A0D13), its text in
            // the ink (#F8FAFC), and the border in the rim the cards use (#1F2937).
            var caption = 0x00130D0A;
            var text = 0x00FCFAF8;
            var border = 0x0037291F;

            DwmSetWindowAttribute(handle, CaptionColour, ref caption, sizeof(int));
            DwmSetWindowAttribute(handle, TextColour, ref text, sizeof(int));
            DwmSetWindowAttribute(handle, BorderColour, ref border, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // A Windows without the window manager's API keeps its ordinary frame.
        }
    }
}
