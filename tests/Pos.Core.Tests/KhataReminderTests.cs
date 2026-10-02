using Pos.Core.Domain;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// The short reminder the owner sends somebody who has not paid in a while: what they owe and since
/// when, and how to pay.
/// </summary>
public class KhataReminderTests
{
    private static readonly Customer Lakshmi = new() { Id = 1, MobileNo = "9500012345", Name = "Lakshmi" };
    private static readonly TimeSpan India = TimeSpan.FromHours(5.5);

    private static DateTimeOffset On(int month, int day) => new(2026, month, day, 11, 0, 0, India);

    private static KhataStatement Statement(params KhataEntry[] ledger) =>
        KhataStatement.SinceLastClear(Lakshmi, ledger, new DateOnly(2026, 10, 2));

    [Fact]
    public void TheReminderSaysWhatIsOwedSinceWhenAndHowToPay()
    {
        var statement = Statement(
            new KhataEntry(On(8, 12), KhataEntryKind.Bought, "RM/26-27/L1-1", 300m),
            new KhataEntry(On(9, 20), KhataEntryKind.Bought, "RM/26-27/L1-9", 189m),
            new KhataEntry(On(9, 25), KhataEntryKind.Paid, string.Empty, 100m, TenderType.Cash));

        var reminder = statement.Reminder("Sri Lakshmi Stores", new UpiPayee("srilakshmi.stores@okaxis", "Sri Lakshmi Stores"));

        Assert.Equal(
            "Dear Lakshmi, a reminder from Sri Lakshmi Stores: Rs 389.00 is due on your khata, unpaid since 12-08-2026 (51 days). "
            + "Please pay by UPI to srilakshmi.stores@okaxis (Sri Lakshmi Stores), or at the counter. Thank you.",
            reminder);
    }

    [Fact]
    public void WithoutAUpiIdTheyAreAskedToPayAtTheCounter()
    {
        var reminder = Statement(new KhataEntry(On(9, 30), KhataEntryKind.Bought, "RM/26-27/L1-12", 189m)).Reminder("Sri Lakshmi Stores");

        Assert.Contains("unpaid since 30-09-2026 (2 days)", reminder);
        Assert.Contains("Please pay at the counter when you next come in.", reminder);
        Assert.DoesNotContain("UPI", reminder);
    }

    [Fact]
    public void SomebodyWhoOwesNothingIsToldTheirKhataIsClear()
    {
        var reminder = Statement(
            new KhataEntry(On(9, 1), KhataEntryKind.Bought, "RM/26-27/L1-3", 100m),
            new KhataEntry(On(9, 2), KhataEntryKind.Paid, string.Empty, 100m, TenderType.Upi)).Reminder("Sri Lakshmi Stores");

        Assert.Equal("Dear Lakshmi, your khata at Sri Lakshmi Stores is clear. Thank you.", reminder);
    }

    /// <summary>A customer known only by number is greeted by number, and a shop with no name as "the shop".</summary>
    [Fact]
    public void ACustomerWithNoNameIsGreetedByNumber()
    {
        var unnamed = new Customer { Id = 2, MobileNo = "9500098765" };
        var statement = KhataStatement.SinceLastClear(unnamed, [new KhataEntry(On(10, 1), KhataEntryKind.Bought, "X", 50m)], new DateOnly(2026, 10, 2));

        Assert.StartsWith("Dear 9500098765, a reminder from the shop:", statement.Reminder(null));
    }
}
