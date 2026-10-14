using System.Globalization;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace Inkwell.Core.Model;

/// <summary>The settings part (settings.xml), kept as XML with typed accessors for what Inkwell uses.</summary>
public sealed class DocSettings
{
    public DocSettings()
        : this(new XDocument(Ns.NewRoot(Ns.W + "settings")))
    {
        DefaultTabStop = 720;
        Root.Add(new XElement(Ns.W + "characterSpacingControl", new XAttribute(Ox.Val, "doNotCompress")));
        SchemaOrder.Sort(Root, SchemaOrder.Settings);
        SetCompatibilityMode(15);
        ClrSchemeMappingDefault();
    }

    public DocSettings(XDocument doc)
    {
        Doc = doc;
        if (Doc.Root == null) Doc.Add(Ns.NewRoot(Ns.W + "settings"));
    }

    public XDocument Doc { get; private set; }

    public XElement Root => Doc.Root!;

    public void Reset(XDocument doc) => Doc = doc;

    public DocSettings Clone() => new(new XDocument(Doc));

    private XElement? El(string name) => Root.Element(Ns.W + name);

    private XElement Ensure(string name)
    {
        var e = El(name);
        if (e != null) return e;
        e = new XElement(Ns.W + name);
        SchemaOrder.Insert(Root, e, SchemaOrder.Settings);
        return e;
    }

    private void SetToggle(string name, bool on)
    {
        Root.Elements(Ns.W + name).Remove();
        if (on) Ensure(name);
    }

    /// <summary>Different headers and footers on odd and even pages (w:evenAndOddHeaders).</summary>
    public bool EvenAndOddHeaders { get => Ox.OnOff(El("evenAndOddHeaders")); set => SetToggle("evenAndOddHeaders", value); }

    /// <summary>Track changes is on (w:trackRevisions).</summary>
    public bool TrackRevisions { get => Ox.OnOff(El("trackRevisions")); set => SetToggle("trackRevisions", value); }

    /// <summary>Default tab interval in twips.</summary>
    public int DefaultTabStop
    {
        get => El("defaultTabStop").IntAttr("val") ?? 720;
        set => Ensure("defaultTabStop").SetAttributeValue(Ox.Val, Ox.Str(value));
    }

    /// <summary>Fonts are embedded in the file (w:embedTrueTypeFonts).</summary>
    public bool EmbedTrueTypeFonts { get => Ox.OnOff(El("embedTrueTypeFonts")); set => SetToggle("embedTrueTypeFonts", value); }

    public bool SaveSubsetFonts { get => Ox.OnOff(El("saveSubsetFonts")); set => SetToggle("saveSubsetFonts", value); }

    public bool MirrorMargins => Ox.OnOff(El("mirrorMargins"));

    public bool GutterAtTop => Ox.OnOff(El("gutterAtTop"));

    /// <summary>Ask the reader to update fields when the file is opened (w:updateFields).</summary>
    public bool UpdateFieldsOnOpen { get => Ox.OnOff(El("updateFields")); set => SetToggle("updateFields", value); }

    /// <summary>Word's compatibility mode (w:compat/w:compatSetting compatibilityMode): 15 = Word 2013 and later.</summary>
    public int CompatibilityMode
    {
        get
        {
            var setting = El("compat")?.Elements(Ns.W + "compatSetting").FirstOrDefault(e => e.Attr("name") == "compatibilityMode");
            return Ox.ParseInt(setting.Attr("val")) ?? 12;
        }
    }

    public void SetCompatibilityMode(int mode)
    {
        var compat = Ensure("compat");
        compat.Elements(Ns.W + "compatSetting").Where(e => e.Attr("name") == "compatibilityMode").Remove();
        compat.Add(new XElement(Ns.W + "compatSetting", new XAttribute(Ns.W + "name", "compatibilityMode"),
            new XAttribute(Ns.W + "uri", "http://schemas.microsoft.com/office/word"), new XAttribute(Ox.Val, Ox.Str(mode))));
    }

    /// <summary>Theme colour mapping (w:clrSchemeMapping): text1 → dark1 etc.</summary>
    public string MapThemeColor(string name)
    {
        var map = El("clrSchemeMapping");
        string Get(string attr, string fallback) => map.Attr(attr) ?? fallback;
        return name switch
        {
            "text1" => Get("t1", "dark1"),
            "text2" => Get("t2", "dark2"),
            "background1" => Get("bg1", "light1"),
            "background2" => Get("bg2", "light2"),
            _ => name,
        };
    }

    private void ClrSchemeMappingDefault()
    {
        var e = Ensure("clrSchemeMapping");
        e.RemoveAttributes();
        foreach (var (k, v) in new[] { ("bg1", "light1"), ("t1", "dark1"), ("bg2", "light2"), ("t2", "dark2"), ("accent1", "accent1"), ("accent2", "accent2"),
                     ("accent3", "accent3"), ("accent4", "accent4"), ("accent5", "accent5"), ("accent6", "accent6"), ("hyperlink", "hyperlink"), ("followedHyperlink", "followedHyperlink") })
            e.SetAttributeValue(Ns.W + k, v);
    }

    // ---- document variables (w:docVars): Inkwell keeps its per-document settings here; Word keeps them untouched.

    public string? Variable(string name) =>
        El("docVars")?.Elements(Ns.W + "docVar").FirstOrDefault(e => e.Attr("name") == name).Attr("val");

    public void SetVariable(string name, string? value)
    {
        var vars = El("docVars");
        vars?.Elements(Ns.W + "docVar").Where(e => e.Attr("name") == name).Remove();
        if (value == null)
        {
            if (vars != null && !vars.HasElements) vars.Remove();
            return;
        }
        vars ??= Ensure("docVars");
        vars.Add(new XElement(Ns.W + "docVar", new XAttribute(Ns.W + "name", name), new XAttribute(Ox.Val, value)));
    }

    /// <summary>Footnote numbering format from w:footnotePr (decimal by default).</summary>
    public string FootnoteFormat => El("footnotePr")?.Element(Ns.W + "numFmt").Attr("val") ?? "decimal";

    public string EndnoteFormat => El("endnotePr")?.Element(Ns.W + "numFmt").Attr("val") ?? "lowerRoman";

    public int FootnoteStart => El("footnotePr")?.Element(Ns.W + "numStart").IntAttr("val") ?? 1;

    public int EndnoteStart => El("endnotePr")?.Element(Ns.W + "numStart").IntAttr("val") ?? 1;

    /// <summary>Note ids of the separator notes listed in settings (w:footnotePr/w:footnote).</summary>
    public IEnumerable<int> SpecialNoteIds(NoteKind kind) =>
        El(kind == NoteKind.Footnote ? "footnotePr" : "endnotePr")?.Elements(Ns.W + (kind == NoteKind.Footnote ? "footnote" : "endnote"))
            .Select(e => e.IntAttr("id") ?? int.MinValue).Where(i => i != int.MinValue) ?? Enumerable.Empty<int>();

    /// <summary>Lists the separator notes in settings (Word expects footnotePr / endnotePr to name them).</summary>
    public void SetSpecialNotes(NoteKind kind, IEnumerable<int> ids)
    {
        var name = kind == NoteKind.Footnote ? "footnotePr" : "endnotePr";
        var child = kind == NoteKind.Footnote ? "footnote" : "endnote";
        var pr = El(name);
        var list = ids.ToList();
        if (pr == null && list.Count == 0) return;
        pr ??= Ensure(name);
        pr.Elements(Ns.W + child).Remove();
        var insertAt = pr.Elements().FirstOrDefault();
        foreach (var id in list)
        {
            var e = new XElement(Ns.W + child, new XAttribute(Ns.W + "id", Ox.Str(id)));
            if (insertAt != null) insertAt.AddBeforeSelf(e);
            else pr.Add(e);
        }
    }
}

/// <summary>The theme part (theme1.xml): colours and fonts the styles refer to.</summary>
public sealed class DocTheme
{
    private Dictionary<string, string>? _colors;

    public DocTheme(XDocument doc)
    {
        Doc = doc;
    }

    public XDocument Doc { get; private set; }

    public int Version { get; private set; }

    public void Reset(XDocument doc)
    {
        Doc = doc;
        _colors = null;
        Version++;
    }

    public DocTheme Clone() => new(new XDocument(Doc));

    private XElement? Elements => Doc.Root?.Element(Ns.A + "themeElements");

    public string Name => Doc.Root.Attr(XName.Get("name")) ?? "Office Theme";

    /// <summary>Theme colours by scheme name (dk1, lt1, dk2, lt2, accent1…6, hlink, folHlink) as RRGGBB.</summary>
    public IReadOnlyDictionary<string, string> Colors => _colors ??= ReadColors();

    private Dictionary<string, string> ReadColors()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        var scheme = Elements?.Element(Ns.A + "clrScheme");
        if (scheme == null) return d;
        foreach (var e in scheme.Elements())
        {
            var srgb = e.Element(Ns.A + "srgbClr")?.Attribute("val")?.Value;
            var sys = e.Element(Ns.A + "sysClr")?.Attribute("lastClr")?.Value;
            var value = srgb ?? sys;
            if (value != null) d[e.Name.LocalName] = value.ToUpperInvariant();
        }
        return d;
    }

    /// <summary>A theme colour by the name used in w:color/@w:themeColor (dark1, light1, accent1, hyperlink…).</summary>
    public string? Color(string themeColorName)
    {
        var key = themeColorName switch
        {
            "dark1" => "dk1",
            "light1" => "lt1",
            "dark2" => "dk2",
            "light2" => "lt2",
            "hyperlink" => "hlink",
            "followedHyperlink" => "folHlink",
            _ => themeColorName,
        };
        return Colors.TryGetValue(key, out var c) ? c : null;
    }

    /// <summary>The theme's heading (major) or body (minor) font for a script slot: latin, ea, cs.</summary>
    public string? Font(bool major, string slot)
    {
        var font = Elements?.Element(Ns.A + "fontScheme")?.Element(Ns.A + (major ? "majorFont" : "minorFont"));
        var name = font?.Element(Ns.A + slot)?.Attribute("typeface")?.Value;
        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>A script-specific theme font (a:font script="Arab"), used for complex scripts when the cs slot is empty.</summary>
    public string? ScriptFont(bool major, string script)
    {
        var font = Elements?.Element(Ns.A + "fontScheme")?.Element(Ns.A + (major ? "majorFont" : "minorFont"));
        return font?.Elements(Ns.A + "font").FirstOrDefault(f => f.Attribute("script")?.Value == script)?.Attribute("typeface")?.Value;
    }

    /// <summary>Resolves a w:rFonts theme reference (majorHAnsi, minorBidi, …).</summary>
    public string? ResolveFontReference(string reference)
    {
        var major = reference.StartsWith("major", StringComparison.Ordinal);
        var slot = reference.Substring(5) switch
        {
            "HAnsi" or "Ascii" => "latin",
            "EastAsia" => "ea",
            "Bidi" => "cs",
            _ => "latin",
        };
        return Font(major, slot);
    }

    /// <summary>Changes the heading and body fonts and the accent colours (applying a design).</summary>
    public void SetFontsAndColors(string majorLatin, string minorLatin, IReadOnlyDictionary<string, string>? colors)
    {
        var fonts = Elements?.Element(Ns.A + "fontScheme");
        fonts?.Element(Ns.A + "majorFont")?.Element(Ns.A + "latin")?.SetAttributeValue("typeface", majorLatin);
        fonts?.Element(Ns.A + "minorFont")?.Element(Ns.A + "latin")?.SetAttributeValue("typeface", minorLatin);
        if (colors != null)
        {
            var scheme = Elements?.Element(Ns.A + "clrScheme");
            if (scheme != null)
            {
                foreach (var (key, value) in colors)
                {
                    var e = scheme.Element(Ns.A + key);
                    if (e == null) continue;
                    e.RemoveNodes();
                    e.Add(new XElement(Ns.A + "srgbClr", new XAttribute("val", value)));
                }
            }
        }
        _colors = null;
        Version++;
    }

    /// <summary>A complete theme part (Office-compatible structure) with Inkwell's fonts and colours.</summary>
    public static DocTheme CreateDefault(string majorFont = "Literata", string minorFont = "Inter", string name = "Inkwell")
    {
        var a = Ns.A;
        XElement Clr(string n, string hex) => new(a + n, new XElement(a + "srgbClr", new XAttribute("val", hex)));
        XElement SysClr(string n, string sys, string last) => new(a + n, new XElement(a + "sysClr", new XAttribute("val", sys), new XAttribute("lastClr", last)));
        XElement FontSet(string tag, string latin) => new(a + tag,
            new XElement(a + "latin", new XAttribute("typeface", latin)),
            new XElement(a + "ea", new XAttribute("typeface", "")),
            new XElement(a + "cs", new XAttribute("typeface", "")),
            new XElement(a + "font", new XAttribute("script", "Arab"), new XAttribute("typeface", "Times New Roman")),
            new XElement(a + "font", new XAttribute("script", "Hebr"), new XAttribute("typeface", "Times New Roman")),
            new XElement(a + "font", new XAttribute("script", "Jpan"), new XAttribute("typeface", "Yu Mincho")),
            new XElement(a + "font", new XAttribute("script", "Hans"), new XAttribute("typeface", "DengXian")),
            new XElement(a + "font", new XAttribute("script", "Hant"), new XAttribute("typeface", "新細明體")),
            new XElement(a + "font", new XAttribute("script", "Hang"), new XAttribute("typeface", "맑은 고딕")),
            new XElement(a + "font", new XAttribute("script", "Thai"), new XAttribute("typeface", "Leelawadee UI")));
        XElement Solid(string scheme) => new(a + "solidFill", new XElement(a + "schemeClr", new XAttribute("val", scheme)));
        XElement Line(int w) => new(a + "ln", new XAttribute("w", Ox.Str(w)), new XAttribute("cap", "flat"), new XAttribute("cmpd", "sng"), new XAttribute("algn", "ctr"),
            Solid("phClr"), new XElement(a + "prstDash", new XAttribute("val", "solid")), new XElement(a + "miter", new XAttribute("lim", "800000")));
        XElement Effect() => new(a + "effectStyle", new XElement(a + "effectLst"));

        var theme = new XElement(a + "theme", new XAttribute(XNamespace.Xmlns + "a", a.NamespaceName), new XAttribute("name", name),
            new XElement(a + "themeElements",
                new XElement(a + "clrScheme", new XAttribute("name", name),
                    SysClr("dk1", "windowText", "000000"), SysClr("lt1", "window", "FFFFFF"),
                    Clr("dk2", "1F2A44"), Clr("lt2", "F2EFE9"),
                    Clr("accent1", "2F5DA8"), Clr("accent2", "C2563A"), Clr("accent3", "3E8E6E"),
                    Clr("accent4", "8A5CB8"), Clr("accent5", "D49A2A"), Clr("accent6", "2E8FA3"),
                    Clr("hlink", "2F5DA8"), Clr("folHlink", "7A4FA8")),
                new XElement(a + "fontScheme", new XAttribute("name", name), FontSet("majorFont", majorFont), FontSet("minorFont", minorFont)),
                new XElement(a + "fmtScheme", new XAttribute("name", name),
                    new XElement(a + "fillStyleLst", Solid("phClr"), Solid("phClr"), Solid("phClr")),
                    new XElement(a + "lnStyleLst", Line(6350), Line(12700), Line(19050)),
                    new XElement(a + "effectStyleLst", Effect(), Effect(), Effect()),
                    new XElement(a + "bgFillStyleLst", Solid("phClr"), Solid("phClr"), Solid("phClr")))),
            new XElement(a + "objectDefaults"),
            new XElement(a + "extraClrSchemeLst"));
        return new DocTheme(new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), theme));
    }
}

/// <summary>Document properties (docProps/core.xml).</summary>
public sealed class CoreProps
{
    public string? Title { get; set; }
    public string? Subject { get; set; }
    public string? Creator { get; set; }
    public string? Keywords { get; set; }
    public string? Description { get; set; }
    public string? LastModifiedBy { get; set; }
    public string? Category { get; set; }
    public string? ContentStatus { get; set; }
    public string? Revision { get; set; }
    public DateTime? Created { get; set; }
    public DateTime? Modified { get; set; }

    /// <summary>The part as loaded (other properties are kept).</summary>
    public XDocument? Original { get; set; }

    public CoreProps Clone() => new()
    {
        Title = Title,
        Subject = Subject,
        Creator = Creator,
        Keywords = Keywords,
        Description = Description,
        LastModifiedBy = LastModifiedBy,
        Category = Category,
        ContentStatus = ContentStatus,
        Revision = Revision,
        Created = Created,
        Modified = Modified,
        Original = Original == null ? null : new XDocument(Original),
    };
}

/// <summary>A picture's bytes (stored once, named by the SHA-256 of its content).</summary>
public sealed class MediaItem
{
    public MediaItem(string id, byte[] data, string contentType, string extension)
    {
        Id = id;
        Data = data;
        ContentType = contentType;
        Extension = extension;
    }

    public string Id { get; }
    public byte[] Data { get; }
    public string ContentType { get; }

    /// <summary>File extension without the dot (png, jpeg, gif, bmp, tiff, emf, wmf, svg).</summary>
    public string Extension { get; }
}

/// <summary>The pictures of a document.</summary>
public sealed class MediaStore
{
    private readonly Dictionary<string, MediaItem> _items = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public MediaItem? Get(string? id)
    {
        if (id == null) return null;
        lock (_gate) return _items.TryGetValue(id, out var m) ? m : null;
    }

    public IReadOnlyList<MediaItem> All
    {
        get
        {
            lock (_gate) return _items.Values.ToList();
        }
    }

    /// <summary>Adds bytes (or finds the identical item) and returns its id.</summary>
    public string Add(byte[] data, string? contentType = null, string? extension = null)
    {
        var (type, ext) = Sniff(data, contentType, extension);
        string id;
        using (var sha = SHA256.Create())
        {
            var hash = sha.ComputeHash(data);
            id = string.Concat(hash.Take(10).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        }
        lock (_gate)
        {
            if (!_items.ContainsKey(id)) _items[id] = new MediaItem(id, data, type, ext);
        }
        return id;
    }

    public MediaStore Clone()
    {
        var copy = new MediaStore();
        lock (_gate)
            foreach (var (k, v) in _items) copy._items[k] = v;
        return copy;
    }

    /// <summary>Content type and extension from the bytes (falls back to the given values).</summary>
    public static (string ContentType, string Extension) Sniff(byte[] data, string? contentType, string? extension)
    {
        bool Starts(params byte[] sig) => data.Length >= sig.Length && sig.Select((b, i) => data[i] == b).All(x => x);
        if (Starts(0x89, 0x50, 0x4E, 0x47)) return ("image/png", "png");
        if (Starts(0xFF, 0xD8, 0xFF)) return ("image/jpeg", "jpeg");
        if (Starts(0x47, 0x49, 0x46, 0x38)) return ("image/gif", "gif");
        if (Starts(0x42, 0x4D)) return ("image/bmp", "bmp");
        if (Starts(0x49, 0x49, 0x2A, 0x00) || Starts(0x4D, 0x4D, 0x00, 0x2A)) return ("image/tiff", "tiff");
        if (Starts(0x01, 0x00, 0x00, 0x00) && data.Length > 44 && data[40] == 0x20 && data[41] == 0x45 && data[42] == 0x4D && data[43] == 0x46) return ("image/x-emf", "emf");
        if (Starts(0xD7, 0xCD, 0xC6, 0x9A) || Starts(0x01, 0x00, 0x09, 0x00)) return ("image/x-wmf", "wmf");
        if (Starts(0x52, 0x49, 0x46, 0x46) && data.Length > 12 && data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50) return ("image/webp", "webp");
        var text = data.Length > 0 ? System.Text.Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 256)) : "";
        if (text.Contains("<svg")) return ("image/svg+xml", "svg");
        var ext = (extension ?? "").TrimStart('.').ToLowerInvariant();
        if (ext == "jpg") ext = "jpeg";
        return (contentType ?? "application/octet-stream", ext.Length > 0 ? ext : "bin");
    }
}
