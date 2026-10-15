using Inkwell.Core.Model;

namespace Inkwell.Core.Editing;

/// <summary>A tracked change for the review list.</summary>
public sealed class RevisionItem
{
    public string Kind { get; init; } = "";
    public string Author { get; init; } = "";
    public DateTime? Date { get; init; }
    public Paragraph Paragraph { get; init; } = null!;
    public int Start { get; init; }
    public int End { get; init; }
    public string Text { get; init; } = "";
}

public sealed partial class DocEditor
{
    /// <summary>Every tracked change in the document, in reading order (consecutive runs of one change merged).</summary>
    public List<RevisionItem> Revisions()
    {
        var list = new List<RevisionItem>();
        foreach (var story in Doc.Stories)
        {
            if (story is Comment) continue;
            foreach (var p in story.Paragraphs)
            {
                var pos = 0;
                RevisionItem? current = null;
                foreach (var inline in p.Inlines)
                {
                    var len = inline.Length;
                    string? kind = inline.Deleted != null ? (inline.IsMove ? "Moved from" : "Deleted")
                        : inline.Inserted != null ? (inline.IsMove ? "Moved to" : "Inserted")
                        : inline.Props.Change != null ? "Formatted" : null;
                    var info = inline.Deleted ?? inline.Inserted ?? inline.Props.Change?.Info;
                    if (kind != null && info != null && len > 0)
                    {
                        var text = inline is TextRun tr ? tr.Text : inline is DrawingObject ? "[picture]" : inline is FieldInline f ? f.ResultText : "";
                        if (current != null && current.Kind == kind && current.Author == info.Author && current.End == pos)
                            current = new RevisionItem { Kind = kind, Author = info.Author, Date = current.Date, Paragraph = p, Start = current.Start, End = pos + len, Text = current.Text + text };
                        else
                        {
                            if (current != null) list.Add(current);
                            current = new RevisionItem { Kind = kind, Author = info.Author, Date = info.Date, Paragraph = p, Start = pos, End = pos + len, Text = text };
                        }
                    }
                    else if (len > 0 && current != null)
                    {
                        list.Add(current);
                        current = null;
                    }
                    pos += len;
                }
                if (current != null) list.Add(current);
                var mark = p.Props.MarkProps();
                if (mark?.MarkInserted is { } mi) list.Add(new RevisionItem { Kind = "New paragraph", Author = mi.Author, Date = mi.Date, Paragraph = p, Start = p.Length, End = p.Length, Text = "¶" });
                if (mark?.MarkDeleted is { } md) list.Add(new RevisionItem { Kind = "Joined paragraphs", Author = md.Author, Date = md.Date, Paragraph = p, Start = p.Length, End = p.Length, Text = "¶" });
                if (p.Props.Change is { } pc) list.Add(new RevisionItem { Kind = "Paragraph formatting", Author = pc.Info.Author, Date = pc.Info.Date, Paragraph = p, Start = 0, End = p.Length, Text = "" });
            }
        }
        return list;
    }

    public bool HasRevisions() =>
        Doc.Stories.Where(s => s is not Comment).SelectMany(s => s.Paragraphs).Any(p =>
            p.Inlines.Any(i => i.Inserted != null || i.Deleted != null || i.Props.Change != null) || p.Props.Change != null ||
            p.Props.MarkProps() is { } m && (m.MarkInserted != null || m.MarkDeleted != null));

    public IReadOnlyList<string> RevisionAuthors() => Revisions().Select(r => r.Author).Distinct().ToList();

    /// <summary>Accepts or rejects every change (optionally only one author's).</summary>
    public void AcceptOrRejectAll(bool accept, string? author)
    {
        Edit(accept ? "Accept all changes" : "Reject all changes", t =>
        {
            foreach (var story in Doc.Stories.Where(s => s is not Comment).ToList())
                foreach (var p in story.Paragraphs.ToList())
                    Resolve(t, p, 0, int.MaxValue, accept, author, includeMark: true);
            return new Selection(Selection.Start);
        });
    }

    /// <summary>Accepts or rejects the changes in the selection (or the one at the caret).</summary>
    public void AcceptOrRejectAtSelection(bool accept)
    {
        var s = Selection;
        var ranges = new List<(Paragraph, int, int, bool)>();
        if (s.IsEmpty)
        {
            var item = Revisions().FirstOrDefault(r => ReferenceEquals(r.Paragraph, s.Active.Para) && s.Active.Offset >= r.Start && s.Active.Offset <= r.End);
            if (item == null) return;
            ranges.Add((item.Paragraph, item.Start, Math.Max(item.End, item.Start + 1), item.Kind is "New paragraph" or "Joined paragraphs" or "Paragraph formatting"));
        }
        else
        {
            foreach (var (p, start, end) in SelectedRanges())
                ranges.Add((p, start, end, end >= p.Length && !ReferenceEquals(p, s.End.Para)));
        }
        Edit(accept ? "Accept change" : "Reject change", t =>
        {
            foreach (var (p, start, end, mark) in ranges) Resolve(t, p, start, end, accept, null, mark);
            return new Selection(new DocPos(ranges[0].Item1, Math.Min(ranges[0].Item2, ranges[0].Item1.Length)));
        });
    }

    private void Resolve(Transaction t, Paragraph p, int start, int end, bool accept, string? author, bool includeMark)
    {
        bool Matches(RevisionInfo? info) => info != null && (author == null || info.Author == author);
        var touched = false;
        void Touch()
        {
            if (touched) return;
            t.Touch(p);
            touched = true;
        }
        var first = p.SplitAt(Math.Min(start, p.Length));
        var last = p.SplitAt(Math.Min(end, p.Length));
        for (var i = last - 1; i >= first; i--)
        {
            var inline = p.Inlines[i];
            if (Matches(inline.Deleted))
            {
                Touch();
                if (accept) p.Inlines.RemoveAt(i);
                else inline.Deleted = null;
                continue;
            }
            if (Matches(inline.Inserted))
            {
                Touch();
                if (accept)
                {
                    inline.Inserted = null;
                    inline.IsMove = false;
                }
                else p.Inlines.RemoveAt(i);
                continue;
            }
            if (inline.Props.Change is { } change && Matches(change.Info))
            {
                Touch();
                if (accept) inline.Props.SetChange(null, null);
                else
                {
                    var old = change.Old.Clone();
                    inline.Props = old;
                }
            }
        }
        if (touched) p.Normalize();
        if (!includeMark) return;
        if (p.Props.Change is { } pc && Matches(pc.Info))
        {
            Touch();
            if (accept) p.Props.SetChange(null, null);
            else
            {
                var keep = p.Props.X.Elements().Where(e => e.Name.LocalName is "rPr" or "sectPr").Select(e => new System.Xml.Linq.XElement(e)).ToList();
                var restored = pc.Old.Clone();
                foreach (var e in keep) restored.SetElement(e.Name.LocalName, e);
                p.Props = restored;
            }
        }
        var mark = p.Props.MarkProps();
        if (mark == null) return;
        var join = false;
        if (Matches(mark.MarkInserted))
        {
            Touch();
            mark.Remove("ins");
            if (!accept) join = true;
        }
        if (Matches(mark.MarkDeleted))
        {
            Touch();
            mark.Remove("del");
            if (accept) join = true;
        }
        if (join && p.Parent is { } list && list.IndexOf(p) + 1 < list.Count && list[list.IndexOf(p) + 1] is Paragraph next)
        {
            t.Touch(list);
            t.Touch(next);
            p.Inlines.AddRange(next.Inlines);
            p.Props.Section = next.Props.Section?.Clone() ?? p.Props.Section;
            p.Normalize();
            list.Remove(next);
        }
    }

    /// <summary>The next (or previous) change after the caret; null at the end.</summary>
    public RevisionItem? FindChange(int direction)
    {
        var all = Revisions().Where(r => r.Paragraph.Story is MainStory).ToList();
        if (all.Count == 0) return null;
        var caret = Selection.Active;
        if (direction > 0)
            return all.FirstOrDefault(r => Nav.Compare(new DocPos(r.Paragraph, r.Start), caret) > 0) ?? all[0];
        return all.LastOrDefault(r => Nav.Compare(new DocPos(r.Paragraph, r.End), caret) < 0) ?? all[all.Count - 1];
    }
}
