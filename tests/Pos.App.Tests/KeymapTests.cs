using System.IO;
using System.Windows.Input;
using Pos.App.Input;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// Phase 2 requires the keymap to be configurable rather than hardcoded.
/// </summary>
public class KeymapTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "pos-keymap-tests", Guid.NewGuid().ToString("N"));

    private string PathTo(string name) => Path.Combine(_directory, name);

    public KeymapTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("F5", Key.F5, ModifierKeys.None)]
    [InlineData("Delete", Key.Delete, ModifierKeys.None)]
    [InlineData("Ctrl+N", Key.N, ModifierKeys.Control)]
    [InlineData("ctrl+shift+F12", Key.F12, ModifierKeys.Control | ModifierKeys.Shift)]
    [InlineData("Alt+D", Key.D, ModifierKeys.Alt)]
    public void GesturesParse(string text, Key key, ModifierKeys modifiers)
    {
        Assert.True(KeyStroke.TryParse(text, out var binding));
        Assert.Equal(new KeyStroke(key, modifiers), binding);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NotAKey")]
    [InlineData("Hyper+F5")]
    [InlineData(null)]
    public void NonsenseGesturesAreRejected(string? text)
    {
        Assert.False(KeyStroke.TryParse(text, out _));
        if (text is not null)
            Assert.Throws<FormatException>(() => KeyStroke.Parse(text));
    }

    [Fact]
    public void GesturesRoundTripThroughTheirTextForm()
    {
        var original = new KeyStroke(Key.F9, ModifierKeys.Control | ModifierKeys.Shift);

        Assert.Equal(original, KeyStroke.Parse(original.ToString()));
    }

    [Fact]
    public void TheDefaultMapCoversEveryAction()
    {
        var keymap = Keymap.Default;

        foreach (var action in Enum.GetValues<PosAction>())
            Assert.True(keymap.GesturesFor(action).Count > 0, $"{action} has no default gesture.");
    }

    [Fact]
    public void AnUnboundGestureResolvesToNothing()
    {
        Assert.Null(Keymap.Default.Resolve(Key.F24, ModifierKeys.None));
        Assert.Null(Keymap.Default.Resolve(Key.F5, ModifierKeys.Control));
    }

    [Fact]
    public void AMissingFileYieldsTheDefaults()
    {
        var keymap = Keymap.LoadOrDefault(PathTo("absent.json"));

        Assert.Equal(PosAction.HoldBill, keymap.Resolve(Key.F5, ModifierKeys.None));
    }

    /// <summary>
    /// A store's file lists only what it wants changed; everything it does not mention keeps its
    /// default gesture.
    /// </summary>
    [Fact]
    public void OverridesLayerOverTheDefaults()
    {
        var path = PathTo("keymap.json");
        File.WriteAllText(path, """
            { "bindings": { "F9": "HoldBill" } }
            """);

        var keymap = Keymap.LoadOrDefault(path);

        Assert.Equal(PosAction.HoldBill, keymap.Resolve(Key.F9, ModifierKeys.None));
        Assert.Equal(PosAction.RecallBill, keymap.Resolve(Key.F6, ModifierKeys.None));
    }

    /// <summary>Rebinding a gesture takes it away from whatever action held it before.</summary>
    [Fact]
    public void RebindingAGestureMovesItToTheNewAction()
    {
        var path = PathTo("keymap.json");
        File.WriteAllText(path, """
            { "bindings": { "F5": "DeleteLine" } }
            """);

        var keymap = Keymap.LoadOrDefault(path);

        Assert.Equal(PosAction.DeleteLine, keymap.Resolve(Key.F5, ModifierKeys.None));
        Assert.DoesNotContain(new KeyStroke(Key.F5), keymap.GesturesFor(PosAction.HoldBill));
    }

    [Fact]
    public void AMalformedFileFailsLoudly()
    {
        var path = PathTo("broken.json");
        File.WriteAllText(path, "{ not json");

        var ex = Assert.Throws<InvalidOperationException>(() => Keymap.LoadOrDefault(path));
        Assert.Contains("not valid JSON", ex.Message);
    }

    [Fact]
    public void AnUnknownGestureNamesTheOffendingEntry()
    {
        var path = PathTo("bad-key.json");
        File.WriteAllText(path, """
            { "bindings": { "Sparkle": "HoldBill" } }
            """);

        var ex = Assert.Throws<InvalidOperationException>(() => Keymap.LoadOrDefault(path));
        Assert.Contains("Sparkle", ex.Message);
    }

    [Fact]
    public void AnUnknownActionNamesTheOffendingEntry()
    {
        var path = PathTo("bad-action.json");
        File.WriteAllText(path, """
            { "bindings": { "F9": "MakeTea" } }
            """);

        var ex = Assert.Throws<InvalidOperationException>(() => Keymap.LoadOrDefault(path));
        Assert.Contains("MakeTea", ex.Message);
    }

    [Fact]
    public void ASavedKeymapReloadsIdentically()
    {
        var path = PathTo("saved.json");
        var original = Keymap.Default.WithOverrides(new Dictionary<KeyStroke, PosAction>
        {
            [new KeyStroke(Key.F9, ModifierKeys.Control)] = PosAction.NewBill,
        });

        original.Save(path);
        var reloaded = Keymap.LoadOrDefault(path);

        Assert.Equal(
            original.Bindings.OrderBy(pair => pair.Key.ToString()).ToList(),
            reloaded.Bindings.OrderBy(pair => pair.Key.ToString()).ToList());
    }
}
