using System.Text.RegularExpressions;
using System.Xml.Linq;
using Inkwell.Core.Templates;

namespace Inkwell.Core.Model;

/// <summary>
/// Builds documents in code (templates, samples, tests). Text may use light markup: **bold**, *italic*,
/// `code` and [text](https://link).
/// </summary>
public sealed class DocBuilder
{
    private int? _bulletList;
    private int? _numberList;

    public DocBuilder(DocumentDesign? design = null, bool letterPaper = false)
    {
        Design = design ?? DocumentDesigns.Default;
        Doc = new Document
        {
            Styles = Design.BuildStyleSheet(),
            Theme = Design.BuildTheme(),
        };
        Doc.Numbering.Styles = Doc.Styles;
        Doc.Body.FinalSection = SectProps.CreateDefault(letterPaper);
        Doc.Settings.SetVariable("InkwellDesign", Design.Id);
    }

    public Document Doc { get; }

    public DocumentDesign Design { get; }

    public BlockList Body => Doc.Body.Blocks;

    // ------------------------------------------------------------------ text

    /// <summary>Inlines from light markup: **bold**, *italic*, `code`, [text](url).</summary>
    public static List<Inline> Inlines(string text)
    {
        var result = new List<Inline>();
        var pattern = new Regex(@"\*\*(.+?)\*\*|\*(.+?)\*|`(.+?)`|\[(.+?)\]\((.+?)\)");
        var pos = 0;
        foreach (Match m in pattern.Matches(text))
        {
            if (m.Index > pos) result.Add(new TextRun(text.Substring(pos, m.Index - pos)));
            if (m.Groups[1].Success)
            {
                var r = new TextRun(m.Groups[1].Value);
                r.Props.Bold = true;
                result.Add(r);
            }
            else if (m.Groups[2].Success)
            {
                var r = new TextRun(m.Groups[2].Value);
                r.Props.Italic = true;
                result.Add(r);
            }
            else if (m.Groups[3].Success)
            {
                var r = new TextRun(m.Groups[3].Value);
                r.Props.StyleId = "InlineCode";
                result.Add(r);
            }
            else
            {
                var link = new Hyperlink { Url = m.Groups[5].Value };
                var r = new TextRun(m.Groups[4].Value) { Link = link };
                r.Props.StyleId = "Hyperlink";
                result.Add(r);
            }
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) result.Add(new TextRun(text.Substring(pos)));
        return result;
    }

    public Paragraph P(string text, string? style = null)
    {
        var p = new Paragraph();
        if (style != null) p.Props.StyleId = style;
        p.Inlines.AddRange(Inlines(text));
        Body.Add(p);
        EndLists();
        return p;
    }

    public Paragraph Title(string text) => P(text, "Title");

    public Paragraph Subtitle(string text) => P(text, "Subtitle");

    public Paragraph Heading(int level, string text) => P(text, "Heading" + MathEx.Clamp(level, 1, 3));

    public Paragraph Quote(string text) => P(text, "Quote");

    public Paragraph Caption(string text) => P(text, "Caption");

    public void Code(params string[] lines)
    {
        foreach (var l in lines)
        {
            var p = new Paragraph(l, "Code");
            Body.Add(p);
        }
        EndLists();
    }

    /// <summary>An empty paragraph with a rule under it (Inkwell's divider).</summary>
    public Paragraph Divider()
    {
        var p = new Paragraph();
        p.Props.SetBorder("bottom", new BorderDef("single", 6, 1, "D9D6CF"));
        p.Props.SpacingAfter = 240;
        Body.Add(p);
        EndLists();
        return p;
    }

    public void PageBreak()
    {
        var p = new Paragraph();
        p.Inlines.Add(new TextRun("\f"));
        Body.Add(p);
    }

    private void EndLists()
    {
        _bulletList = null;
        _numberList = null;
    }

    /// <summary>A bulleted list; items may start with spaces (two per level) to nest.</summary>
    public void Bullets(params string[] items)
    {
        _bulletList ??= NewList(true);
        foreach (var item in items) ListItem(_bulletList.Value, item);
        var keep = _bulletList;
        _numberList = null;
        _bulletList = keep;
    }

    /// <summary>A numbered list (continues the previous numbered list unless <paramref name="restart"/>).</summary>
    public void Numbered(bool restart, params string[] items)
    {
        if (restart || _numberList == null) _numberList = NewList(false);
        foreach (var item in items) ListItem(_numberList.Value, item);
        var keep = _numberList;
        _bulletList = null;
        _numberList = keep;
    }

    public int NewList(bool bullets)
    {
        var abs = Doc.Numbering.AddAbstract(Numbering.StandardDefinition(bullets));
        return Doc.Numbering.AddNum(abs);
    }

    private void ListItem(int numId, string item)
    {
        var level = 0;
        while (level * 2 < item.Length && item.Substring(level * 2).StartsWith("  ", StringComparison.Ordinal)) level++;
        var p = new Paragraph { Props = { StyleId = "ListParagraph" } };
        p.Props.SetNumbering(numId, level);
        p.Inlines.AddRange(Inlines(item.TrimStart()));
        Body.Add(p);
    }

    // ------------------------------------------------------------------ tables

    /// <summary>A table filling the text width; the first row is a repeated header row when <paramref name="header"/>.</summary>
    public Table Table(IList<string[]> rows, bool header = true, string style = "InkwellTable", int[]? columnWidths = null)
    {
        var columns = rows.Max(r => r.Length);
        var textWidth = TextWidthTwips();
        var widths = columnWidths ?? Enumerable.Repeat(textWidth / columns, columns).ToArray();
        var t = new Table();
        t.Props.StyleId = style;
        t.Props.Width = new TableWidth("dxa", widths.Sum());
        t.Props.Layout = "fixed";
        t.Props.Look = (header, false, false, false, true, false);
        t.Grid = widths.ToList();
        for (var r = 0; r < rows.Count; r++)
        {
            var row = new TableRow();
            if (header && r == 0) row.Props.IsHeader = true;
            for (var c = 0; c < columns; c++)
            {
                var text = c < rows[r].Length ? rows[r][c] : "";
                var p = new Paragraph();
                p.Inlines.AddRange(Inlines(text));
                row.AddCell(TableCell.Create(widths[c], p));
            }
            t.AddRow(row);
        }
        Body.Add(t);
        EndLists();
        return t;
    }

    public int TextWidthTwips()
    {
        var s = Doc.Body.FinalSection;
        return s.PageWidth - s.MarginLeft - s.MarginRight - s.Gutter;
    }

    // ------------------------------------------------------------------ notes, pictures, sections

    /// <summary>Adds a footnote (or endnote) at the end of a paragraph.</summary>
    public Note Note(Paragraph p, string text, NoteKind kind = NoteKind.Footnote)
    {
        EnsureSeparators(kind);
        var id = Doc.NextNoteId(kind);
        var note = new Note(kind) { Id = id };
        var np = new Paragraph { Props = { StyleId = kind == NoteKind.Footnote ? "FootnoteText" : "EndnoteText" } };
        var mark = new NoteMark { Kind = kind };
        mark.Props.StyleId = kind == NoteKind.Footnote ? "FootnoteReference" : "EndnoteReference";
        np.Inlines.Add(mark);
        np.Inlines.Add(new TextRun(" "));
        np.Inlines.AddRange(Inlines(text));
        note.Blocks.Add(np);
        (kind == NoteKind.Footnote ? Doc.Footnotes : Doc.Endnotes)[id] = note;
        var reference = new NoteReference { Kind = kind, Id = id };
        reference.Props.StyleId = kind == NoteKind.Footnote ? "FootnoteReference" : "EndnoteReference";
        p.Inlines.Add(reference);
        return note;
    }

    /// <summary>The separator notes Word expects (ids -1 and 0) when the first note is added.</summary>
    public static void EnsureSeparators(Document doc, NoteKind kind)
    {
        var notes = kind == NoteKind.Footnote ? doc.Footnotes : doc.Endnotes;
        if (notes.Values.Any(n => n.Type == "separator")) return;
        Note Special(int id, string type, bool continuation)
        {
            var n = new Note(kind) { Id = id, Type = type };
            var p = new Paragraph();
            p.Props.SpacingAfter = 0;
            p.Props.SpacingLine = 240;
            p.Props.SpacingLineRule = "auto";
            p.Inlines.Add(new NoteSeparator { Continuation = continuation });
            n.Blocks.Add(p);
            return n;
        }
        notes[-1] = Special(-1, "separator", false);
        notes[0] = Special(0, "continuationSeparator", true);
        doc.Settings.SetSpecialNotes(kind, new[] { -1, 0 });
    }

    private void EnsureSeparators(NoteKind kind) => EnsureSeparators(Doc, kind);

    /// <summary>A picture in line with text, <paramref name="widthInches"/> wide (height from the picture's aspect).</summary>
    public DrawingObject Picture(Paragraph p, byte[] data, double widthInches, double? heightInches = null, string? alt = null)
    {
        var id = Doc.Media.Add(data);
        var size = Rendering.MediaImages.Measure(data);
        var aspect = size.Width > 0 ? (double)size.Height / size.Width : 0.66;
        var d = new DrawingObject
        {
            MediaId = id,
            Width = (long)(widthInches * 914400),
            Height = (long)((heightInches ?? widthInches * aspect) * 914400),
            AltText = alt,
            Name = "Picture",
        };
        p.Inlines.Add(d);
        return d;
    }

    /// <summary>A floating picture anchored in a paragraph, positioned relative to the margin (it stays put when text changes).</summary>
    public DrawingObject FloatingPicture(Paragraph p, byte[] data, double widthInches, double xInches, double yInches, WrapMode wrap = WrapMode.Square, string? alt = null)
    {
        var d = Picture(p, data, widthInches, null, alt);
        d.Wrap = wrap;
        d.Float = new FloatInfo
        {
            Horizontal = new FloatPosition { RelativeFrom = "margin", Offset = (long)(xInches * 914400) },
            Vertical = new FloatPosition { RelativeFrom = "margin", Offset = (long)(yInches * 914400) },
        };
        // Floating drawings are anchors (length 0): put it at the start of the paragraph.
        p.Inlines.Remove(d);
        p.Inlines.Insert(0, d);
        return d;
    }

    /// <summary>Ends the current section here (the previous paragraph gets the section break).</summary>
    public SectProps SectionBreak(string type = "nextPage", bool? landscape = null)
    {
        var last = Body.OfType<Paragraph>().LastOrDefault() ?? P("");
        var ending = Doc.Body.FinalSection.Clone();
        last.Props.Section = ending;
        var next = Doc.Body.FinalSection.Clone();
        next.BreakType = type;
        if (landscape is { } l && l != next.Landscape)
        {
            var w = next.PageWidth;
            next.PageWidth = next.PageHeight;
            next.PageHeight = w;
            next.Landscape = l;
        }
        Doc.Body.FinalSection = next;
        return next;
    }

    /// <summary>Adds a header or footer to the last section with the given paragraphs.</summary>
    public HeaderFooter HeaderFooter(bool header, string kind, params Paragraph[] paragraphs)
    {
        var story = new HeaderFooter(header) { RelId = Doc.NewRelId() };
        foreach (var p in paragraphs)
        {
            p.Props.StyleId ??= header ? "Header" : "Footer";
            story.Blocks.Add(p);
        }
        story.EnsureParagraph();
        Doc.HeadersFooters[story.RelId] = story;
        Doc.Body.FinalSection.SetReference(header, kind, story.RelId);
        if (kind == "first") Doc.Body.FinalSection.TitlePage = true;
        return story;
    }

    /// <summary>A paragraph holding "Page N" style text with a PAGE field (for footers).</summary>
    public static Paragraph PageNumberParagraph(string before = "", string after = "", string alignment = "center", bool withTotal = false)
    {
        var p = new Paragraph();
        p.Props.Justification = alignment;
        if (before.Length > 0) p.Inlines.Add(new TextRun(before));
        p.Inlines.Add(new FieldInline { Code = " PAGE ", Result = { new TextRun("1") } });
        if (withTotal)
        {
            p.Inlines.Add(new TextRun(" of "));
            p.Inlines.Add(new FieldInline { Code = " NUMPAGES ", Result = { new TextRun("1") } });
        }
        if (after.Length > 0) p.Inlines.Add(new TextRun(after));
        return p;
    }

    /// <summary>A Word-style content control around blocks (used for the table of contents).</summary>
    public static SdtBlock ContentControl(string gallery, IEnumerable<Block> blocks)
    {
        var sdt = new SdtBlock();
        sdt.Properties.Add(new XElement(Ns.W + "docPartObj",
            new XElement(Ns.W + "docPartGallery", new XAttribute(Ox.Val, gallery)),
            new XElement(Ns.W + "docPartUnique")));
        foreach (var b in blocks) sdt.Blocks.Add(b);
        return sdt;
    }
}
