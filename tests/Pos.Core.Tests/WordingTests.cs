using System.Text.RegularExpressions;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// One word for one thing, wherever a person reads it.
/// </summary>
public class WordingTests
{
    /// <summary>A string literal on one line, escapes and all.</summary>
    private static readonly Regex Literal = new("\"(?:[^\"\\\\\\r\\n]|\\\\.)*\"");

    /// <summary>
    /// A bill put aside is "held": F5 is Hold, and F6 lists the "Held bills". The messages, the
    /// printed bill and the day-end report said "parked" for the same thing, so a cashier met two
    /// words for it.
    /// </summary>
    /// <remarks>
    /// Only what a person reads is held to it. The code keeps its own names (<c>HeldBillRepository.Park</c>),
    /// and comments are left alone.
    /// </remarks>
    [Fact]
    public void ABillPutAsideIsHeldNotParked()
    {
        var found = StringsSaying(new Regex(@"\b[Pp]ark(ed|ing|s)?\b"));

        Assert.True(found.Count == 0, "These strings still say \"park\":\n  " + string.Join("\n  ", found));
    }

    /// <summary>Every string literal in the source, C# and XAML, that says what the pattern matches.</summary>
    private static List<string> StringsSaying(Regex words)
    {
        var src = Path.Combine(RepositoryRoot(), "src");
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

                if (Literal.Matches(lines[i]).Any(literal => words.IsMatch(literal.Value)))
                    found.Add($"{Path.GetRelativePath(src, file)}:{i + 1}: {line}");
            }
        }

        return found;
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
