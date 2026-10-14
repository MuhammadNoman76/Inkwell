using System.Globalization;
using System.Text;
using Inkwell.Core.Model;

namespace Inkwell.Core.Citations;

/// <summary>One source cited in a citation, with Word's per-source options.</summary>
public sealed class CitationRef
{
    public CitationRef()
    {
    }

    public CitationRef(string tag, string? pages = null)
    {
        Tag = tag;
        Pages = pages;
    }

    /// <summary>The source's tag (b:Tag).</summary>
    public string Tag { get; set; } = "";

    /// <summary>Page or other locator (\p): "12", "12-14", "para. 3".</summary>
    public string? Pages { get; set; }

    /// <summary>Volume (\v).</summary>
    public string? Volume { get; set; }

    /// <summary>Text before the citation (\f), e.g. "see".</summary>
    public string? Prefix { get; set; }

    /// <summary>Text after the citation (\s), e.g. "emphasis added".</summary>
    public string? Suffix { get; set; }

    /// <summary>Leave the author out (\n): "(1843, p. 12)".</summary>
    public bool SuppressAuthor { get; set; }

    /// <summary>Leave the year out (\y).</summary>
    public bool SuppressYear { get; set; }

    /// <summary>Leave the title out (\t) when it would stand in for a missing author.</summary>
    public bool SuppressTitle { get; set; }

    public CitationRef Clone() => (CitationRef)MemberwiseClone();
}

/// <summary>
/// A CITATION field code: <c>CITATION Lov43 \l 1033 \p 12 \m Bab64 \p 3</c>. Switches before the first \m belong to
/// the first source; switches after "\m Tag" belong to that source. \l (language) applies to the whole citation.
/// </summary>
public sealed class CitationField
{
    public List<CitationRef> Sources { get; } = new();

    /// <summary>The language id (\l), e.g. 1033 for en-US.</summary>
    public int? LanguageId { get; set; }

    public CitationField()
    {
    }

    public CitationField(params CitationRef[] sources)
    {
        Sources.AddRange(sources);
    }

    /// <summary>Parses a CITATION field code; null when the code isn't a citation or names no source.</summary>
    public static CitationField? Parse(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var parsed = FieldCode.Parse(code!);
        if (parsed.Type != "CITATION") return null;
        var field = new CitationField();
        CitationRef? current = null;
        if (parsed.FirstArgument is { } tag)
        {
            current = new CitationRef { Tag = tag };
            field.Sources.Add(current);
        }
        foreach (var (rawName, value) in parsed.Switches)
        {
            var name = rawName.ToLowerInvariant();
            if (name == "\\l")
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lcid)) field.LanguageId = lcid;
                continue;
            }
            if (name == "\\m")
            {
                if (string.IsNullOrWhiteSpace(value)) continue;
                current = new CitationRef { Tag = value!.Trim() };
                field.Sources.Add(current);
                continue;
            }
            if (current == null) continue;
            switch (name)
            {
                case "\\p": current.Pages = value; break;
                case "\\v": current.Volume = value; break;
                case "\\f": current.Prefix = value; break;
                case "\\s": current.Suffix = value; break;
                case "\\n": current.SuppressAuthor = true; break;
                case "\\y": current.SuppressYear = true; break;
                case "\\t": current.SuppressTitle = true; break;
            }
        }
        return field.Sources.Count == 0 ? null : field;
    }

    /// <summary>The field code as Word writes it (with a leading and trailing space).</summary>
    public string ToCode()
    {
        var sb = new StringBuilder(" CITATION");
        for (var i = 0; i < Sources.Count; i++)
        {
            var s = Sources[i];
            sb.Append(i == 0 ? " " : " \\m ").Append(Token(s.Tag, quoteAlways: false));
            if (i == 0 && LanguageId != null) sb.Append(" \\l ").Append(LanguageId.Value.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(s.Pages)) sb.Append(" \\p ").Append(Token(s.Pages!, false));
            if (!string.IsNullOrEmpty(s.Volume)) sb.Append(" \\v ").Append(Token(s.Volume!, false));
            if (!string.IsNullOrEmpty(s.Prefix)) sb.Append(" \\f ").Append(Token(s.Prefix!, true));
            if (!string.IsNullOrEmpty(s.Suffix)) sb.Append(" \\s ").Append(Token(s.Suffix!, true));
            if (s.SuppressAuthor) sb.Append(" \\n");
            if (s.SuppressYear) sb.Append(" \\y");
            if (s.SuppressTitle) sb.Append(" \\t");
        }
        return sb.Append(' ').ToString();
    }

    private static string Token(string value, bool quoteAlways)
    {
        var needsQuotes = quoteAlways || value.Length == 0 || value.Any(c => char.IsWhiteSpace(c) || c == '"' || c == '\\');
        return needsQuotes ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;
    }

    /// <summary>The tags cited, in order.</summary>
    public IEnumerable<string> Tags => Sources.Select(s => s.Tag);
}

/// <summary>The BIBLIOGRAPHY field: <c>BIBLIOGRAPHY \l 1033</c>.</summary>
public static class BibliographyField
{
    public static bool IsBibliography(string? code) => code != null && FieldCode.KindOf(code) == FieldKind.Bibliography;

    /// <summary>The \l language id, if any.</summary>
    public static int? LanguageId(string? code)
    {
        if (!IsBibliography(code)) return null;
        var value = FieldCode.Parse(code!).SwitchValue("\\l");
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lcid) ? lcid : null;
    }

    /// <summary>" BIBLIOGRAPHY " or " BIBLIOGRAPHY \l 1033 ".</summary>
    public static string ToCode(int? languageId = null) =>
        languageId == null ? " BIBLIOGRAPHY " : " BIBLIOGRAPHY \\l " + languageId.Value.ToString(CultureInfo.InvariantCulture) + " ";
}
