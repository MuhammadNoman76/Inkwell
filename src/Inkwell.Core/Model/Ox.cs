using System.Globalization;
using System.Xml.Linq;

namespace Inkwell.Core.Model;

/// <summary>The XML namespaces of WordprocessingML and the parts around it.</summary>
public static class Ns
{
    public static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    public static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public static readonly XNamespace WP = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    public static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    public static readonly XNamespace Pic = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    public static readonly XNamespace MC = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    public static readonly XNamespace W14 = "http://schemas.microsoft.com/office/word/2010/wordml";
    public static readonly XNamespace W15 = "http://schemas.microsoft.com/office/word/2012/wordml";
    public static readonly XNamespace W16CID = "http://schemas.microsoft.com/office/word/2016/wordml/cid";
    public static readonly XNamespace W16CEX = "http://schemas.microsoft.com/office/word/2018/wordml/cex";
    public static readonly XNamespace W16SE = "http://schemas.microsoft.com/office/word/2015/wordml/symex";
    public static readonly XNamespace WP14 = "http://schemas.microsoft.com/office/word/2010/wordprocessingDrawing";
    public static readonly XNamespace WPS = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";
    public static readonly XNamespace WPG = "http://schemas.microsoft.com/office/word/2010/wordprocessingGroup";
    public static readonly XNamespace WPC = "http://schemas.microsoft.com/office/word/2010/wordprocessingCanvas";
    public static readonly XNamespace M = "http://schemas.openxmlformats.org/officeDocument/2006/math";
    public static readonly XNamespace V = "urn:schemas-microsoft-com:vml";
    public static readonly XNamespace O = "urn:schemas-microsoft-com:office:office";
    public static readonly XNamespace W10 = "urn:schemas-microsoft-com:office:word";
    public static readonly XNamespace WNE = "http://schemas.microsoft.com/office/word/2006/wordml";
    public static readonly XNamespace B = "http://schemas.openxmlformats.org/officeDocument/2006/bibliography";
    public static readonly XNamespace C = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    public static readonly XNamespace DGM = "http://schemas.openxmlformats.org/drawingml/2006/diagram";
    public static readonly XNamespace A14 = "http://schemas.microsoft.com/office/drawing/2010/main";
    public static readonly XNamespace CP = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
    public static readonly XNamespace DC = "http://purl.org/dc/elements/1.1/";
    public static readonly XNamespace DCTerms = "http://purl.org/dc/terms/";
    public static readonly XNamespace DCMIType = "http://purl.org/dc/dcmitype/";
    public static readonly XNamespace XSI = "http://www.w3.org/2001/XMLSchema-instance";
    public static readonly XNamespace EP = "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties";
    public static readonly XNamespace VT = "http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes";
    /// <summary>Inkwell's own markup inside the document (ignored by Word through mc:Ignorable).</summary>
    public static readonly XNamespace IW = "http://schemas.inkwell.app/word/2026";

    /// <summary>The prefixes a new document part declares (Word's usual set) and lists as ignorable.</summary>
    public static readonly (string Prefix, XNamespace Ns)[] StandardDeclarations =
    {
        ("wpc", WPC), ("mc", MC), ("o", O), ("r", R), ("m", M), ("v", V), ("wp14", WP14), ("wp", WP), ("w10", W10),
        ("w", W), ("w14", W14), ("w15", W15), ("w16cid", W16CID), ("w16se", W16SE), ("wpg", WPG), ("wne", WNE), ("wps", WPS),
    };

    public const string StandardIgnorable = "w14 w15 w16se w16cid wp14";

    /// <summary>A root element for a new part with Word's namespace declarations.</summary>
    public static XElement NewRoot(XName name)
    {
        var root = new XElement(name);
        foreach (var (prefix, ns) in StandardDeclarations) root.SetAttributeValue(XNamespace.Xmlns + prefix, ns.NamespaceName);
        root.SetAttributeValue(MC + "Ignorable", StandardIgnorable);
        return root;
    }
}

/// <summary>Schema order of child elements, so elements Inkwell adds land where the schema expects them.</summary>
public static class SchemaOrder
{
    private static Dictionary<string, int> Make(params string[] names)
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < names.Length; i++)
            if (!d.ContainsKey(names[i])) d[names[i]] = i * 10;
        return d;
    }

    /// <summary>Gives <paramref name="alias"/> the same position as <paramref name="of"/> (elements of one choice group).</summary>
    private static Dictionary<string, int> WithAlias(Dictionary<string, int> d, string alias, string of)
    {
        d[alias] = d[of];
        return d;
    }

    public static readonly Dictionary<string, int> RunProps = Make(
        "ins", "del", "moveFrom", "moveTo", "rStyle", "rFonts", "b", "bCs", "i", "iCs", "caps", "smallCaps", "strike", "dstrike", "outline",
        "shadow", "emboss", "imprint", "noProof", "snapToGrid", "vanish", "webHidden", "color", "spacing", "w", "kern", "position", "sz",
        "szCs", "highlight", "u", "effect", "bdr", "shd", "fitText", "vertAlign", "rtl", "cs", "em", "lang", "eastAsianLayout", "specVanish",
        "oMath", "w14:glow", "w14:shadow", "w14:reflection", "w14:textOutline", "w14:textFill", "w14:scene3d", "w14:props3d",
        "w14:ligatures", "w14:numForm", "w14:numSpacing", "w14:stylisticSets", "w14:cntxtAlts", "*", "rPrChange");

    public static readonly Dictionary<string, int> ParaProps = Make(
        "pStyle", "keepNext", "keepLines", "pageBreakBefore", "framePr", "widowControl", "numPr", "suppressLineNumbers", "pBdr", "shd", "tabs",
        "suppressAutoHyphens", "kinsoku", "wordWrap", "overflowPunct", "topLinePunct", "autoSpaceDE", "autoSpaceDN", "bidi", "adjustRightInd",
        "snapToGrid", "spacing", "ind", "contextualSpacing", "mirrorIndents", "suppressOverlap", "jc", "textDirection", "textAlignment",
        "textboxTightWrap", "outlineLvl", "divId", "cnfStyle", "*", "rPr", "sectPr", "pPrChange");

    public static readonly Dictionary<string, int> SectProps = WithAlias(Make(
        "headerReference", "footnotePr", "endnotePr", "type", "pgSz", "pgMar", "paperSrc", "pgBorders", "lnNumType", "pgNumType", "cols",
        "formProt", "vAlign", "noEndnote", "titlePg", "textDirection", "bidi", "rtlGutter", "docGrid", "printerSettings", "*", "sectPrChange"), "footerReference", "headerReference");

    public static readonly Dictionary<string, int> TableProps = Make(
        "tblStyle", "tblpPr", "tblOverlap", "bidiVisual", "tblStyleRowBandSize", "tblStyleColBandSize", "tblW", "jc", "tblCellSpacing", "tblInd",
        "tblBorders", "shd", "tblLayout", "tblCellMar", "tblLook", "tblCaption", "tblDescription", "*", "tblPrChange");

    public static readonly Dictionary<string, int> RowProps = Make(
        "cnfStyle", "divId", "gridBefore", "gridAfter", "wBefore", "wAfter", "cantSplit", "trHeight", "tblHeader", "tblCellSpacing", "jc", "hidden",
        "*", "ins", "del", "trPrChange");

    public static readonly Dictionary<string, int> CellProps = Make(
        "cnfStyle", "tcW", "gridSpan", "hMerge", "vMerge", "tcBorders", "shd", "noWrap", "tcMar", "textDirection", "tcFitText", "vAlign", "hideMark",
        "headers", "*", "cellIns", "cellDel", "cellMerge", "tcPrChange");

    public static readonly Dictionary<string, int> Borders = Make("top", "left", "start", "bottom", "right", "end", "between", "bar", "insideH", "insideV", "tl2br", "tr2bl");

    public static readonly Dictionary<string, int> Margins = Make("top", "left", "start", "bottom", "right", "end");

    public static readonly Dictionary<string, int> Style = Make(
        "name", "aliases", "basedOn", "next", "link", "autoRedefine", "hidden", "uiPriority", "semiHidden", "unhideWhenUsed", "qFormat", "locked",
        "personal", "personalCompose", "personalReply", "rsid", "pPr", "rPr", "tblPr", "trPr", "tcPr", "tblStylePr", "*");

    public static readonly Dictionary<string, int> Styles = Make("docDefaults", "latentStyles", "style");

    public static readonly Dictionary<string, int> Level = Make(
        "start", "numFmt", "lvlRestart", "pStyle", "isLgl", "suff", "lvlText", "lvlPicBulletId", "legacy", "lvlJc", "pPr", "rPr", "*");

    public static readonly Dictionary<string, int> AbstractNum = Make("nsid", "multiLevelType", "tmpl", "name", "styleLink", "numStyleLink", "lvl", "*");

    public static readonly Dictionary<string, int> Num = Make("abstractNumId", "lvlOverride", "*");

    public static readonly Dictionary<string, int> NumberingRoot = Make("numPicBullet", "abstractNum", "num", "numIdMacAtCleanup", "*");

    public static readonly Dictionary<string, int> Settings = Make(
        "writeProtection", "view", "zoom", "removePersonalInformation", "removeDateAndTime", "doNotDisplayPageBoundaries", "displayBackgroundShape",
        "printPostScriptOverText", "printFractionalCharacterWidth", "printFormsData", "embedTrueTypeFonts", "embedSystemFonts", "saveSubsetFonts",
        "saveFormsData", "mirrorMargins", "alignBordersAndEdges", "bordersDoNotSurroundHeader", "bordersDoNotSurroundFooter", "gutterAtTop",
        "hideSpellingErrors", "hideGrammaticalErrors", "activeWritingStyle", "proofState", "formsDesign", "attachedTemplate", "linkStyles",
        "stylePaneFormatFilter", "stylePaneSortMethod", "documentType", "mailMerge", "revisionView", "trackRevisions", "doNotTrackMoves",
        "doNotTrackFormatting", "documentProtection", "autoFormatOverride", "styleLockTheme", "styleLockQFSet", "defaultTabStop", "autoHyphenation",
        "consecutiveHyphenLimit", "hyphenationZone", "doNotHyphenateCaps", "showEnvelope", "summaryLength", "clickAndTypeStyle", "defaultTableStyle",
        "evenAndOddHeaders", "bookFoldRevPrinting", "bookFoldPrinting", "bookFoldPrintingSheets", "drawingGridHorizontalSpacing",
        "drawingGridVerticalSpacing", "displayHorizontalDrawingGridEvery", "displayVerticalDrawingGridEvery", "doNotUseMarginsForDrawingGridOrigin",
        "drawingGridHorizontalOrigin", "drawingGridVerticalOrigin", "doNotShadeFormData", "noPunctuationKerning", "characterSpacingControl",
        "printTwoOnOne", "strictFirstAndLastChars", "noLineBreaksAfter", "noLineBreaksBefore", "savePreviewPicture", "doNotValidateAgainstSchema",
        "saveInvalidXml", "ignoreMixedContent", "alwaysShowPlaceholderText", "doNotDemarcateInvalidXml", "saveXmlDataOnly", "useXSLTWhenSaving",
        "saveThroughXslt", "showXMLTags", "alwaysMergeEmptyNamespace", "updateFields", "hdrShapeDefaults", "footnotePr", "endnotePr", "compat",
        "docVars", "rsids", "m:mathPr", "attachedSchema", "themeFontLang", "clrSchemeMapping", "doNotIncludeSubdocsInStats",
        "doNotAutoCompressPictures", "forceUpgrade", "captions", "readModeInkLockDown", "smartTagType", "sl:schemaLibrary", "shapeDefaults",
        "doNotEmbedSmartTags", "decimalSymbol", "listSeparator", "*");

    /// <summary>The key of an element in an order table: the local name for w: elements, "w14:x" etc. otherwise.</summary>
    public static string Key(XName name)
    {
        var ns = name.Namespace;
        if (ns == Ns.W) return name.LocalName;
        if (ns == Ns.W14) return "w14:" + name.LocalName;
        if (ns == Ns.M) return "m:" + name.LocalName;
        if (ns == Ns.W15) return "w15:" + name.LocalName;
        return "?" + name.LocalName;
    }

    /// <summary>Position of an element in a table; unknown elements go where the table's "*" is (before *Change elements).</summary>
    public static int IndexOf(Dictionary<string, int> table, XName name)
    {
        if (table.TryGetValue(Key(name), out var i)) return i;
        return table.TryGetValue("*", out var star) ? star : int.MaxValue;
    }

    /// <summary>Adds <paramref name="child"/> to <paramref name="parent"/> at its schema position (after equal ones).</summary>
    public static void Insert(XElement parent, XElement child, Dictionary<string, int> table)
    {
        var index = IndexOf(table, child.Name);
        foreach (var existing in parent.Elements())
        {
            if (IndexOf(table, existing.Name) > index)
            {
                existing.AddBeforeSelf(child);
                return;
            }
        }
        parent.Add(child);
    }

    /// <summary>Re-sorts the children of <paramref name="parent"/> into schema order (stable).</summary>
    public static void Sort(XElement parent, Dictionary<string, int> table)
    {
        var children = parent.Elements().ToList();
        var sorted = children.Select((e, i) => (e, i)).OrderBy(t => IndexOf(table, t.e.Name)).ThenBy(t => t.i).Select(t => t.e).ToList();
        if (sorted.SequenceEqual(children)) return;
        foreach (var c in children) c.Remove();
        foreach (var c in sorted) parent.Add(c);
    }
}

/// <summary>Small helpers to read and write WordprocessingML attribute values.</summary>
public static class Ox
{
    public static readonly XName Val = Ns.W + "val";

    public static string? Attr(this XElement? e, string localName) => e?.Attribute(Ns.W + localName)?.Value;

    public static string? Attr(this XElement? e, XName name) => e?.Attribute(name)?.Value;

    public static int? IntAttr(this XElement? e, string localName) => ParseInt(e.Attr(localName));

    public static long? LongAttr(this XElement? e, XName name) =>
        long.TryParse(e?.Attribute(name)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    public static int? ParseInt(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
        // Some producers write decimals ("720.0") or units ("1in", "12pt"): accept the common ones.
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return (int)Math.Round(d);
        return ParseUniversalMeasure(s!);
    }

    /// <summary>ST_UniversalMeasure ("2.5cm", "12pt", "1in", "3mm", "1pc", "4pi") in twips.</summary>
    public static int? ParseUniversalMeasure(string s)
    {
        s = s.Trim();
        if (s.Length < 3) return null;
        var unit = s.Substring(s.Length - 2);
        if (!double.TryParse(s.Substring(0, s.Length - 2), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return null;
        double twips = unit switch
        {
            "mm" => n * 1440 / 25.4,
            "cm" => n * 1440 / 2.54,
            "in" => n * 1440,
            "pt" => n * 20,
            "pc" or "pi" => n * 240,
            _ => double.NaN,
        };
        return double.IsNaN(twips) ? null : (int)Math.Round(twips);
    }

    /// <summary>ST_OnOff: missing value = true; "0", "false", "off" = false.</summary>
    public static bool OnOff(XElement? e)
    {
        if (e == null) return false;
        var v = e.Attribute(Val)?.Value;
        return v == null || !(v == "0" || v == "false" || v == "off");
    }

    public static bool? OnOffOrNull(XElement? e) => e == null ? null : OnOff(e);

    public static string Str(int v) => v.ToString(CultureInfo.InvariantCulture);

    public static string Str(long v) => v.ToString(CultureInfo.InvariantCulture);

    public static string Str(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public static XElement W(string localName, params object?[] content) => new(Ns.W + localName, content);

    public static XAttribute WA(string localName, object value) => new(Ns.W + localName, value is int i ? Str(i) : value is long l ? Str(l) : value.ToString()!);

    /// <summary>Deep copy (null-safe).</summary>
    public static XElement? Copy(this XElement? e) => e == null ? null : new XElement(e);

    /// <summary>Removes an attribute and returns the element.</summary>
    public static void SetOrRemove(this XElement e, XName name, string? value)
    {
        if (value == null) e.Attribute(name)?.Remove();
        else e.SetAttributeValue(name, value);
    }

    /// <summary>Equal XML (names, attributes in any order, children in order).</summary>
    public static bool SameXml(XElement? a, XElement? b)
    {
        if (a == null || b == null) return a == null && b == null;
        if (a.Name != b.Name) return false;
        var aa = a.Attributes().Where(x => !x.IsNamespaceDeclaration).ToList();
        var ba = b.Attributes().Where(x => !x.IsNamespaceDeclaration).ToList();
        if (aa.Count != ba.Count) return false;
        foreach (var x in aa)
            if (b.Attribute(x.Name)?.Value != x.Value) return false;
        var ac = a.Elements().ToList();
        var bc = b.Elements().ToList();
        if (ac.Count != bc.Count) return false;
        for (var i = 0; i < ac.Count; i++)
            if (!SameXml(ac[i], bc[i])) return false;
        if (ac.Count == 0 && a.Value != b.Value) return false;
        return true;
    }
}
