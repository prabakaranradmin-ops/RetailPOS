using Pos.Core.Tax;

namespace Pos.Core.Loyalty;

/// <summary>
/// Points arithmetic for the reward scheme (SRS section 4). Pure and stateless, like the tax
/// engine, so it can be pinned against exact expected figures.
/// </summary>
/// <remarks>
/// Redemption is a <b>tender</b>, not a line discount. Points offset what the customer hands over;
/// they never touch a line's price, taxable value or GST split. That is why nothing in this class
/// takes a line or returns one — it only ever sees an invoice total and a balance. SRS section 4
/// reads the same way, putting accrual on "the net bill after any redemption".
/// </remarks>
public static class LoyaltyEngine
{
    /// <summary>
    /// Points the customer may actually spend on this invoice: the lesser of the scheme's
    /// percentage cap and what they hold. Never negative, and never more than the invoice.
    /// </summary>
    public static int MaxRedeemablePoints(decimal invoiceTotal, int balance, LoyaltyRules? rules = null)
    {
        rules ??= LoyaltyRules.Default;
        rules.Validate();

        if (balance <= 0 || invoiceTotal <= 0m)
            return 0;

        // The cap is a share of the invoice, converted into whole points. Truncating rather than
        // rounding keeps the redemption at or under the cap; rounding up could push it past.
        var cappedValue = invoiceTotal * rules.RedemptionCapPercent / 100m;
        var pointsAllowedByCap = (int)decimal.Truncate(cappedValue / rules.RupeesPerPoint);

        return Math.Max(0, Math.Min(balance, pointsAllowedByCap));
    }

    /// <summary>Rupee value of a number of points, at presentation precision.</summary>
    public static decimal ValueOfPoints(int points, LoyaltyRules? rules = null)
    {
        rules ??= LoyaltyRules.Default;
        rules.Validate();

        if (points <= 0)
            return 0m;

        return Money.ToPresentation(points * rules.RupeesPerPoint);
    }

    /// <summary>
    /// Largest redemption available on this invoice, in both points and rupees.
    /// </summary>
    public static LoyaltyRedemption Quote(decimal invoiceTotal, int balance, LoyaltyRules? rules = null)
    {
        rules ??= LoyaltyRules.Default;

        var points = MaxRedeemablePoints(invoiceTotal, balance, rules);

        return new LoyaltyRedemption(points, ValueOfPoints(points, rules));
    }

    /// <summary>
    /// Validates a redemption the cashier asked for, clamping it to what the rules and the
    /// customer's balance permit.
    /// </summary>
    /// <returns>The redemption that will actually be applied.</returns>
    public static LoyaltyRedemption Redeem(decimal invoiceTotal, int balance, int requestedPoints, LoyaltyRules? rules = null)
    {
        rules ??= LoyaltyRules.Default;

        if (requestedPoints < 0)
            throw new ArgumentOutOfRangeException(nameof(requestedPoints), requestedPoints, "Cannot redeem a negative number of points.");

        var allowed = MaxRedeemablePoints(invoiceTotal, balance, rules);
        var points = Math.Min(requestedPoints, allowed);

        return new LoyaltyRedemption(points, ValueOfPoints(points, rules));
    }

    /// <summary>
    /// Points earned on this sale. Applied to the net bill — the amount left after any redemption —
    /// so points spent on an invoice cannot themselves earn points back.
    /// </summary>
    public static int PointsEarned(decimal netBill, LoyaltyRules? rules = null)
    {
        rules ??= LoyaltyRules.Default;
        rules.Validate();

        if (netBill <= 0m)
            return 0;

        return (int)decimal.Truncate(netBill / rules.RupeesPerPointEarned);
    }

    /// <summary>
    /// The customer's balance after this sale: what they had, less what they spent, plus what they
    /// earned. Never goes below zero.
    /// </summary>
    public static int NewBalance(int balance, int pointsRedeemed, int pointsEarned)
    {
        if (pointsRedeemed > balance)
            throw new ArgumentOutOfRangeException(nameof(pointsRedeemed), pointsRedeemed, "Cannot redeem more points than the customer holds.");

        return Math.Max(0, balance - pointsRedeemed + pointsEarned);
    }
}

/// <param name="Points">Whole points spent.</param>
/// <param name="Value">What those points take off the amount payable.</param>
public readonly record struct LoyaltyRedemption(int Points, decimal Value)
{
    public static LoyaltyRedemption None => new(0, 0m);

    public bool IsSomething => Points > 0;
}
