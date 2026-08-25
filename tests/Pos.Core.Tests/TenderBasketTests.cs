using Pos.Core.Domain;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// SRS 2.4: cash, card, UPI and store credit; split across several in one transaction; change due
/// on cash; tendered must cover the total.
/// </summary>
public class TenderBasketTests
{
    [Fact]
    public void AFreshBasketOwesTheWholeBill()
    {
        var basket = new TenderBasket(467.00m);

        Assert.Equal(467.00m, basket.AmountDue);
        Assert.Equal(467.00m, basket.Remaining);
        Assert.Equal(0m, basket.TotalTendered);
        Assert.Equal(0m, basket.ChangeDue);
        Assert.False(basket.IsSettled);
        Assert.True(basket.IsEmpty);
    }

    [Fact]
    public void PayingTheExactAmountSettlesTheBillWithNoChange()
    {
        var basket = new TenderBasket(467.00m);

        basket.Add(TenderType.Cash, 467.00m);

        Assert.True(basket.IsSettled);
        Assert.Equal(0m, basket.Remaining);
        Assert.Equal(0m, basket.ChangeDue);
    }

    /// <summary>The split-tender case the gate asks for: part cash, part UPI, reconciling exactly.</summary>
    [Fact]
    public void CashAndUpiSplitReconcilesToTheGrandTotal()
    {
        var basket = new TenderBasket(467.00m);

        basket.Add(TenderType.Cash, 200.00m);
        basket.Add(TenderType.Upi, 267.00m, "UPI/2026/8842");

        Assert.True(basket.IsSettled);
        Assert.Equal(467.00m, basket.TotalTendered);
        Assert.Equal(0m, basket.Remaining);
        Assert.Equal(0m, basket.ChangeDue);
        Assert.Equal(2, basket.Tenders.Count);
        Assert.Equal("UPI/2026/8842", basket.Tenders[1].ReferenceNo);
    }

    [Fact]
    public void FourWaySplitAcrossEveryTenderTypeReconciles()
    {
        var basket = new TenderBasket(1_000.00m);

        basket.Add(TenderType.LoyaltyPoints, 300.00m, "600 points");
        basket.Add(TenderType.Card, 400.00m, "AUTH 004411");
        basket.Add(TenderType.Upi, 200.00m, "UPI/2026/9001");
        basket.Add(TenderType.StoreCredit, 100.00m);

        Assert.True(basket.IsSettled);
        Assert.Equal(1_000.00m, basket.TotalTendered);
        Assert.Equal(0m, basket.ChangeDue);
    }

    [Fact]
    public void RemainingFallsAsPaymentsAreTaken()
    {
        var basket = new TenderBasket(500.00m);

        basket.Add(TenderType.Card, 200.00m);
        Assert.Equal(300.00m, basket.Remaining);

        basket.Add(TenderType.Upi, 150.00m);
        Assert.Equal(150.00m, basket.Remaining);

        basket.Add(TenderType.Cash, 150.00m);
        Assert.Equal(0m, basket.Remaining);
    }

    // ---- Change ------------------------------------------------------------------------------

    [Fact]
    public void OverTenderedCashProducesChange()
    {
        var basket = new TenderBasket(467.00m);

        basket.Add(TenderType.Cash, 500.00m);

        Assert.True(basket.IsSettled);
        Assert.Equal(33.00m, basket.ChangeDue);
        Assert.Equal(0m, basket.Remaining);
    }

    [Fact]
    public void ChangeIsComputedOnTheWholeSplitNotJustTheCashPart()
    {
        var basket = new TenderBasket(467.00m);

        basket.Add(TenderType.Upi, 200.00m);
        basket.Add(TenderType.Cash, 300.00m);

        Assert.Equal(33.00m, basket.ChangeDue);
    }

    /// <summary>
    /// There is no way to give change on a card or a UPI transfer, so those tenders may never
    /// exceed what is owed.
    /// </summary>
    [Theory]
    [InlineData(TenderType.Card)]
    [InlineData(TenderType.Upi)]
    [InlineData(TenderType.StoreCredit)]
    [InlineData(TenderType.LoyaltyPoints)]
    public void OnlyCashMayBeOverTendered(TenderType type)
    {
        var basket = new TenderBasket(100.00m);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => basket.Add(type, 150.00m));
        Assert.Contains("only cash gives change", ex.Message);

        Assert.True(basket.IsEmpty);
    }

    [Theory]
    [InlineData(TenderType.Card)]
    [InlineData(TenderType.Upi)]
    [InlineData(TenderType.LoyaltyPoints)]
    public void ANonCashTenderForExactlyTheRemainderIsFine(TenderType type)
    {
        var basket = new TenderBasket(100.00m);

        basket.Add(TenderType.Cash, 40.00m);
        basket.Add(type, 60.00m);

        Assert.True(basket.IsSettled);
        Assert.Equal(0m, basket.ChangeDue);
    }

    // ---- Rejections --------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void APaymentMustBeForSomething(decimal amount)
    {
        var basket = new TenderBasket(100.00m);

        Assert.Throws<ArgumentOutOfRangeException>(() => basket.Add(TenderType.Cash, amount));
    }

    [Fact]
    public void NothingMoreCanBeTakenOnceTheBillIsCovered()
    {
        var basket = new TenderBasket(100.00m);
        basket.Add(TenderType.Cash, 100.00m);

        var ex = Assert.Throws<InvalidOperationException>(() => basket.Add(TenderType.Cash, 10.00m));
        Assert.Contains("already covered", ex.Message);
    }

    [Fact]
    public void AnUnknownTenderTypeIsRejected()
    {
        var basket = new TenderBasket(100.00m);

        Assert.Throws<ArgumentOutOfRangeException>(() => basket.Add((TenderType)99, 10.00m));
    }

    [Fact]
    public void ABillCannotComeToLessThanNothing()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TenderBasket(-1m));
    }

    // ---- Corrections at the till -------------------------------------------------------------

    [Fact]
    public void APaymentEnteredByMistakeCanBeRemoved()
    {
        var basket = new TenderBasket(500.00m);
        basket.Add(TenderType.Card, 500.00m, "AUTH 1");

        basket.RemoveAt(0);

        Assert.True(basket.IsEmpty);
        Assert.Equal(500.00m, basket.Remaining);
        Assert.False(basket.IsSettled);
    }

    [Fact]
    public void RemovingAPaymentThatIsNotThereIsRejected()
    {
        var basket = new TenderBasket(500.00m);

        Assert.Throws<ArgumentOutOfRangeException>(() => basket.RemoveAt(0));
    }

    [Fact]
    public void ClearingStartsTheSettlementOver()
    {
        var basket = new TenderBasket(500.00m);
        basket.Add(TenderType.Cash, 200.00m);
        basket.Add(TenderType.Upi, 100.00m);

        basket.Clear();

        Assert.True(basket.IsEmpty);
        Assert.Equal(500.00m, basket.Remaining);
    }

    [Fact]
    public void TotalsPerTenderTypeAreAvailableForTheDrawerDecision()
    {
        var basket = new TenderBasket(500.00m);
        basket.Add(TenderType.Cash, 100.00m);
        basket.Add(TenderType.Cash, 150.00m);
        basket.Add(TenderType.Upi, 250.00m);

        Assert.True(basket.Contains(TenderType.Cash));
        Assert.False(basket.Contains(TenderType.Card));
        Assert.Equal(250.00m, basket.TotalOf(TenderType.Cash));
        Assert.Equal(250.00m, basket.TotalOf(TenderType.Upi));
        Assert.Equal(0m, basket.TotalOf(TenderType.Card));
    }
}
