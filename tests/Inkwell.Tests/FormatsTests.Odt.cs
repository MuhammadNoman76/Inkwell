using System.IO.Compression;
using System.Text;
using Inkwell.Core.Formats;
using Inkwell.Core.Model;

namespace Inkwell.Tests;

public class FormatsTestsOdt
{
    private const string Namespaces =
        "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\" " +
        "xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" " +
        "xmlns:draw=\"urn:oasis:names:tc:opendocument:xmlns:drawing:1.0\" xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\" " +
        "xmlns:xlink=\"http://www.w3.org/1999/xlink\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:meta=\"urn:oasis:names:tc:opendocument:xmlns:meta:1.0\" " +
        "xmlns:svg=\"urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0\" xmlns:loext=\"urn:org:documentfoundation:names:experimental:office:xmlns:loext:1.0\"";

    private const string FontFaces =
        "<office:font-face-decls><style:font-face style:name=\"Liberation Serif\" svg:font-family=\"'Liberation Serif'\" style:font-family-generic=\"roman\"/>" +
        "<style:font-face style:name=\"Noto Nastaliq Urdu\" svg:font-family=\"'Noto Nastaliq Urdu'\"/></office:font-face-decls>";

    private const string AutomaticStyles =
        "<office:automatic-styles>" +
        "<style:style style:name=\"P1\" style:family=\"paragraph\" style:parent-style-name=\"Text_20_body\"><style:paragraph-properties fo:text-align=\"center\" fo:margin-top=\"0.1in\" fo:margin-bottom=\"0.2in\"/></style:style>" +
        "<style:style style:name=\"P2\" style:family=\"paragraph\" style:parent-style-name=\"Heading_20_1\"/>" +
        "<style:style style:name=\"P3\" style:family=\"paragraph\" style:parent-style-name=\"Text_20_body\"><style:paragraph-properties style:writing-mode=\"rl-tb\"/>" +
        "<style:text-properties style:font-name-complex=\"Noto Nastaliq Urdu\" style:language-complex=\"ur\" style:country-complex=\"PK\"/></style:style>" +
        "<style:style style:name=\"T1\" style:family=\"text\"><style:text-properties fo:font-weight=\"bold\"/></style:style>" +
        "<style:style style:name=\"T2\" style:family=\"text\"><style:text-properties fo:font-style=\"italic\" style:text-underline-style=\"solid\" style:text-underline-width=\"auto\" style:text-underline-color=\"font-color\"/></style:style>" +
        "<style:style style:name=\"T3\" style:family=\"text\"><style:text-properties style:text-line-through-style=\"solid\" fo:color=\"#c9211e\" fo:font-size=\"14pt\" style:font-name=\"Liberation Serif\"/></style:style>" +
        "<style:style style:name=\"T4\" style:family=\"text\"><style:text-properties style:text-position=\"super 58%\"/></style:style>" +
        "<style:style style:name=\"T5\" style:family=\"text\"><style:text-properties fo:background-color=\"#ffff00\"/></style:style>" +
        "<style:style style:name=\"Table1\" style:family=\"table\"><style:table-properties style:width=\"6in\" table:align=\"margins\"/></style:style>" +
        "<style:style style:name=\"Table1.A\" style:family=\"table-column\"><style:table-column-properties style:column-width=\"2in\"/></style:style>" +
        "<style:style style:name=\"Table1.A1\" style:family=\"table-cell\"><style:table-cell-properties fo:background-color=\"#dddddd\" fo:border=\"0.5pt solid #000000\"/></style:style>" +
        "<text:list-style style:name=\"L1\"><text:list-level-style-bullet text:level=\"1\" text:bullet-char=\"•\"><style:list-level-properties text:list-level-position-and-space-mode=\"label-alignment\">" +
        "<style:list-level-label-alignment text:label-followed-by=\"listtab\" fo:text-indent=\"-0.25in\" fo:margin-left=\"0.5in\"/></style:list-level-properties></text:list-level-style-bullet>" +
        "<text:list-level-style-bullet text:level=\"2\" text:bullet-char=\"◦\"><style:list-level-properties text:list-level-position-and-space-mode=\"label-alignment\">" +
        "<style:list-level-label-alignment fo:text-indent=\"-0.25in\" fo:margin-left=\"0.75in\"/></style:list-level-properties></text:list-level-style-bullet></text:list-style>" +
        "<text:list-style style:name=\"L2\"><text:list-level-style-number text:level=\"1\" style:num-suffix=\".\" style:num-format=\"1\" text:start-value=\"4\"/>" +
        "<text:list-level-style-number text:level=\"2\" style:num-suffix=\")\" style:num-format=\"a\"/></text:list-style>" +
        "</office:automatic-styles>";

    private const string Body =
        "<office:body><office:text>\n" +
        "  <text:sequence-decls><text:sequence-decl text:display-outline-level=\"0\" text:name=\"Illustration\"/></text:sequence-decls>\n" +
        "  <text:h text:style-name=\"P2\" text:outline-level=\"1\"><text:bookmark-start text:name=\"intro\"/>Introduction<text:bookmark-end text:name=\"intro\"/></text:h>\n" +
        "  <text:p text:style-name=\"P1\">Centered  <text:span text:style-name=\"T1\">bold</text:span> <text:span text:style-name=\"T2\">it<text:s/>alic</text:span><text:tab/>" +
        "<text:span text:style-name=\"T3\">struck</text:span> x<text:span text:style-name=\"T4\">2</text:span> <text:span text:style-name=\"T5\">marked</text:span><text:line-break/>" +
        "next line <text:a xlink:type=\"simple\" xlink:href=\"https://example.com/\">a link</text:a> and <text:a xlink:href=\"#intro\">back</text:a>.</text:p>\n" +
        "  <text:p text:style-name=\"Quotations\">A quotation<text:note text:id=\"ftn1\" text:note-class=\"footnote\"><text:note-citation>1</text:note-citation><text:note-body>" +
        "<text:p text:style-name=\"Footnote\">The footnote.</text:p></text:note-body></text:note> with a note<text:note text:id=\"edn1\" text:note-class=\"endnote\">" +
        "<text:note-citation>i</text:note-citation><text:note-body><text:p text:style-name=\"Endnote\">The endnote.</text:p></text:note-body></text:note>.</text:p>\n" +
        "  <text:p text:style-name=\"P3\">اردو متن</text:p>\n" +
        "  <text:p text:style-name=\"Preformatted_20_Text\">code<text:s text:c=\"3\"/>line</text:p>\n" +
        "  <text:list text:style-name=\"L1\"><text:list-item><text:p text:style-name=\"P1\">Bullet one</text:p><text:list><text:list-item><text:p>Nested</text:p></text:list-item></text:list></text:list-item>" +
        "<text:list-item><text:p>Bullet two</text:p><text:p>continued</text:p></text:list-item></text:list>\n" +
        "  <text:list xml:id=\"list1\" text:style-name=\"L2\"><text:list-item><text:p>Four</text:p></text:list-item><text:list-item><text:p>Five</text:p>" +
        "<text:list><text:list-item><text:p>Five a</text:p></text:list-item></text:list></text:list-item></text:list>\n" +
        "  <text:p>Between</text:p>\n" +
        "  <text:list text:continue-numbering=\"true\" text:style-name=\"L2\"><text:list-item><text:p>Six</text:p></text:list-item></text:list>\n" +
        "  <table:table table:name=\"Table1\" table:style-name=\"Table1\">\n" +
        "    <table:table-column table:style-name=\"Table1.A\" table:number-columns-repeated=\"3\"/>\n" +
        "    <table:table-header-rows><table:table-row><table:table-cell table:style-name=\"Table1.A1\"><text:p>H1</text:p></table:table-cell>" +
        "<table:table-cell><text:p>H2</text:p></table:table-cell><table:table-cell><text:p>H3</text:p></table:table-cell></table:table-row></table:table-header-rows>\n" +
        "    <table:table-row><table:table-cell table:number-columns-spanned=\"2\"><text:p>wide</text:p></table:table-cell><table:covered-table-cell/>" +
        "<table:table-cell table:number-rows-spanned=\"2\"><text:p>tall</text:p></table:table-cell></table:table-row>\n" +
        "    <table:table-row><table:table-cell><text:p>a</text:p></table:table-cell><table:table-cell><text:p>b</text:p></table:table-cell><table:covered-table-cell/></table:table-row>\n" +
        "  </table:table>\n" +
        "  <text:p>Picture <draw:frame draw:name=\"Image1\" text:anchor-type=\"as-char\" svg:width=\"1in\" svg:height=\"0.5in\"><draw:image xlink:href=\"Pictures/pic.png\" xlink:type=\"simple\"/>" +
        "<svg:title>Pic title</svg:title><svg:desc>Pic description</svg:desc></draw:frame> and box<draw:frame text:anchor-type=\"paragraph\" svg:width=\"2in\">" +
        "<draw:text-box><text:p>Inside the box</text:p></draw:text-box></draw:frame></text:p>\n" +
        "  <text:p>Commented <office:annotation office:name=\"c1\"><dc:creator>Ayesha Khan</dc:creator><dc:date>2026-10-01T10:20:30</dc:date><text:p>Please check.</text:p></office:annotation>" +
        "words<office:annotation-end office:name=\"c1\"/> end.</text:p>\n" +
        "</office:text></office:body>";

    private const string NamedStyles =
        "<office:styles>" +
        "<style:default-style style:family=\"paragraph\"><style:text-properties style:font-name=\"Liberation Serif\" fo:font-size=\"12pt\"/></style:default-style>" +
        "<style:style style:name=\"Standard\" style:family=\"paragraph\" style:class=\"text\"/>" +
        "<style:style style:name=\"Text_20_body\" style:display-name=\"Text body\" style:family=\"paragraph\" style:parent-style-name=\"Standard\"><style:paragraph-properties fo:margin-bottom=\"0.0972in\"/></style:style>" +
        "<style:style style:name=\"Heading\" style:family=\"paragraph\" style:parent-style-name=\"Standard\"/>" +
        "<style:style style:name=\"Heading_20_1\" style:display-name=\"Heading 1\" style:family=\"paragraph\" style:parent-style-name=\"Heading\"><style:text-properties fo:font-size=\"130%\" fo:font-weight=\"bold\"/></style:style>" +
        "<style:style style:name=\"Quotations\" style:family=\"paragraph\" style:parent-style-name=\"Standard\"/>" +
        "<style:style style:name=\"Preformatted_20_Text\" style:display-name=\"Preformatted Text\" style:family=\"paragraph\" style:parent-style-name=\"Standard\"/>" +
        "<style:style style:name=\"Footnote\" style:family=\"paragraph\" style:parent-style-name=\"Standard\"/>" +
        "<style:style style:name=\"Endnote\" style:family=\"paragraph\" style:parent-style-name=\"Standard\"/>" +
        "</office:styles>";

    private const string PageStyles =
        "<office:automatic-styles><style:page-layout style:name=\"pm1\"><style:page-layout-properties fo:page-width=\"8.5in\" fo:page-height=\"11in\" style:print-orientation=\"portrait\" " +
        "fo:margin-top=\"0.7874in\" fo:margin-bottom=\"0.7874in\" fo:margin-left=\"1in\" fo:margin-right=\"1in\"/></style:page-layout></office:automatic-styles>" +
        "<office:master-styles><style:master-page style:name=\"Standard\" style:page-layout-name=\"pm1\"/></office:master-styles>";

    private const string MetaBody =
        "<office:meta><meta:initial-creator>Muhammad Noman</meta:initial-creator><dc:creator>Editor Person</dc:creator><dc:title>ODT Sample</dc:title>" +
        "<dc:subject>Testing</dc:subject><meta:keyword>alpha</meta:keyword><meta:keyword>beta</meta:keyword><meta:creation-date>2026-10-01T08:00:00</meta:creation-date>" +
        "<dc:date>2026-10-02T09:00:00Z</dc:date></office:meta>";

    private static byte[] Package(byte[] png)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Add(string name, string text)
            {
                var entry = zip.CreateEntry(name, name == "mimetype" ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                using var s = entry.Open();
                var bytes = new UTF8Encoding(false).GetBytes(text);
                s.Write(bytes, 0, bytes.Length);
            }
            Add("mimetype", "application/vnd.oasis.opendocument.text");
            Add("content.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document-content " + Namespaces + " office:version=\"1.3\">" + FontFaces + AutomaticStyles + Body + "</office:document-content>");
            Add("styles.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document-styles " + Namespaces + " office:version=\"1.3\">" + FontFaces + NamedStyles + PageStyles + "</office:document-styles>");
            Add("meta.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document-meta " + Namespaces + " office:version=\"1.3\">" + MetaBody + "</office:document-meta>");
            var picture = zip.CreateEntry("Pictures/pic.png", CompressionLevel.NoCompression);
            using (var s = picture.Open()) s.Write(png, 0, png.Length);
        }
        return ms.ToArray();
    }

    private static Document Sample(out byte[] png)
    {
        var picture = Sta.Run(() => TestImages.Png(32, 16));
        png = picture;
        return OdtImporter.Import(Package(picture));
    }

    private static List<Paragraph> Paragraphs(Document doc) => doc.Body.Paragraphs.ToList();

    private static TextRun Run(Paragraph p, string text) => p.Inlines.OfType<TextRun>().First(t => t.Text.Contains(text));

    [Fact]
    public void HeadingsParagraphsAndDirectFormatting()
    {
        var doc = Sample(out _);
        var ps = Paragraphs(doc);
        var heading = ps[0];
        Assert.Equal("Heading1", heading.StyleId);
        Assert.Equal("Introduction", heading.PlainText());
        var marks = heading.Inlines.OfType<BookmarkMarker>().ToList();
        Assert.Equal("intro", marks[0].Name);
        Assert.Equal(marks[0].Id, marks[1].Id);
        Assert.False(marks[1].IsStart);
        // The heading's named style formatting is replaced by Inkwell's style: no direct bold or size.
        Assert.Null(Run(heading, "Introduction").Props.Bold);

        var p = ps[1];
        Assert.Equal("Normal", p.StyleId);
        Assert.Equal("center", p.Props.Justification);
        Assert.Equal(144, p.Props.SpacingBefore);
        Assert.Equal(288, p.Props.SpacingAfter);
        Assert.Equal("Centered bold it alic\tstruck x2 marked\vnext line a link and back.", p.Text);
        Assert.True(Run(p, "bold").Props.Bold);
        var italic = Run(p, "it alic");
        Assert.True(italic.Props.Italic);
        Assert.Equal("single", italic.Props.Underline);
        var struck = Run(p, "struck");
        Assert.True(struck.Props.Strike);
        Assert.Equal("C9211E", struck.Props.Color);
        Assert.Equal(28, struck.Props.Size);
        Assert.Equal("Liberation Serif", struck.Props.FontAscii);
        Assert.Equal("superscript", p.Inlines.OfType<TextRun>().Single(t => t.Text == "2").Props.VertAlign);
        Assert.Equal("yellow", Run(p, "marked").Props.Highlight);
        var link = Run(p, "a link");
        Assert.Equal("https://example.com/", link.Link!.Url);
        Assert.Equal("Hyperlink", link.Props.StyleId);
        Assert.Equal("intro", Run(p, "back").Link!.Anchor);
    }

    [Fact]
    public void QuotesCodeRightToLeftAndNotes()
    {
        var doc = Sample(out _);
        var ps = Paragraphs(doc);
        var quote = ps[2];
        Assert.Equal("Quote", quote.StyleId);
        Assert.Equal("A quotation with a note.", quote.PlainText());
        var refs = quote.Inlines.OfType<NoteReference>().ToList();
        Assert.Equal(NoteKind.Footnote, refs[0].Kind);
        Assert.Equal(NoteKind.Endnote, refs[1].Kind);
        Assert.Equal("The footnote.", doc.Footnotes[refs[0].Id].Paragraphs.Single().PlainText().Trim());
        Assert.Equal("FootnoteText", doc.Footnotes[refs[0].Id].Paragraphs.Single().StyleId);
        Assert.Equal("The endnote.", doc.Endnotes[refs[1].Id].Paragraphs.Single().PlainText().Trim());
        Assert.Equal("EndnoteText", doc.Endnotes[refs[1].Id].Paragraphs.Single().StyleId);

        var urdu = ps[3];
        Assert.Equal("اردو متن", urdu.PlainText());
        Assert.True(urdu.Props.Bidi);
        var urduRun = urdu.Inlines.OfType<TextRun>().Single();
        Assert.Equal("Noto Nastaliq Urdu", urduRun.Props.FontCs);
        Assert.Equal("ur-PK", urduRun.Props.LangBidi);

        Assert.Equal("Code", ps[4].StyleId);
        Assert.Equal("code   line", ps[4].PlainText());
    }

    [Fact]
    public void ListsWithLevelsFormatsAndContinuedNumbering()
    {
        var doc = Sample(out _);
        var ps = Paragraphs(doc);
        Paragraph P(string text) => ps.Single(p => p.PlainText() == text);
        var one = P("Bullet one");
        var nested = P("Nested");
        var two = P("Bullet two");
        var continued = P("continued");
        Assert.Equal(one.Props.NumId, nested.Props.NumId);
        Assert.Equal(one.Props.NumId, two.Props.NumId);
        Assert.Equal(1, nested.Props.NumLevel);
        Assert.Equal("ListParagraph", one.StyleId);
        Assert.Equal("center", one.Props.Justification);
        Assert.Null(continued.Props.NumId);
        Assert.Equal(360, continued.Props.IndentStart);
        var level0 = doc.Numbering.Level(one.Props.NumId!.Value, 0)!;
        Assert.True(level0.IsBullet);
        Assert.Equal("•", level0.Text);
        Assert.Equal("◦", doc.Numbering.Level(one.Props.NumId.Value, 1)!.Text);
        Assert.Equal("720", level0.ParaPropsXml!.Element(Ns.W + "ind").Attr("left"));

        var four = P("Four");
        var fiveA = P("Five a");
        var six = P("Six");
        var l2 = doc.Numbering.Level(four.Props.NumId!.Value, 0)!;
        Assert.Equal(("decimal", "%1.", 4), (l2.Format, l2.Text, l2.Start));
        var sub = doc.Numbering.Level(four.Props.NumId.Value, 1)!;
        Assert.Equal(("lowerLetter", "%2)"), (sub.Format, sub.Text));
        Assert.Equal(1, fiveA.Props.NumLevel);
        Assert.Equal(four.Props.NumId, six.Props.NumId);
        Assert.Equal("6. Six", TextExporter.Export(doc).Split(new[] { "\r\n" }, StringSplitOptions.None).Single(l => l.EndsWith("Six", StringComparison.Ordinal)));
    }

    [Fact]
    public void TablesWithSpansCoveredCellsAndHeaderRows()
    {
        var doc = Sample(out _);
        var table = doc.Body.Blocks.OfType<Table>().Single();
        Assert.Equal(new[] { 2880, 2880, 2880 }, table.Grid);
        Assert.Equal("TableGrid", table.Props.StyleId);
        Assert.Equal(new TableWidth("dxa", 8640), table.Props.Width);
        Assert.Equal(3, table.Rows.Count);
        Assert.True(table.Rows[0].Props.IsHeader);
        Assert.False(table.Rows[1].Props.IsHeader);
        var h1 = table.Rows[0].Cells[0];
        Assert.Equal("DDDDDD", h1.Props.ShadingFill);
        Assert.Equal(new BorderDef("single", 4, 0, "000000"), h1.Props.Border("top"));
        Assert.False(table.Rows[0].Cells[1].Props.Border("top")!.Value.IsVisible);
        Assert.Equal(2, table.Rows[1].Cells.Count);
        Assert.Equal(2, table.Rows[1].Cells[0].Props.GridSpan);
        Assert.Equal("restart", table.Rows[1].Cells[1].Props.VerticalMerge);
        Assert.Equal(3, table.Rows[2].Cells.Count);
        Assert.Equal("continue", table.Rows[2].Cells[2].Props.VerticalMerge);
        Assert.Equal("b", ((Paragraph)table.Rows[2].Cells[1].Blocks[0]).PlainText());
    }

    [Fact]
    public void PicturesTextBoxesAndComments()
    {
        var doc = Sample(out var png);
        var ps = Paragraphs(doc);
        var picturePara = ps.Single(p => p.PlainText().StartsWith("Picture", StringComparison.Ordinal));
        var picture = picturePara.Inlines.OfType<DrawingObject>().Single();
        Assert.Equal(914400, picture.Width);
        Assert.Equal(457200, picture.Height);
        Assert.Equal("Pic title", picture.Title);
        Assert.Equal("Pic description", picture.AltText);
        Assert.Equal(png, doc.Media.Get(picture.MediaId)!.Data);
        var index = ps.IndexOf(picturePara);
        Assert.Equal("Inside the box", ps[index + 1].PlainText());

        var commented = ps.Single(p => p.PlainText().StartsWith("Commented", StringComparison.Ordinal));
        Assert.Equal("Commented words end.", commented.PlainText());
        var comment = doc.Comments.Single();
        Assert.Equal("Ayesha Khan", comment.Author);
        Assert.Equal("AK", comment.Initials);
        Assert.Equal("Please check.", comment.Text);
        var markers = commented.Inlines.OfType<CommentMarker>().Select(m => m.Kind).ToList();
        Assert.Equal(new[] { CommentMarkerKind.RangeStart, CommentMarkerKind.RangeEnd, CommentMarkerKind.Reference }, markers);
        var start = commented.Inlines.FindIndex(i => i is CommentMarker { Kind: CommentMarkerKind.RangeStart });
        Assert.Equal("words", ((TextRun)commented.Inlines[start + 1]).Text);
    }

    [Fact]
    public void DocumentInformationAndPageLayout()
    {
        var doc = Sample(out _);
        Assert.Equal("ODT Sample", doc.Properties.Title);
        Assert.Equal("Testing", doc.Properties.Subject);
        Assert.Equal("Muhammad Noman", doc.Properties.Creator);
        Assert.Equal("Editor Person", doc.Properties.LastModifiedBy);
        Assert.Equal("alpha, beta", doc.Properties.Keywords);
        Assert.Equal(new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc), doc.Properties.Modified);
        var s = doc.Body.FinalSection;
        Assert.Equal(12240, s.PageWidth);
        Assert.Equal(15840, s.PageHeight);
        Assert.Equal(1440, s.MarginLeft);
        Assert.Equal(1134, s.MarginTop);
    }

    [Fact]
    public void FlatOdtAndFiles()
    {
        var flat = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document " + Namespaces + " office:version=\"1.3\" office:mimetype=\"application/vnd.oasis.opendocument.text\">" +
                   MetaBody + FontFaces + NamedStyles + AutomaticStyles.Replace("</office:automatic-styles>", "") +
                   "<style:page-layout style:name=\"pm1\"><style:page-layout-properties fo:page-width=\"21cm\" fo:page-height=\"29.7cm\"/></style:page-layout></office:automatic-styles>" +
                   "<office:master-styles><style:master-page style:name=\"Standard\" style:page-layout-name=\"pm1\"/></office:master-styles>" +
                   "<office:body><office:text><text:h text:outline-level=\"2\">Flat heading</text:h><text:p text:style-name=\"P1\">Flat <text:span text:style-name=\"T1\">text</text:span></text:p></office:text></office:body></office:document>";
        using var dir = new TempDir();
        var path = dir.File("flat.fodt");
        File.WriteAllText(path, flat, new UTF8Encoding(false));
        var doc = OdtImporter.ImportFile(path);
        var ps = Paragraphs(doc);
        Assert.Equal("Heading2", ps[0].StyleId);
        Assert.Equal("Flat text", ps[1].PlainText());
        Assert.True(Run(ps[1], "text").Props.Bold);
        Assert.Equal("ODT Sample", doc.Properties.Title);
        Assert.Equal(11906, doc.Body.FinalSection.PageWidth);

        var odtPath = dir.File("sample.odt");
        File.WriteAllBytes(odtPath, Package(Sta.Run(() => TestImages.Png(4, 4))));
        Assert.Equal("Introduction", OdtImporter.ImportFile(odtPath).Body.Paragraphs.First().PlainText());
    }

    [Fact]
    public void MarkdownExportOfAnOdtDocument()
    {
        var doc = Sample(out _);
        var md = MarkdownExporter.Export(doc);
        Assert.StartsWith("# Introduction\n", md, StringComparison.Ordinal);
        Assert.Contains("> A quotation[^1] with a note[^2].", md);
        Assert.Contains("- Bullet one\n  - Nested\n- Bullet two\n\n  continued\n", md);
        Assert.Contains("4. Four\n5. Five\n   1. Five a\n", md);
        Assert.Contains("| H1 | H2 | H3 |", md);
        Assert.Contains("[^1]: The footnote.", md);
    }
}
