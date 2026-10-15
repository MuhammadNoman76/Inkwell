using System.Windows;
using System.Windows.Media.TextFormatting;
using TextRun = Inkwell.Core.Model.TextRun;
using Inkwell.Core.Model;
using Inkwell.Core.Styles;

namespace Inkwell.Core.Layout;

/// <summary>A block in the body's reading order (content controls and multi-paragraph fields opened up).</summary>
public sealed class FlowItem
{
    public Block Block = null!;

    /// <summary>The block directly in the body that contains it.</summary>
    public Block Top = null!;

    public int Section;

    /// <summary>The paragraph whose w:sectPr ends its section.</summary>
    public bool EndsSection;

    /// <summary>Notes appended after the body (endnotes, or footnotes in pageless view).</summary>
    public bool IsNoteContent;
}

internal enum ColumnEnd { Full, ColumnBreak, PageBreak, SectionBreak, Done }

/// <summary>
/// Flows the body into pages: sections (size, margins, columns, break types), headers and footers, keep with
/// next, keep lines together, widow / orphan control, page and column breaks, tables split across pages with
/// repeated header rows, footnotes at the bottom of their column (continued when long), floating pictures with
/// text wrap, and endnotes at the end. Pageless view flows everything into one tall page.
/// </summary>
public sealed class Paginator
{
    private readonly BlockLayouter _blocks;
    private readonly Document _doc;
    private List<FlowItem> _flow = new();
    private List<SectionInfo> _sections = new();
    private readonly Dictionary<(NoteKind, int), List<BlockLayout>> _noteLayouts = new();
    private readonly Dictionary<string, (List<BlockLayout> Blocks, double Height)> _separators = new();

    public Paginator(BlockLayouter blocks)
    {
        _blocks = blocks;
        _doc = blocks.Env.Document;
    }

    private LayoutEnv Env => _blocks.Env;
    private ViewOptions View => Env.View;
    private bool Pageless => View.Mode == ViewMode.Pageless;

    public IReadOnlyList<FlowItem> Flow => _flow;
    public IReadOnlyList<SectionInfo> Sections => _sections;

    /// <summary>Safety limit against runaway layouts.</summary>
    public int MaxPages { get; set; } = 20000;

    // ------------------------------------------------------------------ flow

    /// <summary>Rebuilds the reading-order flow of the body (call after structural changes).</summary>
    public void PrepareFlow()
    {
        _flow = new List<FlowItem>();
        _sections = _doc.Sections();
        _noteLayouts.Clear();
        _separators.Clear();
        var blocks = _doc.Body.Blocks;
        for (var s = 0; s < _sections.Count; s++)
        {
            var sec = _sections[s];
            for (var i = sec.Start; i < sec.End && i < blocks.Count; i++)
            {
                var top = blocks[i];
                foreach (var b in BlockLayouter.Flatten(new[] { top }))
                    _flow.Add(new FlowItem { Block = b, Top = top, Section = s });
                if (top is Paragraph p && ReferenceEquals(p, sec.BreakParagraph) && _flow.Count > 0 && _flow[_flow.Count - 1].Block == p)
                    _flow[_flow.Count - 1].EndsSection = true;
            }
        }
        if (_flow.Count == 0)
        {
            // An empty body still has one (virtual) paragraph so there is a page and a caret.
            var empty = new Paragraph();
            _doc.Body.Blocks.Add(empty);
            _flow.Add(new FlowItem { Block = empty, Top = empty, Section = _sections.Count - 1 });
        }

        // Notes after the body: endnotes (and footnotes in pageless view), in order of their references.
        var lastSection = Math.Max(0, _sections.Count - 1);
        void AddNotes(NoteKind kind)
        {
            var refs = new List<int>();
            foreach (var p in _doc.Body.Paragraphs)
                foreach (var r in p.Inlines.OfType<NoteReference>())
                    if (r.Kind == kind && !refs.Contains(r.Id)) refs.Add(r.Id);
            if (refs.Count == 0) return;
            var notes = kind == NoteKind.Footnote ? _doc.Footnotes : _doc.Endnotes;
            var separator = notes.Values.FirstOrDefault(n => n.Type == "separator");
            if (separator != null)
                foreach (var b in BlockLayouter.Flatten(separator.Blocks))
                    _flow.Add(new FlowItem { Block = b, Top = b, Section = lastSection, IsNoteContent = true });
            foreach (var id in refs)
            {
                if (!notes.TryGetValue(id, out var note)) continue;
                foreach (var b in BlockLayouter.Flatten(note.Blocks))
                    _flow.Add(new FlowItem { Block = b, Top = b, Section = lastSection, IsNoteContent = true });
            }
        }
        AddNotes(NoteKind.Endnote);
        if (Pageless) AddNotes(NoteKind.Footnote);
    }

    public int FlowIndexOf(Block top)
    {
        for (var i = 0; i < _flow.Count; i++)
            if (ReferenceEquals(_flow[i].Top, top)) return i;
        return -1;
    }

    // ------------------------------------------------------------------ pages

    /// <summary>
    /// Lays out pages starting with <paramref name="startIndex"/> at <paramref name="start"/>. When
    /// <paramref name="canStop"/> returns true for a new page's start (the old layout is valid from there), stops
    /// early and reports the page index where it stopped.
    /// </summary>
    public List<PageLayout> Paginate(int startIndex, FlowCursor start, int startNumber, Func<PageLayout, bool>? canStop, out bool stoppedEarly)
    {
        stoppedEarly = false;
        var pages = new List<PageLayout>();
        var cursor = start;
        var number = startNumber;
        var pageIndex = startIndex;
        var guard = 0;
        while (guard++ < MaxPages)
        {
            if (cursor.Index >= _flow.Count && pages.Count > 0) break;
            var sectionIndex = cursor.Index < _flow.Count ? _flow[cursor.Index].Section : Math.Max(0, _sections.Count - 1);
            var section = _sections[sectionIndex].Props;
            var firstOfSection = IsSectionStart(cursor, sectionIndex);
            if (firstOfSection && section.PageNumberStart is { } restart) number = restart;

            // A section that must start on an odd or even page may need a blank page first.
            if (firstOfSection && !Pageless && pageIndex > 0 && section.BreakType is "evenPage" or "oddPage")
            {
                var wantOdd = section.BreakType == "oddPage";
                if ((number % 2 == 1) != wantOdd)
                {
                    var blank = NewPage(pageIndex++, number++, sectionIndex, false, cursor);
                    blank.IsBlank = true;
                    blank.End = cursor;
                    pages.Add(blank);
                }
            }

            var page = NewPage(pageIndex, number, sectionIndex, firstOfSection, cursor);
            if (canStop != null && pages.Count > 0 && canStop(page))
            {
                stoppedEarly = true;
                break;
            }
            cursor = FillPage(page, cursor);
            page.End = cursor;
            pages.Add(page);
            pageIndex++;
            number++;
            if (Pageless) break;
        }
        return pages;
    }

    private bool IsSectionStart(FlowCursor cursor, int sectionIndex)
    {
        if (cursor.Pos != 0 || cursor.Row != 0 || cursor.Split != null) return false;
        if (cursor.Index == 0) return true;
        if (cursor.Index >= _flow.Count) return false;
        return _flow[cursor.Index - 1].Section != sectionIndex;
    }

    /// <summary>A new page with its geometry, header and footer.</summary>
    private PageLayout NewPage(int index, int number, int sectionIndex, bool firstOfSection, FlowCursor start)
    {
        var section = _sections[sectionIndex].Props;
        var page = new PageLayout
        {
            Index = index,
            Number = number,
            NumberText = NumberFormats.Format(number, section.PageNumberFormat),
            Section = section,
            SectionIndex = sectionIndex,
            FirstOfSection = firstOfSection,
            Start = start,
        };
        if (Pageless)
        {
            var width = View.PagelessWidth;
            var pad = 48.0;
            page.Size = new Size(width + 2 * pad, 1e7);
            page.Margins = new Thickness(pad, 40, pad, 60);
            page.BodyRect = new Rect(pad, 40, width, 1e7 - 100);
            return page;
        }
        var w = Units.Twips(section.PageWidth);
        var h = Units.Twips(section.PageHeight);
        if (w < 72 || h < 72)
        {
            w = 816;
            h = 1056;
        }
        page.Size = new Size(w, h);
        var left = Units.Twips(section.MarginLeft);
        var right = Units.Twips(section.MarginRight);
        var top = Units.Twips(Math.Abs(section.MarginTop));
        var bottom = Units.Twips(Math.Abs(section.MarginBottom));
        var gutter = Units.Twips(section.Gutter);
        if (_doc.Settings.GutterAtTop) top += gutter;
        else left += gutter;
        if (_doc.Settings.MirrorMargins && number % 2 == 0) (left, right) = (right, left);
        page.Margins = new Thickness(left, top, right, bottom);
        var body = new Rect(left, top, Math.Max(36, w - left - right), Math.Max(36, h - top - bottom));

        // Header and footer.
        var even = number % 2 == 0;
        page.HeaderStory = PickStory(sectionIndex, true, firstOfSection, even);
        page.FooterStory = PickStory(sectionIndex, false, firstOfSection, even);
        var fields = FieldsFor(page);
        var env = Env.With(fields);
        if (page.HeaderStory != null)
        {
            var (blocks, height) = _blocks.Stack(page.HeaderStory.Blocks, body.Width, null, env);
            var headerTop = Units.Twips(section.HeaderDistance);
            BlockLayouter.PlaceStack(blocks, left, headerTop, page.Header);
            page.HeaderRect = new Rect(left, headerTop, body.Width, height);
            var headerBottom = headerTop + height;
            if (headerBottom > body.Top && section.MarginTop >= 0)
                body = new Rect(body.Left, headerBottom, body.Width, Math.Max(36, body.Bottom - headerBottom));
            AddHeaderFloats(page, page.Header, env);
        }
        if (page.FooterStory != null)
        {
            var (blocks, height) = _blocks.Stack(page.FooterStory.Blocks, body.Width, null, env);
            var footerTop = h - Units.Twips(section.FooterDistance) - height;
            BlockLayouter.PlaceStack(blocks, left, footerTop, page.Footer);
            page.FooterRect = new Rect(left, footerTop, body.Width, height);
            if (footerTop < body.Bottom && section.MarginBottom >= 0)
                body = new Rect(body.Left, body.Top, body.Width, Math.Max(36, footerTop - body.Top));
            AddHeaderFloats(page, page.Footer, env);
        }
        page.BodyRect = body;
        return page;
    }

    /// <summary>Values for page fields in the header and footer of a page (NUMPAGES uses the last known count).</summary>
    public FieldContext FieldsFor(PageLayout page) => new()
    {
        PageNumber = page.Number,
        PageFormat = page.Section.PageNumberFormat ?? "decimal",
        PageCount = Math.Max(page.Index + 1, KnownPageCount),
        SectionPageCount = Math.Max(1, KnownPageCount),
        Title = _doc.Properties.Title,
        Author = _doc.Properties.Creator,
        FileName = FileName,
    };

    /// <summary>Page count from the previous layout (for NUMPAGES while laying out).</summary>
    public int KnownPageCount { get; set; } = 1;

    public string? FileName { get; set; }

    private HeaderFooter? PickStory(int sectionIndex, bool header, bool firstOfSection, bool evenPage)
    {
        var titlePage = _sections[sectionIndex].Props.TitlePage;
        var kind = firstOfSection && titlePage ? "first" : _doc.Settings.EvenAndOddHeaders && evenPage ? "even" : "default";
        for (var s = sectionIndex; s >= 0; s--)
        {
            var id = _sections[s].Props.Reference(header, kind);
            if (id != null && _doc.HeadersFooters.TryGetValue(id, out var story)) return story;
        }
        return null;
    }

    private void AddHeaderFloats(PageLayout page, List<Fragment> fragments, LayoutEnv env)
    {
        foreach (var f in fragments.OfType<ParaFragment>())
        {
            foreach (var (offset, drawing) in f.Layout.Source.Anchors)
            {
                var top = f.Lines.FirstOrDefault()?.Top ?? f.Y;
                var placement = PlaceFloat(drawing, page, new Rect(page.Margins.Left, page.BodyRect.Top, page.BodyRect.Width, page.BodyRect.Height), top, top, f.X);
                placement.Anchor = f.Paragraph;
                placement.AnchorOffset = offset;
                placement.InHeaderFooter = true;
                page.Floats.Add(placement);
            }
        }
    }

    // ------------------------------------------------------------------ filling a page

    private sealed class PageRestart : Exception
    {
    }

    /// <summary>Fills a page from <paramref name="start"/>; returns where the next page starts.</summary>
    private FlowCursor FillPage(PageLayout page, FlowCursor start)
    {
        var preplaced = new List<FloatPlacement>();
        for (var attempt = 0; ; attempt++)
        {
            page.Columns.Clear();
            page.ColumnSeparators.Clear();
            page.Floats.RemoveAll(f => !f.InHeaderFooter);
            page.Floats.AddRange(preplaced);
            page.EndMarker = null;
            try
            {
                return FillRegions(page, start, attempt < 4 ? preplaced : null);
            }
            catch (PageRestart)
            {
                // A float anchored further down overlaps content above it: lay the page out again knowing it.
                preplaced = page.Floats.Where(f => !f.InHeaderFooter).ToList();
            }
        }
    }

    private FlowCursor FillRegions(PageLayout page, FlowCursor cursor, List<FloatPlacement>? restartable)
    {
        var regionTop = page.BodyRect.Top;
        var sectionIndex = page.SectionIndex;
        var pendingNotes = new List<NoteChunk>();
        var hardBreak = page.FirstOfSection || page.Index == 0;
        while (true)
        {
            var section = _sections[sectionIndex].Props;
            var columns = ColumnRects(page, section, regionTop);
            var regionStart = cursor;
            var regionColumns = new List<ColumnArea>();
            ColumnEnd end = ColumnEnd.Done;
            for (var c = 0; c < columns.Count; c++)
            {
                var area = new ColumnArea { Box = columns[c] };
                regionColumns.Add(area);
                page.Columns.Add(area);
                end = FillColumn(page, area, ref cursor, sectionIndex, pendingNotes, hardBreak, restartable);
                hardBreak = end is ColumnEnd.ColumnBreak or ColumnEnd.PageBreak or ColumnEnd.SectionBreak;
                if (end is ColumnEnd.Full or ColumnEnd.ColumnBreak) continue;
                break;
            }
            if (columns.Count > 1) AddSeparators(page, section, regionColumns);

            if (end != ColumnEnd.SectionBreak || cursor.Index >= _flow.Count) return cursor;

            // A section ends on this page: how does the next one start?
            var nextSection = _flow[cursor.Index].Section;
            var next = _sections[nextSection].Props;
            var continuous = next.BreakType == "continuous" && next.PageWidth == section.PageWidth && next.PageHeight == section.PageHeight && !Pageless;
            if (Pageless) continuous = true;
            if (!continuous)
            {
                page.EndMarker = "Section break (" + BreakName(next.BreakType) + ")";
                return cursor;
            }
            // Continuous: balance this region's columns, then start the next section below on the same page.
            if (columns.Count > 1 && !Pageless)
            {
                var balanced = Balance(page, regionColumns, regionStart, sectionIndex, restartable);
                if (balanced != null) cursor = balanced.Value;
            }
            var bottom = regionColumns.Select(c => c.Content.Count == 0 ? c.Box.Top : c.Content.Max(f => f.Y + f.Height)).DefaultIfEmpty(regionTop).Max();
            regionTop = bottom + 6;
            sectionIndex = nextSection;
            hardBreak = true;
            if (regionTop > page.BodyRect.Bottom - 24) return cursor;
            if (Pageless) continue;
        }
    }

    private static string BreakName(string type) => type switch
    {
        "continuous" => "Continuous",
        "evenPage" => "Even Page",
        "oddPage" => "Odd Page",
        "nextColumn" => "Next Column",
        _ => "Next Page",
    };

    private List<Rect> ColumnRects(PageLayout page, SectProps section, double top)
    {
        var body = page.BodyRect;
        var height = Math.Max(24, body.Bottom - top);
        var count = Pageless ? 1 : section.ColumnCount;
        var list = new List<Rect>();
        if (count <= 1)
        {
            list.Add(new Rect(body.Left, top, body.Width, height));
            return list;
        }
        var explicitWidths = section.ColumnWidths;
        var x = body.Left;
        if (explicitWidths.Count == count)
        {
            foreach (var (w, space) in explicitWidths)
            {
                list.Add(new Rect(x, top, Math.Max(24, Units.Twips(w)), height));
                x += Units.Twips(w) + Units.Twips(space);
            }
        }
        else
        {
            var space = Units.Twips(section.ColumnSpace);
            var width = Math.Max(24, (body.Width - space * (count - 1)) / count);
            for (var i = 0; i < count; i++)
            {
                list.Add(new Rect(x, top, width, height));
                x += width + space;
            }
        }
        if (section.Bidi) list.Reverse();
        return list;
    }

    private static void AddSeparators(PageLayout page, SectProps section, List<ColumnArea> columns)
    {
        if (!section.ColumnSeparator) return;
        var ordered = columns.OrderBy(c => c.Box.Left).ToList();
        for (var i = 0; i + 1 < ordered.Count; i++)
        {
            var x = (ordered[i].Box.Right + ordered[i + 1].Box.Left) / 2;
            var bottom = columns.Select(c => c.Content.Count == 0 ? c.Box.Top : c.Content.Max(f => f.Y + f.Height)).Max();
            page.ColumnSeparators.Add((new Point(x, ordered[i].Box.Top), new Point(x, bottom)));
        }
    }

    /// <summary>Re-fills a region's columns with the smallest height that still holds the same content (balanced columns).</summary>
    private FlowCursor? Balance(PageLayout page, List<ColumnArea> columns, FlowCursor start, int sectionIndex, List<FloatPlacement>? restartable)
    {
        var top = columns[0].Box.Top;
        var full = columns[0].Box.Height;
        var used = columns.Sum(c => c.Content.Count == 0 ? 0 : c.Content.Max(f => f.Y + f.Height) - top);
        double lo = Math.Max(12, used / columns.Count - 2), hi = full;
        FlowCursor? best = null;
        List<ColumnArea>? bestColumns = null;
        for (var iteration = 0; iteration < 10 && hi - lo > 2; iteration++)
        {
            var mid = (lo + hi) / 2;
            var trial = TryFill(page, columns, start, sectionIndex, mid, restartable, out var end, out var reachedBreak);
            if (reachedBreak)
            {
                hi = mid;
                best = end;
                bestColumns = trial;
            }
            else
            {
                lo = mid;
            }
        }
        if (bestColumns == null) return null;
        foreach (var c in columns) page.Columns.Remove(c);
        for (var i = 0; i < columns.Count; i++)
        {
            columns[i].Content.Clear();
            columns[i].Notes.Clear();
            columns[i].Content.AddRange(bestColumns[i].Content);
            columns[i].Notes.AddRange(bestColumns[i].Notes);
            columns[i].NotesTop = bestColumns[i].NotesTop;
            page.Columns.Add(columns[i]);
        }
        return best;
    }

    private List<ColumnArea> TryFill(PageLayout page, List<ColumnArea> template, FlowCursor start, int sectionIndex, double height,
        List<FloatPlacement>? restartable, out FlowCursor end, out bool reachedBreak)
    {
        var cursor = start;
        var result = new List<ColumnArea>();
        var pending = new List<NoteChunk>();
        reachedBreak = false;
        var saveFloats = page.Floats.ToList();
        var saveColumns = page.Columns.ToList();
        foreach (var t in template)
        {
            var area = new ColumnArea { Box = new Rect(t.Box.Left, t.Box.Top, t.Box.Width, height) };
            result.Add(area);
            var outcome = FillColumn(page, area, ref cursor, sectionIndex, pending, true, null);
            if (outcome == ColumnEnd.SectionBreak)
            {
                reachedBreak = true;
                break;
            }
            if (outcome is not (ColumnEnd.Full or ColumnEnd.ColumnBreak)) break;
        }
        page.Floats.Clear();
        page.Floats.AddRange(saveFloats);
        page.Columns.Clear();
        page.Columns.AddRange(saveColumns);
        end = cursor;
        return result;
    }

    // ------------------------------------------------------------------ columns

    /// <summary>A piece of a footnote placed at the bottom of a column.</summary>
    private sealed class NoteChunk
    {
        public Note Note = null!;
        public List<BlockLayout> Blocks = null!;
        public int FromBlock;
        public int FromPos;
        public int ToBlock;
        public int ToPos;
        public double Height;
        public bool Continued;
        public bool Complete => ToBlock >= Blocks.Count;
    }

    private sealed class KeepChain
    {
        public int ContentCount;
        public FlowCursor Cursor;
        public double Y;
        public int NoteCount;
        public double NotesHeight;
        public int FloatCount;
    }

    private ColumnEnd FillColumn(PageLayout page, ColumnArea area, ref FlowCursor cursor, int sectionIndex, List<NoteChunk> pendingNotes, bool hardBreakBefore,
        List<FloatPlacement>? restartable)
    {
        var col = area.Box;
        var y = col.Top;
        var notes = new List<NoteChunk>();
        var notesHeight = 0.0;
        KeepChain? chain = null;
        ParagraphLayout? previous = null;
        var lastParagraphEnded = true;

        // Footnote text carried over from the previous column goes first.
        if (pendingNotes.Count > 0)
        {
            var carried = pendingNotes.ToList();
            pendingNotes.Clear();
            foreach (var chunk in carried)
            {
                var available = col.Height * 0.6 - notesHeight - (notes.Count == 0 ? SeparatorHeight(true, col.Width) : 0);
                var piece = SliceNote(chunk.Note, chunk.Blocks, chunk.FromBlock, chunk.FromPos, available);
                piece.Continued = true;
                if (piece.Height <= 0 && notes.Count > 0)
                {
                    pendingNotes.Add(chunk);
                    continue;
                }
                if (notes.Count == 0) notesHeight += SeparatorHeight(true, col.Width);
                notes.Add(piece);
                notesHeight += piece.Height;
                if (!piece.Complete) pendingNotes.Add(new NoteChunk { Note = chunk.Note, Blocks = chunk.Blocks, FromBlock = piece.ToBlock, FromPos = piece.ToPos });
            }
        }

        double Bottom() => col.Bottom - notesHeight;

        var result = ColumnEnd.Done;
        var guard = 0;
        while (cursor.Index < _flow.Count && guard++ < 100000)
        {
            var item = _flow[cursor.Index];
            if (item.Section != sectionIndex && !item.IsNoteContent)
            {
                result = ColumnEnd.SectionBreak;
                break;
            }
            var atTop = area.Content.Count == 0;
            if (item.Block is Paragraph p)
            {
                var layout = _blocks.Paragraph(p, col.Width);
                var starting = cursor.Pos == 0;
                // Page break before: only when something is already on the page.
                if (starting && layout.Props.PageBreakBefore && !Pageless && !(atTop && page.Columns.Count == 1 && page.Columns[0] == area && col.Top <= page.BodyRect.Top + 0.5))
                {
                    result = ColumnEnd.PageBreak;
                    break;
                }
                var outcome = PlaceParagraph(page, area, layout, ref cursor, ref y, Bottom, previous, atTop, hardBreakBefore && atTop, notes, ref notesHeight, pendingNotes, restartable);
                if (outcome.Placed)
                {
                    previous = layout;
                    lastParagraphEnded = outcome.Finished;
                }
                if (outcome.End != null)
                {
                    // The paragraph didn't fit. Keep with next: move the chain of keep-with-next paragraphs along.
                    if (!outcome.Placed && chain != null && chain.ContentCount > 0 && outcome.End == ColumnEnd.Full)
                    {
                        Rollback(area, chain, notes, ref notesHeight, page);
                        cursor = chain.Cursor;
                    }
                    result = outcome.End.Value;
                    break;
                }
                // Keep with next chain bookkeeping.
                if (layout.Props.KeepNext && !InTableOrNotes(item))
                {
                    if (chain == null && starting)
                        chain = new KeepChain
                        {
                            ContentCount = outcome.ContentBefore,
                            Cursor = new FlowCursor(cursor.Index - 1, 0),
                            Y = outcome.YBefore,
                            NoteCount = outcome.NotesBefore,
                            NotesHeight = outcome.NotesHeightBefore,
                            FloatCount = outcome.FloatsBefore,
                        };
                }
                else
                {
                    chain = null;
                }
                if (item.EndsSection && cursor.Index < _flow.Count && _flow[cursor.Index].Section != sectionIndex)
                {
                    result = ColumnEnd.SectionBreak;
                    break;
                }
                if (outcome.HardBreak is { } hb)
                {
                    result = hb;
                    break;
                }
                continue;
            }
            if (item.Block is Table t)
            {
                var tableLayout = _blocks.Table(t, col.Width);
                var outcome = PlaceTable(page, area, tableLayout, ref cursor, ref y, Bottom(), atTop);
                if (outcome == ColumnEnd.Full)
                {
                    if (chain != null && chain.ContentCount > 0 && cursor.Row == 0 && cursor.Split == null && !area.Content.Any(f => f.Block == t))
                    {
                        Rollback(area, chain, notes, ref notesHeight, page);
                        cursor = chain.Cursor;
                    }
                    result = ColumnEnd.Full;
                    break;
                }
                previous = null;
                chain = null;
                continue;
            }
            // A preserved block without display content: a placeholder box.
            var boxHeight = 40.0;
            if (y + boxHeight > Bottom() && !atTop)
            {
                result = ColumnEnd.Full;
                break;
            }
            var label = item.Block is OpaqueBlock ob && ob.Description.Length > 0 ? ob.Description : "Content kept as is";
            area.Content.Add(new BoxFragment(item.Block, label) { X = col.Left, Y = y, Width = col.Width, Height = boxHeight });
            y += boxHeight + 6;
            cursor = new FlowCursor(cursor.Index + 1, 0);
            previous = null;
            chain = null;
        }
        _ = lastParagraphEnded;
        FinishNotes(page, area, notes, notesHeight);
        return result;
    }

    private static bool InTableOrNotes(FlowItem item) => item.IsNoteContent;

    private void Rollback(ColumnArea area, KeepChain chain, List<NoteChunk> notes, ref double notesHeight, PageLayout page)
    {
        if (area.Content.Count > chain.ContentCount) area.Content.RemoveRange(chain.ContentCount, area.Content.Count - chain.ContentCount);
        if (notes.Count > chain.NoteCount) notes.RemoveRange(chain.NoteCount, notes.Count - chain.NoteCount);
        notesHeight = chain.NotesHeight;
        var bodyFloats = page.Floats.Count(f => !f.InHeaderFooter);
        if (bodyFloats > chain.FloatCount)
        {
            var keep = page.Floats.Where(f => f.InHeaderFooter).Concat(page.Floats.Where(f => !f.InHeaderFooter).Take(chain.FloatCount)).ToList();
            page.Floats.Clear();
            page.Floats.AddRange(keep);
        }
    }

    // ------------------------------------------------------------------ paragraphs

    private sealed class ParagraphOutcome
    {
        public bool Placed;
        public bool Finished;
        public ColumnEnd? End;
        public ColumnEnd? HardBreak;
        public int ContentBefore;
        public double YBefore;
        public int NotesBefore;
        public double NotesHeightBefore;
        public int FloatsBefore;
    }

    private ParagraphOutcome PlaceParagraph(PageLayout page, ColumnArea area, ParagraphLayout layout, ref FlowCursor cursor, ref double y, Func<double> bottom,
        ParagraphLayout? previous, bool atTop, bool afterHardBreak, List<NoteChunk> notes, ref double notesHeight, List<NoteChunk> pendingNotes,
        List<FloatPlacement>? restartable)
    {
        var col = area.Box;
        var outcome = new ParagraphOutcome
        {
            ContentBefore = area.Content.Count,
            YBefore = y,
            NotesBefore = notes.Count,
            NotesHeightBefore = notesHeight,
            FloatsBefore = page.Floats.Count(f => !f.InHeaderFooter),
        };
        var props = layout.Props;
        var starting = cursor.Pos == 0;
        var startY = y;

        // Space before: dropped at the top of a column after a natural break (Word), kept after hard breaks.
        var spaceBefore = 0.0;
        if (starting)
        {
            spaceBefore = BlockLayouter.SpaceBefore(layout, previous);
            if (atTop && !afterHardBreak) spaceBefore = 0;
            if (Pageless && atTop && page.Columns.Count == 1) spaceBefore = Math.Min(spaceBefore, 0);
        }
        var drawTop = starting && props.BorderTop != null && !(previous != null && BlockLayouter.SameBorders(previous, layout));
        var fragment = new ParaFragment(layout) { X = col.Left, Width = col.Width, IsStart = starting };
        y += spaceBefore;
        fragment.Y = y;
        fragment.BoxTop = y;
        fragment.DrawTopBorder = drawTop;
        if (drawTop) y += layout.BorderTop;

        var lines = layout.Lines;
        var startPos = cursor.Pos;
        var cachedIndex = lines.FindIndex(l => l.SrcStart == startPos);
        var diverged = cachedIndex < 0;
        if (diverged) cachedIndex = 0;
        var pos = cursor.Pos;
        TextLineBreak? previousBreak = null;
        var placedLines = 0;
        var totalLines = lines.Count;
        var linesBefore = starting ? 0 : lines.Count(l => l.SrcStart < pos);
        ColumnEnd? end = null;
        ColumnEnd? hardBreak = null;
        var finished = false;
        var guard = 0;
        while (guard++ < 100000)
        {
            // The next line: the cached one, unless floating pictures narrow this part of the column.
            var estimate = !diverged && cachedIndex < lines.Count ? lines[cachedIndex].Height : lines.Count > 0 ? lines[0].Height : 16;
            var span = SpanAt(page, col, y, estimate);
            if (span.SkipTo is { } skip)
            {
                if (skip >= bottom())
                {
                    end = ColumnEnd.Full;
                    break;
                }
                y = skip;
                continue;
            }
            LineInfo line;
            if (!diverged && span.Full && cachedIndex < lines.Count && lines[cachedIndex].SrcStart == pos)
            {
                line = lines[cachedIndex].Clone();
            }
            else
            {
                diverged = true;
                var left = span.Full ? 0 : span.Left - col.Left;
                var width = span.Full ? col.Width : span.Width;
                line = _blocks.Lines.FormatLine(layout.Source, pos, pos == 0, left, width, col.Width, previousBreak, _blocks.DefaultTab, out var next);
                previousBreak = next;
                // The narrowed line turned out taller than estimated and now overlaps another float: try again lower.
                if (!span.Full && line.Height > estimate + 0.5)
                {
                    var recheck = SpanAt(page, col, y, line.Height);
                    if (recheck.SkipTo != null || (!recheck.Full && (Math.Abs(recheck.Left - span.Left) > 0.5 || Math.Abs(recheck.Width - span.Width) > 0.5)))
                    {
                        var left2 = recheck.Full ? 0 : recheck.Left - col.Left;
                        var width2 = recheck.Full ? col.Width : recheck.Width;
                        if (recheck.SkipTo is { } s2)
                        {
                            y = s2;
                            continue;
                        }
                        line = _blocks.Lines.FormatLine(layout.Source, pos, pos == 0, left2, width2, col.Width, null, _blocks.DefaultTab, out next);
                        previousBreak = next;
                    }
                }
            }

            var isLast = line.EndsParagraph;
            var lineBottom = y + line.Height + (isLast && props.BorderBottom != null ? layout.BorderBottom : 0);

            // Footnotes referenced on this line must fit at the bottom of the column too.
            var newNotes = Pageless ? new List<(NoteKind, int)>() : NotesOnLine(layout, line).Where(n => !notes.Any(c => c.Note.Id == n.Item2 && c.Note.NoteKind == n.Item1)).ToList();
            var notesNeed = 0.0;
            var noteLayouts = new List<(Note Note, List<BlockLayout> Blocks, double Height)>();
            foreach (var (kind, id) in newNotes)
            {
                var note = _doc.FindNote(kind, id);
                if (note == null) continue;
                var blocks = NoteLayout(note, col.Width);
                var h = BlockLayouter.StackHeight(blocks);
                noteLayouts.Add((note, blocks, h));
                notesNeed += h;
            }
            if (noteLayouts.Count > 0 && notes.Count == 0) notesNeed += SeparatorHeight(false, col.Width);

            var fits = lineBottom <= bottom() - notesNeed + 0.01;
            var partialNotes = false;
            if (!fits && noteLayouts.Count > 0 && lineBottom <= bottom() + 0.01)
            {
                // Place the line and as much of the note as fits; the rest continues in the next column.
                var room = bottom() - lineBottom - (notes.Count == 0 ? SeparatorHeight(false, col.Width) : 0);
                var firstLineHeight = noteLayouts[0].Blocks.FirstOrDefault()?.Paragraph?.Lines.FirstOrDefault()?.Height ?? 1e9;
                if (room >= firstLineHeight) partialNotes = true;
            }
            var forced = !fits && !partialNotes && atTop && placedLines == 0 && area.Content.Count == 0;
            if (!fits && !partialNotes && !forced)
            {
                end = ColumnEnd.Full;
                break;
            }

            // Place the line.
            line.Top = y;
            line.DocStart = Math.Max(0, line.DocStart);
            fragment.Lines.Add(line);
            placedLines++;
            y += line.Height;

            if (noteLayouts.Count > 0)
            {
                if (notes.Count == 0) notesHeight += SeparatorHeight(false, col.Width);
                foreach (var (note, blocks, height) in noteLayouts)
                {
                    if (!partialNotes || bottom() - y >= height)
                    {
                        notes.Add(new NoteChunk { Note = note, Blocks = blocks, ToBlock = blocks.Count, Height = height });
                        notesHeight += height;
                    }
                    else
                    {
                        var piece = SliceNote(note, blocks, 0, 0, Math.Max(0, bottom() - y));
                        if (piece.Height > 0)
                        {
                            notes.Add(piece);
                            notesHeight += piece.Height;
                        }
                        pendingNotes.Add(new NoteChunk { Note = note, Blocks = blocks, FromBlock = piece.ToBlock, FromPos = piece.ToPos });
                    }
                }
            }

            // Floating pictures anchored in this line.
            RegisterFloats(page, area, layout, fragment, line, restartable);

            pos = line.SrcStart + Math.Max(1, line.SrcLength);
            if (!diverged) cachedIndex++;
            if (isLast)
            {
                finished = true;
                break;
            }
            if (line.EndBreak == TextRun.PageBreak && !Pageless)
            {
                hardBreak = ColumnEnd.PageBreak;
                page.EndMarker = "Page break";
                break;
            }
            if (line.EndBreak == TextRun.ColumnBreak && !Pageless)
            {
                hardBreak = ColumnEnd.ColumnBreak;
                break;
            }
            if (forced) atTop = false;
        }

        // Widow and orphan control, keep lines together.
        if (end != null && !finished)
        {
            var remaining = Math.Max(0, totalLines - linesBefore - placedLines);
            if (diverged) remaining = Math.Max(remaining, 1);
            var keepWhole = props.KeepLines && starting && !atTop;
            var orphan = props.WidowControl && starting && placedLines == 1 && totalLines > 1 && !atTop;
            if (keepWhole || orphan)
            {
                placedLines = 0;
                fragment.Lines.Clear();
            }
            else if (props.WidowControl && remaining == 1 && placedLines >= 2 && totalLines >= 3)
            {
                // Move one more line down so the last line isn't alone.
                var last = fragment.Lines[fragment.Lines.Count - 1];
                fragment.Lines.RemoveAt(fragment.Lines.Count - 1);
                placedLines--;
                if (placedLines == 1 && starting && !atTop)
                {
                    placedLines = 0;
                    fragment.Lines.Clear();
                }
                else
                {
                    pos = last.SrcStart;
                }
            }
            if (placedLines == 0)
            {
                // Nothing of the paragraph stays here: undo notes and floats it brought.
                if (notes.Count > outcome.NotesBefore) notes.RemoveRange(outcome.NotesBefore, notes.Count - outcome.NotesBefore);
                notesHeight = outcome.NotesHeightBefore;
                pendingNotes.Clear();
                var bodyFloats = page.Floats.Where(f => !f.InHeaderFooter).ToList();
                if (bodyFloats.Count > outcome.FloatsBefore)
                {
                    foreach (var f in bodyFloats.Skip(outcome.FloatsBefore)) page.Floats.Remove(f);
                }
                y = outcome.YBefore;
                outcome.End = ColumnEnd.Full;
                return outcome;
            }
            // Notes of lines that moved away go with them.
            var kept = fragment.Lines;
            var keptIds = new HashSet<(NoteKind, int)>(layout.Source.NoteRefs
                .Where(n => kept.Any(l => n.Offset >= l.DocStart && (n.Offset < l.DocEnd || (l.EndsParagraph && n.Offset <= l.DocEnd))))
                .Select(n => (n.Note.Kind, n.Note.Id)));
            for (var i = notes.Count - 1; i >= outcome.NotesBefore; i--)
            {
                if (!keptIds.Contains((notes[i].Note.NoteKind, notes[i].Note.Id)))
                {
                    notesHeight -= notes[i].Height;
                    notes.RemoveAt(i);
                }
            }
            pendingNotes.RemoveAll(c => !keptIds.Contains((c.Note.NoteKind, c.Note.Id)) && notes.All(n => n.Note != c.Note));
            if (notes.Count == 0) notesHeight = 0;
            y = fragment.Lines[fragment.Lines.Count - 1].Top + fragment.Lines[fragment.Lines.Count - 1].Height;
        }

        if (fragment.Lines.Count == 0)
        {
            y = outcome.YBefore;
            outcome.End = end ?? ColumnEnd.Full;
            return outcome;
        }

        fragment.IsEnd = finished;
        fragment.FirstLineIndex = linesBefore;
        fragment.DrawBottomBorder = finished && props.BorderBottom != null;
        if (fragment.DrawBottomBorder) y += layout.BorderBottom;
        fragment.BoxBottom = y;
        fragment.Height = y - fragment.Y;
        area.Content.Add(fragment);
        outcome.Placed = true;
        outcome.Finished = finished;
        if (finished)
        {
            y += BlockLayouter.SpaceAfter(layout, NextParagraphLayout(cursor.Index, col.Width));
            cursor = new FlowCursor(cursor.Index + 1, 0);
        }
        else
        {
            cursor = new FlowCursor(cursor.Index, pos);
        }
        outcome.End = end;
        outcome.HardBreak = hardBreak;
        _ = startY;
        return outcome;
    }

    private ParagraphLayout? NextParagraphLayout(int index, double width)
    {
        if (index + 1 < _flow.Count && _flow[index + 1].Block is Paragraph next && !Pageless)
        {
            var p = _blocks.Paragraph(next, width);
            return p;
        }
        if (index + 1 < _flow.Count && _flow[index + 1].Block is Paragraph n2) return _blocks.Paragraph(n2, width);
        return null;
    }

    private static IEnumerable<(NoteKind, int)> NotesOnLine(ParagraphLayout layout, LineInfo line)
    {
        foreach (var (offset, note) in layout.Source.NoteRefs)
        {
            if (note.Kind != NoteKind.Footnote) continue;
            if (offset >= line.DocStart && (offset < line.DocEnd || (line.EndsParagraph && offset <= line.DocEnd))) yield return (note.Kind, note.Id);
        }
    }

    // ------------------------------------------------------------------ floats

    private readonly struct Span
    {
        public Span(bool full, double left, double width, double? skipTo)
        {
            Full = full;
            Left = left;
            Width = width;
            SkipTo = skipTo;
        }

        public bool Full { get; }
        public double Left { get; }
        public double Width { get; }
        public double? SkipTo { get; }
    }

    /// <summary>The horizontal span text may use at [y, y + height) given the page's wrapping floats.</summary>
    private static Span SpanAt(PageLayout page, Rect col, double y, double height)
    {
        if (page.Floats.Count == 0) return new Span(true, col.Left, col.Width, null);
        var top = y;
        var bottom = y + Math.Max(1, height);
        List<FloatPlacement>? hits = null;
        foreach (var f in page.Floats)
        {
            if (!f.Wraps || f.InHeaderFooter) continue;
            var w = f.WrapBox;
            if (w.Bottom <= top || w.Top >= bottom) continue;
            if (w.Right <= col.Left || w.Left >= col.Right) continue;
            (hits ??= new List<FloatPlacement>()).Add(f);
        }
        if (hits == null) return new Span(true, col.Left, col.Width, null);
        if (hits.Any(h => h.Drawing.Wrap == WrapMode.TopAndBottom))
            return new Span(false, col.Left, col.Width, hits.Where(h => h.Drawing.Wrap == WrapMode.TopAndBottom).Max(h => h.WrapBox.Bottom));
        // Free intervals of the column outside the floats.
        var free = new List<(double L, double R)> { (col.Left, col.Right) };
        foreach (var h in hits)
        {
            var next = new List<(double, double)>();
            foreach (var (l, r) in free)
            {
                if (h.WrapBox.Right <= l || h.WrapBox.Left >= r)
                {
                    next.Add((l, r));
                    continue;
                }
                if (h.WrapBox.Left > l) next.Add((l, h.WrapBox.Left));
                if (h.WrapBox.Right < r) next.Add((h.WrapBox.Right, r));
            }
            free = next;
        }
        var side = hits[0].Drawing.Float?.WrapSide ?? "bothSides";
        (double L, double R)? pick = null;
        if (side == "left") pick = free.Where(f => f.R <= hits.Min(h => h.WrapBox.Left) + 0.5).OrderByDescending(f => f.R - f.L).Cast<(double, double)?>().FirstOrDefault();
        else if (side == "right") pick = free.Where(f => f.L >= hits.Max(h => h.WrapBox.Right) - 0.5).OrderByDescending(f => f.R - f.L).Cast<(double, double)?>().FirstOrDefault();
        pick ??= free.OrderByDescending(f => f.R - f.L).Cast<(double, double)?>().FirstOrDefault();
        if (pick == null || pick.Value.R - pick.Value.L < 48)
            return new Span(false, col.Left, col.Width, hits.Min(h => h.WrapBox.Bottom));
        return new Span(false, pick.Value.L, pick.Value.R - pick.Value.L, null);
    }

    private void RegisterFloats(PageLayout page, ColumnArea area, ParagraphLayout layout, ParaFragment fragment, LineInfo line, List<FloatPlacement>? restartable)
    {
        foreach (var (offset, drawing) in layout.Source.Anchors)
        {
            var inLine = offset >= line.DocStart && (offset < line.DocEnd || (line.EndsParagraph && offset <= line.DocEnd) || (line.IsFirst && offset == 0));
            if (!inLine) continue;
            if (page.Floats.Any(f => ReferenceEquals(f.Drawing, drawing))) continue;
            var paraTop = fragment.Lines.Count > 0 ? fragment.Lines[0].Top : line.Top;
            var placement = PlaceFloat(drawing, page, area.Box, paraTop, line.Top, area.Box.Left + line.X);
            placement.Anchor = layout.Paragraph;
            placement.AnchorOffset = offset;
            page.Floats.Add(placement);
            // Content above the anchor that the new float should push aside: lay the page out again.
            if (placement.Wraps && restartable != null && !Pageless && OverlapsPlaced(page, placement, fragment, line))
                throw new PageRestart();
        }
    }

    private static bool OverlapsPlaced(PageLayout page, FloatPlacement f, ParaFragment current, LineInfo currentLine)
    {
        foreach (var col in page.Columns)
        {
            foreach (var frag in col.Content)
            {
                if (frag is ParaFragment pf)
                {
                    foreach (var l in pf.Lines)
                        if (l.Top + l.Height > f.WrapBox.Top + 0.5 && l.Top < f.WrapBox.Bottom && Overlaps(pf.X + l.X, l.FormatWidth, f.WrapBox)) return true;
                }
                else if (frag.Y + frag.Height > f.WrapBox.Top + 0.5 && frag.Y < f.WrapBox.Bottom && Overlaps(frag.X, frag.Width, f.WrapBox))
                {
                    return true;
                }
            }
        }
        foreach (var l in current.Lines.Contains(currentLine) ? current.Lines : current.Lines.Append(currentLine))
        {
            // The anchor line itself counts too: a float beside its own paragraph narrows that first line.
            if (l.Top + l.Height > f.WrapBox.Top + 0.5 && l.Top < f.WrapBox.Bottom && Overlaps(current.X + l.X, l.FormatWidth, f.WrapBox)) return true;
        }
        return false;
    }

    private static bool Overlaps(double x, double width, Rect box) => x < box.Right && x + width > box.Left;

    /// <summary>Computes a floating drawing's box on the page from its anchor settings.</summary>
    public FloatPlacement PlaceFloat(DrawingObject d, PageLayout page, Rect column, double paraTop, double lineTop, double charX)
    {
        var w = Math.Max(1, Units.Emu(d.Width));
        var h = Math.Max(1, Units.Emu(d.Height));
        var info = d.Float ?? new FloatInfo();
        var size = page.Size;
        var m = page.Margins;
        var even = page.Number % 2 == 0;

        (double A, double B) HFrame(string rel) => rel switch
        {
            "page" => (0, size.Width),
            "margin" => (m.Left, size.Width - m.Right),
            "column" => (column.Left, column.Right),
            "character" => (charX, charX),
            "leftMargin" => (0, m.Left),
            "rightMargin" => (size.Width - m.Right, size.Width),
            "insideMargin" => even ? (size.Width - m.Right, size.Width) : (0, m.Left),
            "outsideMargin" => even ? (0, m.Left) : (size.Width - m.Right, size.Width),
            _ => (column.Left, column.Right),
        };
        (double A, double B) VFrame(string rel) => rel switch
        {
            "page" => (0, size.Height),
            "margin" => (m.Top, size.Height - m.Bottom),
            "paragraph" => (paraTop, paraTop),
            "line" => (lineTop, lineTop),
            "topMargin" => (0, m.Top),
            "bottomMargin" => (size.Height - m.Bottom, size.Height),
            "insideMargin" or "outsideMargin" => (0, m.Top),
            _ => (paraTop, paraTop),
        };

        var hf = HFrame(info.Horizontal.RelativeFrom);
        double x = info.Horizontal.Align switch
        {
            "left" or "inside" => hf.A,
            "center" => hf.A + (hf.B - hf.A - w) / 2,
            "right" or "outside" => hf.B - w,
            _ => hf.A + Units.Emu(info.Horizontal.Offset),
        };
        var vf = VFrame(info.Vertical.RelativeFrom);
        double y = info.Vertical.Align switch
        {
            "top" or "inside" => vf.A,
            "center" => vf.A + (vf.B - vf.A - h) / 2,
            "bottom" or "outside" => vf.B - h,
            _ => vf.A + Units.Emu(info.Vertical.Offset),
        };
        if (Pageless)
        {
            // Pageless view: keep the picture beside its paragraph.
            y = paraTop + (info.Vertical.RelativeFrom is "paragraph" or "line" ? Units.Emu(info.Vertical.Offset) : 0);
            x = MathEx.Clamp(x, column.Left, Math.Max(column.Left, column.Right - w));
        }
        var box = new Rect(x, y, w, h);
        var wrap = new Rect(box.Left - Units.Emu(info.DistLeft), box.Top - Units.Emu(info.DistTop),
            box.Width + Units.Emu(info.DistLeft) + Units.Emu(info.DistRight), box.Height + Units.Emu(info.DistTop) + Units.Emu(info.DistBottom));
        return new FloatPlacement { Drawing = d, Box = box, WrapBox = wrap };
    }

    // ------------------------------------------------------------------ notes

    private List<BlockLayout> NoteLayout(Note note, double width)
    {
        var key = (note.NoteKind, note.Id);
        if (_noteLayouts.TryGetValue(key, out var cached) && cached.All(b => b.Paragraph == null || Math.Abs(b.Paragraph.Width - width) < 0.01) &&
            cached.Count > 0 && cached.All(b => b.Paragraph == null || b.Paragraph.Version == b.Paragraph.Paragraph.Version))
            return cached;
        var (blocks, _) = _blocks.Stack(note.Blocks, width);
        _noteLayouts[key] = blocks;
        return blocks;
    }

    private double SeparatorHeight(bool continuation, double width)
    {
        var (blocks, height) = Separator(continuation, width);
        return blocks.Count == 0 ? 12 : height;
    }

    private (List<BlockLayout> Blocks, double Height) Separator(bool continuation, double width)
    {
        var key = (continuation ? "c" : "s") + width.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        if (_separators.TryGetValue(key, out var cached)) return cached;
        var note = _doc.Footnotes.Values.FirstOrDefault(n => n.Type == (continuation ? "continuationSeparator" : "separator"));
        var result = note != null ? _blocks.Stack(note.Blocks, width) : (new List<BlockLayout>(), 12.0);
        // A little air between the rule and the first note (note numbers are raised).
        result = (result.Item1, result.Item2 + 4);
        _separators[key] = result;
        return result;
    }

    /// <summary>As much of a note as fits in <paramref name="available"/> height, from a block / source position.</summary>
    private static NoteChunk SliceNote(Note note, List<BlockLayout> blocks, int fromBlock, int fromPos, double available)
    {
        var chunk = new NoteChunk { Note = note, Blocks = blocks, FromBlock = fromBlock, FromPos = fromPos, ToBlock = fromBlock, ToPos = fromPos };
        var used = 0.0;
        for (var b = fromBlock; b < blocks.Count; b++)
        {
            var bl = blocks[b];
            if (bl.Paragraph is { } p)
            {
                if (b > fromBlock || fromPos == 0) used += b > fromBlock ? BlockLayouter.SpaceBefore(p, blocks[b - 1].Paragraph) : 0;
                foreach (var line in p.Lines)
                {
                    if (b == fromBlock && line.SrcStart < fromPos) continue;
                    if (used + line.Height > available + 0.01)
                    {
                        chunk.ToBlock = b;
                        chunk.ToPos = line.SrcStart;
                        chunk.Height = used;
                        return chunk;
                    }
                    used += line.Height;
                }
                used += BlockLayouter.SpaceAfter(p, b + 1 < blocks.Count ? blocks[b + 1].Paragraph : null);
            }
            else
            {
                var h = bl.Table?.Height ?? bl.BoxHeight;
                if (used + h > available + 0.01 && b > fromBlock)
                {
                    chunk.ToBlock = b;
                    chunk.ToPos = 0;
                    chunk.Height = used;
                    return chunk;
                }
                used += h;
            }
        }
        chunk.ToBlock = blocks.Count;
        chunk.ToPos = 0;
        chunk.Height = used;
        return chunk;
    }

    /// <summary>Places a column's notes at its bottom: the separator, then each note (or part of one).</summary>
    private void FinishNotes(PageLayout page, ColumnArea area, List<NoteChunk> notes, double notesHeight)
    {
        if (notes.Count == 0) return;
        var col = area.Box;
        var y = col.Bottom - notesHeight;
        area.NotesTop = y;
        var continued = notes[0].Continued;
        area.NotesContinued = continued;
        var (sepBlocks, sepHeight) = Separator(continued, col.Width);
        if (sepBlocks.Count > 0) BlockLayouter.PlaceStack(sepBlocks, col.Left, y, area.Notes);
        y += sepBlocks.Count > 0 ? sepHeight : 12;
        foreach (var chunk in notes)
        {
            for (var b = chunk.FromBlock; b < chunk.Blocks.Count && b <= chunk.ToBlock; b++)
            {
                if (b == chunk.ToBlock && chunk.ToPos == 0) break;
                var bl = chunk.Blocks[b];
                if (bl.Paragraph is { } p)
                {
                    var from = b == chunk.FromBlock ? chunk.FromPos : 0;
                    var to = b == chunk.ToBlock ? chunk.ToPos : int.MaxValue;
                    if (from == 0 && b > chunk.FromBlock) y += BlockLayouter.SpaceBefore(p, chunk.Blocks[b - 1].Paragraph);
                    var f = new ParaFragment(p) { X = col.Left, Y = y, Width = col.Width, IsStart = from == 0, BoxTop = y };
                    foreach (var line in p.Lines)
                    {
                        if (line.SrcStart < from || line.SrcStart >= to) continue;
                        var copy = line.Clone();
                        copy.Top = y;
                        f.Lines.Add(copy);
                        y += line.Height;
                    }
                    f.IsEnd = to == int.MaxValue;
                    f.BoxBottom = y;
                    f.Height = y - f.Y;
                    if (f.Lines.Count > 0) area.Notes.Add(f);
                    if (f.IsEnd) y += BlockLayouter.SpaceAfter(p, b + 1 < chunk.Blocks.Count ? chunk.Blocks[b + 1].Paragraph : null);
                }
                else if (bl.Table is { } t)
                {
                    area.Notes.Add(BlockLayouter.PlaceTableWhole(t, col.Left, y));
                    y += t.Height;
                }
            }
        }
    }

    // ------------------------------------------------------------------ tables

    private ColumnEnd PlaceTable(PageLayout page, ColumnArea area, TableLayout t, ref FlowCursor cursor, ref double y, double bottom, bool atTop)
    {
        var col = area.Box;
        var tf = new TableFragment(t) { X = col.Left + t.X, Y = y, Width = t.Width };
        var startRow = cursor.Row;
        var split = cursor.Split;
        var rowTop = y;
        var placedRows = 0;

        // Repeat header rows on a continuation.
        if ((startRow > 0 || split != null) && t.HeaderRows > 0 && startRow >= t.HeaderRows)
        {
            var headerHeight = t.Rows.Take(t.HeaderRows).Sum(r => r.Height);
            if (rowTop + headerHeight + (startRow < t.Rows.Count ? Math.Min(t.Rows[startRow].Height, 24) : 0) <= bottom)
            {
                for (var r = 0; r < t.HeaderRows; r++)
                {
                    var rf = PlaceRowWhole(tf, t, r, rowTop);
                    rf.IsRepeatedHeader = true;
                    rowTop += rf.Height;
                }
            }
        }

        for (var r = startRow; r < t.Rows.Count; r++)
        {
            var row = t.Rows[r];
            var isSplitContinuation = r == startRow && split != null;
            var height = isSplitContinuation ? RemainingRowHeight(t, r, split!) : row.Height;
            // Keep rows of a merged cell together with the first row when the merge is short.
            if (rowTop + height <= bottom + 0.01)
            {
                if (isSplitContinuation) PlaceRowPart(tf, t, r, rowTop, split!, double.MaxValue, out _);
                else PlaceRowWhole(tf, t, r, rowTop);
                rowTop += height;
                placedRows++;
                continue;
            }
            // The row doesn't fit.
            var canSplit = !row.CantSplit && row.ExactHeight <= 0 && !Pageless;
            var nothingHere = placedRows == 0 && area.Content.Count == 0 && atTop;
            if (canSplit && bottom - rowTop > 20)
            {
                var start = split != null && r == startRow ? split : EmptySplit(t, r);
                var rf = PlaceRowPart(tf, t, r, rowTop, start, bottom - rowTop, out var progress);
                if (progress == null)
                {
                    // Everything fitted after all.
                    rowTop += rf.Height;
                    placedRows++;
                    continue;
                }
                var advanced = !progress.Equals(start);
                if (advanced)
                {
                    rf.ContinuesOnNext = true;
                    rf.ContinuesFromPrevious = isSplitContinuation;
                    rowTop += rf.Height;
                    placedRows++;
                    cursor = new FlowCursor(cursor.Index, 0, r, progress);
                    y = Finish();
                    return ColumnEnd.Full;
                }
                tf.Rows.Remove(rf);
            }
            if (nothingHere)
            {
                // Taller than a whole page and can't split: place it anyway (cut off at the bottom).
                PlaceRowWhole(tf, t, r, rowTop);
                rowTop += height;
                placedRows++;
                continue;
            }
            cursor = new FlowCursor(cursor.Index, 0, r, r == startRow ? split : null);
            y = Finish();
            return ColumnEnd.Full;
        }
        cursor = new FlowCursor(cursor.Index + 1, 0);
        y = Finish();
        // Space after a table: Word adds none, but the next paragraph's space before applies.
        return ColumnEnd.Done;

        double Finish()
        {
            if (tf.Rows.Count == 0) return rowTop;
            tf.Height = rowTop - tf.Y;
            area.Content.Add(tf);
            return rowTop;
        }
    }

    private static CellSplit EmptySplit(TableLayout t, int r) => new(t.Rows[r].Cells.Select(_ => (0, 0)).ToArray());

    private static double RemainingRowHeight(TableLayout t, int r, CellSplit split)
    {
        var row = t.Rows[r];
        var h = 0.0;
        for (var c = 0; c < row.Cells.Count; c++)
        {
            var cell = row.Cells[c];
            if (cell.IsMergedContinuation) continue;
            var (block, pos) = c < split.Positions.Length ? split.Positions[c] : (0, 0);
            var content = ContentHeightFrom(cell.Content, block, pos);
            h = Math.Max(h, content + cell.MarginTop + cell.MarginBottom);
        }
        return Math.Max(h, 8);
    }

    private static double ContentHeightFrom(List<BlockLayout> blocks, int fromBlock, int fromPos)
    {
        var h = 0.0;
        for (var b = fromBlock; b < blocks.Count; b++)
        {
            if (blocks[b].Paragraph is { } p)
            {
                foreach (var l in p.Lines)
                    if (b > fromBlock || l.SrcStart >= fromPos) h += l.Height;
                if (b > fromBlock || fromPos == 0) h += BlockLayouter.SpaceBefore(p, b > 0 ? blocks[b - 1].Paragraph : null);
                h += BlockLayouter.SpaceAfter(p, b + 1 < blocks.Count ? blocks[b + 1].Paragraph : null);
            }
            else h += blocks[b].Table?.Height ?? blocks[b].BoxHeight;
        }
        return h;
    }

    private static RowFragment PlaceRowWhole(TableFragment tf, TableLayout t, int r, double top)
    {
        var row = t.Rows[r];
        var rf = new RowFragment { Row = row, Y = top, Height = row.Height };
        tf.Rows.Add(rf);
        foreach (var cell in row.Cells)
        {
            if (cell.IsMergedContinuation) continue;
            var span = Math.Min(cell.RowSpan, t.Rows.Count - r);
            var height = 0.0;
            for (var k = 0; k < span; k++) height += t.Rows[r + k].Height;
            var cf = new CellFragment { Cell = cell, Box = new Rect(tf.X + cell.X, top, cell.Width, height), RowSpan = span };
            var contentTop = top + cell.MarginTop + BlockLayouter.VerticalOffset(cell, height);
            BlockLayouter.PlaceStack(cell.Content, tf.X + cell.X + cell.MarginLeft, contentTop, cf.Content, cell.Context);
            rf.Cells.Add(cf);
        }
        return rf;
    }

    /// <summary>Places part of a row (from <paramref name="from"/>) within <paramref name="limit"/> height; reports how far each cell got.</summary>
    private static RowFragment PlaceRowPart(TableFragment tf, TableLayout t, int r, double top, CellSplit from, double limit, out CellSplit? progress)
    {
        var row = t.Rows[r];
        var rf = new RowFragment { Row = row, Y = top };
        tf.Rows.Add(rf);
        var positions = new (int, int)[row.Cells.Count];
        var height = 0.0;
        for (var c = 0; c < row.Cells.Count; c++)
        {
            var cell = row.Cells[c];
            var start = c < from.Positions.Length ? from.Positions[c] : (0, 0);
            if (cell.IsMergedContinuation)
            {
                positions[c] = start;
                continue;
            }
            var cf = new CellFragment { Cell = cell };
            var x = tf.X + cell.X + cell.MarginLeft;
            var y = top + cell.MarginTop;
            var avail = limit - cell.MarginTop - cell.MarginBottom;
            var (block, pos) = start;
            var used = 0.0;
            var done = true;
            for (var b = block; b < cell.Content.Count; b++)
            {
                var bl = cell.Content[b];
                if (bl.Paragraph is { } p)
                {
                    var from0 = b == block ? pos : 0;
                    var space = from0 == 0 && b > block ? BlockLayouter.SpaceBefore(p, cell.Content[b - 1].Paragraph) : from0 == 0 && b == block && b > 0 ? BlockLayouter.SpaceBefore(p, cell.Content[b - 1].Paragraph) : 0;
                    if (from0 == 0 && b == 0) space = BlockLayouter.SpaceBefore(p, null);
                    var f = new ParaFragment(p) { X = x, Y = y + used + space, Width = p.Width, IsStart = from0 == 0, Table = cell.Context };
                    f.BoxTop = f.Y;
                    var lineTop = f.Y;
                    var stopped = false;
                    foreach (var line in p.Lines)
                    {
                        if (line.SrcStart < from0) continue;
                        if (lineTop + line.Height - y > avail + 0.01 && f.Lines.Count + (b > block ? 1 : 0) > 0)
                        {
                            positions[c] = (b, line.SrcStart);
                            stopped = true;
                            break;
                        }
                        var copy = line.Clone();
                        copy.Top = lineTop;
                        f.Lines.Add(copy);
                        lineTop += line.Height;
                    }
                    if (f.Lines.Count > 0)
                    {
                        f.IsEnd = !stopped;
                        f.BoxBottom = lineTop;
                        f.Height = lineTop - f.Y;
                        cf.Content.Add(f);
                    }
                    used = lineTop - y;
                    if (stopped)
                    {
                        done = false;
                        break;
                    }
                    used += BlockLayouter.SpaceAfter(p, b + 1 < cell.Content.Count ? cell.Content[b + 1].Paragraph : null);
                }
                else
                {
                    var h = bl.Table?.Height ?? bl.BoxHeight;
                    if (used + h > avail + 0.01 && used > 0)
                    {
                        positions[c] = (b, 0);
                        done = false;
                        break;
                    }
                    if (bl.Table is { } nested) cf.Content.Add(BlockLayouter.PlaceTableWhole(nested, x, y + used));
                    used += h;
                }
            }
            if (done) positions[c] = (cell.Content.Count, 0);
            height = Math.Max(height, used + cell.MarginTop + cell.MarginBottom);
            rf.Cells.Add(cf);
        }
        height = Math.Min(height, limit);
        rf.Height = Math.Max(height, 4);
        foreach (var cf in rf.Cells) cf.Box = new Rect(tf.X + cf.Cell.X, top, cf.Cell.Width, rf.Height);
        var all = positions.Select((p, i) => row.Cells[i].IsMergedContinuation || p.Item1 >= row.Cells[i].Content.Count).All(x => x);
        progress = all ? null : new CellSplit(positions);
        return rf;
    }
}
