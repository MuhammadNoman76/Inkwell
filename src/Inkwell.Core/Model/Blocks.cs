using System.Text;
using System.Xml.Linq;

namespace Inkwell.Core.Model;

/// <summary>Something that holds a list of blocks: a story (body, header, note, comment) or a table cell.</summary>
public interface IBlockContainer
{
    BlockList Blocks { get; }
}

/// <summary>A paragraph-level element: paragraph, table, content control, multi-paragraph field, preserved block.</summary>
public abstract class Block
{
    /// <summary>The list this block is in (maintained by <see cref="BlockList"/>).</summary>
    public BlockList? Parent { get; internal set; }

    /// <summary>Bumped whenever the block (or anything inside it) changes; the layout caches key on it.</summary>
    public int Version { get; private set; }

    public void MarkChanged()
    {
        Version++;
        // A change inside a table cell or a content control changes the blocks around it too.
        var owner = Parent?.Owner;
        switch (owner)
        {
            case TableCell cell:
                cell.Row?.Table?.MarkChanged();
                break;
            case Block block:
                block.MarkChanged();
                break;
        }
    }

    public abstract Block CloneBlock();

    /// <summary>The story this block belongs to (through cells and content controls).</summary>
    public Story? Story
    {
        get
        {
            var owner = Parent?.Owner;
            while (true)
            {
                switch (owner)
                {
                    case Story s:
                        return s;
                    case TableCell cell:
                        owner = cell.Row?.Table?.Parent?.Owner;
                        break;
                    case Block b:
                        owner = b.Parent?.Owner;
                        break;
                    default:
                        return null;
                }
            }
        }
    }

    /// <summary>The table cell directly containing this block, or null.</summary>
    public TableCell? Cell => Parent?.Owner as TableCell;

    public int Index => Parent?.IndexOf(this) ?? -1;
}

/// <summary>A list of blocks that keeps each block's <see cref="Block.Parent"/> up to date.</summary>
public sealed class BlockList : IList<Block>
{
    private readonly List<Block> _items = new();
    private Dictionary<Block, int>? _index;

    public BlockList(IBlockContainer owner)
    {
        Owner = owner;
    }

    public IBlockContainer Owner { get; }

    public int Count => _items.Count;

    public bool IsReadOnly => false;

    public Block this[int index]
    {
        get => _items[index];
        set
        {
            if (ReferenceEquals(_items[index], value)) return;
            Detach(_items[index]);
            Attach(value);
            _items[index] = value;
            _index = null;
        }
    }

    public int IndexOf(Block item)
    {
        if (_items.Count < 24) return _items.IndexOf(item);
        if (_index == null)
        {
            _index = new Dictionary<Block, int>(_items.Count, ReferenceEqualityComparer<Block>.Instance);
            for (var i = 0; i < _items.Count; i++) _index[_items[i]] = i;
        }
        return _index.TryGetValue(item, out var at) ? at : -1;
    }

    public void Insert(int index, Block item)
    {
        Attach(item);
        _items.Insert(index, item);
        _index = null;
    }

    public void InsertRange(int index, IEnumerable<Block> items)
    {
        var list = items.ToList();
        foreach (var b in list) Attach(b);
        _items.InsertRange(index, list);
        _index = null;
    }

    public void RemoveAt(int index)
    {
        Detach(_items[index]);
        _items.RemoveAt(index);
        _index = null;
    }

    public void RemoveRange(int index, int count)
    {
        for (var i = index; i < index + count; i++) Detach(_items[i]);
        _items.RemoveRange(index, count);
        _index = null;
    }

    public void Add(Block item)
    {
        Attach(item);
        _items.Add(item);
        _index = null;
    }

    public void AddRange(IEnumerable<Block> items)
    {
        foreach (var b in items) Add(b);
    }

    public void Clear()
    {
        foreach (var b in _items) Detach(b);
        _items.Clear();
        _index = null;
    }

    public bool Contains(Block item) => IndexOf(item) >= 0;

    public void CopyTo(Block[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);

    public bool Remove(Block item)
    {
        var i = IndexOf(item);
        if (i < 0) return false;
        RemoveAt(i);
        return true;
    }

    /// <summary>Replaces the whole content (undo / redo).</summary>
    public void Reset(IEnumerable<Block> items)
    {
        foreach (var b in _items)
            if (ReferenceEquals(b.Parent, this)) b.Parent = null;
        _items.Clear();
        foreach (var b in items)
        {
            b.Parent = this;
            _items.Add(b);
        }
        _index = null;
    }

    public Block[] ToArray() => _items.ToArray();

    public IEnumerator<Block> GetEnumerator() => _items.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _items.GetEnumerator();

    private void Attach(Block b)
    {
        if (b.Parent != null && !ReferenceEquals(b.Parent, this)) b.Parent.Remove(b);
        b.Parent = this;
    }

    private void Detach(Block b)
    {
        if (ReferenceEquals(b.Parent, this)) b.Parent = null;
    }
}

/// <summary>Compares by reference (for dictionaries keyed by model objects).</summary>
public sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T> where T : class
{
    public static readonly ReferenceEqualityComparer<T> Instance = new();

    public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

    public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
}

/// <summary>A paragraph (w:p): properties and inline content.</summary>
public sealed class Paragraph : Block
{
    public Paragraph()
    {
    }

    public Paragraph(string text, string? styleId = null)
    {
        if (styleId != null) Props.StyleId = styleId;
        if (text.Length > 0) Inlines.Add(new TextRun(text));
    }

    public ParaProps Props { get; set; } = new();

    public List<Inline> Inlines { get; set; } = new();

    /// <summary>The w:p element's own attributes (w14:paraId, w14:textId, rsids), without children.</summary>
    public XElement? Shell { get; set; }

    public string? StyleId => Props.StyleId;

    /// <summary>Number of positions (text characters + objects; markers count 0).</summary>
    public int Length
    {
        get
        {
            var n = 0;
            foreach (var i in Inlines) n += i.Length;
            return n;
        }
    }

    /// <summary>The text with U+FFFC for each object; index i is position i.</summary>
    public string Text
    {
        get
        {
            var sb = new StringBuilder();
            foreach (var i in Inlines)
            {
                if (i is TextRun t) sb.Append(t.Text);
                else if (i.Length == 1) sb.Append('￼');
            }
            return sb.ToString();
        }
    }

    /// <summary>Readable text: objects become their text (field results, symbols), breaks become spaces / new lines.</summary>
    public string PlainText(bool includeDeleted = false)
    {
        var sb = new StringBuilder();
        foreach (var i in Inlines)
        {
            if (i.Deleted != null && !includeDeleted) continue;
            switch (i)
            {
                case TextRun t:
                    foreach (var c in t.Text)
                    {
                        sb.Append(c switch
                        {
                            TextRun.LineBreak or TextRun.PageBreak or TextRun.ColumnBreak => '\n',
                            TextRun.NoBreakHyphen => '-',
                            TextRun.SoftHyphen => '\0',
                            _ => c,
                        });
                    }
                    break;
                case FieldInline f:
                    sb.Append(f.ResultText);
                    break;
                case SymbolChar s:
                    sb.Append(s.Char);
                    break;
                case OpaqueInline o when !o.IsMarker:
                    sb.Append(o.Display);
                    break;
            }
        }
        return sb.ToString().Replace("\0", "");
    }

    public bool IsEmpty => Length == 0;

    public override Block CloneBlock() => new Paragraph
    {
        Props = Props.Clone(),
        Inlines = Inlines.Select(i => i.Clone()).ToList(),
        Shell = Shell.Copy(),
    };

    /// <summary>A copy for the clipboard / templates without the paragraph ids.</summary>
    public Paragraph CloneFresh()
    {
        var p = (Paragraph)CloneBlock();
        p.Shell = null;
        return p;
    }

    /// <summary>Finds the inline that contains <paramref name="offset"/>: its index and the offset inside it.
    /// At a boundary, <paramref name="preferAfter"/> picks the following inline instead of the previous one.</summary>
    public (int Index, int Inner) Locate(int offset, bool preferAfter = false)
    {
        var pos = 0;
        for (var i = 0; i < Inlines.Count; i++)
        {
            var len = Inlines[i].Length;
            if (offset < pos + len || (offset == pos + len && !preferAfter && len > 0))
                return (i, offset - pos);
            pos += len;
        }
        return (Inlines.Count, 0);
    }

    /// <summary>The position where inline <paramref name="index"/> starts.</summary>
    public int OffsetOf(int index)
    {
        var pos = 0;
        for (var i = 0; i < index && i < Inlines.Count; i++) pos += Inlines[i].Length;
        return pos;
    }

    public int OffsetOf(Inline inline)
    {
        var pos = 0;
        foreach (var i in Inlines)
        {
            if (ReferenceEquals(i, inline)) return pos;
            pos += i.Length;
        }
        return -1;
    }

    /// <summary>The inline at a position (the one starting there, or covering it), or null at the end.</summary>
    public Inline? InlineAt(int offset)
    {
        var pos = 0;
        foreach (var i in Inlines)
        {
            if (i.Length > 0 && offset >= pos && offset < pos + i.Length) return i;
            pos += i.Length;
        }
        return null;
    }

    /// <summary>The character properties to use when typing at <paramref name="offset"/> (Word: those of the previous character).</summary>
    public RunProps PropsForTypingAt(int offset)
    {
        Inline? candidate = null;
        var pos = 0;
        foreach (var i in Inlines)
        {
            if (i.Length > 0)
            {
                if (pos < offset) candidate = i;
                else if (candidate == null && pos >= offset) candidate = i;
                if (pos >= offset) break;
            }
            pos += i.Length;
        }
        if (candidate is TextRun || candidate is FieldInline || candidate is SymbolChar) return candidate.Props.Clone();
        var mark = Props.MarkProps();
        if (candidate == null && mark != null)
        {
            var copy = mark.Clone();
            copy.Remove("ins");
            copy.Remove("del");
            copy.Remove("moveFrom");
            copy.Remove("moveTo");
            copy.Remove("rPrChange");
            return copy;
        }
        return candidate?.Props.Clone() ?? new RunProps();
    }

    /// <summary>
    /// Splits text runs so a boundary falls exactly at <paramref name="offset"/>. Returns the index of the first
    /// inline at or after the offset.
    /// </summary>
    public int SplitAt(int offset)
    {
        var pos = 0;
        for (var i = 0; i < Inlines.Count; i++)
        {
            var inline = Inlines[i];
            var len = inline.Length;
            if (offset == pos) return i;
            if (offset < pos + len)
            {
                if (inline is TextRun run)
                {
                    var cut = offset - pos;
                    var tail = (TextRun)run.Clone();
                    tail.Text = run.Text.Substring(cut);
                    run.Text = run.Text.Substring(0, cut);
                    Inlines.Insert(i + 1, tail);
                    return i + 1;
                }
                return i; // objects are length 1: offset == pos here
            }
            pos += len;
        }
        return Inlines.Count;
    }

    /// <summary>Inserts an inline at a position (after markers that sit there when <paramref name="afterMarkers"/>).</summary>
    public void InsertInline(int offset, Inline inline, bool afterMarkers = false)
    {
        var i = SplitAt(offset);
        if (afterMarkers)
            while (i < Inlines.Count && Inlines[i].Length == 0 && Inlines[i] is not DrawingObject) i++;
        Inlines.Insert(i, inline);
    }

    /// <summary>Inserts text with the given properties, merging with a neighbouring run that has the same formatting.</summary>
    public void InsertText(int offset, string text, RunProps props, Hyperlink? link = null, RevisionInfo? inserted = null)
    {
        if (text.Length == 0) return;
        var probe = new TextRun(text) { Props = props, Link = link, Inserted = inserted };
        var (index, inner) = Locate(offset);
        if (index < Inlines.Count && Inlines[index] is TextRun run && inner > 0 && inner <= run.Length && run.CanMergeWith(probe))
        {
            run.Text = run.Text.Insert(inner, text);
            return;
        }
        // At the start of a run with the same formatting.
        var after = Locate(offset, preferAfter: true);
        if (after.Index < Inlines.Count && Inlines[after.Index] is TextRun next && after.Inner == 0 && next.CanMergeWith(probe))
        {
            next.Text = text + next.Text;
            return;
        }
        InsertInline(offset, probe);
    }

    /// <summary>Removes positions [start, end). Markers inside the range are kept (they collapse to the start).</summary>
    public List<Inline> RemoveRange(int start, int end)
    {
        var removed = new List<Inline>();
        if (end <= start) return removed;
        var first = SplitAt(start);
        var last = SplitAt(end);
        var keep = new List<Inline>();
        for (var i = first; i < last; i++)
        {
            var inline = Inlines[i];
            if (inline.Length == 0) keep.Add(inline);
            else removed.Add(inline);
        }
        Inlines.RemoveRange(first, last - first);
        Inlines.InsertRange(first, keep);
        Normalize();
        return removed;
    }

    /// <summary>Copies of the inlines in [start, end) (markers inside included).</summary>
    public List<Inline> CopyRange(int start, int end)
    {
        var result = new List<Inline>();
        var pos = 0;
        foreach (var inline in Inlines)
        {
            var len = inline.Length;
            if (len == 0)
            {
                if (pos > start && pos < end) result.Add(inline.Clone());
                continue;
            }
            var s = Math.Max(start, pos);
            var e = Math.Min(end, pos + len);
            if (s < e)
            {
                if (inline is TextRun t)
                {
                    var c = (TextRun)t.Clone();
                    c.Text = t.Text.Substring(s - pos, e - s);
                    result.Add(c);
                }
                else
                {
                    result.Add(inline.Clone());
                }
            }
            pos += len;
        }
        return result;
    }

    /// <summary>Merges neighbouring text runs with identical formatting and drops empty text runs.</summary>
    public void Normalize()
    {
        for (var i = Inlines.Count - 1; i >= 0; i--)
        {
            if (Inlines[i] is TextRun { Text.Length: 0 }) Inlines.RemoveAt(i);
        }
        for (var i = Inlines.Count - 2; i >= 0; i--)
        {
            if (Inlines[i] is TextRun a && Inlines[i + 1] is TextRun b && a.CanMergeWith(b))
            {
                a.Text += b.Text;
                Inlines.RemoveAt(i + 1);
            }
        }
    }

    /// <summary>Applies a change to the run properties of everything in [start, end) (splitting runs at the ends).</summary>
    public void FormatRange(int start, int end, Action<RunProps> change)
    {
        if (end <= start) return;
        var first = SplitAt(start);
        var last = SplitAt(end);
        for (var i = first; i < last; i++)
        {
            var inline = Inlines[i];
            if (inline.Length == 0 && inline is not DrawingObject) continue;
            change(inline.Props);
            if (inline is FieldInline f)
                foreach (var r in f.Result) change(r.Props);
        }
        Normalize();
    }

    /// <summary>The inlines from <paramref name="offset"/> to the end, removed from this paragraph (for splitting it).</summary>
    public List<Inline> TakeTail(int offset)
    {
        var i = SplitAt(offset);
        var tail = Inlines.Skip(i).ToList();
        Inlines.RemoveRange(i, Inlines.Count - i);
        return tail;
    }
}

/// <summary>A table (w:tbl).</summary>
public sealed class Table : Block
{
    public TableProps Props { get; set; } = new();

    /// <summary>Grid column widths in twips (w:tblGrid).</summary>
    public List<int> Grid { get; set; } = new();

    public List<TableRow> Rows { get; set; } = new();

    public XElement? Shell { get; set; }

    public void AddRow(TableRow row)
    {
        row.Table = this;
        Rows.Add(row);
    }

    public void InsertRow(int index, TableRow row)
    {
        row.Table = this;
        Rows.Insert(index, row);
    }

    /// <summary>Re-points every row and cell at its parent (after undo or bulk changes).</summary>
    public void FixParents()
    {
        foreach (var r in Rows)
        {
            r.Table = this;
            foreach (var c in r.Cells) c.Row = r;
        }
    }

    public override Block CloneBlock()
    {
        var t = new Table { Props = Props.Clone(), Grid = Grid.ToList(), Shell = Shell.Copy() };
        foreach (var r in Rows) t.AddRow(r.Clone());
        return t;
    }

    public int ColumnCount => Grid.Count > 0 ? Grid.Count : Rows.Count == 0 ? 0 : Rows.Max(r => r.GridBefore + r.Cells.Sum(c => c.Props.GridSpan) + r.GridAfter);

    /// <summary>The grid column where a cell starts.</summary>
    public int GridColumnOf(TableCell cell)
    {
        var row = cell.Row!;
        var col = row.GridBefore;
        foreach (var c in row.Cells)
        {
            if (ReferenceEquals(c, cell)) return col;
            col += c.Props.GridSpan;
        }
        return -1;
    }

    /// <summary>The cell of a row that covers a grid column, or null.</summary>
    public static TableCell? CellAtColumn(TableRow row, int gridColumn)
    {
        var col = row.GridBefore;
        foreach (var c in row.Cells)
        {
            var span = c.Props.GridSpan;
            if (gridColumn >= col && gridColumn < col + span) return c;
            col += span;
        }
        return null;
    }
}

/// <summary>A table row (w:tr).</summary>
public sealed class TableRow
{
    public RowProps Props { get; set; } = new();

    public List<TableCell> Cells { get; set; } = new();

    public Table? Table { get; set; }

    /// <summary>w:tr attributes (w14:paraId…).</summary>
    public XElement? Shell { get; set; }

    /// <summary>Row-level table property exceptions (w:tblPrEx), kept as they were.</summary>
    public XElement? PropertyExceptions { get; set; }

    /// <summary>Elements between rows (bookmarks, permission ranges) written before this row.</summary>
    public List<XElement> Leading { get; set; } = new();

    public int GridBefore => Props.GridBefore;
    public int GridAfter => Props.GridAfter;

    public int Index => Table?.Rows.IndexOf(this) ?? -1;

    public void AddCell(TableCell cell)
    {
        cell.Row = this;
        Cells.Add(cell);
    }

    public void InsertCell(int index, TableCell cell)
    {
        cell.Row = this;
        Cells.Insert(index, cell);
    }

    public TableRow Clone()
    {
        var r = new TableRow
        {
            Props = Props.Clone(),
            Shell = Shell.Copy(),
            PropertyExceptions = PropertyExceptions.Copy(),
            Leading = Leading.Select(e => new XElement(e)).ToList(),
        };
        foreach (var c in Cells) r.AddCell(c.Clone());
        return r;
    }
}

/// <summary>A table cell (w:tc): properties and blocks.</summary>
public sealed class TableCell : IBlockContainer
{
    public TableCell()
    {
        Blocks = new BlockList(this);
    }

    public CellProps Props { get; set; } = new();

    public BlockList Blocks { get; }

    public TableRow? Row { get; set; }

    public XElement? Shell { get; set; }

    public int Index => Row?.Cells.IndexOf(this) ?? -1;

    public TableCell Clone()
    {
        var c = new TableCell { Props = Props.Clone(), Shell = Shell.Copy() };
        foreach (var b in Blocks) c.Blocks.Add(b.CloneBlock());
        return c;
    }

    /// <summary>A new cell with one empty paragraph.</summary>
    public static TableCell Create(int widthTwips, Paragraph? content = null)
    {
        var c = new TableCell();
        c.Props.Width = new TableWidth("dxa", widthTwips);
        c.Blocks.Add(content ?? new Paragraph());
        return c;
    }
}

/// <summary>
/// A block-level content control (w:sdt). Tables of contents and bibliographies (Word puts them in content
/// controls) are editable through Inkwell's commands; other controls are kept as they were and shown read-only.
/// </summary>
public sealed class SdtBlock : Block, IBlockContainer
{
    public SdtBlock()
    {
        Blocks = new BlockList(this);
    }

    /// <summary>w:sdtPr.</summary>
    public XElement Properties { get; set; } = new(Ns.W + "sdtPr");

    /// <summary>w:sdtEndPr, if any.</summary>
    public XElement? EndProperties { get; set; }

    public BlockList Blocks { get; }

    /// <summary>The whole element as loaded, for read-only controls (written back unchanged).</summary>
    public XElement? Original { get; set; }

    public string? SourcePart { get; set; }

    /// <summary>Table of Contents / Bibliographies / other gallery name, or null.</summary>
    public string? Gallery => Properties.Element(Ns.W + "docPartObj")?.Element(Ns.W + "docPartGallery").Attr("val");

    public bool IsTableOfContents => Gallery == "Table of Contents";

    public bool IsBibliography => Gallery == "Bibliographies";

    /// <summary>Content Inkwell manages (TOC, bibliography); everything else is read-only.</summary>
    public bool IsManaged => IsTableOfContents || IsBibliography;

    public string? Tag => Properties.Element(Ns.W + "tag").Attr("val");

    public string? Alias => Properties.Element(Ns.W + "alias").Attr("val");

    public override Block CloneBlock()
    {
        var s = new SdtBlock { Properties = new XElement(Properties), EndProperties = EndProperties.Copy(), Original = Original.Copy(), SourcePart = SourcePart };
        foreach (var b in Blocks) s.Blocks.Add(b.CloneBlock());
        return s;
    }
}

/// <summary>
/// A field whose result spans several paragraphs (a table of contents, a bibliography, an index). The result
/// paragraphs are kept as blocks; the field begins at inline <see cref="BeginAt"/> of the first paragraph and ends
/// at inline <see cref="EndAt"/> of the last.
/// </summary>
public sealed class FieldBlock : Block, IBlockContainer
{
    public FieldBlock()
    {
        Blocks = new BlockList(this);
    }

    public string Code { get; set; } = "";

    public BlockList Blocks { get; }

    public int BeginAt { get; set; }

    public int EndAt { get; set; }

    public bool Dirty { get; set; }

    public bool Locked { get; set; }

    public RunProps? BeginProps { get; set; }

    public RunProps? EndProps { get; set; }

    public FieldKind Kind => FieldCode.KindOf(Code);

    public override Block CloneBlock()
    {
        var f = new FieldBlock
        {
            Code = Code,
            BeginAt = BeginAt,
            EndAt = EndAt,
            Dirty = Dirty,
            Locked = Locked,
            BeginProps = BeginProps?.Clone(),
            EndProps = EndProps?.Clone(),
        };
        foreach (var b in Blocks) f.Blocks.Add(b.CloneBlock());
        return f;
    }
}

/// <summary>A block-level element Inkwell keeps exactly as it was and shows read-only (w:altChunk, unusual tables…).</summary>
public sealed class OpaqueBlock : Block
{
    public OpaqueBlock(XElement xml, string kind)
    {
        Xml = xml;
        Kind = kind;
    }

    public XElement Xml { get; set; }

    public string Kind { get; set; }

    public string? SourcePart { get; set; }

    /// <summary>Blocks to show in its place (parsed from it for display), or null for a placeholder.</summary>
    public List<Block>? Display { get; set; }

    public string Description { get; set; } = "";

    public override Block CloneBlock() => new OpaqueBlock(new XElement(Xml), Kind)
    {
        SourcePart = SourcePart,
        Display = Display?.Select(b => b.CloneBlock()).ToList(),
        Description = Description,
    };
}

/// <summary>Bookmark / comment range markers that sit between blocks (for example around a table).</summary>
public sealed class MarkerBlock : Block
{
    public List<Inline> Markers { get; set; } = new();

    /// <summary>Other between-block elements kept as they were (permission ranges, move ranges).</summary>
    public List<XElement> Other { get; set; } = new();

    public override Block CloneBlock() => new MarkerBlock
    {
        Markers = Markers.Select(m => m.Clone()).ToList(),
        Other = Other.Select(e => new XElement(e)).ToList(),
    };
}
