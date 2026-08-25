namespace Pos.Core.Domain;

/// <summary>
/// Where settled invoices are written. Declared here and implemented in the data layer, so the
/// checkout logic can be tested without a database and the domain does not depend on SQLite.
/// </summary>
public interface IInvoiceStore
{
    /// <summary>
    /// Mints the invoice number and writes the header, lines and payments as one unit. Either the
    /// whole sale lands or none of it does, and a rolled-back save returns its number to the
    /// sequence rather than leaving a hole in it.
    /// </summary>
    SettledInvoice Save(SaleDraft sale);

    SettledInvoice? FindByInvoiceNo(string invoiceNo);
}

/// <summary>Where parked bills live between being held and being recalled (SRS 2.5).</summary>
/// <remarks>
/// Parked bills are kept apart from invoices on purpose. A parked bill is not a tax invoice: it
/// has no invoice number and no tax point, and giving it one would consume a number from a
/// sequence that has to stay unbroken. It gets a number only if and when it is settled.
/// </remarks>
public interface IHeldBillStore
{
    /// <summary>Parks a bill under a token unique to this lane.</summary>
    HeldBill Park(string laneId, string token, DateTimeOffset heldAt, Customer? customer, IReadOnlyList<InvoiceLine> lines);

    /// <summary>The recall list for this lane, most recently parked first.</summary>
    IReadOnlyList<HeldBillSummary> List(string laneId);

    /// <summary>
    /// Takes a parked bill back off the shelf: reads it and removes it in one transaction, so the
    /// same bill cannot be recalled twice.
    /// </summary>
    HeldBill? Recall(string laneId, string token);

    /// <summary>Discards a parked bill without recalling it.</summary>
    bool Discard(string laneId, string token);

    /// <summary>
    /// A token free to use on this lane. Tokens are short so a cashier can read one off a slip,
    /// and are reused once the bill they named has been recalled.
    /// </summary>
    string NextToken(string laneId);
}

/// <summary>Where customer records and loyalty balances live.</summary>
public interface ICustomerStore
{
    Customer? FindByMobile(string mobileNo);

    Customer Add(Customer customer);

    /// <summary>Writes back a balance after a sale has redeemed and accrued points.</summary>
    void UpdateLoyaltyBalance(long customerId, int balance);
}
