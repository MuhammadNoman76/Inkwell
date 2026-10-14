using System.Xml.Linq;
using Inkwell.Core.Model;

namespace Inkwell.Core.Styles;

/// <summary>Which conditional parts of a table style apply to a cell (w:tblStylePr types).</summary>
public sealed class TableContext
{
    public TableContext(string? styleId, IReadOnlyList<string> conditions)
    {
        StyleId = styleId;
        Conditions = conditions;
    }

    public string? StyleId { get; }

    /// <summary>Applicable types in increasing priority: wholeTable, band1Vert…, band1Horz…, firstCol, lastCol, firstRow, lastRow, nwCell….</summary>
    public IReadOnlyList<string> Conditions { get; }

    public string Key => (StyleId ?? "") + "|" + string.Join(",", Conditions);
}

/// <summary>
/// Resolves effective paragraph and character formatting through Word's style hierarchy: document defaults →
/// table style → paragraph style (basedOn chain) → numbering → character style → direct formatting. Toggle
/// properties (bold, italic, caps…) flip when several style levels turn them on, as in Word.
/// </summary>
public sealed class StyleResolver
{
    private readonly Document _doc;
    private int _stamp = -1;
    private readonly Dictionary<string, RunState> _paraChainRuns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RunState> _charChainRuns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ParaState> _paraChainParas = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (ParaState Para, RunState Run)> _tableStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResolvedPara> _paraCache = new(StringComparer.Ordinal);
    private RunState? _defaultRun;
    private ParaState? _defaultPara;

    public StyleResolver(Document doc)
    {
        _doc = doc;
    }

    public Document Document => _doc;

    private void Check()
    {
        var stamp = _doc.Styles.Version * 7919 + _doc.Numbering.Version * 31 + _doc.Theme.Version * 131071;
        if (stamp == _stamp) return;
        _stamp = stamp;
        _paraChainRuns.Clear();
        _charChainRuns.Clear();
        _paraChainParas.Clear();
        _tableStates.Clear();
        _paraCache.Clear();
        _defaultRun = null;
        _defaultPara = null;
        _doc.Numbering.Styles ??= _doc.Styles;
    }

    /// <summary>Forgets cached results (call after editing style XML without bumping its version).</summary>
    public void Invalidate() => _stamp = -1;

    private RunState DefaultRun => _defaultRun ??= RunState.From(_doc.Styles.DefaultRunProps.X);

    private ParaState DefaultPara => _defaultPara ??= ParaState.From(_doc.Styles.DefaultParaProps.X);

    // ------------------------------------------------------------------ chains

    private RunState ParaChainRun(string styleId)
    {
        if (_paraChainRuns.TryGetValue(styleId, out var s)) return s;
        s = new RunState();
        foreach (var style in Enumerable.Reverse(_doc.Styles.Chain(styleId)))
            if (style.Type == "paragraph" && style.X.Element(Ns.W + "rPr") is { } rPr) s.Apply(rPr);
        _paraChainRuns[styleId] = s;
        return s;
    }

    private RunState CharChainRun(string styleId)
    {
        if (_charChainRuns.TryGetValue(styleId, out var s)) return s;
        s = new RunState();
        foreach (var style in Enumerable.Reverse(_doc.Styles.Chain(styleId)))
            if (style.Type == "character" && style.X.Element(Ns.W + "rPr") is { } rPr) s.Apply(rPr);
        _charChainRuns[styleId] = s;
        return s;
    }

    private ParaState ParaChainPara(string styleId)
    {
        if (_paraChainParas.TryGetValue(styleId, out var s)) return s;
        s = new ParaState();
        foreach (var style in Enumerable.Reverse(_doc.Styles.Chain(styleId)))
            if (style.Type == "paragraph" && style.X.Element(Ns.W + "pPr") is { } pPr) s.Apply(pPr);
        _paraChainParas[styleId] = s;
        return s;
    }

    /// <summary>A table style's paragraph and run properties for the given conditions (whole table + conditional parts).</summary>
    private (ParaState Para, RunState Run) TableState(TableContext table)
    {
        if (_tableStates.TryGetValue(table.Key, out var cached)) return cached;
        var para = new ParaState();
        var run = new RunState();
        var chain = Enumerable.Reverse(_doc.Styles.Chain(table.StyleId)).Where(s => s.Type == "table").ToList();
        foreach (var style in chain)
        {
            if (style.X.Element(Ns.W + "pPr") is { } p) para.Apply(p);
            if (style.X.Element(Ns.W + "rPr") is { } r) run.Apply(r);
        }
        foreach (var condition in table.Conditions)
        {
            foreach (var style in chain)
            {
                foreach (var (type, x) in style.Conditional)
                {
                    if (type != condition) continue;
                    if (x.Element(Ns.W + "pPr") is { } p) para.Apply(p);
                    if (x.Element(Ns.W + "rPr") is { } r) run.Apply(r);
                }
            }
        }
        var result = (para, run);
        _tableStates[table.Key] = result;
        return result;
    }

    private string ParagraphStyleId(Paragraph p)
    {
        var id = p.Props.StyleId;
        if (id != null && _doc.Styles.Get(id) is { Type: "paragraph" }) return id;
        return _doc.Styles.DefaultParagraphStyleId;
    }

    // ------------------------------------------------------------------ paragraphs

    /// <summary>The effective paragraph formatting.</summary>
    public ResolvedPara ResolvePara(Paragraph p, TableContext? table = null)
    {
        Check();
        var key = p.Props.X.ToString(SaveOptions.DisableFormatting) + "\u0001" + table?.Key;
        if (_paraCache.TryGetValue(key, out var hit)) return hit;

        var styleId = ParagraphStyleId(p);
        var state = DefaultPara.Clone();
        var tableState = table?.StyleId != null ? TableState(table) : default;
        // Word: a table style's paragraph formatting beats the Normal style (but no other style).
        var tableOverNormal = table?.StyleId != null && styleId == _doc.Styles.DefaultParagraphStyleId;
        if (table?.StyleId != null && !tableOverNormal) state.Merge(tableState.Para);

        var chain = ParaChainPara(styleId);
        if (tableOverNormal)
        {
            chain = chain.Clone();
            chain.Merge(tableState.Para);
        }
        var direct = ParaState.From(p.Props.X);
        var directNumbering = direct.NumId != null || direct.NumLevel != null;
        var numId = direct.NumId ?? chain.NumId ?? 0;
        var level = direct.NumLevel ?? chain.NumLevel ?? 0;
        var lvl = numId > 0 ? _doc.Numbering.Level(numId, level) : null;
        if (directNumbering)
        {
            state.Merge(chain);
            if (lvl?.ParaPropsXml != null) state.Apply(lvl.ParaPropsXml);
        }
        else
        {
            if (lvl?.ParaPropsXml != null) state.Apply(lvl.ParaPropsXml);
            state.Merge(chain);
        }
        state.Merge(direct);

        var result = state.Finish(styleId, lvl == null ? 0 : numId, level);
        result.Shading = OoxColor.Resolve(p.Props.El("shd") ?? state.ShadingElement, "fill", "themeFill", "themeFillTint", "themeFillShade", _doc.Theme, _doc.Settings);
        if (result.Shading == null && state.ShadingElement != null)
            result.Shading = OoxColor.Resolve(state.ShadingElement, "fill", "themeFill", "themeFillTint", "themeFillShade", _doc.Theme, _doc.Settings);

        // The paragraph mark: defaults → table → paragraph style → mark's own formatting.
        var mark = DefaultRun.Clone();
        if (table?.StyleId != null && !tableOverNormal) mark.MergeToggles(tableState.Run, toggle: true);
        mark.MergeToggles(ParaChainRun(styleId), toggle: true);
        if (tableOverNormal) mark.MergeToggles(tableState.Run, toggle: false);
        if (p.Props.El("rPr") is { } markX)
        {
            var markStyle = markX.Element(Ns.W + "rStyle").Attr("val");
            if (markStyle != null) mark.MergeToggles(CharChainRun(markStyle), toggle: true);
            mark.MergeToggles(RunState.From(markX), toggle: false);
        }
        result.Mark = Finish(mark, null);
        if (_paraCache.Count > 4096) _paraCache.Clear();
        _paraCache[key] = result;
        return result;
    }

    // ------------------------------------------------------------------ runs

    /// <summary>The effective character formatting of a run in a paragraph.</summary>
    public ResolvedRun ResolveRun(RunProps direct, Paragraph p, TableContext? table = null) =>
        ResolveRun(direct.X, ParagraphStyleId(p), table);

    public ResolveRunCache CreateCache(Paragraph p, TableContext? table) => new(this, ParagraphStyleId(p), table);

    internal ResolvedRun ResolveRun(XElement direct, string paraStyleId, TableContext? table)
    {
        Check();
        var state = DefaultRun.Clone();
        var tableOverNormal = table?.StyleId != null && paraStyleId == _doc.Styles.DefaultParagraphStyleId;
        if (table?.StyleId != null && !tableOverNormal) state.MergeToggles(TableState(table).Run, toggle: true);
        state.MergeToggles(ParaChainRun(paraStyleId), toggle: true);
        if (tableOverNormal) state.MergeToggles(TableState(table!).Run, toggle: false);
        var charStyle = direct.Element(Ns.W + "rStyle").Attr("val");
        if (charStyle != null && _doc.Styles.Get(charStyle) is { Type: "character" }) state.MergeToggles(CharChainRun(charStyle), toggle: true);
        state.MergeToggles(RunState.From(direct), toggle: false);
        var result = Finish(state, charStyle);
        return result;
    }

    /// <summary>The formatting of a list label: the paragraph mark's formatting plus the level's w:rPr.</summary>
    public ResolvedRun ResolveLabel(Paragraph p, NumberingLevel level, TableContext? table = null)
    {
        Check();
        var styleId = ParagraphStyleId(p);
        var state = DefaultRun.Clone();
        if (table?.StyleId != null) state.MergeToggles(TableState(table).Run, toggle: true);
        state.MergeToggles(ParaChainRun(styleId), toggle: true);
        if (p.Props.El("rPr") is { } markX) state.MergeToggles(RunState.From(markX), toggle: false);
        if (level.RunPropsXml != null) state.MergeToggles(RunState.From(level.RunPropsXml), toggle: false);
        return Finish(state, null);
    }

    /// <summary>The run formatting a character style alone gives (for the style gallery and Reveal formatting).</summary>
    public ResolvedRun ResolveStyleRun(string styleId)
    {
        Check();
        var style = _doc.Styles.Get(styleId);
        var state = DefaultRun.Clone();
        if (style?.Type == "character") state.MergeToggles(CharChainRun(styleId), toggle: true);
        else state.MergeToggles(ParaChainRun(style?.Type == "paragraph" ? styleId : _doc.Styles.DefaultParagraphStyleId), toggle: true);
        return Finish(state, style?.Type == "character" ? styleId : null);
    }

    private ResolvedRun Finish(RunState s, string? charStyle)
    {
        var theme = _doc.Theme;
        var r = new ResolvedRun
        {
            Bold = s.Bold ?? false,
            Italic = s.Italic ?? false,
            BoldCs = s.BoldCs ?? s.Bold ?? false,
            ItalicCs = s.ItalicCs ?? s.Italic ?? false,
            Caps = s.Caps ?? false,
            SmallCaps = s.SmallCaps ?? false,
            Strike = s.Strike ?? false,
            DoubleStrike = s.DoubleStrike ?? false,
            Vanish = s.Vanish ?? false,
            SpecVanish = s.SpecVanish ?? false,
            WebHidden = s.WebHidden ?? false,
            NoProof = s.NoProof ?? false,
            Rtl = s.Rtl ?? false,
            ComplexScript = s.Cs ?? false,
            Emboss = s.Emboss ?? false,
            Imprint = s.Imprint ?? false,
            Outline = s.Outline ?? false,
            Shadow = s.Shadow ?? false,
            Size = (s.Size ?? 20) / 2.0,
            SizeCs = (s.SizeCs ?? s.Size ?? 20) / 2.0,
            Underline = s.Underline ?? "none",
            VertAlign = s.VertAlign switch { "superscript" => 1, "subscript" => 2, _ => 0 },
            Position = (s.Position ?? 0) / 2.0,
            Spacing = (s.Spacing ?? 0) / 20.0,
            WidthScale = s.WidthScale ?? 100,
            KernFrom = (s.Kern ?? 0) / 2.0,
            Lang = s.Lang ?? "en-US",
            LangBidi = s.LangBidi ?? "ar-SA",
            LangEastAsia = s.LangEastAsia ?? "zh-CN",
            Ligatures = s.Ligatures ?? "none",
            CharStyleId = charStyle,
            Hint = s.Hint,
        };
        if (r.Underline == "none" && s.Underline == null) r.Underline = "none";
        r.Color = OoxColor.Resolve(s.ColorElement, "val", "themeColor", "themeTint", "themeShade", theme, _doc.Settings);
        r.UnderlineColor = s.UnderlineColorHex != null ? OoxColor.Parse(s.UnderlineColorHex) : null;
        r.Highlight = OoxColor.Highlight(s.Highlight);
        r.Shading = s.ShadingElement != null
            ? OoxColor.Resolve(s.ShadingElement, "fill", "themeFill", "themeFillTint", "themeFillShade", theme, _doc.Settings)
            : null;

        string Font(string? name, string? themeRef, bool major, string slot, string fallback)
        {
            if (themeRef != null)
            {
                var t = theme.ResolveFontReference(themeRef);
                if (!string.IsNullOrEmpty(t)) return t!;
            }
            if (!string.IsNullOrEmpty(name)) return name!;
            return theme.Font(major, slot) ?? fallback;
        }
        r.AsciiFont = Font(s.Ascii, s.AsciiTheme, false, "latin", "Calibri");
        r.HAnsiFont = Font(s.HAnsi, s.HAnsiTheme, false, "latin", r.AsciiFont);
        r.EastAsiaFont = Font(s.EastAsia, s.EastAsiaTheme, false, "ea", r.HAnsiFont);
        var cs = s.CsTheme != null ? theme.ResolveFontReference(s.CsTheme) : s.CsFont;
        if (string.IsNullOrEmpty(cs))
        {
            // An empty theme slot: use the theme's font for the script of the bidi language.
            var major = s.CsTheme?.StartsWith("major", StringComparison.Ordinal) == true;
            var script = ScriptOf(r.LangBidi);
            cs = script != null ? theme.ScriptFont(major, script) : null;
        }
        r.CsFont = string.IsNullOrEmpty(cs) ? "Times New Roman" : cs!;
        return r;
    }

    private static string? ScriptOf(string lang)
    {
        var l = lang.ToLowerInvariant();
        if (l.StartsWith("ar", StringComparison.Ordinal) || l.StartsWith("ur", StringComparison.Ordinal) || l.StartsWith("fa", StringComparison.Ordinal) ||
            l.StartsWith("ps", StringComparison.Ordinal) || l.StartsWith("sd", StringComparison.Ordinal) || l.StartsWith("ug", StringComparison.Ordinal)) return "Arab";
        if (l.StartsWith("he", StringComparison.Ordinal) || l.StartsWith("yi", StringComparison.Ordinal)) return "Hebr";
        if (l.StartsWith("th", StringComparison.Ordinal)) return "Thai";
        if (l.StartsWith("hi", StringComparison.Ordinal) || l.StartsWith("mr", StringComparison.Ordinal) || l.StartsWith("ne", StringComparison.Ordinal)) return "Deva";
        if (l.StartsWith("bn", StringComparison.Ordinal)) return "Beng";
        if (l.StartsWith("ta", StringComparison.Ordinal)) return "Taml";
        return null;
    }

    // ------------------------------------------------------------------ states

    /// <summary>Character properties being accumulated (null = not set at this level).</summary>
    internal sealed class RunState
    {
        public bool? Bold, BoldCs, Italic, ItalicCs, Caps, SmallCaps, Strike, DoubleStrike, Vanish, SpecVanish, WebHidden, NoProof, Rtl, Cs, Emboss, Imprint, Outline, Shadow;
        public int? Size, SizeCs, Position, Spacing, Kern;
        public double? WidthScale;
        public string? Ascii, HAnsi, EastAsia, CsFont, AsciiTheme, HAnsiTheme, EastAsiaTheme, CsTheme, Hint;
        public string? Underline, UnderlineColorHex, Highlight, VertAlign, Lang, LangBidi, LangEastAsia, Ligatures;
        public XElement? ColorElement, ShadingElement;

        public RunState Clone() => (RunState)MemberwiseClone();

        public static RunState From(XElement rPr)
        {
            var s = new RunState();
            s.Apply(rPr);
            return s;
        }

        public void Apply(XElement rPr)
        {
            foreach (var e in rPr.Elements())
            {
                if (e.Name.Namespace == Ns.W14)
                {
                    if (e.Name.LocalName == "ligatures") Ligatures = e.Attribute(Ns.W14 + "val")?.Value;
                    continue;
                }
                if (e.Name.Namespace != Ns.W) continue;
                switch (e.Name.LocalName)
                {
                    case "b": Bold = Ox.OnOff(e); break;
                    case "bCs": BoldCs = Ox.OnOff(e); break;
                    case "i": Italic = Ox.OnOff(e); break;
                    case "iCs": ItalicCs = Ox.OnOff(e); break;
                    case "caps": Caps = Ox.OnOff(e); break;
                    case "smallCaps": SmallCaps = Ox.OnOff(e); break;
                    case "strike": Strike = Ox.OnOff(e); break;
                    case "dstrike": DoubleStrike = Ox.OnOff(e); break;
                    case "vanish": Vanish = Ox.OnOff(e); break;
                    case "specVanish": SpecVanish = Ox.OnOff(e); break;
                    case "webHidden": WebHidden = Ox.OnOff(e); break;
                    case "noProof": NoProof = Ox.OnOff(e); break;
                    case "rtl": Rtl = Ox.OnOff(e); break;
                    case "cs": Cs = Ox.OnOff(e); break;
                    case "emboss": Emboss = Ox.OnOff(e); break;
                    case "imprint": Imprint = Ox.OnOff(e); break;
                    case "outline": Outline = Ox.OnOff(e); break;
                    case "shadow": Shadow = Ox.OnOff(e); break;
                    case "sz": Size = e.IntAttr("val") ?? Size; break;
                    case "szCs": SizeCs = e.IntAttr("val") ?? SizeCs; break;
                    case "position": Position = e.IntAttr("val") ?? Position; break;
                    case "spacing": Spacing = e.IntAttr("val") ?? Spacing; break;
                    case "kern": Kern = e.IntAttr("val") ?? Kern; break;
                    case "w": WidthScale = e.IntAttr("val") ?? WidthScale; break;
                    case "color": ColorElement = e; break;
                    case "shd": ShadingElement = e; break;
                    case "highlight": Highlight = e.Attr("val"); break;
                    case "vertAlign": VertAlign = e.Attr("val"); break;
                    case "u":
                        Underline = e.Attr("val") ?? "single";
                        UnderlineColorHex = e.Attr("color");
                        break;
                    case "lang":
                        Lang = e.Attr("val") ?? Lang;
                        LangBidi = e.Attr("bidi") ?? LangBidi;
                        LangEastAsia = e.Attr("eastAsia") ?? LangEastAsia;
                        break;
                    case "rFonts":
                        Slot(e, "ascii", "asciiTheme", ref Ascii, ref AsciiTheme);
                        Slot(e, "hAnsi", "hAnsiTheme", ref HAnsi, ref HAnsiTheme);
                        Slot(e, "eastAsia", "eastAsiaTheme", ref EastAsia, ref EastAsiaTheme);
                        Slot(e, "cs", "cstheme", ref CsFont, ref CsTheme);
                        Hint = e.Attr("hint") ?? Hint;
                        break;
                }
            }
        }

        private static void Slot(XElement e, string nameAttr, string themeAttr, ref string? name, ref string? theme)
        {
            var t = e.Attr(themeAttr);
            var n = e.Attr(nameAttr);
            if (t != null)
            {
                theme = t;
                name = n;
            }
            else if (n != null)
            {
                name = n;
                theme = null;
            }
        }

        /// <summary>Overlays another level. Style levels (toggle = true) flip toggle properties they turn on.</summary>
        public void MergeToggles(RunState o, bool toggle)
        {
            Bold = Toggle(Bold, o.Bold, toggle);
            BoldCs = Toggle(BoldCs, o.BoldCs, toggle);
            Italic = Toggle(Italic, o.Italic, toggle);
            ItalicCs = Toggle(ItalicCs, o.ItalicCs, toggle);
            Caps = Toggle(Caps, o.Caps, toggle);
            SmallCaps = Toggle(SmallCaps, o.SmallCaps, toggle);
            Strike = Toggle(Strike, o.Strike, toggle);
            DoubleStrike = Toggle(DoubleStrike, o.DoubleStrike, toggle);
            Vanish = Toggle(Vanish, o.Vanish, toggle);
            Emboss = Toggle(Emboss, o.Emboss, toggle);
            Imprint = Toggle(Imprint, o.Imprint, toggle);
            Outline = Toggle(Outline, o.Outline, toggle);
            Shadow = Toggle(Shadow, o.Shadow, toggle);
            SpecVanish = o.SpecVanish ?? SpecVanish;
            WebHidden = o.WebHidden ?? WebHidden;
            NoProof = o.NoProof ?? NoProof;
            Rtl = o.Rtl ?? Rtl;
            Cs = o.Cs ?? Cs;
            Size = o.Size ?? Size;
            SizeCs = o.SizeCs ?? SizeCs;
            Position = o.Position ?? Position;
            Spacing = o.Spacing ?? Spacing;
            Kern = o.Kern ?? Kern;
            WidthScale = o.WidthScale ?? WidthScale;
            ColorElement = o.ColorElement ?? ColorElement;
            ShadingElement = o.ShadingElement ?? ShadingElement;
            Highlight = o.Highlight ?? Highlight;
            VertAlign = o.VertAlign ?? VertAlign;
            if (o.Underline != null)
            {
                Underline = o.Underline;
                UnderlineColorHex = o.UnderlineColorHex;
            }
            Lang = o.Lang ?? Lang;
            LangBidi = o.LangBidi ?? LangBidi;
            LangEastAsia = o.LangEastAsia ?? LangEastAsia;
            Ligatures = o.Ligatures ?? Ligatures;
            if (o.AsciiTheme != null || o.Ascii != null) { Ascii = o.Ascii; AsciiTheme = o.AsciiTheme; }
            if (o.HAnsiTheme != null || o.HAnsi != null) { HAnsi = o.HAnsi; HAnsiTheme = o.HAnsiTheme; }
            if (o.EastAsiaTheme != null || o.EastAsia != null) { EastAsia = o.EastAsia; EastAsiaTheme = o.EastAsiaTheme; }
            if (o.CsTheme != null || o.CsFont != null) { CsFont = o.CsFont; CsTheme = o.CsTheme; }
            Hint = o.Hint ?? Hint;
        }

        private static bool? Toggle(bool? current, bool? level, bool toggle)
        {
            if (level == null) return current;
            if (!toggle) return level;
            // A style level that turns the property on flips it; one that turns it off leaves it.
            return level.Value ? !(current ?? false) : current;
        }
    }

    /// <summary>Paragraph properties being accumulated.</summary>
    internal sealed class ParaState
    {
        public string? Justification, LineRule;
        public int? IndStart, IndEnd, FirstLine, Before, After, Line, OutlineLevel, NumId, NumLevel;
        public bool? BeforeAuto, AfterAuto, Contextual, KeepNext, KeepLines, PageBreakBefore, WidowControl, Bidi, SuppressAutoHyphens, MirrorIndents;
        public BorderDef? Top, Bottom, Left, Right, Between;
        public XElement? ShadingElement;
        public List<TabStopDef>? Tabs;

        public ParaState Clone()
        {
            var c = (ParaState)MemberwiseClone();
            c.Tabs = Tabs?.ToList();
            return c;
        }

        public static ParaState From(XElement pPr)
        {
            var s = new ParaState();
            s.Apply(pPr);
            return s;
        }

        public void Apply(XElement pPr)
        {
            foreach (var e in pPr.Elements())
            {
                if (e.Name.Namespace != Ns.W) continue;
                switch (e.Name.LocalName)
                {
                    case "jc": Justification = e.Attr("val") ?? Justification; break;
                    case "keepNext": KeepNext = Ox.OnOff(e); break;
                    case "keepLines": KeepLines = Ox.OnOff(e); break;
                    case "pageBreakBefore": PageBreakBefore = Ox.OnOff(e); break;
                    case "widowControl": WidowControl = Ox.OnOff(e); break;
                    case "bidi": Bidi = Ox.OnOff(e); break;
                    case "contextualSpacing": Contextual = Ox.OnOff(e); break;
                    case "suppressAutoHyphens": SuppressAutoHyphens = Ox.OnOff(e); break;
                    case "mirrorIndents": MirrorIndents = Ox.OnOff(e); break;
                    case "outlineLvl": OutlineLevel = e.IntAttr("val") ?? OutlineLevel; break;
                    case "shd": ShadingElement = e; break;
                    case "numPr":
                        NumId = e.Element(Ns.W + "numId").IntAttr("val") ?? NumId;
                        NumLevel = e.Element(Ns.W + "ilvl").IntAttr("val") ?? NumLevel;
                        break;
                    case "spacing":
                        Before = e.IntAttr("before") ?? Before;
                        After = e.IntAttr("after") ?? After;
                        Line = e.IntAttr("line") ?? Line;
                        LineRule = e.Attr("lineRule") ?? (e.Attr("line") != null ? "auto" : LineRule);
                        if (e.Attr("beforeAutospacing") is { } ba) BeforeAuto = ba is "1" or "true" or "on";
                        if (e.Attr("afterAutospacing") is { } aa) AfterAuto = aa is "1" or "true" or "on";
                        break;
                    case "ind":
                        IndStart = e.IntAttr("start") ?? e.IntAttr("left") ?? IndStart;
                        IndEnd = e.IntAttr("end") ?? e.IntAttr("right") ?? IndEnd;
                        if (e.IntAttr("hanging") is { } h) FirstLine = -h;
                        else if (e.IntAttr("firstLine") is { } f) FirstLine = f;
                        break;
                    case "pBdr":
                        Top = BorderDef.Parse(e.Element(Ns.W + "top")) ?? Top;
                        Bottom = BorderDef.Parse(e.Element(Ns.W + "bottom")) ?? Bottom;
                        Left = BorderDef.Parse(e.Element(Ns.W + "left") ?? e.Element(Ns.W + "start")) ?? Left;
                        Right = BorderDef.Parse(e.Element(Ns.W + "right") ?? e.Element(Ns.W + "end")) ?? Right;
                        Between = BorderDef.Parse(e.Element(Ns.W + "between")) ?? Between;
                        break;
                    case "tabs":
                        Tabs ??= new List<TabStopDef>();
                        foreach (var t in e.Elements(Ns.W + "tab").Select(TabStopDef.Parse))
                        {
                            Tabs.RemoveAll(x => x.Position == t.Position);
                            if (t.Kind != "clear") Tabs.Add(t);
                        }
                        break;
                }
            }
        }

        public void Merge(ParaState o)
        {
            Justification = o.Justification ?? Justification;
            IndStart = o.IndStart ?? IndStart;
            IndEnd = o.IndEnd ?? IndEnd;
            FirstLine = o.FirstLine ?? FirstLine;
            Before = o.Before ?? Before;
            After = o.After ?? After;
            Line = o.Line ?? Line;
            LineRule = o.LineRule ?? LineRule;
            BeforeAuto = o.BeforeAuto ?? BeforeAuto;
            AfterAuto = o.AfterAuto ?? AfterAuto;
            Contextual = o.Contextual ?? Contextual;
            KeepNext = o.KeepNext ?? KeepNext;
            KeepLines = o.KeepLines ?? KeepLines;
            PageBreakBefore = o.PageBreakBefore ?? PageBreakBefore;
            WidowControl = o.WidowControl ?? WidowControl;
            Bidi = o.Bidi ?? Bidi;
            SuppressAutoHyphens = o.SuppressAutoHyphens ?? SuppressAutoHyphens;
            MirrorIndents = o.MirrorIndents ?? MirrorIndents;
            OutlineLevel = o.OutlineLevel ?? OutlineLevel;
            NumId = o.NumId ?? NumId;
            NumLevel = o.NumLevel ?? NumLevel;
            Top = o.Top ?? Top;
            Bottom = o.Bottom ?? Bottom;
            Left = o.Left ?? Left;
            Right = o.Right ?? Right;
            Between = o.Between ?? Between;
            ShadingElement = o.ShadingElement ?? ShadingElement;
            if (o.Tabs != null)
            {
                Tabs ??= new List<TabStopDef>();
                foreach (var t in o.Tabs)
                {
                    Tabs.RemoveAll(x => x.Position == t.Position);
                    Tabs.Add(t);
                }
            }
        }

        public ResolvedPara Finish(string styleId, int numId, int level) => new()
        {
            StyleId = styleId,
            Justification = Justification ?? "left",
            IndentStart = IndStart ?? 0,
            IndentEnd = IndEnd ?? 0,
            FirstLine = FirstLine ?? 0,
            SpacingBefore = Before ?? 0,
            SpacingAfter = After ?? 0,
            BeforeAutospacing = BeforeAuto ?? false,
            AfterAutospacing = AfterAuto ?? false,
            Line = Line ?? 240,
            LineRule = LineRule ?? "auto",
            ContextualSpacing = Contextual ?? false,
            KeepNext = KeepNext ?? false,
            KeepLines = KeepLines ?? false,
            PageBreakBefore = PageBreakBefore ?? false,
            WidowControl = WidowControl ?? false,
            Bidi = Bidi ?? false,
            SuppressAutoHyphens = SuppressAutoHyphens ?? false,
            MirrorIndents = MirrorIndents ?? false,
            OutlineLevel = OutlineLevel ?? 9,
            NumId = numId,
            NumLevel = level,
            BorderTop = Top is { IsVisible: true } ? Top : null,
            BorderBottom = Bottom is { IsVisible: true } ? Bottom : null,
            BorderLeft = Left is { IsVisible: true } ? Left : null,
            BorderRight = Right is { IsVisible: true } ? Right : null,
            BorderBetween = Between is { IsVisible: true } ? Between : null,
            Tabs = (Tabs ?? new List<TabStopDef>()).OrderBy(t => t.Position).ToList(),
        };
    }
}

/// <summary>Caches resolved run formatting for one paragraph (runs often share identical w:rPr).</summary>
public sealed class ResolveRunCache
{
    private readonly StyleResolver _resolver;
    private readonly string _paraStyle;
    private readonly TableContext? _table;
    private readonly Dictionary<string, ResolvedRun> _cache = new(StringComparer.Ordinal);

    internal ResolveRunCache(StyleResolver resolver, string paraStyle, TableContext? table)
    {
        _resolver = resolver;
        _paraStyle = paraStyle;
        _table = table;
    }

    public ResolvedRun Get(RunProps props)
    {
        var key = props.X.HasElements ? props.X.ToString(SaveOptions.DisableFormatting) : "";
        if (_cache.TryGetValue(key, out var r)) return r;
        r = _resolver.ResolveRun(props.X, _paraStyle, _table);
        _cache[key] = r;
        return r;
    }
}
