using System.Windows.Input;

namespace Pos.App.Input;

/// <summary>
/// A key plus its modifiers, in a form that round-trips through the keymap config file.
/// </summary>
public readonly record struct KeyStroke(Key Key, ModifierKeys Modifiers = ModifierKeys.None)
{
    /// <summary>Renders as "Ctrl+Shift+F5". Modifiers are always written in a fixed order.</summary>
    public override string ToString()
    {
        var parts = new List<string>(4);

        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

        parts.Add(Key.ToString());

        return string.Join('+', parts);
    }

    public static bool TryParse(string? text, out KeyStroke binding)
    {
        binding = default;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0)
            return false;

        var modifiers = ModifierKeys.None;

        for (var i = 0; i < parts.Length - 1; i++)
        {
            var modifier = parts[i].ToLowerInvariant() switch
            {
                "ctrl" or "control" => ModifierKeys.Control,
                "alt" => ModifierKeys.Alt,
                "shift" => ModifierKeys.Shift,
                "win" or "windows" => ModifierKeys.Windows,
                _ => (ModifierKeys?)null,
            };

            if (modifier is null)
                return false;

            modifiers |= modifier.Value;
        }

        if (!Enum.TryParse<Key>(parts[^1], ignoreCase: true, out var key) || !Enum.IsDefined(key))
            return false;

        binding = new KeyStroke(key, modifiers);
        return true;
    }

    public static KeyStroke Parse(string text) =>
        TryParse(text, out var binding)
            ? binding
            : throw new FormatException($"'{text}' is not a valid key binding. Expected something like 'F5' or 'Ctrl+Shift+D'.");
}
