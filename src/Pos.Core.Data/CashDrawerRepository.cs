using System.Globalization;
using Microsoft.Data.Sqlite;
using Pos.Core.Domain;

namespace Pos.Core.Data;

/// <summary>
/// Cash put into and taken out of the drawer, and what the shop spends.
/// </summary>
/// <remarks>
/// <para>
/// Anything that moves cash is a row in <c>cash_movements</c>, which is how the day-end report
/// works out what the drawer should hold. An expense is also a row in <c>expenses</c>, which is how
/// the owner's figures count spending - including what was paid from the bank and never touched the
/// drawer at all. One paid from the drawer is both, linked, written in one transaction.
/// </para>
/// </remarks>
public sealed class CashDrawerRepository(PosDatabase database) : ICashDrawerStore
{
    private readonly PosDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

    public DrawerEntryRecorded Record(DrawerEntry entry, string laneId, DateTimeOffset at, string? cashierName)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(laneId);

        if (entry.Problem() is { } problem)
            throw new ArgumentException(problem, nameof(entry));

        var note = string.IsNullOrWhiteSpace(entry.Note) ? null : entry.Note.Trim();
        var category = entry.Kind.IsExpense() ? entry.Category!.Trim() : null;

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);

        long? movementId = null;

        if (entry.Kind.MovesCash())
        {
            var kind = entry.Kind switch
            {
                DrawerEntryKind.OpeningFloat => DrawerKinds.Float,
                DrawerEntryKind.ExpenseFromDrawer => DrawerKinds.Expense,
                DrawerEntryKind.CashIn => DrawerKinds.CashIn,
                _ => DrawerKinds.CashOut,
            };

            CashMovements.Insert(connection, transaction, laneId, at, kind, entry.DrawerChange,
                category is null ? note : note is null ? category : $"{category}: {note}", reference: null, cashierName);

            using var id = connection.CreateCommand();
            id.Transaction = transaction;
            id.CommandText = "SELECT last_insert_rowid();";
            movementId = Convert.ToInt64(id.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        Expense? expense = null;

        if (entry.Kind.IsExpense())
        {
            var paidFrom = entry.Kind == DrawerEntryKind.ExpenseFromDrawer ? ExpensePaidFrom.Drawer : ExpensePaidFrom.Outside;

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO expenses (lane_id, spent_at, category, amount, paid_from, note, cashier_name, cash_movement_id)
                VALUES ($lane, $at, $category, $amount, $from, $note, $cashier, $movement);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("$lane", laneId);
            insert.Parameters.AddWithValue("$at", at);
            insert.Parameters.AddWithValue("$category", category!);
            insert.Parameters.AddWithValue("$amount", entry.Amount);
            insert.Parameters.AddWithValue("$from", paidFrom.ToString());
            insert.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
            insert.Parameters.AddWithValue("$cashier", (object?)cashierName ?? DBNull.Value);
            insert.Parameters.AddWithValue("$movement", (object?)movementId ?? DBNull.Value);

            var id = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
            expense = new Expense(id, laneId, at, category!, entry.Amount, paidFrom, note, cashierName);
        }

        var floatNow = PaiseSql.Rupees(FloatPaise(connection, transaction, laneId));

        transaction.Commit();

        return new DrawerEntryRecorded(entry, floatNow, expense);
    }

    public decimal FloatSinceLastClose(string laneId)
    {
        using var connection = _database.OpenConnection();
        return PaiseSql.Rupees(FloatPaise(connection, null, laneId));
    }

    private static long FloatPaise(SqliteConnection connection, SqliteTransaction? transaction, string laneId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT COALESCE({PaiseSql.Sum("amount")}, 0) FROM cash_movements
            WHERE lane_id = $lane AND day_close_id IS NULL AND kind = $kind;
            """;
        command.Parameters.AddWithValue("$lane", laneId);
        command.Parameters.AddWithValue("$kind", DrawerKinds.Float);

        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public IReadOnlyList<Expense> Expenses(DateTimeOffset from, DateTimeOffset to, int limit = 200)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, lane_id, spent_at, category, amount, paid_from, note, cashier_name
            FROM expenses
            WHERE spent_at >= $from AND spent_at < $to
            ORDER BY spent_at DESC, id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 10_000));

        var expenses = new List<Expense>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            expenses.Add(new Expense(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetDateTimeOffset(2),
                reader.GetString(3),
                reader.GetDecimal(4),
                Enum.TryParse<ExpensePaidFrom>(reader.GetString(5), out var paidFrom) ? paidFrom : ExpensePaidFrom.Outside,
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return expenses;
    }

    public IReadOnlyList<ExpenseTotal> ExpenseTotals(DateTimeOffset from, DateTimeOffset to)
    {
        using var connection = _database.OpenConnection();
        return ReadTotals(connection, from, to);
    }

    /// <summary>Shared with the owner's figures, which read it inside their own connection.</summary>
    public static List<ExpenseTotal> ReadTotals(SqliteConnection connection, DateTimeOffset from, DateTimeOffset to)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT category, COUNT(*), {PaiseSql.Sum("amount")} AS paise
            FROM expenses
            WHERE spent_at >= $from AND spent_at < $to
            GROUP BY category
            ORDER BY paise DESC, category;
            """;
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);

        var totals = new List<ExpenseTotal>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
            totals.Add(new ExpenseTotal(reader.GetString(0), reader.GetInt32(1), PaiseSql.Rupees(reader.GetInt64(2))));

        return totals;
    }
}
