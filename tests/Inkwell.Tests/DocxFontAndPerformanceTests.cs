using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using Inkwell.Core.IO;
using Inkwell.Core.Model;
using Inkwell.Core.Rendering;

namespace Inkwell.Tests;

public class DocxFontTests
{
    private static readonly XNamespace W = Ns.W;

    [Fact]
    public void Obfuscation_FollowsTheSpecAndIsItsOwnInverse()
    {
        const string key = "{00112233-4455-6677-8899-AABBCCDDEEFF}";
        var keyBytes = DocxFonts.KeyBytes(key);
        // The GUID's bytes in written order, reversed.
        Assert.Equal(new byte[] { 0xFF, 0xEE, 0xDD, 0xCC, 0xBB, 0xAA, 0x99, 0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11, 0x00 }, keyBytes);
        var font = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        var obfuscated = DocxFonts.Obfuscate(font, key);
        Assert.Equal((byte)(0 ^ 0xFF), obfuscated[0]);
        Assert.Equal((byte)(16 ^ 0xFF), obfuscated[16]);
        Assert.Equal((byte)(31 ^ 0x00), obfuscated[31]);
        Assert.Equal(font.Skip(32), obfuscated.Skip(32));
        Assert.Equal(font, DocxFonts.Deobfuscate(obfuscated, key));
    }

    [Fact]
    public void EmbedFonts_EmbedsTheBundledFontsTheDocumentUses_AndTheyReadBack()
    {
        Assert.True(Directory.Exists(FontCatalog.Directory), "The bundled fonts should be next to the tests: " + FontCatalog.Directory);
        var doc = new Document(); // theme fonts: Literata (not shipped as files here) and Inter
        var p = new Paragraph("Body in the theme font. ");
        var code = new TextRun("code();");
        code.Props.SetLatinFont("JetBrains Mono");
        p.Inlines.Add(code);
        var arial = new TextRun(" Arial isn't bundled, so it is never embedded.");
        arial.Props.SetLatinFont("Arial");
        p.Inlines.Add(arial);
        doc.Body.Blocks.Add(p);

        var bytes = Docx.Save(doc, new DocxSaveOptions { EmbedFonts = true });
        Docx.AssertValid(bytes);
        var fontParts = Docx.PartNames(bytes).Where(n => n.StartsWith("word/fonts/", StringComparison.Ordinal)).ToList();
        Assert.All(fontParts, n => Assert.EndsWith(".odttf", n));
        Assert.Equal(DocxFonts.ObfuscatedFontContentType, Docx.ContentTypeOf(bytes, "/" + fontParts[0]));
        Assert.NotNull(Docx.PartXml(bytes, "word/settings.xml").Root!.Element(W + "embedTrueTypeFonts"));
        var table = Docx.PartXml(bytes, "word/fontTable.xml").Root!;
        var inter = table.Elements(W + "font").Single(f => (string?)f.Attribute(W + "name") == "Inter");
        Assert.NotNull(inter.Element(W + "embedRegular")!.Attribute(W + "fontKey"));
        Assert.DoesNotContain(table.Elements(W + "font"), f => (string?)f.Attribute(W + "name") == "Arial" && f.Elements().Any(e => e.Name.LocalName.StartsWith("embed", StringComparison.Ordinal)));

        var embedded = DocxFonts.Read(bytes);
        var expected = FontCatalog.FilesFor("Inter").Concat(FontCatalog.FilesFor("JetBrains Mono")).ToList();
        Assert.Equal(expected.Count, embedded.Count);
        Assert.Equal(fontParts.Count, embedded.Count);
        foreach (var font in embedded)
        {
            var file = expected.Single(f => Path.GetFileName(f.File).StartsWith(font.Family.Replace(" ", ""), StringComparison.Ordinal) && f.Bold == font.Bold && f.Italic == font.Italic);
            Assert.True(File.ReadAllBytes(file.File).SequenceEqual(font.Data), font.Family + " " + font.Bold + " " + font.Italic);
            Assert.True(font.Data[0] == 0 && font.Data[1] == 1 || Encoding.ASCII.GetString(font.Data, 0, 4) == "OTTO");
        }

        // Embedding again replaces the fonts instead of adding more parts.
        var loaded = Docx.Load(bytes);
        var again = Docx.Save(loaded, new DocxSaveOptions { EmbedFonts = true });
        Docx.AssertValid(again);
        Assert.Equal(fontParts.Count, Docx.PartNames(again).Count(n => n.StartsWith("word/fonts/", StringComparison.Ordinal)));
        Assert.Equal(embedded.Count, DocxFonts.Read(loaded).Count);

        // Saving without the option keeps what is already embedded.
        var kept = Docx.Save(Docx.Load(again));
        Assert.Equal(fontParts.Count, Docx.PartNames(kept).Count(n => n.StartsWith("word/fonts/", StringComparison.Ordinal)));
    }
}

public class DocxPerformanceTests
{
    private readonly ITestOutputHelper _output;

    public DocxPerformanceTests(ITestOutputHelper output) => _output = output;

    private static readonly string[] Words =
        "the quick brown fox jumps over a lazy dog while writers draft chapters about history science music travel design and the craft of clear sentences"
            .Split(' ');

    /// <summary>About 300 pages: 3000 paragraphs of ~60 words, headings every 100 paragraphs, 30 tables, some formatting and links.</summary>
    private static Document BigDocument()
    {
        var doc = new Document();
        var rnd = new Random(42);
        var sb = new StringBuilder();
        var tableCount = 0;
        for (var i = 0; i < 3000; i++)
        {
            if (i % 100 == 0) doc.Body.Blocks.Add(new Paragraph("Chapter " + (i / 100 + 1), "Heading1"));
            sb.Clear();
            for (var w = 0; w < 60; w++) sb.Append(Words[rnd.Next(Words.Length)]).Append(' ');
            var p = new Paragraph(sb.ToString(0, sb.Length - 1));
            if (i % 7 == 0)
            {
                var bold = new TextRun(" Important.");
                bold.Props.Bold = true;
                p.Inlines.Add(bold);
            }
            if (i % 50 == 0) p.Inlines.Add(new TextRun(" link") { Link = new Hyperlink { Url = "https://example.com/" + i } });
            doc.Body.Blocks.Add(p);
            if (i % 100 == 50 && tableCount < 30)
            {
                tableCount++;
                var t = new Table { Grid = new List<int> { 2250, 2250, 2250, 2250 } };
                t.Props.StyleId = "TableGrid";
                for (var r = 0; r < 6; r++)
                {
                    var row = new TableRow();
                    for (var c = 0; c < 4; c++) row.AddCell(TableCell.Create(2250, new Paragraph("Cell " + r + "." + c)));
                    t.AddRow(row);
                }
                doc.Body.Blocks.Add(t);
            }
        }
        return doc;
    }

    /// <summary>
    /// The same size of document as Word writes it: rsid and paragraph-id attributes, a run every few words (as
    /// editing sessions leave them), proofing marks, about 5 MB of document.xml.
    /// </summary>
    private static byte[] WordStyleDocument()
    {
        var rnd = new Random(7);
        var sb = new StringBuilder(6_000_000);
        string Rsid() => rnd.Next(0x100000, 0xFFFFFF).ToString("X8");
        var tables = 0;
        for (var i = 0; i < 3000; i++)
        {
            if (i % 100 == 0)
            {
                sb.Append("<w:p w14:paraId=\"").Append((0x10000000 + i * 2).ToString("X8")).Append("\" w14:textId=\"77777777\" w:rsidR=\"").Append(Rsid()).Append("\" w:rsidRDefault=\"").Append(Rsid())
                  .Append("\"><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr><w:r><w:t>Chapter ").Append(i / 100 + 1).Append("</w:t></w:r></w:p>");
            }
            sb.Append("<w:p w14:paraId=\"").Append((0x10000001 + i * 2).ToString("X8")).Append("\" w14:textId=\"").Append(Rsid()).Append("\" w:rsidR=\"").Append(Rsid())
              .Append("\" w:rsidRDefault=\"").Append(Rsid()).Append("\" w:rsidP=\"").Append(Rsid()).Append("\"><w:pPr><w:spacing w:after=\"160\" w:line=\"259\" w:lineRule=\"auto\"/><w:jc w:val=\"both\"/></w:pPr>");
            for (var run = 0; run < 8; run++)
            {
                var bold = run == 3 && i % 5 == 0;
                if (run == 5) sb.Append("<w:proofErr w:type=\"spellStart\"/>");
                sb.Append("<w:r w:rsidR=\"").Append(Rsid()).Append("\"><w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\" w:cs=\"Calibri\"/>");
                if (bold) sb.Append("<w:b/><w:bCs/>");
                sb.Append("<w:sz w:val=\"22\"/><w:szCs w:val=\"22\"/><w:lang w:val=\"en-US\"/></w:rPr><w:t xml:space=\"preserve\">");
                for (var w = 0; w < 8; w++) sb.Append(Words[rnd.Next(Words.Length)]).Append(' ');
                sb.Append("</w:t></w:r>");
                if (run == 5) sb.Append("<w:proofErr w:type=\"spellEnd\"/>");
            }
            sb.Append("</w:p>");
            if (i % 100 == 50 && tables++ < 30)
            {
                sb.Append("<w:tbl><w:tblPr><w:tblStyle w:val=\"TableGrid\"/><w:tblW w:w=\"0\" w:type=\"auto\"/><w:tblLook w:val=\"04A0\" w:firstRow=\"1\" w:lastRow=\"0\" w:firstColumn=\"1\" w:lastColumn=\"0\" w:noHBand=\"0\" w:noVBand=\"1\"/></w:tblPr>")
                  .Append("<w:tblGrid><w:gridCol w:w=\"2337\"/><w:gridCol w:w=\"2337\"/><w:gridCol w:w=\"2338\"/><w:gridCol w:w=\"2338\"/></w:tblGrid>");
                for (var r = 0; r < 6; r++)
                {
                    sb.Append("<w:tr w:rsidR=\"").Append(Rsid()).Append("\">");
                    for (var c = 0; c < 4; c++)
                        sb.Append("<w:tc><w:tcPr><w:tcW w:w=\"2337\" w:type=\"dxa\"/></w:tcPr><w:p w:rsidR=\"").Append(Rsid()).Append("\"><w:r><w:t>Cell ").Append(r).Append('.').Append(c).Append("</w:t></w:r></w:p></w:tc>");
                    sb.Append("</w:tr>");
                }
                sb.Append("</w:tbl>");
            }
        }
        return TestDocx.Create(sb.ToString());
    }

    [Fact]
    public void ThreeHundredPageDocument_LoadsAndSavesQuickly()
    {
        var bytes = WordStyleDocument();
        var documentXml = Docx.PartBytes(bytes, "word/document.xml")!.Length;

        // Warm-up (JIT), then measure.
        Docx.Save(Docx.Load(bytes));
        var sw = Stopwatch.StartNew();
        var loaded = Docx.Load(bytes);
        var load = sw.Elapsed;
        sw.Restart();
        var saved = Docx.Save(loaded);
        var save = sw.Elapsed;

        var built = BigDocument();
        sw.Restart();
        var builtBytes = Docx.Save(built);
        var saveNew = sw.Elapsed;

        Assert.Equal(3030, loaded.Body.Blocks.OfType<Paragraph>().Count());
        Assert.Equal(30, loaded.Body.Blocks.OfType<Table>().Count());
        Assert.True(saved.Length > 0 && builtBytes.Length > 0);
        _output.WriteLine($"Word-style document.xml: {documentXml / 1024.0 / 1024.0:0.00} MB (package {bytes.Length / 1024} KB)");
        _output.WriteLine($"load: {load.TotalMilliseconds:0} ms");
        _output.WriteLine($"save (from the loaded package): {save.TotalMilliseconds:0} ms");
        _output.WriteLine($"save (same size, new document built with the model): {saveNew.TotalMilliseconds:0} ms");
        Assert.True(documentXml > 4_000_000, "the test document should be about 5 MB");
        Assert.True(load < TimeSpan.FromSeconds(5), "load took " + load);
        Assert.True(save < TimeSpan.FromSeconds(10), "save took " + save);
    }
}
