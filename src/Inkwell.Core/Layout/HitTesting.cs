using System.Windows;
using System.Windows.Media.TextFormatting;
using Inkwell.Core.Editing;
using Inkwell.Core.Model;

namespace Inkwell.Core.Layout;

/// <summary>The caret's place on a page.</summary>
public readonly struct CaretPlace
{
    public CaretPlace(PageLayout page, Rect rect, bool rtl, ParaLocation location, LineInfo line)
    {
        Page = page;
        Rect = rect;
        Rtl = rtl;
        Location = location;
        Line = line;
    }

    public PageLayout Page { get; }

    /// <summary>A thin rectangle in page coordinates (x, top, 0, height).</summary>
    public Rect Rect { get; }

    public bool Rtl { get; }
    public ParaLocation Location { get; }
    public LineInfo Line { get; }
}

/// <summary>What is under a point on a page.</summary>
public sealed class PageHit
{
    public DocPos Position;
    public PageRegion Region;
    public ParaFragment? Fragment;
    public LineInfo? Line;

    /// <summary>A floating drawing under the point.</summary>
    public FloatPlacement? Float;

    /// <summary>An in-line drawing under the point (and its paragraph position).</summary>
    public DrawingObject? InlineDrawing;

    /// <summary>A table cell under the point.</summary>
    public CellFragment? Cell;
    public TableFragment? Table;

    /// <summary>The point is in the left margin beside a line (selects the line).</summary>
    public bool InSelectionMargin;

    /// <summary>The hit landed after the end of the line's text (caret shows at the line end).</summary>
    public bool AtLineEnd;
}

/// <summary>Caret placement, hit testing, selection rectangles and line-wise movement on laid-out pages.</summary>
public static class HitTesting
{
    // ------------------------------------------------------------------ caret

    /// <summary>
    /// The caret for a position. <paramref name="atLineEnd"/> places a position that ends one line and starts the
    /// next at the end of the first (after End). <paramref name="preferPage"/> picks the page for header text.
    /// </summary>
    public static CaretPlace? Caret(this DocumentLayout layout, DocPos pos, bool atLineEnd = false, PageLayout? preferPage = null)
    {
        if (pos.Para == null) return null;
        var locations = layout.Locate(pos.Para);
        if (locations.Count == 0) return null;
        IEnumerable<ParaLocation> candidates = preferPage != null && locations.Any(l => l.Page == preferPage) ? locations.Where(l => l.Page == preferPage) : locations;
        ParaLocation? chosenLoc = null;
        LineInfo? chosenLine = null;
        foreach (var loc in candidates)
        {
            for (var i = 0; i < loc.Fragment.Lines.Count; i++)
            {
                var line = loc.Fragment.Lines[i];
                var inLine = pos.Offset >= line.DocStart && (pos.Offset < line.DocEnd || line.EndsParagraph || (atLineEnd && pos.Offset == line.DocEnd));
                if (!inLine) continue;
                chosenLoc = loc;
                chosenLine = line;
                break;
            }
            if (chosenLine != null) break;
        }
        if (chosenLine == null)
        {
            var last = candidates.Last();
            if (last.Fragment.Lines.Count == 0) return null;
            chosenLoc = last;
            chosenLine = last.Fragment.Lines[last.Fragment.Lines.Count - 1];
        }
        var source = chosenLoc!.Fragment.Layout.Source;
        using var textLine = layout.Lines.Recreate(source, chosenLine);
        double x;
        if (atLineEnd && pos.Offset == chosenLine.DocEnd && !chosenLine.EndsParagraph)
        {
            var lastSrc = chosenLine.SrcStart + chosenLine.SrcLength - 1;
            x = textLine.GetDistanceFromCharacterHit(new CharacterHit(lastSrc, 1));
        }
        else
        {
            var src = source.DocToSrc(pos.Offset);
            src = MathEx.Clamp(src, chosenLine.SrcStart, chosenLine.SrcStart + Math.Max(0, chosenLine.SrcLength - 1));
            x = textLine.GetDistanceFromCharacterHit(new CharacterHit(src, 0));
        }
        var fragment = chosenLoc.Fragment;
        var originX = fragment.X + chosenLine.X;
        var top = chosenLine.Top + chosenLine.TextOffset;
        var height = Math.Max(8, Math.Min(chosenLine.Height, textLine.Height));
        if (top + height > chosenLine.Top + chosenLine.Height) top = chosenLine.Top + chosenLine.Height - height;
        return new CaretPlace(chosenLoc.Page, new Rect(originX + x, top, 0, height), source.IsRtl, chosenLoc, chosenLine);
    }

    // ------------------------------------------------------------------ hit testing

    /// <summary>What is at a point (page coordinates). Points between lines snap to the nearest line.</summary>
    public static PageHit? HitTest(this DocumentLayout layout, PageLayout page, Point point, bool includeHeaderFooter = false)
    {
        var hit = new PageHit();
        // Floating drawings on top (in front of or wrapped by text) come first.
        foreach (var f in page.Floats.AsEnumerable().Reverse())
        {
            if (f.InHeaderFooter && !includeHeaderFooter) continue;
            if (f.Box.Contains(point) && !f.Behind)
            {
                hit.Float = f;
                hit.Position = new DocPos(f.Anchor, f.AnchorOffset);
                return hit;
            }
        }

        if (includeHeaderFooter)
        {
            var inHeader = point.Y < page.BodyRect.Top;
            var inFooter = point.Y > page.BodyRect.Bottom;
            if (inHeader || inFooter)
            {
                var fragments = inHeader ? page.Header : page.Footer;
                var r = HitFragments(layout, fragments, point);
                if (r != null)
                {
                    r.Region = inHeader ? PageRegion.Header : PageRegion.Footer;
                    return r;
                }
                return null;
            }
        }

        // The column under the point (or the nearest).
        if (page.Columns.Count == 0) return null;
        var column = page.Columns.OrderBy(c => c.Box.Left <= point.X && point.X <= c.Box.Right ? 0 : Math.Min(Math.Abs(point.X - c.Box.Left), Math.Abs(point.X - c.Box.Right)) + 1)
            .ThenBy(c => c.Box.Top <= point.Y && point.Y <= c.Box.Bottom ? 0 : 1).First();
        if (column.Notes.Count > 0 && point.Y >= column.NotesTop)
        {
            var noteHit = HitFragments(layout, column.Notes, point);
            if (noteHit != null)
            {
                noteHit.Region = PageRegion.Notes;
                return noteHit;
            }
        }
        var body = HitFragments(layout, column.Content, point);
        if (body == null)
        {
            // An empty column: the nearest content of the page.
            var all = page.Columns.SelectMany(c => c.Content).ToList();
            body = HitFragments(layout, all, point);
        }
        if (body != null)
        {
            body.Region = PageRegion.Body;
            body.InSelectionMargin = point.X < column.Box.Left - 4;
        }
        return body;
    }

    private static PageHit? HitFragments(DocumentLayout layout, List<Fragment> fragments, Point point)
    {
        if (fragments.Count == 0) return null;
        Fragment? best = null;
        var bestDistance = double.MaxValue;
        foreach (var f in fragments)
        {
            double d;
            if (point.Y < f.Y) d = f.Y - point.Y;
            else if (point.Y > f.Y + f.Height) d = point.Y - (f.Y + f.Height);
            else d = 0;
            if (f is BoxFragment) d += 0.5;
            if (d < bestDistance || (d == 0 && bestDistance == 0 && f is TableFragment))
            {
                bestDistance = d;
                best = f;
            }
        }
        switch (best)
        {
            case ParaFragment pf:
                return HitParagraph(layout, pf, point);
            case TableFragment tf:
                return HitTable(layout, tf, point);
            default:
                // A placeholder: the nearest paragraph instead.
                var paras = fragments.OfType<ParaFragment>().ToList();
                if (paras.Count == 0) return null;
                var nearest = paras.OrderBy(p => Math.Abs(p.Y + p.Height / 2 - point.Y)).First();
                return HitParagraph(layout, nearest, point);
        }
    }

    private static PageHit? HitTable(DocumentLayout layout, TableFragment tf, Point point)
    {
        if (tf.Rows.Count == 0) return null;
        var row = tf.Rows.OrderBy(r => point.Y < r.Y ? r.Y - point.Y : point.Y > r.Y + r.Height ? point.Y - r.Y - r.Height : 0).First();
        CellFragment? cell = null;
        var bestD = double.MaxValue;
        foreach (var r in tf.Rows)
        {
            foreach (var c in r.Cells)
            {
                var box = c.Box;
                var dx = point.X < box.Left ? box.Left - point.X : point.X > box.Right ? point.X - box.Right : 0;
                var dy = point.Y < box.Top ? box.Top - point.Y : point.Y > box.Bottom ? point.Y - box.Bottom : 0;
                var d = dx * 3 + dy;
                if (d < bestD)
                {
                    bestD = d;
                    cell = c;
                }
            }
        }
        _ = row;
        if (cell == null) return null;
        var inner = HitFragments(layout, cell.Content, point);
        if (inner == null)
        {
            var p = Nav.FirstIn(cell.Cell.Cell.Blocks);
            if (p == null) return null;
            inner = new PageHit { Position = new DocPos(p, 0) };
        }
        inner.Cell ??= cell;
        inner.Table ??= tf;
        return inner;
    }

    private static PageHit HitParagraph(DocumentLayout layout, ParaFragment f, Point point)
    {
        var hit = new PageHit { Fragment = f };
        if (f.Lines.Count == 0)
        {
            hit.Position = new DocPos(f.Paragraph, 0);
            return hit;
        }
        var line = f.Lines[0];
        foreach (var l in f.Lines)
        {
            line = l;
            if (point.Y < l.Top + l.Height) break;
        }
        hit.Line = line;
        var source = f.Layout.Source;
        using var textLine = layout.Lines.Recreate(source, line);
        var x = point.X - (f.X + line.X);
        var ch = textLine.GetCharacterHitFromDistance(x);
        var doc = source.SrcToDoc(ch.FirstCharacterIndex, ch.TrailingLength);
        // Clicking past the end of a wrapped line puts the caret at that line's end, not the next line's start.
        if (doc >= line.DocEnd && !line.EndsParagraph)
        {
            doc = line.DocEnd;
            hit.AtLineEnd = true;
        }
        doc = MathEx.Clamp(doc, line.DocStart, line.EndsParagraph ? source.DocLength : line.DocEnd);
        hit.Position = new DocPos(f.Paragraph, doc);

        // An in-line picture under the point.
        foreach (var seg in source.Segments)
        {
            if (seg.Kind != SegKind.Object || seg.Inline is not DrawingObject d) continue;
            if (seg.SrcStart < line.SrcStart || seg.SrcStart >= line.SrcStart + line.SrcLength) continue;
            foreach (var b in textLine.GetTextBounds(seg.SrcStart, 1))
            {
                var r = b.Rectangle;
                r.Offset(f.X + line.X, line.Top + line.TextOffset);
                if (r.Contains(point))
                {
                    hit.InlineDrawing = d;
                    hit.Position = new DocPos(f.Paragraph, seg.DocStart);
                }
            }
        }
        return hit;
    }

    // ------------------------------------------------------------------ selection

    /// <summary>Highlight rectangles of a selection on one page (page coordinates).</summary>
    public static List<Rect> SelectionRects(this DocumentLayout layout, Selection selection, PageLayout page)
    {
        var rects = new List<Rect>();
        if (selection.IsEmpty || selection.Start.Para == null) return rects;
        var start = selection.Start;
        var end = selection.End;
        void Visit(Fragment f)
        {
            switch (f)
            {
                case ParaFragment pf:
                    AddParagraph(layout, pf, start, end, rects);
                    break;
                case TableFragment tf:
                    foreach (var row in tf.Rows)
                        foreach (var cell in row.Cells)
                            foreach (var inner in cell.Content)
                                Visit(inner);
                    break;
            }
        }
        var inBody = start.Para.Story is MainStory;
        if (inBody)
        {
            foreach (var col in page.Columns)
                foreach (var f in col.Content)
                    Visit(f);
        }
        else
        {
            foreach (var col in page.Columns)
                foreach (var f in col.Notes)
                    Visit(f);
            foreach (var f in page.Header) Visit(f);
            foreach (var f in page.Footer) Visit(f);
        }
        return rects;
    }

    private static void AddParagraph(DocumentLayout layout, ParaFragment f, DocPos start, DocPos end, List<Rect> rects)
    {
        var p = f.Paragraph;
        // Is this paragraph inside the selection?
        int s, e;
        bool includesMark;
        if (ReferenceEquals(p, start.Para) && ReferenceEquals(p, end.Para))
        {
            s = start.Offset;
            e = end.Offset;
            includesMark = false;
        }
        else if (ReferenceEquals(p, start.Para))
        {
            s = start.Offset;
            e = p.Length;
            includesMark = true;
        }
        else if (ReferenceEquals(p, end.Para))
        {
            s = 0;
            e = end.Offset;
            includesMark = false;
        }
        else
        {
            if (p.Story != start.Para.Story) return;
            if (Nav.Compare(new DocPos(p, 0), start) < 0 || Nav.Compare(new DocPos(p, 0), end) > 0) return;
            s = 0;
            e = p.Length;
            includesMark = true;
        }
        var source = f.Layout.Source;
        foreach (var line in f.Lines)
        {
            var ls = Math.Max(s, line.DocStart);
            var le = Math.Min(e, line.EndsParagraph ? source.DocLength : line.DocEnd);
            var markHere = includesMark && line.EndsParagraph;
            if (le <= ls && !markHere) continue;
            using var textLine = layout.Lines.Recreate(source, line);
            var originX = f.X + line.X;
            if (le > ls)
            {
                var (srcStart, srcEnd) = source.DocRangeToSrc(ls, le);
                foreach (var b in textLine.GetTextBounds(srcStart, Math.Max(1, srcEnd - srcStart)))
                {
                    var r = b.Rectangle;
                    rects.Add(new Rect(originX + r.X, line.Top, r.Width, line.Height));
                }
            }
            if (markHere)
            {
                var x = textLine.GetDistanceFromCharacterHit(new CharacterHit(line.SrcStart + line.SrcLength - 1, 0));
                var w = 6;
                rects.Add(new Rect(source.IsRtl ? originX + x - w : originX + x, line.Top, w, line.Height));
            }
        }
    }

    // ------------------------------------------------------------------ moving by lines

    /// <summary>The position one line up or down from the caret, keeping <paramref name="desiredX"/> (page coordinates).</summary>
    public static DocPos? LineUpDown(this DocumentLayout layout, CaretPlace caret, double desiredX, int direction, out bool atLineEnd)
    {
        atLineEnd = false;
        var page = caret.Page;
        var line = caret.Line;
        var region = caret.Location.Region;
        var probeY = direction < 0 ? line.Top - 2 : line.Top + line.Height + 2;
        var pageIndex = layout.Pages.IndexOf(page);
        for (var attempt = 0; attempt < 4 && pageIndex >= 0 && pageIndex < layout.Pages.Count; attempt++)
        {
            var current = layout.Pages[pageIndex];
            var top = region == PageRegion.Header ? current.HeaderRect.Top : current.BodyRect.Top;
            var bottom = region == PageRegion.Footer ? current.FooterRect.Bottom : region == PageRegion.Header ? current.HeaderRect.Bottom : current.BodyRect.Bottom;
            if (probeY >= top - 1 && probeY <= bottom + 1)
            {
                var hit = layout.HitTest(current, new Point(desiredX, probeY), region is PageRegion.Header or PageRegion.Footer);
                if (hit != null && hit.Float == null && hit.Position.Para != null && hit.Position.Para.Story == caret.Location.Fragment.Paragraph.Story)
                {
                    atLineEnd = hit.AtLineEnd;
                    // Moving inside the same line (a tall line, a picture) means we're at the edge: go further.
                    if (hit.Line == null || !ReferenceEquals(hit.Fragment?.Paragraph, caret.Location.Fragment.Paragraph) || hit.Line.Top != line.Top || hit.Line.SrcStart != line.SrcStart)
                        return hit.Position;
                    probeY += direction * 6;
                    continue;
                }
            }
            if (region is PageRegion.Header or PageRegion.Footer) return null;
            // Off this page: continue on the next / previous one.
            pageIndex += direction;
            if (pageIndex < 0 || pageIndex >= layout.Pages.Count) return null;
            var next = layout.Pages[pageIndex];
            probeY = direction < 0 ? LastContentBottom(next) - 2 : FirstContentTop(next) + 2;
        }
        return null;
    }

    private static double FirstContentTop(PageLayout page) =>
        page.Columns.SelectMany(c => c.Content).Select(f => f is ParaFragment pf && pf.Lines.Count > 0 ? pf.Lines[0].Top : f.Y).DefaultIfEmpty(page.BodyRect.Top).Min();

    private static double LastContentBottom(PageLayout page) =>
        page.Columns.SelectMany(c => c.Content).Select(f => f.Y + f.Height).DefaultIfEmpty(page.BodyRect.Bottom).Max();

    /// <summary>Start and end positions of the visual line holding the caret (Home / End).</summary>
    public static (int Start, int End) LineBounds(CaretPlace caret)
    {
        var line = caret.Line;
        var source = caret.Location.Fragment.Layout.Source;
        return (line.DocStart, line.EndsParagraph ? source.DocLength : line.DocEnd);
    }
}
