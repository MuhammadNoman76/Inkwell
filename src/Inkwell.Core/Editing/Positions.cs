using Inkwell.Core.Model;

namespace Inkwell.Core.Editing;

/// <summary>A position in a paragraph (before the item at <see cref="Offset"/>).</summary>
public readonly struct DocPos : IEquatable<DocPos>
{
    public DocPos(Paragraph para, int offset)
    {
        Para = para;
        Offset = offset;
    }

    public Paragraph Para { get; }
    public int Offset { get; }

    public bool IsValid => Para != null;

    public DocPos With(int offset) => new(Para, offset);

    public bool Equals(DocPos other) => ReferenceEquals(Para, other.Para) && Offset == other.Offset;

    public override bool Equals(object? obj) => obj is DocPos p && Equals(p);

    public override int GetHashCode() => (Para == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Para)) * 31 + Offset;

    public static bool operator ==(DocPos a, DocPos b) => a.Equals(b);
    public static bool operator !=(DocPos a, DocPos b) => !a.Equals(b);

    public override string ToString() => Para == null ? "(none)" : $"[{Para.PlainText().Substring(0, Math.Min(12, Para.PlainText().Length))}…@{Offset}]";
}

/// <summary>A selection: anchor (where it started) and active end (where the caret is).</summary>
public readonly struct Selection : IEquatable<Selection>
{
    public Selection(DocPos anchor, DocPos active)
    {
        Anchor = anchor;
        Active = active;
    }

    public Selection(DocPos caret) : this(caret, caret)
    {
    }

    public DocPos Anchor { get; }
    public DocPos Active { get; }

    public bool IsEmpty => Anchor == Active;

    public DocPos Start => Nav.Compare(Anchor, Active) <= 0 ? Anchor : Active;
    public DocPos End => Nav.Compare(Anchor, Active) <= 0 ? Active : Anchor;

    /// <summary>The selection lies in one paragraph.</summary>
    public bool InOneParagraph => ReferenceEquals(Anchor.Para, Active.Para);

    public Story? Story => Active.Para?.Story;

    public bool Equals(Selection other) => Anchor == other.Anchor && Active == other.Active;

    public override bool Equals(object? obj) => obj is Selection s && Equals(s);

    public override int GetHashCode() => Anchor.GetHashCode() * 17 + Active.GetHashCode();
}

/// <summary>Navigation in document order: paths, comparisons, next / previous paragraph.</summary>
public static class Nav
{
    /// <summary>The path of indices from the story to a block (top-level index, then row / cell / inner indices).</summary>
    public static List<int> PathOf(Block block)
    {
        var path = new List<int>(4);
        Block? current = block;
        while (current != null)
        {
            var list = current.Parent;
            if (list == null) break;
            path.Add(list.IndexOf(current));
            switch (list.Owner)
            {
                case TableCell cell:
                    var row = cell.Row;
                    path.Add(row?.Cells.IndexOf(cell) ?? 0);
                    path.Add(row?.Table?.Rows.IndexOf(row) ?? 0);
                    current = row?.Table;
                    break;
                case Block b:
                    current = b;
                    break;
                default:
                    current = null;
                    break;
            }
        }
        path.Reverse();
        return path;
    }

    /// <summary>Compares positions in the same story (paragraph order, then offset).</summary>
    public static int Compare(DocPos a, DocPos b)
    {
        if (ReferenceEquals(a.Para, b.Para)) return a.Offset.CompareTo(b.Offset);
        if (a.Para == null) return -1;
        if (b.Para == null) return 1;
        var pa = PathOf(a.Para);
        var pb = PathOf(b.Para);
        for (var i = 0; i < Math.Min(pa.Count, pb.Count); i++)
            if (pa[i] != pb[i]) return pa[i].CompareTo(pb[i]);
        return pa.Count.CompareTo(pb.Count);
    }

    public static int Compare(Block a, Block b)
    {
        if (ReferenceEquals(a, b)) return 0;
        var pa = PathOf(a);
        var pb = PathOf(b);
        for (var i = 0; i < Math.Min(pa.Count, pb.Count); i++)
            if (pa[i] != pb[i]) return pa[i].CompareTo(pb[i]);
        return pa.Count.CompareTo(pb.Count);
    }

    /// <summary>The first paragraph inside a block (descending into tables and content controls), or null.</summary>
    public static Paragraph? FirstIn(Block block)
    {
        switch (block)
        {
            case Paragraph p:
                return p;
            case Table t:
                foreach (var r in t.Rows)
                    foreach (var c in r.Cells)
                        if (FirstIn(c.Blocks) is { } inCell) return inCell;
                return null;
            case SdtBlock s:
                return FirstIn(s.Blocks);
            case FieldBlock f:
                return FirstIn(f.Blocks);
            default:
                return null;
        }
    }

    public static Paragraph? FirstIn(IEnumerable<Block> blocks)
    {
        foreach (var b in blocks)
            if (FirstIn(b) is { } p) return p;
        return null;
    }

    public static Paragraph? LastIn(Block block)
    {
        switch (block)
        {
            case Paragraph p:
                return p;
            case Table t:
                for (var r = t.Rows.Count - 1; r >= 0; r--)
                    for (var c = t.Rows[r].Cells.Count - 1; c >= 0; c--)
                        if (LastIn(t.Rows[r].Cells[c].Blocks) is { } inCell) return inCell;
                return null;
            case SdtBlock s:
                return LastIn(s.Blocks);
            case FieldBlock f:
                return LastIn(f.Blocks);
            default:
                return null;
        }
    }

    public static Paragraph? LastIn(IList<Block> blocks)
    {
        for (var i = blocks.Count - 1; i >= 0; i--)
            if (LastIn(blocks[i]) is { } p) return p;
        return null;
    }

    /// <summary>The next paragraph in reading order within the same story, or null.</summary>
    public static Paragraph? Next(Paragraph p)
    {
        Block current = p;
        while (true)
        {
            var list = current.Parent;
            if (list == null) return null;
            for (var i = list.IndexOf(current) + 1; i < list.Count; i++)
                if (FirstIn(list[i]) is { } found) return found;
            switch (list.Owner)
            {
                case TableCell cell:
                    var row = cell.Row!;
                    var table = row.Table!;
                    for (var c = row.Cells.IndexOf(cell) + 1; c < row.Cells.Count; c++)
                        if (FirstIn(row.Cells[c].Blocks) is { } inNext) return inNext;
                    for (var r = table.Rows.IndexOf(row) + 1; r < table.Rows.Count; r++)
                        foreach (var c2 in table.Rows[r].Cells)
                            if (FirstIn(c2.Blocks) is { } inRow) return inRow;
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

    /// <summary>The previous paragraph in reading order within the same story, or null.</summary>
    public static Paragraph? Prev(Paragraph p)
    {
        Block current = p;
        while (true)
        {
            var list = current.Parent;
            if (list == null) return null;
            for (var i = list.IndexOf(current) - 1; i >= 0; i--)
                if (LastIn(list[i]) is { } found) return found;
            switch (list.Owner)
            {
                case TableCell cell:
                    var row = cell.Row!;
                    var table = row.Table!;
                    for (var c = row.Cells.IndexOf(cell) - 1; c >= 0; c--)
                        if (LastIn(row.Cells[c].Blocks) is { } inPrev) return inPrev;
                    for (var r = table.Rows.IndexOf(row) - 1; r >= 0; r--)
                        for (var c2 = table.Rows[r].Cells.Count - 1; c2 >= 0; c2--)
                            if (LastIn(table.Rows[r].Cells[c2].Blocks) is { } inRow) return inRow;
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

    /// <summary>Paragraphs from <paramref name="first"/> to <paramref name="last"/> inclusive, in reading order.</summary>
    public static IEnumerable<Paragraph> Range(Paragraph first, Paragraph last)
    {
        var p = first;
        var guard = 0;
        while (p != null && guard++ < 1_000_000)
        {
            yield return p;
            if (ReferenceEquals(p, last)) yield break;
            p = Next(p);
        }
    }

    public static DocPos StartOf(Story story) => new(FirstIn(story.Blocks) ?? EnsureParagraph(story), 0);

    public static DocPos EndOf(Story story)
    {
        var p = LastIn(story.Blocks) ?? EnsureParagraph(story);
        return new DocPos(p, p.Length);
    }

    private static Paragraph EnsureParagraph(Story story)
    {
        var p = new Paragraph();
        story.Blocks.Add(p);
        return p;
    }

    /// <summary>Content Inkwell shows but doesn't edit: generated tables of contents and bibliographies, preserved controls and fields.</summary>
    public static bool IsReadOnly(Paragraph p)
    {
        var owner = p.Parent?.Owner;
        while (owner != null)
        {
            switch (owner)
            {
                case FieldBlock:
                    return true;
                case SdtBlock sdt when !sdt.IsManaged || sdt.Original != null:
                    if (!sdt.IsManaged) return true;
                    owner = sdt.Parent?.Owner;
                    break;
                case SdtBlock sdt:
                    owner = sdt.Parent?.Owner;
                    break;
                case TableCell cell:
                    owner = cell.Row?.Table?.Parent?.Owner;
                    break;
                case Block b:
                    owner = b.Parent?.Owner;
                    break;
                default:
                    return false;
            }
        }
        return false;
    }

    /// <summary>The table cell containing a paragraph (innermost), or null.</summary>
    public static TableCell? CellOf(Paragraph p)
    {
        var owner = p.Parent?.Owner;
        while (owner is Block b && owner is not TableCell) owner = b.Parent?.Owner;
        return owner as TableCell;
    }
}
