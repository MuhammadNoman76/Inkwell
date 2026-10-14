using System.Globalization;
using System.Text.RegularExpressions;
using Inkwell.Core.Model;
using static Inkwell.Core.Citations.CiteText;

namespace Inkwell.Core.Citations;

/// <summary>One entry of a formatted bibliography.</summary>
public sealed class BibliographyEntry
{
    public BibliographyEntry(BibSource source, int? number, StyledText text)
    {
        Source = source;
        Number = number;
        Text = text;
    }

    public BibSource Source { get; }

    /// <summary>The IEEE reference number (null in the other styles).</summary>
    public int? Number { get; }

    /// <summary>"[1]" in IEEE, null otherwise.</summary>
    public string? Label => Number == null ? null : "[" + Number.Value.ToString(CultureInfo.InvariantCulture) + "]";

    public StyledText Text { get; }

    public override string ToString() => (Label != null ? Label + " " : "") + Text.PlainText;
}

/// <summary>
/// Formats in-text citations and the bibliography for one source list in one style. It works out what depends on
/// the whole list: "2020a" / "2020b" for works by the same authors in the same year (APA, Chicago), IEEE numbers
/// in order of first citation, and short titles for authors with several works (MLA). Create a new engine when the
/// sources, the style or the citation order change; it is cheap.
/// </summary>
public sealed class CitationEngine
{
    /// <summary>What Word shows when a bibliography has no sources.</summary>
    public const string NoSourcesText = "There are no sources in the current document.";

    private readonly List<BibSource> _sources;
    private readonly Dictionary<string, BibSource> _byTag = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<BibSource, string> _yearLabels = new(ReferenceEqualityComparer<BibSource>.Instance);
    private readonly Dictionary<string, int> _numbers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<BibSource> _sharedAuthor = new(ReferenceEqualityComparer<BibSource>.Instance);
    private readonly StyleFormatter _formatter;

    /// <param name="style">The citation style.</param>
    /// <param name="sources">The document's sources (Word's "current list").</param>
    /// <param name="citedTagsInOrder">The tags of the citations in document order (repeats allowed); IEEE numbers follow it.</param>
    public CitationEngine(CitationStyle style, IEnumerable<BibSource> sources, IEnumerable<string>? citedTagsInOrder = null)
    {
        Style = style;
        _formatter = style switch
        {
            CitationStyle.Mla => new MlaFormatter(),
            CitationStyle.Chicago => new ChicagoFormatter(),
            CitationStyle.Ieee => new IeeeFormatter(),
            _ => new ApaFormatter(),
        };
        _sources = sources.Where(s => s != null).ToList();
        foreach (var s in _sources)
            if (Has(s.Tag) && !_byTag.ContainsKey(s.Tag.Trim())) _byTag[s.Tag.Trim()] = s;
        ComputeYearLabels();
        ComputeSharedAuthors();
        ComputeNumbers(citedTagsInOrder ?? Enumerable.Empty<string>());
    }

    /// <summary>An engine for a document: its sources, its style (unless given) and the order of its CITATION fields.</summary>
    public static CitationEngine ForDocument(Document document, CitationStyle? style = null) =>
        new(style ?? CitationStyles.FromName(document.Sources.StyleName), document.Sources.Items, CitedTags(document));

    /// <summary>
    /// The tags cited by the CITATION fields of a document in reading order: the body (tables and content controls
    /// included), then footnotes, then endnotes. Deleted tracked citations are skipped.
    /// </summary>
    public static List<string> CitedTags(Document document)
    {
        var result = new List<string>();
        var stories = new List<Story> { document.Body };
        stories.AddRange(document.Footnotes.Values.Where(n => !n.IsSpecial));
        stories.AddRange(document.Endnotes.Values.Where(n => !n.IsSpecial));
        foreach (var story in stories)
            foreach (var p in story.Paragraphs)
                foreach (var inline in p.Inlines)
                    if (inline is FieldInline { Kind: FieldKind.Citation } f && f.Deleted == null && CitationField.Parse(f.Code) is { } field)
                        result.AddRange(field.Tags);
        return result;
    }

    public CitationStyle Style { get; }

    public IReadOnlyList<BibSource> Sources => _sources;

    /// <summary>The source with this tag (ignoring case), or null.</summary>
    public BibSource? Find(string? tag) => tag != null && _byTag.TryGetValue(tag.Trim(), out var s) ? s : null;

    /// <summary>The IEEE reference number of a source, or null if the tag is unknown.</summary>
    public int? NumberOf(string? tag) => tag != null && _numbers.TryGetValue(tag.Trim(), out var n) ? n : null;

    /// <summary>The year as cited: "1843", "1843a", "n.d." (and "n.d.-a").</summary>
    public string YearLabel(BibSource source) =>
        _yearLabels.TryGetValue(source, out var label) ? label : YearText(source);

    /// <summary>True when the list holds other works by the same author (MLA adds a short title to the citation).</summary>
    public bool HasOtherWorksBySameAuthor(BibSource source) => _sharedAuthor.Contains(source);

    // ---- in-text citations ----

    /// <summary>The in-text citation for a CITATION field (unknown tags are shown as typed).</summary>
    public StyledText Cite(CitationField field) => Cite(field.Sources);

    public StyledText Cite(IEnumerable<CitationRef> refs)
    {
        var items = refs.Where(r => r != null).Select(r => new ResolvedRef(r, Find(r.Tag))).ToList();
        if (items.Count == 0) return new StyledText();
        return _formatter.InText(items, this);
    }

    public StyledText Cite(string tag, string? pages = null) => Cite(new[] { new CitationRef(tag, pages) });

    /// <summary>The tags of a field that aren't in the source list.</summary>
    public List<string> MissingTags(CitationField field) => field.Tags.Where(t => Find(t) == null).ToList();

    // ---- bibliography ----

    /// <summary>One source's bibliography entry (without the IEEE number).</summary>
    public StyledText Reference(BibSource source) => _formatter.Reference(source, this, repeatedAuthor: false);

    /// <summary>
    /// The bibliography: every source in the list, alphabetically (APA, MLA, Chicago) or by number (IEEE: cited
    /// sources in order of first citation, then uncited ones alphabetically).
    /// </summary>
    public List<BibliographyEntry> Bibliography()
    {
        var result = new List<BibliographyEntry>();
        if (Style == CitationStyle.Ieee)
        {
            foreach (var s in _sources.Where(s => NumberOf(s.Tag) != null).OrderBy(s => NumberOf(s.Tag)!.Value))
                result.Add(new BibliographyEntry(s, NumberOf(s.Tag), _formatter.Reference(s, this, false)));
            return result;
        }
        string? previousAuthor = null;
        foreach (var s in Sorted(_sources))
        {
            var author = _formatter.AuthorLabel(s);
            var repeated = Style == CitationStyle.Mla && author.Length > 0 && previousAuthor != null && SameText(author, previousAuthor);
            result.Add(new BibliographyEntry(s, null, _formatter.Reference(s, this, repeated)));
            previousAuthor = author;
        }
        return result;
    }

    /// <summary>Sources in reference-list order for this style (alphabetical; IEEE: by number).</summary>
    public List<BibSource> Sorted(IEnumerable<BibSource> sources)
    {
        if (Style == CitationStyle.Ieee) return sources.OrderBy(s => NumberOf(s.Tag) ?? int.MaxValue).ToList();
        var list = sources.ToList();
        var index = new Dictionary<BibSource, int>(ReferenceEqualityComparer<BibSource>.Instance);
        for (var i = list.Count - 1; i >= 0; i--) index[list[i]] = i;
        list.Sort((a, b) =>
        {
            var c = CompareForList(a, b);
            return c != 0 ? c : index[a].CompareTo(index[b]);
        });
        return list;
    }

    private int CompareForList(BibSource a, BibSource b)
    {
        var names = CompareNames(SortNames(a), SortNames(b));
        if (names != 0) return names;
        if (Style == CitationStyle.Mla)
        {
            var t = Compare(SortTitle(a.Title), SortTitle(b.Title));
            if (t != 0) return t;
            return YearOrder(a).CompareTo(YearOrder(b));
        }
        var y = YearOrder(a).CompareTo(YearOrder(b));
        if (y != 0) return y;
        return Compare(SortTitle(a.Title), SortTitle(b.Title));
    }

    /// <summary>The names a reference sorts by: each author (surname, given names), the organisation, the editors, or the title.</summary>
    private static List<(string Last, string Given)> SortNames(BibSource s)
    {
        var people = Valid(s.Authors);
        if (people.Count > 0) return people.Select(p => (Last(p), Given(p))).ToList();
        if (Has(s.CorporateAuthor)) return new List<(string, string)> { (SortTitle(s.CorporateAuthor), "") };
        var editors = Valid(s.Editors);
        if (editors.Count > 0) return editors.Select(p => (Last(p), Given(p))).ToList();
        return new List<(string, string)> { (SortTitle(s.Title), "") };
    }

    /// <summary>Surname first, then given names, author by author; a list that is a prefix of another sorts first ("nothing precedes something").</summary>
    private static int CompareNames(List<(string Last, string Given)> a, List<(string Last, string Given)> b)
    {
        for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            var c = Compare(a[i].Last, b[i].Last);
            if (c == 0) c = Compare(a[i].Given, b[i].Given);
            if (c != 0) return c;
        }
        return a.Count.CompareTo(b.Count);
    }

    /// <summary>Undated works first, then by year, "in press" last.</summary>
    private static int YearOrder(BibSource s)
    {
        var m = Regex.Match(Clean(s.Year), @"\d{3,4}");
        if (m.Success) return int.Parse(m.Value, CultureInfo.InvariantCulture);
        return Has(s.Year) && Clean(s.Year).IndexOf("press", StringComparison.OrdinalIgnoreCase) >= 0 ? int.MaxValue : -1;
    }

    private static string YearText(BibSource s) => Has(s.Year) ? Clean(s.Year) : "n.d.";

    /// <summary>Who wrote it, for "same authors" checks: surnames and initials, the organisation or the editors.</summary>
    private static string? AuthorIdentity(BibSource s)
    {
        var people = Valid(s.Authors);
        if (people.Count > 0) return "a:" + string.Join("|", people.Select(p => Fold(Last(p)) + "," + Fold(Initials(p))));
        if (Has(s.CorporateAuthor)) return "c:" + Fold(Clean(s.CorporateAuthor));
        var editors = Valid(s.Editors);
        if (editors.Count > 0) return "e:" + string.Join("|", editors.Select(p => Fold(Last(p)) + "," + Fold(Initials(p))));
        return null;
    }

    private void ComputeYearLabels()
    {
        if (Style is not (CitationStyle.Apa or CitationStyle.Chicago)) return;
        var groups = _sources
            .Select(s => (Source: s, Author: AuthorIdentity(s)))
            .Where(x => x.Author != null)
            .GroupBy(x => x.Author + "\u0001" + YearText(x.Source).ToLowerInvariant());
        foreach (var g in groups)
        {
            var list = g.Select(x => x.Source).ToList();
            if (list.Count < 2) continue;
            list.Sort((a, b) =>
            {
                var c = Compare(SortTitle(a.Title), SortTitle(b.Title));
                return c != 0 ? c : _sources.IndexOf(a).CompareTo(_sources.IndexOf(b));
            });
            for (var i = 0; i < list.Count; i++)
            {
                var year = YearText(list[i]);
                var letter = NumberFormats.Letters(i + 1);
                _yearLabels[list[i]] = Has(list[i].Year) ? year + letter : year + "-" + letter;
            }
        }
    }

    private void ComputeSharedAuthors()
    {
        if (Style != CitationStyle.Mla) return;
        foreach (var g in _sources.Select(s => (Source: s, Author: AuthorIdentity(s))).Where(x => x.Author != null).GroupBy(x => x.Author))
            if (g.Count() > 1)
                foreach (var x in g) _sharedAuthor.Add(x.Source);
    }

    private void ComputeNumbers(IEnumerable<string> cited)
    {
        var n = 0;
        foreach (var tag in cited)
        {
            var s = Find(tag);
            if (s == null || _numbers.ContainsKey(s.Tag.Trim())) continue;
            _numbers[s.Tag.Trim()] = ++n;
        }
        // Sources that aren't cited follow, alphabetically.
        var uncited = _sources.Where(s => Has(s.Tag) && !_numbers.ContainsKey(s.Tag.Trim())).ToList();
        uncited.Sort((a, b) =>
        {
            var c = CompareNames(SortNames(a), SortNames(b));
            if (c == 0) c = Compare(SortTitle(a.Title), SortTitle(b.Title));
            return c != 0 ? c : _sources.IndexOf(a).CompareTo(_sources.IndexOf(b));
        });
        foreach (var s in uncited)
            if (!_numbers.ContainsKey(s.Tag.Trim())) _numbers[s.Tag.Trim()] = ++n;
    }
}
