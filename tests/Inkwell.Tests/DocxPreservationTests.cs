using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Inkwell.Core.IO;
using Inkwell.Core.Model;

namespace Inkwell.Tests;

public class DocxPreservationTests
{
    private static readonly XNamespace W = Ns.W;

    private static byte[] Bytes(int count, int seed)
    {
        var data = new byte[count];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static readonly System.Text.RegularExpressions.Regex Owned = new(
        @"^(\[Content_Types\]\.xml|.*\.rels|word/(document|styles|settings|numbering|fontTable|footnotes|endnotes|comments|commentsExtended|commentsIds|commentsExtensible|header\d*|footer\d*)\.xml|word/theme/theme\d*\.xml|docProps/(core|app)\.xml)$");

    /// <summary>Every part Inkwell doesn't rewrite is identical after saving.</summary>
    private static void AssertOtherPartsUnchanged(byte[] before, byte[] after)
    {
        var names = Docx.PartNames(before).Where(n => !Owned.IsMatch(n)).ToList();
        Assert.NotEmpty(names);
        foreach (var name in names) AssertSamePart(before, after, name);
    }

    private static void AssertSamePart(byte[] before, byte[] after, string name)
    {
        var a = Docx.PartBytes(before, name);
        var b = Docx.PartBytes(after, name);
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.True(a!.SequenceEqual(b!), name + " changed");
    }

    // ------------------------------------------------------------------ macros

    private static byte[] MacroDocument(out byte[] vba)
    {
        var project = Bytes(4096, 1);
        vba = project;
        return TestDocx.Create(TestDocx.P("Has macros"), (_, main) =>
        {
            var part = main.AddNewPart<VbaProjectPart>("rIdVba");
            TestDocx.Write(part, project);
            var data = part.AddNewPart<VbaDataPart>("rIdVbaData");
            TestDocx.Write(data, "<wne:vbaSuppData xmlns:wne=\"http://schemas.microsoft.com/office/word/2006/wordml\"><wne:mcds/></wne:vbaSuppData>");
        }, WordprocessingDocumentType.MacroEnabledDocument);
    }

    [Fact]
    public void Docm_KeepsItsVbaProjectByteForByte()
    {
        var original = MacroDocument(out var vba);
        var doc = Docx.Load(original);
        Assert.True(doc.Source!.HasMacros);
        Assert.Contains(doc.Preserved, p => p.Category == "macros");

        ((Paragraph)doc.Body.Blocks[0]).Inlines.Add(new TextRun(" and an edit"));
        var saved = Docx.Save(doc);
        Docx.AssertValid(saved, n => n.Contains("vbaData"));
        Assert.Equal(DocxWriter.ContentType(DocxFormat.MacroEnabledDocument), Docx.ContentTypeOf(saved, "/word/document.xml"));
        Assert.True(Docx.PartBytes(saved, "word/vbaProject.bin")!.SequenceEqual(vba));
        AssertOtherPartsUnchanged(original, saved);
        Assert.Equal("Has macros and an edit", Docx.Text(Docx.Load(saved)));
    }

    [Fact]
    public void DropMacros_RemovesTheVbaProjectAndSwitchesToDocx()
    {
        var doc = Docx.Load(MacroDocument(out _));
        var options = new DocxSaveOptions { DropMacros = true };
        var saved = Docx.Save(doc, options);
        Docx.AssertValid(saved);
        Assert.DoesNotContain(Docx.PartNames(saved), n => n.Contains("vba", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(DocxWriter.ContentType(DocxFormat.Document), Docx.ContentTypeOf(saved, "/word/document.xml"));
        Assert.False(doc.Source!.HasMacros);
        Assert.Empty(options.Warnings);
    }

    [Fact]
    public void SavingADocmAsDocx_DropsTheMacrosWithAWarning()
    {
        var doc = Docx.Load(MacroDocument(out _));
        using var dir = new TempDir();
        var options = new DocxSaveOptions();
        var bytes = DocxWriter.Save(doc, dir.File("copy.docx"), options);
        Docx.AssertValid(bytes);
        Assert.Equal(DocxWriter.ContentType(DocxFormat.Document), Docx.ContentTypeOf(bytes, "/word/document.xml"));
        Assert.Single(options.Warnings);
        // …and a .docm path keeps a macro-enabled type.
        var docm = DocxWriter.Save(Docx.Load(MacroDocument(out _)), dir.File("copy.docm"));
        Assert.Equal(DocxWriter.ContentType(DocxFormat.MacroEnabledDocument), Docx.ContentTypeOf(docm, "/word/document.xml"));
    }

    // ------------------------------------------------------------------ charts and SmartArt

    private const string ChartXml =
        "<c:chartSpace xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><c:chart><c:plotArea><c:layout/><c:barChart><c:barDir val=\"col\"/>" +
        "<c:grouping val=\"clustered\"/><c:varyColors val=\"0\"/><c:ser><c:idx val=\"0\"/><c:order val=\"0\"/><c:val><c:numLit><c:formatCode>General</c:formatCode>" +
        "<c:ptCount val=\"2\"/><c:pt idx=\"0\"><c:v>1</c:v></c:pt><c:pt idx=\"1\"><c:v>2</c:v></c:pt></c:numLit></c:val></c:ser><c:axId val=\"1\"/><c:axId val=\"2\"/></c:barChart>" +
        "<c:catAx><c:axId val=\"1\"/><c:scaling><c:orientation val=\"minMax\"/></c:scaling><c:delete val=\"0\"/><c:axPos val=\"b\"/><c:crossAx val=\"2\"/></c:catAx>" +
        "<c:valAx><c:axId val=\"2\"/><c:scaling><c:orientation val=\"minMax\"/></c:scaling><c:delete val=\"0\"/><c:axPos val=\"l\"/><c:crossAx val=\"1\"/></c:valAx>" +
        "</c:plotArea></c:chart></c:chartSpace>";

    private static string GraphicFrame(int id, string name, string uri, string data) =>
        "<w:r><w:drawing><wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\"><wp:extent cx=\"5486400\" cy=\"3200400\"/><wp:effectExtent l=\"0\" t=\"0\" r=\"0\" b=\"0\"/>" +
        "<wp:docPr id=\"" + id + "\" name=\"" + name + "\"/><wp:cNvGraphicFramePr/><a:graphic><a:graphicData uri=\"" + uri + "\">" + data + "</a:graphicData></a:graphic></wp:inline></w:drawing></w:r>";

    private static string ChartFrame(int id, string relId) =>
        GraphicFrame(id, "Chart " + id, "http://schemas.openxmlformats.org/drawingml/2006/chart",
            "<c:chart xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" r:id=\"" + relId + "\"/>");

    private static byte[] ChartsAndSmartArt()
    {
        var smartArt = GraphicFrame(3, "Diagram 3", "http://schemas.openxmlformats.org/drawingml/2006/diagram",
            "<dgm:relIds xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\" r:dm=\"rIdDm\" r:lo=\"rIdLo\" r:qs=\"rIdQs\" r:cs=\"rIdCs\"/>");
        return TestDocx.Create("<w:p>" + ChartFrame(1, "rIdChart1") + "</w:p>" + TestDocx.P("Between") + "<w:p>" + ChartFrame(2, "rIdChart2") + "</w:p><w:p>" + smartArt + "</w:p>", (_, main) =>
        {
            foreach (var id in new[] { "rIdChart1", "rIdChart2" })
            {
                var chart = main.AddNewPart<ChartPart>(id);
                TestDocx.Write(chart, ChartXml);
                var embedded = chart.AddNewPart<EmbeddedPackagePart>("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "rIdXlsx");
                TestDocx.Write(embedded, Bytes(2000, id.Length));
            }
            const string dgm = "xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"";
            TestDocx.Write(main.AddNewPart<DiagramDataPart>("rIdDm"), "<dgm:dataModel " + dgm + "><dgm:ptLst><dgm:pt modelId=\"{00000000-0000-0000-0000-000000000001}\" type=\"doc\"/></dgm:ptLst><dgm:cxnLst/><dgm:bg/><dgm:whole/></dgm:dataModel>");
            TestDocx.Write(main.AddNewPart<DiagramLayoutDefinitionPart>("rIdLo"), "<dgm:layoutDef " + dgm + "><dgm:layoutNode/></dgm:layoutDef>");
            TestDocx.Write(main.AddNewPart<DiagramStylePart>("rIdQs"), "<dgm:styleDef " + dgm + "/>");
            TestDocx.Write(main.AddNewPart<DiagramColorsPart>("rIdCs"), "<dgm:colorsDef " + dgm + "/>");
        });
    }

    private static bool Fabricated(string uri) => uri.Contains("/charts/") || uri.Contains("/diagrams/") || uri.Contains("/graphics/") || uri.Contains("/embeddings/");

    [Fact]
    public void ChartsAndSmartArt_ArePreservedByteForByte_AndListedAsKept()
    {
        var original = ChartsAndSmartArt();
        var doc = Docx.Load(original);
        var drawings = doc.Body.Paragraphs.SelectMany(p => p.Inlines).OfType<DrawingObject>().ToList();
        Assert.Equal(new[] { "chart", "chart", "smartart" }, drawings.Select(d => d.Kind).ToArray());
        Assert.All(drawings, d => Assert.False(d.IsPicture));
        Assert.Contains(doc.Preserved, p => p.Category == "chart" && p.Count == 2 && p.Description == "2 charts");
        Assert.Contains(doc.Preserved, p => p.Category == "smartart" && p.Description == "1 SmartArt graphic");

        ((Paragraph)doc.Body.Blocks[1]).Inlines.Add(new TextRun(" (edited)"));
        var saved = Docx.Save(doc);
        Docx.AssertValid(saved, Fabricated);
        Assert.True(Docx.PartNames(original).Count(n => !Owned.IsMatch(n)) >= 8);
        AssertOtherPartsUnchanged(original, saved);
        var back = Docx.Load(saved);
        Assert.Equal(3, back.Body.Paragraphs.SelectMany(p => p.Inlines).OfType<DrawingObject>().Count());
        Assert.Equal("Between (edited)", ((Paragraph)back.Body.Blocks[1]).PlainText());
    }

    [Fact]
    public void ChartMovedIntoAHeader_GetsItsRelationshipRecreatedThere()
    {
        var original = ChartsAndSmartArt();
        var doc = Docx.Load(original);
        var chartParagraph = (Paragraph)doc.Body.Blocks[0];
        var chart = (DrawingObject)chartParagraph.Inlines.Single();
        chartParagraph.Inlines.Clear();
        var header = new HeaderFooter(true) { RelId = doc.NewRelId() };
        var hp = new Paragraph();
        hp.Inlines.Add(chart);
        header.Blocks.Add(hp);
        doc.HeadersFooters[header.RelId] = header;
        doc.Body.FinalSection.SetReference(true, "default", header.RelId);

        var saved = Docx.Save(doc);
        Docx.AssertValid(saved, Fabricated);
        var headerName = Docx.PartNames(saved).Single(n => n.StartsWith("word/header", StringComparison.Ordinal) && n.EndsWith(".xml", StringComparison.Ordinal));
        var rels = Docx.PartXml(saved, "word/_rels/" + headerName.Substring(5) + ".rels").Root!.Elements().ToList();
        var chartRel = Assert.Single(rels, r => ((string)r.Attribute("Type")!).EndsWith("/chart", StringComparison.Ordinal));
        var headerXml = Docx.PartXml(saved, headerName);
        Assert.Equal((string?)chartRel.Attribute("Id"), (string?)headerXml.Descendants(Ns.C + "chart").Single().Attribute(Ns.R + "id"));
        // The model follows the save: the drawing now belongs to the header part.
        Assert.Equal("/" + headerName, chart.SourcePart);
        var again = Docx.Save(doc);
        Docx.AssertValid(again, Fabricated);
    }

    // ------------------------------------------------------------------ equations, content controls, custom XML

    [Fact]
    public void Equations_AreKeptAsTheyWere()
    {
        const string inline = "<m:oMath><m:r><m:t>x=</m:t></m:r><m:f><m:num><m:r><m:t>1</m:t></m:r></m:num><m:den><m:r><m:t>2</m:t></m:r></m:den></m:f></m:oMath>";
        const string display = "<m:oMathPara><m:oMath><m:r><m:t>E=m</m:t></m:r><m:sSup><m:e><m:r><m:t>c</m:t></m:r></m:e><m:sup><m:r><m:t>2</m:t></m:r></m:sup></m:sSup></m:oMath></m:oMathPara>";
        var original = TestDocx.Create("<w:p><w:r><w:t xml:space=\"preserve\">Inline </w:t></w:r>" + inline + "<w:r><w:t xml:space=\"preserve\"> done</w:t></w:r></w:p><w:p>" + display + "</w:p>");
        var doc = Docx.Load(original);
        var p0 = (Paragraph)doc.Body.Blocks[0];
        var eq = Assert.IsType<OpaqueInline>(p0.Inlines[1]);
        Assert.Equal("equation", eq.Kind);
        Assert.Equal("x=12", eq.Display);
        Assert.Equal("Inline x=12 done", p0.PlainText());
        Assert.Contains(doc.Preserved, p => p.Category == "equation" && p.Count == 2);

        var saved = Docx.Save(doc);
        Docx.AssertValid(saved);
        var before = Docx.Body(original).Descendants(Ns.M + "oMath").ToList();
        var after = Docx.Body(saved).Descendants(Ns.M + "oMath").ToList();
        Assert.Equal(2, after.Count);
        for (var i = 0; i < 2; i++) Assert.True(Ox.SameXml(before[i], after[i]));
        Assert.Single(Docx.Body(saved).Descendants(Ns.M + "oMathPara"));
    }

    [Fact]
    public void ContentControls_AreKeptAsTheyWere()
    {
        var original = TestDocx.Create(
            "<w:p><w:r><w:t xml:space=\"preserve\">Name: </w:t></w:r><w:sdt><w:sdtPr><w:alias w:val=\"Name\"/><w:tag w:val=\"name\"/><w:id w:val=\"12345\"/><w:text/></w:sdtPr>" +
            "<w:sdtContent><w:r><w:t>Ada</w:t></w:r></w:sdtContent></w:sdt>" +
            "<w:sdt><w:sdtPr><w:id w:val=\"333\"/><w14:checkbox><w14:checked w14:val=\"1\"/><w14:checkedState w14:val=\"2612\" w14:font=\"MS Gothic\"/><w14:uncheckedState w14:val=\"2610\" w14:font=\"MS Gothic\"/></w14:checkbox></w:sdtPr>" +
            "<w:sdtContent><w:r><w:rPr><w:rFonts w:ascii=\"MS Gothic\" w:eastAsia=\"MS Gothic\" w:hAnsi=\"MS Gothic\" w:hint=\"eastAsia\"/></w:rPr><w:t>☒</w:t></w:r></w:sdtContent></w:sdt></w:p>" +
            "<w:sdt><w:sdtPr><w:alias w:val=\"Block\"/><w:id w:val=\"222\"/></w:sdtPr><w:sdtContent>" + TestDocx.P("Inside a block control") + "</w:sdtContent></w:sdt>");
        var doc = Docx.Load(original);
        var p0 = (Paragraph)doc.Body.Blocks[0];
        var name = Assert.IsType<OpaqueInline>(p0.Inlines[1]);
        Assert.Equal("contentControl", name.Kind);
        Assert.Equal("Ada", name.Display);
        Assert.Equal("☒", ((OpaqueInline)p0.Inlines[2]).Display);
        var block = Assert.IsType<SdtBlock>(doc.Body.Blocks[1]);
        Assert.False(block.IsManaged);
        Assert.NotNull(block.Original);
        Assert.Equal("Inside a block control", block.Blocks.OfType<Paragraph>().Single().PlainText());
        Assert.Contains(doc.Preserved, p => p.Category == "contentControl" && p.Count == 3);

        var saved = Docx.Save(doc);
        Docx.AssertValid(saved);
        var before = Docx.Body(original).Descendants(W + "sdt").ToList();
        var after = Docx.Body(saved).Descendants(W + "sdt").ToList();
        Assert.Equal(before.Count, after.Count);
        for (var i = 0; i < before.Count; i++) Assert.True(Ox.SameXml(before[i], after[i]), after[i].ToString());
    }

    [Fact]
    public void CustomXmlParts_GlossaryAndOtherParts_StayByteForByte()
    {
        var original = TestDocx.Create(TestDocx.P("Text"), (_, main) =>
        {
            var custom = main.AddNewPart<CustomXmlPart>("application/xml", "rIdCx");
            TestDocx.Write(custom, "<?xml version=\"1.0\" encoding=\"utf-8\"?><root xmlns=\"urn:example\"><value>42</value></root>");
            var props = custom.AddNewPart<CustomXmlPropertiesPart>("rIdCxp");
            TestDocx.Write(props, "<ds:datastoreItem ds:itemID=\"{11111111-2222-3333-4444-555555555555}\" xmlns:ds=\"http://schemas.openxmlformats.org/officeDocument/2006/customXml\"><ds:schemaRefs><ds:schemaRef ds:uri=\"urn:example\"/></ds:schemaRefs></ds:datastoreItem>");
            var web = main.AddNewPart<WebSettingsPart>("rIdWeb");
            TestDocx.Write(web, "<w:webSettings xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:optimizeForBrowser/><w:allowPNG/></w:webSettings>");
            var people = main.AddNewPart<WordprocessingPeoplePart>("rIdPeople");
            TestDocx.Write(people, "<w15:people xmlns:w15=\"http://schemas.microsoft.com/office/word/2012/wordml\"><w15:person w15:author=\"Ada\"><w15:presenceInfo w15:providerId=\"None\" w15:userId=\"Ada\"/></w15:person></w15:people>");
            var glossary = main.AddNewPart<GlossaryDocumentPart>("rIdGlossary");
            TestDocx.Write(glossary, "<w:glossaryDocument xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:docParts><w:docPart><w:docPartPr><w:name w:val=\"Saved\"/><w:category><w:name w:val=\"General\"/><w:gallery w:val=\"placeholder\"/></w:category><w:guid w:val=\"{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}\"/></w:docPartPr><w:docPartBody><w:p/></w:docPartBody></w:docPart></w:docParts></w:glossaryDocument>");
        });
        var doc = Docx.Load(original);
        Assert.Contains(doc.Preserved, p => p.Category == "customXml" && p.Count == 1);
        doc.Body.Blocks.Add(new Paragraph("More"));
        var saved = Docx.Save(doc);
        Docx.AssertValid(saved);
        Assert.Equal(5, Docx.PartNames(original).Count(n => !Owned.IsMatch(n)));
        AssertOtherPartsUnchanged(original, saved);
    }

    // ------------------------------------------------------------------ fields kept as XML

    private const string NestedIfField =
        "<w:p><w:r><w:t xml:space=\"preserve\">Dear </w:t></w:r><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> IF </w:instrText></w:r>" +
        "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> MERGEFIELD Gender </w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>" +
        "<w:r><w:t>F</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> = \"F\" \"Ms\" \"Mr\" </w:instrText></w:r>" +
        "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:rPr><w:b/></w:rPr><w:t>Ms</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r><w:r><w:t xml:space=\"preserve\"> Smith</w:t></w:r></w:p>";

    [Fact]
    public void FieldWithANestedFieldInItsCode_IsKeptExactly()
    {
        var original = TestDocx.Create(NestedIfField);
        var doc = Docx.Load(original);
        var p = (Paragraph)doc.Body.Blocks[0];
        Assert.Equal(3, p.Inlines.Count);
        var field = Assert.IsType<FieldInline>(p.Inlines[1]);
        Assert.NotNull(field.PreservedXml);
        Assert.Equal(FieldKind.If, field.Kind);
        Assert.Contains("MERGEFIELD Gender", field.Code);
        Assert.Equal("Ms", field.ResultText);
        Assert.Equal("Dear Ms Smith", p.PlainText());
        Assert.Contains(doc.Preserved, f => f.Category == "field");

        var saved = Docx.Save(doc);
        Docx.AssertValid(saved);
        var runsBefore = Docx.Body(original).Element(W + "p")!.Elements(W + "r").ToList();
        var runsAfter = Docx.Body(saved).Element(W + "p")!.Elements(W + "r").ToList();
        Assert.Equal(runsBefore.Count, runsAfter.Count);
        for (var i = 0; i < runsBefore.Count; i++) Assert.True(Docx.SameXml(runsBefore[i], runsAfter[i]), runsAfter[i].ToString());
        Assert.Empty(Docx.Body(saved).Descendants(Ns.IW + "runs"));
    }

    [Fact]
    public void FormFields_AreKeptExactly()
    {
        var original = TestDocx.Create(
            "<w:p><w:r><w:fldChar w:fldCharType=\"begin\"><w:ffData><w:name w:val=\"Text1\"/><w:enabled/><w:calcOnExit w:val=\"0\"/><w:textInput/></w:ffData></w:fldChar></w:r>" +
            "<w:r><w:instrText xml:space=\"preserve\"> FORMTEXT </w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>typed</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r></w:p>");
        var doc = Docx.Load(original);
        var field = Assert.IsType<FieldInline>(((Paragraph)doc.Body.Blocks[0]).Inlines.Single());
        Assert.NotNull(field.PreservedXml);
        Assert.Equal(FieldKind.FormText, field.Kind);
        Assert.Contains(doc.Preserved, f => f.Category == "formField" && f.Description == "1 form field");
        var saved = Docx.Save(doc);
        Docx.AssertValid(saved);
        Assert.Single(Docx.Body(saved).Descendants(W + "ffData"));
    }

    // ------------------------------------------------------------------ drawings kept as XML

    private const string TextBoxRun =
        "<w:r><mc:AlternateContent><mc:Choice Requires=\"wps\"><w:drawing><wp:anchor distT=\"0\" distB=\"0\" distL=\"114300\" distR=\"114300\" simplePos=\"0\" relativeHeight=\"251659264\" behindDoc=\"0\" locked=\"0\" layoutInCell=\"1\" allowOverlap=\"1\">" +
        "<wp:simplePos x=\"0\" y=\"0\"/><wp:positionH relativeFrom=\"column\"><wp:posOffset>12700</wp:posOffset></wp:positionH><wp:positionV relativeFrom=\"paragraph\"><wp:posOffset>25400</wp:posOffset></wp:positionV>" +
        "<wp:extent cx=\"2000000\" cy=\"500000\"/><wp:effectExtent l=\"0\" t=\"0\" r=\"0\" b=\"0\"/><wp:wrapSquare wrapText=\"bothSides\"/><wp:docPr id=\"5\" name=\"Text Box 5\"/><wp:cNvGraphicFramePr/>" +
        "<a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"><wps:wsp><wps:cNvSpPr txBox=\"1\"/><wps:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"2000000\" cy=\"500000\"/></a:xfrm>" +
        "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></wps:spPr><wps:txbx><w:txbxContent><w:p><w:r><w:t>Text in a box</w:t></w:r></w:p></w:txbxContent></wps:txbx><wps:bodyPr/></wps:wsp></a:graphicData></a:graphic></wp:anchor></w:drawing></mc:Choice>" +
        "<mc:Fallback><w:pict><v:shape id=\"Text Box 5\" o:spid=\"_x0000_s1026\" type=\"#_x0000_t202\" style=\"position:absolute;width:157.5pt;height:39.4pt\"><v:textbox><w:txbxContent><w:p><w:r><w:t>Text in a box</w:t></w:r></w:p></w:txbxContent></v:textbox></v:shape></w:pict></mc:Fallback></mc:AlternateContent></w:r>";

    [Fact]
    public void AlternateContentTextBox_IsReadForDisplay_AndKeptAsItWas()
    {
        var original = TestDocx.Create("<w:p>" + TextBoxRun + "<w:r><w:t>Anchor paragraph</w:t></w:r></w:p>");
        var doc = Docx.Load(original);
        var p = (Paragraph)doc.Body.Blocks[0];
        var box = Assert.IsType<DrawingObject>(p.Inlines[0]);
        Assert.Equal("textbox", box.Kind);
        Assert.Equal(WrapMode.Square, box.Wrap);
        Assert.Equal(12700, box.Float!.Horizontal.Offset);
        Assert.Equal(2000000, box.Width);
        Assert.Equal(Ns.MC + "AlternateContent", box.Original!.Name);
        Assert.Equal("Text in a box", Assert.IsType<Paragraph>(Assert.Single(box.TextBoxContent!)).PlainText());
        Assert.Contains(doc.Preserved, f => f.Category == "textbox");

        var saved = Docx.Save(doc);
        Docx.AssertValid(saved);
        var before = Docx.Body(original).Descendants(Ns.MC + "AlternateContent").Single();
        var after = Docx.Body(saved).Descendants(Ns.MC + "AlternateContent").Single();
        Assert.True(Ox.SameXml(before, after), after.ToString());
        Assert.Contains("wps", Docx.PartXml(saved, "word/document.xml").Root!.Attributes().Where(a => a.IsNamespaceDeclaration).Select(a => a.Name.LocalName));
    }

    [Fact]
    public void VmlPicturesAndObjects_AreKeptWithAPlaceholderSize()
    {
        var original = TestDocx.Create(
            "<w:p><w:r><w:pict><v:shape id=\"_x0000_i1025\" type=\"#_x0000_t75\" style=\"width:72pt;height:36pt\"><v:imagedata r:id=\"rIdImg\" o:title=\"\"/></v:shape></w:pict></w:r>" +
            "<w:r><w:object w:dxaOrig=\"1440\" w:dyaOrig=\"720\"><v:shape id=\"_x0000_i1026\" type=\"#_x0000_t75\" style=\"width:1in;height:.5in\"><v:imagedata r:id=\"rIdImg\" o:title=\"\"/></v:shape>" +
            "<o:OLEObject Type=\"Embed\" ProgID=\"Package\" ShapeID=\"_x0000_i1026\" DrawAspect=\"Content\" ObjectID=\"_1234567890\" r:id=\"rIdOle\"/></w:object></w:r></w:p>",
            (_, main) =>
            {
                TestDocx.AddImage(main, "rIdImg", TestImages.Png());
                var ole = main.AddNewPart<EmbeddedObjectPart>("application/vnd.openxmlformats-officedocument.oleObject", "rIdOle");
                TestDocx.Write(ole, Bytes(512, 3));
            });
        var doc = Docx.Load(original);
        var p = (Paragraph)doc.Body.Blocks[0];
        var vml = Assert.IsType<OpaqueInline>(p.Inlines[0]);
        Assert.Equal("vml", vml.Kind);
        Assert.Equal(72, vml.WidthPoints, 3);
        Assert.Equal(36, vml.HeightPoints, 3);
        var obj = Assert.IsType<OpaqueInline>(p.Inlines[1]);
        Assert.Equal("object", obj.Kind);
        Assert.Equal(72, obj.WidthPoints, 3);
        Assert.Contains(doc.Preserved, f => f.Category == "object");
        Assert.Contains(doc.Preserved, f => f.Category == "vml");

        var saved = Docx.Save(doc);
        Docx.AssertValid(saved, n => n.Contains("/embeddings/"));
        // The picture the VML refers to is still referenced, so its part stays.
        AssertOtherPartsUnchanged(original, saved);
    }

    [Fact]
    public void WriteReadWrite_GivesTheSameXml()
    {
        var original = TestDocx.Create(NestedIfField + "<w:p>" + TextBoxRun + "</w:p>" +
                                       "<w:p><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr><w:bookmarkStart w:id=\"0\" w:name=\"_Toc1\"/><w:r><w:t>Heading</w:t></w:r><w:bookmarkEnd w:id=\"0\"/></w:p>" +
                                       "<w:p><w:hyperlink w:anchor=\"_Toc1\" w:history=\"1\"><w:r><w:t>Link</w:t></w:r></w:hyperlink><w:fldSimple w:instr=\" PAGE \"><w:r><w:t>1</w:t></w:r></w:fldSimple></w:p>" +
                                       "<w:p><w:ins w:id=\"1\" w:author=\"A\" w:date=\"2026-01-01T00:00:00Z\"><w:r><w:t>new</w:t></w:r></w:ins><w:del w:id=\"2\" w:author=\"A\" w:date=\"2026-01-01T00:00:00Z\"><w:r><w:delText>old</w:delText></w:r></w:del></w:p>");
        var options = new DocxSaveOptions { Now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        var first = Docx.Save(Docx.Load(original), options);
        Docx.AssertValid(first);
        var second = Docx.Save(Docx.Load(first), new DocxSaveOptions { Now = options.Now });
        foreach (var part in Docx.PartNames(first).Where(n => n.EndsWith(".xml", StringComparison.Ordinal) || n.EndsWith(".rels", StringComparison.Ordinal)))
            Assert.Equal(Docx.PartXml(first, part).ToString(), Docx.PartXml(second, part).ToString());
    }
}
