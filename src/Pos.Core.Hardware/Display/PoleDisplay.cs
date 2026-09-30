using System.Text;
using Pos.Core.Hardware.Printing;
using Pos.Core.Hardware.Serial;

namespace Pos.Core.Hardware.Display;

/// <summary>
/// The small two-line display on a pole that faces the customer, showing the item just rung up and
/// the total.
/// </summary>
/// <remarks>
/// Never a reason for a sale to fail. A display that is unplugged or switched off shows nothing,
/// and the till carries on - which is why <see cref="Show"/> reports rather than throws.
/// </remarks>
public interface IPoleDisplay
{
    bool IsConfigured { get; }

    string Name { get; }

    /// <summary>Characters on each line.</summary>
    int Width { get; }

    /// <summary>Puts two lines up. False when the display did not take them.</summary>
    bool Show(string top, string bottom);
}

/// <summary>No pole display: the ordinary counter.</summary>
public sealed class NoPoleDisplay : IPoleDisplay
{
    public bool IsConfigured => false;

    public string Name => "none";

    public int Width => 20;

    public bool Show(string top, string bottom) => false;
}

/// <summary>
/// A pole display on a serial port, spoken to with the command set these displays share: clear the
/// screen, write the first line, move to the second, write it.
/// </summary>
/// <remarks>
/// <c>FF</c> (0x0C) clears the screen and puts the cursor at the top left; <c>US $ x y</c> (0x1F 0x24)
/// moves it to column x of row y. Text is reduced to ASCII, which every such display shows; each line
/// is padded to the width so the previous line's tail is overwritten.
/// </remarks>
public sealed class SerialPoleDisplay(ISerialPort port, int width = 20) : IPoleDisplay
{
    private readonly ISerialPort _port = port ?? throw new ArgumentNullException(nameof(port));

    public bool IsConfigured => true;

    public string Name => $"pole display on {_port.PortName}";

    public int Width { get; } = width is >= 8 and <= 80 ? width : throw new ArgumentOutOfRangeException(nameof(width), width, "A pole display is 8 to 80 characters wide.");

    /// <summary>The bytes that put two lines up.</summary>
    public byte[] Frame(string top, string bottom)
    {
        var bytes = new List<byte>(Width * 2 + 8) { 0x0C };
        bytes.AddRange(Line(top));
        bytes.AddRange([0x1F, 0x24, 0x01, 0x02]);
        bytes.AddRange(Line(bottom));
        return [.. bytes];
    }

    private byte[] Line(string? text)
    {
        var ascii = EscPos.Transliterate(text ?? string.Empty);
        var fitted = ascii.Length > Width ? ascii[..Width] : ascii.PadRight(Width);
        return Encoding.ASCII.GetBytes(fitted);
    }

    public bool Show(string top, string bottom)
    {
        try
        {
            if (!_port.IsOpen)
                _port.Open();

            _port.Write(Frame(top, bottom));
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or TimeoutException)
        {
            return false;
        }
    }

    /// <summary>A label on the left and a figure against the right edge, as a line of this display.</summary>
    public static string Pair(string left, string right, int width)
    {
        var room = Math.Max(0, width - right.Length - 1);
        var label = left.Length > room ? left[..room] : left;
        return label + new string(' ', Math.Max(1, width - label.Length - right.Length)) + right;
    }
}
