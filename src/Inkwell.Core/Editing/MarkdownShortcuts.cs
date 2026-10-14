using System.Globalization;
using System.Text.RegularExpressions;

namespace Inkwell.Core.Editing;

public enum BlockShortcutKind
{
    /// <summary>"# " … "###### ": a heading (<see cref="BlockShortcut.Level"/> 1–6).</summary>
    Heading,

    /// <summary>"- ", "* " or "+ ": a bulleted list.</summary>
    BulletList,

    /// <summary>"1. " or "1) ": a numbered list starting at <see cref="BlockShortcut.StartNumber"/>.</summary>
    NumberedList,

    /// <summary>"> ": a quote.</summary>
    Quote,

    /// <summary>"```" (optionally with a language) then space or Enter: a code block.</summary>
    CodeBlock,

    /// <summary>"---" then space or Enter: a divider (horizontal rule).</summary>
    Divider,
}

/// <summary>A Markdown paragraph shortcut: remove [0, <see cref="RemoveLength"/>) and turn the paragraph into <see cref="Kind"/>.</summary>
public sealed class BlockShortcut
{
    public BlockShortcutKind Kind { get; init; }

    /// <summary>Heading level 1–6.</summary>
    public int Level { get; init; }

    /// <summary>The first number of a numbered list ("3. " → 3).</summary>
    public int StartNumber { get; init; } = 1;

    /// <summary>"." or ")" after the number.</summary>
    public string? NumberSuffix { get; init; }

    /// <summary>The bullet typed: '-', '*' or '+'.</summary>
    public char Bullet { get; init; }

    /// <summary>The code block's language ("```cs" → "cs"), or null.</summary>
    public string? CodeLanguage { get; init; }

    /// <summary>How many characters to remove from the start of the paragraph (the marker and the space typed).</summary>
    public int RemoveLength { get; init; }

    /// <summary>The shortcut was completed with Enter (the editor shouldn't also split the paragraph there).</summary>
    public bool ByEnter { get; init; }

    public string Description { get; init; } = "";

    public string RuleId => AutoCorrectRules.MarkdownBlocks;
}

public enum InlineFormat { Bold, Italic, Code, Strikethrough }

/// <summary>
/// A Markdown inline shortcut: remove the closing marker [CloseStart, CloseStart + CloseLength), then the opening one
/// [OpenStart, OpenStart + OpenLength), and format the content, which then spans [OpenStart, OpenStart + ContentLength).
/// </summary>
public sealed class InlineShortcut
{
    public InlineFormat Format { get; init; }
    public int OpenStart { get; init; }
    public int OpenLength { get; init; }
    public int CloseStart { get; init; }
    public int CloseLength { get; init; }
    public int ContentStart => OpenStart + OpenLength;
    public int ContentLength => CloseStart - ContentStart;
    public string Description { get; init; } = "";
    public string RuleId => AutoCorrectRules.MarkdownInline;

    /// <summary>The text after removing the markers (the caller then formats [OpenStart, OpenStart + ContentLength)).</summary>
    public string Apply(string text) =>
        text.Substring(0, OpenStart) + text.Substring(ContentStart, ContentLength) + text.Substring(CloseStart + CloseLength);
}

/// <summary>Detects Markdown shortcuts as they are typed (used by <see cref="AutoCorrect"/>; callable on their own).</summary>
public static class MarkdownShortcuts
{
    private static readonly Regex Numbered = new(@"^(\d{1,3})([.)]) $", RegexOptions.CultureInvariant);
    private static readonly Regex Fence = new(@"^```([A-Za-z0-9_#+.\-]*)$", RegexOptions.CultureInvariant);

    /// <summary>
    /// A paragraph shortcut completed by the space (or Enter) just typed at the start of the paragraph: the text
    /// before the caret must be exactly the marker. <paramref name="text"/> is the paragraph after the keystroke;
    /// for Enter ('\r' / '\n') it doesn't contain it.
    /// </summary>
    public static BlockShortcut? DetectBlock(string text, int caret, char typed, AutoCorrectContext? context = null)
    {
        context ??= new AutoCorrectContext();
        if (context.InCode || context.InField || caret > text.Length) return null;
        var enter = typed is '\r' or '\n';
        if (enter)
        {
            var whole = text.Substring(0, caret);
            if (caret != text.Length) return null;
            if (whole == "---") return new BlockShortcut { Kind = BlockShortcutKind.Divider, RemoveLength = 3, ByEnter = true, Description = "Inserted a divider" };
            var fence = Fence.Match(whole);
            if (fence.Success)
                return new BlockShortcut
                {
                    Kind = BlockShortcutKind.CodeBlock, CodeLanguage = fence.Groups[1].Value.Length > 0 ? fence.Groups[1].Value : null,
                    RemoveLength = caret, ByEnter = true, Description = "Started a code block",
                };
            return null;
        }
        if (typed != ' ' || caret < 2 || text[caret - 1] != ' ') return null;
        var marker = text.Substring(0, caret - 1);
        var prefix = text.Substring(0, caret);

        if (marker.Length is >= 1 and <= 6 && marker.All(c => c == '#'))
            return new BlockShortcut { Kind = BlockShortcutKind.Heading, Level = marker.Length, RemoveLength = caret, Description = "Made a heading " + marker.Length };
        if (marker == ">")
            return new BlockShortcut { Kind = BlockShortcutKind.Quote, RemoveLength = caret, Description = "Made a quote" };
        if (marker == "---" && caret == text.Length)
            return new BlockShortcut { Kind = BlockShortcutKind.Divider, RemoveLength = caret, Description = "Inserted a divider" };
        var fenceSpace = Fence.Match(marker);
        if (fenceSpace.Success)
            return new BlockShortcut
            {
                Kind = BlockShortcutKind.CodeBlock, CodeLanguage = fenceSpace.Groups[1].Value.Length > 0 ? fenceSpace.Groups[1].Value : null,
                RemoveLength = caret, Description = "Started a code block",
            };
        if (context.IsListItem) return null;
        if (marker is "-" or "*" or "+")
            return new BlockShortcut { Kind = BlockShortcutKind.BulletList, Bullet = marker[0], RemoveLength = caret, Description = "Started a bulleted list" };
        var numbered = Numbered.Match(prefix);
        if (numbered.Success)
        {
            var start = int.Parse(numbered.Groups[1].Value, CultureInfo.InvariantCulture);
            return new BlockShortcut
            {
                Kind = BlockShortcutKind.NumberedList, StartNumber = start, NumberSuffix = numbered.Groups[2].Value, RemoveLength = caret,
                Description = "Started a numbered list",
            };
        }
        return null;
    }

    /// <summary>
    /// An inline shortcut completed by the marker character just typed: **bold**, __bold__, *italic*, _italic_,
    /// `code`, ~~strikethrough~~. The content can't start or end with a space; "_" and "*" don't open inside a word
    /// (snake_case, 2*3*4), and "_" doesn't close before a letter or digit.
    /// </summary>
    public static InlineShortcut? DetectInline(string text, int caret, char typed)
    {
        if (caret < 3 || caret > text.Length || text[caret - 1] != typed) return null;
        switch (typed)
        {
            case '*':
            case '_':
                return Pair(text, caret, typed, InlineFormat.Bold, "Made the text bold") ?? Single(text, caret, typed);
            case '~':
                return Pair(text, caret, typed, InlineFormat.Strikethrough, "Struck through the text");
            case '`':
                return Code(text, caret);
            default:
                return null;
        }
    }

    private static bool Blank(char c) => char.IsWhiteSpace(c);

    /// <summary>A doubled marker: **x**, __x__, ~~x~~.</summary>
    private static InlineShortcut? Pair(string text, int caret, char m, InlineFormat format, string description)
    {
        var close = caret - 2;
        if (close < 3 || text[close] != m) return null;
        if (Blank(text[close - 1]) || text[close - 1] == m) return null;
        if (m == '_' && caret < text.Length && char.IsLetterOrDigit(text[caret])) return null;
        for (var open = close - 3; open >= 0; open--)
        {
            if (text[open] == '\v' || text[open] == '\n') return null;
            if (text[open] != m || text[open + 1] != m) continue;
            if (open > 0 && (text[open - 1] == m || (m != '~' && char.IsLetterOrDigit(text[open - 1])))) continue;
            if (Blank(text[open + 2])) continue;
            if (open + 2 >= close) continue;
            return new InlineShortcut { Format = format, OpenStart = open, OpenLength = 2, CloseStart = close, CloseLength = 2, Description = description };
        }
        return null;
    }

    /// <summary>A single marker: *x* or _x_.</summary>
    private static InlineShortcut? Single(string text, int caret, char m)
    {
        var close = caret - 1;
        if (text[close - 1] == m || Blank(text[close - 1])) return null;
        if (m == '_' && caret < text.Length && char.IsLetterOrDigit(text[caret])) return null;
        for (var open = close - 2; open >= 0; open--)
        {
            if (text[open] == '\v' || text[open] == '\n') return null;
            if (text[open] != m) continue;
            if (text[open + 1] == m) continue;
            if (open > 0 && (text[open - 1] == m || char.IsLetterOrDigit(text[open - 1]))) continue;
            if (Blank(text[open + 1])) continue;
            return new InlineShortcut { Format = InlineFormat.Italic, OpenStart = open, OpenLength = 1, CloseStart = close, CloseLength = 1, Description = "Made the text italic" };
        }
        return null;
    }

    private static InlineShortcut? Code(string text, int caret)
    {
        var close = caret - 1;
        if (text[close - 1] == '`') return null;
        for (var open = close - 2; open >= 0; open--)
        {
            if (text[open] == '\v' || text[open] == '\n') return null;
            if (text[open] != '`') continue;
            if (open > 0 && text[open - 1] == '`') return null;
            if (text.Substring(open + 1, close - open - 1).Trim().Length == 0) return null;
            return new InlineShortcut { Format = InlineFormat.Code, OpenStart = open, OpenLength = 1, CloseStart = close, CloseLength = 1, Description = "Formatted the text as code" };
        }
        return null;
    }
}
