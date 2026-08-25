using Pos.Core.Loyalty;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// SRS section 4. The scheme's reference defaults are a 30% cap, ₹0.50 a point, and one point per
/// ₹50 of net bill.
/// </summary>
public class LoyaltyEngineTests
{
    private static readonly LoyaltyRules Default = LoyaltyRules.Default;

    [Fact]
    public void TheDefaultsMatchTheSpecifiedReferenceValues()
    {
        Assert.Equal(30m, Default.RedemptionCapPercent);
        Assert.Equal(0.50m, Default.RupeesPerPoint);
        Assert.Equal(50m, Default.RupeesPerPointEarned);
    }

    // ---- Redemption cap ----------------------------------------------------------------------

    /// <summary>
    /// On a ₹1,000 bill the cap allows ₹300, which at ₹0.50 a point is 600 points — even though
    /// the customer is holding far more.
    /// </summary>
    [Fact]
    public void RedemptionIsCappedAtTheSchemePercentage()
    {
        var points = LoyaltyEngine.MaxRedeemablePoints(1_000m, balance: 5_000);

        Assert.Equal(600, points);
        Assert.Equal(300.00m, LoyaltyEngine.ValueOfPoints(points));
    }

    /// <summary>A customer can never spend points they do not have, cap or no cap.</summary>
    [Fact]
    public void RedemptionIsCappedAtTheCustomersBalance()
    {
        var points = LoyaltyEngine.MaxRedeemablePoints(1_000m, balance: 120);

        Assert.Equal(120, points);
        Assert.Equal(60.00m, LoyaltyEngine.ValueOfPoints(points));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-40)]
    public void ANonPositiveBalanceRedeemsNothing(int balance)
    {
        Assert.Equal(0, LoyaltyEngine.MaxRedeemablePoints(1_000m, balance));
    }

    [Fact]
    public void AnEmptyBillRedeemsNothing()
    {
        Assert.Equal(0, LoyaltyEngine.MaxRedeemablePoints(0m, balance: 5_000));
    }

    /// <summary>
    /// The cap converts to whole points by truncation. Rounding up would let a redemption tip a
    /// paisa past the cap the scheme promises.
    /// </summary>
    [Fact]
    public void ThePointsAllowedByTheCapAreTruncatedNotRounded()
    {
        // 30% of 33 is 9.90, which buys 19.8 points at 50 paise each.
        var points = LoyaltyEngine.MaxRedeemablePoints(33m, balance: 5_000);

        Assert.Equal(19, points);
        Assert.True(LoyaltyEngine.ValueOfPoints(points) <= 33m * 0.30m);
    }

    [Fact]
    public void TheCapPercentageIsConfigurable()
    {
        var generous = new LoyaltyRules(RedemptionCapPercent: 100m);

        Assert.Equal(2_000, LoyaltyEngine.MaxRedeemablePoints(1_000m, 5_000, generous));
        Assert.Equal(1_000.00m, LoyaltyEngine.ValueOfPoints(2_000, generous));
    }

    [Fact]
    public void ThePointValueIsConfigurable()
    {
        var richer = new LoyaltyRules(RupeesPerPoint: 2m);

        // 30% of 1000 is 300, which at ₹2 a point is 150 points.
        Assert.Equal(150, LoyaltyEngine.MaxRedeemablePoints(1_000m, 5_000, richer));
    }

    [Fact]
    public void RequestingLessThanTheMaximumRedeemsWhatWasAsked()
    {
        var redemption = LoyaltyEngine.Redeem(1_000m, balance: 5_000, requestedPoints: 100);

        Assert.Equal(100, redemption.Points);
        Assert.Equal(50.00m, redemption.Value);
    }

    /// <summary>Asking for more than the rules allow is clamped, not refused.</summary>
    [Fact]
    public void RequestingMoreThanAllowedIsClampedToTheCap()
    {
        var redemption = LoyaltyEngine.Redeem(1_000m, balance: 5_000, requestedPoints: 4_000);

        Assert.Equal(600, redemption.Points);
        Assert.Equal(300.00m, redemption.Value);
    }

    [Fact]
    public void RequestingMoreThanTheBalanceIsClampedToTheBalance()
    {
        var redemption = LoyaltyEngine.Redeem(1_000m, balance: 90, requestedPoints: 500);

        Assert.Equal(90, redemption.Points);
    }

    [Fact]
    public void ANegativeRedemptionRequestIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LoyaltyEngine.Redeem(1_000m, 500, -1));
    }

    // ---- Accrual -----------------------------------------------------------------------------

    [Theory]
    [InlineData(50, 1)]
    [InlineData(99, 1)]
    [InlineData(100, 2)]
    [InlineData(1_000, 20)]
    [InlineData(49, 0)]
    [InlineData(0, 0)]
    public void PointsAccrueOnceForEveryFullFiftyRupees(decimal netBill, int expected)
    {
        Assert.Equal(expected, LoyaltyEngine.PointsEarned(netBill));
    }

    /// <summary>
    /// Accrual is on the net bill after redemption, so points spent on an invoice cannot earn
    /// points straight back. A ₹1,000 bill settled with ₹300 of points earns on ₹700, not ₹1,000.
    /// </summary>
    [Fact]
    public void AccrualIsOnTheNetBillAfterRedemption()
    {
        var redemption = LoyaltyEngine.Redeem(1_000m, balance: 5_000, requestedPoints: 600);
        var netBill = 1_000m - redemption.Value;

        Assert.Equal(700m, netBill);
        Assert.Equal(14, LoyaltyEngine.PointsEarned(netBill));

        // Had accrual been on the gross bill it would have been 20 — the difference this rule makes.
        Assert.Equal(20, LoyaltyEngine.PointsEarned(1_000m));
    }

    [Fact]
    public void TheAccrualRateIsConfigurable()
    {
        var generous = new LoyaltyRules(RupeesPerPointEarned: 10m);

        Assert.Equal(70, LoyaltyEngine.PointsEarned(700m, generous));
    }

    // ---- Balance -----------------------------------------------------------------------------

    [Fact]
    public void TheBalanceMovesDownByRedemptionAndUpByAccrual()
    {
        Assert.Equal(414, LoyaltyEngine.NewBalance(balance: 1_000, pointsRedeemed: 600, pointsEarned: 14));
    }

    [Fact]
    public void SpendingTheWholeBalanceLeavesItAtZero()
    {
        Assert.Equal(0, LoyaltyEngine.NewBalance(balance: 600, pointsRedeemed: 600, pointsEarned: 0));
    }

    /// <summary>SRS section 4: the balance never redeems below zero.</summary>
    [Fact]
    public void RedeemingMoreThanTheBalanceIsRefusedRatherThanGoingNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LoyaltyEngine.NewBalance(balance: 100, pointsRedeemed: 101, pointsEarned: 0));
    }

    /// <summary>
    /// A full sweep of the reachable range: no combination the engine itself permits can drive a
    /// balance negative.
    /// </summary>
    [Fact]
    public void NoPermittedRedemptionCanDriveTheBalanceNegative()
    {
        foreach (var balance in new[] { 0, 1, 19, 100, 599, 600, 5_000 })
        {
            foreach (var total in new[] { 1m, 33m, 100m, 999.99m, 10_000m })
            {
                var redemption = LoyaltyEngine.Redeem(total, balance, requestedPoints: int.MaxValue);

                Assert.True(redemption.Points <= balance);
                Assert.True(redemption.Value <= total);

                var earned = LoyaltyEngine.PointsEarned(total - redemption.Value);
                Assert.True(LoyaltyEngine.NewBalance(balance, redemption.Points, earned) >= 0);
            }
        }
    }

    // ---- Rule validation ---------------------------------------------------------------------

    [Theory]
    [InlineData(-1, 0.5, 50)]
    [InlineData(101, 0.5, 50)]
    [InlineData(30, 0, 50)]
    [InlineData(30, -1, 50)]
    [InlineData(30, 0.5, 0)]
    [InlineData(30, 0.5, -10)]
    public void IncoherentRulesAreRejected(decimal cap, decimal rupeesPerPoint, decimal rupeesPerPointEarned)
    {
        var rules = new LoyaltyRules(cap, rupeesPerPoint, rupeesPerPointEarned);

        Assert.Throws<ArgumentOutOfRangeException>(() => LoyaltyEngine.MaxRedeemablePoints(1_000m, 500, rules));
    }
}
