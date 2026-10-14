using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using Inkwell.Core.Editing;
using Inkwell.Core.Layout;
using Inkwell.Core.Model;

namespace Inkwell.Views.Editing;

/// <summary>A highlighted range (search results, spelling, comments).</summary>
public readonly struct HighlightRange
{
    public HighlightRange(DocPos start, DocPos end, Color color)
    {
        Start = start;
        End = end;
        Color = color;
    }

    public DocPos Start { get; }
    public DocPos End { get; }
    public Color Color { get; }
}

public sealed partial class DocumentView
{
    private CaretPlace? _caretPlace;
    private bool _caretAtLineEnd;
    private double? _desiredX;

    /// <summary>Header / footer editing: the body is dimmed and the caret is in a header or footer.</summary>
    public bool EditingHeaderFooter { get; private set; }

    /// <summary>The page the header / footer being edited is shown on.</summary>
    public PageLayout? HeaderFooterPage { get; private set; }

    /// <summary>The selected picture (handles shown), or null.</summary>
    public DrawingObject? SelectedDrawing { get; private set; }

    /// <summary>Search results and other highlights.</summary>
    public List<HighlightRange> Highlights { get; } = new();

    /// <summary>Spelling errors per paragraph: (start, length) ranges; null when spell checking is off.</summary>
    public Func<Paragraph, IReadOnlyList<(int Start, int Length)>>? Squiggles { get; set; }

    /// <summary>Alignment guides shown while dragging a picture (page coordinates).</summary>
    public List<(PageLayout Page, Point From, Point To)> Guides { get; } = new();

    public event Action? DrawingSelectionChanged;

    public CaretPlace? CaretPlace
    {
        get
        {
            if (_caretPlace != null) return _caretPlace;
            if (Editor == null || Layout == null) return null;
            var pos = Editor.Selection.Active;
            if (pos.Para == null) return null;
            _caretPlace = Layout.Caret(pos, _caretAtLineEnd, EditingHeaderFooter ? HeaderFooterPage : null);
            return _caretPlace;
        }
    }

    /// <summary>The caret's rectangle in view coordinates (IME window, popups).</summary>
    public Rect? CaretViewRect
    {
        get
        {
            var caret = CaretPlace;
            if (caret == null) return null;
            var top = PageToView(caret.Value.Page, caret.Value.Rect.TopLeft);
            return new Rect(top.X, top.Y, 1, caret.Value.Rect.Height * _zoom);
        }
    }

    public void SelectDrawing(DrawingObject? drawing)
    {
        if (ReferenceEquals(SelectedDrawing, drawing)) return;
        SelectedDrawing = drawing;
        DrawOverlay();
        DrawCaret();
        DrawingSelectionChanged?.Invoke();
    }

    private void DrawCaret()
    {
        using var dc = _caretVisual.RenderOpen();
        if (SelectedDrawing != null || Editor == null || !Editor.Selection.IsEmpty) return;
        var caret = CaretPlace;
        if (caret == null) return;
        var r = PageRect(caret.Value.Page);
        var c = caret.Value.Rect;
        var width = Math.Max(1.25, 1.5 / _zoom);
        var color = Layout!.View.DarkPaper ? Colors.White : Colors.Black;
        dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(r.X + c.X - width / 2, r.Y + c.Y, width, c.Height));
        // A small flag shows the typing direction in right-to-left text.
        if (caret.Value.Rtl)
        {
            var flag = new StreamGeometry();
            using (var g = flag.Open())
            {
                var x = r.X + c.X;
                g.BeginFigure(new Point(x, r.Y + c.Y), true, true);
                g.LineTo(new Point(x - 4 / _zoom * 1.2, r.Y + c.Y), true, false);
                g.LineTo(new Point(x, r.Y + c.Y + 3.5 / _zoom * 1.2), true, false);
            }
            dc.DrawGeometry(new SolidColorBrush(color), null, flag);
        }
    }

    /// <summary>Selection, highlights, spelling squiggles, focus-mode dimming and picture handles.</summary>
    private void DrawOverlay()
    {
        using var dc = _overlay.RenderOpen();
        var layout = Layout;
        var editor = Editor;
        if (layout == null || editor == null) return;
        var visibleTop = _offset.Y / _zoom - 100;
        var visibleBottom = (_offset.Y + _viewport.Height) / _zoom + 100;
        var selection = editor.Selection;
        var selBrush = new SolidColorBrush(IsKeyboardFocusWithin ? SelectionColor : Color.FromArgb((byte)(SelectionColor.A * 0.6), SelectionColor.R, SelectionColor.G, SelectionColor.B));
        selBrush.Freeze();
        for (var i = 0; i < layout.Pages.Count && i < _pageRects.Count; i++)
        {
            var rect = _pageRects[i];
            if (rect.Bottom < visibleTop || rect.Top > visibleBottom) continue;
            var page = layout.Pages[i];
            dc.PushTransform(new TranslateTransform(rect.X, rect.Y));
            if (FocusMode && !EditingHeaderFooter) DrawFocusDim(dc, page, selection);
            foreach (var h in Highlights)
            {
                if (h.Start.Para == null) continue;
                var brush = new SolidColorBrush(h.Color);
                foreach (var r in layout.SelectionRects(new Selection(h.Start, h.End), page)) dc.DrawRectangle(brush, null, r);
            }
            if (Squiggles != null) DrawSquiggles(dc, page);
            if (!selection.IsEmpty && SelectedDrawing == null)
                foreach (var r in layout.SelectionRects(selection, page)) dc.DrawRectangle(selBrush, null, r);
            if (SelectedDrawing != null) DrawDrawingHandles(dc, page);
            foreach (var (gp, from, to) in Guides)
                if (gp == page) dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(0xF0, 0x28, 0x7A)), 1 / _zoom), from, to);
            dc.Pop();
        }
    }

    private void DrawFocusDim(DrawingContext dc, PageLayout page, Selection selection)
    {
        var current = selection.Active.Para;
        var paper = Layout!.View.DarkPaper ? Color.FromArgb(0xB4, 0x1E, 0x1F, 0x22) : Color.FromArgb(0xB4, 0xFF, 0xFF, 0xFF);
        var wash = new SolidColorBrush(paper);
        wash.Freeze();
        foreach (var col in page.Columns)
        {
            foreach (var f in col.Content.Concat(col.Notes))
            {
                if (f is ParaFragment pf && ReferenceEquals(pf.Paragraph, current)) continue;
                if (f is TableFragment tf && current != null && Nav.CellOf(current) is { } cell && tf.Layout.Table.Rows.Any(r => r.Cells.Contains(cell))) continue;
                dc.DrawRectangle(wash, null, new Rect(f.X - 4, f.Y - 2, f.Width + 8, f.Height + 4));
            }
        }
        foreach (var f in page.Header.Concat(page.Footer)) dc.DrawRectangle(wash, null, new Rect(f.X - 4, f.Y - 2, f.Width + 8, f.Height + 4));
    }

    private void DrawSquiggles(DrawingContext dc, PageLayout page)
    {
        var layout = Layout!;
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xE0, 0x30, 0x30)), Math.Max(0.8, 1.1 / _zoom));
        pen.Freeze();
        void Visit(Fragment f)
        {
            switch (f)
            {
                case ParaFragment pf:
                    var errors = Squiggles!(pf.Paragraph);
                    if (errors.Count == 0) return;
                    var source = pf.Layout.Source;
                    foreach (var line in pf.Lines)
                    {
                        List<(int, int)>? hits = null;
                        foreach (var (start, length) in errors)
                            if (start < line.DocEnd + (line.EndsParagraph ? 1 : 0) && start + length > line.DocStart) (hits ??= new List<(int, int)>()).Add((start, length));
                        if (hits == null) continue;
                        using var textLine = layout.Lines.Recreate(source, line);
                        foreach (var (start, length) in hits)
                        {
                            var (s, e) = source.DocRangeToSrc(Math.Max(start, line.DocStart), Math.Min(start + length, line.EndsParagraph ? source.DocLength : line.DocEnd));
                            if (e <= s) continue;
                            foreach (var b in textLine.GetTextBounds(s, e - s))
                            {
                                var r = b.Rectangle;
                                var y = line.Top + line.Baseline + 2.2;
                                DrawWave(dc, pen, pf.X + line.X + r.Left, pf.X + line.X + r.Right, y);
                            }
                        }
                    }
                    break;
                case TableFragment tf:
                    foreach (var row in tf.Rows)
                        foreach (var cell in row.Cells)
                            foreach (var inner in cell.Content)
                                Visit(inner);
                    break;
            }
        }
        foreach (var col in page.Columns)
            foreach (var f in col.Content)
                Visit(f);
    }

    private static void DrawWave(DrawingContext dc, Pen pen, double x1, double x2, double y)
    {
        if (x2 - x1 < 2) return;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(x1, y), false, false);
            var up = true;
            for (var x = x1 + 2; x <= x2; x += 2)
            {
                ctx.LineTo(new Point(x, up ? y - 1.4 : y), true, true);
                up = !up;
            }
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }

    /// <summary>The selected picture's box on a page (floating or in line), or null.</summary>
    public Rect? DrawingBox(PageLayout page, DrawingObject drawing)
    {
        var f = page.Floats.FirstOrDefault(x => ReferenceEquals(x.Drawing, drawing));
        if (f != null) return f.Box;
        // In line: find the object in the page's lines.
        foreach (var col in page.Columns)
        {
            foreach (var frag in AllParaFragments(col.Content))
            {
                foreach (var seg in frag.Layout.Source.Segments)
                {
                    if (seg.Kind != SegKind.Object || !ReferenceEquals(seg.Inline, drawing)) continue;
                    foreach (var line in frag.Lines)
                    {
                        if (seg.SrcStart < line.SrcStart || seg.SrcStart >= line.SrcStart + line.SrcLength) continue;
                        using var textLine = Layout!.Lines.Recreate(frag.Layout.Source, line);
                        var bounds = textLine.GetTextBounds(seg.SrcStart, 1);
                        if (bounds.Count == 0) continue;
                        var r = bounds[0].Rectangle;
                        var w = Core.Layout.Units.Emu(drawing.Width);
                        var h = Core.Layout.Units.Emu(drawing.Height);
                        return new Rect(frag.X + line.X + r.X, line.Top + line.TextOffset + line.Baseline - h + (line.Height - line.TextOffset - line.Baseline) * 0, w, h);
                    }
                }
            }
        }
        return null;
    }

    private static IEnumerable<ParaFragment> AllParaFragments(IEnumerable<Fragment> fragments)
    {
        foreach (var f in fragments)
        {
            if (f is ParaFragment pf) yield return pf;
            else if (f is TableFragment tf)
                foreach (var row in tf.Rows)
                    foreach (var cell in row.Cells)
                        foreach (var inner in AllParaFragments(cell.Content))
                            yield return inner;
        }
    }

    private void DrawDrawingHandles(DrawingContext dc, PageLayout page)
    {
        var drawing = SelectedDrawing!;
        var box = DrawingBox(page, drawing);
        if (box == null) return;
        var b = box.Value;
        var accent = new SolidColorBrush(AccentColor);
        var pen = new Pen(accent, 1.25 / _zoom);
        dc.DrawRectangle(null, pen, b);
        var locked = drawing.Float?.Locked == true;
        var size = 8 / _zoom;
        if (!locked)
        {
            foreach (var p in HandlePoints(b))
                dc.DrawEllipse(Brushes.White, pen, p, size / 2, size / 2);
        }
        // The anchor: shows which paragraph a floating picture belongs to.
        var placement = page.Floats.FirstOrDefault(x => ReferenceEquals(x.Drawing, drawing));
        if (placement != null)
        {
            var caret = Layout!.Caret(new DocPos(placement.Anchor, placement.AnchorOffset), false, page);
            if (caret != null && caret.Value.Page == page)
            {
                var a = new Point(Math.Max(4, page.Margins.Left - 22), caret.Value.Rect.Top + 2);
                DrawAnchorGlyph(dc, a, accent);
                dc.DrawLine(new Pen(accent, 0.75 / _zoom) { DashStyle = DashStyles.Dot }, new Point(a.X + 14, a.Y + 7), new Point(b.Left, b.Top));
            }
            if (locked) DrawLockGlyph(dc, new Point(b.Right - 18 / _zoom, b.Top + 4 / _zoom), accent);
        }
    }

    private void DrawAnchorGlyph(DrawingContext dc, Point at, Brush brush)
    {
        var scale = 1 / _zoom;
        var geometry = Geometry.Parse("M7,0 A2.2,2.2 0 1 1 6.99,0 Z M7,4.4 L7,13 M3,10 C3,12.5 5,13.5 7,13.5 C9,13.5 11,12.5 11,10 M4.5,6.5 L9.5,6.5");
        dc.PushTransform(new TranslateTransform(at.X, at.Y));
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.DrawGeometry(null, new Pen(brush, 1.4), geometry);
        dc.Pop();
        dc.Pop();
    }

    private void DrawLockGlyph(DrawingContext dc, Point at, Brush brush)
    {
        var scale = 1 / _zoom;
        dc.PushTransform(new TranslateTransform(at.X, at.Y));
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.DrawRoundedRectangle(Brushes.White, new Pen(brush, 1.2), new Rect(0, 0, 16, 16), 4, 4);
        dc.DrawGeometry(null, new Pen(brush, 1.3), Geometry.Parse("M5,8 L5,6 C5,3.5 11,3.5 11,6 L11,8 M4,8 L12,8 L12,13 L4,13 Z"));
        dc.Pop();
        dc.Pop();
    }

    public static IEnumerable<Point> HandlePoints(Rect b)
    {
        yield return b.TopLeft;
        yield return new Point(b.Left + b.Width / 2, b.Top);
        yield return b.TopRight;
        yield return new Point(b.Right, b.Top + b.Height / 2);
        yield return b.BottomRight;
        yield return new Point(b.Left + b.Width / 2, b.Bottom);
        yield return b.BottomLeft;
        yield return new Point(b.Left, b.Top + b.Height / 2);
    }

    /// <summary>Header / footer editing chrome drawn with the page: the body washed out, labelled header and footer areas.</summary>
    private void DrawHeaderFooterChrome(DrawingContext dc, PageLayout page)
    {
        if (!EditingHeaderFooter || IsPageless) return;
        var dark = Layout!.View.DarkPaper;
        var wash = new SolidColorBrush(dark ? Color.FromArgb(0x99, 0x1E, 0x1F, 0x22) : Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
        dc.DrawRectangle(wash, null, new Rect(0, page.BodyRect.Top - 2, page.Size.Width, page.BodyRect.Height + 4));
        var accent = new SolidColorBrush(AccentColor);
        var pen = new Pen(accent, 0.8) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
        var headerBottom = Math.Max(page.HeaderRect.Bottom, page.Margins.Top) + 2;
        var footerTop = Math.Min(page.FooterStory != null ? page.FooterRect.Top : page.Size.Height - page.Margins.Bottom, page.Size.Height - page.Margins.Bottom) - 2;
        dc.DrawLine(pen, new Point(0, headerBottom), new Point(page.Size.Width, headerBottom));
        dc.DrawLine(pen, new Point(0, footerTop), new Point(page.Size.Width, footerTop));
        var kind = page.FirstOfSection && page.Section.TitlePage ? "First page " : Layout.Document.Settings.EvenAndOddHeaders ? page.Number % 2 == 0 ? "Even page " : "Odd page " : "";
        DrawTab(dc, kind + "header" + (Layout.Document.Sections().Count > 1 ? " · Section " + (page.SectionIndex + 1) : ""), new Point(page.Margins.Left, headerBottom), accent, above: false);
        DrawTab(dc, kind + "footer" + (Layout.Document.Sections().Count > 1 ? " · Section " + (page.SectionIndex + 1) : ""), new Point(page.Margins.Left, footerTop), accent, above: true);
    }

    private static void DrawTab(DrawingContext dc, string text, Point at, Brush accent, bool above)
    {
        var ft = new FormattedText(char.ToUpperInvariant(text[0]) + text.Substring(1), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 10, Brushes.White, 1.0);
        var r = new Rect(at.X, above ? at.Y - ft.Height - 4 : at.Y, ft.Width + 12, ft.Height + 4);
        dc.DrawRoundedRectangle(accent, null, r, 3, 3);
        dc.DrawText(ft, new Point(r.X + 6, r.Y + 2));
    }

    // ------------------------------------------------------------------ header / footer editing

    /// <summary>Starts editing the header or footer of a page (creating it when it doesn't exist yet).</summary>
    public void EnterHeaderFooter(PageLayout page, bool header, Paragraph target)
    {
        EditingHeaderFooter = true;
        HeaderFooterPage = page;
        _caretPlace = null;
        Editor!.Selection = new Selection(new DocPos(target, target.Length));
        Redraw();
        _ = header;
    }

    public void ExitHeaderFooter()
    {
        if (!EditingHeaderFooter) return;
        EditingHeaderFooter = false;
        var page = HeaderFooterPage;
        HeaderFooterPage = null;
        _caretPlace = null;
        // Back to the body near where the header was.
        if (Layout != null && page != null)
        {
            var first = page.Columns.SelectMany(c => c.Content).OfType<ParaFragment>().FirstOrDefault();
            if (first != null) Editor!.Selection = new Selection(new DocPos(first.Paragraph, first.Lines.FirstOrDefault()?.DocStart ?? 0));
        }
        Redraw();
    }
}
