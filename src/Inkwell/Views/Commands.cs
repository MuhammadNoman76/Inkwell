using System.Globalization;
using System.Windows.Input;

namespace Inkwell.Views;

/// <summary>
/// A keyboard shortcut. Every shortcut Inkwell binds or mentions is one of the <see cref="Shortcuts"/>, and menus,
/// tooltips and the palette show its <see cref="Text"/>, so what they say is what the key does.
/// </summary>
public sealed class KeyChord
{
    public KeyChord(Key key, ModifierKeys modifiers = ModifierKeys.None)
    {
        Key = key;
        Modifiers = modifiers;
    }

    public Key Key { get; }
    public ModifierKeys Modifiers { get; }

    /// <summary>
    /// For people: the key names of the keyboard layout in use, e.g. "Ctrl+=" on a US keyboard and "Ctrl++" on a
    /// German one for the same key (punctuation keys sit in different places on different layouts).
    /// </summary>
    public string Text => Describe(Key, Modifiers, layoutNames: true);

    /// <summary>With US key names, as in the README (and what <see cref="TryParse"/> reads).</summary>
    public string UsText => Describe(Key, Modifiers, layoutNames: false);

    public bool Matches(Key key, ModifierKeys modifiers) => key == Key && modifiers == Modifiers;

    /// <summary>The same keys (for finding duplicates).</summary>
    public bool SameKeys(KeyChord other) => other.Key == Key && other.Modifiers == Modifiers;

    public override string ToString() => UsText;

    private static string Describe(Key key, ModifierKeys mods, bool layoutNames)
    {
        var parts = new List<string>(4);
        if ((mods & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((mods & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((mods & ModifierKeys.Shift) != 0) parts.Add("Shift");
        parts.Add(layoutNames && IsPunctuation(key) && Interop.Native.KeyLabel(key) is { } label ? label : UsName(key));
        return string.Join("+", parts);
    }

    private static bool IsPunctuation(Key key) => key is >= Key.Oem1 and <= Key.Oem102;

    // The names used in the README and parsed by TryParse. Letters, F1–F12, Home, End… are the key's own name.
    private static readonly (Key Key, string Name)[] Names =
    {
        (Key.OemPlus, "="), (Key.OemMinus, "-"), (Key.OemComma, ","), (Key.OemPeriod, "."), (Key.OemQuestion, "/"),
        (Key.OemOpenBrackets, "["), (Key.OemCloseBrackets, "]"), (Key.OemSemicolon, ";"), (Key.OemQuotes, "'"),
        (Key.Oem5, "\\"), (Key.OemTilde, "`"), (Key.Space, "Space"), (Key.Enter, "Enter"), (Key.Tab, "Tab"),
        (Key.Back, "Backspace"), (Key.Delete, "Del"), (Key.Escape, "Esc"), (Key.Prior, "Page Up"), (Key.Next, "Page Down"),
    };

    private static string UsName(Key key)
    {
        if (key is >= Key.D0 and <= Key.D9) return ((int)(key - Key.D0)).ToString(CultureInfo.InvariantCulture);
        foreach (var (k, name) in Names)
            if (k == key) return name;
        return key.ToString();
    }

    /// <summary>Reads "Ctrl+Shift+E", "Ctrl+Alt+=", "F9", "Shift+Tab"… (US key names, as <see cref="UsText"/> writes them).</summary>
    public static bool TryParse(string text, out KeyChord chord)
    {
        chord = null!;
        var rest = text.Trim();
        var mods = ModifierKeys.None;
        foreach (var (prefix, modifier) in new[] { ("Ctrl+", ModifierKeys.Control), ("Alt+", ModifierKeys.Alt), ("Shift+", ModifierKeys.Shift) })
        {
            // In this order only (Ctrl+Alt+Shift+K), as UsText writes them.
            if (rest.Length <= prefix.Length || !rest.StartsWith(prefix, StringComparison.Ordinal)) continue;
            mods |= modifier;
            rest = rest.Substring(prefix.Length);
        }
        Key key;
        if (rest.Length == 1 && rest[0] is >= '0' and <= '9') key = Key.D0 + (rest[0] - '0');
        else if (rest.Length == 1 && rest[0] is >= 'A' and <= 'Z') key = Key.A + (rest[0] - 'A');
        else if (Names.FirstOrDefault(n => n.Name == rest) is { Name: not null } named) key = named.Key;
        else if (rest.Length is 2 or 3 && rest[0] == 'F' && int.TryParse(rest.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out var f) && f is >= 1 and <= 12) key = Key.F1 + (f - 1);
        else if (rest is "Home" or "End" or "Left" or "Right" or "Up" or "Down" or "Insert") key = (Key)Enum.Parse(typeof(Key), rest);
        else return false;
        chord = new KeyChord(key, mods);
        return true;
    }
}

/// <summary>A command: in the command palette, on a shortcut, or both.</summary>
public sealed class AppCommand
{
    public AppCommand(string category, string title, Action execute, KeyChord? gesture = null, string? icon = null, string? keywords = null)
    {
        Category = category;
        Title = title;
        Execute = execute;
        Gesture = gesture;
        Icon = icon;
        Keywords = keywords ?? "";
    }

    public string Category { get; }
    public string Title { get; }
    public Action Execute { get; }
    public KeyChord? Gesture { get; }
    public string? Icon { get; }
    public string Keywords { get; }
    public Func<bool> CanExecute { get; init; } = () => true;

    /// <summary>The shortcut also works while the keyboard is in a text box.</summary>
    public bool WorksWhileTyping { get; init; }

    /// <summary>The shortcut also works while typing in the document.</summary>
    public bool WorksInDocument { get; init; }

    /// <summary>Shown in the palette (some shortcut-only commands aren't).</summary>
    public bool InPalette { get; init; } = true;
}
