using System.Globalization;
using System.Text;
using Inkwell.Core.Model;

namespace Inkwell.Core.Formats;

/// <summary>Options for <see cref="HtmlExporter"/>.</summary>
public sealed class HtmlExportOptions
{
    /// <summary>
    /// Put each element's formatting in its own style attribute instead of CSS classes (used for the clipboard, where
    /// the receiving program may ignore the style sheet).
    /// </summary>
    public bool InlineStyles { get; set; }

    /// <summary>Folder for picture files (created when needed); null embeds pictures as data: URIs.</summary>
    public string? ImageFolder { get; set; }

    /// <summary>How the HTML refers to <see cref="ImageFolder"/> (a relative path); the folder's name by default.</summary>
    public string? ImageFolderReference { get; set; }

    /// <summary>Refer to the picture files with absolute file:/// URLs (for the clipboard).</summary>
    public bool ImageFileUrls { get; set; }

    /// <summary>The page title; the document's title or first heading by default.</summary>
    public string? Title { get; set; }

    /// <summary>Write footnotes and endnotes as a notes section with links both ways.</summary>
    public bool IncludeNotes { get; set; } = true;
}

/// <summary>The HTML Inkwell puts on the clipboard for a selection.</summary>
public sealed class ClipboardHtml
{
    public ClipboardHtml(string fragment, string payload)
    {
        Fragment = fragment;
        Payload = payload;
        PayloadBytes = Encoding.UTF8.GetBytes(payload);
    }

    /// <summary>The HTML of the copied blocks (with inline styles).</summary>
    public string Fragment { get; }

    /// <summary>The CF_HTML text: header with byte offsets, then an HTML document with the fragment between StartFragment / EndFragment comments.</summary>
    public string Payload { get; }

    /// <summary>The CF_HTML payload as UTF-8 bytes (what goes on the clipboard as "HTML Format").</summary>
    public byte[] PayloadBytes { get; }
}

/// <summary>
/// HTML export: a standalone HTML5 page with a style sheet made from the document's styles (fonts with fallbacks,
/// sizes, colours, spacing, borders), headings with ids (bookmarks become anchors, so links inside the document
/// work), nested lists with the numbering's formats, tables (spans from merged cells, borders, shading, header
/// rows), pictures (data: URIs or files), footnotes and endnotes with links both ways, hyperlinks, right-to-left
/// paragraphs (dir="rtl"), languages (lang) and page breaks (CSS break-before). Also the HTML for copying blocks to
/// the clipboard (CF_HTML).
/// </summary>
public static class HtmlExporter
{
    /// <summary>The document as a complete HTML page.</summary>
    public static string Export(Document doc, HtmlExportOptions? options = null)
    {
        options ??= new HtmlExportOptions();
        var renderer = new HtmlRenderer(doc, options, wholeDocument: true);
        return renderer.Page(doc.Body.Blocks);
    }

    /// <summary>Saves the document as an HTML file (UTF-8).</summary>
    public static void ExportToFile(Document doc, string path, HtmlExportOptions? options = null) =>
        File.WriteAllText(path, Export(doc, options), new UTF8Encoding(false));

    /// <summary>The HTML of some blocks of a document (with inline styles unless <paramref name="options"/> says otherwise).</summary>
    public static string ExportFragment(Document doc, IEnumerable<Block> blocks, HtmlExportOptions? options = null)
    {
        options ??= new HtmlExportOptions { InlineStyles = true };
        var renderer = new HtmlRenderer(doc, options, wholeDocument: false);
        return renderer.Fragment(blocks);
    }

    /// <summary>
    /// The clipboard HTML (CF_HTML) for some blocks of a document. Pass copies of partly selected paragraphs, trimmed
    /// to the selection.
    /// </summary>
    public static ClipboardHtml ExportClipboard(Document doc, IEnumerable<Block> blocks, HtmlExportOptions? options = null)
    {
        var o = options ?? new HtmlExportOptions();
        var copy = new HtmlExportOptions
        {
            InlineStyles = true,
            ImageFolder = o.ImageFolder,
            ImageFolderReference = o.ImageFolderReference,
            ImageFileUrls = o.ImageFileUrls || o.ImageFolder != null,
            IncludeNotes = o.IncludeNotes,
            Title = o.Title,
        };
        var fragment = ExportFragment(doc, blocks, copy);
        return new ClipboardHtml(fragment, CfHtml.Build(fragment));
    }
}

/// <summary>CSS declarations in order (later values replace earlier ones).</summary>
internal sealed class Css
{
    private readonly List<KeyValuePair<string, string>> _items = new();

    public bool IsEmpty => _items.Count == 0;

    public string? this[string name] => _items.FirstOrDefault(i => i.Key == name).Value;

    public Css Set(string name, string value)
    {
        var i = _items.FindIndex(x => x.Key == name);
        if (i >= 0) _items[i] = new KeyValuePair<string, string>(name, value);
        else _items.Add(new KeyValuePair<string, string>(name, value));
        return this;
    }

    public Css Remove(string name)
    {
        _items.RemoveAll(x => x.Key == name);
        return this;
    }

    public Css Merge(Css? other)
    {
        if (other != null)
            foreach (var kv in other._items) Set(kv.Key, kv.Value);
        return this;
    }

    public Css Clone() => new Css().Merge(this);

    public override string ToString() => string.Join(";", _items.Select(kv => kv.Key + ":" + kv.Value));
}

/// <summary>Converts Word formatting (styles and direct properties) to CSS.</summary>
internal sealed class CssMapper
{
    private readonly Document _doc;
    private readonly Dictionary<string, Css> _paraStyles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Css> _charStyles = new(StringComparer.Ordinal);

    public CssMapper(Document doc)
    {
        _doc = doc;
    }

    /// <summary>Font slots and text decoration collected while applying run properties.</summary>
    private sealed class RunState
    {
        public string? Ascii;
        public string? Cs;
        public string? EastAsia;
        public string? Underline;
        public bool? Strike;
        public int? Size;
        public string? VertAlign;
    }

    /// <summary>Everything a paragraph style gives (document defaults + the style chain), flattened.</summary>
    public Css ParagraphStyle(string styleId)
    {
        if (_paraStyles.TryGetValue(styleId, out var cached)) return cached;
        var css = new Css().Set("margin-top", "0").Set("margin-bottom", "0");
        var run = new RunState();
        ApplyPara(css, _doc.Styles.DefaultParaProps);
        ApplyRun(css, _doc.Styles.DefaultRunProps, run);
        foreach (var s in _doc.Styles.Chain(styleId).AsEnumerable().Reverse())
        {
            if (s.ParaProps() is { } p) ApplyPara(css, p);
            if (s.RunProps() is { } r) ApplyRun(css, r, run);
        }
        FinishRun(css, run);
        _paraStyles[styleId] = css;
        return css;
    }

    /// <summary>The run formatting a character style gives (its chain).</summary>
    public Css CharacterStyle(string styleId)
    {
        if (_charStyles.TryGetValue(styleId, out var cached)) return cached;
        var css = new Css();
        var run = new RunState();
        foreach (var s in _doc.Styles.Chain(styleId).AsEnumerable().Reverse())
            if (s.RunProps() is { } r) ApplyRun(css, r, run);
        FinishRun(css, run);
        _charStyles[styleId] = css;
        return css;
    }

    /// <summary>The document defaults and the default paragraph style's run formatting (for the page's base font).</summary>
    public Css BaseRun()
    {
        var css = new Css();
        var run = new RunState();
        ApplyRun(css, _doc.Styles.DefaultRunProps, run);
        foreach (var s in _doc.Styles.Chain(_doc.Styles.DefaultParagraphStyleId).AsEnumerable().Reverse())
            if (s.RunProps() is { } r) ApplyRun(css, r, run);
        FinishRun(css, run);
        return css;
    }

    public Css DirectParagraph(ParaProps p)
    {
        var css = new Css();
        ApplyPara(css, p.FormattingOnly());
        return css;
    }

    public Css DirectRun(RunProps r)
    {
        var css = new Css();
        var run = new RunState();
        ApplyRun(css, r, run);
        FinishRun(css, run);
        return css;
    }

    /// <summary>Applies table style paragraph properties (spacing inside cells).</summary>
    public Css TableParagraph(string? tableStyleId)
    {
        var css = new Css();
        if (tableStyleId == null) return css;
        foreach (var s in _doc.Styles.Chain(tableStyleId).AsEnumerable().Reverse())
            if (s.ParaProps() is { } p) ApplyPara(css, p);
        return css;
    }

    public Css RunFromXml(System.Xml.Linq.XElement? rPr)
    {
        var css = new Css();
        if (rPr == null) return css;
        var run = new RunState();
        ApplyRun(css, new RunProps(rPr), run);
        FinishRun(css, run);
        return css;
    }

    private static string Pt(double twips) => ExportText.Invariant(twips / 20.0) + "pt";

    private void ApplyPara(Css css, ParaProps p)
    {
        if (p.SpacingBefore is { } before) css.Set("margin-top", Pt(before));
        if (p.BeforeAutospacing == true) css.Set("margin-top", "14pt");
        if (p.SpacingAfter is { } after) css.Set("margin-bottom", Pt(after));
        if (p.AfterAutospacing == true) css.Set("margin-bottom", "14pt");
        if (p.SpacingLine is { } line)
        {
            var rule = p.SpacingLineRule ?? "auto";
            if (rule == "auto") css.Set("line-height", ExportText.Invariant(Math.Max(0.5, line / 240.0 * 1.2)));
            else css.Set("line-height", Pt(line));
        }
        if (p.IndentStart is { } start) css.Set("margin-inline-start", Pt(start));
        if (p.IndentEnd is { } end) css.Set("margin-inline-end", Pt(end));
        if (p.FirstLine is { } first) css.Set("text-indent", Pt(first));
        if (p.Justification is { } jc)
        {
            css.Set("text-align", jc switch
            {
                "center" => "center",
                "right" or "end" => "end",
                "both" or "distribute" or "highKashida" or "mediumKashida" or "lowKashida" or "thaiDistribute" => "justify",
                _ => "start",
            });
        }
        foreach (var (side, cssSide) in new[] { ("top", "top"), ("bottom", "bottom"), ("left", "inline-start"), ("right", "inline-end") })
        {
            var b = p.Border(side) ?? p.Border(side == "left" ? "start" : side == "right" ? "end" : side);
            if (b == null) continue;
            if (b.Value.IsVisible)
            {
                css.Set("border-" + cssSide, Border(b.Value));
                css.Set("padding-" + cssSide, ExportText.Invariant(b.Value.Space) + "pt");
            }
            else
            {
                css.Set("border-" + cssSide, "none");
            }
        }
        if (p.ShadingFill is { } fill && fill != "auto" && IsHex(fill)) css.Set("background-color", "#" + fill);
        if (p.KeepNext == true) css.Set("break-after", "avoid");
        if (p.KeepLines == true) css.Set("break-inside", "avoid");
        if (p.PageBreakBefore == true) css.Set("break-before", "page");
        if (p.Bidi == true) css.Set("direction", "rtl");
        else if (p.Bidi == false) css.Set("direction", "ltr");
    }

    private void ApplyRun(Css css, RunProps r, RunState state)
    {
        state.Ascii = r.FontAscii ?? Theme(r.X.Element(Ns.W + "rFonts").Attr("asciiTheme")) ?? r.FontHAnsi ?? state.Ascii;
        state.Cs = r.FontCs ?? Theme(r.X.Element(Ns.W + "rFonts").Attr("cstheme")) ?? state.Cs;
        state.EastAsia = r.FontEastAsia ?? Theme(r.X.Element(Ns.W + "rFonts").Attr("eastAsiaTheme")) ?? state.EastAsia;
        if (r.Size is { } size)
        {
            state.Size = size;
            css.Set("font-size", ExportText.Invariant(size / 2.0) + "pt");
        }
        if (r.Bold is { } b) css.Set("font-weight", b ? "700" : "400");
        if (r.Italic is { } i) css.Set("font-style", i ? "italic" : "normal");
        if (r.Underline is { } u) state.Underline = u;
        if (r.Strike is { } s) state.Strike = s;
        if (r.DoubleStrike == true) state.Strike = true;
        if (r.Caps is { } caps) css.Set("text-transform", caps ? "uppercase" : "none");
        if (r.SmallCaps is { } sc) css.Set("font-variant", sc ? "small-caps" : "normal");
        if (r.Color is { } color && color != "auto" && IsHex(color)) css.Set("color", "#" + color);
        if (r.Highlight is { } hl && hl != "none" && HighlightColor(hl) is { } hlc) css.Set("background-color", hlc);
        else if (r.ShadingFill is { } fill && fill != "auto" && IsHex(fill)) css.Set("background-color", "#" + fill);
        if (r.VertAlign is { } va) state.VertAlign = va;
        if (r.Spacing is { } spacing && spacing != 0) css.Set("letter-spacing", Pt(spacing));
        if (r.Position is { } pos && pos != 0) css.Set("vertical-align", ExportText.Invariant(pos / 2.0) + "pt");
        if (r.Vanish == true) css.Set("display", "none");
    }

    private void FinishRun(Css css, RunState state)
    {
        var fonts = new List<string>();
        foreach (var f in new[] { state.Ascii, state.Cs, state.EastAsia })
            if (!string.IsNullOrWhiteSpace(f) && !fonts.Contains(f!, StringComparer.OrdinalIgnoreCase)) fonts.Add(f!);
        if (fonts.Count > 0) css.Set("font-family", FontStack(fonts));
        if (state.Underline != null || state.Strike != null)
        {
            var parts = new List<string>();
            if (state.Underline != null && state.Underline != "none") parts.Add("underline");
            if (state.Strike == true) parts.Add("line-through");
            css.Set("text-decoration", parts.Count == 0 ? "none" : string.Join(" ", parts) + UnderlineStyle(state.Underline));
        }
        if (state.VertAlign is "superscript" or "subscript")
        {
            css.Set("vertical-align", state.VertAlign == "superscript" ? "super" : "sub");
            css.Set("font-size", state.Size is { } size ? ExportText.Invariant(size / 3.0) + "pt" : "smaller");
        }
        else if (state.VertAlign == "baseline")
        {
            css.Set("vertical-align", "baseline");
        }
    }

    private static string UnderlineStyle(string? u) => u switch
    {
        "double" => " double",
        "dotted" or "dottedHeavy" => " dotted",
        "dash" or "dashedHeavy" or "dashLong" or "dashLongHeavy" or "dotDash" or "dotDotDash" => " dashed",
        "wave" or "wavyHeavy" or "wavyDouble" => " wavy",
        _ => "",
    };

    private string? Theme(string? reference) => reference == null ? null : _doc.Theme.ResolveFontReference(reference);

    public static bool IsHex(string s) => s.Length == 6 && s.All(Uri.IsHexDigit);

    public static string Border(BorderDef b)
    {
        var width = Math.Max(0.25, b.Size / 8.0);
        var style = b.Style switch
        {
            "double" or "triple" or "thinThickSmallGap" or "thickThinSmallGap" or "thinThickMediumGap" or "thickThinMediumGap" or "thinThickLargeGap" or "thickThinLargeGap" => "double",
            "dotted" => "dotted",
            "dashed" or "dashSmallGap" or "dotDash" or "dotDotDash" => "dashed",
            "inset" => "inset",
            "outset" => "outset",
            _ => "solid",
        };
        if (style == "double") width = Math.Max(width, 2.25);
        var color = b.Color is { } c && c != "auto" && IsHex(c) ? "#" + c : "currentColor";
        return ExportText.Invariant(width) + "pt " + style + " " + color;
    }

    public static string? HighlightColor(string name) => name switch
    {
        "yellow" => "#FFFF00",
        "green" => "#00FF00",
        "cyan" => "#00FFFF",
        "magenta" => "#FF00FF",
        "blue" => "#0000FF",
        "red" => "#FF0000",
        "darkBlue" => "#000080",
        "darkCyan" => "#008080",
        "darkGreen" => "#008000",
        "darkMagenta" => "#800080",
        "darkRed" => "#800000",
        "darkYellow" => "#808000",
        "darkGray" => "#808080",
        "lightGray" => "#C0C0C0",
        "black" => "#000000",
        "white" => "#FFFFFF",
        _ => null,
    };

    private static readonly string[] SerifHints =
    {
        "serif", "times", "georgia", "garamond", "cambria", "literata", "playfair", "palatino", "book antiqua", "constantia", "baskerville",
        "minion", "caslon", "nastaliq", "naskh", "mincho", "batang", "song", "ming", "amiri", "scheherazade", "traditional arabic",
    };

    /// <summary>A CSS font-family list with generic fallbacks chosen by the first font's kind.</summary>
    public static string FontStack(List<string> fonts)
    {
        var first = fonts[0].ToLowerInvariant();
        string fallback;
        if (first.Contains("mono") || first.Contains("code") || first.Contains("consolas") || first.Contains("courier")) fallback = "Consolas, \"Courier New\", monospace";
        else if (SerifHints.Any(h => first.Contains(h)) && !first.Contains("sans")) fallback = "Georgia, \"Times New Roman\", serif";
        else fallback = "\"Segoe UI\", Arial, sans-serif";
        return string.Join(", ", fonts.Select(f => "\"" + f.Replace("\"", "") + "\"")) + ", " + fallback;
    }
}

internal sealed class HtmlRenderer
{
    private readonly Document _doc;
    private readonly HtmlExportOptions _options;
    private readonly bool _wholeDocument;
    private readonly CssMapper _css;
    private readonly ListTracker _lists;
    private readonly HashSet<string> _usedParaStyles = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedCharStyles = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private readonly HashSet<string> _headingBookmarks = new(StringComparer.Ordinal);
    private readonly List<(Note Note, int Number)> _footnotes = new();
    private readonly List<(Note Note, int Number)> _endnotes = new();
    private readonly Dictionary<string, string> _imageFiles = new(StringComparer.Ordinal);
    private readonly HashSet<Block> _breakBefore = new(ReferenceEqualityComparer<Block>.Instance);
    private readonly string _defaultLang;
    private readonly string _defaultBidiLang;
    private string? _firstHeading;

    public HtmlRenderer(Document doc, HtmlExportOptions options, bool wholeDocument)
    {
        _doc = doc;
        _options = options;
        _wholeDocument = wholeDocument;
        _css = new CssMapper(doc);
        _lists = new ListTracker(doc);
        _defaultLang = doc.Styles.DefaultRunProps.Lang ?? "en-US";
        _defaultBidiLang = doc.Styles.DefaultRunProps.LangBidi ?? "ar-SA";
    }

    private bool Inline => _options.InlineStyles;

    // ------------------------------------------------------------------ documents

    public string Page(IEnumerable<Block> blocks)
    {
        var list = blocks.ToList();
        foreach (var s in _doc.Sections().Skip(1))
            if (s.Props.BreakType is not ("continuous" or "nextColumn") && s.Start < _doc.Body.Blocks.Count) _breakBefore.Add(_doc.Body.Blocks[s.Start]);
        ReserveBookmarks(list);
        var body = new StringBuilder();
        RenderBlocks(body, list, null);
        var notes = Notes();
        var title = _options.Title ?? _doc.Properties.Title ?? _firstHeading ?? "Document";
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html lang=\"").Append(Attr(_defaultLang)).Append("\">\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<meta name=\"generator\" content=\"Inkwell\">\n");
        if (!string.IsNullOrEmpty(_doc.Properties.Creator)) sb.Append("<meta name=\"author\" content=\"").Append(Attr(_doc.Properties.Creator!)).Append("\">\n");
        if (!string.IsNullOrEmpty(_doc.Properties.Description)) sb.Append("<meta name=\"description\" content=\"").Append(Attr(_doc.Properties.Description!)).Append("\">\n");
        sb.Append("<title>").Append(Text(title)).Append("</title>\n");
        if (!Inline) sb.Append("<style>\n").Append(StyleSheet()).Append("</style>\n");
        sb.Append("</head>\n<body>\n<article class=\"iw-doc\"");
        if (Inline) sb.Append(" style=\"").Append(Attr(PageCss().ToString())).Append('"');
        sb.Append(">\n").Append(body).Append(notes).Append("</article>\n</body>\n</html>\n");
        return sb.ToString();
    }

    public string Fragment(IEnumerable<Block> blocks)
    {
        var list = blocks.ToList();
        ReserveBookmarks(list);
        var body = new StringBuilder();
        RenderBlocks(body, list, null);
        body.Append(Notes());
        return body.ToString();
    }

    private Css PageCss()
    {
        var css = _css.BaseRun();
        var s = _doc.Body.FinalSection;
        css.Set("max-width", ExportText.Invariant((s.PageWidth - s.MarginLeft - s.MarginRight) / 20.0) + "pt");
        css.Set("margin", "0 auto");
        css.Set("padding", "40px 24px");
        return css;
    }

    private string StyleSheet()
    {
        var sb = new StringBuilder();
        var s = _doc.Body.FinalSection;
        sb.Append("@page { size: ").Append(ExportText.Invariant(s.PageWidth / 20.0)).Append("pt ").Append(ExportText.Invariant(s.PageHeight / 20.0))
            .Append("pt; margin: ").Append(ExportText.Invariant(s.MarginTop / 20.0)).Append("pt ").Append(ExportText.Invariant(s.MarginRight / 20.0)).Append("pt ")
            .Append(ExportText.Invariant(s.MarginBottom / 20.0)).Append("pt ").Append(ExportText.Invariant(s.MarginLeft / 20.0)).Append("pt; }\n");
        sb.Append("body { margin: 0; background: #ffffff; }\n");
        sb.Append(".iw-doc { ").Append(PageCss()).Append("; }\n");
        sb.Append(".iw-doc h1, .iw-doc h2, .iw-doc h3, .iw-doc h4, .iw-doc h5, .iw-doc h6, .iw-doc p, .iw-doc pre, .iw-doc li { margin: 0; font-size: 1em; font-weight: 400; }\n");
        sb.Append(".iw-doc img { max-width: 100%; height: auto; }\n");
        sb.Append(".iw-doc table { border-collapse: collapse; }\n");
        sb.Append(".iw-doc pre { white-space: pre-wrap; }\n");
        sb.Append(".iw-doc blockquote { margin: 0; }\n");
        sb.Append(".iw-doc a.iw-anchor { display: inline; }\n");
        sb.Append(".iw-tab { white-space: pre; }\n");
        sb.Append(".iw-noteref a, a.iw-backref { text-decoration: none; }\n");
        sb.Append(".iw-notes { margin-top: 2em; }\n");
        sb.Append(".iw-notes hr { border: 0; border-top: 1px solid #c8c8c8; width: 30%; margin: 0 0 1em 0; }\n");
        sb.Append("@media print { .iw-doc { max-width: none; padding: 0; } }\n");
        foreach (var id in _usedParaStyles.OrderBy(x => x, StringComparer.Ordinal))
            sb.Append(".p-").Append(ClassName(id)).Append(" { ").Append(_css.ParagraphStyle(id)).Append("; }\n");
        sb.Append(".iw-doc li { margin-inline-start: 0; text-indent: 0; }\n");
        foreach (var id in _usedCharStyles.OrderBy(x => x, StringComparer.Ordinal))
            sb.Append(".c-").Append(ClassName(id)).Append(" { ").Append(_css.CharacterStyle(id)).Append("; }\n");
        return sb.ToString();
    }

    private static string ClassName(string id)
    {
        var sb = new StringBuilder();
        foreach (var c in id) sb.Append(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' ? c : '_');
        return sb.ToString();
    }

    private void ReserveBookmarks(IEnumerable<Block> blocks)
    {
        foreach (var p in DocumentWalker.Paragraphs(blocks))
            foreach (var b in p.Inlines.OfType<BookmarkMarker>())
                if (b is { IsStart: true, Name: { } n }) _ids.Add(n);
    }

    // ------------------------------------------------------------------ blocks

    private sealed class OpenList
    {
        public int NumId;
        public int Level;
        public bool Ordered;
        public int NextValue;
    }

    /// <summary>Formatting a table cell gives its default-style paragraphs (conditional run formatting, table style spacing).</summary>
    private sealed class CellContext
    {
        public Css Para = new();
        public Css Run = new();
    }

    private static IEnumerable<Block> Flatten(IEnumerable<Block> blocks)
    {
        foreach (var b in blocks)
        {
            switch (b)
            {
                case SdtBlock s:
                    foreach (var inner in Flatten(s.Blocks)) yield return inner;
                    break;
                case FieldBlock f:
                    foreach (var inner in Flatten(f.Blocks)) yield return inner;
                    break;
                case OpaqueBlock { Display: { } display }:
                    foreach (var inner in Flatten(display)) yield return inner;
                    break;
                default:
                    yield return b;
                    break;
            }
        }
    }

    private void RenderBlocks(StringBuilder sb, IEnumerable<Block> blocks, CellContext? cell, string? appendToLast = null)
    {
        var items = Flatten(blocks).ToList();
        var lists = new List<OpenList>();
        var lastParagraph = appendToLast == null ? null : items.OfType<Paragraph>().LastOrDefault();

        void CloseLists(int toCount)
        {
            while (lists.Count > toCount)
            {
                var l = lists[lists.Count - 1];
                sb.Append("</li></").Append(l.Ordered ? "ol" : "ul").Append(">\n");
                lists.RemoveAt(lists.Count - 1);
            }
        }

        for (var i = 0; i < items.Count; i++)
        {
            var block = items[i];
            if (_breakBefore.Contains(block)) sb.Append("<div style=\"break-before:page\"></div>\n");
            switch (block)
            {
                case Table t:
                    CloseLists(0);
                    RenderTable(sb, t);
                    break;
                case MarkerBlock m:
                    foreach (var b in m.Markers.OfType<BookmarkMarker>()) Anchor(sb, b);
                    break;
                case Paragraph p:
                {
                    var extra = ReferenceEquals(p, lastParagraph) ? appendToLast : null;
                    var headingLevel = HeadingLevel(p);
                    var label = headingLevel > 0 ? null : _lists.Next(p);
                    if (headingLevel > 0)
                    {
                        var headingLabel = _lists.Next(p);
                        CloseLists(0);
                        RenderParagraph(sb, p, "h" + headingLevel.ToString(CultureInfo.InvariantCulture), cell, headingLabel?.Text, extra);
                        break;
                    }
                    if (label != null)
                    {
                        OpenItem(sb, lists, label, p, cell, extra, CloseLists);
                        break;
                    }
                    if (lists.Count > 0 && IsContinuation(p))
                    {
                        RenderParagraph(sb, p, "p", cell, null, extra, inList: true);
                        break;
                    }
                    CloseLists(0);
                    if (IsCode(p) && cell == null)
                    {
                        var code = new List<Paragraph> { p };
                        while (i + 1 < items.Count && items[i + 1] is Paragraph next && IsCode(next) && _lists.ListOf(next) == null && !_breakBefore.Contains(next))
                        {
                            code.Add(next);
                            i++;
                        }
                        foreach (var c in code.Skip(1)) _lists.Next(c);
                        RenderCode(sb, code, ReferenceEquals(code[code.Count - 1], lastParagraph) ? appendToLast : null);
                        break;
                    }
                    if (IsQuote(p) && cell == null)
                    {
                        var quote = new List<Paragraph> { p };
                        while (i + 1 < items.Count && items[i + 1] is Paragraph next && IsQuote(next) && _lists.ListOf(next) == null && !_breakBefore.Contains(next) && HeadingLevel(next) == 0)
                        {
                            quote.Add(next);
                            i++;
                        }
                        sb.Append(Inline ? "<blockquote style=\"margin:0\">\n" : "<blockquote>\n");
                        foreach (var q in quote)
                        {
                            if (!ReferenceEquals(q, p)) _lists.Next(q);
                            RenderParagraph(sb, q, "p", null, null, ReferenceEquals(q, lastParagraph) ? appendToLast : null);
                        }
                        sb.Append("</blockquote>\n");
                        break;
                    }
                    if (Content.IsDivider(p))
                    {
                        var border = p.Props.Border("bottom")!.Value;
                        sb.Append("<hr style=\"border:0;border-top:").Append(Attr(CssMapper.Border(border))).Append(";margin:0.5em 0\">\n");
                        break;
                    }
                    if (IsPageBreakOnly(p))
                    {
                        sb.Append("<div style=\"break-before:page\"></div>\n");
                        break;
                    }
                    RenderParagraph(sb, p, "p", cell, null, extra);
                    break;
                }
            }
        }
        CloseLists(0);
    }

    private void OpenItem(StringBuilder sb, List<OpenList> lists, ListLabel label, Paragraph p, CellContext? cell, string? extra, Action<int> closeLists)
    {
        var level = label.Level;
        var ordered = label.IsOrdered;
        while (lists.Count > 0 && lists[lists.Count - 1].Level > level) closeLists(lists.Count - 1);
        if (lists.Count > 0 && lists[lists.Count - 1].Level == level && (lists[lists.Count - 1].NumId != label.NumId || lists[lists.Count - 1].Ordered != ordered))
            closeLists(lists.Count - 1);
        OpenList current;
        if (lists.Count > 0 && lists[lists.Count - 1].Level == level)
        {
            current = lists[lists.Count - 1];
            sb.Append("</li>\n");
        }
        else
        {
            var parentLevel = lists.Count > 0 ? lists[lists.Count - 1].Level : -1;
            var indent = LevelLeft(label.NumId, level) - (parentLevel >= 0 ? LevelLeft(label.NumId, parentLevel) : 0);
            var css = new Css().Set("margin-top", "0").Set("margin-bottom", "0").Set("padding-inline-start", ExportText.Invariant(Math.Max(0, indent) / 20.0) + "pt");
            var type = ListStyleType(label);
            if (type != null) css.Set("list-style-type", type);
            sb.Append(ordered ? "<ol" : "<ul");
            if (ordered && type != "none" && label.Value != 1) sb.Append(" start=\"").Append(label.Value.ToString(CultureInfo.InvariantCulture)).Append('"');
            sb.Append(" style=\"").Append(Attr(css.ToString())).Append("\">\n");
            current = new OpenList { NumId = label.NumId, Level = level, Ordered = ordered, NextValue = label.Value };
            lists.Add(current);
        }
        var explicitLabel = ListStyleType(label) == "none" && label.Text.Length > 0 ? label.Text : null;
        var value = ordered && label.Value != current.NextValue && explicitLabel == null ? label.Value : (int?)null;
        current.NextValue = label.Value + 1;
        RenderParagraph(sb, p, "li", cell, explicitLabel, extra, inList: true, value: value, close: false);
    }

    private int LevelLeft(int numId, int level)
    {
        var def = _doc.Numbering.Level(numId, level);
        var ind = def?.ParaPropsXml?.Element(Ns.W + "ind");
        return ind.IntAttr("start") ?? ind.IntAttr("left") ?? ListFactory.LevelIndent(level);
    }

    /// <summary>The CSS list-style-type for a level; "none" when the label can't be shown natively (it is then written as text).</summary>
    private static string? ListStyleType(ListLabel label)
    {
        var def = label.Definition;
        if (label.IsPlain) return "none";
        if (label.IsBullet)
        {
            return label.Text switch
            {
                "•" or "●" => "disc",
                "◦" or "○" => "circle",
                "▪" or "■" => "square",
                _ => "\"" + label.Text.Replace("\"", "") + " \"",
            };
        }
        var simple = def.Text == "%" + (label.Level + 1).ToString(CultureInfo.InvariantCulture) + ".";
        if (!simple) return "none";
        return def.Format switch
        {
            "decimal" => "decimal",
            "decimalZero" => "decimal-leading-zero",
            "lowerLetter" => "lower-alpha",
            "upperLetter" => "upper-alpha",
            "lowerRoman" => "lower-roman",
            "upperRoman" => "upper-roman",
            _ => "none",
        };
    }

    private bool IsContinuation(Paragraph p)
    {
        if (p.Props.NumId != null) return false;
        var styleId = p.Props.StyleId;
        return styleId != null && _doc.Styles.Chain(styleId).Any(s => s.Id == "ListParagraph" || s.Name.Equals("List Paragraph", StringComparison.OrdinalIgnoreCase));
    }

    private int HeadingLevel(Paragraph p)
    {
        var styleId = p.Props.StyleId ?? _doc.Styles.DefaultParagraphStyleId;
        if (styleId == "Title" || StyleChain.NameOf(_doc.Styles, styleId) == "title") return 1;
        return Math.Min(6, StyleChain.HeadingLevel(_doc, p));
    }

    private bool IsCode(Paragraph p)
    {
        var styleId = p.Props.StyleId;
        return styleId != null && _doc.Styles.Chain(styleId).Any(s => s.Id == "Code" || s.Name.Equals("Code", StringComparison.OrdinalIgnoreCase) || s.Name.Equals("HTML Preformatted", StringComparison.OrdinalIgnoreCase));
    }

    private bool IsQuote(Paragraph p)
    {
        var styleId = p.Props.StyleId;
        return styleId != null && _doc.Styles.Chain(styleId).Any(s => s.Id is "Quote" or "IntenseQuote" || s.Name.Equals("Quote", StringComparison.OrdinalIgnoreCase) || s.Name.Equals("Intense Quote", StringComparison.OrdinalIgnoreCase));
    }

    private bool IsCodeStyle(string styleId) =>
        _doc.Styles.Chain(styleId).Any(s => s.Id == "InlineCode" || s.Name.Equals("Inline Code", StringComparison.OrdinalIgnoreCase) || s.Name.Equals("HTML Code", StringComparison.OrdinalIgnoreCase));

    private static bool IsPageBreakOnly(Paragraph p) =>
        p.Inlines.Count > 0 && p.Inlines.All(i => i.Length == 0 || i is TextRun t && t.Text.Length > 0 && t.Text.All(c => c == TextRun.PageBreak)) &&
        p.Inlines.OfType<TextRun>().Any(t => t.Text.Contains(TextRun.PageBreak));

    private void RenderParagraph(StringBuilder sb, Paragraph p, string tag, CellContext? cell, string? labelText, string? extra, bool inList = false, int? value = null, bool close = true)
    {
        var styleId = p.Props.StyleId ?? _doc.Styles.DefaultParagraphStyleId;
        var isDefaultStyle = styleId == _doc.Styles.DefaultParagraphStyleId;
        var direct = _css.DirectParagraph(p.Props);
        if (inList)
        {
            direct.Remove("margin-inline-start").Remove("text-indent");
        }
        Css? style;
        if (Inline)
        {
            style = new Css().Merge(_css.ParagraphStyle(styleId));
            if (inList) style.Remove("margin-inline-start").Remove("text-indent");
            if (tag[0] == 'h') style.Set("font-weight", style["font-weight"] ?? "400");
        }
        else
        {
            _usedParaStyles.Add(styleId);
            style = new Css();
        }
        if (cell != null && isDefaultStyle)
        {
            style.Merge(cell.Para);
            style.Merge(cell.Run);
        }
        style.Merge(direct);
        var rtl = IsBidi(p, styleId);
        if (rtl || style["direction"] == "ltr") style.Remove("direction");
        sb.Append('<').Append(tag);
        if (!Inline) sb.Append(" class=\"p-").Append(ClassName(styleId)).Append('"');
        if (tag[0] == 'h')
        {
            var id = HeadingId(p);
            if (id != null) sb.Append(" id=\"").Append(Attr(id)).Append('"');
        }
        if (value != null) sb.Append(" value=\"").Append(value.Value.ToString(CultureInfo.InvariantCulture)).Append('"');
        if (rtl) sb.Append(" dir=\"rtl\"");
        if (!style.IsEmpty) sb.Append(" style=\"").Append(Attr(style.ToString())).Append('"');
        sb.Append('>');
        var start = sb.Length;
        if (labelText != null) sb.Append(Text(labelText)).Append(' ');
        RenderInlines(sb, p.Inlines, p, null);
        if (extra != null) sb.Append(extra);
        if (sb.Length == start) sb.Append("<br>");
        if (close) sb.Append("</").Append(tag).Append(">\n");
        if (tag[0] == 'h' && _firstHeading == null) _firstHeading = p.PlainText().Trim();
    }

    private bool IsBidi(Paragraph p, string styleId) => p.Props.Bidi ?? StyleChain.ParaValue(_doc.Styles, styleId, x => x.Bidi) ?? false;

    private string? HeadingId(Paragraph p)
    {
        var bookmark = p.Inlines.OfType<BookmarkMarker>().FirstOrDefault(b => b.IsStart && b.Name != null && b.Name != "_GoBack");
        if (bookmark?.Name != null)
        {
            _headingBookmarks.Add(bookmark.Name);
            return bookmark.Name;
        }
        var slug = Slug(p.PlainText());
        var id = slug;
        for (var n = 2; _ids.Contains(id); n++) id = slug + "-" + n.ToString(CultureInfo.InvariantCulture);
        _ids.Add(id);
        return id;
    }

    private static string Slug(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if ((c == ' ' || c == '-' || c == '_') && sb.Length > 0 && sb[sb.Length - 1] != '-') sb.Append('-');
        }
        var s = sb.ToString().Trim('-');
        return s.Length == 0 ? "section" : s;
    }

    private void RenderCode(StringBuilder sb, List<Paragraph> lines, string? extra)
    {
        var styleId = lines[0].Props.StyleId ?? "Code";
        var style = Inline ? new Css().Merge(_css.ParagraphStyle(styleId)) : new Css();
        if (!Inline) _usedParaStyles.Add(styleId);
        style.Merge(_css.DirectParagraph(lines[0].Props).Remove("margin-top").Remove("margin-bottom"));
        if (Inline) style.Set("white-space", "pre-wrap");
        sb.Append("<pre");
        if (!Inline) sb.Append(" class=\"p-").Append(ClassName(styleId)).Append('"');
        if (!style.IsEmpty) sb.Append(" style=\"").Append(Attr(style.ToString())).Append('"');
        sb.Append('>');
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            RenderInlines(sb, lines[i].Inlines, lines[i], null, preformatted: true);
        }
        if (extra != null) sb.Append(extra);
        sb.Append("</pre>\n");
    }

    // ------------------------------------------------------------------ tables

    private void RenderTable(StringBuilder sb, Table t)
    {
        var styleId = t.Props.StyleId;
        var tableCss = new Css().Set("border-collapse", "collapse");
        var width = t.Props.Width;
        if (width.Type == "pct" && width.Value > 0) tableCss.Set("width", ExportText.Invariant(width.Value / 50.0) + "%");
        else if (width.Type == "dxa" && width.Value > 0) tableCss.Set("width", ExportText.Invariant(width.Value / 20.0) + "pt");
        var jc = t.Props.Alignment ?? TableStyleValue(styleId, x => x.Alignment);
        if (jc == "center")
        {
            tableCss.Set("margin-left", "auto");
            tableCss.Set("margin-right", "auto");
        }
        else if (jc is "right" or "end")
        {
            tableCss.Set("margin-left", "auto");
        }
        else if (t.Props.Indent is { Type: "dxa" } ind && ind.Value != 0)
        {
            tableCss.Set("margin-inline-start", ExportText.Invariant(ind.Value / 20.0) + "pt");
        }
        if (t.Props.BidiVisual) tableCss.Set("direction", "rtl");
        sb.Append("<table style=\"").Append(Attr(tableCss.ToString())).Append("\">\n");
        if (t.Grid.Count > 0)
        {
            sb.Append("<colgroup>");
            foreach (var g in t.Grid) sb.Append("<col style=\"width:").Append(ExportText.Invariant(g / 20.0)).Append("pt\">");
            sb.Append("</colgroup>\n");
        }
        var look = t.Props.Look;
        var tableBorders = new Dictionary<string, BorderDef?>();
        foreach (var side in new[] { "top", "left", "bottom", "right", "insideH", "insideV" })
            tableBorders[side] = t.Props.Border(side) ?? TableStyleBorder(styleId, side);
        var firstRow = ConditionalOf(styleId, "firstRow");
        var band1 = ConditionalOf(styleId, "band1Horz");
        var band2 = ConditionalOf(styleId, "band2Horz");
        var tableParaCss = _css.TableParagraph(styleId);
        var baseRunCss = TableStyleRun(styleId);
        var columns = t.ColumnCount;
        var headerRows = 0;
        while (headerRows < t.Rows.Count && t.Rows[headerRows].Props.IsHeader) headerRows++;
        var rowCount = t.Rows.Count;
        for (var r = 0; r < rowCount; r++)
        {
            var row = t.Rows[r];
            if (r == 0 && headerRows > 0) sb.Append("<thead>\n");
            if (r == headerRows) sb.Append("<tbody>\n");
            sb.Append("<tr>");
            var isFirst = r == 0 && look.FirstRow;
            var dataIndex = r - (look.FirstRow ? 1 : 0);
            System.Xml.Linq.XElement? conditional = isFirst ? firstRow : look.BandedRows && dataIndex >= 0 ? (dataIndex % 2 == 0 ? band1 : band2) : null;
            var col = row.GridBefore;
            if (row.GridBefore > 0) sb.Append("<td colspan=\"").Append(row.GridBefore.ToString(CultureInfo.InvariantCulture)).Append("\" style=\"border:none\"></td>");
            foreach (var cell in row.Cells)
            {
                var span = cell.Props.GridSpan;
                if (cell.Props.VerticalMerge == "continue")
                {
                    col += span;
                    continue;
                }
                var rowSpan = 1;
                if (cell.Props.VerticalMerge == "restart")
                {
                    for (var below = r + 1; below < rowCount; below++)
                    {
                        var other = Table.CellAtColumn(t.Rows[below], col);
                        if (other?.Props.VerticalMerge != "continue") break;
                        rowSpan++;
                    }
                }
                var tag = r < headerRows ? "th" : "td";
                var css = new Css().Set("vertical-align", cell.Props.VerticalAlignment switch { "center" => "middle", "bottom" => "bottom", _ => "top" });
                if (r < headerRows) css.Set("text-align", "start").Set("font-weight", "inherit");
                var lastRow = r + rowSpan - 1 == rowCount - 1;
                var lastCol = col + span >= columns;
                BorderDef? CellBorder(string side, string outer, string inner, bool edge)
                {
                    var own = cell.Props.Border(side);
                    if (own != null) return own;
                    if (conditional?.Element(Ns.W + "tcPr")?.Element(Ns.W + "tcBorders")?.Element(Ns.W + side) is { } cb) return BorderDef.Parse(cb);
                    return edge ? tableBorders[outer] : tableBorders[inner];
                }
                var top = CellBorder("top", "top", "insideH", r == 0);
                var bottom = CellBorder("bottom", "bottom", "insideH", lastRow);
                var left = CellBorder("left", "left", "insideV", col == 0);
                var right = CellBorder("right", "right", "insideV", lastCol);
                foreach (var (name, b) in new[] { ("top", top), ("bottom", bottom), ("left", left), ("right", right) })
                    if (b is { } bd && bd.IsVisible) css.Set("border-" + name, CssMapper.Border(bd));
                var fill = cell.Props.ShadingFill;
                if (fill == null || fill == "auto") fill = conditional?.Element(Ns.W + "tcPr")?.Element(Ns.W + "shd").Attr("fill");
                if (fill != null && fill != "auto" && CssMapper.IsHex(fill)) css.Set("background-color", "#" + fill);
                css.Set("padding", Padding(t, cell, styleId));
                if (cell.Props.Width is { Type: "dxa", Value: > 0 } cw) css.Set("width", ExportText.Invariant(cw.Value / 20.0) + "pt");
                sb.Append('<').Append(tag);
                if (span > 1) sb.Append(" colspan=\"").Append(span.ToString(CultureInfo.InvariantCulture)).Append('"');
                if (rowSpan > 1) sb.Append(" rowspan=\"").Append(rowSpan.ToString(CultureInfo.InvariantCulture)).Append('"');
                sb.Append(" style=\"").Append(Attr(css.ToString())).Append("\">");
                var context = new CellContext { Para = tableParaCss.Clone(), Run = baseRunCss.Clone().Merge(_css.RunFromXml(conditional?.Element(Ns.W + "rPr"))) };
                if (cell.Blocks.Count == 0) sb.Append("<br>");
                else RenderBlocks(sb, cell.Blocks, context);
                sb.Append("</").Append(tag).Append('>');
                col += span;
            }
            sb.Append("</tr>\n");
            if (r == headerRows - 1) sb.Append("</thead>\n");
        }
        if (rowCount > headerRows) sb.Append("</tbody>\n");
        sb.Append("</table>\n");
    }

    private string? TableStyleValue(string? styleId, Func<TableProps, string?> get)
    {
        foreach (var s in _doc.Styles.Chain(styleId))
            if (s.TableProps() is { } tp && get(tp) is { } v) return v;
        return null;
    }

    private BorderDef? TableStyleBorder(string? styleId, string side)
    {
        foreach (var s in _doc.Styles.Chain(styleId))
            if (s.TableProps() is { } tp && tp.Border(side) is { } b) return b;
        return null;
    }

    private Css TableStyleRun(string? styleId)
    {
        var css = new Css();
        foreach (var s in _doc.Styles.Chain(styleId).AsEnumerable().Reverse())
            if (s.RunProps() is { } r) css.Merge(_css.DirectRun(r));
        return css;
    }

    private System.Xml.Linq.XElement? ConditionalOf(string? styleId, string type)
    {
        foreach (var s in _doc.Styles.Chain(styleId))
            foreach (var (t, x) in s.Conditional)
                if (t == type) return x;
        return null;
    }

    private string Padding(Table t, TableCell cell, string? styleId)
    {
        int Side(string side, int fallback)
        {
            if (cell.Props.Margin(side) is { } m) return m;
            if (t.Props.CellMargin(side) is { } tm) return tm;
            foreach (var s in _doc.Styles.Chain(styleId))
                if (s.TableProps()?.CellMargin(side) is { } sm) return sm;
            if (_doc.Styles.DefaultOf("table")?.TableProps()?.CellMargin(side) is { } dm) return dm;
            return fallback;
        }
        return string.Join(" ", new[] { Side("top", 0), Side("right", 108), Side("bottom", 0), Side("left", 108) }.Select(v => ExportText.Invariant(v / 20.0) + "pt"));
    }

    // ------------------------------------------------------------------ inlines

    private void RenderInlines(StringBuilder sb, List<Inline> inlines, Paragraph p, Hyperlink? outerLink, bool preformatted = false)
    {
        var afterMark = false;
        var atStart = true;
        var i = 0;
        while (i < inlines.Count)
        {
            var inline = inlines[i];
            var link = inline.Link ?? outerLink;
            if (link != null && !ReferenceEquals(link, outerLink))
            {
                var group = new List<Inline>();
                while (i < inlines.Count && ReferenceEquals(inlines[i].Link, link)) group.Add(inlines[i++]);
                var href = Href(link);
                sb.Append("<a");
                if (href != null) sb.Append(" href=\"").Append(Attr(href)).Append('"');
                if (!string.IsNullOrEmpty(link.Tooltip)) sb.Append(" title=\"").Append(Attr(link.Tooltip!)).Append('"');
                sb.Append('>');
                RenderInlines(sb, group, p, link, preformatted);
                sb.Append("</a>");
                atStart = false;
                continue;
            }
            i++;
            if (inline.Deleted != null) continue;
            switch (inline)
            {
                case NoteMark:
                    afterMark = true;
                    continue;
                case TextRun t:
                {
                    var text = t.Text;
                    if (afterMark) text = text.TrimStart(' ');
                    afterMark = false;
                    if (text.Length == 0) continue;
                    if (t.Props.Vanish == true) continue;
                    RenderRun(sb, t.Props, text, preformatted, atStart);
                    atStart = false;
                    break;
                }
                case FieldInline f:
                {
                    Hyperlink? fieldLink = null;
                    if (f.Kind == FieldKind.Hyperlink && link == null)
                    {
                        var code = FieldCode.Parse(f.Code);
                        fieldLink = new Hyperlink { Url = code.FirstArgument, Anchor = code.SwitchValue("\\l"), Tooltip = code.SwitchValue("\\o") };
                    }
                    if (fieldLink != null && (fieldLink.Url != null || fieldLink.Anchor != null))
                    {
                        var copies = f.Result.Select(r => { var c = r.Clone(); c.Link = fieldLink; return c; }).ToList();
                        RenderInlines(sb, copies, p, null, preformatted);
                    }
                    else
                    {
                        RenderInlines(sb, f.Result, p, link, preformatted);
                    }
                    atStart = false;
                    break;
                }
                case SymbolChar s:
                    RenderRun(sb, s.Props, s.Char.ToString(), preformatted, false, s.Font);
                    atStart = false;
                    break;
                case DrawingObject d:
                    RenderPicture(sb, d);
                    atStart = false;
                    break;
                case NoteReference r:
                    RenderNoteReference(sb, r);
                    atStart = false;
                    break;
                case BookmarkMarker b:
                    Anchor(sb, b);
                    break;
                case ClearBreak:
                    sb.Append("<br style=\"clear:both\">");
                    break;
                case OpaqueInline o when !o.IsMarker && o.Display.Length > 0:
                    RenderRun(sb, o.Props, o.Display, preformatted, atStart);
                    atStart = false;
                    break;
            }
        }
    }

    private void Anchor(StringBuilder sb, BookmarkMarker b)
    {
        if (!b.IsStart || b.Name == null || b.Name == "_GoBack" || _headingBookmarks.Contains(b.Name)) return;
        sb.Append("<a id=\"").Append(Attr(b.Name)).Append("\" class=\"iw-anchor\"></a>");
    }

    private static string? Href(Hyperlink link)
    {
        if (link.Url != null) return link.Anchor != null ? link.Url + "#" + link.Anchor : link.Url;
        return link.Anchor != null ? "#" + link.Anchor : null;
    }

    private void RenderRun(StringBuilder sb, RunProps props, string text, bool preformatted, bool atStart, string? symbolFont = null)
    {
        var css = new Css();
        string? cls = null;
        if (props.StyleId is { } rs && _doc.Styles.Get(rs) != null)
        {
            if (Inline) css.Merge(_css.CharacterStyle(rs));
            else
            {
                _usedCharStyles.Add(rs);
                cls = "c-" + ClassName(rs);
            }
        }
        var direct = props.Clone();
        direct.StyleId = null;
        var vert = direct.VertAlign;
        direct.VertAlign = null;
        css.Merge(_css.DirectRun(direct));
        if (symbolFont != null) css.Set("font-family", "\"" + symbolFont + "\"");
        var lang = LangOf(props, text);
        var isCode = props.StyleId is { } codeStyle && IsCodeStyle(codeStyle);
        var wrapTag = isCode ? "code" : "span";
        var wrap = isCode || cls != null || !css.IsEmpty || lang != null;
        var tag = vert == "superscript" ? "sup" : vert == "subscript" ? "sub" : null;
        if (tag != null) sb.Append('<').Append(tag).Append('>');
        if (wrap)
        {
            sb.Append('<').Append(wrapTag);
            if (cls != null) sb.Append(" class=\"").Append(cls).Append('"');
            if (lang != null) sb.Append(" lang=\"").Append(Attr(lang)).Append('"');
            if (!css.IsEmpty) sb.Append(" style=\"").Append(Attr(css.ToString())).Append('"');
            sb.Append('>');
        }
        AppendText(sb, text, preformatted, atStart);
        if (wrap) sb.Append("</").Append(wrapTag).Append('>');
        if (tag != null) sb.Append("</").Append(tag).Append('>');
    }

    private string? LangOf(RunProps props, string text)
    {
        var complex = props.Rtl == true || text.Any(c => c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= 'ﻼ');
        var lang = complex ? props.LangBidi ?? props.Lang : props.Lang;
        if (lang == null) return null;
        return string.Equals(lang, complex ? _defaultBidiLang : _defaultLang, StringComparison.OrdinalIgnoreCase) ? null : lang;
    }

    private void AppendText(StringBuilder sb, string text, bool preformatted, bool atStart)
    {
        var prevSpace = atStart;
        foreach (var c in text)
        {
            switch (c)
            {
                case '&':
                    sb.Append("&amp;");
                    break;
                case '<':
                    sb.Append("&lt;");
                    break;
                case '>':
                    sb.Append("&gt;");
                    break;
                case TextRun.LineBreak:
                case TextRun.ColumnBreak:
                    sb.Append(preformatted ? "\n" : "<br>");
                    prevSpace = true;
                    continue;
                case TextRun.PageBreak:
                    sb.Append(preformatted ? "\n" : "<span style=\"display:block;break-before:page\"></span>");
                    prevSpace = true;
                    continue;
                case '\t':
                    sb.Append(preformatted ? "\t" : "<span class=\"iw-tab\" style=\"white-space:pre\">\t</span>");
                    break;
                case TextRun.SoftHyphen:
                    sb.Append("&shy;");
                    break;
                case TextRun.NoBreakHyphen:
                    sb.Append("&#8209;");
                    break;
                case ' ':
                    if (!preformatted && prevSpace)
                    {
                        sb.Append("&nbsp;");
                        prevSpace = false;
                        continue;
                    }
                    sb.Append(' ');
                    prevSpace = true;
                    continue;
                default:
                    sb.Append(c);
                    break;
            }
            prevSpace = false;
        }
    }

    private void RenderPicture(StringBuilder sb, DrawingObject d)
    {
        var media = d.IsPicture ? _doc.Media.Get(d.MediaId) : null;
        if (media == null)
        {
            if (!string.IsNullOrEmpty(d.AltText)) sb.Append("<span class=\"iw-object\">[").Append(Text(d.AltText!)).Append("]</span>");
            return;
        }
        var w = Math.Max(1, (int)Math.Round(d.Width / 9525.0));
        var h = Math.Max(1, (int)Math.Round(d.Height / 9525.0));
        sb.Append("<img src=\"").Append(Attr(ImageSource(media))).Append("\" alt=\"").Append(Attr(d.AltText ?? "")).Append('"');
        if (!string.IsNullOrEmpty(d.Title)) sb.Append(" title=\"").Append(Attr(d.Title!)).Append('"');
        sb.Append(" width=\"").Append(w.ToString(CultureInfo.InvariantCulture)).Append("\" height=\"").Append(h.ToString(CultureInfo.InvariantCulture)).Append('"');
        var css = new Css();
        if (d.IsFloating)
        {
            var align = d.Float?.Horizontal.Align;
            if (d.Wrap is WrapMode.Square or WrapMode.Tight)
            {
                css.Set("float", align is "right" or "outside" ? "right" : "left");
                css.Set("margin", align is "right" or "outside" ? "0 0 6pt 9pt" : "0 9pt 6pt 0");
            }
            else if (d.Wrap == WrapMode.TopAndBottom)
            {
                css.Set("display", "block");
                if (align == "center") css.Set("margin", "0 auto");
            }
        }
        if (Inline) css.Set("max-width", "100%");
        if (!css.IsEmpty) sb.Append(" style=\"").Append(Attr(css.ToString())).Append('"');
        sb.Append('>');
    }

    private string ImageSource(MediaItem media)
    {
        if (_options.ImageFolder == null) return "data:" + media.ContentType + ";base64," + Convert.ToBase64String(media.Data);
        if (!_imageFiles.TryGetValue(media.Id, out var file))
        {
            var ext = media.Extension == "jpeg" ? "jpg" : media.Extension;
            file = "image" + (_imageFiles.Count + 1).ToString(CultureInfo.InvariantCulture) + "." + ext;
            Directory.CreateDirectory(_options.ImageFolder);
            File.WriteAllBytes(Path.Combine(_options.ImageFolder, file), media.Data);
            _imageFiles[media.Id] = file;
        }
        var full = Path.Combine(_options.ImageFolder, file);
        if (_options.ImageFileUrls) return new Uri(Path.GetFullPath(full)).AbsoluteUri;
        var folder = _options.ImageFolderReference ?? Path.GetFileName(_options.ImageFolder.TrimEnd('\\', '/'));
        return (string.IsNullOrEmpty(folder) ? "" : Uri.EscapeDataString(folder).Replace("%2F", "/") + "/") + file;
    }

    // ------------------------------------------------------------------ notes

    private void RenderNoteReference(StringBuilder sb, NoteReference r)
    {
        var note = _doc.FindNote(r.Kind, r.Id);
        if (note == null || !_options.IncludeNotes) return;
        var list = r.Kind == NoteKind.Footnote ? _footnotes : _endnotes;
        var number = list.Count + 1;
        list.Add((note, number));
        var prefix = r.Kind == NoteKind.Footnote ? "fn" : "en";
        var label = r.Kind == NoteKind.Footnote
            ? NumberFormats.Format(_doc.Settings.FootnoteStart + number - 1, _doc.Settings.FootnoteFormat)
            : NumberFormats.Format(_doc.Settings.EndnoteStart + number - 1, _doc.Settings.EndnoteFormat);
        var n = number.ToString(CultureInfo.InvariantCulture);
        sb.Append("<sup class=\"iw-noteref\"><a href=\"#").Append(prefix).Append('-').Append(n).Append("\" id=\"").Append(prefix).Append("ref-").Append(n)
            .Append("\" role=\"doc-noteref\"").Append(Inline ? " style=\"text-decoration:none\"" : "").Append('>').Append(Text(label)).Append("</a></sup>");
    }

    private string Notes()
    {
        if (!_options.IncludeNotes || (_footnotes.Count == 0 && _endnotes.Count == 0)) return "";
        var sb = new StringBuilder();
        sb.Append("<section class=\"iw-notes\" role=\"doc-endnotes\"").Append(Inline ? " style=\"margin-top:2em\"" : "").Append(">\n");
        sb.Append(Inline ? "<hr style=\"border:0;border-top:1px solid #c8c8c8;width:30%;margin:0 0 1em 0\">\n" : "<hr>\n");
        RenderNoteList(sb, _footnotes, "fn", _doc.Settings.FootnoteFormat, _doc.Settings.FootnoteStart);
        RenderNoteList(sb, _endnotes, "en", _doc.Settings.EndnoteFormat, _doc.Settings.EndnoteStart);
        sb.Append("</section>\n");
        return sb.ToString();
    }

    private void RenderNoteList(StringBuilder sb, List<(Note Note, int Number)> notes, string prefix, string format, int start)
    {
        if (notes.Count == 0) return;
        var type = format switch
        {
            "lowerRoman" => "lower-roman",
            "upperRoman" => "upper-roman",
            "lowerLetter" => "lower-alpha",
            "upperLetter" => "upper-alpha",
            _ => "decimal",
        };
        sb.Append("<ol class=\"iw-").Append(prefix == "fn" ? "footnotes" : "endnotes").Append("\" style=\"list-style-type:").Append(type).Append('"');
        if (start != 1) sb.Append(" start=\"").Append(start.ToString(CultureInfo.InvariantCulture)).Append('"');
        sb.Append(">\n");
        // Notes can reference more notes while being rendered: iterate by index.
        for (var k = 0; k < notes.Count; k++)
        {
            var (note, number) = notes[k];
            var n = number.ToString(CultureInfo.InvariantCulture);
            sb.Append("<li id=\"").Append(prefix).Append('-').Append(n).Append("\">");
            var back = "<a href=\"#" + prefix + "ref-" + n + "\" class=\"iw-backref\" role=\"doc-backlink\"" + (Inline ? " style=\"text-decoration:none\"" : "") + "> ↩</a>";
            RenderBlocks(sb, note.Blocks, null, back);
            sb.Append("</li>\n");
        }
        sb.Append("</ol>\n");
    }

    // ------------------------------------------------------------------ escaping

    public static string Text(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    public static string Attr(string s) => s.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");
}
