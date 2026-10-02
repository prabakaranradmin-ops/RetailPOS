using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using Pos.Core.Configuration;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// The four looks are the same palette in four colourings, and every screen takes its colours from
/// whichever one is loaded.
/// </summary>
/// <remarks>
/// A look missing one colour name would not fail to load. WPF would leave whatever used that name
/// in its default colour, black text on a dark page or nothing at all, and only on that look, which
/// is exactly the kind of fault found by somebody at a counter at four in the afternoon.
/// </remarks>
public partial class PaletteTests
{
    private static readonly string[] Looks = ["Night", "Evening", "Morning", "Noon"];

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RetailPOS.sln")))
                return dir.FullName;
        }

        throw new InvalidOperationException($"No RetailPOS.sln above {AppContext.BaseDirectory}.");
    }

    /// <summary>Every name a look defines, whatever kind of resource it is.</summary>
    private static SortedSet<string> NamesIn(string look) =>
        new(ThemeContrastTests.DocumentOf(look).Root!.Elements()
                .Select(e => (string?)e.Attribute(ThemeContrastTests.Xaml + "Key"))
                .OfType<string>(),
            StringComparer.Ordinal);

    [Fact]
    public void EveryLookDefinesTheSameColourNames()
    {
        var night = NamesIn("Night");

        foreach (var look in Looks.Skip(1))
        {
            var names = NamesIn(look);

            Assert.True(night.SetEquals(names),
                $"{look}.xaml is not the same palette as Night.xaml. "
                + $"Missing: {string.Join(", ", night.Except(names))}. "
                + $"Extra: {string.Join(", ", names.Except(night))}.");
        }
    }

    /// <summary>
    /// The looks each say whether they are dark, because the title bar Windows draws asks.
    /// </summary>
    [Theory]
    [InlineData("Night", true)]
    [InlineData("Evening", true)]
    [InlineData("Morning", false)]
    [InlineData("Noon", false)]
    public void EveryLookSaysWhetherItIsDark(string look, bool dark)
    {
        var flag = ThemeContrastTests.DocumentOf(look).Root!.Elements()
            .Single(e => (string?)e.Attribute(ThemeContrastTests.Xaml + "Key") == "IsDarkTheme");

        Assert.Equal(dark, bool.Parse(flag.Value));
    }

    /// <summary>
    /// The styles hold no colours of their own: one left in <c>Theme.xaml</c> would be the same in
    /// every look, and right in at most one of them.
    /// </summary>
    [Fact]
    public void TheStylesHoldNoColoursOfTheirOwn()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Theme.xaml");
        var brushes = XDocument.Load(path).Root!.Elements()
            .Where(e => e.Name.LocalName.EndsWith("Brush", StringComparison.Ordinal))
            .Select(e => (string?)e.Attribute(ThemeContrastTests.Xaml + "Key") ?? e.Name.LocalName)
            .ToList();

        Assert.True(brushes.Count == 0,
            "Theme.xaml defines brushes, which would look the same in every look: "
            + string.Join(", ", brushes) + ". They belong in each palette in Themes\\.");
    }

    [GeneratedRegex(@"\{StaticResource\s+([A-Za-z0-9_]+)\s*\}")]
    private static partial Regex StaticReference();

    /// <summary>
    /// Every use of a palette colour is a dynamic reference, so it changes with the look.
    /// </summary>
    /// <remarks>
    /// A static one is resolved once, when the window is built, and stays the colour of whatever
    /// look was loaded then. It compiles, it runs, and on a light look it is a dark patch.
    /// </remarks>
    [Fact]
    public void EveryUseOfAPaletteColourFollowsTheLook()
    {
        var palette = NamesIn("Night");
        var src = Path.Combine(RepositoryRoot(), "src", "Pos.App");
        var found = new List<string>();

        foreach (var file in Directory.EnumerateFiles(src, "*.xaml", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);

            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match match in StaticReference().Matches(lines[i]))
                {
                    if (palette.Contains(match.Groups[1].Value))
                        found.Add($"{Path.GetRelativePath(src, file)}:{i + 1} {match.Value}");
                }
            }
        }

        Assert.True(found.Count == 0,
            "These take a palette colour once and keep it whatever the look:\n  " + string.Join("\n  ", found));
    }

    // ---- Changing the look while the windows are open --------------------------------------------

    [Theory]
    [InlineData(ScreenTheme.Morning)]
    [InlineData(ScreenTheme.Noon)]
    [InlineData(ScreenTheme.Evening)]
    [InlineData(ScreenTheme.Night)]
    public void ChangingTheLookSwapsEveryColour(ScreenTheme look)
    {
        var expected = ThemeContrastTests.PaletteOf(look.ToString());

        Wpf.Run(() =>
        {
            try
            {
                Pos.App.Looks.Apply(look);

                Assert.Equal(look, Pos.App.Looks.Showing);

                foreach (var (name, colour) in expected)
                {
                    var brush = Assert.IsType<SolidColorBrush>(Application.Current.FindResource(name));
                    Assert.Equal((Color)ColorConverter.ConvertFromString(colour), brush.Color);
                }

                // One palette, swapped rather than stacked: a second would hide the first's colours
                // only where it happened to define the same names.
                Assert.Single(Application.Current.Resources.MergedDictionaries,
                    d => d.Source?.OriginalString.Contains("Themes/", StringComparison.Ordinal) == true);
            }
            finally
            {
                Pos.App.Looks.Apply(ScreenTheme.Night);
            }
        });
    }

    /// <summary>
    /// Following the time of day shows the hour's look, and a look chosen outright stops the clock
    /// changing it.
    /// </summary>
    [Fact]
    public void FollowingTheTimeOfDayShowsTheHoursLook()
    {
        var now = new TimeOnly(12, 30);

        Wpf.Run(() =>
        {
            try
            {
                Pos.App.Looks.Follow(ScreenTheme.ByTimeOfDay, () => now);
                Assert.Equal(ScreenTheme.ByTimeOfDay, Pos.App.Looks.Chosen);
                Assert.Equal(ScreenTheme.Noon, Pos.App.Looks.Showing);

                now = new TimeOnly(17, 0);
                Pos.App.Looks.Tick();
                Assert.Equal(ScreenTheme.Evening, Pos.App.Looks.Showing);

                now = new TimeOnly(19, 0);
                Pos.App.Looks.Tick();
                Assert.Equal(ScreenTheme.Night, Pos.App.Looks.Showing);

                Pos.App.Looks.Follow(ScreenTheme.Morning);
                now = new TimeOnly(21, 0);
                Pos.App.Looks.Tick();
                Assert.Equal(ScreenTheme.Morning, Pos.App.Looks.Showing);
            }
            finally
            {
                Pos.App.Looks.Follow(ScreenTheme.Night, () => TimeOnly.FromDateTime(DateTime.Now));
            }
        });
    }
}
