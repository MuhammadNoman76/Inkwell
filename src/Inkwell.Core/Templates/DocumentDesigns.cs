using System.Globalization;
using System.Xml.Linq;
using Inkwell.Core.Model;

namespace Inkwell.Core.Templates;

/// <summary>
/// A document design: a small set of styles (Title, Subtitle, Heading 1–3, Body, Quote, Caption, Code and the
/// supporting ones Word expects) with real typography, built on the theme's heading and body fonts so the styles
/// follow the theme. Style ids are Word's built-in ones, so documents behave the same in Word.
/// </summary>
public sealed class DocumentDesign
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";

    public string HeadingFont { get; init; } = "Inter";
    public string BodyFont { get; init; } = "Literata";
    public string MonoFont { get; init; } = "JetBrains Mono";

    /// <summary>Body size in half-points.</summary>
    public int BodySize { get; init; } = 22;

    /// <summary>Line spacing in 240ths of a line.</summary>
    public int LineSpacing { get; init; } = 300;

    /// <summary>Space after body paragraphs, in twips.</summary>
    public int ParagraphAfter { get; init; } = 180;

    public int TitleSize { get; init; } = 60;
    public int SubtitleSize { get; init; } = 30;
    public int Heading1Size { get; init; } = 36;
    public int Heading2Size { get; init; } = 28;
    public int Heading3Size { get; init; } = 24;

    /// <summary>Heading weight: Bold, or Regular for display faces that are already heavy.</summary>
    public bool HeadingsBold { get; init; } = true;

    /// <summary>Heading 3 in small capitals with a little letter spacing.</summary>
    public bool Heading3SmallCaps { get; init; }

    public string TextColor { get; init; } = "1F2328";
    public string HeadingColor { get; init; } = "1F2A44";
    public string TitleColor { get; init; } = "1F2A44";
    public string MutedColor { get; init; } = "5E6470";
    public string AccentColor { get; init; } = "2F5DA8";
    public string CodeBackground { get; init; } = "F3F2EF";
    public string RuleColor { get; init; } = "D9D6CF";

    /// <summary>OpenType standard ligatures and kerning on by default.</summary>
    public bool Ligatures { get; init; } = true;

    /// <summary>Theme colours (accent1…6, dk2, lt2, hlink, folHlink).</summary>
    public IReadOnlyDictionary<string, string> ThemeColors { get; init; } = new Dictionary<string, string>();

    // ------------------------------------------------------------------ building

    private static XElement W(string name, params object[] content) => new(Ns.W + name, content);

    private static XAttribute A(string name, object value) => new(Ns.W + name, Convert.ToString(value, CultureInfo.InvariantCulture)!);

    private static XElement Val(string name, object value) => W(name, A("val", value));

    private static XElement ThemeFonts(bool major) => W("rFonts",
        A("asciiTheme", major ? "majorHAnsi" : "minorHAnsi"), A("eastAsiaTheme", major ? "majorEastAsia" : "minorEastAsia"),
        A("hAnsiTheme", major ? "majorHAnsi" : "minorHAnsi"), A("cstheme", major ? "majorBidi" : "minorBidi"));

    private static XElement Fonts(string name) => W("rFonts", A("ascii", name), A("hAnsi", name), A("cs", name));

    private static XElement Spacing(int? before = null, int? after = null, int? line = null, string rule = "auto")
    {
        var e = W("spacing");
        if (before != null) e.Add(A("before", before.Value));
        if (after != null) e.Add(A("after", after.Value));
        if (line != null) e.Add(A("line", line.Value), A("lineRule", rule));
        return e;
    }

    private static XElement Ind(int? left = null, int? right = null, int? hanging = null, int? firstLine = null)
    {
        var e = W("ind");
        if (left != null) e.Add(A("left", left.Value));
        if (right != null) e.Add(A("right", right.Value));
        if (hanging != null) e.Add(A("hanging", hanging.Value));
        if (firstLine != null) e.Add(A("firstLine", firstLine.Value));
        return e;
    }

    private static XElement Color(string hex) => Val("color", hex);

    private static XElement Size(int halfPoints) => new XElement(Ns.W + "sz", A("val", halfPoints));

    private static XElement SizeCs(int halfPoints) => new XElement(Ns.W + "szCs", A("val", halfPoints));

    private static Style Make(string type, string id, string name, string? basedOn, string? next, int priority, bool quick, XElement? pPr, XElement? rPr,
        bool hidden = false, bool custom = false, string? link = null, bool isDefault = false)
    {
        var s = StyleSheet.Create(type, id, name, basedOn, next, custom, quick, priority);
        if (isDefault) s.X.SetAttributeValue(Ns.W + "default", "1");
        if (link != null) s.Link = link;
        if (hidden)
        {
            SchemaOrder.Insert(s.X, W("semiHidden"), SchemaOrder.Style);
            SchemaOrder.Insert(s.X, W("unhideWhenUsed"), SchemaOrder.Style);
        }
        if (pPr != null)
        {
            SchemaOrder.Sort(pPr, SchemaOrder.ParaProps);
            SchemaOrder.Insert(s.X, pPr, SchemaOrder.Style);
        }
        if (rPr != null)
        {
            SchemaOrder.Sort(rPr, SchemaOrder.RunProps);
            SchemaOrder.Insert(s.X, rPr, SchemaOrder.Style);
        }
        return s;
    }

    /// <summary>The styles part for this design.</summary>
    public StyleSheet BuildStyleSheet()
    {
        var root = Ns.NewRoot(Ns.W + "styles");
        var sheet = new StyleSheet(new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root));
        var (dr, dp) = sheet.EnsureDefaults();
        dr.X.Add(ThemeFonts(false), Size(BodySize), SizeCs(BodySize),
            W("lang", A("val", "en-US"), A("eastAsia", "en-US"), A("bidi", "ar-SA")));
        if (Ligatures) dr.X.Add(new XElement(Ns.W14 + "ligatures", new XAttribute(Ns.W14 + "val", "standard")));
        dr.Kern = 16;
        SchemaOrder.Sort(dr.X, SchemaOrder.RunProps);
        dp.X.Add(Spacing(after: ParagraphAfter, line: LineSpacing));

        // Latent styles: Word's usual list, so Word shows its own style gallery normally.
        var latent = W("latentStyles", A("defLockedState", 0), A("defUIPriority", 99), A("defSemiHidden", 0), A("defUnhideWhenUsed", 0),
            A("defQFormat", 0), A("count", 376));
        foreach (var (name, priority, semi, quick) in LatentNames)
        {
            var ls = W("lsdException", A("name", name), A("uiPriority", priority));
            if (semi) ls.Add(A("semiHidden", 1), A("unhideWhenUsed", 1));
            if (quick) ls.Add(A("qFormat", 1));
            latent.Add(ls);
        }
        root.Element(Ns.W + "docDefaults")!.AddAfterSelf(latent);

        var headingWeight = HeadingsBold ? new object[] { W("b"), W("bCs") } : Array.Empty<object>();
        var styles = new List<Style>
        {
            Make("paragraph", "Normal", "Normal", null, null, 0, true, W("pPr", W("widowControl")),
                W("rPr", Color(TextColor)), isDefault: true),
            Make("character", "DefaultParagraphFont", "Default Paragraph Font", null, null, 1, false, null, null, hidden: true, isDefault: true),
            Make("table", "TableNormal", "Normal Table", null, null, 99, false, null, null, hidden: true, isDefault: true),
            Make("numbering", "NoList", "No List", null, null, 99, false, null, null, hidden: true, isDefault: true),

            Make("paragraph", "Title", "Title", "Normal", "Normal", 10, true,
                W("pPr", Spacing(after: 120, line: 240), W("contextualSpacing")),
                W("rPr", ThemeFonts(true), Color(TitleColor), Val("spacing", -10), Val("kern", 28), Size(TitleSize), SizeCs(TitleSize))),
            Make("paragraph", "Subtitle", "Subtitle", "Normal", "Normal", 11, true,
                W("pPr", Spacing(after: 360, line: 264)),
                W("rPr", Color(MutedColor), Size(SubtitleSize), SizeCs(SubtitleSize))),
            Make("paragraph", "Heading1", "heading 1", "Normal", "Normal", 9, true,
                W("pPr", W("keepNext"), W("keepLines"), Spacing(before: 400, after: 120, line: 240), Val("outlineLvl", 0)),
                W("rPr", new object[] { ThemeFonts(true) }.Concat(headingWeight).Concat(new object[] { Color(HeadingColor), Size(Heading1Size), SizeCs(Heading1Size) }).ToArray())),
            Make("paragraph", "Heading2", "heading 2", "Normal", "Normal", 9, true,
                W("pPr", W("keepNext"), W("keepLines"), Spacing(before: 320, after: 80, line: 240), Val("outlineLvl", 1)),
                W("rPr", new object[] { ThemeFonts(true) }.Concat(headingWeight).Concat(new object[] { Color(HeadingColor), Size(Heading2Size), SizeCs(Heading2Size) }).ToArray())),
            Make("paragraph", "Heading3", "heading 3", "Normal", "Normal", 9, true,
                W("pPr", W("keepNext"), W("keepLines"), Spacing(before: 240, after: 60, line: 240), Val("outlineLvl", 2)),
                W("rPr", new object[] { ThemeFonts(true) }.Concat(headingWeight)
                    .Concat(Heading3SmallCaps ? new object[] { W("smallCaps"), Val("spacing", 10) } : Array.Empty<object>())
                    .Concat(new object[] { Color(AccentColor), Size(Heading3Size), SizeCs(Heading3Size) }).ToArray())),
            Make("paragraph", "Quote", "Quote", "Normal", "Normal", 29, true,
                W("pPr", W("pBdr", W("left", A("val", "single"), A("sz", 18), A("space", 12), A("color", AccentColor))),
                    Spacing(before: 120, after: 240), Ind(left: 432, right: 432)),
                W("rPr", W("i"), W("iCs"), Color(MutedColor))),
            Make("paragraph", "Caption", "caption", "Normal", "Normal", 35, true,
                W("pPr", Spacing(before: 60, after: 240, line: 240)),
                W("rPr", W("i"), W("iCs"), Color(MutedColor), Size(BodySize - 4), SizeCs(BodySize - 4))),
            Make("paragraph", "Code", "Code", "Normal", "Normal", 30, true,
                W("pPr", W("shd", A("val", "clear"), A("color", "auto"), A("fill", CodeBackground)), Spacing(after: 0, line: 252), W("contextualSpacing"), Ind(left: 144, right: 144)),
                W("rPr", Fonts(MonoFont), W("noProof"), Size(Math.Max(16, BodySize - 3)), SizeCs(Math.Max(16, BodySize - 3))), custom: true),
            Make("character", "InlineCode", "Inline Code", "DefaultParagraphFont", null, 31, true, null,
                W("rPr", Fonts(MonoFont), W("noProof"), W("shd", A("val", "clear"), A("color", "auto"), A("fill", CodeBackground)), Size(Math.Max(16, BodySize - 2))), custom: true),
            Make("paragraph", "ListParagraph", "List Paragraph", "Normal", null, 34, true,
                W("pPr", Spacing(after: 80), Ind(left: 720), W("contextualSpacing")), null),
            Make("character", "Hyperlink", "Hyperlink", "DefaultParagraphFont", null, 99, false, null,
                W("rPr", W("color", A("val", AccentColor), A("themeColor", "hyperlink")), Val("u", "single")), hidden: true),
            Make("character", "FollowedHyperlink", "FollowedHyperlink", "DefaultParagraphFont", null, 99, false, null,
                W("rPr", W("color", A("val", "7A4FA8"), A("themeColor", "followedHyperlink")), Val("u", "single")), hidden: true),
            Make("paragraph", "FootnoteText", "footnote text", "Normal", null, 99, false,
                W("pPr", Spacing(after: 40, line: 240)), W("rPr", Size(BodySize - 4), SizeCs(BodySize - 4)), hidden: true, link: null),
            Make("character", "FootnoteReference", "footnote reference", "DefaultParagraphFont", null, 99, false, null,
                W("rPr", Val("vertAlign", "superscript")), hidden: true),
            Make("paragraph", "EndnoteText", "endnote text", "Normal", null, 99, false,
                W("pPr", Spacing(after: 40, line: 240)), W("rPr", Size(BodySize - 4), SizeCs(BodySize - 4)), hidden: true),
            Make("character", "EndnoteReference", "endnote reference", "DefaultParagraphFont", null, 99, false, null,
                W("rPr", Val("vertAlign", "superscript")), hidden: true),
            Make("paragraph", "Header", "header", "Normal", null, 99, false,
                W("pPr", W("tabs", W("tab", A("val", "center"), A("pos", 4680)), W("tab", A("val", "right"), A("pos", 9360))), Spacing(after: 0, line: 240)),
                W("rPr", Color(MutedColor), Size(BodySize - 4), SizeCs(BodySize - 4)), hidden: true),
            Make("paragraph", "Footer", "footer", "Normal", null, 99, false,
                W("pPr", W("tabs", W("tab", A("val", "center"), A("pos", 4680)), W("tab", A("val", "right"), A("pos", 9360))), Spacing(after: 0, line: 240)),
                W("rPr", Color(MutedColor), Size(BodySize - 4), SizeCs(BodySize - 4)), hidden: true),
            Make("paragraph", "TOCHeading", "TOC Heading", "Heading1", "Normal", 39, true,
                W("pPr", Val("outlineLvl", 9)), null, hidden: true),
            Make("paragraph", "TOC1", "toc 1", "Normal", "Normal", 39, false,
                W("pPr", Spacing(before: 120, after: 40)), W("rPr", W("b"), W("bCs")), hidden: true),
            Make("paragraph", "TOC2", "toc 2", "Normal", "Normal", 39, false,
                W("pPr", Spacing(after: 40), Ind(left: 240)), null, hidden: true),
            Make("paragraph", "TOC3", "toc 3", "Normal", "Normal", 39, false,
                W("pPr", Spacing(after: 40), Ind(left: 480)), W("rPr", Color(MutedColor)), hidden: true),
            Make("paragraph", "TableofFigures", "table of figures", "Normal", "Normal", 99, false,
                W("pPr", Spacing(after: 40)), null, hidden: true),
            Make("paragraph", "Bibliography", "Bibliography", "Normal", "Normal", 37, false,
                W("pPr", Ind(left: 720, hanging: 720)), null, hidden: true),
            Make("paragraph", "CommentText", "annotation text", "Normal", null, 99, false,
                W("pPr", Spacing(after: 40, line: 240)), W("rPr", Size(20), SizeCs(20)), hidden: true),
            Make("character", "CommentReference", "annotation reference", "DefaultParagraphFont", null, 99, false, null,
                W("rPr", Size(16), SizeCs(16)), hidden: true),
            Make("table", "TableGrid", "Table Grid", "TableNormal", null, 39, false,
                W("pPr", Spacing(after: 0, line: 240)), null),
            Make("table", "InkwellTable", "Inkwell Table", "TableNormal", null, 40, true,
                W("pPr", Spacing(before: 40, after: 40, line: 252)), null, custom: true),
        };

        // Table styles: cell margins, borders and conditional formatting.
        var normalTable = styles.First(s => s.Id == "TableNormal");
        normalTable.X.Add(W("tblPr", W("tblInd", A("w", 0), A("type", "dxa")),
            W("tblCellMar", W("top", A("w", 0), A("type", "dxa")), W("left", A("w", 108), A("type", "dxa")),
                W("bottom", A("w", 0), A("type", "dxa")), W("right", A("w", 108), A("type", "dxa")))));
        var grid = styles.First(s => s.Id == "TableGrid");
        grid.X.Add(W("tblPr", W("tblBorders",
            Border("top", "auto"), Border("left", "auto"), Border("bottom", "auto"), Border("right", "auto"), Border("insideH", "auto"), Border("insideV", "auto"))));
        var inkwell = styles.First(s => s.Id == "InkwellTable");
        inkwell.X.Add(W("tblPr",
            W("tblBorders", Border("top", HeadingColor, 8), Border("bottom", HeadingColor, 8), Border("insideH", RuleColor, 4)),
            W("tblCellMar", W("top", A("w", 72), A("type", "dxa")), W("left", A("w", 115), A("type", "dxa")),
                W("bottom", A("w", 72), A("type", "dxa")), W("right", A("w", 115), A("type", "dxa")))));
        inkwell.X.Add(W("tblStylePr", A("type", "firstRow"),
            W("rPr", W("b"), W("bCs"), Color(HeadingColor)),
            W("tcPr", W("tcBorders", Border("bottom", HeadingColor, 8)))));
        inkwell.X.Add(W("tblStylePr", A("type", "band1Horz"),
            W("tcPr", W("shd", A("val", "clear"), A("color", "auto"), A("fill", Lighten(CodeBackground))))));

        foreach (var s in styles)
        {
            SchemaOrder.Sort(s.X, SchemaOrder.Style);
            root.Add(s.X);
        }
        sheet.Changed();
        return sheet;
    }

    private static XElement Border(string side, string color, int size = 4) =>
        new(Ns.W + side, A("val", "single"), A("sz", size), A("space", 0), A("color", color));

    private static string Lighten(string hex)
    {
        if (hex.Length != 6) return "F7F7F7";
        int Channel(int i) => int.Parse(hex.Substring(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        int Mix(int c) => (int)Math.Round(c + (255 - c) * 0.45);
        return $"{Mix(Channel(0)):X2}{Mix(Channel(2)):X2}{Mix(Channel(4)):X2}";
    }

    /// <summary>The theme part for this design.</summary>
    public DocTheme BuildTheme()
    {
        var theme = DocTheme.CreateDefault(HeadingFont, BodyFont, Name);
        var colors = new Dictionary<string, string>(StringComparer.Ordinal) { ["dk2"] = HeadingColor, ["accent1"] = AccentColor, ["hlink"] = AccentColor };
        foreach (var (k, v) in ThemeColors) colors[k] = v;
        theme.SetFontsAndColors(HeadingFont, BodyFont, colors);
        return theme;
    }

    /// <summary>Word's built-in style names for latentStyles (name, priority, semi-hidden, quick style).</summary>
    private static readonly (string Name, int Priority, bool SemiHidden, bool Quick)[] LatentNames =
    {
        ("Normal", 0, false, true), ("heading 1", 9, false, true), ("heading 2", 9, true, true), ("heading 3", 9, true, true),
        ("heading 4", 9, true, true), ("heading 5", 9, true, true), ("heading 6", 9, true, true), ("heading 7", 9, true, true),
        ("heading 8", 9, true, true), ("heading 9", 9, true, true), ("toc 1", 39, true, false), ("toc 2", 39, true, false),
        ("toc 3", 39, true, false), ("caption", 35, true, true), ("Title", 10, false, true), ("Default Paragraph Font", 1, true, false),
        ("Subtitle", 11, false, true), ("Strong", 22, false, true), ("Emphasis", 20, false, true), ("Table Grid", 39, false, false),
        ("No Spacing", 1, false, true), ("List Paragraph", 34, false, true), ("Quote", 29, false, true), ("Intense Quote", 30, false, true),
        ("Bibliography", 37, true, false), ("TOC Heading", 39, true, true),
    };
}

/// <summary>The designs offered for new documents and in the Design panel.</summary>
public static class DocumentDesigns
{
    public static DocumentDesign Default { get; } = new()
    {
        Id = "inkwell",
        Name = "Inkwell",
        Description = "Literata for reading, Inter for headings",
        HeadingFont = "Inter",
        BodyFont = "Literata",
        BodySize = 22,
        LineSpacing = 300,
        ParagraphAfter = 180,
        HeadingColor = "1F2A44",
        TitleColor = "1F2A44",
        AccentColor = "2F5DA8",
        ThemeColors = new Dictionary<string, string> { ["accent2"] = "C2563A", ["accent3"] = "3E8E6E" },
    };

    public static DocumentDesign Modern { get; } = new()
    {
        Id = "modern",
        Name = "Modern",
        Description = "Clean sans-serif throughout",
        HeadingFont = "Inter",
        BodyFont = "Inter",
        BodySize = 21,
        LineSpacing = 312,
        ParagraphAfter = 160,
        HeadingColor = "111827",
        TitleColor = "111827",
        TextColor = "1F2937",
        MutedColor = "6B7280",
        AccentColor = "0F766E",
        CodeBackground = "F1F5F4",
        RuleColor = "D1D5DB",
        ThemeColors = new Dictionary<string, string> { ["accent2"] = "B45309", ["accent3"] = "4338CA" },
    };

    public static DocumentDesign Classic { get; } = new()
    {
        Id = "classic",
        Name = "Classic",
        Description = "Source Serif with Playfair Display headings",
        HeadingFont = "Playfair Display",
        BodyFont = "Source Serif 4",
        BodySize = 23,
        LineSpacing = 300,
        ParagraphAfter = 160,
        Heading1Size = 38,
        Heading2Size = 30,
        Heading3Size = 24,
        Heading3SmallCaps = true,
        HeadingColor = "2B2118",
        TitleColor = "2B2118",
        TextColor = "2A2622",
        MutedColor = "6B6158",
        AccentColor = "8C2F39",
        CodeBackground = "F5F1EA",
        RuleColor = "D8CFC2",
        ThemeColors = new Dictionary<string, string> { ["accent2"] = "2F4F6F", ["accent3"] = "6F7F3F" },
    };

    public static DocumentDesign Editorial { get; } = new()
    {
        Id = "editorial",
        Name = "Editorial",
        Description = "DM Serif Display headings with DM Sans",
        HeadingFont = "DM Serif Display",
        BodyFont = "DM Sans",
        BodySize = 21,
        LineSpacing = 312,
        ParagraphAfter = 170,
        HeadingsBold = false,
        TitleSize = 68,
        Heading1Size = 40,
        Heading2Size = 30,
        Heading3Size = 23,
        HeadingColor = "1E1B18",
        TitleColor = "1E1B18",
        TextColor = "26231F",
        MutedColor = "6F6A62",
        AccentColor = "C2563A",
        CodeBackground = "F6F1EB",
        ThemeColors = new Dictionary<string, string> { ["accent2"] = "2F5DA8", ["accent3"] = "3E8E6E" },
    };

    public static DocumentDesign Technical { get; } = new()
    {
        Id = "technical",
        Name = "Technical",
        Description = "Plus Jakarta Sans, Inter and JetBrains Mono",
        HeadingFont = "Plus Jakarta Sans",
        BodyFont = "Inter",
        BodySize = 20,
        LineSpacing = 312,
        ParagraphAfter = 150,
        Heading1Size = 32,
        Heading2Size = 26,
        Heading3Size = 22,
        HeadingColor = "0B1F3A",
        TitleColor = "0B1F3A",
        TextColor = "1C2430",
        MutedColor = "5B6576",
        AccentColor = "1D4ED8",
        CodeBackground = "EEF2F7",
        RuleColor = "CBD5E1",
        ThemeColors = new Dictionary<string, string> { ["accent2"] = "0E7490", ["accent3"] = "9333EA" },
    };

    /// <summary>Fonts every Windows PC has (Calibri, Cambria): for documents you send to people using Word.</summary>
    public static DocumentDesign Universal { get; } = new()
    {
        Id = "universal",
        Name = "Universal",
        Description = "Calibri and Cambria: looks the same everywhere",
        HeadingFont = "Cambria",
        BodyFont = "Calibri",
        BodySize = 22,
        LineSpacing = 276,
        ParagraphAfter = 160,
        HeadingColor = "1F3864",
        TitleColor = "1F3864",
        TextColor = "222222",
        AccentColor = "2E74B5",
        Ligatures = false,
        ThemeColors = new Dictionary<string, string> { ["accent2"] = "C55A11", ["accent3"] = "548235" },
    };

    public static IReadOnlyList<DocumentDesign> All { get; } = new[] { Default, Modern, Classic, Editorial, Technical, Universal };

    public static DocumentDesign Find(string? id) => All.FirstOrDefault(d => d.Id == id) ?? Default;
}
