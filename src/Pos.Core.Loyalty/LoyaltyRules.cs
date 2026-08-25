namespace Pos.Core.Loyalty;

/// <summary>
/// The scheme's parameters (SRS section 4). Every figure is configurable; the defaults here are
/// the reference values the SRS quotes, not policy.
/// </summary>
/// <param name="RedemptionCapPercent">
/// Most of an invoice that may be settled with points, as a percentage of the invoice total.
/// </param>
/// <param name="RupeesPerPoint">What one point is worth when redeemed.</param>
/// <param name="RupeesPerPointEarned">
/// Spend needed to earn one point, applied to the net bill after any redemption.
/// </param>
public sealed record LoyaltyRules(
    decimal RedemptionCapPercent = 30m,
    decimal RupeesPerPoint = 0.50m,
    decimal RupeesPerPointEarned = 50m)
{
    public static LoyaltyRules Default { get; } = new();

    /// <summary>Throws if the scheme is configured in a way that cannot be applied coherently.</summary>
    public void Validate()
    {
        if (RedemptionCapPercent is < 0m or > 100m)
            throw new ArgumentOutOfRangeException(nameof(RedemptionCapPercent), RedemptionCapPercent, "The redemption cap must be between 0 and 100 percent.");

        if (RupeesPerPoint <= 0m)
            throw new ArgumentOutOfRangeException(nameof(RupeesPerPoint), RupeesPerPoint, "A point must be worth more than nothing.");

        if (RupeesPerPointEarned <= 0m)
            throw new ArgumentOutOfRangeException(nameof(RupeesPerPointEarned), RupeesPerPointEarned, "The spend per earned point must be greater than zero.");
    }
}
