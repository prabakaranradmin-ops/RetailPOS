namespace Pos.App;

/// <summary>
/// The icons, as characters of the icon font Windows ships with - Segoe Fluent Icons on Windows 11,
/// Segoe MDL2 Assets on Windows 10, which share these code points.
/// </summary>
/// <remarks>
/// A font rather than pictures: nothing to ship or install, sharp at any size and scaling, and
/// coloured like text, so an icon takes the theme's colours without a second copy of each. Every
/// code point here was checked against a rendered sheet of the font; a wrong one draws an empty box,
/// which is easy to see and easy to fix here in one place.
/// </remarks>
public static class Glyphs
{
    // ---- Actions
    public const string Search = "";
    public const string Refresh = "";
    public const string Save = "";
    public const string Print = "";
    public const string Copy = "";
    public const string Close = "";
    public const string Undo = "";
    public const string Trash = "";
    public const string Download = "";
    public const string ZoomOut = "";
    public const string Pause = "";
    public const string Keyboard = "";

    // ---- How a message went
    public const string Check = "";
    public const string Done = "";
    public const string Error = "";
    public const string Warning = "";
    public const string Info = "";

    // ---- Things in the shop
    public const string People = "";
    public const string Person = "";
    public const string Cart = "";
    public const string Bag = "";
    public const string Card = "";
    public const string Bank = "";
    public const string Till = "";
    public const string Drawer = "";
    public const string Tea = "";
    public const string Tag = "";
    public const string Package = "";
    public const string Book = "";
    public const string Building = "";
    public const string Chat = "";
    public const string Phone = "";
    public const string QrCode = "";
    public const string Document = "";
    public const string Report = "";
    public const string Checklist = "";
    public const string Calendar = "";
    public const string Clock = "";
    public const string Star = "";
    public const string Heart = "";
    public const string Lock = "";

    // ---- The owner's screen
    public const string Dashboard = "";
    public const string Settings = "";
    public const string Wrench = "";
    public const string Tools = "";
    public const string Health = "";

    // ---- Charts
    public const string Chart = "";
    public const string Trend = "";
    public const string Pie = "";
    public const string Bubbles = "";
    public const string Heat = "";
    public const string Table = "";
}
