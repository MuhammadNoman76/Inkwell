using System.Reflection;
using System.Windows.Input;

namespace Inkwell.Views;

/// <summary>
/// Every keyboard shortcut in one table. The command table (EditorView.Commands) binds these, and menus, tooltips,
/// the palette and messages show their <see cref="KeyChord.Text"/>: no shortcut is written out anywhere else, so what
/// the app says a key does is what it does. The tests check that no two commands share keys, that every command's
/// keys come from here and that the README's keyboard table matches.
/// </summary>
public static class Shortcuts
{
    private const ModifierKeys None = ModifierKeys.None, C = ModifierKeys.Control, S = ModifierKeys.Shift, A = ModifierKeys.Alt;

    // ---------------------------------------------------------------- commands (EditorView.Commands)

    // File and app
    public static readonly KeyChord Save = new(Key.S, C);
    public static readonly KeyChord SaveAs = new(Key.S, C | S);
    public static readonly KeyChord SaveAsF12 = new(Key.F12);
    public static readonly KeyChord Open = new(Key.O, C);
    public static readonly KeyChord NewDocument = new(Key.N, C);
    /// <summary>Ctrl+Shift+E is track changes, as in Word.</summary>
    public static readonly KeyChord Export = new(Key.X, C | S);
    public static readonly KeyChord Print = new(Key.P, C);
    public static readonly KeyChord Close = new(Key.W, C);
    public static readonly KeyChord Settings = new(Key.OemComma, C);
    public static readonly KeyChord Menu = new(Key.F, A);

    // Edit
    public static readonly KeyChord Undo = new(Key.Z, C);
    public static readonly KeyChord Redo = new(Key.Y, C);
    public static readonly KeyChord RedoShiftZ = new(Key.Z, C | S);
    public static readonly KeyChord Cut = new(Key.X, C);
    public static readonly KeyChord Copy = new(Key.C, C);
    public static readonly KeyChord Paste = new(Key.V, C);
    public static readonly KeyChord PasteTextOnly = new(Key.V, C | S);
    public static readonly KeyChord PasteKeepSource = new(Key.V, C | A);
    public static readonly KeyChord SelectAll = new(Key.A, C);
    public static readonly KeyChord Find = new(Key.F, C);
    public static readonly KeyChord Replace = new(Key.H, C);
    public static readonly KeyChord GoToPage = new(Key.G, C);
    public static readonly KeyChord CopyFormatting = new(Key.C, C | S);
    public static readonly KeyChord CommandPalette = new(Key.K, C);

    // Format
    public static readonly KeyChord Bold = new(Key.B, C);
    public static readonly KeyChord Italic = new(Key.I, C);
    public static readonly KeyChord Underline = new(Key.U, C);
    public static readonly KeyChord Strikethrough = new(Key.D5, C | S);
    public static readonly KeyChord Superscript = new(Key.OemPlus, C | S);
    public static readonly KeyChord Subscript = new(Key.OemPlus, C);
    public static readonly KeyChord SmallCaps = new(Key.K, C | A | S);
    public static readonly KeyChord BiggerText = new(Key.OemCloseBrackets, C);
    public static readonly KeyChord SmallerText = new(Key.OemOpenBrackets, C);
    public static readonly KeyChord BiggerTextShift = new(Key.OemPeriod, C | S);
    public static readonly KeyChord SmallerTextShift = new(Key.OemComma, C | S);
    public static readonly KeyChord ClearCharacterFormatting = new(Key.Space, C);
    public static readonly KeyChord Highlight = new(Key.H, C | A);
    public static readonly KeyChord ChangeCase = new(Key.F3, S);
    public static readonly KeyChord RevealFormatting = new(Key.F1, S);

    // Paragraph
    public static readonly KeyChord AlignLeft = new(Key.L, C);
    public static readonly KeyChord Centre = new(Key.E, C);
    public static readonly KeyChord AlignRight = new(Key.R, C);
    public static readonly KeyChord Justify = new(Key.J, C);
    public static readonly KeyChord RightToLeft = new(Key.OemPeriod, C | A);
    public static readonly KeyChord LeftToRight = new(Key.OemComma, C | A);
    public static readonly KeyChord SingleSpacing = new(Key.D1, C);
    public static readonly KeyChord OneAndHalfSpacing = new(Key.D5, C);
    public static readonly KeyChord DoubleSpacing = new(Key.D2, C);
    public static readonly KeyChord IncreaseIndent = new(Key.M, C);
    public static readonly KeyChord DecreaseIndent = new(Key.M, C | S);
    public static readonly KeyChord Bullets = new(Key.L, C | S);
    public static readonly KeyChord BodyText = new(Key.N, C | S);
    public static readonly KeyChord Heading1 = new(Key.D1, C | A);
    public static readonly KeyChord Heading2 = new(Key.D2, C | A);
    public static readonly KeyChord Heading3 = new(Key.D3, C | A);

    // Insert
    public static readonly KeyChord Link = new(Key.K, C | S);
    public static readonly KeyChord Comment = new(Key.M, C | A);
    public static readonly KeyChord Footnote = new(Key.F, C | A);
    public static readonly KeyChord Endnote = new(Key.D, C | A);
    public static readonly KeyChord PageBreak = new(Key.Enter, C);
    public static readonly KeyChord ColumnBreak = new(Key.Enter, C | S);
    public static readonly KeyChord UpdateFields = new(Key.F9);
    public static readonly KeyChord Bookmark = new(Key.F5, C | S);
    public static readonly KeyChord Date = new(Key.D, A | S);

    // Review
    public static readonly KeyChord TrackChanges = new(Key.E, C | S);
    public static readonly KeyChord WordCount = new(Key.G, C | S);
    public static readonly KeyChord CheckSpelling = new(Key.F7);

    // View
    public static readonly KeyChord FocusMode = new(Key.F11);
    public static readonly KeyChord FormattingMarks = new(Key.D8, C | S);
    public static readonly KeyChord Panel = new(Key.F1, C);
    public static readonly KeyChord ZoomIn = new(Key.OemPlus, C | A);
    public static readonly KeyChord ZoomOut = new(Key.OemMinus, C | A);
    public static readonly KeyChord FitPageWidth = new(Key.D0, C);

    // ---------------------------------------------------------------- keys the page handles itself

    /// <summary>Keys the page surface handles itself (DocumentView.OnKeyDown): no command may take them.</summary>
    public static class Document
    {
        public static readonly KeyChord LineBreak = new(Key.Enter, S);
        public static readonly KeyChord Tab = new(Key.Tab);
        public static readonly KeyChord ShiftTab = new(Key.Tab, S);
        public static readonly KeyChord Delete = new(Key.Delete);

        /// <summary>Every key combination DocumentView.OnKeyDown acts on (Ctrl+Enter and Ctrl+Shift+Enter are commands).</summary>
        public static IEnumerable<KeyChord> All
        {
            get
            {
                foreach (var key in new[] { Key.Left, Key.Right, Key.Up, Key.Down, Key.Home, Key.End, Key.PageUp, Key.PageDown })
                    foreach (var mods in new[] { None, S, C, C | S })
                        yield return new KeyChord(key, mods);
                yield return new KeyChord(Key.Up, A);
                yield return new KeyChord(Key.Down, A);
                yield return new KeyChord(Key.Back);
                yield return new KeyChord(Key.Back, C);
                yield return Delete;
                yield return new KeyChord(Key.Delete, C);
                yield return new KeyChord(Key.Enter);
                yield return LineBreak;
                yield return Tab;
                yield return ShiftTab;
                yield return new KeyChord(Key.Escape);
            }
        }
    }

    // ---------------------------------------------------------------- keys of one text box

    /// <summary>Keys that only work in one box (they never reach the commands while it has the keyboard).</summary>
    public static class Local
    {
        /// <summary>The find box: the previous match (Enter is the next one).</summary>
        public static readonly KeyChord FindPrevious = new(Key.Enter, S);

        /// <summary>The replace box: replace every match.</summary>
        public static readonly KeyChord ReplaceAll = new(Key.Enter, C);

        /// <summary>A comment or reply box: post it.</summary>
        public static readonly KeyChord PostComment = new(Key.Enter, C);
    }

    // ---------------------------------------------------------------- text

    /// <summary>"Bold (Ctrl+B)"; several chords are joined with " / ".</summary>
    public static string Tip(string label, params KeyChord[] chords) =>
        chords.Length == 0 ? label : label + " (" + string.Join(" / ", chords.Select(c => c.Text)) + ")";

    /// <summary>Every command chord in the table above (not the page's or the boxes' own keys), with its field name.</summary>
    public static IReadOnlyList<(string Name, KeyChord Chord)> All { get; } =
        typeof(Shortcuts).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(KeyChord))
            .Select(f => (f.Name, (KeyChord)f.GetValue(null)!))
            .ToList();
}
