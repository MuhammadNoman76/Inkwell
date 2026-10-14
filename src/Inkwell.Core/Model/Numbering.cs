using System.Globalization;
using System.Xml.Linq;

namespace Inkwell.Core.Model;

/// <summary>One level of a list definition, after applying the list instance's overrides.</summary>
public sealed class NumberingLevel
{
    public int AbstractNumId { get; init; }
    public int NumId { get; init; }
    public int Level { get; init; }
    public int Start { get; init; } = 1;

    /// <summary>decimal, bullet, lowerLetter, upperRoman, none…</summary>
    public string Format { get; init; } = "decimal";

    /// <summary>The label pattern ("%1.", "%1.%2.", "•").</summary>
    public string Text { get; init; } = "%1.";

    /// <summary>Restart after this level (w:lvlRestart; 0 = never; null = after any higher level).</summary>
    public int? Restart { get; init; }

    /// <summary>Legal numbering: every level shown as a decimal number.</summary>
    public bool IsLegal { get; init; }

    /// <summary>What follows the label: tab, space or nothing.</summary>
    public string Suffix { get; init; } = "tab";

    /// <summary>Label alignment: left/start, center, right/end.</summary>
    public string Alignment { get; init; } = "left";

    /// <summary>Paragraph style linked to this level (w:pStyle).</summary>
    public string? StyleId { get; init; }

    public XElement? ParaPropsXml { get; init; }

    public XElement? RunPropsXml { get; init; }

    /// <summary>The list instance sets a start for this level (w:startOverride).</summary>
    public int? StartOverride { get; init; }

    public bool IsBullet => Format == "bullet";
}

/// <summary>The numbering part (numbering.xml): list definitions (w:abstractNum) and instances (w:num).</summary>
public sealed class Numbering
{
    private Dictionary<int, XElement>? _abstract;
    private Dictionary<int, XElement>? _nums;
    private readonly Dictionary<(int, int), NumberingLevel?> _levels = new();

    public Numbering()
        : this(new XDocument(Ns.NewRoot(Ns.W + "numbering")))
    {
    }

    public Numbering(XDocument doc)
    {
        Doc = doc;
        if (Doc.Root == null) Doc.Add(Ns.NewRoot(Ns.W + "numbering"));
    }

    public XDocument Doc { get; private set; }

    public XElement Root => Doc.Root!;

    public int Version { get; private set; }

    /// <summary>The styles part, for lists defined through a numbering style (w:numStyleLink).</summary>
    public StyleSheet? Styles { get; set; }

    public bool IsEmpty => !Root.HasElements;

    public void Changed()
    {
        Version++;
        _abstract = null;
        _nums = null;
        _levels.Clear();
    }

    public void Reset(XDocument doc)
    {
        Doc = doc;
        Changed();
    }

    public Numbering Clone() => new(new XDocument(Doc)) { Styles = Styles };

    private Dictionary<int, XElement> Abstracts => _abstract ??= Root.Elements(Ns.W + "abstractNum")
        .Where(e => e.IntAttr("abstractNumId") != null).GroupBy(e => e.IntAttr("abstractNumId")!.Value).ToDictionary(g => g.Key, g => g.First());

    private Dictionary<int, XElement> Nums => _nums ??= Root.Elements(Ns.W + "num")
        .Where(e => e.IntAttr("numId") != null).GroupBy(e => e.IntAttr("numId")!.Value).ToDictionary(g => g.Key, g => g.First());

    public IEnumerable<int> NumIds => Nums.Keys;

    public bool HasNum(int numId) => Nums.ContainsKey(numId);

    /// <summary>The abstract definition a list instance uses (following numStyleLink), or -1.</summary>
    public int AbstractOf(int numId)
    {
        if (!Nums.TryGetValue(numId, out var num)) return -1;
        var id = num.Element(Ns.W + "abstractNumId").IntAttr("val") ?? -1;
        return FollowStyleLink(id, 0);
    }

    private int FollowStyleLink(int abstractId, int depth)
    {
        if (depth > 4 || !Abstracts.TryGetValue(abstractId, out var abs)) return abstractId;
        var link = abs.Element(Ns.W + "numStyleLink").Attr("val");
        if (link == null || Styles?.Get(link) is not { } style) return abstractId;
        var linkedNum = style.ParaProps()?.NumId;
        if (linkedNum == null || !Nums.TryGetValue(linkedNum.Value, out var num)) return abstractId;
        var target = num.Element(Ns.W + "abstractNumId").IntAttr("val") ?? -1;
        return target == abstractId ? abstractId : FollowStyleLink(target, depth + 1);
    }

    /// <summary>The level definition for a paragraph's list (numId, ilvl), or null (numId 0 or unknown).</summary>
    public NumberingLevel? Level(int numId, int level)
    {
        if (numId <= 0) return null;
        level = MathEx.Clamp(level, 0, 8);
        if (_levels.TryGetValue((numId, level), out var cached)) return cached;
        var result = Compute(numId, level);
        _levels[(numId, level)] = result;
        return result;
    }

    private NumberingLevel? Compute(int numId, int level)
    {
        if (!Nums.TryGetValue(numId, out var num)) return null;
        var abstractId = AbstractOf(numId);
        if (!Abstracts.TryGetValue(abstractId, out var abs)) return null;
        var lvl = abs.Elements(Ns.W + "lvl").FirstOrDefault(e => (e.IntAttr("ilvl") ?? 0) == level);
        int? startOverride = null;
        var ovr = num.Elements(Ns.W + "lvlOverride").FirstOrDefault(e => (e.IntAttr("ilvl") ?? 0) == level);
        if (ovr != null)
        {
            startOverride = ovr.Element(Ns.W + "startOverride").IntAttr("val");
            var replacement = ovr.Element(Ns.W + "lvl");
            if (replacement != null) lvl = replacement;
        }
        if (lvl == null) return null;
        return new NumberingLevel
        {
            AbstractNumId = abstractId,
            NumId = numId,
            Level = level,
            Start = startOverride ?? lvl.Element(Ns.W + "start").IntAttr("val") ?? 0,
            Format = lvl.Element(Ns.W + "numFmt").Attr("val") ?? "decimal",
            Text = lvl.Element(Ns.W + "lvlText").Attr("val") ?? "",
            Restart = lvl.Element(Ns.W + "lvlRestart").IntAttr("val"),
            IsLegal = Ox.OnOff(lvl.Element(Ns.W + "isLgl")),
            Suffix = lvl.Element(Ns.W + "suff").Attr("val") ?? "tab",
            Alignment = lvl.Element(Ns.W + "lvlJc").Attr("val") ?? "left",
            StyleId = lvl.Element(Ns.W + "pStyle").Attr("val"),
            ParaPropsXml = lvl.Element(Ns.W + "pPr"),
            RunPropsXml = lvl.Element(Ns.W + "rPr"),
            StartOverride = startOverride,
        };
    }

    /// <summary>The list instance whose level is linked to a paragraph style (w:pStyle in a w:lvl), for heading numbering.</summary>
    public (int NumId, int Level)? ForStyle(string styleId)
    {
        foreach (var (absId, abs) in Abstracts)
        {
            foreach (var lvl in abs.Elements(Ns.W + "lvl"))
            {
                if (lvl.Element(Ns.W + "pStyle").Attr("val") != styleId) continue;
                var num = Nums.Values.FirstOrDefault(n => n.Element(Ns.W + "abstractNumId").IntAttr("val") == absId);
                if (num != null) return (num.IntAttr("numId") ?? 0, lvl.IntAttr("ilvl") ?? 0);
            }
        }
        return null;
    }

    // ------------------------------------------------------------------ editing

    public int NextAbstractId() => Abstracts.Count == 0 ? 0 : Abstracts.Keys.Max() + 1;

    public int NextNumId() => Nums.Count == 0 ? 1 : Nums.Keys.Max() + 1;

    /// <summary>Adds a list definition (its abstractNumId attribute is set) and returns its id.</summary>
    public int AddAbstract(XElement abstractNum)
    {
        var id = NextAbstractId();
        abstractNum.SetAttributeValue(Ns.W + "abstractNumId", Ox.Str(id));
        var lastAbstract = Root.Elements(Ns.W + "abstractNum").LastOrDefault();
        if (lastAbstract != null) lastAbstract.AddAfterSelf(abstractNum);
        else
        {
            var firstNum = Root.Elements(Ns.W + "num").FirstOrDefault();
            if (firstNum != null) firstNum.AddBeforeSelf(abstractNum);
            else SchemaOrder.Insert(Root, abstractNum, SchemaOrder.NumberingRoot);
        }
        Changed();
        return id;
    }

    /// <summary>Adds a list instance of a definition, optionally restarting level 0 at <paramref name="restartAt"/>.</summary>
    public int AddNum(int abstractId, int? restartAt = null, int restartLevel = 0)
    {
        var id = NextNumId();
        var num = new XElement(Ns.W + "num", new XAttribute(Ns.W + "numId", Ox.Str(id)),
            new XElement(Ns.W + "abstractNumId", new XAttribute(Ox.Val, Ox.Str(abstractId))));
        if (restartAt != null)
        {
            num.Add(new XElement(Ns.W + "lvlOverride", new XAttribute(Ns.W + "ilvl", Ox.Str(restartLevel)),
                new XElement(Ns.W + "startOverride", new XAttribute(Ox.Val, Ox.Str(restartAt.Value)))));
        }
        var lastNum = Root.Elements(Ns.W + "num").LastOrDefault();
        if (lastNum != null) lastNum.AddAfterSelf(num);
        else SchemaOrder.Insert(Root, num, SchemaOrder.NumberingRoot);
        Changed();
        return id;
    }

    /// <summary>The abstract definition element (for editing levels).</summary>
    public XElement? AbstractElement(int abstractId) => Abstracts.TryGetValue(abstractId, out var e) ? e : null;

    public XElement? NumElement(int numId) => Nums.TryGetValue(numId, out var e) ? e : null;

    /// <summary>
    /// A standard list definition: bullets (•, ◦, ▪ …) or numbers (1., a., i., …) with Inkwell's consistent
    /// indents (0.25" steps, 0.25" hanging).
    /// </summary>
    public static XElement StandardDefinition(bool bullets, string? fontForBullets = null)
    {
        var nsid = Compat.NextRandom(0x10000000, int.MaxValue).ToString("X8", CultureInfo.InvariantCulture);
        var abs = new XElement(Ns.W + "abstractNum",
            new XElement(Ns.W + "nsid", new XAttribute(Ox.Val, nsid)),
            new XElement(Ns.W + "multiLevelType", new XAttribute(Ox.Val, "hybridMultilevel")));
        string[] bulletChars = { "•", "◦", "▪", "•", "◦", "▪", "•", "◦", "▪" };
        string[] numberFormats = { "decimal", "lowerLetter", "lowerRoman", "decimal", "lowerLetter", "lowerRoman", "decimal", "lowerLetter", "lowerRoman" };
        for (var level = 0; level < 9; level++)
        {
            var indent = 360 + level * 360;
            var lvl = new XElement(Ns.W + "lvl", new XAttribute(Ns.W + "ilvl", Ox.Str(level)),
                new XElement(Ns.W + "start", new XAttribute(Ox.Val, "1")),
                new XElement(Ns.W + "numFmt", new XAttribute(Ox.Val, bullets ? "bullet" : numberFormats[level])),
                new XElement(Ns.W + "lvlText", new XAttribute(Ox.Val, bullets ? bulletChars[level] : "%" + (level + 1) + ".")),
                new XElement(Ns.W + "lvlJc", new XAttribute(Ox.Val, "left")),
                new XElement(Ns.W + "pPr",
                    new XElement(Ns.W + "ind", new XAttribute(Ns.W + "left", Ox.Str(indent)), new XAttribute(Ns.W + "hanging", "360"))));
            if (bullets && fontForBullets != null)
                lvl.Add(new XElement(Ns.W + "rPr", new XElement(Ns.W + "rFonts", new XAttribute(Ns.W + "ascii", fontForBullets), new XAttribute(Ns.W + "hAnsi", fontForBullets), new XAttribute(Ns.W + "hint", "default"))));
            abs.Add(lvl);
        }
        return abs;
    }

    /// <summary>A multi-level outline numbering for headings: 1, 1.1, 1.1.1 linked to Heading 1–3.</summary>
    public static XElement HeadingDefinition()
    {
        var nsid = Compat.NextRandom(0x10000000, int.MaxValue).ToString("X8", CultureInfo.InvariantCulture);
        var abs = new XElement(Ns.W + "abstractNum",
            new XElement(Ns.W + "nsid", new XAttribute(Ox.Val, nsid)),
            new XElement(Ns.W + "multiLevelType", new XAttribute(Ox.Val, "multilevel")));
        for (var level = 0; level < 9; level++)
        {
            var text = string.Join(".", Enumerable.Range(1, level + 1).Select(i => "%" + i));
            var lvl = new XElement(Ns.W + "lvl", new XAttribute(Ns.W + "ilvl", Ox.Str(level)),
                new XElement(Ns.W + "start", new XAttribute(Ox.Val, "1")),
                new XElement(Ns.W + "numFmt", new XAttribute(Ox.Val, "decimal")));
            if (level < 3) lvl.Add(new XElement(Ns.W + "pStyle", new XAttribute(Ox.Val, "Heading" + (level + 1))));
            lvl.Add(new XElement(Ns.W + "lvlText", new XAttribute(Ox.Val, text)),
                new XElement(Ns.W + "lvlJc", new XAttribute(Ox.Val, "left")),
                new XElement(Ns.W + "pPr", new XElement(Ns.W + "ind", new XAttribute(Ns.W + "left", Ox.Str(0)), new XAttribute(Ns.W + "firstLine", "0"))));
            abs.Add(lvl);
        }
        return abs;
    }
}
