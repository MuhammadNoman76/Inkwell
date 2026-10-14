using Inkwell.Core.Model;

namespace Inkwell.Core.Editing;

/// <summary>Raised when an edit is refused (read-only content, nothing to do) with a message for the user.</summary>
public sealed class EditRefusedException : Exception
{
    public EditRefusedException(string message) : base(message)
    {
    }
}

/// <summary>
/// Edits a document: every change runs in a transaction that records the touched objects, so Ctrl+Z undoes it
/// exactly. Holds the selection, the formatting chosen for the next typed text, and track-changes settings.
/// </summary>
public sealed partial class DocEditor
{
    private Selection _selection;

    public DocEditor(Document doc)
    {
        Doc = doc;
        History = new UndoStack(doc);
        _selection = new Selection(Nav.StartOf(doc.Body));
    }

    public Document Doc { get; }

    public UndoStack History { get; }

    /// <summary>Name used for tracked changes and comments.</summary>
    public string Author { get; set; } = Environment.UserName;

    public string Initials => new string(Author.Split(new[] { ' ', '.', '_' }, StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0])).Take(3).ToArray());

    public bool TrackChanges => Doc.Settings.TrackRevisions;

    /// <summary>Formatting for the next typed text when the selection is empty (Ctrl+B then type).</summary>
    public RunProps? PendingProps { get; set; }

    /// <summary>Raised after every committed change (or undo / redo).</summary>
    public event Action? Changed;

    public event Action? SelectionChanged;

    public Selection Selection
    {
        get => _selection;
        set
        {
            var fixedSel = Normalize(value);
            if (fixedSel.Equals(_selection)) return;
            var moved = !ReferenceEquals(fixedSel.Active.Para, _selection.Active.Para);
            _selection = fixedSel;
            PendingProps = null;
            if (moved) History.BreakMerge();
            SelectionChanged?.Invoke();
        }
    }

    /// <summary>Sets the selection without clearing pending formatting (used after typing).</summary>
    public void SetSelectionKeepFormatting(Selection s)
    {
        var pending = PendingProps;
        Selection = s;
        PendingProps = pending;
    }

    private static Selection Normalize(Selection s)
    {
        DocPos Clamp(DocPos p) => p.Para == null ? p : new DocPos(p.Para, MathEx.Clamp(p.Offset, 0, p.Para.Length));
        return new Selection(Clamp(s.Anchor), Clamp(s.Active));
    }

    public Story Story => Selection.Active.Para?.Story ?? Doc.Body;

    // ------------------------------------------------------------------ transactions

    /// <summary>A fresh revision stamp for tracked changes.</summary>
    public RevisionInfo NewRevision() => new() { Author = Author, Date = DateTime.UtcNow, Id = 0 };

    /// <summary>
    /// Runs <paramref name="action"/> as one undoable step. The action touches what it changes and returns the
    /// new selection (null keeps the current one). Nothing touched means nothing is recorded.
    /// </summary>
    public bool Edit(string label, Func<Transaction, Selection?> action, string? mergeKey = null, bool automatic = false)
    {
        var t = new Transaction(Doc, label, Selection) { MergeKey = mergeKey, IsAutomatic = automatic };
        Selection? after;
        try
        {
            after = action(t);
        }
        catch
        {
            t.Rollback();
            throw;
        }
        if (t.IsEmpty)
        {
            if (after is { } a) Selection = a;
            return false;
        }
        var newSelection = Normalize(after ?? Selection);
        var entry = t.Commit(newSelection);
        History.Push(entry);
        var pending = mergeKey != null && mergeKey.StartsWith("type:", StringComparison.Ordinal) ? PendingProps : null;
        _selection = newSelection;
        PendingProps = pending;
        Changed?.Invoke();
        SelectionChanged?.Invoke();
        return true;
    }

    public void Undo()
    {
        var s = History.Undo();
        if (s == null) return;
        _selection = Normalize(s.Value);
        PendingProps = null;
        Changed?.Invoke();
        SelectionChanged?.Invoke();
    }

    public void Redo()
    {
        var s = History.Redo();
        if (s == null) return;
        _selection = Normalize(s.Value);
        PendingProps = null;
        Changed?.Invoke();
        SelectionChanged?.Invoke();
    }

    /// <summary>Signals a change made outside <see cref="Edit"/> (loading, replacing the document).</summary>
    public void NotifyChanged() => Changed?.Invoke();

    private static void Refuse(string message) => throw new EditRefusedException(message);

    public static void EnsureEditable(Paragraph p)
    {
        if (Nav.IsReadOnly(p))
            Refuse("This part is generated or kept exactly as it was, so it can't be edited here. Tables of contents and bibliographies update from their own commands.");
    }

    // ------------------------------------------------------------------ typing

    /// <summary>Types text at the selection (replacing it). New lines split paragraphs; tabs and breaks are kept.</summary>
    public void InsertText(string text, RunProps? props = null)
    {
        if (string.IsNullOrEmpty(text)) return;
        var caret = Selection.Start;
        var mergeKey = !text.Contains('\n') && Selection.IsEmpty ? "type:" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(caret.Para) : null;
        Edit(text.Length == 1 ? "Typing" : "Insert text", t =>
        {
            var pos = Selection.IsEmpty ? Selection.Active : DeleteRange(t, Selection.Start, Selection.End);
            EnsureEditable(pos.Para);
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0) pos = SplitParagraph(t, pos, keepStyle: true);
                if (lines[i].Length > 0) pos = InsertRun(t, pos, lines[i], props);
            }
            return new Selection(pos);
        }, mergeKey);
    }

    /// <summary>Inserts text at a position as part of a transaction; returns the position after it.</summary>
    public DocPos InsertRun(Transaction t, DocPos pos, string text, RunProps? props = null)
    {
        var p = pos.Para;
        t.Touch(p);
        var runProps = props?.Clone() ?? PendingProps?.Clone() ?? p.PropsForTypingAt(pos.Offset);
        runProps.Remove("rPrChange");
        var link = LinkAt(p, pos.Offset);
        p.InsertText(pos.Offset, text, runProps, link, TrackChanges ? NewRevision() : null);
        return new DocPos(p, pos.Offset + text.Length);
    }

    /// <summary>The hyperlink to continue when typing at a position: only inside a link, not at its ends.</summary>
    private static Hyperlink? LinkAt(Paragraph p, int offset)
    {
        var before = offset > 0 ? p.InlineAt(offset - 1) : null;
        var after = p.InlineAt(offset);
        if (before?.Link != null && ReferenceEquals(before.Link, after?.Link)) return before.Link;
        return null;
    }

    /// <summary>Enter: splits the paragraph. An empty list item ends the list instead.</summary>
    public void Enter()
    {
        var caret = Selection.Start;
        Edit("New paragraph", t =>
        {
            var pos = Selection.IsEmpty ? Selection.Active : DeleteRange(t, Selection.Start, Selection.End);
            EnsureEditable(pos.Para);
            var p = pos.Para;
            if (p.IsEmpty && p.Props.HasNumbering && p.Props.NumId is > 0)
            {
                // Enter on an empty list item: a level up, or out of the list.
                t.Touch(p);
                var level = p.Props.NumLevel ?? 0;
                if (level > 0) p.Props.NumLevel = level - 1;
                else
                {
                    p.Props.Remove("numPr");
                    if (p.Props.StyleId == "ListParagraph") p.Props.StyleId = null;
                    p.Props.Remove("ind");
                }
                return new Selection(pos);
            }
            return new Selection(SplitParagraph(t, pos, keepStyle: false));
        });
        _ = caret;
    }

    /// <summary>Splits a paragraph at a position; returns the start of the new (second) paragraph.</summary>
    public DocPos SplitParagraph(Transaction t, DocPos pos, bool keepStyle)
    {
        var p = pos.Para;
        var list = p.Parent ?? throw new InvalidOperationException("Paragraph without a parent.");
        t.Touch(p);
        t.Touch(list);
        var atEnd = pos.Offset >= p.Length;
        var tail = p.TakeTail(pos.Offset);
        var np = new Paragraph { Props = p.Props.Clone() };
        np.Props.Remove("pPrChange");
        np.Inlines.AddRange(tail);
        // The section break stays at the end: it moves to the second part.
        if (p.Props.Section != null)
        {
            np.Props.Section = p.Props.Section.Clone();
            p.Props.Section = null;
        }
        if (atEnd && !keepStyle)
        {
            var style = Doc.Styles.Get(p.Props.StyleId ?? Doc.Styles.DefaultParagraphStyleId);
            var next = style?.Next;
            if (next != null && next != style!.Id && Doc.Styles.Get(next) != null)
            {
                np.Props.StyleId = next == Doc.Styles.DefaultParagraphStyleId ? null : next;
                if (!p.Props.HasNumbering) np.Props.Remove("numPr");
            }
            // A new paragraph after a page-break-before heading doesn't break the page again.
            np.Props.PageBreakBefore = null;
        }
        if (TrackChanges)
        {
            var mark = p.Props.MarkProps(create: true)!;
            mark.SetMarkRevision("ins", NewRevision());
        }
        // An empty first part keeps the typing formatting of the text that moved on.
        if (p.Inlines.Count == 0 && np.Inlines.FirstOrDefault() is TextRun first)
        {
            var mark = p.Props.MarkProps(create: true)!;
            foreach (var e in first.Props.X.Elements().Where(e => e.Name.LocalName is not ("ins" or "del" or "rPrChange")))
                mark.SetElement(e.Name.LocalName, new System.Xml.Linq.XElement(e));
        }
        list.Insert(list.IndexOf(p) + 1, np);
        return new DocPos(np, 0);
    }

    /// <summary>Shift+Enter: a line break; Ctrl+Enter: a page break; Ctrl+Shift+Enter: a column break.</summary>
    public void InsertBreak(char kind)
    {
        var label = kind switch
        {
            TextRun.PageBreak => "Page break",
            TextRun.ColumnBreak => "Column break",
            _ => "Line break",
        };
        Edit(label, t =>
        {
            var pos = Selection.IsEmpty ? Selection.Active : DeleteRange(t, Selection.Start, Selection.End);
            EnsureEditable(pos.Para);
            if (kind == TextRun.PageBreak && Nav.CellOf(pos.Para) != null) Refuse("A page break can't go inside a table.");
            pos = InsertRun(t, pos, kind.ToString());
            // A page break gets its own paragraph line after it, like Word.
            if (kind == TextRun.PageBreak && pos.Offset >= pos.Para.Length) pos = SplitParagraph(t, pos, keepStyle: true);
            return new Selection(pos);
        });
    }

    // ------------------------------------------------------------------ deleting

    /// <summary>Backspace (Ctrl: the word before).</summary>
    public void Backspace(bool word = false)
    {
        if (!Selection.IsEmpty)
        {
            DeleteSelection();
            return;
        }
        var caret = Selection.Active;
        var p = caret.Para;
        if (caret.Offset == 0)
        {
            Edit("Delete", t =>
            {
                EnsureEditable(p);
                // A list item first loses its bullet / number.
                if (p.Props.HasNumbering && p.Props.NumId is > 0)
                {
                    t.Touch(p);
                    p.Props.Remove("numPr");
                    if (p.Props.StyleId == "ListParagraph") p.Props.StyleId = null;
                    p.Props.Remove("ind");
                    return new Selection(caret);
                }
                var prev = Nav.Prev(p);
                if (prev == null) return null;
                // Only merge paragraphs that sit side by side (not across a table edge or cell boundary).
                if (!ReferenceEquals(prev.Parent, p.Parent) || p.Parent!.IndexOf(prev) != p.Parent.IndexOf(p) - 1)
                {
                    if (p.IsEmpty && p.Parent!.Count > 1 && Nav.CellOf(p) == null && !(p.Props.Section != null))
                    {
                        // An empty paragraph after a table: remove it and go to the end of the table.
                        t.Touch(p.Parent);
                        p.Parent.Remove(p);
                        return new Selection(new DocPos(prev, prev.Length));
                    }
                    return new Selection(new DocPos(prev, prev.Length));
                }
                EnsureEditable(prev);
                return new Selection(MergeWithNext(t, prev));
            });
            return;
        }
        var start = word ? WordStartBefore(p, caret.Offset) : PreviousPosition(p, caret.Offset);
        Edit(word ? "Delete word" : "Delete", t =>
        {
            EnsureEditable(p);
            return new Selection(DeleteRange(t, new DocPos(p, start), caret));
        }, word ? null : "del:" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(p));
    }

    /// <summary>Delete (Ctrl: the word after).</summary>
    public void DeleteForward(bool word = false)
    {
        if (!Selection.IsEmpty)
        {
            DeleteSelection();
            return;
        }
        var caret = Selection.Active;
        var p = caret.Para;
        if (caret.Offset >= p.Length)
        {
            Edit("Delete", t =>
            {
                EnsureEditable(p);
                var next = Nav.Next(p);
                if (next == null) return null;
                if (!ReferenceEquals(next.Parent, p.Parent) || p.Parent!.IndexOf(next) != p.Parent.IndexOf(p) + 1)
                {
                    // An empty paragraph before a table: remove it.
                    if (p.IsEmpty && p.Parent!.Count > 1 && Nav.CellOf(p) == null && p.Props.Section == null)
                    {
                        t.Touch(p.Parent);
                        p.Parent.Remove(p);
                        return new Selection(new DocPos(next, 0));
                    }
                    return null;
                }
                EnsureEditable(next);
                return new Selection(MergeWithNext(t, p));
            });
            return;
        }
        var end = word ? WordEndAfter(p, caret.Offset) : NextPosition(p, caret.Offset);
        Edit(word ? "Delete word" : "Delete", t =>
        {
            EnsureEditable(p);
            return new Selection(DeleteRange(t, caret, new DocPos(p, end)));
        });
    }

    public void DeleteSelection()
    {
        if (Selection.IsEmpty) return;
        Edit("Delete", t => new Selection(DeleteRange(t, Selection.Start, Selection.End)));
    }

    /// <summary>Joins the paragraph after <paramref name="p"/> onto it. Returns the join position.</summary>
    private DocPos MergeWithNext(Transaction t, Paragraph p)
    {
        var list = p.Parent!;
        var next = (Paragraph)list[list.IndexOf(p) + 1];
        var join = new DocPos(p, p.Length);
        if (TrackChanges)
        {
            // Tracked: mark this paragraph's mark as deleted instead of joining (unless this author just inserted it).
            t.Touch(p);
            var mark = p.Props.MarkProps(create: true)!;
            var inserted = mark.MarkInserted;
            if (inserted == null || !inserted.SameAuthor(NewRevision()))
            {
                mark.SetMarkRevision("del", NewRevision());
                return new DocPos(next, 0);
            }
        }
        t.Touch(p);
        t.Touch(next);
        t.Touch(list);
        // An empty paragraph takes the next one's formatting (deleting an empty line keeps the text's style);
        // otherwise the next paragraph's section break (if any) survives and this one's merges away.
        if (p.IsEmpty) p.Props = next.Props.Clone();
        else p.Props.Section = next.Props.Section?.Clone();
        p.Inlines.AddRange(next.Inlines);
        p.Normalize();
        list.Remove(next);
        p.Props.MarkProps()?.Remove("ins");
        return join;
    }

    /// <summary>
    /// Deletes [start, end) in one story (across paragraphs and whole tables). Tracked changes mark text deleted
    /// instead. Returns the position where the deleted content was.
    /// </summary>
    public DocPos DeleteRange(Transaction t, DocPos start, DocPos end)
    {
        if (Nav.Compare(start, end) > 0) (start, end) = (end, start);
        if (start == end) return start;
        if (ReferenceEquals(start.Para, end.Para))
        {
            EnsureEditable(start.Para);
            DeleteInParagraph(t, start.Para, start.Offset, end.Offset);
            return start;
        }

        var startCell = Nav.CellOf(start.Para);
        var endCell = Nav.CellOf(end.Para);
        if (startCell != null || endCell != null)
        {
            if (!ReferenceEquals(startCell, endCell) || startCell == null)
            {
                // Across table cells: clear the text of covered paragraphs, keep the table's structure.
                foreach (var p in Nav.Range(start.Para, end.Para).ToList())
                {
                    if (Nav.IsReadOnly(p)) continue;
                    var s = ReferenceEquals(p, start.Para) ? start.Offset : 0;
                    var e = ReferenceEquals(p, end.Para) ? end.Offset : p.Length;
                    DeleteInParagraph(t, p, s, e);
                }
                // Whole tables between two body paragraphs go entirely.
                RemoveCoveredTables(t, start.Para, end.Para);
                return start;
            }
        }

        // Same container (body, cell, note…): delete between, then join.
        var first = start.Para;
        var last = end.Para;
        EnsureEditable(first);
        if (TrackChanges)
        {
            foreach (var p in Nav.Range(first, last).ToList())
            {
                if (Nav.IsReadOnly(p)) continue;
                var s = ReferenceEquals(p, first) ? start.Offset : 0;
                var e = ReferenceEquals(p, last) ? end.Offset : p.Length;
                DeleteInParagraph(t, p, s, e);
                if (!ReferenceEquals(p, last))
                {
                    t.Touch(p);
                    p.Props.MarkProps(create: true)!.SetMarkRevision("del", NewRevision());
                }
            }
            return start;
        }

        var list = first.Parent!;
        var firstTop = TopIn(list, first);
        var lastTop = TopIn(list, last);
        if (firstTop == null || lastTop == null || !ReferenceEquals(firstTop, first) || !ReferenceEquals(lastTop, last))
        {
            // The ends are nested differently (one inside a content control): clear text only.
            foreach (var p in Nav.Range(first, last).ToList())
            {
                if (Nav.IsReadOnly(p)) continue;
                var s = ReferenceEquals(p, first) ? start.Offset : 0;
                var e = ReferenceEquals(p, last) ? end.Offset : p.Length;
                DeleteInParagraph(t, p, s, e);
            }
            return start;
        }
        t.Touch(list);
        t.Touch(first);
        t.Touch(last);
        DeleteInParagraph(t, first, start.Offset, first.Length);
        DeleteInParagraph(t, last, 0, end.Offset);
        var i0 = list.IndexOf(first);
        var i1 = list.IndexOf(last);
        // Markers (bookmarks, comment ranges) inside removed blocks are kept on the joined paragraph.
        var keptMarkers = new List<Inline>();
        for (var i = i0 + 1; i < i1; i++)
            if (list[i] is Paragraph mid) keptMarkers.AddRange(mid.Inlines.Where(x => x.Length == 0 && x is BookmarkMarker or CommentMarker));
        list.RemoveRange(i0 + 1, i1 - i0 - 1);
        first.Inlines.AddRange(keptMarkers);
        // Join: the last paragraph's remaining text moves up; its section break survives.
        first.Inlines.AddRange(last.Inlines);
        if (last.Props.Section != null || first.Props.Section != null) first.Props.Section = last.Props.Section?.Clone();
        if (first.IsEmpty && start.Offset == 0) first.Props = last.Props.Clone();
        first.Normalize();
        list.Remove(last);
        return start;
    }

    private static Block? TopIn(BlockList list, Paragraph p)
    {
        Block current = p;
        while (current.Parent != null && !ReferenceEquals(current.Parent, list))
        {
            switch (current.Parent.Owner)
            {
                case TableCell cell:
                    current = cell.Row!.Table!;
                    break;
                case Block b:
                    current = b;
                    break;
                default:
                    return null;
            }
        }
        return ReferenceEquals(current.Parent, list) ? current : null;
    }

    private void RemoveCoveredTables(Transaction t, Paragraph first, Paragraph last)
    {
        var list = first.Parent;
        if (list == null || !ReferenceEquals(list, last.Parent) || list.Owner is TableCell) return;
        var i0 = list.IndexOf(first);
        var i1 = list.IndexOf(last);
        if (i0 < 0 || i1 < 0) return;
        t.Touch(list);
        for (var i = i1 - 1; i > i0; i--)
            if (list[i] is Table) list.RemoveAt(i);
    }

    /// <summary>Deletes positions [start, end) of one paragraph (tracked: marks them deleted).</summary>
    public void DeleteInParagraph(Transaction t, Paragraph p, int start, int end)
    {
        if (end <= start) return;
        t.Touch(p);
        if (!TrackChanges)
        {
            p.RemoveRange(start, end);
            return;
        }
        var rev = NewRevision();
        var first = p.SplitAt(start);
        var last = p.SplitAt(end);
        for (var i = last - 1; i >= first; i--)
        {
            var inline = p.Inlines[i];
            if (inline.Length == 0) continue;
            // Text this author inserted (and hasn't been deleted) simply goes away.
            if (inline.Inserted != null && inline.Inserted.SameAuthor(rev) && inline.Deleted == null)
            {
                p.Inlines.RemoveAt(i);
                continue;
            }
            inline.Deleted ??= rev.Clone();
        }
        p.Normalize();
    }

    // ------------------------------------------------------------------ positions inside a paragraph

    /// <summary>The previous caret position (skips hidden-in-the-middle characters such as surrogate halves).</summary>
    public static int PreviousPosition(Paragraph p, int offset)
    {
        if (offset <= 0) return 0;
        var text = p.Text;
        var o = offset - 1;
        if (o > 0 && char.IsLowSurrogate(text[o]) && char.IsHighSurrogate(text[o - 1])) o--;
        // Combining marks (Arabic diacritics, accents) go with their base letter.
        while (o > 0 && IsCombining(text[o])) o--;
        return o;
    }

    public static int NextPosition(Paragraph p, int offset)
    {
        var text = p.Text;
        if (offset >= text.Length) return text.Length;
        var o = offset + 1;
        if (o < text.Length && char.IsLowSurrogate(text[o]) && char.IsHighSurrogate(text[o - 1])) o++;
        while (o < text.Length && IsCombining(text[o])) o++;
        return o;
    }

    private static bool IsCombining(char c)
    {
        var cat = char.GetUnicodeCategory(c);
        return cat is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark or System.Globalization.UnicodeCategory.EnclosingMark;
    }

    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '\'' or '’' || IsCombining(c);

    /// <summary>Start of the word before the offset (Ctrl+Left, Ctrl+Backspace).</summary>
    public static int WordStartBefore(Paragraph p, int offset)
    {
        var text = p.Text;
        var o = Math.Min(offset, text.Length);
        while (o > 0 && char.IsWhiteSpace(text[o - 1])) o--;
        if (o > 0 && !IsWordChar(text[o - 1]))
        {
            while (o > 0 && !IsWordChar(text[o - 1]) && !char.IsWhiteSpace(text[o - 1])) o--;
            return o;
        }
        while (o > 0 && IsWordChar(text[o - 1])) o--;
        return o;
    }

    /// <summary>Start of the next word after the offset (Ctrl+Right, Ctrl+Delete deletes to here).</summary>
    public static int WordEndAfter(Paragraph p, int offset)
    {
        var text = p.Text;
        var o = offset;
        if (o < text.Length && IsWordChar(text[o]))
            while (o < text.Length && IsWordChar(text[o])) o++;
        else if (o < text.Length && !char.IsWhiteSpace(text[o]))
            while (o < text.Length && !IsWordChar(text[o]) && !char.IsWhiteSpace(text[o])) o++;
        while (o < text.Length && char.IsWhiteSpace(text[o]) && text[o] != '\t') o++;
        return o;
    }

    /// <summary>The word around an offset (double-click).</summary>
    public static (int Start, int End) WordAt(Paragraph p, int offset)
    {
        var text = p.Text;
        if (text.Length == 0) return (0, 0);
        var o = MathEx.Clamp(offset, 0, text.Length - 1);
        if (!IsWordChar(text[o]) && o > 0 && IsWordChar(text[o - 1])) o--;
        if (!IsWordChar(text[o])) return (o, Math.Min(text.Length, o + 1));
        var s = o;
        while (s > 0 && IsWordChar(text[s - 1])) s--;
        var e = o;
        while (e < text.Length && IsWordChar(text[e])) e++;
        // Double-click selects the trailing space too, like Word.
        while (e < text.Length && text[e] == ' ') e++;
        return (s, e);
    }

    /// <summary>The sentence around an offset (Ctrl+click).</summary>
    public static (int Start, int End) SentenceAt(Paragraph p, int offset)
    {
        var text = p.Text;
        var s = Math.Min(offset, text.Length);
        while (s > 0 && !(text[s - 1] is '.' or '!' or '?' or '۔' or '؟' && (s >= text.Length || char.IsWhiteSpace(text[s])))) s--;
        while (s < text.Length && char.IsWhiteSpace(text[s])) s++;
        var e = Math.Min(offset, text.Length);
        while (e < text.Length && !(text[e] is '.' or '!' or '?' or '۔' or '؟')) e++;
        if (e < text.Length) e++;
        while (e < text.Length && text[e] == ' ') e++;
        return (s, e);
    }
}
