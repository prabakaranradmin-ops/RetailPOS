using Microsoft.Data.Sqlite;

namespace Pos.Core.Data;

/// <summary>
/// Opens connections to the lane's local database file. Everything the till needs lives in this
/// one file on this one machine — there is no server to reach and nothing to fall back to.
/// </summary>
public sealed class PosDatabase
{
    private readonly string _connectionString;

    /// <param name="databasePath">
    /// Path to the SQLite file, or <c>:memory:</c> for a throwaway database in tests.
    /// </param>
    public PosDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        DatabasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Pooling keeps an in-memory database alive between connections, which matters for
            // tests and costs nothing on a file database.
            Pooling = true,
        }.ToString();
    }

    public string DatabasePath { get; }

    /// <summary>Opens a connection with the pragmas this schema depends on already applied.</summary>
    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            // The schema declares cascades and references; SQLite ignores them unless asked.
            "PRAGMA foreign_keys = ON;" +
            // A power cut mid-sale must not corrupt the file or lose the committed invoice.
            "PRAGMA journal_mode = WAL;" +
            "PRAGMA synchronous = FULL;";
        command.ExecuteNonQuery();

        return connection;
    }

    /// <summary>
    /// Creates the file if absent and applies any outstanding migrations. Called once at startup.
    /// </summary>
    public void EnsureMigrated()
    {
        using var connection = OpenConnection();
        Migrator.Migrate(connection);
    }
}
