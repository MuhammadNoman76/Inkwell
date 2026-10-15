using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Inkwell.Core.Model;
using Inkwell.Core.Rendering;
using Inkwell.Core.Styles;

namespace Inkwell.Core.Layout;

/// <summary>Where a paragraph appears: on which page, in which fragment, in which part of the page.</summary>
public sealed class ParaLocation
{
    public ParaLocation(PageLayout page, ParaFragment fragment, PageRegion region)
    {
        Page = page;
        Fragment = fragment;
        Region = region;
    }

    public PageLayout Page { get; }
    public ParaFragment Fragment { get; }
    public PageRegion Region { get; }
}

public enum PageRegion { Body, Notes, Header, Footer }

/// <summary>
/// The laid-out document: pages for the current view, kept up to date incrementally. After an edit only the
/// pages from the first changed block are laid out again, and pagination stops as soon as a page starts where
/// it started before (so typing stays instant in long documents).
/// </summary>
public sealed class DocumentLayout : IDisposable
{
    private readonly LineFormatter _lines = new();
    private readonly BlockLayouter _blocks;
    private readonly Paginator _paginator;
    private List<(Block Block, int Version)> _flowStamp = new();
    private Dictionary<Paragraph, string> _labels = new(ReferenceEqualityComparer<Paragraph>.Instance);
    private Dictionary<(NoteKind, int), string> _noteNumbers = new();
    private int _docStamp = int.MinValue;
    private readonly Dictionary<Paragraph, List<ParaLocation>> _index = new(ReferenceEqualityComparer<Paragraph>.Instance);
    private int _pageStamp;

    public DocumentLayout(Document document, ViewOptions? view = null)
    {
        Document = document;
        View = view ?? new ViewOptions();
        Styles = new StyleResolver(document);
        Images = new MediaImages(document.Media);
        Env = new LayoutEnv(document, Styles, View);
        Env.Images = Images.Get;
        Env.DrawTextBox = DrawTextBox;
        _blocks = new BlockLayouter(_lines, Env);
        _paginator = new Paginator(_blocks);
    }

    public Document Document { get; }
    public ViewOptions View { get; }
    public StyleResolver Styles { get; }
    public MediaImages Images { get; }
    public LayoutEnv Env { get; }
    public LineFormatter Lines => _lines;
    public BlockLayouter Blocks => _blocks;
    public Paginator Paginator => _paginator;

    public List<PageLayout> Pages { get; private set; } = new();

    /// <summary>Shown in FILENAME fields.</summary>
    public string? FileName
    {
        get => _paginator.FileName;
        set => _paginator.FileName = value;
    }

    /// <summary>Time the last update took, and how many pages it laid out (for diagnostics and the performance tests).</summary>
    public TimeSpan LastUpdateTime { get; private set; }
    public int LastPagesLaidOut { get; private set; }
    public bool LastUpdateWasFull { get; private set; }

    public void Dispose() => _lines.Dispose();

    // Section settings (page size, margins, columns, headers) change every page of a section, not just the edited
    // paragraph's: they are part of the stamp, so a change lays out everything again.
    private int DocumentStamp() => unchecked(Document.Version * 31 + Document.Styles.Version * 1009 + Document.Numbering.Version * 7919 + Document.Theme.Version * 104729 + View.LayoutStamp
        + string.Join("|", Document.Sections().Select(s => s.Props.LayoutKey())).GetHashCode() * 13);

    /// <summary>Forgets everything (after view option or style changes).</summary>
    public void Invalidate()
    {
        _docStamp = int.MinValue;
        _blocks.Clear();
        Styles.Invalidate();
    }

    /// <summary>Lays out the whole document.</summary>
    public void LayoutAll()
    {
        Invalidate();
        Update();
    }

    /// <summary>Brings the pages up to date with the document (incrementally when possible).</summary>
    public void Update()
    {
        var watch = Stopwatch.StartNew();
        var stamp = DocumentStamp();
        var full = stamp != _docStamp || Pages.Count == 0;
        if (full)
        {
            _blocks.Clear();
            Styles.Invalidate();
            Images.Store = Document.Media;
        }

        // Labels and note numbers can change anywhere after an edit (inserting a list item renumbers the rest).
        var labels = Numberer.Labels(Document, Styles, Document.Stories);
        var notes = Numberer.NoteNumbers(Document);
        Env.Labels = labels;
        Env.NoteNumbers = notes;

        var oldFlowStamp = _flowStamp;
        var oldPages = Pages;
        _paginator.KnownPageCount = Math.Max(1, oldPages.Count);
        _paginator.PrepareFlow();
        var flow = _paginator.Flow;
        _flowStamp = flow.Select(f => (f.Block, f.Block.Version)).ToList();

        List<PageLayout> pages;
        if (full)
        {
            pages = _paginator.Paginate(0, new FlowCursor(0, 0), 1, null, out _);
            LastPagesLaidOut = pages.Count;
        }
        else
        {
            // Changed range of the flow: compare (block, version) from both ends, plus paragraphs whose label / note numbers changed.
            var first = 0;
            while (first < oldFlowStamp.Count && first < _flowStamp.Count && ReferenceEquals(oldFlowStamp[first].Block, _flowStamp[first].Block) && oldFlowStamp[first].Version == _flowStamp[first].Version)
                first++;
            int oldLast = oldFlowStamp.Count - 1, newLast = _flowStamp.Count - 1;
            while (oldLast >= first && newLast >= first && ReferenceEquals(oldFlowStamp[oldLast].Block, _flowStamp[newLast].Block) && oldFlowStamp[oldLast].Version == _flowStamp[newLast].Version)
            {
                oldLast--;
                newLast--;
            }
            var dirtyFrom = first;
            var dirtyTo = Math.Max(newLast, first);
            for (var i = 0; i < flow.Count; i++)
            {
                if (flow[i].Block is not Paragraph p) continue;
                var had = _labels.TryGetValue(p, out var oldLabel);
                var has = labels.TryGetValue(p, out var newLabel);
                if (had != has || oldLabel != newLabel || HasChangedNotes(p, notes))
                {
                    dirtyFrom = Math.Min(dirtyFrom, i);
                    dirtyTo = Math.Max(dirtyTo, i);
                }
            }
            if (dirtyFrom >= _flowStamp.Count && oldFlowStamp.Count == _flowStamp.Count && dirtyFrom >= oldFlowStamp.Count)
            {
                pages = oldPages; // nothing changed in the body
                LastPagesLaidOut = 0;
            }
            else
            {
                pages = Incremental(oldPages, oldFlowStamp, dirtyFrom, dirtyTo);
            }
        }
        _labels = labels;
        _noteNumbers = notes;

        // NUMPAGES in headers and footers: lay those out again if the page count changed.
        if (pages.Count != _paginator.KnownPageCount)
        {
            _paginator.KnownPageCount = pages.Count;
            foreach (var p in pages) RefreshHeaderFooter(p);
        }
        for (var i = 0; i < pages.Count; i++)
        {
            pages[i].Index = i;
            if (pages[i].Stamp == 0) pages[i].Stamp = ++_pageStamp;
        }
        if (View.Mode == ViewMode.Pageless && pages.Count == 1) FitPagelessHeight(pages[0]);
        Pages = pages;
        _docStamp = stamp;
        BuildIndex();
        LastUpdateWasFull = full;
        LastUpdateTime = watch.Elapsed;
    }

    private bool HasChangedNotes(Paragraph p, Dictionary<(NoteKind, int), string> notes)
    {
        foreach (var i in p.Inlines)
        {
            if (i is not NoteReference r) continue;
            var key = (r.Kind, r.Id);
            _noteNumbers.TryGetValue(key, out var oldNumber);
            notes.TryGetValue(key, out var newNumber);
            if (oldNumber != newNumber) return true;
        }
        return false;
    }

    private List<PageLayout> Incremental(List<PageLayout> oldPages, List<(Block Block, int Version)> oldFlow, int dirtyFrom, int dirtyTo)
    {
        var flow = _paginator.Flow;
        // Map old flow indices to blocks.
        Block? OldBlock(int index) => index < oldFlow.Count ? oldFlow[index].Block : null;
        var newIndex = new Dictionary<Block, int>(ReferenceEqualityComparer<Block>.Instance);
        for (var i = 0; i < flow.Count; i++) newIndex[flow[i].Block] = i;
        FlowCursor? Map(FlowCursor c)
        {
            if (c.Index >= oldFlow.Count) return new FlowCursor(flow.Count, 0);
            var block = OldBlock(c.Index);
            if (block == null || !newIndex.TryGetValue(block, out var at)) return null;
            return new FlowCursor(at, c.Pos, c.Row, c.Split);
        }

        // First page to lay out: the one before the page holding the first change (keep-with-next and widows can reach back).
        var startPage = 0;
        for (var i = 0; i < oldPages.Count; i++)
        {
            var mapped = Map(oldPages[i].Start);
            if (mapped == null || mapped.Value.Index >= dirtyFrom) break;
            startPage = i;
        }
        startPage = Math.Max(0, startPage - 1);
        while (startPage > 0 && Map(oldPages[startPage].Start) == null) startPage--;
        var start = Map(oldPages[startPage].Start) ?? new FlowCursor(0, 0);
        if (startPage == 0) start = new FlowCursor(0, 0);
        var number = oldPages[startPage].Number;
        if (startPage == 0) number = 1;

        // Old pages after the change, by their (mapped) start, for resynchronisation.
        var resync = new Dictionary<FlowCursor, int>();
        for (var i = startPage + 1; i < oldPages.Count; i++)
        {
            var mapped = Map(oldPages[i].Start);
            if (mapped != null && mapped.Value.Index > dirtyTo && !resync.ContainsKey(mapped.Value)) resync[mapped.Value] = i;
        }
        int? resumeAt = null;
        var fresh = _paginator.Paginate(startPage, start, number, page =>
        {
            if (!resync.TryGetValue(page.Start, out var oldIndex)) return false;
            var old = oldPages[oldIndex];
            // The same page geometry and header / footer arrangement, else the old page isn't valid.
            if (old.SectionIndex != page.SectionIndex || old.FirstOfSection != page.FirstOfSection || old.HeaderStory != page.HeaderStory ||
                old.FooterStory != page.FooterStory || Math.Abs(old.BodyRect.Top - page.BodyRect.Top) > 0.01 || Math.Abs(old.BodyRect.Bottom - page.BodyRect.Bottom) > 0.01 ||
                old.Size != page.Size)
                return false;
            resumeAt = oldIndex;
            return true;
        }, out var stopped);
        LastPagesLaidOut = fresh.Count;
        var result = new List<PageLayout>(oldPages.Count + 4);
        result.AddRange(oldPages.Take(startPage));
        result.AddRange(fresh);
        if (stopped && resumeAt is { } resume)
        {
            var nextNumber = fresh.Count > 0 ? fresh[fresh.Count - 1].Number + 1 : number;
            for (var i = resume; i < oldPages.Count; i++)
            {
                var page = oldPages[i];
                // Map the reused page's cursors to the new flow.
                page.Start = Map(page.Start) ?? page.Start;
                page.End = Map(page.End) ?? page.End;
                if (page.FirstOfSection && page.Section.PageNumberStart is { } restart) nextNumber = restart;
                if (page.Number != nextNumber)
                {
                    page.Number = nextNumber;
                    page.NumberText = NumberFormats.Format(nextNumber, page.Section.PageNumberFormat);
                    RefreshHeaderFooter(page);
                }
                nextNumber++;
                result.Add(page);
            }
        }
        return result;
    }

    /// <summary>Lays out a page's header and footer again (page numbers changed).</summary>
    private void RefreshHeaderFooter(PageLayout page)
    {
        if (page.HeaderStory == null && page.FooterStory == null) return;
        var fields = _paginator.FieldsFor(page);
        var env = Env.With(fields);
        var width = page.BodyRect.Width;
        if (page.HeaderStory != null)
        {
            page.Header.Clear();
            var (blocks, height) = _blocks.Stack(page.HeaderStory.Blocks, width, null, env);
            BlockLayouter.PlaceStack(blocks, page.HeaderRect.Left, page.HeaderRect.Top, page.Header);
            page.HeaderRect = new Rect(page.HeaderRect.Left, page.HeaderRect.Top, width, height);
        }
        if (page.FooterStory != null)
        {
            page.Footer.Clear();
            var (blocks, height) = _blocks.Stack(page.FooterStory.Blocks, width, null, env);
            var top = page.Size.Height - Units.Twips(page.Section.FooterDistance) - height;
            BlockLayouter.PlaceStack(blocks, page.FooterRect.Left, top, page.Footer);
            page.FooterRect = new Rect(page.FooterRect.Left, top, width, height);
        }
        page.Stamp = ++_pageStamp;
    }

    private static void FitPagelessHeight(PageLayout page)
    {
        var bottom = page.Columns.SelectMany(c => c.Content.Concat(c.Notes)).Select(f => f.Y + f.Height)
            .Concat(page.Floats.Select(f => f.Box.Bottom)).DefaultIfEmpty(page.BodyRect.Top).Max();
        var height = Math.Max(400, bottom + page.Margins.Bottom);
        page.Size = new Size(page.Size.Width, height);
        page.BodyRect = new Rect(page.BodyRect.Left, page.BodyRect.Top, page.BodyRect.Width, height - page.BodyRect.Top - page.Margins.Bottom);
        foreach (var c in page.Columns) c.Box = new Rect(c.Box.Left, c.Box.Top, c.Box.Width, page.BodyRect.Height);
        page.Stamp = 0;
    }

    // ------------------------------------------------------------------ lookup

    private void BuildIndex()
    {
        _index.Clear();
        foreach (var page in Pages)
        {
            foreach (var col in page.Columns)
            {
                foreach (var f in col.Content) Index(page, f, PageRegion.Body);
                foreach (var f in col.Notes) Index(page, f, PageRegion.Notes);
            }
            foreach (var f in page.Header) Index(page, f, PageRegion.Header);
            foreach (var f in page.Footer) Index(page, f, PageRegion.Footer);
        }
    }

    private void Index(PageLayout page, Fragment f, PageRegion region)
    {
        switch (f)
        {
            case ParaFragment pf:
                if (!_index.TryGetValue(pf.Paragraph, out var list))
                {
                    list = new List<ParaLocation>(1);
                    _index[pf.Paragraph] = list;
                }
                list.Add(new ParaLocation(page, pf, region));
                break;
            case TableFragment tf:
                foreach (var row in tf.Rows)
                    foreach (var cell in row.Cells)
                        foreach (var inner in cell.Content)
                            Index(page, inner, region);
                break;
        }
    }

    /// <summary>Where a paragraph is laid out (body paragraphs: one entry per page it spans; header ones: every page).</summary>
    public IReadOnlyList<ParaLocation> Locate(Paragraph p) => _index.TryGetValue(p, out var list) ? list : Array.Empty<ParaLocation>();

    /// <summary>The fragment and line holding a document position (the first page for headers), or null.</summary>
    public (ParaLocation Location, LineInfo Line)? LocateLine(Paragraph p, int offset, PageLayout? preferPage = null)
    {
        var locations = Locate(p);
        if (locations.Count == 0) return null;
        IEnumerable<ParaLocation> candidates = locations;
        if (preferPage != null && locations.Any(l => l.Page == preferPage)) candidates = locations.Where(l => l.Page == preferPage);
        ParaLocation? lastLoc = null;
        LineInfo? lastLine = null;
        foreach (var loc in candidates)
        {
            foreach (var line in loc.Fragment.Lines)
            {
                if (offset >= line.DocStart && offset < line.DocEnd) return (loc, line);
                if (line.EndsParagraph && offset >= line.DocStart) return (loc, line);
                lastLoc = loc;
                lastLine = line;
            }
        }
        // The position is at the very end of a fragment that continues on the next page.
        return lastLoc != null && lastLine != null ? (lastLoc, lastLine) : null;
    }

    // ------------------------------------------------------------------ drawing helpers

    /// <summary>Draws a text box's blocks into a box (read-only content of drawings).</summary>
    private void DrawTextBox(DrawingContext dc, Rect box, List<Block> blocks)
    {
        var (layouts, _) = _blocks.Stack(blocks, box.Width);
        var fragments = new List<Fragment>();
        BlockLayouter.PlaceStack(layouts, box.X, box.Y, fragments);
        dc.PushClip(new RectangleGeometry(box));
        var renderer = new PageRenderer(this);
        foreach (var f in fragments) renderer.DrawFragment(dc, f, null);
        dc.Pop();
    }
}
