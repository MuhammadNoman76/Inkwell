using System.Xml.Linq;

namespace Inkwell.Core.Model;

public enum StoryKind { Main, Header, Footer, Footnote, Endnote, Comment, TextBox }

/// <summary>A flow of blocks: the body, a header or footer, a note, a comment.</summary>
public abstract class Story : IBlockContainer
{
    protected Story(StoryKind kind)
    {
        Kind = kind;
        Blocks = new BlockList(this);
    }

    public StoryKind Kind { get; }

    public BlockList Blocks { get; }

    /// <summary>Every paragraph in reading order, including those in tables, content controls and fields.</summary>
    public IEnumerable<Paragraph> Paragraphs => DocumentWalker.Paragraphs(Blocks);

    /// <summary>Makes sure the story has at least one paragraph (Word requires one in cells, notes, headers).</summary>
    public void EnsureParagraph()
    {
        if (!Blocks.OfType<Paragraph>().Any() && Blocks.Count == 0) Blocks.Add(new Paragraph());
    }
}

/// <summary>The main text of the document.</summary>
public sealed class MainStory : Story
{
    public MainStory() : base(StoryKind.Main)
    {
    }

    /// <summary>The last section's properties (w:body/w:sectPr).</summary>
    public SectProps FinalSection { get; set; } = SectProps.CreateDefault(false);
}

/// <summary>A header or footer part.</summary>
public sealed class HeaderFooter : Story
{
    public HeaderFooter(bool isHeader) : base(isHeader ? StoryKind.Header : StoryKind.Footer)
    {
    }

    public bool IsHeader => Kind == StoryKind.Header;

    /// <summary>Relationship id from the document part (r:id in headerReference / footerReference).</summary>
    public string RelId { get; set; } = "";

    /// <summary>The root element's attributes (namespace declarations, mc:Ignorable), as loaded.</summary>
    public XElement? RootShell { get; set; }

    /// <summary>The part's URI in the package (null for new ones).</summary>
    public string? PartUri { get; set; }
}

/// <summary>A footnote or endnote.</summary>
public sealed class Note : Story
{
    public Note(NoteKind kind) : base(kind == NoteKind.Footnote ? StoryKind.Footnote : StoryKind.Endnote)
    {
        NoteKind = kind;
    }

    public NoteKind NoteKind { get; }

    public int Id { get; set; }

    /// <summary>separator, continuationSeparator, continuationNotice, or null for a normal note.</summary>
    public string? Type { get; set; }

    public bool IsSpecial => Type != null && Type != "normal";

    public XElement? Shell { get; set; }
}

/// <summary>A comment (w:comment) with its reply link and resolved state (commentsExtended).</summary>
public sealed class Comment : Story
{
    public Comment() : base(StoryKind.Comment)
    {
    }

    public int Id { get; set; }
    public string Author { get; set; } = "";
    public string Initials { get; set; } = "";
    public DateTime? Date { get; set; }

    /// <summary>The comment this one replies to.</summary>
    public int? ParentId { get; set; }

    /// <summary>Resolved (w15:commentEx done="1").</summary>
    public bool Done { get; set; }

    /// <summary>w14:paraId of the comment's last paragraph (links commentsExtended).</summary>
    public string? ParaId { get; set; }

    /// <summary>w16cid:durableId.</summary>
    public string? DurableId { get; set; }

    public DateTime? DateUtc { get; set; }

    public XElement? Shell { get; set; }

    public string Text => string.Join("\n", Paragraphs.Select(p => p.PlainText()));
}

/// <summary>Something in the file that Inkwell keeps but can't edit, for the notice shown after opening.</summary>
public sealed class PreservedFeature
{
    public PreservedFeature(string category, string description)
    {
        Category = category;
        Description = description;
    }

    /// <summary>macros, smartart, chart, equation, contentControl, field, customXml, textbox, shape, object, other…</summary>
    public string Category { get; }

    public string Description { get; }

    public int Count { get; set; } = 1;
}

/// <summary>The package a document was loaded from, kept so everything Inkwell doesn't rewrite survives byte for byte.</summary>
public sealed class SourcePackage
{
    public SourcePackage(byte[] bytes, string mainContentType)
    {
        Bytes = bytes;
        MainContentType = mainContentType;
    }

    /// <summary>The original file's bytes.</summary>
    public byte[] Bytes { get; }

    /// <summary>The main document part's content type (tells .docx, .docm, .dotx, .dotm apart).</summary>
    public string MainContentType { get; }

    public bool HasMacros => MainContentType.Contains("macroEnabled");

    public bool IsTemplate => MainContentType.Contains("template");
}

/// <summary>
/// A word-processing document: stories (body, headers and footers, notes, comments), styles, numbering, theme,
/// settings, pictures and properties. Close to WordprocessingML so .docx files round-trip faithfully.
/// </summary>
public sealed class Document
{
    public Document()
    {
        Body = new MainStory();
        Styles = StyleSheet.CreateDefault();
        Numbering = new Numbering();
        Settings = new DocSettings();
        Theme = DocTheme.CreateDefault();
    }

    public MainStory Body { get; }

    public StyleSheet Styles { get; set; }

    public Numbering Numbering { get; set; }

    public DocSettings Settings { get; set; }

    public DocTheme Theme { get; set; }

    /// <summary>Headers and footers by their relationship id.</summary>
    public Dictionary<string, HeaderFooter> HeadersFooters { get; } = new(StringComparer.Ordinal);

    public SortedDictionary<int, Note> Footnotes { get; } = new();

    public SortedDictionary<int, Note> Endnotes { get; } = new();

    public List<Comment> Comments { get; } = new();

    public MediaStore Media { get; set; } = new();

    public CoreProps Properties { get; set; } = new();

    public BibliographySources Sources { get; set; } = new();

    /// <summary>The original file (null for new documents).</summary>
    public SourcePackage? Source { get; set; }

    /// <summary>The w:document element without its body (namespace declarations, mc:Ignorable, w:background).</summary>
    public XElement? DocumentShell { get; set; }

    /// <summary>Page colour (w:background), kept as it was.</summary>
    public XElement? Background { get; set; }

    /// <summary>The font table part (w:fonts), kept as it was; Inkwell adds embedded fonts to it.</summary>
    public XDocument? FontTable { get; set; }

    /// <summary>What was kept but can't be edited (shown after opening).</summary>
    public List<PreservedFeature> Preserved { get; } = new();

    /// <summary>Things that couldn't be read exactly (shown after opening).</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>Changes to anything outside the blocks (styles, numbering, settings, notes…): bumped by the editor.</summary>
    public int Version { get; set; }

    public void NotePreserved(string category, string description)
    {
        var existing = Preserved.FirstOrDefault(p => p.Category == category && p.Description == description);
        if (existing != null) existing.Count++;
        else Preserved.Add(new PreservedFeature(category, description));
    }

    /// <summary>All stories: body, headers and footers, notes, comments.</summary>
    public IEnumerable<Story> Stories
    {
        get
        {
            yield return Body;
            foreach (var h in HeadersFooters.Values) yield return h;
            foreach (var n in Footnotes.Values) yield return n;
            foreach (var n in Endnotes.Values) yield return n;
            foreach (var c in Comments) yield return c;
        }
    }

    /// <summary>The sections of the body: each paragraph with a w:sectPr ends one; the body's sectPr ends the last.</summary>
    public List<SectionInfo> Sections()
    {
        var list = new List<SectionInfo>();
        var start = 0;
        for (var i = 0; i < Body.Blocks.Count; i++)
        {
            if (Body.Blocks[i] is Paragraph p && p.Props.Section is { } s)
            {
                list.Add(new SectionInfo(s, start, i + 1, p));
                start = i + 1;
            }
        }
        list.Add(new SectionInfo(Body.FinalSection, start, Body.Blocks.Count, null));
        return list;
    }

    /// <summary>The section a body block belongs to.</summary>
    public SectProps SectionOf(Block block)
    {
        var top = DocumentWalker.TopLevel(block);
        if (top == null || top.Story is not MainStory) return Body.FinalSection;
        var index = Body.Blocks.IndexOf(top);
        for (var i = Math.Max(0, index); i < Body.Blocks.Count; i++)
            if (Body.Blocks[i] is Paragraph p && p.Props.Section is { } s) return s;
        return Body.FinalSection;
    }

    public Comment? FindComment(int id) => Comments.FirstOrDefault(c => c.Id == id);

    public Note? FindNote(NoteKind kind, int id) =>
        (kind == NoteKind.Footnote ? Footnotes : Endnotes).TryGetValue(id, out var n) ? n : null;

    /// <summary>A fresh id for a footnote / endnote (Word reserves -1, 0 and 1 for separators in practice).</summary>
    public int NextNoteId(NoteKind kind)
    {
        var notes = kind == NoteKind.Footnote ? Footnotes : Endnotes;
        return Math.Max(1, notes.Count == 0 ? 1 : notes.Keys.Max()) + 1;
    }

    public int NextCommentId() => Comments.Count == 0 ? 0 : Comments.Max(c => c.Id) + 1;

    /// <summary>A fresh bookmark id (unique across the document).</summary>
    public int NextBookmarkId()
    {
        var max = -1;
        foreach (var story in Stories)
            foreach (var p in story.Paragraphs)
                foreach (var b in p.Inlines.OfType<BookmarkMarker>())
                    max = Math.Max(max, b.Id);
        foreach (var m in DocumentWalker.AllBlocks(Body.Blocks).OfType<MarkerBlock>())
            foreach (var b in m.Markers.OfType<BookmarkMarker>())
                max = Math.Max(max, b.Id);
        return max + 1;
    }

    /// <summary>Bookmark names in the document.</summary>
    public IEnumerable<(string Name, Paragraph Paragraph, int Offset)> Bookmarks()
    {
        foreach (var p in Body.Paragraphs)
        {
            var pos = 0;
            foreach (var i in p.Inlines)
            {
                if (i is BookmarkMarker { IsStart: true, Name: { } name }) yield return (name, p, pos);
                pos += i.Length;
            }
        }
    }
}

/// <summary>One section of the body: its properties and the block range [Start, End).</summary>
public sealed class SectionInfo
{
    public SectionInfo(SectProps props, int start, int end, Paragraph? breakParagraph)
    {
        Props = props;
        Start = start;
        End = end;
        BreakParagraph = breakParagraph;
    }

    public SectProps Props { get; }
    public int Start { get; }
    public int End { get; }

    /// <summary>The paragraph whose w:sectPr ends this section (null for the last one).</summary>
    public Paragraph? BreakParagraph { get; }
}

/// <summary>Walks blocks and paragraphs in reading order.</summary>
public static class DocumentWalker
{
    public static IEnumerable<Paragraph> Paragraphs(IEnumerable<Block> blocks)
    {
        foreach (var b in blocks)
        {
            switch (b)
            {
                case Paragraph p:
                    yield return p;
                    break;
                case Table t:
                    foreach (var r in t.Rows)
                        foreach (var c in r.Cells)
                            foreach (var p in Paragraphs(c.Blocks))
                                yield return p;
                    break;
                case SdtBlock s:
                    foreach (var p in Paragraphs(s.Blocks)) yield return p;
                    break;
                case FieldBlock f:
                    foreach (var p in Paragraphs(f.Blocks)) yield return p;
                    break;
            }
        }
    }

    public static IEnumerable<Block> AllBlocks(IEnumerable<Block> blocks)
    {
        foreach (var b in blocks)
        {
            yield return b;
            switch (b)
            {
                case Table t:
                    foreach (var r in t.Rows)
                        foreach (var c in r.Cells)
                            foreach (var inner in AllBlocks(c.Blocks))
                                yield return inner;
                    break;
                case SdtBlock s:
                    foreach (var inner in AllBlocks(s.Blocks)) yield return inner;
                    break;
                case FieldBlock f:
                    foreach (var inner in AllBlocks(f.Blocks)) yield return inner;
                    break;
            }
        }
    }

    /// <summary>The top-level block (directly in its story) that contains <paramref name="block"/>.</summary>
    public static Block? TopLevel(Block block)
    {
        var current = block;
        while (true)
        {
            var owner = current.Parent?.Owner;
            switch (owner)
            {
                case Story:
                    return current;
                case TableCell cell:
                    if (cell.Row?.Table is not { } table) return null;
                    current = table;
                    break;
                case Block b:
                    current = b;
                    break;
                default:
                    return null;
            }
        }
    }

    /// <summary>All inlines of a paragraph, including those inside field results.</summary>
    public static IEnumerable<Inline> DeepInlines(Paragraph p)
    {
        foreach (var i in p.Inlines)
        {
            yield return i;
            if (i is FieldInline f)
                foreach (var r in f.Result)
                    yield return r;
        }
    }
}
