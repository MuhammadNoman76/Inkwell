using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using Inkwell.Core.Model;

namespace Inkwell.Core.IO;

// Pictures (patched into their original XML or built new), relationships of preserved XML moved between parts,
// image and hyperlink relationships.
internal sealed partial class DocxWriteSession
{
    private const string PictureUri = "http://schemas.openxmlformats.org/drawingml/2006/picture";

    /// <summary>A part being written: the relationships it gets for pictures, hyperlinks and moved content.</summary>
    private sealed class PartWriter
    {
        private readonly DocxWriteSession _session;
        private readonly Dictionary<string, string> _media = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string?> _mapped = new(StringComparer.Ordinal);
        private Dictionary<string, string>? _links;
        private Dictionary<string, string>? _images;

        public PartWriter(DocxWriteSession session, OpenXmlPart part)
        {
            _session = session;
            Part = part;
            Uri = part.Uri.OriginalString;
        }

        public OpenXmlPart Part { get; }

        public string Uri { get; }

        private string NewId() => ReferenceEquals(Part, _session._main) ? _session.NewMainRelId() : DocxXml.NewRelId(Part);

        /// <summary>The relationship id of the image part holding a picture's bytes (an identical existing part is reused).</summary>
        public string? ImageRel(string mediaId)
        {
            if (_media.TryGetValue(mediaId, out var known)) return known;
            var item = _session._doc.Media.Get(mediaId);
            if (item == null) return null;
            if (_images == null)
            {
                _images = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var pair in Part.Parts)
                {
                    if (pair.OpenXmlPart is not ImagePart image) continue;
                    try
                    {
                        var hash = DocxXml.MediaHash(DocxXml.ReadAllBytes(image));
                        if (!_images.ContainsKey(hash)) _images[hash] = pair.RelationshipId;
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException)
                    {
                        // unreadable picture: not reused
                    }
                }
            }
            if (!_images.TryGetValue(mediaId, out var id))
            {
                id = NewId();
                var contentType = item.ContentType == "application/octet-stream" ? "image/png" : item.ContentType;
                var part = Part.AddNewPart<ImagePart>(contentType, id);
                using (var s = part.GetStream(FileMode.Create, FileAccess.Write)) s.Write(item.Data, 0, item.Data.Length);
                _images[mediaId] = id;
            }
            _media[mediaId] = id;
            return id;
        }

        /// <summary>The relationship id of an external hyperlink (an existing one with the same address is reused).</summary>
        public string? LinkRel(string url)
        {
            if (_links == null)
            {
                _links = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var h in Part.HyperlinkRelationships)
                    if (!_links.ContainsKey(h.Uri.OriginalString)) _links[h.Uri.OriginalString] = h.Id;
            }
            if (_links.TryGetValue(url, out var id)) return id;
            var uri = MakeUri(url);
            if (uri == null) return null;
            id = NewId();
            Part.AddHyperlinkRelationship(uri, true, id);
            _links[url] = id;
            return id;
        }

        private static System.Uri? MakeUri(string url)
        {
            if (System.Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out var uri)) return uri;
            var escaped = url.Replace(" ", "%20").Replace("\"", "%22").Replace("<", "%3C").Replace(">", "%3E")
                .Replace("{", "%7B").Replace("}", "%7D").Replace("|", "%7C").Replace("^", "%5E").Replace("`", "%60");
            return System.Uri.TryCreate(escaped, UriKind.RelativeOrAbsolute, out uri) ? uri : null;
        }

        /// <summary>Re-creates in this part a relationship <paramref name="id"/> of another part; returns the new id (null if impossible).</summary>
        public string? MapRel(OpenXmlPart source, string id)
        {
            var key = source.Uri.OriginalString + "|" + id;
            if (_mapped.TryGetValue(key, out var known)) return known;
            string? result = null;
            try
            {
                if (source.TryGetPartById(id, out var target))
                {
                    foreach (var pair in Part.Parts)
                    {
                        if (ReferenceEquals(pair.OpenXmlPart, target))
                        {
                            result = pair.RelationshipId;
                            break;
                        }
                    }
                    if (result == null)
                    {
                        result = NewId();
                        Part.CreateRelationshipToPart(target, result);
                    }
                }
                else if (source.HyperlinkRelationships.FirstOrDefault(r => r.Id == id) is { } link)
                {
                    result = NewId();
                    Part.AddHyperlinkRelationship(link.Uri, link.IsExternal, result);
                }
                else if (source.ExternalRelationships.FirstOrDefault(r => r.Id == id) is { } external)
                {
                    result = NewId();
                    Part.AddExternalRelationship(external.RelationshipType, external.Uri, result);
                }
            }
            catch (Exception ex) when (ex is OpenXmlPackageException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                result = null;
            }
            _mapped[key] = result;
            return result;
        }
    }

    /// <summary>
    /// Preserved XML written to a part other than the one it came from: the relationships it references are
    /// re-created in the target part and the ids patched. Returns null (and warns) if one can't be.
    /// </summary>
    private XElement? MapRels(XElement x, string? sourcePart, PartWriter pw, object? owner)
    {
        if (sourcePart == null || string.Equals(sourcePart, pw.Uri, StringComparison.OrdinalIgnoreCase)) return x;
        var attrs = RelAttributes(x).ToList();
        if (attrs.Count > 0)
        {
            if (!_partsByUri.TryGetValue(sourcePart, out var source))
            {
                Warn("An object whose original data is no longer available was left out.");
                return null;
            }
            foreach (var a in attrs)
            {
                var id = pw.MapRel(source, a.Value);
                if (id == null)
                {
                    Warn("An object whose original data is no longer available was left out.");
                    return null;
                }
                a.Value = id;
            }
        }
        if (owner != null)
        {
            var snapshot = new XElement(x);
            var uri = pw.Uri;
            _after.Add(() => UpdateOwner(owner, snapshot, uri));
        }
        return x;
    }

    private static IEnumerable<XAttribute> RelAttributes(XElement x)
    {
        foreach (var e in x.DescendantsAndSelf())
            foreach (var a in e.Attributes())
                if (a.Name.Namespace == Ns.R || a.Name == Ns.O + "relid") yield return a;
    }

    /// <summary>After a save, preserved XML that moved to another part refers to that part's relationships.</summary>
    private static void UpdateOwner(object owner, XElement xml, string partUri)
    {
        switch (owner)
        {
            case DrawingObject d:
                d.Original = xml;
                d.SourcePart = partUri;
                break;
            case OpaqueInline o:
                o.Xml = xml;
                o.SourcePart = partUri;
                break;
            case OpaqueBlock b:
                b.Xml = xml;
                b.SourcePart = partUri;
                break;
            case SdtBlock s:
                s.Original = xml;
                s.SourcePart = partUri;
                break;
        }
    }

    // ------------------------------------------------------------------ drawings

    private XElement? DrawingXml(DrawingObject d, PartWriter pw)
    {
        if (d.IsPicture)
        {
            var relId = pw.ImageRel(d.MediaId!);
            if (relId != null)
            {
                XElement? drawing = null;
                var source = d.Original == null ? null : d.Original.Name == Wn.Drawing ? d.Original : d.Original.Descendants(Wn.Drawing).FirstOrDefault();
                if (source != null) drawing = PatchPicture(new XElement(source), d, relId, pw);
                drawing ??= BuildPicture(d, relId);
                if (d.Original != null && !string.Equals(d.SourcePart, pw.Uri, StringComparison.OrdinalIgnoreCase))
                {
                    var snapshot = new XElement(drawing);
                    var uri = pw.Uri;
                    _after.Add(() => UpdateOwner(d, snapshot, uri));
                }
                return drawing;
            }
            if (d.Original == null)
            {
                Warn("A picture whose data is missing was left out.");
                return null;
            }
        }
        if (d.Original == null)
        {
            Warn("A drawing without data was left out.");
            return null;
        }
        return MapRels(new XElement(d.Original), d.SourcePart, pw, d);
    }

    /// <summary>The original picture XML with Inkwell's size, wrap, position, alt text and image patched in (effects and crops stay).</summary>
    private XElement? PatchPicture(XElement drawing, DrawingObject d, string relId, PartWriter pw)
    {
        var container = drawing.Element(Wn.WpInline) ?? drawing.Element(Wn.WpAnchor);
        var pic = container?.Element(Ns.A + "graphic")?.Element(Ns.A + "graphicData")?.Element(Ns.Pic + "pic");
        var blip = pic?.Element(Ns.Pic + "blipFill")?.Element(Ns.A + "blip");
        if (container == null || pic == null || blip == null) return null;
        blip.Attribute(Ns.R + "embed")?.Remove();
        if (MapRels(drawing, d.SourcePart, pw, null) == null) return null;
        blip.SetAttributeValue(Ns.R + "embed", relId);

        if (d.Wrap == WrapMode.Inline && container.Name == Wn.WpAnchor) container = ConvertToInline(container);
        else if (d.Wrap != WrapMode.Inline && container.Name == Wn.WpInline) container = ConvertToAnchor(container);

        var cx = Ox.Str(Math.Max(0, d.Width));
        var cy = Ox.Str(Math.Max(0, d.Height));
        var extent = container.Element(Ns.WP + "extent");
        if (extent == null)
        {
            extent = new XElement(Ns.WP + "extent");
            container.AddFirst(extent);
        }
        extent.SetAttributeValue("cx", cx);
        extent.SetAttributeValue("cy", cy);

        var docPr = container.Element(Wn.WpDocPr);
        if (docPr == null)
        {
            docPr = new XElement(Wn.WpDocPr, new XAttribute("id", "0"));
            (container.Element(Ns.WP + "effectExtent") ?? extent).AddAfterSelf(docPr);
        }
        docPr.SetAttributeValue("name", string.IsNullOrEmpty(d.Name) ? "Picture" : d.Name);
        docPr.SetAttributeValue("descr", string.IsNullOrEmpty(d.AltText) ? null : d.AltText);
        docPr.SetAttributeValue("title", string.IsNullOrEmpty(d.Title) ? null : d.Title);

        var spPr = pic.Element(Ns.Pic + "spPr");
        if (spPr == null)
        {
            spPr = new XElement(Ns.Pic + "spPr");
            pic.Add(spPr);
        }
        var xfrm = spPr.Element(Ns.A + "xfrm");
        if (xfrm == null)
        {
            xfrm = new XElement(Ns.A + "xfrm", new XElement(Ns.A + "off", new XAttribute("x", "0"), new XAttribute("y", "0")));
            spPr.AddFirst(xfrm);
        }
        var ext = xfrm.Element(Ns.A + "ext");
        if (ext == null)
        {
            ext = new XElement(Ns.A + "ext");
            xfrm.Add(ext);
        }
        ext.SetAttributeValue("cx", cx);
        ext.SetAttributeValue("cy", cy);

        if (container.Name == Wn.WpAnchor) UpdateAnchor(container, d);
        return drawing;
    }

    private static XElement BuildPicture(DrawingObject d, string relId)
    {
        var name = string.IsNullOrEmpty(d.Name) ? "Picture" : d.Name;
        var cx = Ox.Str(Math.Max(0, d.Width));
        var cy = Ox.Str(Math.Max(0, d.Height));
        var docPr = new XElement(Wn.WpDocPr, new XAttribute("id", "0"), new XAttribute("name", name));
        if (!string.IsNullOrEmpty(d.AltText)) docPr.Add(new XAttribute("descr", d.AltText));
        if (!string.IsNullOrEmpty(d.Title)) docPr.Add(new XAttribute("title", d.Title));
        var inline = new XElement(Wn.WpInline,
            new XAttribute("distT", "0"), new XAttribute("distB", "0"), new XAttribute("distL", "0"), new XAttribute("distR", "0"),
            new XElement(Ns.WP + "extent", new XAttribute("cx", cx), new XAttribute("cy", cy)),
            new XElement(Ns.WP + "effectExtent", new XAttribute("l", "0"), new XAttribute("t", "0"), new XAttribute("r", "0"), new XAttribute("b", "0")),
            docPr,
            new XElement(Ns.WP + "cNvGraphicFramePr", new XElement(Ns.A + "graphicFrameLocks", new XAttribute("noChangeAspect", "1"))),
            new XElement(Ns.A + "graphic",
                new XElement(Ns.A + "graphicData", new XAttribute("uri", PictureUri),
                    new XElement(Ns.Pic + "pic",
                        new XElement(Ns.Pic + "nvPicPr",
                            new XElement(Ns.Pic + "cNvPr", new XAttribute("id", "0"), new XAttribute("name", name)),
                            new XElement(Ns.Pic + "cNvPicPr")),
                        new XElement(Ns.Pic + "blipFill",
                            new XElement(Ns.A + "blip", new XAttribute(Ns.R + "embed", relId)),
                            new XElement(Ns.A + "stretch", new XElement(Ns.A + "fillRect"))),
                        new XElement(Ns.Pic + "spPr",
                            new XElement(Ns.A + "xfrm",
                                new XElement(Ns.A + "off", new XAttribute("x", "0"), new XAttribute("y", "0")),
                                new XElement(Ns.A + "ext", new XAttribute("cx", cx), new XAttribute("cy", cy))),
                            new XElement(Ns.A + "prstGeom", new XAttribute("prst", "rect"), new XElement(Ns.A + "avLst")))))));
        var drawing = new XElement(Wn.Drawing, inline);
        if (d.Wrap != WrapMode.Inline)
        {
            var anchor = ConvertToAnchor(inline);
            UpdateAnchor(anchor, d);
        }
        return drawing;
    }

    private static XElement ConvertToAnchor(XElement inline)
    {
        var anchor = new XElement(Wn.WpAnchor);
        foreach (var a in inline.Attributes())
            if (a.Name.Namespace == Ns.WP14 || a.IsNamespaceDeclaration) anchor.Add(new XAttribute(a));
        foreach (var name in new[] { "extent", "effectExtent", "docPr", "cNvGraphicFramePr" })
            if (inline.Element(Ns.WP + name) is { } e) anchor.Add(new XElement(e));
        if (inline.Element(Ns.A + "graphic") is { } graphic) anchor.Add(new XElement(graphic));
        inline.ReplaceWith(anchor);
        return anchor;
    }

    private static XElement ConvertToInline(XElement anchor)
    {
        var inline = new XElement(Wn.WpInline,
            new XAttribute("distT", "0"), new XAttribute("distB", "0"), new XAttribute("distL", "0"), new XAttribute("distR", "0"));
        foreach (var a in anchor.Attributes())
            if (a.Name.Namespace == Ns.WP14 || a.IsNamespaceDeclaration) inline.Add(new XAttribute(a));
        foreach (var name in new[] { "extent", "effectExtent", "docPr", "cNvGraphicFramePr" })
            if (anchor.Element(Ns.WP + name) is { } e) inline.Add(new XElement(e));
        if (anchor.Element(Ns.A + "graphic") is { } graphic) inline.Add(new XElement(graphic));
        anchor.ReplaceWith(inline);
        return inline;
    }

    private static readonly HashSet<string> HorizontalFrom = new(StringComparer.Ordinal)
        { "margin", "page", "column", "character", "leftMargin", "rightMargin", "insideMargin", "outsideMargin" };

    private static readonly HashSet<string> VerticalFrom = new(StringComparer.Ordinal)
        { "margin", "page", "paragraph", "line", "topMargin", "bottomMargin", "insideMargin", "outsideMargin" };

    private static readonly HashSet<string> HorizontalAlign = new(StringComparer.Ordinal) { "left", "right", "center", "inside", "outside" };

    private static readonly HashSet<string> VerticalAlign = new(StringComparer.Ordinal) { "top", "bottom", "center", "inside", "outside" };

    private static readonly string[] WrapNames = { "wrapNone", "wrapSquare", "wrapTight", "wrapThrough", "wrapTopAndBottom" };

    /// <summary>Writes the float settings into a wp:anchor and puts its children in schema order.</summary>
    private static void UpdateAnchor(XElement anchor, DrawingObject d)
    {
        var f = d.Float ?? new FloatInfo();
        static string U(long v) => Ox.Str(MathEx.Clamp(v, 0L, uint.MaxValue));
        anchor.SetAttributeValue("distT", U(f.DistTop));
        anchor.SetAttributeValue("distB", U(f.DistBottom));
        anchor.SetAttributeValue("distL", U(f.DistLeft));
        anchor.SetAttributeValue("distR", U(f.DistRight));
        anchor.SetAttributeValue("simplePos", "0");
        anchor.SetAttributeValue("relativeHeight", U(f.ZOrder));
        anchor.SetAttributeValue("behindDoc", d.Wrap == WrapMode.Behind ? "1" : "0");
        anchor.SetAttributeValue("locked", f.Locked ? "1" : "0");
        anchor.SetAttributeValue("layoutInCell", f.LayoutInCell ? "1" : "0");
        anchor.SetAttributeValue("allowOverlap", f.AllowOverlap ? "1" : "0");

        var children = anchor.Elements().ToList();
        XElement? Take(XName name)
        {
            var e = children.FirstOrDefault(c => c.Name == name);
            if (e != null) children.Remove(e);
            return e;
        }
        var simplePos = Take(Ns.WP + "simplePos") ?? new XElement(Ns.WP + "simplePos", new XAttribute("x", "0"), new XAttribute("y", "0"));
        Take(Ns.WP + "positionH");
        Take(Ns.WP + "positionV");
        var extent = Take(Ns.WP + "extent");
        var effectExtent = Take(Ns.WP + "effectExtent");
        XElement? oldWrap = null;
        foreach (var w in WrapNames)
        {
            var e = Take(Ns.WP + w);
            oldWrap ??= e;
        }
        var docPr = Take(Wn.WpDocPr);
        var frame = Take(Ns.WP + "cNvGraphicFramePr");
        var graphic = Take(Ns.A + "graphic");

        anchor.RemoveNodes();
        anchor.Add(simplePos,
            Position("positionH", f.Horizontal, HorizontalFrom, HorizontalAlign, "column"),
            Position("positionV", f.Vertical, VerticalFrom, VerticalAlign, "paragraph"),
            extent, effectExtent, WrapElement(oldWrap, d, f), docPr, frame, graphic);
        anchor.Add(children); // wp14:sizeRelH / sizeRelV and anything newer, in their order
    }

    private static XElement Position(string name, FloatPosition pos, HashSet<string> from, HashSet<string> aligns, string defaultFrom)
    {
        var el = new XElement(Ns.WP + name, new XAttribute("relativeFrom", from.Contains(pos.RelativeFrom) ? pos.RelativeFrom : defaultFrom));
        if (!string.IsNullOrEmpty(pos.Align) && aligns.Contains(pos.Align!)) el.Add(new XElement(Ns.WP + "align", pos.Align));
        else el.Add(new XElement(Ns.WP + "posOffset", Ox.Str(MathEx.Clamp(pos.Offset, int.MinValue, int.MaxValue))));
        return el;
    }

    private static XElement WrapElement(XElement? old, DrawingObject d, FloatInfo f)
    {
        var side = f.WrapSide is "bothSides" or "left" or "right" or "largest" ? f.WrapSide : "bothSides";
        var oldName = old?.Name.LocalName;
        switch (d.Wrap)
        {
            case WrapMode.Square:
            {
                var e = oldName == "wrapSquare" ? old! : new XElement(Ns.WP + "wrapSquare");
                e.SetAttributeValue("wrapText", side);
                return e;
            }
            case WrapMode.Tight:
            {
                var e = oldName is "wrapTight" or "wrapThrough" ? old! : new XElement(Ns.WP + "wrapTight");
                e.SetAttributeValue("wrapText", side);
                if (e.Element(Ns.WP + "wrapPolygon") == null)
                {
                    e.Add(new XElement(Ns.WP + "wrapPolygon", new XAttribute("edited", "0"),
                        Point("start", 0, 0), Point("lineTo", 0, 21600), Point("lineTo", 21600, 21600), Point("lineTo", 21600, 0), Point("lineTo", 0, 0)));
                }
                return e;
            }
            case WrapMode.TopAndBottom:
                return oldName == "wrapTopAndBottom" ? old! : new XElement(Ns.WP + "wrapTopAndBottom");
            default:
                return oldName == "wrapNone" ? old! : new XElement(Ns.WP + "wrapNone");
        }
    }

    private static XElement Point(string name, int x, int y) =>
        new(Ns.WP + name, new XAttribute("x", Ox.Str(x)), new XAttribute("y", Ox.Str(y)));
}
