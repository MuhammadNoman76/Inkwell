using System.Xml.Linq;
using Inkwell.Core.Model;

namespace Inkwell.Core.Editing;

/// <summary>A saved state of one model object, restorable in place (objects keep their identity).</summary>
internal interface ISnapshot
{
    void Restore();
}

/// <summary>
/// Records the state of every object an edit touches before it changes, and after. Undo restores the "before"
/// states, redo the "after" ones. Objects keep their identity, so layouts and positions stay valid.
/// </summary>
public sealed class Transaction
{
    private readonly Document _doc;
    private readonly Dictionary<object, ISnapshot> _before = new(ReferenceEqualityComparer<object>.Instance);
    private readonly List<object> _order = new();
    private readonly Func<object, ISnapshot> _snapshot;

    internal Transaction(Document doc, string label, Selection selectionBefore)
    {
        _doc = doc;
        Label = label;
        SelectionBefore = selectionBefore;
        _snapshot = o => Snapshots.Take(doc, o);
    }

    public string Label { get; set; }

    public Selection SelectionBefore { get; }

    /// <summary>Typing in the same place merges into one undo step when the keys match.</summary>
    public string? MergeKey { get; set; }

    /// <summary>An automatic change (autocorrect, Markdown shortcut): undone separately with one Ctrl+Z.</summary>
    public bool IsAutomatic { get; set; }

    public bool IsEmpty => _order.Count == 0;

    public IReadOnlyCollection<object> Touched => _order;

    private void TouchObject(object target)
    {
        if (_before.ContainsKey(target)) return;
        _before[target] = _snapshot(target);
        _order.Add(target);
    }

    public void Touch(Paragraph p) => TouchObject(p);
    public void Touch(BlockList list) => TouchObject(list);
    public void Touch(Table t) => TouchObject(t);
    public void Touch(Story story) => TouchObject(story.Blocks);
    public void TouchStyles() => TouchObject(_doc.Styles);
    public void TouchNumbering() => TouchObject(_doc.Numbering);
    public void TouchSettings() => TouchObject(_doc.Settings);
    public void TouchTheme() => TouchObject(_doc.Theme);
    public void TouchNotes() => TouchObject(Snapshots.NotesKey);
    public void TouchComments() => TouchObject(_doc.Comments);
    public void TouchHeadersFooters() => TouchObject(_doc.HeadersFooters);
    public void TouchFinalSection() => TouchObject(_doc.Body);
    public void TouchProperties() => TouchObject(Snapshots.PropertiesKey);
    public void TouchSources() => TouchObject(Snapshots.SourcesKey);

    /// <summary>Touches a block (paragraph, table, or the list holding it for structural changes).</summary>
    public void TouchBlock(Block b)
    {
        switch (b)
        {
            case Paragraph p:
                Touch(p);
                break;
            case Table t:
                Touch(t);
                break;
            case SdtBlock s:
                Touch(s.Blocks);
                break;
            case FieldBlock f:
                Touch(f.Blocks);
                break;
        }
    }

    internal UndoEntry Commit(Selection selectionAfter)
    {
        var entry = new UndoEntry(Label, SelectionBefore, selectionAfter) { MergeKey = MergeKey, IsAutomatic = IsAutomatic, Time = DateTime.UtcNow };
        foreach (var target in _order)
        {
            entry.Targets.Add(target);
            entry.Before.Add(_before[target]);
            entry.After.Add(_snapshot(target));
            Snapshots.MarkChanged(_doc, target);
        }
        return entry;
    }

    /// <summary>Restores everything touched so far (an edit that failed half-way).</summary>
    internal void Rollback()
    {
        for (var i = _order.Count - 1; i >= 0; i--)
        {
            _before[_order[i]].Restore();
            Snapshots.MarkChanged(_doc, _order[i]);
        }
    }
}

/// <summary>One undoable step.</summary>
public sealed class UndoEntry
{
    public UndoEntry(string label, Selection before, Selection after)
    {
        Label = label;
        SelectionBefore = before;
        SelectionAfter = after;
    }

    public string Label { get; }
    public Selection SelectionBefore { get; internal set; }
    public Selection SelectionAfter { get; internal set; }
    public string? MergeKey { get; set; }
    public bool IsAutomatic { get; set; }
    public DateTime Time { get; set; }
    internal List<object> Targets { get; } = new();
    internal List<ISnapshot> Before { get; } = new();
    internal List<ISnapshot> After { get; } = new();

    /// <summary>Folds a later step into this one (consecutive typing).</summary>
    internal void Absorb(UndoEntry later, Document doc)
    {
        for (var i = 0; i < later.Targets.Count; i++)
        {
            var target = later.Targets[i];
            var at = Targets.FindIndex(t => ReferenceEquals(t, target));
            if (at < 0)
            {
                Targets.Add(target);
                Before.Add(later.Before[i]);
                After.Add(later.After[i]);
            }
            else
            {
                After[at] = later.After[i];
            }
        }
        SelectionAfter = later.SelectionAfter;
        Time = later.Time;
        _ = doc;
    }
}

/// <summary>Undo and redo stacks.</summary>
public sealed class UndoStack
{
    private readonly List<UndoEntry> _undo = new();
    private readonly List<UndoEntry> _redo = new();
    private readonly Document _doc;

    public UndoStack(Document doc)
    {
        _doc = doc;
    }

    public int Limit { get; set; } = 500;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public UndoEntry? Top => _undo.Count > 0 ? _undo[_undo.Count - 1] : null;

    public string? UndoLabel => Top?.Label;
    public string? RedoLabel => _redo.Count > 0 ? _redo[_redo.Count - 1].Label : null;

    /// <summary>Bumped on every change, for "is the document saved?" checks.</summary>
    public int Version { get; private set; }

    public event Action? Changed;

    public void Push(UndoEntry entry)
    {
        Version++;
        _redo.Clear();
        var top = Top;
        if (top != null && entry.MergeKey != null && top.MergeKey == entry.MergeKey && !top.IsAutomatic && (entry.Time - top.Time).TotalSeconds < 4)
        {
            top.Absorb(entry, _doc);
        }
        else
        {
            _undo.Add(entry);
            if (_undo.Count > Limit) _undo.RemoveAt(0);
        }
        Changed?.Invoke();
    }

    /// <summary>Stops the next edit from merging into the top step (after moving the caret, for example).</summary>
    public void BreakMerge()
    {
        if (Top != null) Top.MergeKey = null;
    }

    public Selection? Undo()
    {
        if (_undo.Count == 0) return null;
        var entry = _undo[_undo.Count - 1];
        _undo.RemoveAt(_undo.Count - 1);
        for (var i = entry.Targets.Count - 1; i >= 0; i--)
        {
            entry.Before[i].Restore();
            Snapshots.MarkChanged(_doc, entry.Targets[i]);
        }
        _redo.Add(entry);
        Version++;
        Changed?.Invoke();
        return entry.SelectionBefore;
    }

    public Selection? Redo()
    {
        if (_redo.Count == 0) return null;
        var entry = _redo[_redo.Count - 1];
        _redo.RemoveAt(_redo.Count - 1);
        for (var i = 0; i < entry.Targets.Count; i++)
        {
            entry.After[i].Restore();
            Snapshots.MarkChanged(_doc, entry.Targets[i]);
        }
        _undo.Add(entry);
        Version++;
        Changed?.Invoke();
        return entry.SelectionAfter;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke();
    }
}

/// <summary>Snapshots of the kinds of objects edits touch.</summary>
internal static class Snapshots
{
    public static readonly object NotesKey = new();
    public static readonly object PropertiesKey = new();
    public static readonly object SourcesKey = new();

    public static ISnapshot Take(Document doc, object target) => target switch
    {
        Paragraph p => new ParagraphSnap(p),
        BlockList list => new ListSnap(list),
        Table t => new TableSnap(t),
        StyleSheet s => new XSnap(() => s.Doc, d => s.Reset(d)),
        Numbering n => new XSnap(() => n.Doc, d => n.Reset(d)),
        DocSettings s => new XSnap(() => s.Doc, d => s.Reset(d)),
        DocTheme t => new XSnap(() => t.Doc, d => t.Reset(d)),
        List<Comment> comments => new CommentsSnap(comments),
        Dictionary<string, HeaderFooter> headers => new HeadersSnap(headers),
        MainStory body => new FinalSectionSnap(body),
        _ when ReferenceEquals(target, NotesKey) => new NotesSnap(doc),
        _ when ReferenceEquals(target, PropertiesKey) => new PropertiesSnap(doc),
        _ when ReferenceEquals(target, SourcesKey) => new SourcesSnap(doc),
        _ => throw new ArgumentException("Can't snapshot " + target.GetType().Name),
    };

    /// <summary>Tells layout caches what changed.</summary>
    public static void MarkChanged(Document doc, object target)
    {
        switch (target)
        {
            case Block b:
                b.MarkChanged();
                break;
            case BlockList list:
                switch (list.Owner)
                {
                    case TableCell cell:
                        cell.Row?.Table?.MarkChanged();
                        break;
                    case Block owner:
                        owner.MarkChanged();
                        break;
                    case Story s when s is not MainStory:
                        doc.Version++;
                        break;
                }
                break;
            case StyleSheet s:
                s.Changed();
                break;
            case Numbering n:
                n.Changed();
                break;
            default:
                doc.Version++;
                break;
        }
    }

    private sealed class ParagraphSnap : ISnapshot
    {
        private readonly Paragraph _p;
        private readonly XElement _props;
        private readonly List<Inline> _inlines;
        private readonly XElement? _shell;

        public ParagraphSnap(Paragraph p)
        {
            _p = p;
            _props = new XElement(p.Props.X);
            _inlines = p.Inlines.Select(i => i.Clone()).ToList();
            _shell = p.Shell.Copy();
        }

        public void Restore()
        {
            _p.Props = new ParaProps(new XElement(_props));
            _p.Inlines = _inlines.Select(i => i.Clone()).ToList();
            _p.Shell = _shell.Copy();
        }
    }

    private sealed class ListSnap : ISnapshot
    {
        private readonly BlockList _list;
        private readonly Block[] _items;

        public ListSnap(BlockList list)
        {
            _list = list;
            _items = list.ToArray();
        }

        public void Restore() => _list.Reset(_items);
    }

    private sealed class TableSnap : ISnapshot
    {
        private readonly Table _t;
        private readonly XElement _props;
        private readonly List<int> _grid;
        private readonly List<(TableRow Row, XElement Props, List<(TableCell Cell, XElement Props)> Cells)> _rows;

        public TableSnap(Table t)
        {
            _t = t;
            _props = new XElement(t.Props.X);
            _grid = t.Grid.ToList();
            _rows = t.Rows.Select(r => (r, new XElement(r.Props.X), r.Cells.Select(c => (c, new XElement(c.Props.X))).ToList())).ToList();
        }

        public void Restore()
        {
            _t.Props = new TableProps(new XElement(_props));
            _t.Grid = _grid.ToList();
            _t.Rows = new List<TableRow>();
            foreach (var (row, props, cells) in _rows)
            {
                row.Props = new RowProps(new XElement(props));
                row.Cells = new List<TableCell>();
                foreach (var (cell, cellProps) in cells)
                {
                    cell.Props = new CellProps(new XElement(cellProps));
                    row.AddCell(cell);
                }
                _t.AddRow(row);
            }
        }
    }

    private sealed class XSnap : ISnapshot
    {
        private readonly XDocument _copy;
        private readonly Action<XDocument> _restore;

        public XSnap(Func<XDocument> get, Action<XDocument> restore)
        {
            _copy = new XDocument(get());
            _restore = restore;
        }

        public void Restore() => _restore(new XDocument(_copy));
    }

    private sealed class CommentsSnap : ISnapshot
    {
        private readonly List<Comment> _list;
        private readonly List<(Comment C, string Author, string Initials, DateTime? Date, int? Parent, bool Done)> _items;

        public CommentsSnap(List<Comment> list)
        {
            _list = list;
            _items = list.Select(c => (c, c.Author, c.Initials, c.Date, c.ParentId, c.Done)).ToList();
        }

        public void Restore()
        {
            _list.Clear();
            foreach (var (c, author, initials, date, parent, done) in _items)
            {
                c.Author = author;
                c.Initials = initials;
                c.Date = date;
                c.ParentId = parent;
                c.Done = done;
                _list.Add(c);
            }
        }
    }

    private sealed class HeadersSnap : ISnapshot
    {
        private readonly Dictionary<string, HeaderFooter> _dict;
        private readonly List<KeyValuePair<string, HeaderFooter>> _items;

        public HeadersSnap(Dictionary<string, HeaderFooter> dict)
        {
            _dict = dict;
            _items = dict.ToList();
        }

        public void Restore()
        {
            _dict.Clear();
            foreach (var kv in _items) _dict[kv.Key] = kv.Value;
        }
    }

    private sealed class NotesSnap : ISnapshot
    {
        private readonly Document _doc;
        private readonly List<KeyValuePair<int, Note>> _foot;
        private readonly List<KeyValuePair<int, Note>> _end;

        public NotesSnap(Document doc)
        {
            _doc = doc;
            _foot = doc.Footnotes.ToList();
            _end = doc.Endnotes.ToList();
        }

        public void Restore()
        {
            _doc.Footnotes.Clear();
            foreach (var kv in _foot) _doc.Footnotes[kv.Key] = kv.Value;
            _doc.Endnotes.Clear();
            foreach (var kv in _end) _doc.Endnotes[kv.Key] = kv.Value;
        }
    }

    private sealed class FinalSectionSnap : ISnapshot
    {
        private readonly MainStory _body;
        private readonly XElement _section;

        public FinalSectionSnap(MainStory body)
        {
            _body = body;
            _section = new XElement(body.FinalSection.X);
        }

        public void Restore() => _body.FinalSection = new SectProps(new XElement(_section));
    }

    private sealed class PropertiesSnap : ISnapshot
    {
        private readonly Document _doc;
        private readonly CoreProps _copy;

        public PropertiesSnap(Document doc)
        {
            _doc = doc;
            _copy = doc.Properties.Clone();
        }

        public void Restore() => _doc.Properties = _copy.Clone();
    }

    private sealed class SourcesSnap : ISnapshot
    {
        private readonly Document _doc;
        private readonly BibliographySources _copy;

        public SourcesSnap(Document doc)
        {
            _doc = doc;
            _copy = doc.Sources.Clone();
        }

        public void Restore() => _doc.Sources = _copy.Clone();
    }
}
