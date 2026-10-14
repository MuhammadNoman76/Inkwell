using System.Xml.Linq;
using Inkwell.Core.IO;
using Inkwell.Core.Model;

namespace Inkwell.Tests;

public class DocxFieldTests
{
    private static readonly XNamespace W = Ns.W;

    private static string Begin(string extra = "") => "<w:r><w:fldChar w:fldCharType=\"begin\"" + extra + "/></w:r>";
    private static string Instr(string code) => "<w:r><w:instrText xml:space=\"preserve\">" + code + "</w:instrText></w:r>";
    private const string Sep = "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>";
    private const string End = "<w:r><w:fldChar w:fldCharType=\"end\"/></w:r>";
    private static string T(string text) => "<w:r><w:t xml:space=\"preserve\">" + text + "</w:t></w:r>";

    [Fact]
    public void SimpleAndComplexFields_WithFlagsAndNestedResults_RoundTrip()
    {
        var doc = new Document();
        var p = new Paragraph("Page ");
        p.Inlines.Add(new FieldInline { Code = " PAGE ", Simple = true, Result = { new TextRun("4") } });
        p.Inlines.Add(new TextRun(" printed "));
        var date = new FieldInline { Code = " DATE \\@ \"d MMMM yyyy\" ", Dirty = true, Result = { new TextRun("1 June 2026") } };
        date.BeginProps = new RunProps();
        date.BeginProps.Italic = true;
        p.Inlines.Add(date);
        p.Inlines.Add(new TextRun(" "));
        var locked = new FieldInline { Code = " QUOTE ", Locked = true };
        locked.Result.Add(new TextRun("see "));
        locked.Result.Add(new FieldInline { Code = " REF Target \\h ", Result = { new TextRun("the target") } });
        p.Inlines.Add(locked);
        doc.Body.Blocks.Add(p);

        var back = Docx.RoundTrip(doc, out var bytes);
        var body = Docx.Body(bytes);
        Assert.Single(body.Descendants(W + "fldSimple"));
        Assert.Equal("true", (string?)body.Descendants(W + "fldChar").First(e => e.Attribute(W + "dirty") != null).Attribute(W + "dirty"));
        Assert.Single(body.Descendants(W + "fldChar"), e => e.Attribute(W + "fldLock") != null);

        var inl = ((Paragraph)back.Body.Blocks[0]).Inlines;
        var page = Assert.IsType<FieldInline>(inl[1]);
        Assert.True(page.Simple);
        Assert.Equal(FieldKind.Page, page.Kind);
        Assert.Equal("4", page.ResultText);
        var d = Assert.IsType<FieldInline>(inl[3]);
        Assert.False(d.Simple);
        Assert.True(d.Dirty);
        Assert.Equal(FieldKind.Date, d.Kind);
        Assert.Equal(" DATE \\@ \"d MMMM yyyy\" ", d.Code);
        Assert.True(d.BeginProps!.Italic);
        Assert.Equal("1 June 2026", d.ResultText);
        var q = Assert.IsType<FieldInline>(inl[5]);
        Assert.True(q.Locked);
        Assert.Null(q.PreservedXml);
        Assert.Equal(2, q.Result.Count);
        Assert.Equal(FieldKind.Ref, Assert.IsType<FieldInline>(q.Result[1]).Kind);
        Assert.Equal("Page 4 printed 1 June 2026 see ", ((Paragraph)back.Body.Blocks[0]).PlainText());
    }

    [Fact]
    public void FieldsWithRevisions_AreWrittenAsCharacterFieldsWithDeletedCode()
    {
        var doc = new Document();
        var when = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var p = new Paragraph();
        p.Inlines.Add(new FieldInline { Code = " PAGE ", Simple = true, Result = { new TextRun("1") { Deleted = new RevisionInfo { Author = "A", Date = when } } }, Deleted = new RevisionInfo { Author = "A", Date = when } });
        doc.Body.Blocks.Add(p);
        var back = Docx.RoundTrip(doc, out var bytes);
        var body = Docx.Body(bytes);
        Assert.Empty(body.Descendants(W + "fldSimple")); // w:fldSimple can't sit in w:del
        Assert.Single(body.Descendants(W + "delInstrText"));
        Assert.Single(body.Descendants(W + "delText"));
        var f = Assert.IsType<FieldInline>(((Paragraph)back.Body.Blocks[0]).Inlines.Single());
        Assert.NotNull(f.Deleted);
        Assert.Equal(" PAGE ", f.Code);
    }

    private static string TocInContentControl() =>
        "<w:sdt><w:sdtPr><w:id w:val=\"-1\"/><w:docPartObj><w:docPartGallery w:val=\"Table of Contents\"/><w:docPartUnique/></w:docPartObj></w:sdtPr>" +
        "<w:sdtEndPr><w:rPr><w:b/><w:bCs/><w:noProof/></w:rPr></w:sdtEndPr><w:sdtContent>" +
        "<w:p><w:pPr><w:pStyle w:val=\"TOCHeading\"/></w:pPr>" + T("Contents") + "</w:p>" +
        "<w:p><w:pPr><w:pStyle w:val=\"TOC1\"/><w:tabs><w:tab w:val=\"right\" w:leader=\"dot\" w:pos=\"9350\"/></w:tabs></w:pPr>" + Begin(" w:dirty=\"true\"") + Instr(" TOC \\o \"1-3\" \\h \\z \\u ") + Sep +
        "<w:hyperlink w:anchor=\"_Toc1\" w:history=\"1\">" + T("Introduction") + "<w:r><w:tab/></w:r>" + Begin() + Instr(" PAGEREF _Toc1 \\h ") + Sep + T("1") + End + "</w:hyperlink></w:p>" +
        "<w:p><w:pPr><w:pStyle w:val=\"TOC1\"/></w:pPr><w:hyperlink w:anchor=\"_Toc2\" w:history=\"1\">" + T("Method") + "<w:r><w:tab/></w:r>" + Begin() + Instr(" PAGEREF _Toc2 \\h ") + Sep + T("2") + End + "</w:hyperlink></w:p>" +
        "<w:p>" + End + "</w:p></w:sdtContent></w:sdt>" +
        "<w:p><w:bookmarkStart w:id=\"0\" w:name=\"_Toc1\"/>" + T("Introduction") + "<w:bookmarkEnd w:id=\"0\"/></w:p>" +
        "<w:p><w:bookmarkStart w:id=\"1\" w:name=\"_Toc2\"/>" + T("Method") + "<w:bookmarkEnd w:id=\"1\"/></w:p>";

    [Fact]
    public void TableOfContents_InAContentControl_IsAFieldBlock()
    {
        var original = TestDocx.Create(TocInContentControl());
        var doc = Docx.Load(original);
        var sdt = Assert.IsType<SdtBlock>(doc.Body.Blocks[0]);
        Assert.True(sdt.IsTableOfContents);
        Assert.True(sdt.IsManaged);
        Assert.Null(sdt.Original);
        Assert.Equal(2, sdt.Blocks.Count);
        Assert.Equal("Contents", ((Paragraph)sdt.Blocks[0]).PlainText());
        var toc = Assert.IsType<FieldBlock>(sdt.Blocks[1]);
        Assert.Equal(FieldKind.Toc, toc.Kind);
        Assert.True(toc.Dirty);
        Assert.Equal(3, toc.Blocks.Count);
        Assert.Equal(0, toc.BeginAt);
        Assert.Equal(0, toc.EndAt);
        var entry = (Paragraph)toc.Blocks[0];
        Assert.Equal("Introduction\t1", entry.PlainText());
        var link = entry.Inlines[0].Link;
        Assert.Equal("_Toc1", link!.Anchor);
        var pageRef = Assert.IsType<FieldInline>(entry.Inlines[1]);
        Assert.Equal(FieldKind.PageRef, pageRef.Kind);
        Assert.Same(link, pageRef.Link);
        Assert.Empty(doc.Warnings);
        Assert.DoesNotContain(doc.Preserved, p => p.Category == "contentControl");

        var saved = Docx.Save(doc);
        Docx.AssertValid(saved);
        var paragraphs = Docx.Body(saved).Element(W + "sdt")!.Element(W + "sdtContent")!.Elements(W + "p").ToList();
        Assert.Equal(4, paragraphs.Count);
        var firstEntry = paragraphs[1];
        Assert.Equal("begin", (string?)firstEntry.Element(W + "r")!.Element(W + "fldChar")!.Attribute(W + "fldCharType"));
        Assert.Equal(2, firstEntry.Element(W + "hyperlink")!.Descendants(W + "fldChar").Count(e => (string?)e.Attribute(W + "fldCharType") != "separate"));
        Assert.Equal("end", (string?)paragraphs[3].Descendants(W + "fldChar").Single().Attribute(W + "fldCharType"));

        var back = Docx.Load(saved);
        var toc2 = (FieldBlock)((SdtBlock)back.Body.Blocks[0]).Blocks[1];
        Assert.Equal(toc.Code, toc2.Code);
        Assert.Equal(3, toc2.Blocks.Count);
    }

    [Fact]
    public void TableOfContents_BuiltWithTheModel_IsWrittenAsWordWritesIt()
    {
        var doc = new Document();
        var sdt = new SdtBlock();
        sdt.Properties.Add(new XElement(W + "docPartObj", new XElement(W + "docPartGallery", new XAttribute(W + "val", "Table of Contents")), new XElement(W + "docPartUnique")));
        sdt.Blocks.Add(new Paragraph("Contents", "TOCHeading"));
        var field = new FieldBlock { Code = " TOC \\o \"1-3\" \\h \\z \\u ", Dirty = true };
        field.Blocks.Add(new Paragraph("Chapter one\t1", "TOC1"));
        field.Blocks.Add(new Paragraph("Chapter two\t5", "TOC1"));
        field.EndAt = 1; // after the text of the last entry
        sdt.Blocks.Add(field);
        doc.Body.Blocks.Add(sdt);
        doc.Body.Blocks.Add(new Paragraph("Chapter one", "Heading1"));

        var back = Docx.RoundTrip(doc, out var bytes);
        var fb = (FieldBlock)((SdtBlock)back.Body.Blocks[0]).Blocks[1];
        Assert.Equal(2, fb.Blocks.Count);
        Assert.Equal(0, fb.BeginAt);
        Assert.Equal(1, fb.EndAt);
        Assert.True(fb.Dirty);
        Assert.Equal("Chapter two\t5", ((Paragraph)fb.Blocks[1]).PlainText());
        var chars = Docx.Body(bytes).Descendants(W + "fldChar").Select(e => (string?)e.Attribute(W + "fldCharType")).ToList();
        Assert.Equal(new[] { "begin", "separate", "end" }, chars);
    }

    [Fact]
    public void FieldSpanningParagraphs_OutsideAContentControl_IsAFieldBlock()
    {
        var original = TestDocx.Create(
            TestDocx.P("Intro") +
            "<w:p>" + T("Lead ") + Begin() + Instr(" TOC \\o \"1-3\" ") + Sep + T("Entry one") + "</w:p>" +
            "<w:p>" + T("Entry two") + "</w:p>" +
            "<w:p>" + End + T("Following text") + "</w:p>");
        var doc = Docx.Load(original);
        Assert.Equal(2, doc.Body.Blocks.Count);
        var fb = Assert.IsType<FieldBlock>(doc.Body.Blocks[1]);
        Assert.Equal(3, fb.Blocks.Count);
        Assert.Equal(1, fb.BeginAt); // after "Lead "
        Assert.Equal(0, fb.EndAt);
        Assert.Equal("Lead Entry one", ((Paragraph)fb.Blocks[0]).PlainText());
        Assert.Equal("Following text", ((Paragraph)fb.Blocks[2]).PlainText());
        Assert.Empty(doc.Warnings);

        var saved = Docx.Save(doc);
        Docx.AssertValid(saved);
        var before = Docx.Body(original).Elements(W + "p").ToList();
        var after = Docx.Body(saved).Elements(W + "p").ToList();
        Assert.Equal(before.Count, after.Count);
        for (var i = 0; i < before.Count; i++) Assert.True(Docx.SameXml(before[i], after[i]), after[i].ToString());
    }

    [Fact]
    public void FieldWhoseCodeSpansParagraphs_IsKeptExactly()
    {
        var original = TestDocx.Create(
            "<w:p>" + Begin() + Instr(" IF 1 = 1 \"first") + "</w:p>" +
            "<w:p>" + Instr("second\" \"\" ") + Sep + T("first") + "</w:p>" +
            "<w:p>" + T("second") + End + "</w:p>" + TestDocx.P("After"));
        var doc = Docx.Load(original);
        var opaque = Assert.IsType<OpaqueBlock>(doc.Body.Blocks[0]);
        Assert.Equal("field", opaque.Kind);
        Assert.Equal(3, opaque.Display!.Count);
        Assert.Equal("second", ((Paragraph)opaque.Display[2]).PlainText());
        Assert.Contains(doc.Preserved, p => p.Category == "field");
        var saved = Docx.Save(doc);
        Docx.AssertValid(saved);
        var before = Docx.Body(original).Elements(W + "p").ToList();
        var after = Docx.Body(saved).Elements(W + "p").ToList();
        Assert.Equal(4, after.Count);
        for (var i = 0; i < 4; i++) Assert.True(Docx.SameXml(before[i], after[i]));
    }

    [Fact]
    public void FieldThatBeginsInACellAndEndsAfterTheTable_KeepsItsTextWithoutTheField()
    {
        var original = TestDocx.Create(
            "<w:tbl><w:tblPr/><w:tblGrid><w:gridCol w:w=\"4000\"/></w:tblGrid><w:tr><w:tc><w:p>" + Begin() + Instr(" TOC ") + Sep + T("in the cell") + "</w:p></w:tc></w:tr></w:tbl>" +
            "<w:p>" + T("after the table") + End + "</w:p>");
        var doc = Docx.Load(original);
        Assert.NotEmpty(doc.Warnings);
        var table = Assert.IsType<Table>(doc.Body.Blocks[0]);
        Assert.Equal("in the cell", table.Rows[0].Cells[0].Paragraphs().Single().PlainText());
        Assert.Equal("after the table", ((Paragraph)doc.Body.Blocks[1]).PlainText());
        var saved = Docx.Save(doc);
        Docx.AssertValid(saved);
        Assert.Empty(Docx.Body(saved).Descendants(W + "fldChar"));
    }

    [Fact]
    public void StrayFieldCharacters_AreDropped()
    {
        var original = TestDocx.Create("<w:p>" + T("a") + End + T("b") + Sep + Instr(" PAGE ") + T("c") + "</w:p>");
        var doc = Docx.Load(original);
        Assert.Equal("abc", ((Paragraph)doc.Body.Blocks[0]).PlainText());
        Assert.NotEmpty(doc.Warnings);
        var saved = Docx.Save(doc);
        Docx.AssertValid(saved);
        Assert.Empty(Docx.Body(saved).Descendants(W + "fldChar"));
    }

    [Fact]
    public void Citation_InAContentControl_IsAFieldThatKeepsItsControl()
    {
        var original = TestDocx.Create("<w:p>" + T("As shown ") +
            "<w:sdt><w:sdtPr><w:id w:val=\"12\"/><w:citation/></w:sdtPr><w:sdtContent>" + Begin() + Instr(" CITATION Lov43 \\l 1033 ") + Sep + T("(Lovelace, 1843)") + End + "</w:sdtContent></w:sdt>" +
            T(".") + "</w:p>");
        var doc = Docx.Load(original);
        var p = (Paragraph)doc.Body.Blocks[0];
        var citation = Assert.IsType<FieldInline>(p.Inlines[1]);
        Assert.Equal(FieldKind.Citation, citation.Kind);
        Assert.NotNull(citation.SdtProperties);
        Assert.Equal("As shown (Lovelace, 1843).", p.PlainText());
        Assert.DoesNotContain(doc.Preserved, f => f.Category == "contentControl");

        var saved = Docx.Save(doc);
        Docx.AssertValid(saved);
        var sdt = Docx.Body(saved).Descendants(W + "sdt").Single();
        Assert.NotNull(sdt.Element(W + "sdtPr")!.Element(W + "citation"));
        Assert.Contains("CITATION Lov43", sdt.Element(W + "sdtContent")!.Descendants(W + "instrText").Single().Value);
    }

    [Fact]
    public void FieldInAHyperlink_StaysInOneHyperlink()
    {
        var original = TestDocx.Create("<w:p><w:hyperlink w:anchor=\"x\" w:history=\"1\">" + T("See page ") + Begin() + Instr(" PAGEREF x \\h ") + Sep + T("3") + End + "</w:hyperlink></w:p>");
        var doc = Docx.Load(original);
        var saved = Docx.Save(doc);
        Docx.AssertValid(saved);
        var p = Docx.Body(saved).Element(W + "p")!;
        Assert.Single(p.Elements(W + "hyperlink"));
        Assert.Empty(p.Elements(W + "r"));
    }
}

internal static class CellExtensions
{
    public static IEnumerable<Paragraph> Paragraphs(this TableCell cell) => DocumentWalker.Paragraphs(cell.Blocks);
}
