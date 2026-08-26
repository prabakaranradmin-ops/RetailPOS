using Microsoft.Data.Sqlite;

namespace Pos.Core.Data;

/// <param name="IsHealthy">True when SQLite found nothing wrong with the file.</param>
/// <param name="Problems">
/// What it found, in its own words. Empty when healthy. These are diagnostics for whoever has to
/// recover the file, not something to show a cashier.
/// </param>
public readonly record struct IntegrityReport(bool IsHealthy, IReadOnlyList<string> Problems)
{
    public override string ToString() =>
        IsHealthy ? "ok" : string.Join("; ", Problems);
}

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

    /// <summary>
    /// How connections to this database are opened.
    /// </summary>
    /// <remarks>
    /// Exposed so a caller can release <em>this</em> database's pooled handles, with
    /// <c>SqliteConnection.ClearPool</c>, rather than every database in the process. The difference
    /// only matters where several are open at once, which on a lane never happens and in a test run
    /// always does.
    /// </remarks>
    public string ConnectionString => _connectionString;

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

    /// <summary>
    /// Refreshes the query planner's statistics. Run this after loading or substantially changing
    /// the item master.
    /// </summary>
    /// <remarks>
    /// This is not optional tuning. With no statistics SQLite assumes an equality test is more
    /// selective than a range, so it serves a SKU prefix search from the <c>is_active</c> index —
    /// which matches nearly every row — and fetches each one to check its SKU. Measured over a
    /// 100k-SKU catalogue that is 225ms, against NFR-01's 100ms budget. With statistics it picks
    /// the unique SKU index and the same query is too fast to time.
    /// </remarks>
    public void Analyze()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "ANALYZE;";
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Checks the database file for damage.
    /// </summary>
    /// <remarks>
    /// Run before a trading day rather than after a problem. A till's database is the shop's book
    /// of account, and corruption on a page nobody has read yet is silent until someone tries to
    /// read it — which will be during a GST filing, not at a convenient moment. The check walks
    /// every page, so it costs real time on a large file and is not something to do at startup.
    /// </remarks>
    public IntegrityReport CheckIntegrity(bool thorough = true)
    {
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();

            // quick_check skips the index cross-checks, which is most of the cost; integrity_check
            // also verifies that every index agrees with its table.
            command.CommandText = thorough ? "PRAGMA integrity_check;" : "PRAGMA quick_check;";

            var problems = new List<string>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var line = reader.GetString(0);

                // A healthy database answers with the single word "ok".
                if (!string.Equals(line, "ok", StringComparison.OrdinalIgnoreCase))
                    problems.Add(line);
            }

            return new IntegrityReport(problems.Count == 0, problems);
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            // A file damaged badly enough that SQLite will not open it is the worst finding of all,
            // and has to come back as a report rather than as an exception out of a health check.
            return new IntegrityReport(false, [ex.Message]);
        }
    }

    /// <summary>Reclaims free pages and defragments the file. Slow; run out of hours.</summary>
    public void Vacuum()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "VACUUM;";
        command.ExecuteNonQuery();
    }
}
