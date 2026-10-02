namespace Pos.Core.Domain;

/// <summary>Something that happened at the till that the owner may want to ask about.</summary>
public enum TillEventKind
{
    /// <summary>A cashier signed on with their PIN.</summary>
    SignedOn = 0,

    /// <summary>A cashier's PIN was wrong three times running.</summary>
    SignOnRefused = 1,

    /// <summary>A paid sale was cancelled.</summary>
    Voided = 2,

    /// <summary>Money was taken off a line by hand.</summary>
    Discounted = 3,

    /// <summary>A return was paid back in cash.</summary>
    CashRefunded = 4,

    /// <summary>Cash went out of the drawer: cash out, or an expense paid from it.</summary>
    CashTakenOut = 5,

    /// <summary>The day was closed.</summary>
    DayClosed = 6,

    /// <summary>The owner's PIN was asked for and not given: wrong three times, or backed out of.</summary>
    ApprovalRefused = 7,

    /// <summary>A sale put on the khata past the customer's limit.</summary>
    OverKhataLimit = 8,
}

/// <param name="Reference">The invoice, credit note or item it was about, where there is one.</param>
/// <param name="Approved">
/// True when the owner's PIN approved it, false when it was asked for and not given, null when the
/// lane did not ask.
/// </param>
public sealed record TillEvent(
    long Id,
    string LaneId,
    DateTimeOffset At,
    TillEventKind Kind,
    string? Cashier,
    string? Reference,
    decimal? Amount,
    bool? Approved,
    string? Detail);

/// <summary>Where the till's exceptions are kept. Written as they happen, never changed afterwards.</summary>
public interface ITillEventStore
{
    void Record(
        string laneId,
        DateTimeOffset at,
        TillEventKind kind,
        string? cashier,
        string? reference = null,
        decimal? amount = null,
        bool? approved = null,
        string? detail = null);

    /// <summary>What happened between two moments, newest first.</summary>
    IReadOnlyList<TillEvent> List(DateTimeOffset from, DateTimeOffset to, int limit = 1000);
}
