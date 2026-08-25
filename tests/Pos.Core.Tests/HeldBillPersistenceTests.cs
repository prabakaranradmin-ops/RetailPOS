using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// SRS 2.5. A parked bill survives a restart, and recalling one restores the exact line state
/// including discounts.
/// </summary>
public class HeldBillPersistenceTests : IDisposable
{
    private const string Lane = "L1";
    private const string HomeState = "33";

    private readonly TempDatabase _temp = new();

    private HeldBillRepository Held => new(_temp.Database);

    private static readonly DateTimeOffset When = new(2026, 8, 25, 10, 30, 0, TimeSpan.FromHours(5.5));

    public void Dispose() => _temp.Dispose();

    private static InvoiceLine Line(string name, decimal price, decimal gst, decimal quantity = 1m, decimal discount = 0m, UnitType unit = UnitType.Each) =>
        InvoiceLine.Rehydrate(1, name, "0713", "8901234567890", null, unit, price, price, true, gst, quantity, discount, false);

    private static InvoiceLine[] TypicalBill() =>
    [
        Line("Basmati Rice 5kg", 649m, 5m, discount: 49m),
        Line("Sugar Loose", 45m, 5m, quantity: 2.75m, unit: UnitType.Kilogram),
        Line("Shampoo 340ml", 299m, 18m, quantity: 2m),
    ];

    // ---- The round trip the gate asks for ----------------------------------------------------

    [Fact]
    public void ParkingAndRecallingRestoresTheExactLineState()
    {
        var lines = TypicalBill();
        var expectedTotal = InvoiceTotals.From(lines).GrandTotal;

        Held.Park(Lane, "H001", When, customer: null, lines);

        var recalled = Held.Recall(Lane, "H001");

        Assert.NotNull(recalled);
        Assert.Equal(3, recalled.Lines.Count);
        Assert.Equal(expectedTotal, InvoiceTotals.From(recalled.Lines).GrandTotal);

        Assert.Equal("Basmati Rice 5kg", recalled.Lines[0].NameSnapshot);
        Assert.Equal(49m, recalled.Lines[0].Discount);
        Assert.Equal(2.75m, recalled.Lines[1].Quantity);
        Assert.Equal(UnitType.Kilogram, recalled.Lines[1].Unit);
        Assert.Equal(2m, recalled.Lines[2].Quantity);
        Assert.Equal(18m, recalled.Lines[2].GstRate);
    }

    [Fact]
    public void AParkedBillOutlivesTheProcessThatParkedIt()
    {
        Held.Park(Lane, "H001", When, null, TypicalBill());

        // A new repository over the same file is what a restart looks like from here.
        var afterRestart = new HeldBillRepository(_temp.Database);

        Assert.Single(afterRestart.List(Lane));
        Assert.Equal(3, afterRestart.Recall(Lane, "H001")!.Lines.Count);
    }

    [Fact]
    public void TheCustomerIsParkedWithTheBill()
    {
        var customer = new CustomerRepository(_temp.Database)
            .Add(new Customer { MobileNo = "9876543210", Name = "Anitha", StateCode = HomeState, LoyaltyBalance = 240 });

        Held.Park(Lane, "H001", When, customer, TypicalBill());
        var recalled = Held.Recall(Lane, "H001")!;

        Assert.Equal("9876543210", recalled.Customer!.MobileNo);
        Assert.Equal(240, recalled.Customer.LoyaltyBalance);
    }

    /// <summary>
    /// Inter-state lines carry IGST rather than a CGST/SGST split, and that has to survive being
    /// parked — otherwise a recalled bill quietly re-taxes itself.
    /// </summary>
    [Fact]
    public void InterStateLinesStayInterStateThroughAPark()
    {
        var line = InvoiceLine.Rehydrate(1, "Shampoo 340ml", "3305", null, null, UnitType.Each, 299m, 299m, true, 18m, 1m, 0m, isInterState: true);

        Held.Park(Lane, "H001", When, null, [line]);
        var recalled = Held.Recall(Lane, "H001")!;

        Assert.True(recalled.Lines[0].IsInterState);
        Assert.Equal(0m, recalled.Lines[0].Tax.Cgst);
        Assert.True(recalled.Lines[0].Tax.Igst > 0m);
    }

    // ---- The recall list -----------------------------------------------------------------------

    /// <summary>SRS 2.5: token, timestamp, item count and customer.</summary>
    [Fact]
    public void TheRecallListShowsWhatTheCashierNeedsToChooseWith()
    {
        var customer = new CustomerRepository(_temp.Database)
            .Add(new Customer { MobileNo = "9876543210", Name = "Anitha", StateCode = HomeState });

        Held.Park(Lane, "H001", When, customer, TypicalBill());

        var summary = Assert.Single(Held.List(Lane));

        Assert.Equal("H001", summary.Token);
        Assert.Equal(When, summary.HeldAt);
        Assert.Equal(3, summary.ItemCount);
        Assert.Equal("Anitha", summary.CustomerLabel);
        Assert.Equal(InvoiceTotals.From(TypicalBill()).GrandTotal, summary.GrandTotal);
    }

    [Fact]
    public void AParkedBillWithNoCustomerIsLabelledAsAWalkIn()
    {
        Held.Park(Lane, "H001", When, null, TypicalBill());

        Assert.Equal("Walk-in", Held.List(Lane)[0].CustomerLabel);
    }

    [Fact]
    public void TheMostRecentlyParkedBillIsListedFirst()
    {
        Held.Park(Lane, "H001", When, null, [Line("First", 100m, 5m)]);
        Held.Park(Lane, "H002", When.AddMinutes(5), null, [Line("Second", 100m, 5m)]);
        Held.Park(Lane, "H003", When.AddMinutes(10), null, [Line("Third", 100m, 5m)]);

        Assert.Equal(["H003", "H002", "H001"], Held.List(Lane).Select(h => h.Token));
    }

    /// <summary>A lane only sees the bills parked at its own till.</summary>
    [Fact]
    public void ParkedBillsAreScopedToTheirLane()
    {
        Held.Park("L1", "H001", When, null, [Line("Lane one", 100m, 5m)]);
        Held.Park("L2", "H001", When, null, [Line("Lane two", 200m, 5m)]);

        Assert.Single(Held.List("L1"));
        Assert.Single(Held.List("L2"));
        Assert.Equal("Lane one", Held.Recall("L1", "H001")!.Lines[0].NameSnapshot);
        Assert.Equal("Lane two", Held.Recall("L2", "H001")!.Lines[0].NameSnapshot);
    }

    // ---- Taking a bill off the shelf ---------------------------------------------------------

    [Fact]
    public void RecallingRemovesTheBillFromTheList()
    {
        Held.Park(Lane, "H001", When, null, TypicalBill());

        Held.Recall(Lane, "H001");

        Assert.Empty(Held.List(Lane));
    }

    /// <summary>The same parked bill must not be recallable twice.</summary>
    [Fact]
    public void ABillCannotBeRecalledTwice()
    {
        Held.Park(Lane, "H001", When, null, TypicalBill());

        Assert.NotNull(Held.Recall(Lane, "H001"));
        Assert.Null(Held.Recall(Lane, "H001"));
    }

    [Fact]
    public void RecallingAnUnknownTokenReturnsNothing()
    {
        Assert.Null(Held.Recall(Lane, "H999"));
    }

    [Fact]
    public void DiscardingRemovesAParkedBillWithoutRecallingIt()
    {
        Held.Park(Lane, "H001", When, null, TypicalBill());

        Assert.True(Held.Discard(Lane, "H001"));
        Assert.Empty(Held.List(Lane));
        Assert.False(Held.Discard(Lane, "H001"));
    }

    [Fact]
    public void DiscardingTakesTheLinesWithIt()
    {
        Held.Park(Lane, "H001", When, null, TypicalBill());
        Held.Discard(Lane, "H001");

        using var connection = _temp.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM held_bill_lines;";

        Assert.Equal(0L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void AnEmptyBillCannotBeParked()
    {
        Assert.Throws<InvalidOperationException>(() => Held.Park(Lane, "H001", When, null, []));
    }

    // ---- Tokens ------------------------------------------------------------------------------

    [Fact]
    public void TokensAreHandedOutInOrder()
    {
        Assert.Equal("H001", Held.NextToken(Lane));

        Held.Park(Lane, "H001", When, null, TypicalBill());
        Assert.Equal("H002", Held.NextToken(Lane));

        Held.Park(Lane, "H002", When, null, TypicalBill());
        Assert.Equal("H003", Held.NextToken(Lane));
    }

    /// <summary>
    /// Tokens are reused once freed, so they stay short enough to read off a slip rather than
    /// climbing forever.
    /// </summary>
    [Fact]
    public void AFreedTokenIsHandedOutAgain()
    {
        Held.Park(Lane, "H001", When, null, TypicalBill());
        Held.Park(Lane, "H002", When, null, TypicalBill());

        Held.Recall(Lane, "H001");

        Assert.Equal("H001", Held.NextToken(Lane));
    }

    [Fact]
    public void EachLaneNumbersItsOwnTokens()
    {
        Held.Park("L1", "H001", When, null, TypicalBill());

        Assert.Equal("H002", Held.NextToken("L1"));
        Assert.Equal("H001", Held.NextToken("L2"));
    }

    [Fact]
    public void TheSameTokenCannotBeParkedTwiceOnOneLane()
    {
        Held.Park(Lane, "H001", When, null, TypicalBill());

        Assert.ThrowsAny<Exception>(() => Held.Park(Lane, "H001", When, null, TypicalBill()));
    }
}
