using System.Xml.Linq;

namespace Inkwell.Core.Model;

/// <summary>A style (w:style), kept as XML with typed accessors.</summary>
public sealed class Style
{
    public Style(XElement x)
    {
        X = x;
    }

    public XElement X { get; }

    public string Id
    {
        get => X.Attr("styleId") ?? "";
        set => X.SetAttributeValue(Ns.W + "styleId", value);
    }

    /// <summary>paragraph, character, table or numbering.</summary>
    public string Type => X.Attr("type") ?? "paragraph";

    public string Name
    {
        get => X.Element(Ns.W + "name").Attr("val") ?? Id;
        set => SetChildVal("name", value);
    }

    public string? BasedOn { get => X.Element(Ns.W + "basedOn").Attr("val"); set => SetChildVal("basedOn", value); }
    public string? Next { get => X.Element(Ns.W + "next").Attr("val"); set => SetChildVal("next", value); }
    public string? Link { get => X.Element(Ns.W + "link").Attr("val"); set => SetChildVal("link", value); }

    public bool IsDefault => X.Attr("default") is "1" or "true" or "on";
    public bool IsCustom => X.Attr("customStyle") is "1" or "true" or "on";
    public bool Hidden => Ox.OnOff(X.Element(Ns.W + "hidden")) || Ox.OnOff(X.Element(Ns.W + "semiHidden"));
    public bool QuickStyle => Ox.OnOff(X.Element(Ns.W + "qFormat"));
    public int UiPriority => X.Element(Ns.W + "uiPriority").IntAttr("val") ?? 99;

    /// <summary>The style's paragraph properties (w:pPr), created when asked to.</summary>
    public ParaProps? ParaProps(bool create = false) => Child("pPr", create, x => new ParaProps(x));

    public RunProps? RunProps(bool create = false) => Child("rPr", create, x => new RunProps(x));

    public TableProps? TableProps(bool create = false) => Child("tblPr", create, x => new TableProps(x));

    public RowProps? RowProps(bool create = false) => Child("trPr", create, x => new RowProps(x));

    public CellProps? CellProps(bool create = false) => Child("tcPr", create, x => new CellProps(x));

    /// <summary>Conditional table formatting (w:tblStylePr) by type: firstRow, lastRow, firstCol, lastCol, band1Horz, band2Horz…</summary>
    public IEnumerable<(string Type, XElement X)> Conditional =>
        X.Elements(Ns.W + "tblStylePr").Select(e => (e.Attr("type") ?? "", e));

    private T? Child<T>(string name, bool create, Func<XElement, T> wrap) where T : class
    {
        var e = X.Element(Ns.W + name);
        if (e == null)
        {
            if (!create) return null;
            e = new XElement(Ns.W + name);
            SchemaOrder.Insert(X, e, SchemaOrder.Style);
        }
        return wrap(e);
    }

    private void SetChildVal(string name, string? value)
    {
        X.Elements(Ns.W + name).Remove();
        if (value != null) SchemaOrder.Insert(X, new XElement(Ns.W + name, new XAttribute(Ox.Val, value)), SchemaOrder.Style);
    }

    public Style Clone() => new(new XElement(X));
}

/// <summary>The styles part (styles.xml): document defaults, latent styles and styles, kept as XML.</summary>
public sealed class StyleSheet
{
    private Dictionary<string, Style>? _byId;
    private Dictionary<string, Style>? _byName;

    public StyleSheet(XDocument doc)
    {
        Doc = doc;
        if (Doc.Root == null) Doc.Add(Ns.NewRoot(Ns.W + "styles"));
    }

    public XDocument Doc { get; private set; }

    public XElement Root => Doc.Root!;

    /// <summary>Bumped on every change (the style resolver's caches key on it).</summary>
    public int Version { get; private set; }

    public void Changed()
    {
        Version++;
        _byId = null;
        _byName = null;
    }

    /// <summary>Replaces everything (undo / redo, applying a design).</summary>
    public void Reset(XDocument doc)
    {
        Doc = doc;
        Changed();
    }

    public IEnumerable<Style> All => Root.Elements(Ns.W + "style").Select(e => new Style(e));

    public Style? Get(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        _byId ??= BuildIndex();
        return _byId.TryGetValue(id!, out var s) ? s : null;
    }

    /// <summary>Finds a style by its name ("heading 1", "Title").</summary>
    public Style? ByName(string name)
    {
        _byName ??= All.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        return _byName.TryGetValue(name, out var s) ? s : null;
    }

    private Dictionary<string, Style> BuildIndex()
    {
        var d = new Dictionary<string, Style>(StringComparer.Ordinal);
        foreach (var s in All)
            if (!d.ContainsKey(s.Id)) d[s.Id] = s;
        return d;
    }

    public Style? DefaultOf(string type) => All.FirstOrDefault(s => s.Type == type && s.IsDefault);

    public string DefaultParagraphStyleId => DefaultOf("paragraph")?.Id ?? "Normal";

    /// <summary>docDefaults/rPrDefault/rPr.</summary>
    public RunProps DefaultRunProps
    {
        get
        {
            var e = Root.Element(Ns.W + "docDefaults")?.Element(Ns.W + "rPrDefault")?.Element(Ns.W + "rPr");
            return new RunProps(e ?? new XElement(Ns.W + "rPr"));
        }
    }

    /// <summary>docDefaults/pPrDefault/pPr.</summary>
    public ParaProps DefaultParaProps
    {
        get
        {
            var e = Root.Element(Ns.W + "docDefaults")?.Element(Ns.W + "pPrDefault")?.Element(Ns.W + "pPr");
            return new ParaProps(e ?? new XElement(Ns.W + "pPr"));
        }
    }

    /// <summary>Makes sure docDefaults/rPrDefault/rPr and pPrDefault/pPr exist and returns them for editing.</summary>
    public (RunProps Run, ParaProps Para) EnsureDefaults()
    {
        var dd = Root.Element(Ns.W + "docDefaults");
        if (dd == null)
        {
            dd = new XElement(Ns.W + "docDefaults");
            Root.AddFirst(dd);
        }
        var rd = dd.Element(Ns.W + "rPrDefault");
        if (rd == null)
        {
            rd = new XElement(Ns.W + "rPrDefault");
            dd.AddFirst(rd);
        }
        var r = rd.Element(Ns.W + "rPr");
        if (r == null)
        {
            r = new XElement(Ns.W + "rPr");
            rd.Add(r);
        }
        var pd = dd.Element(Ns.W + "pPrDefault");
        if (pd == null)
        {
            pd = new XElement(Ns.W + "pPrDefault");
            rd.AddAfterSelf(pd);
        }
        var p = pd.Element(Ns.W + "pPr");
        if (p == null)
        {
            p = new XElement(Ns.W + "pPr");
            pd.Add(p);
        }
        return (new RunProps(r), new ParaProps(p));
    }

    /// <summary>Adds (or replaces, by id) a style.</summary>
    public Style Put(Style style)
    {
        var existing = Root.Elements(Ns.W + "style").FirstOrDefault(e => e.Attr("styleId") == style.Id);
        if (existing != null) existing.ReplaceWith(style.X);
        else Root.Add(style.X);
        Changed();
        return style;
    }

    public void Remove(string id)
    {
        Root.Elements(Ns.W + "style").Where(e => e.Attr("styleId") == id).Remove();
        Changed();
    }

    /// <summary>A new style element.</summary>
    public static Style Create(string type, string id, string name, string? basedOn = null, string? next = null, bool custom = false, bool quick = true, int priority = 0)
    {
        var x = new XElement(Ns.W + "style", new XAttribute(Ns.W + "type", type), new XAttribute(Ns.W + "styleId", id));
        if (custom) x.Add(new XAttribute(Ns.W + "customStyle", "1"));
        x.Add(new XElement(Ns.W + "name", new XAttribute(Ox.Val, name)));
        if (basedOn != null) x.Add(new XElement(Ns.W + "basedOn", new XAttribute(Ox.Val, basedOn)));
        if (next != null) x.Add(new XElement(Ns.W + "next", new XAttribute(Ox.Val, next)));
        if (priority > 0) x.Add(new XElement(Ns.W + "uiPriority", new XAttribute(Ox.Val, Ox.Str(priority))));
        if (quick) x.Add(new XElement(Ns.W + "qFormat"));
        return new Style(x);
    }

    public StyleSheet Clone() => new(new XDocument(Doc));

    /// <summary>Inkwell's default styles (the default design).</summary>
    public static StyleSheet CreateDefault() => Templates.DocumentDesigns.Default.BuildStyleSheet();

    /// <summary>The style chain from <paramref name="id"/> up through basedOn (cycle-safe).</summary>
    public List<Style> Chain(string? id)
    {
        var chain = new List<Style>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = Get(id);
        while (current != null && seen.Add(current.Id) && chain.Count < 32)
        {
            chain.Add(current);
            current = Get(current.BasedOn);
        }
        return chain;
    }
}
