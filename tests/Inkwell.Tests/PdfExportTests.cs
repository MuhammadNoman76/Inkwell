using Inkwell.Core.Export;
using Inkwell.Core.Model;
using Inkwell.Core.Samples;
using Inkwell.Core.Templates;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using PdfSharp.Pdf.IO;
using Xunit;

namespace Inkwell.Tests;

/// <summary>PDF export: one page per page, bookmarks from headings, live links, a selectable text layer.</summary>
public class PdfExportTests
{
    private static PdfDocument ExportAndRead(Document doc, PdfExportOptions options, out int pages)
    {
        using var ms = new MemoryStream();
        using (var layout = new Core.Layout.DocumentLayout(doc, new Core.Layout.ViewOptions()))
        {
            layout.LayoutAll();
            pages = layout.Pages.Count;
        }
        PdfExporter.Export(doc, ms, options);
        ms.Position = 0;
        return PdfReader.Open(ms, PdfDocumentOpenMode.Import);
    }

    private static List<string> OutlineTitles(PdfDocument pdf)
    {
        var titles = new List<string>();
        void Walk(PdfDictionary? item)
        {
            while (item != null)
            {
                titles.Add(item.Elements.GetString("/Title"));
                Walk(item.Elements.GetDictionary("/First"));
                item = item.Elements.GetDictionary("/Next");
            }
        }
        var root = pdf.Internals.Catalog.Elements.GetDictionary("/Outlines");
        Walk(root?.Elements.GetDictionary("/First"));
        return titles;
    }

    [Fact]
    public void Report_HasAPagePerPage_BookmarksForHeadings_AndATextLayer() => Sta.Run(() =>
    {
        var doc = DemoDocuments.Report();
        using var pdf = ExportAndRead(doc, new PdfExportOptions { FileName = "report.docx" }, out var pages);
        Assert.Equal(pages, pdf.PageCount);
        var titles = OutlineTitles(pdf);
        Assert.Contains("Summary", titles);
        Assert.Contains("How decisions are made", titles);
        Assert.Contains("Next year", titles);
        Assert.Equal("Harbour Light Cooperative: Annual report 2026", pdf.Info.Title);

        // Every page with text has text-showing operators in a font with a ToUnicode map (copy and search work).
        for (var i = 0; i < pdf.PageCount; i++)
        {
            var content = ContentReader.ReadContent(pdf.Pages[i]);
            var ops = Flatten(content).OfType<COperator>().Select(o => o.OpCode.Name).ToList();
            Assert.Contains(ops, o => o is "Tj" or "TJ");
            var fonts = pdf.Pages[i].Resources.Elements.GetDictionary("/Font");
            Assert.NotNull(fonts);
            Assert.Contains(fonts!.Elements.Values.Select(v => (v as PdfReference)?.Value as PdfDictionary ?? v as PdfDictionary), f => f?.Elements.ContainsKey("/ToUnicode") == true);
        }
    });

    [Fact]
    public void WebLinks_AndInternalLinks_BecomeAnnotations() => Sta.Run(() =>
    {
        var b = new DocBuilder(DocumentDesigns.Default);
        var top = b.P("See [the website](https://example.com/inkwell) for more.");
        b.PageBreak();
        b.Heading(1, "Target heading");
        var doc = b.Doc;
        var editor = new Core.Editing.DocEditor(doc);
        var heading = doc.Body.Paragraphs.Last();
        var name = editor.AddBookmark("Target", new Core.Editing.Selection(new Core.Editing.DocPos(heading, 0), new Core.Editing.DocPos(heading, heading.Length)));
        editor.Selection = new Core.Editing.Selection(new Core.Editing.DocPos(top, top.Length));
        editor.InsertLink(null, name, " Jump");
        using var pdf = ExportAndRead(doc, new PdfExportOptions(), out _);
        var annots = pdf.Pages[0].Elements.GetArray("/Annots");
        Assert.NotNull(annots);
        var dicts = annots!.Elements.Select(e => (e as PdfReference)?.Value as PdfDictionary ?? e as PdfDictionary).Where(d => d != null).ToList();
        Assert.Contains(dicts, d => d!.Elements.GetDictionary("/A")?.Elements.GetString("/URI") == "https://example.com/inkwell");
        Assert.Contains(dicts, d => d!.Elements.ContainsKey("/Dest") || d!.Elements.GetDictionary("/A")?.Elements.GetName("/S") == "/GoTo");
    });

    [Fact]
    public void WithoutSelectableText_ThereIsNoTextLayer() => Sta.Run(() =>
    {
        var b = new DocBuilder(DocumentDesigns.Default);
        b.P("Only outlines, no text layer.");
        using var pdf = ExportAndRead(b.Doc, new PdfExportOptions { SelectableText = false }, out _);
        var ops = Flatten(ContentReader.ReadContent(pdf.Pages[0])).OfType<COperator>().Select(o => o.OpCode.Name).ToList();
        Assert.DoesNotContain(ops, o => o is "Tj" or "TJ");
    });

    [Fact]
    public void UrduText_IsExportedWithAnArabicScriptTextLayer() => Sta.Run(() =>
    {
        using var pdf = ExportAndRead(DemoDocuments.Bilingual(), new PdfExportOptions(), out var pages);
        Assert.Equal(pages, pdf.PageCount);
        var fonts = pdf.Pages[0].Resources.Elements.GetDictionary("/Font")!;
        var names = fonts.Elements.Values.Select(v => ((v as PdfReference)?.Value as PdfDictionary ?? v as PdfDictionary)?.Elements.GetName("/BaseFont") ?? "").ToList();
        Assert.Contains(names, n => n.Contains("InkwellTextLayerArabic") || n.Contains("Naskh"));
    });

    private static IEnumerable<CObject> Flatten(CSequence sequence)
    {
        foreach (var item in sequence)
        {
            yield return item;
            if (item is COperator op)
                foreach (var operand in op.Operands)
                    if (operand is CSequence inner)
                        foreach (var x in Flatten(inner))
                            yield return x;
            if (item is CSequence seq)
                foreach (var x in Flatten(seq))
                    yield return x;
        }
    }
}
