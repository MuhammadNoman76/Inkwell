using Inkwell.Core.Model;
using static Inkwell.Core.Citations.CiteText;

namespace Inkwell.Core.Citations;

/// <summary>How a source type is treated by the styles.</summary>
internal enum SourceKind
{
    Book,
    BookSection,
    Journal,
    Periodical,
    Conference,
    Report,
    Web,
    Electronic,
    Misc,
}

/// <summary>A cited source resolved against the source list (Source is null for an unknown tag).</summary>
internal readonly record struct ResolvedRef(CitationRef Ref, BibSource? Source);

/// <summary>One citation style: reference-list entries and in-text citations.</summary>
internal abstract class StyleFormatter
{
    public abstract CitationStyle Style { get; }

    /// <summary>The bibliography entry (without the IEEE number). <paramref name="repeatedAuthor"/>: same authors as the previous entry (MLA "---.").</summary>
    public abstract StyledText Reference(BibSource s, CitationEngine engine, bool repeatedAuthor);

    /// <summary>The in-text citation for one or more sources.</summary>
    public abstract StyledText InText(IReadOnlyList<ResolvedRef> items, CitationEngine engine);

    /// <summary>The author element as it appears in the reference list (used to detect repeated authors).</summary>
    public abstract string AuthorLabel(BibSource s);

    public static SourceKind KindOf(BibSource s)
    {
        switch ((s.Type ?? "").Trim().ToLowerInvariant())
        {
            case "book": return SourceKind.Book;
            case "booksection": return SourceKind.BookSection;
            case "journalarticle": return SourceKind.Journal;
            case "articleinaperiodical": return SourceKind.Periodical;
            case "conferenceproceedings": return SourceKind.Conference;
            case "report": return SourceKind.Report;
            case "internetsite":
            case "documentfrominternetsite": return SourceKind.Web;
            case "electronicsource": return SourceKind.Electronic;
            default: return SourceKind.Misc;
        }
    }

    /// <summary>Works that live inside a container (article in a journal, chapter in a book): their titles go in quotes.</summary>
    public static bool IsPart(SourceKind kind) =>
        kind is SourceKind.BookSection or SourceKind.Journal or SourceKind.Periodical or SourceKind.Conference;

    protected static List<SourcePerson> Authors(BibSource s) => Valid(s.Authors);

    protected static List<SourcePerson> Editors(BibSource s) => Valid(s.Editors);

    protected static string Corporate(BibSource s) => Clean(s.CorporateAuthor);

    /// <summary>The container title of a part (journal, periodical, book, conference).</summary>
    protected static string Container(BibSource s, SourceKind kind) => kind switch
    {
        SourceKind.Journal => FirstOf(s.JournalName, s.PeriodicalTitle),
        SourceKind.Periodical => FirstOf(s.PeriodicalTitle, s.JournalName),
        SourceKind.BookSection => Clean(s.BookTitle),
        SourceKind.Conference => FirstOf(s.ConferenceName, s.BookTitle),
        SourceKind.Web => Clean(s.InternetSiteTitle),
        _ => "",
    };

    protected static string FirstOf(params string?[] values)
    {
        foreach (var v in values)
            if (Has(v)) return Clean(v);
        return "";
    }

    protected static string Publisher(BibSource s, SourceKind kind) =>
        kind == SourceKind.Report ? FirstOf(s.Institution, s.Publisher) : FirstOf(s.Publisher, s.Institution);

    protected static bool HasAccessed(BibSource s) => Has(s.YearAccessed);

    /// <summary>In-text names: "Lovelace", "Lovelace and Babbage" / "Lovelace &amp; Babbage", "Lovelace et al.".</summary>
    protected static string InTextNames(BibSource s, string and, int maxListed)
    {
        var corporate = Corporate(s);
        var people = Authors(s);
        if (people.Count == 0 && corporate.Length == 0) people = Editors(s);
        if (people.Count == 0) return corporate;
        if (people.Count > maxListed) return Last(people[0]) + " et al.";
        var lasts = people.Select(Last).ToList();
        if (lasts.Count == 2) return lasts[0] + " " + and + " " + lasts[1];
        return Series(lasts, and);
    }

    /// <summary>The short title for an in-text citation (italic for whole works, in quotes for parts).</summary>
    protected static StyledText InTextTitle(BibSource s, string? quotedMark = null)
    {
        var title = ShortTitle(s.Title);
        if (title.Length == 0) title = Clean(s.Tag);
        var kind = KindOf(s);
        if (IsPart(kind) || (kind == SourceKind.Web && Has(s.InternetSiteTitle)))
            return new StyledText(OpenQuote + title + (quotedMark ?? "") + CloseQuote);
        var t = new StyledText(title, italic: true);
        if (quotedMark != null) t.Append(quotedMark);
        return t;
    }

    /// <summary>True if the source has an author of some kind (people, organisation or editors).</summary>
    protected static bool HasAuthor(BibSource s) => Authors(s).Count > 0 || Corporate(s).Length > 0 || Editors(s).Count > 0;

    /// <summary>Prefix / suffix around one citation: "see " + text + ", emphasis added".</summary>
    protected static StyledText WithAffixes(CitationRef r, StyledText body)
    {
        var t = new StyledText();
        if (Has(r.Prefix))
        {
            var prefix = r.Prefix!;
            t.Append(prefix);
            if (!body.IsEmpty && !char.IsWhiteSpace(prefix[prefix.Length - 1])) t.Append(" ");
        }
        t.Append(body);
        if (Has(r.Suffix))
        {
            var suffix = r.Suffix!;
            if (!body.IsEmpty && char.IsLetterOrDigit(suffix[0])) t.Append(", ");
            t.Append(suffix);
        }
        return t;
    }

    /// <summary>"p. 12" / "pp. 12–14" (or the locator as typed when it names its own unit).</summary>
    protected static string PageLocator(string? pages)
    {
        var p = PageRange(pages);
        if (p.Length == 0) return "";
        if (HasOwnLabel(p)) return p;
        return (IsPageRange(p) ? "pp. " : "p. ") + p;
    }

    /// <summary>The unknown-tag text shown in place of a citation for a source that isn't in the list.</summary>
    protected static StyledText Missing(CitationRef r) => new(Clean(r.Tag).Length > 0 ? Clean(r.Tag) : "?");

    /// <summary>Joins in-text parts with "; " inside the given brackets.</summary>
    protected static StyledText Wrap(IEnumerable<StyledText> parts, string open, string close, string separator = "; ")
    {
        var t = new StyledText(open);
        var first = true;
        foreach (var p in parts)
        {
            if (p.IsEmpty) continue;
            if (!first) t.Append(separator);
            t.Append(p);
            first = false;
        }
        return t.Append(close);
    }
}
