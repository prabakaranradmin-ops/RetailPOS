using Microsoft.Data.Sqlite;
using Pos.Core.Domain;

namespace Pos.Core.Data;

/// <summary>
/// Customer records and their loyalty balances. Looked up by mobile number at the till, which is
/// why that column carries a unique index.
/// </summary>
public sealed class CustomerRepository : ICustomerStore
{
    private const string SelectColumns = "id, mobile_no, name, loyalty_balance, state_code, gstin, address";

    private readonly PosDatabase _database;

    public CustomerRepository(PosDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    public Customer? FindByMobile(string mobileNo)
    {
        if (string.IsNullOrWhiteSpace(mobileNo))
            return null;

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM customers WHERE mobile_no = $mobile;";
        command.Parameters.AddWithValue("$mobile", mobileNo.Trim());

        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public Customer Add(Customer customer)
    {
        ArgumentNullException.ThrowIfNull(customer);
        ArgumentException.ThrowIfNullOrWhiteSpace(customer.MobileNo);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO customers (mobile_no, name, loyalty_balance, state_code)
            VALUES ($mobile, $name, $balance, $stateCode);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$mobile", customer.MobileNo.Trim());
        command.Parameters.AddWithValue("$name", (object?)customer.Name ?? DBNull.Value);
        command.Parameters.AddWithValue("$balance", customer.LoyaltyBalance);
        command.Parameters.AddWithValue("$stateCode", (object?)customer.StateCode ?? DBNull.Value);

        var id = Convert.ToInt64(command.ExecuteScalar());

        return new Customer
        {
            Id = id,
            MobileNo = customer.MobileNo.Trim(),
            Name = customer.Name,
            LoyaltyBalance = customer.LoyaltyBalance,
            StateCode = customer.StateCode,
        };
    }

    public void UpdateLoyaltyBalance(long customerId, int balance)
    {
        if (balance < 0)
            throw new ArgumentOutOfRangeException(nameof(balance), balance, "A loyalty balance cannot go negative.");

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE customers SET loyalty_balance = $balance WHERE id = $id;";
        command.Parameters.AddWithValue("$balance", balance);
        command.Parameters.AddWithValue("$id", customerId);

        if (command.ExecuteNonQuery() == 0)
            throw new InvalidOperationException($"No customer with id {customerId}.");
    }

    public IReadOnlyList<Customer> Search(string text, int limit = 8)
    {
        var term = text?.Trim() ?? string.Empty;

        if (term.Length == 0 || limit < 1)
            return [];

        // LIKE with the wildcards escaped: a name containing % or _ is data, not a pattern.
        var escaped = term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM customers
            WHERE mobile_no LIKE $contains ESCAPE '\' OR name LIKE $contains ESCAPE '\' OR gstin LIKE $contains ESCAPE '\'
            ORDER BY
                CASE
                    WHEN mobile_no LIKE $starts ESCAPE '\' THEN 0
                    WHEN name LIKE $starts ESCAPE '\' THEN 1
                    ELSE 2
                END,
                name IS NULL, name COLLATE NOCASE, mobile_no
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$contains", $"%{escaped}%");
        command.Parameters.AddWithValue("$starts", $"{escaped}%");
        command.Parameters.AddWithValue("$limit", limit);

        var found = new List<Customer>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
            found.Add(Map(reader));

        return found;
    }

    public Customer SetBusiness(long customerId, string? gstin, string? address)
    {
        var number = string.IsNullOrWhiteSpace(gstin) ? null : Gstin.Normalise(gstin);

        if (number is not null && Gstin.Problem(number) is { } problem)
            throw new ArgumentException(problem, nameof(gstin));

        var place = string.IsNullOrWhiteSpace(address) ? null : address.Trim();

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);

        if (number is not null)
        {
            using var taken = connection.CreateCommand();
            taken.Transaction = transaction;
            taken.CommandText = "SELECT COALESCE(name, mobile_no) FROM customers WHERE gstin = $gstin AND id <> $id;";
            taken.Parameters.AddWithValue("$gstin", number);
            taken.Parameters.AddWithValue("$id", customerId);

            if (taken.ExecuteScalar() is string other)
                throw new InvalidOperationException($"GSTIN {number} is already {other}'s.");
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;

            // The GSTIN says which state they are in, and so how their bills are taxed. Taking it off
            // leaves the state as it was: they are still wherever they were.
            command.CommandText = number is null
                ? "UPDATE customers SET gstin = NULL, address = $address WHERE id = $id;"
                : "UPDATE customers SET gstin = $gstin, address = $address, state_code = $state WHERE id = $id;";
            command.Parameters.AddWithValue("$gstin", (object?)number ?? DBNull.Value);
            command.Parameters.AddWithValue("$address", (object?)place ?? DBNull.Value);
            command.Parameters.AddWithValue("$state", number is null ? DBNull.Value : Gstin.StateCode(number));
            command.Parameters.AddWithValue("$id", customerId);

            if (command.ExecuteNonQuery() == 0)
                throw new InvalidOperationException($"No customer with id {customerId}.");
        }

        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = $"SELECT {SelectColumns} FROM customers WHERE id = $id;";
            read.Parameters.AddWithValue("$id", customerId);

            using var reader = read.ExecuteReader();
            reader.Read();
            var customer = Map(reader);
            reader.Close();

            transaction.Commit();
            return customer;
        }
    }

    public Customer? FindByGstin(string gstin)
    {
        if (string.IsNullOrWhiteSpace(gstin))
            return null;

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM customers WHERE gstin = $gstin;";
        command.Parameters.AddWithValue("$gstin", Gstin.Normalise(gstin));

        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public void Rename(long customerId, string? name)
    {
        var tidy = string.IsNullOrWhiteSpace(name) ? null : name.Trim();

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE customers SET name = $name WHERE id = $id;";
        command.Parameters.AddWithValue("$name", (object?)tidy ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", customerId);

        if (command.ExecuteNonQuery() == 0)
            throw new InvalidOperationException($"No customer with id {customerId}.");
    }

    public int Forget(long customerId)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        int Run(string sql)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", customerId);
            return command.ExecuteNonQuery();
        }

        // A customer who still owes the shop money is not forgotten. Deleting them would delete the
        // only record of who the debt belongs to, and the shop has a plain, lawful reason to keep it
        // until it is settled. Checked in the same transaction as the delete.
        var owed = CreditRepository.OwedPaise(connection, transaction, customerId);

        if (owed != 0)
        {
            transaction.Rollback();

            throw new InvalidOperationException(owed > 0
                ? $"They still owe {PaiseSql.Rupees(owed).ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("en-IN"))} on the khata. Take the payment first, then forget them."
                : $"The shop owes them {PaiseSql.Rupees(-owed):0.00}. Settle that first, then forget them.");
        }

        // Every table that references the customer, and foreign keys are enforced, so the links go
        // first. A parked bill loses its customer too: it would otherwise bring them back. Their
        // repayments stay - the money was received and its day has to reconcile - but anonymous.
        var unlinked = Run("UPDATE invoices SET customer_id = NULL WHERE customer_id = $id;");
        Run("UPDATE held_bills SET customer_id = NULL WHERE customer_id = $id;");
        Run("UPDATE credit_payments SET customer_id = NULL WHERE customer_id = $id;");
        Run("UPDATE credit_notes SET customer_id = NULL WHERE customer_id = $id;");

        if (Run("DELETE FROM customers WHERE id = $id;") == 0)
        {
            transaction.Rollback();
            return -1;
        }

        transaction.Commit();
        return unlinked;
    }

    private static Customer Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        MobileNo = reader.GetString(1),
        Name = reader.IsDBNull(2) ? null : reader.GetString(2),
        LoyaltyBalance = reader.GetInt32(3),
        StateCode = reader.IsDBNull(4) ? null : reader.GetString(4),
        Gstin = reader.FieldCount > 5 && !reader.IsDBNull(5) ? reader.GetString(5) : null,
        Address = reader.FieldCount > 6 && !reader.IsDBNull(6) ? reader.GetString(6) : null,
    };
}
