using Inkwell.Core.Formats;
using Inkwell.Core.Model;
using Inkwell.Core.Templates;

namespace Inkwell.Tests;

public class FormatsTestsMarkdown
{
    /// <summary>A rich sample written the way the exporter writes Markdown (so the round trip must give it back exactly).</summary>
    private const string Sample =
        "# Inkwell sample\n" +
        "\n" +
        "Some **bold**, *italic*, ~~struck~~ and `code` text with a [link](https://example.com \"Example\") and <https://inkwell.app>.\\\n" +
        "A second line after a hard break, ***both*** and <sup>sup</sup>/<sub>sub</sub>/<u>under</u>.\n" +
        "\n" +
        "## Lists\n" +
        "\n" +
        "- First bullet\n" +
        "- Second bullet with **bold**\n" +
        "  - Nested bullet\n" +
        "  - Another nested\n" +
        "- Third bullet\n" +
        "\n" +
        "3. Three\n" +
        "4. Four\n" +
        "   1. Four point one\n" +
        "5. Five\n" +
        "\n" +
        "- [ ] Open task\n" +
        "- [x] Done task\n" +
        "\n" +
        "### Quote and code\n" +
        "\n" +
        "> A quoted paragraph with *emphasis*.\n" +
        ">\n" +
        "> Second quote paragraph.\n" +
        "\n" +
        "```\n" +
        "static void Main()\n" +
        "{\n" +
        "\tConsole.WriteLine(\"tab kept\");\n" +
        "}\n" +
        "```\n" +
        "\n" +
        "#### Table\n" +
        "\n" +
        "| Name | Count | Price |\n" +
        "| :--- | :---: | ---: |\n" +
        "| Apples | 3 | 1.20 |\n" +
        "| Pears | 10 | 2.50 |\n" +
        "\n" +
        "---\n" +
        "\n" +
        "Escaped \\*stars\\*, \\[brackets\\], a\\_b and 2 \\< 3.\n" +
        "\n" +
        "Footnote reference here.[^1]\n" +
        "\n" +
        "[^1]: The footnote text.\n";

    private static List<Paragraph> Paragraphs(Document doc) => doc.Body.Paragraphs.ToList();

    [Fact]
    public void RoundTripGivesTheSameMarkdown()
    {
        var doc = MarkdownImporter.Import(Sample);
        var md = MarkdownExporter.Export(doc);
        Assert.Equal(Sample, md);
        var again = MarkdownExporter.Export(MarkdownImporter.Import(md));
        Assert.Equal(md, again);
    }

    [Fact]
    public void HeadingsParagraphsAndFormatting()
    {
        var doc = MarkdownImporter.Import(Sample);
        var body = doc.Body.Blocks.OfType<Paragraph>().ToList();
        Assert.Equal("Heading1", body[0].StyleId);
        Assert.Equal("Inkwell sample", body[0].PlainText());
        var p = body[1];
        Assert.Equal("Normal", p.StyleId);
        var runs = p.Inlines.OfType<TextRun>().ToList();
        Assert.True(runs.Single(r => r.Text == "bold").Props.Bold);
        Assert.True(runs.Single(r => r.Text == "italic").Props.Italic);
        Assert.True(runs.Single(r => r.Text == "struck").Props.Strike);
        Assert.Equal("InlineCode", runs.Single(r => r.Text == "code").Props.StyleId);
        var link = runs.Single(r => r.Text == "link");
        Assert.Equal("https://example.com", link.Link!.Url);
        Assert.Equal("Example", link.Link.Tooltip);
        Assert.Equal("Hyperlink", link.Props.StyleId);
        Assert.Equal("https://inkwell.app", runs.Single(r => r.Text == "https://inkwell.app").Link!.Url);
        Assert.Contains(TextRun.LineBreak, p.Text);
        var both = runs.Single(r => r.Text == "both");
        Assert.True(both.Props.Bold == true && both.Props.Italic == true);
        Assert.Equal("superscript", runs.Single(r => r.Text == "sup").Props.VertAlign);
        Assert.Equal("subscript", runs.Single(r => r.Text == "sub").Props.VertAlign);
        Assert.Equal("single", runs.Single(r => r.Text == "under").Props.Underline);
    }

    [Fact]
    public void ListsHaveLevelsFormatsAndStartNumbers()
    {
        var doc = MarkdownImporter.Import(Sample);
        var items = Paragraphs(doc).Where(p => p.Props.NumId != null).ToList();
        var nested = items.Single(p => p.PlainText() == "Nested bullet");
        var first = items.Single(p => p.PlainText() == "First bullet");
        Assert.Equal(1, nested.Props.NumLevel);
        Assert.Equal(0, first.Props.NumLevel);
        Assert.Equal(first.Props.NumId, nested.Props.NumId);
        Assert.Equal("ListParagraph", first.StyleId);
        Assert.True(doc.Numbering.Level(first.Props.NumId!.Value, 0)!.IsBullet);

        var three = items.Single(p => p.PlainText() == "Three");
        var level = doc.Numbering.Level(three.Props.NumId!.Value, 0)!;
        Assert.Equal("decimal", level.Format);
        Assert.Equal(3, level.Start);
        Assert.NotEqual(first.Props.NumId, three.Props.NumId);
        var sub = items.Single(p => p.PlainText() == "Four point one");
        Assert.Equal(1, sub.Props.NumLevel);
    }

    [Fact]
    public void TaskListsGetBoxesWithoutBullets()
    {
        var doc = MarkdownImporter.Import("- [ ] open\n- [x] done\n");
        var items = Paragraphs(doc).Where(p => p.Props.NumId != null).ToList();
        Assert.Equal("☐ open", items[0].PlainText());
        Assert.Equal("☑ done", items[1].PlainText());
        Assert.Equal("none", doc.Numbering.Level(items[0].Props.NumId!.Value, 0)!.Format);
    }

    [Fact]
    public void CodeBlocksKeepWhitespaceOneParagraphPerLine()
    {
        var doc = MarkdownImporter.Import("```cs\nif (x)\n\n    y();\n\tz();\n```\n\n    indented\n      more\n");
        var code = Paragraphs(doc).Where(p => p.StyleId == "Code").Select(p => p.PlainText()).ToList();
        Assert.Equal(new[] { "if (x)", "", "    y();", "\tz();", "indented", "  more" }, code);
    }

    [Fact]
    public void TablesUseTheInkwellStyleWithHeaderRowAndAlignment()
    {
        var doc = MarkdownImporter.Import("| A | B |\n| --- | ---: |\n| 1 | 2 |\n| 3 | **4** |\n");
        var table = doc.Body.Blocks.OfType<Table>().Single();
        Assert.Equal("InkwellTable", table.Props.StyleId);
        Assert.Equal(3, table.Rows.Count);
        Assert.True(table.Rows[0].Props.IsHeader);
        Assert.False(table.Rows[1].Props.IsHeader);
        Assert.Equal(2, table.Grid.Count);
        var cell = (Paragraph)table.Rows[2].Cells[1].Blocks[0];
        Assert.Equal("right", cell.Props.Justification);
        Assert.True(cell.Inlines.OfType<TextRun>().Single().Props.Bold);
        Assert.IsType<Paragraph>(doc.Body.Blocks.Last());
    }

    [Fact]
    public void FootnotesBecomeRealFootnotes()
    {
        var doc = MarkdownImporter.Import("Text[^a] and more[^b].\n\n[^a]: First *note*.\n[^b]: Second note.\n");
        Assert.Equal(2, doc.Footnotes.Count);
        var refs = doc.Body.Paragraphs.SelectMany(p => p.Inlines).OfType<NoteReference>().ToList();
        Assert.Equal(2, refs.Count);
        Assert.Equal("FootnoteReference", refs[0].Props.StyleId);
        var note = doc.Footnotes[refs[0].Id];
        var p = note.Paragraphs.First();
        Assert.Equal("FootnoteText", p.StyleId);
        Assert.IsType<NoteMark>(p.Inlines[0]);
        Assert.Equal("FootnoteReference", p.Inlines[0].Props.StyleId);
        Assert.Equal(" First note.", p.PlainText().Replace("\uFFFC", ""));
        Assert.True(p.Inlines.OfType<TextRun>().Single(r => r.Text == "note").Props.Italic);
    }

    [Fact]
    public void ThematicBreakIsADivider()
    {
        var doc = MarkdownImporter.Import("a\n\n***\n\nb\n");
        var divider = doc.Body.Blocks.OfType<Paragraph>().ElementAt(1);
        Assert.True(divider.IsEmpty);
        Assert.True(divider.Props.Border("bottom")!.Value.IsVisible);
        Assert.Equal("a\n\n---\n\nb\n", MarkdownExporter.Export(doc));
    }

    [Fact]
    public void DeepHeadingsCreateHeading4To6()
    {
        var doc = MarkdownImporter.Import("#### Four\n\n##### Five\n\n###### Six\n");
        var ps = Paragraphs(doc);
        Assert.Equal(new[] { "Heading4", "Heading5", "Heading6" }, ps.Take(3).Select(p => p.StyleId));
        Assert.Equal("Heading3", doc.Styles.Get("Heading4")!.BasedOn);
        Assert.Equal(3, doc.Styles.Get("Heading4")!.ParaProps()!.OutlineLevel);
        Assert.Equal(5, doc.Styles.Get("Heading6")!.ParaProps()!.OutlineLevel);
    }

    [Fact]
    public void ImportUsesTheGivenDesign()
    {
        var doc = MarkdownImporter.Import("# Title\n", design: DocumentDesigns.Classic);
        Assert.Equal("Playfair Display", doc.Theme.Font(true, "latin"));
        Assert.Equal(38, doc.Styles.Get("Heading1")!.RunProps()!.Size);
    }

    [Fact]
    public void PicturesFromDataUrisAndFilesWebPicturesKeepAltText()
    {
        var png = Sta.Run(() => TestImages.Png(1200, 300));
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("pic one.png"), png);
        var dataUri = "data:image/png;base64," + Convert.ToBase64String(png);
        var md = "![wide](<pic one.png>) ![inline](" + dataUri + ") ![web](https://example.com/x.png)\n";
        var result = MarkdownImporter.ImportFragment(md, new Document(), dir.Path);
        var p = (Paragraph)result.Blocks.Single();
        var pictures = p.Inlines.OfType<DrawingObject>().ToList();
        Assert.Equal(2, pictures.Count);
        Assert.Equal("wide", pictures[0].AltText);
        Assert.True(pictures[0].Width <= (long)(6.5 * 914400));
        Assert.Equal(pictures[0].Width / 4.0, pictures[0].Height, 0);
        Assert.Contains("web", p.PlainText());
        Assert.Equal(2, result.ImagesImported);
        Assert.Equal(1, result.ImagesSkipped);
        Assert.Equal("https://example.com/x.png", result.SkippedImageUrls.Single());
    }

    [Fact]
    public void ExportWritesPicturesNextToTheFile()
    {
        var png = Sta.Run(() => TestImages.Png(40, 20));
        using var dir = new TempDir();
        var doc = MarkdownImporter.Import("![a picture](data:image/png;base64," + Convert.ToBase64String(png) + ")\n");
        var path = dir.File("out.md");
        MarkdownExporter.ExportToFile(doc, path);
        Assert.Equal("![a picture](out_files/image1.png)\n", File.ReadAllText(path));
        Assert.Equal(png, File.ReadAllBytes(Path.Combine(dir.Path, "out_files", "image1.png")));
        var back = MarkdownImporter.ImportFile(path);
        var picture = back.Body.Paragraphs.SelectMany(x => x.Inlines).OfType<DrawingObject>().Single();
        Assert.Equal(png, back.Media.Get(picture.MediaId)!.Data);
        // Without a folder the alt text is written.
        Assert.Equal("a picture\n", MarkdownExporter.Export(doc));
    }

    [Fact]
    public void SpecialCharactersAreEscapedAndSurviveTheRoundTrip()
    {
        var texts = new[]
        {
            "1. not a list", "- not a bullet", "# not a heading", "> not a quote", "a *b* _c_ `d` [e](f) <g> ~h~ | i", "see http://example.com here",
            "www.example.com", "&amp; stays", "back\\slash", "+ plus", "== equals",
        };
        foreach (var text in texts)
        {
            var doc = new Document();
            doc.Body.Blocks.Add(new Paragraph(text));
            var md = MarkdownExporter.Export(doc);
            var back = MarkdownImporter.Import(md);
            var p = back.Body.Blocks.OfType<Paragraph>().First();
            Assert.Equal(text, p.PlainText());
            Assert.Equal("Normal", p.StyleId);
            Assert.DoesNotContain(p.Inlines, i => i.Link != null);
        }
    }

    [Fact]
    public void ListsFromWordNumberingExportWithTheirNumbers()
    {
        var doc = new Document();
        var lists = doc.Numbering;
        var abs = lists.AddAbstract(Numbering.StandardDefinition(false));
        var num = lists.AddNum(abs, 7);
        foreach (var text in new[] { "seven", "eight" })
        {
            var p = new Paragraph(text, "ListParagraph");
            p.Props.SetNumbering(num, 0);
            doc.Body.Blocks.Add(p);
        }
        var child = new Paragraph("child", "ListParagraph");
        child.Props.SetNumbering(num, 1);
        doc.Body.Blocks.Add(child);
        Assert.Equal("7. seven\n8. eight\n   1. child\n", MarkdownExporter.Export(doc));
    }

    [Fact]
    public void AdjacentListsStaySeparate()
    {
        var md = "- a\n- b\n\n* c\n* d\n";
        var doc = MarkdownImporter.Import(md);
        var ids = doc.Body.Paragraphs.Select(p => p.Props.NumId).Distinct().ToList();
        Assert.Equal(2, ids.Count);
        Assert.Equal(md, MarkdownExporter.Export(doc));
    }

    [Fact]
    public void FragmentUsesTheTargetsStyles()
    {
        var target = new Document();
        target.Styles.Remove("Quote");
        var result = MarkdownImporter.ImportFragment("> quoted\n\n1. one\n", target);
        Assert.Equal(2, result.Blocks.Count);
        Assert.Equal("Quote", ((Paragraph)result.Blocks[0]).StyleId);
        Assert.NotNull(target.Styles.Get("Quote"));
        Assert.True(target.Numbering.HasNum(((Paragraph)result.Blocks[1]).Props.NumId!.Value));
    }

    [Fact]
    public void ListItemsWithSeveralParagraphsAndCode()
    {
        var md = "1. Step one\n\n   More about step one.\n\n   ```\n   code here\n   ```\n2. Step two\n";
        var doc = MarkdownImporter.Import(md);
        var ps = Paragraphs(doc);
        Assert.Equal(0, ps[0].Props.NumLevel);
        Assert.Null(ps[1].Props.NumId);
        Assert.Equal(360, ps[1].Props.IndentStart);
        Assert.Equal("Code", ps[2].StyleId);
        Assert.Equal(360, ps[2].Props.IndentStart);
        var again = MarkdownExporter.Export(doc);
        Assert.Equal("1. Step one\n\n   More about step one.\n\n   ```\n   code here\n   ```\n\n2. Step two\n", again);
        Assert.Equal(again, MarkdownExporter.Export(MarkdownImporter.Import(again)));
    }
}
