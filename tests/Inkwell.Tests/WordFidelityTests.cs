using System.Globalization;
using System.Text;
using Inkwell.Core.Editing;
using Inkwell.Core.IO;
using Inkwell.Core.Layout;
using Inkwell.Core.Model;
using Inkwell.Core.Samples;
using Inkwell.Core.Templates;
using Xunit;

namespace Inkwell.Tests;

/// <summary>
/// Material for the Microsoft Word comparison (tools\word-fidelity.ps1), which only runs when asked:
/// INKWELL_FIDELITY_DIR=folder writes the test documents and Inkwell's own counts (inkwell.csv) there;
/// after Word has opened them and saved copies (*.word.docx), the second test reads those copies back.
/// Without the variable both tests do nothing.
/// </summary>
public class WordFidelityTests
{
    private static string? Folder => Environment.GetEnvironmentVariable("INKWELL_FIDELITY_DIR");

    public static IEnumerable<(string Name, Func<Document> Create)> Documents()
    {
        foreach (var demo in DemoDocuments.All) yield return (demo.Id, demo.Create);
        yield return ("sections", Sections);
        yield return ("lists", Lists);
        yield return ("tables", Tables);
        yield return ("fields", Fields);
    }

    private static Document Sections()
    {
        var b = new DocBuilder(DocumentDesigns.Default);
        b.Title("Sections and pages");
        for (var i = 0; i < 25; i++) b.P("Portrait text " + i + ": a paragraph long enough to wrap across the line so the page fills up with ordinary prose.");
        b.HeaderFooter(true, "default", new Paragraph("Odd page header"));
        b.HeaderFooter(true, "even", new Paragraph("Even page header"));
        b.HeaderFooter(false, "default", DocBuilder.PageNumberParagraph("Page ", "", "center", withTotal: true));
        b.Doc.Settings.EvenAndOddHeaders = true;
        b.Heading(1, "Two columns");
        b.SectionBreak("continuous");
        b.Doc.Body.FinalSection.ColumnCount = 2;
        for (var i = 0; i < 8; i++) b.P("Column text " + i + " flows from the first column into the second when the first one is full.");
        b.SectionBreak("continuous");
        b.Doc.Body.FinalSection.ColumnCount = 1;
        b.P("Back to one column.");
        var doc = b.Doc;
        var editor = new DocEditor(doc);
        using (var layout = new DocumentLayout(doc, new ViewOptions()))
        {
            layout.LayoutAll();
            var onPage2 = layout.Pages[1].Columns[0].Content.OfType<ParaFragment>().First().Paragraph;
            editor.Selection = new Selection(new DocPos(onPage2, 0));
            editor.MakePageLandscape(true, layout);
        }
        return doc;
    }

    private static Document Lists()
    {
        var b = new DocBuilder(DocumentDesigns.Default);
        b.Heading(1, "Lists");
        b.Bullets("Top level", "  Second level", "    Third level", "Back to the top");
        b.P("A numbered list:");
        b.Numbered(true, "One", "Two", "  Two point one", "  Two point two", "Three");
        b.P("This list continues the one above:");
        b.Numbered(false, "Four", "Five");
        b.P("And this one starts again:");
        b.Numbered(true, "One again", "Two again");
        return b.Doc;
    }

    private static Document Tables()
    {
        var b = new DocBuilder(DocumentDesigns.Default);
        b.Heading(1, "Tables");
        var rows = new List<string[]> { new[] { "Region", "Q1", "Q2", "Q3", "Q4" } };
        for (var i = 1; i <= 60; i++) rows.Add(new[] { "Region " + i, (i * 3).ToString(CultureInfo.InvariantCulture), (i * 4).ToString(CultureInfo.InvariantCulture), (i * 5).ToString(CultureInfo.InvariantCulture), (i * 6).ToString(CultureInfo.InvariantCulture) });
        var big = b.Table(rows);
        foreach (var r in big.Rows) r.Props.CantSplit = true;
        b.P("");
        var merged = b.Table(new List<string[]> { new[] { "Merged across", "", "C" }, new[] { "Merged down", "B2", "C2" }, new[] { "", "B3", "C3" } });
        b.P("");
        var doc = b.Doc;
        var editor = new DocEditor(doc);
        var a1 = Nav.FirstIn(merged.Rows[0].Cells[0].Blocks)!;
        var b1 = Nav.FirstIn(merged.Rows[0].Cells[1].Blocks)!;
        editor.Selection = new Selection(new DocPos(a1, 0), new DocPos(b1, 0));
        editor.MergeCells();
        var a2 = Nav.FirstIn(merged.Rows[1].Cells[0].Blocks)!;
        var a3 = Nav.FirstIn(merged.Rows[2].Cells[0].Blocks)!;
        editor.Selection = new Selection(new DocPos(a2, 0), new DocPos(a3, 0));
        editor.MergeCells();
        editor.Selection = new Selection(new DocPos(Nav.FirstIn(merged.Rows[1].Cells.Last().Blocks)!, 0));
        editor.SetCellShading("FFF2CC");
        return doc;
    }

    private static Document Fields()
    {
        var b = new DocBuilder(DocumentDesigns.Default);
        b.Heading(1, "Fields, captions and links");
        var pic = b.P("");
        b.Picture(pic, SampleArt.BarChart(600, 320), 3.0, null, "A chart");
        var doc = b.Doc;
        var editor = new DocEditor(doc);
        editor.Selection = new Selection(new DocPos(pic, pic.Length));
        var caption = editor.InsertCaption("Figure", "A small chart");
        var after = b.P("The chart above is ");
        editor.Selection = new Selection(new DocPos(after, after.Length));
        editor.InsertCrossReference(caption, "number", null);
        editor.InsertText(". Visit ");
        editor.InsertLink("https://example.com/", null, "the example site");
        editor.InsertText(" or ");
        var target = b.Heading(2, "A target heading");
        var name = editor.AddBookmark("TargetHeading", new Selection(new DocPos(target, 0), new DocPos(target, target.Length)));
        editor.Selection = new Selection(new DocPos(after, after.Length));
        editor.InsertLink(null, name, "jump to the heading");
        editor.InsertText(".");
        using (var layout = new DocumentLayout(doc, new ViewOptions()))
        {
            layout.LayoutAll();
            editor.UpdateFields(layout);
        }
        return doc;
    }

    /// <summary>Counts Inkwell computes for a document, in the same terms as Word's object model.</summary>
    public static Dictionary<string, int> Counts(Document doc)
    {
        using var layout = new DocumentLayout(doc, new ViewOptions());
        layout.LayoutAll();
        var stats = new DocumentStats().Count(doc, new StatsOptions { IncludeNotes = false, IncludeTextBoxes = false });
        var body = doc.Body.Paragraphs.ToList();
        var inlines = body.SelectMany(p => p.Inlines).ToList();
        var editor = new DocEditor(doc);
        return new Dictionary<string, int>
        {
            ["pages"] = layout.Pages.Count,
            ["words"] = stats.Words,
            ["tables"] = doc.Body.Blocks.Count(b => b is Table),
            ["inlinePictures"] = inlines.OfType<DrawingObject>().Count(d => !d.IsFloating),
            ["floatingPictures"] = inlines.OfType<DrawingObject>().Count(d => d.IsFloating),
            ["footnotes"] = doc.Footnotes.Values.Count(n => !n.IsSpecial),
            ["endnotes"] = doc.Endnotes.Values.Count(n => !n.IsSpecial),
            ["comments"] = doc.Comments.Count,
            ["sections"] = doc.Sections().Count,
            ["hyperlinks"] = body.Sum(p => p.Inlines.Where(i => i.Link != null).Select(i => i.Link).Distinct().Count()),
            ["tocs"] = DocumentWalker.AllBlocks(doc.Body.Blocks).OfType<FieldBlock>().Count(f => f.Kind == FieldKind.Toc),
            ["sources"] = doc.Sources.Items.Count,
            ["revisions"] = editor.HasRevisions() ? 1 : 0,
        };
    }

    [Fact]
    public void WriteDocumentsAndCountsForWord() => Sta.Run(() =>
    {
        var folder = Folder;
        if (string.IsNullOrEmpty(folder)) return;
        Directory.CreateDirectory(folder!);
        var csv = new StringBuilder();
        foreach (var (name, create) in Documents())
        {
            var doc = create();
            var path = Path.Combine(folder!, name + ".docx");
            // Fonts embedded, as Inkwell saves by default: Word lays the pages out with the same fonts.
            DocumentFiles.SaveDocx(doc, path, embedFonts: true);
            var counts = Counts(DocumentFiles.Open(path).Document);
            foreach (var (key, value) in counts) csv.AppendLine($"{name},{key},{value}");
        }
        File.WriteAllText(Path.Combine(folder!, "inkwell.csv"), csv.ToString());
    });

    [Fact]
    public void ReadWordsCopiesBack() => Sta.Run(() =>
    {
        var folder = Folder;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder!)) return;
        var copies = Directory.GetFiles(folder!, "*.word.docx");
        if (copies.Length == 0) return;
        var csv = new StringBuilder();
        foreach (var copy in copies)
        {
            var name = Path.GetFileName(copy).Replace(".word.docx", "");
            var original = DocumentFiles.Open(Path.Combine(folder!, name + ".docx")).Document;
            var fromWord = DocumentFiles.Open(copy).Document;
            string Text(Document d) => string.Join("\n", d.Body.Paragraphs.Select(p => p.PlainText()));
            var same = Text(original) == Text(fromWord);
            csv.AppendLine($"{name},textSame,{(same ? 1 : 0)}");
            foreach (var (key, value) in Counts(fromWord)) csv.AppendLine($"{name},{key},{value}");
        }
        File.WriteAllText(Path.Combine(folder!, "inkwell-from-word.csv"), csv.ToString());
    });
}
