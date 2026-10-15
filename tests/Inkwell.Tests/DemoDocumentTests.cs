using Inkwell.Core.Editing;
using Inkwell.Core.Export;
using Inkwell.Core.IO;
using Inkwell.Core.Layout;
using Inkwell.Core.Model;
using Inkwell.Core.Samples;
using Xunit;

namespace Inkwell.Tests;

/// <summary>
/// The demo documents build, lay out, save as valid .docx and read back. INKWELL_SAMPLES_OUT=folder also writes
/// them (with a PDF of each) for the repository's samples folder; INKWELL_RENDER_OUT renders their pages.
/// </summary>
public class DemoDocumentTests
{
    public static IEnumerable<object[]> Ids => DemoDocuments.All.Select(d => new object[] { d.Id });

    [Theory]
    [MemberData(nameof(Ids))]
    public void Demo_BuildsLaysOutAndSavesValidDocx(string id) => Sta.Run(() =>
    {
        var demo = DemoDocuments.All.Single(d => d.Id == id);
        var doc = demo.Create();
        using var layout = new DocumentLayout(doc, new ViewOptions());
        layout.LayoutAll();
        Assert.True(layout.Pages.Count >= 1);
        RenderHelper.SavePages(layout, "demo-" + id, maxPages: 8, scale: 1.0);

        var bytes = Docx.Save(doc);
        Docx.AssertValid(bytes);
        AssertFieldsNotSplit(bytes);
        var back = Docx.Load(bytes);
        Assert.Equal(Docx.Text(doc), Docx.Text(back));

        var folder = Environment.GetEnvironmentVariable("INKWELL_SAMPLES_OUT");
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder!);
            DocumentFiles.SaveDocx(DemoDocuments.All.Single(d => d.Id == id).Create(), Path.Combine(folder!, id + ".docx"), embedFonts: false);
            PdfExporter.Export(doc, Path.Combine(folder!, id + ".pdf"), new PdfExportOptions { FileName = id + ".docx" });
        }
    });

    /// <summary>
    /// Word refuses a document where a field's characters are spread over several hyperlinks (begin in one,
    /// end in another), which the schema allows: every hyperlink and tracked change must hold whole fields.
    /// </summary>
    private static void AssertFieldsNotSplit(byte[] bytes)
    {
        var body = Docx.PartXml(bytes, "word/document.xml");
        foreach (var wrapper in body.Descendants().Where(e => e.Name == Docx.W + "hyperlink" || e.Name == Docx.W + "ins" && e.Parent?.Name == Docx.W + "p"))
        {
            var depth = 0;
            foreach (var c in wrapper.Descendants(Docx.W + "fldChar"))
            {
                var type = (string?)c.Attribute(Docx.W + "fldCharType");
                if (type == "begin") depth++;
                else if (type == "end") depth--;
                Assert.True(depth >= 0, "a field ends in a hyperlink it didn't start in");
            }
            Assert.True(depth == 0, "a field starts in a hyperlink and ends outside it");
        }
    }

    [Fact]
    public void Report_HasTocFootnotesTablePicturesAndCaptions() => Sta.Run(() =>
    {
        var doc = DemoDocuments.Report();
        Assert.Contains(DocumentWalker.AllBlocks(doc.Body.Blocks).OfType<FieldBlock>(), f => f.Kind == FieldKind.Toc);
        Assert.True(doc.Footnotes.Values.Count(n => !n.IsSpecial) >= 3);
        Assert.Contains(doc.Body.Blocks, b => b is Table);
        var drawings = doc.Body.Paragraphs.SelectMany(p => p.Inlines).OfType<DrawingObject>().ToList();
        Assert.Contains(drawings, d => d.IsFloating && d.Wrap == WrapMode.Square);
        Assert.Contains(drawings, d => !d.IsFloating);
        var captions = doc.Body.Paragraphs.Where(p => p.Props.StyleId == "Caption").Select(p => p.PlainText()).ToList();
        Assert.Contains(captions, c => c.StartsWith("Figure 1", StringComparison.Ordinal));
        Assert.Contains(captions, c => c.StartsWith("Table 1", StringComparison.Ordinal));
        // The table of contents lists the headings with page numbers.
        var toc = DocumentWalker.AllBlocks(doc.Body.Blocks).OfType<FieldBlock>().First(f => f.Kind == FieldKind.Toc);
        var entries = DocumentWalker.Paragraphs(toc.Blocks).Select(p => p.PlainText()).ToList();
        Assert.Contains(entries, e => e.StartsWith("Summary", StringComparison.Ordinal) && char.IsDigit(e.TrimEnd()[e.TrimEnd().Length - 1]));
        Assert.Contains(entries, e => e.StartsWith("Next year", StringComparison.Ordinal));
    });

    [Fact]
    public void Essay_HasCitationsBibliographyCommentsAndTrackedChanges() => Sta.Run(() =>
    {
        var doc = DemoDocuments.Essay();
        var editor = new DocEditor(doc);
        Assert.Equal(4, doc.Sources.Items.Count);
        var citations = doc.Body.Paragraphs.SelectMany(p => p.Inlines).OfType<FieldInline>().Where(f => f.Kind == FieldKind.Citation).ToList();
        Assert.Equal(4, citations.Count);
        Assert.Contains(citations, c => c.ResultText.Contains("Barton, 2014"));
        var bibliography = DocumentWalker.AllBlocks(doc.Body.Blocks).OfType<FieldBlock>().Single(f => f.Kind == FieldKind.Bibliography);
        Assert.Equal(4, DocumentWalker.Paragraphs(bibliography.Blocks).Count());
        Assert.Equal(3, doc.Comments.Count);
        Assert.Single(doc.Comments, c => c.ParentId != null);
        Assert.True(editor.HasRevisions());
        Assert.Contains("Dr Amina Siddiqui", editor.RevisionAuthors());
    });

    [Fact]
    public void Bilingual_UrduParagraphsAreRightToLeftWithNastaliq() => Sta.Run(() =>
    {
        var doc = DemoDocuments.Bilingual();
        var editor = new DocEditor(doc);
        var urdu = doc.Body.Paragraphs.Where(p => p.Text.Any(Inkwell.Core.Layout.RunPropsFactory.IsRtlChar) && Nav.CellOf(p) == null).ToList();
        Assert.True(urdu.Count >= 5);
        foreach (var p in urdu)
        {
            Assert.True(editor.Styles.ResolvePara(p).Bidi);
            var run = p.Inlines.OfType<TextRun>().First(r => r.Text.Any(Inkwell.Core.Layout.RunPropsFactory.IsRtlChar));
            var resolved = editor.Styles.ResolveRun(run.Props, p);
            Assert.True(resolved.Rtl);
            Assert.Contains(resolved.CsFont, new[] { "Noto Nastaliq Urdu", "Noto Naskh Arabic" });
        }
        using var layout = new DocumentLayout(doc, new ViewOptions());
        layout.LayoutAll();
        // Right-to-left lines start at the right margin: the short last line of an Urdu paragraph ends at the right edge.
        var p0 = urdu.First(p => p.Props.StyleId == null);
        var loc = layout.Locate(p0).First();
        var last = loc.Fragment.Lines[loc.Fragment.Lines.Count - 1];
        Assert.True(loc.Fragment.Lines.Count >= 2, "the paragraph should wrap");
        Assert.True(Math.Abs(last.ContentStart + last.ContentWidth - last.FormatWidth) < 3, $"the last line should end at the right: {last.ContentStart} + {last.ContentWidth} vs {last.FormatWidth}");
        Assert.True(last.ContentStart > 20, "the last (short) line should start well right of the left edge");
    });
}
