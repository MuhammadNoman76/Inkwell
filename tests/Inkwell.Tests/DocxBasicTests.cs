using System.Xml.Linq;
using Inkwell.Core.IO;
using Inkwell.Core.Model;

namespace Inkwell.Tests;

public class DocxBasicTests
{
    private static readonly XNamespace W = Ns.W;

    [Fact]
    public void NewDocument_WritesACompleteValidPackage()
    {
        var doc = new Document();
        doc.Body.Blocks.Add(Docx.Para("Hello, world.", "Title"));
        doc.Body.Blocks.Add(Docx.Para("Second paragraph."));
        var back = Docx.RoundTrip(doc, out var bytes);

        var names = Docx.PartNames(bytes);
        foreach (var expected in new[] { "[Content_Types].xml", "_rels/.rels", "word/document.xml", "word/styles.xml", "word/settings.xml",
                     "word/theme/theme1.xml", "word/fontTable.xml", "word/webSettings.xml", "docProps/core.xml", "docProps/app.xml" })
            Assert.Contains(expected, names);
        Assert.Equal("Hello, world.\nSecond paragraph.", Docx.Text(back));
        Assert.Equal("Title", ((Paragraph)back.Body.Blocks[0]).StyleId);
        Assert.NotNull(back.Source);
        Assert.Equal(DocxWriter.ContentType(DocxFormat.Document), back.Source!.MainContentType);
        Assert.Empty(back.Warnings);
        Assert.Empty(back.Preserved);
        Assert.Equal("Inkwell", Docx.PartXml(bytes, "docProps/app.xml").Root!.Element(Ns.EP + "Application")!.Value);
    }

    [Fact]
    public void Save_SetsSourceSoTheNextSaveStartsFromIt()
    {
        var doc = new Document();
        doc.Body.Blocks.Add(Docx.Para("One"));
        Assert.Null(doc.Source);
        var bytes = Docx.Save(doc);
        Assert.NotNull(doc.Source);
        Assert.Same(bytes, doc.Source!.Bytes);
        ((Paragraph)doc.Body.Blocks[0]).Inlines.Add(new TextRun(" more"));
        var again = Docx.Save(doc);
        Docx.AssertValid(again);
        Assert.Equal("One more", Docx.Text(Docx.Load(again)));
    }

    [Fact]
    public void ControlCharacters_BecomeWordElements_AndRoundTrip()
    {
        var doc = new Document();
        var text = " lead\ttab\vline\fpage\u000Ecolumn­soft‑hard trail ";
        doc.Body.Blocks.Add(new Paragraph(text));
        var back = Docx.RoundTrip(doc, out var bytes);
        var run = Docx.Body(bytes).Element(W + "p")!.Element(W + "r")!;
        var names = run.Elements().Where(e => e.Name != W + "rPr").Select(e => e.Name.LocalName + (e.Attribute(W + "type") is { } t ? ":" + t.Value : "")).ToList();
        Assert.Equal(new[] { "t", "tab", "t", "br", "t", "br:page", "t", "br:column", "t", "softHyphen", "t", "noBreakHyphen", "t" }, names);
        Assert.Equal("preserve", run.Element(W + "t")!.Attribute(XNamespace.Xml + "space")?.Value);
        Assert.Equal(text, ((Paragraph)back.Body.Blocks[0]).Text);
    }

    [Fact]
    public void CharactersXmlCantHold_AreDroppedOrReplaced()
    {
        var doc = new Document();
        doc.Body.Blocks.Add(new Paragraph("a\u0001b\u0007c\uD800d￾e\U0001F600f"));
        var back = Docx.RoundTrip(doc);
        Assert.Equal("abc�de\U0001F600f", ((Paragraph)back.Body.Blocks[0]).Text);
    }

    [Fact]
    public void RunFormatting_RoundTrips()
    {
        var doc = new Document();
        var p = new Paragraph();
        var run = new TextRun("formatted");
        run.Props.Bold = true;
        run.Props.Italic = true;
        run.Props.Color = "C00000";
        run.Props.Size = 28;
        run.Props.Underline = "double";
        run.Props.SetLatinFont("Georgia");
        run.Props.Highlight = "yellow";
        run.Props.VertAlign = "superscript";
        run.Props.Spacing = 20;
        run.Props.Ligatures = "standard";
        run.Props.Lang = "fr-FR";
        p.Inlines.Add(run);
        var plain = new TextRun(" plain");
        p.Inlines.Add(plain);
        doc.Body.Blocks.Add(p);

        var back = Docx.RoundTrip(doc);
        var bp = (Paragraph)back.Body.Blocks[0];
        Assert.Equal(2, bp.Inlines.Count);
        var r = (TextRun)bp.Inlines[0];
        Assert.True(r.Props.SameFormatting(run.Props), r.Props.ToString());
        Assert.True(r.Props.Bold);
        Assert.Equal("Georgia", r.Props.FontAscii);
        Assert.Equal("standard", r.Props.Ligatures);
        Assert.True(bp.Inlines[1].Props.IsEmpty);
    }

    [Fact]
    public void AdjacentRunsWithTheSameFormatting_AreMergedOnRead()
    {
        var bytes = TestDocx.Create("<w:p><w:r w:rsidR=\"00A1\"><w:rPr><w:b/></w:rPr><w:t>Hel</w:t></w:r><w:proofErr w:type=\"spellStart\"/>" +
                                    "<w:r w:rsidR=\"00B2\"><w:rPr><w:b/></w:rPr><w:t>lo</w:t></w:r><w:r><w:t xml:space=\"preserve\"> world</w:t></w:r></w:p>");
        var doc = Docx.Load(bytes);
        var p = (Paragraph)doc.Body.Blocks[0];
        Assert.Equal(2, p.Inlines.Count);
        Assert.Equal("Hello", ((TextRun)p.Inlines[0]).Text);
        Assert.Equal(" world", ((TextRun)p.Inlines[1]).Text);
    }

    [Fact]
    public void ParagraphProperties_RoundTrip()
    {
        var doc = new Document();
        var p = new Paragraph("Body text", "Heading2");
        p.Props.Justification = "both";
        p.Props.SpacingBefore = 240;
        p.Props.SpacingAfter = 120;
        p.Props.SpacingLine = 360;
        p.Props.SpacingLineRule = "auto";
        p.Props.IndentStart = 720;
        p.Props.FirstLine = -360;
        p.Props.KeepNext = true;
        p.Props.Tabs = new List<TabStopDef> { new("right", 9000, "dot"), new("center", 4500, null) };
        p.Props.SetBorder("bottom", BorderDef.Single(8, "1F2A44"));
        p.Props.ShadingFill = "F2F2F2";
        p.Props.OutlineLevel = 1;
        p.Props.MarkProps(true)!.Bold = true;
        doc.Body.Blocks.Add(p);

        var back = Docx.RoundTrip(doc);
        var bp = (Paragraph)back.Body.Blocks[0];
        Assert.True(Ox.SameXml(p.Props.X, bp.Props.X), bp.Props.ToString());
        Assert.Equal(-360, bp.Props.FirstLine);
        Assert.Equal(2, bp.Props.Tabs.Count);
    }

    [Fact]
    public void Styles_BuiltInAndCustom_RoundTrip()
    {
        var doc = new Document();
        var custom = StyleSheet.Create("paragraph", "Callout", "Callout", "Normal", "Normal", custom: true, priority: 50);
        custom.RunProps(true)!.Color = "8C2F39";
        custom.ParaProps(true)!.SpacingBefore = 180;
        doc.Styles.Put(custom);
        doc.Body.Blocks.Add(Docx.Para("Callout text", "Callout"));
        doc.Body.Blocks.Add(Docx.Para("Heading", "Heading1"));

        var back = Docx.RoundTrip(doc);
        var style = back.Styles.Get("Callout");
        Assert.NotNull(style);
        Assert.True(Ox.SameXml(custom.X, style!.X));
        Assert.True(style.IsCustom);
        Assert.Equal(doc.Styles.All.Count(), back.Styles.All.Count());
        Assert.True(Ox.SameXml(doc.Styles.Get("Heading1")!.X, back.Styles.Get("Heading1")!.X));
        Assert.Equal("Inkwell", back.Theme.Name);
    }

    [Fact]
    public void Numbering_ListsAndRestartOverrides_RoundTrip()
    {
        var doc = new Document();
        var bulletsAbs = doc.Numbering.AddAbstract(Numbering.StandardDefinition(true));
        var numbersAbs = doc.Numbering.AddAbstract(Numbering.StandardDefinition(false));
        var bullets = doc.Numbering.AddNum(bulletsAbs);
        var numbers = doc.Numbering.AddNum(numbersAbs);
        var restarted = doc.Numbering.AddNum(numbersAbs, restartAt: 5);
        void Item(string text, int numId, int level)
        {
            var p = new Paragraph(text, "ListParagraph");
            p.Props.SetNumbering(numId, level);
            doc.Body.Blocks.Add(p);
        }
        Item("bullet", bullets, 0);
        Item("nested bullet", bullets, 1);
        Item("one", numbers, 0);
        Item("two", numbers, 0);
        Item("five", restarted, 0);

        var back = Docx.RoundTrip(doc, out var bytes);
        Assert.Contains("word/numbering.xml", Docx.PartNames(bytes));
        var paragraphs = back.Body.Blocks.OfType<Paragraph>().ToList();
        Assert.Equal(new int?[] { bullets, bullets, numbers, numbers, restarted }, paragraphs.Select(p => p.Props.NumId).ToArray());
        Assert.Equal(1, paragraphs[1].Props.NumLevel);
        Assert.Equal(5, back.Numbering.Level(restarted, 0)!.Start);
        Assert.True(back.Numbering.Level(bullets, 0)!.IsBullet);
        Assert.Equal("%2.", back.Numbering.Level(numbers, 1)!.Text);
    }

    [Fact]
    public void NoNumbering_NoNumberingPart()
    {
        var doc = new Document();
        doc.Body.Blocks.Add(Docx.Para("Plain"));
        var bytes = Docx.Save(doc);
        Assert.DoesNotContain("word/numbering.xml", Docx.PartNames(bytes));
    }

    [Fact]
    public void PageAndColumnBreaks_RoundTrip()
    {
        var doc = new Document();
        doc.Body.Blocks.Add(new Paragraph("before\fafter"));
        doc.Body.Blocks.Add(new Paragraph("left\u000Eright"));
        var back = Docx.RoundTrip(doc);
        Assert.Equal("before\fafter", ((Paragraph)back.Body.Blocks[0]).Text);
        Assert.Equal("left\u000Eright", ((Paragraph)back.Body.Blocks[1]).Text);
    }

    [Fact]
    public void ClearBreakAndSymbol_RoundTrip()
    {
        var doc = new Document();
        var p = new Paragraph();
        p.Inlines.Add(new TextRun("a"));
        p.Inlines.Add(new ClearBreak { Clear = "left" });
        p.Inlines.Add(new SymbolChar { Font = "Wingdings", Char = '' });
        doc.Body.Blocks.Add(p);
        var back = Docx.RoundTrip(doc);
        var bp = (Paragraph)back.Body.Blocks[0];
        Assert.Equal("left", Assert.IsType<ClearBreak>(bp.Inlines[1]).Clear);
        var sym = Assert.IsType<SymbolChar>(bp.Inlines[2]);
        Assert.Equal("Wingdings", sym.Font);
        Assert.Equal('', sym.Char);
    }

    [Fact]
    public void DocumentProperties_RoundTrip_AndAppPropertiesUpdated()
    {
        var doc = new Document();
        doc.Body.Blocks.Add(Docx.Para("x"));
        doc.Properties.Title = "The Title";
        doc.Properties.Subject = "Subject";
        doc.Properties.Creator = "Ada Lovelace";
        doc.Properties.Keywords = "one, two";
        doc.Properties.Description = "Notes";
        doc.Properties.Category = "Reports";
        var when = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var options = new DocxSaveOptions { LastModifiedBy = "Muhammad", Pages = 3, Words = 1234, Now = when };
        var back = Docx.RoundTrip(doc, out var bytes, options);
        Assert.Equal("The Title", back.Properties.Title);
        Assert.Equal("Ada Lovelace", back.Properties.Creator);
        Assert.Equal("one, two", back.Properties.Keywords);
        Assert.Equal("Reports", back.Properties.Category);
        Assert.Equal("Muhammad", back.Properties.LastModifiedBy);
        Assert.Equal(when, back.Properties.Modified);
        var app = Docx.PartXml(bytes, "docProps/app.xml").Root!;
        Assert.Equal("3", app.Element(Ns.EP + "Pages")!.Value);
        Assert.Equal("1234", app.Element(Ns.EP + "Words")!.Value);

        // Unknown core properties survive (kept through CoreProps.Original).
        var withExtra = TestDocx.Create(TestDocx.P("x"), (pkg, _) =>
        {
            var core = pkg.AddCoreFilePropertiesPart();
            TestDocx.Write(core, "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
                                 "xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"><dc:title>T</dc:title><dc:identifier>ID-42</dc:identifier><dc:language>en-GB</dc:language></cp:coreProperties>");
        });
        var loaded = Docx.Load(withExtra);
        Assert.Equal("T", loaded.Properties.Title);
        var resaved = Docx.Save(loaded);
        Docx.AssertValid(resaved);
        var core2 = Docx.PartXml(resaved, "docProps/core.xml").Root!;
        Assert.Equal("ID-42", core2.Element(Ns.DC + "identifier")!.Value);
        Assert.Equal("en-GB", core2.Element(Ns.DC + "language")!.Value);
        Assert.NotNull(core2.Element(Ns.DCTerms + "modified"));
    }
}
