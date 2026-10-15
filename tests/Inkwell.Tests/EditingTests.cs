using Inkwell.Core.Editing;
using Inkwell.Core.Layout;
using Inkwell.Core.Model;
using Inkwell.Core.Templates;
using Xunit;
using TextRun = Inkwell.Core.Model.TextRun;

namespace Inkwell.Tests;

/// <summary>The editing model: typing and undo, Markdown shortcuts and autocorrect as single undo steps, lists, tables, sections.</summary>
public class EditingTests
{
    private static (Document Doc, DocEditor Editor) New(params string[] paragraphs)
    {
        var b = new DocBuilder(DocumentDesigns.Default);
        foreach (var p in paragraphs) b.P(p);
        if (paragraphs.Length == 0) b.P("");
        var editor = new DocEditor(b.Doc);
        editor.Selection = new Selection(Nav.StartOf(b.Doc.Body));
        return (b.Doc, editor);
    }

    private static Paragraph Para(Document doc, int i) => doc.Body.Paragraphs.ElementAt(i);

    private static void Type(DocEditor editor, string text)
    {
        foreach (var c in text) editor.InsertText(c.ToString());
    }

    [Fact]
    public void Typing_MergesIntoOneUndoStep_AndUndoRedoRestore()
    {
        var (doc, editor) = New();
        Type(editor, "Hello world");
        Assert.Equal("Hello world", Para(doc, 0).Text);
        Assert.Equal(1, editor.History.UndoCount);
        editor.Undo();
        Assert.Equal("", Para(doc, 0).Text);
        editor.Redo();
        Assert.Equal("Hello world", Para(doc, 0).Text);
    }

    [Fact]
    public void Enter_SplitsParagraph_AndBackspaceJoinsThem()
    {
        var (doc, editor) = New("FirstSecond");
        editor.Selection = new Selection(new DocPos(Para(doc, 0), 5));
        editor.Enter();
        Assert.Equal(new[] { "First", "Second" }, doc.Body.Paragraphs.Select(p => p.Text).ToArray());
        editor.Backspace();
        Assert.Equal(new[] { "FirstSecond" }, doc.Body.Paragraphs.Select(p => p.Text).ToArray());
        editor.Undo();
        editor.Undo();
        Assert.Equal("FirstSecond", Para(doc, 0).Text);
        Assert.Single(doc.Body.Paragraphs);
    }

    [Fact]
    public void DeleteAcrossParagraphs_IsOneUndoStep()
    {
        var (doc, editor) = New("One two", "three four", "five six");
        editor.Selection = new Selection(new DocPos(Para(doc, 0), 4), new DocPos(Para(doc, 2), 5));
        editor.DeleteSelection();
        Assert.Equal("One six", Para(doc, 0).Text);
        Assert.Single(doc.Body.Paragraphs);
        editor.Undo();
        Assert.Equal(new[] { "One two", "three four", "five six" }, doc.Body.Paragraphs.Select(p => p.Text).ToArray());
    }

    [Fact]
    public void BoldToggle_OnSelection_AndUndo()
    {
        var (doc, editor) = New("Make this bold");
        editor.Selection = new Selection(new DocPos(Para(doc, 0), 5), new DocPos(Para(doc, 0), 9));
        editor.ToggleBold();
        var bold = Para(doc, 0).Inlines.OfType<TextRun>().Single(r => r.Props.Bold == true);
        Assert.Equal("this", bold.Text);
        editor.Undo();
        Assert.DoesNotContain(Para(doc, 0).Inlines, i => i.Props.Bold == true);
    }

    [Fact]
    public void MarkdownHeading_IsAppliedAndUndoneWithOneUndo()
    {
        var (doc, editor) = New();
        Type(editor, "#");
        Type(editor, " ");
        var p = Para(doc, 0);
        var ac = new AutoCorrect();
        var result = ac.Process(p.Text, 2, ' ', new AutoCorrectContext());
        Assert.NotNull(result.Block);
        editor.ApplyBlockShortcut(result.Block!);
        Assert.Equal("Heading1", p.Props.StyleId);
        Assert.Equal("", p.Text);
        // One Ctrl+Z puts the "# " back as typed.
        editor.Undo();
        Assert.Equal("# ", Para(doc, 0).Text);
        Assert.NotEqual("Heading1", Para(doc, 0).Props.StyleId);
    }

    [Fact]
    public void MarkdownBold_IsAppliedAndUndoneWithOneUndo()
    {
        var (doc, editor) = New();
        Type(editor, "say **hi**");
        var p = Para(doc, 0);
        var result = new AutoCorrect().Process(p.Text, p.Length, '*', new AutoCorrectContext());
        Assert.NotNull(result.Inline);
        editor.ApplyInlineShortcut(result.Inline!);
        Assert.Equal("say hi", p.Text);
        Assert.Equal("hi", p.Inlines.OfType<TextRun>().Single(r => r.Props.Bold == true).Text);
        editor.Undo();
        Assert.Equal("say **hi**", Para(doc, 0).Text);
    }

    [Fact]
    public void AutoCorrect_CapitalI_IsOneUndoStep_AndNeverCapitalisesAfterIe()
    {
        var (doc, editor) = New();
        var ac = new AutoCorrect();
        void TypeWithAutoCorrect(string text)
        {
            foreach (var c in text)
            {
                editor.InsertText(c.ToString());
                var caret = editor.Selection.Active;
                var r = ac.Process(caret.Para.Text, caret.Offset, c, new AutoCorrectContext());
                if (r.Edits.Count > 0) editor.ApplyAutoCorrect(r.Edits);
            }
        }
        TypeWithAutoCorrect("i went home. this is it, i.e. the end. ");
        Assert.Equal("I went home. This is it, i.e. the end. ", Para(doc, 0).Text);
        // The last automatic change ("the" stays) — undo the last correction only.
        var before = Para(doc, 0).Text;
        TypeWithAutoCorrect("so i ");
        Assert.EndsWith("So I ", Para(doc, 0).Text);
        editor.Undo();
        Assert.EndsWith("So i ", Para(doc, 0).Text);
        Assert.StartsWith(before, Para(doc, 0).Text);
    }

    /// <summary>
    /// A correction made by Enter (the editor presses Enter first, then corrects the paragraph above): one Ctrl+Z
    /// puts back what was typed and keeps the new paragraph, as after a space; the caret stays in the new paragraph.
    /// </summary>
    [Fact]
    public void AutoCorrect_ByEnter_IsOneUndoStep_AfterTheNewParagraph()
    {
        var (doc, editor) = New("the plan works. so i");
        var p = Para(doc, 0);
        editor.Selection = new Selection(new DocPos(p, p.Length));
        var result = new AutoCorrect().Process(p.Text, p.Length, '\r', new AutoCorrectContext());
        Assert.NotEmpty(result.Edits);
        editor.Enter();
        Assert.True(editor.ApplyAutoCorrect(p, result.Edits));
        Assert.Equal(new[] { "the plan works. so I", "" }, doc.Body.Paragraphs.Select(x => x.Text).ToArray());
        Assert.Same(Para(doc, 1), editor.Selection.Active.Para);
        Assert.True(editor.History.Top!.IsAutomatic);

        editor.Undo();
        Assert.Equal(new[] { "the plan works. so i", "" }, doc.Body.Paragraphs.Select(x => x.Text).ToArray());
        Assert.Same(Para(doc, 1), editor.Selection.Active.Para);
        editor.Undo();
        Assert.Equal(new[] { "the plan works. so i" }, doc.Body.Paragraphs.Select(x => x.Text).ToArray());
    }

    [Fact]
    public void Lists_RestartAndContinue()
    {
        var b = new DocBuilder(DocumentDesigns.Default);
        b.Numbered(true, "a", "b");
        b.P("between");
        b.Numbered(true, "c", "d");
        var doc = b.Doc;
        var editor = new DocEditor(doc);
        string Label(int i)
        {
            using var layout = new DocumentLayout(doc, new ViewOptions());
            layout.LayoutAll();
            var labels = Numberer.Labels(doc, editor.Styles, new Story[] { doc.Body });
            return labels.TryGetValue(doc.Body.Paragraphs.ElementAt(i), out var l) ? l : "";
        }
        Assert.Equal("1.", Label(3));
        editor.Selection = new Selection(new DocPos(doc.Body.Paragraphs.ElementAt(3), 0));
        Assert.True(editor.CanContinueNumbering());
        editor.ContinueNumbering();
        Assert.Equal("3.", Label(3));
        Assert.Equal("4.", Label(4));
        editor.RestartNumbering(1);
        Assert.Equal("1.", Label(3));
        Assert.Equal("2.", Label(4));
    }

    [Fact]
    public void TabInLastTableCell_AddsARow()
    {
        var b = new DocBuilder(DocumentDesigns.Default);
        var table = b.Table(new List<string[]> { new[] { "A", "B" }, new[] { "1", "2" } });
        b.P("after");
        var editor = new DocEditor(b.Doc);
        var last = Nav.LastIn(table.Rows[1].Cells[1].Blocks)!;
        editor.Selection = new Selection(new DocPos(last, last.Length));
        Assert.True(editor.TabInTable(false));
        Assert.Equal(3, table.Rows.Count);
        Assert.Same(table.Rows[2].Cells[0], Nav.CellOf(editor.Selection.Active.Para));
        editor.Undo();
        Assert.Equal(2, table.Rows.Count);
    }

    [Fact]
    public void MakeThisPageLandscape_AddsSectionBreaksAroundThePage() => Sta.Run(() =>
    {
        var b = new DocBuilder(DocumentDesigns.Default);
        for (var i = 0; i < 60; i++) b.P("Paragraph " + i + " with enough words to take a line or two of the page width in the document body text.");
        var doc = b.Doc;
        var editor = new DocEditor(doc);
        using var layout = new DocumentLayout(doc, new ViewOptions());
        layout.LayoutAll();
        var pagesBefore = layout.Pages.Count;
        Assert.True(pagesBefore >= 3);
        var onPage2 = layout.Pages[1].Columns[0].Content.OfType<ParaFragment>().First().Paragraph;
        editor.Selection = new Selection(new DocPos(onPage2, 0));
        editor.MakePageLandscape(true, layout);
        var sections = doc.Sections();
        Assert.Equal(3, sections.Count);
        Assert.False(sections[0].Props.Landscape);
        Assert.True(sections[1].Props.Landscape);
        Assert.False(sections[2].Props.Landscape);
        layout.LayoutAll();
        Assert.Contains(layout.Pages, p => p.Size.Width > p.Size.Height);
        Assert.True(layout.Pages[0].Size.Width < layout.Pages[0].Size.Height);
        editor.Undo();
        Assert.Single(doc.Sections());
    });

    [Fact]
    public void TableOfContents_ListsHeadingsAndUpdatesPageNumbers() => Sta.Run(() =>
    {
        var b = new DocBuilder(DocumentDesigns.Default);
        var top = b.P("");
        b.Heading(1, "First chapter");
        for (var i = 0; i < 40; i++) b.P("Body text " + i + " that fills the page with enough words to wrap over the line width.");
        b.Heading(1, "Second chapter");
        b.Heading(2, "A section");
        var doc = b.Doc;
        var editor = new DocEditor(doc);
        editor.Selection = new Selection(new DocPos(top, 0));
        editor.InsertTableOfContents();
        using var layout = new DocumentLayout(doc, new ViewOptions());
        layout.LayoutAll();
        editor.UpdateTablesOfContents(layout);
        var toc = DocumentWalker.AllBlocks(doc.Body.Blocks).OfType<FieldBlock>().Single(f => f.Kind == FieldKind.Toc);
        var entries = DocumentWalker.Paragraphs(toc.Blocks).Select(p => p.PlainText()).ToList();
        Assert.Equal(3, entries.Count);
        Assert.StartsWith("First chapter", entries[0]);
        Assert.EndsWith("1", entries[0].TrimEnd());
        Assert.StartsWith("Second chapter", entries[1]);
        Assert.NotEqual("1", entries[1].TrimEnd().Split('\t').Last());
        // Each entry links to a bookmark on its heading.
        var link = DocumentWalker.Paragraphs(toc.Blocks).First().Inlines.First(i => i.Link != null).Link!;
        Assert.NotNull(editor.FindBookmark(link.Anchor!));
    });

    [Fact]
    public void TrackChanges_RecordsInsertionsAndDeletions_AcceptAndReject()
    {
        var (doc, editor) = New("The quick fox");
        editor.Author = "Reviewer";
        editor.SetTrackChanges(true);
        var p = Para(doc, 0);
        editor.Selection = new Selection(new DocPos(p, 4), new DocPos(p, 9));
        editor.DeleteSelection();
        editor.Selection = new Selection(new DocPos(p, 4));
        editor.InsertText("slow");
        var revisions = editor.Revisions();
        Assert.Contains(revisions, r => r.Kind == "Deleted" && r.Text == "quick");
        Assert.Contains(revisions, r => r.Kind == "Inserted" && r.Text == "slow");
        Assert.All(revisions, r => Assert.Equal("Reviewer", r.Author));
        // Accept all: deletions go, insertions stay.
        editor.AcceptOrRejectAll(true, null);
        Assert.Equal("The slow fox", p.PlainText());
        Assert.False(editor.HasRevisions());
        editor.Undo();
        Assert.True(editor.HasRevisions());
        editor.AcceptOrRejectAll(false, null);
        Assert.Equal("The quick fox", Para(doc, 0).PlainText());
    }

    [Fact]
    public void Comments_AddReplyResolveDelete()
    {
        var (doc, editor) = New("A sentence worth discussing.");
        var p = Para(doc, 0);
        editor.Selection = new Selection(new DocPos(p, 2), new DocPos(p, 10));
        var comment = editor.AddComment("Why this word?");
        var reply = editor.AddComment("Because it fits.", comment);
        Assert.Equal(2, doc.Comments.Count);
        Assert.Equal(comment.Id, reply.ParentId);
        var range = editor.FindCommentRange(comment.Id)!.Value;
        Assert.Equal(2, range.Start.Offset);
        Assert.Equal(10, range.End.Offset);
        editor.ResolveComment(comment, true);
        Assert.True(doc.Comments.All(c => c.Done));
        editor.DeleteComment(doc.Comments.First(c => c.Id == comment.Id));
        Assert.Empty(doc.Comments);
        Assert.DoesNotContain(Para(doc, 0).Inlines, i => i is CommentMarker);
        Assert.Equal("A sentence worth discussing.", Para(doc, 0).Text);
    }

    [Fact]
    public void Footnote_IsInsertedAndNumbered()
    {
        var (doc, editor) = New("Claim that needs a source.");
        var p = Para(doc, 0);
        editor.Selection = new Selection(new DocPos(p, p.Length));
        var note = editor.InsertNote(NoteKind.Footnote);
        Assert.Contains(p.Inlines, i => i is NoteReference r && r.Id == note.Id);
        Assert.Same(note, doc.Footnotes[note.Id]);
        Assert.Equal(1, Numberer.NoteNumbers(doc).Count(kv => kv.Key.Item1 == NoteKind.Footnote));
    }

    [Fact]
    public void ApplyStyle_RemovesDirectFormattingThatTheStyleOverrides()
    {
        var (doc, editor) = New("A title");
        var p = Para(doc, 0);
        p.Inlines.OfType<TextRun>().First().Props.Size = 40;
        editor.Selection = new Selection(new DocPos(p, 0));
        editor.ApplyStyle("Heading1");
        Assert.Equal("Heading1", p.Props.StyleId);
        Assert.Null(p.Inlines.OfType<TextRun>().First().Props.Size);
    }

    [Fact]
    public void ClearFormatting_ReturnsToTheStyle()
    {
        var (doc, editor) = New("Some text");
        var p = Para(doc, 0);
        var run = p.Inlines.OfType<TextRun>().First();
        run.Props.Bold = true;
        run.Props.SetLatinFont("Comic Sans MS");
        p.Props.Justification = "center";
        editor.Selection = new Selection(new DocPos(p, 0), new DocPos(p, p.Length));
        editor.ClearFormatting();
        Assert.Null(p.Props.Justification);
        Assert.Null(p.Inlines.OfType<TextRun>().First().Props.Bold);
        Assert.Null(p.Inlines.OfType<TextRun>().First().Props.FontAscii);
    }

    [Fact]
    public void StyleConsistency_FindsAndFixesFakeHeadingsAndTypedLists()
    {
        var (doc, editor) = New("Quarterly results", "Body text that follows the heading and explains the figures in full sentences.", "1. First thing", "2. Second thing");
        Para(doc, 0).Inlines.OfType<TextRun>().First().Props.Bold = true;
        var issues = StyleConsistency.Check(editor);
        var fake = issues.Single(i => i.Title.Contains("heading"));
        var lists = issues.Single(i => i.Title.Contains("typed number"));
        fake.Fix(editor);
        lists.Fix(editor);
        Assert.Equal("Heading2", Para(doc, 0).Props.StyleId);
        Assert.Equal("First thing", Para(doc, 2).Text);
        Assert.True(editor.Styles.ResolvePara(Para(doc, 2)).NumId > 0);
        Assert.Equal(editor.Styles.ResolvePara(Para(doc, 2)).NumId, editor.Styles.ResolvePara(Para(doc, 3)).NumId);
    }

    [Fact]
    public void FindReplace_MatchCaseWholeWordsRegexAndInNotes()
    {
        var (doc, editor) = New("Cat cat concatenate CAT", "The cat sat.");
        var p = Para(doc, 1);
        editor.Selection = new Selection(new DocPos(p, p.Length));
        var note = editor.InsertNote(NoteKind.Footnote);
        editor.InsertText("A note about a cat.");
        Assert.Equal(6, FindReplace.FindAll(doc, "cat", new FindOptions()).Count);
        Assert.Equal(4, FindReplace.FindAll(doc, "cat", new FindOptions { MatchCase = true }).Count);
        Assert.Equal(5, FindReplace.FindAll(doc, "cat", new FindOptions { WholeWords = true }).Count);
        Assert.Equal(4, FindReplace.FindAll(doc, "cat", new FindOptions { WholeWords = true, IncludeNotes = false }).Count);
        Assert.Single(FindReplace.FindAll(doc, @"\bC[a-z]t\b", new FindOptions { Regex = true, MatchCase = true }));
        var n = FindReplace.ReplaceAll(editor, "cat", "dog", new FindOptions { WholeWords = true, MatchCase = true });
        Assert.Equal(3, n);
        Assert.Contains("A note about a dog.", note.Paragraphs.First().PlainText());
        editor.Undo();
        Assert.Contains("A note about a cat.", note.Paragraphs.First().PlainText());
    }

    [Fact]
    public void RevealFormatting_NamesTheSourceOfEachValue()
    {
        var (doc, editor) = New("Plain and bold");
        var p = Para(doc, 0);
        editor.Selection = new Selection(new DocPos(p, 10));
        editor.Selection = new Selection(new DocPos(p, 10), new DocPos(p, 14));
        editor.ToggleBold();
        var items = RevealFormatting.Explain(editor, new DocPos(p, 11));
        var bold = items.Single(i => i.Name == "Bold");
        Assert.Equal("On", bold.Value);
        Assert.True(bold.IsDirect);
        Assert.Equal("Direct formatting", bold.Source);
        Assert.Contains(items, i => i.Name == "Style" && i.Value == "Body");
    }
}
