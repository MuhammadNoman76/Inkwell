using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Inkwell.Core.Formats;
using Inkwell.Core.Model;

namespace Inkwell.Tests;

public class FormatsTestsHtmlExport
{
    private static Document Rich() => MarkdownImporter.Import(
        "# Report\n\nIntro with **bold** and a [link](#details).\n\n" +
        "1. one\n   1. one a\n2. two\n\n- dot\n\n" +
        "| H1 | H2 |\n| --- | --- |\n| a | b |\n\n" +
        "## Details\n\nText with a note.[^1]\n\n[^1]: Note body.\n");

    [Fact]
    public void ACompletePageWithStylesFromTheDocument()
    {
        var html = HtmlExporter.Export(Rich());
        Assert.StartsWith("<!DOCTYPE html>", html, StringComparison.Ordinal);
        Assert.Contains("<meta charset=\"utf-8\">", html);
        Assert.Contains("<title>Report</title>", html);
        Assert.Contains("<html lang=\"en-US\">", html);
        var h1Rule = Regex.Match(html, @"\.p-Heading1 \{([^}]*)\}").Groups[1].Value;
        Assert.Contains("font-size:18pt", h1Rule);
        Assert.Contains("font-weight:700", h1Rule);
        Assert.Contains("color:#1F2A44", h1Rule);
        Assert.Contains("\"Inter\", \"Segoe UI\", Arial, sans-serif", h1Rule);
        var normal = Regex.Match(html, @"\.p-Normal \{([^}]*)\}").Groups[1].Value;
        Assert.Contains("\"Literata\", Georgia, \"Times New Roman\", serif", normal);
        Assert.Contains("margin-bottom:9pt", normal);
        Assert.Contains("line-height:1.5", normal);
        Assert.Contains(".c-InlineCode", Regex.Replace(HtmlExporter.Export(MarkdownImporter.Import("`x`\n")), @"\s+", " "));
    }

    [Fact]
    public void HeadingsGetIdsAndInternalLinksWork()
    {
        var html = HtmlExporter.Export(Rich());
        Assert.Contains("<h1 class=\"p-Heading1\" id=\"report\">Report</h1>", html);
        Assert.Contains("id=\"details\"", html);
        Assert.Contains("<a href=\"#details\">", html);
    }

    [Fact]
    public void BookmarksBecomeAnchors()
    {
        var doc = new Document();
        var p = new Paragraph();
        p.Inlines.Add(new TextRun("before "));
        p.Inlines.Add(new BookmarkMarker { Id = 0, Name = "spot", IsStart = true });
        p.Inlines.Add(new TextRun("after"));
        p.Inlines.Add(new BookmarkMarker { Id = 0, IsStart = false });
        doc.Body.Blocks.Add(p);
        var h = new Paragraph("Head", "Heading2");
        h.Inlines.Insert(0, new BookmarkMarker { Id = 1, Name = "_Toc1", IsStart = true });
        doc.Body.Blocks.Add(h);
        var html = HtmlExporter.Export(doc);
        Assert.Contains("before <a id=\"spot\" class=\"iw-anchor\"></a>after", html);
        Assert.Contains("<h2 class=\"p-Heading2\" id=\"_Toc1\">Head</h2>", html);
    }

    [Fact]
    public void NestedListsUseTheNumberingFormats()
    {
        var html = HtmlExporter.Export(Rich());
        var ol = Regex.Match(html, "<ol style=\"([^\"]*)\">\\s*<li class=\"p-ListParagraph\">one").Groups[1].Value;
        Assert.Contains("list-style-type:decimal", ol);
        Assert.Contains("<ol style=\"margin-top:0;margin-bottom:0;padding-inline-start:18pt;list-style-type:lower-alpha\">", html);
        Assert.Matches("<li class=\"p-ListParagraph\">one<ol[^>]*>\\s*<li class=\"p-ListParagraph\">one a</li></ol>\\s*</li>\\s*<li class=\"p-ListParagraph\">two", html);
        Assert.Contains("list-style-type:disc", html);

        var doc = new Document();
        var abs = doc.Numbering.AddAbstract(Numbering.StandardDefinition(false));
        var num = doc.Numbering.AddNum(abs, 4);
        var p = new Paragraph("four", "ListParagraph");
        p.Props.SetNumbering(num, 0);
        doc.Body.Blocks.Add(p);
        Assert.Contains("<ol start=\"4\"", HtmlExporter.Export(doc));
    }

    [Fact]
    public void TablesHaveSpansHeaderCellsBordersAndShading()
    {
        var doc = new Document();
        var t = new Table { Props = { StyleId = "InkwellTable" } };
        t.Props.Look = (true, false, false, false, true, false);
        t.Grid.AddRange(new[] { 2000, 2000, 2000 });
        string[][] rows = { new[] { "H1", "H2", "H3" }, new[] { "wide", "tall" }, new[] { "a", "b", "" } };
        foreach (var r in rows)
        {
            var tr = new TableRow();
            foreach (var text in r) tr.AddCell(TableCell.Create(2000, new Paragraph(text)));
            t.AddRow(tr);
        }
        t.Rows[0].Props.IsHeader = true;
        t.Rows[1].Cells[0].Props.GridSpan = 2;
        t.Rows[1].Cells[1].Props.VerticalMerge = "restart";
        t.Rows[2].Cells[2].Props.VerticalMerge = "continue";
        t.Rows[2].Cells[0].Props.ShadingFill = "FFEEDD";
        doc.Body.Blocks.Add(t);
        var html = HtmlExporter.Export(doc);
        Assert.Contains("<thead>", html);
        Assert.Matches("(?s)<th [^>]*><p [^>]*>H1</p>\\s*</th>", html);
        Assert.Matches("<td colspan=\"2\" [^>]*>.*wide", html);
        Assert.Matches("<td rowspan=\"2\" [^>]*>.*tall", html);
        Assert.Equal(4, Regex.Matches(Regex.Match(html, "<tbody>.*</tbody>", RegexOptions.Singleline).Value, "<td").Count);
        Assert.Contains("background-color:#FFEEDD", html);
        // InkwellTable: a 1pt rule above the table, rules between rows, header text bold in the heading colour.
        Assert.Contains("border-top:1pt solid #1F2A44", html);
        Assert.Contains("border-top:0.5pt solid #D9D6CF", html);
        Assert.Matches("<th [^>]*><p class=\"p-Normal\" style=\"[^\"]*font-weight:700[^\"]*color:#1F2A44", html);
    }

    [Fact]
    public void PicturesAreDataUrisOrFiles()
    {
        var png = Sta.Run(() => TestImages.Png(96, 48));
        var doc = MarkdownImporter.Import("![chart](data:image/png;base64," + Convert.ToBase64String(png) + ")\n");
        var html = HtmlExporter.Export(doc);
        Assert.Contains("<img src=\"data:image/png;base64," + Convert.ToBase64String(png) + "\" alt=\"chart\" width=\"96\" height=\"48\">", html);
        using var dir = new TempDir();
        var withFiles = HtmlExporter.Export(doc, new HtmlExportOptions { ImageFolder = Path.Combine(dir.Path, "page_files") });
        Assert.Contains("<img src=\"page_files/image1.png\"", withFiles);
        Assert.Equal(png, File.ReadAllBytes(Path.Combine(dir.Path, "page_files", "image1.png")));
    }

    [Fact]
    public void NotesSectionWithLinksBothWays()
    {
        var html = HtmlExporter.Export(Rich());
        Assert.Contains("<sup class=\"iw-noteref\"><a href=\"#fn-1\" id=\"fnref-1\" role=\"doc-noteref\">1</a></sup>", html);
        Assert.Matches("<li id=\"fn-1\"><p class=\"p-FootnoteText\">Note body.<a href=\"#fnref-1\" class=\"iw-backref\"", html);
    }

    [Fact]
    public void RightToLeftLanguagesAndPageBreaks()
    {
        var doc = new Document();
        var urdu = new Paragraph();
        urdu.Props.Bidi = true;
        var run = new TextRun("اردو متن");
        run.Props.Rtl = true;
        run.Props.LangBidi = "ur-PK";
        urdu.Inlines.Add(run);
        doc.Body.Blocks.Add(urdu);
        var french = new Paragraph();
        var fr = new TextRun("Bonjour");
        fr.Props.Lang = "fr-FR";
        french.Inlines.Add(fr);
        french.Props.PageBreakBefore = true;
        doc.Body.Blocks.Add(french);
        doc.Body.Blocks.Add(new Paragraph("\f"));
        doc.Body.Blocks.Add(new Paragraph("a  b\tc"));
        var html = HtmlExporter.Export(doc);
        Assert.Contains("<p class=\"p-Normal\" dir=\"rtl\"><span lang=\"ur-PK\">اردو متن</span></p>", html);
        Assert.Contains("<p class=\"p-Normal\" style=\"break-before:page\"><span lang=\"fr-FR\">Bonjour</span></p>", html);
        Assert.Contains("<div style=\"break-before:page\"></div>", html);
        Assert.Contains("a &nbsp;b<span class=\"iw-tab\" style=\"white-space:pre\">\t</span>c", html);
    }

    [Fact]
    public void ClipboardPayloadHasCorrectByteOffsets()
    {
        var doc = MarkdownImporter.Import("Urdu: اردو **bold** — “quoted”\n\n- item\n");
        var clip = HtmlExporter.ExportClipboard(doc, doc.Body.Blocks);
        var bytes = clip.PayloadBytes;
        var header = Encoding.ASCII.GetString(bytes, 0, 200);
        int Field(string name) => int.Parse(Regex.Match(header, name + @":(\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
        var startHtml = Field("StartHTML");
        var endHtml = Field("EndHTML");
        var startFragment = Field("StartFragment");
        var endFragment = Field("EndFragment");
        Assert.Equal("<html>", Encoding.UTF8.GetString(bytes, startHtml, 6));
        Assert.Equal(bytes.Length, endHtml);
        Assert.Equal(clip.Fragment, Encoding.UTF8.GetString(bytes, startFragment, endFragment - startFragment));
        Assert.EndsWith("<!--StartFragment-->", Encoding.UTF8.GetString(bytes, 0, startFragment), StringComparison.Ordinal);
        Assert.StartsWith("<!--EndFragment-->", Encoding.UTF8.GetString(bytes, endFragment, bytes.Length - endFragment), StringComparison.Ordinal);
        Assert.DoesNotContain("class=", clip.Fragment);
        Assert.Contains("style=\"", clip.Fragment);
        Assert.Equal(clip.Fragment, CfHtml.Parse(bytes).Fragment);
        Assert.Equal(clip.Fragment, CfHtml.Parse(clip.Payload).Fragment);

        var back = HtmlImporter.ImportClipboard(bytes, new Document());
        var p = (Paragraph)back.Blocks[0];
        Assert.Equal("Urdu: اردو bold — “quoted”", p.PlainText());
        Assert.True(p.Inlines.OfType<TextRun>().Single(r => r.Text == "bold").Props.Bold);
        Assert.NotNull(((Paragraph)back.Blocks[1]).Props.NumId);
    }

    [Fact]
    public void HtmlRoundTripKeepsTheStructure()
    {
        const string md = "# Title\n\nSome **bold**, *italic* and `code` with a [link](https://example.com).\n\n" +
                          "1. one\n   - inner\n2. two\n\n> quoted\n\n```\ncode line\n\tindented\n```\n\n| A | B |\n| --- | --- |\n| 1 | 2 |\n\n---\n\nEnd.\n";
        var doc = MarkdownImporter.Import(md);
        var clip = HtmlExporter.ExportClipboard(doc, doc.Body.Blocks);
        var target = new Document();
        var pasted = HtmlImporter.ImportClipboard(clip.PayloadBytes, target);
        target.Body.Blocks.AddRange(pasted.Blocks);
        Assert.Equal(md, MarkdownExporter.Export(target));
        var page = HtmlExporter.Export(doc);
        var opened = HtmlImporter.ImportDocument(page);
        Assert.Equal(md, MarkdownExporter.Export(opened));
        Assert.Equal("Title", opened.Properties.Title);
    }
}

public class FormatsTestsHtmlImport
{
    private static PasteResult Paste(string html, bool keep = false, Document? target = null) =>
        HtmlImporter.Import(html, target ?? new Document(), new HtmlImportOptions { KeepSourceFormatting = keep });

    private static List<Paragraph> Paragraphs(PasteResult r) => DocumentWalker.Paragraphs(r.Blocks).ToList();

    private static TextRun Run(PasteResult r, string text) =>
        Paragraphs(r).SelectMany(p => p.Inlines).OfType<TextRun>().First(t => t.Text.Contains(text));

    internal const string Browser =
        "<html><body>\n<!--StartFragment--><h2 style=\"color: rgb(0, 0, 0); font-family: Georgia;\">Section &amp; more</h2>" +
        "<p>Text with <a href=\"/docs/page.html\">a relative link</a>, <code>code()</code>, <strong>strong</strong>, <em>em</em>, <u>u</u>, <s>s</s>, H<sub>2</sub>O, x<sup>2</sup>.</p>\n" +
        "<blockquote><p>Quoted text</p></blockquote>\n" +
        "<pre>line one\n    indented\ttab\n</pre>\n" +
        "<ol start=\"3\" type=\"a\"><li>three<ol><li>inner</li></ol></li><li>four</li></ol>\n" +
        "<hr>\n" +
        "<table><thead><tr><th>H1</th><th>H2</th><th>H3</th></tr></thead><tbody><tr><td colspan=\"2\">wide</td><td rowspan=\"2\">tall</td></tr><tr><td>a</td><td>b</td></tr></tbody></table>\n" +
        "<p>Image <img src=\"https://example.com/x.png\" alt=\"web\"> end</p><!--EndFragment-->\n</body></html>";

    [Fact]
    public void BrowserHtmlKeepsStructureInTheTargetsStyles()
    {
        var payload = CfHtml.Build(Browser.Substring(Browser.IndexOf("<h2", StringComparison.Ordinal), Browser.IndexOf("<!--EndFragment", StringComparison.Ordinal) - Browser.IndexOf("<h2", StringComparison.Ordinal)),
            sourceUrl: "https://example.com/articles/one.html");
        var r = HtmlImporter.ImportClipboard(payload, new Document());
        var ps = Paragraphs(r);
        Assert.Equal("Heading2", ps[0].StyleId);
        Assert.Equal("Section & more", ps[0].PlainText());
        Assert.Null(ps[0].Inlines.OfType<TextRun>().Single().Props.FontAscii);
        Assert.Null(ps[0].Inlines.OfType<TextRun>().Single().Props.Color);
        Assert.Equal("https://example.com/docs/page.html", Run(r, "relative").Link!.Url);
        Assert.Equal("Hyperlink", Run(r, "relative").Props.StyleId);
        Assert.Equal("InlineCode", Run(r, "code()").Props.StyleId);
        Assert.True(Run(r, "strong").Props.Bold);
        Assert.True(Run(r, "em").Props.Italic);
        Assert.Equal("single", ps[1].Inlines.OfType<TextRun>().Single(t => t.Text == "u").Props.Underline);
        Assert.True(ps[1].Inlines.OfType<TextRun>().Single(t => t.Text == "s").Props.Strike);
        Assert.Equal("subscript", ps[1].Inlines.OfType<TextRun>().Single(t => t.Text == "2" && t.Props.VertAlign == "subscript").Props.VertAlign);
        Assert.Equal("Text with a relative link, code(), strong, em, u, s, H2O, x2.", ps[1].PlainText());
        Assert.Equal("Quote", ps[2].StyleId);
        Assert.Equal(new[] { "line one", "    indented\ttab" }, ps.Where(p => p.StyleId == "Code").Select(p => p.PlainText()));
        var three = ps.Single(p => p.PlainText() == "three");
        var four = ps.Single(p => p.PlainText() == "four");
        var inner = ps.Single(p => p.PlainText() == "inner");
        Assert.Equal(three.Props.NumId, four.Props.NumId);
        Assert.Equal(1, inner.Props.NumLevel);
        Assert.Contains(r.Blocks.OfType<Paragraph>(), Content_IsDivider);
        var table = r.Blocks.OfType<Table>().Single();
        Assert.Equal(3, table.Rows.Count);
        Assert.True(table.Rows[0].Props.IsHeader);
        Assert.Equal(2, table.Rows[1].Cells[0].Props.GridSpan);
        Assert.Equal("restart", table.Rows[1].Cells[1].Props.VerticalMerge);
        Assert.Equal("continue", table.Rows[2].Cells[2].Props.VerticalMerge);
        Assert.Equal(3, table.Grid.Count);
        Assert.Equal("Image end", ps.Last().PlainText());
        Assert.Equal(1, r.ImagesSkipped);
        Assert.Equal("https://example.com/x.png", r.SkippedImageUrls.Single());
    }

    private static bool Content_IsDivider(Paragraph p) => p.IsEmpty && p.Props.Border("bottom") is { IsVisible: true };

    [Fact]
    public void OrderedListStartAndTypeBecomeNumbering()
    {
        var target = new Document();
        var r = Paste("<ol start=\"3\" type=\"a\"><li>three<ol><li>inner</li></ol></li><li>four</li></ol>", target: target);
        var ps = Paragraphs(r);
        var level = target.Numbering.Level(ps[0].Props.NumId!.Value, 0)!;
        Assert.Equal("lowerLetter", level.Format);
        Assert.Equal(3, level.Start);
        var inner = target.Numbering.Level(ps[1].Props.NumId!.Value, 1)!;
        Assert.Equal("decimal", inner.Format);
        Assert.Equal("ListParagraph", ps[0].StyleId);
    }

    [Fact]
    public void MatchDestinationDropsFontsAndColoursKeepSourceKeepsThem()
    {
        const string html = "<p style=\"text-align:center\">Plain <span style=\"font-family:'Georgia',serif;color:#ff0000;font-size:14pt;background-color:#ffff00\">styled</span> " +
                            "<span style=\"background:rgb(204, 238, 255)\">shaded</span> <b>bold</b></p>";
        var match = Paste(html);
        var styled = Run(match, "styled");
        Assert.Null(styled.Props.FontAscii);
        Assert.Null(styled.Props.Color);
        Assert.Null(styled.Props.Size);
        Assert.Null(styled.Props.Highlight);
        Assert.Null(Paragraphs(match)[0].Props.Justification);
        Assert.True(Run(match, "bold").Props.Bold);

        var keep = Paste(html, keep: true);
        styled = Run(keep, "styled");
        Assert.Equal("Georgia", styled.Props.FontAscii);
        Assert.Equal("FF0000", styled.Props.Color);
        Assert.Equal(28, styled.Props.Size);
        Assert.Equal("yellow", styled.Props.Highlight);
        Assert.Equal("CCEEFF", Run(keep, "shaded").Props.ShadingFill);
        Assert.Equal("center", Paragraphs(keep)[0].Props.Justification);
    }

    internal const string Word =
        "<html xmlns:o=\"urn:schemas-microsoft-com:office:office\" xmlns:w=\"urn:schemas-microsoft-com:office:word\">\r\n<head>\r\n" +
        "<meta http-equiv=Content-Type content=\"text/html; charset=utf-8\">\r\n<meta name=ProgId content=Word.Document>\r\n" +
        "<!--[if gte mso 9]><xml>\r\n <o:OfficeDocumentSettings>\r\n  <o:AllowPNG/>\r\n </o:OfficeDocumentSettings>\r\n</xml><![endif]-->\r\n" +
        "<style>\r\n<!--\r\n /* Font Definitions */\r\n @font-face\r\n\t{font-family:\"Cambria Math\";\r\n\tpanose-1:2 4 5 3 5 4 6 3 2 4;}\r\n" +
        "p.MsoNormal, li.MsoNormal, div.MsoNormal\r\n\t{margin-top:0in;\r\n\tmargin-bottom:8.0pt;\r\n\tline-height:107%;\r\n\tfont-size:11.0pt;\r\n\tfont-family:\"Calibri\",sans-serif;}\r\n" +
        "p.MsoTitle, li.MsoTitle, div.MsoTitle\r\n\t{mso-style-link:\"Title Char\";\r\n\tfont-size:28.0pt;\r\n\tfont-family:\"Calibri Light\",sans-serif;}\r\n" +
        "@list l0\r\n\t{mso-list-id:1;}\r\n@list l0:level1\r\n\t{mso-level-number-format:bullet;\r\n\tmso-level-text:\\F0B7;\r\n\tfont-family:Symbol;}\r\n" +
        "@list l0:level2\r\n\t{mso-level-number-format:bullet;\r\n\tmso-level-text:o;\r\n\tfont-family:\"Courier New\";}\r\n" +
        "@list l1:level1\r\n\t{mso-level-tab-stop:none;\r\n\ttext-indent:-.25in;}\r\n@list l1:level2\r\n\t{mso-level-number-format:alpha-lower;}\r\n-->\r\n</style>\r\n</head>\r\n" +
        "<body lang=EN-US style='tab-interval:.5in'>\r\n<!--StartFragment-->\r\n" +
        "<p class=MsoTitle>Report <span style='color:red'>title</span><o:p></o:p></p>\r\n" +
        "<p class=MsoNormal>Plain <b>bold</b> and <span style='font-family:\"Times New Roman\",serif;\r\ncolor:#C00000'>red serif</span>&nbsp;&nbsp; text.<o:p></o:p></p>\r\n" +
        "<p class=MsoNormal><o:p>&nbsp;</o:p></p>\r\n" +
        "<p class=MsoListParagraphCxSpFirst style='text-indent:-.25in;mso-list:l0 level1 lfo1'><![if !supportLists]><span\r\nstyle='font-family:Symbol'><span style='mso-list:Ignore'>·<span style='font:7.0pt \"Times New Roman\"'>&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;\r\n</span></span></span><![endif]>Bullet one<o:p></o:p></p>\r\n" +
        "<p class=MsoListParagraphCxSpMiddle style='margin-left:1.0in;mso-add-space:auto;text-indent:-.25in;mso-list:l0 level2 lfo1'><![if !supportLists]><span\r\nstyle='font-family:\"Courier New\"'><span style='mso-list:Ignore'>o<span style='font:7.0pt \"Times New Roman\"'>&nbsp;&nbsp;\r\n</span></span></span><![endif]>Sub bullet<o:p></o:p></p>\r\n" +
        "<p class=MsoListParagraphCxSpLast style='text-indent:-.25in;mso-list:l0 level1 lfo1'><![if !supportLists]><span\r\nstyle='font-family:Symbol'><span style='mso-list:Ignore'>·<span style='font:7.0pt \"Times New Roman\"'>&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;\r\n</span></span></span><![endif]>Bullet two<o:p></o:p></p>\r\n" +
        "<p class=MsoListParagraphCxSpFirst style='text-indent:-.25in;mso-list:l1 level1 lfo2'><![if !supportLists]><span style='mso-list:Ignore'>3.<span style='font:7.0pt \"Times New Roman\"'>&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;\r\n</span></span><![endif]>Number three<o:p></o:p></p>\r\n" +
        "<p class=MsoListParagraphCxSpLast style='margin-left:1.0in;text-indent:-.25in;mso-list:l1 level2 lfo2'><![if !supportLists]><span style='mso-list:Ignore'>a.<span style='font:7.0pt \"Times New Roman\"'>&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;\r\n</span></span><![endif]>Letter a<o:p></o:p></p>\r\n" +
        "<p class=MsoNormal>Note here<a style='mso-footnote-id:ftn1' href=\"#_ftn1\" name=\"_ftnref1\" title=\"\"><span class=MsoFootnoteReference><span style='mso-special-character:footnote'><![if !supportFootnotes]><span class=MsoFootnoteReference><span style='font-size:11.0pt'>[1]</span></span><![endif]></span></span></a>.<span style='mso-tab-count:1'>&nbsp;&nbsp;&nbsp; </span>Tabbed<o:p></o:p></p>\r\n" +
        "<!--EndFragment-->\r\n" +
        "<div style='mso-element:footnote-list'><![if !supportFootnotes]><br clear=all>\r\n<hr align=left size=1 width=\"33%\">\r\n<![endif]>\r\n" +
        "<div style='mso-element:footnote' id=ftn1>\r\n<p class=MsoFootnoteText><a style='mso-footnote-id:ftn1' href=\"#_ftnref1\" name=\"_ftn1\" title=\"\"><span class=MsoFootnoteReference><span style='mso-special-character:footnote'><![if !supportFootnotes]><span class=MsoFootnoteReference><span style='font-size:10.0pt'>[1]</span></span><![endif]></span></span></a> The footnote text.<o:p></o:p></p>\r\n</div>\r\n</div>\r\n" +
        "</body>\r\n</html>";

    [Fact]
    public void WordListParagraphsBecomeARealList()
    {
        var target = new Document();
        var r = HtmlImporter.Import(Word, target);
        var ps = Paragraphs(r);
        Assert.Equal("Title", ps[0].StyleId);
        Assert.Equal("Report title", ps[0].PlainText());
        Assert.Equal("Plain bold and red serif   text.", ps[1].PlainText());
        Assert.True(ps[2].IsEmpty);
        var b1 = ps.Single(p => p.PlainText() == "Bullet one");
        var sub = ps.Single(p => p.PlainText() == "Sub bullet");
        var b2 = ps.Single(p => p.PlainText() == "Bullet two");
        Assert.Equal(b1.Props.NumId, sub.Props.NumId);
        Assert.Equal(b1.Props.NumId, b2.Props.NumId);
        Assert.Equal(0, b1.Props.NumLevel);
        Assert.Equal(1, sub.Props.NumLevel);
        Assert.Equal("ListParagraph", b1.StyleId);
        var numbering = target.Numbering;
        Assert.True(numbering.Level(b1.Props.NumId!.Value, 0)!.IsBullet);
        Assert.Equal("•", numbering.Level(b1.Props.NumId.Value, 0)!.Text);
        Assert.Equal("◦", numbering.Level(b1.Props.NumId.Value, 1)!.Text);
        var three = ps.Single(p => p.PlainText() == "Number three");
        var letter = ps.Single(p => p.PlainText() == "Letter a");
        Assert.NotEqual(b1.Props.NumId, three.Props.NumId);
        Assert.Equal(three.Props.NumId, letter.Props.NumId);
        var l0 = numbering.Level(three.Props.NumId!.Value, 0)!;
        Assert.Equal("decimal", l0.Format);
        Assert.Equal(3, l0.Start);
        Assert.Equal("%1.", l0.Text);
        Assert.Equal("lowerLetter", numbering.Level(three.Props.NumId.Value, 1)!.Format);
        // Word's footnote became a real footnote, and mso-tab-count a tab.
        var noteParagraph = ps.Single(p => p.PlainText().StartsWith("Note here", StringComparison.Ordinal));
        Assert.Equal("Note here.\tTabbed", noteParagraph.PlainText());
        var reference = noteParagraph.Inlines.OfType<NoteReference>().Single();
        Assert.Equal("The footnote text.", target.Footnotes[reference.Id].Paragraphs.First().PlainText().Trim());
        Assert.Same(target.Footnotes[reference.Id], r.AddedNotes.Single());
        // mso- styles and class fonts are ignored when matching the destination.
        Assert.All(ps.SelectMany(p => p.Inlines).OfType<TextRun>(), t => Assert.Null(t.Props.FontAscii));
    }

    [Fact]
    public void WordHtmlKeepingSourceFormattingUsesItsClassFonts()
    {
        var r = HtmlImporter.Import(Word, new Document(), new HtmlImportOptions { KeepSourceFormatting = true });
        var serif = Run(r, "red serif");
        Assert.Equal("Times New Roman", serif.Props.FontAscii);
        Assert.Equal("C00000", serif.Props.Color);
        Assert.Equal("Calibri", Run(r, "Plain").Props.FontAscii);
        Assert.Equal(22, Run(r, "Plain").Props.Size);
        Assert.Equal("FF0000", Run(r, "title").Props.Color);
    }

    internal const string Google =
        "<meta charset=\"utf-8\"><b style=\"font-weight:normal;\" id=\"docs-internal-guid-1a2b3c4d-7fff-0b2c-9d8e-0123456789ab\">" +
        "<h1 dir=\"ltr\" style=\"line-height:1.38;margin-top:20pt;margin-bottom:6pt;\"><span style=\"font-size:20pt;font-family:Arial,sans-serif;color:#000000;background-color:transparent;font-weight:400;font-style:normal;font-variant:normal;text-decoration:none;vertical-align:baseline;white-space:pre;white-space:pre-wrap;\">Heading</span></h1>" +
        "<p dir=\"ltr\" style=\"line-height:1.38;margin-top:0pt;margin-bottom:0pt;\"><span style=\"font-size:11pt;font-family:Arial,sans-serif;color:#000000;background-color:transparent;font-weight:400;font-style:normal;font-variant:normal;text-decoration:none;vertical-align:baseline;white-space:pre;white-space:pre-wrap;\">Normal  </span>" +
        "<span style=\"font-size:11pt;font-family:Arial,sans-serif;color:#000000;background-color:transparent;font-weight:700;font-style:normal;font-variant:normal;text-decoration:none;vertical-align:baseline;white-space:pre;white-space:pre-wrap;\">bold</span>" +
        "<span style=\"font-size:11pt;font-family:Arial,sans-serif;color:#000000;background-color:transparent;font-weight:400;font-style:italic;font-variant:normal;text-decoration:none;vertical-align:baseline;white-space:pre;white-space:pre-wrap;\"> italic </span>" +
        "<span style=\"font-size:11pt;font-family:'Courier New',monospace;color:#ff0000;background-color:transparent;font-weight:400;font-style:normal;font-variant:normal;text-decoration:underline;-webkit-text-decoration-skip:none;text-decoration-skip-ink:none;vertical-align:baseline;white-space:pre;white-space:pre-wrap;\">red mono</span></p>" +
        "<ul style=\"margin-top:0;margin-bottom:0;padding-inline-start:48px;\"><li dir=\"ltr\" style=\"list-style-type:disc;font-size:11pt;font-family:Arial,sans-serif;color:#000000;background-color:transparent;font-weight:400;font-style:normal;font-variant:normal;text-decoration:none;vertical-align:baseline;white-space:pre;\" aria-level=\"1\"><p dir=\"ltr\" style=\"line-height:1.38;margin-top:0pt;margin-bottom:0pt;\" role=\"presentation\"><span style=\"font-size:11pt;font-family:Arial,sans-serif;color:#000000;font-weight:400;white-space:pre;white-space:pre-wrap;\">Item one</span></p></li>" +
        "<ul style=\"margin-top:0;margin-bottom:0;padding-inline-start:48px;\"><li dir=\"ltr\" style=\"list-style-type:circle;font-size:11pt;\" aria-level=\"2\"><p dir=\"ltr\" role=\"presentation\"><span style=\"font-size:11pt;white-space:pre-wrap;\">Sub item</span></p></li></ul></ul><br />" +
        "<ol style=\"margin-top:0;margin-bottom:0;padding-inline-start:48px;\"><li dir=\"ltr\" style=\"list-style-type:decimal;font-size:11pt;\" aria-level=\"1\"><p dir=\"ltr\" role=\"presentation\"><span style=\"font-size:11pt;\">First</span></p></li>" +
        "<li dir=\"ltr\" style=\"list-style-type:decimal;font-size:11pt;\" aria-level=\"1\"><p dir=\"ltr\" role=\"presentation\"><span style=\"font-size:11pt;\">Second</span></p></li></ol></b>";

    [Fact]
    public void GoogleDocsHtmlMatchingTheDestination()
    {
        var target = new Document();
        var r = Paste(Google, target: target);
        var ps = Paragraphs(r);
        Assert.Equal("Heading1", ps[0].StyleId);
        Assert.Null(Run(r, "Heading").Props.Bold);
        Assert.Equal("Normal  bold italic red mono", ps[1].PlainText());
        Assert.Null(Run(r, "Normal").Props.Bold);
        Assert.True(Run(r, "bold").Props.Bold);
        Assert.True(Run(r, "italic").Props.Italic);
        Assert.Null(Run(r, "italic").Props.Bold);
        var mono = Run(r, "red mono");
        Assert.Null(mono.Props.FontAscii);
        Assert.Null(mono.Props.Color);
        Assert.Equal("single", mono.Props.Underline);
        var item = ps.Single(p => p.PlainText() == "Item one");
        var sub = ps.Single(p => p.PlainText() == "Sub item");
        Assert.Equal(0, item.Props.NumLevel);
        Assert.Equal(1, sub.Props.NumLevel);
        Assert.True(target.Numbering.Level(item.Props.NumId!.Value, 0)!.IsBullet);
        var first = ps.Single(p => p.PlainText() == "First");
        var second = ps.Single(p => p.PlainText() == "Second");
        Assert.Equal(first.Props.NumId, second.Props.NumId);
        Assert.Equal("decimal", target.Numbering.Level(first.Props.NumId!.Value, 0)!.Format);
    }

    [Fact]
    public void GoogleDocsHtmlKeepingTheSourceFormatting()
    {
        var r = Paste(Google, keep: true);
        Assert.False(Run(r, "Heading").Props.Bold);
        Assert.Equal(40, Run(r, "Heading").Props.Size);
        var mono = Run(r, "red mono");
        Assert.Equal("Courier New", mono.Props.FontAscii);
        Assert.Equal("FF0000", mono.Props.Color);
        Assert.Equal("Arial", Run(r, "Normal").Props.FontAscii);
        Assert.Equal("000000", Run(r, "Normal").Props.Color);
    }

    [Fact]
    public void EntitiesAndWhitespaceFollowBrowserRules()
    {
        var r = Paste("<p>  Hello \n  <b> world </b>  again </p><p>&amp; &lt;tag&gt; caf&eacute; &#233; &#xE9; &#147;q&#148; &copy 2026 &unknown; &nbsp;x 10&nbsp;kg &#x1F600;</p>");
        var ps = Paragraphs(r);
        Assert.Equal("Hello world again", ps[0].PlainText());
        Assert.True(ps[0].Inlines.OfType<TextRun>().Single(t => t.Text.Contains("world")).Props.Bold);
        Assert.Equal("& <tag> café é é “q” © 2026 &unknown;  x 10\u00A0kg 😀", ps[1].PlainText());
    }

    [Fact]
    public void BrokenHtmlIsReadLikeABrowserWould()
    {
        var r = Paste("<p>one<p>two<ul><li>a<li>b</ul><table><tr><td>x<td>y</table><div>tail <b>bold</div> after");
        var ps = Paragraphs(r);
        Assert.Equal(new[] { "one", "two", "a", "b", "x", "y", "tail bold", "after" }, ps.Select(p => p.PlainText()));
        var table = r.Blocks.OfType<Table>().Single();
        Assert.Equal(2, table.Rows[0].Cells.Count);
        Assert.True(ps.Last().Inlines.OfType<TextRun>().Single().Props.Bold);
    }

    [Fact]
    public void ConditionalCommentsAndFragmentMarkers()
    {
        var png = Sta.Run(() => TestImages.Png(10, 10));
        var html = "<p>before</p><!--StartFragment--><!--[if gte mso 9]><p>hidden</p><![endif]--><p>shown</p>" +
                   "<!--[if gte vml 1]><v:shape><v:imagedata src=\"x.png\"/></v:shape><![endif]--><![if !vml]><img src=\"data:image/png;base64," + Convert.ToBase64String(png) + "\" width=10 height=10><![endif]>" +
                   "<!--EndFragment--><p>after</p>";
        var r = Paste(html);
        var ps = Paragraphs(r);
        Assert.Equal(2, ps.Count);
        Assert.Equal("shown", ps[0].PlainText());
        Assert.IsType<DrawingObject>(ps[1].Inlines.Single());
        Assert.Equal(1, r.ImagesImported);
    }

    [Fact]
    public void PicturesFromDataUrisAndFileUrlsOnly()
    {
        var png = Sta.Run(() => TestImages.Png(200, 100));
        using var dir = new TempDir();
        var path = dir.File("pic.png");
        File.WriteAllBytes(path, png);
        var notAPicture = dir.File("secret.txt");
        File.WriteAllText(notAPicture, "not an image");
        var html = "<p><img src=\"" + new Uri(path).AbsoluteUri + "\" alt=\"file\"><img src=\"data:image/png;base64," + Convert.ToBase64String(png) + "\" style=\"width:50px\">" +
                   "<img src=\"http://example.com/a.png\"><img src=\"" + new Uri(notAPicture).AbsoluteUri + "\"><img src=\"data:image/gif;base64,R0lGODlhAQABAAAAACw=\" width=\"1\" height=\"1\"></p>";
        var target = new Document();
        var r = Paste(html, target: target);
        var pictures = Paragraphs(r).Single().Inlines.OfType<DrawingObject>().ToList();
        Assert.Equal(2, pictures.Count);
        Assert.Equal("file", pictures[0].AltText);
        Assert.Equal(200 * 9525, pictures[0].Width);
        Assert.Equal(50 * 9525, pictures[1].Width);
        Assert.Equal(25 * 9525, pictures[1].Height);
        Assert.Equal(png, target.Media.Get(pictures[0].MediaId)!.Data);
        Assert.Equal(2, r.ImagesImported);
        Assert.Equal(2, r.ImagesSkipped);
    }

    [Fact]
    public void TablesWithMergesHeaderCellsAndShading()
    {
        var html = "<table border=\"1\"><tr><th>A</th><th>B</th><th>C</th></tr>" +
                   "<tr><td rowspan=\"3\" style=\"background-color:#eeeeee;vertical-align:middle\">tall</td><td colspan=\"2\">wide</td></tr>" +
                   "<tr><td>1</td><td>2</td></tr><tr><td colspan=\"2\">3</td></tr></table>";
        var keep = Paste(html, keep: true);
        var t = keep.Blocks.OfType<Table>().Single();
        Assert.Equal("TableGrid", t.Props.StyleId);
        Assert.Equal(4, t.Rows.Count);
        Assert.True(t.Rows[0].Props.IsHeader);
        Assert.Equal("restart", t.Rows[1].Cells[0].Props.VerticalMerge);
        Assert.Equal("EEEEEE", t.Rows[1].Cells[0].Props.ShadingFill);
        Assert.Equal("center", t.Rows[1].Cells[0].Props.VerticalAlignment);
        Assert.Equal(2, t.Rows[1].Cells[1].Props.GridSpan);
        Assert.Equal("continue", t.Rows[2].Cells[0].Props.VerticalMerge);
        Assert.Equal("continue", t.Rows[3].Cells[0].Props.VerticalMerge);
        Assert.Equal(2, t.Rows[3].Cells[1].Props.GridSpan);
        Assert.All(t.Rows, row => Assert.Equal(3, row.Cells.Sum(c => c.Props.GridSpan)));

        var match = Paste(html);
        var m = match.Blocks.OfType<Table>().Single();
        Assert.Equal("InkwellTable", m.Props.StyleId);
        Assert.Null(m.Rows[1].Cells[0].Props.ShadingFill);
    }

    [Fact]
    public void RightToLeftParagraphs()
    {
        var r = Paste("<div dir=\"rtl\"><p>اردو متن</p><p dir=\"ltr\">English</p></div>");
        var ps = Paragraphs(r);
        Assert.True(ps[0].Props.Bidi);
        Assert.Null(ps[1].Props.Bidi);
    }

    [Fact]
    public void MissingStylesAreAddedToTheTarget()
    {
        var target = new Document();
        target.Styles.Remove("Code");
        target.Styles.Remove("InlineCode");
        var r = Paste("<h5>deep</h5><pre>x</pre><p><code>y</code></p>", target: target);
        var ps = Paragraphs(r);
        Assert.Equal("Heading5", ps[0].StyleId);
        Assert.NotNull(target.Styles.Get("Heading5"));
        Assert.Equal("Code", ps[1].StyleId);
        Assert.NotNull(target.Styles.Get("Code"));
        Assert.NotNull(target.Styles.Get("InlineCode"));
    }

    [Fact]
    public void ClipboardParserFallsBackToTheMarkers()
    {
        var html = "Version:0.9\r\nStartHTML:-1\r\nEndHTML:-1\r\nStartFragment:999999\r\nEndFragment:999999\r\n<html><body><p>out</p><!--StartFragment--><p>in</p><!--EndFragment--></body></html>";
        var parts = CfHtml.Parse(html);
        Assert.False(parts.FromOffsets);
        Assert.Equal("<p>in</p>", parts.Fragment);
        var r = HtmlImporter.ImportClipboard(html, new Document());
        Assert.Equal("in", Paragraphs(r).Single().PlainText());
    }
}
