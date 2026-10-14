using Inkwell.Core.Citations;
using Inkwell.Core.Model;

namespace Inkwell.Tests;

/// <summary>Citation and bibliography formatting in APA 7, MLA 9, Chicago 17 (author-date) and IEEE.</summary>
public class CitationTests
{
    private static SourcePerson P(string last, string first = "", string middle = "") => new() { Last = last, First = first, Middle = middle };

    private static BibSource Book() => new()
    {
        Tag = "Lov43", Type = "Book", Authors = { P("Lovelace", "Ada", "Augusta"), P("Babbage", "Charles") },
        Title = "Notes on the analytical engine", Year = "1843", City = "London", Publisher = "Taylor", Edition = "2",
    };

    private static BibSource SoloBook() => new()
    {
        Tag = "Ada1", Type = "Book", Authors = { P("Lovelace", "Ada") }, Title = "Sketch of the analytical engine", Year = "1842",
        Publisher = "Richard Taylor", Doi = "10.1000/xyz123",
    };

    private static BibSource Journal() => new()
    {
        Tag = "Tur50", Type = "JournalArticle", Authors = { P("Turing", "Alan", "Mathison") }, Title = "Computing machinery and intelligence",
        JournalName = "Mind", Year = "1950", Month = "October", Volume = "59", Issue = "236", Pages = "433-460",
        Doi = "https://doi.org/10.1093/mind/LIX.236.433",
    };

    private static BibSource Chapter() => new()
    {
        Tag = "Hop52", Type = "BookSection", Authors = { P("Hopper", "Grace") }, Editors = { P("Babbage", "Charles"), P("Somerville", "Mary") },
        Title = "The education of a computer", BookTitle = "Readings in computing", Year = "1952", Pages = "1-20", Publisher = "MIT Press",
        City = "Cambridge, MA",
    };

    private static BibSource Magazine() => new()
    {
        Tag = "Lev84", Type = "ArticleInAPeriodical", Authors = { P("Levy", "Steven") }, Title = "Hackers", PeriodicalTitle = "Newsweek",
        Year = "1984", Month = "3", Day = "15", Pages = "4-5",
    };

    private static BibSource Conference() => new()
    {
        Tag = "Ker75", Type = "ConferenceProceedings", Authors = { P("Kernighan", "Brian"), P("Ritchie", "Dennis"), P("Thompson", "Ken") },
        Title = "A portable compiler", ConferenceName = "Proceedings of the Systems Conference", Year = "1975", Pages = "10-19",
        Publisher = "ACM", City = "New York",
    };

    private static BibSource Report() => new()
    {
        Tag = "WHO20", Type = "Report", CorporateAuthor = "World Health Organization", Title = "World health statistics 2020", Year = "2020",
        Publisher = "World Health Organization", City = "Geneva", Url = "https://www.who.int/data",
    };

    private static BibSource WebPage() => new()
    {
        Tag = "Doe23", Type = "InternetSite", Authors = { P("Doe", "Jane") }, Title = "Using fonts on the web", InternetSiteTitle = "MDN Web Docs",
        Year = "2023", Month = "May", Day = "2", Url = "https://developer.mozilla.org/fonts", YearAccessed = "2024", MonthAccessed = "January",
        DayAccessed = "3",
    };

    private static BibSource WebDocument() => new()
    {
        Tag = "Gui", Type = "DocumentFromInternetSite", Title = "Style guide", InternetSiteTitle = "Inkwell", Year = "2026",
        Url = "https://inkwell.example/style",
    };

    private static BibSource Software() => new()
    {
        Tag = "Sw", Type = "ElectronicSource", CorporateAuthor = "Inkwell Project", Title = "Inkwell", Medium = "Computer software",
        Year = "2026", Publisher = "Muhammad Noman", Url = "https://inkwell.example",
    };

    private static BibSource Film() => new() { Tag = "Film", Type = "Film", Title = "Hidden figures", Year = "2016", Publisher = "20th Century Fox" };

    private static BibSource EditedBook() => new()
    {
        Tag = "Ed1", Type = "Book", Editors = { P("Babbage", "Charles") }, Title = "Collected papers", Year = "1850", Publisher = "Murray",
    };

    private static string Ref(CitationStyle style, BibSource s) => new CitationEngine(style, new[] { s }).Reference(s).ToMarkup();

    // ---- APA 7 ----

    [Fact]
    public void Apa_References()
    {
        const CitationStyle apa = CitationStyle.Apa;
        Assert.Equal("Lovelace, A. A., & Babbage, C. (1843). *Notes on the analytical engine* (2nd ed.). Taylor.", Ref(apa, Book()));
        Assert.Equal("Lovelace, A. (1842). *Sketch of the analytical engine*. Richard Taylor. https://doi.org/10.1000/xyz123", Ref(apa, SoloBook()));
        Assert.Equal("Turing, A. M. (1950). Computing machinery and intelligence. *Mind*, *59*(236), 433–460. https://doi.org/10.1093/mind/LIX.236.433", Ref(apa, Journal()));
        Assert.Equal("Hopper, G. (1952). The education of a computer. In C. Babbage & M. Somerville (Eds.), *Readings in computing* (pp. 1–20). MIT Press.", Ref(apa, Chapter()));
        Assert.Equal("Levy, S. (1984, March 15). Hackers. *Newsweek*, 4–5.", Ref(apa, Magazine()));
        Assert.Equal("Kernighan, B., Ritchie, D., & Thompson, K. (1975). A portable compiler. In *Proceedings of the Systems Conference* (pp. 10–19). ACM.", Ref(apa, Conference()));
        // The publisher is left out when it is the author.
        Assert.Equal("World Health Organization. (2020). *World health statistics 2020*. https://www.who.int/data", Ref(apa, Report()));
        Assert.Equal("Doe, J. (2023, May 2). *Using fonts on the web*. MDN Web Docs. Retrieved January 3, 2024, from https://developer.mozilla.org/fonts", Ref(apa, WebPage()));
        // No author: the title moves to the author position.
        Assert.Equal("*Style guide*. (2026). Inkwell. https://inkwell.example/style", Ref(apa, WebDocument()));
        Assert.Equal("Inkwell Project. (2026). *Inkwell* [Computer software]. Muhammad Noman. https://inkwell.example", Ref(apa, Software()));
        Assert.Equal("*Hidden figures*. (2016). 20th Century Fox.", Ref(apa, Film()));
        Assert.Equal("Babbage, C. (Ed.). (1850). *Collected papers*. Murray.", Ref(apa, EditedBook()));
    }

    [Fact]
    public void Apa_MissingYearAndManyAuthors()
    {
        var s = SoloBook();
        s.Year = null;
        s.Doi = null;
        Assert.Equal("Lovelace, A. (n.d.). *Sketch of the analytical engine*. Richard Taylor.", Ref(CitationStyle.Apa, s));

        var many = new BibSource { Tag = "Many", Type = "JournalArticle", Title = "Big team", JournalName = "Science", Year = "2020" };
        for (var i = 1; i <= 22; i++) many.Authors.Add(P("Author" + i, "Ann"));
        var text = Ref(CitationStyle.Apa, many);
        Assert.StartsWith("Author1, A., Author2, A., ", text);
        Assert.Contains("Author19, A., . . . Author22, A. (2020).", text);
        Assert.DoesNotContain("Author20", text);
    }

    [Fact]
    public void Apa_InText()
    {
        var engine = new CitationEngine(CitationStyle.Apa, new[] { Book(), SoloBook(), Conference(), Report(), Film(), WebDocument(), Journal() });
        Assert.Equal("(Lovelace, 1842)", engine.Cite("Ada1").PlainText);
        Assert.Equal("(Lovelace & Babbage, 1843)", engine.Cite("Lov43").PlainText);
        Assert.Equal("(Kernighan et al., 1975)", engine.Cite("Ker75").PlainText);
        Assert.Equal("(Lovelace, 1842, p. 12)", engine.Cite("Ada1", "12").PlainText);
        Assert.Equal("(Lovelace, 1842, pp. 12–14)", engine.Cite("Ada1", "12-14").PlainText);
        Assert.Equal("(Lovelace, 1842, para. 3)", engine.Cite("Ada1", "para. 3").PlainText);
        Assert.Equal("(World Health Organization, 2020)", engine.Cite("WHO20").PlainText);
        Assert.Equal("(*Hidden figures*, 2016)", engine.Cite("Film").ToMarkup());
        Assert.Equal("(“Style guide,” 2026)", engine.Cite("Gui").PlainText);
        Assert.Equal("(1842, p. 12)", engine.Cite(new[] { new CitationRef("Ada1", "12") { SuppressAuthor = true } }).PlainText);
        Assert.Equal("(Lovelace, p. 12)", engine.Cite(new[] { new CitationRef("Ada1", "12") { SuppressYear = true } }).PlainText);
        Assert.Equal("(see Lovelace, 1842, p. 12, emphasis added)",
            engine.Cite(new[] { new CitationRef("Ada1", "12") { Prefix = "see", Suffix = "emphasis added" } }).PlainText);
        // Several sources: alphabetical, separated by semicolons.
        Assert.Equal("(Lovelace, 1842; Lovelace & Babbage, 1843; Turing, 1950)",
            engine.Cite(new[] { new CitationRef("Tur50"), new CitationRef("Lov43"), new CitationRef("Ada1") }).PlainText);
        Assert.Equal("(Nope)", engine.Cite("Nope").PlainText);
    }

    [Fact]
    public void Apa_SameAuthorAndYear_GetLetters()
    {
        var a = SoloBook();
        var b = new BibSource { Tag = "Ada2", Type = "Book", Authors = { P("Lovelace", "Ada") }, Title = "Notes on the engine", Year = "1842", Publisher = "Taylor" };
        var c = new BibSource { Tag = "Ada3", Type = "Book", Authors = { P("Lovelace", "Ada") }, Title = "Letters", Year = "1850", Publisher = "Taylor" };
        var engine = new CitationEngine(CitationStyle.Apa, new[] { a, b, c });
        // Ordered by title: "Notes…" is a, "Sketch…" is b.
        Assert.Equal("1842b", engine.YearLabel(a));
        Assert.Equal("1842a", engine.YearLabel(b));
        Assert.Equal("1850", engine.YearLabel(c));
        Assert.Equal("(Lovelace, 1842b)", engine.Cite("Ada1").PlainText);
        Assert.Equal("(Lovelace, 1842a, 1842b, 1850)",
            engine.Cite(new[] { new CitationRef("Ada1"), new CitationRef("Ada3"), new CitationRef("Ada2") }).PlainText);
        var list = engine.Bibliography().Select(e => e.Source.Tag).ToList();
        Assert.Equal(new[] { "Ada2", "Ada1", "Ada3" }, list);
        Assert.StartsWith("Lovelace, A. (1842a).", engine.Bibliography()[0].Text.PlainText);
    }

    [Fact]
    public void Apa_BibliographyOrder()
    {
        var engine = new CitationEngine(CitationStyle.Apa, new[] { Book(), SoloBook(), Journal(), Chapter(), Film(), Report() });
        var tags = engine.Bibliography().Select(e => e.Source.Tag).ToList();
        // One-author works come before multi-author works by the same first author; no-author works sort by title.
        Assert.Equal(new[] { "Film", "Hop52", "Ada1", "Lov43", "Tur50", "WHO20" }, tags);
        Assert.All(engine.Bibliography(), e => Assert.Null(e.Label));

        var smith = new BibSource { Tag = "S1", Authors = { P("Smith", "Zed") }, Title = "A", Year = "2000" };
        var smithers = new BibSource { Tag = "S2", Authors = { P("Smithers", "Al") }, Title = "B", Year = "2000" };
        var order = new CitationEngine(CitationStyle.Apa, new[] { smithers, smith }).Bibliography().Select(e => e.Source.Tag);
        Assert.Equal(new[] { "S1", "S2" }, order);
    }

    // ---- MLA 9 ----

    [Fact]
    public void Mla_References()
    {
        const CitationStyle mla = CitationStyle.Mla;
        Assert.Equal("Lovelace, Ada Augusta, and Charles Babbage. *Notes on the analytical engine*. 2nd ed., Taylor, 1843.", Ref(mla, Book()));
        Assert.Equal("Lovelace, Ada. *Sketch of the analytical engine*. Richard Taylor, 1842, https://doi.org/10.1000/xyz123.", Ref(mla, SoloBook()));
        Assert.Equal("Turing, Alan Mathison. “Computing machinery and intelligence.” *Mind*, vol. 59, no. 236, Oct. 1950, pp. 433–460, https://doi.org/10.1093/mind/LIX.236.433.", Ref(mla, Journal()));
        Assert.Equal("Hopper, Grace. “The education of a computer.” *Readings in computing*, edited by Charles Babbage and Mary Somerville, MIT Press, 1952, pp. 1–20.", Ref(mla, Chapter()));
        Assert.Equal("Levy, Steven. “Hackers.” *Newsweek*, 15 Mar. 1984, pp. 4–5.", Ref(mla, Magazine()));
        Assert.Equal("Kernighan, Brian, et al. “A portable compiler.” *Proceedings of the Systems Conference*, ACM, 1975, pp. 10–19.", Ref(mla, Conference()));
        // An organisation that is also the publisher is given only once, as the publisher.
        Assert.Equal("*World health statistics 2020*. World Health Organization, 2020, https://www.who.int/data.", Ref(mla, Report()));
        Assert.Equal("Doe, Jane. “Using fonts on the web.” *MDN Web Docs*, 2 May 2023, https://developer.mozilla.org/fonts. Accessed 3 Jan. 2024.", Ref(mla, WebPage()));
        Assert.Equal("“Style guide.” *Inkwell*, 2026, https://inkwell.example/style.", Ref(mla, WebDocument()));
        Assert.Equal("Inkwell Project. *Inkwell*. Muhammad Noman, 2026, https://inkwell.example.", Ref(mla, Software()));
        Assert.Equal("*Hidden figures*. 20th Century Fox, 2016.", Ref(mla, Film()));
        Assert.Equal("Babbage, Charles, editor. *Collected papers*. Murray, 1850.", Ref(mla, EditedBook()));

        var edited = Book();
        edited.Editors.Add(P("Somerville", "Mary"));
        edited.Edition = null;
        Assert.Equal("Lovelace, Ada Augusta, and Charles Babbage. *Notes on the analytical engine*. Edited by Mary Somerville, Taylor, 1843.", Ref(mla, edited));
    }

    [Fact]
    public void Mla_InText()
    {
        var engine = new CitationEngine(CitationStyle.Mla, new[] { Book(), SoloBook(), Conference(), Film(), Journal(), Chapter() });
        Assert.Equal("(Lovelace 12)", engine.Cite("Ada1", "12").PlainText);
        Assert.Equal("(Lovelace and Babbage)", engine.Cite("Lov43").PlainText);
        Assert.Equal("(Kernighan et al. 10–12)", engine.Cite("Ker75", "10-12").PlainText);
        Assert.Equal("(*Hidden figures*)", engine.Cite("Film").ToMarkup());
        Assert.Equal("(12)", engine.Cite(new[] { new CitationRef("Ada1", "12") { SuppressAuthor = true } }).PlainText);
        Assert.Equal("(Lovelace 12; Turing 433)", engine.Cite(new[] { new CitationRef("Ada1", "12"), new CitationRef("Tur50", "433") }).PlainText);
    }

    [Fact]
    public void Mla_SameAuthor_ShortTitleAndThreeHyphens()
    {
        var a = SoloBook();
        a.Doi = null;
        var b = new BibSource { Tag = "Ada2", Type = "Book", Authors = { P("Lovelace", "Ada") }, Title = "Notes on the engine: A commentary", Year = "1845", Publisher = "Taylor" };
        var engine = new CitationEngine(CitationStyle.Mla, new[] { a, b });
        Assert.Equal("(Lovelace, *Notes on the engine* 12)", engine.Cite("Ada2", "12").ToMarkup());
        var list = engine.Bibliography();
        Assert.Equal("Lovelace, Ada. *Notes on the engine: A commentary*. Taylor, 1845.", list[0].Text.ToMarkup());
        Assert.Equal("---. *Sketch of the analytical engine*. Richard Taylor, 1842.", list[1].Text.ToMarkup());
    }

    // ---- Chicago 17 author-date ----

    [Fact]
    public void Chicago_References()
    {
        const CitationStyle chi = CitationStyle.Chicago;
        Assert.Equal("Lovelace, Ada Augusta, and Charles Babbage. 1843. *Notes on the analytical engine*. 2nd ed. London: Taylor.", Ref(chi, Book()));
        Assert.Equal("Lovelace, Ada. 1842. *Sketch of the analytical engine*. Richard Taylor. https://doi.org/10.1000/xyz123.", Ref(chi, SoloBook()));
        Assert.Equal("Turing, Alan Mathison. 1950. “Computing machinery and intelligence.” *Mind* 59 (236): 433–460. https://doi.org/10.1093/mind/LIX.236.433.", Ref(chi, Journal()));
        Assert.Equal("Hopper, Grace. 1952. “The education of a computer.” In *Readings in computing*, edited by Charles Babbage and Mary Somerville, 1–20. Cambridge, MA: MIT Press.", Ref(chi, Chapter()));
        Assert.Equal("Levy, Steven. 1984. “Hackers.” *Newsweek*, March 15, 1984, 4–5.", Ref(chi, Magazine()));
        Assert.Equal("Kernighan, Brian, Dennis Ritchie, and Ken Thompson. 1975. “A portable compiler.” In *Proceedings of the Systems Conference*, 10–19. New York: ACM.", Ref(chi, Conference()));
        Assert.Equal("World Health Organization. 2020. *World health statistics 2020*. Geneva: World Health Organization. https://www.who.int/data.", Ref(chi, Report()));
        Assert.Equal("Doe, Jane. 2023. “Using fonts on the web.” MDN Web Docs. May 2, 2023. Accessed January 3, 2024. https://developer.mozilla.org/fonts.", Ref(chi, WebPage()));
        Assert.Equal("“Style guide.” 2026. Inkwell. https://inkwell.example/style.", Ref(chi, WebDocument()));
        Assert.Equal("Inkwell Project. 2026. *Inkwell*. Computer software. Muhammad Noman. https://inkwell.example.", Ref(chi, Software()));
        Assert.Equal("*Hidden figures*. 2016. 20th Century Fox.", Ref(chi, Film()));
        Assert.Equal("Babbage, Charles, ed. 1850. *Collected papers*. Murray.", Ref(chi, EditedBook()));

        var undated = Film();
        undated.Year = null;
        Assert.Equal("*Hidden figures*. n.d. 20th Century Fox.", Ref(chi, undated));
    }

    [Fact]
    public void Chicago_InText()
    {
        var four = Conference();
        four.Tag = "Four";
        four.Authors.Add(P("Pike", "Rob"));
        var engine = new CitationEngine(CitationStyle.Chicago, new[] { Book(), SoloBook(), Conference(), four, Film() });
        Assert.Equal("(Lovelace 1842, 12)", engine.Cite("Ada1", "12").PlainText);
        Assert.Equal("(Lovelace and Babbage 1843)", engine.Cite("Lov43").PlainText);
        Assert.Equal("(Kernighan, Ritchie, and Thompson 1975)", engine.Cite("Ker75").PlainText);
        Assert.Equal("(Kernighan et al. 1975, 10–12)", engine.Cite("Four", "10-12").PlainText);
        Assert.Equal("(1842, 12)", engine.Cite(new[] { new CitationRef("Ada1", "12") { SuppressAuthor = true } }).PlainText);
        Assert.Equal("(Lovelace, 12)", engine.Cite(new[] { new CitationRef("Ada1", "12") { SuppressYear = true } }).PlainText);
        Assert.Equal("(*Hidden figures* 2016)", engine.Cite("Film").ToMarkup());
        Assert.Equal("(Lovelace 1842; Lovelace and Babbage 1843)", engine.Cite(new[] { new CitationRef("Ada1"), new CitationRef("Lov43") }).PlainText);
    }

    // ---- IEEE ----

    [Fact]
    public void Ieee_References()
    {
        const CitationStyle ieee = CitationStyle.Ieee;
        Assert.Equal("A. A. Lovelace and C. Babbage, *Notes on the analytical engine*, 2nd ed. London: Taylor, 1843.", Ref(ieee, Book()));
        Assert.Equal("A. Lovelace, *Sketch of the analytical engine*. Richard Taylor, 1842, doi: 10.1000/xyz123.", Ref(ieee, SoloBook()));
        Assert.Equal("A. M. Turing, “Computing machinery and intelligence,” *Mind*, vol. 59, no. 236, pp. 433–460, Oct. 1950, doi: 10.1093/mind/LIX.236.433.", Ref(ieee, Journal()));
        Assert.Equal("G. Hopper, “The education of a computer,” in *Readings in computing*, C. Babbage and M. Somerville, Eds. Cambridge, MA: MIT Press, 1952, pp. 1–20.", Ref(ieee, Chapter()));
        Assert.Equal("S. Levy, “Hackers,” *Newsweek*, pp. 4–5, Mar. 15, 1984.", Ref(ieee, Magazine()));
        Assert.Equal("B. Kernighan, D. Ritchie, and K. Thompson, “A portable compiler,” in *Proceedings of the Systems Conference*, New York, 1975, pp. 10–19.", Ref(ieee, Conference()));
        Assert.Equal("World Health Organization, “World health statistics 2020,” World Health Organization, Geneva, 2020. [Online]. Available: https://www.who.int/data", Ref(ieee, Report()));
        Assert.Equal("J. Doe. “Using fonts on the web.” MDN Web Docs. https://developer.mozilla.org/fonts (accessed Jan. 3, 2024).", Ref(ieee, WebPage()));
        Assert.Equal("“Style guide.” Inkwell. https://inkwell.example/style", Ref(ieee, WebDocument()));
        Assert.Equal("Inkwell Project, *Inkwell* [Computer software]. Muhammad Noman, 2026. [Online]. Available: https://inkwell.example", Ref(ieee, Software()));
        Assert.Equal("*Hidden figures*. 20th Century Fox, 2016.", Ref(ieee, Film()));
        Assert.Equal("C. Babbage, Ed., *Collected papers*. Murray, 1850.", Ref(ieee, EditedBook()));

        var seven = Journal();
        seven.Authors.Clear();
        for (var i = 1; i <= 7; i++) seven.Authors.Add(P("Writer" + i, "Ann"));
        Assert.StartsWith("A. Writer1 et al., “Computing", Ref(ieee, seven));
        var report = Report();
        report.Url = null;
        report.Publisher = null;
        report.City = null;
        report.Year = null;
        Assert.Equal("World Health Organization, “World health statistics 2020.”", Ref(ieee, report));
    }

    [Fact]
    public void Ieee_NumbersFollowFirstCitation()
    {
        var sources = new[] { Book(), SoloBook(), Journal(), Film() };
        var engine = new CitationEngine(CitationStyle.Ieee, sources, new[] { "Tur50", "Lov43", "Tur50", "Ada1" });
        Assert.Equal(1, engine.NumberOf("Tur50"));
        Assert.Equal(2, engine.NumberOf("Lov43"));
        Assert.Equal(3, engine.NumberOf("ada1"));
        Assert.Equal(4, engine.NumberOf("Film")); // not cited: after the cited ones
        Assert.Equal("[2]", engine.Cite("Lov43").PlainText);
        Assert.Equal("[2, p. 12]", engine.Cite("Lov43", "12").PlainText);
        Assert.Equal("[2, pp. 12–14]", engine.Cite("Lov43", "12-14").PlainText);
        Assert.Equal("[1]–[3]", engine.Cite(new[] { new CitationRef("Ada1"), new CitationRef("Tur50"), new CitationRef("Lov43") }).PlainText);
        Assert.Equal("[1], [3]", engine.Cite(new[] { new CitationRef("Ada1"), new CitationRef("Tur50") }).PlainText);
        Assert.Equal("[Nope]", engine.Cite("Nope").PlainText);

        var list = engine.Bibliography();
        Assert.Equal(new[] { "Tur50", "Lov43", "Ada1", "Film" }, list.Select(e => e.Source.Tag));
        Assert.Equal("[1]", list[0].Label);
        Assert.Equal("[4]", list[3].Label);
    }

    // ---- field codes ----

    [Fact]
    public void CitationField_ParsesWordCodes()
    {
        var f = CitationField.Parse(" CITATION Lov43 \\l 1033 \\p 12 \\m Tur50 \\p 433-460 \\n ")!;
        Assert.Equal(1033, f.LanguageId);
        Assert.Equal(2, f.Sources.Count);
        Assert.Equal("Lov43", f.Sources[0].Tag);
        Assert.Equal("12", f.Sources[0].Pages);
        Assert.False(f.Sources[0].SuppressAuthor);
        Assert.Equal("Tur50", f.Sources[1].Tag);
        Assert.Equal("433-460", f.Sources[1].Pages);
        Assert.True(f.Sources[1].SuppressAuthor);

        var g = CitationField.Parse("CITATION Ada1 \\f \"see\" \\s \"emphasis added\" \\v 2 \\y \\t")!;
        Assert.Equal("see", g.Sources[0].Prefix);
        Assert.Equal("emphasis added", g.Sources[0].Suffix);
        Assert.Equal("2", g.Sources[0].Volume);
        Assert.True(g.Sources[0].SuppressYear);
        Assert.True(g.Sources[0].SuppressTitle);
        Assert.Null(g.LanguageId);

        Assert.Null(CitationField.Parse(" PAGE "));
        Assert.Null(CitationField.Parse(" CITATION "));
        Assert.Null(CitationField.Parse(null));
    }

    [Fact]
    public void CitationField_RoundTrips()
    {
        var f = new CitationField(
            new CitationRef("Lov43", "12-14") { Prefix = "see", Suffix = "for \"details\"", SuppressYear = true },
            new CitationRef("Tur50") { Volume = "2", SuppressAuthor = true, SuppressTitle = true }) { LanguageId = 1033 };
        var code = f.ToCode();
        Assert.Equal(" CITATION Lov43 \\l 1033 \\p 12-14 \\f \"see\" \\s \"for \\\"details\\\"\" \\y \\m Tur50 \\v 2 \\n \\t ", code);
        Assert.Equal(FieldKind.Citation, FieldCode.KindOf(code));
        var back = CitationField.Parse(code)!;
        Assert.Equal(1033, back.LanguageId);
        Assert.Equal("for \"details\"", back.Sources[0].Suffix);
        Assert.Equal("see", back.Sources[0].Prefix);
        Assert.True(back.Sources[0].SuppressYear);
        Assert.Equal("2", back.Sources[1].Volume);
        Assert.True(back.Sources[1].SuppressAuthor && back.Sources[1].SuppressTitle);
        Assert.Equal(code, back.ToCode());
    }

    [Fact]
    public void BibliographyField_Codes()
    {
        Assert.True(BibliographyField.IsBibliography(" BIBLIOGRAPHY \\l 1033 "));
        Assert.Equal(1033, BibliographyField.LanguageId(" BIBLIOGRAPHY \\l 1033 "));
        Assert.Null(BibliographyField.LanguageId(" BIBLIOGRAPHY "));
        Assert.False(BibliographyField.IsBibliography(" CITATION X "));
        Assert.Equal(" BIBLIOGRAPHY ", BibliographyField.ToCode());
        Assert.Equal(" BIBLIOGRAPHY \\l 1033 ", BibliographyField.ToCode(1033));
    }

    // ---- model helpers ----

    [Fact]
    public void Model_RunsAndParagraphs()
    {
        var engine = new CitationEngine(CitationStyle.Apa, new[] { Journal() });
        var p = CitationModel.EntryParagraph(engine.Bibliography()[0]);
        Assert.Equal("Bibliography", p.StyleId);
        var runs = p.Inlines.OfType<TextRun>().ToList();
        Assert.Equal("Turing, A. M. (1950). Computing machinery and intelligence. ", runs[0].Text);
        Assert.Null(runs[0].Props.Italic);
        Assert.Equal("Mind", runs[1].Text);
        Assert.True(runs[1].Props.Italic);
        Assert.Equal("59", runs[3].Text);
        Assert.True(runs[3].Props.Italic);

        var ieee = new CitationEngine(CitationStyle.Ieee, new[] { Journal() }, new[] { "Tur50" });
        var q = CitationModel.EntryParagraph(ieee.Bibliography()[0]);
        Assert.StartsWith("[1]\tA. M. Turing, “Computing", q.PlainText());

        var empty = CitationModel.BibliographyParagraphs(Array.Empty<BibliographyEntry>());
        Assert.Single(empty);
        Assert.Equal(CitationEngine.NoSourcesText, empty[0].PlainText());
    }

    [Fact]
    public void Model_DocumentCitationsAndUpdate()
    {
        var doc = new Document();
        doc.Sources.StyleName = "IEEE";
        doc.Sources.Items.AddRange(new[] { Book(), Journal() });
        var engine = CitationEngine.ForDocument(doc);
        var first = CitationModel.CitationInline(engine, new CitationField(new CitationRef("Tur50")) { LanguageId = 1033 });
        var second = CitationModel.CitationInline(engine, new CitationField(new CitationRef("Lov43", "5")));
        var p = new Paragraph("As shown ");
        p.Inlines.Add(first);
        p.Inlines.Add(new TextRun(" and "));
        p.Inlines.Add(second);
        doc.Body.Blocks.Add(p);
        Assert.Equal(new[] { "Tur50", "Lov43" }, CitationEngine.CitedTags(doc));

        engine = CitationEngine.ForDocument(doc);
        Assert.True(CitationModel.UpdateResult(first, engine));
        Assert.True(CitationModel.UpdateResult(second, engine));
        Assert.Equal("As shown [1] and [2, p. 5]", p.PlainText());

        // Switching the style re-formats the same fields.
        doc.Sources.StyleName = "APA";
        engine = CitationEngine.ForDocument(doc);
        CitationModel.UpdateResult(first, engine);
        CitationModel.UpdateResult(second, engine);
        Assert.Equal("As shown (Turing, 1950) and (Lovelace & Babbage, 1843, p. 5)", p.PlainText());

        var bib = CitationModel.BibliographyParagraphs(doc);
        Assert.Equal(2, bib.Count);
        Assert.StartsWith("Lovelace, A. A., & Babbage, C. (1843).", bib[0].PlainText());

        Assert.False(CitationModel.UpdateResult(new FieldInline { Code = " PAGE " }, engine));
    }

    [Fact]
    public void Text_Helpers()
    {
        Assert.Equal(CitationStyle.Mla, CitationStyles.FromName("\\MLASeventhEditionOfficeOnline.xsl"));
        Assert.Equal(CitationStyle.Ieee, CitationStyles.FromName("IEEE"));
        Assert.Equal(CitationStyle.Apa, CitationStyles.FromName(null));
        Assert.Equal("Chicago", CitationStyles.ToName(CitationStyle.Chicago));
        Assert.Equal("Works Cited", CitationStyles.BibliographyTitle(CitationStyle.Mla));

        // Initials: hyphenated given names and initials typed with a period.
        var s = new BibSource { Tag = "X", Authors = { P("Sartre", "Jean-Paul"), P("King", "Martin", "L.") }, Title = "T", Year = "1960" };
        Assert.StartsWith("Sartre, J.-P., & King, M. L. (1960).", Ref(CitationStyle.Apa, s));
        // A title ending with a question mark doesn't take another period.
        var q = new BibSource { Tag = "Q", Type = "Book", Authors = { P("Doe", "Jo") }, Title = "Why write?", Year = "2001", Publisher = "Pub" };
        Assert.Equal("Doe, J. (2001). *Why write?* Pub.", Ref(CitationStyle.Apa, q));
        Assert.Equal("Doe, Jo. *Why write?* Pub, 2001.", Ref(CitationStyle.Mla, q));
        var article = new BibSource { Tag = "Q2", Type = "JournalArticle", Authors = { P("Doe", "Jo") }, Title = "Why write?", JournalName = "J", Year = "2001" };
        Assert.Equal("Doe, Jo. “Why write?” *J*, 2001.", Ref(CitationStyle.Mla, article));

        var styled = new StyledText("a").AppendItalic("b").AppendItalic("c").Append("d");
        Assert.Equal(3, styled.Count);
        Assert.Equal("a*bc*d", styled.ToMarkup());
    }
}
