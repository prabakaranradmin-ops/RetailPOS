using System.Windows.Input;
using Pos.Core.Configuration;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// The khata limit at the till: a sale on the khata past what the owner set for the customer waits
/// for the owner's PIN, and the till says what they owe, their limit and when they last paid.
/// </summary>
public class KhataLimitTests
{
    private const string Dal = "8901234567890";
    private const string Mobile = "9500012345";

    private static PinCredential Fake(string pin) => new() { Salt = "c2FsdA==", Hash = pin, Iterations = 1 };

    private static BillingHarness Till(decimal? limit, bool ownerPin = true)
    {
        var till = new BillingHarness(Catalogue.Item(sku: "DAL001", barcode: Dal, name: "Toor Dal 1kg", price: 189m));

        var customer = till.AddCustomer(Mobile, name: "Lakshmi");
        till.Customers.SetCreditLimit(customer.Id, limit);

        var settings = new PosSettings();

        if (ownerPin)
            settings.Security.DashboardPin = Fake("4826");

        till.ViewModel.Security = new TillSecurity(settings, new TillEventRepository(till.Database),
            (pin, stored) => stored is not null && pin == stored.Hash);

        return till;
    }

    private static IReadOnlyList<TillEvent> Recorded(BillingHarness till) =>
        new TillEventRepository(till.Database).List(DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now.AddHours(1));

    private static void Attach(BillingHarness till)
    {
        till.Press(Key.F7);
        till.ViewModel.EditBuffer = Mobile;
        till.Press(Key.Enter);
    }

    /// <summary>The dal on the bill, F12, down to the khata, and the whole of it on there.</summary>
    private static void PutOnKhata(BillingHarness till)
    {
        Attach(till);
        till.Scan(Dal);
        till.Press(Key.F12);

        for (var i = 0; i < 3; i++)
            till.Press(Key.Down);

        Assert.Equal(TenderType.StoreCredit, till.ViewModel.SelectedTenderType);
        till.Press(Key.Enter);
    }

    private static void Finish(BillingHarness till) => till.Press(Key.Enter);

    [Fact]
    public void ASaleWithinTheLimitGoesOnTheKhataWithoutAsking()
    {
        using var till = Till(limit: 500m);

        PutOnKhata(till);

        Assert.False(till.ViewModel.IsApproving);
        Finish(till);

        Assert.Equal(189m, till.Credit.Balance(till.Customers.FindByMobile(Mobile)!.Id));
        Assert.Empty(Recorded(till));
    }

    [Fact]
    public void ASalePastTheLimitWaitsForTheOwnerSayingByHowMuch()
    {
        using var till = Till(limit: 300m);
        PutOnKhata(till);
        Finish(till);

        // A second dal: ₹378.00 against a limit of ₹300.00.
        PutOnKhata(till);

        Assert.True(till.ViewModel.IsApproving);
        Assert.Contains("past their limit of ₹300.00", till.ViewModel.ApprovalWhat);
        Assert.Contains("they would owe ₹378.00", till.ViewModel.ApprovalWhat);
        Assert.Equal(0m, till.ViewModel.TenderedCredit);

        till.ViewModel.ApprovalPin = "4826";
        till.Press(Key.Enter);

        Assert.Equal(189m, till.ViewModel.TenderedCredit);
        Finish(till);

        Assert.Equal(378m, till.Credit.Balance(till.Customers.FindByMobile(Mobile)!.Id));

        var past = Assert.Single(Recorded(till));
        Assert.Equal(TillEventKind.OverKhataLimit, past.Kind);
        Assert.Equal(true, past.Approved);
        Assert.Equal(189m, past.Amount);
        Assert.Contains("owes ₹378.00 against a limit of ₹300.00", past.Detail);
    }

    /// <summary>Nobody can approve on a lane with no owner's PIN, so the khata stops at the limit.</summary>
    [Fact]
    public void WithoutAnOwnersPinASalePastTheLimitIsRefused()
    {
        using var till = Till(limit: 100m, ownerPin: false);

        PutOnKhata(till);

        Assert.False(till.ViewModel.IsApproving);
        Assert.True(till.ViewModel.IsTendering);
        Assert.Equal(0m, till.ViewModel.TenderedCredit);
        Assert.Contains("khata limit is ₹100.00", till.ViewModel.StatusMessage);
        Assert.Contains("raise the limit", till.ViewModel.StatusMessage);
    }

    [Fact]
    public void BackingOutLeavesThePaymentToBeTakenAnotherWay()
    {
        using var till = Till(limit: 100m);

        PutOnKhata(till);
        till.Press(Key.Escape);

        Assert.False(till.ViewModel.IsApproving);
        Assert.True(till.ViewModel.IsTendering);
        Assert.Equal(0m, till.ViewModel.TenderedCredit);
        Assert.Equal(TillEventKind.ApprovalRefused, Assert.Single(Recorded(till)).Kind);
    }

    /// <summary>A customer with no limit is not asked about, however much they owe.</summary>
    [Fact]
    public void NoLimitIsNeverAsked()
    {
        using var till = Till(limit: null);

        for (var i = 0; i < 3; i++)
        {
            PutOnKhata(till);
            Assert.False(till.ViewModel.IsApproving);
            Finish(till);
        }

        Assert.Equal(567m, till.Credit.Balance(till.Customers.FindByMobile(Mobile)!.Id));
    }

    // ---- What the till shows -----------------------------------------------------------------------

    [Fact]
    public void TheTillShowsTheLimitAndThatNothingIsPaidBackYet()
    {
        using var till = Till(limit: 300m);
        PutOnKhata(till);
        Finish(till);

        Attach(till);

        Assert.Equal(189m, till.ViewModel.CustomerOwes);
        Assert.Equal("limit ₹300.00 · nothing paid back yet", till.ViewModel.CustomerKhataNote);
    }

    [Fact]
    public void TheTillShowsWhenTheyLastPaid()
    {
        using var till = Till(limit: null);
        PutOnKhata(till);
        Finish(till);
        PutOnKhata(till);
        Finish(till);

        // F8, Lakshmi, fifty rupees back.
        till.Press(Key.F8);
        till.ViewModel.EditBuffer = "Lak";
        till.Press(Key.Down);
        till.Press(Key.Enter);
        till.ViewModel.EditBuffer = "50";
        till.Press(Key.Enter);

        Attach(till);

        Assert.Equal(328m, till.ViewModel.CustomerOwes);
        Assert.Equal("last paid today", till.ViewModel.CustomerKhataNote);
    }
}
