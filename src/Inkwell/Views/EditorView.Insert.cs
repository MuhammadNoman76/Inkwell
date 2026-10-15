using System.Windows;
using System.Windows.Input;
using System.Xml.Linq;
using Inkwell.Core.Editing;
using Inkwell.Core.Model;
using Inkwell.Services;
using Inkwell.Views.Panels;

namespace Inkwell.Views;

/// <summary>Inserting things (pictures, tables, links, comments, notes, captions, references, bookmarks, symbols) and the format painter.</summary>
public partial class EditorView
{
    public void InsertPicture()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Insert a picture",
            Filter = "Pictures|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.tif;*.tiff|All files|*.*",
            CheckFileExists = true,
            Multiselect = true,
        };
        if (AppSettings.Current.LastFolder is { } folder && Directory.Exists(folder)) dialog.InitialDirectory = folder;
        if (dialog.ShowDialog(_window) != true) return;
        foreach (var file in dialog.FileNames)
        {
            byte[] data;
            try
            {
                data = File.ReadAllBytes(file);
            }
            catch (Exception ex)
            {
                _window.ShowMessage("The picture couldn't be read: " + ex.Message, 5, warning: true);
                continue;
            }
            Run(() => Editor.InsertPicture(data, null));
        }
        Doc.ScrollToCaret();
        FocusDocument();
    }

    public void InsertTableDefault() => InsertTable(3, 3);

    public void InsertTable(int rows, int columns)
    {
        Run(() => Editor.InsertTable(rows, columns, headerRow: true));
        Doc.ScrollToCaret();
        FocusDocument();
    }

    /// <summary>Insert or edit a link (to a web address or a heading / bookmark in the document).</summary>
    public void InsertLink()
    {
        var existing = Editor.LinkAtSelection();
        var targets = Editor.CrossReferenceTargets().Where(t => t.Kind == "Heading").Select(t => (t.Text, t.Para)).ToList();
        var bookmarks = Session.Document.Bookmarks().Select(b => b.Name).Where(b => !b.StartsWith("_", StringComparison.Ordinal)).Distinct().ToList();
        var selected = Editor.Selection.IsEmpty ? "" : string.Concat(Editor.SelectedRanges().Select(r => r.Para.Text.Substring(r.Start, r.End - r.Start)));
        var result = LinkDialog.Show(_window, existing, selected, targets, bookmarks);
        if (result == null) return;
        Run(() =>
        {
            if (result.Remove && existing != null)
            {
                Editor.RemoveLink(existing);
                return;
            }
            var anchor = result.Anchor;
            if (result.HeadingTarget != null) anchor = Editor.AddBookmark("_Ref" + Compat.NextRandom(100000000, 999999999), new Selection(new DocPos(result.HeadingTarget, 0), new DocPos(result.HeadingTarget, result.HeadingTarget.Length)));
            if (existing != null) Editor.EditLink(existing, result.Url, anchor, result.Tooltip);
            else Editor.InsertLink(result.Url, anchor, Editor.Selection.IsEmpty ? result.Text : null, result.Tooltip);
        });
        FocusDocument();
    }

    /// <summary>Adds a comment on the selection and opens it in the review panel to type.</summary>
    public void AddComment()
    {
        if (Editor.Selection.Active.Para.Story is Comment) return;
        Comment? comment = null;
        Run(() => comment = Editor.AddComment(""));
        if (comment == null) return;
        ShowPanel(PanelMode.Review);
        Panel.EditComment(comment);
    }

    public void InsertNote(NoteKind kind)
    {
        Run(() => Editor.InsertNote(kind));
        Doc.ScrollToCaret();
        FocusDocument();
    }

    public void InsertCaption()
    {
        var p = Editor.Selection.Active.Para;
        var inTable = Nav.CellOf(p) != null;
        var hasPicture = p.Inlines.OfType<DrawingObject>().Any();
        var result = CaptionDialog.Show(_window, inTable ? "Table" : hasPicture ? "Figure" : "Figure", inTable);
        if (result == null) return;
        Run(() => Editor.InsertCaption(result.Value.Label, result.Value.Text, result.Value.Above));
        FocusDocument();
    }

    public void InsertCrossReference()
    {
        var targets = Editor.CrossReferenceTargets();
        if (targets.Count == 0)
        {
            _window.ShowMessage("There's nothing to refer to yet: add headings or captions first.", 5);
            return;
        }
        var choice = CrossReferenceDialog.Show(_window, targets);
        if (choice == null) return;
        Run(() => Editor.InsertCrossReference(choice.Value.Target, choice.Value.Show, Layout));
        FocusDocument();
    }

    public void InsertBookmark()
    {
        var suggestion = Editor.Selection.IsEmpty ? "" : new string(string.Concat(Editor.SelectedRanges().Select(r => r.Para.Text.Substring(r.Start, r.End - r.Start)))
            .Where(c => char.IsLetterOrDigit(c) || c == '_').Take(30).ToArray());
        var name = InputDialog.Ask(_window, "Bookmark", "A name for this place (letters, digits and _; it must start with a letter). Links can point to it.", suggestion, "Add bookmark");
        if (name == null) return;
        name = name.Trim().Replace(' ', '_');
        if (name.Length == 0 || !char.IsLetter(name[0]) || name.Any(c => !char.IsLetterOrDigit(c) && c != '_') || name.Length > 40)
        {
            _window.ShowMessage("A bookmark name starts with a letter and has only letters, digits and _ (40 at most).", 5, warning: true);
            return;
        }
        Run(() => Editor.AddBookmark(name));
        _window.ShowMessage($"Bookmark “{name}” added. Links can point to it.", 3);
    }

    public void ShowCitations(bool insert) => CitationDialog.Show(this, insert);

    public void InsertSymbol() => SymbolPicker.Show(this);

    // ------------------------------------------------------------------ format painter

    private sealed class CopiedFormat
    {
        public RunProps Run = new();
        public ParaProps? Para;
    }

    private CopiedFormat? _copiedFormat;
    private bool _formatPainter;

    /// <summary>Copies the formatting at the caret; the next selection made with the mouse gets it (Esc cancels).</summary>
    private void StartFormatPainter()
    {
        var sel = Editor.Selection;
        var p = sel.Start.Para;
        var inline = sel.IsEmpty ? p.InlineAt(Math.Max(0, sel.Start.Offset - 1)) ?? p.InlineAt(sel.Start.Offset) : p.InlineAt(sel.Start.Offset);
        var run = (inline?.Props ?? p.PropsForTypingAt(sel.Start.Offset)).Clone();
        run.Remove("rPrChange");
        // A whole paragraph (or just the caret) carries the paragraph's look too, like Word.
        var wholeParagraph = sel.IsEmpty || (sel.Start.Offset == 0 && (sel.End.Offset >= sel.End.Para.Length || !sel.InOneParagraph));
        _copiedFormat = new CopiedFormat { Run = run, Para = wholeParagraph ? p.Props.FormattingOnly() : null };
        _formatPainter = true;
        Doc.Cursor = Cursors.Pen;
        Doc.PreviewMouseLeftButtonUp -= OnPainterMouseUp;
        Doc.PreviewMouseLeftButtonUp += OnPainterMouseUp;
        _window.ShowMessage("Select the text that should look like this. Esc cancels.", 3);
    }

    private void StopFormatPainter()
    {
        _formatPainter = false;
        Doc.Cursor = Cursors.IBeam;
        Doc.PreviewMouseLeftButtonUp -= OnPainterMouseUp;
    }

    private void OnPainterMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_formatPainter) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_formatPainter) return;
            StopFormatPainter();
            PasteFormatting();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Applies the copied formatting to the selection (or the word at the caret).</summary>
    private void PasteFormatting()
    {
        var copied = _copiedFormat;
        if (copied == null) return;
        Run(() =>
        {
            Editor.ApplyRunFormat("Paste formatting", r =>
            {
                var keep = r.X.Elements().Where(x => x.Name.LocalName is "ins" or "del" or "rPrChange").Select(x => new XElement(x)).ToList();
                r.X.RemoveNodes();
                foreach (var x in keep) r.X.Add(x);
                foreach (var x in copied.Run.X.Elements().Where(x => x.Name.LocalName is not ("ins" or "del" or "rPrChange"))) r.X.Add(new XElement(x));
            });
            if (copied.Para != null)
            {
                var para = copied.Para;
                Editor.ApplyParaFormat("Paste formatting", p =>
                {
                    var section = p.Section;
                    var keep = p.X.Elements().Where(x => x.Name.LocalName is "rPr" or "pPrChange").Select(x => new XElement(x)).ToList();
                    p.X.RemoveNodes();
                    foreach (var x in para.X.Elements()) p.X.Add(new XElement(x));
                    foreach (var x in keep) p.X.Add(x);
                    p.Section = section;
                });
            }
        });
        FocusDocument();
    }
}
