using Pos.Core.Hardware.Drawer;
using Pos.Core.Logging;

namespace Pos.Core.Domain;

/// <summary>
/// The kinds of cash that go into or out of the drawer other than through a sale, as they are
/// stored and as the day-end report groups them.
/// </summary>
public static class DrawerKinds
{
    public const string SupplierPayment = "SupplierPayment";
    public const string Refund = "Refund";
    public const string Float = "Float";
    public const string Expense = "Expense";
    public const string CashIn = "CashIn";
    public const string CashOut = "CashOut";
}

/// <summary>Where an expense was paid from.</summary>
public enum ExpensePaidFrom
{
    /// <summary>Cash out of the till: it comes off what the drawer should hold.</summary>
    Drawer = 0,

    /// <summary>The bank, UPI, or cash that never went through the till.</summary>
    Outside = 1,
}

/// <summary>What the shop spends money on that is not stock, in the words a shop uses.</summary>
public static class ExpenseCategories
{
    public const string Other = "Other";

    public static IReadOnlyList<string> All { get; } =
    [
        "Tea and snacks",
        "Transport and delivery",
        "Wages",
        "Electricity",
        "Rent",
        "Repairs",
        "Packing material",
        "Cleaning",
        Other,
    ];
}

/// <summary>What is being put into or taken out of the drawer, or spent.</summary>
public enum DrawerEntryKind
{
    /// <summary>The change the day starts with, counted into the drawer.</summary>
    OpeningFloat = 0,

    /// <summary>Something paid for out of the till.</summary>
    ExpenseFromDrawer = 1,

    /// <summary>Something paid for from the bank, UPI or the owner's pocket: recorded, the drawer untouched.</summary>
    ExpenseFromOutside = 2,

    /// <summary>Cash put in: more change from the bank, the owner topping it up.</summary>
    CashIn = 3,

    /// <summary>Cash taken out: to the bank, to the owner, to a safe.</summary>
    CashOut = 4,
}

public static class DrawerEntryKindExtensions
{
    /// <summary>Whether the drawer opens for it: anything that puts cash in or takes it out.</summary>
    public static bool MovesCash(this DrawerEntryKind kind) => kind != DrawerEntryKind.ExpenseFromOutside;

    public static bool IsExpense(this DrawerEntryKind kind) =>
        kind is DrawerEntryKind.ExpenseFromDrawer or DrawerEntryKind.ExpenseFromOutside;

    public static string Label(this DrawerEntryKind kind) => kind switch
    {
        DrawerEntryKind.OpeningFloat => "Opening float",
        DrawerEntryKind.ExpenseFromDrawer => "Expense, paid from the drawer",
        DrawerEntryKind.ExpenseFromOutside => "Expense, paid by bank, UPI or own cash",
        DrawerEntryKind.CashIn => "Put cash in",
        DrawerEntryKind.CashOut => "Take cash out",
        _ => kind.ToString(),
    };
}

/// <summary>An entry about to be recorded.</summary>
/// <param name="Amount">Rupees, always positive; the kind says which way it goes.</param>
/// <param name="Category">What an expense was for. Required for an expense, ignored otherwise.</param>
/// <param name="Note">Free text. Required when cash is taken out: somebody will ask where it went.</param>
public sealed record DrawerEntry(DrawerEntryKind Kind, decimal Amount, string? Category = null, string? Note = null)
{
    /// <summary>Why this entry cannot be recorded, or null when it can.</summary>
    public string? Problem()
    {
        if (Amount <= 0m)
            return "The amount has to be more than nothing.";

        if (decimal.Round(Amount, 2) != Amount)
            return "An amount cannot be finer than a paisa.";

        if (Amount > 10_00_000m)
            return "That is more than any drawer holds. Check the amount.";

        if (Kind.IsExpense() && string.IsNullOrWhiteSpace(Category))
            return "Say what the expense was for.";

        if (Kind == DrawerEntryKind.CashOut && string.IsNullOrWhiteSpace(Note))
            return "Say where the cash is going - to the bank, to the owner - so the drawer can be explained.";

        return null;
    }

    /// <summary>What it does to the drawer: positive in, negative out, zero for an expense paid from outside.</summary>
    public decimal DrawerChange => Kind switch
    {
        DrawerEntryKind.OpeningFloat or DrawerEntryKind.CashIn => Amount,
        DrawerEntryKind.ExpenseFromDrawer or DrawerEntryKind.CashOut => -Amount,
        _ => 0m,
    };
}

/// <summary>An expense, wherever it was paid from.</summary>
public sealed record Expense(
    long Id,
    string LaneId,
    DateTimeOffset SpentAt,
    string Category,
    decimal Amount,
    ExpensePaidFrom PaidFrom,
    string? Note,
    string? CashierName);

/// <summary>One category's spending over a period.</summary>
public sealed record ExpenseTotal(string Category, int Count, decimal Amount);

/// <summary>What recording an entry came to.</summary>
/// <param name="FloatSinceClose">The float recorded since the last close, this entry included - what the day started with.</param>
public sealed record DrawerEntryRecorded(DrawerEntry Entry, decimal FloatSinceClose, Expense? Expense);

/// <summary>Where cash put in, taken out and spent is recorded.</summary>
public interface ICashDrawerStore
{
    /// <summary>
    /// Records an entry: a cash movement for anything that moves cash, and an expense for an
    /// expense, in one transaction.
    /// </summary>
    DrawerEntryRecorded Record(DrawerEntry entry, string laneId, DateTimeOffset at, string? cashierName);

    /// <summary>The float recorded since this lane's last close.</summary>
    decimal FloatSinceLastClose(string laneId);

    /// <summary>Expenses in a window, newest first.</summary>
    IReadOnlyList<Expense> Expenses(DateTimeOffset from, DateTimeOffset to, int limit = 200);

    /// <summary>Expenses in a window by category, most first.</summary>
    IReadOnlyList<ExpenseTotal> ExpenseTotals(DateTimeOffset from, DateTimeOffset to);
}

/// <param name="Drawer">Whether the drawer opened; <c>NoDrawerAttached</c> for an expense paid from outside.</param>
public sealed record DrawerEntryResult(DrawerEntryRecorded Recorded, DrawerKickResult Drawer);

/// <summary>
/// Cash in and out of the drawer at the counter, and expenses: recorded first, then the drawer.
/// </summary>
/// <remarks>
/// Shaped like the other counter services: the books are written before the hardware is touched,
/// so a drawer that will not open is a message, never an entry lost.
/// </remarks>
public sealed class CashDrawerService(
    ICashDrawerStore store,
    IDrawerService drawer,
    TimeProvider? clock = null,
    IPosLog? log = null,
    Func<string?>? cashier = null)
{
    private readonly ICashDrawerStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IDrawerService _drawer = drawer ?? throw new ArgumentNullException(nameof(drawer));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly IPosLog _log = log ?? NullLog.Instance;

    public decimal FloatSinceLastClose(string laneId) => _store.FloatSinceLastClose(laneId);

    /// <exception cref="ArgumentException">The entry is not one that can be recorded.</exception>
    public DrawerEntryResult Record(DrawerEntry entry, string laneId)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.Problem() is { } problem)
            throw new ArgumentException(problem, nameof(entry));

        var recorded = _store.Record(entry, laneId, _clock.GetLocalNow(), cashier?.Invoke());

        _log.Info("drawer", $"{entry.Kind} of {entry.Amount:0.00} on {laneId}{(entry.Category is { } c ? $" ({c})" : "")}");

        var kicked = entry.Kind.MovesCash() && _drawer.IsConfigured
            ? _drawer.Kick()
            : DrawerKickResult.NoDrawerAttached;

        if (kicked == DrawerKickResult.Failed)
            _log.Warn("drawer", $"the drawer did not open for {entry.Kind} ({_drawer.Name})");

        return new DrawerEntryResult(recorded, kicked);
    }
}
