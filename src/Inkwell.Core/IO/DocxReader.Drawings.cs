using System.Globalization;
using System.Xml.Linq;
using Inkwell.Core.Model;

namespace Inkwell.Core.IO;

// Drawings (w:drawing, mc:AlternateContent with a drawing), VML pictures and shapes (w:pict), embedded objects (w:object).
internal sealed partial class DocxReadSession
{
    private const string PictureUri = "http://schemas.openxmlformats.org/drawingml/2006/picture";

    private DrawingObject ReadDrawing(XElement drawing, XElement original)
    {
        var d = new DrawingObject { Original = new XElement(original), SourcePart = _part.Uri, Kind = "other" };
        var container = drawing.Element(Wn.WpInline) ?? drawing.Element(Wn.WpAnchor);
        if (container == null)
        {
            Tally("other");
            return d;
        }

        var extent = container.Element(Ns.WP + "extent");
        d.Width = Math.Max(0, DocxXml.ParseLong(extent?.Attribute("cx")?.Value) ?? 0);
        d.Height = Math.Max(0, DocxXml.ParseLong(extent?.Attribute("cy")?.Value) ?? 0);
        var docPr = container.Element(Wn.WpDocPr);
        if (docPr != null)
        {
            var name = docPr.Attribute("name")?.Value;
            if (!string.IsNullOrEmpty(name)) d.Name = name!;
            d.AltText = docPr.Attribute("descr")?.Value;
            d.Title = docPr.Attribute("title")?.Value;
        }

        var graphicData = container.Element(Ns.A + "graphic")?.Element(Ns.A + "graphicData");
        d.Kind = KindOf(graphicData);
        switch (d.Kind)
        {
            case "picture":
                ReadPicture(d, graphicData!);
                break;
            case "textbox":
                ReadTextBox(d, graphicData!);
                Tally("textbox");
                break;
            case "chart":
                Tally("chart");
                break;
            case "smartart":
                Tally("smartart");
                break;
            case "ink":
                Tally("ink");
                break;
            case "shape":
            case "group":
            case "canvas":
            case "model3d":
                Tally("shape");
                break;
            default:
                Tally("other");
                break;
        }

        if (container.Name == Wn.WpAnchor) ReadAnchor(d, container);
        else d.Wrap = WrapMode.Inline;
        return d;
    }

    private static string KindOf(XElement? graphicData)
    {
        var uri = graphicData?.Attribute("uri")?.Value ?? "";
        switch (uri)
        {
            case PictureUri:
                return "picture";
            case "http://schemas.openxmlformats.org/drawingml/2006/chart":
            case "http://schemas.microsoft.com/office/drawing/2014/chartex":
                return "chart";
            case "http://schemas.openxmlformats.org/drawingml/2006/diagram":
                return "smartart";
            case "http://schemas.microsoft.com/office/word/2010/wordprocessingShape":
            {
                var wsp = graphicData!.Element(Ns.WPS + "wsp");
                return wsp != null && (wsp.Element(Ns.WPS + "txbx") != null || wsp.Element(Ns.WPS + "linkedTxbx") != null) ? "textbox" : "shape";
            }
            case "http://schemas.microsoft.com/office/word/2010/wordprocessingGroup":
                return "group";
            case "http://schemas.microsoft.com/office/word/2010/wordprocessingCanvas":
            case "http://schemas.openxmlformats.org/drawingml/2006/lockedCanvas":
                return "canvas";
            case "http://schemas.microsoft.com/office/word/2010/wordprocessingInk":
            case "http://schemas.microsoft.com/office/drawing/2016/ink":
                return "ink";
            case "http://schemas.microsoft.com/office/drawing/2017/model3d":
                return "model3d";
            default:
                return "other";
        }
    }

    private void ReadPicture(DrawingObject d, XElement graphicData)
    {
        var blip = graphicData.Element(Ns.Pic + "pic")?.Element(Ns.Pic + "blipFill")?.Element(Ns.A + "blip");
        var embed = blip?.Attribute(Ns.R + "embed")?.Value;
        var link = blip?.Attribute(Ns.R + "link")?.Value;
        if (embed != null) d.MediaId = ReadImage(embed);
        if (d.MediaId == null && link != null)
        {
            d.Kind = "linkedpicture";
            Tally("linkedPicture");
        }
    }

    /// <summary>Adds an image part's bytes to the media store (once per part).</summary>
    private string? ReadImage(string relId)
    {
        if (!_part.Part.TryGetPartById(relId, out var target)) return null;
        var key = target.Uri.OriginalString;
        if (_mediaByPart.TryGetValue(key, out var known)) return known;
        string? id;
        try
        {
            var bytes = DocxXml.ReadAllBytes(target);
            id = _doc.Media.Add(bytes, target.ContentType, Path.GetExtension(key));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            Warn("A picture in the document couldn't be read; it is kept but can't be shown.");
            id = null;
        }
        _mediaByPart[key] = id;
        return id;
    }

    private void ReadTextBox(DrawingObject d, XElement graphicData)
    {
        var content = graphicData.Element(Ns.WPS + "wsp")?.Element(Ns.WPS + "txbx")?.Element(Wn.TxbxContent);
        if (content == null) return;
        _displayDepth++;
        try
        {
            d.TextBoxContent = ParseBlocks(content.Elements());
        }
        finally
        {
            _displayDepth--;
        }
    }

    private static void ReadAnchor(DrawingObject d, XElement anchor)
    {
        bool Flag(string name, bool fallback) => anchor.Attribute(name) != null ? DocxXml.OnOffAttr(anchor, XName.Get(name)) : fallback;
        long Long(string name) => Math.Max(0, DocxXml.ParseLong(anchor.Attribute(name)?.Value) ?? 0);

        var f = new FloatInfo
        {
            Horizontal = ReadPosition(anchor.Element(Ns.WP + "positionH"), "column"),
            Vertical = ReadPosition(anchor.Element(Ns.WP + "positionV"), "paragraph"),
            DistTop = Long("distT"),
            DistBottom = Long("distB"),
            DistLeft = Long("distL"),
            DistRight = Long("distR"),
            ZOrder = Long("relativeHeight"),
            Locked = Flag("locked", false),
            AllowOverlap = Flag("allowOverlap", true),
            LayoutInCell = Flag("layoutInCell", true),
        };
        if (Flag("simplePos", false) && anchor.Element(Ns.WP + "simplePos") is { } simple)
        {
            f.Horizontal = new FloatPosition { RelativeFrom = "page", Offset = DocxXml.ParseLong(simple.Attribute("x")?.Value) ?? 0 };
            f.Vertical = new FloatPosition { RelativeFrom = "page", Offset = DocxXml.ParseLong(simple.Attribute("y")?.Value) ?? 0 };
        }

        var behind = Flag("behindDoc", false);
        var wrap = anchor.Elements().FirstOrDefault(e => e.Name.Namespace == Ns.WP && e.Name.LocalName.StartsWith("wrap", StringComparison.Ordinal));
        switch (wrap?.Name.LocalName)
        {
            case "wrapSquare":
                d.Wrap = WrapMode.Square;
                f.WrapSide = wrap.Attribute("wrapText")?.Value ?? "bothSides";
                break;
            case "wrapTight":
            case "wrapThrough":
                d.Wrap = WrapMode.Tight;
                f.WrapSide = wrap.Attribute("wrapText")?.Value ?? "bothSides";
                break;
            case "wrapTopAndBottom":
                d.Wrap = WrapMode.TopAndBottom;
                break;
            default:
                d.Wrap = behind ? WrapMode.Behind : WrapMode.InFront;
                break;
        }
        d.Float = f;
    }

    private static FloatPosition ReadPosition(XElement? position, string defaultFrom)
    {
        var p = new FloatPosition { RelativeFrom = position?.Attribute("relativeFrom")?.Value ?? defaultFrom };
        var align = position?.Element(Ns.WP + "align")?.Value;
        if (!string.IsNullOrWhiteSpace(align)) p.Align = align!.Trim();
        else p.Offset = DocxXml.ParseLong(position?.Element(Ns.WP + "posOffset")?.Value?.Trim()) ?? 0;
        return p;
    }

    private Inline ReadRunAlternateContent(XElement ac)
    {
        var choice = ac.Elements(Wn.Choice).FirstOrDefault(c => c.Element(Wn.Drawing) != null);
        if (choice != null)
        {
            var drawing = choice.Element(Wn.Drawing)!;
            var d = ReadDrawing(drawing, ac);
            // A picture is written back as a plain w:drawing (what every current Word reads); anything else keeps
            // the whole mc:AlternateContent with its fallback.
            d.Original = d.IsPicture ? new XElement(drawing) : CopyAlternateContent(ac);
            return d;
        }
        var pict = ac.Element(Wn.Fallback)?.Element(Wn.Pict);
        if (pict != null)
        {
            var o = ReadPict(pict);
            o.Xml = CopyAlternateContent(ac);
            return o;
        }
        return new OpaqueInline(CopyAlternateContent(ac), "other", AlternateText(ac)) { SourcePart = _part.Uri };
    }

    private OpaqueInline ReadPict(XElement pict)
    {
        string kind;
        if (pict.Descendants(Wn.Control).Any()) kind = "activeX";
        else if (pict.Descendants(Ns.V + "textbox").Any()) kind = "textbox";
        else if (pict.Descendants(Ns.V + "imagedata").Any()) kind = "vml";
        else kind = "shape";
        Tally(kind);
        var (w, h) = VmlSize(pict);
        var display = kind == "textbox" ? DocxXml.VisibleText(pict) : "";
        return new OpaqueInline(new XElement(pict), kind, display) { SourcePart = _part.Uri, WidthPoints = w, HeightPoints = h };
    }

    private OpaqueInline ReadEmbeddedObject(XElement obj)
    {
        var kind = obj.Descendants(Wn.Control).Any() ? "activeX" : "object";
        Tally(kind);
        var (w, h) = VmlSize(obj);
        if (w <= 0 && h <= 0)
        {
            w = (obj.IntAttr("dxaOrig") ?? 0) / 20.0;
            h = (obj.IntAttr("dyaOrig") ?? 0) / 20.0;
        }
        return new OpaqueInline(new XElement(obj), kind, "") { SourcePart = _part.Uri, WidthPoints = w, HeightPoints = h };
    }

    /// <summary>The size in points of the first VML element with a CSS width / height in its style.</summary>
    internal static (double Width, double Height) VmlSize(XElement e)
    {
        foreach (var d in e.Descendants())
        {
            if (d.Name.Namespace != Ns.V) continue;
            var style = d.Attribute("style")?.Value;
            if (style == null) continue;
            double w = 0, h = 0;
            foreach (var part in style.Split(';'))
            {
                var colon = part.IndexOf(':');
                if (colon < 0) continue;
                var key = part.Substring(0, colon).Trim().ToLowerInvariant();
                var value = part.Substring(colon + 1).Trim();
                if (key == "width") w = CssPoints(value);
                else if (key == "height") h = CssPoints(value);
            }
            if (w > 0 || h > 0) return (w, h);
        }
        return (0, 0);
    }

    private static double CssPoints(string value)
    {
        value = value.Trim().ToLowerInvariant();
        var unit = value.Length >= 2 && char.IsLetter(value[value.Length - 1]) ? value.Substring(value.Length - 2) : "";
        var number = unit.Length > 0 ? value.Substring(0, value.Length - 2) : value;
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return 0;
        return unit switch
        {
            "pt" => n,
            "in" => n * 72,
            "cm" => n * 72 / 2.54,
            "mm" => n * 72 / 25.4,
            "pc" => n * 12,
            "px" => n * 0.75,
            _ => n * 0.75,
        };
    }
}
