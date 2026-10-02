using System.Collections.ObjectModel;
using System.Globalization;
using Pos.Core.Configuration;
using Pos.Core.Domain;

namespace Pos.App.ViewModels;

/// <summary>
/// The people who work the till, and what waits for the owner's PIN.
/// </summary>
public sealed partial class OwnerViewModel
{
    private Func<string, string, string?>? _addCashier;
    private Func<string, string?>? _removeCashier;
    private Func<ApprovalSettings, string?>? _applyApprovals;
    private ApprovalSettings _approvals = new();
    private string _discountLimitText = "10";

    /// <summary>
    /// Offers the cashiers and the approvals on this screen. Without it - a lane wired with nowhere
    /// to save them - neither is shown.
    /// </summary>
    /// <param name="addCashier">Adds a cashier with their PIN, saved for the lane. Returns why not, or null.</param>
    /// <param name="removeCashier">Takes a cashier off. Returns why not, or null.</param>
    /// <param name="applyApprovals">Changes what waits for the owner's PIN, at once and for the lane.</param>
    public void UseTillAccess(
        IReadOnlyList<string> cashiers,
        Func<string, string, string?> addCashier,
        Func<string, string?> removeCashier,
        ApprovalSettings approvals,
        Func<ApprovalSettings, string?> applyApprovals)
    {
        ArgumentNullException.ThrowIfNull(cashiers);

        _addCashier = addCashier ?? throw new ArgumentNullException(nameof(addCashier));
        _removeCashier = removeCashier ?? throw new ArgumentNullException(nameof(removeCashier));
        _applyApprovals = applyApprovals ?? throw new ArgumentNullException(nameof(applyApprovals));
        _approvals = (approvals ?? throw new ArgumentNullException(nameof(approvals))).Copy();

        if (_approvals.DiscountAbovePercent is { } limit)
            _discountLimitText = limit.ToString("0.##", CultureInfo.InvariantCulture);

        Cashiers.Clear();

        foreach (var name in cashiers)
            Cashiers.Add(name);

        Raise(nameof(CanManageAccess));
        Raise(nameof(HasCashiers));
        Raise(nameof(DiscountLimitText));
        RaiseApprovals();
    }

    // ---- Cashiers ------------------------------------------------------------------------------

    /// <summary>Whether cashiers and approvals can be changed from this screen at all.</summary>
    public bool CanManageAccess => _addCashier is not null;

    public ObservableCollection<string> Cashiers { get; } = [];

    public bool HasCashiers => Cashiers.Count > 0;

    /// <summary>
    /// Adds a cashier with their own PIN. The PIN is typed twice, because it is never shown and a
    /// mistyped one would keep them off their own till.
    /// </summary>
    /// <returns>Null when it worked, or why it did not.</returns>
    public string? AddCashier(string? name, string? pin, string? pinAgain)
    {
        if (_addCashier is null)
            return "This lane has nowhere to keep its cashiers.";

        if (CashierRules.NameProblem(name, Cashiers) is { } nameProblem)
            return nameProblem;

        if (DashboardLock.Rejection(pin) is { } pinProblem)
            return pinProblem;

        if (pin != pinAgain)
            return "The two PINs do not match.";

        var trimmed = name!.Trim();

        if (_addCashier(trimmed, pin!) is { } refused)
            return refused;

        Cashiers.Add(trimmed);
        Raise(nameof(HasCashiers));

        Status = Cashiers.Count == 1
            ? $"{trimmed} added. From now on the till asks whoever is on it to sign on with their own PIN."
            : $"{trimmed} added, with {Plural.Of(Cashiers.Count, "cashier")} on this lane.";

        return null;
    }

    /// <summary>Takes a cashier off. Their past sales keep their name.</summary>
    public string? RemoveCashier(string? name)
    {
        if (_removeCashier is null)
            return "This lane has nowhere to keep its cashiers.";

        if (string.IsNullOrWhiteSpace(name) || !Cashiers.Contains(name))
            return "Pick the cashier to take off first.";

        if (_removeCashier(name) is { } refused)
            return refused;

        Cashiers.Remove(name);
        Raise(nameof(HasCashiers));

        Status = Cashiers.Count == 0
            ? $"{name} taken off. With no cashiers left, the till takes a typed name again."
            : $"{name} taken off. Their past sales keep their name.";

        return null;
    }

    // ---- What waits for the owner's PIN --------------------------------------------------------

    /// <summary>Whether anything can be asked of the owner: only once the owner's PIN is set.</summary>
    public bool CanAskForApproval => CanManageAccess && IsPinSet;

    /// <summary>Said beside the choices while they cannot take effect.</summary>
    public string ApprovalNote => IsPinSet
        ? "The till asks for the owner's PIN before any of these, and records whether it was given."
        : "Set the owner's PIN above first. Without it nobody could approve anything, so the till asks for nothing.";

    public bool ApproveVoids
    {
        get => _approvals.Voids;
        set => ChangeApprovals(a => a.Voids = value);
    }

    public bool ApproveDiscounts
    {
        get => _approvals.DiscountAbovePercent is not null;
        set => ChangeApprovals(a => a.DiscountAbovePercent = value ? ParsedLimit() ?? 10m : null);
    }

    /// <summary>The share of a line a discount may take before the owner is asked, as typed.</summary>
    public string DiscountLimitText
    {
        get => _discountLimitText;
        set => Set(ref _discountLimitText, value ?? string.Empty);
    }

    public bool ApproveCashRefunds
    {
        get => _approvals.CashRefunds;
        set => ChangeApprovals(a => a.CashRefunds = value);
    }

    public bool ApproveCashOut
    {
        get => _approvals.CashOut;
        set => ChangeApprovals(a => a.CashOut = value);
    }

    public bool ApproveCloseDay
    {
        get => _approvals.CloseDay;
        set => ChangeApprovals(a => a.CloseDay = value);
    }

    /// <summary>Saves the share a discount may take before the owner is asked.</summary>
    public string? SaveDiscountLimit()
    {
        if (ParsedLimit() is not { } limit)
            return $"'{_discountLimitText}' is not a share of the line. Type 0 for every discount, or up to 99.";

        // Leaving the box without changing it is not a change, and is not saved again.
        if (limit == _approvals.DiscountAbovePercent)
            return null;

        return ChangeApprovals(a => a.DiscountAbovePercent = limit);
    }

    private decimal? ParsedLimit() =>
        decimal.TryParse(_discountLimitText.Trim().TrimEnd('%'), NumberStyles.Number, CultureInfo.InvariantCulture, out var limit)
        && limit >= 0m && limit < 100m
            ? limit
            : null;

    private string? ChangeApprovals(Action<ApprovalSettings> edit)
    {
        if (_applyApprovals is null)
            return "This lane has nowhere to keep what waits for the owner.";

        var next = _approvals.Copy();
        edit(next);

        if (next.Problem() is { } problem)
        {
            Status = problem;
            RaiseApprovals();
            return problem;
        }

        if (_applyApprovals(next) is { } refused)
        {
            Status = refused;
            RaiseApprovals();
            return refused;
        }

        _approvals = next;

        if (next.DiscountAbovePercent is { } limit)
            DiscountLimitText = limit.ToString("0.##", CultureInfo.InvariantCulture);

        RaiseApprovals();

        Status = next.AnyOn
            ? $"The till now asks for the owner's PIN before: {Describe(next)}."
            : "The till asks the owner before nothing.";

        return null;
    }

    /// <summary>What waits for the owner, in words.</summary>
    private static string Describe(ApprovalSettings approvals)
    {
        var asked = new List<string>();

        if (approvals.Voids)
            asked.Add("voiding a bill");

        if (approvals.DiscountAbovePercent is { } limit)
            asked.Add(limit == 0m ? "any discount typed by hand" : $"a discount over {limit:0.##}% of a line");

        if (approvals.CashRefunds)
            asked.Add("a refund in cash");

        if (approvals.CashOut)
            asked.Add("cash taken out of the drawer");

        if (approvals.CloseDay)
            asked.Add("closing the day");

        return asked.Count switch
        {
            1 => asked[0],
            _ => string.Join(", ", asked.Take(asked.Count - 1)) + " and " + asked[^1],
        };
    }

    private void RaiseApprovals()
    {
        Raise(nameof(CanAskForApproval));
        Raise(nameof(ApprovalNote));
        Raise(nameof(ApproveVoids));
        Raise(nameof(ApproveDiscounts));
        Raise(nameof(ApproveCashRefunds));
        Raise(nameof(ApproveCashOut));
        Raise(nameof(ApproveCloseDay));
    }
}
