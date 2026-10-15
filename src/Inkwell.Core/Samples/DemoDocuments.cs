using Inkwell.Core.Citations;
using Inkwell.Core.Editing;
using Inkwell.Core.Layout;
using Inkwell.Core.Model;
using Inkwell.Core.Templates;

namespace Inkwell.Core.Samples;

/// <summary>A demo document: id (file name), title, and the code that builds it.</summary>
public sealed class DemoDocument
{
    public DemoDocument(string id, string name, Func<Document> create)
    {
        Id = id;
        Name = name;
        Create = create;
    }

    public string Id { get; }
    public string Name { get; }
    public Func<Document> Create { get; }
}

/// <summary>
/// The demo documents (README, screenshots, tests): a report with a table of contents, footnotes, a table and
/// pictures; a letter; a résumé; an essay with citations, comments and tracked changes; Urdu and English.
/// </summary>
public static class DemoDocuments
{
    public static IReadOnlyList<DemoDocument> All { get; } = new[]
    {
        new DemoDocument("report", "Annual report", Report),
        new DemoDocument("letter", "Letter", Letter),
        new DemoDocument("resume", "Résumé", Resume),
        new DemoDocument("essay", "Essay with citations", Essay),
        new DemoDocument("urdu-english", "Urdu and English", Bilingual),
    };

    // ------------------------------------------------------------------ report

    public static Document Report()
    {
        var b = new DocBuilder(DocumentDesigns.Default);
        b.Title("Harbour Light Cooperative");
        b.Subtitle("Annual report 2026 · A year of community energy");
        b.P("Prepared by the board for members · 14 March 2027", "Subtitle");
        var tocAt = b.P("");
        b.PageBreak();

        b.Heading(1, "Summary");
        b.P("Harbour Light finished its fourth year with **1,284 members**, two new solar roofs and the first battery store owned by a neighbourhood in the county. Members saved an average of 23 % on their electricity bills, and the cooperative ended the year with a small surplus that goes back into the repair fund.");
        b.P("This report explains what we built, what it cost, what worked and what didn't. Figures are for the calendar year unless stated otherwise; the full accounts are available to every member on request.");

        b.Heading(1, "Background");
        var bg = b.P("The cooperative was founded in 2022 by thirty households who wanted cheaper, cleaner power and a say in how it is produced. Membership costs one share, and every member has one vote, whatever the size of their roof.");
        b.Note(bg, "One share costs €50 and can be paid back when a member leaves; it does not earn interest.");
        b.P("Our first project, the school roof, has produced electricity every day since it was switched on. The second and third projects followed in 2025. This year was about storage: keeping the sun's energy for the evening, when the street needs it most.");
        b.Heading(2, "How decisions are made");
        b.Bullets("The general meeting elects the board and approves the budget", "The board meets monthly; minutes are published within a week", "Any ten members can put a proposal on the agenda");

        b.Heading(1, "What we built");
        b.P("Two roofs were added this year: the sports hall on Quay Street and the library annexe. Together they add 148 kilowatts of panels. The battery store next to the community centre holds 420 kilowatt-hours, enough to cover the street lights and the centre through most winter evenings.");
        var chart = b.P("");
        chart.Props.Justification = "center";
        chart.Props.KeepNext = true;
        chart.Props.SpacingLine = 240;
        chart.Props.SpacingLineRule = "auto";
        b.Picture(chart, SampleArt.BarChart(), 5.6, null, "Bar chart: electricity produced per quarter, rising from 38 to 81 megawatt-hours over two years.");
        var figCaption = b.P("");
        figCaption.Props.StyleId = "Caption";
        var figure = AddCaption(b.Doc, figCaption, "Figure", "Electricity produced per quarter, in megawatt-hours. The darker bars are 2026.");
        b.P("Production grew in every quarter but one. The dip in the third quarter came from a week of maintenance on the school roof, planned for the summer holidays.");

        b.Heading(1, "Results");
        var resultsIntro = b.P("The table below compares the year with the one before. ");
        b.Table(new List<string[]>
        {
            new[] { "Measure", "2025", "2026", "Change" },
            new[] { "Members", "1,032", "1,284", "+24 %" },
            new[] { "Electricity produced", "212 MWh", "263 MWh", "+24 %" },
            new[] { "Used locally", "61 %", "78 %", "+17 points" },
            new[] { "Average bill saving", "18 %", "23 %", "+5 points" },
            new[] { "Volunteer hours", "2,140", "2,960", "+38 %" },
            new[] { "Surplus", "€4,200", "€11,900", "+183 %" },
        }, header: true, style: "InkwellTable", columnWidths: new[] { 3600, 1700, 1700, 2000 });
        var tableCaption = b.P("");
        tableCaption.Props.StyleId = "Caption";
        var table = AddCaption(b.Doc, tableCaption, "Table", "The year in numbers.");
        resultsIntro.Inlines.Add(new TextRun("The biggest change is in how much of our electricity is used right here, thanks to the battery ("));
        resultsIntro.Inlines.Add(CrossReference(table, "Table 1"));
        resultsIntro.Inlines.Add(new TextRun(")."));
        var used = b.P("Using our own electricity locally matters more than producing more of it: every kilowatt-hour used on the street is worth about three times what the grid pays us for it.");
        b.Note(used, "Based on the 2026 average retail price and the export tariff in our contract with the grid operator.");

        b.Heading(1, "The neighbourhood");
        var wrapped = b.P("The battery changed the way the street works. On summer evenings the community centre runs on stored sunshine until well after dark, and the café next door has started opening late on Fridays. Several members told us they now plan laundry and cooking for the sunny part of the day, because the app shows when the panels are producing more than the street needs.");
        var landscape = b.FloatingPicture(wrapped, SampleArt.Landscape(), 2.6, 3.7, 0.05, WrapMode.Square, "A calm landscape with hills, a lake and the evening sun.");
        // Beside its paragraph (it moves with it), not at a fixed place on the page.
        landscape.Float!.Vertical = new FloatPosition { RelativeFrom = "paragraph", Offset = 45720 };
        b.P("We also ran six open days, a repair café and a course for teenagers who want to become electricians. The course was full within a week; we will run it twice next year.");
        b.P("Not everything worked. The car-sharing trial ended early because too few members needed a car at the same time of day, and the first version of the app confused more people than it helped. Both lessons are reflected in next year's plan.");
        b.Quote("We used to talk about energy as something that arrives through a cable. Now we talk about it like the weather: something we share, watch and plan around.");

        b.Heading(1, "Finances");
        var fin = b.P("Income came from three sources, in this order of size:");
        b.Numbered(true, "Electricity sold to members at the cooperative rate", "Electricity exported to the grid at the contract tariff", "A one-off grant for the battery store");
        var surplus = b.P("Costs were close to the budget. Insurance rose more than expected and the battery's installation took two weeks longer than planned, which we paid for from the contingency line. The surplus of €11,900 goes to the repair fund, as the general meeting decided.");
        b.Note(surplus, "The repair fund now holds €38,400, enough to replace the inverters on all four roofs once.");
        _ = fin;

        b.Heading(1, "Next year");
        b.Heading(2, "Plans");
        b.Bullets("A fifth roof: the bus depot, if the city agrees to a twenty-year lease", "A simpler app, designed with the members who found the first one hard", "Two more teenager courses and a mentoring programme");
        b.Heading(2, "How you can help");
        b.P("Come to the general meeting on 18 April, bring a neighbour, and tell us what you think. The agenda and the full accounts are on the members' page.");

        // Header and footer: the title page has neither; the rest show the report's name and page numbers.
        b.HeaderFooter(true, "default", new Paragraph("Harbour Light Cooperative · Annual report 2026"));
        b.HeaderFooter(false, "default", DocBuilder.PageNumberParagraph("Page ", "", "center", withTotal: true));
        b.HeaderFooter(true, "first", new Paragraph(""));
        b.HeaderFooter(false, "first", new Paragraph(""));
        b.Doc.Properties.Title = "Harbour Light Cooperative: Annual report 2026";
        b.Doc.Properties.Creator = "Harbour Light board";
        _ = figure;

        // The table of contents and the fields, through the editor (as a user would insert them).
        var editor = new DocEditor(b.Doc) { Author = "Harbour Light board" };
        editor.Selection = new Selection(new DocPos(tocAt, 0));
        editor.InsertTableOfContents();
        RefreshFields(editor);
        editor.History.Clear();
        return b.Doc;
    }

    /// <summary>"Figure 1: text" with a SEQ field and a bookmark (like Insert › Caption).</summary>
    private static Paragraph AddCaption(Document doc, Paragraph caption, string label, string text)
    {
        var id = doc.NextBookmarkId();
        var name = "_Ref" + (100000 + id * 7919).ToString(System.Globalization.CultureInfo.InvariantCulture);
        caption.Inlines.Add(new BookmarkMarker { Id = id, Name = name, IsStart = true });
        caption.Inlines.Add(new TextRun(label + " "));
        caption.Inlines.Add(new FieldInline { Code = $" SEQ {label} \\* ARABIC ", Result = { new TextRun("1") } });
        caption.Inlines.Add(new BookmarkMarker { Id = id, IsStart = false });
        caption.Inlines.Add(new TextRun(". " + text));
        return caption;
    }

    private static FieldInline CrossReference(Paragraph caption, string text)
    {
        var name = caption.Inlines.OfType<BookmarkMarker>().First(m => m.IsStart).Name!;
        return new FieldInline { Code = $" REF {name} \\h ", Result = { new TextRun(text) } };
    }

    /// <summary>Lays the document out and updates its fields (table of contents page numbers, captions, references).</summary>
    public static void RefreshFields(DocEditor editor)
    {
        using var layout = new DocumentLayout(editor.Doc, new ViewOptions());
        layout.LayoutAll();
        editor.UpdateFields(layout);
        // Page numbers in the table of contents may shift once it has its entries: once more.
        layout.LayoutAll();
        editor.UpdateFields(layout);
    }

    // ------------------------------------------------------------------ letter

    public static Document Letter()
    {
        var b = new DocBuilder(DocumentDesigns.Find("classic"));
        var name = b.P("Mariam Qureshi", "Title");
        name.Props.SpacingAfter = 40;
        b.P("12 Fenwick Lane · Bristol BS6 5QA · mariam.qureshi@example.org · 0117 496 0321", "Subtitle");
        b.P("14 March 2027");
        foreach (var line in new[] { "Ms Helen Ashworth", "Head of Collections", "The City Archive", "Prince Street, Bristol BS1 4QA" })
        {
            var p = b.P(line);
            p.Props.SpacingAfter = 0;
        }
        b.P("");
        b.P("**Re: Access to the harbour master's logbooks, 1890–1910**");
        b.P("Dear Ms Ashworth,");
        b.P("I am writing to ask for access to the harbour master's logbooks for 1890 to 1910, which your catalogue lists as held in the reading room store. I am researching how the port handled the arrival of steam ships, for a book to be published next year by a local history press.");
        b.P("I would need to consult the volumes over about five visits in April and May. I am happy to work with whatever handling rules you set, and I can bring a letter of introduction from the press if that helps.");
        b.P("If some volumes are too fragile to be handled, could you tell me whether digitised copies exist, or whether the archive could photograph selected pages for a fee?");
        b.P("Thank you for your time. I look forward to hearing from you.");
        b.P("Yours sincerely,");
        b.P("");
        b.P("Mariam Qureshi");
        b.Doc.Properties.Title = "Letter to the City Archive";
        b.Doc.Properties.Creator = "Mariam Qureshi";
        return b.Doc;
    }

    // ------------------------------------------------------------------ résumé

    public static Document Resume()
    {
        var b = new DocBuilder(DocumentDesigns.Find("modern"));
        var name = b.Title("Daniel Okafor");
        b.FloatingPicture(name, SampleArt.Portrait(), 1.15, 5.35, 0.0, WrapMode.Square, "Portrait placeholder");
        b.Subtitle("Product designer · Leeds · daniel.okafor@example.com · danielokafor.example.com");
        b.Heading(2, "Profile");
        b.P("Product designer with eight years of experience making complicated services simple, from council tax forms to hospital booking. I like research, clear writing and working closely with engineers.");
        b.Heading(2, "Experience");
        b.P("**Lead product designer** · Northgate Health · 2023 – present");
        b.Bullets("Redesigned outpatient booking; missed appointments fell by 18 %", "Built the design system now used by five product teams", "Mentor to four designers; run the monthly design critique");
        b.P("**Senior designer** · Civic Forms Ltd · 2019 – 2023");
        b.Bullets("Led the move of 40 paper forms to accessible online services", "Ran usability studies with more than 200 residents", "Cut average completion time for the benefits form from 31 to 12 minutes");
        b.P("**Designer** · Studio Haverly · 2016 – 2019");
        b.Bullets("Websites and apps for charities and small businesses", "First design hire; set up the studio's research practice");
        b.Heading(2, "Skills");
        var skills = b.Table(new List<string[]>
        {
            new[] { "Research", "Interviews, usability testing, diary studies" },
            new[] { "Design", "Prototyping, interaction design, typography" },
            new[] { "Accessibility", "WCAG 2.2, screen reader testing, plain language" },
            new[] { "Tools", "Figma, HTML and CSS, basic JavaScript" },
        }, header: false, style: "TableNormal", columnWidths: new[] { 2200, 6800 });
        foreach (var row in skills.Rows)
            foreach (var p in DocumentWalker.Paragraphs(row.Cells[0].Blocks))
                foreach (var r in p.Inlines)
                    r.Props.Bold = true;
        b.Heading(2, "Education");
        b.P("**BA (Hons) Graphic Communication** · Leeds Arts University · 2016");
        b.Heading(2, "Languages");
        b.P("English (native) · Yoruba (fluent) · French (conversational)");
        b.Doc.Properties.Title = "Daniel Okafor: CV";
        b.Doc.Properties.Creator = "Daniel Okafor";
        return b.Doc;
    }

    // ------------------------------------------------------------------ essay

    public static Document Essay()
    {
        var b = new DocBuilder(DocumentDesigns.Find("classic"));
        b.Title("Why Cities Forget Their Rivers");
        b.Subtitle("Samira Haddad · Urban History 204 · Dr Amina Siddiqui · March 2027");
        b.Heading(1, "Introduction");
        var intro = b.P("Most old cities were founded on a river, and most of them spent the twentieth century hiding it. Rivers were straightened, culverted and built over until, in many places, people walking above them no longer knew they were there");
        var intro2 = b.P("This essay argues that cities forgot their rivers not out of carelessness but because forgetting was useful: a buried river made land valuable, removed a smell and a danger, and suited a way of planning that saw water as a technical problem.");
        b.Heading(1, "Burying the river");
        var bury = b.P("In London, the Fleet was covered in stages between 1730 and 1870 as it became an open sewer");
        var bury2 = b.P("Engineers of the period described the work as an improvement to public health, and in a narrow sense it was. Yet the same decisions turned rivers into infrastructure that only specialists understood.");
        b.Heading(1, "Bringing them back");
        var back = b.P("Since the 1990s, cities from Seoul to Sheffield have opened up buried streams again. The best-known case, Cheonggyecheon in Seoul, replaced an elevated motorway with a public stream in 2005");
        var back2 = b.P("Daylighting projects are expensive, but they are rarely justified by flood control alone; they are justified by what people do near water once they can see it");
        b.Heading(1, "Conclusion");
        b.P("Remembering a river is a political act. It means deciding that the slow, visible city is worth the land and the money that the fast, hidden one saved.");
        var refsAt = b.P("");
        b.Doc.Properties.Title = "Why Cities Forget Their Rivers";
        b.Doc.Properties.Creator = "Samira Haddad";

        var doc = b.Doc;
        doc.Sources.StyleName = "APA";
        doc.Sources.Items.AddRange(new[]
        {
            Source("Bar14", "Book", "Barton, Nicholas", "The lost rivers of London", "2014", publisher: "Historical Publications"),
            Source("Kim19", "JournalArticle", "Kim, Ji-won; Lee, Sun-hee", "Restoring Cheonggyecheon: Fifteen years of a daylighted stream", "2019", journal: "Journal of Urban Design", volume: "24", issue: "3", pages: "412-431"),
            Source("Pin18", "Book", "Pinder, David", "Visions of the city: Utopianism, power and politics in twentieth-century urbanism", "2018", publisher: "Edinburgh University Press"),
            Source("Wil21", "Report", "Wild, Tom", "Daylighting rivers: Lessons from UK projects", "2021", publisher: "Sheffield Water Centre"),
        });
        var editor = new DocEditor(doc) { Author = "Samira Haddad" };
        void CiteAt(Paragraph p, string tag, string? pages = null)
        {
            editor.Selection = new Selection(new DocPos(p, p.Length));
            editor.InsertText(" ");
            editor.Selection = new Selection(new DocPos(p, p.Length));
            editor.InsertCitation(new[] { new CitationRef(tag, pages) });
            editor.Selection = new Selection(new DocPos(p, p.Length));
            editor.InsertText(".");
        }
        TrimEnd(intro);
        CiteAt(intro, "Pin18");
        CiteAt(bury, "Bar14", "45-52");
        CiteAt(back, "Kim19");
        CiteAt(back2, "Wil21");
        _ = intro2;
        _ = bury2;
        editor.Selection = new Selection(new DocPos(refsAt, 0));
        editor.InsertBibliography();

        // Comments, with a reply.
        var thesis = intro2;
        editor.Selection = new Selection(new DocPos(thesis, 0), new DocPos(thesis, 52));
        editor.Author = "Dr Amina Siddiqui";
        var c1 = editor.AddComment("Strong thesis. Can you name the planners who argued this explicitly?");
        editor.Author = "Samira Haddad";
        editor.AddComment("Good idea: I'll add Abercrombie's 1944 plan in the second section.", c1);
        editor.Author = "Dr Amina Siddiqui";
        editor.Selection = new Selection(new DocPos(back2, 0), new DocPos(back2, 22));
        editor.AddComment("Give one cost figure here; the reader will want a sense of scale.");

        // Tracked changes by the tutor.
        editor.SetTrackChanges(true);
        var at = bury2.Text.IndexOf("narrow", StringComparison.Ordinal);
        if (at >= 0)
        {
            editor.Selection = new Selection(new DocPos(bury2, at), new DocPos(bury2, at + "narrow".Length));
            editor.InsertText("limited");
        }
        var cut = intro.Text.IndexOf("Most old", StringComparison.Ordinal);
        if (cut >= 0)
        {
            editor.Selection = new Selection(new DocPos(intro, cut), new DocPos(intro, cut + "Most old".Length));
            editor.InsertText("Nearly all old");
        }
        var add = back.Text.IndexOf(" replaced", StringComparison.Ordinal);
        if (add >= 0)
        {
            editor.Selection = new Selection(new DocPos(back, add));
            editor.InsertText(", famously,");
        }
        editor.Author = "Samira Haddad";
        editor.SetTrackChanges(false);
        editor.History.Clear();
        return doc;
    }

    private static void TrimEnd(Paragraph p)
    {
        if (p.Inlines.LastOrDefault() is TextRun r) r.Text = r.Text.TrimEnd();
    }

    private static BibSource Source(string tag, string type, string authors, string title, string year, string? publisher = null, string? journal = null, string? volume = null, string? issue = null, string? pages = null)
    {
        var s = new BibSource
        {
            Tag = tag,
            Type = type,
            Title = title,
            Year = year,
            Publisher = publisher,
            JournalName = journal,
            Volume = volume,
            Issue = issue,
            Pages = pages,
            Guid = "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}",
        };
        foreach (var a in authors.Split(';')) s.Authors.Add(SourcePerson.Parse(a.Trim()));
        return s;
    }

    // ------------------------------------------------------------------ Urdu and English

    public static Document Bilingual()
    {
        var b = new DocBuilder(DocumentDesigns.Default);
        b.Title("Bilingual notes");
        Urdu(b, "دو لسانی نوٹس", "Subtitle");
        b.P("Inkwell writes Urdu, Arabic and English side by side. Type Urdu letters into an empty paragraph and it turns right-to-left by itself; the bundled Noto Nastaliq Urdu font gives Urdu its traditional calligraphic shape.");
        b.Heading(1, "Urdu");
        Urdu(b, "اردو پاکستان کی قومی زبان ہے اور دنیا بھر میں کروڑوں لوگ اسے بولتے اور سمجھتے ہیں۔ نستعلیق خط میں حروف ایک دوسرے سے جڑ کر خوبصورت انداز میں لکھے جاتے ہیں۔");
        Urdu(b, "Inkwell میں اردو لکھنا آسان ہے: جیسے ہی آپ اردو حروف لکھنا شروع کرتے ہیں، پیراگراف خود بخود دائیں سے بائیں ہو جاتا ہے۔");
        var list = b.NewList(true);
        foreach (var item in new[] { "خود بخود دائیں سے بائیں سمت", "نستعلیق فونٹ ساتھ شامل ہے", "انگریزی اور اردو ایک ہی سطر میں" })
        {
            var p = Urdu(b, item, "ListParagraph");
            p.Props.SetNumbering(list, 0);
        }
        b.Heading(1, "Arabic");
        var arabic = b.P("");
        arabic.Props.Bidi = true;
        AddScriptRuns(arabic, "مرحبًا بالعالم. هذه فقرة باللغة العربية بخط النسخ.", "ar-SA", "Noto Naskh Arabic", 28);
        b.Heading(1, "Side by side");
        var table = b.Table(new List<string[]>
        {
            new[] { "English", "اردو" },
            new[] { "Book", "کتاب" },
            new[] { "Water", "پانی" },
            new[] { "Friend", "دوست" },
            new[] { "Thank you", "شکریہ" },
        }, header: true, style: "InkwellTable", columnWidths: new[] { 4500, 4500 });
        foreach (var row in table.Rows)
        {
            var p = Nav.FirstIn(row.Cells[1].Blocks)!;
            var text = p.Text;
            p.Inlines.Clear();
            p.Props.Bidi = true;
            AddScriptRuns(p, text, "ur-PK", "Noto Nastaliq Urdu", 26);
        }
        b.P("Mixed lines work too: the word “Inkwell” keeps its left-to-right order inside an Urdu sentence, and numbers such as 2027 read the right way round.");
        b.Doc.Properties.Title = "Bilingual notes";
        return b.Doc;
    }

    /// <summary>A right-to-left Urdu paragraph (Latin words in it stay left-to-right runs).</summary>
    private static Paragraph Urdu(DocBuilder b, string text, string? style = null)
    {
        var p = b.P("", style);
        p.Props.Bidi = true;
        AddScriptRuns(p, text, "ur-PK", "Noto Nastaliq Urdu", 26);
        return p;
    }

    /// <summary>Splits text into right-to-left runs (language, font) and left-to-right runs for Latin words.</summary>
    private static void AddScriptRuns(Paragraph p, string text, string language, string font, int halfPoints)
    {
        var i = 0;
        while (i < text.Length)
        {
            var latin = IsLatin(text[i]);
            var j = i + 1;
            while (j < text.Length && (IsLatin(text[j]) == latin || (!char.IsLetter(text[j]) && !IsLatin(text[j]) && latin == IsLatin(text[j - 1])))) j++;
            var run = new TextRun(text.Substring(i, j - i));
            if (!latin)
            {
                run.Props.Rtl = true;
                run.Props.LangBidi = language;
                run.Props.SetComplexFont(font);
                run.Props.SizeCs = halfPoints;
            }
            p.Inlines.Add(run);
            i = j;
        }
        p.Normalize();
    }

    private static bool IsLatin(char c) => c < 0x0590 && (char.IsLetter(c) || char.IsDigit(c));
}
