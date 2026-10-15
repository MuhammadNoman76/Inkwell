using System.Xml.Linq;
using Inkwell.Core.IO;
using Inkwell.Core.Model;

namespace Inkwell.Tests;

public class DocxRoundTripTests
{
    private static readonly XNamespace W = Ns.W;

    [Fact]
    public void MoveRanges_KeepTheirStartsAndEndsPaired()
    {
        const string date = "w:date=\"2026-01-01T00:00:00Z\"";
        var original = TestDocx.Create(
            "<w:p><w:moveFromRangeStart w:id=\"10\" w:author=\"A\" " + date + " w:name=\"move1\"/><w:moveFrom w:id=\"11\" w:author=\"A\" " + date + "><w:r><w:t>moved</w:t></w:r></w:moveFrom><w:moveFromRangeEnd w:id=\"10\"/></w:p>" +
            "<w:p><w:moveToRangeStart w:id=\"10\" w:author=\"A\" " + date + " w:name=\"move1\"/><w:moveTo w:id=\"13\" w:author=\"A\" " + date + "><w:r><w:t>moved</w:t></w:r></w:moveTo><w:moveToRangeEnd w:id=\"10\"/></w:p>");
        var doc = Docx.Load(original);
        var p0 = (Paragraph)doc.Body.Blocks[0];
        Assert.Equal("moveRange", Assert.IsType<OpaqueInline>(p0.Inlines[0]).Kind);
        Assert.True(((OpaqueInline)p0.Inlines[0]).IsMarker);
        Assert.True(p0.Inlines[1].IsMove);
        Assert.NotNull(p0.Inlines[1].Deleted);

        var saved = Docx.Save(doc);
        Docx.AssertValid(saved);
        var body = Docx.Body(saved);
        string? Id(string name) => (string?)body.Descendants(W + name).Single().Attribute(W + "id");
        Assert.Equal(Id("moveFromRangeStart"), Id("moveFromRangeEnd"));
        Assert.Equal(Id("moveToRangeStart"), Id("moveToRangeEnd"));
        Assert.NotEqual(Id("moveFromRangeStart"), Id("moveToRangeStart"));
        var all = body.Descendants().Where(e => e.Name.LocalName is "moveFrom" or "moveTo" or "moveFromRangeStart" or "moveToRangeStart").Select(e => (string?)e.Attribute(W + "id")).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Fact]
    public void MarkersInsideAHyperlinkOrRevision_DoNotSplitIt()
    {
        var doc = new Document();
        var link = new Hyperlink { Url = "https://example.com/" };
        var when = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc);
        var p = new Paragraph();
        p.Inlines.Add(new TextRun("one ") { Link = link });
        p.Inlines.Add(new BookmarkMarker { Id = 1, Name = "Mid", IsStart = true });
        p.Inlines.Add(new CommentMarker { Id = 0, Kind = CommentMarkerKind.RangeStart });
        p.Inlines.Add(new TextRun("two") { Link = link });
        p.Inlines.Add(new BookmarkMarker { Id = 1, IsStart = false });
        p.Inlines.Add(new TextRun(" inserted") { Inserted = new RevisionInfo { Author = "A", Date = when } });
        p.Inlines.Add(new BookmarkMarker { Id = 2, Name = "InIns", IsStart = true });
        p.Inlines.Add(new TextRun(" more") { Inserted = new RevisionInfo { Author = "A", Date = when } });
        p.Inlines.Add(new BookmarkMarker { Id = 2, IsStart = false });
        p.Inlines.Add(new CommentMarker { Id = 0, Kind = CommentMarkerKind.RangeEnd });
        p.Inlines.Add(new CommentMarker { Id = 0, Kind = CommentMarkerKind.Reference });
        doc.Body.Blocks.Add(p);
        var comment = new Comment { Id = 0, Author = "A" };
        comment.Blocks.Add(new Paragraph("Note"));
        doc.Comments.Add(comment);

        var back = Docx.RoundTrip(doc, out var bytes);
        var xp = Docx.Body(bytes).Element(W + "p")!;
        var hyperlink = Assert.Single(xp.Elements(W + "hyperlink"));
        Assert.Equal(2, hyperlink.Elements(W + "r").Count());
        Assert.Single(hyperlink.Elements(W + "bookmarkStart"));
        var ins = Assert.Single(xp.Elements(W + "ins"));
        Assert.Equal(2, ins.Elements(W + "r").Count());
        Assert.Single(ins.Elements(W + "bookmarkStart"));
        Assert.Equal("one two inserted more", ((Paragraph)back.Body.Blocks[0]).PlainText());
    }

    /// <summary>A document using most of the model, built in code.</summary>
    private static Document KitchenSink()
    {
        var doc = new Document();
        doc.Properties.Title = "Kitchen sink";
        var png = TestImages.Png(30, 20);
        var media = doc.Media.Add(png, "image/png");

        var header = new HeaderFooter(true) { RelId = doc.NewRelId() };
        header.Blocks.Add(new Paragraph("Header", "Header"));
        doc.HeadersFooters[header.RelId] = header;
        var footer = new HeaderFooter(false) { RelId = doc.NewRelId() };
        var fp = new Paragraph("Page ", "Footer");
        fp.Inlines.Add(new FieldInline { Code = " PAGE ", Result = { new TextRun("1") } });
        footer.Blocks.Add(fp);
        doc.HeadersFooters[footer.RelId] = footer;
        doc.Body.FinalSection.SetReference(true, "default", header.RelId);
        doc.Body.FinalSection.SetReference(false, "default", footer.RelId);

        var toc = new SdtBlock();
        toc.Properties.Add(new XElement(W + "docPartObj", new XElement(W + "docPartGallery", new XAttribute(W + "val", "Table of Contents")), new XElement(W + "docPartUnique")));
        var tocField = new FieldBlock { Code = " TOC \\o \"1-3\" \\h \\z \\u " };
        tocField.Blocks.Add(new Paragraph("Introduction\t1", "TOC1"));
        tocField.Blocks.Add(new Paragraph()); // Word ends a table of contents in a paragraph of its own
        toc.Blocks.Add(tocField);
        doc.Body.Blocks.Add(toc);

        var heading = new Paragraph("Introduction", "Heading1");
        heading.Inlines.Insert(0, new BookmarkMarker { Id = 0, Name = "_Toc1", IsStart = true });
        heading.Inlines.Add(new BookmarkMarker { Id = 0, IsStart = false });
        doc.Body.Blocks.Add(heading);

        var abs = doc.Numbering.AddAbstract(Numbering.StandardDefinition(false));
        var num = doc.Numbering.AddNum(abs);
        for (var i = 0; i < 3; i++)
        {
            var item = new Paragraph("Item " + i, "ListParagraph");
            item.Props.SetNumbering(num, 0);
            doc.Body.Blocks.Add(item);
        }

        var p = new Paragraph("Text with a picture ");
        p.Inlines.Add(new DrawingObject { MediaId = media, Width = 914400, Height = 609600, AltText = "Picture" });
        p.Inlines.Add(new TextRun(", a floating one"));
        p.Inlines.Add(new DrawingObject { MediaId = media, Width = 457200, Height = 304800, Wrap = WrapMode.Square, Float = new FloatInfo() });
        p.Inlines.Add(new TextRun(", a note"));
        p.Inlines.Add(new NoteReference { Kind = NoteKind.Footnote, Id = 1 });
        p.Inlines.Add(new TextRun(" and a link") { Link = new Hyperlink { Url = "https://example.com/a?b=c" } });
        p.Inlines.Add(new CommentMarker { Id = 0, Kind = CommentMarkerKind.Reference });
        doc.Body.Blocks.Add(p);

        var note = new Note(NoteKind.Footnote) { Id = 1 };
        note.Blocks.Add(new Paragraph("The note.", "FootnoteText"));
        doc.Footnotes[1] = note;
        var comment = new Comment { Id = 0, Author = "Ada", Date = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        comment.Blocks.Add(new Paragraph("A comment."));
        doc.Comments.Add(comment);
        var reply = new Comment { Id = 1, Author = "Grace", ParentId = 0, Done = true };
        reply.Blocks.Add(new Paragraph("A reply."));
        doc.Comments.Add(reply);

        var t = new Table { Grid = new List<int> { 3000, 3000 } };
        var row = new TableRow();
        row.AddCell(TableCell.Create(3000, new Paragraph("a")));
        row.AddCell(TableCell.Create(3000, new Paragraph("b")));
        t.AddRow(row);
        doc.Body.Blocks.Add(t);
        var changed = new Paragraph();
        changed.Inlines.Add(new TextRun("added") { Inserted = new RevisionInfo { Author = "A", Date = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) } });
        changed.Inlines.Add(new TextRun("removed") { Deleted = new RevisionInfo { Author = "A", Date = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) } });
        doc.Body.Blocks.Add(changed);
        doc.Sources.Items.Add(new BibSource { Tag = "X1", Title = "A book", Year = "2020", Authors = { SourcePerson.Parse("Ada Lovelace") } });
        return doc;
    }

    [Fact]
    public void KitchenSink_IsValid_AndStableAcrossSaves()
    {
        var options = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var first = Docx.Save(KitchenSink(), new DocxSaveOptions { Now = options });
        Docx.AssertValid(first);
        var second = Docx.Save(Docx.Load(first), new DocxSaveOptions { Now = options });
        var third = Docx.Save(Docx.Load(second), new DocxSaveOptions { Now = options });
        Docx.AssertValid(third);
        Assert.Equal(Docx.PartNames(second).OrderBy(n => n), Docx.PartNames(third).OrderBy(n => n));
        foreach (var part in Docx.PartNames(second).Where(n => n.EndsWith(".xml", StringComparison.Ordinal) || n.EndsWith(".rels", StringComparison.Ordinal)))
            Assert.Equal(Docx.PartXml(second, part).ToString(), Docx.PartXml(third, part).ToString());

        var doc = Docx.Load(third);
        Assert.Equal("Kitchen sink", doc.Properties.Title);
        Assert.Equal(2, doc.HeadersFooters.Count);
        Assert.IsType<FieldBlock>(((SdtBlock)doc.Body.Blocks[0]).Blocks[0]);
        Assert.Equal(2, doc.Comments.Count);
        Assert.Equal(0, doc.FindComment(1)!.ParentId);
        Assert.True(doc.FindComment(1)!.Done);
        Assert.Equal(3, doc.Footnotes.Count);
        Assert.Single(doc.Sources.Items);
        Assert.Equal(2, doc.Body.Paragraphs.SelectMany(x => x.Inlines).OfType<DrawingObject>().Count(d => d.IsPicture));
        Assert.Empty(doc.Warnings);
        Assert.Empty(doc.Preserved);
    }
}
