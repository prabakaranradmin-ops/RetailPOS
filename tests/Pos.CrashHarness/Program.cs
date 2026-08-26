using Pos.Core.Data;
using Pos.Core.Domain;

// A till that dies mid-sale, on demand.
//
// Test infrastructure, never shipped. Durability is a property of what survives a process ceasing
// to exist, and that cannot be tested inside the process that has to survive it: disposing a
// connection runs the rollback path in an orderly way, which is precisely the case that was never
// in doubt. So the tests launch this, let it get partway through a sale, and kill it the way a
// power cut would.
//
//   Pos.CrashHarness <database-path> <mode>
//
// Modes:
//   commit-then-die       settle a sale, commit, then stop existing without unwinding anything
//   die-mid-transaction   open a transaction, write the invoice, stop before committing
//   die-while-writing     start writing lines and stop partway through the batch

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: Pos.CrashHarness <database-path> <mode>");
    return 2;
}

var databasePath = args[0];
var mode = args[1].ToLowerInvariant();

var database = new PosDatabase(databasePath);
database.EnsureMigrated();

var invoices = new InvoiceRepository(database);
var sale = BuildSale("L1");

switch (mode)
{
    case "commit-then-die":
    {
        var saved = invoices.Save(sale);
        Console.Out.WriteLine(saved.InvoiceNo);
        Console.Out.Flush();

        // Straight out, with no unwinding: no finalizers, no flush, no orderly close. Whatever is
        // on disk at this instant is what a power cut would have left.
        Die();
        return 0;
    }

    case "die-mid-transaction":
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);

        // Take a number and write the header, then stop before the commit that would make it real.
        using (var seed = connection.CreateCommand())
        {
            seed.Transaction = transaction;
            seed.CommandText = "INSERT INTO invoice_sequences (lane_id, fiscal_year_start, next_value) VALUES ('L1', 2026, 2) ON CONFLICT (lane_id, fiscal_year_start) DO UPDATE SET next_value = next_value + 1;";
            seed.ExecuteNonQuery();
        }

        using (var header = connection.CreateCommand())
        {
            header.Transaction = transaction;
            header.CommandText = """
                INSERT INTO invoices
                  (invoice_no, lane_id, created_at, status, subtotal_taxable, total_discount,
                   total_cgst, total_sgst, total_igst, grand_total)
                VALUES ('L1-2026-999999', 'L1', '2026-08-26T10:00:00+05:30', 2, '100', '0', '0', '0', '0', '100');
                """;
            header.ExecuteNonQuery();
        }

        Console.Out.WriteLine("uncommitted");
        Console.Out.Flush();
        Die();
        return 0;
    }

    case "die-while-writing":
    {
        // A long batch, abandoned halfway. The header is in, some lines are in, and the commit
        // never comes.
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);

        using (var header = connection.CreateCommand())
        {
            header.Transaction = transaction;
            header.CommandText = """
                INSERT INTO invoices
                  (invoice_no, lane_id, created_at, status, subtotal_taxable, total_discount,
                   total_cgst, total_sgst, total_igst, grand_total)
                VALUES ('L1-2026-888888', 'L1', '2026-08-26T10:00:00+05:30', 2, '100', '0', '0', '0', '0', '100');
                SELECT last_insert_rowid();
                """;
            var invoiceId = Convert.ToInt64(header.ExecuteScalar());

            for (var i = 1; i <= 20; i++)
            {
                using var line = connection.CreateCommand();
                line.Transaction = transaction;
                line.CommandText = $"""
                    INSERT INTO invoice_lines
                      (invoice_id, line_no, item_id, name_snapshot, hsn_snapshot, unit_type, mrp,
                       unit_price, is_tax_inclusive, gst_rate, quantity, discount, is_inter_state,
                       taxable_value, cgst_amount, sgst_amount, igst_amount, line_total)
                    VALUES ({invoiceId}, {i}, 1, 'Item {i}', '0713', 0, '100', '100', 1, '5', '1', '0', 0,
                            '95.2381', '2.38', '2.38', '0', '100.00');
                    """;
                line.ExecuteNonQuery();
            }
        }

        Console.Out.WriteLine("partial");
        Console.Out.Flush();
        Die();
        return 0;
    }

    default:
        Console.Error.WriteLine($"unknown mode '{mode}'");
        return 2;
}

// Ends the process immediately, without running finalizers, without flushing anything the runtime
// is holding, and without giving SQLite a chance to close cleanly.
static void Die() => Environment.FailFast(null);

static SaleDraft BuildSale(string laneId)
{
    InvoiceLine[] lines =
    [
        InvoiceLine.Rehydrate(1, "Toor Dal 1kg", "0713", "8901234567890", null, UnitType.Each, 189m, 189m, true, 5m, 1m, 0m, false),
        InvoiceLine.Rehydrate(2, "Basmati Rice 5kg", "1006", "8901234567891", null, UnitType.Each, 649m, 649m, true, 5m, 1m, 0m, false),
    ];

    var totals = InvoiceTotals.From(lines);

    return new SaleDraft(
        laneId,
        new DateTimeOffset(2026, 8, 26, 10, 0, 0, TimeSpan.FromHours(5.5)),
        Customer: null,
        lines,
        totals,
        [new Tender(TenderType.Cash, totals.GrandTotal)],
        ChangeDue: 0m,
        PointsRedeemed: 0,
        PointsEarned: 0,
        RecalledFromToken: null);
}
