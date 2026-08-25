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

    private static Customer Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        MobileNo = reader.GetString(1),
        Name = reader.IsDBNull(2) ? null : reader.GetString(2),
        LoyaltyBalance = reader.GetInt32(3),
        StateCode = reader.IsDBNull(4) ? null : reader.GetString(4),
    };
}
