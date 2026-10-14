using Inkwell.Core.Model;
using static Inkwell.Core.Citations.CiteText;

namespace Inkwell.Core.Citations;

/// <summary>
/// The Chicago Manual of Style, 17th edition, author-date system. References: "Lovelace, Ada, and Charles Babbage.
/// 1843. <i>Title</i>. 2nd ed. London: Taylor."; in text: "(Lovelace and Babbage 1843, 12)"; four or more authors
/// are cited as "Lovelace et al.".
/// </summary>
internal sealed class ChicagoFormatter : StyleFormatter
{
    public override CitationStyle Style => CitationStyle.Chicago;

    public override string AuthorLabel(BibSource s) => AuthorElement(s) ?? "";

    private static string? AuthorElement(BibSource s)
    {
        var people = Authors(s);
        if (people.Count > 0) return Names(people);
        var corporate = Corporate(s);
        if (corporate.Length > 0) return corporate;
        var editors = Editors(s);
        if (editors.Count > 0) return Names(editors) + (editors.Count == 1 ? ", ed" : ", eds");
        return null;
    }

    /// <summary>First author inverted, the others in normal order; more than ten: the first seven and "et al."</summary>
    private static string Names(List<SourcePerson> people)
    {
        var first = Inverted(people[0], initials: false);
        if (people.Count == 1) return first;
        if (people.Count == 2) return first + ", and " + Normal(people[1], initials: false);
        if (people.Count > 10)
            return first + ", " + string.Join(", ", people.Skip(1).Take(6).Select(p => Normal(p, false))) + ", et al";
        return first + ", " + string.Join(", ", people.Skip(1).Take(people.Count - 2).Select(p => Normal(p, false))) +
               ", and " + Normal(people[people.Count - 1], false);
    }

    private static string EditorsNormal(List<SourcePerson> editors) =>
        Series(editors.Select(p => Normal(p, initials: false)).ToList(), "and");

    /// <summary>"London: Taylor" / "Taylor" / "London".</summary>
    private static string Place(BibSource s, SourceKind kind)
    {
        var city = Clean(s.City);
        var publisher = Publisher(s, kind);
        if (city.Length > 0 && publisher.Length > 0) return city + ": " + publisher;
        return city.Length > 0 ? city : publisher;
    }

    public override StyledText Reference(BibSource s, CitationEngine engine, bool repeatedAuthor)
    {
        var kind = KindOf(s);
        var t = new StyledText();
        var author = AuthorElement(s);
        var year = engine.YearLabel(s);
        var title = Clean(s.Title);
        var quoted = IsPart(kind) || (kind == SourceKind.Web && Has(s.InternetSiteTitle));
        var titleText = new StyledText();
        if (title.Length > 0)
        {
            if (quoted) titleText.Append(Quoted(title, "."));
            else titleText.AppendItalic(title).Terminate();
        }

        // "n.d." already ends with a period.
        if (author != null)
        {
            t.Append(author).Terminate().Space().Append(year).Terminate();
            if (!titleText.IsEmpty) t.Space().Append(titleText);
        }
        else
        {
            if (!titleText.IsEmpty) t.Append(titleText).Space();
            t.Append(year).Terminate();
        }

        void Sentence(StyledText? part)
        {
            if (part == null || part.IsEmpty) return;
            t.Space().Append(part).Terminate();
        }

        StyledText? Plain(string? text) => Has(text) ? new StyledText(Clean(text)) : null;

        var editors = Editors(s);
        var hasPeople = Authors(s).Count + Corporate(s).Length > 0;
        switch (kind)
        {
            case SourceKind.Book:
                if (hasPeople && editors.Count > 0) Sentence(Plain("Edited by " + EditorsNormal(editors)));
                Sentence(Plain(Has(s.Volume) ? "Vol. " + Clean(s.Volume) : null));
                Sentence(Plain(Edition(s.Edition)));
                Sentence(Plain(Place(s, kind)));
                break;
            case SourceKind.BookSection:
            case SourceKind.Conference:
            {
                var container = Container(s, kind);
                var inPart = new StyledText();
                if (container.Length > 0) inPart.Append("In ").AppendItalic(container);
                var edition = Edition(s.Edition);
                if (edition.Length > 0 && !inPart.IsEmpty) inPart.Append(", " + edition);
                if (hasPeople && editors.Count > 0) inPart.Append((inPart.IsEmpty ? "Edited by " : ", edited by ") + EditorsNormal(editors));
                if (Has(s.Pages)) inPart.Append((inPart.IsEmpty ? "" : ", ") + PageRange(s.Pages));
                Sentence(inPart);
                Sentence(Plain(Place(s, kind)));
                break;
            }
            case SourceKind.Journal:
            {
                var part = new StyledText();
                part.AppendItalic(Container(s, kind));
                if (Has(s.Volume))
                {
                    part.Space().Append(Clean(s.Volume));
                    if (Has(s.Issue)) part.Append(" (" + Clean(s.Issue) + ")");
                }
                else if (Has(s.Issue))
                {
                    part.Append((part.IsEmpty ? "" : ", ") + "no. " + Clean(s.Issue));
                }
                if (Has(s.Pages)) part.Append((part.IsEmpty ? "" : ": ") + PageRange(s.Pages));
                Sentence(part);
                break;
            }
            case SourceKind.Periodical:
            {
                var part = new StyledText();
                part.AppendItalic(Container(s, kind));
                if (Has(s.Month)) part.Append((part.IsEmpty ? "" : ", ") + DateMdy(s.Year, s.Month, s.Day, shortMonth: false));
                if (Has(s.Pages)) part.Append((part.IsEmpty ? "" : ", ") + PageRange(s.Pages));
                Sentence(part);
                break;
            }
            case SourceKind.Web:
                Sentence(Plain(Container(s, kind)));
                if (Has(s.Month)) Sentence(Plain(DateMdy(s.Year, s.Month, s.Day, shortMonth: false)));
                break;
            case SourceKind.Report:
                Sentence(Plain(Place(s, kind)));
                break;
            default:
                Sentence(Plain(Has(s.Medium) ? Clean(s.Medium) : null));
                Sentence(Plain(Place(s, kind)));
                break;
        }
        if (HasAccessed(s) && (kind == SourceKind.Web || Has(s.Url)))
            Sentence(Plain("Accessed " + DateMdy(s.YearAccessed, s.MonthAccessed, s.DayAccessed, shortMonth: false)));
        var doi = DoiUrl(s.Doi);
        var url = Clean(s.Url);
        if (doi.Length > 0 || url.Length > 0) t.Space().Append((doi.Length > 0 ? doi : url) + ".");
        return t;
    }

    public override StyledText InText(IReadOnlyList<ResolvedRef> items, CitationEngine engine)
    {
        var parts = new List<StyledText>();
        foreach (var (r, s) in items)
        {
            if (s == null)
            {
                parts.Add(Missing(r));
                continue;
            }
            var body = new StyledText();
            if (!r.SuppressAuthor)
            {
                var names = InTextNames(s, "and", 3);
                if (names.Length > 0) body.Append(names);
                else if (!r.SuppressTitle) body.Append(InTextTitle(s));
            }
            if (!r.SuppressYear) body.Space().Append(engine.YearLabel(s));
            var locator = PageRange(r.Pages);
            if (Has(r.Volume)) locator = Clean(r.Volume) + (locator.Length > 0 ? ":" + locator : "");
            if (locator.Length > 0) body.Append(body.IsEmpty ? locator : ", " + locator);
            parts.Add(WithAffixes(r, body));
        }
        return Wrap(parts, "(", ")");
    }
}
