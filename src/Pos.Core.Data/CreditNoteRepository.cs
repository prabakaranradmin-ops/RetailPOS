using System.Globalization;
using Microsoft.Data.Sqlite;
using Pos.Core.Domain;

namespace Pos.Core.Data;

/// <summary>
/// Credit notes: goods a customer brought back, and everything returning them changes.
/// </summary>
/// <remarks>
/// <para>
/// Issuing one is a single transaction: its number, its lines, the shelf count of what goes back on
/// the shelf, the drawer if the refund was cash, and the customer's points. A return half-recorded -
/// the money handed back but the goods not counted, or the other way round - is a drawer or a shelf
/// that is out with nothing to say why.
/// </para>
/// <para>
/// The bill itself is never touched. It was issued and it stays what it was; the credit note is the
/// second document that says part of it came back.
/// </para>
/// </remarks>
public sealed class CreditNoteRepository(PosDatabase database) : ICreditNoteStore
{
    private readonly PosDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly InvoiceRepository _invoices = new(database);

    /// <summary>What goes on the drawer movement for a cash refund.</summary>
    public const string RefundKind = "Refund";

    /// <summary>The series credit notes are numbered in, beside the lane's bills.</summary>
    private static string Series(string laneId) => "CN:" + laneId;

    /// <summary>A credit note number: <c>CN/26-27/L1-7</c>. Short enough for the GST return's sixteen characters.</summary>
    public static string Number(string laneId, FiscalYear year, long sequence) =>
        string.Create(CultureInfo.InvariantCulture, $"CN/{year.ShortLabel}/{laneId}-{sequence}");

    public ReturnableBill? Returnable(string invoiceNo)
    {
        if (string.IsNullOrWhiteSpace(invoiceNo))
            return null;

        var invoice = _invoices.FindByInvoiceNo(invoiceNo.Trim());

        if (invoice is null)
            return null;

        using var connection = _database.OpenConnection();
        return Returnable(connection, null, invoice);
    }

    private static ReturnableBill Returnable(SqliteConnection connection, SqliteTransaction? transaction, SettledInvoice invoice)
    {
        var charged = new Dictionary<int, LineFigures>();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT line_no, taxable_value, cgst_amount, sgst_amount, igst_amount, line_total
                FROM invoice_lines WHERE invoice_id = $id ORDER BY line_no;
                """;
            command.Parameters.AddWithValue("$id", invoice.Id);

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                charged[reader.GetInt32(0)] = new LineFigures(
                    Pos.Core.Tax.Money.ToPresentation(reader.GetDecimal(1)),
                    reader.GetDecimal(2),
                    reader.GetDecimal(3),
                    reader.GetDecimal(4),
                    reader.GetDecimal(5));
            }
        }

        var (returned, credited) = AlreadyReturned(connection, transaction, invoice.Id);
        long refundedPaise;
        int pointsReversed;

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT COALESCE(SUM({CreditRepository.RefundedPaiseSql("n")}), 0), COALESCE(SUM(n.points_reversed), 0)
                FROM credit_notes n WHERE n.invoice_id = $id;
                """;
            command.Parameters.AddWithValue("$id", invoice.Id);

            using var reader = command.ExecuteReader();
            reader.Read();

            refundedPaise = reader.GetInt64(0);
            pointsReversed = reader.GetInt32(1);
        }

        var lines = invoice.Sale.Lines
            .Select((line, index) =>
            {
                var lineNo = index + 1;

                return new ReturnableLine(
                    lineNo,
                    line,
                    charged.TryGetValue(lineNo, out var figures) ? figures : LineFigures.Of(line),
                    returned.GetValueOrDefault(lineNo),
                    credited.GetValueOrDefault(lineNo));
            })
            .ToList();

        return new ReturnableBill(invoice, lines, PaiseSql.Rupees(refundedPaise), pointsReversed);
    }

    /// <summary>
    /// What earlier credit notes took back from each line of a bill. Summed here in decimals rather
    /// than by SQLite, which would add the quantities as floating point.
    /// </summary>
    private static (Dictionary<int, decimal> Quantity, Dictionary<int, LineFigures> Credited) AlreadyReturned(
        SqliteConnection connection, SqliteTransaction? transaction, long invoiceId)
    {
        var quantity = new Dictionary<int, decimal>();
        var credited = new Dictionary<int, LineFigures>();

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT l.invoice_line_no, l.quantity, l.taxable_value, l.cgst_amount, l.sgst_amount, l.igst_amount, l.line_total
            FROM credit_note_lines l JOIN credit_notes n ON n.id = l.credit_note_id
            WHERE n.invoice_id = $id;
            """;
        command.Parameters.AddWithValue("$id", invoiceId);

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var lineNo = reader.GetInt32(0);

            quantity[lineNo] = quantity.GetValueOrDefault(lineNo) + reader.GetDecimal(1);
            credited[lineNo] = credited.GetValueOrDefault(lineNo) + new LineFigures(
                reader.GetDecimal(2), reader.GetDecimal(3), reader.GetDecimal(4), reader.GetDecimal(5), reader.GetDecimal(6));
        }

        return (quantity, credited);
    }

    public CreditNote Issue(CreditNoteDraft draft, string laneId, DateTimeOffset at, string? cashierName)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(laneId);

        var invoice = draft.Bill.Invoice;

        if (invoice.IsVoided)
            throw new InvalidOperationException($"{invoice.InvoiceNo} was voided, so nothing on it can come back.");

        if (draft.Lines.Count == 0)
            throw new InvalidOperationException("A credit note needs something on it.");

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);

        long? customerId;

        using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "SELECT voided_at, customer_id FROM invoices WHERE id = $id;";
            check.Parameters.AddWithValue("$id", invoice.Id);

            using var reader = check.ExecuteReader();

            if (!reader.Read())
                throw new InvalidOperationException($"There is no bill {invoice.InvoiceNo}.");

            if (!reader.IsDBNull(0))
                throw new InvalidOperationException($"{invoice.InvoiceNo} has been voided since it was looked up, so nothing on it can come back.");

            customerId = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        }

        // Read again inside the transaction. Another lane returning goods from the same bill since it
        // was looked up changes what is left, and what this draft is priced against.
        var (returned, _) = AlreadyReturned(connection, transaction, invoice.Id);

        foreach (var line in draft.Bill.Lines)
        {
            if (returned.GetValueOrDefault(line.LineNo) != line.AlreadyReturned)
                throw new InvalidOperationException($"Goods have been returned against {invoice.InvoiceNo} since it was looked up. Look it up again.");
        }

        foreach (var line in draft.Lines)
        {
            var source = draft.Bill.Lines.Single(l => l.LineNo == line.InvoiceLineNo);

            if (line.Quantity > source.Remaining)
                throw new InvalidOperationException($"Only {source.Remaining:0.###} of {line.Name} can come back.");
        }

        if (draft.Refund is TenderType.StoreCredit)
        {
            if (customerId is null)
                throw new InvalidOperationException("The bill has no customer now, so there is no khata to refund to.");

            // Off what they owe, and no further. A refund that took the khata below nothing would
            // leave the shop holding the customer's money as an advance, which is a different thing
            // and not one to make by picking the wrong refund.
            var owed = PaiseSql.Rupees(CreditRepository.OwedPaise(connection, transaction, customerId.Value));

            if (draft.Refunded > owed)
            {
                throw new InvalidOperationException(owed <= 0m
                    ? "They owe nothing on their khata, so the refund has to be in money."
                    : $"They owe {owed:0.00} on their khata, less than the {draft.Refunded:0.00} to refund. Refund it in money instead.");
            }
        }

        var year = FiscalYear.For(at);
        var sequence = InvoiceRepository.TakeNextSequence(connection, transaction, Series(laneId), year);
        var number = Number(laneId, year, sequence);

        long id;

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO credit_notes
                  (credit_note_no, lane_id, created_at, invoice_id, invoice_no, customer_id, tax_mode, reason,
                   refund_tender, taxable_value, total_cgst, total_sgst, total_igst, total, round_off,
                   points_reversed, cashier_name)
                VALUES
                  ($no, $lane, $at, $invoiceId, $invoiceNo, $customer, $taxMode, $reason,
                   $refund, $taxable, $cgst, $sgst, $igst, $total, $roundOff,
                   $points, $cashier);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("$no", number);
            insert.Parameters.AddWithValue("$lane", laneId);
            insert.Parameters.AddWithValue("$at", at);
            insert.Parameters.AddWithValue("$invoiceId", invoice.Id);
            insert.Parameters.AddWithValue("$invoiceNo", invoice.InvoiceNo);
            insert.Parameters.AddWithValue("$customer", (object?)customerId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$taxMode", invoice.Sale.TaxMode.ToString());
            insert.Parameters.AddWithValue("$reason", draft.Reason);
            insert.Parameters.AddWithValue("$refund", (int)draft.Refund);
            insert.Parameters.AddWithValue("$taxable", draft.TaxableValue);
            insert.Parameters.AddWithValue("$cgst", draft.Cgst);
            insert.Parameters.AddWithValue("$sgst", draft.Sgst);
            insert.Parameters.AddWithValue("$igst", draft.Igst);
            insert.Parameters.AddWithValue("$total", draft.LinesTotal);
            insert.Parameters.AddWithValue("$roundOff", draft.RoundOff);
            insert.Parameters.AddWithValue("$points", customerId is null ? 0 : draft.PointsReversed);
            insert.Parameters.AddWithValue("$cashier", (object?)cashierName ?? DBNull.Value);

            id = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        foreach (var line in draft.Lines)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO credit_note_lines
                  (credit_note_id, invoice_line_no, item_id, name_snapshot, hsn_snapshot, unit_type, quantity,
                   gst_rate, is_inter_state, taxable_value, cgst_amount, sgst_amount, igst_amount, line_total, restocked)
                VALUES
                  ($id, $lineNo, $item, $name, $hsn, $unit, $qty,
                   $rate, $inter, $taxable, $cgst, $sgst, $igst, $total, $restocked);
                """;
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$lineNo", line.InvoiceLineNo);
            insert.Parameters.AddWithValue("$item", line.ItemId);
            insert.Parameters.AddWithValue("$name", line.Name);
            insert.Parameters.AddWithValue("$hsn", line.Hsn);
            insert.Parameters.AddWithValue("$unit", (int)line.Unit);
            insert.Parameters.AddWithValue("$qty", line.Quantity);
            insert.Parameters.AddWithValue("$rate", line.GstRate);
            insert.Parameters.AddWithValue("$inter", line.InterState ? 1 : 0);
            insert.Parameters.AddWithValue("$taxable", line.TaxableValue);
            insert.Parameters.AddWithValue("$cgst", line.Cgst);
            insert.Parameters.AddWithValue("$sgst", line.Sgst);
            insert.Parameters.AddWithValue("$igst", line.Igst);
            insert.Parameters.AddWithValue("$total", line.LineTotal);
            insert.Parameters.AddWithValue("$restocked", line.Restocked ? 1 : 0);
            insert.ExecuteNonQuery();

            // Back on the shelf, if it is fit to sell and the shop counts it. Damaged goods are
            // refunded but not counted back in: they are not there to sell.
            if (line.Restocked)
            {
                var quantity = line.Quantity;
                StockRepository.WriteIn(connection, transaction, line.ItemId, laneId, StockReason.Return, number,
                    current => current + quantity, startCounting: false);
            }
        }

        // Cash handed back came out of the drawer, and the day-end report has to say so.
        if (draft.Refund is TenderType.Cash && draft.Refunded != 0m)
        {
            CashMovements.Insert(connection, transaction, laneId, at, RefundKind, -draft.Refunded,
                $"bill {invoice.InvoiceNo}", number, cashierName);
        }

        if (customerId is not null && draft.PointsReversed > 0)
        {
            using var points = connection.CreateCommand();
            points.Transaction = transaction;

            // Never below nothing: points already spent elsewhere are not clawed back as a debt.
            points.CommandText = "UPDATE customers SET loyalty_balance = MAX(0, loyalty_balance - $points) WHERE id = $id;";
            points.Parameters.AddWithValue("$points", draft.PointsReversed);
            points.Parameters.AddWithValue("$id", customerId.Value);
            points.ExecuteNonQuery();
        }

        transaction.Commit();

        return Find(number) ?? throw new InvalidOperationException($"Credit note {number} was written but cannot be read back.");
    }

    public CreditNote? Find(string creditNoteNo)
    {
        if (string.IsNullOrWhiteSpace(creditNoteNo))
            return null;

        using var connection = _database.OpenConnection();

        long id;
        CreditNote note;

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT n.id, n.credit_note_no, n.created_at, n.lane_id, n.invoice_no, i.created_at,
                       c.id, c.mobile_no, c.name, c.loyalty_balance, c.state_code,
                       n.tax_mode, n.reason, n.refund_tender, n.round_off, n.points_reversed, n.cashier_name
                FROM credit_notes n
                JOIN invoices i ON i.id = n.invoice_id
                LEFT JOIN customers c ON c.id = n.customer_id
                WHERE n.credit_note_no = $no;
                """;
            command.Parameters.AddWithValue("$no", creditNoteNo.Trim());

            using var reader = command.ExecuteReader();

            if (!reader.Read())
                return null;

            id = reader.GetInt64(0);

            var customer = reader.IsDBNull(6)
                ? null
                : new Customer
                {
                    Id = reader.GetInt64(6),
                    MobileNo = reader.GetString(7),
                    Name = reader.IsDBNull(8) ? null : reader.GetString(8),
                    LoyaltyBalance = reader.GetInt32(9),
                    StateCode = reader.IsDBNull(10) ? null : reader.GetString(10),
                };

            note = new CreditNote(
                id,
                reader.GetString(1),
                reader.GetDateTimeOffset(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetDateTimeOffset(5),
                customer,
                Enum.TryParse<TaxMode>(reader.GetString(11), out var mode) ? mode : TaxMode.Gst,
                reader.GetString(12),
                (TenderType)reader.GetInt32(13),
                [],
                reader.GetDecimal(14),
                reader.GetInt32(15),
                reader.IsDBNull(16) ? null : reader.GetString(16));
        }

        var lines = new List<CreditNoteLine>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT invoice_line_no, item_id, name_snapshot, hsn_snapshot, unit_type, quantity, gst_rate,
                       is_inter_state, taxable_value, cgst_amount, sgst_amount, igst_amount, line_total, restocked
                FROM credit_note_lines WHERE credit_note_id = $id ORDER BY id;
                """;
            command.Parameters.AddWithValue("$id", id);

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                lines.Add(new CreditNoteLine(
                    reader.GetInt32(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    (UnitType)reader.GetInt32(4),
                    reader.GetDecimal(5),
                    reader.GetDecimal(6),
                    reader.GetInt32(7) != 0,
                    reader.GetDecimal(8),
                    reader.GetDecimal(9),
                    reader.GetDecimal(10),
                    reader.GetDecimal(11),
                    reader.GetDecimal(12),
                    reader.GetInt32(13) != 0));
            }
        }

        return note with { Lines = lines };
    }

    /// <summary>The credit notes issued against a bill, oldest first.</summary>
    public IReadOnlyList<string> ForInvoice(string invoiceNo)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT credit_note_no FROM credit_notes WHERE invoice_no = $no ORDER BY id;";
        command.Parameters.AddWithValue("$no", invoiceNo.Trim());

        var numbers = new List<string>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
            numbers.Add(reader.GetString(0));

        return numbers;
    }
}
