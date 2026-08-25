using Pos.Core.Hardware.Printing;
using Pos.Core.Hardware.Serial;

namespace Pos.Core.Hardware.Drawer;

/// <summary>
/// Kicks a drawer wired into the receipt printer's RJ11 port — how almost every counter is built,
/// because it needs no second cable and no second COM port.
/// </summary>
/// <remarks>
/// The pulse rides through the printer as an ordinary ESC/POS command, so a printer that is
/// offline takes the drawer with it. That is reported rather than hidden: a cashier who is told
/// the drawer did not open will open it by key, whereas one who is told nothing will stand there
/// pulling at it.
/// </remarks>
public sealed class PrinterPassthroughDrawerService(
    IPrinterService printer,
    int pin = 0,
    int onMilliseconds = 60,
    int offMilliseconds = 120) : IDrawerService
{
    private readonly IPrinterService _printer = printer ?? throw new ArgumentNullException(nameof(printer));

    public bool IsConfigured => _printer.IsConfigured;

    public string Name => $"printer passthrough ({_printer.Name}, pin {pin + 2})";

    public DrawerKickResult Kick()
    {
        if (!IsConfigured)
            return DrawerKickResult.NoDrawerAttached;

        var outcome = _printer.Print(EscPos.KickDrawer(pin, onMilliseconds, offMilliseconds));

        return outcome.Status switch
        {
            PrintStatus.Printed => DrawerKickResult.Opened,
            PrintStatus.NoPrinterConfigured => DrawerKickResult.NoDrawerAttached,
            _ => DrawerKickResult.Failed,
        };
    }
}

/// <summary>
/// Kicks a drawer on its own serial line, for counters where the drawer is not hanging off the
/// printer.
/// </summary>
public sealed class SerialDrawerService(
    ISerialPort port,
    int pin = 0,
    int onMilliseconds = 60,
    int offMilliseconds = 120) : IDrawerService
{
    private readonly ISerialPort _port = port ?? throw new ArgumentNullException(nameof(port));

    public bool IsConfigured => true;

    public string Name => $"serial drawer ({_port.PortName})";

    public DrawerKickResult Kick()
    {
        try
        {
            if (!_port.IsOpen)
                _port.Open();

            _port.Write(EscPos.KickDrawer(pin, onMilliseconds, offMilliseconds));
            return DrawerKickResult.Opened;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or TimeoutException)
        {
            return DrawerKickResult.Failed;
        }
    }
}
