using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Inkwell.Core.Model;
using Inkwell.Core.Templates;

namespace Inkwell.Core.Formats;

/// <summary>Options for <see cref="HtmlImporter"/>.</summary>
public sealed class HtmlImportOptions
{
    /// <summary>
    /// False (the default, "match destination"): keep the structure (headings, lists, tables, quotes, code) in the
    /// target's styles, plus bold, italic, underline, strikethrough, superscript, subscript, links and inline code;
    /// drop fonts, sizes, colours and backgrounds. True ("keep source formatting"): also keep fonts, sizes, colours,
    /// highlights, alignment, cell shading and borders.
    /// </summary>
    public bool KeepSourceFormatting { get; set; }

    /// <summary>The address of the page the HTML came from (CF_HTML's SourceURL): relative links are resolved against it.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Folder relative picture paths are read from (for HTML files opened from disk).</summary>
    public string? BaseFolder { get; set; }
}

/// <summary>
/// HTML to document conversion, for pasting (and for opening .html files). Understands plain browser HTML, Word's
/// clipboard HTML (mso-list paragraphs with &lt;![if !supportLists]&gt; markers become real lists, MsoTitle /
/// MsoQuote… classes map to styles, mso- properties are ignored, Word footnotes become footnotes) and Google Docs
/// HTML (the &lt;b id="docs-internal-guid-…" style="font-weight:normal"&gt; wrapper, span styles for bold and italic,
/// aria-level list levels). Pictures are taken from data: URIs and local files only; web pictures are counted and
/// left out (see <see cref="PasteResult.SkippedImageUrls"/>).
/// </summary>
public static class HtmlImporter
{
    /// <summary>Blocks for pasting HTML into <paramref name="target"/> (its styles; lists, pictures and notes are added to it).</summary>
    public static PasteResult Import(string html, Document target, HtmlImportOptions? options = null)
    {
        var result = new PasteResult();
        var converter = new HtmlConverter(target, options ?? new HtmlImportOptions(), result);
        result.Blocks.AddRange(converter.Convert(HtmlParser.Parse(html)));
        return result;
    }

    /// <summary>Blocks for pasting the clipboard's CF_HTML text (only the fragment that was copied, read in its context).</summary>
    public static PasteResult ImportClipboard(string cfHtml, Document target, HtmlImportOptions? options = null) =>
        ImportParts(CfHtml.Parse(cfHtml), target, options);

    /// <summary>Blocks for pasting the clipboard's CF_HTML bytes (UTF-8).</summary>
    public static PasteResult ImportClipboard(byte[] cfHtml, Document target, HtmlImportOptions? options = null) =>
        ImportParts(CfHtml.Parse(cfHtml), target, options);

    private static PasteResult ImportParts(CfHtmlParts parts, Document target, HtmlImportOptions? options)
    {
        var o = options ?? new HtmlImportOptions();
        if (o.BaseUrl == null && parts.SourceUrl != null)
            o = new HtmlImportOptions { KeepSourceFormatting = o.KeepSourceFormatting, BaseFolder = o.BaseFolder, BaseUrl = parts.SourceUrl };
        return Import(parts.Html, target, o);
    }

    /// <summary>Opens an HTML file as a new document (match-destination conversion into the design's styles).</summary>
    public static Document ImportFile(string path, DocumentDesign? design = null)
    {
        var bytes = File.ReadAllBytes(path);
        return ImportDocument(DecodeHtml(bytes), Path.GetDirectoryName(Path.GetFullPath(path)), design);
    }

    /// <summary>A new document from an HTML page (title → document title).</summary>
    public static Document ImportDocument(string html, string? baseFolder = null, DocumentDesign? design = null)
    {
        var doc = NewDocument.Create(design);
        var root = HtmlParser.Parse(html);
        var converter = new HtmlConverter(doc, new HtmlImportOptions { BaseFolder = baseFolder }, new PasteResult());
        doc.Body.Blocks.AddRange(converter.Convert(root));
        var title = root.Descendants().FirstOrDefault(n => n.Name == "title")?.InnerText().Trim();
        if (!string.IsNullOrEmpty(title)) doc.Properties.Title = title;
        NewDocument.Finish(doc);
        return doc;
    }

    /// <summary>The text of an HTML file: byte order mark, then &lt;meta charset&gt;, then UTF-8 / ANSI detection.</summary>
    public static string DecodeHtml(byte[] bytes)
    {
        var hasBom = bytes.Length >= 2 && ((bytes[0] == 0xEF && bytes.Length >= 3 && bytes[1] == 0xBB && bytes[2] == 0xBF) || (bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF));
        if (!hasBom)
        {
            var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 2048));
            var m = Regex.Match(head, @"<meta[^>]+charset\s*=\s*[""']?\s*([A-Za-z0-9_\-:.]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (m.Success)
            {
                try
                {
                    TextEncodings.Get(1252);
                    var enc = Encoding.GetEncoding(m.Groups[1].Value);
                    if (enc.CodePage != 65001 && enc.CodePage != 1200 && enc.CodePage != 1201) return enc.GetString(bytes);
                }
                catch (ArgumentException)
                {
                    // Unknown charset name: detect.
                }
            }
        }
        return TextImporter.Decode(bytes, out _);
    }
}

internal sealed class HtmlConverter
{
    private readonly Document _doc;
    private readonly HtmlImportOptions _options;
    private readonly PasteResult _result;
    private readonly bool _keep;
    private readonly StyleMap _styles;
    private readonly ListFactory _lists;
    private readonly CssSheet _sheet = new();
    private readonly Dictionary<string, HtmlNode> _ids = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WordList> _wordLists = new(StringComparer.OrdinalIgnoreCase);
    private readonly long _maxImageWidth;

    private List<Block> _out = new();
    private Stack<ItemState> _items = new();
    private Paragraph? _para;
    private bool _lineStart;
    private bool _pendingSpace;
    private RunProps? _pendingSpaceProps;
    private Hyperlink? _pendingSpaceLink;
    private bool _inFragment = true;
    private (int NumId, int Level)? _wordNumbering;
    private int _emitted;
    private bool _skipPreNewline;
    private int _noteDepth;

    public HtmlConverter(Document doc, HtmlImportOptions options, PasteResult result)
    {
        _doc = doc;
        _options = options;
        _result = result;
        _keep = options.KeepSourceFormatting;
        _styles = new StyleMap(doc);
        _lists = new ListFactory(doc);
        _maxImageWidth = Content.TextWidth(doc) * Pictures.EmuPerTwip;
    }

    // ------------------------------------------------------------------ context

    private sealed record BlockCtx
    {
        public string Style { get; init; } = "Normal";
        public bool InQuote { get; init; }
        public bool Pre { get; init; }
        public string? Align { get; init; }
        public bool Rtl { get; init; }
        public int ListDepth { get; init; } = -1;
        public LazyList? List { get; init; }
    }

    private sealed record RunCtx
    {
        public bool? Bold { get; init; }
        public bool? Italic { get; init; }
        public string? Underline { get; init; }
        public bool? Strike { get; init; }
        public string? VertAlign { get; init; }
        public bool Code { get; init; }
        public Hyperlink? Link { get; init; }
        public string? Font { get; init; }
        public double? SizePt { get; init; }
        public string? Color { get; init; }
        public string? Background { get; init; }
        public string? Lang { get; init; }
        public bool PreserveSpace { get; init; }
    }

    /// <summary>A list whose instance is created when its first item gets a paragraph (so lists outside the copied fragment leave nothing behind).</summary>
    private sealed class LazyList
    {
        public ListKind Kind;
        public int Level;
        public int Start = 1;
        public string? Format;
        public string? Bullet;
        public int? NumId;

        public int Get(ListFactory factory) => NumId ??= factory.NewList(Kind, Level, Start, Format, Bullet);
    }

    private sealed class ItemState
    {
        public ItemState(LazyList list, int level)
        {
            List = list;
            Level = level;
        }

        public LazyList List { get; }
        public int Level { get; }
        public bool Consumed { get; set; }
    }

    /// <summary>A list in Word's clipboard HTML (mso-list: lN levelM lfoK): its definition is filled in level by level.</summary>
    private sealed class WordList
    {
        public WordList(int numId, XElement definition)
        {
            NumId = numId;
            Definition = definition;
        }

        public int NumId { get; }
        public XElement Definition { get; }
        public HashSet<int> Levels { get; } = new();
    }

    // ------------------------------------------------------------------ entry

    public List<Block> Convert(HtmlNode root)
    {
        foreach (var node in root.Descendants())
        {
            if (node.Name == "style") _sheet.Parse(node.InnerText());
            if (node.IsElement && node.Attr("id") is { Length: > 0 } id && !_ids.ContainsKey(id)) _ids[id] = node;
        }
        var comments = root.Descendants().Where(n => n.Name == "#comment").Select(n => n.Text.Trim()).ToList();
        if (comments.Any(c => c.Equals("StartFragment", StringComparison.OrdinalIgnoreCase)) && comments.Any(c => c.Equals("EndFragment", StringComparison.OrdinalIgnoreCase)))
            _inFragment = false;
        var block = new BlockCtx();
        var body = root.Descendants().FirstOrDefault(n => n.Name == "body");
        var bodyDecls = body == null ? new List<KeyValuePair<string, string>>() : Cascade(body);
        if (body != null && IsRtl(body, bodyDecls)) block = block with { Rtl = true };
        WalkChildren(root, block, new RunCtx());
        Flush();
        return _out;
    }

    // ------------------------------------------------------------------ walking

    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
    {
        "script", "style", "head", "title", "meta", "link", "noscript", "template", "iframe", "object", "embed", "video", "audio", "canvas", "svg", "select",
        "option", "input", "textarea", "button", "map", "area", "xml", "base", "frame", "frameset", "noframes", "datalist", "progress", "meter", "param", "source",
    };

    private static readonly HashSet<string> Blocks = new(StringComparer.Ordinal)
    {
        "p", "div", "h1", "h2", "h3", "h4", "h5", "h6", "blockquote", "pre", "address", "article", "aside", "section", "header", "footer", "nav", "main",
        "figure", "figcaption", "center", "dl", "dt", "dd", "form", "fieldset", "legend", "details", "summary", "body", "caption", "listing", "plaintext", "xmp",
        "tr", "td", "th", "thead", "tbody", "tfoot", "dir", "menu", "hgroup", "search",
    };

    private void WalkChildren(HtmlNode node, BlockCtx block, RunCtx run)
    {
        foreach (var child in node.Children) Walk(child, block, run);
    }

    private void Walk(HtmlNode node, BlockCtx block, RunCtx run)
    {
        switch (node.Name)
        {
            case "#text":
                AddText(node.Text, run, block);
                return;
            case "#comment":
            {
                var t = node.Text.Trim();
                if (t.Equals("StartFragment", StringComparison.OrdinalIgnoreCase)) _inFragment = true;
                else if (t.Equals("EndFragment", StringComparison.OrdinalIgnoreCase)) _inFragment = false;
                return;
            }
            case "!if":
            {
                var expr = node.Text.Replace(" ", "");
                // The list label and the extra line break Word writes for programs without list support.
                if (expr.IndexOf("supportLists", StringComparison.OrdinalIgnoreCase) >= 0 || expr.IndexOf("supportLineBreakNewLine", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    expr.IndexOf("supportFootnotes", StringComparison.OrdinalIgnoreCase) >= 0 || expr.IndexOf("supportAnnotations", StringComparison.OrdinalIgnoreCase) >= 0) return;
                WalkChildren(node, block, run);
                return;
            }
            case "#document":
                WalkChildren(node, block, run);
                return;
        }
        var name = node.Name;
        if (Skipped.Contains(name) || name.StartsWith("v:", StringComparison.Ordinal)) return;
        var decls = Cascade(node);
        var display = Value(decls, "display");
        if (node.Attr("hidden") != null || display == "none" || Value(decls, "mso-hide") == "all" || Value(decls, "visibility") == "hidden") return;
        if (Value(decls, "mso-list") is { } msoList && msoList.Trim().Equals("Ignore", StringComparison.OrdinalIgnoreCase)) return;
        if (Value(decls, "mso-element") is { } element && (element.StartsWith("footnote-list", StringComparison.OrdinalIgnoreCase) || element.StartsWith("endnote-list", StringComparison.OrdinalIgnoreCase) ||
                                                           element.StartsWith("comment-list", StringComparison.OrdinalIgnoreCase) || element.StartsWith("footnote", StringComparison.OrdinalIgnoreCase) ||
                                                           element.StartsWith("endnote", StringComparison.OrdinalIgnoreCase) || element.StartsWith("comment", StringComparison.OrdinalIgnoreCase)))
            return;
        if (Value(decls, "mso-tab-count") is { } tabs)
        {
            var count = int.TryParse(tabs.Split(' ')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? MathEx.Clamp(c, 1, 20) : 1;
            Emit(new string('\t', count), run, block);
            return;
        }
        if (name == "a" && (Value(decls, "mso-footnote-id") ?? Value(decls, "mso-endnote-id")) is { } noteId)
        {
            WordNote(noteId, Value(decls, "mso-footnote-id") != null ? NoteKind.Footnote : NoteKind.Endnote, block);
            return;
        }
        if (_noteDepth > 0 && name == "a" && node.Attr("href") is { } back && (back.StartsWith("#_ftnref", StringComparison.OrdinalIgnoreCase) || back.StartsWith("#_ednref", StringComparison.OrdinalIgnoreCase)))
            return;

        var newRun = ApplyInline(node, decls, run, block);
        switch (name)
        {
            case "br":
                LineBreak(newRun, block);
                return;
            case "hr":
                Flush();
                if (_inFragment)
                {
                    var color = _keep ? CssParser.Color(Value(decls, "border-top-color") ?? Value(decls, "color") ?? "") : null;
                    _out.Add(Content.Divider(color ?? "BFBFBF"));
                    _emitted++;
                }
                return;
            case "img":
                AddImage(node, decls, newRun, block);
                return;
            case "ul":
            case "ol":
                ConvertList(node, decls, block, newRun);
                return;
            case "li":
                ConvertItem(node, decls, block, newRun);
                return;
            case "table":
                ConvertTable(node, decls, block, newRun);
                return;
        }
        if (Blocks.Contains(name) || display is "block" or "list-item" or "table" or "flex" or "grid")
        {
            Flush();
            var nb = BlockFor(node, decls, block);
            if (nb.Pre) _skipPreNewline = true;
            var numbering = WordListNumbering(node, decls);
            if (numbering != null) _wordNumbering = numbering;
            WalkChildren(node, nb, newRun);
            if (numbering != null && _wordNumbering != null && _inFragment) EnsureParagraph(nb);
            Flush();
            _wordNumbering = null;
            _skipPreNewline = false;
            return;
        }
        WalkChildren(node, block, newRun);
    }

    private List<KeyValuePair<string, string>> Cascade(HtmlNode node)
    {
        var list = new List<KeyValuePair<string, string>>();
        list.AddRange(_sheet.Match(node.Name, node.Attr("class")));
        list.AddRange(CssParser.Declarations(node.Attr("style")));
        return list;
    }

    private static string? Value(List<KeyValuePair<string, string>> decls, string name)
    {
        string? v = null;
        foreach (var kv in decls)
            if (kv.Key == name) v = kv.Value;
        return v;
    }

    private static bool IsRtl(HtmlNode node, List<KeyValuePair<string, string>> decls) =>
        string.Equals(node.Attr("dir"), "rtl", StringComparison.OrdinalIgnoreCase) || string.Equals(Value(decls, "direction"), "rtl", StringComparison.OrdinalIgnoreCase);

    private BlockCtx BlockFor(HtmlNode node, List<KeyValuePair<string, string>> decls, BlockCtx parent)
    {
        var b = parent;
        var name = node.Name;
        var cls = node.Attr("class") ?? "";
        string? style = null;
        if (name.Length == 2 && name[0] == 'h' && name[1] is >= '1' and <= '6') style = "Heading" + name[1];
        if (cls.Length > 0)
        {
            foreach (var c in cls.Split(' '))
            {
                var mapped = c switch
                {
                    _ when c.Equals("MsoTitle", StringComparison.OrdinalIgnoreCase) || c.Equals("title", StringComparison.Ordinal) => "Title",
                    _ when c.Equals("MsoSubtitle", StringComparison.OrdinalIgnoreCase) || c.Equals("subtitle", StringComparison.Ordinal) => "Subtitle",
                    _ when c.Equals("MsoQuote", StringComparison.OrdinalIgnoreCase) || c.Equals("MsoIntenseQuote", StringComparison.OrdinalIgnoreCase) => "Quote",
                    _ when c.Equals("MsoCaption", StringComparison.OrdinalIgnoreCase) => "Caption",
                    _ when c.StartsWith("MsoListParagraph", StringComparison.OrdinalIgnoreCase) => "ListParagraph",
                    _ when c.Equals("MsoPlainText", StringComparison.OrdinalIgnoreCase) || c.Equals("HTMLPreformatted", StringComparison.OrdinalIgnoreCase) => "Code",
                    _ when c.StartsWith("MsoHeading", StringComparison.OrdinalIgnoreCase) && c.Length == 11 && char.IsDigit(c[10]) => "Heading" + c[10],
                    // Inkwell's own HTML: class="p-<style id>".
                    _ when c.StartsWith("p-", StringComparison.Ordinal) && c.Length > 2 => c.Substring(2),
                    _ => null,
                };
                if (mapped != null) style = mapped;
            }
        }
        switch (name)
        {
            case "blockquote":
                b = b with { InQuote = true };
                break;
            case "pre":
            case "listing":
            case "xmp":
            case "plaintext":
                b = b with { Pre = true, Style = "Code" };
                break;
            case "figcaption":
            case "caption":
                style = "Caption";
                break;
        }
        if (style != null) b = b with { Style = style };
        else if (name is "p" or "div" && b.Style != "Code" && !b.Pre) b = b with { Style = b.InQuote ? "Normal" : b.Style is "Title" or "Subtitle" or "Caption" || b.Style.StartsWith("Heading", StringComparison.Ordinal) ? b.Style : "Normal" };
        var dir = node.Attr("dir") ?? Value(decls, "direction");
        if (dir != null) b = b with { Rtl = dir.Equals("rtl", StringComparison.OrdinalIgnoreCase) };
        if (_keep)
        {
            var align = Value(decls, "text-align") ?? node.Attr("align") ?? (name == "center" ? "center" : null);
            if (align != null) b = b with { Align = MapAlign(align, b.Rtl) };
        }
        return b;
    }

    private static string? MapAlign(string align, bool rtl) => align.Trim().ToLowerInvariant() switch
    {
        "center" or "middle" or "-webkit-center" => "center",
        "right" => "right",
        "left" => "left",
        "justify" => "both",
        "start" => rtl ? "right" : "left",
        "end" => rtl ? "left" : "right",
        _ => null,
    };

    private RunCtx ApplyInline(HtmlNode node, List<KeyValuePair<string, string>> decls, RunCtx run, BlockCtx block)
    {
        var r = run;
        switch (node.Name)
        {
            case "b":
            case "strong":
                r = r with { Bold = true };
                break;
            case "i":
            case "em":
            case "cite":
            case "dfn":
            case "var":
                r = r with { Italic = true };
                break;
            case "u":
            case "ins":
                r = r with { Underline = "single" };
                break;
            case "s":
            case "strike":
            case "del":
                r = r with { Strike = true };
                break;
            case "sup":
                r = r with { VertAlign = "superscript" };
                break;
            case "sub":
                r = r with { VertAlign = "subscript" };
                break;
            case "code":
            case "kbd":
            case "samp":
            case "tt":
                if (!block.Pre) r = r with { Code = true };
                break;
            case "th" when _keep:
                r = r with { Bold = true };
                break;
            case "mark":
                r = r with { Background = "FFFF00" };
                break;
            case "a":
                if (node.Attr("href") is { } href && MakeLink(href, node.Attr("title")) is { } link) r = r with { Link = link };
                break;
            case "font":
                if (node.Attr("face") is { } face) r = r with { Font = CssParser.FontFamily(face) };
                if (node.Attr("color") is { } color) r = r with { Color = CssParser.Color(color) };
                if (node.Attr("size") is { } size && int.TryParse(size.Trim().TrimStart('+'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
                {
                    if (size.Trim().StartsWith("+", StringComparison.Ordinal) || size.Trim().StartsWith("-", StringComparison.Ordinal)) s += 3;
                    r = r with { SizePt = MathEx.Clamp(s, 1, 7) switch { 1 => 7.5, 2 => 10, 3 => 12, 4 => 13.5, 5 => 18, 6 => 24, _ => 36 } };
                }
                break;
        }
        if (node.Attr("lang") is { Length: > 0 } lang) r = r with { Lang = lang };
        // Matching the destination, weight and slant set on a block (a heading, a quote, a header cell, a paragraph's
        // class) describe the source's paragraph style, which the destination's style replaces; only inline
        // elements (spans, b, i…) carry direct formatting.
        var name = node.Name;
        var ownsWeight = !_keep && (Blocks.Contains(name) || name is "li" or "ul" or "ol" or "table");
        foreach (var kv in decls)
        {
            var v = kv.Value.Trim();
            var lower = v.ToLowerInvariant();
            if (ownsWeight && kv.Key is "font-weight" or "font-style" or "font") continue;
            switch (kv.Key)
            {
                case "white-space":
                    r = r with { PreserveSpace = lower is "pre" or "pre-wrap" or "break-spaces" or "pre-line" };
                    break;
                case "font-weight":
                    if (lower is "bold" or "bolder") r = r with { Bold = true };
                    else if (lower is "normal" or "lighter") r = r with { Bold = false };
                    else if (int.TryParse(lower, NumberStyles.Integer, CultureInfo.InvariantCulture, out var weight)) r = r with { Bold = weight >= 600 };
                    break;
                case "font-style":
                    r = r with { Italic = lower is "italic" or "oblique" };
                    break;
                case "text-decoration":
                case "text-decoration-line":
                {
                    // "none" doesn't remove what the parents draw (decorations propagate in CSS): nothing to change.
                    if (lower.Contains("underline"))
                    {
                        var kind = lower.Contains("double") ? "double" : lower.Contains("dotted") ? "dotted" : lower.Contains("dashed") ? "dash" : lower.Contains("wavy") ? "wave" : "single";
                        r = r with { Underline = kind };
                    }
                    if (lower.Contains("line-through")) r = r with { Strike = true };
                    break;
                }
                case "vertical-align" when name is not ("td" or "th" or "tr" or "table" or "tbody" or "thead" or "tfoot"):
                    if (lower == "super") r = r with { VertAlign = "superscript" };
                    else if (lower == "sub") r = r with { VertAlign = "subscript" };
                    else if (lower == "baseline") r = r with { VertAlign = null };
                    break;
                case "font-family":
                    r = r with { Font = CssParser.FontFamily(v) ?? r.Font };
                    break;
                case "font-size":
                    r = r with { SizePt = CssParser.FontSize(lower, r.SizePt ?? 12) ?? r.SizePt };
                    break;
                case "font":
                    r = ApplyFontShorthand(r, v);
                    break;
                case "color":
                    r = r with { Color = CssParser.Color(v) ?? (lower is "inherit" or "currentcolor" ? r.Color : null) };
                    break;
                case "background-color":
                case "background":
                case "mso-highlight":
                {
                    var color = kv.Key == "background" ? CssParser.ColorIn(v) : CssParser.Color(v);
                    if (color != null || lower is "transparent" or "none") r = r with { Background = color };
                    break;
                }
                case "text-transform":
                    break;
            }
        }
        return r;
    }

    private static RunCtx ApplyFontShorthand(RunCtx r, string value)
    {
        var m = Regex.Match(value, @"^(?<pre>.*?)(?<size>[0-9.]+(pt|px|em|%|rem))(\s*/\s*\S+)?\s+(?<family>.+)$", RegexOptions.CultureInvariant);
        if (!m.Success) return r;
        var pre = m.Groups["pre"].Value.ToLowerInvariant();
        if (pre.Contains("bold")) r = r with { Bold = true };
        if (pre.Contains("italic")) r = r with { Italic = true };
        return r with { SizePt = CssParser.FontSize(m.Groups["size"].Value, r.SizePt ?? 12) ?? r.SizePt, Font = CssParser.FontFamily(m.Groups["family"].Value) ?? r.Font };
    }

    private Hyperlink? MakeLink(string href, string? title)
    {
        var h = href.Trim();
        if (h.Length == 0) return null;
        var lower = h.ToLowerInvariant();
        if (lower.StartsWith("javascript:", StringComparison.Ordinal) || lower.StartsWith("vbscript:", StringComparison.Ordinal) || lower.StartsWith("data:", StringComparison.Ordinal)) return null;
        var link = new Hyperlink { Tooltip = string.IsNullOrEmpty(title) ? null : title };
        if (h.StartsWith("#", StringComparison.Ordinal))
        {
            if (h.Length == 1) return null;
            if (_options.BaseUrl != null && !h.StartsWith("#_", StringComparison.Ordinal) && Uri.TryCreate(_options.BaseUrl, UriKind.Absolute, out var page) && page.Scheme.StartsWith("http", StringComparison.Ordinal))
            {
                link.Url = page.GetLeftPart(UriPartial.Query) + h;
                return link;
            }
            link.Anchor = Uri.UnescapeDataString(h.Substring(1));
            return link;
        }
        if (Uri.TryCreate(h, UriKind.Absolute, out var absolute) && absolute.Scheme.Length > 1)
        {
            link.Url = h;
            return link;
        }
        if (_options.BaseUrl != null && Uri.TryCreate(_options.BaseUrl, UriKind.Absolute, out var baseUri) && Uri.TryCreate(baseUri, h, out var resolved))
        {
            link.Url = resolved.AbsoluteUri;
            return link;
        }
        link.Url = h;
        return link;
    }

    // ------------------------------------------------------------------ paragraphs and text

    private Paragraph EnsureParagraph(BlockCtx block)
    {
        if (_para != null) return _para;
        var p = new Paragraph();
        var canonical = block.Style;
        if (block.InQuote && canonical == "Normal") canonical = "Quote";
        p.Props.StyleId = _styles.Get(canonical);
        if (_keep && block.Align != null) p.Props.Justification = block.Align;
        if (block.Rtl) p.Props.Bidi = true;
        if (_wordNumbering is { } wn)
        {
            p.Props.SetNumbering(wn.NumId, wn.Level);
            if (canonical == "Normal") p.Props.StyleId = _styles.Get("ListParagraph");
            _wordNumbering = null;
        }
        else if (_items.Count > 0)
        {
            var item = _items.Peek();
            if (canonical == "Normal") p.Props.StyleId = _styles.Get("ListParagraph");
            if (!item.Consumed)
            {
                p.Props.SetNumbering(item.List.Get(_lists), item.Level);
                item.Consumed = true;
            }
            else
            {
                p.Props.IndentStart = ListFactory.LevelIndent(item.Level);
            }
        }
        _para = p;
        _lineStart = true;
        _pendingSpace = false;
        return p;
    }

    private void Flush()
    {
        var p = _para;
        _para = null;
        _pendingSpace = false;
        if (p == null) return;
        // A <br> at the end of a block doesn't make a line of its own.
        if (p.Inlines.Count > 0 && p.Inlines[p.Inlines.Count - 1] is TextRun last && last.Text.EndsWith(TextRun.LineBreak.ToString(), StringComparison.Ordinal))
        {
            last.Text = last.Text.Substring(0, last.Text.Length - 1);
            if (last.Text.Length == 0) p.Inlines.RemoveAt(p.Inlines.Count - 1);
        }
        // Trailing spaces; a paragraph of nothing but (non-breaking) spaces is an empty line.
        if (p.Inlines.Count > 0 && p.Inlines[p.Inlines.Count - 1] is TextRun end)
        {
            end.Text = end.Text.TrimEnd(' ');
            if (end.Text.Length == 0) p.Inlines.RemoveAt(p.Inlines.Count - 1);
        }
        if (p.Inlines.All(i => i is TextRun t && t.Text.All(c => c is ' ' or '\u00A0')))
            p.Inlines.Clear();
        p.Normalize();
        _out.Add(p);
    }

    private RunProps PropsFor(RunCtx run)
    {
        var props = new RunProps();
        if (run.Link != null) props.StyleId = _styles.Get("Hyperlink");
        else if (run.Code) props.StyleId = _styles.Get("InlineCode");
        if (_keep)
        {
            if (run.Bold != null) props.Bold = run.Bold;
            if (run.Italic != null) props.Italic = run.Italic;
            if (run.Underline != null) props.Underline = run.Underline;
            if (run.Strike == true) props.Strike = true;
            if (run.Font != null)
            {
                props.SetLatinFont(run.Font);
                props.SetComplexFont(run.Font);
            }
            if (run.SizePt is { } pt && pt > 0)
            {
                var half = (int)Math.Round(MathEx.Clamp(pt, 1, 1638) * 2);
                props.Size = half;
                props.SizeCs = half;
            }
            if (run.Color != null) props.Color = run.Color;
            if (run.Background != null)
            {
                var highlight = HighlightName(run.Background);
                if (highlight != null) props.Highlight = highlight;
                else props.ShadingFill = run.Background;
            }
            if (run.Lang != null && Regex.IsMatch(run.Lang, "^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$", RegexOptions.CultureInvariant)) props.Lang = run.Lang;
        }
        else
        {
            if (run.Bold == true) props.Bold = true;
            if (run.Italic == true) props.Italic = true;
            if (run.Link == null && run.Underline is { } u && u != "none") props.Underline = u;
            if (run.Strike == true) props.Strike = true;
        }
        if (run.VertAlign != null) props.VertAlign = run.VertAlign;
        return props;
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

    private const char KeptSpace = '\uE000';

    private void AddText(string text, RunCtx run, BlockCtx block)
    {
        if (!_inFragment || text.Length == 0) return;
        if (block.Pre)
        {
            var t = text.Replace("\r\n", "\n").Replace('\r', '\n');
            if (_skipPreNewline)
            {
                if (t.StartsWith("\n", StringComparison.Ordinal)) t = t.Substring(1);
                _skipPreNewline = false;
            }
            var parts = t.Split('\n');
            for (var k = 0; k < parts.Length; k++)
            {
                if (k > 0)
                {
                    EnsureParagraph(block);
                    Flush();
                }
                if (parts[k].Length > 0) Emit(parts[k], run, block, raw: true);
            }
            return;
        }
        _skipPreNewline = false;
        if (run.PreserveSpace)
        {
            Emit(text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\n', TextRun.LineBreak), run, block, raw: true);
            return;
        }
        // Collapse white space the way browsers do; a non-breaking space next to a space is a space that must not collapse.
        var sb = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is ' ' or '\t' or '\n' or '\r' or '\f')
            {
                if (sb.Length > 0)
                {
                    Emit(sb.ToString(), run, block);
                    sb.Clear();
                }
                if (_para != null && !_lineStart && !_pendingSpace)
                {
                    _pendingSpace = true;
                    _pendingSpaceProps = PropsFor(run);
                    _pendingSpaceLink = run.Link;
                }
                continue;
            }
            if (c == '\u00A0' && ((i > 0 && text[i - 1] is ' ' or '\u00A0') || (i + 1 < text.Length && text[i + 1] is ' ' or '\u00A0')))
            {
                sb.Append(KeptSpace);
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) Emit(sb.ToString(), run, block);
    }

    private void Emit(string text, RunCtx run, BlockCtx block, bool raw = false)
    {
        if (!_inFragment || text.Length == 0) return;
        var p = EnsureParagraph(block);
        if (_pendingSpace)
        {
            Content.AppendText(p.Inlines, " ", _pendingSpaceProps ?? PropsFor(run), _pendingSpaceLink);
            _pendingSpace = false;
        }
        var clean = Content.CleanText(text.Replace(KeptSpace, ' '));
        if (raw) clean = clean.Replace('\u00A0', ' ');
        Content.AppendText(p.Inlines, clean, PropsFor(run), run.Link);
        _lineStart = false;
        _emitted++;
    }

    private void LineBreak(RunCtx run, BlockCtx block)
    {
        if (!_inFragment) return;
        var p = EnsureParagraph(block);
        if (block.Pre)
        {
            Flush();
            return;
        }
        Content.AppendText(p.Inlines, TextRun.LineBreak.ToString(), PropsFor(run), run.Link);
        _pendingSpace = false;
        _lineStart = true;
        _emitted++;
    }

    // ------------------------------------------------------------------ pictures

    private void AddImage(HtmlNode node, List<KeyValuePair<string, string>> decls, RunCtx run, BlockCtx block)
    {
        if (!_inFragment) return;
        var src = (node.Attr("src") ?? "").Trim();
        var alt = node.Attr("alt") ?? node.Attr("title");
        long? Size(string attr, string css)
        {
            var v = Value(decls, css) ?? node.Attr(attr);
            if (v == null) return null;
            var pt = CssParser.Points(v.Trim().EndsWith("%", StringComparison.Ordinal) ? "" : v.Trim());
            return pt is > 0 ? (long)Math.Round(pt.Value * 12700) : null;
        }
        var w = Size("width", "width");
        var h = Size("height", "height");
        if (w is <= 19050 && h is <= 19050) return; // 1-pixel tracking images
        byte[]? data = null;
        var web = false;
        if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) data = Pictures.DecodeDataUri(src);
        else if (Pictures.FileUrlToPath(src) is { } path) data = Pictures.ReadFile(path);
        else if (src.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || src.StartsWith("https:", StringComparison.OrdinalIgnoreCase) || src.StartsWith("//", StringComparison.Ordinal)) web = true;
        else if (src.Length > 0 && _options.BaseFolder != null && !src.Contains(':'))
        {
            try
            {
                data = Pictures.ReadFile(Path.Combine(_options.BaseFolder, Uri.UnescapeDataString(src).Replace('/', Path.DirectorySeparatorChar)));
            }
            catch (ArgumentException)
            {
                data = null;
            }
        }
        else if (src.Length > 0 && _options.BaseUrl != null) web = true;
        var drawing = data == null ? null : Pictures.Create(_doc, data, alt, _maxImageWidth, w, h, useResolution: false, title: node.Attr("title"));
        if (drawing == null)
        {
            _result.ImagesSkipped++;
            if (web)
            {
                var url = src;
                if (src.StartsWith("//", StringComparison.Ordinal)) url = "https:" + src;
                else if (_options.BaseUrl != null && Uri.TryCreate(_options.BaseUrl, UriKind.Absolute, out var b) && Uri.TryCreate(b, src, out var resolved)) url = resolved.AbsoluteUri;
                _result.SkippedImageUrls.Add(url);
            }
            return;
        }
        var p = EnsureParagraph(block);
        if (_pendingSpace)
        {
            Content.AppendText(p.Inlines, " ", _pendingSpaceProps ?? PropsFor(run), _pendingSpaceLink);
            _pendingSpace = false;
        }
        drawing.Link = run.Link;
        p.Inlines.Add(drawing);
        _lineStart = false;
        _emitted++;
        _result.ImagesImported++;
    }

    // ------------------------------------------------------------------ lists

    private void ConvertList(HtmlNode node, List<KeyValuePair<string, string>> decls, BlockCtx block, RunCtx run)
    {
        Flush();
        var ordered = node.Name == "ol";
        var depth = Math.Min(block.ListDepth + 1, 8);
        var type = Value(decls, "list-style-type") ?? (Value(decls, "list-style") is { } ls ? ls.Split(' ')[0] : null);
        var list = new LazyList { Level = depth, Kind = ordered ? ListKind.Ordered : ListKind.Bullet };
        if (ordered)
        {
            if (int.TryParse(node.Attr("start"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var start)) list.Start = start;
            list.Format = FormatOfType(type) ?? FormatOfTypeAttribute(node.Attr("type")) ?? "decimal";
        }
        ApplyBulletType(list, type);
        WalkChildren(node, block with { ListDepth = depth, List = list }, run);
        Flush();
    }

    private static void ApplyBulletType(LazyList list, string? type)
    {
        switch (type?.Trim().ToLowerInvariant())
        {
            case "none":
                list.Kind = ListKind.Plain;
                break;
            case "disc":
                if (list.Kind == ListKind.Bullet) list.Bullet = "•";
                break;
            case "circle":
                if (list.Kind == ListKind.Bullet) list.Bullet = "◦";
                break;
            case "square":
                if (list.Kind == ListKind.Bullet) list.Bullet = "▪";
                break;
            case { } t when t.StartsWith("\"", StringComparison.Ordinal) && list.Kind == ListKind.Bullet:
                list.Bullet = t.Trim('"').Trim();
                break;
        }
    }

    private static string? FormatOfType(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        "decimal" => "decimal",
        "decimal-leading-zero" => "decimalZero",
        "lower-alpha" or "lower-latin" => "lowerLetter",
        "upper-alpha" or "upper-latin" => "upperLetter",
        "lower-roman" => "lowerRoman",
        "upper-roman" => "upperRoman",
        "arabic-indic" => "arabicAlpha",
        _ => null,
    };

    private static string? FormatOfTypeAttribute(string? type) => type switch
    {
        "1" => "decimal",
        "a" => "lowerLetter",
        "A" => "upperLetter",
        "i" => "lowerRoman",
        "I" => "upperRoman",
        _ => null,
    };

    private void ConvertItem(HtmlNode node, List<KeyValuePair<string, string>> decls, BlockCtx block, RunCtx run)
    {
        Flush();
        var list = block.List;
        if (list == null)
        {
            list = new LazyList { Kind = ListKind.Bullet, Level = Math.Max(0, block.ListDepth) };
            block = block with { List = list, ListDepth = Math.Max(0, block.ListDepth) };
        }
        var level = block.ListDepth < 0 ? 0 : block.ListDepth;
        if (int.TryParse(node.Attr("aria-level"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var aria) && aria >= 1) level = Math.Min(aria - 1, 8);
        if (list.NumId == null)
        {
            var type = Value(decls, "list-style-type");
            if (type != null)
            {
                var fmt = FormatOfType(type);
                if (fmt != null && list.Kind == ListKind.Ordered) list.Format = fmt;
                else if (fmt != null && list.Kind == ListKind.Bullet)
                {
                    list.Kind = ListKind.Ordered;
                    list.Format = fmt;
                }
                else ApplyBulletType(list, type);
            }
            if (level != list.Level) list.Level = level;
        }
        var item = new ItemState(list, level);
        _items.Push(item);
        WalkChildren(node, block, run);
        Flush();
        _items.Pop();
        if (!item.Consumed && _inFragment)
        {
            _items.Push(item);
            EnsureParagraph(block with { Style = "Normal" });
            Flush();
            _items.Pop();
        }
    }

    /// <summary>The numbering of a Word clipboard list paragraph (style "mso-list: l0 level1 lfo1"), or null.</summary>
    private (int NumId, int Level)? WordListNumbering(HtmlNode node, List<KeyValuePair<string, string>> decls)
    {
        var mso = Value(decls, "mso-list");
        if (mso == null) return null;
        var m = Regex.Match(mso, @"(l\d+)\s+level(\d+)\s+(lfo\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!m.Success) return null;
        var listId = m.Groups[1].Value.ToLowerInvariant();
        var level = MathEx.Clamp(int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) - 1, 0, 8);
        var key = listId + ":" + m.Groups[3].Value.ToLowerInvariant();
        var marker = MarkerText(node);
        _sheet.ListRules.TryGetValue(listId + ":level" + (level + 1).ToString(CultureInfo.InvariantCulture), out var rule);
        var (format, text, start) = ParseMarker(marker, level, rule);
        if (!_wordLists.TryGetValue(key, out var list))
        {
            var definition = ListFactory.Definition(new Dictionary<int, XElement>(), format == "bullet");
            var numId = _lists.AddDefinition(definition);
            list = new WordList(numId, definition);
            _wordLists[key] = list;
        }
        if (list.Levels.Add(level))
        {
            var lvl = list.Definition.Elements(Ns.W + "lvl").FirstOrDefault(e => e.IntAttr("ilvl") == level);
            var replacement = ListFactory.Level(level, format, text, start);
            if (lvl != null) lvl.ReplaceWith(replacement);
            else list.Definition.Add(replacement);
            _doc.Numbering.Changed();
        }
        return (list.NumId, level);
    }

    /// <summary>The label Word wrote for programs without list support (inside &lt;![if !supportLists]&gt; or mso-list:Ignore spans).</summary>
    private string MarkerText(HtmlNode node)
    {
        foreach (var d in node.Descendants())
        {
            if (d.Name == "!if" && d.Text.IndexOf("supportLists", StringComparison.OrdinalIgnoreCase) >= 0) return CleanMarker(d);
            if (d.IsElement && Value(CssParser.Declarations(d.Attr("style")), "mso-list") is { } v && v.Trim().Equals("Ignore", StringComparison.OrdinalIgnoreCase)) return CleanMarker(d);
        }
        return "";
    }

    private static string CleanMarker(HtmlNode node)
    {
        var sb = new StringBuilder();
        void Walk(HtmlNode n)
        {
            // The padding span (font: 7.0pt "Times New Roman") only holds spaces.
            if (n.Name == "#text") sb.Append(n.Text);
            foreach (var c in n.Children) Walk(c);
        }
        Walk(node);
        return sb.ToString().Replace('\u00A0', ' ').Trim();
    }

    private static readonly Regex RomanLower = new("^[ivxlcdm]+$", RegexOptions.CultureInvariant);
    private static readonly Regex RomanUpper = new("^[IVXLCDM]+$", RegexOptions.CultureInvariant);

    /// <summary>A Word list level from its label ("1.", "a)", "(iv)", "1.2.", "·") and Word's @list rule when the page has one.</summary>
    internal static (string Format, string Text, int Start) ParseMarker(string marker, int level, List<KeyValuePair<string, string>>? rule)
    {
        var placeholder = "%" + (level + 1).ToString(CultureInfo.InvariantCulture);
        string? ruleFormat = null;
        if (rule != null)
        {
            var nf = Value(rule, "mso-level-number-format")?.Trim().ToLowerInvariant();
            ruleFormat = nf switch
            {
                "bullet" or "image" => "bullet",
                "alpha-lower" => "lowerLetter",
                "alpha-upper" => "upperLetter",
                "roman-lower" => "lowerRoman",
                "roman-upper" => "upperRoman",
                "arabic-leading-zero" => "decimalZero",
                "none" => "none",
                null => null,
                _ => "decimal",
            };
            if (ruleFormat == null && Value(rule, "mso-level-text") != null) ruleFormat = "decimal";
        }
        var m = marker.Trim();
        var multi = Regex.Match(m, @"^(\d+)((?:\.\d+)+)\.?$", RegexOptions.CultureInvariant);
        if (multi.Success && ruleFormat is null or "decimal")
        {
            var count = m.TrimEnd('.').Split('.').Length;
            var text = string.Join(".", Enumerable.Range(Math.Max(1, level + 2 - count), count).Select(k => "%" + k.ToString(CultureInfo.InvariantCulture))) + (m.EndsWith(".", StringComparison.Ordinal) ? "." : "");
            var last = int.Parse(m.TrimEnd('.').Split('.').Last(), CultureInfo.InvariantCulture);
            return ("decimal", text, last);
        }
        var single = Regex.Match(m, @"^(?<pre>[(\[]?)(?<num>[0-9]+|[A-Za-z]{1,6})(?<post>[.)\]:]?)$", RegexOptions.CultureInvariant);
        if (single.Success && ruleFormat != "bullet" && (single.Groups["num"].Value.All(char.IsDigit) || single.Groups["pre"].Length + single.Groups["post"].Length > 0))
        {
            var num = single.Groups["num"].Value;
            var text = single.Groups["pre"].Value + placeholder + single.Groups["post"].Value;
            if (num.All(char.IsDigit)) return (ruleFormat ?? "decimal", text, int.Parse(num, CultureInfo.InvariantCulture));
            var roman = ruleFormat is "lowerRoman" or "upperRoman" || (ruleFormat == null && (num is "i" or "I" || (num.Length > 1 && (RomanLower.IsMatch(num) || RomanUpper.IsMatch(num)))));
            if (roman) return (char.IsUpper(num[0]) ? "upperRoman" : "lowerRoman", text, RomanValue(num));
            if (num.Length == 1 || num.All(c => c == num[0]))
                return (char.IsUpper(num[0]) ? "upperLetter" : "lowerLetter", text, (char.ToLowerInvariant(num[0]) - 'a' + 1) + 26 * (num.Length - 1));
        }
        if (ruleFormat is not null and not "bullet")
            return (ruleFormat, ruleFormat == "none" ? "" : placeholder + ".", 1);
        return ("bullet", ListFactory.NormalizeBullet(m.Length == 0 ? "•" : m.Substring(0, 1)), 1);
    }

    private static int RomanValue(string s)
    {
        var total = 0;
        var prev = 0;
        for (var i = s.Length - 1; i >= 0; i--)
        {
            var v = char.ToLowerInvariant(s[i]) switch { 'i' => 1, 'v' => 5, 'x' => 10, 'l' => 50, 'c' => 100, 'd' => 500, 'm' => 1000, _ => 0 };
            total += v < prev ? -v : v;
            prev = Math.Max(prev, v);
        }
        return Math.Max(1, total);
    }

    // ------------------------------------------------------------------ Word footnotes

    private void WordNote(string id, NoteKind kind, BlockCtx block)
    {
        if (!_inFragment || _noteDepth > 0) return;
        if (!_ids.TryGetValue(id.Trim(), out var target)) return;
        var saved = SaveState();
        _noteDepth++;
        try
        {
            WalkChildren(target, new BlockCtx(), new RunCtx());
            Flush();
        }
        finally
        {
            _noteDepth--;
        }
        var blocks = _out;
        RestoreState(saved);
        // Word's note text starts with the reference mark (a link back), which was skipped; drop the space after it.
        if (blocks.FirstOrDefault() is Paragraph first && first.Inlines.FirstOrDefault() is TextRun t)
        {
            t.Text = t.Text.TrimStart(' ');
            if (t.Text.Length == 0) first.Inlines.RemoveAt(0);
        }
        var note = NoteFactory.Create(_doc, _styles, kind, blocks);
        _result.AddedNotes.Add(note);
        var p = EnsureParagraph(block);
        p.Inlines.Add(NoteFactory.Reference(_styles, kind, note.Id));
        _lineStart = false;
        _emitted++;
    }

    private sealed class SavedState
    {
        public List<Block> Out = new();
        public Stack<ItemState> Items = new();
        public Paragraph? Para;
        public bool LineStart;
        public bool PendingSpace;
        public RunProps? PendingSpaceProps;
        public Hyperlink? PendingSpaceLink;
        public (int NumId, int Level)? WordNumbering;
    }

    private SavedState SaveState()
    {
        var s = new SavedState
        {
            Out = _out,
            Items = _items,
            Para = _para,
            LineStart = _lineStart,
            PendingSpace = _pendingSpace,
            PendingSpaceProps = _pendingSpaceProps,
            PendingSpaceLink = _pendingSpaceLink,
            WordNumbering = _wordNumbering,
        };
        _out = new List<Block>();
        _items = new Stack<ItemState>();
        _para = null;
        _pendingSpace = false;
        _wordNumbering = null;
        return s;
    }

    private void RestoreState(SavedState s)
    {
        _out = s.Out;
        _items = s.Items;
        _para = s.Para;
        _lineStart = s.LineStart;
        _pendingSpace = s.PendingSpace;
        _pendingSpaceProps = s.PendingSpaceProps;
        _pendingSpaceLink = s.PendingSpaceLink;
        _wordNumbering = s.WordNumbering;
    }

    // ------------------------------------------------------------------ tables

    private sealed class SourceCell
    {
        public SourceCell(HtmlNode node, int row, int column, int colSpan, int rowSpan)
        {
            Node = node;
            Row = row;
            Column = column;
            ColSpan = colSpan;
            RowSpan = rowSpan;
        }

        public HtmlNode Node { get; }
        public int Row { get; }
        public int Column { get; }
        public int ColSpan { get; }
        public int RowSpan { get; }
    }

    private void ConvertTable(HtmlNode node, List<KeyValuePair<string, string>> decls, BlockCtx block, RunCtx run)
    {
        Flush();
        var emittedBefore = _emitted;
        var wasInFragment = _inFragment;
        var rows = new List<(HtmlNode Row, bool Header)>();
        HtmlNode? caption = null;
        var colWidths = new List<double?>();
        void CollectRows(HtmlNode parent, bool header)
        {
            foreach (var c in parent.Children)
            {
                switch (c.Name)
                {
                    case "tr":
                        rows.Add((c, header));
                        break;
                    case "thead":
                        CollectRows(c, true);
                        break;
                    case "tbody":
                    case "tfoot":
                    case "!if":
                        CollectRows(c, false);
                        break;
                    case "caption":
                        caption ??= c;
                        break;
                    case "colgroup":
                        foreach (var col in c.Children.Where(x => x.Name == "col")) AddCol(col);
                        break;
                    case "col":
                        AddCol(c);
                        break;
                    case "#comment":
                        Walk(c, block, run);
                        break;
                }
            }
        }
        void AddCol(HtmlNode col)
        {
            var span = int.TryParse(col.Attr("span"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? MathEx.Clamp(s, 1, 100) : 1;
            var width = Value(CssParser.Declarations(col.Attr("style")), "width") ?? col.Attr("width");
            var pt = width != null && !width.EndsWith("%", StringComparison.Ordinal) ? CssParser.Points(width) : null;
            for (var k = 0; k < span; k++) colWidths.Add(pt);
        }
        CollectRows(node, false);
        if (caption != null)
        {
            Walk(caption, block with { Style = "Caption" }, run);
            Flush();
        }

        // Place cells on the grid (row spans take room in the rows below).
        var occupied = new Dictionary<(int, int), SourceCell>();
        var placed = new List<List<SourceCell>>();
        var columns = 0;
        for (var r = 0; r < rows.Count; r++)
        {
            var list = new List<SourceCell>();
            var col = 0;
            foreach (var cellNode in rows[r].Row.Children.Where(c => c.Name is "td" or "th"))
            {
                while (occupied.ContainsKey((r, col))) col++;
                var colSpan = int.TryParse(cellNode.Attr("colspan"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cs) ? MathEx.Clamp(cs, 1, 63) : 1;
                var rowSpan = int.TryParse(cellNode.Attr("rowspan"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rs) ? rs : 1;
                if (rowSpan <= 0) rowSpan = rows.Count - r;
                rowSpan = MathEx.Clamp(rowSpan, 1, rows.Count - r);
                var cell = new SourceCell(cellNode, r, col, colSpan, rowSpan);
                for (var dr = 0; dr < rowSpan; dr++)
                    for (var dc = 0; dc < colSpan; dc++)
                        occupied[(r + dr, col + dc)] = cell;
                list.Add(cell);
                col += colSpan;
                columns = Math.Max(columns, col);
            }
            placed.Add(list);
        }
        foreach (var key in occupied.Keys) columns = Math.Max(columns, key.Item2 + 1);
        if (columns == 0 || rows.Count == 0) return;

        // Column widths: <col> widths, else the first row's cell widths, else equal; scaled to the text width.
        var textWidth = Content.TextWidth(_doc);
        var widths = new double[columns];
        for (var c = 0; c < columns; c++) widths[c] = c < colWidths.Count && colWidths[c] is { } w && w > 0 ? w * 20 : 0;
        if (widths.All(w => w <= 0) && placed.Count > 0)
        {
            foreach (var cell in placed[0])
            {
                var width = Value(CssParser.Declarations(cell.Node.Attr("style")), "width") ?? cell.Node.Attr("width");
                var pt = width != null && !width.EndsWith("%", StringComparison.Ordinal) ? CssParser.Points(width) : null;
                if (pt is > 0)
                    for (var k = 0; k < cell.ColSpan && cell.Column + k < columns; k++) widths[cell.Column + k] = pt.Value * 20 / cell.ColSpan;
            }
        }
        var known = widths.Where(w => w > 0).ToList();
        var fill = known.Count == 0 ? textWidth / (double)columns : known.Average();
        for (var c = 0; c < columns; c++)
            if (widths[c] <= 0) widths[c] = fill;
        var total = widths.Sum();
        if (total > textWidth || known.Count == 0)
            for (var c = 0; c < columns; c++) widths[c] = widths[c] * textWidth / total;
        var grid = widths.Select(w => Math.Max(240, (int)Math.Round(w))).ToList();

        var table = new Model.Table();
        var hasBorders = (int.TryParse(node.Attr("border"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var border) && border > 0) ||
                         placed.SelectMany(r => r).Any(c => CellBorders(Cascade(c.Node)).Any(b => b.Border.IsVisible));
        table.Props.StyleId = _styles.Get(_keep && hasBorders ? "TableGrid" : "InkwellTable");
        table.Props.Width = new TableWidth("pct", 5000);
        table.Grid = grid;
        var headerRows = 0;
        while (headerRows < rows.Count && (rows[headerRows].Header || (placed[headerRows].Count > 0 && placed[headerRows].All(c => c.Node.Name == "th")))) headerRows++;
        table.Props.Look = (headerRows > 0, false, false, false, true, false);
        if (_keep && Value(decls, "margin-left") is "auto" && Value(decls, "margin-right") is "auto") table.Props.Alignment = "center";
        if (_keep && node.Attr("align") is { } talign && talign.Equals("center", StringComparison.OrdinalIgnoreCase)) table.Props.Alignment = "center";

        for (var r = 0; r < rows.Count; r++)
        {
            var tr = new Model.TableRow();
            if (r < headerRows) tr.Props.IsHeader = true;
            var rowDecls = Cascade(rows[r].Row);
            for (var c = 0; c < columns;)
            {
                occupied.TryGetValue((r, c), out var source);
                if (source == null)
                {
                    tr.AddCell(Model.TableCell.Create(grid[c]));
                    c++;
                    continue;
                }
                var span = Math.Min(source.ColSpan, columns - source.Column);
                var cell = new Model.TableCell();
                cell.Props.Width = new TableWidth("dxa", grid.Skip(source.Column).Take(span).Sum());
                if (span > 1) cell.Props.GridSpan = span;
                if (source.Row == r)
                {
                    if (source.RowSpan > 1) cell.Props.VerticalMerge = "restart";
                    var cellDecls = Cascade(source.Node);
                    ConvertCell(source.Node, cellDecls, rowDecls, cell, block, run);
                }
                else
                {
                    cell.Props.VerticalMerge = "continue";
                    cell.Blocks.Add(new Paragraph());
                }
                tr.AddCell(cell);
                c = source.Column + span;
            }
            table.AddRow(tr);
        }
        if (_emitted == emittedBefore && !wasInFragment && !_inFragment) return;
        _out.Add(table);
        _emitted++;
    }

    private void ConvertCell(HtmlNode node, List<KeyValuePair<string, string>> decls, List<KeyValuePair<string, string>> rowDecls, Model.TableCell cell, BlockCtx outer, RunCtx run)
    {
        var saved = SaveState();
        var block = new BlockCtx { Rtl = outer.Rtl || IsRtl(node, decls) };
        if (_keep)
        {
            var align = Value(decls, "text-align") ?? node.Attr("align");
            if (align != null) block = block with { Align = MapAlign(align, block.Rtl) };
        }
        var cellRun = ApplyInline(node, decls, run, block);
        if (!_keep && node.Name == "th" && !(node.Parent?.Parent?.Name == "thead")) cellRun = cellRun with { Bold = true };
        WalkChildren(node, block, cellRun);
        Flush();
        var blocks = _out;
        RestoreState(saved);
        if (blocks.Count == 0 || blocks[blocks.Count - 1] is not Paragraph) blocks.Add(new Paragraph());
        cell.Blocks.AddRange(blocks);
        var valign = (Value(decls, "vertical-align") ?? node.Attr("valign") ?? Value(rowDecls, "vertical-align"))?.Trim().ToLowerInvariant();
        if (valign is "middle" or "center") cell.Props.VerticalAlignment = "center";
        else if (valign == "bottom") cell.Props.VerticalAlignment = "bottom";
        if (!_keep) return;
        var bg = Value(decls, "background-color") ?? node.Attr("bgcolor") ?? Value(rowDecls, "background-color");
        if (bg != null && CssParser.Color(bg) is { } fill) cell.Props.ShadingFill = fill;
        foreach (var (side, b) in CellBorders(decls)) cell.Props.SetBorder(side, b);
    }

    /// <summary>The visible or explicitly hidden borders a cell's CSS gives (border, border-top…).</summary>
    private static List<(string Side, BorderDef Border)> CellBorders(List<KeyValuePair<string, string>> decls)
    {
        var result = new Dictionary<string, BorderDef>(StringComparer.Ordinal);
        foreach (var kv in decls)
        {
            string[] sides;
            switch (kv.Key)
            {
                case "border":
                    sides = new[] { "top", "left", "bottom", "right" };
                    break;
                case "border-top":
                case "border-bottom":
                case "border-left":
                case "border-right":
                    sides = new[] { kv.Key.Substring(7) };
                    break;
                default:
                    continue;
            }
            var b = ParseBorder(kv.Value);
            if (b == null) continue;
            foreach (var s in sides) result[s] = b.Value;
        }
        return result.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    private static BorderDef? ParseBorder(string value)
    {
        var v = value.Trim().ToLowerInvariant();
        if (v is "none" or "0" or "hidden" or "0px" or "0pt") return BorderDef.None;
        string? style = null;
        double? width = null;
        string? color = null;
        foreach (var token in Regex.Split(v, @"\s+(?![^(]*\))"))
        {
            if (token.Length == 0) continue;
            switch (token)
            {
                case "solid":
                case "double":
                case "dotted":
                case "dashed":
                case "groove":
                case "ridge":
                case "inset":
                case "outset":
                    style = token;
                    continue;
                case "none":
                case "hidden":
                    return BorderDef.None;
                case "thin":
                    width = 0.75;
                    continue;
                case "medium":
                    width = 2.25;
                    continue;
                case "thick":
                    width = 3.75;
                    continue;
            }
            if (CssParser.Points(token) is { } pt) width = pt;
            else if (CssParser.Color(token) is { } c) color = c;
        }
        if (style == null) return null;
        if (width is <= 0) return BorderDef.None;
        var eighths = (int)MathEx.Clamp(Math.Round((width ?? 0.75) * 8), 2, 96);
        var wordStyle = style switch { "double" => "double", "dotted" => "dotted", "dashed" => "dashed", "inset" => "inset", "outset" => "outset", _ => "single" };
        return new BorderDef(wordStyle, eighths, 0, color ?? "auto");
    }
}
