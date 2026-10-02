using System.Globalization;
using System.Text;

namespace Pos.Core.Domain;

/// <summary>What a line of a khata was.</summary>
public enum KhataEntryKind
{
    /// <summary>Goods bought on credit, on a bill.</summary>
    Bought = 0,

    /// <summary>Money paid back.</summary>
    Paid = 1,

    /// <summary>Goods brought back and taken off the khata, on a credit note.</summary>
    Returned = 2,
}

/// <summary>One line of a customer's khata, as the books have it.</summary>
/// <param name="Reference">The bill or credit note number; empty for a payment.</param>
/// <param name="Amount">Always positive: what was bought, paid back or returned.</param>
/// <param name="Tender">How a payment was made; null for the other kinds.</param>
public sealed record KhataEntry(DateTimeOffset At, KhataEntryKind Kind, string Reference, decimal Amount, TenderType? Tender = null)
{
    /// <summary>What it did to what is owed: up for a purchase, down for a payment or a return.</summary>
    public decimal Change => Kind == KhataEntryKind.Bought ? Amount : -Amount;

    /// <summary>The shop's day it happened on, in the time it was recorded in.</summary>
    public DateOnly Day => DateOnly.FromDateTime(At.DateTime);
}

/// <summary>A line of a statement, with what was owed once it was entered.</summary>
public sealed record KhataLine(KhataEntry Entry, decimal BalanceAfter);

/// <summary>
/// How old what is owed is: the unpaid part of each credit bill, by how many days ago it was bought.
/// </summary>
/// <remarks>
/// Payments and returns settle the oldest bills first, the way a shopkeeper and a customer both
/// reckon a khata. What is still owed is therefore the newest credit bills, and the oldest of those
/// is how long the customer has been behind.
/// </remarks>
public sealed record KhataAgeing(decimal UpTo30Days, decimal Days31To60, decimal Days61To90, decimal Over90Days, DateOnly? OldestUnpaid)
{
    public static readonly KhataAgeing Nothing = new(0m, 0m, 0m, 0m, null);

    public decimal Total => UpTo30Days + Days31To60 + Days61To90 + Over90Days;

    /// <summary>Ages what is owed as at a day, over the credit bills up to it.</summary>
    public static KhataAgeing Of(IEnumerable<KhataEntry> entries, decimal owed, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (owed <= 0m)
            return Nothing;

        decimal upTo30 = 0m, upTo60 = 0m, upTo90 = 0m, older = 0m;
        DateOnly? oldest = null;
        var left = owed;

        foreach (var bill in entries
                     .Where(e => e.Kind == KhataEntryKind.Bought && e.Day <= asOf)
                     .OrderByDescending(e => e.At))
        {
            var unpaid = Math.Min(left, bill.Amount);
            var days = asOf.DayNumber - bill.Day.DayNumber;

            switch (days)
            {
                case <= 30: upTo30 += unpaid; break;
                case <= 60: upTo60 += unpaid; break;
                case <= 90: upTo90 += unpaid; break;
                default: older += unpaid; break;
            }

            oldest = bill.Day;
            left -= unpaid;

            if (left <= 0m)
                break;
        }

        return new KhataAgeing(upTo30, upTo60, upTo90, older, oldest);
    }

    /// <summary>How many days the oldest unpaid bill has waited, as at a day.</summary>
    public int? DaysWaiting(DateOnly asOf) => OldestUnpaid is { } day ? asOf.DayNumber - day.DayNumber : null;
}

/// <summary>
/// A customer's khata over a period: what they owed at the start, each bill, payment and return in
/// it with the balance after, and what they owe at the end - and how old that is.
/// </summary>
/// <remarks>
/// Built from the same books as the balance the till shows, so the closing figure is always the
/// balance. Nothing about a statement is stored: it is read again whenever it is asked for.
/// </remarks>
public sealed record KhataStatement(
    Customer Customer,
    DateOnly From,
    DateOnly To,
    decimal Opening,
    IReadOnlyList<KhataLine> Lines,
    decimal Closing,
    KhataAgeing Ageing,
    KhataEntry? LastPayment)
{
    private static readonly CultureInfo Figures = CultureInfo.GetCultureInfo("en-IN");

    /// <summary>The most lines a statement at the counter carries; older ones go into the opening balance.</summary>
    public const int MostLinesAtTheCounter = 60;

    public decimal Bought => Lines.Where(l => l.Entry.Kind == KhataEntryKind.Bought).Sum(l => l.Entry.Amount);

    public int Bills => Lines.Count(l => l.Entry.Kind == KhataEntryKind.Bought);

    public decimal Paid => Lines.Where(l => l.Entry.Kind == KhataEntryKind.Paid).Sum(l => l.Entry.Amount);

    public decimal Returned => Lines.Where(l => l.Entry.Kind == KhataEntryKind.Returned).Sum(l => l.Entry.Amount);

    public bool OwesAnything => Closing > 0m;

    /// <summary>A statement over the days given.</summary>
    /// <param name="ledger">Every line of the customer's khata, in any order.</param>
    public static KhataStatement Build(Customer customer, IReadOnlyList<KhataEntry> ledger, DateOnly from, DateOnly to)
    {
        ArgumentNullException.ThrowIfNull(customer);
        ArgumentNullException.ThrowIfNull(ledger);

        if (to < from)
            throw new ArgumentOutOfRangeException(nameof(to), to, $"A statement cannot end ({to:dd-MM-yyyy}) before it starts ({from:dd-MM-yyyy}).");

        var ordered = Ordered(ledger);
        var opening = ordered.Where(e => e.Day < from).Sum(e => e.Change);
        var running = opening;
        var lines = new List<KhataLine>();

        foreach (var entry in ordered.Where(e => e.Day >= from && e.Day <= to))
        {
            running += entry.Change;
            lines.Add(new KhataLine(entry, running));
        }

        var upTo = ordered.Where(e => e.Day <= to).ToList();

        return new KhataStatement(
            customer,
            from,
            to,
            opening,
            lines,
            running,
            KhataAgeing.Of(upTo, running, to),
            upTo.LastOrDefault(e => e.Kind == KhataEntryKind.Paid));
    }

    /// <summary>
    /// The statement a customer asks for at the counter: everything since they last owed nothing,
    /// so it adds up to what they owe from zero.
    /// </summary>
    /// <remarks>
    /// Kept to <paramref name="mostLines"/> lines, the oldest folded into the opening balance, so a
    /// customer who has run a khata for years does not get a metre of paper.
    /// </remarks>
    public static KhataStatement SinceLastClear(Customer customer, IReadOnlyList<KhataEntry> ledger, DateOnly asOf, int mostLines = MostLinesAtTheCounter)
    {
        ArgumentNullException.ThrowIfNull(ledger);

        var ordered = Ordered(ledger).Where(e => e.Day <= asOf).ToList();

        if (ordered.Count == 0)
            return Build(customer, ledger, asOf, asOf);

        // The last time the balance came down to nothing: the statement starts the day after.
        var start = 0;
        var running = 0m;

        for (var i = 0; i < ordered.Count; i++)
        {
            running += ordered[i].Change;

            if (running <= 0m)
                start = i + 1;
        }

        // Clear now: nothing to show, even if it was bought and paid off earlier today - a statement
        // over today would bring those lines back and add them up to nothing.
        if (start == ordered.Count)
            return new KhataStatement(customer, asOf, asOf, 0m, [], 0m, KhataAgeing.Nothing, ordered.LastOrDefault(e => e.Kind == KhataEntryKind.Paid));

        if (ordered.Count - start > mostLines)
            start = ordered.Count - Math.Max(1, mostLines);

        return Build(customer, ledger, ordered[start].Day, asOf);
    }

    /// <summary>
    /// The statement as a message to send the customer - WhatsApp or SMS, pasted by the shop.
    /// </summary>
    public string Message(string? shopName, UpiPayee? upi = null)
    {
        var shop = string.IsNullOrWhiteSpace(shopName) ? "The shop" : shopName.Trim();
        var who = Customer.Name ?? Customer.MobileNo;
        var text = new StringBuilder();

        text.Append(CultureInfo.InvariantCulture, $"{shop}: khata for {who}, {Date(From)} to {Date(To)}.\n");

        if (Opening != 0m)
            text.Append(CultureInfo.InvariantCulture, $"Owed on {Date(From)}: Rs {Money(Opening)}\n");

        if (Bills > 0)
            text.Append(CultureInfo.InvariantCulture, $"Bought on khata: Rs {Money(Bought)} ({Plural.Of(Bills, "bill")})\n");

        if (Paid > 0m)
            text.Append(CultureInfo.InvariantCulture, $"Paid: Rs {Money(Paid)}\n");

        if (Returned > 0m)
            text.Append(CultureInfo.InvariantCulture, $"Returned: Rs {Money(Returned)}\n");

        text.Append(CultureInfo.InvariantCulture, $"Owed now: Rs {Money(Math.Max(0m, Closing))}\n");

        if (Ageing.OldestUnpaid is { } oldest)
            text.Append(CultureInfo.InvariantCulture, $"Oldest unpaid bill: {Date(oldest)}\n");

        if (OwesAnything && upi is not null)
            text.Append(CultureInfo.InvariantCulture, $"Pay by UPI to {upi.Id} ({upi.Name}).\n");

        text.Append("Thank you.");
        return text.ToString();
    }

    /// <summary>
    /// A short reminder of what is owed and since when - for somebody who has not paid in a while,
    /// where the whole statement would be more than they need to read.
    /// </summary>
    /// <remarks>
    /// Polite and plain, and naming the oldest unpaid bill: "since 12-08-2026, 51 days" is what makes
    /// a reminder a reminder rather than a bill. The shop sends it from its own phone; nothing here
    /// goes anywhere.
    /// </remarks>
    public string Reminder(string? shopName, UpiPayee? upi = null)
    {
        var shop = string.IsNullOrWhiteSpace(shopName) ? "the shop" : shopName.Trim();
        var who = Customer.Name ?? Customer.MobileNo;

        if (!OwesAnything)
            return $"Dear {who}, your khata at {shop} is clear. Thank you.";

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Dear {who}, a reminder from {shop}: Rs {Money(Closing)} is due on your khata");

        if (Ageing.OldestUnpaid is { } oldest)
        {
            var days = To.DayNumber - oldest.DayNumber;
            text.Append(CultureInfo.InvariantCulture, $", unpaid since {Date(oldest)} ({Plural.Of(days, "day")})");
        }

        text.Append(". ");
        text.Append(upi is not null
            ? $"Please pay by UPI to {upi.Id} ({upi.Name}), or at the counter. "
            : "Please pay at the counter when you next come in. ");
        text.Append("Thank you.");

        return text.ToString();
    }

    /// <summary>Oldest first; a purchase before a payment in the same instant, as the balance was walked.</summary>
    private static List<KhataEntry> Ordered(IEnumerable<KhataEntry> ledger) =>
        [.. ledger.OrderBy(e => e.At).ThenByDescending(e => e.Change)];

    private static string Date(DateOnly day) => day.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);

    private static string Money(decimal amount) => amount.ToString("N2", Figures);
}
