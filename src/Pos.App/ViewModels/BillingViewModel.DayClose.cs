using Pos.Core.Domain;

namespace Pos.App.ViewModels;

/// <summary>One line of the close-the-day pane: what it is, and how much.</summary>
/// <param name="Emphasis">The figure to count against: the cash that should be in the drawer.</param>
public sealed record DayCloseRow(string Label, string Value, bool Emphasis = false);

public sealed partial class BillingViewModel
{
    /// <summary>
    /// The first press of the close key: the day is shown, and a second press closes it.
    /// </summary>
    /// <remarks>
    /// The preview used to be one 160-character status line ending "Press again to close" - the
    /// part a narrow screen cut - with no word on how to back out. A close cannot be undone, so the
    /// figures now get a pane of their own, in a table, with both keys spelled out under them.
    /// </remarks>
    public bool IsConfirmingDayClose => _pendingDayClose;

    private bool PendingDayClose
    {
        get => _pendingDayClose;
        set
        {
            if (_pendingDayClose == value)
                return;

            _pendingDayClose = value;

            // A count belongs to the close it was typed for. Backing out and coming back counts again.
            if (!value)
            {
                _cashCounted = null;
                _dayClosePreview = null;
            }

            Raise(nameof(IsConfirmingDayClose));
            Raise(nameof(IsCountingDrawer));
            Raise(nameof(DayCloseKeys));
        }
    }

    private decimal? _cashCounted;
    private DayCloseSummary? _dayClosePreview;

    /// <summary>
    /// True while the close pane waits for the drawer to be counted. The cash the till expects is not
    /// on screen until it has been: a count made with the answer showing is a copy, not a count.
    /// </summary>
    public bool IsCountingDrawer => _pendingDayClose && _cashCounted is null;

    /// <summary>
    /// Enter in the close pane: what was typed is the cash counted. The expected figure and the
    /// difference come up once it is in.
    /// </summary>
    private void CommitCount()
    {
        var typed = EditBuffer.Trim();

        if (typed.Length == 0)
        {
            StatusMessage = _cashCounted is null
                ? $"Count the cash in the drawer, type it, then {CommitKey}. Or {CloseDayKey} to close without a count."
                : $"{CloseDayKey} again to close the day, {CancelKey} to keep selling.";
            return;
        }

        if (!TryParseAmount(typed, out var counted) || counted < 0m)
        {
            StatusMessage = $"'{typed}' is not an amount of cash.";
            return;
        }

        if (_dayClosePreview is not { } preview)
            return;

        _cashCounted = counted;
        EditBuffer = string.Empty;
        DayCloseRows = CloseRows(preview, counted);
        Raise(nameof(IsCountingDrawer));
        Raise(nameof(DayCloseKeys));

        var difference = counted - preview.CashExpected;

        StatusMessage = difference switch
        {
            0m => $"Counted {Show.Money(counted)}: exactly right. {CloseDayKey} again to close the day.",
            > 0m => $"Counted {Show.Money(counted)}: over by {Show.Money(difference)}. Count again, or {CloseDayKey} to close the day.",
            _ => $"Counted {Show.Money(counted)}: short by {Show.Money(-difference)}. Count again, or {CloseDayKey} to close the day.",
        };
    }

    /// <summary>The day about to be closed, a figure a line.</summary>
    public IReadOnlyList<DayCloseRow> DayCloseRows
    {
        get => _dayCloseRows;
        private set => Set(ref _dayCloseRows, value);
    }

    private IReadOnlyList<DayCloseRow> _dayCloseRows = [];

    /// <summary>A day with no sales says so over the figures.</summary>
    public string DayCloseNote
    {
        get => _dayCloseNote;
        private set => Set(ref _dayCloseNote, value);
    }

    private string _dayCloseNote = string.Empty;

    /// <summary>The key that closes the day, as the messages name it. Set from the keymap by the view.</summary>
    public string CloseDayKey { get; set; } = "Shift+F12";

    /// <summary>The key that backs out, as the messages name it. Set from the keymap by the view.</summary>
    public string CancelKey { get; set; } = "Esc";

    /// <summary>The foot of the close pane: how to go ahead, and how not to.</summary>
    public string DayCloseKeys => IsCountingDrawer
        ? $"Count the drawer, type it, {CommitKey}  ·  {CloseDayKey} closes without a count  ·  {CancelKey} keeps selling"
        : $"{CloseDayKey} again closes the day  ·  {CancelKey} keeps selling  ·  a close cannot be undone";

    /// <summary>A drawer difference in words: "counted exactly right", "over by ₹5.00", "short by ₹20.00".</summary>
    private static string Drawer(decimal difference) => difference switch
    {
        0m => "counted exactly right",
        > 0m => $"over by {Show.Money(difference)}",
        _ => $"short by {Show.Money(-difference)}",
    };

    /// <param name="counted">
    /// The cash counted, once it has been. Until then the drawer figure is left off, so the count is
    /// made without the answer on screen.
    /// </param>
    private static List<DayCloseRow> CloseRows(DayCloseSummary day, decimal? counted = null)
    {
        var rows = new List<DayCloseRow>
        {
            new("Bills", day.InvoiceCount.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("en-IN"))),
            new("Net sales", Show.Money(day.NetSales)),
        };

        if (day.CollectedCredit)
            rows.Add(new("Khata collected", Show.Money(day.CreditCollected)));

        // The count goes with the words, not the figure: the figures are set in the figure font,
        // and "₹189.00 on 1 return" there read as typewriting.
        if (day.HadReturns)
            rows.Add(new($"Refunded on {Plural.Of(day.ReturnsCount, "return")}", Show.Money(day.ReturnsValue)));

        if (day.CashPaidOut != 0m)
            rows.Add(new("Paid out of the drawer", Show.Money(day.CashPaidOut)));

        if (day.CashPaidIn != 0m)
            rows.Add(new("Put into the drawer", Show.Money(day.CashPaidIn)));

        if (counted is not { } count)
        {
            rows.Add(new("Cash in the drawer", "count it first", Emphasis: true));
            return rows;
        }

        rows.Add(new("Cash counted", Show.Money(count)));
        rows.Add(new("Cash expected in the drawer", Show.Money(day.CashExpected)));

        var difference = count - day.CashExpected;

        rows.Add(difference switch
        {
            0m => new DayCloseRow("The drawer is", "exactly right", Emphasis: true),
            > 0m => new DayCloseRow("The drawer is over by", Show.Money(difference), Emphasis: true),
            _ => new DayCloseRow("The drawer is short by", Show.Money(-difference), Emphasis: true),
        });

        return rows;
    }
}
