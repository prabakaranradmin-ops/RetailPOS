using System.Globalization;

namespace Pos.Core.Domain;

/// <summary>The kinds of entry a day book is made of, named as an accounting package names them.</summary>
public enum VoucherKind
{
    /// <summary>A bill.</summary>
    Sales = 0,

    /// <summary>Goods a customer brought back.</summary>
    CreditNote = 1,

    /// <summary>Money a customer paid back against their khata.</summary>
    Receipt = 2,

    /// <summary>A supplier's bill.</summary>
    Purchase = 3,

    /// <summary>Money paid out: to a supplier, or for an expense.</summary>
    Payment = 4,

    /// <summary>Goods sent back to a supplier.</summary>
    DebitNote = 5,

    /// <summary>Cash moved into or out of the till that was neither earned nor spent.</summary>
    Contra = 6,
}

/// <summary>
/// The group a ledger belongs under, as the groups every accounting package starts with are named.
/// </summary>
public enum LedgerGroup
{
    CashInHand = 0,
    BankAccounts = 1,
    SundryDebtors = 2,
    SundryCreditors = 3,
    SalesAccounts = 4,
    PurchaseAccounts = 5,
    DutiesAndTaxes = 6,
    IndirectExpenses = 7,

    /// <summary>Somewhere for the accountant to say where it belongs: cash taken out, or put in, by the owner.</summary>
    Suspense = 8,
}

/// <summary>One line of a voucher: a ledger, and what it is debited or credited.</summary>
/// <remarks>One of the two is zero. Both are rupees to the paisa.</remarks>
public sealed record DayBookEntry(string Ledger, LedgerGroup Group, decimal Debit, decimal Credit);

/// <summary>One voucher: a bill, a credit note, a payment - with its entries, which balance.</summary>
/// <param name="Number">The document's own number where it has one - the bill, the credit note, the supplier's bill.</param>
/// <param name="Party">Who it was with, where anybody was.</param>
public sealed record DayBookVoucher(
    VoucherKind Kind,
    string Number,
    DateOnly Date,
    string? Party,
    string Narration,
    IReadOnlyList<DayBookEntry> Entries)
{
    public decimal Debits => Entries.Sum(e => e.Debit);

    public decimal Credits => Entries.Sum(e => e.Credit);

    /// <summary>What every voucher must be. Kept as a check, never assumed.</summary>
    public bool IsBalanced => Debits == Credits;
}

/// <summary>
/// The ledger names the day book uses, so the files it writes match the accountant's own books.
/// </summary>
/// <remarks>
/// <para>
/// The defaults are plain names an accountant will recognise. A shop whose accountant keeps them by
/// other names sets them in the settings file, under <c>dayBook</c>, once.
/// </para>
/// <para>
/// Ledgers that depend on the rate are made from these: <c>Sales @ 5%</c>, <c>Sales inter-state @ 18%</c>,
/// <c>Purchases @ 5%</c>, <c>Sales, bill of supply</c>. Customers on the khata and suppliers are ledgers of
/// their own, by name.
/// </para>
/// </remarks>
public sealed class DayBookLedgers
{
    public string Cash { get; set; } = "Cash";
    public string Bank { get; set; } = "Bank";
    public string Card { get; set; } = "Card collections";
    public string Upi { get; set; } = "UPI collections";

    /// <summary>For khata entries of a customer the shop has since forgotten, at their request.</summary>
    public string KhataCustomers { get; set; } = "Khata customers";

    public string LoyaltyPoints { get; set; } = "Loyalty points redeemed";
    public string Sales { get; set; } = "Sales";
    public string Purchases { get; set; } = "Purchases";
    public string OutputCgst { get; set; } = "Output CGST";
    public string OutputSgst { get; set; } = "Output SGST";
    public string OutputIgst { get; set; } = "Output IGST";
    public string InputCgst { get; set; } = "Input CGST";
    public string InputSgst { get; set; } = "Input SGST";
    public string InputIgst { get; set; } = "Input IGST";
    public string RoundOff { get; set; } = "Round off";

    /// <summary>An expense paid from outside the till: usually the bank, or UPI from it.</summary>
    public string ExpensesPaidOutside { get; set; } = "Bank";

    public string CashTakenOut { get; set; } = "Cash taken out of the till";
    public string CashPutIn { get; set; } = "Cash put into the till";

    private static string Rate(decimal rate) => rate.ToString("0.##", CultureInfo.InvariantCulture);

    public string SalesAt(decimal rate, bool interState) =>
        interState ? $"{Sales} inter-state @ {Rate(rate)}%" : $"{Sales} @ {Rate(rate)}%";

    public string SalesBillOfSupply => $"{Sales}, bill of supply";

    public string PurchasesAt(decimal rate, bool interState) =>
        interState ? $"{Purchases} inter-state @ {Rate(rate)}%" : $"{Purchases} @ {Rate(rate)}%";

    public string PurchasesNoGst => $"{Purchases}, no GST";

    /// <summary>What is wrong with them, or null: a name left empty, or two the same.</summary>
    public string? Problem()
    {
        var names = new (string Key, string? Name)[]
        {
            ("cash", Cash), ("bank", Bank), ("card", Card), ("upi", Upi), ("khataCustomers", KhataCustomers),
            ("loyaltyPoints", LoyaltyPoints), ("sales", Sales), ("purchases", Purchases),
            ("outputCgst", OutputCgst), ("outputSgst", OutputSgst), ("outputIgst", OutputIgst),
            ("inputCgst", InputCgst), ("inputSgst", InputSgst), ("inputIgst", InputIgst),
            ("roundOff", RoundOff), ("cashTakenOut", CashTakenOut), ("cashPutIn", CashPutIn),
        };

        if (names.FirstOrDefault(n => string.IsNullOrWhiteSpace(n.Name)) is { Key: not null } empty)
            return $"{empty.Key} has no ledger name.";

        // Paid outside may well be the bank: the one name allowed to repeat another.
        var clash = names
            .GroupBy(n => n.Name!.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);

        if (clash is not null)
            return $"{string.Join(" and ", clash.Select(n => n.Key))} have the same ledger name, '{clash.Key}'.";

        return string.IsNullOrWhiteSpace(ExpensesPaidOutside) ? "expensesPaidOutside has no ledger name." : null;
    }
}
