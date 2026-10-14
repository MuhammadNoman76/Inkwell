using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Inkwell.Core.IO;
using Inkwell.Core.Model;

namespace Inkwell.Tests;

public class DocxRobustnessTests
{
    private static readonly XNamespace W = Ns.W;

    private static DocxException Fails(byte[] bytes, string? name = "file.docx") =>
        Assert.Throws<DocxException>(() => DocxReader.Load(new MemoryStream(bytes), name));

    /// <summary>A compound file (OLE) header followed by a directory entry name.</summary>
    private static byte[] CompoundFile(string streamName)
    {
        var bytes = new byte[2048];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(bytes, 0);
        Encoding.Unicode.GetBytes(streamName).CopyTo(bytes, 1024);
        return bytes;
    }

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var (name, content) in entries)
            {
                using var s = zip.CreateEntry(name).Open();
                var b = new UTF8Encoding(false).GetBytes(content);
                s.Write(b, 0, b.Length);
            }
        }
        return ms.ToArray();
    }

    private static byte[] Replace(byte[] package, string name, string content)
    {
        using var ms = new MemoryStream();
        ms.Write(package, 0, package.Length);
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Update, true))
        {
            zip.GetEntry(name)!.Delete();
            using var s = zip.CreateEntry(name).Open();
            var b = new UTF8Encoding(false).GetBytes(content);
            s.Write(b, 0, b.Length);
        }
        return ms.ToArray();
    }

    [Fact]
    public void Validator_ReportsInvalidPackages()
    {
        var bad = TestDocx.Create("<w:p><w:tbl/></w:p>");
        Assert.ThrowsAny<Exception>(() => Docx.AssertValid(bad));
    }

    [Fact]
    public void EmptyFile_Throws() => Assert.Equal(DocxErrorKind.Empty, Fails(Array.Empty<byte>()).Kind);

    [Fact]
    public void RandomBytes_AreNotAWordDocument()
    {
        var bytes = new byte[5000];
        new Random(7).NextBytes(bytes);
        bytes[0] = 0x12;
        var ex = Fails(bytes, "notes.txt");
        Assert.Equal(DocxErrorKind.NotAWordDocument, ex.Kind);
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void PasswordProtectedDocument_SaysSo()
    {
        var ex = Fails(CompoundFile("EncryptedPackage"));
        Assert.Equal(DocxErrorKind.PasswordProtected, ex.Kind);
        Assert.Contains("password", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Word97Document_SaysToSaveAsDocx()
    {
        var ex = Fails(CompoundFile("WordDocument"), "old.doc");
        Assert.Equal(DocxErrorKind.LegacyWordFormat, ex.Kind);
        Assert.Contains(".docx", ex.Message);
    }

    [Fact]
    public void OtherFormats_AreNamed()
    {
        Assert.Contains("RTF", Fails(Encoding.ASCII.GetBytes("{\\rtf1\\ansi Hello}"), "a.docx").Message);
        Assert.Contains("PDF", Fails(Encoding.ASCII.GetBytes("%PDF-1.7 ..."), "a.docx").Message);
        Assert.Contains("OpenDocument", Fails(Zip(("mimetype", "application/vnd.oasis.opendocument.text"), ("content.xml", "<x/>"))).Message);
        Assert.Equal(DocxErrorKind.NotAWordDocument, Fails(Zip(("readme.txt", "hello"))).Kind);

        using var ms = new MemoryStream();
        using (var xlsx = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook))
        {
            var wb = xlsx.AddWorkbookPart();
            TestDocx.Write(wb, "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheets/></workbook>");
        }
        var excel = Fails(ms.ToArray(), "book.docx");
        Assert.Equal(DocxErrorKind.OtherFormat, excel.Kind);
        Assert.Contains("Excel", excel.Message);
    }

    [Fact]
    public void TruncatedFile_IsDamaged()
    {
        var doc = new Document();
        for (var i = 0; i < 50; i++) doc.Body.Blocks.Add(new Paragraph("Paragraph number " + i + " with some text to make the file bigger."));
        var bytes = Docx.Save(doc);
        var ex = Fails(bytes.Take(bytes.Length / 2).ToArray());
        Assert.Equal(DocxErrorKind.Damaged, ex.Kind);
    }

    [Fact]
    public void MalformedMainPart_IsDamaged()
    {
        var good = TestDocx.Create(TestDocx.P("x"));
        Assert.Equal(DocxErrorKind.Damaged, Fails(Replace(good, "word/document.xml", "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p>")).Kind);
        Assert.Equal(DocxErrorKind.Damaged, Fails(Replace(good, "word/document.xml", "<html><body/></html>")).Kind);
    }

    [Fact]
    public void UnusualContent_DoesNotCrash()
    {
        var bytes = TestDocx.Create(
            "<w:p><w:r><w:t>a</w:t><w:unknownThing w:val=\"1\"/><w:sym w:font=\"Symbol\" w:char=\"zz\"/><w:br w:type=\"weird\"/></w:r>" +
            "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r></w:p>" +
            "<w:tbl><w:tblPr/><w:tblGrid/></w:tbl>" +
            "<w:tbl><w:tblPr/><w:tr><w:tc/></w:tr></w:tbl>" +
            "<w:p><w:hyperlink r:id=\"rIdMissing\"><w:r><w:t>broken link</w:t></w:r></w:hyperlink><w:r><w:drawing/></w:r></w:p>" +
            "<w:customXml w:element=\"x\"><w:p><w:r><w:t>inside custom xml</w:t></w:r></w:p></w:customXml>" +
            "<w:unknownBlock/>");
        var doc = Docx.Load(bytes);
        Assert.Contains("inside custom xml", Docx.Text(doc));
        Assert.Contains(doc.Warnings, w => w.Contains("Custom XML"));
        var saved = Docx.Save(doc);
        Docx.Load(saved);
        Assert.Empty(Docx.Body(saved).Descendants(W + "customXml"));
    }

    [Fact]
    public void PackageWithOnlyTheMainPart_OpensAndSavesComplete()
    {
        var bytes = TestDocx.Create(TestDocx.P("Bare"), styles: false);
        var doc = Docx.Load(bytes);
        Assert.Equal("Bare", Docx.Text(doc));
        Assert.Empty(doc.Styles.All);
        var saved = Docx.Save(doc);
        Docx.AssertValid(saved);
        Assert.Contains("word/styles.xml", Docx.PartNames(saved));
        Assert.Contains("word/settings.xml", Docx.PartNames(saved));
        Assert.Contains("word/theme/theme1.xml", Docx.PartNames(saved));
    }

    [Fact]
    public void MalformedHyperlinkAddress_IsTolerated()
    {
        var bytes = TestDocx.Create("<w:p><w:hyperlink r:id=\"rIdLink\"><w:r><w:t>odd link</w:t></w:r></w:hyperlink></w:p>",
            (_, main) => main.AddHyperlinkRelationship(new Uri("https://example.com/placeholder"), true, "rIdLink"));
        var rels = Encoding.UTF8.GetString(Docx.PartBytes(bytes, "word/_rels/document.xml.rels")!).Replace("https://example.com/placeholder", "https://exa mple.com/a b|c");
        bytes = Replace(bytes, "word/_rels/document.xml.rels", rels);
        var doc = Docx.Load(bytes);
        Assert.Equal("odd link", Docx.Text(doc));
        var saved = Docx.Save(doc);
        Docx.AssertValid(saved);
    }

    [Fact]
    public void StrictOpenXml_IsConvertedOnOpen()
    {
        const string w = "http://purl.oclc.org/ooxml/wordprocessingml/main";
        const string r = "http://purl.oclc.org/ooxml/officeDocument/relationships";
        var bytes = Zip(
            ("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                                    "<Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
                                    "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/></Types>"),
            ("_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"" + r + "/officeDocument\" Target=\"word/document.xml\"/></Relationships>"),
            ("word/_rels/document.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"" + r + "/styles\" Target=\"styles.xml\"/>" +
                                             "<Relationship Id=\"rId2\" Type=\"" + r + "/hyperlink\" Target=\"https://example.com/\" TargetMode=\"External\"/></Relationships>"),
            ("word/document.xml", "<w:document xmlns:w=\"" + w + "\" xmlns:r=\"" + r + "\" w:conformance=\"strict\"><w:body><w:p><w:pPr><w:pStyle w:val=\"Title\"/><w:jc w:val=\"start\"/></w:pPr>" +
                                  "<w:r><w:rPr><w:b w:val=\"true\"/></w:rPr><w:t>Strict </w:t></w:r><w:hyperlink r:id=\"rId2\"><w:r><w:t>link</w:t></w:r></w:hyperlink></w:p><w:sectPr/></w:body></w:document>"),
            ("word/styles.xml", "<w:styles xmlns:w=\"" + w + "\"><w:style w:type=\"paragraph\" w:styleId=\"Title\"><w:name w:val=\"Title\"/></w:style></w:styles>"));
        var doc = Docx.Load(bytes);
        Assert.Contains(doc.Warnings, x => x.Contains("Strict"));
        var p = (Paragraph)doc.Body.Blocks[0];
        Assert.Equal("Strict link", p.PlainText());
        Assert.Equal("Title", p.StyleId);
        Assert.True(p.Inlines[0].Props.Bold);
        Assert.Equal("https://example.com/", p.Inlines[1].Link!.Url);
        Assert.NotNull(doc.Styles.Get("Title"));
        Assert.Null(doc.DocumentShell!.Attribute(W + "conformance"));

        var saved = Docx.Save(doc);
        Docx.AssertValid(saved);
        Assert.DoesNotContain("purl.oclc.org", Encoding.UTF8.GetString(Docx.PartBytes(saved, "word/document.xml")!));
        Assert.Equal("Strict link", Docx.Text(Docx.Load(saved)));
    }

    [Fact]
    public void LoadFromAMissingPath_IsAFileAccessError()
    {
        using var dir = new TempDir();
        var ex = Assert.Throws<DocxException>(() => DocxReader.Load(dir.File("missing.docx")));
        Assert.Equal(DocxErrorKind.FileAccess, ex.Kind);
    }

    [Fact]
    public void SaveToAPath_IsAtomicAndLeavesNoTemporaryFiles()
    {
        using var dir = new TempDir();
        var path = dir.File("report.docx");
        var doc = new Document();
        doc.Body.Blocks.Add(new Paragraph("Version one"));
        DocxWriter.Save(doc, path);
        Assert.Equal("Version one", Docx.Text(DocxReader.Load(path)));

        ((Paragraph)doc.Body.Blocks[0]).Inlines.Add(new TextRun(", then two"));
        DocxWriter.Save(doc, path);
        var loaded = DocxReader.Load(path);
        Assert.Equal("Version one, then two", Docx.Text(loaded));
        Assert.Equal(new[] { "report.docx" }, Directory.GetFiles(dir.Path).Select(Path.GetFileName).ToArray());

        // A read-only target can't be replaced: the save fails cleanly and the file is untouched.
        var before = File.ReadAllBytes(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            ((Paragraph)loaded.Body.Blocks[0]).Inlines.Add(new TextRun(", then three"));
            var ex = Assert.Throws<DocxException>(() => DocxWriter.Save(loaded, path));
            Assert.Equal(DocxErrorKind.FileAccess, ex.Kind);
            Assert.True(File.ReadAllBytes(path).SequenceEqual(before));
            Assert.Single(Directory.GetFiles(dir.Path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void SavingTwice_DoesNotGrowThePackage()
    {
        var doc = new Document();
        var png = TestImages.Png();
        var p = new Paragraph("Picture: ");
        p.Inlines.Add(new DrawingObject { MediaId = doc.Media.Add(png, "image/png"), Width = 914400, Height = 914400 });
        p.Inlines.Add(new TextRun(" link") { Link = new Hyperlink { Url = "https://example.com" } });
        doc.Body.Blocks.Add(p);
        var first = Docx.Save(doc);
        var second = Docx.Save(doc);
        var third = Docx.Save(Docx.Load(second));
        Assert.Equal(Docx.PartNames(first).OrderBy(n => n), Docx.PartNames(third).OrderBy(n => n));
        Assert.Equal(Docx.PartXml(first, "word/_rels/document.xml.rels").Root!.Elements().Count(), Docx.PartXml(third, "word/_rels/document.xml.rels").Root!.Elements().Count());
    }
}
