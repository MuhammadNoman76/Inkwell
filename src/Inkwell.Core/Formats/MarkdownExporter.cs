using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Inkwell.Core.Model;

namespace Inkwell.Core.Formats;

/// <summary>Options for <see cref="MarkdownExporter"/>.</summary>
public sealed class MarkdownExportOptions
{
    /// <summary>
    /// Folder the pictures are written to (created when needed). When null, pictures are left out and their alt
    /// text is written instead. <see cref="MarkdownExporter.ExportToFile"/> uses a "&lt;name&gt;_files" folder next to the file.
    /// </summary>
    public string? ImageFolder { get; set; }

    /// <summary>How the Markdown refers to <see cref="ImageFolder"/> (a relative path); the folder's name by default.</summary>
    public string? ImageFolderReference { get; set; }

    /// <summary>Line ending ("\n" by default, as Markdown tools expect).</summary>
    public string NewLine { get; set; } = "\n";
}

/// <summary>
/// Markdown export (CommonMark + GitHub tables, strikethrough, task lists and footnotes):
/// <list type="bullet">
/// <item>Heading N styles (or outline levels) → "#"…"######"; Title → "#" (Markdown has no title); Subtitle and other
/// styles → paragraphs.</item>
/// <item>Lists from the numbering (bullet / numbered with their numbers, nesting by level; list items whose text
/// starts with ☐ / ☑ → task items); bold, italic, strikethrough, inline code; underline, superscript and subscript as
/// &lt;u&gt;, &lt;sup&gt;, &lt;sub&gt;; links; line breaks as "\".</item>
/// <item>Quote → "&gt;", Code → fenced blocks, dividers → "---", pipe tables (merged cells flattened, several
/// paragraphs in a cell joined with &lt;br&gt;), footnotes and endnotes → "[^n]".</item>
/// <item>Pictures are written to a folder when one is given (otherwise their alt text is written).</item>
/// </list>
/// Markdown's special characters are escaped, so importing the result gives back the same document structure.
/// </summary>
public static class MarkdownExporter
{
    /// <summary>The document as Markdown.</summary>
    public static string Export(Document doc, MarkdownExportOptions? options = null) => ExportBlocks(doc, doc.Body.Blocks, options);

    /// <summary>Some blocks of a document as Markdown (for copying).</summary>
    public static string ExportBlocks(Document doc, IEnumerable<Block> blocks, MarkdownExportOptions? options = null)
    {
        var writer = new MarkdownWriter(doc, options ?? new MarkdownExportOptions());
        writer.WriteBlocks(blocks);
        writer.WriteNotes();
        return writer.Result();
    }

    /// <summary>Saves the document as a Markdown file (UTF-8), with its pictures in a "&lt;name&gt;_files" folder next to it.</summary>
    public static void ExportToFile(Document doc, string path, MarkdownExportOptions? options = null)
    {
        var full = Path.GetFullPath(path);
        var name = Path.GetFileNameWithoutExtension(full) + "_files";
        var copy = new MarkdownExportOptions
        {
            NewLine = options?.NewLine ?? "\n",
            ImageFolder = options?.ImageFolder ?? Path.Combine(Path.GetDirectoryName(full) ?? ".", name),
            ImageFolderReference = options?.ImageFolderReference ?? (options?.ImageFolder == null ? name : null),
        };
        File.WriteAllText(full, Export(doc, copy), new UTF8Encoding(false));
    }
}

internal sealed class MarkdownWriter
{
    private readonly Document _doc;
    private readonly MarkdownExportOptions _options;
    private readonly ListTracker _lists;
    private readonly List<string> _out = new();
    private readonly List<Note> _notes = new();
    private readonly Dictionary<Note, int> _noteNumbers = new(ReferenceEqualityComparer<Note>.Instance);
    private readonly Dictionary<string, string> _imageFiles = new(StringComparer.Ordinal);
    private (bool Ordered, char Marker)? _previousList;

    public MarkdownWriter(Document doc, MarkdownExportOptions options)
    {
        _doc = doc;
        _options = options;
        _lists = new ListTracker(doc);
    }

    public string Result()
    {
        while (_out.Count > 0 && _out[_out.Count - 1].Length == 0) _out.RemoveAt(_out.Count - 1);
        return _out.Count == 0 ? "" : string.Join(_options.NewLine, _out) + _options.NewLine;
    }

    private void Emit(List<string> lines, (bool Ordered, char Marker)? list = null)
    {
        if (lines.Count == 0) return;
        if (_out.Count > 0) _out.Add("");
        _out.AddRange(lines);
        _previousList = list;
    }

    // ------------------------------------------------------------------ blocks

    private static IEnumerable<Block> Flatten(IEnumerable<Block> blocks)
    {
        foreach (var b in blocks)
        {
            switch (b)
            {
                case Paragraph:
                case Table:
                    yield return b;
                    break;
                case SdtBlock s:
                    foreach (var inner in Flatten(s.Blocks)) yield return inner;
                    break;
                case FieldBlock f:
                    foreach (var inner in Flatten(f.Blocks)) yield return inner;
                    break;
                case OpaqueBlock { Display: { } display }:
                    foreach (var inner in Flatten(display)) yield return inner;
                    break;
            }
        }
    }

    private enum Kind { Empty, Normal, Heading, Code, Quote, Divider, List }

    private Kind Classify(Paragraph p)
    {
        if (Content.IsDivider(p)) return Kind.Divider;
        if (HeadingLevel(p) > 0) return Kind.Heading;
        if (IsList(p)) return Kind.List;
        if (IsCode(p)) return Kind.Code;
        if (IsEmpty(p)) return Kind.Empty;
        if (IsQuote(p)) return Kind.Quote;
        return Kind.Normal;
    }

    private bool IsList(Paragraph p) => _lists.ListOf(p) is { } l && _doc.Numbering.Level(l.NumId, l.Level) != null;

    private static bool IsEmpty(Paragraph p) =>
        p.Inlines.All(i => i.Deleted != null || i.Length == 0 && i is not DrawingObject || i is TextRun t && t.Text.All(c => c is TextRun.PageBreak or TextRun.ColumnBreak or ' '));

    private int HeadingLevel(Paragraph p)
    {
        var styleId = p.Props.StyleId ?? _doc.Styles.DefaultParagraphStyleId;
        if (string.Equals(styleId, "Title", StringComparison.Ordinal) || StyleChain.NameOf(_doc.Styles, styleId) == "title") return 1;
        return Math.Min(6, StyleChain.HeadingLevel(_doc, p));
    }

    private bool IsCode(Paragraph p)
    {
        var styleId = p.Props.StyleId;
        if (styleId == null) return false;
        return _doc.Styles.Chain(styleId).Any(s => s.Id == "Code" || s.Name.Equals("Code", StringComparison.OrdinalIgnoreCase) ||
                                                    s.Name.Equals("HTML Preformatted", StringComparison.OrdinalIgnoreCase));
    }

    private bool IsQuote(Paragraph p)
    {
        var styleId = p.Props.StyleId;
        if (styleId == null) return false;
        return _doc.Styles.Chain(styleId).Any(s => s.Id is "Quote" or "IntenseQuote" || s.Name.Equals("Quote", StringComparison.OrdinalIgnoreCase) ||
                                                    s.Name.Equals("Intense Quote", StringComparison.OrdinalIgnoreCase));
    }

    private bool IsListParagraphStyle(Paragraph p)
    {
        var styleId = p.Props.StyleId;
        return styleId != null && _doc.Styles.Chain(styleId).Any(s => s.Id == "ListParagraph" || s.Name.Equals("List Paragraph", StringComparison.OrdinalIgnoreCase));
    }

    public void WriteBlocks(IEnumerable<Block> blocks)
    {
        var items = Flatten(blocks).ToList();
        var i = 0;
        while (i < items.Count)
        {
            if (items[i] is Table t)
            {
                Emit(RenderTable(t));
                i++;
                continue;
            }
            var p = (Paragraph)items[i];
            switch (Classify(p))
            {
                case Kind.Empty:
                    i++;
                    break;
                case Kind.Divider:
                    Emit(new List<string> { "---" });
                    i++;
                    break;
                case Kind.Heading:
                    _lists.Next(p);
                    Emit(new List<string> { new string('#', HeadingLevel(p)) + " " + string.Join(" ", RenderInlines(p, " ")).Trim() });
                    i++;
                    break;
                case Kind.Code:
                {
                    var lines = new List<string>();
                    var code = new List<Paragraph>();
                    while (i < items.Count && items[i] is Paragraph c && Classify(c) == Kind.Code && c.Props.IndentStart == null)
                    {
                        code.Add(c);
                        i++;
                    }
                    if (code.Count == 0)
                    {
                        code.Add(p);
                        i++;
                    }
                    lines.AddRange(Fence(code));
                    Emit(lines);
                    break;
                }
                case Kind.Quote:
                {
                    var lines = new List<string>();
                    while (i < items.Count && items[i] is Paragraph q && Classify(q) == Kind.Quote)
                    {
                        if (lines.Count > 0) lines.Add(">");
                        foreach (var line in RenderInlines(q, null)) lines.Add(line.Length == 0 ? ">" : "> " + line);
                        i++;
                    }
                    Emit(lines);
                    break;
                }
                case Kind.List:
                {
                    var lines = RenderList(items, ref i, out var marker);
                    Emit(lines, null);
                    _previousList = marker;
                    break;
                }
                default:
                    _lists.Next(p);
                    Emit(RenderInlines(p, null));
                    i++;
                    break;
            }
        }
    }

    private List<string> Fence(List<Paragraph> paragraphs)
    {
        var text = new List<string>();
        foreach (var p in paragraphs)
        {
            _lists.Next(p);
            text.AddRange(PlainText(p).Split('\n'));
        }
        var longest = 0;
        foreach (var line in text)
        {
            var run = 0;
            foreach (var c in line)
            {
                run = c == '`' ? run + 1 : 0;
                longest = Math.Max(longest, run);
            }
        }
        var fence = new string('`', Math.Max(3, longest + 1));
        var lines = new List<string> { fence };
        lines.AddRange(text);
        lines.Add(fence);
        return lines;
    }

    private sealed class LevelState
    {
        public int NumId;
        public bool Ordered;
        public char Marker;
        public int ContentIndent;
    }

    private List<string> RenderList(List<Block> items, ref int i, out (bool Ordered, char Marker)? topMarker)
    {
        var lines = new List<string>();
        var stack = new List<LevelState>();
        var closed = new Dictionary<int, (bool Ordered, char Marker)>();
        var needBlank = false;
        topMarker = null;
        while (i < items.Count && items[i] is Paragraph p)
        {
            var kind = Classify(p);
            if (kind == Kind.List)
            {
                var label = _lists.Next(p)!;
                var level = Math.Min(label.Level, stack.Count);
                var ordered = label.IsOrdered;
                var sibling = false;
                if (stack.Count > level)
                {
                    for (var d = stack.Count - 1; d > level; d--) closed.Remove(d);
                    stack.RemoveRange(level + 1, stack.Count - level - 1);
                    var current = stack[level];
                    if (current.NumId != label.NumId || current.Ordered != ordered)
                    {
                        closed[level] = (current.Ordered, current.Marker);
                        stack.RemoveAt(level);
                        sibling = true;
                    }
                }
                if (stack.Count == level)
                {
                    var marker = ordered ? '.' : '-';
                    var previous = closed.TryGetValue(level, out var c) ? c : level == 0 && lines.Count == 0 ? _previousList : null;
                    if (previous is { } prev && prev.Ordered == ordered && prev.Marker == marker) marker = ordered ? ')' : '*';
                    stack.Add(new LevelState { NumId = label.NumId, Ordered = ordered, Marker = marker });
                    if (level == 0) topMarker = (ordered, marker);
                    // A list that follows another one, or a numbered list not starting at 1, can't interrupt the line
                    // before it in Markdown (it would continue that paragraph): separate it with a blank line.
                    if (lines.Count > 0 && (sibling || (ordered && label.Value != 1))) needBlank = true;
                }
                var state = stack[level];
                var indent = level == 0 ? 0 : stack[level - 1].ContentIndent;
                var bullet = ordered ? label.Value.ToString(CultureInfo.InvariantCulture) + state.Marker : state.Marker.ToString();
                state.ContentIndent = indent + bullet.Length + 1;
                var body = RenderInlines(p, null);
                var task = "";
                if (!ordered && body.Count > 0)
                {
                    var first = body[0];
                    if (first.StartsWith("☐ ", StringComparison.Ordinal) || first == "☐") task = "[ ] ";
                    else if (first.StartsWith("☑ ", StringComparison.Ordinal) || first.StartsWith("☒ ", StringComparison.Ordinal) || first == "☑" || first == "☒") task = "[x] ";
                    if (task.Length > 0) body[0] = first.Length > 2 ? first.Substring(2) : "";
                }
                if (needBlank) lines.Add("");
                needBlank = false;
                var pad = new string(' ', indent);
                lines.Add((pad + bullet + " " + task + (body.Count > 0 ? body[0] : "")).TrimEnd());
                var contentPad = new string(' ', state.ContentIndent);
                for (var k = 1; k < body.Count; k++) lines.Add(contentPad + body[k]);
                i++;
                continue;
            }
            if (stack.Count > 0 && kind != Kind.Heading && kind != Kind.Divider && IsContinuation(p, stack.Count - 1, out var contLevel))
            {
                var pad = new string(' ', stack[contLevel].ContentIndent);
                var block = new List<string>();
                if (kind == Kind.Code)
                {
                    var code = new List<Paragraph>();
                    var indentValue = p.Props.IndentStart;
                    while (i < items.Count && items[i] is Paragraph c && Classify(c) == Kind.Code && c.Props.IndentStart == indentValue)
                    {
                        code.Add(c);
                        i++;
                    }
                    block.AddRange(Fence(code));
                }
                else if (kind == Kind.Quote)
                {
                    var indentValue = p.Props.IndentStart;
                    while (i < items.Count && items[i] is Paragraph q && Classify(q) == Kind.Quote && q.Props.IndentStart == indentValue)
                    {
                        if (block.Count > 0) block.Add(">");
                        foreach (var line in RenderInlines(q, null)) block.Add(line.Length == 0 ? ">" : "> " + line);
                        _lists.Next(q);
                        i++;
                    }
                }
                else
                {
                    _lists.Next(p);
                    if (kind != Kind.Empty) block.AddRange(RenderInlines(p, null));
                    i++;
                }
                if (block.Count > 0)
                {
                    lines.Add("");
                    foreach (var line in block) lines.Add(line.Length == 0 ? "" : pad + line);
                    needBlank = true;
                }
                continue;
            }
            break;
        }
        return lines;
    }

    /// <summary>A paragraph that continues a list item (a later paragraph or code block of the item, indented to its level).</summary>
    private bool IsContinuation(Paragraph p, int maxLevel, out int level)
    {
        level = maxLevel;
        var indent = p.Props.IndentStart;
        if (indent != null)
        {
            for (var l = 0; l <= maxLevel; l++)
            {
                if (indent.Value == ListFactory.LevelIndent(l))
                {
                    level = l;
                    return true;
                }
            }
            return false;
        }
        return IsListParagraphStyle(p);
    }

    // ------------------------------------------------------------------ tables

    private List<string> RenderTable(Table t)
    {
        var columns = Math.Max(1, t.ColumnCount);
        var rows = new List<List<string>>();
        foreach (var row in t.Rows)
        {
            var cells = new List<string>();
            for (var g = 0; g < row.GridBefore; g++) cells.Add("");
            foreach (var cell in row.Cells)
            {
                cells.Add(cell.Props.VerticalMerge == "continue" ? "" : CellText(cell.Blocks));
                for (var s = 1; s < cell.Props.GridSpan; s++) cells.Add("");
            }
            while (cells.Count < columns) cells.Add("");
            rows.Add(cells);
        }
        if (rows.Count == 0) return new List<string>();
        columns = rows.Max(r => r.Count);
        foreach (var r in rows)
            while (r.Count < columns) r.Add("");
        var lines = new List<string> { "| " + string.Join(" | ", rows[0]) + " |" };
        var align = new List<string>();
        for (var c = 0; c < columns; c++)
        {
            var jc = ColumnAlignment(t, c);
            align.Add(jc switch
            {
                "center" => ":---:",
                "right" or "end" => "---:",
                "left" or "start" => ":---",
                _ => "---",
            });
        }
        lines.Add("| " + string.Join(" | ", align) + " |");
        foreach (var r in rows.Skip(1)) lines.Add("| " + string.Join(" | ", r) + " |");
        return lines;
    }

    private static string? ColumnAlignment(Table t, int column)
    {
        foreach (var row in t.Rows)
        {
            var cell = Table.CellAtColumn(row, column);
            if (cell?.Blocks.OfType<Paragraph>().FirstOrDefault() is { } p) return p.Props.Justification;
        }
        return null;
    }

    private string CellText(IEnumerable<Block> blocks)
    {
        var parts = new List<string>();
        foreach (var b in Flatten(blocks))
        {
            if (b is Paragraph p)
            {
                var label = _lists.Next(p);
                if (IsEmpty(p) && label == null) continue;
                var text = string.Join("<br>", RenderInlines(p, "<br>"));
                if (label != null && label.Text.Length > 0) text = EscapeText(label.Text, false) + " " + text;
                parts.Add(text);
            }
            else if (b is Table inner)
            {
                foreach (var r in inner.Rows)
                    foreach (var c in r.Cells)
                        if (CellText(c.Blocks) is { Length: > 0 } s)
                            parts.Add(s);
            }
        }
        return string.Join("<br>", parts);
    }

    // ------------------------------------------------------------------ inlines

    [Flags]
    private enum Fmt
    {
        None = 0,
        Strike = 1,
        Bold = 2,
        Italic = 4,
        Underline = 8,
        Sup = 16,
        Sub = 32,
    }

    private enum SegKind { Text, Code, Image, NoteRef, Break }

    private sealed class Seg
    {
        public SegKind Kind;
        public string Text = "";
        public Fmt Format;
        public Hyperlink? Link;
        public DrawingObject? Image;
        public NoteReference? Note;
    }

    private static readonly Fmt[] Order = { Fmt.Strike, Fmt.Bold, Fmt.Italic, Fmt.Underline, Fmt.Sup, Fmt.Sub };
    private static readonly Fmt[] ReverseOrder = { Fmt.Sub, Fmt.Sup, Fmt.Underline, Fmt.Italic, Fmt.Bold, Fmt.Strike };

    private static string Open(Fmt f) => f switch
    {
        Fmt.Strike => "~~",
        Fmt.Bold => "**",
        Fmt.Italic => "*",
        Fmt.Underline => "<u>",
        Fmt.Sup => "<sup>",
        Fmt.Sub => "<sub>",
        _ => "",
    };

    private static string Close(Fmt f) => f switch
    {
        Fmt.Strike => "~~",
        Fmt.Bold => "**",
        Fmt.Italic => "*",
        Fmt.Underline => "</u>",
        Fmt.Sup => "</sup>",
        Fmt.Sub => "</sub>",
        _ => "",
    };

    private List<Seg> Segments(Paragraph p)
    {
        var segs = new List<Seg>();
        foreach (var inline in p.Inlines) AddSegments(segs, inline, null);
        return NormalizeWhitespace(segs);
    }

    private void AddSegments(List<Seg> segs, Inline inline, Hyperlink? fieldLink)
    {
        if (inline.Deleted != null || inline.Props.Vanish == true) return;
        var link = inline.Link ?? fieldLink;
        switch (inline)
        {
            case TextRun t:
            {
                var code = IsCodeRun(inline.Props);
                var fmt = FormatOf(inline.Props, link != null);
                var sb = new StringBuilder();
                void FlushText()
                {
                    if (sb.Length == 0) return;
                    segs.Add(new Seg { Kind = code ? SegKind.Code : SegKind.Text, Text = sb.ToString(), Format = fmt, Link = link });
                    sb.Clear();
                }
                foreach (var c in t.Text)
                {
                    switch (c)
                    {
                        case TextRun.LineBreak:
                            FlushText();
                            segs.Add(new Seg { Kind = SegKind.Break });
                            break;
                        case TextRun.PageBreak:
                        case TextRun.ColumnBreak:
                        case TextRun.SoftHyphen:
                            break;
                        case TextRun.NoBreakHyphen:
                            sb.Append('-');
                            break;
                        default:
                            sb.Append(c);
                            break;
                    }
                }
                FlushText();
                break;
            }
            case FieldInline f:
            {
                Hyperlink? target = fieldLink;
                if (f.Kind == FieldKind.Hyperlink)
                {
                    var code = FieldCode.Parse(f.Code);
                    var anchor = code.SwitchValue("\\l");
                    target = new Hyperlink { Url = code.FirstArgument, Anchor = anchor };
                    if (target.Url == null && target.Anchor == null) target = fieldLink;
                }
                foreach (var r in f.Result) AddSegments(segs, r, inline.Link ?? target);
                break;
            }
            case SymbolChar s:
                segs.Add(new Seg { Kind = SegKind.Text, Text = s.Char >= '\uF000' ? ListFactory.NormalizeBullet(s.Char.ToString()) : s.Char.ToString(), Format = FormatOf(s.Props, link != null), Link = link });
                break;
            case DrawingObject d when d.IsPicture:
                segs.Add(new Seg { Kind = SegKind.Image, Image = d, Link = link });
                break;
            case NoteReference r:
                segs.Add(new Seg { Kind = SegKind.NoteRef, Note = r });
                break;
            case ClearBreak:
                segs.Add(new Seg { Kind = SegKind.Break });
                break;
            case OpaqueInline o when !o.IsMarker && o.Display.Length > 0:
                segs.Add(new Seg { Kind = SegKind.Text, Text = o.Display, Format = FormatOf(o.Props, link != null), Link = link });
                break;
        }
    }

    /// <summary>Moves spaces at the edges of formatted text outside the delimiters ("**a **b" isn't bold in Markdown).</summary>
    private static List<Seg> NormalizeWhitespace(List<Seg> segs)
    {
        var result = new List<Seg>();
        for (var i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            if (s.Kind != SegKind.Text || s.Format == Fmt.None)
            {
                result.Add(s);
                continue;
            }
            var text = s.Text;
            var start = 0;
            while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
            var end = text.Length;
            while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
            var prev = i > 0 && segs[i - 1].Kind == SegKind.Text && ReferenceEquals(segs[i - 1].Link, s.Link) ? segs[i - 1].Format : Fmt.None;
            var next = i + 1 < segs.Count && segs[i + 1].Kind == SegKind.Text && ReferenceEquals(segs[i + 1].Link, s.Link) ? segs[i + 1].Format : Fmt.None;
            if (start > 0) result.Add(new Seg { Kind = SegKind.Text, Text = text.Substring(0, start), Format = s.Format & prev, Link = s.Link });
            if (end > start) result.Add(new Seg { Kind = SegKind.Text, Text = text.Substring(start, end - start), Format = s.Format, Link = s.Link });
            if (end < text.Length && end >= start) result.Add(new Seg { Kind = SegKind.Text, Text = text.Substring(Math.Max(end, start)), Format = s.Format & next, Link = s.Link });
        }
        return result;
    }

    private Fmt FormatOf(RunProps props, bool inLink)
    {
        var sheet = _doc.Styles;
        var style = props.StyleId;
        var f = Fmt.None;
        if (props.Bold ?? StyleChain.RunValue(sheet, style, r => r.Bold) ?? false) f |= Fmt.Bold;
        if (props.Italic ?? StyleChain.RunValue(sheet, style, r => r.Italic) ?? false) f |= Fmt.Italic;
        if ((props.Strike ?? StyleChain.RunValue(sheet, style, r => r.Strike) ?? false) || (props.DoubleStrike ?? false)) f |= Fmt.Strike;
        var underline = props.Underline ?? (inLink ? null : StyleChain.RunString(sheet, style, r => r.Underline));
        if (!inLink && underline != null && underline != "none") f |= Fmt.Underline;
        var vert = props.VertAlign ?? StyleChain.RunString(sheet, style, r => r.VertAlign);
        if (vert == "superscript") f |= Fmt.Sup;
        else if (vert == "subscript") f |= Fmt.Sub;
        return f;
    }

    private bool IsCodeRun(RunProps props)
    {
        var style = props.StyleId;
        if (style == null) return false;
        return _doc.Styles.Chain(style).Any(s => s.Id == "InlineCode" || s.Name.Equals("Inline Code", StringComparison.OrdinalIgnoreCase) ||
                                                 s.Name.Equals("HTML Code", StringComparison.OrdinalIgnoreCase) || s.Name.Equals("HTML Keyboard", StringComparison.OrdinalIgnoreCase) ||
                                                 s.Name.Equals("HTML Typewriter", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A paragraph's content as Markdown lines. Hard line breaks end a line with "\" (or become
    /// <paramref name="breakAs"/>, for headings and table cells, which are one line).
    /// </summary>
    private List<string> RenderInlines(Paragraph p, string? breakAs)
    {
        var segs = Segments(p);
        var lines = new List<string>();
        var line = new StringBuilder();
        var open = new List<Fmt>();

        void CloseAll()
        {
            for (var k = open.Count - 1; k >= 0; k--) line.Append(Close(open[k]));
            open.Clear();
        }

        void Apply(Fmt wanted)
        {
            var keep = 0;
            while (keep < open.Count && (wanted & open[keep]) != 0) keep++;
            for (var k = open.Count - 1; k >= keep; k--) line.Append(Close(open[k]));
            open.RemoveRange(keep, open.Count - keep);
            foreach (var f in Order)
            {
                if ((wanted & f) != 0 && !open.Contains(f))
                {
                    line.Append(Open(f));
                    open.Add(f);
                }
            }
        }

        void NewLine()
        {
            CloseAll();
            if (breakAs != null)
            {
                line.Append(breakAs);
                return;
            }
            lines.Add(line.ToString().TrimEnd() + "\\");
            line.Clear();
        }

        var i = 0;
        while (i < segs.Count)
        {
            var s = segs[i];
            if (s.Kind == SegKind.Break)
            {
                NewLine();
                i++;
                continue;
            }
            if (s.Link != null && (s.Kind == SegKind.Text || s.Kind == SegKind.Code || s.Kind == SegKind.Image))
            {
                var group = new List<Seg>();
                while (i < segs.Count && ReferenceEquals(segs[i].Link, s.Link) && segs[i].Kind != SegKind.Break && segs[i].Kind != SegKind.NoteRef) group.Add(segs[i++]);
                CloseAll();
                line.Append(RenderLink(s.Link, group, line.Length == 0));
                continue;
            }
            Apply(s.Kind == SegKind.Text || s.Kind == SegKind.Code ? s.Format : Fmt.None);
            line.Append(RenderSeg(s, line.Length == 0 && open.Count == 0));
            i++;
        }
        CloseAll();
        var last = line.ToString();
        if (breakAs == null || last.Length > 0 || lines.Count == 0) lines.Add(last);
        // Markdown ignores spaces at the start of a line (four make a code block): drop them.
        for (var k = 0; k < lines.Count; k++) lines[k] = lines[k].TrimStart(' ', '\t');
        return lines;
    }

    private string RenderSeg(Seg s, bool atLineStart)
    {
        switch (s.Kind)
        {
            case SegKind.Text:
                return EscapeText(atLineStart ? s.Text.TrimStart(' ', '\t') : s.Text, atLineStart);
            case SegKind.Code:
                return CodeSpan(s.Text);
            case SegKind.Image:
                return RenderImage(s.Image!);
            case SegKind.NoteRef:
                return NoteRef(s.Note!);
            default:
                return "";
        }
    }

    private string RenderLink(Hyperlink link, List<Seg> group, bool atLineStart)
    {
        var url = link.Url ?? "";
        if (link.Anchor != null) url = url + "#" + link.Anchor;
        if (url.Length == 0) return string.Concat(group.Select(g => RenderSeg(g, atLineStart)));
        // An address shown as itself: an autolink.
        if (group.Count == 1 && group[0].Kind == SegKind.Text && group[0].Format == Fmt.None && link.Tooltip == null && IsAbsolute(url) && url.IndexOfAny(new[] { ' ', '<', '>' }) < 0)
        {
            var text = group[0].Text;
            if (text == url) return "<" + url + ">";
            if (url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) && "mailto:" + text == url && text.Contains('@')) return "<" + text + ">";
        }
        var sb = new StringBuilder("[");
        var open = Fmt.None;
        foreach (var g in group)
        {
            var wanted = g.Kind is SegKind.Text or SegKind.Code ? g.Format : Fmt.None;
            if (wanted != open)
            {
                foreach (var f in ReverseOrder)
                    if ((open & f) != 0) sb.Append(Close(f));
                foreach (var f in Order)
                    if ((wanted & f) != 0) sb.Append(Open(f));
                open = wanted;
            }
            sb.Append(RenderSeg(g, false));
        }
        foreach (var f in ReverseOrder)
            if ((open & f) != 0) sb.Append(Close(f));
        sb.Append("](").Append(FormatUrl(url));
        if (!string.IsNullOrEmpty(link.Tooltip)) sb.Append(" \"").Append(link.Tooltip!.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
        sb.Append(')');
        return sb.ToString();
    }

    private static bool IsAbsolute(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase);

    private static string FormatUrl(string url)
    {
        var depth = 0;
        var balanced = true;
        foreach (var c in url)
        {
            if (c == '(') depth++;
            else if (c == ')' && --depth < 0) balanced = false;
        }
        if (depth != 0) balanced = false;
        if (!balanced || url.IndexOfAny(new[] { ' ', '<', '>', '\t' }) >= 0)
            return "<" + url.Replace("<", "%3C").Replace(">", "%3E") + ">";
        return url;
    }

    private static string CodeSpan(string text)
    {
        var longest = 0;
        var run = 0;
        foreach (var c in text)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        var fence = new string('`', longest + 1);
        var pad = text.StartsWith("`", StringComparison.Ordinal) || text.EndsWith("`", StringComparison.Ordinal) ||
                  (text.Length > 1 && text.StartsWith(" ", StringComparison.Ordinal) && text.EndsWith(" ", StringComparison.Ordinal) && text.Trim().Length > 0);
        return fence + (pad ? " " + text + " " : text) + fence;
    }

    private static readonly Regex Entity = new("&(#[0-9]+|#[xX][0-9a-fA-F]+|[A-Za-z][A-Za-z0-9]*);", RegexOptions.CultureInvariant);

    /// <summary>Escapes Markdown's special characters (and what would start a list, heading or quote at a line start).</summary>
    public static string EscapeText(string text, bool atLineStart)
    {
        var sb = new StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '\\':
                case '`':
                case '*':
                case '_':
                case '[':
                case ']':
                case '<':
                case '>':
                case '~':
                case '|':
                case '#':
                    sb.Append('\\').Append(c);
                    break;
                case '!' when i + 1 < text.Length && text[i + 1] == '[':
                    sb.Append("\\!");
                    break;
                case '&' when Entity.IsMatch(text, i) && Entity.Match(text, i).Index == i:
                    sb.Append("\\&");
                    break;
                case ':' when i + 2 < text.Length && text[i + 1] == '/' && text[i + 2] == '/':
                    // Keeps "http://…" in plain text from turning into a link.
                    sb.Append(":\\/");
                    i++;
                    break;
                case '.' when i >= 3 && string.Compare(text, i - 3, "www", 0, 3, StringComparison.OrdinalIgnoreCase) == 0 && (i == 3 || !char.IsLetterOrDigit(text[i - 4])):
                    sb.Append("\\.");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        var s = sb.ToString();
        if (atLineStart && s.Length > 0)
        {
            bool BlankFrom(int from) => from >= s.Length || s[from] is ' ' or '\t';
            // "- x" / "+ x" start a list; a line of only "-" or "=" underlines the line before as a heading.
            if (s[0] is '-' or '+' && (BlankFrom(1) || s.Trim().All(ch => ch == s[0] || ch == ' '))) return "\\" + s;
            if (s[0] == '=' && s.Trim().All(ch => ch is '=' or ' ')) return "\\" + s;
            var digits = 0;
            while (digits < s.Length && char.IsDigit(s[digits])) digits++;
            if (digits is > 0 and <= 9 && digits < s.Length && s[digits] is '.' or ')' && BlankFrom(digits + 1)) return s.Substring(0, digits) + "\\" + s.Substring(digits);
        }
        return s;
    }

    private string PlainText(Paragraph p)
    {
        var sb = new StringBuilder();
        void Add(Inline inline)
        {
            if (inline.Deleted != null) return;
            switch (inline)
            {
                case TextRun t:
                    foreach (var c in t.Text)
                    {
                        if (c is TextRun.LineBreak) sb.Append('\n');
                        else if (c is TextRun.PageBreak or TextRun.ColumnBreak or TextRun.SoftHyphen) continue;
                        else if (c == TextRun.NoBreakHyphen) sb.Append('-');
                        else sb.Append(c);
                    }
                    break;
                case FieldInline f:
                    foreach (var r in f.Result) Add(r);
                    break;
                case SymbolChar s:
                    sb.Append(s.Char);
                    break;
                case OpaqueInline o when !o.IsMarker:
                    sb.Append(o.Display);
                    break;
            }
        }
        foreach (var inline in p.Inlines) Add(inline);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ pictures and notes

    private string RenderImage(DrawingObject d)
    {
        var alt = d.AltText ?? d.Title ?? "";
        var media = _doc.Media.Get(d.MediaId);
        if (_options.ImageFolder == null || media == null) return EscapeText(alt, false);
        if (!_imageFiles.TryGetValue(media.Id, out var file))
        {
            var ext = media.Extension == "jpeg" ? "jpg" : media.Extension;
            file = "image" + (_imageFiles.Count + 1).ToString(CultureInfo.InvariantCulture) + "." + ext;
            Directory.CreateDirectory(_options.ImageFolder);
            File.WriteAllBytes(Path.Combine(_options.ImageFolder, file), media.Data);
            _imageFiles[media.Id] = file;
        }
        var folder = _options.ImageFolderReference ?? Path.GetFileName(_options.ImageFolder.TrimEnd('\\', '/'));
        var url = string.IsNullOrEmpty(folder) ? file : folder.TrimEnd('/', '\\').Replace('\\', '/') + "/" + file;
        var sb = new StringBuilder("![").Append(EscapeText(alt, false)).Append("](").Append(FormatUrl(url));
        if (!string.IsNullOrEmpty(d.Title)) sb.Append(" \"").Append(d.Title!.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
        return sb.Append(')').ToString();
    }

    private string NoteRef(NoteReference r)
    {
        var note = _doc.FindNote(r.Kind, r.Id);
        if (note == null) return "";
        if (!_noteNumbers.TryGetValue(note, out var n))
        {
            n = _notes.Count + 1;
            _notes.Add(note);
            _noteNumbers[note] = n;
        }
        return "[^" + n.ToString(CultureInfo.InvariantCulture) + "]";
    }

    public void WriteNotes()
    {
        for (var k = 0; k < _notes.Count; k++)
        {
            var note = _notes[k];
            var lines = new List<string>();
            var first = true;
            foreach (var p in note.Paragraphs)
            {
                var copy = (Paragraph)p.CloneBlock();
                copy.Inlines.RemoveAll(x => x is NoteMark);
                if (copy.Inlines.Count > 0 && copy.Inlines[0] is TextRun t)
                {
                    t.Text = t.Text.TrimStart(' ');
                    if (t.Text.Length == 0) copy.Inlines.RemoveAt(0);
                }
                var body = RenderInlines(copy, null);
                if (first)
                {
                    lines.Add("[^" + (k + 1).ToString(CultureInfo.InvariantCulture) + "]: " + body[0]);
                    lines.AddRange(body.Skip(1).Select(l => "    " + l));
                }
                else
                {
                    lines.Add("");
                    lines.AddRange(body.Select(l => l.Length == 0 ? "" : "    " + l));
                }
                first = false;
            }
            if (lines.Count == 0) lines.Add("[^" + (k + 1).ToString(CultureInfo.InvariantCulture) + "]: ");
            Emit(lines);
        }
    }
}
