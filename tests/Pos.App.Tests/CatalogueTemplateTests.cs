using System.IO;
using Pos.App.Views;
using Pos.Core.Domain.Import;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// The catalogue template the Catalogue tab hands over. It is the file the installer ships, carried
/// inside the program, so the two cannot drift - and it has to import cleanly, or the first thing a
/// new shop does with it is fail.
/// </summary>
public class CatalogueTemplateTests
{
    private static byte[] Embedded()
    {
        using var stream = typeof(OwnerView).Assembly.GetManifestResourceStream("catalog_template.csv");
        Assert.NotNull(stream);

        using var copy = new MemoryStream();
        stream!.CopyTo(copy);
        return copy.ToArray();
    }

    [Fact]
    public void TheTemplateOnTheScreenIsTheOneTheInstallerShips()
    {
        var shipped = File.ReadAllBytes(Path.Combine(RepositoryRoot(), "deploy", "catalog_template.csv"));

        Assert.Equal(shipped, Embedded());
    }

    [Fact]
    public void TheTemplateParsesWithoutAProblem()
    {
        using var reader = new StreamReader(new MemoryStream(Embedded()));

        var (items, problems) = ItemCsvParser.Parse(reader);

        Assert.Empty(problems);
        Assert.NotEmpty(items);
    }

    private static string RepositoryRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);

        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "RetailPOS.sln")))
            folder = folder.Parent;

        return folder?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }
}
