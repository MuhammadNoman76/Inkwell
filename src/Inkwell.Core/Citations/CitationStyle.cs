using Inkwell.Core.Model;

namespace Inkwell.Core.Citations;

/// <summary>The citation styles Inkwell formats.</summary>
public enum CitationStyle
{
    /// <summary>APA 7th edition: (Lovelace, 1843) and a "References" list.</summary>
    Apa,

    /// <summary>MLA 9th edition: (Lovelace 12) and a "Works Cited" list.</summary>
    Mla,

    /// <summary>Chicago Manual of Style 17th edition, author-date: (Lovelace 1843, 12) and a "References" list.</summary>
    Chicago,

    /// <summary>IEEE: [1], numbered in the order sources are first cited.</summary>
    Ieee,
}

public static class CitationStyles
{
    /// <summary>The style for a name stored in the sources part ("APA", "MLA", "Chicago", "IEEE", or Word's style sheet names).</summary>
    public static CitationStyle FromName(string? name) => BibliographySources.NormalizeStyle(name, null) switch
    {
        "MLA" => CitationStyle.Mla,
        "Chicago" => CitationStyle.Chicago,
        "IEEE" => CitationStyle.Ieee,
        _ => CitationStyle.Apa,
    };

    /// <summary>The name stored in <see cref="BibliographySources.StyleName"/>.</summary>
    public static string ToName(CitationStyle style) => style switch
    {
        CitationStyle.Mla => "MLA",
        CitationStyle.Chicago => "Chicago",
        CitationStyle.Ieee => "IEEE",
        _ => "APA",
    };

    /// <summary>The name shown in menus.</summary>
    public static string DisplayName(CitationStyle style) => style switch
    {
        CitationStyle.Mla => "MLA (9th edition)",
        CitationStyle.Chicago => "Chicago (17th edition, author-date)",
        CitationStyle.Ieee => "IEEE",
        _ => "APA (7th edition)",
    };

    /// <summary>The usual heading of the bibliography in this style.</summary>
    public static string BibliographyTitle(CitationStyle style) => style == CitationStyle.Mla ? "Works Cited" : "References";
}
