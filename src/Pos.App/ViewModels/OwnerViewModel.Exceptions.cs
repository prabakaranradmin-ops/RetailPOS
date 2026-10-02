using System.Collections.ObjectModel;
using Pos.Core.Analytics;
using Pos.Core.Domain;

namespace Pos.App.ViewModels;

/// <summary>One person's exceptions over the period, as the owner's screen shows them.</summary>
public sealed record ExceptionTotalsRow(string Cashier, string Voids, string Discounts, string CashRefunds, string CashOut, string Refused, string OverKhataLimit = "—");

/// <summary>One exception, as the owner's screen lists it.</summary>
/// <param name="Owner">"approved", "refused", or empty when the lane did not ask.</param>
public sealed record ExceptionRow(DateTimeOffset At, string Who, string What, string Amount, string Owner);

/// <summary>
/// What happened at the till that an owner asks about: voids, discounts typed by hand, cash refunds,
/// cash taken out, and PINs that were asked for and not given - by who was on the till.
/// </summary>
public sealed partial class OwnerViewModel
{
    private string _exceptionsLine = string.Empty;

    public ObservableCollection<ExceptionTotalsRow> ExceptionsByCashier { get; } = [];

    public ObservableCollection<ExceptionRow> LatestExceptions { get; } = [];

    public string ExceptionsLine
    {
        get => _exceptionsLine;
        private set => Set(ref _exceptionsLine, value);
    }

    public bool HasExceptions => ExceptionsByCashier.Count > 0;

    private void FillExceptions(DashboardData d)
    {
        var exceptions = d.Exceptions;

        ExceptionsByCashier.Clear();
        LatestExceptions.Clear();

        foreach (var c in exceptions.ByCashier)
        {
            ExceptionsByCashier.Add(new ExceptionTotalsRow(
                c.Cashier,
                Pair(c.Voids, c.Voided),
                Pair(c.Discounts, c.Discounted),
                Pair(c.CashRefunds, c.CashRefunded),
                Pair(c.CashOuts, c.CashTakenOut),
                c.Refused == 0 ? "—" : c.Refused.ToString("N0", Indian),
                Pair(c.OverKhataLimit, c.OverKhataLimitValue)));
        }

        foreach (var e in exceptions.Latest)
        {
            LatestExceptions.Add(new ExceptionRow(
                e.At,
                e.Cashier ?? TillExceptions.Nobody,
                Describe(e),
                e.Amount is { } amount ? Show.Money(amount) : string.Empty,
                e.Approved switch { true => "approved", false => "refused", null => string.Empty }));
        }

        ExceptionsLine = Summarise(d);
        Raise(nameof(HasExceptions));
    }

    private static string Pair(int count, decimal value) =>
        count == 0 ? "—" : $"{count.ToString("N0", Indian)} · {Show.Money(value)}";

    /// <summary>One line on the period: what happened, or that nothing did, or that nothing has been recorded yet.</summary>
    private static string Summarise(DashboardData d)
    {
        var exceptions = d.Exceptions;

        if (exceptions.RecordedSince is not { } since)
            return "Nothing recorded yet. From now on every void, discount typed by hand, cash refund and cash taken out of the drawer is listed here, with who did it and whether the owner approved.";

        // A period that starts before the record does is not a period in which nothing happened.
        var partly = since > d.From ? $" Recorded since {Show.Date(since)}, so earlier days are not here." : string.Empty;

        if (!exceptions.Any)
            return $"Nothing out of the ordinary in this period.{partly}";

        var totals = exceptions.ByCashier;
        var parts = new List<string>();

        void Add(int count, string one, string many, decimal value)
        {
            if (count > 0)
                parts.Add($"{Plural.Of(count, one, many)}, {Show.Money(value)}");
        }

        Add(totals.Sum(c => c.Voids), "void", "voids", totals.Sum(c => c.Voided));
        Add(totals.Sum(c => c.Discounts), "discount typed by hand", "discounts typed by hand", totals.Sum(c => c.Discounted));
        Add(totals.Sum(c => c.CashRefunds), "cash refund", "cash refunds", totals.Sum(c => c.CashRefunded));
        Add(totals.Sum(c => c.CashOuts), "cash out of the drawer", "cash out of the drawer", totals.Sum(c => c.CashTakenOut));
        Add(totals.Sum(c => c.OverKhataLimit), "khata past its limit", "khatas past their limits", totals.Sum(c => c.OverKhataLimitValue));

        var refused = totals.Sum(c => c.Refused);

        if (refused > 0)
            parts.Add($"{Plural.Of(refused, "PIN", "PINs")} asked for and not given");

        return string.Join("; ", parts) + "." + partly;
    }

    /// <summary>An exception in words.</summary>
    private static string Describe(TillEvent e) => e.Kind switch
    {
        TillEventKind.Voided => $"Voided {e.Reference}",
        TillEventKind.Discounted => e.Detail is { } detail ? Capital(detail) : $"Discount on {e.Reference}",
        TillEventKind.CashRefunded => $"Cash refund {e.Reference}{(e.Detail is { } against ? $", {Lower(against)}" : string.Empty)}",
        TillEventKind.CashTakenOut => e.Detail is { } note ? $"{e.Reference}: {note}" : $"{e.Reference}",
        TillEventKind.ApprovalRefused => e.Detail ?? "The owner's PIN was not given",
        TillEventKind.SignOnRefused => $"Sign-on refused: {Lower(e.Detail ?? "wrong PIN")}",
        TillEventKind.OverKhataLimit => $"Past the khata limit: {Lower(e.Detail ?? e.Reference ?? "a customer")}",
        _ => e.Kind.ToString(),
    };

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Lower(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];
}
