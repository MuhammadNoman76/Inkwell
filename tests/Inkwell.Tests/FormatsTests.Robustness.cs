using System.IO.Compression;
using System.Text;
using Inkwell.Core.Formats;
using Inkwell.Core.Model;

namespace Inkwell.Tests;

/// <summary>Damaged, truncated and hostile input: the importers must not crash, and what they produce must export.</summary>
public class FormatsTestsRobustness
{
    private static void ExportsCleanly(Document doc)
    {
        Assert.NotEmpty(HtmlExporter.Export(doc));
        MarkdownExporter.Export(doc);
        TextExporter.Export(doc);
        HtmlExporter.ExportClipboard(doc, doc.Body.Blocks);
    }

    private static byte[] Garbage(int length, int seed)
    {
        var random = new Random(seed);
        var bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }

    [Fact]
    public void TruncatedRtfNeverThrows()
    {
        var png = Sta.Run(() => TestImages.Png(8, 8));
        var full = Encoding.ASCII.GetBytes(FormatsTestsRtf.WordSample(png));
        for (var cut = 0; cut < full.Length; cut += 97)
        {
            var part = full.Take(cut).ToArray();
            var doc = RtfImporter.Import(part);
            RtfImporter.ImportFragment(part, new Document());
            if (cut % 970 == 0) ExportsCleanly(doc);
        }
        var wordPad = Encoding.ASCII.GetBytes(FormatsTestsRtf.WordPad);
        for (var cut = 0; cut < wordPad.Length; cut += 13) RtfImporter.Import(wordPad.Take(cut).ToArray());
    }

    [Fact]
    public void GarbageRtfNeverThrows()
    {
        for (var seed = 0; seed < 20; seed++)
        {
            var bytes = Garbage(2000, seed);
            var prefixed = Encoding.ASCII.GetBytes("{\\rtf1\\ansi ").Concat(bytes).ToArray();
            ExportsCleanly(RtfImporter.Import(prefixed));
        }
        var unbalanced = "}}}{\\rtf1{{{{\\pard\\intbl\\itap99999 x\\nestcell\\nestrow\\cell\\row\\cellx-5\\cellx-10\\row}\\bin999999999 \\u99999999 \\'zz\\";
        ExportsCleanly(RtfImporter.Import(unbalanced));
    }

    [Fact]
    public void TruncatedAndGarbageHtmlNeverThrows()
    {
        foreach (var sample in new[] { FormatsTestsHtmlImport.Word, FormatsTestsHtmlImport.Google, FormatsTestsHtmlImport.Browser })
        {
            for (var cut = 0; cut < sample.Length; cut += 61)
            {
                var part = sample.Substring(0, cut);
                HtmlImporter.Import(part, new Document());
                HtmlImporter.Import(part, new Document(), new HtmlImportOptions { KeepSourceFormatting = true });
            }
        }
        for (var seed = 0; seed < 20; seed++)
        {
            var text = Encoding.UTF8.GetString(Garbage(3000, seed)).Replace('\0', '<');
            HtmlImporter.Import(text, new Document());
            HtmlImporter.ImportClipboard(Garbage(500, seed), new Document());
        }
        HtmlImporter.Import("<table><tr><td rowspan=0 colspan=99999>x<td rowspan=-3><table><tr><td></table></table><ol start=-5><li value=x>", new Document());
    }

    [Fact]
    public void DeeplyNestedHtmlKeepsItsText()
    {
        var deep = string.Concat(Enumerable.Repeat("<div><span><b>", 5000)) + "deep text" + string.Concat(Enumerable.Repeat("</b></span></div>", 5000));
        var r = HtmlImporter.Import(deep, new Document());
        Assert.Contains(DocumentWalker.Paragraphs(r.Blocks), p => p.PlainText() == "deep text");
        var lists = string.Concat(Enumerable.Repeat("<ul><li>", 3000)) + "item";
        Assert.NotEmpty(HtmlImporter.Import(lists, new Document()).Blocks);
        var tables = string.Concat(Enumerable.Repeat("<table><tr><td>", 300)) + "cell";
        Assert.NotEmpty(HtmlImporter.Import(tables, new Document()).Blocks);
    }

    [Fact]
    public void OddMarkdownNeverThrows()
    {
        var samples = new[]
        {
            new string('>', 200) + " deep quote",
            string.Concat(Enumerable.Range(0, 60).Select(i => new string(' ', i * 2) + "- item\n")),
            "| a | b |\n|---|\n| 1 | 2 | 3 | 4 |\n",
            "[^missing] and [^1]\n\n[^1]: [^1]\n",
            "![x](data:image/png;base64,!!!!) ![y](../../nowhere.png) <div><table><tr><td>html",
            "```\nunclosed fence\n",
            "* * *\n- - -\n___\n",
        };
        foreach (var md in samples)
        {
            var doc = MarkdownImporter.Import(md);
            ExportsCleanly(doc);
            MarkdownImporter.ImportFragment(md, new Document());
        }
        for (var seed = 0; seed < 10; seed++) ExportsCleanly(MarkdownImporter.Import(Encoding.UTF8.GetString(Garbage(2000, seed))));
        // Markdig refuses absurd nesting: the text is kept as plain paragraphs, with a warning.
        var tooDeep = MarkdownImporter.Import(new string('>', 5000) + " x");
        Assert.NotEmpty(tooDeep.Warnings);
        Assert.Contains("x", tooDeep.Body.Paragraphs.First().PlainText());
    }

    [Fact]
    public void DamagedOdtPackages()
    {
        var garbageZip = new byte[] { (byte)'P', (byte)'K', 3, 4 }.Concat(Garbage(500, 1)).ToArray();
        Assert.ThrowsAny<InvalidDataException>(() => OdtImporter.Import(garbageZip));

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            zip.CreateEntry("mimetype");
        var empty = OdtImporter.Import(ms.ToArray());
        Assert.Single(empty.Body.Blocks);
        Assert.NotEmpty(empty.Warnings);

        const string ns = "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\"";
        var deep = "<office:document " + ns + "><office:body><office:text>" + string.Concat(Enumerable.Repeat("<text:list><text:list-item>", 2000)) + "<text:p>deep</text:p>" +
                   string.Concat(Enumerable.Repeat("</text:list-item></text:list>", 2000)) + "</office:text></office:body></office:document>";
        var doc = OdtImporter.Import(Encoding.UTF8.GetBytes(deep));
        ExportsCleanly(doc);
        Assert.Throws<System.Xml.XmlException>(() => OdtImporter.Import(Encoding.UTF8.GetBytes("<office:document><unclosed>")));
    }

    [Fact]
    public void GarbageTextDecodes()
    {
        for (var seed = 0; seed < 20; seed++)
        {
            var doc = TextImporter.Import(Garbage(1000, seed));
            Assert.All(doc.Body.Paragraphs, p => Assert.DoesNotContain(p.Text, c => c < ' ' && c != '\t' && c != '\v' && c != '\f' && c != '\u000E'));
            ExportsCleanly(doc);
        }
    }
}
