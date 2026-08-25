namespace Pos.Core.Hardware.Drawer;

public enum DrawerKickResult
{
    /// <summary>The pulse was sent and the drawer released.</summary>
    Opened = 0,

    /// <summary>No drawer is configured on this lane. Not an error — plenty of lanes have none.</summary>
    NoDrawerAttached = 1,

    /// <summary>A drawer is configured but the pulse could not be delivered.</summary>
    Failed = 2,
}

/// <summary>
/// Releases the cash drawer. SRS 2.4 fires this on cash tender confirmation.
/// </summary>
/// <remarks>
/// The pulse is a standard ESC/POS command, not a vendor extension, and reaches the drawer one of
/// two ways (ARCHITECTURE.md section 5): through the receipt printer's RJ11 passthrough port, which
/// is how nearly every counter is wired, or straight down a serial line for a drawer with its own
/// controller.
/// <para>
/// <see cref="Kick"/> reports failure rather than throwing, and settlement treats the result as
/// information rather than as a condition of the sale. A drawer that will not open is a problem for
/// the shop to deal with; it is not a reason to refuse a customer's money or to lose an invoice
/// that has already been tendered.
/// </para>
/// </remarks>
public interface IDrawerService
{
    /// <summary>False when no drawer is configured, so callers can skip the prompt entirely.</summary>
    bool IsConfigured { get; }

    /// <summary>Identifies the drawer in diagnostics and error messages.</summary>
    string Name { get; }

    DrawerKickResult Kick();
}

/// <summary>
/// Stands in for a drawer on a lane that has none — card-only counters, or a bench during
/// development. Every kick is a no-op that reports honestly.
/// </summary>
public sealed class NoDrawerService : IDrawerService
{
    public bool IsConfigured => false;

    public string Name => "none";

    public DrawerKickResult Kick() => DrawerKickResult.NoDrawerAttached;
}
