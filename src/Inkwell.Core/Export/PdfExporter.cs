using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.TextFormatting;
using Inkwell.Core.Layout;
using Inkwell.Core.Model;
using Inkwell.Core.Rendering;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace Inkwell.Core.Export;

public sealed class PdfExportOptions
{
    /// <summary>An invisible text layer so the words can be selected, copied and searched.</summary>
    public bool SelectableText { get; set; } = true;

    /// <summary>PDF bookmarks (the outline) from the headings.</summary>
    public bool Bookmarks { get; set; } = true;

    /// <summary>Clickable links (web addresses, and links inside the document such as the table of contents).</summary>
    public bool Links { get; set; } = true;

    /// <summary>Show tracked changes as markup (otherwise the final text).</summary>
    public bool ShowMarkup { get; set; }

    /// <summary>Pixel width of pieces that have to be drawn as pictures (soft effects).</summary>
    public int RasterWidth { get; set; } = 2480;

    public string? FileName { get; set; }

    /// <summary>Called after each page with (done, total).</summary>
    public Action<int, int>? Progress { get; set; }
}

/// <summary>Encodes bitmaps.</summary>
internal static class ImageCodec
{
    public static byte[] Png(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    public static byte[] Jpeg(BitmapSource bitmap, int quality)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}

/// <summary>
/// PDF export with PDFsharp. Pages are drawn by the same renderer as the editor into WPF drawings, which are
/// translated into PDF vector graphics: text becomes outlines identical to the screen (complex scripts such as
/// Urdu keep their shaping) with an invisible text layer on top for selecting, copying and searching; pictures
/// are embedded. Headings become PDF bookmarks and links stay clickable.
/// </summary>
public static class PdfExporter
{
    /// <summary>DIPs (1/96 inch) to points (1/72 inch).</summary>
    public const double PointsPerDip = 0.75;

    public static void Export(Document doc, string path, PdfExportOptions options)
    {
        var temp = path + ".part";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                Export(doc, stream, options);
            Compat.MoveOverwrite(temp, path);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
        }
    }

    private sealed class LinkArea
    {
        public int Page;
        public Rect Box;
        public Hyperlink Link = null!;
    }

    public static void Export(Document doc, Stream output, PdfExportOptions options)
    {
        var view = new ViewOptions { Revisions = options.ShowMarkup ? RevisionView.AllMarkup : RevisionView.NoMarkup, FieldShading = false };
        using var layout = new DocumentLayout(doc, view) { FileName = options.FileName };
        layout.LayoutAll();
        var renderer = new PageRenderer(layout);
        using var pdf = new PdfDocument();
        pdf.Info.Title = doc.Properties.Title ?? (options.FileName != null ? Path.GetFileNameWithoutExtension(options.FileName) : "");
        if (!string.IsNullOrEmpty(doc.Properties.Creator)) pdf.Info.Author = doc.Properties.Creator!;
        if (!string.IsNullOrEmpty(doc.Properties.Subject)) pdf.Info.Subject = doc.Properties.Subject!;
        if (!string.IsNullOrEmpty(doc.Properties.Keywords)) pdf.Info.Keywords = doc.Properties.Keywords!;
        pdf.Info.Creator = "Inkwell";
        var images = new Dictionary<ImageSource, XImage>();
        var links = new List<LinkArea>();

        for (var i = 0; i < layout.Pages.Count; i++)
        {
            var page = layout.Pages[i];
            var pdfPage = pdf.AddPage();
            pdfPage.Width = XUnit.FromPoint(page.Size.Width * PointsPerDip);
            pdfPage.Height = XUnit.FromPoint(page.Size.Height * PointsPerDip);
            var drawing = new DrawingGroup();
            var pageIndex = i;
            using (var dc = drawing.Open())
            {
                var extras = new RenderExtras();
                if (options.Links) extras.LineDrawn = (fragment, line, textLine, origin) => CollectLinks(fragment, line, textLine, origin, pageIndex, links);
                renderer.DrawPage(dc, page, extras);
            }
            using (var gfx = XGraphics.FromPdfPage(pdfPage))
            {
                gfx.ScaleTransform(PointsPerDip);
                var writer = new Writer(gfx, options.RasterWidth / Math.Max(1, page.Size.Width), images) { SelectableText = options.SelectableText };
                writer.Draw(drawing, 1.0);
            }
            options.Progress?.Invoke(i + 1, layout.Pages.Count);
        }
        if (layout.Pages.Count == 0) pdf.AddPage();

        if (options.Links) AddLinks(pdf, layout, links);
        if (options.Bookmarks) AddBookmarks(pdf, layout);
        pdf.Save(output, false);
    }

    private static void CollectLinks(ParaFragment fragment, LineInfo line, TextLine textLine, Point origin, int page, List<LinkArea> links)
    {
        var source = fragment.Layout.Source;
        foreach (var seg in source.Segments)
        {
            if (seg.Inline?.Link == null || seg.SrcLen == 0) continue;
            if (seg.SrcEnd <= line.SrcStart || seg.SrcStart >= line.SrcStart + line.SrcLength) continue;
            var start = Math.Max(seg.SrcStart, line.SrcStart);
            var end = Math.Min(seg.SrcEnd, line.SrcStart + line.SrcLength);
            foreach (var b in textLine.GetTextBounds(start, end - start))
            {
                var r = b.Rectangle;
                r.Offset(origin.X, origin.Y);
                links.Add(new LinkArea { Page = page, Box = r, Link = seg.Inline.Link });
            }
        }
    }

    private static PdfRectangle ToPdf(Rect r, double pageHeightPt) => new(
        new XPoint(r.Left * PointsPerDip, pageHeightPt - r.Bottom * PointsPerDip),
        new XPoint(r.Right * PointsPerDip, pageHeightPt - r.Top * PointsPerDip));

    private static void AddLinks(PdfDocument pdf, DocumentLayout layout, List<LinkArea> links)
    {
        // Bookmark name → page of the paragraph that has it.
        var targets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in layout.Pages)
            foreach (var p in page.Columns.SelectMany(c => c.Content).SelectMany(Paragraphs))
                foreach (var b in p.Paragraph.Inlines.OfType<BookmarkMarker>().Where(b => b.IsStart && b.Name != null))
                    if (!targets.ContainsKey(b.Name!) && p.IsStart) targets[b.Name!] = page.Index;
        // Neighbouring pieces of the same link on one line become one annotation.
        foreach (var group in links.GroupBy(l => (l.Page, l.Link)))
        {
            var pdfPage = pdf.Pages[group.Key.Page];
            var heightPt = pdfPage.Height.Point;
            foreach (var box in Merge(group.Select(g => g.Box)))
            {
                try
                {
                    var link = group.Key.Link;
                    if (!string.IsNullOrEmpty(link.Url) && IsSafeUrl(link.Url!)) pdfPage.AddWebLink(ToPdf(box, heightPt), link.Url!);
                    else if (!string.IsNullOrEmpty(link.Anchor) && targets.TryGetValue(link.Anchor!, out var target)) pdfPage.AddDocumentLink(ToPdf(box, heightPt), target + 1);
                }
                catch (Exception)
                {
                    // A link that can't be written leaves the page as it is.
                }
            }
        }
    }

    private static IEnumerable<ParaFragment> Paragraphs(Fragment f)
    {
        switch (f)
        {
            case ParaFragment pf:
                yield return pf;
                break;
            case TableFragment tf:
                foreach (var row in tf.Rows)
                    foreach (var cell in row.Cells)
                        foreach (var inner in cell.Content.SelectMany(Paragraphs))
                            yield return inner;
                break;
        }
    }

    private static IEnumerable<Rect> Merge(IEnumerable<Rect> rects)
    {
        var list = rects.OrderBy(r => r.Top).ThenBy(r => r.Left).ToList();
        var result = new List<Rect>();
        foreach (var r in list)
        {
            if (result.Count > 0)
            {
                var last = result[result.Count - 1];
                if (Math.Abs(last.Top - r.Top) < 1 && r.Left <= last.Right + 2)
                {
                    result[result.Count - 1] = Rect.Union(last, r);
                    continue;
                }
            }
            result.Add(r);
        }
        return result;
    }

    private static bool IsSafeUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto";

    /// <summary>PDF bookmarks from the headings (nested by level).</summary>
    private static void AddBookmarks(PdfDocument pdf, DocumentLayout layout)
    {
        var stack = new List<(int Level, PdfOutline Outline)>();
        foreach (var page in layout.Pages)
        {
            foreach (var pf in page.Columns.SelectMany(c => c.Content).OfType<ParaFragment>())
            {
                if (!pf.IsStart) continue;
                var level = pf.Layout.Props.OutlineLevel;
                if (level is < 0 or > 5) continue;
                var title = pf.Paragraph.PlainText().Replace('\n', ' ').Trim();
                if (title.Length == 0) continue;
                if (title.Length > 120) title = title.Substring(0, 117) + "…";
                while (stack.Count > 0 && stack[stack.Count - 1].Level >= level) stack.RemoveAt(stack.Count - 1);
                var parent = stack.Count > 0 ? stack[stack.Count - 1].Outline.Outlines : pdf.Outlines;
                var outline = parent.Add(title, pdf.Pages[page.Index], true);
                stack.Add((level, outline));
            }
        }
    }

    /// <summary>
    /// The fonts of the invisible text layer: Inter for most text and Noto Naskh Arabic for Arabic-script text
    /// (both bundled, SIL OFL, embedding allowed), subset into the PDF. Only their character codes matter; the
    /// visible text is drawn as outlines in the document's real fonts.
    /// </summary>
    private static class TextLayer
    {
        private const string Face = "InkwellTextLayer";
        private const string ArabicFace = "InkwellTextLayerArabic";
        private static readonly object Gate = new();
        private static bool? s_ready;

        public static readonly XBrush Invisible = new XSolidBrush(XColor.FromArgb(0, 0, 0, 0));

        public static bool Ready
        {
            get
            {
                lock (Gate)
                {
                    if (s_ready.HasValue) return s_ready.Value;
                    try
                    {
                        var latin = FontCatalog.FindFile("Inter", 400, false);
                        var arabic = FontCatalog.FindFile("Noto Naskh Arabic", 400, false);
                        if (latin == null || !File.Exists(latin))
                        {
                            s_ready = false;
                        }
                        else
                        {
                            GlobalFontSettings.FontResolver ??= new Resolver(File.ReadAllBytes(latin), arabic != null && File.Exists(arabic) ? File.ReadAllBytes(arabic) : null);
                            s_ready = GlobalFontSettings.FontResolver is Resolver;
                        }
                    }
                    catch (Exception)
                    {
                        s_ready = false;
                    }
                    return s_ready.Value;
                }
            }
        }

        public static bool NeedsArabic(string text) => text.Any(RunPropsFactory.IsRtlChar);

        public static XFont Font(double size, bool arabic) => new(arabic && (GlobalFontSettings.FontResolver as Resolver)?.HasArabic == true ? ArabicFace : Face, size,
            XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));

        private sealed class Resolver : IFontResolver
        {
            private readonly byte[] _latin;
            private readonly byte[]? _arabic;

            public Resolver(byte[] latin, byte[]? arabic)
            {
                _latin = latin;
                _arabic = arabic;
            }

            public bool HasArabic => _arabic != null;

            public FontResolverInfo ResolveTypeface(string familyName, bool bold, bool italic) =>
                new(familyName == ArabicFace && _arabic != null ? ArabicFace : Face);

            public byte[] GetFont(string faceName) => faceName == ArabicFace && _arabic != null ? _arabic : _latin;
        }
    }

    /// <summary>Walks a WPF drawing and issues the matching PDFsharp calls.</summary>
    private sealed class Writer
    {
        private readonly XGraphics _gfx;
        private readonly double _rasterScale;
        private readonly Dictionary<ImageSource, XImage> _images;

        public Writer(XGraphics gfx, double rasterScale, Dictionary<ImageSource, XImage> images)
        {
            _gfx = gfx;
            _rasterScale = Math.Max(1, rasterScale);
            _images = images;
        }

        public void Draw(Drawing drawing, double opacity)
        {
            switch (drawing)
            {
                case DrawingGroup group:
                    DrawGroup(group, opacity);
                    break;
                case GeometryDrawing geometry:
                    DrawGeometry(geometry, opacity);
                    break;
                case GlyphRunDrawing glyphs:
                    DrawGlyphs(glyphs, opacity);
                    break;
                case ImageDrawing image:
                    DrawImage(image, opacity);
                    break;
                default:
                    Rasterize(drawing, opacity);
                    break;
            }
        }

        private void DrawGroup(DrawingGroup group, double opacity)
        {
            if (group.OpacityMask != null || group.BitmapEffect != null)
            {
                Rasterize(group, opacity);
                return;
            }
            var state = _gfx.Save();
            try
            {
                if (group.Transform != null && !group.Transform.Value.IsIdentity)
                {
                    var m = group.Transform.Value;
                    _gfx.MultiplyTransform(new XMatrix(m.M11, m.M12, m.M21, m.M22, m.OffsetX, m.OffsetY));
                }
                if (group.ClipGeometry != null)
                {
                    var clip = ToPath(group.ClipGeometry);
                    if (clip != null) _gfx.IntersectClip(clip);
                }
                var o = opacity * group.Opacity;
                foreach (var child in group.Children) Draw(child, o);
            }
            finally
            {
                _gfx.Restore(state);
            }
        }

        private void DrawGeometry(GeometryDrawing drawing, double opacity)
        {
            if (drawing.Geometry == null) return;
            XBrush? brush = null;
            if (drawing.Brush != null)
            {
                brush = ToBrush(drawing.Brush, drawing.Geometry.Bounds, opacity);
                if (brush == null)
                {
                    Rasterize(drawing, opacity);
                    return;
                }
            }
            XPen? pen = null;
            if (drawing.Pen is { Brush: SolidColorBrush penBrush } p && p.Thickness > 0)
            {
                pen = new XPen(ToColor(penBrush.Color, penBrush.Opacity * opacity), p.Thickness)
                {
                    LineCap = p.StartLineCap switch { PenLineCap.Round => XLineCap.Round, PenLineCap.Square => XLineCap.Square, _ => XLineCap.Flat },
                    LineJoin = p.LineJoin switch { PenLineJoin.Round => XLineJoin.Round, PenLineJoin.Bevel => XLineJoin.Bevel, _ => XLineJoin.Miter },
                };
                if (p.DashStyle != null && p.DashStyle.Dashes.Count > 0)
                {
                    pen.DashStyle = XDashStyle.Custom;
                    pen.DashPattern = p.DashStyle.Dashes.Select(d => Math.Max(0.01, d)).ToArray();
                }
            }
            else if (drawing.Pen?.Brush != null && drawing.Pen.Brush is not SolidColorBrush)
            {
                Rasterize(drawing, opacity);
                return;
            }
            if (brush == null && pen == null) return;
            var path = ToPath(drawing.Geometry);
            if (path == null) return;
            if (brush != null && pen != null) _gfx.DrawPath(pen, brush, path);
            else if (brush != null) _gfx.DrawPath(brush, path);
            else _gfx.DrawPath(pen!, path);
        }

        private void DrawGlyphs(GlyphRunDrawing drawing, double opacity)
        {
            if (drawing.GlyphRun == null || drawing.ForegroundBrush == null) return;
            var geometry = drawing.GlyphRun.BuildGeometry();
            var brush = ToBrush(drawing.ForegroundBrush, geometry.Bounds, opacity);
            if (brush == null)
            {
                Rasterize(drawing, opacity);
            }
            else
            {
                var path = ToPath(geometry);
                if (path != null) _gfx.DrawPath(brush, path);
            }
            if (_selectableText) DrawInvisibleText(drawing.GlyphRun);
        }

        private bool _selectableText;

        public bool SelectableText
        {
            get => _selectableText;
            set => _selectableText = value && TextLayer.Ready;
        }

        /// <summary>
        /// The words of a glyph run as invisible PDF text over the outlines, stretched to the same width, so the
        /// text can be selected, copied and searched.
        /// </summary>
        private void DrawInvisibleText(GlyphRun run)
        {
            try
            {
                var chars = run.Characters;
                if (chars == null || chars.Count == 0) return;
                var text = new string(chars.ToArray()).Replace('​', ' ');
                if (string.IsNullOrWhiteSpace(text)) return;
                var size = run.FontRenderingEmSize;
                if (size <= 0.5) return;
                var advance = run.AdvanceWidths.Sum();
                var font = TextLayer.Font(size, TextLayer.NeedsArabic(text));
                var measured = _gfx.MeasureString(text, font).Width;
                var state = _gfx.Save();
                try
                {
                    _gfx.TranslateTransform(run.BaselineOrigin.X, run.BaselineOrigin.Y);
                    if (measured > 0.01 && advance > 0.01) _gfx.ScaleTransform(advance / measured, 1);
                    _gfx.DrawString(text, font, TextLayer.Invisible, 0, 0, XStringFormats.BaseLineLeft);
                }
                finally
                {
                    _gfx.Restore(state);
                }
            }
            catch (Exception)
            {
                // The text layer is a bonus: the page itself is already drawn.
                _selectableText = false;
            }
        }

        private void DrawImage(ImageDrawing drawing, double opacity)
        {
            if (drawing.ImageSource is not BitmapSource bitmap) return;
            if (opacity < 0.999)
            {
                Rasterize(drawing, opacity);
                return;
            }
            if (!_images.TryGetValue(bitmap, out var image))
            {
                var bytes = HasTransparency(bitmap) ? ImageCodec.Png(bitmap) : ImageCodec.Jpeg(bitmap, 92);
                image = XImage.FromStream(new MemoryStream(bytes));
                _images[bitmap] = image;
            }
            var r = drawing.Rect;
            _gfx.DrawImage(image, r.X, r.Y, r.Width, r.Height);
        }

        private static bool HasTransparency(BitmapSource bitmap)
        {
            try
            {
                var src = bitmap.Format == PixelFormats.Bgra32 ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                var stride = src.PixelWidth * 4;
                var pixels = new byte[stride * src.PixelHeight];
                src.CopyPixels(pixels, stride, 0);
                for (var i = 3; i < pixels.Length; i += 4)
                    if (pixels[i] < 250) return true;
                return false;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>Draws something PDF can't express as a picture of itself (in its own coordinate space).</summary>
        private void Rasterize(Drawing drawing, double opacity)
        {
            var bounds = drawing.Bounds;
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return;
            var scale = _rasterScale;
            var w = (int)Math.Ceiling(bounds.Width * scale);
            var h = (int)Math.Ceiling(bounds.Height * scale);
            var max = 3000.0;
            if (w > max || h > max)
            {
                var k = Math.Min(max / w, max / h);
                w = Math.Max(1, (int)(w * k));
                h = Math.Max(1, (int)(h * k));
                scale = w / bounds.Width;
            }
            if (w < 1 || h < 1) return;
            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            using (var dc = visual.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(scale, scale));
                dc.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
                dc.PushOpacity(opacity);
                dc.DrawDrawing(drawing);
            }
            var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var image = XImage.FromStream(new MemoryStream(ImageCodec.Png(bitmap)));
            _gfx.DrawImage(image, bounds.X, bounds.Y, w / scale, h / scale);
        }

        private static XColor ToColor(Color c, double opacity)
        {
            var a = MathEx.Clamp(c.A / 255.0 * opacity, 0, 1);
            return XColor.FromArgb((int)Math.Round(a * 255), c.R, c.G, c.B);
        }

        /// <summary>Solid colours and opaque two-colour linear gradients; null for anything else (rasterized).</summary>
        private static XBrush? ToBrush(Brush brush, Rect bounds, double opacity)
        {
            switch (brush)
            {
                case SolidColorBrush solid:
                    return new XSolidBrush(ToColor(solid.Color, solid.Opacity * opacity));
                case LinearGradientBrush linear when linear.GradientStops.Count == 2 && linear.Transform.Value.IsIdentity && linear.RelativeTransform.Value.IsIdentity
                                                     && linear.GradientStops.All(s => s.Color.A == 255) && opacity * linear.Opacity > 0.999:
                {
                    var stops = linear.GradientStops.OrderBy(s => s.Offset).ToList();
                    var start = linear.StartPoint;
                    var end = linear.EndPoint;
                    if (linear.MappingMode == BrushMappingMode.RelativeToBoundingBox)
                    {
                        start = new Point(bounds.X + start.X * bounds.Width, bounds.Y + start.Y * bounds.Height);
                        end = new Point(bounds.X + end.X * bounds.Width, bounds.Y + end.Y * bounds.Height);
                    }
                    // Stops not at 0 and 1: move the end points so the colours change over the same stretch.
                    var v = end - start;
                    var s0 = stops[0].Offset;
                    var s1 = stops[1].Offset;
                    var p0 = start + v * s0;
                    var p1 = start + v * s1;
                    if ((p1 - p0).Length < 0.01) return new XSolidBrush(ToColor(stops[1].Color, 1));
                    return new XLinearGradientBrush(new XPoint(p0.X, p0.Y), new XPoint(p1.X, p1.Y), ToColor(stops[0].Color, 1), ToColor(stops[1].Color, 1));
                }
                default:
                    return null;
            }
        }

        private static XGraphicsPath? ToPath(Geometry geometry)
        {
            PathGeometry pg;
            try
            {
                pg = PathGeometry.CreateFromGeometry(geometry);
            }
            catch
            {
                return null;
            }
            var path = new XGraphicsPath { FillMode = pg.FillRule == FillRule.EvenOdd ? XFillMode.Alternate : XFillMode.Winding };
            var transform = pg.Transform != null && !pg.Transform.Value.IsIdentity ? pg.Transform.Value : (Matrix?)null;
            XPoint T(Point p)
            {
                if (transform is { } m) p = m.Transform(p);
                return new XPoint(p.X, p.Y);
            }

            foreach (var figure in pg.Figures)
            {
                path.StartFigure();
                var current = figure.StartPoint;
                var hasArc = figure.Segments.Any(s => s is ArcSegment);
                if (hasArc && transform != null)
                {
                    // Arcs under a transform: flatten this figure.
                    var single = new PathGeometry(new[] { figure }) { Transform = pg.Transform };
                    var flat = single.GetFlattenedPathGeometry(0.05, ToleranceType.Absolute);
                    foreach (var ff in flat.Figures)
                    {
                        var pts = new List<XPoint> { new(ff.StartPoint.X, ff.StartPoint.Y) };
                        foreach (var seg in ff.Segments)
                        {
                            if (seg is PolyLineSegment pls) pts.AddRange(pls.Points.Select(p => new XPoint(p.X, p.Y)));
                            else if (seg is LineSegment ls) pts.Add(new XPoint(ls.Point.X, ls.Point.Y));
                        }
                        if (pts.Count > 1) path.AddLines(pts.ToArray());
                        if (ff.IsClosed) path.CloseFigure();
                    }
                    continue;
                }
                foreach (var segment in figure.Segments)
                {
                    switch (segment)
                    {
                        case LineSegment line:
                            path.AddLine(T(current), T(line.Point));
                            current = line.Point;
                            break;
                        case PolyLineSegment poly:
                            foreach (var p in poly.Points)
                            {
                                path.AddLine(T(current), T(p));
                                current = p;
                            }
                            break;
                        case BezierSegment bezier:
                            path.AddBezier(T(current), T(bezier.Point1), T(bezier.Point2), T(bezier.Point3));
                            current = bezier.Point3;
                            break;
                        case PolyBezierSegment polyBezier:
                            for (var i = 0; i + 2 < polyBezier.Points.Count; i += 3)
                            {
                                path.AddBezier(T(current), T(polyBezier.Points[i]), T(polyBezier.Points[i + 1]), T(polyBezier.Points[i + 2]));
                                current = polyBezier.Points[i + 2];
                            }
                            break;
                        case QuadraticBezierSegment quad:
                            AddQuadratic(path, current, quad.Point1, quad.Point2, T);
                            current = quad.Point2;
                            break;
                        case PolyQuadraticBezierSegment polyQuad:
                            for (var i = 0; i + 1 < polyQuad.Points.Count; i += 2)
                            {
                                AddQuadratic(path, current, polyQuad.Points[i], polyQuad.Points[i + 1], T);
                                current = polyQuad.Points[i + 1];
                            }
                            break;
                        case ArcSegment arc:
                            path.AddArc(T(current), T(arc.Point), new XSize(arc.Size.Width, arc.Size.Height), arc.RotationAngle, arc.IsLargeArc,
                                arc.SweepDirection == SweepDirection.Clockwise ? XSweepDirection.Clockwise : XSweepDirection.Counterclockwise);
                            current = arc.Point;
                            break;
                    }
                }
                if (figure.IsClosed) path.CloseFigure();
            }
            return path;
        }

        private static void AddQuadratic(XGraphicsPath path, Point p0, Point q, Point p3, Func<Point, XPoint> t)
        {
            var c1 = new Point(p0.X + 2.0 / 3 * (q.X - p0.X), p0.Y + 2.0 / 3 * (q.Y - p0.Y));
            var c2 = new Point(p3.X + 2.0 / 3 * (q.X - p3.X), p3.Y + 2.0 / 3 * (q.Y - p3.Y));
            path.AddBezier(t(p0), t(c1), t(c2), t(p3));
        }
    }
}
