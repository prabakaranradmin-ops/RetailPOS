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
            Raise(nameof(IsConfirmingDayClose));
            Raise(nameof(DayCloseKeys));
        }
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
    public string DayCloseKeys => $"{CloseDayKey} again closes the day  ·  {CancelKey} keeps selling  ·  a close cannot be undone";

    private static List<DayCloseRow> CloseRows(DayCloseSummary day)
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

        rows.Add(new("Cash expected in the drawer", Show.Money(day.CashExpected), Emphasis: true));

        return rows;
    }
}
