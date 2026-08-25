using Pos.Core.Tax;

namespace Pos.Core.Domain;

/// <summary>
/// The payments taken against one bill while the cashier is settling it (SRS 2.4). Tracks what is
/// still owed, and what change is due once more cash has been handed over than the bill came to.
/// </summary>
public sealed class TenderBasket
{
    private readonly List<Tender> _tenders = [];

    public TenderBasket(decimal amountDue)
    {
        if (amountDue < 0m)
            throw new ArgumentOutOfRangeException(nameof(amountDue), amountDue, "A bill cannot come to less than nothing.");

        AmountDue = Money.ToPresentation(amountDue);
    }

    public decimal AmountDue { get; }

    public IReadOnlyList<Tender> Tenders => _tenders;

    public decimal TotalTendered => Money.ToPresentation(_tenders.Sum(t => t.Amount));

    /// <summary>Still to collect. Zero once the bill is covered — never negative.</summary>
    public decimal Remaining => Math.Max(0m, Money.ToPresentation(AmountDue - TotalTendered));

    /// <summary>
    /// Change to hand back. Only ever non-zero when cash was over-tendered, because no other
    /// tender is allowed to exceed what is owed.
    /// </summary>
    public decimal ChangeDue => Math.Max(0m, Money.ToPresentation(TotalTendered - AmountDue));

    public bool IsSettled => TotalTendered >= AmountDue;

    public bool IsEmpty => _tenders.Count == 0;

    /// <summary>
    /// Takes a payment. Cash may exceed what is owed, which produces change; anything else must be
    /// for the remaining balance or less, since there is no way to give change on a card.
    /// </summary>
    public Tender Add(TenderType type, decimal amount, string? referenceNo = null)
    {
        if (!Enum.IsDefined(type))
            throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown tender type.");

        var rounded = Money.ToPresentation(amount);

        if (rounded <= 0m)
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "A payment must be for more than nothing.");

        if (Remaining == 0m)
            throw new InvalidOperationException("The bill is already covered; remove a payment before adding another.");

        if (!type.AllowsOverTender() && rounded > Remaining)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                amount,
                $"A {type} payment cannot exceed the {Remaining:0.00} still owed — only cash gives change.");
        }

        var tender = new Tender(type, rounded, referenceNo);
        _tenders.Add(tender);
        return tender;
    }

    public void RemoveAt(int index)
    {
        if (index < 0 || index >= _tenders.Count)
            throw new ArgumentOutOfRangeException(nameof(index), index, "No such payment.");

        _tenders.RemoveAt(index);
    }

    public void Clear() => _tenders.Clear();

    /// <summary>True when a tender of this type has already been taken.</summary>
    public bool Contains(TenderType type) => _tenders.Any(t => t.Type == type);

    /// <summary>Total taken under one tender type, for the drawer decision and the day's summary.</summary>
    public decimal TotalOf(TenderType type) =>
        Money.ToPresentation(_tenders.Where(t => t.Type == type).Sum(t => t.Amount));
}
