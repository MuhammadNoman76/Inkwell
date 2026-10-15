using System.Text.RegularExpressions;
using System.Windows.Input;
using Inkwell.Views;

namespace Inkwell.Tests;

/// <summary>
/// The editor's keyboard shortcuts: no two commands share keys, every shortcut comes from the one table
/// (<see cref="Shortcuts"/>), nothing in the app writes a shortcut out by hand, and the README's keyboard table
/// says what the keys really do.
/// </summary>
public class ShortcutTests
{
    /// <summary>
    /// The editor's real command table. Building it only captures the editor in the commands' actions, so an
    /// editor whose constructor never ran (no window, no document) is enough.
    /// </summary>
    private static IReadOnlyList<AppCommand> Commands() => Sta.Run(() =>
    {
#if NETFRAMEWORK
        var editor = (EditorView)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(EditorView));
#else
        var editor = (EditorView)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(EditorView));
#endif
        return editor.Commands;
    });

    private static string Describe(AppCommand c) => $"{c.Category} › {c.Title} ({c.Gesture?.UsText})";

    [Fact]
    public void EveryKeyBinding_IsUnique()
    {
        var bound = Commands().Where(c => c.Gesture != null).ToList();
        Assert.True(bound.Count > 50, "The command table looks empty: " + bound.Count);
        var duplicates = bound
            .GroupBy(c => (c.Gesture!.Key, c.Gesture.Modifiers))
            .Where(g => g.Count() > 1)
            .Select(g => string.Join(" and ", g.Select(Describe)))
            .ToList();
        Assert.True(duplicates.Count == 0, "Keys bound twice: " + string.Join("; ", duplicates));
    }

    [Fact]
    public void NoCommand_TakesAKeyThePageHandles()
    {
        var page = Shortcuts.Document.All.ToList();
        var clashes = Commands().Where(c => c.Gesture != null && page.Any(k => k.SameKeys(c.Gesture))).Select(Describe).ToList();
        Assert.True(clashes.Count == 0, "These commands would take keys the page needs: " + string.Join("; ", clashes));
    }

    [Fact]
    public void EveryCommandKey_ComesFromTheTable_AndEveryTableKeyIsBoundOnce()
    {
        var commands = Commands();
        var table = Shortcuts.All;
        foreach (var c in commands.Where(c => c.Gesture != null))
            Assert.True(table.Any(t => ReferenceEquals(t.Chord, c.Gesture)), Describe(c) + " has keys that aren't in the Shortcuts table");
        foreach (var (name, chord) in table)
        {
            var users = commands.Where(c => ReferenceEquals(c.Gesture, chord)).ToList();
            Assert.True(users.Count == 1, $"Shortcuts.{name} ({chord.UsText}) is bound to {users.Count} commands");
        }
        var same = table.GroupBy(t => (t.Chord.Key, t.Chord.Modifiers)).Where(g => g.Count() > 1).Select(g => string.Join(" = ", g.Select(t => t.Name))).ToList();
        Assert.True(same.Count == 0, "The table has the same keys twice: " + string.Join("; ", same));
    }

    [Fact]
    public void TrackChanges_IsCtrlShiftE_AsInWord_AndExportHasItsOwnKeys()
    {
        var commands = Commands();
        var track = Assert.Single(commands, c => c.Title == "Track changes");
        Assert.Equal("Ctrl+Shift+E", track.Gesture!.UsText);
        var export = Assert.Single(commands, c => c.Title == "Export…");
        Assert.NotNull(export.Gesture);
        Assert.Equal("Ctrl+Shift+X", export.Gesture!.UsText);
        Assert.Single(commands, c => c.Gesture != null && c.Gesture.SameKeys(export.Gesture));
        // The menu button's tooltip says Alt+F: Alt+F opens the menu.
        Assert.Equal("Menu", Assert.Single(commands, c => c.Gesture != null && c.Gesture.UsText == "Alt+F").Title);
    }

    [Fact]
    public void KeyNames_AreReadBackAsTheSameKeys()
    {
        Assert.Equal("Ctrl+=", Shortcuts.Subscript.UsText);
        Assert.Equal("Ctrl+Shift+=", Shortcuts.Superscript.UsText);
        Assert.Equal("Ctrl+Alt+=", Shortcuts.ZoomIn.UsText);
        Assert.Equal("Ctrl+Alt+-", Shortcuts.ZoomOut.UsText);
        Assert.Equal("Ctrl+Alt+Shift+K", Shortcuts.SmallCaps.UsText);
        Assert.Equal("Ctrl+Enter", Shortcuts.PageBreak.UsText);
        foreach (var chord in Shortcuts.All.Select(t => t.Chord).Concat(Shortcuts.Document.All))
        {
            Assert.True(KeyChord.TryParse(chord.UsText, out var back), "Can't read " + chord.UsText);
            Assert.True(back.SameKeys(chord), chord.UsText + " reads back as " + back.UsText);
        }
        Assert.False(KeyChord.TryParse("Ctrl++", out _));
        Assert.False(KeyChord.TryParse("Ctrl+click", out _));
    }

    // ------------------------------------------------------------------ README

    [Fact]
    public void ReadmeKeyboardTable_MatchesTheBindings()
    {
        var readme = RepoFiles.Read("README.md");
        var start = readme.IndexOf("\n## Keyboard", StringComparison.Ordinal);
        Assert.True(start >= 0, "The README has no Keyboard section");
        var end = readme.IndexOf("\n## ", start + 5, StringComparison.Ordinal);
        var rows = readme.Substring(start, (end < 0 ? readme.Length : end) - start)
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("|", StringComparison.Ordinal) && !l.StartsWith("|---", StringComparison.Ordinal) && !l.StartsWith("| Keys", StringComparison.Ordinal))
            .ToList();
        Assert.True(rows.Count >= 20, "The keyboard table looks empty");

        var commands = Commands().Where(c => c.Gesture != null).ToList();
        var page = Shortcuts.Document.All.ToList();
        var problems = new List<string>();
        var checkedKeys = 0;
        foreach (var row in rows)
        {
            var cells = row.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            var keys = cells[0];
            var what = cells.Length > 1 ? cells[1] : "";
            if (keys.StartsWith("/", StringComparison.Ordinal)) continue; // "/ at the start of a word": slash commands, typed

            var keyGroups = SplitOutsideParentheses(keys, ", ");
            var whatGroups = SplitOutsideParentheses(what, ", ");
            for (var g = 0; g < keyGroups.Count; g++)
            {
                var chords = Alternatives(keyGroups[g]);
                var descriptions = keyGroups.Count == whatGroups.Count ? Alternatives(whatGroups[g]) : null;
                if (descriptions != null && chords.Count == 1) descriptions = new List<string> { whatGroups[g] };
                for (var i = 0; i < chords.Count; i++)
                {
                    if (!KeyChord.TryParse(chords[i], out var chord))
                    {
                        problems.Add($"\"{chords[i]}\" (row \"{row}\") isn't a key name the app uses");
                        continue;
                    }
                    checkedKeys++;
                    var command = commands.FirstOrDefault(c => c.Gesture!.SameKeys(chord));
                    if (command == null)
                    {
                        if (!page.Any(k => k.SameKeys(chord))) problems.Add($"{chords[i]} (row \"{row}\") does nothing in the editor");
                        continue;
                    }
                    var description = descriptions != null && descriptions.Count == chords.Count ? descriptions[i] : what;
                    if (!Words(command.Title).Overlaps(Words(description)))
                        problems.Add($"{chords[i]} is \"{command.Title}\", the README says \"{description}\"");
                }
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        Assert.True(checkedKeys >= 30, "Only " + checkedKeys + " keys were read from the table");
    }

    /// <summary>"Ctrl+B / I / U" → Ctrl+B, Ctrl+I, Ctrl+U; "Heading 1 / 2 / 3" → Heading 1, Heading 2, Heading 3.</summary>
    private static List<string> Alternatives(string text)
    {
        var parts = text.Split(new[] { " / " }, StringSplitOptions.None).Select(p => p.Trim()).ToList();
        for (var i = 1; i < parts.Count; i++)
        {
            if (parts[i].Contains(' ') || parts[i].Contains('+')) continue;
            var first = parts[0];
            var cut = Math.Max(first.LastIndexOf('+'), first.LastIndexOf(' '));
            if (cut > 0) parts[i] = first.Substring(0, cut + 1) + parts[i];
        }
        return parts;
    }

    private static List<string> SplitOutsideParentheses(string text, string separator)
    {
        var result = new List<string>();
        var depth = 0;
        var from = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')') depth--;
            else if (depth == 0 && string.CompareOrdinal(text, i, separator, 0, separator.Length) == 0)
            {
                result.Add(text.Substring(from, i - from).Trim());
                from = i + separator.Length;
            }
        }
        result.Add(text.Substring(from).Trim());
        return result;
    }

    private static readonly HashSet<string> Ignored = new(StringComparer.Ordinal) { "the", "a", "an", "or", "and", "on", "off", "to", "of", "with", "one", "in", "for" };

    private static HashSet<string> Words(string text) =>
        new(Regex.Matches(text.ToLowerInvariant(), "[a-z0-9]+").Cast<Match>().Select(m => m.Value).Where(w => !Ignored.Contains(w)));

    // ------------------------------------------------------------------ no shortcut written by hand

    private static readonly Regex Chord = new(
        @"(?<![A-Za-z])(?:(?:Ctrl|Alt|Shift)\+)+(?:F(?:1[0-2]|[1-9])(?![0-9])|Space|Enter|Tab|Del|Delete|Backspace|Esc|Home|End|Page ?Up|Page ?Down|Left|Right|Up|Down|[A-Z0-9](?![A-Za-z0-9])|[=\-\[\],./;'\\`+])",
        RegexOptions.CultureInvariant);

    private static readonly Regex FunctionKey = new(@"(?<![A-Za-z0-9+])F(?:1[0-2]|[1-9])(?![0-9A-Za-z])", RegexOptions.CultureInvariant);

    // String and character literals and comments of C#, in one pass (comments are skipped, literals checked).
    private static readonly Regex CSharpTokens = new(
        @"//[^\n]*|/\*.*?\*/|@""(?:[^""]|"""")*""|'(?:\\.|[^'\\])'|""(?:\\.|[^""\\\n])*""",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex XamlText = new(
        @"(?:ToolTip|Content|Text|Header|Title|InputGestureText|AutomationProperties\.Name)=""([^""]*)""",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Menus, tooltips, messages and descriptions take shortcut text from the Shortcuts table, so none can say a
    /// key does something it doesn't. Only the table and the key names (Commands.cs) spell keys out.
    /// </summary>
    [Fact]
    public void NoShortcut_IsWrittenOutsideTheTable()
    {
        var allowed = new[] { "src/Inkwell/Views/Shortcuts.cs", "src/Inkwell/Views/Commands.cs" };
        var found = new List<string>();
        foreach (var file in RepoFiles.Files("src/Inkwell", "*.cs"))
        {
            var name = RepoFiles.Relative(file);
            if (allowed.Contains(name)) continue;
            var text = RepoFiles.Read(name).Replace("\r\n", "\n");
            foreach (Match token in CSharpTokens.Matches(text))
            {
                if (!token.Value.StartsWith("\"", StringComparison.Ordinal) && !token.Value.StartsWith("@\"", StringComparison.Ordinal)) continue;
                Check(name, LineOf(text, token.Index), token.Value);
            }
        }
        foreach (var file in RepoFiles.Files("src/Inkwell", "*.xaml"))
        {
            var name = RepoFiles.Relative(file);
            var text = Regex.Replace(RepoFiles.Read(name).Replace("\r\n", "\n"), "<!--.*?-->", "", RegexOptions.Singleline);
            foreach (Match attribute in XamlText.Matches(text)) Check(name, LineOf(text, attribute.Index), attribute.Groups[1].Value);
        }
        Assert.True(found.Count == 0, "Shortcuts written by hand (use Shortcuts.X.Text):\n" + string.Join("\n", found));

        void Check(string file, int line, string literal)
        {
            foreach (Match m in Chord.Matches(literal)) found.Add($"{file}:{line}: {m.Value} in {literal}");
            // A lone function key in a sentence ("press F7"); a single word such as the number format "F1" isn't one.
            if (literal.Contains(' '))
                foreach (Match m in FunctionKey.Matches(literal)) found.Add($"{file}:{line}: {m.Value} in {literal}");
        }
    }

    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index; i++)
            if (text[i] == '\n') line++;
        return line;
    }
}
