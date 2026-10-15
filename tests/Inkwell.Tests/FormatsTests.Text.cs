using System.Text;
using Inkwell.Core.Formats;
using Inkwell.Core.Model;

namespace Inkwell.Tests;

public class FormatsTestsText
{
    [Fact]
    public void DetectsByteOrderMarks()
    {
        var text = "Grüße, اردو\tend";
        var utf8 = new UTF8Encoding(true);
        var cases = new (byte[] Bytes, string Name)[]
        {
            (utf8.GetPreamble().Concat(utf8.GetBytes(text)).ToArray(), "utf-8"),
            (Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray(), "utf-16"),
            (Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(text)).ToArray(), "utf-16BE"),
            (new UTF32Encoding(false, true).GetPreamble().Concat(new UTF32Encoding(false, true).GetBytes(text)).ToArray(), "utf-32"),
        };
        foreach (var (bytes, name) in cases)
        {
            var decoded = TextImporter.Decode(bytes, out var encoding);
            Assert.Equal(text, decoded);
            Assert.Equal(name, encoding.WebName);
        }
    }

    [Fact]
    public void ValidUtf8WithoutBomIsUtf8()
    {
        var bytes = Encoding.UTF8.GetBytes("naïve café — ok");
        Assert.Equal("naïve café — ok", TextImporter.Decode(bytes, out var encoding));
        Assert.Equal("utf-8", encoding.WebName);
    }

    [Fact]
    public void InvalidUtf8FallsBackToTheAnsiCodePage()
    {
        // "café résumé" in Windows-1252: é is the single byte E9, which isn't valid UTF-8 here.
        var bytes = new byte[] { 0x63, 0x61, 0x66, 0xE9, 0x20, 0x72, 0xE9, 0x73, 0x75, 0x6D, 0xE9, 0x20, 0x80 };
        Assert.Equal("café résumé €", TextImporter.Decode(bytes, out var encoding, fallbackCodePage: 1252));
        Assert.Equal(1252, encoding.CodePage);
        // Without an explicit code page the system's ANSI code page is used (whatever it is, it isn't UTF-8).
        TextImporter.Decode(bytes, out var system);
        Assert.NotEqual(65001, system.CodePage);
    }

    [Fact]
    public void Utf16WithoutBomIsDetected()
    {
        var bytes = Encoding.Unicode.GetBytes("plain ascii text in utf-16");
        Assert.Equal("plain ascii text in utf-16", TextImporter.Decode(bytes, out var encoding));
        Assert.Equal("utf-16", encoding.WebName);
    }

    [Fact]
    public void OneParagraphPerLineTabsKept()
    {
        var doc = TextImporter.Import(Encoding.UTF8.GetBytes("first\tcol\r\nsecond\n\nfourth\rfifth\n"));
        var texts = doc.Body.Paragraphs.Select(p => p.PlainText()).ToList();
        Assert.Equal(new[] { "first\tcol", "second", "", "fourth", "fifth" }, texts);
        Assert.All(doc.Body.Paragraphs, p => Assert.Null(p.StyleId));
    }

    [Fact]
    public void PasteHelperKeepsTrailingBreakAndRemovesControls()
    {
        var ps = TextImporter.ToParagraphs("a\u0001b\fc\nnext\n");
        Assert.Equal(3, ps.Count);
        Assert.Equal("ab\fc", ps[0].Text);
        Assert.Equal("next", ps[1].Text);
        Assert.True(ps[2].IsEmpty);
        Assert.Single(TextImporter.ToParagraphs(""));
    }

    private static Document Sample()
    {
        var doc = new Document();
        doc.Body.Blocks.Add(new Paragraph("Title", "Title"));
        var abs = doc.Numbering.AddAbstract(Numbering.StandardDefinition(false));
        var num = doc.Numbering.AddNum(abs, 1);
        var bulletsAbs = doc.Numbering.AddAbstract(Numbering.StandardDefinition(true));
        var bullets = doc.Numbering.AddNum(bulletsAbs);
        foreach (var (text, level) in new[] { ("one", 0), ("one a", 1), ("one b", 1), ("two", 0) })
        {
            var p = new Paragraph(text, "ListParagraph");
            p.Props.SetNumbering(num, level);
            doc.Body.Blocks.Add(p);
        }
        var b = new Paragraph("bullet", "ListParagraph");
        b.Props.SetNumbering(bullets, 0);
        doc.Body.Blocks.Add(b);
        var t = new Table();
        foreach (var row in new[] { new[] { "A", "B", "C" }, new[] { "1", "2", "3" } })
        {
            var tr = new TableRow();
            foreach (var cell in row) tr.AddCell(TableCell.Create(1000, new Paragraph(cell)));
            t.AddRow(tr);
        }
        t.Rows[1].Cells[0].Props.GridSpan = 2;
        t.Rows[1].Cells.RemoveAt(1);
        doc.Body.Blocks.Add(t);
        var note = new Note(NoteKind.Footnote) { Id = 2 };
        var np = new Paragraph();
        np.Inlines.Add(new NoteMark());
        np.Inlines.Add(new TextRun(" The note."));
        note.Blocks.Add(np);
        doc.Footnotes[2] = note;
        var withNote = new Paragraph("Line one\vline two");
        withNote.Inlines.Add(new NoteReference { Id = 2 });
        doc.Body.Blocks.Add(withNote);
        return doc;
    }

    [Fact]
    public void ExportWritesLabelsTablesAndNotesWithCrLf()
    {
        var text = TextExporter.Export(Sample());
        var expected = string.Join("\r\n",
            "Title", "1. one", "  a. one a", "  b. one b", "2. two", "• bullet", "A\tB\tC", "1\t\t3", "Line one", "line two[1]", "", "__________", "[1] The note.");
        Assert.Equal(expected, text);
    }

    [Fact]
    public void ExportBytesHasOptionalBom()
    {
        var doc = new Document();
        doc.Body.Blocks.Add(new Paragraph("é"));
        Assert.Equal(new byte[] { 0xC3, 0xA9 }, TextExporter.ExportBytes(doc));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF, 0xC3, 0xA9 }, TextExporter.ExportBytes(doc, new TextExportOptions { IncludeBom = true }));
        using var dir = new TempDir();
        var path = dir.File("x.txt");
        TextExporter.ExportToFile(doc, path, new TextExportOptions { IncludeBom = true });
        Assert.Equal("é", TextImporter.ImportFile(path).Body.Paragraphs.First().PlainText());
    }
}
