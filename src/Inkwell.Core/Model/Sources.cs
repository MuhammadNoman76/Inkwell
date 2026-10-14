using System.Xml.Linq;

namespace Inkwell.Core.Model;

/// <summary>A person in a source (author, editor…).</summary>
public sealed class SourcePerson
{
    public string Last { get; set; } = "";
    public string First { get; set; } = "";
    public string Middle { get; set; } = "";

    public SourcePerson Clone() => new() { Last = Last, First = First, Middle = Middle };

    public override string ToString() => string.Join(" ", new[] { First, Middle, Last }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>"Ada Lovelace", "Lovelace, Ada", "Ada M. Lovelace" → a person.</summary>
    public static SourcePerson Parse(string text)
    {
        text = text.Trim();
        var p = new SourcePerson();
        if (text.Contains(','))
        {
            var parts = text.Split(',');
            p.Last = parts[0].Trim();
            var rest = parts[1].Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (rest.Length > 0) p.First = rest[0];
            if (rest.Length > 1) p.Middle = string.Join(" ", rest.Skip(1));
            return p;
        }
        var words = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 1) p.Last = words[0];
        else if (words.Length > 1)
        {
            p.First = words[0];
            p.Last = words[words.Length - 1];
            if (words.Length > 2) p.Middle = string.Join(" ", words.Skip(1).Take(words.Length - 2));
        }
        return p;
    }
}

/// <summary>One source of the bibliography (b:Source).</summary>
public sealed class BibSource
{
    public string Tag { get; set; } = "";

    /// <summary>Book, BookSection, JournalArticle, ArticleInAPeriodical, ConferenceProceedings, Report, InternetSite,
    /// DocumentFromInternetSite, ElectronicSource, Film, Interview, Patent, Case, Misc…</summary>
    public string Type { get; set; } = "Book";

    public List<SourcePerson> Authors { get; set; } = new();
    public string? CorporateAuthor { get; set; }
    public List<SourcePerson> Editors { get; set; } = new();

    public string? Title { get; set; }
    public string? Year { get; set; }
    public string? Month { get; set; }
    public string? Day { get; set; }
    public string? City { get; set; }
    public string? Publisher { get; set; }
    public string? JournalName { get; set; }
    public string? PeriodicalTitle { get; set; }
    public string? BookTitle { get; set; }
    public string? ConferenceName { get; set; }
    public string? Volume { get; set; }
    public string? Issue { get; set; }
    public string? Pages { get; set; }
    public string? Edition { get; set; }
    public string? Url { get; set; }
    public string? Doi { get; set; }
    public string? InternetSiteTitle { get; set; }
    public string? Institution { get; set; }
    public string? YearAccessed { get; set; }
    public string? MonthAccessed { get; set; }
    public string? DayAccessed { get; set; }
    public string? Medium { get; set; }

    public string? Guid { get; set; }

    /// <summary>The element as loaded: fields Inkwell doesn't edit are kept.</summary>
    public XElement? Original { get; set; }

    public BibSource Clone() => new()
    {
        Tag = Tag, Type = Type, Authors = Authors.Select(a => a.Clone()).ToList(), CorporateAuthor = CorporateAuthor,
        Editors = Editors.Select(a => a.Clone()).ToList(), Title = Title, Year = Year, Month = Month, Day = Day, City = City,
        Publisher = Publisher, JournalName = JournalName, PeriodicalTitle = PeriodicalTitle, BookTitle = BookTitle,
        ConferenceName = ConferenceName, Volume = Volume, Issue = Issue, Pages = Pages, Edition = Edition, Url = Url, Doi = Doi,
        InternetSiteTitle = InternetSiteTitle, Institution = Institution, YearAccessed = YearAccessed, MonthAccessed = MonthAccessed,
        DayAccessed = DayAccessed, Medium = Medium, Guid = Guid, Original = Original.Copy(),
    };

    private static readonly (string Element, Func<BibSource, string?> Get, Action<BibSource, string?> Set)[] Fields =
    {
        ("Title", s => s.Title, (s, v) => s.Title = v),
        ("Year", s => s.Year, (s, v) => s.Year = v),
        ("Month", s => s.Month, (s, v) => s.Month = v),
        ("Day", s => s.Day, (s, v) => s.Day = v),
        ("City", s => s.City, (s, v) => s.City = v),
        ("Publisher", s => s.Publisher, (s, v) => s.Publisher = v),
        ("JournalName", s => s.JournalName, (s, v) => s.JournalName = v),
        ("PeriodicalTitle", s => s.PeriodicalTitle, (s, v) => s.PeriodicalTitle = v),
        ("BookTitle", s => s.BookTitle, (s, v) => s.BookTitle = v),
        ("ConferenceName", s => s.ConferenceName, (s, v) => s.ConferenceName = v),
        ("Volume", s => s.Volume, (s, v) => s.Volume = v),
        ("Issue", s => s.Issue, (s, v) => s.Issue = v),
        ("Pages", s => s.Pages, (s, v) => s.Pages = v),
        ("Edition", s => s.Edition, (s, v) => s.Edition = v),
        ("URL", s => s.Url, (s, v) => s.Url = v),
        ("DOI", s => s.Doi, (s, v) => s.Doi = v),
        ("InternetSiteTitle", s => s.InternetSiteTitle, (s, v) => s.InternetSiteTitle = v),
        ("Institution", s => s.Institution, (s, v) => s.Institution = v),
        ("YearAccessed", s => s.YearAccessed, (s, v) => s.YearAccessed = v),
        ("MonthAccessed", s => s.MonthAccessed, (s, v) => s.MonthAccessed = v),
        ("DayAccessed", s => s.DayAccessed, (s, v) => s.DayAccessed = v),
        ("Medium", s => s.Medium, (s, v) => s.Medium = v),
        ("Guid", s => s.Guid, (s, v) => s.Guid = v),
    };

    public static BibSource FromXml(XElement e)
    {
        var b = Ns.B;
        var s = new BibSource
        {
            Tag = e.Element(b + "Tag")?.Value ?? "",
            Type = e.Element(b + "SourceType")?.Value ?? "Misc",
            Original = new XElement(e),
        };
        foreach (var (name, _, set) in Fields)
        {
            var v = e.Element(b + name)?.Value;
            if (!string.IsNullOrWhiteSpace(v)) set(s, v);
        }
        var author = e.Element(b + "Author");
        var main = author?.Element(b + "Author");
        if (main != null)
        {
            s.CorporateAuthor = main.Element(b + "Corporate")?.Value;
            s.Authors = People(main);
        }
        var editor = author?.Element(b + "Editor");
        if (editor != null) s.Editors = People(editor);
        return s;
    }

    private static List<SourcePerson> People(XElement role) =>
        role.Element(Ns.B + "NameList")?.Elements(Ns.B + "Person").Select(p => new SourcePerson
        {
            Last = p.Element(Ns.B + "Last")?.Value ?? "",
            First = p.Element(Ns.B + "First")?.Value ?? "",
            Middle = p.Element(Ns.B + "Middle")?.Value ?? "",
        }).ToList() ?? new List<SourcePerson>();

    public XElement ToXml()
    {
        var b = Ns.B;
        var known = new HashSet<string>(Fields.Select(f => f.Element).Concat(new[] { "Tag", "SourceType", "Author" }));
        var e = new XElement(b + "Source",
            new XElement(b + "Tag", Tag),
            new XElement(b + "SourceType", Type));
        if (Authors.Count > 0 || CorporateAuthor != null || Editors.Count > 0)
        {
            var author = new XElement(b + "Author");
            if (CorporateAuthor != null) author.Add(new XElement(b + "Author", new XElement(b + "Corporate", CorporateAuthor)));
            else if (Authors.Count > 0) author.Add(new XElement(b + "Author", NameList(Authors)));
            if (Editors.Count > 0) author.Add(new XElement(b + "Editor", NameList(Editors)));
            e.Add(author);
        }
        foreach (var (name, get, _) in Fields)
        {
            var v = get(this);
            if (!string.IsNullOrWhiteSpace(v)) e.Add(new XElement(b + name, v));
        }
        // Fields Inkwell doesn't edit (StandardNumber, LCID, Comments…) stay.
        if (Original != null)
            foreach (var other in Original.Elements().Where(x => x.Name.Namespace == b && !known.Contains(x.Name.LocalName)))
                e.Add(new XElement(other));
        return e;
    }

    private static XElement NameList(IEnumerable<SourcePerson> people) =>
        new(Ns.B + "NameList", people.Select(p =>
        {
            var person = new XElement(Ns.B + "Person", new XElement(Ns.B + "Last", p.Last));
            if (p.First.Length > 0) person.Add(new XElement(Ns.B + "First", p.First));
            if (p.Middle.Length > 0) person.Add(new XElement(Ns.B + "Middle", p.Middle));
            return person;
        }));
}

/// <summary>
/// The bibliography sources (Word's b:Sources custom XML part) and the chosen citation style.
/// Word reads the same part, so sources added in Inkwell show up in Word's Source Manager.
/// </summary>
public sealed class BibliographySources
{
    public List<BibSource> Items { get; set; } = new();

    /// <summary>APA, MLA, Chicago or IEEE (Inkwell's styles); stored in the part's StyleName attribute.</summary>
    public string StyleName { get; set; } = "APA";

    /// <summary>Word's style sheet path (SelectedStyle), matched to <see cref="StyleName"/> on save.</summary>
    public string? SelectedStyle { get; set; }

    /// <summary>The customXml part's path in the package (e.g. /customXml/item1.xml), when loaded from one.</summary>
    public string? PartPath { get; set; }

    public bool IsEmpty => Items.Count == 0;

    public BibSource? Find(string tag) => Items.FirstOrDefault(s => string.Equals(s.Tag, tag, StringComparison.OrdinalIgnoreCase));

    public BibliographySources Clone() => new()
    {
        Items = Items.Select(s => s.Clone()).ToList(),
        StyleName = StyleName,
        SelectedStyle = SelectedStyle,
        PartPath = PartPath,
    };

    public static BibliographySources FromXml(XDocument doc, string? partPath)
    {
        var root = doc.Root;
        var result = new BibliographySources { PartPath = partPath };
        if (root == null || root.Name != Ns.B + "Sources") return result;
        var styleName = root.Attribute("StyleName")?.Value;
        result.SelectedStyle = root.Attribute("SelectedStyle")?.Value;
        result.StyleName = NormalizeStyle(styleName, result.SelectedStyle);
        result.Items = root.Elements(Ns.B + "Source").Select(BibSource.FromXml).ToList();
        return result;
    }

    public XDocument ToXml()
    {
        var root = new XElement(Ns.B + "Sources",
            new XAttribute(XNamespace.Xmlns + "b", Ns.B.NamespaceName),
            new XAttribute("xmlns", Ns.B.NamespaceName),
            new XAttribute("SelectedStyle", WordStyleSheet(StyleName)),
            new XAttribute("StyleName", StyleName),
            new XAttribute("Version", "6"));
        foreach (var s in Items) root.Add(s.ToXml());
        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root);
    }

    /// <summary>The XSL file name Word uses for a style.</summary>
    public static string WordStyleSheet(string style) => style switch
    {
        "MLA" => "\\MLASeventhEditionOfficeOnline.xsl",
        "Chicago" => "\\CHICAGO.XSL",
        "IEEE" => "\\IEEE2006OfficeOnline.xsl",
        _ => "\\APASixthEditionOfficeOnline.xsl",
    };

    public static string NormalizeStyle(string? styleName, string? selected)
    {
        var text = (styleName ?? "") + " " + (selected ?? "");
        if (text.IndexOf("MLA", StringComparison.OrdinalIgnoreCase) >= 0) return "MLA";
        if (text.IndexOf("Chicago", StringComparison.OrdinalIgnoreCase) >= 0) return "Chicago";
        if (text.IndexOf("IEEE", StringComparison.OrdinalIgnoreCase) >= 0) return "IEEE";
        return "APA";
    }
}
