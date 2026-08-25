using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Pos.Core.Data;
using Pos.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Pos.Core.Tests;

/// <summary>
/// What survives a till that stops existing mid-sale: a power cut, a pulled plug, a Windows update
/// that reboots at the wrong moment.
/// </summary>
/// <remarks>
/// These launch a separate process, let it get partway through a sale, and end it with
/// <c>Environment.FailFast</c> — no finalizers, no flush, no orderly close. Testing this inside the
/// test process would only exercise the tidy rollback path, which was never the case in doubt.
/// </remarks>
public class CrashDurabilityTests : IDisposable
{
    private readonly ITestOutputHelper _output;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "pos-crash-tests", Guid.NewGuid().ToString("N"));

    public CrashDurabilityTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string DatabasePath => Path.Combine(_directory, "pos.db");

    /// <summary>Runs the harness and waits for it to die. Returns whatever it managed to say first.</summary>
    private string Crash(string mode)
    {
        var harness = LocateHarness();

        var process = Process.Start(new ProcessStartInfo(harness, [DatabasePath, mode])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;

        var stdout = process.StandardOutput.ReadToEnd().Trim();
        var stderr = process.StandardError.ReadToEnd().Trim();

        Assert.True(process.WaitForExit(60_000), "The crash harness did not finish.");

        _output.WriteLine($"mode {mode}: exit {process.ExitCode}, said '{stdout}'");

        if (!string.IsNullOrWhiteSpace(stderr))
            _output.WriteLine($"stderr: {stderr}");

        // FailFast is not a clean exit, and that is the whole point.
        Assert.NotEqual(0, process.ExitCode);

        return stdout;
    }

    private static string LocateHarness()
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(typeof(CrashDurabilityTests).Assembly.Location)!);

        // tests/Pos.Core.Tests/bin/<config>/<tfm> -> tests/
        var testsRoot = directory.Parent?.Parent?.Parent?.Parent
            ?? throw new InvalidOperationException("Could not walk up to the tests folder.");

        var candidates = new DirectoryInfo(Path.Combine(testsRoot.FullName, "Pos.CrashHarness", "bin"))
            .EnumerateFiles("Pos.CrashHarness.exe", SearchOption.AllDirectories)
            .Concat(new DirectoryInfo(Path.Combine(testsRoot.FullName, "Pos.CrashHarness", "bin"))
                .EnumerateFiles("Pos.CrashHarness", SearchOption.AllDirectories))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();

        Assert.NotEmpty(candidates);
        return candidates[0].FullName;
    }

    // ---- What must survive --------------------------------------------------------------------

    /// <summary>
    /// A sale the customer has paid for and walked away from must be on disk, even if the till
    /// never got to shut down. This is what <c>synchronous = FULL</c> is paid for.
    /// </summary>
    [Fact]
    public void AnInvoiceCommittedBeforeTheCrashIsStillThereAfterwards()
    {
        var invoiceNo = Crash("commit-then-die");

        Assert.False(string.IsNullOrWhiteSpace(invoiceNo));

        var database = new PosDatabase(DatabasePath);
        var saved = new InvoiceRepository(database).FindByInvoiceNo(invoiceNo);

        Assert.NotNull(saved);
        Assert.Equal(838.00m, saved.Sale.Totals.GrandTotal);
        Assert.Equal(2, saved.Sale.Lines.Count);
        Assert.Single(saved.Sale.Payments);
    }

    [Fact]
    public void TheDatabaseIsStillIntactAfterACrash()
    {
        Crash("commit-then-die");

        var report = new PosDatabase(DatabasePath).CheckIntegrity();

        Assert.True(report.IsHealthy, report.ToString());
    }

    // ---- What must not survive ----------------------------------------------------------------

    /// <summary>
    /// A sale that was never committed must leave nothing behind. A half-written invoice in the
    /// books is worse than a lost one: the customer can be asked to pay again, but a phantom
    /// invoice has to be found and explained.
    /// </summary>
    [Fact]
    public void AnUncommittedInvoiceLeavesNoTrace()
    {
        Crash("die-mid-transaction");

        var database = new PosDatabase(DatabasePath);

        Assert.Null(new InvoiceRepository(database).FindByInvoiceNo("L1-2026-999999"));
        Assert.True(database.CheckIntegrity().IsHealthy);
    }

    /// <summary>
    /// The number the abandoned sale took must go back, or the invoice run has a hole in it — and
    /// a GST invoice sequence is expected to be unbroken.
    /// </summary>
    [Fact]
    public void ANumberTakenByAnAbandonedSaleIsReturnedToTheSequence()
    {
        Crash("die-mid-transaction");

        var database = new PosDatabase(DatabasePath);
        var saved = new InvoiceRepository(database).Save(SampleSale());

        Assert.Equal("L1-2026-000001", saved.InvoiceNo);
    }

    /// <summary>A batch abandoned halfway must not leave a header with some of its lines.</summary>
    [Fact]
    public void AnInvoiceAbandonedPartWayThroughItsLinesLeavesNothing()
    {
        Crash("die-while-writing");

        var database = new PosDatabase(DatabasePath);

        Assert.Null(new InvoiceRepository(database).FindByInvoiceNo("L1-2026-888888"));

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM invoice_lines;";

        Assert.Equal(0L, (long)command.ExecuteScalar()!);
    }

    /// <summary>
    /// The till has to come back up on its own. Nobody at a shop counter is going to run a repair
    /// tool before opening, so a database left mid-write by a crash must simply open and work.
    /// </summary>
    [Fact]
    public void TheTillCarriesOnBillingAfterACrash()
    {
        Crash("die-mid-transaction");

        var database = new PosDatabase(DatabasePath);
        database.EnsureMigrated();

        var invoices = new InvoiceRepository(database);
        var first = invoices.Save(SampleSale());
        var second = invoices.Save(SampleSale());

        Assert.Equal("L1-2026-000001", first.InvoiceNo);
        Assert.Equal("L1-2026-000002", second.InvoiceNo);
        Assert.True(database.CheckIntegrity().IsHealthy);
    }

    private static Pos.Core.Domain.SaleDraft SampleSale()
    {
        Pos.Core.Domain.InvoiceLine[] lines =
        [
            Pos.Core.Domain.InvoiceLine.Rehydrate(1, "Toor Dal 1kg", "0713", "8901234567890", null,
                Pos.Core.Domain.UnitType.Each, 189m, 189m, true, 5m, 1m, 0m, false),
        ];

        var totals = Pos.Core.Domain.InvoiceTotals.From(lines);

        return new Pos.Core.Domain.SaleDraft(
            "L1",
            new DateTimeOffset(2026, 8, 26, 11, 0, 0, TimeSpan.FromHours(5.5)),
            null,
            lines,
            totals,
            [new Pos.Core.Domain.Tender(Pos.Core.Domain.TenderType.Cash, totals.GrandTotal)],
            0m,
            0,
            0,
            null);
    }
}
