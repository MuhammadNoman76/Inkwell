using System.Xml.Linq;

namespace Inkwell.Core.Model;

/// <summary>
/// A deep copy of a document, taken on the UI thread so saving (automatic saves, recovery copies) can run on a
/// background thread while the user keeps typing. Pictures and the original package bytes are shared (immutable).
/// </summary>
public static class DocumentCloner
{
    public static Document Clone(Document source)
    {
        var copy = new Document
        {
            Styles = source.Styles.Clone(),
            Numbering = source.Numbering.Clone(),
            Settings = source.Settings.Clone(),
            Theme = source.Theme.Clone(),
            Media = source.Media.Clone(),
            Properties = source.Properties.Clone(),
            Sources = source.Sources.Clone(),
            Source = source.Source,
            DocumentShell = source.DocumentShell.Copy(),
            Background = source.Background.Copy(),
            FontTable = source.FontTable == null ? null : new XDocument(source.FontTable),
            Version = source.Version,
        };
        copy.Numbering.Styles = copy.Styles;
        foreach (var id in source.ReservedRelIds) copy.ReservedRelIds.Add(id);
        copy.Body.FinalSection = source.Body.FinalSection.Clone();
        CopyBlocks(source.Body.Blocks, copy.Body.Blocks);
        foreach (var (key, story) in source.HeadersFooters)
        {
            var h = new HeaderFooter(story.IsHeader) { RelId = story.RelId, RootShell = story.RootShell.Copy(), PartUri = story.PartUri };
            CopyBlocks(story.Blocks, h.Blocks);
            copy.HeadersFooters[key] = h;
        }
        foreach (var (id, note) in source.Footnotes) copy.Footnotes[id] = CopyNote(note);
        foreach (var (id, note) in source.Endnotes) copy.Endnotes[id] = CopyNote(note);
        foreach (var c in source.Comments)
        {
            var comment = new Comment
            {
                Id = c.Id,
                Author = c.Author,
                Initials = c.Initials,
                Date = c.Date,
                ParentId = c.ParentId,
                Done = c.Done,
                ParaId = c.ParaId,
                DurableId = c.DurableId,
                DateUtc = c.DateUtc,
                Shell = c.Shell.Copy(),
            };
            CopyBlocks(c.Blocks, comment.Blocks);
            copy.Comments.Add(comment);
        }
        foreach (var p in source.Preserved) copy.Preserved.Add(new PreservedFeature(p.Category, p.Description) { Count = p.Count });
        RelinkHyperlinks(copy);
        return copy;
    }

    private static Note CopyNote(Note note)
    {
        var n = new Note(note.NoteKind) { Id = note.Id, Type = note.Type, Shell = note.Shell.Copy() };
        CopyBlocks(note.Blocks, n.Blocks);
        return n;
    }

    private static void CopyBlocks(BlockList from, BlockList to)
    {
        foreach (var b in from) to.Add(b.CloneBlock());
    }

    /// <summary>Clones share Hyperlink instances with the original; give the copy its own (same grouping).</summary>
    private static void RelinkHyperlinks(Document doc)
    {
        var map = new Dictionary<Hyperlink, Hyperlink>(ReferenceEqualityComparer<Hyperlink>.Instance);
        foreach (var story in doc.Stories)
            foreach (var p in story.Paragraphs)
                foreach (var inline in DocumentWalker.DeepInlines(p))
                {
                    if (inline.Link == null) continue;
                    if (!map.TryGetValue(inline.Link, out var replacement))
                    {
                        replacement = inline.Link.Clone();
                        map[inline.Link] = replacement;
                    }
                    inline.Link = replacement;
                }
    }
}
