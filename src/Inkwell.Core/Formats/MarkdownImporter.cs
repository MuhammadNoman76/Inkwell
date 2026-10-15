using System.Globalization;
using Inkwell.Core.Model;
using Inkwell.Core.Templates;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Block = Inkwell.Core.Model.Block;
using Inline = Inkwell.Core.Model.Inline;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;

namespace Inkwell.Core.Formats;

/// <summary>
/// Markdown import (CommonMark with GitHub's pipe tables, ~~strikethrough~~, bare-URL autolinks, task lists and
/// footnotes), parsed by Markdig:
/// <list type="bullet">
/// <item>Headings → Heading 1–6. Heading 4–6 are added to the document (based on Heading 3) when it has none, so
/// the structure survives a round trip.</item>
/// <item>Paragraphs → Normal; block quotes → Quote; fenced and indented code → Code, one paragraph per line with the
/// whitespace kept; inline code → the Inline Code character style.</item>
/// <item>Bold, italic, ~~strike~~, and the inline HTML tags &lt;u&gt;, &lt;sup&gt;, &lt;sub&gt;, &lt;br&gt;; links →
/// hyperlinks with the Hyperlink character style ("#name" links point to bookmarks).</item>
/// <item>Pictures: local files (relative to the base folder) and data: URIs become in-line pictures, scaled to fit
/// 6.5" (or the text width); web pictures are left out and their alt text kept.</item>
/// <item>Bullet and numbered lists with nesting and start numbers; task lists become label-less list items starting
/// with ☐ / ☑.</item>
/// <item>Pipe tables → tables in the Inkwell Table style with a repeating header row and the column alignment;
/// thematic breaks → a divider (an empty paragraph with a bottom border); footnotes → real footnotes; raw HTML
/// blocks are converted like pasted HTML.</item>
/// </list>
/// </summary>
public static class MarkdownImporter
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .UseAutoLinks()
        .UseTaskLists()
        .UseFootnotes()
        .Build();

    /// <summary>Opens a Markdown file as a new document (pictures are found relative to the file's folder).</summary>
    public static Document ImportFile(string path, DocumentDesign? design = null)
    {
        var text = TextImporter.Decode(File.ReadAllBytes(path), out _);
        return Import(text, Path.GetDirectoryName(Path.GetFullPath(path)), design);
    }

    /// <summary>
    /// A new document from Markdown text, with the styles and theme of <paramref name="design"/> (the default design
    /// when null). Relative picture paths are resolved against <paramref name="baseFolder"/>.
    /// </summary>
    public static Document Import(string markdown, string? baseFolder = null, DocumentDesign? design = null)
    {
        design ??= DocumentDesigns.Default;
        var doc = NewDocument.Create(design);
        var result = new PasteResult();
        var converter = new MarkdownConverter(doc, baseFolder, design.RuleColor, result, MaxPictureWidth(doc));
        var parsed = Parse(markdown, result);
        doc.Body.Blocks.AddRange(parsed == null ? TextImporter.ToParagraphs(markdown, keepTrailingEmpty: false) : converter.Convert(parsed));
        doc.Warnings.AddRange(result.Warnings);
        NewDocument.Finish(doc);
        return doc;
    }

    private static long MaxPictureWidth(Document doc) => Math.Min(Content.TextWidth(doc) * Pictures.EmuPerTwip, 6 * Pictures.EmuPerInch + Pictures.EmuPerInch / 2);

    /// <summary>The parsed Markdown, or null (with a warning) when it is nested too deeply for Markdig.</summary>
    private static MarkdownDocument? Parse(string markdown, PasteResult result)
    {
        try
        {
            return Markdown.Parse(markdown, Pipeline);
        }
        catch (ArgumentException)
        {
            result.Warnings.Add("The Markdown is nested too deeply to read its structure, so it was read as plain text.");
            return null;
        }
    }

    /// <summary>
    /// Blocks from Markdown text for pasting into <paramref name="target"/>: they use its styles, and their lists,
    /// pictures and footnotes are added to it.
    /// </summary>
    public static PasteResult ImportFragment(string markdown, Document target, string? baseFolder = null)
    {
        var result = new PasteResult();
        var converter = new MarkdownConverter(target, baseFolder, "BFBFBF", result, MaxPictureWidth(target));
        var parsed = Parse(markdown, result);
        result.Blocks.AddRange(parsed == null ? TextImporter.ToParagraphs(markdown) : converter.Convert(parsed));
        return result;
    }
}

internal sealed class MarkdownConverter
{
    private readonly Document _doc;
    private readonly string? _baseFolder;
    private readonly string _ruleColor;
    private readonly PasteResult _result;
    private readonly long _maxImageWidth;
    private readonly StyleMap _styles;
    private readonly ListFactory _lists;
    private int _noteDepth;
    private bool _afterTask;

    public MarkdownConverter(Document doc, string? baseFolder, string ruleColor, PasteResult result, long maxImageWidth)
    {
        _doc = doc;
        _baseFolder = baseFolder;
        _ruleColor = ruleColor;
        _result = result;
        _maxImageWidth = maxImageWidth;
        _styles = new StyleMap(doc);
        _lists = new ListFactory(doc);
    }

    /// <summary>A list item whose first paragraph hasn't been made yet (it takes the item's number).</summary>
    private sealed class ItemState
    {
        public ItemState(int numId, int level)
        {
            NumId = numId;
            Level = level;
        }

        public int NumId { get; }
        public int Level { get; }
        public bool Consumed { get; set; }
    }

    private sealed class Ctx
    {
        public bool InQuote { get; init; }
        public int ListLevel { get; init; } = -1;
        public int? NumId { get; init; }
        public ListKind Kind { get; init; }
        public ItemState? Item { get; init; }

        public Ctx With(bool? inQuote = null) => new() { InQuote = inQuote ?? InQuote, ListLevel = ListLevel, NumId = NumId, Kind = Kind, Item = Item };
    }

    /// <summary>The formatting of the text being converted.</summary>
    private readonly struct RunState
    {
        public bool Bold { get; init; }
        public bool Italic { get; init; }
        public bool Strike { get; init; }
        public bool Code { get; init; }
        public Hyperlink? Link { get; init; }
    }

    /// <summary>Formatting switched by inline HTML tags (&lt;u&gt;…&lt;/u&gt;), which Markdig keeps as separate open / close inlines.</summary>
    private sealed class HtmlToggles
    {
        public int Underline;
        public int Sup;
        public int Sub;
        public int Bold;
        public int Italic;
        public int Strike;
        public int Code;
    }

    public List<Block> Convert(MarkdownDocument md)
    {
        var output = new List<Block>();
        foreach (var block in md) ConvertBlock(block, output, new Ctx());
        return output;
    }

    private void ConvertBlock(MdBlock block, List<Block> output, Ctx ctx)
    {
        switch (block)
        {
            case FootnoteGroup:
            case LinkReferenceDefinitionGroup:
            case BlankLineBlock:
                return;
            case HeadingBlock h:
            {
                var p = new Paragraph { Props = { StyleId = _styles.Heading(h.Level) } };
                AddInlines(p, h.Inline);
                ApplyItem(p, ctx, keepStyle: true);
                output.Add(p);
                return;
            }
            case ParagraphBlock pb:
            {
                var p = new Paragraph { Props = { StyleId = _styles.Get(ctx.InQuote ? "Quote" : "Normal") } };
                AddInlines(p, pb.Inline);
                ApplyItem(p, ctx, keepStyle: ctx.InQuote);
                output.Add(p);
                return;
            }
            case QuoteBlock q:
            {
                var inner = ctx.With(inQuote: true);
                foreach (var child in q) ConvertBlock(child, output, inner);
                return;
            }
            case ListBlock list:
                ConvertList(list, output, ctx);
                return;
            case CodeBlock code:
                ConvertCode(code, output, ctx);
                return;
            case ThematicBreakBlock:
            {
                var p = Content.Divider(_ruleColor);
                ApplyItem(p, ctx, keepStyle: true);
                output.Add(p);
                return;
            }
            case MdTable table:
                FlushItem(output, ctx);
                output.Add(ConvertTable(table));
                return;
            case HtmlBlock html:
            {
                var text = html.Lines.ToString();
                var converted = HtmlImporter.Import(text, _doc, new HtmlImportOptions());
                _result.ImagesImported += converted.ImagesImported;
                _result.ImagesSkipped += converted.ImagesSkipped;
                _result.SkippedImageUrls.AddRange(converted.SkippedImageUrls);
                foreach (var b in converted.Blocks)
                {
                    if (b is Paragraph hp) ApplyItem(hp, ctx, keepStyle: true);
                    output.Add(b);
                }
                return;
            }
            case ContainerBlock container:
                foreach (var child in container) ConvertBlock(child, output, ctx);
                return;
            case LeafBlock leaf when leaf.Inline != null:
            {
                var p = new Paragraph { Props = { StyleId = _styles.Get("Normal") } };
                AddInlines(p, leaf.Inline);
                ApplyItem(p, ctx, keepStyle: false);
                output.Add(p);
                return;
            }
        }
    }

    /// <summary>Gives a paragraph its list item's number (the item's first paragraph) or the item's indent (later ones).</summary>
    private void ApplyItem(Paragraph p, Ctx ctx, bool keepStyle)
    {
        var item = ctx.Item;
        if (item == null) return;
        if (!keepStyle) p.Props.StyleId = _styles.Get("ListParagraph");
        if (!item.Consumed)
        {
            p.Props.SetNumbering(item.NumId, item.Level);
            item.Consumed = true;
        }
        else
        {
            p.Props.IndentStart = ListFactory.LevelIndent(item.Level);
        }
    }

    /// <summary>A list item that starts with something other than a paragraph gets an empty numbered paragraph first.</summary>
    private void FlushItem(List<Block> output, Ctx ctx)
    {
        if (ctx.Item is not { Consumed: false } item) return;
        var p = new Paragraph { Props = { StyleId = _styles.Get("ListParagraph") } };
        p.Props.SetNumbering(item.NumId, item.Level);
        item.Consumed = true;
        output.Add(p);
    }

    private void ConvertList(ListBlock list, List<Block> output, Ctx ctx)
    {
        var level = Math.Min(ctx.ListLevel + 1, 8);
        var items = list.OfType<ListItemBlock>().ToList();
        var allTasks = !list.IsOrdered && items.Count > 0 && items.All(IsTaskItem);
        var kind = list.IsOrdered ? ListKind.Ordered : allTasks ? ListKind.Plain : ListKind.Bullet;
        var start = 1;
        if (list.IsOrdered && int.TryParse(list.OrderedStart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) start = s;
        int numId;
        if (ctx.NumId is { } parent && ctx.Kind == kind && (kind != ListKind.Ordered || start == 1)) numId = parent;
        else numId = _lists.NewList(kind, level, start);
        foreach (var item in items)
        {
            var state = new ItemState(numId, level);
            var inner = new Ctx { InQuote = ctx.InQuote, ListLevel = level, NumId = numId, Kind = kind, Item = state };
            foreach (var child in item) ConvertBlock(child, output, inner);
            FlushItem(output, inner);
        }
    }

    private static bool IsTaskItem(ListItemBlock item) =>
        item.Count > 0 && item[0] is ParagraphBlock { Inline.FirstChild: TaskList };

    private void ConvertCode(CodeBlock code, List<Block> output, Ctx ctx)
    {
        var lines = new List<string>();
        var group = code.Lines;
        for (var i = 0; i < group.Count; i++) lines.Add(group.Lines[i].Slice.ToString());
        if (code is not FencedCodeBlock)
            while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        if (lines.Count == 0) lines.Add("");
        var style = _styles.Get("Code");
        var first = true;
        foreach (var line in lines)
        {
            var p = new Paragraph(Content.CleanText(line.TrimEnd('\r', '\n')), style);
            if (ctx.Item != null)
            {
                if (first && !ctx.Item.Consumed) ApplyItem(p, ctx, keepStyle: true);
                else p.Props.IndentStart = ListFactory.LevelIndent(ctx.Item.Level);
            }
            first = false;
            output.Add(p);
        }
    }

    private Model.Table ConvertTable(MdTable source)
    {
        var rows = source.OfType<MdTableRow>().ToList();
        var columns = Math.Max(1, rows.Count == 0 ? 1 : rows.Max(r => r.OfType<MdTableCell>().Sum(c => Math.Max(1, c.ColumnSpan))));
        var width = Content.TextWidth(_doc);
        var colWidth = width / columns;
        var table = new Model.Table();
        table.Props.StyleId = _styles.Get("InkwellTable");
        table.Props.Width = new TableWidth("pct", 5000);
        table.Props.Look = (true, false, false, false, true, false);
        for (var i = 0; i < columns; i++) table.Grid.Add(colWidth);
        var headerZone = true;
        foreach (var row in rows)
        {
            var tr = new Model.TableRow();
            if (row.IsHeader && headerZone) tr.Props.IsHeader = true;
            else headerZone = false;
            var col = 0;
            foreach (var cell in row.OfType<MdTableCell>())
            {
                var span = Math.Max(1, cell.ColumnSpan);
                var tc = new Model.TableCell();
                tc.Props.Width = new TableWidth("dxa", colWidth * span);
                if (span > 1) tc.Props.GridSpan = span;
                var blocks = new List<Block>();
                foreach (var child in cell) ConvertBlock(child, blocks, new Ctx());
                if (blocks.Count == 0 || blocks[blocks.Count - 1] is not Paragraph) blocks.Add(new Paragraph());
                var align = col < source.ColumnDefinitions.Count ? source.ColumnDefinitions[col].Alignment : null;
                foreach (var p in blocks.OfType<Paragraph>())
                {
                    p.Props.Justification = align switch
                    {
                        TableColumnAlign.Center => "center",
                        TableColumnAlign.Right => "right",
                        TableColumnAlign.Left => "left",
                        _ => null,
                    };
                }
                tc.Blocks.AddRange(blocks);
                tr.AddCell(tc);
                col += span;
            }
            for (; col < columns; col++) tr.AddCell(Model.TableCell.Create(colWidth));
            table.AddRow(tr);
        }
        if (table.Rows.Count == 0)
        {
            var tr = new Model.TableRow();
            tr.AddCell(Model.TableCell.Create(colWidth));
            table.AddRow(tr);
        }
        return table;
    }

    // ------------------------------------------------------------------ inlines

    private void AddInlines(Paragraph p, ContainerInline? container)
    {
        if (container == null) return;
        ConvertInlines(container, p.Inlines, new RunState(), new HtmlToggles());
        // Markdown drops leading and trailing spaces of a paragraph; soft breaks at the ends become nothing.
        if (p.Inlines.Count > 0 && p.Inlines[p.Inlines.Count - 1] is TextRun last)
        {
            last.Text = last.Text.TrimEnd(' ');
            if (last.Text.Length == 0) p.Inlines.RemoveAt(p.Inlines.Count - 1);
        }
        p.Normalize();
    }

    private void ConvertInlines(ContainerInline container, List<Inline> output, RunState state, HtmlToggles html)
    {
        foreach (var inline in container) ConvertInline(inline, output, state, html);
    }

    private void ConvertInline(MdInline inline, List<Inline> output, RunState state, HtmlToggles html)
    {
        switch (inline)
        {
            case LiteralInline lit:
                AddText(output, lit.Content.ToString(), state, html);
                break;
            case EmphasisInline em:
            {
                var next = state;
                if (em.DelimiterChar == '~') next = new RunState { Bold = state.Bold, Italic = state.Italic, Strike = true, Code = state.Code, Link = state.Link };
                else if (em.DelimiterCount >= 2) next = new RunState { Bold = true, Italic = state.Italic || em.DelimiterCount >= 3, Strike = state.Strike, Code = state.Code, Link = state.Link };
                else next = new RunState { Bold = state.Bold, Italic = true, Strike = state.Strike, Code = state.Code, Link = state.Link };
                ConvertInlines(em, output, next, html);
                break;
            }
            case CodeInline code:
                AddText(output, code.Content, new RunState { Bold = state.Bold, Italic = state.Italic, Strike = state.Strike, Code = true, Link = state.Link }, html);
                break;
            case LinkInline link when link.IsImage:
                AddImage(output, link, state);
                break;
            case LinkInline link:
            {
                var target = MakeLink(link.Url, link.Title);
                var next = new RunState { Bold = state.Bold, Italic = state.Italic, Strike = state.Strike, Code = state.Code, Link = target ?? state.Link };
                if (link.FirstChild == null) AddText(output, link.Url ?? "", next, html);
                else ConvertInlines(link, output, next, html);
                break;
            }
            case AutolinkInline auto:
            {
                var url = auto.IsEmail && !auto.Url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? "mailto:" + auto.Url : auto.Url;
                var next = new RunState { Bold = state.Bold, Italic = state.Italic, Strike = state.Strike, Code = state.Code, Link = MakeLink(url, null) };
                AddText(output, auto.Url, next, html);
                break;
            }
            case LineBreakInline br:
                AddText(output, br.IsHard ? TextRun.LineBreak.ToString() : " ", state, html);
                break;
            case HtmlEntityInline entity:
                AddText(output, entity.Transcoded.ToString(), state, html);
                break;
            case HtmlInline tag:
                ApplyHtmlTag(tag.Tag, output, state, html);
                break;
            case TaskList task:
                AddText(output, task.Checked ? "☑ " : "☐ ", new RunState(), new HtmlToggles());
                _afterTask = true;
                break;
            case FootnoteLink { IsBackLink: true }:
                break;
            case FootnoteLink fl:
                AddFootnote(output, fl.Footnote);
                break;
            case DelimiterInline delimiter:
                AddText(output, delimiter.ToLiteral(), state, html);
                ConvertInlines(delimiter, output, state, html);
                break;
            case ContainerInline other:
                ConvertInlines(other, output, state, html);
                break;
        }
    }

    private void ApplyHtmlTag(string tag, List<Inline> output, RunState state, HtmlToggles html)
    {
        var t = tag.Trim();
        if (t.StartsWith("<!--", StringComparison.Ordinal)) return;
        var closing = t.StartsWith("</", StringComparison.Ordinal);
        var name = new string(t.TrimStart('<', '/').TakeWhile(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var delta = closing ? -1 : 1;
        switch (name)
        {
            case "br":
                AddText(output, TextRun.LineBreak.ToString(), state, html);
                break;
            case "u":
            case "ins":
                html.Underline = Math.Max(0, html.Underline + delta);
                break;
            case "sup":
                html.Sup = Math.Max(0, html.Sup + delta);
                break;
            case "sub":
                html.Sub = Math.Max(0, html.Sub + delta);
                break;
            case "b":
            case "strong":
                html.Bold = Math.Max(0, html.Bold + delta);
                break;
            case "i":
            case "em":
                html.Italic = Math.Max(0, html.Italic + delta);
                break;
            case "s":
            case "del":
            case "strike":
                html.Strike = Math.Max(0, html.Strike + delta);
                break;
            case "code":
            case "kbd":
                html.Code = Math.Max(0, html.Code + delta);
                break;
        }
    }

    private RunProps PropsFor(RunState state, HtmlToggles html)
    {
        var props = new RunProps();
        if (state.Link != null) props.StyleId = _styles.Get("Hyperlink");
        else if (state.Code || html.Code > 0) props.StyleId = _styles.Get("InlineCode");
        if (state.Bold || html.Bold > 0) props.Bold = true;
        if (state.Italic || html.Italic > 0) props.Italic = true;
        if (state.Strike || html.Strike > 0) props.Strike = true;
        if (html.Underline > 0) props.Underline = "single";
        if (html.Sup > 0) props.VertAlign = "superscript";
        else if (html.Sub > 0) props.VertAlign = "subscript";
        return props;
    }

    private void AddText(List<Inline> output, string text, RunState state, HtmlToggles html)
    {
        text = Content.CleanText(text.Replace('\n', ' ').Replace('\r', ' '));
        if (_afterTask && !text.StartsWith("☐", StringComparison.Ordinal) && !text.StartsWith("☑", StringComparison.Ordinal))
        {
            // Markdig keeps the space after "[ ]" in the text that follows the box.
            text = text.TrimStart(' ');
            _afterTask = false;
        }
        if (text.Length == 0) return;
        Content.AppendText(output, text, PropsFor(state, html), state.Link);
    }

    private static Hyperlink? MakeLink(string? url, string? title)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var link = new Hyperlink { Tooltip = string.IsNullOrEmpty(title) ? null : title };
        if (url!.StartsWith("#", StringComparison.Ordinal))
        {
            link.Anchor = Uri.UnescapeDataString(url.Substring(1));
            if (link.Anchor.Length == 0) return null;
        }
        else
        {
            link.Url = url;
        }
        return link;
    }

    private void AddImage(List<Inline> output, LinkInline link, RunState state)
    {
        var alt = PlainText(link);
        var url = link.Url ?? "";
        byte[]? data = null;
        var web = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                  url.StartsWith("//", StringComparison.Ordinal);
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) data = Pictures.DecodeDataUri(url);
        else if (Pictures.FileUrlToPath(url) is { } filePath) data = Pictures.ReadFile(filePath);
        else if (!web && url.Length > 0) data = ReadRelative(url);
        var drawing = data == null ? null : Pictures.Create(_doc, data, alt, _maxImageWidth, title: link.Title);
        if (drawing == null)
        {
            _result.ImagesSkipped++;
            if (web) _result.SkippedImageUrls.Add(url);
            if (alt.Length > 0) AddText(output, alt, state, new HtmlToggles());
            return;
        }
        _result.ImagesImported++;
        drawing.Link = state.Link;
        output.Add(drawing);
    }

    private byte[]? ReadRelative(string url)
    {
        foreach (var candidate in new[] { url, SafeUnescape(url) }.Distinct())
        {
            try
            {
                var path = Path.IsPathRooted(candidate) ? candidate : _baseFolder != null ? Path.Combine(_baseFolder, candidate.Replace('/', Path.DirectorySeparatorChar)) : null;
                if (path == null) continue;
                var data = Pictures.ReadFile(path);
                if (data != null) return data;
            }
            catch (ArgumentException)
            {
                // Characters that can't be in a path.
            }
        }
        return null;
    }

    private static string SafeUnescape(string s)
    {
        try
        {
            return Uri.UnescapeDataString(s);
        }
        catch (UriFormatException)
        {
            return s;
        }
    }

    private static string PlainText(ContainerInline container)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline lit:
                    sb.Append(lit.Content.ToString());
                    break;
                case CodeInline code:
                    sb.Append(code.Content);
                    break;
                case HtmlEntityInline e:
                    sb.Append(e.Transcoded.ToString());
                    break;
                case LineBreakInline:
                    sb.Append(' ');
                    break;
                case ContainerInline c:
                    sb.Append(PlainText(c));
                    break;
            }
        }
        return sb.ToString();
    }

    private void AddFootnote(List<Inline> output, Footnote footnote)
    {
        if (_noteDepth > 2) return;
        _noteDepth++;
        try
        {
            var blocks = new List<Block>();
            foreach (var child in footnote) ConvertBlock(child, blocks, new Ctx());
            var note = NoteFactory.Create(_doc, _styles, NoteKind.Footnote, blocks);
            _result.AddedNotes.Add(note);
            output.Add(NoteFactory.Reference(_styles, NoteKind.Footnote, note.Id));
        }
        finally
        {
            _noteDepth--;
        }
    }
}
