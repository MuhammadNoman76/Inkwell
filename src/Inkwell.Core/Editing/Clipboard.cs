using System.Text;
using System.Xml.Linq;
using Inkwell.Core.Model;
using Inkwell.Core.Styles;

namespace Inkwell.Core.Editing;

/// <summary>How pasted content is formatted.</summary>
public enum PasteMode
{
    /// <summary>Match the destination: keep emphasis, links, lists, headings and tables; take the document's fonts, sizes and colours.</summary>
    Default,

    /// <summary>Plain text in the formatting at the caret.</summary>
    TextOnly,

    /// <summary>Keep the source's look (fonts, sizes, colours, spacing).</summary>
    KeepSource,
}

/// <summary>
/// Copied content: whole blocks (paragraphs, tables), where the last paragraph may be partial (no paragraph mark),
/// with the document they came from (for styles, lists, pictures and notes).
/// </summary>
public sealed class DocFragment
{
    public List<Block> Blocks { get; } = new();

    /// <summary>The document the content came from (styles, numbering, pictures, notes), or null for plain text.</summary>
    public Document? Source { get; set; }

    /// <summary>True when the last paragraph was copied without its paragraph mark (it merges into the destination).</summary>
    public bool OpenEnd { get; set; } = true;

    public bool IsEmpty => Blocks.Count == 0 || (Blocks.Count == 1 && Blocks[0] is Paragraph { IsEmpty: true } && OpenEnd);

    /// <summary>Plain text: paragraphs on their own lines, table cells separated by tabs.</summary>
    public string Text
    {
        get
        {
            var sb = new StringBuilder();
            for (var i = 0; i < Blocks.Count; i++)
            {
                AppendText(sb, Blocks[i]);
                if (i < Blocks.Count - 1 || !OpenEnd) sb.Append("\r\n");
            }
            return sb.ToString();
        }
    }

    private static void AppendText(StringBuilder sb, Block block)
    {
        switch (block)
        {
            case Paragraph p:
                sb.Append(VisibleText(p));
                break;
            case Table t:
                for (var r = 0; r < t.Rows.Count; r++)
                {
                    var row = t.Rows[r];
                    for (var c = 0; c < row.Cells.Count; c++)
                    {
                        if (c > 0) sb.Append('\t');
                        sb.Append(string.Join(" ", DocumentWalker.Paragraphs(row.Cells[c].Blocks).Select(VisibleText)));
                    }
                    if (r < t.Rows.Count - 1) sb.Append("\r\n");
                }
                break;
            default:
                foreach (var p in DocumentWalker.Paragraphs(new[] { block })) sb.Append(VisibleText(p)).Append("\r\n");
                break;
        }
    }

    private static string VisibleText(Paragraph p)
    {
        var sb = new StringBuilder();
        foreach (var inline in p.Inlines)
        {
            if (inline.Deleted != null) continue;
            switch (inline)
            {
                case TextRun r:
                    foreach (var ch in r.Text)
                    {
                        if (ch == TextRun.LineBreak) sb.Append("\r\n");
                        else if (ch is TextRun.PageBreak or TextRun.ColumnBreak or TextRun.SoftHyphen) continue;
                        else if (ch == TextRun.NoBreakHyphen) sb.Append('-');
                        else sb.Append(ch);
                    }
                    break;
                case FieldInline f:
                    sb.Append(f.ResultText);
                    break;
                case SymbolChar s:
                    sb.Append(s.Char);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Plain text as a fragment (lines become paragraphs, no formatting).</summary>
    public static DocFragment FromText(string text)
    {
        var fragment = new DocFragment();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        // A final line break means the last paragraph is complete.
        var count = lines.Length;
        if (count > 1 && lines[count - 1].Length == 0)
        {
            count--;
            fragment.OpenEnd = false;
        }
        for (var i = 0; i < count; i++)
        {
            var p = new Paragraph();
            var line = lines[i].Replace("\0", "");
            if (line.Length > 0) p.Inlines.Add(new TextRun(line));
            fragment.Blocks.Add(p);
        }
        return fragment;
    }

    /// <summary>A whole document's main text as a fragment (imported HTML, RTF or Markdown being pasted).</summary>
    public static DocFragment FromDocument(Document doc)
    {
        var fragment = new DocFragment { Source = doc, OpenEnd = true };
        foreach (var b in doc.Body.Blocks) fragment.Blocks.Add(b.CloneBlock());
        // Drop the empty paragraph importers leave at the end, and the section break of the last paragraph.
        while (fragment.Blocks.Count > 1 && fragment.Blocks[fragment.Blocks.Count - 1] is Paragraph { IsEmpty: true })
            fragment.Blocks.RemoveAt(fragment.Blocks.Count - 1);
        foreach (var p in DocumentWalker.Paragraphs(fragment.Blocks)) p.Props.Section = null;
        // Content that ends with a table needs a paragraph to go on with.
        if (fragment.Blocks.LastOrDefault() is Table) fragment.OpenEnd = false;
        return fragment;
    }
}

public sealed partial class DocEditor
{
    // ------------------------------------------------------------------ copy

    /// <summary>A copy of the selected content (null when nothing is selected).</summary>
    public DocFragment? CopySelection()
    {
        var sel = Selection;
        if (sel.IsEmpty) return null;
        var start = sel.Start;
        var end = sel.End;
        var fragment = new DocFragment { Source = Doc };
        if (ReferenceEquals(start.Para, end.Para))
        {
            var p = new Paragraph { Props = start.Para.Props.Clone() };
            p.Inlines.AddRange(start.Para.CopyRange(start.Offset, end.Offset));
            p.Props.Section = null;
            fragment.Blocks.Add(p);
            fragment.OpenEnd = true;
            return Clean(fragment);
        }

        var startCell = Nav.CellOf(start.Para);
        var endCell = Nav.CellOf(end.Para);
        if (startCell != null && endCell != null && ReferenceEquals(startCell.Row?.Table, endCell.Row?.Table))
        {
            // Inside one table: the rows that are touched (whole rows).
            var table = startCell.Row!.Table!;
            var r0 = startCell.Row.Index;
            var r1 = endCell.Row!.Index;
            if (r0 > r1) (r0, r1) = (r1, r0);
            var copy = (Table)table.CloneBlock();
            copy.Rows = copy.Rows.Skip(r0).Take(r1 - r0 + 1).ToList();
            foreach (var row in copy.Rows) row.Table = copy;
            copy.FixParents();
            fragment.Blocks.Add(copy);
            fragment.OpenEnd = false;
            return Clean(fragment);
        }

        // Top-level blocks of the story between the two ends.
        var list = TopList(start.Para);
        var first = TopBlock(list, start.Para);
        var last = TopBlock(list, end.Para);
        if (list == null || first == null || last == null)
        {
            fragment.Blocks.AddRange(Nav.Range(start.Para, end.Para).Select(p =>
            {
                var s = ReferenceEquals(p, start.Para) ? start.Offset : 0;
                var e = ReferenceEquals(p, end.Para) ? end.Offset : p.Length;
                var c = new Paragraph { Props = p.Props.Clone() };
                c.Inlines.AddRange(p.CopyRange(s, e));
                return (Block)c;
            }));
            return Clean(fragment);
        }
        var i0 = list.IndexOf(first);
        var i1 = list.IndexOf(last);
        for (var i = i0; i <= i1; i++)
        {
            var block = list[i];
            if (block is Paragraph p && (ReferenceEquals(p, start.Para) || ReferenceEquals(p, end.Para)))
            {
                var s = ReferenceEquals(p, start.Para) ? start.Offset : 0;
                var e = ReferenceEquals(p, end.Para) ? end.Offset : p.Length;
                var c = new Paragraph { Props = p.Props.Clone() };
                c.Inlines.AddRange(p.CopyRange(s, e));
                fragment.Blocks.Add(c);
            }
            else fragment.Blocks.Add(block.CloneBlock());
        }
        fragment.OpenEnd = fragment.Blocks[fragment.Blocks.Count - 1] is Paragraph;
        if (!fragment.OpenEnd) fragment.Blocks.Add(new Paragraph());
        fragment.OpenEnd = true;
        return Clean(fragment);
    }

    private static BlockList? TopList(Paragraph p)
    {
        var story = p.Story;
        return story?.Blocks;
    }

    private static Block? TopBlock(BlockList? list, Paragraph p)
    {
        if (list == null) return null;
        Block current = p;
        var guard = 0;
        while (current.Parent != null && !ReferenceEquals(current.Parent, list) && guard++ < 64)
        {
            switch (current.Parent.Owner)
            {
                case TableCell cell when cell.Row?.Table != null:
                    current = cell.Row.Table;
                    break;
                case Block b:
                    current = b;
                    break;
                default:
                    return null;
            }
        }
        return ReferenceEquals(current.Parent, list) ? current : null;
    }

    /// <summary>Removes what must not be duplicated (bookmarks, comment anchors, deleted text) and copies the notes.</summary>
    private DocFragment Clean(DocFragment fragment)
    {
        foreach (var p in DocumentWalker.Paragraphs(fragment.Blocks))
        {
            p.Props.Section = null;
            p.Props.Remove("pPrChange");
            p.Props.MarkProps()?.Remove("ins");
            p.Props.MarkProps()?.Remove("del");
            p.Inlines.RemoveAll(i => i is BookmarkMarker or CommentMarker or CommentMark || i.Deleted != null);
            foreach (var i in p.Inlines)
            {
                i.Inserted = null;
                i.Props.Remove("rPrChange");
            }
            p.Normalize();
        }
        return fragment;
    }

    // ------------------------------------------------------------------ paste

    /// <summary>Pastes a fragment at the selection as one undo step.</summary>
    public void Paste(DocFragment fragment, PasteMode mode)
    {
        if (fragment.IsEmpty) return;
        Edit(mode == PasteMode.TextOnly ? "Paste text" : "Paste", t =>
        {
            var pos = Selection.IsEmpty ? Selection.Active : DeleteRange(t, Selection.Start, Selection.End);
            EnsureEditable(pos.Para);
            var prepared = PasteNormalizer.Prepare(this, t, fragment, mode, pos);
            return new Selection(InsertBlocks(t, pos, prepared.Blocks, prepared.OpenEnd));
        });
    }

    /// <summary>Inserts blocks at a position; returns the position after them.</summary>
    private DocPos InsertBlocks(Transaction t, DocPos pos, List<Block> blocks, bool openEnd)
    {
        var p = pos.Para;
        t.Touch(p);
        var rev = TrackChanges ? NewRevision() : null;
        if (rev != null)
            foreach (var para in DocumentWalker.Paragraphs(blocks))
                foreach (var inline in para.Inlines)
                    inline.Inserted = rev;

        // A single partial paragraph: its content goes in the paragraph at the caret.
        if (blocks.Count == 1 && blocks[0] is Paragraph only && openEnd)
        {
            var tail = p.TakeTail(pos.Offset);
            var length = only.Inlines.Sum(i => i.Length);
            if (p.IsEmpty && tail.Count == 0 && only.Props.X.HasElements) p.Props = MergeParaProps(p.Props, only.Props);
            p.Inlines.AddRange(only.Inlines);
            p.Inlines.AddRange(tail);
            p.Normalize();
            return new DocPos(p, pos.Offset + length);
        }

        var list = p.Parent!;
        t.Touch(list);
        var next = SplitParagraph(t, pos, keepStyle: true).Para;
        t.Touch(next);
        var insertAt = list.IndexOf(p) + 1;
        var index = 0;
        // The first pasted paragraph joins the text before the caret.
        if (blocks[0] is Paragraph firstPara)
        {
            if (p.IsEmpty) p.Props = MergeParaProps(p.Props, firstPara.Props);
            p.Inlines.AddRange(firstPara.Inlines);
            p.Normalize();
            index = 1;
        }
        // The last one joins the text after the caret when it has no paragraph mark of its own.
        Paragraph? lastPara = null;
        var end = blocks.Count;
        if (openEnd && blocks.Count > index && blocks[blocks.Count - 1] is Paragraph lp)
        {
            lastPara = lp;
            end--;
        }
        for (var i = index; i < end; i++)
        {
            var b = blocks[i];
            if (rev != null && b is Paragraph bp) bp.Props.MarkProps(create: true)!.SetMarkRevision("ins", rev);
            list.Insert(insertAt++, b);
        }
        if (lastPara != null)
        {
            var length = lastPara.Inlines.Sum(i => i.Length);
            if (next.IsEmpty) next.Props = MergeParaProps(next.Props, lastPara.Props);
            next.Inlines.InsertRange(0, lastPara.Inlines);
            next.Normalize();
            return new DocPos(next, length);
        }
        return new DocPos(next, 0);
    }

    /// <summary>Pasted paragraph formatting on an empty destination paragraph (its section break stays).</summary>
    private static ParaProps MergeParaProps(ParaProps destination, ParaProps pasted)
    {
        var result = pasted.Clone();
        result.Section = destination.Section?.Clone();
        return result;
    }
}

/// <summary>
/// Makes copied content fit the destination document: "match destination" keeps the meaning (emphasis, links,
/// lists, headings, tables) and drops the source's fonts, sizes and colours; "keep source" keeps its look; "text
/// only" keeps just the words.
/// </summary>
public static class PasteNormalizer
{
    public sealed class Prepared
    {
        public List<Block> Blocks { get; } = new();
        public bool OpenEnd { get; set; }
    }

    /// <summary>The semantic paragraph styles that survive "match destination" (by Word's built-in names).</summary>
    private static readonly string[] SemanticStyles =
    {
        "Title", "Subtitle", "Heading 1", "Heading 2", "Heading 3", "Heading 4", "Heading 5", "Heading 6", "Quote", "Intense Quote", "Caption", "List Paragraph",
    };

    private static readonly string[] MonospaceFonts = { "Consolas", "Courier New", "Courier", "Cascadia Code", "Cascadia Mono", "Lucida Console", "Menlo", "Monaco", "JetBrains Mono", "Source Code Pro", "monospace" };

    public static Prepared Prepare(DocEditor editor, Transaction t, DocFragment fragment, PasteMode mode, DocPos at)
    {
        var dest = editor.Doc;
        var result = new Prepared { OpenEnd = fragment.OpenEnd };
        if (mode == PasteMode.TextOnly || fragment.Source == null)
        {
            var text = DocFragment.FromText(fragment.Text);
            var props = editor.PendingProps?.Clone() ?? at.Para.PropsForTypingAt(at.Offset);
            props.Remove("rPrChange");
            foreach (var p in text.Blocks.Cast<Paragraph>())
            {
                // New paragraphs look like the one the caret is in.
                var copy = new Paragraph { Props = at.Para.Props.Clone() };
                copy.Props.Section = null;
                copy.Props.Remove("pPrChange");
                foreach (var inline in p.Inlines)
                {
                    inline.Props = props.Clone();
                    copy.Inlines.Add(inline);
                }
                result.Blocks.Add(copy);
            }
            result.OpenEnd = text.OpenEnd;
            FitContainer(result, at);
            return result;
        }

        var source = fragment.Source;
        var same = ReferenceEquals(source, dest);
        foreach (var b in fragment.Blocks) result.Blocks.Add(b.CloneBlock());

        if (!same)
        {
            var context = new Context(editor, t, source, mode);
            foreach (var b in result.Blocks) context.Adapt(b);
        }
        CopyNotes(editor, t, source, result.Blocks);
        if (!same) CopyMedia(dest, source, result.Blocks);
        FitContainer(result, at);
        return result;
    }

    /// <summary>Tables can't go inside table cells, notes or comments here: they become tab-separated lines.</summary>
    private static void FitContainer(Prepared prepared, DocPos at)
    {
        var story = at.Para.Story;
        var inCell = Nav.CellOf(at.Para) != null;
        if (!inCell && story is not Note && story is not Comment) return;
        for (var i = 0; i < prepared.Blocks.Count; i++)
        {
            if (prepared.Blocks[i] is not Table table) continue;
            var lines = new List<Block>();
            foreach (var row in table.Rows)
            {
                var p = new Paragraph();
                for (var c = 0; c < row.Cells.Count; c++)
                {
                    if (c > 0) p.Inlines.Add(new TextRun("\t"));
                    var first = true;
                    foreach (var cp in DocumentWalker.Paragraphs(row.Cells[c].Blocks))
                    {
                        if (!first) p.Inlines.Add(new TextRun(" "));
                        first = false;
                        p.Inlines.AddRange(cp.Inlines.Where(x => x.Length > 0 && !(x is DrawingObject { IsFloating: true })));
                    }
                }
                p.Normalize();
                lines.Add(p);
            }
            prepared.Blocks.RemoveAt(i);
            prepared.Blocks.InsertRange(i, lines);
            i += lines.Count - 1;
        }
        // Notes and comments hold no other blocks either.
        prepared.Blocks.RemoveAll(b => b is not Paragraph && (story is Note || story is Comment));
        if (prepared.Blocks.Count == 0) prepared.Blocks.Add(new Paragraph());
        if (prepared.Blocks[prepared.Blocks.Count - 1] is Paragraph) { }
    }

    /// <summary>Footnotes and endnotes in the pasted text get their own copies (new numbers).</summary>
    private static void CopyNotes(DocEditor editor, Transaction t, Document source, List<Block> blocks)
    {
        var dest = editor.Doc;
        var touched = false;
        foreach (var p in DocumentWalker.Paragraphs(blocks).ToList())
        {
            foreach (var reference in p.Inlines.OfType<NoteReference>())
            {
                var notes = reference.Kind == NoteKind.Footnote ? source.Footnotes : source.Endnotes;
                if (!touched)
                {
                    t.TouchNotes();
                    t.TouchSettings();
                    touched = true;
                }
                DocBuilder.EnsureSeparators(dest, reference.Kind);
                var id = dest.NextNoteId(reference.Kind);
                var note = new Note(reference.Kind) { Id = id };
                if (notes.TryGetValue(reference.Id, out var original))
                    foreach (var b in original.Blocks) note.Blocks.Add(b.CloneBlock());
                else note.Blocks.Add(new Paragraph { Props = { StyleId = reference.Kind == NoteKind.Footnote ? "FootnoteText" : "EndnoteText" } });
                (reference.Kind == NoteKind.Footnote ? dest.Footnotes : dest.Endnotes)[id] = note;
                reference.Id = id;
            }
        }
    }

    private static void CopyMedia(Document dest, Document source, List<Block> blocks)
    {
        foreach (var p in DocumentWalker.Paragraphs(blocks))
            foreach (var d in p.Inlines.OfType<DrawingObject>())
            {
                if (d.MediaId == null) continue;
                var item = source.Media.Get(d.MediaId);
                if (item != null) d.MediaId = dest.Media.Add(item.Data, item.ContentType, item.Extension);
                else if (dest.Media.Get(d.MediaId) == null) d.MediaId = null;
                // Preserved drawing XML refers to the source package's relationships: rebuild it from the model.
                d.Original = null;
                d.SourcePart = null;
            }
    }

    private sealed class Context
    {
        private readonly DocEditor _editor;
        private readonly Transaction _t;
        private readonly Document _source;
        private readonly Document _dest;
        private readonly PasteMode _mode;
        private readonly StyleResolver _sourceStyles;
        private readonly Dictionary<int, int> _numbering = new();
        private readonly HashSet<string> _copiedStyles = new(StringComparer.Ordinal);
        private bool _numberingTouched;
        private bool _stylesTouched;

        public Context(DocEditor editor, Transaction t, Document source, PasteMode mode)
        {
            _editor = editor;
            _t = t;
            _source = source;
            _dest = editor.Doc;
            _mode = mode;
            _sourceStyles = new StyleResolver(source);
        }

        public void Adapt(Block block)
        {
            switch (block)
            {
                case Paragraph p:
                    AdaptParagraph(p);
                    break;
                case Table table:
                    AdaptTable(table);
                    break;
                default:
                    foreach (var p in DocumentWalker.Paragraphs(new[] { block }).ToList()) AdaptParagraph(p);
                    break;
            }
        }

        private void AdaptTable(Table table)
        {
            if (_mode == PasteMode.Default)
            {
                var style = new[] { "InkwellTable", "TableGrid" }.FirstOrDefault(id => _dest.Styles.Get(id) != null);
                table.Props.StyleId = style;
                table.Props.Remove("tblBorders");
                table.Props.Remove("shd");
                foreach (var row in table.Rows)
                    foreach (var cell in row.Cells)
                    {
                        cell.Props.Remove("tcBorders");
                        cell.Props.Remove("shd");
                    }
            }
            else if (table.Props.StyleId is { } id) table.Props.StyleId = MapStyle(id, "table");
            foreach (var row in table.Rows)
                foreach (var cell in row.Cells)
                    foreach (var b in cell.Blocks.ToList())
                        Adapt(b);
        }

        private void AdaptParagraph(Paragraph p)
        {
            // Resolve everything in the source first (before the paragraph's properties change).
            var resolvedPara = _sourceStyles.ResolvePara(p);
            var runs = p.Inlines.Select(i => (Inline: i, Resolved: _sourceStyles.ResolveRun(i.Props, p))).ToList();
            var baseline = _sourceStyles.ResolveRun(new RunProps(), p);
            var sourceStyle = _source.Styles.Get(resolvedPara.StyleId);
            var numId = resolvedPara.NumId;
            var numLevel = resolvedPara.NumLevel;

            if (_mode == PasteMode.Default)
            {
                var props = new ParaProps();
                var mapped = MapSemanticStyle(sourceStyle);
                if (mapped != null && mapped != _dest.Styles.DefaultParagraphStyleId) props.StyleId = mapped;
                if (p.Props.Justification is "center" or "right" or "end" or "both") props.Justification = p.Props.Justification;
                if (resolvedPara.Bidi) props.Bidi = true;
                if (numId > 0 && _source.Numbering.Level(numId, numLevel) is { } level)
                {
                    props.SetNumbering(MapList(numId, level.IsBullet), numLevel);
                    props.StyleId ??= _dest.Styles.Get("ListParagraph") != null ? "ListParagraph" : null;
                }
                p.Props = props;
                // A whole paragraph in a typewriter font is a font choice, not code: only some monospace words become code.
                var texts = runs.Where(r => r.Inline is TextRun { Text.Length: > 0 }).ToList();
                var codeAllowed = props.StyleId != "Code" && texts.Count > 0 && !texts.All(r => IsMonospace(r.Resolved.AsciiFont));
                foreach (var (inline, resolved) in runs)
                    inline.Props = MatchDestination(inline, resolved, baseline, codeAllowed);
            }
            else
            {
                // Keep source: the paragraph keeps its own formatting; styles the document lacks are copied.
                var props = p.Props.Clone();
                if (props.StyleId != null) props.StyleId = MapStyle(props.StyleId, "paragraph");
                if (numId > 0)
                {
                    if (props.NumId is > 0 || props.HasNumbering) props.SetNumbering(CopyList(numId), numLevel);
                    else props.SetNumbering(CopyList(numId), numLevel);
                }
                // The source's style-defined spacing and alignment become direct formatting when the destination's
                // style of the same name looks different.
                var destResolved = new StyleResolver(_dest).ResolvePara(new Paragraph { Props = props.Clone() });
                if (destResolved.SpacingBefore != resolvedPara.SpacingBefore) props.SpacingBefore = resolvedPara.SpacingBefore;
                if (destResolved.SpacingAfter != resolvedPara.SpacingAfter) props.SpacingAfter = resolvedPara.SpacingAfter;
                if (destResolved.Line != resolvedPara.Line || destResolved.LineRule != resolvedPara.LineRule)
                {
                    props.SpacingLine = resolvedPara.Line;
                    props.SpacingLineRule = resolvedPara.LineRule;
                }
                if (destResolved.Justification != resolvedPara.Justification) props.Justification = resolvedPara.Justification;
                p.Props = props;
                var destStyles = new StyleResolver(_dest);
                foreach (var (inline, resolved) in runs)
                {
                    var rp = inline.Props.Clone();
                    if (rp.StyleId != null) rp.StyleId = MapStyle(rp.StyleId, "character");
                    var now = destStyles.ResolveRun(rp, p);
                    if (now.AsciiFont != resolved.AsciiFont) rp.SetLatinFont(resolved.AsciiFont);
                    if (Math.Abs(now.Size - resolved.Size) > 0.01) rp.Size = (int)Math.Round(resolved.Size * 2);
                    if (now.Color != resolved.Color) rp.Color = resolved.Color is { } c ? $"{c.R:X2}{c.G:X2}{c.B:X2}" : "auto";
                    if (now.Bold != resolved.Bold) rp.Bold = resolved.Bold;
                    if (now.Italic != resolved.Italic) rp.Italic = resolved.Italic;
                    if (now.Underline != resolved.Underline) rp.Underline = resolved.Underline;
                    inline.Props = rp;
                }
            }
            p.Normalize();
        }

        /// <summary>Emphasis and meaning only (bold, italic, underline, strike, super/subscript, caps, direction, language, links, code).</summary>
        private RunProps MatchDestination(Inline inline, ResolvedRun resolved, ResolvedRun baseline, bool codeAllowed)
        {
            var rp = new RunProps();
            if (resolved.Bold != baseline.Bold) rp.Bold = resolved.Bold;
            if (resolved.Italic != baseline.Italic) rp.Italic = resolved.Italic;
            if (resolved.Underlined != baseline.Underlined && inline.Link == null) rp.Underline = resolved.Underlined ? "single" : "none";
            if (resolved.Strike != baseline.Strike) rp.Strike = resolved.Strike;
            if (resolved.SmallCaps != baseline.SmallCaps) rp.SmallCaps = resolved.SmallCaps;
            if (resolved.Caps != baseline.Caps) rp.Caps = resolved.Caps;
            if (resolved.VertAlign != 0) rp.VertAlign = resolved.VertAlign > 0 ? "superscript" : "subscript";
            if (resolved.Rtl) rp.Rtl = true;
            if (inline.Props.Lang is { } lang) rp.Lang = lang;
            if (inline.Props.LangBidi is { } bidi) rp.LangBidi = bidi;
            if (resolved.IsHidden) rp.Vanish = true;
            var style = inline.Props.StyleId is { } sid ? _source.Styles.Get(sid) : null;
            if (inline.Link != null && _dest.Styles.Get("Hyperlink") != null) rp.StyleId = "Hyperlink";
            else if (inline is NoteReference nr) rp.StyleId = nr.Kind == NoteKind.Footnote ? "FootnoteReference" : "EndnoteReference";
            else if (style != null && _dest.Styles.ByName(style.Name) is { Type: "character" } same && style.Name is "Strong" or "Emphasis" or "Hyperlink")
                rp.StyleId = same.Id;
            else if (codeAllowed && IsMonospace(resolved.AsciiFont) && _dest.Styles.Get("InlineCode") != null) rp.StyleId = "InlineCode";
            // Complex-script text (Urdu, Arabic…) keeps its font: the document's font may not have those letters.
            if (resolved.ComplexScript || resolved.Rtl)
            {
                rp.SetComplexFont(resolved.CsFont);
                if (Math.Abs(resolved.SizeCs - baseline.SizeCs) > 0.01) rp.SizeCs = (int)Math.Round(resolved.SizeCs * 2);
            }
            return rp;
        }

        private static bool IsMonospace(string font) => MonospaceFonts.Any(f => string.Equals(f, font, StringComparison.OrdinalIgnoreCase));

        /// <summary>The destination style for a source paragraph style when matching the destination (null: body text).</summary>
        private string? MapSemanticStyle(Style? sourceStyle)
        {
            if (sourceStyle == null) return null;
            var name = sourceStyle.Name;
            // Code blocks (by style name or id).
            if (name.IndexOf("code", StringComparison.OrdinalIgnoreCase) >= 0 || sourceStyle.Id is "HTMLPreformatted" or "Code")
                return _dest.Styles.Get("Code") != null ? "Code" : null;
            if (!SemanticStyles.Any(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase))) return null;
            if (_dest.Styles.ByName(name) is { Type: "paragraph" } same) return same.Id;
            // Deeper headings than the document has become its lowest heading.
            if (name.StartsWith("Heading", StringComparison.OrdinalIgnoreCase))
                for (var level = 9; level >= 1; level--)
                    if (_dest.Styles.Get("Heading" + level) != null && level <= 3)
                        return "Heading" + level;
            if (name.Equals("Intense Quote", StringComparison.OrdinalIgnoreCase) && _dest.Styles.Get("Quote") != null) return "Quote";
            return null;
        }

        /// <summary>A destination style for a source style id (keep source): same id if it exists, else a copy of the source style.</summary>
        private string? MapStyle(string id, string type)
        {
            if (_dest.Styles.Get(id) != null) return id;
            var style = _source.Styles.Get(id);
            if (style == null) return null;
            if (_dest.Styles.ByName(style.Name) is { } byName && byName.Type == type) return byName.Id;
            CopyStyle(style);
            return id;
        }

        private void CopyStyle(Style style)
        {
            if (!_copiedStyles.Add(style.Id) || _dest.Styles.Get(style.Id) != null) return;
            if (!_stylesTouched)
            {
                _t.TouchStyles();
                _stylesTouched = true;
            }
            if (style.BasedOn is { } basedOn && _dest.Styles.Get(basedOn) == null && _source.Styles.Get(basedOn) is { } parent) CopyStyle(parent);
            var copy = style.Clone();
            copy.X.Attribute(Ns.W + "default")?.Remove();
            _dest.Styles.Put(copy);
        }

        /// <summary>A new destination list per source list (standard bullets or numbers, Inkwell's indents).</summary>
        private int MapList(int sourceNumId, bool bullets)
        {
            if (_numbering.TryGetValue(sourceNumId, out var id)) return id;
            TouchNumbering();
            var abs = _dest.Numbering.AddAbstract(Numbering.StandardDefinition(bullets));
            id = _dest.Numbering.AddNum(abs);
            _numbering[sourceNumId] = id;
            return id;
        }

        /// <summary>A copy of the source's list definition (keep source).</summary>
        private int CopyList(int sourceNumId)
        {
            if (_numbering.TryGetValue(sourceNumId, out var id)) return id;
            TouchNumbering();
            var absId = _source.Numbering.AbstractOf(sourceNumId);
            var element = _source.Numbering.AbstractElement(absId);
            int newAbs;
            if (element != null)
            {
                var copy = new XElement(element);
                copy.Elements(Ns.W + "numStyleLink").Remove();
                copy.Elements(Ns.W + "styleLink").Remove();
                newAbs = _dest.Numbering.AddAbstract(copy);
            }
            else newAbs = _dest.Numbering.AddAbstract(Numbering.StandardDefinition(false));
            id = _dest.Numbering.AddNum(newAbs);
            _numbering[sourceNumId] = id;
            return id;
        }

        private void TouchNumbering()
        {
            if (_numberingTouched) return;
            _t.TouchNumbering();
            _numberingTouched = true;
        }
    }
}
