using Pos.Core.Domain.Printing;
using Pos.Core.Hardware.Drawer;
using Pos.Core.Hardware.Printing;
using Pos.Core.Logging;

namespace Pos.Core.Domain;

/// <param name="Note">The credit note as issued.</param>
/// <param name="Drawer">Whether the drawer opened for a cash refund; <c>NoDrawerAttached</c> otherwise.</param>
/// <param name="Print">Whether the credit note printed.</param>
public sealed record ReturnResult(CreditNote Note, DrawerKickResult Drawer, PrintOutcome Print);

/// <summary>
/// Taking goods back at the counter: the credit note, then the drawer and the paper.
/// </summary>
/// <remarks>
/// Shaped like <see cref="CreditService"/>: the credit note is recorded first and the hardware comes
/// after. A drawer that will not open or a printer out of paper is reported, never a reason to lose a
/// return the customer has already handed the goods back for.
/// </remarks>
public sealed class ReturnService(
    ICreditNoteStore store,
    IDrawerService drawer,
    TimeProvider? clock = null,
    IPrinterService? printer = null,
    ReceiptComposer? receipts = null,
    IPosLog? log = null,
    Func<string?>? cashier = null,
    Func<bool>? roundToRupee = null)
{
    private readonly ICreditNoteStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IDrawerService _drawer = drawer ?? throw new ArgumentNullException(nameof(drawer));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly IPrinterService _printer = printer ?? new NoPrinterService();
    private readonly IPosLog _log = log ?? NullLog.Instance;

    /// <summary>A bill and what can still come back from it, or null for no such bill.</summary>
    public ReturnableBill? Find(string invoiceNo) => _store.Returnable(invoiceNo);

    /// <summary>Works out a return without recording it, for the screen to show before it is committed.</summary>
    public CreditNoteDraft Draft(
        ReturnableBill bill,
        IReadOnlyList<(int LineNo, decimal Quantity, bool Restock)> picked,
        TenderType refund,
        string? reason) =>
        CreditNoteDraft.Build(bill, picked, refund, reason, roundToRupee?.Invoke() ?? false);

    /// <summary>
    /// Issues the credit note, then opens the drawer for a cash refund and prints it.
    /// </summary>
    public ReturnResult Issue(CreditNoteDraft draft, string laneId)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var note = _store.Issue(draft, laneId, _clock.GetLocalNow(), cashier?.Invoke());

        _log.Info("returns", $"{note.Number} against {note.InvoiceNo}: {Plural.Of(note.Lines.Count, "line")}, {note.Refunded:0.00} refunded by {note.Refund} on {laneId}");

        var drawerResult = note.Refund == TenderType.Cash && note.Refunded != 0m && _drawer.IsConfigured
            ? _drawer.Kick()
            : DrawerKickResult.NoDrawerAttached;

        if (drawerResult == DrawerKickResult.Failed)
            _log.Warn("drawer", $"the drawer did not open for a refund ({_drawer.Name})");

        return new ReturnResult(note, drawerResult, Print(note, isReprint: false));
    }

    /// <summary>Prints a credit note again, marked as a reprint.</summary>
    public PrintOutcome Reprint(string creditNoteNo)
    {
        var note = _store.Find(creditNoteNo);

        return note is null
            ? PrintOutcome.Failed($"There is no credit note {creditNoteNo}.")
            : Print(note, isReprint: true);
    }

    private PrintOutcome Print(CreditNote note, bool isReprint)
    {
        if (receipts is null || !_printer.IsConfigured)
            return PrintOutcome.NotConfigured();

        try
        {
            return _printer.Print(receipts.ComposeCreditNote(note, isReprint).ToEscPos(raster: _printer.Raster));
        }
        catch (Exception ex)
        {
            _log.Error("printer", "the credit note did not print", ex);
            return PrintOutcome.Failed(ex.Message);
        }
    }
}
