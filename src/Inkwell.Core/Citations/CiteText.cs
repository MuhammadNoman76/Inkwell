using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Inkwell.Core.Model;

namespace Inkwell.Core.Citations;

/// <summary>Text helpers shared by the citation styles: names, initials, dates, page ranges, editions, DOIs.</summary>
internal static class CiteText
{
    public const string OpenQuote = "“";
    public const string CloseQuote = "”";
    public const string EnDash = "–";

    public static bool Has(string? s) => !string.IsNullOrWhiteSpace(s);

    public static string Clean(string? s) => s == null ? "" : Regex.Replace(s.Trim(), @"\s+", " ");

    /// <summary>Ends with . ? or !, possibly followed by closing quotes (not brackets: "(Ed.)" still takes a period).</summary>
    public static bool EndsWithTerminal(string text)
    {
        for (var i = text.Length - 1; i >= 0; i--)
        {
            var c = text[i];
            if (c is '”' or '’' or '"' or '\'') continue;
            return c is '.' or '?' or '!';
        }
        return false;
    }

    /// <summary>Ends with ? or ! (a title that already carries its own punctuation).</summary>
    public static bool EndsWithQuestionOrExclamation(string text)
    {
        var t = text.TrimEnd();
        return t.Length > 0 && t[t.Length - 1] is '?' or '!';
    }

    /// <summary>“Title<paramref name="mark"/>” with the mark inside the quotes (dropped when the title ends with ? or !).</summary>
    public static string Quoted(string title, string mark)
    {
        title = Clean(title);
        if (EndsWithQuestionOrExclamation(title) || (mark == "." && title.EndsWith(".", StringComparison.Ordinal))) mark = "";
        return OpenQuote + title + mark + CloseQuote;
    }

    // ---- names ----

    public static string Given(SourcePerson p) => Clean(p.First + " " + p.Middle);

    public static string Last(SourcePerson p) => Has(p.Last) ? Clean(p.Last) : Clean(p.First);

    /// <summary>"Ada Augusta" → "A. A."; "Jean-Paul" → "J.-P."; initials already given ("M.") are kept.</summary>
    public static string Initials(SourcePerson p)
    {
        if (!Has(p.Last)) return "";
        var parts = Given(p).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>();
        foreach (var part in parts)
        {
            var pieces = part.Split('-').Where(x => x.Length > 0).Select(Initial).Where(x => x.Length > 0);
            var joined = string.Join("-", pieces);
            if (joined.Length > 0) result.Add(joined);
        }
        return string.Join(" ", result);
    }

    private static string Initial(string name)
    {
        // "M." or "M" stays "M."; "Ma." (an abbreviation like "Wm.") stays as typed.
        var trimmed = name.TrimEnd('.');
        if (trimmed.Length == 0) return "";
        if (name.EndsWith(".", StringComparison.Ordinal) && trimmed.Length <= 3) return trimmed + ".";
        var first = 0;
        while (first < trimmed.Length && !char.IsLetter(trimmed[first])) first++;
        if (first >= trimmed.Length) return "";
        var len = char.IsHighSurrogate(trimmed[first]) && first + 1 < trimmed.Length ? 2 : 1;
        return trimmed.Substring(first, len).ToUpperInvariant() + ".";
    }

    /// <summary>"Lovelace, A. A." (initials) or "Lovelace, Ada Augusta".</summary>
    public static string Inverted(SourcePerson p, bool initials)
    {
        var last = Last(p);
        if (!Has(p.Last)) return last;
        var given = initials ? Initials(p) : Given(p);
        return given.Length > 0 ? last + ", " + given : last;
    }

    /// <summary>"A. A. Lovelace" (initials) or "Ada Augusta Lovelace".</summary>
    public static string Normal(SourcePerson p, bool initials)
    {
        var last = Last(p);
        if (!Has(p.Last)) return last;
        var given = initials ? Initials(p) : Given(p);
        return given.Length > 0 ? given + " " + last : last;
    }

    public static List<SourcePerson> Valid(IEnumerable<SourcePerson> people) =>
        people.Where(p => Has(p.Last) || Has(p.First) || Has(p.Middle)).ToList();

    /// <summary>"A, B, and C" / "A and B" (serial comma before the conjunction when there are three or more).</summary>
    public static string Series(IReadOnlyList<string> items, string conjunction)
    {
        if (items.Count == 0) return "";
        if (items.Count == 1) return items[0];
        if (items.Count == 2) return items[0] + " " + conjunction + " " + items[1];
        return string.Join(", ", items.Take(items.Count - 1)) + ", " + conjunction + " " + items[items.Count - 1];
    }

    // ---- dates ----

    private static readonly string[] MonthNames =
        { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };

    private static readonly string[] MonthAbbreviations =
        { "Jan.", "Feb.", "Mar.", "Apr.", "May", "June", "July", "Aug.", "Sept.", "Oct.", "Nov.", "Dec." };

    /// <summary>1–12 for a month given as a number or an English name / abbreviation, or 0.</summary>
    public static int MonthNumber(string? month)
    {
        var m = Clean(month).TrimEnd('.');
        if (m.Length == 0) return 0;
        if (int.TryParse(m, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n is >= 1 and <= 12 ? n : 0;
        if (m.Length < 3) return 0;
        for (var i = 0; i < 12; i++)
            if (MonthNames[i].StartsWith(m, StringComparison.OrdinalIgnoreCase)) return i + 1;
        return 0;
    }

    /// <summary>The month's full English name ("March"); unknown text is returned as typed.</summary>
    public static string MonthFull(string? month)
    {
        var n = MonthNumber(month);
        return n > 0 ? MonthNames[n - 1] : Clean(month);
    }

    /// <summary>MLA / IEEE abbreviation ("Mar.", "Sept.", May, June and July in full).</summary>
    public static string MonthShort(string? month)
    {
        var n = MonthNumber(month);
        return n > 0 ? MonthAbbreviations[n - 1] : Clean(month);
    }

    /// <summary>"March 15, 1843" / "March 1843" / "1843" (any part may be missing).</summary>
    public static string DateMdy(string? year, string? month, string? day, bool shortMonth)
    {
        var m = Has(month) ? (shortMonth ? MonthShort(month) : MonthFull(month)) : "";
        var d = Has(month) && Has(day) ? Clean(day) : "";
        var y = Clean(year);
        var md = d.Length > 0 ? m + " " + d : m;
        if (md.Length == 0) return y;
        if (y.Length == 0) return md;
        return d.Length > 0 ? md + ", " + y : md + " " + y;
    }

    /// <summary>"15 Mar. 1843" (MLA).</summary>
    public static string DateDmy(string? year, string? month, string? day)
    {
        var parts = new List<string>();
        if (Has(month) && Has(day)) parts.Add(Clean(day));
        if (Has(month)) parts.Add(MonthShort(month));
        if (Has(year)) parts.Add(Clean(year));
        return string.Join(" ", parts);
    }

    // ---- numbers, pages, editions ----

    /// <summary>Hyphens between page numbers become en dashes: "45-67" → "45–67".</summary>
    public static string PageRange(string? pages)
    {
        var p = Clean(pages);
        return Regex.Replace(p, @"(?<=[0-9A-Za-z])\s*(?:-{1,2}|‐|‑|‒|—)\s*(?=[0-9A-Za-z])", EnDash);
    }

    /// <summary>More than one page ("12–14", "12, 15", "12 & 15").</summary>
    public static bool IsPageRange(string pages) =>
        pages.IndexOfAny(new[] { '–', '-', ',', '&', '—' }) >= 0;

    /// <summary>A locator that already names its unit ("para. 3", "chap. 2", "fig. 1").</summary>
    public static bool HasOwnLabel(string locator) => locator.Length > 0 && char.IsLetter(locator[0]) && !Regex.IsMatch(locator, "^[ivxlcdm]+$", RegexOptions.IgnoreCase);

    /// <summary>"2" → "2nd ed."; "Revised" → "Revised ed."; "3rd ed." stays; first editions give "".</summary>
    public static string Edition(string? edition)
    {
        var e = Clean(edition).TrimEnd('.');
        if (e.Length == 0) return "";
        if (Regex.IsMatch(e, @"^(1|1st|first)$", RegexOptions.IgnoreCase)) return "";
        if (int.TryParse(e, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return NumberFormats.Ordinal(n) + " ed.";
        if (Regex.IsMatch(e, @"\b(ed|edn|edition)$", RegexOptions.IgnoreCase))
            return Regex.Replace(e, @"\b(edn|edition)$", "ed", RegexOptions.IgnoreCase) + ".";
        return e + " ed.";
    }

    // ---- links ----

    /// <summary>The DOI without a resolver prefix: "10.1000/xyz".</summary>
    public static string BareDoi(string? doi)
    {
        var d = Clean(doi);
        d = Regex.Replace(d, @"^(https?://(dx\.)?doi\.org/|doi:\s*)", "", RegexOptions.IgnoreCase);
        return d;
    }

    /// <summary>"https://doi.org/10.1000/xyz".</summary>
    public static string DoiUrl(string? doi)
    {
        var d = BareDoi(doi);
        return d.Length == 0 ? "" : "https://doi.org/" + d;
    }

    // ---- titles ----

    /// <summary>The title without its subtitle ("Notes: A study" → "Notes"), for in-text citations.</summary>
    public static string ShortTitle(string? title)
    {
        var t = Clean(title);
        var colon = t.IndexOf(": ", StringComparison.Ordinal);
        if (colon > 0) t = t.Substring(0, colon);
        return t.TrimEnd('.', ',', ';');
    }

    /// <summary>The title for sorting: without a leading article and quotes.</summary>
    public static string SortTitle(string? title)
    {
        var t = Clean(title).TrimStart('"', '“', '‘', '\'', '(', '[');
        var m = Regex.Match(t, @"^(the|an|a)\s+", RegexOptions.IgnoreCase);
        return m.Success ? t.Substring(m.Length) : t;
    }

    public static bool SameText(string? a, string? b) =>
        string.Equals(Clean(a), Clean(b), StringComparison.OrdinalIgnoreCase);

    public static int Compare(string a, string b) =>
        string.Compare(a, b, CultureInfo.InvariantCulture, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreSymbols);

    /// <summary>Lower-case text without diacritics, for identity checks (same author list).</summary>
    public static string Fold(string text)
    {
        var d = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
