using System.Diagnostics;
using Inkwell.Core.Editing;
using Inkwell.Core.Model;

namespace Inkwell.Tests;

/// <summary>Document statistics. The expected counts in <see cref="CountText_MatchesWord"/> were measured with
/// Microsoft Word's own statistics (Range.ComputeStatistics) on the same strings.</summary>
public class DocumentStatsTests
{
    private readonly ITestOutputHelper _output;

    public DocumentStatsTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData("word—word", 2, 9, 9, 0)]
    [InlineData("word–word", 2, 9, 9, 0)]
    [InlineData("word – word", 2, 9, 11, 0)]
    [InlineData("word - word", 3, 9, 11, 0)]
    [InlineData("word -- word", 3, 10, 12, 0)]
    [InlineData("x—y—z", 3, 5, 5, 0)]
    [InlineData("a – – b", 2, 4, 7, 0)]
    [InlineData("a — b", 2, 3, 5, 0)]
    [InlineData("—word", 1, 5, 5, 0)]
    [InlineData("word— next", 2, 9, 10, 0)]
    [InlineData("a • b", 2, 3, 5, 0)]
    [InlineData("a•b", 2, 3, 3, 0)]
    [InlineData("a & b", 3, 3, 5, 0)]
    [InlineData("a ... b", 3, 5, 7, 0)]
    [InlineData("word…word", 1, 9, 9, 0)]
    [InlineData("word/word", 1, 9, 9, 0)]
    [InlineData("Hello, world!", 2, 12, 13, 0)]
    [InlineData("don't stop", 2, 9, 10, 0)]
    [InlineData("3.14 is pi", 3, 8, 10, 0)]
    [InlineData("e-mail user@example.com http://x.y/z", 3, 34, 36, 0)]
    [InlineData("  leading and trailing  ", 3, 18, 24, 0)]
    [InlineData("tab\tseparated", 2, 12, 13, 0)]
    [InlineData("line\vbreak", 2, 9, 10, 0)]
    [InlineData("10\u00A0kg", 2, 4, 5, 0)]
    [InlineData("a\u00A0\u00A0b", 2, 2, 4, 0)]
    [InlineData("a\u3000b", 2, 2, 3, 0)]
    [InlineData("x \u2003 y", 3, 3, 5, 0)]
    [InlineData("a\u2009b", 1, 3, 3, 0)]
    [InlineData("a\u200Bb", 1, 2, 2, 0)]
    [InlineData("hyphen\u00ADated", 1, 11, 11, 0)]
    [InlineData("non‑breaking", 1, 12, 12, 0)]
    [InlineData("日本語のテキスト", 8, 8, 8, 8)]
    [InlineData("これは日本語です。", 9, 9, 9, 9)]
    [InlineData("你好，世界。", 6, 6, 6, 6)]
    [InlineData("한국어 텍스트", 6, 6, 7, 6)]
    [InlineData("ＡＢＣ ｄｅｆ", 6, 6, 7, 6)]
    [InlineData("１２３", 3, 3, 3, 3)]
    [InlineData("ｶﾀｶﾅ", 4, 4, 4, 4)]
    [InlineData("a･b", 3, 3, 3, 1)]
    [InlineData("Inkwell 日本 test", 4, 13, 15, 2)]
    [InlineData("abc日本def", 4, 8, 8, 2)]
    [InlineData("یہ اردو ہے", 3, 8, 10, 0)]
    [InlineData("مرحبا بالعالم", 2, 12, 13, 0)]
    [InlineData("สวัสดีครับ ไทย", 2, 13, 14, 0)]
    [InlineData("ไทยภาษาไทย", 1, 10, 10, 0)]
    [InlineData("", 0, 0, 0, 0)]
    public void CountText_MatchesWord(string text, int words, int characters, int withSpaces, int eastAsian)
    {
        var c = DocumentStats.CountText(text);
        Assert.Equal(words, c.Words);
        Assert.Equal(characters, c.Characters);
        Assert.Equal(withSpaces, c.CharactersWithSpaces);
        Assert.Equal(eastAsian, c.EastAsianCharacters);
    }

    [Fact]
    public void CountText_SurrogatePairs()
    {
        // An emoji and a CJK extension B ideograph are one character each.
        var c = DocumentStats.CountText("hi 😀 𠀀");
        Assert.Equal(3, c.Words);
        Assert.Equal(4, c.Characters);
        Assert.Equal(1, c.EastAsianCharacters);
    }

    private static Document SampleDocument()
    {
        var doc = new Document();
        var body = doc.Body.Blocks;
        body.Add(new Paragraph("The quick brown fox."));
        body.Add(new Paragraph(""));
        body.Add(new Paragraph("   "));

        var tracked = new Paragraph();
        tracked.Inlines.Add(new TextRun("kept "));
        tracked.Inlines.Add(new TextRun("deleted words ") { Deleted = new RevisionInfo { Author = "A" } });
        tracked.Inlines.Add(new TextRun("added") { Inserted = new RevisionInfo { Author = "A" } });
        body.Add(tracked);

        var withField = new Paragraph("Page ");
        withField.Inlines.Add(new FieldInline { Code = " PAGE ", Result = { new TextRun("3") } });
        withField.Inlines.Add(new TextRun(" of the report"));
        body.Add(withField);

        var table = new Table();
        var row = new TableRow();
        row.AddCell(TableCell.Create(2000, new Paragraph("Cell one")));
        row.AddCell(TableCell.Create(2000, new Paragraph("Cell two")));
        table.AddRow(row);
        body.Add(table);

        var sdt = new SdtBlock();
        sdt.Blocks.Add(new Paragraph("Inside control"));
        body.Add(sdt);

        var claim = new Paragraph("Claim");
        claim.Inlines.Add(new NoteReference { Kind = NoteKind.Footnote, Id = 2 });
        body.Add(claim);

        var picture = new Paragraph();
        picture.Inlines.Add(new DrawingObject { Kind = "textbox", Width = 914400, Height = 914400, TextBoxContent = new List<Block> { new Paragraph("Box text here") } });
        picture.Inlines.Add(new TextRun("Caption"));
        body.Add(picture);

        var separator = new Note(NoteKind.Footnote) { Id = -1, Type = "separator" };
        separator.Blocks.Add(new Paragraph("not counted"));
        doc.Footnotes[-1] = separator;
        var footnote = new Note(NoteKind.Footnote) { Id = 2 };
        var fp = new Paragraph();
        fp.Inlines.Add(new NoteMark { Kind = NoteKind.Footnote });
        fp.Inlines.Add(new TextRun(" A note."));
        footnote.Blocks.Add(fp);
        doc.Footnotes[2] = footnote;
        var endnote = new Note(NoteKind.Endnote) { Id = 1 };
        endnote.Blocks.Add(new Paragraph("End note text"));
        doc.Endnotes[1] = endnote;

        var header = new HeaderFooter(true) { RelId = "rId9" };
        header.Blocks.Add(new Paragraph("Header words are never counted"));
        doc.HeadersFooters["rId9"] = header;
        var comment = new Comment { Id = 0, Author = "A" };
        comment.Blocks.Add(new Paragraph("Comments are not counted either"));
        doc.Comments.Add(comment);
        return doc;
    }

    [Fact]
    public void Document_CountsLikeWord()
    {
        var stats = new DocumentStats();
        var doc = SampleDocument();

        // Body: 4 + 2 (deleted text left out) + 5 (field result) + 4 (table) + 2 (content control) + 1 + 1 = 19,
        // text box 3, footnote 2, endnote 3.
        var all = stats.Count(doc);
        Assert.Equal(27, all.Words);
        Assert.Equal(11, all.Paragraphs);

        var bodyOnly = stats.Count(doc, new StatsOptions { IncludeNotes = false, IncludeTextBoxes = false });
        Assert.Equal(19, bodyOnly.Words);
        Assert.Equal(8, bodyOnly.Paragraphs);
        Assert.Null(bodyOnly.Lines);
        Assert.Null(bodyOnly.Pages);

        var notesNoBoxes = stats.Count(doc, new StatsOptions { IncludeTextBoxes = false });
        Assert.Equal(24, notesNoBoxes.Words);

        var simple = new Document();
        simple.Body.Blocks.Add(new Paragraph("The quick brown fox."));
        simple.Body.Blocks.Add(new Paragraph(" "));
        var s = stats.Count(simple, new StatsOptions { LineCounter = p => p.IsEmpty ? 0 : 2, Pages = 1 });
        Assert.Equal(4, s.Words);
        Assert.Equal(17, s.Characters);
        Assert.Equal(21, s.CharactersWithSpaces);
        Assert.Equal(1, s.Paragraphs);
        Assert.Equal(4, s.Lines);
        Assert.Equal(1, s.Pages);
    }

    [Fact]
    public void Selection_CountsParts()
    {
        var stats = new DocumentStats();
        var a = new Paragraph("Hello wonderful world");
        var b = new Paragraph("Second paragraph here");
        // "lo wonder": a word cut by the selection still counts.
        var part = stats.Count(new[] { new StatsRange(a, 3, 12) });
        Assert.Equal(2, part.Words);
        Assert.Equal(8, part.Characters);
        Assert.Equal(9, part.CharactersWithSpaces);
        Assert.Equal(1, part.Paragraphs);

        var both = stats.Count(new[] { new StatsRange(a, 12, 3), new StatsRange(b, 0, b.Length), new StatsRange(b, 0, 0) },
            new StatsOptions { LineCounter = _ => 1 });
        Assert.Equal(5, both.Words);
        Assert.Equal(2, both.Paragraphs);
        Assert.Equal(2, both.Lines);

        var tracked = new Paragraph();
        tracked.Inlines.Add(new TextRun("one "));
        tracked.Inlines.Add(new TextRun("two ") { Deleted = new RevisionInfo() });
        tracked.Inlines.Add(new TextRun("three"));
        Assert.Equal(2, stats.Count(new[] { new StatsRange(tracked, 2, 11) }).Words); // "e " + (deleted) + "thr"
    }

    [Fact]
    public void ReadingAndSpeakingTime()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), DocumentStats.ReadingTime(476));
        Assert.Equal(TimeSpan.FromMinutes(1), DocumentStats.SpeakingTime(130));
        var r = new DocumentStatistics { Words = 238 };
        Assert.Equal(TimeSpan.FromMinutes(1), r.ReadingTime);
        Assert.Equal(TimeSpan.FromMinutes(238 / 130.0), r.SpeakingTime);
    }

    [Fact]
    public void Cache_FollowsTheParagraphVersion()
    {
        var stats = new DocumentStats();
        var p = new Paragraph("one two");
        Assert.Equal(2, stats.CountParagraph(p).Words);
        p.Inlines.Add(new TextRun(" three"));
        p.MarkChanged();
        Assert.Equal(3, stats.CountParagraph(p).Words);
        p.Inlines.Add(new TextRun(" four"));
        stats.ClearCache();
        Assert.Equal(4, stats.CountParagraph(p).Words);
    }

    [Fact]
    public void ThreeHundredPages_AreFast()
    {
        // About 300 pages: 3,000 paragraphs of 50 words (150,000 words, roughly 900,000 characters).
        var doc = new Document();
        const string sentence = "Inkwell counts words quickly, even in long documents — and it matches Word's rules. ";
        var words = DocumentStats.CountText(sentence).Words;
        for (var i = 0; i < 3000; i++)
        {
            var text = string.Concat(Enumerable.Repeat(sentence, 50 / words + 1));
            doc.Body.Blocks.Add(new Paragraph(text));
        }
        var stats = new DocumentStats();
        var sw = Stopwatch.StartNew();
        var cold = stats.Count(doc);
        var coldMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        var warm = stats.Count(doc);
        var warmMs = sw.Elapsed.TotalMilliseconds;
        var edited = (Paragraph)doc.Body.Blocks[1500];
        edited.Inlines.Add(new TextRun(" one more"));
        edited.MarkChanged();
        sw.Restart();
        var afterEdit = stats.Count(doc);
        var editMs = sw.Elapsed.TotalMilliseconds;
        _output.WriteLine($"{cold.Words:N0} words, {cold.CharactersWithSpaces:N0} characters: cold {coldMs:F1} ms, cached {warmMs:F1} ms, after one edit {editMs:F1} ms");

        Assert.True(cold.Words >= 150_000);
        Assert.Equal(cold.Words, warm.Words);
        Assert.Equal(cold.Words + 2, afterEdit.Words);
        Assert.Equal(3000, cold.Paragraphs);
        Assert.True(coldMs < 3000, "cold count took " + coldMs + " ms");
        Assert.True(warmMs < 500, "cached count took " + warmMs + " ms");
    }
}
