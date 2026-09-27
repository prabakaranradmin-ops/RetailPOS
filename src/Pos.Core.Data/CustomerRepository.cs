using Microsoft.Data.Sqlite;
using Pos.Core.Domain;

namespace Pos.Core.Data;

/// <summary>
/// Customer records and their loyalty balances. Looked up by mobile number at the till, which is
/// why that column carries a unique index.
/// </summary>
public sealed class CustomerRepository : ICustomerStore
{
    private const string SelectColumns = "id, mobile_no, name, loyalty_balance, state_code";

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
            WHERE mobile_no LIKE $contains ESCAPE '\' OR name LIKE $contains ESCAPE '\'
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

        // Both tables reference the customer, and foreign keys are enforced, so the links go
        // first. A parked bill loses its customer too: it would otherwise bring them back.
        var unlinked = Run("UPDATE invoices SET customer_id = NULL WHERE customer_id = $id;");
        Run("UPDATE held_bills SET customer_id = NULL WHERE customer_id = $id;");

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
    };
}
