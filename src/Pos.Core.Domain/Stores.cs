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

    /// <summary>The last sale this lane rang up. What "reprint the bill" nearly always means.</summary>
    SettledInvoice? FindLatest(string laneId);

    /// <summary>
    /// The most recent sale to a customer, found by mobile number — because a customer asking for a
    /// duplicate has their phone, not the invoice number.
    /// </summary>
    SettledInvoice? FindLatestForMobile(string mobileNo);

    /// <summary>
    /// Cancels a sale in place. Nothing is deleted and the number stays consumed.
    /// </summary>
    /// <remarks>
    /// Refuses an invoice that has already appeared on a Z-report. Once a day is closed its figures
    /// have been reported, and quietly changing them afterwards alters a filed number — that
    /// correction is a credit note, not a void. Also refuses an invoice already voided.
    /// </remarks>
    /// <returns>The cancelled invoice, or null if there was nothing by that number.</returns>
    SettledInvoice? Void(string invoiceNo, DateTimeOffset voidedAt, string? reason);

    /// <summary>True when the invoice has already been reported on a Z-report.</summary>
    bool IsReported(string invoiceNo);
}

/// <summary>What happened when a snapshot was taken.</summary>
public readonly record struct BackupOutcome(bool Succeeded, string Path, string Detail);

/// <summary>
/// Takes a snapshot of the lane's books. Declared here so day-end close can insist on one without
/// the domain knowing how a SQLite file is copied.
/// </summary>
public interface IBackupService
{
    BackupOutcome Create(DateTimeOffset takenAt);
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

/// <summary>
/// Where the item master lives. Declared here so catalogue import can be written against domain
/// rules — GST slabs, barcode check digits, MRP — without the domain depending on SQLite.
/// </summary>
public interface IItemStore
{
    Item? FindBySku(string sku);

    Item? FindByBarcode(string barcode);

    /// <summary>Inserts a batch as one unit. Either the whole catalogue lands or none of it does.</summary>
    void AddRange(IEnumerable<Item> items);

    /// <summary>
    /// Inserts or updates by SKU, as one unit. Used by a re-import, which is nearly always a price
    /// change rather than a new catalogue.
    /// </summary>
    void UpsertRange(IEnumerable<Item> items);

    int Count();
}

/// <summary>Where customer records and loyalty balances live.</summary>
public interface ICustomerStore
{
    Customer? FindByMobile(string mobileNo);

    Customer Add(Customer customer);

    /// <summary>Writes back a balance after a sale has redeemed and accrued points.</summary>
    void UpdateLoyaltyBalance(long customerId, int balance);
}
