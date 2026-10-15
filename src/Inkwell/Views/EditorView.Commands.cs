using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Inkwell.Core.Editing;
using Inkwell.Core.Layout;
using Inkwell.Core.Model;
using Inkwell.Services;
using Inkwell.Themes;
using Inkwell.Views.Panels;

namespace Inkwell.Views;

public partial class EditorView
{
    private List<AppCommand>? _commands;
    private bool _paletteOpen;

    public IReadOnlyList<AppCommand> Commands => _commands ??= BuildCommands();

    private bool HasSelection => !Editor.Selection.IsEmpty;
    private bool InTable => Nav.CellOf(Editor.Selection.Active.Para) != null;
    private bool HasDrawing => Doc.SelectedDrawing != null;

    private List<AppCommand> BuildCommands()
    {
        var list = new List<AppCommand>
        {
            // File
            new("File", "Save", () => Save(), Shortcuts.Save, "Icon.Save") { WorksWhileTyping = true, WorksInDocument = true },
            new("File", "Save as…", () => SaveAs(), Shortcuts.SaveAs, "Icon.SaveCopy") { WorksWhileTyping = true, WorksInDocument = true },
            new("File", "Save as…", () => SaveAs(), Shortcuts.SaveAsF12, "Icon.SaveCopy") { WorksWhileTyping = true, WorksInDocument = true, InPalette = false },
            new("File", "Open…", () => OpenDocument(), Shortcuts.Open, "Icon.FolderOpen") { WorksWhileTyping = true, WorksInDocument = true },
            new("File", "New document", () => App.Current.ShowStart(null), Shortcuts.NewDocument, "Icon.DocumentAdd", "blank start template window") { WorksWhileTyping = true, WorksInDocument = true },
            new("File", "Export…", () => ShowExport(null), Shortcuts.Export, "Icon.ArrowExportLtr", "pdf markdown html text share") { WorksWhileTyping = true, WorksInDocument = true },
            new("File", "Export as PDF…", () => ShowExport("pdf"), null, "Icon.DocumentPdf", "portable"),
            new("File", "Export as Markdown…", () => ShowExport("md"), null, "Icon.Code", "md"),
            new("File", "Export as HTML…", () => ShowExport("html"), null, "Icon.Globe", "web page"),
            new("File", "Export as plain text…", () => ShowExport("txt"), null, "Icon.DocumentText", "txt"),
            new("File", "Print…", () => Print(), Shortcuts.Print, "Icon.Print", "printer paper") { WorksWhileTyping = true, WorksInDocument = true },
            new("File", "Version history…", () => ShowVersionHistory(), null, "Icon.History", "versions restore earlier backup snapshot") { CanExecute = () => Session.FilePath != null },
            new("File", "Document properties…", () => ShowProperties(), null, "Icon.Info", "title author subject keywords"),
            new("File", "Start screen", () => GoToStart(), null, "Icon.Home", "home recent close"),
            new("File", "Close", () => _window.Close(), Shortcuts.Close, "Icon.Dismiss") { WorksWhileTyping = true, WorksInDocument = true },
            new("App", "Settings", () => SettingsWindow.ShowSingle(_window), Shortcuts.Settings, "Icon.Settings", "preferences options autosave updates autocorrect") { WorksWhileTyping = true, WorksInDocument = true },
            new("App", "Menu", () => OpenAppMenu(), Shortcuts.Menu, null) { WorksWhileTyping = true, WorksInDocument = true, InPalette = false, CanExecute = () => !FocusMode },
            new("App", "About Inkwell", () => AboutDialog.Open(_window), null, "Icon.Info", "version licences"),
            new("App", "Use light mode", () => SetAppTheme(AppTheme.Light), null, "Icon.DarkTheme", "appearance theme"),
            new("App", "Use dark mode", () => SetAppTheme(AppTheme.Dark), null, "Icon.DarkTheme", "appearance theme night"),
            new("App", "Use my Windows setting for light / dark", () => SetAppTheme(AppTheme.System), null, "Icon.DarkTheme", "appearance theme"),
            new("App", "Dark pages in dark mode", () => AppSettings.Update(s => s.DarkPaper = !s.DarkPaper), null, "Icon.WeatherMoon", "paper night reading"),

            // Edit
            new("Edit", "Undo", () => Undo(), Shortcuts.Undo, "Icon.ArrowUndo") { CanExecute = () => Editor.History.CanUndo, WorksInDocument = true },
            new("Edit", "Redo", () => Redo(), Shortcuts.Redo, "Icon.ArrowRedo") { CanExecute = () => Editor.History.CanRedo, WorksInDocument = true },
            new("Edit", "Redo", () => Redo(), Shortcuts.RedoShiftZ, "Icon.ArrowRedo") { CanExecute = () => Editor.History.CanRedo, InPalette = false, WorksInDocument = true },
            new("Edit", "Cut", () => Cut(), Shortcuts.Cut, "Icon.Cut") { WorksInDocument = true },
            new("Edit", "Copy", () => Copy(), Shortcuts.Copy, "Icon.Copy") { WorksInDocument = true },
            new("Edit", "Paste", () => Paste(PasteMode.Default), Shortcuts.Paste, "Icon.ClipboardPaste") { WorksInDocument = true },
            new("Edit", "Paste text only", () => Paste(PasteMode.TextOnly), Shortcuts.PasteTextOnly, "Icon.ClipboardText", "plain unformatted") { WorksInDocument = true },
            new("Edit", "Paste keeping source formatting", () => Paste(PasteMode.KeepSource), Shortcuts.PasteKeepSource, "Icon.ClipboardPaste", "original formatting") { WorksInDocument = true },
            new("Edit", "Select all", () => SelectAll(), Shortcuts.SelectAll, null) { WorksInDocument = true },
            new("Edit", "Find…", () => OpenFind(false), Shortcuts.Find, "Icon.Search", "search look for") { WorksWhileTyping = true, WorksInDocument = true },
            new("Edit", "Replace…", () => OpenFind(true), Shortcuts.Replace, "Icon.ArrowSwap", "find change") { WorksWhileTyping = true, WorksInDocument = true },
            new("Edit", "Go to page…", () => GoToPagePrompt(), Shortcuts.GoToPage, "Icon.DocumentSearch", "jump") { WorksInDocument = true },
            new("Edit", "Copy formatting (format painter)", () => StartFormatPainter(), Shortcuts.CopyFormatting, "Icon.PaintBrush", "painter style") { WorksInDocument = true },
            new("Edit", "Paste formatting", () => PasteFormatting(), null, "Icon.PaintBrush", "painter style") { CanExecute = () => _copiedFormat != null },
            new("Edit", "Command palette", () => OpenPalette(), Shortcuts.CommandPalette, "Icon.Search") { WorksWhileTyping = true, WorksInDocument = true, InPalette = false },

            // Format: text
            new("Format", "Bold", () => Editor.ToggleBold(), Shortcuts.Bold, "Icon.TextBold") { WorksInDocument = true },
            new("Format", "Italic", () => Editor.ToggleItalic(), Shortcuts.Italic, "Icon.TextItalic") { WorksInDocument = true },
            new("Format", "Underline", () => Editor.ToggleUnderline(), Shortcuts.Underline, "Icon.TextUnderline") { WorksInDocument = true },
            new("Format", "Strikethrough", () => Editor.ToggleStrike(), Shortcuts.Strikethrough, "Icon.TextStrikethrough") { WorksInDocument = true },
            new("Format", "Superscript", () => Editor.ToggleSuperscript(), Shortcuts.Superscript, "Icon.TextSuperscript") { WorksInDocument = true },
            new("Format", "Subscript", () => Editor.ToggleSubscript(), Shortcuts.Subscript, "Icon.TextSubscript") { WorksInDocument = true },
            new("Format", "Small capitals", () => Editor.ToggleSmallCaps(), Shortcuts.SmallCaps, "Icon.TextCaseTitle") { WorksInDocument = true },
            new("Format", "Bigger text", () => Editor.GrowFont(true), Shortcuts.BiggerText, "Icon.TextGrow", "font size increase") { WorksInDocument = true },
            new("Format", "Smaller text", () => Editor.GrowFont(false), Shortcuts.SmallerText, "Icon.TextShrink", "font size decrease") { WorksInDocument = true },
            new("Format", "Bigger text", () => Editor.GrowFont(true), Shortcuts.BiggerTextShift, "Icon.TextGrow") { WorksInDocument = true, InPalette = false },
            new("Format", "Smaller text", () => Editor.GrowFont(false), Shortcuts.SmallerTextShift, "Icon.TextShrink") { WorksInDocument = true, InPalette = false },
            new("Format", "Clear character formatting", () => Editor.ClearCharacterFormatting(), Shortcuts.ClearCharacterFormatting, "Icon.TextClearFormatting") { WorksInDocument = true },
            new("Format", "Clear formatting", () => Editor.ClearFormatting(), null, "Icon.TextClearFormatting", "remove direct reset") { WorksInDocument = true },
            new("Format", "Highlight", () => Editor.SetHighlight("yellow"), Shortcuts.Highlight, "Icon.Highlight", "marker yellow") { WorksInDocument = true },
            new("Format", "Remove highlight", () => Editor.SetHighlight(null), null, "Icon.Highlight", "marker none"),
            new("Format", "Change case…", () => CycleCase(), Shortcuts.ChangeCase, "Icon.TextChangeCase", "upper lower title capitals") { WorksInDocument = true },
            new("Format", "Reveal formatting", () => ShowPanel(PanelMode.Reveal), Shortcuts.RevealFormatting, "Icon.TextEffects", "why does this look inspect style direct") { WorksInDocument = true },
            new("Format", "Check style consistency", () => ShowPanel(PanelMode.Reveal), null, "Icon.TextEffects", "stray direct formatting tidy"),
            new("Format", "Update style to match selection", () => Editor.UpdateStyleFromSelection(), null, "Icon.TextEditStyle", "redefine"),

            // Paragraph
            new("Paragraph", "Align left", () => Editor.SetAlignment("left"), Shortcuts.AlignLeft, "Icon.TextAlignLeft", "start") { WorksInDocument = true },
            new("Paragraph", "Centre", () => Editor.SetAlignment("center"), Shortcuts.Centre, "Icon.TextAlignCenter", "center middle") { WorksInDocument = true },
            new("Paragraph", "Align right", () => Editor.SetAlignment("right"), Shortcuts.AlignRight, "Icon.TextAlignRight", "end") { WorksInDocument = true },
            new("Paragraph", "Justify", () => Editor.SetAlignment("both"), Shortcuts.Justify, "Icon.TextAlignJustify", "both full") { WorksInDocument = true },
            new("Paragraph", "Right-to-left paragraph", () => Editor.SetDirection(true), Shortcuts.RightToLeft, "Icon.TextParagraphDirectionLeft", "urdu arabic hebrew rtl direction") { WorksInDocument = true },
            new("Paragraph", "Left-to-right paragraph", () => Editor.SetDirection(false), Shortcuts.LeftToRight, "Icon.TextParagraphDirectionRight", "ltr direction") { WorksInDocument = true },
            new("Paragraph", "Single line spacing", () => Editor.SetLineSpacing(240), Shortcuts.SingleSpacing, "Icon.TextLineSpacing") { WorksInDocument = true },
            new("Paragraph", "1.5 line spacing", () => Editor.SetLineSpacing(360), Shortcuts.OneAndHalfSpacing, "Icon.TextLineSpacing") { WorksInDocument = true },
            new("Paragraph", "Double line spacing", () => Editor.SetLineSpacing(480), Shortcuts.DoubleSpacing, "Icon.TextLineSpacing") { WorksInDocument = true },
            new("Paragraph", "Increase indent", () => Editor.Indent(true), Shortcuts.IncreaseIndent, "Icon.TextIndentIncreaseLtr") { WorksInDocument = true },
            new("Paragraph", "Decrease indent", () => Editor.Indent(false), Shortcuts.DecreaseIndent, "Icon.TextIndentDecreaseLtr") { WorksInDocument = true },
            new("Paragraph", "Bullets", () => Editor.ToggleList(true), Shortcuts.Bullets, "Icon.TextBulletListLtr", "list") { WorksInDocument = true },
            new("Paragraph", "Numbering", () => Editor.ToggleList(false), null, "Icon.TextNumberListLtr", "list numbered") { WorksInDocument = true },
            new("Paragraph", "Restart numbering at 1", () => Editor.RestartNumbering(1), null, "Icon.TextNumberListLtr", "list") { CanExecute = () => Editor.ListKindOf(Editor.Selection.Active.Para) == "number" },
            new("Paragraph", "Continue numbering from previous", () => Editor.ContinueNumbering(), null, "Icon.TextNumberListLtr", "list") { CanExecute = () => Editor.CanContinueNumbering() },
            new("Paragraph", "Start numbering at…", () => AskStartNumber(), null, "Icon.TextNumberListLtr", "list restart number") { CanExecute = () => Editor.ListKindOf(Editor.Selection.Active.Para) == "number" },
            new("Paragraph", "Keep with next", () => Editor.SetKeepWithNext(true), null, "Icon.DocumentPageBreak", "pagination together"),
            new("Paragraph", "Keep lines together", () => Editor.SetKeepLinesTogether(true), null, "Icon.DocumentPageBreak", "pagination"),
            new("Paragraph", "Page break before", () => Editor.SetPageBreakBefore(true), null, "Icon.DocumentPageBreak", "pagination new page"),
            new("Paragraph", "Body text", () => Editor.ApplyStyle(Session.Document.Styles.DefaultParagraphStyleId), Shortcuts.BodyText, "Icon.TextT", "normal style") { WorksInDocument = true },
            new("Paragraph", "Heading 1", () => Editor.ApplyStyle("Heading1"), Shortcuts.Heading1, "Icon.TextHeader1", "style title") { WorksInDocument = true },
            new("Paragraph", "Heading 2", () => Editor.ApplyStyle("Heading2"), Shortcuts.Heading2, "Icon.TextHeader2", "style") { WorksInDocument = true },
            new("Paragraph", "Heading 3", () => Editor.ApplyStyle("Heading3"), Shortcuts.Heading3, "Icon.TextHeader3", "style") { WorksInDocument = true },

            // Insert
            new("Insert", "Picture…", () => InsertPicture(), null, "Icon.ImageAdd", "image photo graphic"),
            new("Insert", "Table", () => InsertTableDefault(), null, "Icon.Table", "grid rows columns"),
            new("Insert", "Link…", () => InsertLink(), Shortcuts.Link, "Icon.Link", "hyperlink url web address bookmark") { WorksInDocument = true },
            new("Insert", "Comment", () => AddComment(), Shortcuts.Comment, "Icon.CommentAdd", "note review") { WorksInDocument = true },
            new("Insert", "Footnote", () => InsertNote(NoteKind.Footnote), Shortcuts.Footnote, "Icon.TextFootnote", "note reference") { WorksInDocument = true },
            new("Insert", "Endnote", () => InsertNote(NoteKind.Endnote), Shortcuts.Endnote, "Icon.TextFootnote", "note reference") { WorksInDocument = true },
            new("Insert", "Page break", () => Editor.InsertBreak(TextRun.PageBreak), Shortcuts.PageBreak, "Icon.DocumentPageBreak", "new page") { WorksInDocument = true },
            new("Insert", "Column break", () => Editor.InsertBreak(TextRun.ColumnBreak), Shortcuts.ColumnBreak, "Icon.TextColumnTwo", "columns") { WorksInDocument = true },
            new("Insert", "Section break (next page)", () => Editor.InsertSectionBreak("nextPage"), null, "Icon.DocumentPageBreak", "section new page"),
            new("Insert", "Section break (continuous)", () => Editor.InsertSectionBreak("continuous"), null, "Icon.DocumentPageBreak", "section columns"),
            new("Insert", "Table of contents", () => Editor.InsertTableOfContents(), null, "Icon.TableOfContents", "toc contents headings"),
            new("Insert", "Update table of contents", () => Editor.UpdateTablesOfContents(Layout), null, "Icon.ArrowSync", "toc refresh page numbers") { CanExecute = () => Editor.HasTableOfContents() },
            new("Insert", "Update all fields", () => Editor.UpdateFields(Layout), Shortcuts.UpdateFields, "Icon.ArrowSync", "refresh captions references page numbers") { WorksInDocument = true },
            new("Insert", "Caption…", () => InsertCaption(), null, "Icon.TextCaption", "figure table label numbering"),
            new("Insert", "Cross-reference…", () => InsertCrossReference(), null, "Icon.Link", "reference heading figure table page"),
            new("Insert", "Bookmark…", () => InsertBookmark(), Shortcuts.Bookmark, "Icon.Bookmark", "anchor name") { WorksInDocument = true },
            new("Insert", "Citation…", () => ShowCitations(true), null, "Icon.BookQuestionMark", "reference source apa mla chicago ieee cite"),
            new("Insert", "Bibliography", () => Editor.InsertBibliography(), null, "Icon.Library", "references works cited sources"),
            new("Insert", "Manage sources…", () => ShowCitations(false), null, "Icon.Library", "citations bibliography references"),
            new("Insert", "Page numbers", () => Editor.AddPageNumbers("footer", "center"), null, "Icon.DocumentPageNumber", "footer header numbering"),
            new("Insert", "Header", () => EditHeaderFooterOfCurrentPage(true), null, "Icon.DocumentHeader", "top page"),
            new("Insert", "Footer", () => EditHeaderFooterOfCurrentPage(false), null, "Icon.DocumentFooter", "bottom page"),
            new("Insert", "Date", () => Editor.InsertText(DateTime.Now.ToString("d MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture)), Shortcuts.Date, "Icon.Calendar", "today time") { WorksInDocument = true },
            new("Insert", "Divider", () => Editor.InsertDivider(), null, "Icon.LineHorizontal1", "rule horizontal line separator"),
            new("Insert", "Symbol…", () => InsertSymbol(), null, "Icon.Symbols", "special character emoji"),
            new("Insert", "Quote", () => Editor.ApplyStyle("Quote"), null, "Icon.TextQuote", "blockquote style"),
            new("Insert", "Code block", () => Editor.ApplyStyle("Code"), null, "Icon.Code", "monospace style"),

            // Table
            new("Table", "Insert row above", () => Editor.InsertRows(false), null, "Icon.TableInsertRow") { CanExecute = () => InTable },
            new("Table", "Insert row below", () => Editor.InsertRows(true), null, "Icon.TableInsertRow") { CanExecute = () => InTable },
            new("Table", "Insert column left", () => Editor.InsertColumn(false), null, "Icon.TableInsertColumn") { CanExecute = () => InTable },
            new("Table", "Insert column right", () => Editor.InsertColumn(true), null, "Icon.TableInsertColumn") { CanExecute = () => InTable },
            new("Table", "Delete row", () => Editor.DeleteRows(), null, "Icon.TableDeleteRow") { CanExecute = () => InTable },
            new("Table", "Delete column", () => Editor.DeleteColumn(), null, "Icon.TableDeleteColumn") { CanExecute = () => InTable },
            new("Table", "Delete table", () => Editor.DeleteTable(), null, "Icon.Delete") { CanExecute = () => InTable },
            new("Table", "Merge cells", () => Editor.MergeCells(), null, "Icon.TableCellsMerge") { CanExecute = () => InTable && HasSelection },
            new("Table", "Split cell", () => Editor.SplitCell(), null, "Icon.TableCellsSplit") { CanExecute = () => InTable },
            new("Table", "Repeat header row on each page", () => Editor.ToggleHeaderRow(), null, "Icon.TableFreezeRow", "heading row") { CanExecute = () => InTable },
            new("Table", "Don't split rows across pages", () => Editor.ToggleCantSplit(), null, "Icon.TableSettings", "keep rows") { CanExecute = () => InTable },
            new("Table", "Distribute columns evenly", () => Editor.DistributeColumns(), null, "Icon.TableResizeColumn", "autofit width equal") { CanExecute = () => InTable },
            new("Table", "Fit table to the page width", () => Editor.FitTableToWidth(), null, "Icon.TableResizeColumn", "autofit window") { CanExecute = () => InTable },
            new("Table", "Fit columns to their contents", () => Editor.AutoFitContents(Layout), null, "Icon.TableResizeColumn", "autofit") { CanExecute = () => InTable },

            // Page
            new("Page", "Make this page landscape", () => Editor.MakePageLandscape(true, Layout), null, "Icon.DocumentLandscape", "orientation rotate one page section") { CanExecute = () => Editor.Selection.Active.Para.Story is MainStory },
            new("Page", "Make this page portrait", () => Editor.MakePageLandscape(false, Layout), null, "Icon.DocumentOnePage", "orientation one page section") { CanExecute = () => Editor.Selection.Active.Para.Story is MainStory },
            new("Page", "Page setup", () => ShowPanel(PanelMode.Page), null, "Icon.DocumentMargins", "margins size orientation columns paper a4 letter"),
            new("Page", "Two columns", () => Editor.SetColumns(2), null, "Icon.TextColumnTwo", "layout newspaper"),
            new("Page", "One column", () => Editor.SetColumns(1), null, "Icon.TextColumnOne", "layout"),
            new("Page", "Different first page header", () => Editor.SetTitlePage(!Session.Document.SectionOf(Editor.Selection.Active.Para).TitlePage), null, "Icon.DocumentHeader", "title page"),
            new("Page", "Different odd and even pages", () => Editor.SetEvenOddHeaders(!Session.Document.Settings.EvenAndOddHeaders), null, "Icon.DocumentHeaderFooter", "header footer facing"),

            // Review
            new("Review", "Track changes", () => Editor.SetTrackChanges(!Editor.TrackChanges), Shortcuts.TrackChanges, "Icon.TrackChanges", "revisions markup review") { WorksInDocument = true },
            new("Review", "Accept change", () => Editor.AcceptOrRejectAtSelection(true), null, "Icon.Accept", "revision") { CanExecute = () => Editor.HasRevisions() },
            new("Review", "Reject change", () => Editor.AcceptOrRejectAtSelection(false), null, "Icon.Reject", "revision") { CanExecute = () => Editor.HasRevisions() },
            new("Review", "Accept all changes", () => Editor.AcceptOrRejectAll(true, null), null, "Icon.Accept", "revisions") { CanExecute = () => Editor.HasRevisions() },
            new("Review", "Reject all changes", () => Editor.AcceptOrRejectAll(false, null), null, "Icon.Reject", "revisions") { CanExecute = () => Editor.HasRevisions() },
            new("Review", "Next change", () => GoToChange(1), null, "Icon.ArrowDown", "revision") { CanExecute = () => Editor.HasRevisions() },
            new("Review", "Previous change", () => GoToChange(-1), null, "Icon.ArrowUp", "revision") { CanExecute = () => Editor.HasRevisions() },
            new("Review", "Show all markup", () => SetRevisionView(RevisionView.AllMarkup), null, "Icon.Eye", "review display"),
            new("Review", "Show simple markup", () => SetRevisionView(RevisionView.Simple), null, "Icon.Eye", "review display"),
            new("Review", "Show no markup", () => SetRevisionView(RevisionView.NoMarkup), null, "Icon.EyeOff", "review final"),
            new("Review", "Show original", () => SetRevisionView(RevisionView.Original), null, "Icon.EyeOff", "review before"),
            new("Review", "Comments and changes", () => ShowPanel(PanelMode.Review), null, "Icon.CommentMultiple", "review pane"),
            new("Review", "Word count", () => ShowWordCount(), Shortcuts.WordCount, "Icon.TextWordCount", "statistics characters reading time") { WorksInDocument = true },
            new("Review", "Set a word goal…", () => SetWordGoal(), null, "Icon.Target", "target count writing"),
            new("Review", "Check spelling", () => NextSpellingError(), Shortcuts.CheckSpelling, "Icon.TextProofingTools", "proofing errors") { WorksInDocument = true },

            // View
            new("View", "Page view", () => SetViewMode(ViewMode.Pages), null, "Icon.DocumentOnePage", "print layout"),
            new("View", "Pageless view", () => SetViewMode(ViewMode.Pageless), null, "Icon.DocumentText", "draft continuous web"),
            new("View", "Focus mode", () => SetFocusMode(!FocusMode), Shortcuts.FocusMode, "Icon.Focus", "distraction free zen") { WorksInDocument = true },
            new("View", "Typewriter scrolling", () => ToggleTypewriter(), null, "Icon.Typewriter", "centre line"),
            new("View", "Show formatting marks", () => ToggleMarks(), Shortcuts.FormattingMarks, "Icon.TextParagraph", "paragraph pilcrow spaces breaks hidden") { WorksInDocument = true },
            new("View", "Outline", () => ToggleOutline(), null, "Icon.PanelLeft", "navigation headings pane"),
            new("View", "Panel", () => SetPanelVisible(!PanelVisible), Shortcuts.Panel, "Icon.PanelRight", "properties") { WorksInDocument = true },
            new("View", "Zoom in", () => ZoomBy(1.1), Shortcuts.ZoomIn, "Icon.ZoomIn") { WorksInDocument = true },
            new("View", "Zoom out", () => ZoomBy(1 / 1.1), Shortcuts.ZoomOut, "Icon.ZoomOut") { WorksInDocument = true },
            new("View", "Fit page width", () => Doc.ZoomToPageWidth(), Shortcuts.FitPageWidth, "Icon.PageFit") { WorksInDocument = true },
            new("View", "Whole page", () => Doc.ZoomToWholePage(), null, "Icon.DocumentOnePage"),
            new("View", "Multiple pages", () => Doc.ShowMultiplePages(), null, "Icon.DocumentMultiple", "side by side"),
        };
        // Designs, styles and templates are commands too.
        foreach (var design in Core.Templates.DocumentDesigns.All)
        {
            var d = design;
            list.Add(new AppCommand("Design", "Design: " + d.Name, () => Editor.ApplyDesign(d), null, "Icon.Color", d.Description + " theme fonts look"));
        }
        foreach (var template in Core.Templates.TemplateCatalog.All)
        {
            var t = template;
            list.Add(new AppCommand("New", "New " + t.Name.ToLowerInvariant(), () => NewFromTemplate(t), null, "Icon.DocumentAdd", t.Description + " template"));
        }
        return list;
    }

    // ------------------------------------------------------------------ keyboard

    private void OnPreviewKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
        if (_paletteOpen) return;
        var mods = Keyboard.Modifiers;
        if (SlashMenuOpen)
        {
            if (mods == ModifierKeys.None && HandleSlashKey(key)) e.Handled = true;
            return;
        }
        if (key == Key.Escape && (_autoCorrectChip != null || _pasteChip != null))
        {
            HideAutoCorrectChip();
            HidePasteChip();
            e.Handled = true;
            return;
        }
        if (key == Key.Escape && _focusMode)
        {
            SetFocusMode(false);
            e.Handled = true;
            return;
        }
        if (key == Key.Escape && _formatPainter)
        {
            _formatPainter = false;
            Doc.Cursor = Cursors.IBeam;
            e.Handled = true;
            return;
        }
        var focus = Keyboard.FocusedElement;
        var inTextBox = focus is TextBox && !ReferenceEquals(focus, Doc);
        var inDocument = ReferenceEquals(focus, Doc) || (focus is DependencyObject d && IsDescendantOf(d, Doc));
        foreach (var command in Commands)
        {
            if (command.Gesture == null || !command.Gesture.Matches(key, mods)) continue;
            if (inTextBox && !command.WorksWhileTyping) continue;
            if (!inTextBox && !inDocument && !command.WorksWhileTyping && command.WorksInDocument && focus is Control) continue;
            bool can;
            try { can = command.CanExecute(); } catch { can = false; }
            if (!can) continue;
            e.Handled = true;
            Run(command.Execute);
            return;
        }
    }

    private static bool IsDescendantOf(DependencyObject d, DependencyObject ancestor)
    {
        var current = d;
        while (current != null)
        {
            if (ReferenceEquals(current, ancestor)) return true;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    // ------------------------------------------------------------------ palette

    public void OpenPalette()
    {
        if (_paletteOpen) return;
        _paletteOpen = true;
        var palette = new CommandPalette(this);
        palette.Closed += () =>
        {
            OverlayLayer.Children.Remove(palette);
            _paletteOpen = false;
            FocusDocument();
        };
        OverlayLayer.Children.Add(palette);
        palette.Open();
    }

    public bool PaletteOpen => _paletteOpen;

    private void SetAppTheme(AppTheme theme)
    {
        AppSettings.Update(s => s.Theme = theme);
        ThemeManager.SetMode(theme);
    }

    public void SetRevisionView(RevisionView view)
    {
        Layout.View.Revisions = view;
        Doc.RelayoutAll();
        RefreshState();
    }

    private void ToggleMarks()
    {
        var on = !Doc.ShowFormattingMarks;
        Doc.ShowFormattingMarks = on;
        Layout.View.ShowFormattingMarks = on;
        AppSettings.Update(s => s.ShowFormattingMarks = on);
        Doc.RelayoutAll();
    }

    private void ToggleTypewriter()
    {
        var on = !AppSettings.Current.TypewriterScrolling;
        AppSettings.Update(s => s.TypewriterScrolling = on);
        Doc.TypewriterScrolling = on || _focusMode;
        _window.ShowMessage(on ? "Typewriter scrolling on: the line you type on stays in the middle." : "Typewriter scrolling off.", 3);
    }

    private void ToggleOutline()
    {
        var visible = !OutlineVisible;
        SetOutlineVisible(visible);
        AppSettings.Update(s => s.ShowOutline = visible);
    }

    private void SelectAll()
    {
        var story = Editor.Story;
        Editor.Selection = new Selection(Nav.StartOf(story), Nav.EndOf(story));
    }

    private void CycleCase()
    {
        Run(() => Editor.CycleCase());
    }

    private void NewFromTemplate(Core.Templates.DocumentTemplate template)
    {
        var paper = AppSettings.Current.DefaultPaper;
        var letter = paper == "letter" || (paper == "auto" && Core.Templates.TemplateCatalog.RegionUsesLetter());
        var doc = template.Build(letter);
        doc.Properties.Creator = AppSettings.Current.Author;
        App.Current.OpenSession(new DocumentSession(doc), null);
    }
}
