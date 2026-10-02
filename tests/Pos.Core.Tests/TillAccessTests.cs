using System.IO;
using Pos.Core.Configuration;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// Cashiers with their own PINs, what waits for the owner's PIN, and the record of the till's
/// exceptions.
/// </summary>
public class TillAccessTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 18, 30, 0, TimeSpan.FromHours(5.5));

    private readonly TempDatabase _temp = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "posaccess-" + Guid.NewGuid().ToString("N"));

    public TillAccessTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _temp.Dispose();

        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    /// <summary>A stand-in credential: the rules only ask whether one is usable.</summary>
    private static PinCredential Usable() => new() { Salt = "c2FsdA==", Hash = "aGFzaA==", Iterations = 1 };

    // ---- What waits for the owner ----------------------------------------------------------------

    /// <summary>
    /// Nothing the owner can switch waits unless they switch it. Going past a customer's khata limit
    /// is the exception: the owner set the limit, so going past it is always theirs to approve.
    /// </summary>
    [Fact]
    public void NothingWaitsForTheOwnerUnlessTheOwnerSaysSo()
    {
        var approvals = new ApprovalSettings();

        Assert.False(approvals.AnyOn);

        foreach (var action in Enum.GetValues<Guarded>().Where(a => a != Guarded.OverKhataLimit))
            Assert.False(approvals.Guards(action, discountPercent: 100m));

        Assert.True(approvals.Guards(Guarded.OverKhataLimit));
    }

    [Theory]
    [InlineData(Guarded.Void)]
    [InlineData(Guarded.CashRefund)]
    [InlineData(Guarded.CashOut)]
    [InlineData(Guarded.CloseDay)]
    public void EachActionIsAskedAboutOnItsOwnSwitch(Guarded action)
    {
        var approvals = new ApprovalSettings
        {
            Voids = action == Guarded.Void,
            CashRefunds = action == Guarded.CashRefund,
            CashOut = action == Guarded.CashOut,
            CloseDay = action == Guarded.CloseDay,
        };

        foreach (var other in Enum.GetValues<Guarded>().Where(a => a != Guarded.OverKhataLimit))
            Assert.Equal(other == action, approvals.Guards(other));
    }

    /// <summary>"More than ten percent": ten itself is not asked about, a paisa over is.</summary>
    [Theory]
    [InlineData(10, 9.99, false)]
    [InlineData(10, 10, false)]
    [InlineData(10, 10.01, true)]
    [InlineData(0, 0.01, true)]
    [InlineData(0, 0, false)]
    public void ADiscountIsAskedAboutOnlyOverTheShareSet(decimal limit, decimal share, bool asked) =>
        Assert.Equal(asked, new ApprovalSettings { DiscountAbovePercent = limit }.Guards(Guarded.Discount, share));

    [Theory]
    [InlineData(-1)]
    [InlineData(100)]
    [InlineData(150)]
    public void AShareThatCannotBeRightIsRefused(decimal limit) =>
        Assert.NotNull(new ApprovalSettings { DiscountAbovePercent = limit }.Problem());

    // ---- Cashiers --------------------------------------------------------------------------------

    [Theory]
    [InlineData("", "Type the cashier's name.")]
    [InlineData("   ", "Type the cashier's name.")]
    [InlineData("murugan", "There is already a cashier called murugan.")]
    [InlineData(" MURUGAN ", "There is already a cashier called MURUGAN.")]
    public void ANameMustBeThereAndNotTakenAlready(string name, string problem) =>
        Assert.Equal(problem, CashierRules.NameProblem(name, ["Murugan", "Lakshmi"]));

    [Fact]
    public void ANameTooLongForTheReportIsRefused() =>
        Assert.NotNull(CashierRules.NameProblem(new string('a', CashierRules.MaximumNameLength + 1), []));

    [Fact]
    public void ACashierWithNoUsablePinCannotBeKept()
    {
        var problem = CashierRules.Problem([new CashierSettings { Name = "Murugan", Pin = null }]);

        Assert.Contains("Murugan has no usable PIN", problem);
    }

    // ---- The settings file -----------------------------------------------------------------------

    [Fact]
    public void ALaneHasNoCashiersAndAsksTheOwnerNothingUntilTold()
    {
        File.WriteAllText(SettingsPath, """{ "laneId": "L2" }""");

        var settings = PosSettings.LoadOrDefault(SettingsPath);

        Assert.Empty(settings.Cashiers);
        Assert.False(settings.Approvals.AnyOn);
    }

    [Fact]
    public void TheCashiersAreSavedAsHashesAndReadBack()
    {
        File.WriteAllText(SettingsPath, """{ "laneId": "L7", "receiptLayout": "Compact" }""");
        var pin = DashboardLock.Create("4826");

        SettingsFile.SetCashiers(SettingsPath, [new CashierSettings { Name = " Murugan ", Pin = pin }]);

        var settings = PosSettings.LoadOrDefault(SettingsPath);
        var cashier = Assert.Single(settings.Cashiers);

        Assert.Equal("Murugan", cashier.Name);
        Assert.True(DashboardLock.Verify("4826", cashier.Pin));
        Assert.Equal("L7", settings.LaneId);

        // Never the PIN itself.
        Assert.DoesNotContain("4826", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void TakingTheLastCashierOffTakesTheListOut()
    {
        SettingsFile.SetCashiers(SettingsPath, [new CashierSettings { Name = "Murugan", Pin = Usable() }]);
        SettingsFile.SetCashiers(SettingsPath, []);

        Assert.DoesNotContain("cashiers", File.ReadAllText(SettingsPath));
        Assert.Empty(PosSettings.LoadOrDefault(SettingsPath).Cashiers);
    }

    [Fact]
    public void TwoCashiersOfTheSameNameAreNotSaved()
    {
        Assert.Throws<ArgumentException>(() => SettingsFile.SetCashiers(SettingsPath,
        [
            new CashierSettings { Name = "Murugan", Pin = Usable() },
            new CashierSettings { Name = "murugan", Pin = Usable() },
        ]));
    }

    [Fact]
    public void TheApprovalsAreSavedAndReadBack()
    {
        SettingsFile.SetApprovals(SettingsPath, new ApprovalSettings { Voids = true, DiscountAbovePercent = 15m, CloseDay = true });

        var approvals = PosSettings.LoadOrDefault(SettingsPath).Approvals;

        Assert.True(approvals.Voids);
        Assert.Equal(15m, approvals.DiscountAbovePercent);
        Assert.False(approvals.CashRefunds);
        Assert.False(approvals.CashOut);
        Assert.True(approvals.CloseDay);
    }

    [Fact]
    public void AHandEditedListWithADuplicateStopsTheLaneSayingWhy()
    {
        File.WriteAllText(SettingsPath, """
            { "cashiers": [
                { "name": "Murugan", "pin": { "salt": "c2FsdA==", "hash": "aGFzaA==", "iterations": 1 } },
                { "name": "MURUGAN", "pin": { "salt": "c2FsdA==", "hash": "aGFzaA==", "iterations": 1 } } ] }
            """);

        var refused = Assert.Throws<InvalidOperationException>(() => PosSettings.LoadOrDefault(SettingsPath));
        Assert.Contains("cashiers list", refused.Message);
    }

    [Fact]
    public void AnApprovalShareThatCannotBeRightStopsTheLaneSayingWhy()
    {
        File.WriteAllText(SettingsPath, """{ "approvals": { "discountAbovePercent": 120 } }""");

        var refused = Assert.Throws<InvalidOperationException>(() => PosSettings.LoadOrDefault(SettingsPath));
        Assert.Contains("approvals section", refused.Message);
    }

    // ---- The record ------------------------------------------------------------------------------

    [Fact]
    public void WhatHappenedIsReadBackAsWritten()
    {
        var events = new TillEventRepository(_temp.Database);

        events.Record("L1", Now, TillEventKind.Voided, "Murugan", "RM/26-27/L1-12", 189.50m, approved: true);
        events.Record("L1", Now.AddMinutes(1), TillEventKind.ApprovalRefused, "Murugan", "RM/26-27/L1-13", 40m, approved: false, detail: "Void: backed out.");
        events.Record("L1", Now.AddMinutes(2), TillEventKind.SignedOn, "Lakshmi");

        var read = events.List(Now.AddHours(-1), Now.AddHours(1));

        Assert.Equal(3, read.Count);

        // Newest first.
        Assert.Equal(TillEventKind.SignedOn, read[0].Kind);
        Assert.Null(read[0].Approved);
        Assert.Null(read[0].Amount);

        Assert.Equal(false, read[1].Approved);
        Assert.Equal("Void: backed out.", read[1].Detail);

        Assert.Equal(TillEventKind.Voided, read[2].Kind);
        Assert.Equal("Murugan", read[2].Cashier);
        Assert.Equal("RM/26-27/L1-12", read[2].Reference);
        Assert.Equal(189.50m, read[2].Amount);
        Assert.Equal(true, read[2].Approved);
        Assert.Equal(Now, read[2].At);
    }

    [Fact]
    public void OnlyThePeriodAskedForIsRead()
    {
        var events = new TillEventRepository(_temp.Database);

        events.Record("L1", Now.AddDays(-2), TillEventKind.DayClosed, "Murugan");
        events.Record("L1", Now, TillEventKind.DayClosed, "Murugan");

        Assert.Single(events.List(Now.AddDays(-1), Now.AddDays(1)));
    }
}
