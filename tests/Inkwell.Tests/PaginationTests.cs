using Inkwell.Core.Editing;
using Inkwell.Core.Layout;
using Inkwell.Core.Model;
using Inkwell.Core.Templates;
using Xunit;

namespace Inkwell.Tests;

/// <summary>Line breaking and pagination: widows and orphans, keep with next, tables across pages, footnotes, floats.</summary>
public class PaginationTests
{
    private static string Words(int count, int seed = 1)
    {
        var vocabulary = "the quick brown fox jumps over a lazy dog while writers draft clear sentences about gardens rivers mountains cities journeys letters reports".Split(' ');
        var r = new Random(seed);
        return string.Join(" ", Enumerable.Range(0, count).Select(_ => vocabulary[r.Next(vocabulary.Length)])) + ".";
    }

    private static DocumentLayout Lay(Document doc)
    {
        var layout = new DocumentLayout(doc, new ViewOptions());
        layout.LayoutAll();
        return layout;
    }

    private static List<ParaLocation> Where(DocumentLayout layout, Paragraph p) => layout.Locate(p).ToList();

    [Fact]
    public void LineBreaking_WrapsAtWordsWithinTheColumn() => Sta.Run(() =>
    {
        var b = new DocBuilder(DocumentDesigns.Default);
        var p = b.P(Words(120));
        using var layout = Lay(b.Doc);
        var frag = Where(layout, p).Single().Fragment;
        Assert.True(frag.Lines.Count >= 4);
        var text = p.Text;
        foreach (var line in frag.Lines.Take(frag.Lines.Count - 1))
        {
            Assert.True(line.ContentWidth <= line.FormatWidth + 0.5, "lines fit the column");
            // Lines end after a space (word boundary).
            Assert.Equal(' ', text[line.DocEnd - 1]);
        }
    });

    [Fact]
    public void WidowAndOrphanControl_NeverLeavesOneLineAlone() => Sta.Run(() =>
    {
        // Fill a page, then a paragraph that would leave one line at the bottom or the top: try many sizes.
        for (var filler = 30; filler < 44; filler++)
        {
            var b = new DocBuilder(DocumentDesigns.Default);
            for (var i = 0; i < filler; i++) b.P("Line " + i);
            var p = b.P(Words(140, filler));
            using var layout = Lay(b.Doc);
            var parts = Where(layout, p);
            if (parts.Count < 2) continue;
            Assert.True(parts[0].Fragment.Lines.Count >= 2, $"orphan with {filler} filler paragraphs");
            Assert.True(parts[parts.Count - 1].Fragment.Lines.Count >= 2, $"widow with {filler} filler paragraphs");
        }
    });

    [Fact]
    public void KeepWithNext_HeadingNeverEndsAPage() => Sta.Run(() =>
    {
        for (var filler = 30; filler < 46; filler++)
        {
            var b = new DocBuilder(DocumentDesigns.Default);
            for (var i = 0; i < filler; i++) b.P("Line " + i);
            var heading = b.Heading(1, "A heading that must stay with its text");
            var body = b.P(Words(60, filler));
            using var layout = Lay(b.Doc);
            var h = Where(layout, heading).Single();
            var first = Where(layout, body).First();
            Assert.Same(h.Page, first.Page);
        }
    });

    [Fact]
    public void Tables_SplitBetweenRows_RepeatTheHeaderRow_AndKeepRowsWhole() => Sta.Run(() =>
    {
        var b = new DocBuilder(DocumentDesigns.Default);
        var rows = new List<string[]> { new[] { "Item", "Description" } };
        for (var i = 1; i <= 80; i++) rows.Add(new[] { "Row " + i, Words(i % 7 == 0 ? 40 : 6, i) });
        var table = b.Table(rows, header: true);
        foreach (var row in table.Rows) row.Props.CantSplit = true;
        b.P("");
        using var layout = Lay(b.Doc);
        var fragments = layout.Pages.SelectMany(pg => pg.Columns.SelectMany(c => c.Content).OfType<TableFragment>().Select(f => (pg, f))).ToList();
        Assert.True(fragments.Count >= 2, "the table spans pages");
        foreach (var (_, f) in fragments)
        {
            // Every part starts with the header row.
            var firstRow = f.Rows.First();
            Assert.Same(table.Rows[0], firstRow.Row.Row);
        }
        // No row is split (cantSplit): each data row appears exactly once.
        var appearances = fragments.SelectMany(x => x.f.Rows).Where(r => !ReferenceEquals(r.Row.Row, table.Rows[0])).GroupBy(r => r.Row.Row).ToList();
        Assert.Equal(80, appearances.Count);
        Assert.All(appearances, g => Assert.Single(g));
    });

    [Fact]
    public void Footnotes_AreOnThePageOfTheirReference() => Sta.Run(() =>
    {
        var b = new DocBuilder(DocumentDesigns.Default);
        var refs = new List<(Paragraph Para, Note Note)>();
        for (var i = 0; i < 30; i++)
        {
            var p = b.P(Words(50, i));
            if (i % 4 == 0) refs.Add((p, b.Note(p, "Note " + i + ": " + Words(12, i))));
        }
        using var layout = Lay(b.Doc);
        foreach (var (para, note) in refs)
        {
            var refPage = Where(layout, para).Last().Page;
            var notePara = note.Paragraphs.First();
            var noteLoc = layout.Locate(notePara).First();
            Assert.Equal(PageRegion.Notes, noteLoc.Region);
            Assert.Same(refPage, noteLoc.Page);
        }
    });

    [Fact]
    public void FloatingPicture_TextWrapsBesideIt() => Sta.Run(() =>
    {
        var b = new DocBuilder(DocumentDesigns.Default);
        b.P(Words(30));
        var p = b.P(Words(150, 2));
        var d = b.FloatingPicture(p, TestImages.Png(), 2.0, 4.0, 0, WrapMode.Square);
        d.Float!.Vertical = new FloatPosition { RelativeFrom = "paragraph", Offset = 0 };
        using var layout = Lay(b.Doc);
        var loc = Where(layout, p).First();
        var placement = loc.Page.Floats.Single(f => ReferenceEquals(f.Drawing, d));
        var beside = loc.Fragment.Lines.Where(l => l.Top < placement.WrapBox.Bottom && l.Top + l.Height > placement.WrapBox.Top).ToList();
        Assert.NotEmpty(beside);
        foreach (var line in beside)
            Assert.True(loc.Fragment.X + line.X + line.FormatWidth <= placement.WrapBox.Left + 0.5, "lines beside the picture end before it");
        // Below the picture the lines use the full width again.
        var below = loc.Fragment.Lines.Where(l => l.Top >= placement.WrapBox.Bottom).ToList();
        Assert.NotEmpty(below);
        Assert.True(below[0].FormatWidth > beside[0].FormatWidth + 50);
    });

    [Fact]
    public void PageBreakBefore_AndHardBreaks_StartNewPages() => Sta.Run(() =>
    {
        var b = new DocBuilder(DocumentDesigns.Default);
        var a = b.P("First page");
        b.PageBreak();
        var c = b.P("Second page");
        var d = b.P("Third page");
        d.Props.PageBreakBefore = true;
        using var layout = Lay(b.Doc);
        Assert.Equal(3, layout.Pages.Count);
        Assert.Same(layout.Pages[0], Where(layout, a).Single().Page);
        Assert.Same(layout.Pages[1], Where(layout, c).Single().Page);
        Assert.Same(layout.Pages[2], Where(layout, d).Single().Page);
    });

    [Fact]
    public void EmptyParagraphAfterAFinalTable_NeverMakesABlankPage() => Sta.Run(() =>
    {
        // Tables of growing length: whatever space is left, the empty last paragraph stays on the table's last page.
        for (var rows = 30; rows < 40; rows++)
        {
            var b = new DocBuilder(DocumentDesigns.Default);
            var data = new List<string[]> { new[] { "A", "B" } };
            for (var i = 0; i < rows; i++) data.Add(new[] { "Row " + i, "Value" });
            var table = b.Table(data);
            var last = b.P("");
            using var layout = Lay(b.Doc);
            var tablePages = layout.Pages.Where(pg => pg.Columns.SelectMany(c => c.Content).Any(f => f.Block == table)).ToList();
            Assert.Same(tablePages.Last(), Where(layout, last).Single().Page);
            Assert.Equal(tablePages.Count, layout.Pages.Count);
        }
    });

    [Fact]
    public void PageBreakBeforeTheParagraphMark_LeavesNoEmptyLineAndNoBlankPage() => Sta.Run(() =>
    {
        var b = new DocBuilder(DocumentDesigns.Default);
        b.P("First page");
        b.PageBreak();
        var next = b.P("Second page starts here");
        var end = b.P("The end");
        end.Inlines.Add(new Inkwell.Core.Model.TextRun(""));
        using var layout = Lay(b.Doc);
        Assert.Equal(2, layout.Pages.Count);
        var first = layout.Pages[1].Columns[0].Content.OfType<ParaFragment>().First();
        Assert.Same(next, first.Paragraph);
    });

    [Fact]
    public void Pageless_IsOneTallPage() => Sta.Run(() =>
    {
        var b = new DocBuilder(DocumentDesigns.Default);
        for (var i = 0; i < 80; i++) b.P(Words(40, i));
        var layout = new DocumentLayout(b.Doc, new ViewOptions { Mode = ViewMode.Pageless });
        layout.LayoutAll();
        Assert.Single(layout.Pages);
        layout.Dispose();
    });

    [Fact]
    public void IncrementalUpdate_MatchesAFullLayout() => Sta.Run(() =>
    {
        var b = new DocBuilder(DocumentDesigns.Default);
        for (var i = 0; i < 120; i++) b.P(Words(45, i));
        var doc = b.Doc;
        var editor = new DocEditor(doc);
        using var layout = Lay(doc);
        var target = doc.Body.Paragraphs.ElementAt(60);
        editor.Selection = new Selection(new DocPos(target, 5));
        editor.InsertText(Words(80, 99));
        layout.Update();
        using var fresh = Lay(doc);
        Assert.Equal(fresh.Pages.Count, layout.Pages.Count);
        for (var i = 0; i < fresh.Pages.Count; i++)
        {
            var a = fresh.Pages[i].Columns[0].Content.OfType<ParaFragment>().Select(f => (f.Paragraph, f.Lines.Count)).ToList();
            var c = layout.Pages[i].Columns[0].Content.OfType<ParaFragment>().Select(f => (f.Paragraph, f.Lines.Count)).ToList();
            Assert.Equal(a, c);
        }
    });
}
