using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Pos.Core.Data;

/// <summary>
/// Applies the embedded schema migrations in order, tracking progress in SQLite's own
/// <c>user_version</c> pragma so no bookkeeping table of our own is needed.
/// </summary>
public static class Migrator
{
    /// <summary>Migration file names, in the order they must be applied. Append only.</summary>
    private static readonly string[] MigrationFiles =
    [
        "001_initial_schema.sql",
        "002_case_insensitive_sku.sql",
        "003_held_bills.sql",
        "004_day_close.sql",
        "005_void_and_cashier.sql",
    ];

    /// <summary>Schema version a freshly migrated database ends up at.</summary>
    public static int LatestVersion => MigrationFiles.Length;

    public static int GetVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>
    /// Brings the database up to <see cref="LatestVersion"/>. Safe to call on every startup:
    /// migrations already applied are skipped, and running against an up-to-date database is a
    /// no-op.
    /// </summary>
    /// <returns>The number of migrations applied.</returns>
    public static int Migrate(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var current = GetVersion(connection);

        if (current > LatestVersion)
        {
            throw new InvalidOperationException(
                $"The database is at schema version {current}, newer than this build understands " +
                $"({LatestVersion}). Upgrade the application rather than downgrading the database.");
        }

        var applied = 0;

        for (var version = current; version < MigrationFiles.Length; version++)
        {
            var sql = ReadMigration(MigrationFiles[version]);

            // Each migration and its version bump land together, so an interrupted upgrade leaves
            // the database on a version that genuinely reflects its schema.
            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }

            using (var bump = connection.CreateCommand())
            {
                bump.Transaction = transaction;
                // PRAGMA does not accept a parameter, and this value is an int from a private array.
                bump.CommandText = $"PRAGMA user_version = {version + 1};";
                bump.ExecuteNonQuery();
            }

            transaction.Commit();
            applied++;
        }

        return applied;
    }

    private static string ReadMigration(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"Pos.Core.Data.Migrations.{fileName}";

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Migration '{fileName}' is missing from the assembly.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
