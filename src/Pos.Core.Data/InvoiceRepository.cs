using Microsoft.Data.Sqlite;
using Pos.Core.Domain;

namespace Pos.Core.Data;

/// <summary>
/// Writes and reads settled invoices, and owns the lane's invoice number sequence.
/// </summary>
public sealed class InvoiceRepository : IInvoiceStore
{
    private readonly PosDatabase _database;

    public InvoiceRepository(PosDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>
    /// Mints the number and writes header, lines and payments as one unit.
    /// </summary>
    /// <remarks>
    /// The number is taken from the lane's sequence <em>inside</em> this transaction. That is what
    /// makes the sequence gapless: if anything below fails, the rollback puts the number back, so
    /// a failed save cannot leave a hole in a run of invoice numbers that has to be unbroken. The
    /// transaction is IMMEDIATE so two threads on one lane cannot both read the same next value.
    /// </remarks>
    public SettledInvoice Save(SaleDraft sale)
    {
        ArgumentNullException.ThrowIfNull(sale);

        if (sale.Lines.Count == 0)
            throw new InvalidOperationException("An invoice must have at least one line.");

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);

        var year = sale.CreatedAt.Year;
        var sequence = TakeNextSequence(connection, transaction, sale.LaneId, year);
        var invoiceNo = FormatInvoiceNo(sale.LaneId, year, sequence);

        var invoiceId = InsertHeader(connection, transaction, sale, invoiceNo);
        InsertLines(connection, transaction, invoiceId, sale.Lines);
        InsertPayments(connection, transaction, invoiceId, sale.Payments);

        transaction.Commit();

        return new SettledInvoice(invoiceId, invoiceNo, sale);
    }

    /// <summary>
    /// `{lane}-{year}-{sequence}`, per ARCHITECTURE.md section 6. The lane prefix is what lets
    /// several tills number their own invoices with nothing coordinating them.
    /// </summary>
    public static string FormatInvoiceNo(string laneId, int year, long sequence) =>
        $"{laneId}-{year}-{sequence:D6}";

    private static long TakeNextSequence(SqliteConnection connection, SqliteTransaction transaction, string laneId, int year)
    {
        using (var seed = connection.CreateCommand())
        {
            seed.Transaction = transaction;
            seed.CommandText = """
                INSERT INTO invoice_sequences (lane_id, year, next_value)
                VALUES ($lane, $year, 1)
                ON CONFLICT (lane_id, year) DO NOTHING;
                """;
            seed.Parameters.AddWithValue("$lane", laneId);
            seed.Parameters.AddWithValue("$year", year);
            seed.ExecuteNonQuery();
        }

        using var take = connection.CreateCommand();
        take.Transaction = transaction;
        take.CommandText = """
            UPDATE invoice_sequences
            SET next_value = next_value + 1
            WHERE lane_id = $lane AND year = $year
            RETURNING next_value - 1;
            """;
        take.Parameters.AddWithValue("$lane", laneId);
        take.Parameters.AddWithValue("$year", year);

        return Convert.ToInt64(take.ExecuteScalar());
    }

    private static long InsertHeader(SqliteConnection connection, SqliteTransaction transaction, SaleDraft sale, string invoiceNo)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO invoices
              (invoice_no, lane_id, created_at, customer_id, status, hold_token,
               subtotal_taxable, total_discount, total_cgst, total_sgst, total_igst, grand_total,
               points_redeemed, points_earned, change_due)
            VALUES
              ($invoiceNo, $lane, $createdAt, $customerId, $status, $holdToken,
               $taxable, $discount, $cgst, $sgst, $igst, $grandTotal,
               $pointsRedeemed, $pointsEarned, $changeDue);
            SELECT last_insert_rowid();
            """;

        var totals = sale.Totals;
        command.Parameters.AddWithValue("$invoiceNo", invoiceNo);
        command.Parameters.AddWithValue("$lane", sale.LaneId);
        command.Parameters.AddWithValue("$createdAt", sale.CreatedAt);
        command.Parameters.AddWithValue("$customerId", (object?)sale.Customer?.Id ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", (int)InvoiceStatus.Settled);
        command.Parameters.AddWithValue("$holdToken", (object?)sale.RecalledFromToken ?? DBNull.Value);
        command.Parameters.AddWithValue("$taxable", totals.SubtotalTaxable);
        command.Parameters.AddWithValue("$discount", totals.TotalDiscount);
        command.Parameters.AddWithValue("$cgst", totals.TotalCgst);
        command.Parameters.AddWithValue("$sgst", totals.TotalSgst);
        command.Parameters.AddWithValue("$igst", totals.TotalIgst);
        command.Parameters.AddWithValue("$grandTotal", totals.GrandTotal);
        command.Parameters.AddWithValue("$pointsRedeemed", sale.PointsRedeemed);
        command.Parameters.AddWithValue("$pointsEarned", sale.PointsEarned);
        command.Parameters.AddWithValue("$changeDue", sale.ChangeDue);

        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void InsertLines(SqliteConnection connection, SqliteTransaction transaction, long invoiceId, IReadOnlyList<InvoiceLine> lines)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO invoice_lines
              (invoice_id, line_no, item_id, name_snapshot, hsn_snapshot, barcode_snapshot, batch_no,
               unit_type, mrp, unit_price, is_tax_inclusive, gst_rate, quantity, discount,
               is_inter_state, taxable_value, cgst_amount, sgst_amount, igst_amount, line_total)
            VALUES
              ($invoiceId, $lineNo, $itemId, $name, $hsn, $barcode, $batch,
               $unitType, $mrp, $unitPrice, $taxInclusive, $gstRate, $quantity, $discount,
               $interState, $taxable, $cgst, $sgst, $igst, $lineTotal);
            """;

        foreach (var name in new[]
                 {
                     "$invoiceId", "$lineNo", "$itemId", "$name", "$hsn", "$barcode", "$batch",
                     "$unitType", "$mrp", "$unitPrice", "$taxInclusive", "$gstRate", "$quantity",
                     "$discount", "$interState", "$taxable", "$cgst", "$sgst", "$igst", "$lineTotal",
                 })
        {
            command.Parameters.Add(new SqliteParameter(name, null));
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var tax = line.Tax;

            command.Parameters["$invoiceId"].Value = invoiceId;
            command.Parameters["$lineNo"].Value = i + 1;
            command.Parameters["$itemId"].Value = line.ItemId;
            command.Parameters["$name"].Value = line.NameSnapshot;
            command.Parameters["$hsn"].Value = line.HsnSnapshot;
            command.Parameters["$barcode"].Value = (object?)line.BarcodeSnapshot ?? DBNull.Value;
            command.Parameters["$batch"].Value = (object?)line.BatchNo ?? DBNull.Value;
            command.Parameters["$unitType"].Value = (int)line.Unit;
            command.Parameters["$mrp"].Value = line.Mrp;
            command.Parameters["$unitPrice"].Value = line.UnitPrice;
            command.Parameters["$taxInclusive"].Value = line.IsTaxInclusive ? 1 : 0;
            command.Parameters["$gstRate"].Value = line.GstRate;
            command.Parameters["$quantity"].Value = line.Quantity;
            command.Parameters["$discount"].Value = line.Discount;
            command.Parameters["$interState"].Value = line.IsInterState ? 1 : 0;

            // The computed figures are stored alongside the inputs rather than re-derived on read.
            // A reprint years from now must show the tax that was actually charged, even if the
            // engine's rounding rules have moved on since.
            command.Parameters["$taxable"].Value = tax.TaxableValue;
            command.Parameters["$cgst"].Value = tax.Cgst;
            command.Parameters["$sgst"].Value = tax.Sgst;
            command.Parameters["$igst"].Value = tax.Igst;
            command.Parameters["$lineTotal"].Value = tax.LineTotal;

            command.ExecuteNonQuery();
        }
    }

    private static void InsertPayments(SqliteConnection connection, SqliteTransaction transaction, long invoiceId, IReadOnlyList<Tender> payments)
    {
        if (payments.Count == 0)
            return;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO payments (invoice_id, tender_type, amount, reference_no)
            VALUES ($invoiceId, $type, $amount, $reference);
            """;

        foreach (var name in new[] { "$invoiceId", "$type", "$amount", "$reference" })
            command.Parameters.Add(new SqliteParameter(name, null));

        foreach (var payment in payments)
        {
            command.Parameters["$invoiceId"].Value = invoiceId;
            command.Parameters["$type"].Value = (int)payment.Type;
            command.Parameters["$amount"].Value = payment.Amount;
            command.Parameters["$reference"].Value = (object?)payment.ReferenceNo ?? DBNull.Value;
            command.ExecuteNonQuery();
        }
    }

    /// <summary>The last sale this lane rang up.</summary>
    public SettledInvoice? FindLatest(string laneId)
    {
        if (string.IsNullOrWhiteSpace(laneId))
            return null;

        return FindByInvoiceNo(ScalarInvoiceNo(
            "SELECT invoice_no FROM invoices WHERE lane_id = $key AND status = $settled ORDER BY id DESC LIMIT 1;",
            laneId.Trim()));
    }

    /// <summary>
    /// The most recent sale to a customer. Found by mobile number, because a customer asking for a
    /// duplicate has their phone rather than the invoice number.
    /// </summary>
    public SettledInvoice? FindLatestForMobile(string mobileNo)
    {
        if (string.IsNullOrWhiteSpace(mobileNo))
            return null;

        return FindByInvoiceNo(ScalarInvoiceNo(
            """
            SELECT i.invoice_no
            FROM invoices i
            JOIN customers c ON c.id = i.customer_id
            WHERE c.mobile_no = $key AND i.status = $settled
            ORDER BY i.id DESC LIMIT 1;
            """,
            mobileNo.Trim()));
    }

    private string? ScalarInvoiceNo(string sql, string key)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$settled", (int)InvoiceStatus.Settled);

        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : (string)value;
    }

    public SettledInvoice? FindByInvoiceNo(string? invoiceNo)
    {
        if (string.IsNullOrWhiteSpace(invoiceNo))
            return null;

        using var connection = _database.OpenConnection();

        long invoiceId;
        SaleDraft sale;

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT i.id, i.lane_id, i.created_at, i.hold_token,
                       i.subtotal_taxable, i.total_discount, i.total_cgst, i.total_sgst,
                       i.total_igst, i.grand_total, i.points_redeemed, i.points_earned, i.change_due,
                       c.id, c.mobile_no, c.name, c.loyalty_balance, c.state_code
                FROM invoices i
                LEFT JOIN customers c ON c.id = i.customer_id
                WHERE i.invoice_no = $invoiceNo;
                """;
            command.Parameters.AddWithValue("$invoiceNo", invoiceNo.Trim());

            using var reader = command.ExecuteReader();

            if (!reader.Read())
                return null;

            invoiceId = reader.GetInt64(0);

            var customer = reader.IsDBNull(13)
                ? null
                : new Customer
                {
                    Id = reader.GetInt64(13),
                    MobileNo = reader.GetString(14),
                    Name = reader.IsDBNull(15) ? null : reader.GetString(15),
                    LoyaltyBalance = reader.GetInt32(16),
                    StateCode = reader.IsDBNull(17) ? null : reader.GetString(17),
                };

            var totals = new InvoiceTotals(
                LineCount: 0,
                TotalQuantity: 0m,
                SubtotalTaxable: reader.GetDecimal(4),
                TotalDiscount: reader.GetDecimal(5),
                TotalCgst: reader.GetDecimal(6),
                TotalSgst: reader.GetDecimal(7),
                TotalIgst: reader.GetDecimal(8),
                GrandTotal: reader.GetDecimal(9));

            sale = new SaleDraft(
                reader.GetString(1),
                reader.GetDateTimeOffset(2),
                customer,
                [],
                totals,
                [],
                reader.GetDecimal(12),
                reader.GetInt32(10),
                reader.GetInt32(11),
                reader.IsDBNull(3) ? null : reader.GetString(3));
        }

        var lines = ReadLines(connection, invoiceId);
        var payments = ReadPayments(connection, invoiceId);

        // Line count and quantity are recovered from the lines themselves rather than stored twice.
        var restored = sale with
        {
            Lines = lines,
            Payments = payments,
            Totals = sale.Totals with
            {
                LineCount = lines.Count,
                TotalQuantity = lines.Sum(l => l.Quantity),
            },
        };

        return new SettledInvoice(invoiceId, invoiceNo.Trim(), restored);
    }

    private static List<InvoiceLine> ReadLines(SqliteConnection connection, long invoiceId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT item_id, name_snapshot, hsn_snapshot, barcode_snapshot, batch_no, unit_type,
                   mrp, unit_price, is_tax_inclusive, gst_rate, quantity, discount, is_inter_state
            FROM invoice_lines
            WHERE invoice_id = $invoiceId
            ORDER BY line_no;
            """;
        command.Parameters.AddWithValue("$invoiceId", invoiceId);

        var lines = new List<InvoiceLine>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
            lines.Add(ReadLine(reader));

        return lines;
    }

    /// <summary>Shared by invoice lines and parked-bill lines, which carry the same columns.</summary>
    internal static InvoiceLine ReadLine(SqliteDataReader reader) => InvoiceLine.Rehydrate(
        itemId: reader.GetInt64(0),
        nameSnapshot: reader.GetString(1),
        hsnSnapshot: reader.GetString(2),
        barcodeSnapshot: reader.IsDBNull(3) ? null : reader.GetString(3),
        batchNo: reader.IsDBNull(4) ? null : reader.GetString(4),
        unit: (UnitType)reader.GetInt32(5),
        mrp: reader.GetDecimal(6),
        unitPrice: reader.GetDecimal(7),
        isTaxInclusive: reader.GetInt32(8) != 0,
        gstRate: reader.GetDecimal(9),
        quantity: reader.GetDecimal(10),
        discount: reader.GetDecimal(11),
        isInterState: reader.GetInt32(12) != 0);

    private static List<Tender> ReadPayments(SqliteConnection connection, long invoiceId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT tender_type, amount, reference_no
            FROM payments
            WHERE invoice_id = $invoiceId
            ORDER BY id;
            """;
        command.Parameters.AddWithValue("$invoiceId", invoiceId);

        var payments = new List<Tender>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            payments.Add(new Tender(
                (TenderType)reader.GetInt32(0),
                reader.GetDecimal(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return payments;
    }
}
