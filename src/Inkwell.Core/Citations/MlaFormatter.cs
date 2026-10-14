using Inkwell.Core.Model;
using static Inkwell.Core.Citations.CiteText;

namespace Inkwell.Core.Citations;

/// <summary>
/// MLA 9th edition. Works Cited: "Lovelace, Ada, and Charles Babbage. <i>Title</i>. 2nd ed., Publisher, 1843.";
/// in text: "(Lovelace and Babbage 12)". A later entry by the same authors starts with "---.". When the list has
/// several works by the same author, in-text citations add a short title: "(Lovelace, <i>Notes</i> 12)".
/// </summary>
internal sealed class MlaFormatter : StyleFormatter
{
    public override CitationStyle Style => CitationStyle.Mla;

    public override string AuthorLabel(BibSource s) => AuthorElement(s) ?? "";

    private static string? AuthorElement(BibSource s)
    {
        var people = Authors(s);
        if (people.Count > 0) return Names(people);
        var corporate = Corporate(s);
        // An organisation that is also the publisher is given only as the publisher.
        if (corporate.Length > 0) return SameText(corporate, s.Publisher) ? null : corporate;
        var editors = Editors(s);
        if (editors.Count > 0) return Names(editors) + (editors.Count == 1 ? ", editor" : ", editors");
        return null;
    }

    /// <summary>"Lovelace, Ada" / "Lovelace, Ada, and Charles Babbage" / "Lovelace, Ada, et al.".</summary>
    private static string Names(List<SourcePerson> people)
    {
        var first = Inverted(people[0], initials: false);
        if (people.Count == 1) return first;
        if (people.Count == 2) return first + ", and " + Normal(people[1], initials: false);
        return first + ", et al.";
    }

    /// <summary>"Charles Babbage" / "Charles Babbage and Mary Somerville" / "Charles Babbage et al." (after "edited by").</summary>
    private static string EditorsNormal(List<SourcePerson> editors)
    {
        if (editors.Count == 1) return Normal(editors[0], initials: false);
        if (editors.Count == 2) return Normal(editors[0], false) + " and " + Normal(editors[1], false);
        return Normal(editors[0], false) + " et al.";
    }

    private static string Pages(string? pages)
    {
        var p = PageRange(pages);
        if (p.Length == 0) return "";
        if (HasOwnLabel(p)) return p;
        return (IsPageRange(p) ? "pp. " : "p. ") + p;
    }

    public override StyledText Reference(BibSource s, CitationEngine engine, bool repeatedAuthor)
    {
        var kind = KindOf(s);
        var t = new StyledText();
        var author = AuthorElement(s);
        if (author != null) t.Append(repeatedAuthor ? "---" : author).Terminate().Space();

        var title = Clean(s.Title);
        var site = Container(s, kind);
        var quotedTitle = IsPart(kind) || (kind == SourceKind.Web && site.Length > 0);
        if (title.Length > 0)
        {
            if (quotedTitle) t.Append(Quoted(title, "."));
            else t.AppendItalic(title).Terminate();
        }

        // The container and publication facts, separated by commas.
        var parts = new List<StyledText>();
        void Add(string? text, bool italic = false)
        {
            if (Has(text)) parts.Add(new StyledText(Clean(text), italic));
        }

        var editors = Editors(s);
        var editedBy = Authors(s).Count + Corporate(s).Length > 0 && editors.Count > 0 ? "edited by " + EditorsNormal(editors) : "";
        var publisher = Publisher(s, kind);
        if (author != null && SameText(publisher, author)) publisher = "";
        var year = Clean(s.Year);
        switch (kind)
        {
            case SourceKind.Book:
                Add(editedBy);
                Add(Edition(s.Edition));
                if (Has(s.Volume)) Add("vol. " + Clean(s.Volume));
                Add(publisher);
                Add(year);
                break;
            case SourceKind.BookSection:
            case SourceKind.Conference:
                Add(Container(s, kind), italic: true);
                Add(editedBy);
                Add(Edition(s.Edition));
                if (Has(s.Volume)) Add("vol. " + Clean(s.Volume));
                Add(publisher);
                Add(year);
                Add(Pages(s.Pages));
                break;
            case SourceKind.Journal:
            case SourceKind.Periodical:
                Add(Container(s, kind), italic: true);
                if (Has(s.Volume)) Add("vol. " + Clean(s.Volume));
                if (Has(s.Issue)) Add("no. " + Clean(s.Issue));
                Add(DateDmy(s.Year, s.Month, s.Day));
                Add(Pages(s.Pages));
                break;
            case SourceKind.Web:
                Add(site, italic: true);
                if (!SameText(publisher, site)) Add(publisher);
                Add(DateDmy(s.Year, s.Month, s.Day));
                break;
            case SourceKind.Report:
                Add(publisher);
                Add(DateDmy(s.Year, s.Month, s.Day));
                break;
            default:
                Add(publisher);
                Add(DateDmy(s.Year, s.Month, s.Day));
                break;
        }
        var doi = DoiUrl(s.Doi);
        Add(doi.Length > 0 ? doi : Clean(s.Url));

        if (parts.Count > 0)
        {
            t.Space();
            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                // An element that starts a sentence is capitalised ("Edited by …").
                if (i == 0 && part[0].Text.StartsWith("edited by", StringComparison.Ordinal))
                    part = new StyledText("E" + part[0].Text.Substring(1), part[0].Italic);
                if (i > 0) t.Append(", ");
                t.Append(part);
            }
            t.Terminate();
        }
        if (HasAccessed(s))
            t.Space().Append("Accessed " + DateDmy(s.YearAccessed, s.MonthAccessed, s.DayAccessed) + ".");
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
                var names = InTextNames(s, "and", 2);
                if (names.Length > 0)
                {
                    body.Append(names);
                    // Several works by this author: add a short title.
                    if (engine.HasOtherWorksBySameAuthor(s) && !r.SuppressTitle && Has(s.Title)) body.Append(", ").Append(InTextTitle(s));
                }
                else if (!r.SuppressTitle)
                {
                    body.Append(InTextTitle(s));
                }
            }
            var locator = PageRange(r.Pages);
            if (Has(r.Volume)) locator = Clean(r.Volume) + (locator.Length > 0 ? ": " + locator : "");
            if (locator.Length > 0) body.Space().Append(locator);
            if (body.IsEmpty && !r.SuppressTitle) body.Append(InTextTitle(s));
            parts.Add(WithAffixes(r, body));
        }
        return Wrap(parts, "(", ")");
    }
}
