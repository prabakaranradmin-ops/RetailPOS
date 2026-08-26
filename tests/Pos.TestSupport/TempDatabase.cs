using Microsoft.Data.Sqlite;
using Pos.Core.Data;

namespace Pos.TestSupport;

/// <summary>
/// A throwaway database file for one test, deleted when the test finishes. A real file rather
/// than <c>:memory:</c> so the tests exercise the same journal and pragma behaviour the till gets.
/// </summary>
public sealed class TempDatabase : IDisposable
{
    private readonly string _directory;
    private ItemRepository? _items;

    public TempDatabase(bool migrate = true)
    {
        _directory = Path.Combine(Path.GetTempPath(), "pos-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        Database = new PosDatabase(Path.Combine(_directory, "pos.db"));

        if (migrate)
            Database.EnsureMigrated();
    }

    public PosDatabase Database { get; }

    public ItemRepository Items => _items ??= new ItemRepository(Database);

    /// <summary>
    /// Releases this database's pooled handles so the file can be deleted, and nobody else's.
    /// </summary>
    /// <remarks>
    /// <c>ClearAllPools</c> would be simpler and is what this used to call, but it is process-wide:
    /// every test finishing would yank the pooled connections out from under every test still
    /// running beside it. That was harmless while few tests touched a database at once and became
    /// an intermittent failure as soon as more did — a test failing because of what a different
    /// test was doing, which is the worst kind to be handed on a red build.
    /// </remarks>
    public void Dispose()
    {
        using (var connection = new SqliteConnection(Database.ConnectionString))
            SqliteConnection.ClearPool(connection);

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
