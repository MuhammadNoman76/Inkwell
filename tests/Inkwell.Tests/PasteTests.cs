using Inkwell.Core.Editing;
using Inkwell.Core.IO;
using Inkwell.Core.Model;
using Inkwell.Core.Templates;
using Inkwell.Core.Styles;
using Xunit;
using TextRun = Inkwell.Core.Model.TextRun;

namespace Inkwell.Tests;

/// <summary>Paste that matches the destination (or keeps the source, or text only), as one undo step.</summary>
public class PasteTests
{
    private const string WebHtml =
        "<h2 style=\"font-family:'Comic Sans MS';color:#c00000\">From a web page</h2>" +
        "<p style=\"font-family:'Times New Roman';font-size:9pt;color:#7030a0\">Text with <b>bold words</b>, <i>italics</i> and a <a href=\"https://example.com\">link</a>.</p>" +
        "<ul><li>First point</li><li>Second point</li></ul>";

    private static (Document Doc, DocEditor Editor, Paragraph Target) Destination()
    {
        var b = new DocBuilder(DocumentDesigns.Default);
        b.P("Before");
        var target = b.P("");
        b.P("After");
        var editor = new DocEditor(b.Doc);
        editor.Selection = new Selection(new DocPos(target, 0));
        return (b.Doc, editor, target);
    }

    private static DocFragment FromWeb() => DocFragment.FromDocument(DocumentFiles.ImportClipboardHtml(Core.Formats.CfHtml.Build(WebHtml))!);

    [Fact]
    public void MatchDestination_KeepsMeaning_DropsFontsSizesAndColours() => Sta.Run(() =>
    {
        var (doc, editor, _) = Destination();
        editor.Paste(FromWeb(), PasteMode.Default);
        var paragraphs = doc.Body.Paragraphs.ToList();
        var heading = paragraphs.Single(p => p.PlainText() == "From a web page");
        Assert.Equal("Heading2", heading.Props.StyleId);
        var body = paragraphs.Single(p => p.PlainText().StartsWith("Text with", StringComparison.Ordinal));
        foreach (var run in body.Inlines.Concat(heading.Inlines))
        {
            Assert.Null(run.Props.FontAscii);
            Assert.Null(run.Props.Size);
            Assert.True(run.Props.Color == null || run.Link != null);
        }
        Assert.Contains(body.Inlines.OfType<TextRun>(), r => r.Text == "bold words" && r.Props.Bold == true);
        Assert.Contains(body.Inlines.OfType<TextRun>(), r => r.Text == "italics" && r.Props.Italic == true);
        Assert.Contains(body.Inlines, r => r.Link?.Url == "https://example.com/" || r.Link?.Url == "https://example.com");
        var items = paragraphs.Where(p => p.PlainText().EndsWith("point", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, items.Count);
        var numId = editor.Styles.ResolvePara(items[0]).NumId;
        Assert.True(numId > 0);
        Assert.True(doc.Numbering.Level(numId, 0)!.IsBullet);
        Assert.Equal(numId, editor.Styles.ResolvePara(items[1]).NumId);
        // The surrounding text is untouched and the whole paste is one undo step.
        Assert.Equal("Before", paragraphs.First().PlainText());
        Assert.Equal("After", paragraphs.Last().PlainText());
        editor.Undo();
        Assert.Equal(new[] { "Before", "", "After" }, doc.Body.Paragraphs.Select(p => p.PlainText()).ToArray());
    });

    [Fact]
    public void KeepSource_KeepsTheFontsAndColours() => Sta.Run(() =>
    {
        var (doc, editor, _) = Destination();
        editor.Paste(FromWeb(), PasteMode.KeepSource);
        var body = doc.Body.Paragraphs.Single(p => p.PlainText().StartsWith("Text with", StringComparison.Ordinal));
        var resolved = editor.Styles.ResolveRun(body.Inlines.OfType<TextRun>().First().Props, body);
        Assert.Equal("Times New Roman", resolved.AsciiFont);
        Assert.Equal(9, resolved.Size, 1);
        Assert.Equal("7030A0", OoxColor.ToHex(resolved.Color!.Value));
    });

    [Fact]
    public void TextOnly_PastesWordsInTheCaretsFormatting() => Sta.Run(() =>
    {
        var (doc, editor, target) = Destination();
        editor.Paste(FromWeb(), PasteMode.TextOnly);
        var texts = doc.Body.Paragraphs.Select(p => p.PlainText()).ToList();
        Assert.Contains("From a web page", texts);
        Assert.Contains("Text with bold words, italics and a link.", texts);
        Assert.DoesNotContain(doc.Body.Paragraphs.SelectMany(p => p.Inlines), i => i.Props.Bold == true || i.Link != null);
        Assert.All(doc.Body.Paragraphs, p => Assert.True(p.Props.StyleId is null or "Normal" || p.Props.StyleId == target.Props.StyleId));
    });

    [Fact]
    public void PlainTextLines_BecomeParagraphs_AndAPartialLineJoinsTheCaretsParagraph()
    {
        var b = new DocBuilder(DocumentDesigns.Default);
        var p = b.P("Start  end");
        var editor = new DocEditor(b.Doc);
        editor.Selection = new Selection(new DocPos(p, 6));
        editor.Paste(DocFragment.FromText("one\r\ntwo\r\nthree"), PasteMode.Default);
        Assert.Equal(new[] { "Start one", "two", "three end" }, b.Doc.Body.Paragraphs.Select(x => x.PlainText()).ToArray());
        Assert.Equal("three", editor.Selection.Active.Para.PlainText().Substring(0, editor.Selection.Active.Offset));
    }

    [Fact]
    public void TablePastedIntoACell_BecomesTabSeparatedLines()
    {
        var src = new DocBuilder(DocumentDesigns.Default);
        src.Table(new List<string[]> { new[] { "a", "b" }, new[] { "c", "d" } });
        src.P("");
        var fragment = DocFragment.FromDocument(src.Doc);
        var b = new DocBuilder(DocumentDesigns.Default);
        var table = b.Table(new List<string[]> { new[] { "x", "y" } });
        b.P("");
        var editor = new DocEditor(b.Doc);
        var cell = Nav.FirstIn(table.Rows[0].Cells[1].Blocks)!;
        editor.Selection = new Selection(new DocPos(cell, cell.Length));
        editor.Paste(fragment, PasteMode.Default);
        var lines = DocumentWalker.Paragraphs(table.Rows[0].Cells[1].Blocks).Select(p => p.PlainText()).ToList();
        Assert.Contains("ya\tb", lines); // the first line joins the text at the caret
        Assert.Contains("c\td", lines);
        Assert.Single(b.Doc.Body.Blocks.OfType<Table>());
    }

    [Fact]
    public void CopyAndPasteWithinTheDocument_KeepsFormattingAndCopiesFootnotes()
    {
        var b = new DocBuilder(DocumentDesigns.Default);
        var p = b.P("Some **bold** text");
        b.Note(p, "A note.");
        var target = b.P("");
        var editor = new DocEditor(b.Doc);
        editor.Selection = new Selection(new DocPos(p, 0), new DocPos(p, p.Length));
        var copy = editor.CopySelection()!;
        editor.Selection = new Selection(new DocPos(target, 0));
        editor.Paste(copy, PasteMode.Default);
        Assert.Contains(target.Inlines.OfType<TextRun>(), r => r.Text == "bold" && r.Props.Bold == true);
        var refs = b.Doc.Body.Paragraphs.SelectMany(x => x.Inlines).OfType<NoteReference>().ToList();
        Assert.Equal(2, refs.Count);
        Assert.NotEqual(refs[0].Id, refs[1].Id);
        Assert.Equal(2, b.Doc.Footnotes.Values.Count(n => !n.IsSpecial));
    }

    [Fact]
    public void Picture_PastedFromAnotherDocument_CarriesItsBytes()
    {
        var src = new DocBuilder(DocumentDesigns.Default);
        var sp = src.P("");
        src.Picture(sp, TestImages.Png(), 1.0);
        var fragment = DocFragment.FromDocument(src.Doc);
        var (doc, editor, _) = Destination();
        editor.Paste(fragment, PasteMode.Default);
        var drawing = doc.Body.Paragraphs.SelectMany(p => p.Inlines).OfType<DrawingObject>().Single();
        Assert.NotNull(doc.Media.Get(drawing.MediaId));
    }
}
