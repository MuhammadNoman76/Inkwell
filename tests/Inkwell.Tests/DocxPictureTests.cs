using System.Xml.Linq;
using Inkwell.Core.IO;
using Inkwell.Core.Model;

namespace Inkwell.Tests;

public class DocxPictureTests
{
    private static readonly XNamespace W = Ns.W;
    private static readonly XNamespace WP = Ns.WP;

    private static DrawingObject Picture(Document doc, byte[] png, long cx = 1828800, long cy = 1371600)
    {
        var id = doc.Media.Add(png, "image/png", "png");
        return new DrawingObject { MediaId = id, Width = cx, Height = cy, Name = "Picture", AltText = "A test picture", Title = "Test" };
    }

    [Fact]
    public void InlinePicture_InANewDocument_RoundTrips()
    {
        var doc = new Document();
        var png = TestImages.Png();
        var p = new Paragraph("Before ");
        p.Inlines.Add(Picture(doc, png));
        p.Inlines.Add(new TextRun(" after"));
        doc.Body.Blocks.Add(p);

        var back = Docx.RoundTrip(doc, out var bytes);
        var d = Assert.IsType<DrawingObject>(((Paragraph)back.Body.Blocks[0]).Inlines[1]);
        Assert.True(d.IsPicture);
        Assert.Equal(WrapMode.Inline, d.Wrap);
        Assert.Equal(1828800, d.Width);
        Assert.Equal(1371600, d.Height);
        Assert.Equal("A test picture", d.AltText);
        Assert.Equal("Test", d.Title);
        Assert.True(back.Media.Get(d.MediaId)!.Data.SequenceEqual(png));
        Assert.Equal("image/png", back.Media.Get(d.MediaId)!.ContentType);
        Assert.NotNull(d.Original);
        Assert.Single(Docx.PartNames(bytes), n => n.EndsWith(".png", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(WrapMode.Square)]
    [InlineData(WrapMode.Tight)]
    [InlineData(WrapMode.TopAndBottom)]
    [InlineData(WrapMode.Behind)]
    [InlineData(WrapMode.InFront)]
    public void FloatingPicture_EveryWrapMode_RoundTrips(WrapMode wrap)
    {
        var doc = new Document();
        var p = new Paragraph("Text around the picture.");
        var d = Picture(doc, TestImages.Png(20, 20));
        d.Wrap = wrap;
        d.Float = new FloatInfo
        {
            Horizontal = new FloatPosition { RelativeFrom = "margin", Align = "right" },
            Vertical = new FloatPosition { RelativeFrom = "paragraph", Offset = 127000 },
            DistTop = 1000, DistBottom = 2000, DistLeft = 3000, DistRight = 4000,
            WrapSide = "largest",
            ZOrder = 251660000,
            Locked = true,
            AllowOverlap = false,
            LayoutInCell = true,
        };
        p.Inlines.Insert(0, d);
        doc.Body.Blocks.Add(p);

        var back = Docx.RoundTrip(doc, out var bytes);
        var bd = (DrawingObject)((Paragraph)back.Body.Blocks[0]).Inlines[0];
        Assert.Equal(wrap, bd.Wrap);
        Assert.True(bd.IsFloating);
        var f = bd.Float!;
        Assert.Equal("margin", f.Horizontal.RelativeFrom);
        Assert.Equal("right", f.Horizontal.Align);
        Assert.Equal("paragraph", f.Vertical.RelativeFrom);
        Assert.Equal(127000, f.Vertical.Offset);
        Assert.Null(f.Vertical.Align);
        Assert.Equal((1000L, 2000L, 3000L, 4000L), (f.DistTop, f.DistBottom, f.DistLeft, f.DistRight));
        Assert.Equal(251660000, f.ZOrder);
        Assert.True(f.Locked);
        Assert.False(f.AllowOverlap);
        if (wrap is WrapMode.Square or WrapMode.Tight) Assert.Equal("largest", f.WrapSide);
        var anchor = Docx.Body(bytes).Descendants(WP + "anchor").Single();
        Assert.Equal(wrap == WrapMode.Behind ? "1" : "0", (string?)anchor.Attribute("behindDoc"));
    }

    [Fact]
    public void PictureFromWord_KeepsEffectsAndCropWhenResizedAndWrapped()
    {
        const string effects = "<a:effectLst><a:outerShdw blurRad=\"50800\" dist=\"38100\" dir=\"2700000\" algn=\"tl\" rotWithShape=\"0\"><a:prstClr val=\"black\"><a:alpha val=\"40000\"/></a:prstClr></a:outerShdw></a:effectLst>";
        const string crop = "<a:srcRect l=\"5000\" t=\"6000\" r=\"7000\" b=\"8000\"/>";
        var original = TestDocx.Create("<w:p><w:r>" + TestDocx.InlinePicture("rIdImg", 7, extraSpPr: effects, blipFillExtra: crop) + "</w:r></w:p>",
            (_, main) => TestDocx.AddImage(main, "rIdImg", TestImages.Png()));
        var doc = Docx.Load(original);
        var d = (DrawingObject)((Paragraph)doc.Body.Blocks[0]).Inlines[0];
        Assert.True(d.IsPicture);
        d.Width = 2743200;
        d.Height = 1828800;
        d.Wrap = WrapMode.Square;
        d.Float = new FloatInfo { Horizontal = new FloatPosition { RelativeFrom = "column", Align = "center" } };
        d.AltText = "New description";

        var saved = Docx.Save(doc);
        Docx.AssertValid(saved);
        var anchor = Docx.Body(saved).Descendants(WP + "anchor").Single();
        Assert.Single(anchor.Descendants(Ns.A + "outerShdw"));
        Assert.Equal("5000", (string?)anchor.Descendants(Ns.A + "srcRect").Single().Attribute("l"));
        Assert.Equal("2743200", (string?)anchor.Element(WP + "extent")!.Attribute("cx"));
        Assert.Equal("2743200", (string?)anchor.Descendants(Ns.A + "ext").Last().Attribute("cx"));
        Assert.Equal("New description", (string?)anchor.Element(WP + "docPr")!.Attribute("descr"));
        Assert.Equal("7", (string?)anchor.Element(WP + "docPr")!.Attribute("id"));
        Assert.NotNull(anchor.Element(WP + "wrapSquare"));

        // and back to inline
        var again = Docx.Load(saved);
        var d2 = (DrawingObject)((Paragraph)again.Body.Blocks[0]).Inlines[0];
        Assert.Equal(WrapMode.Square, d2.Wrap);
        d2.Wrap = WrapMode.Inline;
        var inlineAgain = Docx.Save(again);
        Docx.AssertValid(inlineAgain);
        var inline = Docx.Body(inlineAgain).Descendants(WP + "inline").Single();
        Assert.Single(inline.Descendants(Ns.A + "outerShdw"));
        Assert.Empty(Docx.Body(inlineAgain).Descendants(WP + "anchor"));
    }

    [Fact]
    public void DrawingIds_AreUniqueAcrossTheDocument()
    {
        var doc = new Document();
        var png = TestImages.Png();
        var hf = new HeaderFooter(true) { RelId = doc.NewRelId() };
        var hp = new Paragraph();
        hp.Inlines.Add(Picture(doc, png));
        hf.Blocks.Add(hp);
        doc.HeadersFooters[hf.RelId] = hf;
        doc.Body.FinalSection.SetReference(true, "default", hf.RelId);
        for (var i = 0; i < 3; i++)
        {
            var p = new Paragraph();
            p.Inlines.Add(Picture(doc, png));
            doc.Body.Blocks.Add(p);
        }
        var bytes = Docx.Save(doc);
        Docx.AssertValid(bytes);
        var header = Docx.PartNames(bytes).Single(n => n.StartsWith("word/header", StringComparison.Ordinal));
        var ids = Docx.Body(bytes).Descendants(WP + "docPr").Concat(Docx.PartXml(bytes, header).Descendants(WP + "docPr"))
            .Select(e => (string?)e.Attribute("id")).ToList();
        Assert.Equal(4, ids.Count);
        Assert.Equal(4, ids.Distinct().Count());
        Assert.DoesNotContain("0", ids);
        // The same bytes in one part are stored once; the header has its own copy.
        Assert.Equal(2, Docx.PartNames(bytes).Count(n => n.EndsWith(".png", StringComparison.Ordinal)));
    }

    [Fact]
    public void PictureRemoved_ItsImagePartGoes_OtherPartsStay()
    {
        var original = TestDocx.Create("<w:p><w:r>" + TestDocx.InlinePicture("rIdA", 1) + "</w:r></w:p><w:p><w:r>" + TestDocx.InlinePicture("rIdB", 2) + "</w:r></w:p>",
            (_, main) =>
            {
                TestDocx.AddImage(main, "rIdA", TestImages.Png(10, 10, 255, 0, 0));
                TestDocx.AddImage(main, "rIdB", TestImages.Png(10, 10, 0, 0, 255));
            });
        Assert.Equal(2, Docx.PartNames(original).Count(n => n.EndsWith(".png", StringComparison.Ordinal)));
        var doc = Docx.Load(original);
        doc.Body.Blocks.RemoveAt(0);
        var saved = Docx.Save(doc);
        Docx.AssertValid(saved);
        Assert.Single(Docx.PartNames(saved), n => n.EndsWith(".png", StringComparison.Ordinal));
        var back = Docx.Load(saved);
        var d = (DrawingObject)((Paragraph)back.Body.Blocks[0]).Inlines[0];
        Assert.True(back.Media.Get(d.MediaId)!.Data.SequenceEqual(TestImages.Png(10, 10, 0, 0, 255)));
    }

    [Fact]
    public void PictureCopiedIntoAnotherPart_GetsItsOwnImagePart()
    {
        var original = TestDocx.Create("<w:p><w:r>" + TestDocx.InlinePicture("rIdImg", 1) + "</w:r></w:p>",
            (_, main) => TestDocx.AddImage(main, "rIdImg", TestImages.Png()));
        var doc = Docx.Load(original);
        var picture = (DrawingObject)((Paragraph)doc.Body.Blocks[0]).Inlines[0];
        var note = new Note(NoteKind.Footnote) { Id = 1 };
        var np = new Paragraph();
        np.Inlines.Add(picture.Clone());
        note.Blocks.Add(np);
        doc.Footnotes[1] = note;
        ((Paragraph)doc.Body.Blocks[0]).Inlines.Add(new NoteReference { Kind = NoteKind.Footnote, Id = 1 });
        var saved = Docx.Save(doc);
        Docx.AssertValid(saved);
        var rels = Docx.PartXml(saved, "word/_rels/footnotes.xml.rels").Root!.Elements().ToList();
        var image = Assert.Single(rels, r => ((string)r.Attribute("Type")!).EndsWith("/image", StringComparison.Ordinal));
        var blip = Docx.PartXml(saved, "word/footnotes.xml").Descendants(Ns.A + "blip").Single();
        Assert.Equal((string?)image.Attribute("Id"), (string?)blip.Attribute(Ns.R + "embed"));
    }

    [Fact]
    public void LinkedPicture_IsKeptWithItsExternalRelationship()
    {
        var original = TestDocx.Create("<w:p><w:r>" + TestDocx.InlinePicture("x", 1).Replace("r:embed=\"x\"", "r:link=\"rIdLink\"") + "</w:r></w:p>",
            (_, m) => m.AddExternalRelationship("http://schemas.openxmlformats.org/officeDocument/2006/relationships/image", new Uri("file:///C:/pictures/photo.png"), "rIdLink"));
        var doc = Docx.Load(original);
        var d = (DrawingObject)((Paragraph)doc.Body.Blocks[0]).Inlines[0];
        Assert.Equal("linkedpicture", d.Kind);
        Assert.False(d.IsPicture);
        Assert.Contains(doc.Preserved, p => p.Category == "linkedPicture");
        var saved = Docx.Save(doc);
        Docx.AssertValid(saved);
        Assert.Contains(Docx.PartXml(saved, "word/_rels/document.xml.rels").Root!.Elements(), e => (string?)e.Attribute("Target") == "file:///C:/pictures/photo.png");
    }
}
