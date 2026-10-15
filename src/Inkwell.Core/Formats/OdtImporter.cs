using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Inkwell.Core.Model;
using Inkwell.Core.Templates;

namespace Inkwell.Core.Formats;

/// <summary>
/// OpenDocument Text import (.odt, and flat .fodt): paragraphs and headings (named styles mapped to Inkwell's by
/// their display names — Heading N, Title, Subtitle, Quotations → Quote, Preformatted Text → Code…; automatic
/// styles are direct formatting: weight, slant, underline, strikethrough, colour, size, font, position, alignment,
/// spacing, indents), spans, links, spaces, tabs and line breaks, lists (bullet and numbered list styles, nesting,
/// continued numbering), tables (column widths, spans, covered cells → merges, header rows, cell shading and
/// borders), pictures (Pictures/ entries with their size), text boxes (their text follows the paragraph),
/// footnotes and endnotes, bookmarks, comments, document information and page layout. Floating pictures are
/// placed in line with the text.
/// </summary>
public static class OdtImporter
{
    /// <summary>Opens an .odt (or flat .fodt) file as a new document.</summary>
    public static Document ImportFile(string path, DocumentDesign? design = null)
    {
        using var stream = File.OpenRead(path);
        return Import(stream, design);
    }

    /// <summary>A new document from the bytes of an .odt (or flat .fodt) file.</summary>
    public static Document Import(byte[] bytes, DocumentDesign? design = null)
    {
        using var stream = new MemoryStream(bytes, false);
        return Import(stream, design);
    }

    /// <summary>A new document from an .odt package (or a flat .fodt XML document) in a stream.</summary>
    public static Document Import(Stream stream, DocumentDesign? design = null)
    {
        var doc = NewDocument.Create(design);
        var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        buffer.Position = 0;
        var head = new byte[2];
        var read = buffer.Read(head, 0, 2);
        buffer.Position = 0;
        if (read == 2 && head[0] == (byte)'P' && head[1] == (byte)'K')
        {
            // Pictures are read from the package while converting: keep it open until then.
            using var zip = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);
            new OdtConverter(doc, OdtPackage.FromZip(zip)).Convert();
        }
        else
        {
            new OdtConverter(doc, OdtPackage.FromFlat(Load(buffer))).Convert();
        }
        NewDocument.Finish(doc);
        return doc;
    }

    internal static XDocument Load(Stream stream)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true };
        using var reader = XmlReader.Create(stream, settings);
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }
}

/// <summary>The parts of an OpenDocument text document.</summary>
internal sealed class OdtPackage
{
    public XDocument? Content { get; set; }
    public XDocument? Styles { get; set; }
    public XDocument? Meta { get; set; }
    public Func<string, byte[]?> ReadEntry { get; set; } = _ => null;

    public static OdtPackage FromZip(ZipArchive zip)
    {
        XDocument? Part(string name)
        {
            var entry = zip.GetEntry(name);
            if (entry == null) return null;
            using var s = entry.Open();
            return OdtImporter.Load(s);
        }
        var entries = zip.Entries.ToDictionary(e => e.FullName, StringComparer.Ordinal);
        return new OdtPackage
        {
            Content = Part("content.xml"),
            Styles = Part("styles.xml"),
            Meta = Part("meta.xml"),
            ReadEntry = name =>
            {
                var key = name.TrimStart('.', '/');
                if (!entries.TryGetValue(key, out var e) && !entries.TryGetValue(Uri.UnescapeDataString(key), out e)) return null;
                if (e.Length > Pictures.MaxBytes) return null;
                using var s = e.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            },
        };
    }

    public static OdtPackage FromFlat(XDocument flat) => new() { Content = flat, Styles = flat, Meta = flat };
}

internal sealed class OdtConverter
{
    private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace StyleNs = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    private static readonly XNamespace TextNs = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace TableNs = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private static readonly XNamespace Draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private static readonly XNamespace Fo = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace MetaNs = "urn:oasis:names:tc:opendocument:xmlns:meta:1.0";
    private static readonly XNamespace Svg = "urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0";
    private static readonly XNamespace Xml = XNamespace.Xml;
    private static readonly XNamespace LoExt = "urn:org:documentfoundation:names:experimental:office:xmlns:loext:1.0";

    private readonly Document _doc;
    private readonly OdtPackage _package;
    private readonly StyleMap _styles;
    private readonly ListFactory _lists;
    private readonly Dictionary<(string Family, string Name), OdfStyle> _odfStyles = new();
    private readonly Dictionary<string, OdfStyle> _defaults = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XElement> _listStyles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _fontFaces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _abstracts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _lastNumByStyle = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _numByXmlId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _bookmarkIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Comment> _openComments = new(StringComparer.Ordinal);
    private int _nextBookmark;
    private long _maxImageWidth;

    public OdtConverter(Document doc, OdtPackage package)
    {
        _doc = doc;
        _package = package;
        _styles = new StyleMap(doc);
        _lists = new ListFactory(doc);
    }

    private sealed class OdfStyle
    {
        public string Name = "";
        public string Family = "";
        public string? DisplayName;
        public string? Parent;
        public bool Automatic;
        public XElement X = new("style");

        public XElement? Props(string local) => X.Element(StyleNs + local);
    }

    /// <summary>Character formatting while walking spans.</summary>
    private sealed record RunFmt
    {
        public bool? Bold { get; init; }
        public bool? Italic { get; init; }
        public string? Underline { get; init; }
        public bool? Strike { get; init; }
        public bool? DoubleStrike { get; init; }
        public string? Color { get; init; }
        public int? Size { get; init; }
        public int? SizeCs { get; init; }
        public string? Font { get; init; }
        public string? FontCs { get; init; }
        public string? VertAlign { get; init; }
        public string? Highlight { get; init; }
        public string? Shading { get; init; }
        public bool? SmallCaps { get; init; }
        public bool? Caps { get; init; }
        public string? Lang { get; init; }
        public string? LangBidi { get; init; }
        public bool Hidden { get; init; }
        public string? CharStyle { get; init; }
        public Hyperlink? Link { get; init; }
    }

    // ------------------------------------------------------------------ entry

    public void Convert()
    {
        _maxImageWidth = Content.TextWidth(_doc) * Pictures.EmuPerTwip;
        ReadStyles(_package.Styles);
        if (!ReferenceEquals(_package.Styles, _package.Content)) ReadStyles(_package.Content);
        ReadPageLayout();
        _maxImageWidth = Content.TextWidth(_doc) * Pictures.EmuPerTwip;
        ReadMeta();
        var text = _package.Content?.Root?.Element(Office + "body")?.Element(Office + "text");
        if (text == null)
        {
            _doc.Warnings.Add("The file has no text body.");
            return;
        }
        var blocks = new List<Block>();
        ConvertBlocks(text.Elements(), blocks);
        _doc.Body.Blocks.AddRange(blocks);
    }

    private void ReadStyles(XDocument? part)
    {
        var root = part?.Root;
        if (root == null) return;
        foreach (var face in root.Descendants(StyleNs + "font-face"))
        {
            var name = face.Attribute(StyleNs + "name")?.Value;
            var family = face.Attribute(Svg + "font-family")?.Value;
            if (name != null && family != null) _fontFaces[name] = family.Trim().Trim('\'', '"');
        }
        foreach (var container in new[] { root.Element(Office + "styles"), root.Element(Office + "automatic-styles") })
        {
            if (container == null) continue;
            var automatic = container.Name.LocalName == "automatic-styles";
            foreach (var s in container.Elements(StyleNs + "style"))
            {
                var style = new OdfStyle
                {
                    Name = s.Attribute(StyleNs + "name")?.Value ?? "",
                    Family = s.Attribute(StyleNs + "family")?.Value ?? "paragraph",
                    DisplayName = s.Attribute(StyleNs + "display-name")?.Value,
                    Parent = s.Attribute(StyleNs + "parent-style-name")?.Value,
                    Automatic = automatic,
                    X = s,
                };
                _odfStyles[(style.Family, style.Name)] = style;
            }
            foreach (var d in container.Elements(StyleNs + "default-style"))
            {
                var family = d.Attribute(StyleNs + "family")?.Value ?? "paragraph";
                _defaults[family] = new OdfStyle { Name = "", Family = family, X = d };
            }
            foreach (var l in container.Elements(TextNs + "list-style"))
                if (l.Attribute(StyleNs + "name")?.Value is { } listName) _listStyles[listName] = l;
        }
    }

    private void ReadPageLayout()
    {
        var root = _package.Styles?.Root;
        var master = root?.Element(Office + "master-styles")?.Elements(StyleNs + "master-page").FirstOrDefault();
        var layoutName = master?.Attribute(StyleNs + "page-layout-name")?.Value;
        if (layoutName == null) return;
        var layout = root!.Descendants(StyleNs + "page-layout").FirstOrDefault(l => l.Attribute(StyleNs + "name")?.Value == layoutName);
        var props = layout?.Element(StyleNs + "page-layout-properties");
        if (props == null) return;
        var s = _doc.Body.FinalSection;
        if (Twips(props.Attribute(Fo + "page-width")?.Value) is > 0 and var w) s.PageWidth = w;
        if (Twips(props.Attribute(Fo + "page-height")?.Value) is > 0 and var h) s.PageHeight = h;
        if (props.Attribute(StyleNs + "print-orientation")?.Value == "landscape") s.Landscape = true;
        s.SetMargins(Twips(props.Attribute(Fo + "margin-top")?.Value) ?? s.MarginTop, Twips(props.Attribute(Fo + "margin-right")?.Value) ?? s.MarginRight,
            Twips(props.Attribute(Fo + "margin-bottom")?.Value) ?? s.MarginBottom, Twips(props.Attribute(Fo + "margin-left")?.Value) ?? s.MarginLeft,
            s.HeaderDistance, s.FooterDistance, 0);
        var columns = props.Element(StyleNs + "columns");
        if (int.TryParse(columns?.Attribute(Fo + "column-count")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count > 1)
        {
            s.ColumnCount = count;
            if (Twips(columns!.Attribute(Fo + "column-gap")?.Value) is > 0 and var gap) s.ColumnSpace = gap;
        }
    }

    private void ReadMeta()
    {
        var meta = _package.Meta?.Root?.Element(Office + "meta");
        if (meta == null) return;
        string? Text(XName name) => meta.Element(name)?.Value.Trim() is { Length: > 0 } v ? v : null;
        var p = _doc.Properties;
        p.Title = Text(Dc + "title");
        p.Subject = Text(Dc + "subject");
        p.Description = Text(Dc + "description");
        var keywords = meta.Elements(MetaNs + "keyword").Select(k => k.Value.Trim()).Where(k => k.Length > 0).ToList();
        if (keywords.Count > 0) p.Keywords = string.Join(", ", keywords);
        p.Creator = Text(MetaNs + "initial-creator") ?? Text(Dc + "creator");
        p.LastModifiedBy = Text(Dc + "creator");
        p.Created = RevisionInfo.ParseDate(Text(MetaNs + "creation-date"));
        p.Modified = RevisionInfo.ParseDate(Text(Dc + "date"));
    }

    // ------------------------------------------------------------------ units and values

    /// <summary>An ODF length ("2.5cm", "1in", "12pt", "0.1665in") in twips.</summary>
    private static int? Twips(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var m = Regex.Match(value!.Trim(), @"^(-?[0-9]*\.?[0-9]+)\s*(cm|mm|in|inch|pt|pc|px|twip)?$", RegexOptions.CultureInvariant);
        if (!m.Success) return null;
        var n = double.Parse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
        var twips = m.Groups[2].Value switch
        {
            "cm" => n * 1440 / 2.54,
            "mm" => n * 1440 / 25.4,
            "in" or "inch" => n * 1440,
            "pt" => n * 20,
            "pc" => n * 240,
            "px" => n * 15,
            "twip" => n,
            _ => n * 20,
        };
        return (int)Math.Round(twips);
    }

    private static string? Hex(string? color)
    {
        if (color == null || !color.StartsWith("#", StringComparison.Ordinal) || color.Length != 7) return null;
        var hex = color.Substring(1).ToUpperInvariant();
        return hex.All(Uri.IsHexDigit) ? hex : null;
    }

    private static string DecodeName(string name) =>
        Regex.Replace(name, "_([0-9a-fA-F]{2})_", m => ((char)int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString());

    private OdfStyle? Find(string family, string? name) => name != null && _odfStyles.TryGetValue((family, name), out var s) ? s : null;

    /// <summary>The style and its parents (cycle-safe).</summary>
    private IEnumerable<OdfStyle> Chain(string family, string? name)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var s = Find(family, name);
        while (s != null && seen.Add(s.Name) && seen.Count < 32)
        {
            yield return s;
            s = Find(family, s.Parent);
        }
    }

    // ------------------------------------------------------------------ style mapping

    private static string? Canonical(OdfStyle s)
    {
        var name = (s.DisplayName ?? DecodeName(s.Name)).Trim().ToLowerInvariant();
        if (name.StartsWith("heading ", StringComparison.Ordinal) && int.TryParse(name.Substring(8), NumberStyles.Integer, CultureInfo.InvariantCulture, out var level) && level >= 1)
            return "Heading" + Math.Min(level, 9).ToString(CultureInfo.InvariantCulture);
        if (name.StartsWith("contents ", StringComparison.Ordinal) && int.TryParse(name.Substring(9), NumberStyles.Integer, CultureInfo.InvariantCulture, out var toc) && toc is >= 1 and <= 3)
            return "TOC" + toc.ToString(CultureInfo.InvariantCulture);
        return name switch
        {
            "heading" => "Heading1",
            "title" => "Title",
            "subtitle" => "Subtitle",
            "quotations" or "quote" or "block quotation" or "intense quote" => "Quote",
            "preformatted text" or "source code" or "code" => "Code",
            "text body" or "body text" or "standard" or "default paragraph style" or "default style" or "default" or "normal" or "first line indent" or "text" => "Normal",
            "list paragraph" or "list contents" => "ListParagraph",
            "caption" or "illustration" or "figure" or "table" or "drawing" => "Caption",
            "footnote" or "footnote text" => "FootnoteText",
            "endnote" or "endnote text" => "EndnoteText",
            "header" => "Header",
            "footer" => "Footer",
            _ => null,
        };
    }

    /// <summary>A paragraph's Inkwell style (from its first named style that maps) and its direct formatting (its automatic styles).</summary>
    private (string Canonical, List<OdfStyle> Automatic) ParagraphStyle(string? name)
    {
        var automatic = new List<OdfStyle>();
        foreach (var s in Chain("paragraph", name))
        {
            if (s.Automatic)
            {
                automatic.Add(s);
                continue;
            }
            if (Canonical(s) is { } mapped) return (mapped, automatic);
        }
        return ("Normal", automatic);
    }

    private RunFmt ApplyTextProps(RunFmt f, XElement? props)
    {
        if (props == null) return f;
        string? A(XNamespace ns, string local) => props.Attribute(ns + local)?.Value;
        var weight = A(Fo, "font-weight");
        if (weight != null) f = f with { Bold = weight == "bold" || (int.TryParse(weight, NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) && w >= 600) };
        var slant = A(Fo, "font-style");
        if (slant != null) f = f with { Italic = slant is "italic" or "oblique" };
        var underline = A(StyleNs, "text-underline-style");
        if (underline != null)
        {
            var type = A(StyleNs, "text-underline-type");
            var width = A(StyleNs, "text-underline-width");
            f = f with
            {
                Underline = underline switch
                {
                    "none" => "none",
                    "dotted" => "dotted",
                    "dash" => "dash",
                    "long-dash" => "dashLong",
                    "dot-dash" => "dotDash",
                    "dot-dot-dash" => "dotDotDash",
                    "wave" => type == "double" ? "wavyDouble" : "wave",
                    _ => type == "double" ? "double" : width is "bold" or "thick" ? "thick" : "single",
                },
            };
        }
        var strike = A(StyleNs, "text-line-through-style");
        if (strike != null)
        {
            var doubled = A(StyleNs, "text-line-through-type") == "double";
            f = f with { Strike = strike != "none" && !doubled, DoubleStrike = strike != "none" && doubled };
        }
        if (Hex(A(Fo, "color")) is { } color) f = f with { Color = color };
        if (A(Fo, "font-size") is { } size && !size.EndsWith("%", StringComparison.Ordinal) && Twips(size) is > 0 and var t) f = f with { Size = (int)Math.Round(t / 10.0) };
        if (A(StyleNs, "font-size-complex") is { } sizeCs && !sizeCs.EndsWith("%", StringComparison.Ordinal) && Twips(sizeCs) is > 0 and var tc) f = f with { SizeCs = (int)Math.Round(tc / 10.0) };
        var font = A(StyleNs, "font-name") is { } fn && _fontFaces.TryGetValue(fn, out var face) ? face : A(Fo, "font-family")?.Trim('\'', '"');
        if (font != null) f = f with { Font = font };
        var fontCs = A(StyleNs, "font-name-complex") is { } fc && _fontFaces.TryGetValue(fc, out var faceCs) ? faceCs : A(StyleNs, "font-family-complex")?.Trim('\'', '"');
        if (fontCs != null) f = f with { FontCs = fontCs };
        if (A(StyleNs, "text-position") is { } position)
        {
            var first = position.Trim().Split(' ')[0];
            if (first == "super") f = f with { VertAlign = "superscript" };
            else if (first == "sub") f = f with { VertAlign = "subscript" };
            else if (first.EndsWith("%", StringComparison.Ordinal) && double.TryParse(first.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                f = f with { VertAlign = pct > 0 ? "superscript" : pct < 0 ? "subscript" : null };
        }
        var background = A(Fo, "background-color");
        if (background != null)
        {
            var hex = Hex(background);
            var name = hex == null ? null : HighlightName(hex);
            f = f with { Highlight = name, Shading = name == null ? hex : null };
        }
        if (A(Fo, "font-variant") is { } variant) f = f with { SmallCaps = variant == "small-caps" };
        if (A(Fo, "text-transform") is { } transform) f = f with { Caps = transform == "uppercase" };
        if (A(Fo, "language") is { } lang && lang != "zxx" && lang != "none")
            f = f with { Lang = A(Fo, "country") is { } country && country != "none" ? lang + "-" + country : lang };
        if (A(StyleNs, "language-complex") is { } langCs && langCs != "zxx" && langCs != "none")
            f = f with { LangBidi = A(StyleNs, "country-complex") is { } countryCs && countryCs != "none" ? langCs + "-" + countryCs : langCs };
        if (A(TextNs, "display") == "none") f = f with { Hidden = true };
        return f;
    }

    private static string? HighlightName(string hex) => hex switch
    {
        "FFFF00" => "yellow",
        "00FF00" => "green",
        "00FFFF" => "cyan",
        "FF00FF" => "magenta",
        "0000FF" => "blue",
        "FF0000" => "red",
        "000080" => "darkBlue",
        "008080" => "darkCyan",
        "008000" => "darkGreen",
        "800080" => "darkMagenta",
        "800000" => "darkRed",
        "808000" => "darkYellow",
        "808080" => "darkGray",
        "C0C0C0" => "lightGray",
        _ => null,
    };

    /// <summary>The formatting of a text style: named styles that map (Emphasis, Strong Emphasis, Source Text…) or the properties of the chain.</summary>
    private RunFmt ApplyTextStyle(RunFmt f, string? name)
    {
        var chain = Chain("text", name).ToList();
        if (chain.Count == 0) return f;
        foreach (var s in chain)
        {
            if (s.Automatic) continue;
            var display = (s.DisplayName ?? DecodeName(s.Name)).Trim().ToLowerInvariant();
            switch (display)
            {
                case "emphasis":
                    f = f with { Italic = true };
                    break;
                case "strong emphasis":
                case "strong":
                    f = f with { Bold = true };
                    break;
                case "source text":
                case "teletype":
                case "example":
                    f = f with { CharStyle = "InlineCode" };
                    break;
                case "internet link":
                case "visited internet link":
                case "footnote symbol":
                case "footnote anchor":
                case "endnote symbol":
                case "endnote anchor":
                case "footnote characters":
                case "endnote characters":
                    break;
                default:
                    continue;
            }
            // A mapped named style: its own properties are the style's, not direct formatting.
            foreach (var a in chain.TakeWhile(x => x.Automatic)) f = ApplyTextProps(f, a.Props("text-properties"));
            return f;
        }
        foreach (var s in chain.AsEnumerable().Reverse()) f = ApplyTextProps(f, s.Props("text-properties"));
        return f;
    }

    private RunProps PropsFor(RunFmt f)
    {
        var r = new RunProps();
        if (f.Link != null) r.StyleId = _styles.Get("Hyperlink");
        else if (f.CharStyle != null) r.StyleId = _styles.Get(f.CharStyle);
        if (f.Bold != null) r.Bold = f.Bold;
        if (f.Italic != null) r.Italic = f.Italic;
        if (f.Underline != null && !(f.Link != null && f.Underline == "single")) r.Underline = f.Underline;
        if (f.Strike == true) r.Strike = true;
        if (f.DoubleStrike == true) r.DoubleStrike = true;
        if (f.Color != null && f.Link == null) r.Color = f.Color;
        if (f.Size != null)
        {
            r.Size = f.Size;
            r.SizeCs = f.SizeCs ?? f.Size;
        }
        if (f.Font != null) r.SetLatinFont(f.Font);
        if (f.FontCs != null) r.SetComplexFont(f.FontCs);
        if (f.VertAlign != null) r.VertAlign = f.VertAlign;
        if (f.Highlight != null) r.Highlight = f.Highlight;
        else if (f.Shading != null) r.ShadingFill = f.Shading;
        if (f.SmallCaps != null) r.SmallCaps = f.SmallCaps;
        if (f.Caps != null) r.Caps = f.Caps;
        if (f.Lang != null) r.Lang = f.Lang;
        if (f.LangBidi != null) r.LangBidi = f.LangBidi;
        if (f.Hidden) r.Vanish = true;
        return r;
    }

    private void ApplyParagraphProps(ParaProps p, XElement? props)
    {
        if (props == null) return;
        string? A(XNamespace ns, string local) => props.Attribute(ns + local)?.Value;
        var align = A(Fo, "text-align");
        if (align != null)
        {
            p.Justification = align switch
            {
                "center" => "center",
                "end" => "end",
                "right" => "right",
                "justify" => "both",
                "left" => "left",
                _ => "start",
            };
        }
        if (Twips(A(Fo, "margin-left")) is { } left) p.IndentStart = left;
        if (Twips(A(Fo, "margin-right")) is { } right) p.IndentEnd = right;
        if (Twips(A(Fo, "text-indent")) is { } indent) p.FirstLine = indent;
        if (Twips(A(Fo, "margin-top")) is { } top) p.SpacingBefore = top;
        if (Twips(A(Fo, "margin-bottom")) is { } bottom) p.SpacingAfter = bottom;
        if (A(Fo, "line-height") is { } lineHeight && lineHeight != "normal")
        {
            if (lineHeight.EndsWith("%", StringComparison.Ordinal) && double.TryParse(lineHeight.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
            {
                p.SpacingLine = (int)Math.Round(240 * pct / 100);
                p.SpacingLineRule = "auto";
            }
            else if (Twips(lineHeight) is > 0 and var exact)
            {
                p.SpacingLine = exact;
                p.SpacingLineRule = "exact";
            }
        }
        if (Twips(A(StyleNs, "line-height-at-least")) is > 0 and var atLeast)
        {
            p.SpacingLine = atLeast;
            p.SpacingLineRule = "atLeast";
        }
        if (A(Fo, "break-before") == "page") p.PageBreakBefore = true;
        if (A(Fo, "keep-with-next") == "always") p.KeepNext = true;
        if (A(Fo, "keep-together") == "always") p.KeepLines = true;
        if (A(StyleNs, "writing-mode") is { } mode) p.Bidi = mode.StartsWith("rl", StringComparison.Ordinal) ? true : null;
        if (Hex(A(Fo, "background-color")) is { } fill) p.ShadingFill = fill;
        foreach (var side in new[] { "top", "bottom", "left", "right" })
        {
            var value = A(Fo, "border-" + side) ?? A(Fo, "border");
            if (value != null && ParseBorder(value) is { } b) p.SetBorder(side, b);
        }
    }

    private static BorderDef? ParseBorder(string value)
    {
        var v = value.Trim().ToLowerInvariant();
        if (v is "none" or "hidden") return BorderDef.None;
        var parts = v.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        double width = 0.5;
        var style = "single";
        string? color = null;
        foreach (var part in parts)
        {
            if (part.StartsWith("#", StringComparison.Ordinal)) color = Hex(part);
            else if (Twips(part) is { } t) width = t / 20.0;
            else if (part is "double") style = "double";
            else if (part is "dotted") style = "dotted";
            else if (part is "dashed") style = "dashed";
            else if (part is "none") return BorderDef.None;
        }
        if (width <= 0) return BorderDef.None;
        return new BorderDef(style, MathEx.Clamp((int)Math.Round(width * 8), 2, 96), 0, color ?? "auto");
    }

    // ------------------------------------------------------------------ blocks

    private sealed class ItemState
    {
        public ItemState(int numId, int level, bool consumed)
        {
            NumId = numId;
            Level = level;
            Consumed = consumed;
        }

        public int NumId { get; }
        public int Level { get; }
        public bool Consumed { get; set; }
    }

    private sealed class ListInfo
    {
        public ListInfo(string? styleName, int numId)
        {
            StyleName = styleName;
            NumId = numId;
        }

        public string? StyleName { get; }
        public int NumId { get; set; }
    }

    private void ConvertBlocks(IEnumerable<XElement> elements, List<Block> output, ItemState? item = null)
    {
        foreach (var e in elements) ConvertBlock(e, output, item);
    }

    /// <summary>How deep blocks and inlines nest (lists in tables in notes…); content deeper than <see cref="MaxDepth"/> is left out.</summary>
    private int _depth;

    private const int MaxDepth = 200;

    private void ConvertBlock(XElement e, List<Block> output, ItemState? item)
    {
        if (_depth >= MaxDepth) return;
        _depth++;
        try
        {
            ConvertBlockCore(e, output, item);
        }
        finally
        {
            _depth--;
        }
    }

    private void ConvertBlockCore(XElement e, List<Block> output, ItemState? item)
    {
        if (e.Name == TextNs + "p" || e.Name == TextNs + "h")
        {
            var after = new List<Block>();
            var p = ConvertParagraph(e, after);
            if (item != null) ApplyItem(p, item);
            output.Add(p);
            output.AddRange(after);
            return;
        }
        if (e.Name == TextNs + "list")
        {
            ConvertList(e, output, item == null ? 0 : item.Level + 1, null);
            return;
        }
        if (e.Name == TextNs + "numbered-paragraph")
        {
            var level = int.TryParse(e.Attribute(TextNs + "level")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? MathEx.Clamp(l - 1, 0, 8) : 0;
            var styleName = e.Attribute(TextNs + "style-name")?.Value;
            var key = e.Attribute(TextNs + "list-id")?.Value ?? styleName ?? "";
            if (!_numByXmlId.TryGetValue("np:" + key, out var numId))
            {
                numId = NewNum(styleName, 0);
                _numByXmlId["np:" + key] = numId;
            }
            ConvertBlocks(e.Elements(), output, new ItemState(numId, level, false));
            return;
        }
        if (e.Name == TableNs + "table")
        {
            output.Add(ConvertTable(e));
            return;
        }
        if (e.Name == TextNs + "section" || e.Name == TextNs + "index-body" || e.Name == TextNs + "index-title")
        {
            ConvertBlocks(e.Elements(), output, item);
            return;
        }
        if (e.Name.Namespace == TextNs && (e.Name.LocalName.EndsWith("index", StringComparison.Ordinal) || e.Name.LocalName == "table-of-content" || e.Name.LocalName == "bibliography"))
        {
            if (e.Element(TextNs + "index-body") is { } body) ConvertBlocks(body.Elements(), output, item);
            return;
        }
        if (e.Name == Draw + "frame" || e.Name == Draw + "a")
        {
            // A frame anchored to the page: a paragraph of its own.
            var p = new Paragraph { Props = { StyleId = _styles.Get("Normal") } };
            var after = new List<Block>();
            ConvertInline(e, p.Inlines, new RunFmt(), after);
            if (p.Inlines.Count > 0) output.Add(p);
            output.AddRange(after);
        }
        // tracked changes, declarations, forms, soft page breaks: nothing to show.
    }

    private void ApplyItem(Paragraph p, ItemState item)
    {
        if (p.Props.StyleId == _styles.Get("Normal")) p.Props.StyleId = _styles.Get("ListParagraph");
        if (!item.Consumed)
        {
            p.Props.SetNumbering(item.NumId, item.Level);
            item.Consumed = true;
        }
        else
        {
            p.Props.IndentStart = ListFactory.LevelIndent(item.Level);
            p.Props.FirstLine = null;
        }
    }

    private Paragraph ConvertParagraph(XElement e, List<Block> after)
    {
        var p = new Paragraph();
        var styleName = e.Attribute(TextNs + "style-name")?.Value;
        var (canonical, automatic) = ParagraphStyle(styleName);
        if (e.Name == TextNs + "h" && canonical is not ("Title" or "Subtitle") && !canonical.StartsWith("Heading", StringComparison.Ordinal))
        {
            var level = int.TryParse(e.Attribute(TextNs + "outline-level")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : 1;
            canonical = "Heading" + MathEx.Clamp(level, 1, 9).ToString(CultureInfo.InvariantCulture);
        }
        p.Props.StyleId = _styles.Get(canonical);
        var run = new RunFmt();
        foreach (var s in automatic.AsEnumerable().Reverse())
        {
            ApplyParagraphProps(p.Props, s.Props("paragraph-properties"));
            run = ApplyTextProps(run, s.Props("text-properties"));
        }
        var context = new TextContext();
        foreach (var node in e.Nodes()) ConvertNode(node, p.Inlines, run, after, context);
        TrimEnd(p.Inlines);
        p.Normalize();
        return p;
    }

    /// <summary>White space state of the paragraph being read (ODF collapses spaces like XML/HTML text).</summary>
    private sealed class TextContext
    {
        public bool LastWasSpace = true;
    }

    /// <summary>White space at the end of a paragraph isn't shown (ODF's white-space rules).</summary>
    private static void TrimEnd(List<Inline> inlines)
    {
        for (var i = inlines.Count - 1; i >= 0; i--)
        {
            if (inlines[i] is not TextRun t) break;
            t.Text = t.Text.TrimEnd(' ');
            if (t.Text.Length == 0) inlines.RemoveAt(i);
            else break;
        }
    }

    private void ConvertNode(XNode node, List<Inline> inlines, RunFmt run, List<Block> after, TextContext context)
    {
        switch (node)
        {
            case XText text:
                AddText(inlines, text.Value, run, context);
                return;
            case XElement e:
                ConvertInline(e, inlines, run, after, context);
                return;
        }
    }

    private void AddText(List<Inline> inlines, string text, RunFmt run, TextContext context)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is ' ' or '\t' or '\n' or '\r')
            {
                if (!context.LastWasSpace) sb.Append(' ');
                context.LastWasSpace = true;
                continue;
            }
            sb.Append(c);
            context.LastWasSpace = false;
        }
        if (sb.Length > 0) Content.AppendText(inlines, Content.CleanText(sb.ToString()), PropsFor(run), run.Link);
    }

    private void Literal(List<Inline> inlines, string text, RunFmt run, TextContext context)
    {
        Content.AppendText(inlines, text, PropsFor(run), run.Link);
        context.LastWasSpace = text.EndsWith("\v", StringComparison.Ordinal);
    }

    private void ConvertInline(XElement e, List<Inline> inlines, RunFmt run, List<Block> after, TextContext? context = null)
    {
        if (_depth >= MaxDepth) return;
        _depth++;
        try
        {
            ConvertInlineCore(e, inlines, run, after, context ?? new TextContext { LastWasSpace = false });
        }
        finally
        {
            _depth--;
        }
    }

    private void ConvertInlineCore(XElement e, List<Inline> inlines, RunFmt run, List<Block> after, TextContext context)
    {
        var ns = e.Name.Namespace;
        var local = e.Name.LocalName;
        if (ns == TextNs)
        {
            switch (local)
            {
                case "s":
                {
                    var count = int.TryParse(e.Attribute(TextNs + "c")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? MathEx.Clamp(c, 1, 1000) : 1;
                    Literal(inlines, new string(' ', count), run, context);
                    context.LastWasSpace = false;
                    return;
                }
                case "tab":
                    Literal(inlines, "\t", run, context);
                    context.LastWasSpace = false;
                    return;
                case "line-break":
                    Literal(inlines, TextRun.LineBreak.ToString(), run, context);
                    context.LastWasSpace = true;
                    return;
                case "soft-page-break":
                    return;
                case "span":
                {
                    var styled = ApplyTextStyle(run, e.Attribute(TextNs + "style-name")?.Value);
                    foreach (var n in e.Nodes()) ConvertNode(n, inlines, styled, after, context);
                    return;
                }
                case "a":
                {
                    var link = MakeLink(e.Attribute(XLink + "href")?.Value, e.Attribute(Office + "title")?.Value);
                    var styled = link == null ? run : run with { Link = link };
                    foreach (var n in e.Nodes()) ConvertNode(n, inlines, styled, after, context);
                    return;
                }
                case "note":
                    Note(e, inlines);
                    context.LastWasSpace = false;
                    return;
                case "bookmark":
                case "bookmark-start":
                case "bookmark-end":
                    Bookmark(e, inlines, local);
                    return;
                case "change":
                case "change-start":
                case "change-end":
                case "toc-mark":
                case "toc-mark-start":
                case "toc-mark-end":
                case "alphabetical-index-mark":
                case "alphabetical-index-mark-start":
                case "alphabetical-index-mark-end":
                case "user-index-mark":
                case "reference-mark":
                case "reference-mark-start":
                case "reference-mark-end":
                case "hidden-text":
                case "hidden-paragraph":
                    return;
                case "ruby":
                    if (e.Element(TextNs + "ruby-base") is { } rubyBase)
                        foreach (var n in rubyBase.Nodes()) ConvertNode(n, inlines, run, after, context);
                    return;
            }
            // Fields and other text elements: their content is their text.
            foreach (var n in e.Nodes()) ConvertNode(n, inlines, run, after, context);
            return;
        }
        if (ns == Office)
        {
            if (local == "annotation") Annotation(e, inlines);
            else if (local == "annotation-end") AnnotationEnd(e, inlines);
            return;
        }
        if (ns == Draw)
        {
            if (local == "frame") Frame(e, inlines, run, after);
            else if (local == "a")
            {
                var link = MakeLink(e.Attribute(XLink + "href")?.Value, null);
                var styled = link == null ? run : run with { Link = link };
                foreach (var child in e.Elements()) ConvertInline(child, inlines, styled, after, context);
            }
            return;
        }
        // Unknown inline elements: keep their text.
        foreach (var n in e.Nodes()) ConvertNode(n, inlines, run, after, context);
    }

    private static Hyperlink? MakeLink(string? href, string? title)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        var h = href!.Trim();
        var link = new Hyperlink { Tooltip = string.IsNullOrEmpty(title) ? null : title };
        if (h.StartsWith("#", StringComparison.Ordinal))
        {
            link.Anchor = Uri.UnescapeDataString(h.Substring(1));
            if (link.Anchor.EndsWith("|outline", StringComparison.Ordinal) || link.Anchor.Length == 0) link.Anchor = link.Anchor.Split('|')[0];
            return link.Anchor.Length == 0 ? null : link;
        }
        link.Url = h;
        return link;
    }

    // ------------------------------------------------------------------ notes, bookmarks, comments

    private void Note(XElement e, List<Inline> inlines)
    {
        var kind = e.Attribute(TextNs + "note-class")?.Value == "endnote" ? NoteKind.Endnote : NoteKind.Footnote;
        var blocks = new List<Block>();
        if (e.Element(TextNs + "note-body") is { } body) ConvertBlocks(body.Elements(), blocks);
        var note = NoteFactory.Create(_doc, _styles, kind, blocks);
        inlines.Add(NoteFactory.Reference(_styles, kind, note.Id));
    }

    private void Bookmark(XElement e, List<Inline> inlines, string kind)
    {
        var name = e.Attribute(TextNs + "name")?.Value;
        if (string.IsNullOrEmpty(name)) return;
        if (kind == "bookmark-end")
        {
            if (_bookmarkIds.TryGetValue(name!, out var openId)) inlines.Add(new BookmarkMarker { Id = openId, IsStart = false });
            return;
        }
        if (_bookmarkIds.ContainsKey(name!)) return;
        var id = _nextBookmark++;
        _bookmarkIds[name!] = id;
        inlines.Add(new BookmarkMarker { Id = id, Name = name, IsStart = true });
        if (kind == "bookmark") inlines.Add(new BookmarkMarker { Id = id, IsStart = false });
    }

    private void Annotation(XElement e, List<Inline> inlines)
    {
        var comment = new Comment
        {
            Id = _doc.NextCommentId(),
            Author = e.Element(Dc + "creator")?.Value.Trim() ?? "",
            Date = RevisionInfo.ParseDate(e.Element(Dc + "date")?.Value),
            Done = e.Attribute(LoExt + "resolved")?.Value == "true",
        };
        comment.Initials = e.Element(MetaNs + "creator-initials")?.Value.Trim() is { Length: > 0 } initials ? initials : Content.Initials(comment.Author);
        var blocks = new List<Block>();
        ConvertBlocks(e.Elements().Where(x => x.Name == TextNs + "p" || x.Name == TextNs + "list"), blocks);
        var textStyle = _styles.Get("CommentText");
        foreach (var p in blocks.OfType<Paragraph>())
            if (p.Props.StyleId == _styles.Get("Normal")) p.Props.StyleId = textStyle;
        if (blocks.Count == 0 || blocks[0] is not Paragraph) blocks.Insert(0, new Paragraph { Props = { StyleId = textStyle } });
        ((Paragraph)blocks[0]).Inlines.Insert(0, new CommentMark());
        foreach (var b in blocks) comment.Blocks.Add(b);
        _doc.Comments.Add(comment);
        inlines.Add(new CommentMarker { Id = comment.Id, Kind = CommentMarkerKind.RangeStart });
        var name = e.Attribute(Office + "name")?.Value;
        if (name != null) _openComments[name] = comment;
        else
        {
            inlines.Add(new CommentMarker { Id = comment.Id, Kind = CommentMarkerKind.RangeEnd });
            inlines.Add(new CommentMarker { Id = comment.Id, Kind = CommentMarkerKind.Reference });
        }
    }

    private void AnnotationEnd(XElement e, List<Inline> inlines)
    {
        var name = e.Attribute(Office + "name")?.Value;
        if (name == null || !_openComments.TryGetValue(name, out var comment)) return;
        _openComments.Remove(name);
        inlines.Add(new CommentMarker { Id = comment.Id, Kind = CommentMarkerKind.RangeEnd });
        inlines.Add(new CommentMarker { Id = comment.Id, Kind = CommentMarkerKind.Reference });
    }

    // ------------------------------------------------------------------ frames

    private void Frame(XElement frame, List<Inline> inlines, RunFmt run, List<Block> after)
    {
        var width = Twips(frame.Attribute(Svg + "width")?.Value);
        var height = Twips(frame.Attribute(Svg + "height")?.Value);
        var title = frame.Element(Svg + "title")?.Value;
        var description = frame.Element(Svg + "desc")?.Value;
        if (frame.Element(Draw + "text-box") is { } textBox)
        {
            ConvertBlocks(textBox.Elements(), after);
            return;
        }
        foreach (var image in frame.Elements(Draw + "image"))
        {
            byte[]? data = null;
            if (image.Element(Office + "binary-data") is { } binary)
            {
                try
                {
                    data = System.Convert.FromBase64String(Regex.Replace(binary.Value, @"\s+", ""));
                }
                catch (FormatException)
                {
                    data = null;
                }
            }
            else if (image.Attribute(XLink + "href")?.Value is { } href && !href.Contains("://"))
            {
                data = _package.ReadEntry(href);
            }
            var drawing = data == null ? null : Pictures.Create(_doc, data, description, _maxImageWidth,
                width is > 0 ? width * Pictures.EmuPerTwip : null, height is > 0 ? height * Pictures.EmuPerTwip : null, title: title);
            if (drawing == null) continue;
            drawing.Link = run.Link;
            inlines.Add(drawing);
            return;
        }
        if (frame.Elements(Draw + "image").Any() || frame.Element(Draw + "object") != null || frame.Element(Draw + "object-ole") != null)
        {
            if (!_doc.Warnings.Contains("Some pictures or objects couldn't be read and were left out."))
                _doc.Warnings.Add("Some pictures or objects couldn't be read and were left out.");
        }
    }

    // ------------------------------------------------------------------ lists

    private int Abstract(string? styleName)
    {
        var key = styleName ?? "";
        if (_abstracts.TryGetValue(key, out var id)) return id;
        XElement definition;
        if (styleName != null && _listStyles.TryGetValue(styleName, out var listStyle))
        {
            var levels = new Dictionary<int, XElement>();
            var firstBullet = false;
            foreach (var level in listStyle.Elements())
            {
                if (!int.TryParse(level.Attribute(TextNs + "level")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) || l < 1 || l > 9) continue;
                var index = l - 1;
                var properties = level.Element(StyleNs + "list-level-properties");
                var alignment = properties?.Element(StyleNs + "list-level-label-alignment");
                var left = Twips(alignment?.Attribute(Fo + "margin-left")?.Value) ?? (Twips(properties?.Attribute(TextNs + "space-before")?.Value) is { } sb ? sb + (Twips(properties?.Attribute(TextNs + "min-label-width")?.Value) ?? 360) : null);
                var hanging = alignment != null ? -(Twips(alignment.Attribute(Fo + "text-indent")?.Value) ?? -360) : Twips(properties?.Attribute(TextNs + "min-label-width")?.Value);
                if (left is null or <= 0) left = ListFactory.LevelIndent(index);
                if (hanging is null or < 0) hanging = 360;
                XElement element;
                if (level.Name == TextNs + "list-level-style-number")
                {
                    var format = (level.Attribute(StyleNs + "num-format")?.Value ?? "1") switch
                    {
                        "a" => "lowerLetter",
                        "A" => "upperLetter",
                        "i" => "lowerRoman",
                        "I" => "upperRoman",
                        "" => "none",
                        "١, ٢, ٣, ..." => "hindiNumbers",
                        _ => "decimal",
                    };
                    var display = int.TryParse(level.Attribute(TextNs + "display-levels")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? MathEx.Clamp(d, 1, l) : 1;
                    var numbers = string.Join(".", Enumerable.Range(l - display + 1, display).Select(k => "%" + k.ToString(CultureInfo.InvariantCulture)));
                    var text = format == "none" ? "" : (level.Attribute(StyleNs + "num-prefix")?.Value ?? "") + numbers + (level.Attribute(StyleNs + "num-suffix")?.Value ?? "");
                    var start = int.TryParse(level.Attribute(TextNs + "start-value")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 1;
                    element = ListFactory.Level(index, format, text, start, left, hanging);
                }
                else
                {
                    var bullet = level.Attribute(TextNs + "bullet-char")?.Value ?? "•";
                    element = ListFactory.Level(index, "bullet", ListFactory.NormalizeBullet(bullet), 1, left, hanging);
                    if (index == 0) firstBullet = true;
                }
                levels[index] = element;
            }
            definition = ListFactory.Definition(levels, firstBullet);
        }
        else
        {
            definition = Numbering.StandardDefinition(true);
        }
        id = _doc.Numbering.AddAbstract(definition);
        _abstracts[key] = id;
        return id;
    }

    private int NewNum(string? styleName, int level, int? restartAt = null)
    {
        var abstractId = Abstract(styleName);
        var probe = _doc.Numbering.AbstractElement(abstractId)?.Elements(Ns.W + "lvl").FirstOrDefault(e => e.IntAttr("ilvl") == level);
        var ordered = probe?.Element(Ns.W + "numFmt").Attr("val") is { } fmt && fmt != "bullet" && fmt != "none";
        var start = restartAt ?? probe?.Element(Ns.W + "start").IntAttr("val") ?? 1;
        return ordered || restartAt != null ? _doc.Numbering.AddNum(abstractId, start, level) : _doc.Numbering.AddNum(abstractId);
    }

    private void ConvertList(XElement list, List<Block> output, int level, ListInfo? parent)
    {
        if (_depth >= MaxDepth) return;
        level = MathEx.Clamp(level, 0, 8);
        var styleName = list.Attribute(TextNs + "style-name")?.Value ?? parent?.StyleName;
        int numId;
        if (parent != null && styleName == parent.StyleName) numId = parent.NumId;
        else if (list.Attribute(TextNs + "continue-list")?.Value is { } continueId && _numByXmlId.TryGetValue(continueId, out var continued)) numId = continued;
        else if (list.Attribute(TextNs + "continue-numbering")?.Value == "true" && styleName != null && _lastNumByStyle.TryGetValue(styleName, out var last)) numId = last;
        else numId = NewNum(styleName, level);
        if (list.Attribute(Xml + "id")?.Value is { } xmlId) _numByXmlId[xmlId] = numId;
        if (parent == null && styleName != null) _lastNumByStyle[styleName] = numId;
        var info = new ListInfo(styleName, numId);
        foreach (var child in list.Elements())
        {
            var header = child.Name == TextNs + "list-header";
            if (!header && child.Name != TextNs + "list-item") continue;
            if (!header && int.TryParse(child.Attribute(TextNs + "start-value")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var restart))
            {
                info.NumId = NewNum(styleName, level, restart);
                if (parent == null && styleName != null) _lastNumByStyle[styleName] = info.NumId;
            }
            var item = new ItemState(info.NumId, level, header);
            foreach (var content in child.Elements())
            {
                if (content.Name == TextNs + "list")
                {
                    _depth++;
                    try
                    {
                        ConvertList(content, output, level + 1, info);
                    }
                    finally
                    {
                        _depth--;
                    }
                }
                else if (content.Name == TextNs + "p" || content.Name == TextNs + "h") ConvertBlock(content, output, item);
                else ConvertBlock(content, output, null);
            }
            if (!item.Consumed)
            {
                var empty = new Paragraph { Props = { StyleId = _styles.Get("Normal") } };
                ApplyItem(empty, item);
                output.Add(empty);
            }
        }
    }

    // ------------------------------------------------------------------ tables

    private sealed class SourceCell
    {
        public XElement? X;
        public int Row, Column, ColSpan = 1, RowSpan = 1;
        public bool Covered;
    }

    private Model.Table ConvertTable(XElement table)
    {
        var columns = new List<int?>();
        var rows = new List<(XElement Row, bool Header)>();
        void Collect(XElement parent, bool header)
        {
            foreach (var c in parent.Elements())
            {
                if (c.Name == TableNs + "table-column")
                {
                    var repeat = int.TryParse(c.Attribute(TableNs + "number-columns-repeated")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? MathEx.Clamp(r, 1, 1024) : 1;
                    var width = Twips(Find("table-column", c.Attribute(TableNs + "style-name")?.Value)?.Props("table-column-properties")?.Attribute(StyleNs + "column-width")?.Value);
                    for (var k = 0; k < repeat; k++) columns.Add(width);
                }
                else if (c.Name == TableNs + "table-row")
                {
                    var repeat = int.TryParse(c.Attribute(TableNs + "number-rows-repeated")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? MathEx.Clamp(r, 1, 1000) : 1;
                    for (var k = 0; k < repeat; k++) rows.Add((c, header));
                }
                else if (c.Name == TableNs + "table-header-rows") Collect(c, true);
                else if (c.Name == TableNs + "table-rows" || c.Name == TableNs + "table-row-group" || c.Name == TableNs + "table-columns" ||
                         c.Name == TableNs + "table-header-columns" || c.Name == TableNs + "table-column-group") Collect(c, header);
            }
        }
        Collect(table, false);

        var cells = new Dictionary<(int, int), SourceCell>();
        var columnCount = columns.Count;
        for (var r = 0; r < rows.Count; r++)
        {
            var col = 0;
            foreach (var c in rows[r].Row.Elements())
            {
                var covered = c.Name == TableNs + "covered-table-cell";
                if (!covered && c.Name != TableNs + "table-cell") continue;
                var repeat = int.TryParse(c.Attribute(TableNs + "number-columns-repeated")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rep) ? MathEx.Clamp(rep, 1, 1024) : 1;
                for (var k = 0; k < repeat; k++)
                {
                    if (!covered)
                    {
                        var cs = int.TryParse(c.Attribute(TableNs + "number-columns-spanned")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) ? MathEx.Clamp(a, 1, 63) : 1;
                        var rs = int.TryParse(c.Attribute(TableNs + "number-rows-spanned")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ? MathEx.Clamp(b, 1, rows.Count - r) : 1;
                        var cell = new SourceCell { X = c, Row = r, Column = col, ColSpan = cs, RowSpan = rs };
                        for (var dr = 0; dr < rs; dr++)
                            for (var dc = 0; dc < cs; dc++)
                                if (dr > 0 || dc > 0) cells[(r + dr, col + dc)] = new SourceCell { X = c, Row = r, Column = col, ColSpan = cs, RowSpan = rs, Covered = true };
                        cells[(r, col)] = cell;
                    }
                    else if (!cells.ContainsKey((r, col)))
                    {
                        cells[(r, col)] = new SourceCell { Row = r, Column = col, Covered = true };
                    }
                    col++;
                }
            }
            columnCount = Math.Max(columnCount, col);
        }
        foreach (var key in cells.Keys) columnCount = Math.Max(columnCount, key.Item2 + 1);
        columnCount = Math.Max(1, columnCount);

        var textWidth = Content.TextWidth(_doc);
        var known = columns.Take(columnCount).Where(w => w is > 0).Select(w => w!.Value).ToList();
        var fill = known.Count > 0 ? (int)known.Average() : textWidth / columnCount;
        var grid = Enumerable.Range(0, columnCount).Select(i => i < columns.Count && columns[i] is > 0 ? columns[i]!.Value : fill).ToList();
        var result = new Model.Table { Grid = grid };
        var tableProps = Find("table", table.Attribute(TableNs + "style-name")?.Value)?.Props("table-properties");
        if (Twips(tableProps?.Attribute(StyleNs + "width")?.Value) is > 0 and var tableWidth) result.Props.Width = new TableWidth("dxa", tableWidth);
        else result.Props.Width = new TableWidth("pct", 5000);
        if (tableProps?.Attribute(TableNs + "align")?.Value == "center") result.Props.Alignment = "center";

        var anyBorder = false;
        var headerRows = 0;
        while (headerRows < rows.Count && rows[headerRows].Header) headerRows++;
        for (var r = 0; r < rows.Count; r++)
        {
            var tr = new Model.TableRow();
            if (r < headerRows) tr.Props.IsHeader = true;
            for (var c = 0; c < columnCount;)
            {
                cells.TryGetValue((r, c), out var source);
                if (source == null || (source.Covered && source.X == null))
                {
                    tr.AddCell(Model.TableCell.Create(grid[c]));
                    c++;
                    continue;
                }
                var span = Math.Min(source.ColSpan, columnCount - source.Column);
                if (source.Covered && source.Row == r)
                {
                    // Covered by a horizontal span of a cell in this row: already part of it.
                    c++;
                    continue;
                }
                var cell = new Model.TableCell();
                cell.Props.Width = new TableWidth("dxa", grid.Skip(source.Column).Take(span).Sum());
                if (span > 1) cell.Props.GridSpan = span;
                if (source.Covered)
                {
                    cell.Props.VerticalMerge = "continue";
                    cell.Blocks.Add(new Paragraph());
                }
                else
                {
                    if (source.RowSpan > 1) cell.Props.VerticalMerge = "restart";
                    var blocks = new List<Block>();
                    ConvertBlocks(source.X!.Elements(), blocks);
                    if (blocks.Count == 0 || blocks[blocks.Count - 1] is not Paragraph) blocks.Add(new Paragraph());
                    cell.Blocks.AddRange(blocks);
                }
                var cellProps = Find("table-cell", source.X?.Attribute(TableNs + "style-name")?.Value)?.Props("table-cell-properties");
                if (cellProps != null)
                {
                    if (Hex(cellProps.Attribute(Fo + "background-color")?.Value) is { } shade) cell.Props.ShadingFill = shade;
                    var valign = cellProps.Attribute(StyleNs + "vertical-align")?.Value;
                    if (valign is "middle" or "center") cell.Props.VerticalAlignment = "center";
                    else if (valign == "bottom") cell.Props.VerticalAlignment = "bottom";
                    foreach (var side in new[] { "top", "bottom", "left", "right" })
                    {
                        var value = cellProps.Attribute(Fo + "border-" + side)?.Value ?? cellProps.Attribute(Fo + "border")?.Value;
                        if (value == null || ParseBorder(value) is not { } b) continue;
                        cell.Props.SetBorder(side, b);
                        if (b.IsVisible) anyBorder = true;
                    }
                }
                tr.AddCell(cell);
                c = source.Column + span;
            }
            result.AddRow(tr);
        }
        result.Props.StyleId = _styles.Get(anyBorder ? "TableGrid" : "InkwellTable");
        if (anyBorder)
        {
            // Cells without a border of their own show none (the Table Grid style would draw one).
            foreach (var row in result.Rows)
                foreach (var cell in row.Cells)
                    foreach (var side in new[] { "top", "bottom", "left", "right" })
                        if (cell.Props.Border(side) == null) cell.Props.SetBorder(side, BorderDef.None);
        }
        result.Props.Look = (headerRows > 0, false, false, false, true, false);
        if (result.Rows.Count == 0)
        {
            var tr = new Model.TableRow();
            tr.AddCell(Model.TableCell.Create(grid[0]));
            result.AddRow(tr);
        }
        return result;
    }
}
