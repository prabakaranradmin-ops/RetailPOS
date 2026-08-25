using Microsoft.Data.Sqlite;
using Pos.Core.Data;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// The Phase 0 gate: the local schema is created and migratable.
/// </summary>
public class SchemaTests
{
    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static T? Scalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, typeof(T));
    }

    [Fact]
    public void AFreshDatabaseMigratesToTheLatestVersion()
    {
        using var temp = new TempDatabase(migrate: false);

        using var connection = temp.Database.OpenConnection();
        var applied = Migrator.Migrate(connection);

        Assert.Equal(Migrator.LatestVersion, applied);
        Assert.Equal(Migrator.LatestVersion, Migrator.GetVersion(connection));
    }

    [Fact]
    public void MigratingAnUpToDateDatabaseIsANoOp()
    {
        using var temp = new TempDatabase();

        using var connection = temp.Database.OpenConnection();
        var applied = Migrator.Migrate(connection);

        Assert.Equal(0, applied);
        Assert.Equal(Migrator.LatestVersion, Migrator.GetVersion(connection));
    }

    [Fact]
    public void StartupMigrationCanRunRepeatedlyWithoutDamage()
    {
        using var temp = new TempDatabase(migrate: false);

        temp.Database.EnsureMigrated();
        temp.Database.EnsureMigrated();
        temp.Database.EnsureMigrated();

        using var connection = temp.Database.OpenConnection();
        Assert.Equal(Migrator.LatestVersion, Migrator.GetVersion(connection));
    }

    /// <summary>
    /// An older build must refuse a database a newer build has already upgraded, rather than
    /// running against a schema it does not understand.
    /// </summary>
    [Fact]
    public void RefusesADatabaseFromANewerBuild()
    {
        using var temp = new TempDatabase();

        using var connection = temp.Database.OpenConnection();
        Execute(connection, $"PRAGMA user_version = {Migrator.LatestVersion + 5};");

        var ex = Assert.Throws<InvalidOperationException>(() => Migrator.Migrate(connection));
        Assert.Contains("newer than this build understands", ex.Message);
    }

    [Theory]
    [InlineData("items")]
    [InlineData("customers")]
    [InlineData("invoices")]
    [InlineData("invoice_lines")]
    [InlineData("payments")]
    [InlineData("invoice_sequences")]
    public void EveryBillingTableExists(string table)
    {
        using var temp = new TempDatabase();
        using var connection = temp.Database.OpenConnection();

        var found = Scalar<long>(
            connection,
            $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{table}';");

        Assert.Equal(1L, found);
    }

    [Fact]
    public void SkuIsUnique()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Database.OpenConnection();

        Execute(connection, InsertItem(sku: "A1", barcode: "'111'"));

        Assert.Throws<SqliteException>(() => Execute(connection, InsertItem(sku: "A1", barcode: "'222'")));
    }

    [Fact]
    public void ABarcodeIdentifiesExactlyOneItem()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Database.OpenConnection();

        Execute(connection, InsertItem(sku: "A1", barcode: "'8901234567890'"));

        Assert.Throws<SqliteException>(() =>
            Execute(connection, InsertItem(sku: "A2", barcode: "'8901234567890'")));
    }

    /// <summary>
    /// Loose and weighed goods often have no barcode at all, so the uniqueness rule must not
    /// collapse every unbarcoded item into a single row.
    /// </summary>
    [Fact]
    public void ManyItemsMayHaveNoBarcode()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Database.OpenConnection();

        Execute(connection, InsertItem(sku: "A1", barcode: "NULL"));
        Execute(connection, InsertItem(sku: "A2", barcode: "NULL"));
        Execute(connection, InsertItem(sku: "A3", barcode: "NULL"));

        Assert.Equal(3L, Scalar<long>(connection, "SELECT COUNT(*) FROM items;"));
    }

    [Fact]
    public void InvoiceNumberIsUnique()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Database.OpenConnection();

        Execute(connection, InsertInvoice("L1-2026-000001"));

        Assert.Throws<SqliteException>(() => Execute(connection, InsertInvoice("L1-2026-000001")));
    }

    /// <summary>
    /// Two lanes generating their own sequences must not be able to collide, because the lane id
    /// is part of the number.
    /// </summary>
    [Fact]
    public void SameSequenceOnDifferentLanesDoesNotCollide()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Database.OpenConnection();

        Execute(connection, InsertInvoice("L1-2026-000001", lane: "L1"));
        Execute(connection, InsertInvoice("L2-2026-000001", lane: "L2"));

        Assert.Equal(2L, Scalar<long>(connection, "SELECT COUNT(*) FROM invoices;"));
    }

    [Fact]
    public void DeletingAnInvoiceRemovesItsLinesAndPayments()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Database.OpenConnection();

        Execute(connection, InsertInvoice("L1-2026-000001"));
        Execute(connection, """
            INSERT INTO invoice_lines
              (invoice_id, line_no, item_id, name_snapshot, hsn_snapshot, unit_type, mrp,
               unit_price, is_tax_inclusive, gst_rate, quantity, discount, is_inter_state,
               taxable_value, cgst_amount, sgst_amount, igst_amount, line_total)
            VALUES (1, 1, 1, 'Toor Dal', '0713', 0, '100', '100', 1, '5', '1', '0', 0,
                    '95.2381', '2.38', '2.38', '0', '100.00');
            """);
        Execute(connection, "INSERT INTO payments (invoice_id, tender_type, amount) VALUES (1, 0, '100.00');");

        Execute(connection, "DELETE FROM invoices WHERE id = 1;");

        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM invoice_lines;"));
        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM payments;"));
    }

    /// <summary>
    /// Money is stored as text on purpose. This is the test that catches anyone "tidying" a money
    /// column into REAL: the value must come back bit-for-bit, not approximately.
    /// </summary>
    [Theory]
    [InlineData("0.01")]
    [InlineData("95.2381")]
    [InlineData("1234567.8912")]
    [InlineData("0.3850")]
    [InlineData("99999999.9999")]
    public void DecimalsRoundTripExactly(string value)
    {
        using var temp = new TempDatabase();
        using var connection = temp.Database.OpenConnection();

        var expected = decimal.Parse(value);

        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO items (sku, barcode, hsn_code, name, mrp, sell_price, gst_rate)
                VALUES ('A1', NULL, '0713', 'Toor Dal', $v, $v, '5');
                """;
            insert.Parameters.AddWithValue("$v", expected);
            insert.ExecuteNonQuery();
        }

        using var read = connection.CreateCommand();
        read.CommandText = "SELECT mrp FROM items WHERE sku = 'A1';";
        using var reader = read.ExecuteReader();
        Assert.True(reader.Read());

        Assert.Equal(expected, reader.GetDecimal(0));
    }

    [Fact]
    public void ALaneOwnsOneSequencePerYear()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Database.OpenConnection();

        Execute(connection, "INSERT INTO invoice_sequences (lane_id, year, next_value) VALUES ('L1', 2026, 1);");
        Execute(connection, "INSERT INTO invoice_sequences (lane_id, year, next_value) VALUES ('L1', 2027, 1);");
        Execute(connection, "INSERT INTO invoice_sequences (lane_id, year, next_value) VALUES ('L2', 2026, 1);");

        Assert.Throws<SqliteException>(() =>
            Execute(connection, "INSERT INTO invoice_sequences (lane_id, year, next_value) VALUES ('L1', 2026, 9);"));
    }

    private static string InsertItem(string sku, string barcode) => $"""
        INSERT INTO items (sku, barcode, hsn_code, name, mrp, sell_price, gst_rate)
        VALUES ('{sku}', {barcode}, '0713', 'Toor Dal', '100', '100', '5');
        """;

    private static string InsertInvoice(string invoiceNo, string lane = "L1") => $"""
        INSERT INTO invoices
          (invoice_no, lane_id, created_at, status, subtotal_taxable, total_discount,
           total_cgst, total_sgst, total_igst, grand_total)
        VALUES ('{invoiceNo}', '{lane}', '2026-08-25T10:00:00', 2, '95.2381', '0',
                '2.38', '2.38', '0', '100.00');
        """;
}
