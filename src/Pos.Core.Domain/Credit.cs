namespace Pos.Core.Domain;

/// <summary>Money a customer paid back against what they owe on store credit.</summary>
/// <param name="CustomerId">Null once the customer has been forgotten; the money was still received.</param>
public sealed record CreditPayment(
    long Id,
    long? CustomerId,
    string LaneId,
    DateTimeOffset ReceivedAt,
    TenderType Tender,
    decimal Amount,
    string? CashierName);

/// <summary>One line of a customer's khata: something bought on credit, or money paid back.</summary>
/// <param name="Change">Positive when they bought on credit, negative when they paid back.</param>
/// <param name="BalanceAfter">What they owed once this line was entered.</param>
public sealed record CreditMovement(DateTimeOffset At, string Description, decimal Change, decimal BalanceAfter);

/// <summary>A customer who owes the shop something, for the list of who owes what.</summary>
public sealed record CustomerBalance(long CustomerId, string MobileNo, string? Name, decimal Owed, DateTimeOffset? LastCreditSale)
{
    public string Label => string.IsNullOrWhiteSpace(Name) ? MobileNo : Name;
}

/// <summary>
/// Customer credit: what customers owe, and taking it back.
/// </summary>
/// <remarks>
/// No balance is ever stored. What a customer owes is their store-credit payments on bills that were
/// not voided, less what they have paid back — read from the books every time, so voiding a credit
/// sale takes it off what they owe without anything having to remember to.
/// </remarks>
public interface ICreditStore
{
    /// <summary>What the customer owes now. Negative if the shop owes them.</summary>
    decimal Balance(long customerId);

    /// <summary>Everybody who owes something, most first.</summary>
    IReadOnlyList<CustomerBalance> Owing(int limit = 200);

    /// <summary>
    /// Records a repayment. Refuses nothing owed, more than is owed, fractions of a paisa, and any
    /// tender that is not money: a debt is not paid off with more credit or with points.
    /// </summary>
    CreditPayment Collect(long customerId, decimal amount, TenderType tender, string laneId, DateTimeOffset receivedAt, string? cashierName);

    /// <summary>The customer's khata, newest first, each line with the balance after it.</summary>
    IReadOnlyList<CreditMovement> History(long customerId, int limit = 50);
}
