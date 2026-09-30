using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Pos.App;

/// <summary>
/// Opening a link with whatever app on this computer handles it - WhatsApp, for a <c>whatsapp:</c>
/// link - but only when one does.
/// </summary>
/// <remarks>
/// Asking Windows to open a link nothing handles does not fail: it offers to find an app in the
/// Store, over the till, and reports success. So whether anything is registered for the link's
/// scheme is asked first, and a scheme with no app is treated as nothing opened - which sends the
/// caller to its fallback, the clipboard.
/// </remarks>
internal static class ShellLinks
{
    private const uint IsProtocol = 0x00001000;          // ASSOCF_IS_PROTOCOL
    private const uint IgnoreUnknown = 0x00000400;       // ASSOCF_INIT_IGNOREUNKNOWN
    private const uint FriendlyAppName = 4;              // ASSOCSTR_FRIENDLYAPPNAME

    /// <summary>Opens the link when an app is registered for its scheme.</summary>
    /// <returns>True when it was handed to that app.</returns>
    public static bool Open(string link)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(link);

        var colon = link.IndexOf(':');

        if (colon <= 0 || !Handles(link[..colon]))
            return false;

        Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
        return true;
    }

    /// <summary>True when an app on this computer is registered for the scheme.</summary>
    public static bool Handles(string scheme)
    {
        uint length = 0;

        // Asked for the size only: S_OK or S_FALSE both mean there is an app; anything else, none.
        var result = AssocQueryString(IsProtocol | IgnoreUnknown, FriendlyAppName, scheme, null, IntPtr.Zero, ref length);
        return result is 0 or 1;
    }

    [DllImport("shlwapi.dll", EntryPoint = "AssocQueryStringW", CharSet = CharSet.Unicode)]
    private static extern uint AssocQueryString(uint flags, uint what, string association, string? extra, IntPtr output, ref uint length);
}
