using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using Inkwell.Core.Model;
using Inkwell.Core.Templates;

namespace Inkwell.Core.Formats;

/// <summary>
/// Content converted for insertion into an existing document (pasting HTML, RTF, Markdown or plain text). The
/// blocks use the target document's styles; lists, pictures and notes they need have already been added to the
/// target's <see cref="Document.Numbering"/>, <see cref="Document.Media"/> and footnotes / endnotes.
/// </summary>
public sealed class PasteResult
{
    /// <summary>The blocks to insert, in order (paragraphs and tables, not attached to any story yet).</summary>
    public List<Block> Blocks { get; } = new();

    /// <summary>Pictures that were converted (data: URIs, local files, embedded pictures).</summary>
    public int ImagesImported { get; set; }

    /// <summary>Pictures that were left out: web addresses (http/https), unreadable files, unsupported formats.</summary>
    public int ImagesSkipped { get; set; }

    /// <summary>The addresses of the pictures that were left out because they are on the web (so the caller can offer to download them).</summary>
    public List<string> SkippedImageUrls { get; } = new();

    /// <summary>Footnotes / endnotes that were added to the target document for these blocks (remove them again if the paste is undone).</summary>
    public List<Note> AddedNotes { get; } = new();

    /// <summary>Things that couldn't be converted exactly.</summary>
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Finds the styles a converter needs in the target document (by id, then by Word's built-in name, so documents
/// made by Word in other languages work too) and adds missing ones from the default design.
/// </summary>
internal sealed class StyleMap
{
    private static readonly object SheetGate = new();
    private static StyleSheet? s_defaultSheet;

    private readonly Document _doc;
    private readonly Dictionary<string, string> _resolved = new(StringComparer.Ordinal);

    public StyleMap(Document doc)
    {
        _doc = doc;
    }

    private static StyleSheet DefaultSheet
    {
        get
        {
            lock (SheetGate) return s_defaultSheet ??= DocumentDesigns.Default.BuildStyleSheet();
        }
    }

    /// <summary>Word's built-in names for Inkwell's canonical style ids.</summary>
    private static readonly Dictionary<string, string> BuiltInNames = new(StringComparer.Ordinal)
    {
        ["Normal"] = "Normal",
        ["Title"] = "Title",
        ["Subtitle"] = "Subtitle",
        ["Heading1"] = "heading 1",
        ["Heading2"] = "heading 2",
        ["Heading3"] = "heading 3",
        ["Heading4"] = "heading 4",
        ["Heading5"] = "heading 5",
        ["Heading6"] = "heading 6",
        ["Heading7"] = "heading 7",
        ["Heading8"] = "heading 8",
        ["Heading9"] = "heading 9",
        ["Quote"] = "Quote",
        ["Code"] = "Code",
        ["InlineCode"] = "Inline Code",
        ["ListParagraph"] = "List Paragraph",
        ["Hyperlink"] = "Hyperlink",
        ["FootnoteText"] = "footnote text",
        ["FootnoteReference"] = "footnote reference",
        ["EndnoteText"] = "endnote text",
        ["EndnoteReference"] = "endnote reference",
        ["Caption"] = "caption",
        ["CommentText"] = "annotation text",
        ["CommentReference"] = "annotation reference",
        ["TableGrid"] = "Table Grid",
        ["InkwellTable"] = "Inkwell Table",
        ["Header"] = "header",
        ["Footer"] = "footer",
    };

    /// <summary>The id of the style in the target document for one of Inkwell's canonical ids, adding the style when it is missing.</summary>
    public string Get(string canonicalId)
    {
        if (_resolved.TryGetValue(canonicalId, out var id)) return id;
        id = Find(canonicalId);
        if (id == null)
        {
            _resolved[canonicalId] = canonicalId; // breaks cycles (Normal → next Normal) while adding
            id = Add(canonicalId);
        }
        _resolved[canonicalId] = id;
        return id;
    }

    /// <summary>The style for a heading level 1–9 (Heading 4–9 are created from Heading 3 when the document has none).</summary>
    public string Heading(int level) => Get("Heading" + MathEx.Clamp(level, 1, 9).ToString(CultureInfo.InvariantCulture));

    private string? Find(string canonicalId)
    {
        var s = _doc.Styles.Get(canonicalId);
        if (s != null) return s.Id;
        if (BuiltInNames.TryGetValue(canonicalId, out var name) && _doc.Styles.ByName(name) is { } byName) return byName.Id;
        return null;
    }

    private string Add(string canonicalId)
    {
        if (canonicalId.StartsWith("Heading", StringComparison.Ordinal) && canonicalId.Length == 8 && canonicalId[7] is >= '4' and <= '9')
            return AddHeading(canonicalId[7] - '0');
        var source = DefaultSheet.Get(canonicalId);
        if (source == null) return canonicalId == "Normal" ? _doc.Styles.DefaultParagraphStyleId : Get("Normal");
        var copy = source.Clone();
        if (copy.BasedOn is { } basedOn) copy.BasedOn = basedOn == "DefaultParagraphFont" || basedOn == "TableNormal" ? FindOrCopy(basedOn) : Get(basedOn);
        if (copy.Next is { } next) copy.Next = Get(next);
        copy.X.Attribute(Ns.W + "default")?.Remove();
        _doc.Styles.Put(copy);
        return copy.Id;
    }

    private string FindOrCopy(string id)
    {
        var s = _doc.Styles.Get(id);
        if (s != null) return s.Id;
        var type = id == "TableNormal" ? "table" : "character";
        if (_doc.Styles.DefaultOf(type) is { } d) return d.Id;
        var source = DefaultSheet.Get(id);
        if (source != null) _doc.Styles.Put(source.Clone());
        return id;
    }

    private string AddHeading(int level)
    {
        var baseId = Get("Heading3");
        var bodySize = _doc.Styles.DefaultRunProps.Size ?? 22;
        var h3Size = StyleChain.RunValue(_doc.Styles, baseId, r => r.Size) ?? bodySize + 2;
        var id = "Heading" + level.ToString(CultureInfo.InvariantCulture);
        var style = StyleSheet.Create("paragraph", id, "heading " + level.ToString(CultureInfo.InvariantCulture), baseId, Get("Normal"), quick: true, priority: 9);
        var pPr = style.ParaProps(true)!;
        pPr.KeepNext = true;
        pPr.KeepLines = true;
        pPr.OutlineLevel = level - 1;
        var rPr = style.RunProps(true)!;
        var size = level == 4 ? Math.Max(bodySize, h3Size - 2) : bodySize;
        rPr.Size = size;
        rPr.SizeCs = size;
        if (level >= 6) rPr.Italic = true;
        // Heading 3 may use small capitals: deeper levels read better without.
        if (StyleChain.RunValue(_doc.Styles, baseId, r => r.SmallCaps) == true) rPr.SmallCaps = false;
        _doc.Styles.Put(style);
        return id;
    }
}

/// <summary>Reads values along a style's basedOn chain.</summary>
internal static class StyleChain
{
    public static T? RunValue<T>(StyleSheet sheet, string? styleId, Func<RunProps, T?> get) where T : struct
    {
        foreach (var s in sheet.Chain(styleId))
        {
            var r = s.RunProps();
            if (r == null) continue;
            var v = get(r);
            if (v != null) return v;
        }
        return null;
    }

    public static string? RunString(StyleSheet sheet, string? styleId, Func<RunProps, string?> get)
    {
        foreach (var s in sheet.Chain(styleId))
        {
            var r = s.RunProps();
            if (r == null) continue;
            var v = get(r);
            if (v != null) return v;
        }
        return null;
    }

    public static T? ParaValue<T>(StyleSheet sheet, string? styleId, Func<ParaProps, T?> get) where T : struct
    {
        foreach (var s in sheet.Chain(styleId))
        {
            var p = s.ParaProps();
            if (p == null) continue;
            var v = get(p);
            if (v != null) return v;
        }
        return null;
    }

    /// <summary>True if the style is <paramref name="ancestorId"/> or based on it.</summary>
    public static bool IsOrBasedOn(StyleSheet sheet, string? styleId, string ancestorId) =>
        sheet.Chain(styleId).Any(s => string.Equals(s.Id, ancestorId, StringComparison.Ordinal));

    /// <summary>The style's name in lower case, or "".</summary>
    public static string NameOf(StyleSheet sheet, string? styleId) => sheet.Get(styleId)?.Name.ToLowerInvariant() ?? "";

    /// <summary>The heading level (1–9) of a paragraph: Heading N styles, then outline levels; 0 for body text.</summary>
    public static int HeadingLevel(Document doc, Paragraph p)
    {
        var styleId = p.Props.StyleId ?? doc.Styles.DefaultParagraphStyleId;
        foreach (var s in doc.Styles.Chain(styleId))
        {
            var level = HeadingLevelOfName(s.Id) ?? HeadingLevelOfName(s.Name.Replace(" ", ""));
            if (level != null) return level.Value;
        }
        var outline = p.Props.OutlineLevel ?? ParaValue(doc.Styles, styleId, x => x.OutlineLevel);
        return outline is >= 0 and <= 8 ? outline.Value + 1 : 0;
    }

    private static int? HeadingLevelOfName(string name) =>
        name.Length == 8 && name.StartsWith("heading", StringComparison.OrdinalIgnoreCase) && name[7] is >= '1' and <= '9' ? name[7] - '0' : null;
}

/// <summary>Kinds of list a converter creates.</summary>
internal enum ListKind
{
    Bullet,
    Ordered,
    /// <summary>No visible label (task lists, whose items start with ☐ / ☑).</summary>
    Plain,
}

/// <summary>
/// Adds list definitions to a document's numbering: one definition per kind / format, one list instance per
/// list. Ordered lists restart at their start number (w:startOverride), so separate lists never continue each
/// other's numbering.
/// </summary>
internal sealed class ListFactory
{
    private readonly Numbering _numbering;
    private readonly Dictionary<string, int> _abstracts = new(StringComparer.Ordinal);

    public ListFactory(Document doc)
    {
        _numbering = doc.Numbering;
    }

    /// <summary>
    /// A new list instance. <paramref name="format"/> (decimal, lowerLetter, upperRoman…) and
    /// <paramref name="bullet"/> override the standard definition at <paramref name="level"/>.
    /// </summary>
    public int NewList(ListKind kind, int level = 0, int start = 1, string? format = null, string? bullet = null)
    {
        level = MathEx.Clamp(level, 0, 8);
        var key = kind + "|" + (format == null && bullet == null ? "" : level.ToString(CultureInfo.InvariantCulture) + "|" + format + "|" + bullet);
        if (!_abstracts.TryGetValue(key, out var abstractId))
        {
            var def = Numbering.StandardDefinition(kind != ListKind.Ordered);
            foreach (var lvl in def.Elements(Ns.W + "lvl"))
            {
                var l = lvl.IntAttr("ilvl") ?? 0;
                if (kind == ListKind.Plain)
                {
                    lvl.Element(Ns.W + "numFmt")!.SetAttributeValue(Ox.Val, "none");
                    lvl.Element(Ns.W + "lvlText")!.SetAttributeValue(Ox.Val, "");
                    continue;
                }
                if (l != level) continue;
                if (kind == ListKind.Ordered && format != null)
                {
                    lvl.Element(Ns.W + "numFmt")!.SetAttributeValue(Ox.Val, format);
                    if (format == "bullet") lvl.Element(Ns.W + "lvlText")!.SetAttributeValue(Ox.Val, bullet ?? "•");
                }
                if (kind == ListKind.Bullet && bullet != null) lvl.Element(Ns.W + "lvlText")!.SetAttributeValue(Ox.Val, bullet);
            }
            abstractId = _numbering.AddAbstract(def);
            _abstracts[key] = abstractId;
        }
        return kind == ListKind.Ordered ? _numbering.AddNum(abstractId, start, level) : _numbering.AddNum(abstractId);
    }

    /// <summary>Adds a ready-made definition (w:abstractNum) and one instance of it.</summary>
    public int AddDefinition(XElement abstractNum, int? restartAt = null, int restartLevel = 0)
    {
        var id = _numbering.AddAbstract(abstractNum);
        return _numbering.AddNum(id, restartAt, restartLevel);
    }

    /// <summary>The left indent (twips) of list level <paramref name="level"/> in the standard definitions.</summary>
    public static int LevelIndent(int level) => 360 + MathEx.Clamp(level, 0, 8) * 360;

    /// <summary>A standard level element (for building definitions level by level).</summary>
    public static XElement Level(int level, string format, string text, int start = 1, int? left = null, int? hanging = null, string? bulletFont = null, string suffix = "tab", string jc = "left")
    {
        var lvl = new XElement(Ns.W + "lvl", new XAttribute(Ns.W + "ilvl", Ox.Str(level)),
            new XElement(Ns.W + "start", new XAttribute(Ox.Val, Ox.Str(start))),
            new XElement(Ns.W + "numFmt", new XAttribute(Ox.Val, format)));
        if (suffix != "tab") lvl.Add(new XElement(Ns.W + "suff", new XAttribute(Ox.Val, suffix)));
        lvl.Add(new XElement(Ns.W + "lvlText", new XAttribute(Ox.Val, text)),
            new XElement(Ns.W + "lvlJc", new XAttribute(Ox.Val, jc)),
            new XElement(Ns.W + "pPr", new XElement(Ns.W + "ind", new XAttribute(Ns.W + "left", Ox.Str(left ?? LevelIndent(level))),
                new XAttribute(Ns.W + "hanging", Ox.Str(hanging ?? 360)))));
        if (bulletFont != null)
            lvl.Add(new XElement(Ns.W + "rPr", new XElement(Ns.W + "rFonts", new XAttribute(Ns.W + "ascii", bulletFont), new XAttribute(Ns.W + "hAnsi", bulletFont), new XAttribute(Ns.W + "hint", "default"))));
        return lvl;
    }

    /// <summary>A definition element with the given levels (missing levels are filled with standard ones).</summary>
    public static XElement Definition(IDictionary<int, XElement> levels, bool bullets)
    {
        var standard = Numbering.StandardDefinition(bullets);
        var def = new XElement(Ns.W + "abstractNum", standard.Element(Ns.W + "nsid"), new XElement(Ns.W + "multiLevelType", new XAttribute(Ox.Val, "hybridMultilevel")));
        foreach (var lvl in standard.Elements(Ns.W + "lvl").ToList())
        {
            var l = lvl.IntAttr("ilvl") ?? 0;
            def.Add(levels.TryGetValue(l, out var custom) ? custom : lvl);
        }
        return def;
    }

    /// <summary>A bullet character that displays without a symbol font (Word's Symbol / Wingdings private-use bullets mapped to Unicode).</summary>
    public static string NormalizeBullet(string text)
    {
        if (text.Length == 0) return "•";
        var c = text[0];
        return c switch
        {
            '\uF0B7' or '·' or '\uF06C' or '\uF09F' or '\u2219' => "•",
            '\uF0A7' or '§' or '\uF06E' or '\uF0FA' or '\uF0A8' => "▪",
            'o' or '\uF06F' or '\u25CB' => "◦",
            '\uF0D8' or 'Ø' => "➢",
            '\uF076' or 'v' => "❖",
            '\uF0FC' or 'ü' => "✓",
            '\uF02D' or '-' => "–",
            _ when c >= '\uF000' && c <= '\uF0FF' => "•",
            _ => text,
        };
    }
}

/// <summary>Creates footnotes and endnotes the way Word structures them.</summary>
internal static class NoteFactory
{
    /// <summary>
    /// Adds a note with the given content to the document: its paragraphs get the note text style and the first
    /// one starts with the note's own number (a <see cref="NoteMark"/> run styled as a note reference).
    /// </summary>
    public static Note Create(Document doc, StyleMap styles, NoteKind kind, IEnumerable<Block> content)
    {
        var note = new Note(kind) { Id = doc.NextNoteId(kind) };
        var textStyle = styles.Get(kind == NoteKind.Footnote ? "FootnoteText" : "EndnoteText");
        var normal = styles.Get("Normal");
        foreach (var block in content)
        {
            if (block is Paragraph p && (p.Props.StyleId == null || p.Props.StyleId == normal)) p.Props.StyleId = textStyle;
            note.Blocks.Add(block);
        }
        if (note.Blocks.Count == 0 || note.Blocks[0] is not Paragraph)
            note.Blocks.Insert(0, new Paragraph { Props = { StyleId = textStyle } });
        var first = (Paragraph)note.Blocks[0];
        if (!first.Inlines.OfType<NoteMark>().Any())
        {
            var mark = new NoteMark { Kind = kind };
            mark.Props.StyleId = styles.Get(kind == NoteKind.Footnote ? "FootnoteReference" : "EndnoteReference");
            // Word writes the mark, a space, then the text.
            var space = new TextRun(" ");
            if (first.Inlines.Count > 0 && first.Inlines[0] is TextRun t && t.Text.StartsWith(" ", StringComparison.Ordinal)) first.Inlines.Insert(0, mark);
            else first.Inlines.InsertRange(0, new Inline[] { mark, space });
        }
        (kind == NoteKind.Footnote ? doc.Footnotes : doc.Endnotes)[note.Id] = note;
        return note;
    }

    /// <summary>The reference to a note in the text, styled as a note reference.</summary>
    public static NoteReference Reference(StyleMap styles, NoteKind kind, int id)
    {
        var r = new NoteReference { Kind = kind, Id = id };
        r.Props.StyleId = styles.Get(kind == NoteKind.Footnote ? "FootnoteReference" : "EndnoteReference");
        return r;
    }
}

/// <summary>Helpers to build paragraph content.</summary>
internal static class Content
{
    /// <summary>Appends text, merging it into the last run when that has the same formatting and link.</summary>
    public static void AppendText(List<Inline> inlines, string text, RunProps props, Hyperlink? link = null)
    {
        if (text.Length == 0) return;
        if (inlines.Count > 0 && inlines[inlines.Count - 1] is TextRun last && ReferenceEquals(last.Link, link) && last.Inserted == null &&
            last.Deleted == null && last.Props.SameFormatting(props))
        {
            last.Text += text;
            return;
        }
        inlines.Add(new TextRun(text) { Props = props.Clone(), Link = link });
    }

    /// <summary>An empty paragraph with a bottom border: a divider (Markdown's thematic break, HTML's hr).</summary>
    public static Paragraph Divider(string color = "BFBFBF")
    {
        var p = new Paragraph();
        p.Props.SetBorder("bottom", new BorderDef("single", 6, 1, color));
        return p;
    }

    /// <summary>True for an empty paragraph with a visible bottom border (and no other text).</summary>
    public static bool IsDivider(Paragraph p) =>
        p.Length == 0 && p.Props.Border("bottom") is { IsVisible: true } && p.Props.NumId == null;

    /// <summary>The usable text width of the document's last section, in twips.</summary>
    public static int TextWidth(Document doc)
    {
        var s = doc.Body.FinalSection;
        var width = s.PageWidth - s.MarginLeft - s.MarginRight;
        return width > 720 ? width : 9026;
    }

    /// <summary>Removes characters that can't appear in a document (C0 controls other than tab and Word's break characters).</summary>
    public static string CleanText(string text)
    {
        StringBuilder? sb = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var bad = c < ' ' && c != '\t' && c != TextRun.LineBreak && c != TextRun.PageBreak && c != TextRun.ColumnBreak;
            bad |= c is '\uFFFE' or '\uFFFF';
            if (bad)
            {
                sb ??= new StringBuilder(text, 0, i, text.Length);
                continue;
            }
            sb?.Append(c);
        }
        return sb?.ToString() ?? text;
    }

    /// <summary>Initials from a name ("Muhammad Noman" → "MN").</summary>
    public static string Initials(string name)
    {
        var parts = name.Split(new[] { ' ', '.', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        foreach (var part in parts.Take(3)) sb.Append(char.ToUpperInvariant(part[0]));
        return sb.ToString();
    }
}

/// <summary>Picture helpers: sizes from file headers, data: URIs, file URLs.</summary>
internal static class Pictures
{
    public const long EmuPerInch = 914400;
    public const long EmuPerTwip = 635;

    /// <summary>The largest picture a converter will read (bytes).</summary>
    public const int MaxBytes = 64 * 1024 * 1024;

    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal) { "png", "jpeg", "gif", "bmp", "tiff", "emf", "wmf" };

    /// <summary>True if the bytes are a picture format Inkwell keeps (PNG, JPEG, GIF, BMP, TIFF, EMF, WMF).</summary>
    public static bool IsSupported(byte[] data) => data.Length > 8 && Supported.Contains(MediaStore.Sniff(data, null, null).Extension);

    /// <summary>
    /// A picture in line with the text, stored in the document's media. The size is the given one (EMU) or the
    /// picture's own (pixels at its resolution, or 96 dpi), scaled down to <paramref name="maxWidthEmu"/>.
    /// Null if the bytes aren't a supported picture.
    /// </summary>
    public static DrawingObject? Create(Document doc, byte[] data, string? altText, long maxWidthEmu, long? widthEmu = null, long? heightEmu = null,
        bool useResolution = true, string? title = null)
    {
        if (!IsSupported(data)) return null;
        var (type, ext) = MediaStore.Sniff(data, null, null);
        long w, h;
        var natural = NaturalSize(data, useResolution);
        if (widthEmu is > 0 && heightEmu is > 0)
        {
            w = widthEmu.Value;
            h = heightEmu.Value;
        }
        else if (widthEmu is > 0)
        {
            w = widthEmu.Value;
            h = natural is { } n1 && n1.Width > 0 ? w * n1.Height / n1.Width : w;
        }
        else if (heightEmu is > 0)
        {
            h = heightEmu.Value;
            w = natural is { } n2 && n2.Height > 0 ? h * n2.Width / n2.Height : h;
        }
        else if (natural is { } n3)
        {
            w = n3.Width;
            h = n3.Height;
        }
        else
        {
            w = 2 * EmuPerInch;
            h = 2 * EmuPerInch;
        }
        if (maxWidthEmu > 0 && w > maxWidthEmu)
        {
            h = (long)Math.Round(h * (double)maxWidthEmu / w);
            w = maxWidthEmu;
        }
        w = Math.Max(w, 9525);
        h = Math.Max(h, 9525);
        var id = doc.Media.Add(data, type, ext);
        return new DrawingObject
        {
            MediaId = id,
            Width = w,
            Height = h,
            AltText = string.IsNullOrWhiteSpace(altText) ? null : altText,
            Title = string.IsNullOrWhiteSpace(title) ? null : title,
            Name = "Picture",
            Kind = "picture",
        };
    }

    /// <summary>The picture's own size in EMU, from its header (pixels at its resolution, or 96 dpi when unknown).</summary>
    public static (long Width, long Height)? NaturalSize(byte[] data, bool useResolution = true)
    {
        if (TryGetPixelSize(data, out var pw, out var ph, out var dx, out var dy))
        {
            if (!useResolution || dx < 30 || dx > 2400) dx = 96;
            if (!useResolution || dy < 30 || dy > 2400) dy = 96;
            return ((long)Math.Round(pw * EmuPerInch / dx), (long)Math.Round(ph * EmuPerInch / dy));
        }
        if (TryGetMetafileSize(data, out var mw, out var mh)) return (mw, mh);
        return null;
    }

    /// <summary>Pixel size and resolution (dpi; 0 when unknown) from a PNG, JPEG, GIF, BMP or TIFF header.</summary>
    public static bool TryGetPixelSize(byte[] d, out int width, out int height, out double dpiX, out double dpiY)
    {
        width = height = 0;
        dpiX = dpiY = 0;
        try
        {
            if (d.Length > 24 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47)
            {
                width = BigEndian32(d, 16);
                height = BigEndian32(d, 20);
                var pos = 8;
                while (pos + 12 <= d.Length)
                {
                    var len = BigEndian32(d, pos);
                    var type = Encoding.ASCII.GetString(d, pos + 4, 4);
                    if (type == "pHYs" && pos + 17 <= d.Length && d[pos + 16] == 1)
                    {
                        dpiX = BigEndian32(d, pos + 8) * 0.0254;
                        dpiY = BigEndian32(d, pos + 12) * 0.0254;
                    }
                    if (type == "IDAT" || type == "IEND" || len < 0) break;
                    pos += 12 + len;
                }
                return width > 0 && height > 0;
            }
            if (d.Length > 4 && d[0] == 0xFF && d[1] == 0xD8)
            {
                var pos = 2;
                while (pos + 4 < d.Length)
                {
                    if (d[pos] != 0xFF)
                    {
                        pos++;
                        continue;
                    }
                    var marker = d[pos + 1];
                    if (marker == 0xFF)
                    {
                        pos++;
                        continue;
                    }
                    if (marker == 0xD8 || marker == 0x01 || marker is >= 0xD0 and <= 0xD7)
                    {
                        pos += 2;
                        continue;
                    }
                    var len = (d[pos + 2] << 8) | d[pos + 3];
                    if (marker == 0xE0 && pos + 16 < d.Length && d[pos + 4] == (byte)'J' && d[pos + 5] == (byte)'F')
                    {
                        var units = d[pos + 11];
                        var xd = (d[pos + 12] << 8) | d[pos + 13];
                        var yd = (d[pos + 14] << 8) | d[pos + 15];
                        if (units == 1)
                        {
                            dpiX = xd;
                            dpiY = yd;
                        }
                        else if (units == 2)
                        {
                            dpiX = xd * 2.54;
                            dpiY = yd * 2.54;
                        }
                    }
                    if (marker is >= 0xC0 and <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC && pos + 8 < d.Length)
                    {
                        height = (d[pos + 5] << 8) | d[pos + 6];
                        width = (d[pos + 7] << 8) | d[pos + 8];
                        return width > 0 && height > 0;
                    }
                    if (marker == 0xD9 || marker == 0xDA) break;
                    pos += 2 + len;
                }
                return false;
            }
            if (d.Length > 10 && d[0] == 'G' && d[1] == 'I' && d[2] == 'F')
            {
                width = d[6] | (d[7] << 8);
                height = d[8] | (d[9] << 8);
                return width > 0 && height > 0;
            }
            if (d.Length > 46 && d[0] == 'B' && d[1] == 'M')
            {
                width = Math.Abs(LittleEndian32(d, 18));
                height = Math.Abs(LittleEndian32(d, 22));
                dpiX = LittleEndian32(d, 38) * 0.0254;
                dpiY = LittleEndian32(d, 42) * 0.0254;
                return width > 0 && height > 0;
            }
            if (d.Length > 16 && ((d[0] == 'I' && d[1] == 'I') || (d[0] == 'M' && d[1] == 'M')))
                return TryGetTiffSize(d, out width, out height);
        }
        catch (IndexOutOfRangeException)
        {
        }
        return false;
    }

    private static bool TryGetTiffSize(byte[] d, out int width, out int height)
    {
        width = height = 0;
        var little = d[0] == 'I';
        int U16(int p) => little ? d[p] | (d[p + 1] << 8) : (d[p] << 8) | d[p + 1];
        int U32(int p) => little ? LittleEndian32(d, p) : BigEndian32(d, p);
        var ifd = U32(4);
        if (ifd <= 0 || ifd + 2 > d.Length) return false;
        var count = U16(ifd);
        for (var i = 0; i < count; i++)
        {
            var e = ifd + 2 + i * 12;
            if (e + 12 > d.Length) break;
            var tag = U16(e);
            var type = U16(e + 2);
            var value = type == 3 ? U16(e + 8) : U32(e + 8);
            if (tag == 256) width = value;
            else if (tag == 257) height = value;
        }
        return width > 0 && height > 0;
    }

    /// <summary>The size of an EMF (its frame) or a placeable WMF, in EMU.</summary>
    public static bool TryGetMetafileSize(byte[] d, out long width, out long height)
    {
        width = height = 0;
        if (d.Length > 40 && d[0] == 1 && d[1] == 0 && d[2] == 0 && d[3] == 0 && d[40] == 0x20 && d[41] == 0x45)
        {
            // rclFrame in 0.01 mm.
            var left = LittleEndian32(d, 24);
            var top = LittleEndian32(d, 28);
            var right = LittleEndian32(d, 32);
            var bottom = LittleEndian32(d, 36);
            width = (long)Math.Round((right - left) * 360.0);
            height = (long)Math.Round((bottom - top) * 360.0);
            return width > 0 && height > 0;
        }
        if (d.Length > 22 && d[0] == 0xD7 && d[1] == 0xCD && d[2] == 0xC6 && d[3] == 0x9A)
        {
            short S16(int p) => (short)(d[p] | (d[p + 1] << 8));
            var w = S16(10) - S16(6);
            var h = S16(12) - S16(8);
            var inch = d[14] | (d[15] << 8);
            if (inch <= 0) return false;
            width = Math.Abs(w) * EmuPerInch / inch;
            height = Math.Abs(h) * EmuPerInch / inch;
            return width > 0 && height > 0;
        }
        return false;
    }

    private static int BigEndian32(byte[] d, int p) => (d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3];

    private static int LittleEndian32(byte[] d, int p) => d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24);

    /// <summary>The bytes of a data: URI (base64 or percent-encoded), or null.</summary>
    public static byte[]? DecodeDataUri(string uri)
    {
        if (!uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;
        var comma = uri.IndexOf(',');
        if (comma < 0) return null;
        var meta = uri.Substring(5, comma - 5);
        var payload = uri.Substring(comma + 1);
        try
        {
            if (meta.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
            {
                var sb = new StringBuilder(payload.Length);
                foreach (var c in Uri.UnescapeDataString(payload))
                    if (!char.IsWhiteSpace(c)) sb.Append(c);
                while (sb.Length % 4 != 0) sb.Append('=');
                return Convert.FromBase64String(sb.ToString());
            }
            return Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The local path of a file: URL ("file:///C:/x.png", "file://server/share/x.png"), or null.</summary>
    public static string? FileUrlToPath(string url)
    {
        if (!url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var uri = new Uri(url);
            return uri.IsFile ? uri.LocalPath : null;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    /// <summary>Reads a local picture file (null if it is missing, too big or not a supported picture).</summary>
    public static byte[]? ReadFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxBytes || info.Length < 8) return null;
            var data = File.ReadAllBytes(path);
            return IsSupported(data) ? data : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }
}

/// <summary>Text encodings, including the legacy Windows code pages (registered on .NET 10).</summary>
internal static class TextEncodings
{
    private static readonly object Gate = new();
    private static bool s_registered;

    private static void EnsureProviders()
    {
        if (s_registered) return;
        lock (Gate)
        {
            if (s_registered) return;
#if !NETFRAMEWORK
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
#endif
            s_registered = true;
        }
    }

    /// <summary>The encoding for a code page (Windows-1252 when the code page is unknown).</summary>
    public static Encoding Get(int codePage)
    {
        EnsureProviders();
        if (codePage <= 0) codePage = 1252;
        try
        {
            return Encoding.GetEncoding(codePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return codePage == 1252 ? Encoding.UTF8 : Get(1252);
        }
    }

    /// <summary>True if the code page is available.</summary>
    public static bool Exists(int codePage)
    {
        EnsureProviders();
        try
        {
            Encoding.GetEncoding(codePage);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>The system's ANSI code page (Windows-1252 on Western systems; 1252 when Windows reports UTF-8).</summary>
    public static int AnsiCodePage
    {
        get
        {
            int acp;
            try
            {
                acp = GetACP();
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
            {
                acp = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
            }
            return acp is 65001 or 0 ? 1252 : acp;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern int GetACP();
}
