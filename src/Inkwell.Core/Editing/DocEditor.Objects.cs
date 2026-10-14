using System.Windows;
using Inkwell.Core.Layout;
using Inkwell.Core.Model;

namespace Inkwell.Core.Editing;

public sealed partial class DocEditor
{
    // ------------------------------------------------------------------ finding things

    /// <summary>The paragraph that holds an inline (searching every story), or null.</summary>
    public Paragraph? ParagraphOf(Inline inline)
    {
        foreach (var story in Doc.Stories)
            foreach (var p in story.Paragraphs)
                if (p.Inlines.Contains(inline)) return p;
        return null;
    }

    // ------------------------------------------------------------------ pictures

    /// <summary>Inserts a picture in line at the selection, no wider than the text column.</summary>
    public DrawingObject? InsertPicture(byte[] data, string? altText = null, double maxWidthDip = 0)
    {
        var size = Rendering.MediaImages.Measure(data);
        if (size.Width <= 0 || size.Height <= 0)
            throw new EditRefusedException("This picture can't be read. Inkwell can insert PNG, JPEG, GIF, BMP and TIFF pictures.");
        var width = size.Width * 96.0 / size.DpiX;
        var height = size.Height * 96.0 / size.DpiY;
        var section = Doc.SectionOf(Selection.Active.Para);
        var textWidth = Units.Twips(section.PageWidth - section.MarginLeft - section.MarginRight);
        var max = maxWidthDip > 0 ? Math.Min(maxWidthDip, textWidth) : textWidth;
        if (width > max)
        {
            height *= max / width;
            width = max;
        }
        var mediaId = Doc.Media.Add(data);
        var drawing = new DrawingObject
        {
            MediaId = mediaId,
            Width = Units.ToEmu(width),
            Height = Units.ToEmu(height),
            AltText = altText,
            Name = "Picture",
        };
        Edit("Insert picture", t =>
        {
            var pos = Selection.IsEmpty ? Selection.Active : DeleteRange(t, Selection.Start, Selection.End);
            EnsureEditable(pos.Para);
            t.Touch(pos.Para);
            pos.Para.InsertInline(pos.Offset, drawing);
            if (TrackChanges) drawing.Inserted = NewRevision();
            return new Selection(new DocPos(pos.Para, pos.Offset + 1));
        });
        return drawing;
    }

    public void DeleteDrawing(DrawingObject drawing)
    {
        var p = ParagraphOf(drawing);
        if (p == null) return;
        Edit("Delete picture", t =>
        {
            EnsureEditable(p);
            t.Touch(p);
            var offset = p.OffsetOf(drawing);
            if (TrackChanges && !drawing.IsFloating) drawing.Deleted = NewRevision();
            else p.Inlines.Remove(drawing);
            return new Selection(new DocPos(p, Math.Max(0, Math.Min(offset, p.Length))));
        });
    }

    /// <summary>Resizes a picture (size in DIPs).</summary>
    public void ResizeDrawing(DrawingObject drawing, Size size)
    {
        var p = ParagraphOf(drawing);
        if (p == null) return;
        Edit("Resize picture", t =>
        {
            t.Touch(p);
            var target = (DrawingObject)p.Inlines.First(i => ReferenceEquals(i, drawing));
            target.Width = Units.ToEmu(Math.Max(4, size.Width));
            target.Height = Units.ToEmu(Math.Max(4, size.Height));
            return null;
        });
    }

    /// <summary>
    /// Moves a floating picture to a box on a page. Its position becomes relative to the margins, so it stays
    /// exactly there when text above it changes. Moving it to another page re-anchors it to that page's text.
    /// </summary>
    public void MoveDrawing(DrawingObject drawing, PageLayout page, Rect box, Paragraph? newAnchor)
    {
        var p = ParagraphOf(drawing);
        if (p == null || drawing.Float == null) return;
        if (drawing.Float.Locked) throw new EditRefusedException("This picture's position is locked. Unlock it in the picture panel to move it.");
        Edit("Move picture", t =>
        {
            t.Touch(p);
            drawing.Float.Horizontal = new FloatPosition { RelativeFrom = "margin", Offset = Units.ToEmu(box.X - page.Margins.Left) };
            drawing.Float.Vertical = new FloatPosition { RelativeFrom = "margin", Offset = Units.ToEmu(box.Y - page.Margins.Top) };
            if (newAnchor != null && !ReferenceEquals(newAnchor, p) && !Nav.IsReadOnly(newAnchor))
            {
                p.Inlines.Remove(drawing);
                t.Touch(newAnchor);
                newAnchor.Inlines.Insert(0, drawing);
            }
            return null;
        });
    }

    /// <summary>Changes how a picture sits in the text. Floating pictures are placed where they are on the page now.</summary>
    public void SetWrap(DrawingObject drawing, WrapMode wrap, PageLayout? page, Rect? box)
    {
        var p = ParagraphOf(drawing);
        if (p == null) return;
        Edit("Wrap text: " + WrapName(wrap), t =>
        {
            t.Touch(p);
            var wasFloating = drawing.IsFloating;
            drawing.Wrap = wrap;
            if (wrap == WrapMode.Inline)
            {
                drawing.Float = null;
                if (wasFloating)
                {
                    // Floating drawings are anchors at the paragraph start; in line it takes a position there.
                    p.Inlines.Remove(drawing);
                    p.Inlines.Insert(0, drawing);
                }
                return null;
            }
            if (drawing.Float == null)
            {
                drawing.Float = new FloatInfo();
                if (page != null && box != null)
                {
                    drawing.Float.Horizontal = new FloatPosition { RelativeFrom = "margin", Offset = Units.ToEmu(box.Value.X - page.Margins.Left) };
                    drawing.Float.Vertical = new FloatPosition { RelativeFrom = "margin", Offset = Units.ToEmu(box.Value.Y - page.Margins.Top) };
                }
                else
                {
                    drawing.Float.Horizontal = new FloatPosition { RelativeFrom = "column", Align = "left" };
                    drawing.Float.Vertical = new FloatPosition { RelativeFrom = "paragraph", Offset = 0 };
                }
                // In line it took a position; as an anchor it takes none: move it to the paragraph start.
                p.Inlines.Remove(drawing);
                p.Inlines.Insert(0, drawing);
            }
            if (wrap == WrapMode.TopAndBottom) drawing.Float.DistTop = drawing.Float.DistBottom = 114300;
            return null;
        });
    }

    public static string WrapName(WrapMode wrap) => wrap switch
    {
        WrapMode.Inline => "In line with text",
        WrapMode.Square => "Square",
        WrapMode.Tight => "Tight",
        WrapMode.TopAndBottom => "Top and bottom",
        WrapMode.Behind => "Behind text",
        WrapMode.InFront => "In front of text",
        _ => wrap.ToString(),
    };

    /// <summary>Aligns a floating picture to the left, centre or right of the margins (and keeps it there).</summary>
    public void AlignDrawing(DrawingObject drawing, string horizontal)
    {
        var p = ParagraphOf(drawing);
        if (p == null || drawing.Float == null) return;
        Edit("Align picture", t =>
        {
            t.Touch(p);
            drawing.Float.Horizontal = new FloatPosition { RelativeFrom = "margin", Align = horizontal };
            return null;
        });
    }

    public void SetDrawingLocked(DrawingObject drawing, bool locked)
    {
        var p = ParagraphOf(drawing);
        if (p == null || drawing.Float == null) return;
        Edit(locked ? "Lock position" : "Unlock position", t =>
        {
            t.Touch(p);
            drawing.Float.Locked = locked;
            return null;
        });
    }

    public void SetAltText(DrawingObject drawing, string? alt)
    {
        var p = ParagraphOf(drawing);
        if (p == null) return;
        Edit("Alt text", t =>
        {
            t.Touch(p);
            drawing.AltText = string.IsNullOrWhiteSpace(alt) ? null : alt!.Trim();
            return null;
        });
    }

    /// <summary>Re-anchors a floating picture to another paragraph (dragging its anchor).</summary>
    public void MoveAnchor(DrawingObject drawing, Paragraph target)
    {
        var p = ParagraphOf(drawing);
        if (p == null || ReferenceEquals(p, target) || Nav.IsReadOnly(target)) return;
        Edit("Move anchor", t =>
        {
            t.Touch(p);
            t.Touch(target);
            p.Inlines.Remove(drawing);
            target.Inlines.Insert(0, drawing);
            return null;
        });
    }

    // ------------------------------------------------------------------ links

    /// <summary>Makes the selection (or the given text at the caret) a link to a web address, e-mail or bookmark.</summary>
    public void InsertLink(string? url, string? anchor, string? text = null, string? tooltip = null)
    {
        var link = new Hyperlink { Url = url, Anchor = anchor, Tooltip = tooltip };
        Edit("Link", t =>
        {
            var s = Selection;
            if (s.IsEmpty || !string.IsNullOrEmpty(text))
            {
                var pos = s.IsEmpty ? s.Active : DeleteRange(t, s.Start, s.End);
                EnsureEditable(pos.Para);
                var label = string.IsNullOrEmpty(text) ? url ?? anchor ?? "" : text!;
                var props = pos.Para.PropsForTypingAt(pos.Offset);
                props.StyleId = "Hyperlink";
                t.Touch(pos.Para);
                var run = new TextRun(label) { Props = props, Link = link, Inserted = TrackChanges ? NewRevision() : null };
                pos.Para.InsertInline(pos.Offset, run);
                pos.Para.Normalize();
                return new Selection(new DocPos(pos.Para, pos.Offset + label.Length));
            }
            foreach (var (p, start, end) in SelectedRanges())
            {
                if (Nav.IsReadOnly(p)) continue;
                t.Touch(p);
                var first = p.SplitAt(start);
                var last = p.SplitAt(end);
                for (var i = first; i < last; i++)
                {
                    if (p.Inlines[i].Length == 0) continue;
                    p.Inlines[i].Link = link;
                    if (p.Inlines[i] is TextRun) p.Inlines[i].Props.StyleId = "Hyperlink";
                }
                p.Normalize();
            }
            return Selection;
        });
    }

    /// <summary>The link at the caret / selection start, if any.</summary>
    public Hyperlink? LinkAtSelection()
    {
        var s = Selection;
        var p = s.Start.Para;
        return p.InlineAt(s.Start.Offset)?.Link ?? (s.Start.Offset > 0 ? p.InlineAt(s.Start.Offset - 1)?.Link : null);
    }

    /// <summary>All inlines sharing a link, with their paragraphs.</summary>
    private IEnumerable<(Paragraph Para, Inline Inline)> LinkMembers(Hyperlink link)
    {
        foreach (var story in Doc.Stories)
            foreach (var p in story.Paragraphs)
                foreach (var i in p.Inlines)
                    if (ReferenceEquals(i.Link, link)) yield return (p, i);
    }

    public void EditLink(Hyperlink link, string? url, string? anchor, string? tooltip)
    {
        var members = LinkMembers(link).ToList();
        if (members.Count == 0) return;
        Edit("Edit link", t =>
        {
            var replacement = new Hyperlink { Url = url, Anchor = anchor, Tooltip = tooltip, Shell = link.Shell.Copy() };
            foreach (var (p, inline) in members)
            {
                t.Touch(p);
                var current = p.Inlines.FirstOrDefault(x => ReferenceEquals(x, inline));
                if (current != null) current.Link = replacement;
            }
            return null;
        });
    }

    public void RemoveLink(Hyperlink link)
    {
        var members = LinkMembers(link).ToList();
        if (members.Count == 0) return;
        Edit("Remove link", t =>
        {
            foreach (var (p, inline) in members)
            {
                t.Touch(p);
                var current = p.Inlines.FirstOrDefault(x => ReferenceEquals(x, inline));
                if (current == null) continue;
                current.Link = null;
                if (current.Props.StyleId == "Hyperlink") current.Props.StyleId = null;
                p.Normalize();
            }
            return null;
        });
    }

    // ------------------------------------------------------------------ bookmarks

    /// <summary>Adds a bookmark around the selection (or at the caret). Names follow Word's rules.</summary>
    public string AddBookmark(string name, Selection? at = null)
    {
        var clean = new string(name.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        if (clean.Length == 0 || char.IsDigit(clean[0])) clean = "B" + clean;
        if (clean.Length > 40) clean = clean.Substring(0, 40);
        var s = at ?? Selection;
        Edit("Bookmark", t =>
        {
            RemoveBookmarkCore(t, clean);
            var id = Doc.NextBookmarkId();
            t.Touch(s.Start.Para);
            t.Touch(s.End.Para);
            s.End.Para.InsertInline(s.End.Offset, new BookmarkMarker { Id = id, IsStart = false });
            s.Start.Para.InsertInline(s.Start.Offset, new BookmarkMarker { Id = id, Name = clean, IsStart = true }, afterMarkers: false);
            return null;
        });
        return clean;
    }

    private void RemoveBookmarkCore(Transaction t, string name)
    {
        int? id = null;
        foreach (var story in Doc.Stories)
            foreach (var p in story.Paragraphs)
                foreach (var b in p.Inlines.OfType<BookmarkMarker>().Where(b => b.IsStart && b.Name == name).ToList())
                {
                    id = b.Id;
                    t.Touch(p);
                    p.Inlines.Remove(b);
                }
        if (id == null) return;
        foreach (var story in Doc.Stories)
            foreach (var p in story.Paragraphs)
                foreach (var b in p.Inlines.OfType<BookmarkMarker>().Where(b => !b.IsStart && b.Id == id).ToList())
                {
                    t.Touch(p);
                    p.Inlines.Remove(b);
                }
    }

    public void RemoveBookmark(string name) => Edit("Remove bookmark", t =>
    {
        RemoveBookmarkCore(t, name);
        return null;
    });

    /// <summary>The position of a bookmark, or null.</summary>
    public DocPos? FindBookmark(string name)
    {
        foreach (var story in Doc.Stories)
            foreach (var p in story.Paragraphs)
            {
                var pos = 0;
                foreach (var i in p.Inlines)
                {
                    if (i is BookmarkMarker { IsStart: true } b && b.Name == name) return new DocPos(p, pos);
                    pos += i.Length;
                }
            }
        return null;
    }

    // ------------------------------------------------------------------ footnotes and endnotes

    /// <summary>Inserts a footnote (or endnote) at the caret and puts the caret in its text.</summary>
    public Note InsertNote(NoteKind kind)
    {
        Note? created = null;
        Edit(kind == NoteKind.Footnote ? "Footnote" : "Endnote", t =>
        {
            var pos = Selection.End;
            EnsureEditable(pos.Para);
            if (pos.Para.Story is not MainStory) throw new EditRefusedException("Footnotes and endnotes can only be added to the main text.");
            t.TouchNotes();
            t.TouchSettings();
            DocBuilder.EnsureSeparators(Doc, kind);
            var id = Doc.NextNoteId(kind);
            var note = new Note(kind) { Id = id };
            var np = new Paragraph { Props = { StyleId = kind == NoteKind.Footnote ? "FootnoteText" : "EndnoteText" } };
            var mark = new NoteMark { Kind = kind };
            mark.Props.StyleId = kind == NoteKind.Footnote ? "FootnoteReference" : "EndnoteReference";
            np.Inlines.Add(mark);
            np.Inlines.Add(new TextRun(" "));
            note.Blocks.Add(np);
            (kind == NoteKind.Footnote ? Doc.Footnotes : Doc.Endnotes)[id] = note;
            var reference = new NoteReference { Kind = kind, Id = id };
            reference.Props.StyleId = kind == NoteKind.Footnote ? "FootnoteReference" : "EndnoteReference";
            if (TrackChanges) reference.Inserted = NewRevision();
            t.Touch(pos.Para);
            pos.Para.InsertInline(pos.Offset, reference);
            created = note;
            return new Selection(new DocPos(np, np.Length));
        });
        return created!;
    }

    /// <summary>Deletes a note and its reference.</summary>
    public void DeleteNote(Note note)
    {
        Edit("Delete note", t =>
        {
            foreach (var p in Doc.Body.Paragraphs)
            {
                var refs = p.Inlines.OfType<NoteReference>().Where(r => r.Kind == note.NoteKind && r.Id == note.Id).ToList();
                if (refs.Count == 0) continue;
                t.Touch(p);
                foreach (var r in refs) p.Inlines.Remove(r);
            }
            t.TouchNotes();
            (note.NoteKind == NoteKind.Footnote ? Doc.Footnotes : Doc.Endnotes).Remove(note.Id);
            return null;
        });
    }

    // ------------------------------------------------------------------ comments

    /// <summary>Adds a comment on the selection (or the word at the caret) and returns it.</summary>
    public Comment AddComment(string text, Comment? replyTo = null)
    {
        Comment? created = null;
        Edit(replyTo == null ? "Comment" : "Reply", t =>
        {
            t.TouchComments();
            var comment = new Comment
            {
                Id = Doc.NextCommentId(),
                Author = Author,
                Initials = Initials,
                Date = DateTime.Now,
                DateUtc = DateTime.UtcNow,
                ParentId = replyTo?.Id,
            };
            var p = new Paragraph { Props = { StyleId = "CommentText" } };
            var mark = new CommentMark();
            mark.Props.StyleId = "CommentReference";
            p.Inlines.Add(mark);
            if (text.Length > 0) p.Inlines.Add(new TextRun(text));
            comment.Blocks.Add(p);
            Doc.Comments.Add(comment);

            DocPos start, end;
            if (replyTo != null && FindCommentRange(replyTo.Id) is { } range)
            {
                start = range.Start;
                end = range.End;
            }
            else
            {
                var s = Selection;
                if (s.IsEmpty)
                {
                    var (ws, we) = WordAt(s.Active.Para, s.Active.Offset);
                    while (we > ws && s.Active.Para.Text[we - 1] == ' ') we--;
                    start = new DocPos(s.Active.Para, ws);
                    end = new DocPos(s.Active.Para, we);
                }
                else
                {
                    start = s.Start;
                    end = s.End;
                }
            }
            t.Touch(start.Para);
            t.Touch(end.Para);
            var refMarker = new CommentMarker { Id = comment.Id, Kind = CommentMarkerKind.Reference };
            refMarker.Props.StyleId = "CommentReference";
            end.Para.InsertInline(end.Offset, refMarker);
            end.Para.InsertInline(end.Offset, new CommentMarker { Id = comment.Id, Kind = CommentMarkerKind.RangeEnd });
            start.Para.InsertInline(start.Offset, new CommentMarker { Id = comment.Id, Kind = CommentMarkerKind.RangeStart }, afterMarkers: true);
            created = comment;
            return null;
        });
        return created!;
    }

    /// <summary>Where a comment's range is (start and end markers), or null.</summary>
    public (DocPos Start, DocPos End)? FindCommentRange(int id)
    {
        DocPos? start = null, end = null;
        foreach (var story in Doc.Stories)
        {
            if (story is Comment) continue;
            foreach (var p in story.Paragraphs)
            {
                var pos = 0;
                foreach (var i in p.Inlines)
                {
                    if (i is CommentMarker m && m.Id == id)
                    {
                        if (m.Kind == CommentMarkerKind.RangeStart) start ??= new DocPos(p, pos);
                        else if (m.Kind == CommentMarkerKind.RangeEnd) end = new DocPos(p, pos);
                        else if (end == null) end = new DocPos(p, pos);
                    }
                    pos += i.Length;
                }
            }
        }
        if (start == null && end == null) return null;
        return (start ?? end!.Value, end ?? start!.Value);
    }

    public void SetCommentText(Comment comment, string text)
    {
        Edit("Edit comment", t =>
        {
            t.Touch(comment);
            var p = comment.Blocks.OfType<Paragraph>().FirstOrDefault();
            var keepMark = p?.Inlines.OfType<CommentMark>().FirstOrDefault();
            comment.Blocks.Clear();
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var np = new Paragraph { Props = { StyleId = "CommentText" } };
                if (i == 0)
                {
                    var mark = keepMark ?? new CommentMark { Props = { StyleId = "CommentReference" } };
                    np.Inlines.Add(mark);
                }
                if (lines[i].Length > 0) np.Inlines.Add(new TextRun(lines[i]));
                comment.Blocks.Add(np);
            }
            return null;
        });
    }

    public void ResolveComment(Comment comment, bool done)
    {
        Edit(done ? "Resolve comment" : "Reopen comment", t =>
        {
            t.TouchComments();
            comment.Done = done;
            foreach (var reply in Doc.Comments.Where(c => c.ParentId == comment.Id)) reply.Done = done;
            return null;
        });
    }

    /// <summary>Deletes a comment, its replies and their markers.</summary>
    public void DeleteComment(Comment comment)
    {
        var ids = new HashSet<int> { comment.Id };
        foreach (var reply in Doc.Comments.Where(c => c.ParentId == comment.Id)) ids.Add(reply.Id);
        Edit("Delete comment", t =>
        {
            foreach (var story in Doc.Stories)
            {
                if (story is Comment) continue;
                foreach (var p in story.Paragraphs)
                {
                    var markers = p.Inlines.OfType<CommentMarker>().Where(m => ids.Contains(m.Id)).ToList();
                    if (markers.Count == 0) continue;
                    t.Touch(p);
                    foreach (var m in markers) p.Inlines.Remove(m);
                }
            }
            t.TouchComments();
            Doc.Comments.RemoveAll(c => ids.Contains(c.Id));
            return null;
        });
    }
}
