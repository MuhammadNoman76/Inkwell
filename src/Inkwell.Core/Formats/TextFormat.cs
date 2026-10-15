using System.Globalization;
using System.Text;
using Inkwell.Core.Model;
using Inkwell.Core.Templates;

namespace Inkwell.Core.Formats;

/// <summary>New documents for the importers.</summary>
internal static class NewDocument
{
    /// <summary>An empty document with a design's styles and theme (the default design when null).</summary>
    public static Document Create(DocumentDesign? design)
    {
        design ??= DocumentDesigns.Default;
        var doc = new Document
        {
            Styles = design.BuildStyleSheet(),
            Theme = design.BuildTheme(),
        };
        doc.Numbering.Styles = doc.Styles;
        return doc;
    }

    /// <summary>Makes sure the body ends with a paragraph (Word requires one after a final table).</summary>
    public static void Finish(Document doc)
    {
        if (doc.Body.Blocks.Count == 0 || doc.Body.Blocks[doc.Body.Blocks.Count - 1] is not Paragraph) doc.Body.Blocks.Add(new Paragraph());
    }
}

/// <summary>
/// Plain text import: detects the encoding (byte order mark, valid UTF-8, UTF-16 without a mark, else the
/// system's ANSI code page) and makes one paragraph per line, keeping tabs.
/// </summary>
public static class TextImporter
{
    /// <summary>Opens a text file as a new document.</summary>
    public static Document ImportFile(string path, DocumentDesign? design = null) => Import(File.ReadAllBytes(path), design);

    /// <summary>A new document from the bytes of a text file.</summary>
    public static Document Import(byte[] bytes, DocumentDesign? design = null, int? fallbackCodePage = null) =>
        ImportText(Decode(bytes, out _, fallbackCodePage), design);

    /// <summary>A new document with one Normal paragraph per line of <paramref name="text"/>.</summary>
    public static Document ImportText(string text, DocumentDesign? design = null)
    {
        var doc = NewDocument.Create(design);
        doc.Body.Blocks.AddRange(ToParagraphs(text, keepTrailingEmpty: false));
        NewDocument.Finish(doc);
        return doc;
    }

    /// <summary>
    /// Paragraphs for pasting plain text: one per line (CR LF, LF, CR, U+2029), tabs kept, other control characters
    /// removed, a form feed kept as a page break. A trailing line break gives a final empty paragraph when
    /// <paramref name="keepTrailingEmpty"/> (so the text after the caret starts a new paragraph, as when typing).
    /// </summary>
    public static List<Paragraph> ToParagraphs(string text, bool keepTrailingEmpty = true)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\u2029', '\n').Replace('\u0085', '\n').Replace('\u2028', TextRun.LineBreak);
        var lines = normalized.Split('\n');
        var count = lines.Length;
        if (!keepTrailingEmpty && count > 1 && lines[count - 1].Length == 0) count--;
        var result = new List<Paragraph>(count);
        for (var i = 0; i < count; i++) result.Add(new Paragraph(Content.CleanText(lines[i])));
        return result;
    }

    /// <summary>The text of a file's bytes, with the encoding that was detected.</summary>
    public static string Decode(byte[] bytes, out Encoding encoding, int? fallbackCodePage = null)
    {
        encoding = DetectEncoding(bytes, fallbackCodePage, out var bomLength);
        return encoding.GetString(bytes, bomLength, bytes.Length - bomLength);
    }

    /// <summary>The encoding of a text file's bytes.</summary>
    public static Encoding DetectEncoding(byte[] bytes, int? fallbackCodePage = null) => DetectEncoding(bytes, fallbackCodePage, out _);

    private static Encoding DetectEncoding(byte[] b, int? fallbackCodePage, out int bomLength)
    {
        bomLength = 0;
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
        {
            bomLength = 3;
            return new UTF8Encoding(true);
        }
        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xFE && b[2] == 0 && b[3] == 0)
        {
            bomLength = 4;
            return new UTF32Encoding(false, true);
        }
        if (b.Length >= 4 && b[0] == 0 && b[1] == 0 && b[2] == 0xFE && b[3] == 0xFF)
        {
            bomLength = 4;
            return new UTF32Encoding(true, true);
        }
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
        {
            bomLength = 2;
            return new UnicodeEncoding(false, true);
        }
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
        {
            bomLength = 2;
            return new UnicodeEncoding(true, true);
        }
        if (LooksLikeUtf16(b, out var bigEndian)) return new UnicodeEncoding(bigEndian, false);
        try
        {
            new UTF8Encoding(false, true).GetCharCount(b);
            return new UTF8Encoding(false);
        }
        catch (DecoderFallbackException)
        {
            return TextEncodings.Get(fallbackCodePage ?? TextEncodings.AnsiCodePage);
        }
    }

    /// <summary>UTF-16 without a byte order mark: mostly-ASCII text has a zero in every other byte.</summary>
    private static bool LooksLikeUtf16(byte[] b, out bool bigEndian)
    {
        bigEndian = false;
        var n = Math.Min(b.Length, 4096) & ~1;
        if (n < 4) return false;
        int evenZeros = 0, oddZeros = 0;
        for (var i = 0; i < n; i += 2)
        {
            if (b[i] == 0) evenZeros++;
            if (b[i + 1] == 0) oddZeros++;
        }
        var pairs = n / 2;
        if (oddZeros > pairs * 0.4 && evenZeros < pairs * 0.05) return true;
        if (evenZeros > pairs * 0.4 && oddZeros < pairs * 0.05)
        {
            bigEndian = true;
            return true;
        }
        return false;
    }
}

/// <summary>Options for <see cref="TextExporter"/>.</summary>
public sealed class TextExportOptions
{
    /// <summary>Line ending (Windows' CR LF by default).</summary>
    public string NewLine { get; set; } = "\r\n";

    /// <summary>Write a UTF-8 byte order mark (for programs that otherwise assume the ANSI code page).</summary>
    public bool IncludeBom { get; set; }

    /// <summary>Write list labels ("1. ", "a. ", "• ") before list paragraphs, indented by level.</summary>
    public bool ListLabels { get; set; } = true;

    /// <summary>Append footnotes and endnotes after the text (references become "[1]").</summary>
    public bool IncludeNotes { get; set; } = true;
}

/// <summary>
/// Plain text export: one line per paragraph with list labels, tables as tab-separated lines, footnotes and
/// endnotes at the end, CR LF line endings, UTF-8 (optionally with a byte order mark).
/// </summary>
public static class TextExporter
{
    /// <summary>The document's text.</summary>
    public static string Export(Document doc, TextExportOptions? options = null) => ExportBlocks(doc, doc.Body.Blocks, options);

    /// <summary>The text of some blocks of a document (for the clipboard's plain text).</summary>
    public static string ExportBlocks(Document doc, IEnumerable<Block> blocks, TextExportOptions? options = null)
    {
        options ??= new TextExportOptions();
        var writer = new Writer(doc, options);
        writer.WriteBlocks(blocks);
        writer.WriteNotes();
        return writer.ToString();
    }

    /// <summary>The document's text as UTF-8 bytes (with a byte order mark when <see cref="TextExportOptions.IncludeBom"/>).</summary>
    public static byte[] ExportBytes(Document doc, TextExportOptions? options = null)
    {
        options ??= new TextExportOptions();
        var text = Export(doc, options);
        var body = Encoding.UTF8.GetBytes(text);
        if (!options.IncludeBom) return body;
        var result = new byte[body.Length + 3];
        result[0] = 0xEF;
        result[1] = 0xBB;
        result[2] = 0xBF;
        Buffer.BlockCopy(body, 0, result, 3, body.Length);
        return result;
    }

    /// <summary>Saves the document as a text file.</summary>
    public static void ExportToFile(Document doc, string path, TextExportOptions? options = null) => File.WriteAllBytes(path, ExportBytes(doc, options));

    private sealed class Writer
    {
        private readonly Document _doc;
        private readonly TextExportOptions _options;
        private readonly StringBuilder _sb = new();
        private readonly ListTracker _lists;
        private readonly List<(Note Note, string Label)> _notes = new();
        private int _footnoteCount;
        private int _endnoteCount;
        private bool _hasLine;

        public Writer(Document doc, TextExportOptions options)
        {
            _doc = doc;
            _options = options;
            _lists = new ListTracker(doc);
        }

        public override string ToString() => _sb.ToString();

        private void Line(string text)
        {
            if (_hasLine) _sb.Append(_options.NewLine);
            _sb.Append(text.Replace("\n", _options.NewLine));
            _hasLine = true;
        }

        public void WriteBlocks(IEnumerable<Block> blocks)
        {
            foreach (var block in blocks)
            {
                switch (block)
                {
                    case Paragraph p:
                        WriteParagraph(p);
                        break;
                    case Table t:
                        WriteTable(t);
                        break;
                    case SdtBlock s:
                        WriteBlocks(s.Blocks);
                        break;
                    case FieldBlock f:
                        WriteBlocks(f.Blocks);
                        break;
                    case OpaqueBlock o when o.Display != null:
                        WriteBlocks(o.Display);
                        break;
                }
            }
        }

        private void WriteParagraph(Paragraph p)
        {
            var text = ParagraphText(p);
            var label = _lists.Next(p);
            if (label != null && _options.ListLabels && label.Text.Length > 0)
                text = new string(' ', label.Level * 2) + label.Text + " " + text;
            Line(text);
        }

        private void WriteTable(Table t)
        {
            foreach (var row in t.Rows)
            {
                var cells = new List<string>();
                foreach (var cell in row.Cells)
                {
                    if (cell.Props.VerticalMerge == "continue")
                    {
                        cells.Add("");
                        continue;
                    }
                    cells.Add(CellText(cell.Blocks));
                    for (var i = 1; i < cell.Props.GridSpan; i++) cells.Add("");
                }
                Line(string.Join("\t", cells));
            }
        }

        private string CellText(IEnumerable<Block> blocks)
        {
            var parts = new List<string>();
            foreach (var b in blocks)
            {
                switch (b)
                {
                    case Paragraph p:
                        var label = _lists.Next(p);
                        var text = ParagraphText(p).Replace("\n", " ");
                        if (label != null && _options.ListLabels && label.Text.Length > 0) text = label.Text + " " + text;
                        if (text.Length > 0) parts.Add(text);
                        break;
                    case Table inner:
                        foreach (var r in inner.Rows)
                            foreach (var c in r.Cells)
                                if (CellText(c.Blocks) is { Length: > 0 } s) parts.Add(s);
                        break;
                    case SdtBlock s2:
                        if (CellText(s2.Blocks) is { Length: > 0 } st) parts.Add(st);
                        break;
                }
            }
            return string.Join(" ", parts).Replace("\t", " ");
        }

        private string ParagraphText(Paragraph p)
        {
            var sb = new StringBuilder();
            foreach (var inline in p.Inlines) AppendInline(sb, inline);
            return sb.ToString();
        }

        private void AppendInline(StringBuilder sb, Inline inline)
        {
            if (inline.Deleted != null || inline.Props.Vanish == true) return;
            switch (inline)
            {
                case TextRun t:
                    foreach (var c in t.Text)
                    {
                        switch (c)
                        {
                            case TextRun.LineBreak:
                            case TextRun.PageBreak:
                            case TextRun.ColumnBreak:
                                sb.Append('\n');
                                break;
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
                    break;
                case FieldInline f:
                    foreach (var r in f.Result) AppendInline(sb, r);
                    break;
                case SymbolChar s:
                    sb.Append(s.Char >= '\uF000' ? ListFactory.NormalizeBullet(s.Char.ToString()) : s.Char.ToString());
                    break;
                case NoteReference r when _options.IncludeNotes:
                    var note = _doc.FindNote(r.Kind, r.Id);
                    if (note == null) break;
                    string label;
                    if (r.Kind == NoteKind.Footnote) label = NumberFormats.Format(_doc.Settings.FootnoteStart + _footnoteCount++, _doc.Settings.FootnoteFormat);
                    else label = NumberFormats.Format(_doc.Settings.EndnoteStart + _endnoteCount++, _doc.Settings.EndnoteFormat);
                    sb.Append('[').Append(label).Append(']');
                    _notes.Add((note, label));
                    break;
                case OpaqueInline o when !o.IsMarker:
                    sb.Append(o.Display);
                    break;
            }
        }

        public void WriteNotes()
        {
            if (_notes.Count == 0) return;
            var footnotes = _notes.Where(n => n.Note.NoteKind == NoteKind.Footnote).ToList();
            var endnotes = _notes.Where(n => n.Note.NoteKind == NoteKind.Endnote).ToList();
            foreach (var group in new[] { footnotes, endnotes })
            {
                if (group.Count == 0) continue;
                Line("");
                Line("__________");
                foreach (var (note, label) in group)
                {
                    var first = true;
                    foreach (var p in note.Paragraphs)
                    {
                        var text = string.Concat(p.Inlines.Where(i => i is not NoteMark).Select(InlineText)).TrimStart();
                        Line(first ? "[" + label + "] " + text : text);
                        first = false;
                    }
                }
            }
        }

        private string InlineText(Inline inline)
        {
            var sb = new StringBuilder();
            if (inline is NoteReference) return "";
            AppendInline(sb, inline);
            return sb.ToString();
        }
    }
}

/// <summary>Shared text helpers for the exporters.</summary>
internal static class ExportText
{
    public static string Invariant(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
