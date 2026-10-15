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
        const ModifierKeys C = ModifierKeys.Control, S = ModifierKeys.Shift, A = ModifierKeys.Alt;
        var list = new List<AppCommand>
        {
            // File
            new("File", "Save", () => Save(), new KeyChord(Key.S, C), "Icon.Save") { WorksWhileTyping = true, WorksInDocument = true },
            new("File", "Save as…", () => SaveAs(), new KeyChord(Key.S, C | S), "Icon.SaveCopy") { WorksWhileTyping = true, WorksInDocument = true },
            new("File", "Save as…", () => SaveAs(), new KeyChord(Key.F12), "Icon.SaveCopy") { WorksWhileTyping = true, WorksInDocument = true, InPalette = false },
            new("File", "Open…", () => OpenDocument(), new KeyChord(Key.O, C), "Icon.FolderOpen") { WorksWhileTyping = true, WorksInDocument = true },
            new("File", "New document", () => App.Current.ShowStart(null), new KeyChord(Key.N, C), "Icon.DocumentAdd", "blank start template window") { WorksWhileTyping = true, WorksInDocument = true },
            new("File", "Export…", () => ShowExport(null), new KeyChord(Key.E, C | S), "Icon.ArrowExportLtr", "pdf markdown html text share") { WorksWhileTyping = true, WorksInDocument = true },
            new("File", "Export as PDF…", () => ShowExport("pdf"), null, "Icon.DocumentPdf", "portable"),
            new("File", "Export as Markdown…", () => ShowExport("md"), null, "Icon.Code", "md"),
            new("File", "Export as HTML…", () => ShowExport("html"), null, "Icon.Globe", "web page"),
            new("File", "Export as plain text…", () => ShowExport("txt"), null, "Icon.DocumentText", "txt"),
            new("File", "Print…", () => Print(), new KeyChord(Key.P, C), "Icon.Print", "printer paper") { WorksWhileTyping = true, WorksInDocument = true },
            new("File", "Version history…", () => ShowVersionHistory(), null, "Icon.History", "versions restore earlier backup snapshot") { CanExecute = () => Session.FilePath != null },
            new("File", "Document properties…", () => ShowProperties(), null, "Icon.Info", "title author subject keywords"),
            new("File", "Start screen", () => GoToStart(), null, "Icon.Home", "home recent close"),
            new("File", "Close", () => _window.Close(), new KeyChord(Key.W, C), "Icon.Dismiss") { WorksWhileTyping = true, WorksInDocument = true },
            new("App", "Settings", () => SettingsWindow.ShowSingle(_window), new KeyChord(Key.OemComma, C), "Icon.Settings", "preferences options autosave updates autocorrect") { WorksWhileTyping = true, WorksInDocument = true },
            new("App", "About Inkwell", () => AboutDialog.Open(_window), null, "Icon.Info", "version licences"),
            new("App", "Use light mode", () => SetAppTheme(AppTheme.Light), null, "Icon.DarkTheme", "appearance theme"),
            new("App", "Use dark mode", () => SetAppTheme(AppTheme.Dark), null, "Icon.DarkTheme", "appearance theme night"),
            new("App", "Use my Windows setting for light / dark", () => SetAppTheme(AppTheme.System), null, "Icon.DarkTheme", "appearance theme"),
            new("App", "Dark pages in dark mode", () => AppSettings.Update(s => s.DarkPaper = !s.DarkPaper), null, "Icon.WeatherMoon", "paper night reading"),

            // Edit
            new("Edit", "Undo", () => Undo(), new KeyChord(Key.Z, C), "Icon.ArrowUndo") { CanExecute = () => Editor.History.CanUndo, WorksInDocument = true },
            new("Edit", "Redo", () => Redo(), new KeyChord(Key.Y, C), "Icon.ArrowRedo") { CanExecute = () => Editor.History.CanRedo, WorksInDocument = true },
            new("Edit", "Redo", () => Redo(), new KeyChord(Key.Z, C | S), "Icon.ArrowRedo") { CanExecute = () => Editor.History.CanRedo, InPalette = false, WorksInDocument = true },
            new("Edit", "Cut", () => Cut(), new KeyChord(Key.X, C), "Icon.Cut") { WorksInDocument = true },
            new("Edit", "Copy", () => Copy(), new KeyChord(Key.C, C), "Icon.Copy") { WorksInDocument = true },
            new("Edit", "Paste", () => Paste(PasteMode.Default), new KeyChord(Key.V, C), "Icon.ClipboardPaste") { WorksInDocument = true },
            new("Edit", "Paste text only", () => Paste(PasteMode.TextOnly), new KeyChord(Key.V, C | S), "Icon.ClipboardText", "plain unformatted") { WorksInDocument = true },
            new("Edit", "Paste keeping source formatting", () => Paste(PasteMode.KeepSource), new KeyChord(Key.V, C | A), "Icon.ClipboardPaste", "original formatting") { WorksInDocument = true },
            new("Edit", "Select all", () => SelectAll(), new KeyChord(Key.A, C), null) { WorksInDocument = true },
            new("Edit", "Find…", () => OpenFind(false), new KeyChord(Key.F, C), "Icon.Search", "search look for") { WorksWhileTyping = true, WorksInDocument = true },
            new("Edit", "Replace…", () => OpenFind(true), new KeyChord(Key.H, C), "Icon.ArrowSwap", "find change") { WorksWhileTyping = true, WorksInDocument = true },
            new("Edit", "Go to page…", () => GoToPagePrompt(), new KeyChord(Key.G, C), "Icon.DocumentSearch", "jump") { WorksInDocument = true },
            new("Edit", "Copy formatting (format painter)", () => StartFormatPainter(), new KeyChord(Key.C, C | S), "Icon.PaintBrush", "painter style") { WorksInDocument = true },
            new("Edit", "Paste formatting", () => PasteFormatting(), null, "Icon.PaintBrush", "painter style") { CanExecute = () => _copiedFormat != null },
            new("Edit", "Command palette", () => OpenPalette(), new KeyChord(Key.K, C), "Icon.Search") { WorksWhileTyping = true, WorksInDocument = true, InPalette = false },

            // Format: text
            new("Format", "Bold", () => Editor.ToggleBold(), new KeyChord(Key.B, C), "Icon.TextBold") { WorksInDocument = true },
            new("Format", "Italic", () => Editor.ToggleItalic(), new KeyChord(Key.I, C), "Icon.TextItalic") { WorksInDocument = true },
            new("Format", "Underline", () => Editor.ToggleUnderline(), new KeyChord(Key.U, C), "Icon.TextUnderline") { WorksInDocument = true },
            new("Format", "Strikethrough", () => Editor.ToggleStrike(), new KeyChord(Key.D5, C | S), "Icon.TextStrikethrough") { WorksInDocument = true },
            new("Format", "Superscript", () => Editor.ToggleSuperscript(), new KeyChord(Key.OemPlus, C | S), "Icon.TextSuperscript") { WorksInDocument = true },
            new("Format", "Subscript", () => Editor.ToggleSubscript(), new KeyChord(Key.OemPlus, C), "Icon.TextSubscript") { WorksInDocument = true },
            new("Format", "Small capitals", () => Editor.ToggleSmallCaps(), new KeyChord(Key.K, C | S | A), "Icon.TextCaseTitle") { WorksInDocument = true },
            new("Format", "Bigger text", () => Editor.GrowFont(true), new KeyChord(Key.OemCloseBrackets, C), "Icon.TextGrow", "font size increase") { WorksInDocument = true },
            new("Format", "Smaller text", () => Editor.GrowFont(false), new KeyChord(Key.OemOpenBrackets, C), "Icon.TextShrink", "font size decrease") { WorksInDocument = true },
            new("Format", "Bigger text", () => Editor.GrowFont(true), new KeyChord(Key.OemPeriod, C | S), "Icon.TextGrow") { WorksInDocument = true, InPalette = false },
            new("Format", "Smaller text", () => Editor.GrowFont(false), new KeyChord(Key.OemComma, C | S), "Icon.TextShrink") { WorksInDocument = true, InPalette = false },
            new("Format", "Clear character formatting", () => Editor.ClearCharacterFormatting(), new KeyChord(Key.Space, C), "Icon.TextClearFormatting") { WorksInDocument = true },
            new("Format", "Clear formatting", () => Editor.ClearFormatting(), null, "Icon.TextClearFormatting", "remove direct reset") { WorksInDocument = true },
            new("Format", "Highlight", () => Editor.SetHighlight("yellow"), new KeyChord(Key.H, C | A), "Icon.Highlight", "marker yellow") { WorksInDocument = true },
            new("Format", "Remove highlight", () => Editor.SetHighlight(null), null, "Icon.Highlight", "marker none"),
            new("Format", "Change case…", () => CycleCase(), new KeyChord(Key.F3, S), "Icon.TextChangeCase", "upper lower title capitals") { WorksInDocument = true },
            new("Format", "Reveal formatting", () => ShowPanel(PanelMode.Reveal), new KeyChord(Key.F1, S), "Icon.TextEffects", "why does this look inspect style direct") { WorksInDocument = true },
            new("Format", "Check style consistency", () => ShowPanel(PanelMode.Reveal), null, "Icon.TextEffects", "stray direct formatting tidy"),
            new("Format", "Update style to match selection", () => Editor.UpdateStyleFromSelection(), null, "Icon.TextEditStyle", "redefine"),

            // Paragraph
            new("Paragraph", "Align left", () => Editor.SetAlignment("left"), new KeyChord(Key.L, C), "Icon.TextAlignLeft", "start") { WorksInDocument = true },
            new("Paragraph", "Centre", () => Editor.SetAlignment("center"), new KeyChord(Key.E, C), "Icon.TextAlignCenter", "center middle") { WorksInDocument = true },
            new("Paragraph", "Align right", () => Editor.SetAlignment("right"), new KeyChord(Key.R, C), "Icon.TextAlignRight", "end") { WorksInDocument = true },
            new("Paragraph", "Justify", () => Editor.SetAlignment("both"), new KeyChord(Key.J, C), "Icon.TextAlignJustify", "both full") { WorksInDocument = true },
            new("Paragraph", "Right-to-left paragraph", () => Editor.SetDirection(true), new KeyChord(Key.OemPeriod, C | A), "Icon.TextParagraphDirectionLeft", "urdu arabic hebrew rtl direction") { WorksInDocument = true },
            new("Paragraph", "Left-to-right paragraph", () => Editor.SetDirection(false), new KeyChord(Key.OemComma, C | A), "Icon.TextParagraphDirectionRight", "ltr direction") { WorksInDocument = true },
            new("Paragraph", "Single line spacing", () => Editor.SetLineSpacing(240), new KeyChord(Key.D1, C), "Icon.TextLineSpacing") { WorksInDocument = true },
            new("Paragraph", "1.5 line spacing", () => Editor.SetLineSpacing(360), new KeyChord(Key.D5, C), "Icon.TextLineSpacing") { WorksInDocument = true },
            new("Paragraph", "Double line spacing", () => Editor.SetLineSpacing(480), new KeyChord(Key.D2, C), "Icon.TextLineSpacing") { WorksInDocument = true },
            new("Paragraph", "Increase indent", () => Editor.Indent(true), new KeyChord(Key.M, C), "Icon.TextIndentIncreaseLtr") { WorksInDocument = true },
            new("Paragraph", "Decrease indent", () => Editor.Indent(false), new KeyChord(Key.M, C | S), "Icon.TextIndentDecreaseLtr") { WorksInDocument = true },
            new("Paragraph", "Bullets", () => Editor.ToggleList(true), new KeyChord(Key.L, C | S), "Icon.TextBulletListLtr", "list") { WorksInDocument = true },
            new("Paragraph", "Numbering", () => Editor.ToggleList(false), null, "Icon.TextNumberListLtr", "list numbered") { WorksInDocument = true },
            new("Paragraph", "Restart numbering at 1", () => Editor.RestartNumbering(1), null, "Icon.TextNumberListLtr", "list") { CanExecute = () => Editor.ListKindOf(Editor.Selection.Active.Para) == "number" },
            new("Paragraph", "Continue numbering from previous", () => Editor.ContinueNumbering(), null, "Icon.TextNumberListLtr", "list") { CanExecute = () => Editor.CanContinueNumbering() },
            new("Paragraph", "Keep with next", () => Editor.SetKeepWithNext(true), null, "Icon.DocumentPageBreak", "pagination together"),
            new("Paragraph", "Keep lines together", () => Editor.SetKeepLinesTogether(true), null, "Icon.DocumentPageBreak", "pagination"),
            new("Paragraph", "Page break before", () => Editor.SetPageBreakBefore(true), null, "Icon.DocumentPageBreak", "pagination new page"),
            new("Paragraph", "Body text", () => Editor.ApplyStyle(Session.Document.Styles.DefaultParagraphStyleId), new KeyChord(Key.N, C | S), "Icon.TextT", "normal style") { WorksInDocument = true },
            new("Paragraph", "Heading 1", () => Editor.ApplyStyle("Heading1"), new KeyChord(Key.D1, C | A), "Icon.TextHeader1", "style title") { WorksInDocument = true },
            new("Paragraph", "Heading 2", () => Editor.ApplyStyle("Heading2"), new KeyChord(Key.D2, C | A), "Icon.TextHeader2", "style") { WorksInDocument = true },
            new("Paragraph", "Heading 3", () => Editor.ApplyStyle("Heading3"), new KeyChord(Key.D3, C | A), "Icon.TextHeader3", "style") { WorksInDocument = true },

            // Insert
            new("Insert", "Picture…", () => InsertPicture(), null, "Icon.ImageAdd", "image photo graphic"),
            new("Insert", "Table", () => InsertTableDefault(), null, "Icon.Table", "grid rows columns"),
            new("Insert", "Link…", () => InsertLink(), new KeyChord(Key.K, C | S), "Icon.Link", "hyperlink url web address bookmark") { WorksInDocument = true },
            new("Insert", "Comment", () => AddComment(), new KeyChord(Key.M, C | A), "Icon.CommentAdd", "note review") { WorksInDocument = true },
            new("Insert", "Footnote", () => InsertNote(NoteKind.Footnote), new KeyChord(Key.F, C | A), "Icon.TextFootnote", "note reference") { WorksInDocument = true },
            new("Insert", "Endnote", () => InsertNote(NoteKind.Endnote), new KeyChord(Key.D, C | A), "Icon.TextFootnote", "note reference") { WorksInDocument = true },
            new("Insert", "Page break", () => Editor.InsertBreak(TextRun.PageBreak), new KeyChord(Key.Enter, C), "Icon.DocumentPageBreak", "new page") { WorksInDocument = true },
            new("Insert", "Column break", () => Editor.InsertBreak(TextRun.ColumnBreak), new KeyChord(Key.Enter, C | S), "Icon.TextColumnTwo", "columns") { WorksInDocument = true },
            new("Insert", "Section break (next page)", () => Editor.InsertSectionBreak("nextPage"), null, "Icon.DocumentPageBreak", "section new page"),
            new("Insert", "Section break (continuous)", () => Editor.InsertSectionBreak("continuous"), null, "Icon.DocumentPageBreak", "section columns"),
            new("Insert", "Table of contents", () => Editor.InsertTableOfContents(), null, "Icon.TableOfContents", "toc contents headings"),
            new("Insert", "Update table of contents", () => Editor.UpdateTablesOfContents(Layout), null, "Icon.ArrowSync", "toc refresh page numbers") { CanExecute = () => Editor.HasTableOfContents() },
            new("Insert", "Update all fields", () => Editor.UpdateFields(Layout), new KeyChord(Key.F9), "Icon.ArrowSync", "refresh captions references page numbers") { WorksInDocument = true },
            new("Insert", "Caption…", () => InsertCaption(), null, "Icon.TextCaption", "figure table label numbering"),
            new("Insert", "Cross-reference…", () => InsertCrossReference(), null, "Icon.Link", "reference heading figure table page"),
            new("Insert", "Bookmark…", () => InsertBookmark(), new KeyChord(Key.F5, C | S), "Icon.Bookmark", "anchor name") { WorksInDocument = true },
            new("Insert", "Citation…", () => ShowCitations(true), null, "Icon.BookQuestionMark", "reference source apa mla chicago ieee cite"),
            new("Insert", "Bibliography", () => Editor.InsertBibliography(), null, "Icon.Library", "references works cited sources"),
            new("Insert", "Manage sources…", () => ShowCitations(false), null, "Icon.Library", "citations bibliography references"),
            new("Insert", "Page numbers", () => Editor.AddPageNumbers("footer", "center"), null, "Icon.DocumentPageNumber", "footer header numbering"),
            new("Insert", "Header", () => EditHeaderFooterOfCurrentPage(true), null, "Icon.DocumentHeader", "top page"),
            new("Insert", "Footer", () => EditHeaderFooterOfCurrentPage(false), null, "Icon.DocumentFooter", "bottom page"),
            new("Insert", "Date", () => Editor.InsertText(DateTime.Now.ToString("d MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture)), new KeyChord(Key.D, A | S), "Icon.Calendar", "today time") { WorksInDocument = true },
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
            new("Review", "Track changes", () => Editor.SetTrackChanges(!Editor.TrackChanges), new KeyChord(Key.E, C | S), "Icon.TrackChanges", "revisions markup review") { WorksInDocument = true },
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
            new("Review", "Word count", () => ShowWordCount(), new KeyChord(Key.G, C | S), "Icon.TextWordCount", "statistics characters reading time") { WorksInDocument = true },
            new("Review", "Set a word goal…", () => SetWordGoal(), null, "Icon.Target", "target count writing"),
            new("Review", "Check spelling", () => NextSpellingError(), new KeyChord(Key.F7), "Icon.TextProofingTools", "proofing errors") { WorksInDocument = true },

            // View
            new("View", "Page view", () => SetViewMode(ViewMode.Pages), null, "Icon.DocumentOnePage", "print layout"),
            new("View", "Pageless view", () => SetViewMode(ViewMode.Pageless), null, "Icon.DocumentText", "draft continuous web"),
            new("View", "Focus mode", () => SetFocusMode(!FocusMode), new KeyChord(Key.F11), "Icon.Focus", "distraction free zen") { WorksInDocument = true },
            new("View", "Typewriter scrolling", () => ToggleTypewriter(), null, "Icon.Typewriter", "centre line"),
            new("View", "Show formatting marks", () => ToggleMarks(), new KeyChord(Key.D8, C | S), "Icon.TextParagraph", "paragraph pilcrow spaces breaks hidden") { WorksInDocument = true },
            new("View", "Outline", () => ToggleOutline(), null, "Icon.PanelLeft", "navigation headings pane"),
            new("View", "Panel", () => SetPanelVisible(!PanelVisible), new KeyChord(Key.F1, C), "Icon.PanelRight", "properties") { WorksInDocument = true },
            new("View", "Zoom in", () => ZoomBy(1.1), new KeyChord(Key.OemPlus, C | A), "Icon.ZoomIn") { WorksInDocument = true },
            new("View", "Zoom out", () => ZoomBy(1 / 1.1), new KeyChord(Key.OemMinus, C | A), "Icon.ZoomOut") { WorksInDocument = true },
            new("View", "Fit page width", () => Doc.ZoomToPageWidth(), new KeyChord(Key.D0, C), "Icon.PageFit") { WorksInDocument = true },
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
