using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Inkwell.Controls;
using Inkwell.Core.Editing;
using Inkwell.Core.Layout;
using Inkwell.Core.Model;
using Inkwell.Services;
using Inkwell.Views.Panels;

namespace Inkwell.Views;

/// <summary>Moving around: links, the table of contents, pages, changes, headers and footers; the right-click menu.</summary>
public partial class EditorView
{
    // ------------------------------------------------------------------ going places

    /// <summary>Puts the caret at a position and scrolls it into the middle of the view.</summary>
    public void GoTo(DocPos pos)
    {
        if (pos.Para == null) return;
        if (Doc.EditingHeaderFooter && pos.Para.Story is not HeaderFooter) Doc.ExitHeaderFooter();
        Doc.SelectDrawing(null);
        Editor.Selection = new Selection(pos);
        Doc.ScrollToCaret(center: true);
        FocusDocument();
    }

    public void GoTo(Selection selection)
    {
        if (selection.Active.Para == null) return;
        if (Doc.EditingHeaderFooter && selection.Active.Para.Story is not HeaderFooter) Doc.ExitHeaderFooter();
        Editor.Selection = selection;
        Doc.ScrollToCaret(center: true);
        FocusDocument();
    }

    private void FollowLink(Hyperlink link)
    {
        if (!string.IsNullOrEmpty(link.Anchor))
        {
            var target = Editor.FindBookmark(link.Anchor!);
            if (target != null) GoTo(target.Value);
            else _window.ShowMessage($"The place this link points to (“{link.Anchor}”) isn't in the document any more.", 5, warning: true);
            return;
        }
        if (!string.IsNullOrEmpty(link.Url)) LinkLauncher.Open(_window, link.Url!);
    }

    /// <summary>A click on a table of contents entry jumps to its heading.</summary>
    private void JumpFromToc(Paragraph entry)
    {
        var anchor = entry.Inlines.Select(i => i.Link?.Anchor).FirstOrDefault(a => !string.IsNullOrEmpty(a));
        if (anchor == null)
        {
            foreach (var field in entry.Inlines.OfType<FieldInline>())
            {
                var code = FieldCode.Parse(field.Code);
                if (code.Type == "PAGEREF" && code.FirstArgument is { Length: > 0 } name)
                {
                    anchor = name;
                    break;
                }
            }
        }
        if (anchor != null && Editor.FindBookmark(anchor) is { } pos)
        {
            GoTo(pos);
            return;
        }
        // An entry without a bookmark: find the heading with the same text.
        var text = entry.PlainText().Split('\t')[0].Trim();
        var heading = Session.Document.Body.Paragraphs.FirstOrDefault(p => p != entry && Editor.Styles.ResolvePara(p).OutlineLevel < 9 && p.PlainText().Trim() == text);
        if (heading != null) GoTo(new DocPos(heading, 0));
    }

    private void GoToPagePrompt()
    {
        if (Layout.View.Mode == ViewMode.Pageless)
        {
            _window.ShowMessage("Pageless view has no pages. Switch to page view to go to a page.", 4);
            return;
        }
        var current = (Doc.CurrentPage?.Index ?? 0) + 1;
        var answer = InputDialog.Ask(_window, "Go to page", $"Page number (1 to {Layout.Pages.Count})", current.ToString(CultureInfo.CurrentCulture), "Go");
        if (answer == null) return;
        if (!int.TryParse(answer.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var n) || n < 1 || n > Layout.Pages.Count)
        {
            _window.ShowMessage($"There's no page {answer.Trim()}. The document has {Layout.Pages.Count}.", 4, warning: true);
            return;
        }
        GoToPage(n - 1);
    }

    public void GoToPage(int index)
    {
        if (index < 0 || index >= Layout.Pages.Count) return;
        var page = Layout.Pages[index];
        var first = page.Columns.SelectMany(c => c.Content).Select(FirstParagraphIn).FirstOrDefault(p => p != null);
        if (first != null)
        {
            var frag = page.Columns.SelectMany(c => c.Content).OfType<ParaFragment>().FirstOrDefault(f => ReferenceEquals(f.Paragraph, first.Value.Para));
            Editor.Selection = new Selection(frag != null ? new DocPos(first.Value.Para, frag.Lines.FirstOrDefault()?.DocStart ?? 0) : first.Value);
        }
        Doc.GoToPage(index);
        FocusDocument();
    }

    private static DocPos? FirstParagraphIn(Fragment f) => f switch
    {
        ParaFragment pf => new DocPos(pf.Paragraph, pf.Lines.FirstOrDefault()?.DocStart ?? 0),
        TableFragment tf => tf.Rows.SelectMany(r => r.Cells).SelectMany(c => c.Content).Select(FirstParagraphIn).FirstOrDefault(p => p != null),
        _ => null,
    };

    private void GoToChange(int direction)
    {
        var change = Editor.FindChange(direction);
        if (change == null)
        {
            _window.ShowMessage("There are no tracked changes.", 3);
            return;
        }
        GoTo(new Selection(new DocPos(change.Paragraph, change.Start), new DocPos(change.Paragraph, change.End)));
    }

    // ------------------------------------------------------------------ headers and footers

    /// <summary>A double-click in the header or footer area: edit it (creating it when the page has none).</summary>
    private void EditHeaderFooter(PageLayout page, bool header)
    {
        if (Layout.View.Mode == ViewMode.Pageless)
        {
            _window.ShowMessage("Headers and footers show in page view.", 3);
            return;
        }
        var story = header ? page.HeaderStory : page.FooterStory;
        var index = page.Index;
        if (story == null)
        {
            var kind = page.FirstOfSection && page.Section.TitlePage ? "first"
                : Session.Document.Settings.EvenAndOddHeaders && page.Number % 2 == 0 ? "even" : "default";
            try
            {
                story = Editor.EnsureHeaderFooter(page.SectionIndex, header, kind);
            }
            catch (EditRefusedException ex)
            {
                _window.ShowMessage(ex.Message, 5, warning: true);
                return;
            }
            Layout.Update();
            if (index >= Layout.Pages.Count) return;
            page = Layout.Pages[index];
        }
        var target = Nav.LastIn(story.Blocks) ?? Nav.EndOf(story).Para;
        Doc.EnterHeaderFooter(page, header, target);
        Doc.ScrollToCaret();
        RefreshState();
        if (!_headerHintShown)
        {
            _headerHintShown = true;
            _window.ShowMessage("Editing the " + (header ? "header" : "footer") + ". Page numbers, first-page and odd/even options are in the panel. Esc goes back to the text.", 5);
        }
        ShowPanel(PanelMode.Page);
    }

    private bool _headerHintShown;

    public void EditHeaderFooterOfCurrentPage(bool header)
    {
        var page = Doc.CurrentPage ?? (Layout.Pages.Count > 0 ? Layout.Pages[0] : null);
        if (page == null) return;
        EditHeaderFooter(page, header);
    }

    // ------------------------------------------------------------------ pictures

    /// <summary>A floating picture was dragged: it stays exactly where it was dropped (relative to the margins).</summary>
    private void OnDrawingMoved(DrawingObject drawing, PageLayout page, Rect box)
    {
        Paragraph? anchor = null;
        // Dropped on another page than its anchor's: anchor it to that page's text, nearest to the picture's top.
        if (!page.Floats.Any(f => ReferenceEquals(f.Drawing, drawing)))
        {
            var candidates = page.Columns.SelectMany(c => c.Content).OfType<ParaFragment>().Where(f => f.FirstLineIndex == 0).ToList();
            anchor = candidates.LastOrDefault(f => f.Y <= box.Top + 1)?.Paragraph ?? candidates.FirstOrDefault()?.Paragraph;
        }
        Run(() => Editor.MoveDrawing(drawing, page, box, anchor));
    }

    // ------------------------------------------------------------------ the right-click menu

    private void ShowContextMenu(PageHit? hit, Point viewPoint)
    {
        var menu = new ContextMenu { PlacementTarget = Doc, Placement = PlacementMode.RelativePoint, HorizontalOffset = viewPoint.X, VerticalOffset = viewPoint.Y };
        var items = menu.Items;
        var caret = Editor.Selection.Active;

        // Spelling suggestions first.
        if (hit != null && Editor.Selection.IsEmpty && SpellingErrorAt(hit.Position) is { } error)
        {
            var suggestions = Suggestions(error.Word, error.Language);
            if (suggestions.Count == 0)
            {
                items.Add(new MenuItem { Header = "No suggestions", IsEnabled = false });
            }
            foreach (var s in suggestions.Take(6))
            {
                var replacement = s;
                var item = Item(replacement, null, () => ReplaceMisspelling(error, replacement));
                item.FontWeight = FontWeights.SemiBold;
                items.Add(item);
            }
            items.Add(Item("Ignore", null, () => IgnoreWord(error.Word)));
            items.Add(Item("Add to dictionary", "Icon.BookAdd", () => AddToDictionary(error.Word, error.Language)));
            items.Add(new Separator());
        }

        // Pictures.
        var drawing = Doc.SelectedDrawing ?? hit?.Float?.Drawing ?? hit?.InlineDrawing;
        if (drawing != null)
        {
            Doc.SelectDrawing(drawing);
            items.Add(Item("Picture options…", "Icon.Image", () => ShowPanel(PanelMode.Picture)));
            if (drawing.IsFloating)
                items.Add(Item(drawing.Float?.Locked == true ? "Unlock position" : "Lock position", "Icon.LockClosed", () => Run(() => Editor.SetDrawingLocked(drawing, drawing.Float?.Locked != true))));
            items.Add(Item("Alt text…", "Icon.TextDescription", () => EditAltText(drawing)));
            items.Add(Item("Cut", "Icon.Cut", Cut, "Ctrl+X"));
            items.Add(Item("Copy", "Icon.Copy", Copy, "Ctrl+C"));
            items.Add(Item("Delete picture", "Icon.Delete", () =>
            {
                Run(() => Editor.DeleteDrawing(drawing));
                Doc.SelectDrawing(null);
            }, "Del"));
            menu.IsOpen = true;
            return;
        }

        // Links.
        var link = Editor.LinkAtSelection();
        if (link != null)
        {
            items.Add(Item(link.Anchor != null ? "Go to the link's place" : "Open link", "Icon.Open", () => FollowLink(link)));
            items.Add(Item("Edit link…", "Icon.Link", () => InsertLink()));
            if (link.Url != null) items.Add(Item("Copy link address", "Icon.Copy", () => TryCopyText(link.Url!)));
            items.Add(Item("Remove link", "Icon.LinkDismiss", () => Run(() => Editor.RemoveLink(link))));
            items.Add(new Separator());
        }

        // Tracked changes.
        if (Editor.Revisions().Any(r => ReferenceEquals(r.Paragraph, caret.Para) && r.Start <= caret.Offset && caret.Offset <= r.End))
        {
            items.Add(Item("Accept change", "Icon.Accept", () => Run(() => Editor.AcceptOrRejectAtSelection(true))));
            items.Add(Item("Reject change", "Icon.Reject", () => Run(() => Editor.AcceptOrRejectAtSelection(false))));
            items.Add(new Separator());
        }

        items.Add(Item("Cut", "Icon.Cut", Cut, "Ctrl+X", HasSelection));
        items.Add(Item("Copy", "Icon.Copy", Copy, "Ctrl+C", HasSelection));
        items.Add(Item("Paste", "Icon.ClipboardPaste", () => Paste(PasteMode.Default), "Ctrl+V"));
        items.Add(Item("Paste text only", "Icon.ClipboardText", () => Paste(PasteMode.TextOnly), "Ctrl+Shift+V"));
        items.Add(new Separator());

        // Tables.
        if (InTable)
        {
            items.Add(Item("Insert row below", "Icon.TableInsertRow", () => Run(() => Editor.InsertRows(true))));
            items.Add(Item("Insert row above", "Icon.TableInsertRow", () => Run(() => Editor.InsertRows(false))));
            items.Add(Item("Insert column right", "Icon.TableInsertColumn", () => Run(() => Editor.InsertColumn(true))));
            items.Add(Item("Delete row", "Icon.TableDeleteRow", () => Run(Editor.DeleteRows)));
            items.Add(Item("Delete column", "Icon.TableDeleteColumn", () => Run(Editor.DeleteColumn)));
            if (HasSelection) items.Add(Item("Merge cells", "Icon.TableCellsMerge", () => Run(Editor.MergeCells)));
            items.Add(Item("Table options…", "Icon.TableSettings", () => ShowPanel(PanelMode.Table)));
            items.Add(new Separator());
        }

        if (Editor.ListKindOf(caret.Para) == "number")
        {
            items.Add(Item("Restart numbering at 1", "Icon.TextNumberListLtr", () => Run(() => Editor.RestartNumbering(1))));
            if (Editor.CanContinueNumbering()) items.Add(Item("Continue from the list above", "Icon.TextNumberListLtr", () => Run(Editor.ContinueNumbering)));
            items.Add(new Separator());
        }

        items.Add(Item("Add comment", "Icon.CommentAdd", AddComment, "Ctrl+Alt+M"));
        items.Add(Item(link != null ? "Edit link…" : "Link…", "Icon.Link", InsertLink, "Ctrl+Shift+K"));
        items.Add(Item("Reveal formatting", "Icon.TextEffects", () => ShowPanel(PanelMode.Reveal), "Shift+F1"));
        items.Add(Item("Clear formatting", "Icon.TextClearFormatting", () => Run(Editor.ClearFormatting)));
        menu.IsOpen = true;
    }

    private MenuItem Item(string header, string? icon, Action action, string? gesture = null, bool enabled = true)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsEnabled = enabled };
        if (icon != null && TryFindResource(icon) is Geometry g) Ui.SetIcon(item, g);
        item.Click += (_, _) =>
        {
            Run(action);
            FocusDocument();
        };
        return item;
    }

    private void TryCopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or System.Runtime.InteropServices.ExternalException)
        {
            _window.ShowMessage("The clipboard is busy. Try again.", 3, warning: true);
        }
    }

    private void EditAltText(DrawingObject drawing)
    {
        var text = InputDialog.Ask(_window, "Alt text", "Describe the picture for people who can't see it (screen readers read this).", drawing.AltText ?? "", "Save");
        if (text == null) return;
        Run(() => Editor.SetAltText(drawing, text.Trim().Length == 0 ? null : text.Trim()));
    }
}

/// <summary>Opens web links in the browser; asks before anything else (files, programs).</summary>
internal static class LinkLauncher
{
    public static void Open(Window owner, string url)
    {
        url = url.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            if (Uri.TryCreate("https://" + url, UriKind.Absolute, out var web) && url.Contains('.') && !url.Contains(' ')) uri = web;
            else
            {
                MessageDialog.Show(owner, "This link can't be opened", $"“{url}” isn't a web address.", "OK", null, null, DialogIcon.Warning);
                return;
            }
        }
        var safe = uri.Scheme is "http" or "https" or "mailto";
        if (!safe)
        {
            var choice = MessageDialog.Show(owner, "Open this link?", $"It points to “{uri.OriginalString}”, which isn't a web page. Only open it if you trust this document.",
                "Open", null, "Cancel", DialogIcon.Warning);
            if (choice != DialogChoice.Primary) return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Open link");
            MessageDialog.Show(owner, "The link couldn't be opened", ex.Message, "OK", null, null, DialogIcon.Warning);
        }
    }
}

public partial class EditorView
{
    /// <summary>Ctrl+F / Ctrl+H: the find bar (with replace).</summary>
    public void OpenFind(bool replace) => Find.Open(replace);
}
