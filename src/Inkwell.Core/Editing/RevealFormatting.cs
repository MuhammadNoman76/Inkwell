using System.Globalization;
using System.Text.RegularExpressions;
using Inkwell.Core.Model;
using Inkwell.Core.Styles;

namespace Inkwell.Core.Editing;

/// <summary>One line of "Reveal formatting": a property, its value and where the value comes from.</summary>
public sealed class RevealItem
{
    public RevealItem(string group, string name, string value, string source, bool direct)
    {
        Group = group;
        Name = name;
        Value = value;
        Source = source;
        IsDirect = direct;
    }

    /// <summary>"Text" or "Paragraph".</summary>
    public string Group { get; }
    public string Name { get; }
    public string Value { get; }

    /// <summary>"Direct formatting", "Style: Heading 1", "Character style: Strong", "List", "Document defaults".</summary>
    public string Source { get; }

    /// <summary>Set directly on the text or paragraph (what "Clear formatting" removes).</summary>
    public bool IsDirect { get; }
}

/// <summary>
/// "Why does this look like this?": the formatting at a position with the source of each value (direct
/// formatting, character style, paragraph style and the styles it's based on, the list, document defaults).
/// </summary>
public static class RevealFormatting
{
    private static readonly (string Name, string[] Elements)[] RunProperties =
    {
        ("Font", new[] { "rFonts" }),
        ("Size", new[] { "sz" }),
        ("Bold", new[] { "b" }),
        ("Italic", new[] { "i" }),
        ("Underline", new[] { "u" }),
        ("Strikethrough", new[] { "strike", "dstrike" }),
        ("Colour", new[] { "color" }),
        ("Highlight", new[] { "highlight", "shd" }),
        ("Capitals", new[] { "caps", "smallCaps" }),
        ("Raised / lowered", new[] { "vertAlign", "position" }),
        ("Character spacing", new[] { "spacing", "kern", "w" }),
        ("Hidden", new[] { "vanish" }),
        ("Language", new[] { "lang" }),
    };

    private static readonly (string Name, string[] Elements)[] ParaProperties =
    {
        ("Alignment", new[] { "jc" }),
        ("Indents", new[] { "ind" }),
        ("Spacing", new[] { "spacing", "contextualSpacing" }),
        ("Keep with next", new[] { "keepNext" }),
        ("Keep lines together", new[] { "keepLines" }),
        ("Page break before", new[] { "pageBreakBefore" }),
        ("Widow and orphan control", new[] { "widowControl" }),
        ("Direction", new[] { "bidi" }),
        ("Borders", new[] { "pBdr" }),
        ("Shading", new[] { "shd" }),
        ("List", new[] { "numPr" }),
        ("Outline level", new[] { "outlineLvl" }),
        ("Tab stops", new[] { "tabs" }),
    };

    public static List<RevealItem> Explain(DocEditor editor, DocPos pos)
    {
        var doc = editor.Doc;
        var resolver = editor.Styles;
        var p = pos.Para;
        var items = new List<RevealItem>();
        var inline = p.InlineAt(Math.Max(0, Math.Min(pos.Offset, p.Length - 1))) ?? p.InlineAt(Math.Max(0, pos.Offset - 1));
        var direct = inline?.Props ?? editor.PendingProps ?? p.PropsForTypingAt(pos.Offset);
        var run = resolver.ResolveRun(direct, p);
        var para = resolver.ResolvePara(p);
        var paraStyleId = p.Props.StyleId ?? doc.Styles.DefaultParagraphStyleId;
        var paraChain = doc.Styles.Chain(paraStyleId);
        var charChain = direct.StyleId != null ? doc.Styles.Chain(direct.StyleId) : new List<Style>();
        var level = para.NumId > 0 ? doc.Numbering.Level(para.NumId, para.NumLevel) : null;

        // Paragraph style first.
        var styleName = doc.Styles.Get(paraStyleId) is { } ps ? DocEditor.DisplayName(ps) : paraStyleId;
        var basedOn = paraChain.Skip(1).Select(DocEditor.DisplayName).ToList();
        items.Add(new RevealItem("Paragraph", "Style", styleName, basedOn.Count > 0 ? "Based on " + string.Join(" › ", basedOn) : "Paragraph style", false));
        if (charChain.Count > 0) items.Add(new RevealItem("Text", "Character style", DocEditor.DisplayName(charChain[0]), "Character style", true));

        foreach (var (name, elements) in RunProperties)
        {
            var source = RunSource(doc, direct, charChain, paraChain, level, elements);
            var value = RunValue(name, run);
            if (source == null && !Interesting(name, value)) continue;
            items.Add(new RevealItem("Text", name, value, source ?? "Document defaults", source == "Direct formatting"));
        }
        foreach (var (name, elements) in ParaProperties)
        {
            var source = ParaSource(doc, p.Props, paraChain, level, elements);
            var value = ParaValue(name, para, doc, level);
            if (source == null && !Interesting(name, value)) continue;
            items.Add(new RevealItem("Paragraph", name, value, source ?? "Document defaults", source == "Direct formatting"));
        }
        return items;
    }

    private static bool Interesting(string name, string value) => name is "Font" or "Size" or "Alignment" or "Spacing" or "Indents" or "Colour";

    private static string? RunSource(Document doc, RunProps direct, List<Style> charChain, List<Style> paraChain, NumberingLevel? level, string[] elements)
    {
        bool Has(PropBag? bag) => bag != null && elements.Any(e => bag.El(e) != null);
        if (Has(direct)) return "Direct formatting";
        foreach (var s in charChain)
            if (Has(s.RunProps()))
                return "Character style: " + DocEditor.DisplayName(s);
        foreach (var s in paraChain)
            if (Has(s.RunProps()))
                return "Style: " + DocEditor.DisplayName(s);
        if (Has(doc.Styles.DefaultRunProps)) return "Document defaults";
        return null;
    }

    private static string? ParaSource(Document doc, ParaProps direct, List<Style> paraChain, NumberingLevel? level, string[] elements)
    {
        bool Has(PropBag? bag) => bag != null && elements.Any(e => bag.El(e) != null);
        if (elements.Any(e => e != "pStyle" && direct.El(e) != null)) return "Direct formatting";
        if (level?.ParaPropsXml != null && elements.Any(e => level.ParaPropsXml.Element(Ns.W + e) != null)) return "List";
        foreach (var s in paraChain)
            if (Has(s.ParaProps()))
                return "Style: " + DocEditor.DisplayName(s);
        if (Has(doc.Styles.DefaultParaProps)) return "Document defaults";
        return null;
    }

    private static string Pt(double points) => points.ToString("0.#", CultureInfo.CurrentCulture) + " pt";

    private static string Cm(int twips) => (twips / 1440.0 * 2.54).ToString("0.##", CultureInfo.CurrentCulture) + " cm";

    private static string RunValue(string name, ResolvedRun r) => name switch
    {
        "Font" => r.ComplexScript || r.Rtl ? r.CsFont : r.AsciiFont,
        "Size" => Pt(r.ComplexScript || r.Rtl ? r.SizeCs : r.Size),
        "Bold" => r.Bold ? "On" : "Off",
        "Italic" => r.Italic ? "On" : "Off",
        "Underline" => r.Underline == "none" ? "None" : r.Underline,
        "Strikethrough" => r.DoubleStrike ? "Double" : r.Strike ? "On" : "Off",
        "Colour" => r.Color is { } c ? "#" + OoxColor.ToHex(c) : "Automatic",
        "Highlight" => r.Highlight is { } h ? "#" + OoxColor.ToHex(h) : r.Shading is { } s ? "Shading #" + OoxColor.ToHex(s) : "None",
        "Capitals" => r.Caps ? "All capitals" : r.SmallCaps ? "Small capitals" : "Normal",
        "Raised / lowered" => r.VertAlign > 0 ? "Superscript" : r.VertAlign < 0 ? "Subscript" : Math.Abs(r.Position) > 0.01 ? Pt(r.Position) : "Normal",
        "Character spacing" => Math.Abs(r.Spacing) > 0.01 ? Pt(r.Spacing) : "Normal",
        "Hidden" => r.IsHidden ? "Hidden" : "Visible",
        "Language" => r.Rtl ? r.LangBidi : r.Lang,
        _ => "",
    };

    private static string ParaValue(string name, ResolvedPara p, Document doc, NumberingLevel? level) => name switch
    {
        "Alignment" => p.Justification switch { "center" => "Centred", "right" or "end" => "Right", "both" or "distribute" => "Justified", _ => "Left" },
        "Indents" => $"Left {Cm(p.IndentStart)}, right {Cm(p.IndentEnd)}" + (p.FirstLine != 0 ? (p.FirstLine > 0 ? ", first line " : ", hanging ") + Cm(Math.Abs(p.FirstLine)) : ""),
        "Spacing" => $"Before {Pt(p.SpacingBefore / 20.0)}, after {Pt(p.SpacingAfter / 20.0)}, line " + (p.LineRule == "auto" ? (p.Line / 240.0).ToString("0.##", CultureInfo.CurrentCulture) + "×" : Pt(p.Line / 20.0) + (p.LineRule == "exact" ? " exactly" : " at least")),
        "Keep with next" => p.KeepNext ? "On" : "Off",
        "Keep lines together" => p.KeepLines ? "On" : "Off",
        "Page break before" => p.PageBreakBefore ? "On" : "Off",
        "Widow and orphan control" => p.WidowControl ? "On" : "Off",
        "Direction" => p.Bidi ? "Right to left" : "Left to right",
        "Borders" => p.BorderTop != null || p.BorderBottom != null || p.BorderLeft != null || p.BorderRight != null ? "Yes" : "None",
        "Shading" => p.Shading is { } s ? "#" + OoxColor.ToHex(s) : "None",
        "List" => level == null ? "Not a list" : (level.IsBullet ? "Bullets" : level.Format + " numbers") + $", level {p.NumLevel + 1}",
        "Outline level" => p.OutlineLevel < 9 ? "Level " + (p.OutlineLevel + 1) : "Body text",
        "Tab stops" => p.Tabs.Count == 0 ? "Default" : string.Join(", ", p.Tabs.Select(t => Cm(t.Position))),
        _ => "",
    };
}

/// <summary>A style consistency problem with a one-click fix.</summary>
public sealed class ConsistencyIssue
{
    public ConsistencyIssue(string title, string detail, string fixLabel, List<Paragraph> paragraphs, Action<DocEditor> fix)
    {
        Title = title;
        Detail = detail;
        FixLabel = fixLabel;
        Paragraphs = paragraphs;
        Fix = fix;
    }

    public string Title { get; }
    public string Detail { get; }
    public string FixLabel { get; }
    public List<Paragraph> Paragraphs { get; }
    public Action<DocEditor> Fix { get; }
}

/// <summary>
/// Finds formatting that fights the styles: text with its own fonts and sizes, paragraphs that look like headings
/// but aren't, lists typed by hand, empty lines used for spacing, paragraphs with their own spacing.
/// </summary>
public static class StyleConsistency
{
    private static readonly Regex HandList = new(@"^(\d{1,2}[.)]|[•·▪◦*–-])[ \t]+\S", RegexOptions.CultureInvariant);

    public static List<ConsistencyIssue> Check(DocEditor editor, int limit = 5000)
    {
        var doc = editor.Doc;
        var resolver = editor.Styles;
        var issues = new List<ConsistencyIssue>();
        var fonts = new List<Paragraph>();
        var fakeHeadings = new List<Paragraph>();
        var handLists = new List<Paragraph>();
        var emptyRuns = new List<Paragraph>();
        var ownSpacing = new List<Paragraph>();
        var body = doc.Body.Paragraphs.Where(p => !Nav.IsReadOnly(p)).Take(limit).ToList();
        var defaultStyle = doc.Styles.DefaultParagraphStyleId;
        Paragraph? previous = null;
        foreach (var p in body)
        {
            var rp = resolver.ResolvePara(p);
            var bodyText = (p.Props.StyleId ?? defaultStyle) == defaultStyle && rp.NumId == 0 && Nav.CellOf(p) == null;
            var runs = p.Inlines.OfType<TextRun>().Where(r => r.Text.Trim().Length > 0).ToList();
            if (runs.Any(r => r.Props.El("rFonts") != null || r.Props.El("sz") != null || (r.Props.El("color") != null && r.Link == null)))
                fonts.Add(p);
            if (bodyText && runs.Count > 0)
            {
                var text = p.PlainText().Trim();
                var allBold = runs.All(r => resolver.ResolveRun(r.Props, p).Bold);
                var bigger = runs.All(r => resolver.ResolveRun(r.Props, p).Size >= resolver.ResolveRun(new RunProps(), p).Size + 2);
                if (text.Length is > 0 and <= 80 && (allBold || bigger) && !text.EndsWith(".", StringComparison.Ordinal) && !text.EndsWith(":", StringComparison.Ordinal))
                    fakeHeadings.Add(p);
                else if (HandList.IsMatch(text)) handLists.Add(p);
            }
            if (bodyText && p.IsEmpty && previous is { IsEmpty: true } && ReferenceEquals(previous.Parent, p.Parent) && p.Props.Section == null)
                emptyRuns.Add(p);
            if (bodyText && (p.Props.El("spacing") != null || p.Props.El("ind") != null)) ownSpacing.Add(p);
            previous = p;
        }

        if (fonts.Count > 0)
            issues.Add(new ConsistencyIssue($"{fonts.Count} {Plural(fonts.Count, "paragraph has", "paragraphs have")} text with its own font, size or colour",
                "The style decides these; direct changes make the document look uneven and don't follow design changes.",
                "Use the styles' fonts", fonts, e => ClearRunFonts(e, fonts)));
        if (fakeHeadings.Count > 0)
            issues.Add(new ConsistencyIssue($"{fakeHeadings.Count} {Plural(fakeHeadings.Count, "line looks", "lines look")} like a heading but {Plural(fakeHeadings.Count, "is", "are")} body text",
                "Real headings appear in the outline and the table of contents, and keep with the text after them.",
                "Make them Heading 2", fakeHeadings, e => MakeHeadings(e, fakeHeadings)));
        if (handLists.Count > 0)
            issues.Add(new ConsistencyIssue($"{handLists.Count} {Plural(handLists.Count, "line starts", "lines start")} with a typed number or bullet",
                "Real lists renumber themselves and keep their indents.",
                "Turn into lists", handLists, e => MakeLists(e, handLists)));
        if (emptyRuns.Count > 0)
            issues.Add(new ConsistencyIssue($"{emptyRuns.Count} extra empty {Plural(emptyRuns.Count, "line is", "lines are")} used for spacing",
                "Paragraph spacing does this better: empty lines shift when text moves and can start a page.",
                "Remove the extra empty lines", emptyRuns, e => RemoveParagraphs(e, emptyRuns)));
        if (ownSpacing.Count > 0)
            issues.Add(new ConsistencyIssue($"{ownSpacing.Count} body {Plural(ownSpacing.Count, "paragraph has", "paragraphs have")} its own spacing or indents",
                "They don't match the other paragraphs of the same style.",
                "Use the style's spacing", ownSpacing, e => ClearSpacing(e, ownSpacing)));
        return issues;
    }

    private static string Plural(int n, string one, string many) => n == 1 ? one : many;

    private static void ClearRunFonts(DocEditor editor, List<Paragraph> paragraphs) => editor.Edit("Use the styles' fonts", t =>
    {
        foreach (var p in paragraphs.Where(p => p.Parent != null && !Nav.IsReadOnly(p)))
        {
            t.Touch(p);
            foreach (var r in p.Inlines)
            {
                r.Props.Remove("rFonts");
                r.Props.Remove("sz");
                r.Props.Remove("szCs");
                if (r.Link == null) r.Props.Remove("color");
            }
            p.Normalize();
        }
        return null;
    });

    private static void MakeHeadings(DocEditor editor, List<Paragraph> paragraphs) => editor.Edit("Make headings", t =>
    {
        var id = editor.Doc.Styles.Get("Heading2") != null ? "Heading2" : "Heading1";
        foreach (var p in paragraphs.Where(p => p.Parent != null && !Nav.IsReadOnly(p)))
        {
            t.Touch(p);
            p.Props.StyleId = id;
            foreach (var r in p.Inlines)
            {
                r.Props.Remove("b");
                r.Props.Remove("bCs");
                r.Props.Remove("sz");
                r.Props.Remove("szCs");
                r.Props.Remove("rFonts");
            }
            p.Normalize();
        }
        return null;
    });

    private static void MakeLists(DocEditor editor, List<Paragraph> paragraphs) => editor.Edit("Make lists", t =>
    {
        var doc = editor.Doc;
        t.TouchNumbering();
        int? currentNum = null;
        Paragraph? last = null;
        bool? lastBullets = null;
        foreach (var p in paragraphs.Where(p => p.Parent != null && !Nav.IsReadOnly(p)))
        {
            var text = p.Text;
            var m = HandList.Match(text);
            if (!m.Success) continue;
            var marker = m.Groups[1].Value;
            var bullets = !char.IsDigit(marker[0]);
            // A new list unless this follows the previous converted line directly.
            if (currentNum == null || last == null || !ReferenceEquals(Nav.Prev(p), last) || lastBullets != bullets)
            {
                var abs = doc.Numbering.AddAbstract(Numbering.StandardDefinition(bullets));
                int? start = bullets ? null : int.Parse(marker.TrimEnd('.', ')'), CultureInfo.InvariantCulture);
                currentNum = doc.Numbering.AddNum(abs, start is 1 ? null : start);
            }
            t.Touch(p);
            var remove = m.Groups[1].Length;
            while (remove < text.Length && text[remove] is ' ' or '\t') remove++;
            p.RemoveRange(0, remove);
            p.Props.SetNumbering(currentNum, 0);
            if (doc.Styles.Get("ListParagraph") != null) p.Props.StyleId = "ListParagraph";
            last = p;
            lastBullets = bullets;
        }
        return null;
    });

    private static void RemoveParagraphs(DocEditor editor, List<Paragraph> paragraphs) => editor.Edit("Remove empty lines", t =>
    {
        foreach (var p in paragraphs.Where(p => p.Parent != null && p.IsEmpty && p.Parent.Count > 1))
        {
            t.Touch(p.Parent!);
            p.Parent!.Remove(p);
        }
        return null;
    });

    private static void ClearSpacing(DocEditor editor, List<Paragraph> paragraphs) => editor.Edit("Use the style's spacing", t =>
    {
        foreach (var p in paragraphs.Where(p => p.Parent != null && !Nav.IsReadOnly(p)))
        {
            t.Touch(p);
            p.Props.Remove("spacing");
            p.Props.Remove("ind");
        }
        return null;
    });
}
