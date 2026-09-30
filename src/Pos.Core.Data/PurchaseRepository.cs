using System.Globalization;
using Microsoft.Data.Sqlite;
using Pos.Core.Domain;

namespace Pos.Core.Data;

/// <summary>
/// Suppliers, their bills, and what the shop owes them, read from the books.
/// </summary>
/// <remarks>
/// <para>
/// Recording a bill is one transaction: the bill, its lines, the shelf count of every counted item
/// it brought, and each item's cost price. A delivery half-entered — the stock up but the bill
/// missing, or the other way round — would leave the count and the supplier's account disagreeing
/// with no way to see which was right.
/// </para>
/// <para>
/// Nothing is stored as a balance. What the shop owes a supplier is its bills that were not
/// cancelled, less its payments, summed in exact paise each time.
/// </para>
/// </remarks>
public sealed class PurchaseRepository(PosDatabase database) : IPurchaseStore
{
    private readonly PosDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

    /// <summary>What goes on a drawer cash movement that paid a supplier.</summary>
    public const string SupplierPaymentKind = "SupplierPayment";

    // ---- Suppliers ---------------------------------------------------------------------------

    private const string SupplierColumns = "id, name, phone, gstin, state_code, charges_gst, address, is_active";

    public IReadOnlyList<Supplier> Suppliers(bool activeOnly = true)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SupplierColumns} FROM suppliers
            {(activeOnly ? "WHERE is_active = 1" : "")}
            ORDER BY name COLLATE NOCASE;
            """;

        using var reader = command.ExecuteReader();
        var suppliers = new List<Supplier>();

        while (reader.Read())
            suppliers.Add(MapSupplier(reader));

        return suppliers;
    }

    public Supplier? FindSupplier(long id)
    {
        using var connection = _database.OpenConnection();
        return FindSupplier(connection, null, id);
    }

    private static Supplier? FindSupplier(SqliteConnection connection, SqliteTransaction? transaction, long id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {SupplierColumns} FROM suppliers WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? MapSupplier(reader) : null;
    }

    public Supplier AddSupplier(Supplier supplier)
    {
        var tidy = Check(supplier);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO suppliers (name, phone, gstin, state_code, charges_gst, address, is_active, created_at)
            VALUES ($name, $phone, $gstin, $state, $charges, $address, $active, $at);
            SELECT last_insert_rowid();
            """;
        Bind(command, tidy);
        command.Parameters.AddWithValue("$at", DateTimeOffset.Now);

        try
        {
            return tidy with { Id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) };
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException(Duplicate(tidy), ex);
        }
    }

    public void UpdateSupplier(Supplier supplier)
    {
        var tidy = Check(supplier);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE suppliers
            SET name = $name, phone = $phone, gstin = $gstin, state_code = $state,
                charges_gst = $charges, address = $address, is_active = $active
            WHERE id = $id;
            """;
        Bind(command, tidy);
        command.Parameters.AddWithValue("$id", tidy.Id);

        try
        {
            if (command.ExecuteNonQuery() == 0)
                throw new InvalidOperationException($"No supplier with id {tidy.Id}.");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException(Duplicate(tidy), ex);
        }
    }

    /// <summary>
    /// A supplier as it may be stored: named, a real GSTIN or none, and a state that exists.
    /// </summary>
    /// <remarks>
    /// The GSTIN decides the state, because it says where the supplier is registered, and that is
    /// what decides CGST and SGST against IGST. A supplier with no GSTIN cannot charge GST at all.
    /// </remarks>
    private static Supplier Check(Supplier supplier)
    {
        ArgumentNullException.ThrowIfNull(supplier);

        var name = supplier.Name?.Trim() ?? string.Empty;

        if (name.Length == 0)
            throw new ArgumentException("A supplier needs a name.", nameof(supplier));

        string? gstin = null;
        var state = supplier.StateCode?.Trim().PadLeft(2, '0') ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(supplier.Gstin))
        {
            if (Pos.Core.Domain.Gstin.Problem(supplier.Gstin) is { } problem)
                throw new ArgumentException(problem, nameof(supplier));

            gstin = Pos.Core.Domain.Gstin.Normalise(supplier.Gstin);
            state = Pos.Core.Domain.Gstin.StateCode(gstin);
        }

        if (GstStates.Name(state) is null)
            throw new ArgumentException($"'{supplier.StateCode}' is not a GST state code.", nameof(supplier));

        return supplier with
        {
            Name = name,
            Phone = string.IsNullOrWhiteSpace(supplier.Phone) ? null : supplier.Phone.Trim(),
            Gstin = gstin,
            StateCode = state,
            ChargesGst = gstin is not null && supplier.ChargesGst,
            Address = string.IsNullOrWhiteSpace(supplier.Address) ? null : supplier.Address.Trim(),
        };
    }

    private static string Duplicate(Supplier supplier) =>
        supplier.Gstin is null
            ? $"There is already a supplier called {supplier.Name}."
            : $"There is already a supplier called {supplier.Name}, or one with GSTIN {supplier.Gstin}.";

    private static void Bind(SqliteCommand command, Supplier supplier)
    {
        command.Parameters.AddWithValue("$name", supplier.Name);
        command.Parameters.AddWithValue("$phone", (object?)supplier.Phone ?? DBNull.Value);
        command.Parameters.AddWithValue("$gstin", (object?)supplier.Gstin ?? DBNull.Value);
        command.Parameters.AddWithValue("$state", supplier.StateCode);
        command.Parameters.AddWithValue("$charges", supplier.ChargesGst ? 1 : 0);
        command.Parameters.AddWithValue("$address", (object?)supplier.Address ?? DBNull.Value);
        command.Parameters.AddWithValue("$active", supplier.IsActive ? 1 : 0);
    }

    private static Supplier MapSupplier(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.GetString(4),
        reader.GetInt32(5) != 0,
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.GetInt32(7) != 0);

    // ---- Bills -------------------------------------------------------------------------------

    public PurchaseRecorded Record(PurchaseBill bill, string laneId, DateTimeOffset receivedAt, string? cashierName)
    {
        ArgumentNullException.ThrowIfNull(bill);
        ArgumentException.ThrowIfNullOrWhiteSpace(laneId);

        var billNo = bill.BillNo?.Trim() ?? string.Empty;

        if (billNo.Length == 0)
            throw new ArgumentException("A purchase needs the supplier's bill number, as printed.", nameof(bill));

        if (bill.Lines.Count == 0)
            throw new ArgumentException("A bill with nothing on it is not a purchase.", nameof(bill));

        if (Math.Abs(bill.RoundOff) > PurchaseBill.MostRoundOff)
            throw new ArgumentException($"A round-off of {bill.RoundOff:N2} is more than a rupee - a line is typed wrong.", nameof(bill));

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);

        if (FindSupplier(connection, transaction, bill.Supplier.Id) is null)
            throw new InvalidOperationException($"No supplier with id {bill.Supplier.Id}.");

        using (var duplicate = connection.CreateCommand())
        {
            duplicate.Transaction = transaction;
            duplicate.CommandText = """
                SELECT bill_date FROM purchases
                WHERE supplier_id = $supplier AND bill_no = $no AND voided_at IS NULL;
                """;
            duplicate.Parameters.AddWithValue("$supplier", bill.Supplier.Id);
            duplicate.Parameters.AddWithValue("$no", billNo);

            if (duplicate.ExecuteScalar() is string dated)
            {
                throw new InvalidOperationException(
                    $"Bill {billNo} from {bill.Supplier.Name} is already entered, dated {dated}. " +
                    "If that entry is wrong, cancel it first and enter the bill again.");
            }
        }

        long purchaseId;

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO purchases
                  (supplier_id, bill_no, bill_date, received_at, lane_id, is_inter_state, charges_gst,
                   taxable_value, total_cgst, total_sgst, total_igst, round_off, total, note)
                VALUES
                  ($supplier, $no, $date, $at, $lane, $inter, $charges,
                   $taxable, $cgst, $sgst, $igst, $round, $total, $note);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("$supplier", bill.Supplier.Id);
            insert.Parameters.AddWithValue("$no", billNo);
            insert.Parameters.AddWithValue("$date", bill.BillDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$at", receivedAt);
            insert.Parameters.AddWithValue("$lane", laneId);
            insert.Parameters.AddWithValue("$inter", bill.InterState ? 1 : 0);
            insert.Parameters.AddWithValue("$charges", bill.Supplier.ChargesGst ? 1 : 0);
            insert.Parameters.AddWithValue("$taxable", bill.TaxableValue);
            insert.Parameters.AddWithValue("$cgst", bill.Cgst);
            insert.Parameters.AddWithValue("$sgst", bill.Sgst);
            insert.Parameters.AddWithValue("$igst", bill.Igst);
            insert.Parameters.AddWithValue("$round", bill.RoundOff);
            insert.Parameters.AddWithValue("$total", bill.Total);
            insert.Parameters.AddWithValue("$note", string.IsNullOrWhiteSpace(bill.Note) ? DBNull.Value : bill.Note.Trim());

            purchaseId = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        var moved = 0;
        var notCounted = new List<string>();
        var costAbove = new List<string>();
        var reference = $"{bill.Supplier.Name} bill {billNo}";

        for (var i = 0; i < bill.Lines.Count; i++)
        {
            var line = bill.Lines[i];

            // Only a counted item's shelf goes up. For one nobody counts, adding the delivery to a
            // count that does not exist would invent a figure nobody took.
            var balance = StockRepository.WriteIn(connection, transaction, line.ItemId, laneId, StockReason.Purchase, reference,
                current => current + line.Quantity, startCounting: false);

            if (balance is null)
                notCounted.Add(line.Name);
            else
                moved++;

            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO purchase_lines
                      (purchase_id, line_no, item_id, name_snapshot, hsn_snapshot, unit_type, quantity, rate,
                       discount, gst_rate, taxable_value, cgst_amount, sgst_amount, igst_amount, line_total,
                       stock_moved, batch_no, expiry_date)
                    VALUES
                      ($purchase, $no, $item, $name, $hsn, $unit, $qty, $rate,
                       $discount, $gst, $taxable, $cgst, $sgst, $igst, $total,
                       $moved, $batch, $expiry);
                    """;
                insert.Parameters.AddWithValue("$purchase", purchaseId);
                insert.Parameters.AddWithValue("$no", i + 1);
                insert.Parameters.AddWithValue("$item", line.ItemId);
                insert.Parameters.AddWithValue("$name", line.Name);
                insert.Parameters.AddWithValue("$hsn", line.Hsn);
                insert.Parameters.AddWithValue("$unit", (int)line.Unit);
                insert.Parameters.AddWithValue("$qty", line.Quantity);
                insert.Parameters.AddWithValue("$rate", line.Rate);
                insert.Parameters.AddWithValue("$discount", line.Discount);
                insert.Parameters.AddWithValue("$gst", line.GstRate);
                insert.Parameters.AddWithValue("$taxable", line.TaxableValue);
                insert.Parameters.AddWithValue("$cgst", line.Cgst);
                insert.Parameters.AddWithValue("$sgst", line.Sgst);
                insert.Parameters.AddWithValue("$igst", line.Igst);
                insert.Parameters.AddWithValue("$total", line.LineTotal);
                insert.Parameters.AddWithValue("$moved", balance is null ? 0 : 1);
                insert.Parameters.AddWithValue("$batch", (object?)line.BatchNo ?? DBNull.Value);
                insert.Parameters.AddWithValue("$expiry", line.ExpiryDate is { } expiry
                    ? expiry.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : DBNull.Value);
                insert.ExecuteNonQuery();
            }

            // The latest price paid is what the margins are worked from from now on. Tax inclusive,
            // like the selling price it is compared with.
            using (var cost = connection.CreateCommand())
            {
                cost.Transaction = transaction;
                cost.CommandText = """
                    UPDATE items SET cost_price = $cost WHERE id = $id;
                    SELECT sell_price FROM items WHERE id = $id;
                    """;
                cost.Parameters.AddWithValue("$cost", line.UnitCost);
                cost.Parameters.AddWithValue("$id", line.ItemId);

                if (cost.ExecuteScalar() is { } sell && line.UnitCost > Convert.ToDecimal(sell, CultureInfo.InvariantCulture))
                    costAbove.Add($"{line.Name}: costs {line.UnitCost:N2}, sells at {Convert.ToDecimal(sell, CultureInfo.InvariantCulture):N2}");
            }
        }

        transaction.Commit();

        return new PurchaseRecorded(purchaseId, moved, notCounted, costAbove);
    }

    public void Void(long purchaseId, string reason, string laneId, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(laneId);

        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Say why the bill is being cancelled; the record keeps it.", nameof(reason));

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);

        string billNo;

        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT bill_no, voided_at FROM purchases WHERE id = $id;";
            read.Parameters.AddWithValue("$id", purchaseId);

            using var reader = read.ExecuteReader();

            if (!reader.Read())
                throw new InvalidOperationException($"No purchase with id {purchaseId}.");

            if (!reader.IsDBNull(1))
                throw new InvalidOperationException($"Bill {reader.GetString(0)} is already cancelled.");

            billNo = reader.GetString(0);
        }

        var taken = new List<(long ItemId, decimal Quantity)>();

        using (var lines = connection.CreateCommand())
        {
            lines.Transaction = transaction;
            lines.CommandText = "SELECT item_id, quantity FROM purchase_lines WHERE purchase_id = $id AND stock_moved = 1;";
            lines.Parameters.AddWithValue("$id", purchaseId);

            using var reader = lines.ExecuteReader();

            while (reader.Read())
                taken.Add((reader.GetInt64(0), reader.GetDecimal(1)));
        }

        // Exactly what the receipt put on, and only where it did.
        foreach (var (itemId, quantity) in taken)
        {
            StockRepository.WriteIn(connection, transaction, itemId, laneId, StockReason.PurchaseVoid, $"bill {billNo} cancelled",
                current => current - quantity, startCounting: false);
        }

        using (var mark = connection.CreateCommand())
        {
            mark.Transaction = transaction;
            mark.CommandText = "UPDATE purchases SET voided_at = $at, void_reason = $reason WHERE id = $id;";
            mark.Parameters.AddWithValue("$at", at);
            mark.Parameters.AddWithValue("$reason", reason.Trim());
            mark.Parameters.AddWithValue("$id", purchaseId);
            mark.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IReadOnlyList<PurchaseSummary> Recent(int limit = 50, long? supplierId = null)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT p.id, p.supplier_id, s.name, p.bill_no, p.bill_date, p.received_at,
                   (SELECT COUNT(*) FROM purchase_lines l WHERE l.purchase_id = p.id),
                   p.total, p.charges_gst, p.voided_at
            FROM purchases p
            JOIN suppliers s ON s.id = p.supplier_id
            {(supplierId is null ? "" : "WHERE p.supplier_id = $supplier")}
            ORDER BY p.received_at DESC, p.id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 10_000));

        if (supplierId is { } id)
            command.Parameters.AddWithValue("$supplier", id);

        using var reader = command.ExecuteReader();
        var purchases = new List<PurchaseSummary>();

        while (reader.Read())
        {
            purchases.Add(new PurchaseSummary(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                DateOnly.ParseExact(reader.GetString(4), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                reader.GetDateTimeOffset(5),
                reader.GetInt32(6),
                reader.GetDecimal(7),
                reader.GetInt32(8) != 0,
                reader.IsDBNull(9) ? null : reader.GetDateTimeOffset(9)));
        }

        return purchases;
    }

    public IReadOnlyList<PurchaseLine> Lines(long purchaseId)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT item_id, name_snapshot, hsn_snapshot, unit_type, quantity, rate, discount, gst_rate,
                   taxable_value, cgst_amount, sgst_amount, igst_amount, line_total, batch_no, expiry_date
            FROM purchase_lines WHERE purchase_id = $id ORDER BY line_no;
            """;
        command.Parameters.AddWithValue("$id", purchaseId);

        using var reader = command.ExecuteReader();
        var lines = new List<PurchaseLine>();

        while (reader.Read())
        {
            lines.Add(new PurchaseLine(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                (UnitType)reader.GetInt32(3),
                reader.GetDecimal(4),
                reader.GetDecimal(5),
                reader.GetDecimal(6),
                reader.GetDecimal(7),
                reader.GetDecimal(8),
                reader.GetDecimal(9),
                reader.GetDecimal(10),
                reader.GetDecimal(11),
                reader.GetDecimal(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : DateOnly.ParseExact(reader.GetString(14), "yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }

        return lines;
    }

    // ---- What is owed ------------------------------------------------------------------------

    /// <summary>The one definition of what the shop owes a supplier, in whole paise.</summary>
    private static string OwedPaiseSql(string supplierId) => $"""
        (COALESCE((SELECT {PaiseSql.Sum("op_p.total")} FROM purchases op_p
                   WHERE op_p.supplier_id = {supplierId} AND op_p.voided_at IS NULL), 0)
         - COALESCE((SELECT {PaiseSql.Sum("op_s.amount")} FROM supplier_payments op_s
                     WHERE op_s.supplier_id = {supplierId}), 0))
        """;

    private static long OwedPaise(SqliteConnection connection, SqliteTransaction? transaction, long supplierId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {OwedPaiseSql("$id")};";
        command.Parameters.AddWithValue("$id", supplierId);

        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public decimal Owed(long supplierId)
    {
        using var connection = _database.OpenConnection();
        return PaiseSql.Rupees(OwedPaise(connection, null, supplierId));
    }

    public IReadOnlyList<SupplierBalance> Balances(bool owingOnly = false)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {string.Join(", ", SupplierColumns.Split(", ").Select(c => "s." + c))},
                   {OwedPaiseSql("s.id")} AS owed,
                   (SELECT MAX(p.bill_date) FROM purchases p WHERE p.supplier_id = s.id AND p.voided_at IS NULL)
            FROM suppliers s
            WHERE s.is_active = 1 {(owingOnly ? $"AND {OwedPaiseSql("s.id")} > 0" : "")}
            ORDER BY owed DESC, s.name COLLATE NOCASE;
            """;

        using var reader = command.ExecuteReader();
        var balances = new List<SupplierBalance>();

        while (reader.Read())
        {
            balances.Add(new SupplierBalance(
                MapSupplier(reader),
                PaiseSql.Rupees(reader.GetInt64(8)),
                reader.IsDBNull(9) ? null : DateOnly.ParseExact(reader.GetString(9), "yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }

        return balances;
    }

    public SupplierPayment Pay(
        long supplierId,
        decimal amount,
        SupplierPaymentMethod method,
        string? reference,
        string laneId,
        DateTimeOffset paidAt,
        string? cashierName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(laneId);

        if (amount <= 0m)
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "A payment has to be more than nothing.");

        if (decimal.Round(amount, 2) != amount)
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "An amount cannot be finer than a paisa.");

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);

        var supplier = FindSupplier(connection, transaction, supplierId)
            ?? throw new InvalidOperationException($"No supplier with id {supplierId}.");

        var owed = PaiseSql.Rupees(OwedPaise(connection, transaction, supplierId));

        // Paying more than is owed is nearly always a mistyped amount. A genuine advance is rare
        // enough to enter the bill it is for first.
        if (amount > owed)
        {
            throw new InvalidOperationException(owed <= 0m
                ? $"Nothing is owed to {supplier.Name}. Enter their bill first."
                : $"{supplier.Name} is owed {owed:N2}. Paying {amount:N2} would be more than that.");
        }

        long id;

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO supplier_payments (supplier_id, paid_at, lane_id, method, amount, reference)
                VALUES ($supplier, $at, $lane, $method, $amount, $reference);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("$supplier", supplierId);
            insert.Parameters.AddWithValue("$at", paidAt);
            insert.Parameters.AddWithValue("$lane", laneId);
            insert.Parameters.AddWithValue("$method", method.ToString());
            insert.Parameters.AddWithValue("$amount", amount);
            insert.Parameters.AddWithValue("$reference", string.IsNullOrWhiteSpace(reference) ? DBNull.Value : reference.Trim());

            id = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        // Cash out of the till is cash the drawer will not hold at closing. Recorded in the same
        // transaction, so the payment and the drawer can never disagree about whether it happened.
        if (method == SupplierPaymentMethod.DrawerCash)
        {
            CashMovements.Insert(connection, transaction, laneId, paidAt, SupplierPaymentKind, -amount,
                $"Paid {supplier.Name}", $"supplier-payment:{id}", cashierName);
        }

        transaction.Commit();

        return new SupplierPayment(id, supplierId, paidAt, method, amount,
            string.IsNullOrWhiteSpace(reference) ? null : reference.Trim());
    }

    public IReadOnlyList<CreditMovement> History(long supplierId, int limit = 50)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();

        // Both sides in one ordered list, oldest first, so the running balance can be walked forward.
        command.CommandText = $"""
            SELECT at, what, paise FROM (
                SELECT received_at AS at, 'bill:' || bill_no AS what, {PaiseSql.Of("total")} AS paise
                FROM purchases WHERE supplier_id = $id AND voided_at IS NULL
                UNION ALL
                SELECT paid_at, 'paid:' || method || ':' || COALESCE(reference, ''), -{PaiseSql.Of("amount")}
                FROM supplier_payments WHERE supplier_id = $id
            )
            ORDER BY at, paise DESC;
            """;
        command.Parameters.AddWithValue("$id", supplierId);

        var movements = new List<CreditMovement>();
        long running = 0;

        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var paise = reader.GetInt64(2);
                running += paise;

                movements.Add(new CreditMovement(
                    reader.GetDateTimeOffset(0),
                    Describe(reader.GetString(1)),
                    PaiseSql.Rupees(paise),
                    PaiseSql.Rupees(running)));
            }
        }

        movements.Reverse();
        return movements.Take(Math.Max(1, limit)).ToList();
    }

    private static string Describe(string what)
    {
        if (what.StartsWith("bill:", StringComparison.Ordinal))
            return $"Bill {what[5..]}";

        var parts = what.Split(':', 3);
        var method = Enum.TryParse<SupplierPaymentMethod>(parts[1], out var m) ? MethodText(m) : parts[1];

        return parts.Length > 2 && parts[2].Length > 0 ? $"Paid, {method} ({parts[2]})" : $"Paid, {method}";
    }

    /// <summary>How a payment method reads in a sentence.</summary>
    public static string MethodText(SupplierPaymentMethod method) => method switch
    {
        SupplierPaymentMethod.DrawerCash => "cash from the till",
        SupplierPaymentMethod.OtherCash => "cash, not from the till",
        SupplierPaymentMethod.Upi => "UPI",
        SupplierPaymentMethod.Bank => "bank transfer",
        SupplierPaymentMethod.Cheque => "cheque",
        _ => method.ToString(),
    };
}

/// <summary>
/// Cash into or out of the drawer other than through a sale, for the day-end report to count.
/// </summary>
public static class CashMovements
{
    /// <param name="amount">Signed: negative leaves the drawer.</param>
    public static void Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string laneId,
        DateTimeOffset at,
        string kind,
        decimal amount,
        string? note,
        string? reference,
        string? cashierName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO cash_movements (lane_id, moved_at, kind, amount, note, reference, cashier_name)
            VALUES ($lane, $at, $kind, $amount, $note, $reference, $cashier);
            """;
        command.Parameters.AddWithValue("$lane", laneId);
        command.Parameters.AddWithValue("$at", at);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$amount", amount);
        command.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
        command.Parameters.AddWithValue("$reference", (object?)reference ?? DBNull.Value);
        command.Parameters.AddWithValue("$cashier", (object?)cashierName ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
}
