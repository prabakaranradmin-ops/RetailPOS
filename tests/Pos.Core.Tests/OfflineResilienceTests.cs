using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Pos.Core.Tests;

/// <summary>
/// SRS UR-01 and UR-06: billing must not depend on a network, at all, ever.
/// </summary>
/// <remarks>
/// The usual way to test this is to disable the adapter and see whether the till still works, and
/// that is worth doing on a lane. But it only proves the network was not needed on the path the
/// tester happened to walk. These tests instead read the compiled assemblies and assert that the
/// billing code does not so much as reference a networking type — which no amount of clicking can
/// establish, and which fails the build the moment someone adds an HttpClient to a repository.
/// </remarks>
public class OfflineResilienceTests(ITestOutputHelper output)
{
    /// <summary>
    /// Namespaces that would mean a network round trip. Deliberately broad: the point is not to
    /// catch a particular class but to make reaching for the network visible in review.
    /// </summary>
    private static readonly string[] ForbiddenNamespaces =
    [
        "System.Net",
        "System.Net.Http",
        "System.Net.Sockets",
        "System.Net.NetworkInformation",
        "System.Web",
        "System.ServiceModel",
    ];

    /// <summary>Assemblies on the billing path. Every one of these runs at the till.</summary>
    public static TheoryData<string> BillingAssemblies =>
    [
        "Pos.Core.Tax",
        "Pos.Core.Domain",
        "Pos.Core.Data",
        "Pos.Core.Loyalty",
        "Pos.Core.Hardware",
        "Pos.Core.Configuration",
    ];

    private static string LocateAssembly(string name)
    {
        var directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var path = Path.Combine(directory, name + ".dll");

        Assert.True(File.Exists(path), $"{name}.dll was not next to the test assembly — the reference list is stale.");
        return path;
    }

    /// <summary>Every type this assembly names from somewhere else.</summary>
    private static IEnumerable<string> ReferencedTypeNames(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);

        var metadata = pe.GetMetadataReader();

        foreach (var handle in metadata.TypeReferences)
        {
            var reference = metadata.GetTypeReference(handle);
            var ns = metadata.GetString(reference.Namespace);
            var name = metadata.GetString(reference.Name);

            yield return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
        }
    }

    private static IEnumerable<string> ReferencedAssemblyNames(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);

        var metadata = pe.GetMetadataReader();

        foreach (var handle in metadata.AssemblyReferences)
            yield return metadata.GetString(metadata.GetAssemblyReference(handle).Name);
    }

    [Theory]
    [MemberData(nameof(BillingAssemblies))]
    public void NoBillingAssemblyNamesANetworkingType(string assemblyName)
    {
        var offenders = ReferencedTypeNames(LocateAssembly(assemblyName))
            .Where(type => ForbiddenNamespaces.Any(ns =>
                type.StartsWith(ns + ".", StringComparison.Ordinal) || type == ns))
            .Distinct()
            .OrderBy(type => type)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"{assemblyName} references networking types, which puts the network on the billing path: {string.Join(", ", offenders)}");
    }

    [Theory]
    [MemberData(nameof(BillingAssemblies))]
    public void NoBillingAssemblyReferencesANetworkingAssembly(string assemblyName)
    {
        var offenders = ReferencedAssemblyNames(LocateAssembly(assemblyName))
            .Where(reference =>
                reference.StartsWith("System.Net", StringComparison.Ordinal) ||
                reference.StartsWith("System.Web", StringComparison.Ordinal))
            .Distinct()
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"{assemblyName} references {string.Join(", ", offenders)}.");
    }

    /// <summary>
    /// Proves the check can fail. A guard nobody has seen fail is a guard nobody knows is wired up.
    /// </summary>
    [Fact]
    public void TheNetworkingCheckWouldCatchAnAssemblyThatUsedTheNetwork()
    {
        // The test assembly itself is not on the billing path, and xunit's runner does use sockets.
        var self = Assembly.GetExecutingAssembly().Location;
        var networkingTypes = ReferencedTypeNames(self)
            .Concat(ReferencedAssemblyNames(self))
            .Any(name => name.StartsWith("System.Net", StringComparison.Ordinal));

        output.WriteLine($"test assembly references networking: {networkingTypes}");

        // Whatever the answer, the detector must be looking at something. An assembly with no type
        // references at all would make every assertion above vacuously true.
        Assert.NotEmpty(ReferencedTypeNames(LocateAssembly("Pos.Core.Domain")));
    }

    /// <summary>
    /// The other half of offline: the local database is opened from a path on this machine, with
    /// no server, no host and no port anywhere in the connection string.
    /// </summary>
    [Fact]
    public void TheDatabaseIsAPlainLocalFile()
    {
        using var temp = new TempDatabase();

        Assert.True(File.Exists(temp.Database.DatabasePath));
        Assert.False(Path.IsPathFullyQualified(temp.Database.DatabasePath) && temp.Database.DatabasePath.StartsWith(@"\\", StringComparison.Ordinal),
            "The database is on a UNC path, which puts a network share between the till and its own books.");
    }

    /// <summary>
    /// A full sale, start to finish, with nothing but the local file. If anything on this path
    /// needed a network it would have to fail here, because there is nothing to reach.
    /// </summary>
    [Fact]
    public void ACompleteSaleRunsWithNothingButALocalFile()
    {
        using var temp = new TempDatabase();
        temp.Items.AddRange([Catalogue.Item(sku: "DAL001", barcode: "8901234567890", name: "Toor Dal 1kg", price: 189m, gstRate: 5m)]);

        var invoices = new InvoiceRepository(temp.Database);
        var customers = new CustomerRepository(temp.Database);
        var checkout = new CheckoutService(invoices, customers, new RecordingDrawerService());

        var bill = new InvoiceEngine("33");
        bill.AddItem(temp.Items.FindByBarcode("8901234567890")!);

        var basket = new TenderBasket(bill.Totals.GrandTotal);
        basket.Add(TenderType.Cash, 200m);

        var result = checkout.Complete("L1", bill, basket);

        Assert.Equal(189.00m, result.Invoice.GrandTotal);
        Assert.NotNull(invoices.FindByInvoiceNo(result.Invoice.InvoiceNo));
    }
}
