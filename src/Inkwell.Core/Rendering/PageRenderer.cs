using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using TextRun = Inkwell.Core.Model.TextRun;
using Inkwell.Core.Layout;
using Inkwell.Core.Model;
using Inkwell.Core.Styles;

namespace Inkwell.Core.Rendering;

/// <summary>What to draw besides the document itself.</summary>
public sealed class RenderExtras
{
    /// <summary>Formatting marks (¶, ·, →) and break markers.</summary>
    public bool FormattingMarks { get; set; }

    /// <summary>Don't draw the paper (PDF and printing draw on their own page).</summary>
    public bool NoPaper { get; set; }

    /// <summary>Called for every text line drawn (PDF links and text, hit testing aids).</summary>
    public Action<ParaFragment, LineInfo, TextLine, Point>? LineDrawn { get; set; }
}

/// <summary>
/// Draws laid-out pages (the editor, thumbnails, PDF, printing): paper, header and footer, columns with text,
/// tables, footnotes, floating pictures, change bars, comment highlights and, on request, formatting marks.
/// </summary>
public sealed class PageRenderer
{
    private readonly DocumentLayout _layout;

    public PageRenderer(DocumentLayout layout)
    {
        _layout = layout;
    }

    private ViewOptions View => _layout.View;
    private LayoutEnv Env => _layout.Env;

    private static readonly Typeface MarkFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public Color MarkColor => View.DarkPaper ? Color.FromRgb(0x6A, 0x9F, 0xE0) : Color.FromRgb(0x4A, 0x7C, 0xC4);

    /// <summary>Draws a whole page at the origin (page coordinates in DIPs).</summary>
    public void DrawPage(DrawingContext dc, PageLayout page, RenderExtras? extras = null)
    {
        extras ??= new RenderExtras();
        var paper = PaperColors.Paper(View.DarkPaper);
        if (!extras.NoPaper) dc.DrawRectangle(Paint.Brush(paper), null, new Rect(page.Size));
        if (page.IsBlank) return;

        foreach (var f in page.Floats.Where(f => f.Behind)) DrawFloat(dc, f);

        // Header and footer.
        var headerEnv = Env.With(_layout.Paginator.FieldsFor(page));
        foreach (var f in page.Header) DrawFragment(dc, f, extras, headerEnv);
        foreach (var f in page.Footer) DrawFragment(dc, f, extras, headerEnv);

        foreach (var col in page.Columns)
        {
            foreach (var f in col.Content) DrawFragment(dc, f, extras);
            foreach (var f in col.Notes) DrawFragment(dc, f, extras);
        }
        foreach (var (from, to) in page.ColumnSeparators)
            dc.DrawLine(Paint.Pen(View.DarkPaper ? Color.FromRgb(0x55, 0x55, 0x5A) : Color.FromRgb(0x99, 0x99, 0x99), 0.75), from, to);

        foreach (var f in page.Floats.Where(f => !f.Behind)) DrawFloat(dc, f);

        if (View.Revisions is RevisionView.AllMarkup or RevisionView.Simple) DrawChangeBars(dc, page);

        if (extras.FormattingMarks && page.EndMarker != null && !View.Mode.Equals(ViewMode.Pageless))
        {
            var last = page.Columns.SelectMany(c => c.Content).OfType<ParaFragment>().LastOrDefault();
            if (last != null && last.Lines.Count > 0)
            {
                var line = last.Lines[last.Lines.Count - 1];
                DrawBreakMarker(dc, last.X, last.X + last.Width, line.Top + line.Height + 2, page.EndMarker);
            }
        }
    }

    private void DrawFloat(DrawingContext dc, FloatPlacement f) => DrawingRun.DrawDrawing(dc, f.Box, f.Drawing, Env);

    /// <summary>Draws one fragment (paragraph lines, a table, a placeholder box).</summary>
    public void DrawFragment(DrawingContext dc, Fragment f, RenderExtras? extras, LayoutEnv? env = null)
    {
        switch (f)
        {
            case ParaFragment pf:
                DrawParagraph(dc, pf, extras, env);
                break;
            case TableFragment tf:
                DrawTable(dc, tf, extras, env);
                break;
            case BoxFragment box:
                DrawingRun.Placeholder(dc, box.Bounds, box.Label, View.DarkPaper);
                break;
        }
    }

    // ------------------------------------------------------------------ paragraphs

    private void DrawParagraph(DrawingContext dc, ParaFragment f, RenderExtras? extras, LayoutEnv? env)
    {
        var layout = f.Layout;
        var props = layout.Props;
        var box = ParagraphBox(f);

        if (props.Shading is { } shading)
        {
            var fill = View.DarkPaper ? RunPropsFactory.BackgroundForDarkPaper(shading) : shading;
            dc.DrawRectangle(Paint.Brush(fill), null, box);
        }
        DrawParagraphBorders(dc, f, box);

        // Header / footer paragraphs vary per page: rebuild their source with this page's field values.
        var source = layout.Source;
        if (env?.Fields != null && layout.Paragraph.Inlines.Any(i => i is FieldInline))
            source = ParaSource.Build(layout.Paragraph, env, f.Table);

        var formatter = _layout.Lines;
        foreach (var line in f.Lines)
        {
            using var textLine = formatter.Recreate(source, line);
            var origin = new Point(f.X + line.X, line.Top + line.TextOffset);
            DrawFieldShading(dc, f, line, textLine, origin);
            textLine.Draw(dc, origin, InvertAxes.None);
            extras?.LineDrawn?.Invoke(f, line, textLine, origin);
            if (extras?.FormattingMarks == true) DrawMarks(dc, f, line, textLine, origin, source);
        }
    }

    /// <summary>The box a paragraph's shading and borders cover.</summary>
    public static Rect ParagraphBox(ParaFragment f)
    {
        var p = f.Layout.Props;
        var rtl = p.Bidi;
        var indStart = Units.Twips(p.IndentStart);
        var indEnd = Units.Twips(p.IndentEnd);
        var left = f.X + (rtl ? indEnd : indStart);
        var right = f.X + f.Width - (rtl ? indStart : indEnd);
        if (p.FirstLine < 0 && !rtl) left = Math.Min(left, f.X + indStart + Units.Twips(p.FirstLine));
        // Borders sit outside the text by their spacing.
        if (p.BorderLeft is { } bl) left -= Units.Points(bl.Space) + Units.Points(bl.Size / 8.0);
        if (p.BorderRight is { } br) right += Units.Points(br.Space) + Units.Points(br.Size / 8.0);
        return new Rect(left, f.BoxTop, Math.Max(0, right - left), Math.Max(0, f.BoxBottom - f.BoxTop));
    }

    private void DrawParagraphBorders(DrawingContext dc, ParaFragment f, Rect box)
    {
        var p = f.Layout.Props;
        void Line(BorderDef b, Point a, Point c)
        {
            var color = OoxColor.Parse(b.Color) ?? (View.DarkPaper ? Color.FromRgb(0xDD, 0xDD, 0xDD) : Colors.Black);
            if (View.DarkPaper && b.Color != null) color = RunPropsFactory.ForDarkPaper(color);
            var width = Math.Max(0.5, Units.Points(b.Size / 8.0));
            var pen = Paint.Pen(color, width, Dash(b.Style));
            if (b.Style is "double" or "thinThickSmallGap" or "thickThinSmallGap" or "thinThickMediumGap" or "thickThinMediumGap")
            {
                var thin = Paint.Pen(color, Math.Max(0.5, width / 3));
                var n = new Vector(c.Y - a.Y, a.X - c.X);
                if (n.Length > 0) n.Normalize();
                dc.DrawLine(thin, a - n * width / 3, c - n * width / 3);
                dc.DrawLine(thin, a + n * width / 3, c + n * width / 3);
                return;
            }
            dc.DrawLine(pen, a, c);
        }
        if (f.DrawTopBorder && p.BorderTop is { } top)
        {
            var y = f.BoxTop + Units.Points(top.Size / 8.0) / 2;
            Line(top, new Point(box.Left, y), new Point(box.Right, y));
        }
        if (f.DrawBottomBorder && p.BorderBottom is { } bottom)
        {
            var y = f.BoxBottom - Units.Points(bottom.Size / 8.0) / 2;
            Line(bottom, new Point(box.Left, y), new Point(box.Right, y));
        }
        if (p.BorderLeft is { } left)
        {
            var x = box.Left + Units.Points(left.Size / 8.0) / 2;
            Line(left, new Point(x, box.Top), new Point(x, box.Bottom));
        }
        if (p.BorderRight is { } right)
        {
            var x = box.Right - Units.Points(right.Size / 8.0) / 2;
            Line(right, new Point(x, box.Top), new Point(x, box.Bottom));
        }
    }

    private static DashStyle Dash(string style) => style switch
    {
        "dotted" => DashStyles.Dot,
        "dashed" or "dashSmallGap" or "dashDotStroked" => DashStyles.Dash,
        "dotDash" => DashStyles.DashDot,
        "dotDotDash" => DashStyles.DashDotDot,
        _ => DashStyles.Solid,
    };

    private void DrawFieldShading(DrawingContext dc, ParaFragment f, LineInfo line, TextLine textLine, Point origin)
    {
        if (!View.FieldShading) return;
        // Read-only pieces (equations, content controls kept as is) get a soft tint so they read as "not editable here".
        foreach (var seg in f.Layout.Source.Segments)
        {
            if (seg.Kind != SegKind.Group || seg.Inline is not OpaqueInline) continue;
            if (seg.SrcEnd <= line.SrcStart || seg.SrcStart >= line.SrcStart + line.SrcLength) continue;
            foreach (var b in textLine.GetTextBounds(seg.SrcStart, seg.SrcLen))
            {
                var r = b.Rectangle;
                r.Offset(origin.X, origin.Y);
                dc.DrawRectangle(Paint.Brush(View.DarkPaper ? Color.FromArgb(0x40, 0x80, 0x80, 0x90) : Color.FromArgb(0x30, 0x80, 0x80, 0x90)), null, r);
            }
        }
    }

    private void DrawMarks(DrawingContext dc, ParaFragment f, LineInfo line, TextLine textLine, Point origin, ParaSource source)
    {
        var brush = Paint.Brush(MarkColor);
        var size = Math.Max(9, source.MarkProps.FontRenderingEmSize * 0.85);
        foreach (var seg in source.Segments)
        {
            if (seg.SrcEnd <= line.SrcStart || seg.SrcStart >= line.SrcStart + line.SrcLength) continue;
            if (seg.Kind == SegKind.Text && seg.Chars != null)
            {
                for (var k = 0; k < seg.SrcLen; k++)
                {
                    var c = seg.Chars[seg.CharsOffset + k];
                    if (c is not (' ' or '\t' or ' ')) continue;
                    var src = seg.SrcStart + k;
                    if (src < line.SrcStart || src >= line.SrcStart + line.SrcLength) continue;
                    var bounds = textLine.GetTextBounds(src, 1);
                    if (bounds.Count == 0) continue;
                    var r = bounds[0].Rectangle;
                    r.Offset(origin.X, origin.Y);
                    var glyph = c == '\t' ? "→" : c == ' ' ? "·" : "°";
                    DrawGlyph(dc, glyph, new Point(r.X + r.Width / 2, r.Y + r.Height / 2), size, brush, center: true);
                }
            }
            else if (seg.Kind == SegKind.LineEnd)
            {
                var bounds = textLine.GetTextBounds(seg.SrcStart, 1);
                if (bounds.Count == 0) continue;
                var r = bounds[0].Rectangle;
                r.Offset(origin.X, origin.Y);
                if (seg.Break == TextRun.LineBreak || seg.Break == 'c')
                    DrawGlyph(dc, "↵", new Point(r.X + 1, r.Y + r.Height / 2), size, brush, center: false);
                else
                    DrawBreakMarker(dc, f.X, f.X + f.Width, line.Top + line.Height / 2, seg.Break == TextRun.PageBreak ? "Page break" : "Column break", r.X + 4);
            }
        }
        if (line.EndsParagraph)
        {
            var end = textLine.GetDistanceFromCharacterHit(new CharacterHit(line.SrcStart + line.SrcLength - 1, 0));
            var x = origin.X + end + 1;
            if (source.IsRtl) x = origin.X + end - size * 0.6 - 1;
            DrawGlyph(dc, source.IsRtl ? "¶" : "¶", new Point(x, line.Top + line.TextOffset + line.NaturalHeight / 2), size, brush, center: false);
            if (source.Paragraph.Props.Section is { } sect)
            {
                var label = "Section break (" + sect.BreakType switch
                {
                    "continuous" => "Continuous",
                    "evenPage" => "Even Page",
                    "oddPage" => "Odd Page",
                    "nextColumn" => "Next Column",
                    _ => "Next Page",
                } + ")";
                DrawBreakMarker(dc, f.X, f.X + f.Width, line.Top + line.Height + 2, label);
            }
        }
    }

    private static void DrawGlyph(DrawingContext dc, string text, Point at, double size, Brush brush, bool center)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, MarkFace, size, brush, 1.0);
        var x = center ? at.X - ft.Width / 2 : at.X;
        dc.DrawText(ft, new Point(x, at.Y - ft.Height / 2));
    }

    /// <summary>A visible, labelled break line: "Page break", "Section break (Next Page)", "Column break".</summary>
    public void DrawBreakMarker(DrawingContext dc, double left, double right, double y, string label, double? from = null)
    {
        var color = MarkColor;
        var pen = Paint.Pen(color, 0.75, new DashStyle(new double[] { 3, 3 }, 0));
        var ft = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, MarkFace, 9.5, Paint.Brush(color), 1.0);
        var start = from ?? left;
        var mid = (start + right) / 2;
        var pad = 6;
        dc.DrawLine(pen, new Point(start, y), new Point(mid - ft.Width / 2 - pad, y));
        dc.DrawLine(pen, new Point(mid + ft.Width / 2 + pad, y), new Point(right, y));
        dc.DrawText(ft, new Point(mid - ft.Width / 2, y - ft.Height / 2));
    }

    private void DrawChangeBars(DrawingContext dc, PageLayout page)
    {
        var pen = Paint.Pen(View.DarkPaper ? Color.FromRgb(0xB0, 0xB0, 0xB8) : Color.FromRgb(0x50, 0x50, 0x58), 1.25);
        var x = Math.Max(4, page.Margins.Left - 14);
        void Visit(Fragment f)
        {
            switch (f)
            {
                case ParaFragment pf:
                    var p = pf.Paragraph;
                    var markRev = p.Props.MarkProps()?.MarkInserted != null || p.Props.MarkProps()?.MarkDeleted != null || p.Props.Change != null;
                    foreach (var line in pf.Lines)
                    {
                        var changed = markRev && line.EndsParagraph;
                        if (!changed)
                        {
                            var pos = 0;
                            foreach (var inline in p.Inlines)
                            {
                                var len = Math.Max(inline.Length, 1);
                                if (pos < line.DocEnd + (line.EndsParagraph ? 1 : 0) && pos + len > line.DocStart &&
                                    (inline.Inserted != null || inline.Deleted != null || inline.Props.Change != null))
                                {
                                    changed = true;
                                    break;
                                }
                                pos += inline.Length;
                            }
                        }
                        if (changed) dc.DrawLine(pen, new Point(x, line.Top), new Point(x, line.Top + line.Height));
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

    // ------------------------------------------------------------------ tables

    private void DrawTable(DrawingContext dc, TableFragment tf, RenderExtras? extras, LayoutEnv? env)
    {
        // Fills first, then content, then borders on top.
        foreach (var row in tf.Rows)
            foreach (var cell in row.Cells)
                if (cell.Cell.Fill is { } fill)
                    dc.DrawRectangle(Paint.Brush(View.DarkPaper ? RunPropsFactory.BackgroundForDarkPaper(fill) : fill), null, cell.Box);
        foreach (var row in tf.Rows)
            foreach (var cell in row.Cells)
            {
                dc.PushClip(new RectangleGeometry(new Rect(cell.Box.X, cell.Box.Y, cell.Box.Width, Math.Max(0, cell.Box.Height))));
                foreach (var f in cell.Content) DrawFragment(dc, f, extras, env);
                dc.Pop();
            }
        var gridlines = extras?.FormattingMarks == true;
        foreach (var row in tf.Rows)
        {
            foreach (var cell in row.Cells)
            {
                var c = cell.Cell;
                var b = cell.Box;
                // A cell continued from the previous page draws its top edge only if the table has one.
                DrawEdge(dc, c.Top, new Point(b.Left, b.Top), new Point(b.Right, b.Top), gridlines);
                DrawEdge(dc, c.Bottom, new Point(b.Left, b.Bottom), new Point(b.Right, b.Bottom), gridlines);
                DrawEdge(dc, c.Left, new Point(b.Left, b.Top), new Point(b.Left, b.Bottom), gridlines);
                DrawEdge(dc, c.Right, new Point(b.Right, b.Top), new Point(b.Right, b.Bottom), gridlines);
            }
        }
    }

    private void DrawEdge(DrawingContext dc, BorderDef? border, Point a, Point b, bool gridlines)
    {
        if (border is not { IsVisible: true } bd)
        {
            if (gridlines) dc.DrawLine(Paint.Pen(Color.FromArgb(0x70, MarkColor.R, MarkColor.G, MarkColor.B), 0.5, DashStyles.Dot), a, b);
            return;
        }
        var color = OoxColor.Parse(bd.Color) ?? (View.DarkPaper ? Color.FromRgb(0xCC, 0xCC, 0xCC) : Colors.Black);
        if (View.DarkPaper && bd.Color is not (null or "auto")) color = RunPropsFactory.ForDarkPaper(color);
        var width = Math.Max(0.5, Units.Points(bd.Size / 8.0));
        if (bd.Style == "double")
        {
            var n = new Vector(b.Y - a.Y, a.X - b.X);
            if (n.Length > 0) n.Normalize();
            var thin = Paint.Pen(color, Math.Max(0.5, width / 2));
            dc.DrawLine(thin, a - n * width, b - n * width);
            dc.DrawLine(thin, a + n * width, b + n * width);
            return;
        }
        dc.DrawLine(Paint.Pen(color, width, Dash(bd.Style)), a, b);
    }
}
