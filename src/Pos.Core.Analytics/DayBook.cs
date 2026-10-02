using System.Globalization;
using Microsoft.Data.Sqlite;
using Pos.Core.Data;
using Pos.Core.Domain;

namespace Pos.Core.Analytics;

/// <summary>A period's day book: every voucher in it, in order, and anything the accountant should know.</summary>
/// <param name="To">The day after the last one in it.</param>
/// <param name="Notes">What did not add up and was put right on round-off, said so somebody looks.</param>
public sealed record DayBookData(
    string LaneId,
    DateOnly From,
    DateOnly To,
    IReadOnlyList<DayBookVoucher> Vouchers,
    IReadOnlyList<string> Notes)
{
    public bool HasAnything => Vouchers.Count > 0;

    public int Count(VoucherKind kind) => Vouchers.Count(v => v.Kind == kind);
}

/// <summary>
/// Reads the books into a day book: the shop's bills, returns, khata repayments, purchases, payments,
/// debit notes, expenses and cash moved in and out, as balanced vouchers an accountant can load.
/// </summary>
/// <remarks>
/// <para>
/// Every sum is taken in whole paise, and every voucher balances: the sales or purchase ledger for a
/// rate takes the lines' totals less the tax on them, so whatever the four-decimal taxable values
/// round to, the voucher's two sides are the same to the paisa. A bill whose payments do not match its
/// lines - something the till never writes, but an old book might hold - is put right on round-off
/// and named in the notes, never left out.
/// </para>
/// <para>
/// What is not in it, on purpose: voided bills and cancelled purchase bills, which did not happen;
/// the opening float, which is the shop's own cash in the drawer rather than money coming in; and the
/// drawer's side of a refund, a supplier paid from the till or an expense, each of which is already a
/// voucher of its own.
/// </para>
/// <para>
/// The till's own records - bills, returns, repayments, expenses, the drawer - are this lane's. The
/// supplier's side - purchases, payments, debit notes - is the whole book's, as the GST return reads
/// them.
/// </para>
/// </remarks>
public sealed class DayBookQuery(PosDatabase database)
{
    private readonly PosDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

    public DayBookData Gather(string laneId, DateOnly from, DateOnly to, DayBookLedgers ledgers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(laneId);
        ArgumentNullException.ThrowIfNull(ledgers);

        if (to <= from)
            throw new ArgumentOutOfRangeException(nameof(to), to, "The day book has to cover at least one day.");

        var window = new Window(laneId, LocalMidnight(from), LocalMidnight(to), from, to);
        var notes = new List<string>();
        var vouchers = new List<DayBookVoucher>();

        using var connection = _database.OpenConnection();

        vouchers.AddRange(Sales(connection, window, ledgers, notes));
        vouchers.AddRange(CreditNotes(connection, window, ledgers));
        vouchers.AddRange(KhataReceipts(connection, window, ledgers));
        vouchers.AddRange(Purchases(connection, window, ledgers, notes));
        vouchers.AddRange(SupplierPayments(connection, window, ledgers));
        vouchers.AddRange(DebitNotes(connection, window, ledgers));
        vouchers.AddRange(Expenses(connection, window, ledgers));
        vouchers.AddRange(CashMoved(connection, window, ledgers));

        var ordered = vouchers
            .OrderBy(v => v.Date)
            .ThenBy(v => v.Kind)
            .ThenBy(v => v.Number, StringComparer.Ordinal)
            .ToList();

        return new DayBookData(laneId, from, to, ordered, notes);
    }

    /// <summary>A month's day book: the first of it to the first of the next.</summary>
    public DayBookData Month(string laneId, DateOnly month, DayBookLedgers ledgers)
    {
        var first = new DateOnly(month.Year, month.Month, 1);
        return Gather(laneId, first, first.AddMonths(1), ledgers);
    }

    private sealed record Window(string LaneId, DateTimeOffset From, DateTimeOffset To, DateOnly FromDay, DateOnly ToDay);

    // ---- Bills ----------------------------------------------------------------------------------

    private sealed record Rated(decimal Rate, bool InterState, long Total, long Cgst, long Sgst, long Igst)
    {
        public long Tax => Cgst + Sgst + Igst;
    }

    private static List<DayBookVoucher> Sales(SqliteConnection connection, Window window, DayBookLedgers ledgers, List<string> notes)
    {
        var bills = new List<(long Id, string No, DateOnly Date, bool Supply, long RoundOff, long Change, string? Customer)>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT i.id, i.invoice_no, i.created_at, i.tax_mode, {PaiseSql.Of("i.round_off")}, {PaiseSql.Of("i.change_due")},
                       c.name, c.mobile_no
                FROM invoices i
                LEFT JOIN customers c ON c.id = i.customer_id
                WHERE i.lane_id = $lane AND i.created_at >= $from AND i.created_at < $to
                  AND i.voided_at IS NULL AND i.hold_token IS NULL
                ORDER BY i.created_at, i.id;
                """;
            Bind(command, window);

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                bills.Add((
                    reader.GetInt64(0),
                    reader.GetString(1),
                    Day(reader.GetDateTimeOffset(2)),
                    reader.GetString(3) == nameof(TaxMode.Composition),
                    reader.GetInt64(4),
                    reader.GetInt64(5),
                    Customer(reader, 6)));
            }
        }

        if (bills.Count == 0)
            return [];

        var lines = ReadRated(connection, window, """
            SELECT l.invoice_id, CAST(l.gst_rate AS REAL), l.is_inter_state,
                   {0}, {1}, {2}, {3}
            FROM invoice_lines l
            JOIN invoices i ON i.id = l.invoice_id
            WHERE i.lane_id = $lane AND i.created_at >= $from AND i.created_at < $to
              AND i.voided_at IS NULL AND i.hold_token IS NULL
            GROUP BY l.invoice_id, CAST(l.gst_rate AS REAL), l.is_inter_state;
            """, "l.line_total", "l.cgst_amount", "l.sgst_amount", "l.igst_amount");

        var payments = new Dictionary<long, List<(TenderType Type, long Amount)>>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT p.invoice_id, p.tender_type, {PaiseSql.Sum("p.amount")}
                FROM payments p
                JOIN invoices i ON i.id = p.invoice_id
                WHERE i.lane_id = $lane AND i.created_at >= $from AND i.created_at < $to
                  AND i.voided_at IS NULL AND i.hold_token IS NULL
                GROUP BY p.invoice_id, p.tender_type;
                """;
            Bind(command, window);

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var id = reader.GetInt64(0);

                if (!payments.TryGetValue(id, out var list))
                    payments[id] = list = [];

                list.Add(((TenderType)reader.GetInt32(1), reader.GetInt64(2)));
            }
        }

        var vouchers = new List<DayBookVoucher>(bills.Count);

        foreach (var bill in bills)
        {
            var voucher = new VoucherBuilder();
            var rated = lines.GetValueOrDefault(bill.Id, []);

            // What came in, by how it was paid. Cash is what was handed over less the change.
            foreach (var (type, amount) in payments.GetValueOrDefault(bill.Id, []))
            {
                var paid = type == TenderType.Cash ? amount - bill.Change : amount;
                var (ledger, group) = TenderLedger(type, bill.Customer, ledgers);
                voucher.Debit(ledger, group, paid);
            }

            // What it was for: each rate's goods, and the tax on them.
            foreach (var line in rated)
            {
                var sales = bill.Supply ? ledgers.SalesBillOfSupply : ledgers.SalesAt(line.Rate, line.InterState);
                voucher.Credit(sales, LedgerGroup.SalesAccounts, line.Total - line.Tax);
                voucher.Credit(ledgers.OutputCgst, LedgerGroup.DutiesAndTaxes, line.Cgst);
                voucher.Credit(ledgers.OutputSgst, LedgerGroup.DutiesAndTaxes, line.Sgst);
                voucher.Credit(ledgers.OutputIgst, LedgerGroup.DutiesAndTaxes, line.Igst);
            }

            voucher.RoundOff(ledgers.RoundOff, bill.RoundOff, roundingUpIsCredit: true);

            if (voucher.Balance(ledgers.RoundOff) is { } difference)
                notes.Add($"Bill {bill.No} did not balance by {Money(difference)}; the difference is on {ledgers.RoundOff}.");

            vouchers.Add(new DayBookVoucher(
                VoucherKind.Sales,
                bill.No,
                bill.Date,
                bill.Customer,
                bill.Customer is { } who ? $"Bill {bill.No} to {who}" : $"Bill {bill.No}",
                voucher.Entries()));
        }

        return vouchers;
    }

    private static List<DayBookVoucher> CreditNotes(SqliteConnection connection, Window window, DayBookLedgers ledgers)
    {
        var notes = new List<(long Id, string No, DateOnly Date, string InvoiceNo, bool Supply, TenderType Refund, long RoundOff, string Reason, string? Customer)>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT n.id, n.credit_note_no, n.created_at, n.invoice_no, n.tax_mode, n.refund_tender,
                       {PaiseSql.Of("n.round_off")}, n.reason, c.name, c.mobile_no
                FROM credit_notes n
                LEFT JOIN customers c ON c.id = n.customer_id
                WHERE n.lane_id = $lane AND n.created_at >= $from AND n.created_at < $to
                ORDER BY n.created_at, n.id;
                """;
            Bind(command, window);

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                notes.Add((
                    reader.GetInt64(0),
                    reader.GetString(1),
                    Day(reader.GetDateTimeOffset(2)),
                    reader.GetString(3),
                    reader.GetString(4) == nameof(TaxMode.Composition),
                    (TenderType)reader.GetInt32(5),
                    reader.GetInt64(6),
                    reader.GetString(7),
                    Customer(reader, 8)));
            }
        }

        if (notes.Count == 0)
            return [];

        var lines = ReadRated(connection, window, """
            SELECT l.credit_note_id, CAST(l.gst_rate AS REAL), l.is_inter_state,
                   {0}, {1}, {2}, {3}
            FROM credit_note_lines l
            JOIN credit_notes n ON n.id = l.credit_note_id
            WHERE n.lane_id = $lane AND n.created_at >= $from AND n.created_at < $to
            GROUP BY l.credit_note_id, CAST(l.gst_rate AS REAL), l.is_inter_state;
            """, "l.line_total", "l.cgst_amount", "l.sgst_amount", "l.igst_amount");

        var vouchers = new List<DayBookVoucher>(notes.Count);

        foreach (var note in notes)
        {
            var voucher = new VoucherBuilder();
            var rated = lines.GetValueOrDefault(note.Id, []);

            // The bill's entries turned round: the goods and their tax come back off the sales.
            foreach (var line in rated)
            {
                var sales = note.Supply ? ledgers.SalesBillOfSupply : ledgers.SalesAt(line.Rate, line.InterState);
                voucher.Debit(sales, LedgerGroup.SalesAccounts, line.Total - line.Tax);
                voucher.Debit(ledgers.OutputCgst, LedgerGroup.DutiesAndTaxes, line.Cgst);
                voucher.Debit(ledgers.OutputSgst, LedgerGroup.DutiesAndTaxes, line.Sgst);
                voucher.Debit(ledgers.OutputIgst, LedgerGroup.DutiesAndTaxes, line.Igst);
            }

            var refunded = rated.Sum(l => l.Total) + note.RoundOff;
            var (ledger, group) = TenderLedger(note.Refund, note.Customer, ledgers);
            voucher.Credit(ledger, group, refunded);

            // A refund rounded up pays out more than the lines: that is a cost, so a debit.
            voucher.RoundOff(ledgers.RoundOff, note.RoundOff, roundingUpIsCredit: false);

            vouchers.Add(new DayBookVoucher(
                VoucherKind.CreditNote,
                note.No,
                note.Date,
                note.Customer,
                $"Goods back against {note.InvoiceNo}: {note.Reason}",
                voucher.Entries()));
        }

        return vouchers;
    }

    private static List<DayBookVoucher> KhataReceipts(SqliteConnection connection, Window window, DayBookLedgers ledgers)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT p.id, p.received_at, p.tender_type, {PaiseSql.Of("p.amount")}, c.name, c.mobile_no
            FROM credit_payments p
            LEFT JOIN customers c ON c.id = p.customer_id
            WHERE p.lane_id = $lane AND p.received_at >= $from AND p.received_at < $to
            ORDER BY p.received_at, p.id;
            """;
        Bind(command, window);

        var vouchers = new List<DayBookVoucher>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var customer = Customer(reader, 4);
            var amount = reader.GetInt64(3);
            var (paidInto, group) = TenderLedger((TenderType)reader.GetInt32(2), customer, ledgers);

            var voucher = new VoucherBuilder();
            voucher.Debit(paidInto, group, amount);
            voucher.Credit(customer ?? ledgers.KhataCustomers, LedgerGroup.SundryDebtors, amount);

            vouchers.Add(new DayBookVoucher(
                VoucherKind.Receipt,
                $"KR-{reader.GetInt64(0)}",
                Day(reader.GetDateTimeOffset(1)),
                customer,
                $"Paid back on the khata{(customer is null ? string.Empty : $" by {customer}")}",
                voucher.Entries()));
        }

        return vouchers;
    }

    // ---- The supplier's side ---------------------------------------------------------------------

    private static List<DayBookVoucher> Purchases(SqliteConnection connection, Window window, DayBookLedgers ledgers, List<string> notes)
    {
        var bills = new List<(long Id, string No, DateOnly Date, string Supplier, bool InterState, bool ChargesGst, long Total)>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT p.id, p.bill_no, p.bill_date, s.name, p.is_inter_state, p.charges_gst, {PaiseSql.Of("p.total")}
                FROM purchases p
                JOIN suppliers s ON s.id = p.supplier_id
                WHERE p.voided_at IS NULL AND p.bill_date >= $fromDay AND p.bill_date < $toDay
                ORDER BY p.bill_date, p.id;
                """;
            Bind(command, window);

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                bills.Add((
                    reader.GetInt64(0),
                    reader.GetString(1),
                    DateOnly.ParseExact(reader.GetString(2)[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    reader.GetString(3),
                    reader.GetInt32(4) != 0,
                    reader.GetInt32(5) != 0,
                    reader.GetInt64(6)));
            }
        }

        if (bills.Count == 0)
            return [];

        var lines = ReadRated(connection, window, """
            SELECT l.purchase_id, CAST(l.gst_rate AS REAL), p.is_inter_state,
                   {0}, {1}, {2}, {3}
            FROM purchase_lines l
            JOIN purchases p ON p.id = l.purchase_id
            WHERE p.voided_at IS NULL AND p.bill_date >= $fromDay AND p.bill_date < $toDay
            GROUP BY l.purchase_id, CAST(l.gst_rate AS REAL);
            """, "l.line_total", "l.cgst_amount", "l.sgst_amount", "l.igst_amount");

        var vouchers = new List<DayBookVoucher>(bills.Count);

        foreach (var bill in bills)
        {
            var voucher = new VoucherBuilder();
            var rated = lines.GetValueOrDefault(bill.Id, []);

            foreach (var line in rated)
            {
                var purchases = bill.ChargesGst ? ledgers.PurchasesAt(line.Rate, bill.InterState) : ledgers.PurchasesNoGst;
                voucher.Debit(purchases, LedgerGroup.PurchaseAccounts, line.Total - line.Tax);
                voucher.Debit(ledgers.InputCgst, LedgerGroup.DutiesAndTaxes, line.Cgst);
                voucher.Debit(ledgers.InputSgst, LedgerGroup.DutiesAndTaxes, line.Sgst);
                voucher.Debit(ledgers.InputIgst, LedgerGroup.DutiesAndTaxes, line.Igst);
            }

            voucher.Credit(bill.Supplier, LedgerGroup.SundryCreditors, bill.Total);

            // The round-off the supplier printed: what the bill says less what its lines come to. A
            // bill rounded up costs the shop more, so a debit.
            voucher.RoundOff(ledgers.RoundOff, bill.Total - rated.Sum(l => l.Total), roundingUpIsCredit: false);

            if (voucher.Balance(ledgers.RoundOff) is { } difference)
                notes.Add($"{bill.Supplier}'s bill {bill.No} did not balance by {Money(difference)}; the difference is on {ledgers.RoundOff}.");

            vouchers.Add(new DayBookVoucher(
                VoucherKind.Purchase,
                bill.No,
                bill.Date,
                bill.Supplier,
                $"{bill.Supplier}'s bill {bill.No}",
                voucher.Entries()));
        }

        return vouchers;
    }

    private static List<DayBookVoucher> SupplierPayments(SqliteConnection connection, Window window, DayBookLedgers ledgers)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT sp.id, sp.paid_at, s.name, sp.method, {PaiseSql.Of("sp.amount")}, sp.reference
            FROM supplier_payments sp
            JOIN suppliers s ON s.id = sp.supplier_id
            WHERE sp.paid_at >= $from AND sp.paid_at < $to
            ORDER BY sp.paid_at, sp.id;
            """;
        Bind(command, window);

        var vouchers = new List<DayBookVoucher>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var supplier = reader.GetString(2);
            var amount = reader.GetInt64(4);
            var reference = reader.IsDBNull(5) ? null : reader.GetString(5);

            // Cash from the till or from the owner's pocket is cash; UPI, a transfer or a cheque comes
            // out of the bank.
            var cash = Enum.TryParse<SupplierPaymentMethod>(reader.GetString(3), out var method)
                       && method is SupplierPaymentMethod.DrawerCash or SupplierPaymentMethod.OtherCash;

            var voucher = new VoucherBuilder();
            voucher.Debit(supplier, LedgerGroup.SundryCreditors, amount);
            voucher.Credit(cash ? ledgers.Cash : ledgers.Bank, cash ? LedgerGroup.CashInHand : LedgerGroup.BankAccounts, amount);

            vouchers.Add(new DayBookVoucher(
                VoucherKind.Payment,
                $"SP-{reader.GetInt64(0)}",
                Day(reader.GetDateTimeOffset(1)),
                supplier,
                $"Paid to {supplier}{(reference is { Length: > 0 } ? $", {reference}" : string.Empty)}",
                voucher.Entries()));
        }

        return vouchers;
    }

    private static List<DayBookVoucher> DebitNotes(SqliteConnection connection, Window window, DayBookLedgers ledgers)
    {
        var notes = new List<(long Id, string No, DateOnly Date, string Supplier, string BillNo, string Reason, bool InterState, bool ChargesGst)>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT r.id, r.number, r.returned_at, s.name, p.bill_no, r.reason, p.is_inter_state, p.charges_gst
                FROM supplier_returns r
                JOIN suppliers s ON s.id = r.supplier_id
                JOIN purchases p ON p.id = r.purchase_id
                WHERE r.returned_at >= $from AND r.returned_at < $to
                ORDER BY r.returned_at, r.id;
                """;
            Bind(command, window);

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                notes.Add((
                    reader.GetInt64(0),
                    reader.GetString(1),
                    Day(reader.GetDateTimeOffset(2)),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetInt32(6) != 0,
                    reader.GetInt32(7) != 0));
            }
        }

        if (notes.Count == 0)
            return [];

        // Each line's rate is on the bill line it came back from.
        var lines = ReadRated(connection, window, """
            SELECT rl.return_id, CAST(pl.gst_rate AS REAL), p.is_inter_state,
                   {0}, {1}, {2}, {3}
            FROM supplier_return_lines rl
            JOIN supplier_returns r ON r.id = rl.return_id
            JOIN purchases p ON p.id = r.purchase_id
            JOIN purchase_lines pl ON pl.purchase_id = r.purchase_id AND pl.line_no = rl.purchase_line_no
            WHERE r.returned_at >= $from AND r.returned_at < $to
            GROUP BY rl.return_id, CAST(pl.gst_rate AS REAL);
            """, "rl.line_total", "rl.cgst_amount", "rl.sgst_amount", "rl.igst_amount");

        var vouchers = new List<DayBookVoucher>(notes.Count);

        foreach (var note in notes)
        {
            var voucher = new VoucherBuilder();
            var rated = lines.GetValueOrDefault(note.Id, []);

            voucher.Debit(note.Supplier, LedgerGroup.SundryCreditors, rated.Sum(l => l.Total));

            foreach (var line in rated)
            {
                var purchases = note.ChargesGst ? ledgers.PurchasesAt(line.Rate, note.InterState) : ledgers.PurchasesNoGst;
                voucher.Credit(purchases, LedgerGroup.PurchaseAccounts, line.Total - line.Tax);
                voucher.Credit(ledgers.InputCgst, LedgerGroup.DutiesAndTaxes, line.Cgst);
                voucher.Credit(ledgers.InputSgst, LedgerGroup.DutiesAndTaxes, line.Sgst);
                voucher.Credit(ledgers.InputIgst, LedgerGroup.DutiesAndTaxes, line.Igst);
            }

            vouchers.Add(new DayBookVoucher(
                VoucherKind.DebitNote,
                note.No,
                note.Date,
                note.Supplier,
                $"Goods sent back to {note.Supplier}, from bill {note.BillNo}: {note.Reason}",
                voucher.Entries()));
        }

        return vouchers;
    }

    // ---- Spending and the drawer ------------------------------------------------------------------

    private static List<DayBookVoucher> Expenses(SqliteConnection connection, Window window, DayBookLedgers ledgers)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, spent_at, category, {PaiseSql.Of("amount")}, paid_from, note
            FROM expenses
            WHERE lane_id = $lane AND spent_at >= $from AND spent_at < $to
            ORDER BY spent_at, id;
            """;
        Bind(command, window);

        var vouchers = new List<DayBookVoucher>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var category = reader.GetString(2);
            var amount = reader.GetInt64(3);
            var fromDrawer = reader.GetString(4) == nameof(ExpensePaidFrom.Drawer);
            var note = reader.IsDBNull(5) ? null : reader.GetString(5);

            var voucher = new VoucherBuilder();
            voucher.Debit(category, LedgerGroup.IndirectExpenses, amount);
            voucher.Credit(
                fromDrawer ? ledgers.Cash : ledgers.ExpensesPaidOutside,
                fromDrawer ? LedgerGroup.CashInHand : LedgerGroup.BankAccounts,
                amount);

            vouchers.Add(new DayBookVoucher(
                VoucherKind.Payment,
                $"EX-{reader.GetInt64(0)}",
                Day(reader.GetDateTimeOffset(1)),
                null,
                note is { Length: > 0 } ? $"{category}: {note}" : category,
                voucher.Entries()));
        }

        return vouchers;
    }

    private static List<DayBookVoucher> CashMoved(SqliteConnection connection, Window window, DayBookLedgers ledgers)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, moved_at, kind, {PaiseSql.Of("amount")}, note
            FROM cash_movements
            WHERE lane_id = $lane AND moved_at >= $from AND moved_at < $to
              AND kind IN ('{DrawerKinds.CashIn}', '{DrawerKinds.CashOut}')
            ORDER BY moved_at, id;
            """;
        Bind(command, window);

        var vouchers = new List<DayBookVoucher>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var takenOut = reader.GetString(2) == DrawerKinds.CashOut;
            var amount = Math.Abs(reader.GetInt64(3));
            var note = reader.IsDBNull(4) ? null : reader.GetString(4);

            var voucher = new VoucherBuilder();

            if (takenOut)
            {
                voucher.Debit(ledgers.CashTakenOut, LedgerGroup.Suspense, amount);
                voucher.Credit(ledgers.Cash, LedgerGroup.CashInHand, amount);
            }
            else
            {
                voucher.Debit(ledgers.Cash, LedgerGroup.CashInHand, amount);
                voucher.Credit(ledgers.CashPutIn, LedgerGroup.Suspense, amount);
            }

            var what = takenOut ? "Cash taken out of the till" : "Cash put into the till";

            vouchers.Add(new DayBookVoucher(
                VoucherKind.Contra,
                $"CM-{reader.GetInt64(0)}",
                Day(reader.GetDateTimeOffset(1)),
                null,
                note is { Length: > 0 } ? $"{what}: {note}" : what,
                voucher.Entries()));
        }

        return vouchers;
    }

    // ---- Shared ---------------------------------------------------------------------------------

    /// <summary>
    /// Lines grouped by document and rate, in paise. The SQL has four placeholders for the four
    /// summed columns: the line total, then CGST, SGST and IGST.
    /// </summary>
    private static Dictionary<long, List<Rated>> ReadRated(SqliteConnection connection, Window window, string sql, params string[] columns)
    {
        using var command = connection.CreateCommand();
        command.CommandText = string.Format(CultureInfo.InvariantCulture, sql, [.. columns.Select(PaiseSql.Sum)]);
        Bind(command, window);

        var rated = new Dictionary<long, List<Rated>>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var id = reader.GetInt64(0);

            if (!rated.TryGetValue(id, out var list))
                rated[id] = list = [];

            list.Add(new Rated(
                Math.Round((decimal)reader.GetDouble(1), 2),
                reader.GetInt32(2) != 0,
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6)));
        }

        return rated;
    }

    private static (string Ledger, LedgerGroup Group) TenderLedger(TenderType type, string? customer, DayBookLedgers ledgers) => type switch
    {
        TenderType.Cash => (ledgers.Cash, LedgerGroup.CashInHand),
        TenderType.Card => (ledgers.Card, LedgerGroup.BankAccounts),
        TenderType.Upi => (ledgers.Upi, LedgerGroup.BankAccounts),
        TenderType.StoreCredit => (customer ?? ledgers.KhataCustomers, LedgerGroup.SundryDebtors),
        TenderType.LoyaltyPoints => (ledgers.LoyaltyPoints, LedgerGroup.IndirectExpenses),
        _ => (ledgers.Cash, LedgerGroup.CashInHand),
    };

    /// <summary>A customer as their ledger is named: "Lakshmi (9500012345)", or the number alone.</summary>
    private static string? Customer(SqliteDataReader reader, int nameColumn)
    {
        var name = reader.IsDBNull(nameColumn) ? null : reader.GetString(nameColumn).Trim();
        var mobile = reader.IsDBNull(nameColumn + 1) ? null : reader.GetString(nameColumn + 1).Trim();

        if (string.IsNullOrEmpty(mobile))
            return string.IsNullOrEmpty(name) ? null : name;

        return string.IsNullOrEmpty(name) ? mobile : $"{name} ({mobile})";
    }

    private static void Bind(SqliteCommand command, Window window)
    {
        command.Parameters.AddWithValue("$lane", window.LaneId);
        command.Parameters.AddWithValue("$from", window.From);
        command.Parameters.AddWithValue("$to", window.To);
        command.Parameters.AddWithValue("$fromDay", window.FromDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$toDay", window.ToDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    private static DateOnly Day(DateTimeOffset at) => DateOnly.FromDateTime(at.ToLocalTime().DateTime);

    private static DateTimeOffset LocalMidnight(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private static string Money(long paise) => "Rs " + PaiseSql.Rupees(paise).ToString("N2", CultureInfo.GetCultureInfo("en-IN"));

    /// <summary>A voucher's entries, built in paise, one entry per ledger and side.</summary>
    private sealed class VoucherBuilder
    {
        private readonly List<(string Ledger, LedgerGroup Group, bool Debit, long Paise)> _entries = [];

        public void Debit(string ledger, LedgerGroup group, long paise) => Add(ledger, group, debit: true, paise);

        public void Credit(string ledger, LedgerGroup group, long paise) => Add(ledger, group, debit: false, paise);

        /// <summary>
        /// A round-off as the document has it: rounding up is a credit on a bill (the customer paid
        /// more) and a debit on a refund or a supplier's bill (the shop paid more).
        /// </summary>
        public void RoundOff(string ledger, long paise, bool roundingUpIsCredit)
        {
            if (paise == 0)
                return;

            var credit = paise > 0 == roundingUpIsCredit;
            Add(ledger, LedgerGroup.IndirectExpenses, debit: !credit, Math.Abs(paise));
        }

        /// <summary>Puts anything that does not add up on <paramref name="ledger"/>, and says how much it was.</summary>
        /// <returns>Null when the two sides already agreed.</returns>
        public long? Balance(string ledger)
        {
            var debits = _entries.Where(e => e.Debit).Sum(e => e.Paise);
            var credits = _entries.Where(e => !e.Debit).Sum(e => e.Paise);

            if (debits == credits)
                return null;

            Add(ledger, LedgerGroup.IndirectExpenses, debit: debits < credits, Math.Abs(debits - credits));
            return debits - credits;
        }

        private void Add(string ledger, LedgerGroup group, bool debit, long paise)
        {
            if (paise == 0)
                return;

            // A negative amount on one side is a positive one on the other.
            if (paise < 0)
            {
                debit = !debit;
                paise = -paise;
            }

            _entries.Add((ledger, group, debit, paise));
        }

        /// <summary>One entry per ledger, its debits and credits netted, debits first.</summary>
        public IReadOnlyList<DayBookEntry> Entries() =>
        [
            .. _entries
                .GroupBy(e => e.Ledger, StringComparer.Ordinal)
                .Select(g =>
                {
                    var net = g.Sum(e => e.Debit ? e.Paise : -e.Paise);
                    return (Ledger: g.Key, g.First().Group, Net: net, Order: _entries.FindIndex(e => e.Ledger == g.Key));
                })
                .Where(e => e.Net != 0)
                .OrderBy(e => e.Net > 0 ? 0 : 1)
                .ThenBy(e => e.Order)
                .Select(e => new DayBookEntry(e.Ledger, e.Group, e.Net > 0 ? PaiseSql.Rupees(e.Net) : 0m, e.Net < 0 ? PaiseSql.Rupees(-e.Net) : 0m)),
        ];
    }
}
