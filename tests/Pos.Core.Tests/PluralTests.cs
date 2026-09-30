using System.Text.RegularExpressions;
using Pos.Core.Domain;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// Counts in the right number: "1 bill", "3 bills" - and never "bill(s)" again.
/// </summary>
public class PluralTests
{
    [Theory]
    [InlineData(0, "bill", null, "0 bills")]
    [InlineData(1, "bill", null, "1 bill")]
    [InlineData(2, "bill", null, "2 bills")]
    [InlineData(125000, "bill", null, "1,25,000 bills")]
    [InlineData(1, "copy", "copies", "1 copy")]
    [InlineData(3, "copy", "copies", "3 copies")]
    [InlineData(2, "credit note", null, "2 credit notes")]
    public void ACountTakesItsNounInTheRightNumber(int count, string one, string? many, string expected) =>
        Assert.Equal(expected, Plural.Of(count, one, many));

    [Fact]
    public void TheNounCanBeHadAlone()
    {
        Assert.Equal("bill", Plural.Noun(1, "bill"));
        Assert.Equal("bills", Plural.Noun(4, "bill"));
    }

    /// <summary>
    /// No "(s)" in any string a person reads - the screens, the printed bills and reports, the web
    /// pages, the command line. There were about a hundred and ten of them; a bracketed plural reads
    /// as a form rather than a sentence.
    /// </summary>
    /// <remarks>
    /// Read from the source rather than from what the program happens to print in a test, so a
    /// message nobody's test reaches is held to it too. Comments are left alone: they are written for
    /// whoever maintains the code, and some of them quote the old wording to say why it went.
    /// </remarks>
    [Fact]
    public void NoStringAPersonReadsHasABracketedPlural()
    {
        var src = Path.Combine(RepositoryRoot(), "src");
        var literal = new Regex("\"[^\"\\r\\n]*\\(s\\)[^\"\\r\\n]*\"");
        var found = new List<string>();

        foreach (var file in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal))
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var lines = File.ReadAllLines(file);

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimStart();

                if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("<!--", StringComparison.Ordinal))
                    continue;

                if (literal.IsMatch(lines[i]) || (file.EndsWith(".xaml", StringComparison.Ordinal) && lines[i].Contains("(s)", StringComparison.Ordinal)))
                    found.Add($"{Path.GetRelativePath(src, file)}:{i + 1}: {line}");
            }
        }

        Assert.True(found.Count == 0, "These strings still say \"(s)\":\n  " + string.Join("\n  ", found));
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RetailPOS.sln")))
                return dir.FullName;
        }

        throw new InvalidOperationException($"No RetailPOS.sln above {AppContext.BaseDirectory}.");
    }
}
