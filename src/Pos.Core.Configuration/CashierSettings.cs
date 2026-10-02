using System.Text.Json.Serialization;

namespace Pos.Core.Configuration;

/// <summary>A person who works the till, and the PIN they sign on with.</summary>
/// <remarks>
/// Each person's own PIN, never a shared one. A shop password everybody knows attributes nothing;
/// a PIN only one person knows puts their name on every sale they ring up, which is what makes a
/// drawer that is short, or a void nobody remembers, answerable.
/// </remarks>
public sealed class CashierSettings
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("pin")]
    public PinCredential? Pin { get; set; }
}

/// <summary>What the till may do only with the owner's PIN.</summary>
public enum Guarded
{
    /// <summary>Cancelling a sale that has been paid for.</summary>
    Void = 0,

    /// <summary>Money off a line, typed by the cashier, above the share the owner set.</summary>
    Discount = 1,

    /// <summary>Paying a return back in cash from the drawer.</summary>
    CashRefund = 2,

    /// <summary>Taking cash out of the drawer: cash out, or an expense paid from it.</summary>
    CashOut = 3,

    /// <summary>Closing the day.</summary>
    CloseDay = 4,

    /// <summary>
    /// A sale on the khata past the customer's limit. Always asked, not a switch: the owner set the
    /// limit, and going past it is theirs to say.
    /// </summary>
    OverKhataLimit = 5,
}

/// <summary>
/// Which of the till's riskier actions wait for the owner's PIN. Nothing does unless the owner says.
/// </summary>
/// <remarks>
/// Off by default, because a one-person shop has nobody to ask, and a lane already trading should
/// not start refusing its own cashier the morning after an update. It takes effect only once the
/// owner's PIN is set: an approval nobody can give would stop the till.
/// </remarks>
public sealed class ApprovalSettings
{
    [JsonPropertyName("voids")]
    public bool Voids { get; set; }

    /// <summary>
    /// A typed discount on a line above this share of the line asks for the owner. 0 asks for every
    /// one; null for none. Offers the shop set up are never asked about: the owner made those.
    /// </summary>
    [JsonPropertyName("discountAbovePercent")]
    public decimal? DiscountAbovePercent { get; set; }

    [JsonPropertyName("cashRefunds")]
    public bool CashRefunds { get; set; }

    [JsonPropertyName("cashOut")]
    public bool CashOut { get; set; }

    [JsonPropertyName("closeDay")]
    public bool CloseDay { get; set; }

    /// <summary>Whether anything at all waits for the owner.</summary>
    [JsonIgnore]
    public bool AnyOn => Voids || DiscountAbovePercent is not null || CashRefunds || CashOut || CloseDay;

    /// <summary>Whether this action waits for the owner's PIN.</summary>
    /// <param name="discountPercent">For a discount: how much of the line it takes off, as a percentage.</param>
    public bool Guards(Guarded action, decimal discountPercent = 0m) => action switch
    {
        Guarded.Void => Voids,
        Guarded.Discount => DiscountAbovePercent is { } limit && discountPercent > limit,
        Guarded.CashRefund => CashRefunds,
        Guarded.CashOut => CashOut,
        Guarded.CloseDay => CloseDay,
        Guarded.OverKhataLimit => true,
        _ => false,
    };

    /// <summary>Why these settings cannot be used, or null when they can.</summary>
    public string? Problem() =>
        DiscountAbovePercent is { } limit && (limit < 0m || limit >= 100m)
            ? $"discountAbovePercent is {limit}. Use 0 to ask about every discount, or a share below 100."
            : null;

    public ApprovalSettings Copy() => new()
    {
        Voids = Voids,
        DiscountAbovePercent = DiscountAbovePercent,
        CashRefunds = CashRefunds,
        CashOut = CashOut,
        CloseDay = CloseDay,
    };
}

/// <summary>The rules for the list of cashiers.</summary>
public static class CashierRules
{
    /// <summary>Long enough for a full name, short enough to print on the day-end report.</summary>
    public const int MaximumNameLength = 30;

    /// <summary>Why this name cannot be added, or null when it can.</summary>
    public static string? NameProblem(string? name, IEnumerable<string> existing)
    {
        var trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
            return "Type the cashier's name.";

        if (trimmed.Length > MaximumNameLength)
            return $"A name of up to {MaximumNameLength} letters, please.";

        if (existing.Any(e => string.Equals(e.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)))
            return $"There is already a cashier called {trimmed}.";

        return null;
    }

    /// <summary>Why this list cannot be used, or null when it can.</summary>
    public static string? Problem(IReadOnlyList<CashierSettings> cashiers)
    {
        var seen = new List<string>();

        foreach (var cashier in cashiers)
        {
            if (NameProblem(cashier.Name, seen) is { } problem)
                return problem;

            if (cashier.Pin?.IsUsable != true)
                return $"{cashier.Name.Trim()} has no usable PIN. Remove them on the owner's screen and add them again.";

            seen.Add(cashier.Name);
        }

        return null;
    }
}
