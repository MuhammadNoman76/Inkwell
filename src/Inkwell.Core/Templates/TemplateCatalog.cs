using Inkwell.Core.Model;

namespace Inkwell.Core.Templates;

/// <summary>A starting point on the start screen: a design and some starter content.</summary>
public sealed class DocumentTemplate
{
    public DocumentTemplate(string id, string name, string description, string designId, Func<DocumentDesign, bool, Document> create)
    {
        Id = id;
        Name = name;
        Description = description;
        DesignId = designId;
        Create = create;
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public string DesignId { get; }

    /// <summary>Builds the document (design, US Letter paper?).</summary>
    public Func<DocumentDesign, bool, Document> Create { get; }

    public Document Build(bool letter, DocumentDesign? design = null) => Create(design ?? DocumentDesigns.Find(DesignId), letter);
}

/// <summary>The templates offered on the start screen. Content is neutral sample text to replace.</summary>
public static class TemplateCatalog
{
    public static IReadOnlyList<DocumentTemplate> All { get; } = new[]
    {
        new DocumentTemplate("blank", "Blank document", "A clean page in your default design", "inkwell", Blank),
        new DocumentTemplate("report", "Report", "Title page feel, headings, a table and notes", "inkwell", Report),
        new DocumentTemplate("letter", "Letter", "A formal letter with your details", "classic", Letter),
        new DocumentTemplate("resume", "Résumé", "A crisp one-page CV", "modern", Resume),
        new DocumentTemplate("essay", "Essay", "Academic essay with citations and references", "classic", Essay),
        new DocumentTemplate("proposal", "Proposal", "Problem, approach, timeline and budget", "technical", Proposal),
        new DocumentTemplate("meeting", "Meeting notes", "Agenda, decisions and action items", "modern", Meeting),
        new DocumentTemplate("newsletter", "Newsletter", "Two columns of stories", "editorial", Newsletter),
    };

    public static DocumentTemplate? Find(string id) => All.FirstOrDefault(t => t.Id == id);

    /// <summary>US Letter for the United States, Canada and a few others; A4 elsewhere.</summary>
    public static bool RegionUsesLetter()
    {
        try
        {
            var region = System.Globalization.RegionInfo.CurrentRegion.TwoLetterISORegionName;
            return region is "US" or "CA" or "MX" or "PH" or "CL" or "CO" or "VE" or "PR" or "GT" or "CR";
        }
        catch
        {
            return false;
        }
    }

    private static Document Blank(DocumentDesign design, bool letter)
    {
        var b = new DocBuilder(design, letter);
        b.P("");
        return b.Doc;
    }

    private static Document Report(DocumentDesign design, bool letter)
    {
        var b = new DocBuilder(design, letter);
        b.Title("Report title");
        b.Subtitle("A one-line summary of what this report covers");
        b.P("Prepared by Your Name · " + DateTime.Now.ToString("d MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture), "Subtitle");
        b.Heading(1, "Summary");
        b.P("Start with the conclusion. Two or three sentences that tell a busy reader what you found and what you recommend. Everything after this paragraph supports it.");
        b.Heading(1, "Background");
        var p = b.P("Explain why this report exists and what question it answers. Keep paragraphs short; Inkwell adds the space between them for you, so there's no need for empty lines.");
        b.Note(p, "Notes like this one sit at the bottom of the page and renumber themselves.");
        b.Heading(2, "Key findings");
        b.Bullets("The first finding, in one sentence", "The second finding, with a number that matters", "The third finding, and why it's important");
        b.Heading(2, "At a glance");
        b.Table(new List<string[]>
        {
            new[] { "Measure", "Before", "After" },
            new[] { "Response time", "4.2 days", "1.1 days" },
            new[] { "Satisfaction", "71 %", "88 %" },
            new[] { "Cost per case", "$46", "$31" },
        });
        b.Caption("Table 1. Replace these sample figures with your own.");
        b.Heading(1, "Recommendations");
        b.Numbered(true, "Do the most important thing first", "Then the next one", "Review the results in three months");
        b.Quote("A good report answers the question before the reader has to ask it.");
        return b.Doc;
    }

    private static Document Letter(DocumentDesign design, bool letter)
    {
        var b = new DocBuilder(design, letter);
        b.P("Your Name", "Title").Props.SpacingAfter = 40;
        b.P("Street address · City, Postcode · name@example.com · +1 555 0100", "Subtitle");
        b.P(DateTime.Now.ToString("d MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture));
        var to = b.P("Recipient Name");
        to.Props.SpacingAfter = 0;
        var org = b.P("Organisation");
        org.Props.SpacingAfter = 0;
        b.P("Street address, City, Postcode");
        b.P("Dear Recipient Name,");
        b.P("The first paragraph says why you are writing. Be direct: one or two sentences are enough to set the purpose of the letter.");
        b.P("The second paragraph gives the details. Facts, dates and what you need from the reader belong here, in plain words.");
        b.P("Close with the next step you expect and thank the reader for their time.");
        b.P("Kind regards,");
        b.P("");
        b.P("Your Name");
        return b.Doc;
    }

    private static Document Resume(DocumentDesign design, bool letter)
    {
        var b = new DocBuilder(design, letter);
        b.Title("Your Name");
        b.Subtitle("Product designer · City · name@example.com · portfolio.example.com");
        b.Heading(2, "Profile");
        b.P("A short paragraph about what you do best and the kind of work you're looking for. Three lines at most.");
        b.Heading(2, "Experience");
        b.P("**Senior Designer** · Company Name · 2022 – present");
        b.Bullets("Led a redesign that cut sign-up time in half", "Mentored four designers and ran weekly critiques", "Built the design system used by six teams");
        b.P("**Designer** · Earlier Company · 2018 – 2022");
        b.Bullets("Shipped the mobile app's first release", "Ran usability studies with 40 participants");
        b.Heading(2, "Education");
        b.P("**BA in Visual Communication** · University Name · 2018");
        b.Heading(2, "Skills");
        b.P("Research · Prototyping · Typography · Design systems · Accessibility · Workshop facilitation");
        return b.Doc;
    }

    private static Document Essay(DocumentDesign design, bool letter)
    {
        var b = new DocBuilder(design, letter);
        b.Title("The Title of Your Essay");
        b.Subtitle("Your Name · Course · Instructor · " + DateTime.Now.ToString("d MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture));
        b.Heading(1, "Introduction");
        b.P("Open with the question your essay answers and why it matters. End the introduction with your thesis: one clear sentence the rest of the essay defends.");
        b.Heading(1, "Argument");
        b.P("Each paragraph makes one point and supports it with evidence. Add a citation from Insert › Citation; Inkwell formats it in APA, MLA, Chicago or IEEE and builds the reference list for you.");
        b.P("Address the strongest counter-argument fairly, then explain why your position still holds.");
        b.Heading(1, "Conclusion");
        b.P("Restate the thesis in new words and say what follows from it. Don't introduce new evidence here.");
        b.Heading(1, "References");
        b.P("Your reference list appears here when you insert a bibliography.");
        return b.Doc;
    }

    private static Document Proposal(DocumentDesign design, bool letter)
    {
        var b = new DocBuilder(design, letter);
        b.Title("Project proposal");
        b.Subtitle("Name of the project · Prepared for Client · " + DateTime.Now.ToString("MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture));
        b.Heading(1, "The problem");
        b.P("Describe the problem in the client's words. What happens today, who it affects and what it costs them.");
        b.Heading(1, "Our approach");
        b.Numbered(true, "Discovery: interviews and a review of what exists", "Design: options, a recommendation and a prototype", "Delivery: build, test and hand over");
        b.Heading(1, "Timeline");
        b.Table(new List<string[]>
        {
            new[] { "Phase", "Weeks", "Outcome" },
            new[] { "Discovery", "1–2", "Findings report" },
            new[] { "Design", "3–5", "Prototype and plan" },
            new[] { "Delivery", "6–10", "Working solution" },
        });
        b.Heading(1, "Budget");
        b.P("State the total, what it includes and the payment schedule. Keep optional extras separate.");
        b.Heading(1, "Next steps");
        b.P("Say what you need from the client to start, and by when.");
        return b.Doc;
    }

    private static Document Meeting(DocumentDesign design, bool letter)
    {
        var b = new DocBuilder(design, letter);
        b.Title("Meeting notes");
        b.Subtitle(DateTime.Now.ToString("dddd d MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture) + " · Team meeting");
        b.P("**Present:** Name, Name, Name   **Notes by:** Name");
        b.Heading(2, "Agenda");
        b.Numbered(true, "Updates since last week", "Decisions needed", "Anything else");
        b.Heading(2, "Decisions");
        b.Bullets("Decision one, with who made it", "Decision two");
        b.Heading(2, "Action items");
        b.Table(new List<string[]>
        {
            new[] { "Action", "Owner", "Due" },
            new[] { "Send the summary to the team", "Name", "Friday" },
            new[] { "Book the review session", "Name", "Next week" },
        });
        return b.Doc;
    }

    private static Document Newsletter(DocumentDesign design, bool letter)
    {
        var b = new DocBuilder(design, letter);
        b.Title("The Neighbourhood Letter");
        b.Subtitle("Issue 12 · " + DateTime.Now.ToString("MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture));
        b.SectionBreak("continuous");
        b.Doc.Body.FinalSection.ColumnCount = 2;
        b.Doc.Body.FinalSection.ColumnSpace = 567;
        b.Heading(2, "The garden opens");
        b.P("After a long winter of planning, the community garden opens its gates this month. Twenty new beds are ready, and there's a waiting list for the rest. Volunteers meet every Saturday morning.");
        b.Heading(2, "A new library corner");
        b.P("The reading corner on the second floor has new shelves, better light and a children's section. Borrowing is free with any local address.");
        b.Quote("Good neighbours make good streets.");
        b.Heading(2, "Dates for your calendar");
        b.Bullets("Garden open day: first Saturday", "Library story hour: Wednesdays", "Street clean-up: last Sunday");
        b.Heading(2, "Thank you");
        b.P("This letter is written by volunteers. If you'd like to contribute a story, send it in before the twentieth of the month.");
        return b.Doc;
    }
}
