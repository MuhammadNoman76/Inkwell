using Inkwell.Core.Model;
using static Inkwell.Core.Citations.CiteText;

namespace Inkwell.Core.Citations;

/// <summary>
/// IEEE. References are numbered in the order the sources are first cited: "[1] A. A. Lovelace and C. Babbage,
/// <i>Title</i>, 2nd ed. London: Taylor, 1843."; in text: "[1]", "[1, p. 12]", "[1], [3]", "[1]–[3]".
/// </summary>
internal sealed class IeeeFormatter : StyleFormatter
{
    public override CitationStyle Style => CitationStyle.Ieee;

    public override string AuthorLabel(BibSource s) => AuthorElement(s) ?? "";

    private static string? AuthorElement(BibSource s)
    {
        var people = Authors(s);
        if (people.Count > 0) return Names(people);
        var corporate = Corporate(s);
        if (corporate.Length > 0) return corporate;
        var editors = Editors(s);
        if (editors.Count > 0) return Names(editors) + (editors.Count == 1 ? ", Ed." : ", Eds.");
        return null;
    }

    /// <summary>"A. A. Lovelace", "A and B", "A, B, and C"; seven or more: "A et al.".</summary>
    private static string Names(List<SourcePerson> people)
    {
        if (people.Count >= 7) return Normal(people[0], initials: true) + " et al.";
        return Series(people.Select(p => Normal(p, initials: true)).ToList(), "and");
    }

    private static string EditorsNormal(List<SourcePerson> editors) =>
        Names(editors) + (editors.Count == 1 ? ", Ed." : ", Eds.");

    private static string Pages(string? pages)
    {
        var p = PageRange(pages);
        if (p.Length == 0) return "";
        return (IsPageRange(p) ? "pp. " : "p. ") + p;
    }

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
        var author = AuthorElement(s);
        var title = Clean(s.Title);
        var t = new StyledText();
        var hasPeople = Authors(s).Count + Corporate(s).Length > 0;
        var editors = Editors(s);
        var doi = BareDoi(s.Doi);
        var url = Clean(s.Url);

        if (kind == SourceKind.Web)
        {
            // Online sources: A. Author. “Title.” Site. URL (accessed Mon. day, year).
            var pieces = new List<string>();
            if (author != null) pieces.Add(new StyledText(author).Terminate().PlainText);
            if (title.Length > 0) pieces.Add(Quoted(title, "."));
            var site = Container(s, kind);
            if (site.Length > 0) pieces.Add(new StyledText(site).Terminate().PlainText);
            if (url.Length > 0) pieces.Add(url);
            if (HasAccessed(s)) pieces.Add("(accessed " + DateMdy(s.YearAccessed, s.MonthAccessed, s.DayAccessed, shortMonth: true) + ").");
            return new StyledText(string.Join(" ", pieces));
        }

        if (author != null) t.Append(author + ", ");
        var tail = new List<string>();
        var year = Clean(s.Year);
        switch (kind)
        {
            case SourceKind.Book:
            {
                t.AppendItalic(title);
                var edition = Edition(s.Edition);
                if (edition.Length > 0) t.Append(", " + edition);
                if (hasPeople && editors.Count > 0) t.Append(", " + EditorsNormal(editors));
                t.Terminate();
                var place = Place(s, kind);
                if (place.Length > 0) tail.Add(place);
                if (year.Length > 0) tail.Add(year);
                break;
            }
            case SourceKind.BookSection:
            {
                if (title.Length > 0) t.Append(Quoted(title, ",") + " ");
                var container = Container(s, kind);
                if (container.Length > 0) t.Append("in ").AppendItalic(container);
                var edition = Edition(s.Edition);
                if (edition.Length > 0) t.Append(", " + edition);
                if (hasPeople && editors.Count > 0) t.Append(", " + EditorsNormal(editors));
                t.Terminate();
                var place = Place(s, kind);
                if (place.Length > 0) tail.Add(place);
                if (year.Length > 0) tail.Add(year);
                if (Has(s.Pages)) tail.Add(Pages(s.Pages));
                break;
            }
            case SourceKind.Journal:
            case SourceKind.Periodical:
            {
                if (title.Length > 0) t.Append(Quoted(title, ",") + " ");
                t.AppendItalic(Container(s, kind));
                if (Has(s.Volume)) tail.Add("vol. " + Clean(s.Volume));
                if (Has(s.Issue)) tail.Add("no. " + Clean(s.Issue));
                if (Has(s.Pages)) tail.Add(Pages(s.Pages));
                var date = DateMdy(s.Year, s.Month, kind == SourceKind.Periodical ? s.Day : null, shortMonth: true);
                if (date.Length > 0) tail.Add(date);
                break;
            }
            case SourceKind.Conference:
            {
                if (title.Length > 0) t.Append(Quoted(title, ",") + " ");
                var container = Container(s, kind);
                if (container.Length > 0) t.Append("in ").AppendItalic(container);
                if (Has(s.City)) tail.Add(Clean(s.City));
                if (year.Length > 0) tail.Add(year);
                if (Has(s.Pages)) tail.Add(Pages(s.Pages));
                break;
            }
            case SourceKind.Report:
            {
                if (title.Length > 0) t.Append(Quoted(title, ","));
                var publisher = Publisher(s, kind);
                if (publisher.Length > 0) tail.Add(publisher);
                if (Has(s.City)) tail.Add(Clean(s.City));
                if (year.Length > 0) tail.Add(year);
                break;
            }
            default:
            {
                t.AppendItalic(title);
                if (Has(s.Medium)) t.Append(" [" + Clean(s.Medium) + "]");
                t.Terminate();
                var place = Place(s, kind);
                if (place.Length > 0) tail.Add(place);
                if (year.Length > 0) tail.Add(year);
                break;
            }
        }
        if (doi.Length > 0) tail.Add("doi: " + doi);

        // Parts after a full stop start with a space; parts after the container or a quoted title follow a comma.
        var text = TrimEnd(t);
        if (tail.Count > 0)
        {
            if (text.EndsWithTerminal || text.IsEmpty) text.Space();
            else if (text.LastChar == ',' || text.LastChar == '”') text.Space();
            else text.Append(", ");
            text.Append(string.Join(", ", tail));
        }
        text = TrimEnd(text);
        // Nothing followed a quoted title: “Title,” → “Title.”
        if (text.PlainText.EndsWith(",”", StringComparison.Ordinal)) text = Replace(text, ",”", ".”");
        else if (text.LastChar == ',') text = Replace(text, ",", "");
        text.Terminate();
        if (doi.Length == 0 && url.Length > 0) text.Append(" [Online]. Available: " + url);
        return text;
    }

    /// <summary>Removes trailing spaces.</summary>
    private static StyledText TrimEnd(StyledText t)
    {
        var copy = new StyledText();
        var spans = t.ToList();
        while (spans.Count > 0 && spans[spans.Count - 1].Text.TrimEnd().Length == 0) spans.RemoveAt(spans.Count - 1);
        for (var i = 0; i < spans.Count; i++)
        {
            var text = i == spans.Count - 1 ? spans[i].Text.TrimEnd() : spans[i].Text;
            copy.Append(text, spans[i].Italic, spans[i].Bold);
        }
        return copy;
    }

    /// <summary>Replaces a trailing piece of text (a dangling comma inside a quoted title becomes nothing).</summary>
    private static StyledText Replace(StyledText t, string trailing, string with)
    {
        var spans = t.ToList();
        var last = spans[spans.Count - 1];
        if (last.Text.EndsWith(trailing, StringComparison.Ordinal))
            spans[spans.Count - 1] = last with { Text = last.Text.Substring(0, last.Text.Length - trailing.Length) + with };
        var copy = new StyledText();
        foreach (var sp in spans) copy.Append(sp.Text, sp.Italic, sp.Bold);
        return copy;
    }

    public override StyledText InText(IReadOnlyList<ResolvedRef> items, CitationEngine engine)
    {
        // Plain numbers are sorted and runs of three or more collapse: [1]–[3], [5].
        var plain = new SortedSet<int>();
        var others = new List<StyledText>();
        var missing = new List<StyledText>();
        foreach (var (r, s) in items)
        {
            var n = s == null ? null : engine.NumberOf(s.Tag);
            if (n == null)
            {
                missing.Add(new StyledText("[" + Missing(r).PlainText + "]"));
                continue;
            }
            if (!Has(r.Pages) && !Has(r.Prefix) && !Has(r.Suffix) && !Has(r.Volume))
            {
                plain.Add(n.Value);
                continue;
            }
            var inner = n.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (Has(r.Volume)) inner += ", vol. " + Clean(r.Volume);
            if (Has(r.Pages)) inner += ", " + (HasOwnLabel(PageRange(r.Pages)) ? PageRange(r.Pages) : Pages(r.Pages));
            others.Add(WithAffixes(r, new StyledText("[" + inner + "]")));
        }

        var parts = new List<StyledText>();
        var numbers = plain.ToList();
        for (var i = 0; i < numbers.Count;)
        {
            var j = i;
            while (j + 1 < numbers.Count && numbers[j + 1] == numbers[j] + 1) j++;
            if (j - i >= 2)
            {
                parts.Add(new StyledText("[" + numbers[i] + "]" + EnDash + "[" + numbers[j] + "]"));
            }
            else
            {
                for (var k = i; k <= j; k++) parts.Add(new StyledText("[" + numbers[k] + "]"));
            }
            i = j + 1;
        }
        parts.AddRange(others);
        parts.AddRange(missing);
        var t = new StyledText();
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0) t.Append(", ");
            t.Append(parts[i]);
        }
        return t;
    }
}
