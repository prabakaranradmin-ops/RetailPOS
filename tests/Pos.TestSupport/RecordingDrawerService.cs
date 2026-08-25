using Pos.Core.Hardware.Drawer;

namespace Pos.TestSupport;

/// <summary>
/// Stands in for the cash drawer until the Phase 3 driver exists. Counts kicks so tests can assert
/// the drawer fired exactly when SRS 2.4 says it should, and can be told to fail so the "sale
/// survives a broken drawer" path is exercised.
/// </summary>
public sealed class RecordingDrawerService(bool isConfigured = true, DrawerKickResult result = DrawerKickResult.Opened)
    : IDrawerService
{
    public bool IsConfigured { get; set; } = isConfigured;

    public string Name => "recording drawer";

    public DrawerKickResult NextResult { get; set; } = result;

    public int KickCount { get; private set; }

    public DrawerKickResult Kick()
    {
        KickCount++;
        return NextResult;
    }
}
