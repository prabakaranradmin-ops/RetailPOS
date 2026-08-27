using Pos.Core.Domain.Printing;
using Pos.Core.Hardware.Printing;

namespace Pos.Core.Domain;

/// <param name="Day">The report that was produced.</param>
/// <param name="Print">Whether it printed.</param>
/// <param name="Backup">Whether the day's books were snapshotted.</param>
public sealed record DayCloseResult(DayCloseSummary Day, PrintOutcome Print, BackupOutcome Backup);

/// <summary>
/// Closes a lane's day: reports what it took, saves the report, prints it, and takes a backup.
/// </summary>
/// <remarks>
/// Ordered like checkout, and for the same reason. The close is committed first, then the report is
/// printed and the books are snapshotted. A printer out of paper must not stop a day being closed —
/// the report can be reprinted from the saved figures, but a close that half happened would leave
/// invoices attributed to nothing.
/// <para>
/// The backup is part of closing rather than a separate chore because it is the only moment in the
/// day when somebody is reliably standing at the till with a reason to wait a few seconds.
/// </para>
/// </remarks>
public sealed class DayCloseService(
    IDayCloseStore closes,
    ZReportComposer reports,
    IPrinterService printer,
    IBackupService? backups = null,
    TimeProvider? clock = null,
    IStockStore? stock = null)
{
    private readonly IDayCloseStore _closes = closes ?? throw new ArgumentNullException(nameof(closes));
    private readonly ZReportComposer _reports = reports ?? throw new ArgumentNullException(nameof(reports));
    private readonly IPrinterService _printer = printer ?? throw new ArgumentNullException(nameof(printer));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Where the reorder list at the foot of the report comes from. Null means no list.</summary>
    private readonly IStockStore? _stock = stock;

    /// <summary>What the lane would report if it closed now. Changes nothing.</summary>
    public DayCloseSummary Preview(string laneId) => _closes.Preview(laneId, _clock.GetLocalNow());

    public DayCloseResult Close(string laneId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(laneId);

        var day = _closes.Close(laneId, _clock.GetLocalNow());

        var print = Print(day);
        var backup = backups?.Create(_clock.GetLocalNow()) ?? new BackupOutcome(false, string.Empty, "No backup is configured for this lane.");

        return new DayCloseResult(day, print, backup);
    }

    /// <summary>Prints a duplicate of a saved report, marked as one.</summary>
    public PrintOutcome Reprint(DayCloseSummary day)
    {
        ArgumentNullException.ThrowIfNull(day);
        return Print(day, isReprint: true);
    }

    /// <summary>The last report this lane produced, for reprinting.</summary>
    public DayCloseSummary? Latest(string laneId) => _closes.FindLatest(laneId);

    /// <summary>
    /// What needs reordering, or null if this lane does not count stock.
    /// </summary>
    /// <remarks>
    /// Swallows its own failure. The day is already closed and the takings are already reported;
    /// a stock query that will not run is not a reason to lose the sheet that says what is in the
    /// drawer.
    /// </remarks>
    private IReadOnlyList<StockLevel>? LowStock()
    {
        if (_stock is null)
            return null;

        try
        {
            var low = _stock.ListLow(50);
            return low.Count == 0 ? null : low;
        }
        catch
        {
            return null;
        }
    }

    private PrintOutcome Print(DayCloseSummary day, bool isReprint = false)
    {
        if (!_printer.IsConfigured)
            return PrintOutcome.NotConfigured();

        try
        {
            // The reorder list is a fact about the shelves right now, not about the day being
            // reported, so it goes on the original and never on a duplicate. A report pulled out
            // of the file months later must not carry today's shelves under last spring's takings.
            var lowStock = isReprint ? null : LowStock();

            return _printer.Print(_reports.Compose(day, isReprint, lowStock).ToEscPos(raster: _printer.Raster));
        }
        catch (Exception ex)
        {
            // The close is already committed. Whatever went wrong here is a message, not a failure
            // of the close.
            return PrintOutcome.Failed($"The report could not be produced: {ex.Message}");
        }
    }
}
