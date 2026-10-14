using System.Globalization;
using System.Xml.Linq;

namespace Inkwell.Core.Model;

/// <summary>
/// A property element of WordprocessingML (w:rPr, w:pPr, w:sectPr, w:tblPr, w:trPr, w:tcPr) kept as XML, so
/// everything Inkwell doesn't model (effects, compatibility flags, extensions) survives a round trip untouched.
/// Typed accessors read and write the parts Inkwell understands; new elements are inserted in schema order.
/// </summary>
public abstract class PropBag
{
    protected PropBag(XElement x)
    {
        X = x;
    }

    /// <summary>The XML element (may have no children).</summary>
    public XElement X { get; }

    protected abstract Dictionary<string, int> Order { get; }

    public bool IsEmpty => !X.HasElements && !X.Attributes().Any(a => !a.IsNamespaceDeclaration);

    public XElement? El(string name) => X.Element(Ns.W + name);

    public XElement Ensure(string name)
    {
        var e = El(name);
        if (e != null) return e;
        e = new XElement(Ns.W + name);
        SchemaOrder.Insert(X, e, Order);
        return e;
    }

    public void Remove(string name) => X.Elements(Ns.W + name).Remove();

    /// <summary>Replaces (or removes, when null) a whole child element.</summary>
    public void SetElement(string name, XElement? element)
    {
        Remove(name);
        if (element != null) SchemaOrder.Insert(X, element, Order);
    }

    public string? Val(string name) => El(name)?.Attribute(Ox.Val)?.Value;

    public void SetVal(string name, string? value)
    {
        if (value == null) Remove(name);
        else Ensure(name).SetAttributeValue(Ox.Val, value);
    }

    public int? IntVal(string name) => Ox.ParseInt(Val(name));

    public void SetIntVal(string name, int? value) => SetVal(name, value?.ToString(CultureInfo.InvariantCulture));

    /// <summary>An on/off element: null when absent.</summary>
    public bool? Toggle(string name) => Ox.OnOffOrNull(El(name));

    public void SetToggle(string name, bool? value)
    {
        if (value == null)
        {
            Remove(name);
            return;
        }
        var e = Ensure(name);
        e.Attribute(Ox.Val)?.Remove();
        if (!value.Value) e.SetAttributeValue(Ox.Val, "0");
    }

    public string? Attr(string name, string attr) => El(name)?.Attribute(Ns.W + attr)?.Value;

    public int? IntAttr(string name, string attr) => Ox.ParseInt(Attr(name, attr));

    public void SetAttr(string name, string attr, string? value)
    {
        if (value == null)
        {
            var e = El(name);
            if (e == null) return;
            e.Attribute(Ns.W + attr)?.Remove();
            if (!e.HasAttributes && !e.HasElements) e.Remove();
            return;
        }
        Ensure(name).SetAttributeValue(Ns.W + attr, value);
    }

    public void SetIntAttr(string name, string attr, int? value) => SetAttr(name, attr, value?.ToString(CultureInfo.InvariantCulture));

    public override string ToString() => X.ToString(SaveOptions.DisableFormatting);
}

/// <summary>Who changed something and when (track changes, comments).</summary>
public sealed class RevisionInfo
{
    public int Id { get; set; }
    public string Author { get; set; } = "";
    public DateTime? Date { get; set; }

    public RevisionInfo Clone() => new() { Id = Id, Author = Author, Date = Date };

    public static RevisionInfo? Parse(XElement? e)
    {
        if (e == null) return null;
        return new RevisionInfo
        {
            Id = e.IntAttr("id") ?? 0,
            Author = e.Attr("author") ?? "",
            Date = ParseDate(e.Attr("date")),
        };
    }

    public static DateTime? ParseDate(string? text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    public static string FormatDate(DateTime d) => d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public void WriteTo(XElement e)
    {
        e.SetAttributeValue(Ns.W + "id", Ox.Str(Id));
        e.SetAttributeValue(Ns.W + "author", Author);
        if (Date is { } d) e.SetAttributeValue(Ns.W + "date", FormatDate(d));
    }

    public bool SameAuthor(RevisionInfo other) => string.Equals(Author, other.Author, StringComparison.Ordinal);
}

/// <summary>Run (character) properties: w:rPr.</summary>
public sealed class RunProps : PropBag
{
    public RunProps() : base(new XElement(Ns.W + "rPr"))
    {
    }

    public RunProps(XElement x) : base(x)
    {
    }

    protected override Dictionary<string, int> Order => SchemaOrder.RunProps;

    public RunProps Clone() => new(new XElement(X));

    public string? StyleId { get => Val("rStyle"); set => SetVal("rStyle", value); }
    public bool? Bold { get => Toggle("b"); set => SetToggle("b", value); }
    public bool? BoldCs { get => Toggle("bCs"); set => SetToggle("bCs", value); }
    public bool? Italic { get => Toggle("i"); set => SetToggle("i", value); }
    public bool? ItalicCs { get => Toggle("iCs"); set => SetToggle("iCs", value); }
    public bool? Caps { get => Toggle("caps"); set => SetToggle("caps", value); }
    public bool? SmallCaps { get => Toggle("smallCaps"); set => SetToggle("smallCaps", value); }
    public bool? Strike { get => Toggle("strike"); set => SetToggle("strike", value); }
    public bool? DoubleStrike { get => Toggle("dstrike"); set => SetToggle("dstrike", value); }
    public bool? Vanish { get => Toggle("vanish"); set => SetToggle("vanish", value); }
    public bool? NoProof { get => Toggle("noProof"); set => SetToggle("noProof", value); }
    public bool? Rtl { get => Toggle("rtl"); set => SetToggle("rtl", value); }
    public bool? ComplexScript { get => Toggle("cs"); set => SetToggle("cs", value); }

    /// <summary>Underline kind (single, double, thick, dotted, dash, wave, words, none…).</summary>
    public string? Underline { get => Val("u"); set => SetVal("u", value); }
    public string? UnderlineColor { get => Attr("u", "color"); set => SetAttr("u", "color", value); }

    /// <summary>Text colour: "RRGGBB" or "auto".</summary>
    public string? Color { get => Val("color"); set => SetVal("color", value); }
    public string? ThemeColor => Attr("color", "themeColor");

    /// <summary>Font size in half-points.</summary>
    public int? Size { get => IntVal("sz"); set => SetIntVal("sz", value); }
    public int? SizeCs { get => IntVal("szCs"); set => SetIntVal("szCs", value); }

    /// <summary>Highlight colour name (yellow, green, cyan…, none).</summary>
    public string? Highlight { get => Val("highlight"); set => SetVal("highlight", value); }

    /// <summary>superscript, subscript or baseline.</summary>
    public string? VertAlign { get => Val("vertAlign"); set => SetVal("vertAlign", value); }

    /// <summary>Character spacing in twips.</summary>
    public int? Spacing { get => IntVal("spacing"); set => SetIntVal("spacing", value); }

    /// <summary>Raised or lowered position in half-points.</summary>
    public int? Position { get => IntVal("position"); set => SetIntVal("position", value); }

    /// <summary>Kerning starts at this size (half-points).</summary>
    public int? Kern { get => IntVal("kern"); set => SetIntVal("kern", value); }

    public string? FontAscii { get => Attr("rFonts", "ascii"); set => SetAttr("rFonts", "ascii", value); }
    public string? FontHAnsi { get => Attr("rFonts", "hAnsi"); set => SetAttr("rFonts", "hAnsi", value); }
    public string? FontEastAsia { get => Attr("rFonts", "eastAsia"); set => SetAttr("rFonts", "eastAsia", value); }
    public string? FontCs { get => Attr("rFonts", "cs"); set => SetAttr("rFonts", "cs", value); }
    public string? FontHint => Attr("rFonts", "hint");

    /// <summary>Sets one font for Latin text (ascii and hAnsi) and drops theme font references for them.</summary>
    public void SetLatinFont(string? name)
    {
        SetAttr("rFonts", "asciiTheme", null);
        SetAttr("rFonts", "hAnsiTheme", null);
        FontAscii = name;
        FontHAnsi = name;
    }

    /// <summary>Sets the font for complex scripts (Arabic, Urdu, Hebrew…).</summary>
    public void SetComplexFont(string? name)
    {
        SetAttr("rFonts", "cstheme", null);
        FontCs = name;
    }

    public string? Lang { get => Attr("lang", "val"); set => SetAttr("lang", "val", value); }
    public string? LangBidi { get => Attr("lang", "bidi"); set => SetAttr("lang", "bidi", value); }
    public string? LangEastAsia { get => Attr("lang", "eastAsia"); set => SetAttr("lang", "eastAsia", value); }

    public string? ShadingFill { get => Attr("shd", "fill"); set { if (value == null) Remove("shd"); else { SetAttr("shd", "val", "clear"); SetAttr("shd", "color", "auto"); SetAttr("shd", "fill", value); } } }

    /// <summary>OpenType ligatures (w14:ligatures): none, standard, contextual, historical, discretional, standardContextual, all.</summary>
    public string? Ligatures
    {
        get => X.Element(Ns.W14 + "ligatures")?.Attribute(Ns.W14 + "val")?.Value;
        set
        {
            X.Elements(Ns.W14 + "ligatures").Remove();
            if (value != null) SchemaOrder.Insert(X, new XElement(Ns.W14 + "ligatures", new XAttribute(Ns.W14 + "val", value)), Order);
        }
    }

    // ---- track changes on the paragraph mark (only in a paragraph's w:rPr) and formatting changes

    public RevisionInfo? MarkInserted => RevisionInfo.Parse(El("ins"));
    public RevisionInfo? MarkDeleted => RevisionInfo.Parse(El("del"));

    public void SetMarkRevision(string kind, RevisionInfo? info)
    {
        Remove(kind);
        if (info == null) return;
        var e = new XElement(Ns.W + kind);
        info.WriteTo(e);
        SchemaOrder.Insert(X, e, Order);
    }

    /// <summary>A tracked formatting change: who changed it, and the properties before.</summary>
    public (RevisionInfo Info, RunProps Old)? Change
    {
        get
        {
            var c = El("rPrChange");
            if (c == null) return null;
            var old = c.Element(Ns.W + "rPr");
            return (RevisionInfo.Parse(c)!, new RunProps(old != null ? new XElement(old) : new XElement(Ns.W + "rPr")));
        }
    }

    public void SetChange(RevisionInfo? info, RunProps? old)
    {
        Remove("rPrChange");
        if (info == null) return;
        var c = new XElement(Ns.W + "rPrChange");
        info.WriteTo(c);
        var copy = old?.Clone() ?? new RunProps();
        copy.Remove("rPrChange");
        copy.Remove("ins");
        copy.Remove("del");
        c.Add(copy.X);
        SchemaOrder.Insert(X, c, Order);
    }

    /// <summary>The formatting itself (without revision marks), for comparing runs.</summary>
    public bool SameFormatting(RunProps other) => Ox.SameXml(X, other.X);
}

/// <summary>A tab stop: kind (left, center, right, decimal, bar, clear, start, end), position in twips, leader.</summary>
public readonly record struct TabStopDef(string Kind, int Position, string? Leader)
{
    public static TabStopDef Parse(XElement e) => new(e.Attr("val") ?? "left", e.IntAttr("pos") ?? 0, e.Attr("leader"));

    public XElement ToXml()
    {
        var e = new XElement(Ns.W + "tab", new XAttribute(Ns.W + "val", Kind));
        if (Leader != null && Leader != "none") e.Add(new XAttribute(Ns.W + "leader", Leader));
        e.Add(new XAttribute(Ns.W + "pos", Ox.Str(Position)));
        return e;
    }
}

/// <summary>A border line: style (single, double, dotted, dashed, thick, nil…), width in eighths of a point, spacing in points, colour.</summary>
public readonly record struct BorderDef(string Style, int Size, int Space, string? Color, string? ThemeColor = null)
{
    public bool IsVisible => Style is not ("nil" or "none" or "") && Size >= 0;

    public static BorderDef? Parse(XElement? e)
    {
        if (e == null) return null;
        return new BorderDef(e.Attr("val") ?? "nil", e.IntAttr("sz") ?? 4, e.IntAttr("space") ?? 0, e.Attr("color"), e.Attr("themeColor"));
    }

    public XElement ToXml(string name)
    {
        var e = new XElement(Ns.W + name, new XAttribute(Ns.W + "val", Style));
        if (IsVisible)
        {
            e.Add(new XAttribute(Ns.W + "sz", Ox.Str(Size)), new XAttribute(Ns.W + "space", Ox.Str(Space)), new XAttribute(Ns.W + "color", Color ?? "auto"));
            if (ThemeColor != null) e.Add(new XAttribute(Ns.W + "themeColor", ThemeColor));
        }
        return e;
    }

    public static BorderDef None => new("nil", 0, 0, null);
    public static BorderDef Single(int eighths = 4, string color = "auto") => new("single", eighths, 0, color);
}

/// <summary>Paragraph properties: w:pPr (also holds the paragraph mark's w:rPr and a section break's w:sectPr).</summary>
public sealed class ParaProps : PropBag
{
    public ParaProps() : base(new XElement(Ns.W + "pPr"))
    {
    }

    public ParaProps(XElement x) : base(x)
    {
    }

    protected override Dictionary<string, int> Order => SchemaOrder.ParaProps;

    public ParaProps Clone() => new(new XElement(X));

    public string? StyleId { get => Val("pStyle"); set => SetVal("pStyle", value); }
    public bool? KeepNext { get => Toggle("keepNext"); set => SetToggle("keepNext", value); }
    public bool? KeepLines { get => Toggle("keepLines"); set => SetToggle("keepLines", value); }
    public bool? PageBreakBefore { get => Toggle("pageBreakBefore"); set => SetToggle("pageBreakBefore", value); }
    public bool? WidowControl { get => Toggle("widowControl"); set => SetToggle("widowControl", value); }
    public bool? Bidi { get => Toggle("bidi"); set => SetToggle("bidi", value); }
    public bool? ContextualSpacing { get => Toggle("contextualSpacing"); set => SetToggle("contextualSpacing", value); }
    public bool? SuppressAutoHyphens { get => Toggle("suppressAutoHyphens"); set => SetToggle("suppressAutoHyphens", value); }

    /// <summary>left/start, center, right/end, both (justified), distribute…</summary>
    public string? Justification { get => Val("jc"); set => SetVal("jc", value); }

    /// <summary>Outline level 0–8 (9 = body text).</summary>
    public int? OutlineLevel { get => IntVal("outlineLvl"); set => SetIntVal("outlineLvl", value); }

    // ---- numbering
    public int? NumId
    {
        get => Ox.ParseInt(El("numPr")?.Element(Ns.W + "numId").Attr("val"));
        set => SetNumbering(value, NumLevel);
    }

    public int? NumLevel
    {
        get => Ox.ParseInt(El("numPr")?.Element(Ns.W + "ilvl").Attr("val"));
        set => SetNumbering(NumId, value);
    }

    public bool HasNumbering => El("numPr") != null;

    /// <summary>Sets w:numPr (numId 0 means "no list", overriding the style's list).</summary>
    public void SetNumbering(int? numId, int? level)
    {
        Remove("numPr");
        if (numId == null && level == null) return;
        var e = new XElement(Ns.W + "numPr");
        if (level != null) e.Add(new XElement(Ns.W + "ilvl", new XAttribute(Ox.Val, Ox.Str(level.Value))));
        if (numId != null) e.Add(new XElement(Ns.W + "numId", new XAttribute(Ox.Val, Ox.Str(numId.Value))));
        SchemaOrder.Insert(X, e, Order);
    }

    // ---- spacing (twips; Line is 240ths of a line when LineRule is auto)
    public int? SpacingBefore { get => IntAttr("spacing", "before"); set => SetIntAttr("spacing", "before", value); }
    public int? SpacingAfter { get => IntAttr("spacing", "after"); set => SetIntAttr("spacing", "after", value); }
    public int? SpacingLine { get => IntAttr("spacing", "line"); set => SetIntAttr("spacing", "line", value); }
    public string? SpacingLineRule { get => Attr("spacing", "lineRule"); set => SetAttr("spacing", "lineRule", value); }

    public bool? BeforeAutospacing
    {
        get => Attr("spacing", "beforeAutospacing") is { } v ? v is "1" or "true" or "on" : null;
        set => SetAttr("spacing", "beforeAutospacing", value == null ? null : value.Value ? "1" : "0");
    }

    public bool? AfterAutospacing
    {
        get => Attr("spacing", "afterAutospacing") is { } v ? v is "1" or "true" or "on" : null;
        set => SetAttr("spacing", "afterAutospacing", value == null ? null : value.Value ? "1" : "0");
    }

    // ---- indentation (twips)
    public int? IndentStart
    {
        get => IntAttr("ind", "start") ?? IntAttr("ind", "left");
        set => SetIndent("start", "left", value);
    }

    public int? IndentEnd
    {
        get => IntAttr("ind", "end") ?? IntAttr("ind", "right");
        set => SetIndent("end", "right", value);
    }

    /// <summary>First-line indent; negative = hanging indent.</summary>
    public int? FirstLine
    {
        get
        {
            var hanging = IntAttr("ind", "hanging");
            if (hanging != null) return -hanging.Value;
            return IntAttr("ind", "firstLine");
        }
        set
        {
            SetAttr("ind", "hanging", null);
            SetAttr("ind", "firstLine", null);
            SetAttr("ind", "hangingChars", null);
            SetAttr("ind", "firstLineChars", null);
            if (value == null) return;
            if (value.Value < 0) SetIntAttr("ind", "hanging", -value.Value);
            else SetIntAttr("ind", "firstLine", value.Value);
        }
    }

    private void SetIndent(string modern, string legacy, int? value)
    {
        var e = El("ind");
        if (e?.Attribute(Ns.W + modern) != null)
        {
            SetIntAttr("ind", modern, value);
            return;
        }
        SetIntAttr("ind", legacy, value);
        SetAttr("ind", legacy + "Chars", null);
    }

    // ---- borders, shading, tabs
    public BorderDef? Border(string side) => BorderDef.Parse(El("pBdr")?.Element(Ns.W + side));

    public void SetBorder(string side, BorderDef? border)
    {
        var box = El("pBdr");
        box?.Elements(Ns.W + side).Remove();
        if (border == null)
        {
            if (box != null && !box.HasElements) box.Remove();
            return;
        }
        SchemaOrder.Insert(Ensure("pBdr"), border.Value.ToXml(side), SchemaOrder.Borders);
    }

    public string? ShadingFill
    {
        get => Attr("shd", "fill");
        set
        {
            if (value == null)
            {
                Remove("shd");
                return;
            }
            SetAttr("shd", "val", "clear");
            SetAttr("shd", "color", "auto");
            SetAttr("shd", "fill", value);
        }
    }

    public List<TabStopDef> Tabs
    {
        get => El("tabs")?.Elements(Ns.W + "tab").Select(TabStopDef.Parse).ToList() ?? new List<TabStopDef>();
        set
        {
            Remove("tabs");
            if (value == null || value.Count == 0) return;
            SetElement("tabs", new XElement(Ns.W + "tabs", value.OrderBy(t => t.Position).Select(t => t.ToXml())));
        }
    }

    // ---- the paragraph mark and section break

    /// <summary>The paragraph mark's run properties (w:pPr/w:rPr), created when <paramref name="create"/>.</summary>
    public RunProps? MarkProps(bool create = false)
    {
        var e = El("rPr");
        if (e == null)
        {
            if (!create) return null;
            e = Ensure("rPr");
        }
        return new RunProps(e);
    }

    /// <summary>The section break that ends at this paragraph (w:pPr/w:sectPr), or null.</summary>
    public SectProps? Section
    {
        get
        {
            var e = El("sectPr");
            return e == null ? null : new SectProps(e);
        }
        set
        {
            Remove("sectPr");
            if (value != null) SchemaOrder.Insert(X, value.X, Order);
        }
    }

    public (RevisionInfo Info, ParaProps Old)? Change
    {
        get
        {
            var c = El("pPrChange");
            if (c == null) return null;
            var old = c.Element(Ns.W + "pPr");
            return (RevisionInfo.Parse(c)!, new ParaProps(old != null ? new XElement(old) : new XElement(Ns.W + "pPr")));
        }
    }

    public void SetChange(RevisionInfo? info, ParaProps? old)
    {
        Remove("pPrChange");
        if (info == null) return;
        var c = new XElement(Ns.W + "pPrChange");
        info.WriteTo(c);
        var copy = old?.Clone() ?? new ParaProps();
        copy.Remove("rPr");
        copy.Remove("sectPr");
        copy.Remove("pPrChange");
        c.Add(copy.X);
        SchemaOrder.Insert(X, c, Order);
    }

    /// <summary>The formatting only (no mark properties, section break or revision), for comparisons and copying.</summary>
    public ParaProps FormattingOnly()
    {
        var copy = Clone();
        copy.Remove("rPr");
        copy.Remove("sectPr");
        copy.Remove("pPrChange");
        return copy;
    }
}

/// <summary>Section properties: w:sectPr (page size, margins, columns, headers and footers…).</summary>
public sealed class SectProps : PropBag
{
    public SectProps() : base(new XElement(Ns.W + "sectPr"))
    {
    }

    public SectProps(XElement x) : base(x)
    {
    }

    protected override Dictionary<string, int> Order => SchemaOrder.SectProps;

    public SectProps Clone() => new(new XElement(X));

    /// <summary>A4 portrait with 1-inch margins, or US Letter in the United States / Canada.</summary>
    public static SectProps CreateDefault(bool letter)
    {
        var s = new SectProps();
        s.PageWidth = letter ? 12240 : 11906;
        s.PageHeight = letter ? 15840 : 16838;
        s.SetMargins(1440, 1440, 1440, 1440, 708, 708, 0);
        s.ColumnSpace = 708;
        return s;
    }

    /// <summary>nextPage, continuous, evenPage, oddPage, nextColumn.</summary>
    public string BreakType { get => Val("type") ?? "nextPage"; set => SetVal("type", value == "nextPage" ? null : value); }

    public int PageWidth { get => IntAttr("pgSz", "w") ?? 12240; set => SetIntAttr("pgSz", "w", value); }
    public int PageHeight { get => IntAttr("pgSz", "h") ?? 15840; set => SetIntAttr("pgSz", "h", value); }

    public bool Landscape
    {
        get => Attr("pgSz", "orient") == "landscape";
        set => SetAttr("pgSz", "orient", value ? "landscape" : null);
    }

    public int MarginTop { get => IntAttr("pgMar", "top") ?? 1440; set => SetIntAttr("pgMar", "top", value); }
    public int MarginBottom { get => IntAttr("pgMar", "bottom") ?? 1440; set => SetIntAttr("pgMar", "bottom", value); }
    public int MarginLeft { get => IntAttr("pgMar", "left") ?? 1440; set => SetIntAttr("pgMar", "left", value); }
    public int MarginRight { get => IntAttr("pgMar", "right") ?? 1440; set => SetIntAttr("pgMar", "right", value); }
    public int HeaderDistance { get => IntAttr("pgMar", "header") ?? 720; set => SetIntAttr("pgMar", "header", value); }
    public int FooterDistance { get => IntAttr("pgMar", "footer") ?? 720; set => SetIntAttr("pgMar", "footer", value); }
    public int Gutter { get => IntAttr("pgMar", "gutter") ?? 0; set => SetIntAttr("pgMar", "gutter", value); }

    public void SetMargins(int top, int right, int bottom, int left, int header, int footer, int gutter)
    {
        var e = Ensure("pgMar");
        e.RemoveAttributes();
        e.Add(new XAttribute(Ns.W + "top", Ox.Str(top)), new XAttribute(Ns.W + "right", Ox.Str(right)), new XAttribute(Ns.W + "bottom", Ox.Str(bottom)),
            new XAttribute(Ns.W + "left", Ox.Str(left)), new XAttribute(Ns.W + "header", Ox.Str(header)), new XAttribute(Ns.W + "footer", Ox.Str(footer)),
            new XAttribute(Ns.W + "gutter", Ox.Str(gutter)));
    }

    public int ColumnCount { get => Math.Max(1, IntAttr("cols", "num") ?? 1); set => SetIntAttr("cols", "num", value <= 1 ? null : value); }
    public int ColumnSpace { get => IntAttr("cols", "space") ?? 720; set => SetIntAttr("cols", "space", value); }
    public bool ColumnSeparator { get => Attr("cols", "sep") is "1" or "true" or "on"; set => SetAttr("cols", "sep", value ? "1" : null); }

    /// <summary>Unequal columns: (width, space after) in twips; empty for equal columns.</summary>
    public List<(int Width, int Space)> ColumnWidths
    {
        get
        {
            var cols = El("cols");
            if (cols == null || cols.Attr("equalWidth") is not ("0" or "false" or "off")) return new List<(int, int)>();
            return cols.Elements(Ns.W + "col").Select(c => (c.IntAttr("w") ?? 0, c.IntAttr("space") ?? 0)).ToList();
        }
    }

    public bool TitlePage { get => Toggle("titlePg") == true; set => SetToggle("titlePg", value ? true : null); }

    public int? PageNumberStart { get => IntAttr("pgNumType", "start"); set => SetIntAttr("pgNumType", "start", value); }

    /// <summary>Page number format (decimal, lowerRoman, upperRoman, lowerLetter, upperLetter).</summary>
    public string? PageNumberFormat { get => Attr("pgNumType", "fmt"); set => SetAttr("pgNumType", "fmt", value); }

    /// <summary>top, center, both, bottom: vertical alignment of text on the page.</summary>
    public string? VerticalAlignment { get => Val("vAlign"); set => SetVal("vAlign", value); }

    public bool Bidi { get => Toggle("bidi") == true; set => SetToggle("bidi", value ? true : null); }

    /// <summary>Header or footer relationship id for a kind (default, first, even).</summary>
    public string? Reference(bool header, string kind) =>
        X.Elements(Ns.W + (header ? "headerReference" : "footerReference")).FirstOrDefault(e => (e.Attr("type") ?? "default") == kind)?.Attribute(Ns.R + "id")?.Value;

    public void SetReference(bool header, string kind, string? relId)
    {
        // headerReference / footerReference come first in w:sectPr, headers before footers.
        var refs = X.Elements().Where(e => e.Name == Ns.W + "headerReference" || e.Name == Ns.W + "footerReference").ToList();
        foreach (var r in refs) r.Remove();
        var local = header ? "headerReference" : "footerReference";
        refs.RemoveAll(e => e.Name.LocalName == local && (e.Attr("type") ?? "default") == kind);
        if (relId != null) refs.Add(new XElement(Ns.W + local, new XAttribute(Ns.W + "type", kind), new XAttribute(Ns.R + "id", relId)));
        var ordered = refs.Where(e => e.Name.LocalName == "headerReference").Concat(refs.Where(e => e.Name.LocalName == "footerReference")).ToArray();
        X.AddFirst(ordered);
    }

    public IEnumerable<(bool Header, string Kind, string RelId)> References =>
        X.Elements().Where(e => e.Name == Ns.W + "headerReference" || e.Name == Ns.W + "footerReference")
            .Select(e => (e.Name.LocalName == "headerReference", e.Attr("type") ?? "default", e.Attribute(Ns.R + "id")?.Value ?? ""));

    /// <summary>The section's layout without its header / footer references (to compare page setups).</summary>
    public string LayoutKey()
    {
        var copy = Clone();
        copy.Remove("headerReference");
        copy.Remove("footerReference");
        copy.Remove("type");
        return copy.X.ToString(SaveOptions.DisableFormatting);
    }
}

/// <summary>A width with a unit: dxa (twips), pct (fiftieths of a percent, or "50%"), auto, nil.</summary>
public readonly record struct TableWidth(string Type, int Value)
{
    public static TableWidth Parse(XElement? e)
    {
        if (e == null) return new TableWidth("auto", 0);
        var type = e.Attr("type") ?? "dxa";
        var raw = e.Attr("w") ?? "0";
        if (raw.EndsWith("%", StringComparison.Ordinal) && double.TryParse(raw.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
            return new TableWidth("pct", (int)Math.Round(percent * 50));
        return new TableWidth(type, Ox.ParseInt(raw) ?? 0);
    }

    public XElement ToXml(string name) =>
        new(Ns.W + name, new XAttribute(Ns.W + "w", Ox.Str(Value)), new XAttribute(Ns.W + "type", Type));

    /// <summary>The width in twips for a given available width (twips); null for auto.</summary>
    public double? Resolve(double available) => Type switch
    {
        "dxa" => Value,
        "pct" => available * Value / 5000.0,
        _ => null,
    };
}

/// <summary>Table properties: w:tblPr.</summary>
public sealed class TableProps : PropBag
{
    public TableProps() : base(new XElement(Ns.W + "tblPr"))
    {
    }

    public TableProps(XElement x) : base(x)
    {
    }

    protected override Dictionary<string, int> Order => SchemaOrder.TableProps;

    public TableProps Clone() => new(new XElement(X));

    public string? StyleId { get => Val("tblStyle"); set => SetVal("tblStyle", value); }

    public TableWidth Width { get => TableWidth.Parse(El("tblW")); set => SetElement("tblW", value.ToXml("tblW")); }

    public TableWidth? Indent { get => El("tblInd") is { } e ? TableWidth.Parse(e) : null; set => SetElement("tblInd", value?.ToXml("tblInd")); }

    /// <summary>left/start, center, right/end.</summary>
    public string? Alignment { get => Val("jc"); set => SetVal("jc", value); }

    /// <summary>fixed or autofit.</summary>
    public string? Layout { get => Attr("tblLayout", "type"); set => SetAttr("tblLayout", "type", value); }

    public bool BidiVisual { get => Toggle("bidiVisual") == true; set => SetToggle("bidiVisual", value ? true : null); }

    public BorderDef? Border(string side) => BorderDef.Parse(El("tblBorders")?.Element(Ns.W + side) ?? El("tblBorders")?.Element(Ns.W + AltSide(side)));

    public void SetBorder(string side, BorderDef? border)
    {
        var box = El("tblBorders");
        box?.Elements(Ns.W + side).Remove();
        box?.Elements(Ns.W + AltSide(side)).Remove();
        if (border == null)
        {
            if (box != null && !box.HasElements) box.Remove();
            return;
        }
        SchemaOrder.Insert(Ensure("tblBorders"), border.Value.ToXml(side), SchemaOrder.Borders);
    }

    internal static string AltSide(string side) => side switch
    {
        "left" => "start",
        "start" => "left",
        "right" => "end",
        "end" => "right",
        _ => side,
    };

    public string? ShadingFill { get => Attr("shd", "fill"); }

    /// <summary>Default cell margin (twips) on one side: top, left/start, bottom, right/end.</summary>
    public int? CellMargin(string side)
    {
        var box = El("tblCellMar");
        var e = box?.Element(Ns.W + side) ?? box?.Element(Ns.W + AltSide(side));
        return e?.IntAttr("w");
    }

    public void SetCellMargin(string side, int? twips)
    {
        var box = El("tblCellMar");
        box?.Elements(Ns.W + side).Remove();
        box?.Elements(Ns.W + AltSide(side)).Remove();
        if (twips == null)
        {
            if (box != null && !box.HasElements) box.Remove();
            return;
        }
        SchemaOrder.Insert(Ensure("tblCellMar"), new TableWidth("dxa", twips.Value).ToXml(side), SchemaOrder.Margins);
    }

    /// <summary>Which parts of a table style apply (w:tblLook).</summary>
    public (bool FirstRow, bool LastRow, bool FirstColumn, bool LastColumn, bool BandedRows, bool BandedColumns) Look
    {
        get
        {
            var e = El("tblLook");
            if (e == null) return (true, false, true, false, true, false);
            bool Flag(string name, int bit)
            {
                var a = e.Attr(name);
                if (a != null) return a is "1" or "true" or "on";
                var hex = e.Attr("val");
                return hex != null && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) && (v & bit) != 0;
            }
            return (Flag("firstRow", 0x20), Flag("lastRow", 0x40), Flag("firstColumn", 0x80), Flag("lastColumn", 0x100),
                !Flag("noHBand", 0x200), !Flag("noVBand", 0x400));
        }
        set
        {
            var e = new XElement(Ns.W + "tblLook");
            var bits = (value.FirstRow ? 0x20 : 0) | (value.LastRow ? 0x40 : 0) | (value.FirstColumn ? 0x80 : 0) | (value.LastColumn ? 0x100 : 0) |
                       (value.BandedRows ? 0 : 0x200) | (value.BandedColumns ? 0 : 0x400);
            e.Add(new XAttribute(Ns.W + "val", bits.ToString("X4", CultureInfo.InvariantCulture)),
                new XAttribute(Ns.W + "firstRow", value.FirstRow ? "1" : "0"), new XAttribute(Ns.W + "lastRow", value.LastRow ? "1" : "0"),
                new XAttribute(Ns.W + "firstColumn", value.FirstColumn ? "1" : "0"), new XAttribute(Ns.W + "lastColumn", value.LastColumn ? "1" : "0"),
                new XAttribute(Ns.W + "noHBand", value.BandedRows ? "0" : "1"), new XAttribute(Ns.W + "noVBand", value.BandedColumns ? "0" : "1"));
            SetElement("tblLook", e);
        }
    }

    /// <summary>Floating table position (w:tblpPr): Inkwell lays these out in line, but keeps the settings.</summary>
    public bool IsFloating => El("tblpPr") != null;
}

/// <summary>Table row properties: w:trPr.</summary>
public sealed class RowProps : PropBag
{
    public RowProps() : base(new XElement(Ns.W + "trPr"))
    {
    }

    public RowProps(XElement x) : base(x)
    {
    }

    protected override Dictionary<string, int> Order => SchemaOrder.RowProps;

    public RowProps Clone() => new(new XElement(X));

    /// <summary>Repeat this row at the top of each page (w:tblHeader).</summary>
    public bool IsHeader { get => Toggle("tblHeader") == true; set => SetToggle("tblHeader", value ? true : null); }

    /// <summary>Don't split this row across pages (w:cantSplit).</summary>
    public bool CantSplit { get => Toggle("cantSplit") == true; set => SetToggle("cantSplit", value ? true : null); }

    /// <summary>Row height in twips and its rule (atLeast, exact, auto).</summary>
    public (int Height, string Rule)? Height
    {
        get
        {
            var e = El("trHeight");
            if (e == null) return null;
            return (e.IntAttr("val") ?? 0, e.Attr("hRule") ?? "atLeast");
        }
        set
        {
            Remove("trHeight");
            if (value == null) return;
            var e = new XElement(Ns.W + "trHeight", new XAttribute(Ns.W + "val", Ox.Str(value.Value.Height)));
            if (value.Value.Rule != "atLeast") e.Add(new XAttribute(Ns.W + "hRule", value.Value.Rule));
            SetElement("trHeight", e);
        }
    }

    public int GridBefore { get => IntVal("gridBefore") ?? 0; set => SetIntVal("gridBefore", value == 0 ? null : value); }
    public int GridAfter { get => IntVal("gridAfter") ?? 0; set => SetIntVal("gridAfter", value == 0 ? null : value); }

    public RevisionInfo? Inserted => RevisionInfo.Parse(El("ins"));
    public RevisionInfo? Deleted => RevisionInfo.Parse(El("del"));
}

/// <summary>Table cell properties: w:tcPr.</summary>
public sealed class CellProps : PropBag
{
    public CellProps() : base(new XElement(Ns.W + "tcPr"))
    {
    }

    public CellProps(XElement x) : base(x)
    {
    }

    protected override Dictionary<string, int> Order => SchemaOrder.CellProps;

    public CellProps Clone() => new(new XElement(X));

    public TableWidth Width { get => TableWidth.Parse(El("tcW")); set => SetElement("tcW", value.ToXml("tcW")); }

    public int GridSpan { get => Math.Max(1, IntVal("gridSpan") ?? 1); set => SetIntVal("gridSpan", value <= 1 ? null : value); }

    /// <summary>Vertical merge: null (none), "restart" (first cell) or "continue".</summary>
    public string? VerticalMerge
    {
        get
        {
            var e = El("vMerge");
            if (e == null) return null;
            return e.Attr("val") ?? "continue";
        }
        set
        {
            Remove("vMerge");
            if (value == null) return;
            var e = new XElement(Ns.W + "vMerge");
            if (value == "restart") e.Add(new XAttribute(Ox.Val, "restart"));
            SetElement("vMerge", e);
        }
    }

    /// <summary>Legacy horizontal merge (w:hMerge): "restart" or "continue".</summary>
    public string? HorizontalMerge => El("hMerge") is { } e ? e.Attr("val") ?? "continue" : null;

    /// <summary>top, center, bottom.</summary>
    public string? VerticalAlignment { get => Val("vAlign"); set => SetVal("vAlign", value); }

    public string? ShadingFill
    {
        get => Attr("shd", "fill");
        set
        {
            if (value == null)
            {
                Remove("shd");
                return;
            }
            SetAttr("shd", "val", "clear");
            SetAttr("shd", "color", "auto");
            SetAttr("shd", "fill", value);
        }
    }

    public string? ShadingThemeFill => Attr("shd", "themeFill");

    public BorderDef? Border(string side) => BorderDef.Parse(El("tcBorders")?.Element(Ns.W + side) ?? El("tcBorders")?.Element(Ns.W + TableProps.AltSide(side)));

    public void SetBorder(string side, BorderDef? border)
    {
        var box = El("tcBorders");
        box?.Elements(Ns.W + side).Remove();
        box?.Elements(Ns.W + TableProps.AltSide(side)).Remove();
        if (border == null)
        {
            if (box != null && !box.HasElements) box.Remove();
            return;
        }
        SchemaOrder.Insert(Ensure("tcBorders"), border.Value.ToXml(side), SchemaOrder.Borders);
    }

    public int? Margin(string side)
    {
        var box = El("tcMar");
        var e = box?.Element(Ns.W + side) ?? box?.Element(Ns.W + TableProps.AltSide(side));
        return e?.IntAttr("w");
    }

    public bool NoWrap => Toggle("noWrap") == true;

    /// <summary>Text direction (btLr, tbRl…); Inkwell shows these cells horizontally but keeps the setting.</summary>
    public string? TextDirection => Val("textDirection");
}
