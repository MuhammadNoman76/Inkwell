using System.Windows.Input;

namespace Inkwell.Views;

/// <summary>A keyboard shortcut.</summary>
public sealed class KeyChord
{
    public KeyChord(Key key, ModifierKeys modifiers = ModifierKeys.None, string? text = null)
    {
        Key = key;
        Modifiers = modifiers;
        Text = text ?? Describe(key, modifiers);
    }

    public Key Key { get; }
    public ModifierKeys Modifiers { get; }
    public string Text { get; }

    public bool Matches(Key key, ModifierKeys modifiers) => key == Key && modifiers == Modifiers;

    private static string Describe(Key key, ModifierKeys mods)
    {
        var parts = new List<string>();
        if ((mods & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((mods & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((mods & ModifierKeys.Shift) != 0) parts.Add("Shift");
        parts.Add(key switch
        {
            Key.OemPlus => "+",
            Key.OemMinus => "-",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Key.OemQuestion => "/",
            Key.Oem5 => "\\",
            Key.OemSemicolon => ";",
            Key.Space => "Space",
            Key.Return => "Enter",
            Key.Next => "Page Down",
            Key.Prior => "Page Up",
            Key.Delete => "Del",
            _ => key.ToString(),
        });
        return string.Join("+", parts);
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

