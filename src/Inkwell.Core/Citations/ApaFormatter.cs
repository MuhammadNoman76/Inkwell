using Inkwell.Core.Model;
using static Inkwell.Core.Citations.CiteText;

namespace Inkwell.Core.Citations;

/// <summary>
/// APA 7th edition. References: "Lovelace, A. A., &amp; Babbage, C. (1843). <i>Title</i> (2nd ed.). Publisher.";
/// in text: "(Lovelace &amp; Babbage, 1843, p. 12)". Titles are kept as typed (APA wants sentence case; Inkwell
/// doesn't change capitalisation).
/// </summary>
internal sealed class ApaFormatter : StyleFormatter
{
    public override CitationStyle Style => CitationStyle.Apa;

    public override string AuthorLabel(BibSource s) => AuthorElement(s) ?? "";

    /// <summary>"Lovelace, A. A., &amp; Babbage, C." / "World Health Organization" / "Babbage, C. (Ed.)", or null.</summary>
    private static string? AuthorElement(BibSource s)
    {
        var people = Authors(s);
        if (people.Count > 0) return Names(people);
        var corporate = Corporate(s);
        if (corporate.Length > 0) return corporate;
        var editors = Editors(s);
        if (editors.Count > 0) return Names(editors) + (editors.Count == 1 ? " (Ed.)" : " (Eds.)");
        return null;
    }

    /// <summary>Inverted names with initials: "A, B, &amp; C"; 21 or more: the first 19, ". . .", the last.</summary>
    private static string Names(List<SourcePerson> people)
    {
        var names = people.Select(p => Inverted(p, initials: true)).ToList();
        if (names.Count == 1) return names[0];
        if (names.Count > 20) return string.Join(", ", names.Take(19)) + ", . . . " + names[names.Count - 1];
        return string.Join(", ", names.Take(names.Count - 1)) + ", & " + names[names.Count - 1];
    }

    /// <summary>Editors in normal order for "In C. Babbage &amp; M. Somerville (Eds.), …".</summary>
    private static string EditorsNormal(List<SourcePerson> editors)
    {
        var names = editors.Select(p => Normal(p, initials: true)).ToList();
        if (names.Count == 1) return names[0];
        if (names.Count == 2) return names[0] + " & " + names[1];
        return string.Join(", ", names.Take(names.Count - 1)) + ", & " + names[names.Count - 1];
    }

    private static string DateElement(BibSource s, SourceKind kind, CitationEngine engine)
    {
        var year = engine.YearLabel(s);
        if ((kind is SourceKind.Periodical or SourceKind.Web) && Has(s.Month) && Has(s.Year))
        {
            var md = MonthFull(s.Month) + (Has(s.Day) ? " " + Clean(s.Day) : "");
            return "(" + year + ", " + md + ")";
        }
        return "(" + year + ")";
    }

    private static StyledText TitleElement(BibSource s, SourceKind kind)
    {
        var title = Clean(s.Title);
        var t = new StyledText();
        if (title.Length == 0) return t;
        if (IsPart(kind))
        {
            t.Append(title);
            return t;
        }
        t.AppendItalic(title);
        var info = new List<string>();
        if (kind == SourceKind.Book)
        {
            var editors = Editors(s);
            if (Authors(s).Count + Corporate(s).Length > 0 && editors.Count > 0)
                info.Add(EditorsNormal(editors) + (editors.Count == 1 ? ", Ed." : ", Eds."));
            var details = new List<string>();
            var edition = Edition(s.Edition);
            if (edition.Length > 0) details.Add(edition);
            if (Has(s.Volume)) details.Add("Vol. " + Clean(s.Volume));
            if (details.Count > 0) info.Insert(0, string.Join(", ", details));
        }
        if (info.Count > 0) t.Append(" (" + string.Join("; ", info) + ")");
        if ((kind is SourceKind.Electronic or SourceKind.Misc) && Has(s.Medium)) t.Append(" [" + Clean(s.Medium) + "]");
        return t;
    }

    private static StyledText SourceElement(BibSource s, SourceKind kind, string? author)
    {
        var t = new StyledText();
        var publisher = Publisher(s, kind);
        // APA leaves the publisher out when it is the author.
        if (author != null && SameText(publisher, author)) publisher = "";
        switch (kind)
        {
            case SourceKind.BookSection:
            case SourceKind.Conference:
            {
                var container = Container(s, kind);
                var editors = Editors(s);
                var hasPeople = Authors(s).Count + Corporate(s).Length > 0;
                if (container.Length > 0 || (hasPeople && editors.Count > 0))
                {
                    t.Append("In ");
                    if (hasPeople && editors.Count > 0)
                        t.Append(EditorsNormal(editors) + (editors.Count == 1 ? " (Ed.)" : " (Eds.)") + (container.Length > 0 ? ", " : ""));
                    t.AppendItalic(container);
                    var details = new List<string>();
                    var edition = Edition(s.Edition);
                    if (edition.Length > 0) details.Add(edition);
                    if (Has(s.Volume)) details.Add("Vol. " + Clean(s.Volume));
                    if (Has(s.Pages)) details.Add(PageLocator(s.Pages));
                    if (details.Count > 0) t.Append(" (" + string.Join(", ", details) + ")");
                }
                else if (Has(s.Pages))
                {
                    t.Append(PageLocator(s.Pages));
                }
                if (publisher.Length > 0)
                {
                    t.Terminate().Space();
                    t.Append(publisher);
                }
                break;
            }
            case SourceKind.Journal:
            case SourceKind.Periodical:
            {
                var container = Container(s, kind);
                t.AppendItalic(container);
                if (Has(s.Volume))
                {
                    if (!t.IsEmpty) t.Append(", ");
                    t.AppendItalic(Clean(s.Volume));
                    if (Has(s.Issue)) t.Append("(" + Clean(s.Issue) + ")");
                }
                else if (Has(s.Issue))
                {
                    if (!t.IsEmpty) t.Append(", ");
                    t.Append("(" + Clean(s.Issue) + ")");
                }
                if (Has(s.Pages))
                {
                    if (!t.IsEmpty) t.Append(", ");
                    t.Append(PageRange(s.Pages));
                }
                break;
            }
            case SourceKind.Web:
            {
                var site = Container(s, kind);
                if (author != null && SameText(site, author)) site = "";
                t.Append(site);
                break;
            }
            default:
                t.Append(publisher);
                break;
        }
        return t;
    }

    public override StyledText Reference(BibSource s, CitationEngine engine, bool repeatedAuthor)
    {
        var kind = KindOf(s);
        var author = AuthorElement(s);
        var date = DateElement(s, kind, engine);
        var title = TitleElement(s, kind);
        var t = new StyledText();
        if (author != null)
        {
            t.Append(author).Terminate();
            t.Space().Append(date + ".");
            if (!title.IsEmpty) t.Space().Append(title).Terminate();
        }
        else
        {
            // No author: the title moves to the author position.
            if (!title.IsEmpty) t.Append(title).Terminate().Space();
            t.Append(date + ".");
        }
        var source = SourceElement(s, kind, author);
        if (!source.IsEmpty) t.Space().Append(source).Terminate();

        var doi = DoiUrl(s.Doi);
        var url = Clean(s.Url);
        if (doi.Length > 0)
        {
            t.Space().Append(doi);
        }
        else if (url.Length > 0)
        {
            if (kind == SourceKind.Web && HasAccessed(s))
                t.Space().Append("Retrieved " + DateMdy(s.YearAccessed, s.MonthAccessed, s.DayAccessed, shortMonth: false) + ", from " + url);
            else
                t.Space().Append(url);
        }
        return t;
    }

    public override StyledText InText(IReadOnlyList<ResolvedRef> items, CitationEngine engine)
    {
        // APA lists several works in one citation alphabetically, as in the reference list.
        var comparer = StringComparer.Create(System.Globalization.CultureInfo.InvariantCulture, true);
        var ordered = items
            .Select((r, i) => (Item: r, Index: i))
            .OrderBy(x => x.Item.Source == null ? 1 : 0)
            .ThenBy(x => x.Item.Source == null ? "" : InTextNames(x.Item.Source, "&", 2), comparer)
            .ThenBy(x => x.Item.Source == null ? "" : engine.YearLabel(x.Item.Source), comparer)
            .ThenBy(x => x.Index)
            .Select(x => x.Item)
            .ToList();

        var parts = new List<StyledText>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var (r, s) = (ordered[i].Ref, ordered[i].Source);
            if (s == null)
            {
                parts.Add(Missing(r));
                continue;
            }
            // Same author, several years: "Lovelace, 1843, 1850".
            if (IsPlain(r) && !r.SuppressAuthor && !r.SuppressYear)
            {
                var label = InTextNames(s, "&", 2);
                var years = new List<string> { engine.YearLabel(s) };
                while (label.Length > 0 && i + 1 < ordered.Count && ordered[i + 1].Source is { } next && IsPlain(ordered[i + 1].Ref) &&
                       !ordered[i + 1].Ref.SuppressAuthor && !ordered[i + 1].Ref.SuppressYear && InTextNames(next, "&", 2) == label)
                {
                    years.Add(engine.YearLabel(next));
                    i++;
                }
                if (years.Count > 1)
                {
                    parts.Add(new StyledText(label + ", " + string.Join(", ", years)));
                    continue;
                }
            }
            parts.Add(WithAffixes(r, One(r, s, engine)));
        }
        return Wrap(parts, "(", ")");
    }

    private static bool IsPlain(CitationRef r) => !Has(r.Pages) && !Has(r.Prefix) && !Has(r.Suffix) && !Has(r.Volume);

    private static StyledText One(CitationRef r, BibSource s, CitationEngine engine)
    {
        var rest = new List<string>();
        if (!r.SuppressYear) rest.Add(engine.YearLabel(s));
        if (Has(r.Volume)) rest.Add("Vol. " + Clean(r.Volume));
        if (Has(r.Pages)) rest.Add(PageLocator(r.Pages));
        var t = new StyledText();
        if (!r.SuppressAuthor)
        {
            var names = InTextNames(s, "&", 2);
            if (names.Length > 0)
            {
                t.Append(names);
                if (rest.Count > 0) t.Append(", ");
            }
            else if (!r.SuppressTitle)
            {
                // No author: the title, with the comma inside the quotes ("“Title,” 2020").
                var kind = KindOf(s);
                var quoted = IsPart(kind) || (kind == SourceKind.Web && Has(s.InternetSiteTitle));
                if (quoted)
                {
                    t.Append(InTextTitle(s, rest.Count > 0 ? "," : null));
                    if (rest.Count > 0) t.Append(" ");
                }
                else
                {
                    t.Append(InTextTitle(s));
                    if (rest.Count > 0) t.Append(", ");
                }
            }
        }
        t.Append(string.Join(", ", rest));
        return t;
    }
}
