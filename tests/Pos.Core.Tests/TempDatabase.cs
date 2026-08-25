using Pos.Core.Data;

namespace Pos.Core.Tests;

/// <summary>
/// A throwaway database file for one test, deleted when the test finishes. A real file rather
/// than <c>:memory:</c> so the tests exercise the same journal and pragma behaviour the till gets.
/// </summary>
public sealed class TempDatabase : IDisposable
{
    private readonly string _directory;

    public TempDatabase(bool migrate = true)
    {
        _directory = Path.Combine(Path.GetTempPath(), "pos-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        Database = new PosDatabase(Path.Combine(_directory, "pos.db"));

        if (migrate)
            Database.EnsureMigrated();
    }

    public PosDatabase Database { get; }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp file is not worth failing a passing test over.
        }
    }
}
