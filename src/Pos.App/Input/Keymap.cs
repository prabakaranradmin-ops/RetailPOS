using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace Pos.App.Input;

/// <summary>
/// Maps key gestures to actions. Every binding is configurable from a JSON file, so a store can
/// match whatever its cashiers already have in their fingers without a rebuild.
/// </summary>
public sealed class Keymap
{
    private readonly Dictionary<KeyStroke, PosAction> _bindings;

    public Keymap(IReadOnlyDictionary<KeyStroke, PosAction> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        _bindings = new Dictionary<KeyStroke, PosAction>(bindings);
    }

    /// <summary>
    /// Bindings shipped out of the box. Chosen to be self-evident (Delete deletes, Escape backs
    /// out, plus and minus change quantity) rather than to imitate any particular existing product.
    /// </summary>
    public static Keymap Default => new(new Dictionary<KeyStroke, PosAction>
    {
        [new(Key.F2)] = PosAction.FocusSearch,
        [new(Key.F3)] = PosAction.EditQuantity,
        [new(Key.F4)] = PosAction.EditDiscount,
        [new(Key.F5)] = PosAction.HoldBill,
        [new(Key.F6)] = PosAction.RecallBill,

        [new(Key.Up)] = PosAction.MoveUp,
        [new(Key.Down)] = PosAction.MoveDown,
        [new(Key.Enter)] = PosAction.Commit,
        [new(Key.Escape)] = PosAction.Cancel,

        [new(Key.Delete)] = PosAction.DeleteLine,

        // Both the numeric keypad and the main row, so it works on a compact till keyboard.
        [new(Key.Add)] = PosAction.IncrementQuantity,
        [new(Key.OemPlus)] = PosAction.IncrementQuantity,
        [new(Key.Subtract)] = PosAction.DecrementQuantity,
        [new(Key.OemMinus)] = PosAction.DecrementQuantity,

        [new(Key.F7)] = PosAction.FindCustomer,
        [new(Key.F12)] = PosAction.Tender,

        [new(Key.N, ModifierKeys.Control)] = PosAction.NewBill,
    });

    public IReadOnlyDictionary<KeyStroke, PosAction> Bindings => _bindings;

    public PosAction? Resolve(Key key, ModifierKeys modifiers) =>
        _bindings.TryGetValue(new KeyStroke(key, modifiers), out var action) ? action : null;

    public PosAction? Resolve(KeyStroke binding) =>
        _bindings.TryGetValue(binding, out var action) ? action : null;

    /// <summary>All gestures currently bound to an action. Useful for rendering a help overlay.</summary>
    public IReadOnlyList<KeyStroke> GesturesFor(PosAction action) =>
        _bindings.Where(pair => pair.Value == action).Select(pair => pair.Key).ToList();

    /// <summary>
    /// Applies overrides on top of the defaults. A store's file only needs to list what it wants
    /// changed, and an action the file does not mention keeps its default gesture. A gesture the
    /// file rebinds is taken away from whatever action held it.
    /// </summary>
    public Keymap WithOverrides(IReadOnlyDictionary<KeyStroke, PosAction> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);

        var merged = new Dictionary<KeyStroke, PosAction>(_bindings);

        foreach (var (binding, action) in overrides)
            merged[binding] = action;

        return new Keymap(merged);
    }

    /// <summary>
    /// Loads the keymap at <paramref name="path"/>, layered over the defaults. A missing file is
    /// the normal case on a fresh install and simply yields the defaults; a malformed one is not
    /// silently ignored, because a cashier discovering at the till that a key does nothing is far
    /// worse than a clear failure at startup.
    /// </summary>
    public static Keymap LoadOrDefault(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
            return Default;

        KeymapFile? file;

        try
        {
            file = JsonSerializer.Deserialize<KeymapFile>(File.ReadAllText(path), SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"The keymap at '{path}' is not valid JSON: {ex.Message}", ex);
        }

        if (file?.Bindings is null)
            return Default;

        var overrides = new Dictionary<KeyStroke, PosAction>();

        foreach (var (gesture, actionName) in file.Bindings)
        {
            if (!KeyStroke.TryParse(gesture, out var binding))
                throw new InvalidOperationException($"The keymap at '{path}' has an unrecognised key gesture: '{gesture}'.");

            if (!Enum.TryParse<PosAction>(actionName, ignoreCase: true, out var action) || !Enum.IsDefined(action))
                throw new InvalidOperationException($"The keymap at '{path}' binds '{gesture}' to an unknown action: '{actionName}'.");

            overrides[binding] = action;
        }

        return Default.WithOverrides(overrides);
    }

    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var file = new KeymapFile
        {
            Bindings = _bindings.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value.ToString()),
        };

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, JsonSerializer.Serialize(file, SerializerOptions));
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private sealed class KeymapFile
    {
        [JsonPropertyName("bindings")]
        public Dictionary<string, string>? Bindings { get; set; }
    }
}
