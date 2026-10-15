using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Inkwell.Core.Editing;
using Inkwell.Core.IO;
using Inkwell.Core.Layout;
using Inkwell.Core.Model;
using Inkwell.Core.Samples;
using Inkwell.Interop;
using Inkwell.Themes;
using Inkwell.Views;
using Inkwell.Views.Panels;

namespace Inkwell.Services;

/// <summary>
/// Development aid (hidden --capture &lt;folder&gt;): opens Inkwell's own windows off screen with the demo documents
/// and saves pictures of them. Only Inkwell windows are captured (PrintWindow on our own handles, or rendering our
/// own visuals), never the screen; nothing takes the focus or goes full screen; it waits while another app is in
/// full-screen mode. Settings, recent files and version history stay out of the user's folders.
/// </summary>
internal static partial class CaptureRunner
{
    private static string s_folder = "";
    private static string s_scratch = "";
    private static readonly List<string> s_written = new();

    static partial void RunCore(string folder) => _ = RunAsync(folder);

    private static async Task RunAsync(string folder)
    {
        s_folder = folder;
        var exitCode = 0;
        s_scratch = Path.Combine(Path.GetTempPath(), "Inkwell-capture-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            Directory.CreateDirectory(folder);
            Directory.CreateDirectory(s_scratch);
            EditorView.UseHistoryRoot(Path.Combine(s_scratch, "History"));
            await WaitWhileBusy();
            await Script(Environment.GetEnvironmentVariable("INKWELL_CAPTURE_ONLY"));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Capture");
            File.WriteAllText(Path.Combine(folder, "capture-error.txt"), ex.ToString());
            exitCode = 1;
        }
        File.WriteAllLines(Path.Combine(folder, "captured.txt"), s_written);
        foreach (var w in Application.Current.Windows.OfType<Window>().ToList())
        {
            try
            {
                if (w is MainWindow { Editor: { } editor }) editor.Session.MarkSaved(editor.Editor.History.Version);
                if (w is MainWindow { Editor: { } e2 }) e2.Detach();
                w.Close();
            }
            catch
            {
                // closing anyway
            }
        }
        try
        {
            Directory.Delete(s_scratch, true);
        }
        catch
        {
            // a temp folder
        }
        Application.Current.Shutdown(exitCode);
    }

    private static async Task WaitWhileBusy()
    {
        var waited = 0;
        while (Native.IsUserBusy() && waited < 900)
        {
            await Task.Delay(2000);
            waited += 2;
        }
    }

    private static async Task Settle(int ms = 450)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(ms);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static bool Wanted(string? only, string name) => string.IsNullOrEmpty(only) || only!.Split(',').Any(o => name.StartsWith(o.Trim(), StringComparison.OrdinalIgnoreCase));

    private static MainWindow NewWindow()
    {
        // Off screen (left of every display): nothing shows up over the owner's work, and clicks can't reach it.
        var window = new MainWindow
        {
            Width = 1440,
            Height = 900,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = OffscreenLeft,
            Top = SystemParameters.VirtualScreenTop + 20,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        window.WindowState = WindowState.Normal;
        window.Show();
        return window;
    }

    private static double OffscreenLeft => SystemParameters.VirtualScreenLeft - 3200;

    private static void MoveOffscreen(Window w)
    {
        w.Left = OffscreenLeft;
        w.Top = SystemParameters.VirtualScreenTop + 20;
    }

    private static EditorView Open(MainWindow window, Document doc, string? path = null)
    {
        window.ShowEditor(new DocumentSession(doc, path));
        return window.Editor!;
    }

    private static async Task Script(string? only)
    {
        foreach (var mode in new[] { AppTheme.Light, AppTheme.Dark })
        {
            ThemeManager.SetMode(mode);
            var s = mode == AppTheme.Light ? "light" : "dark";
            var window = NewWindow();
            window.ShowStartView();
            await Settle(900);
            if (Wanted(only, "start")) await Capture(window, $"start-{s}");

            foreach (var demo in DemoDocuments.All)
            {
                if (!Wanted(only, "editor")) break;
                Open(window, demo.Create());
                await Settle(900);
                await Capture(window, $"editor-{demo.Id}-{s}");
            }

            if (Wanted(only, "palette"))
            {
                var view = Open(window, DemoDocuments.Report());
                await Settle(600);
                view.OpenPalette();
                await Settle(300);
                var box = FindChild<System.Windows.Controls.TextBox>(window, t => System.Windows.Automation.AutomationProperties.GetName(t) == "Search commands");
                if (box != null) box.Text = "insert";
                await Settle(400);
                await Capture(window, $"palette-{s}");
                view.ClosePaletteForTest();
            }

            if (Wanted(only, "slash"))
            {
                var view = Open(window, Core.Templates.TemplateCatalog.Find("blank")!.Build(false));
                await Settle(500);
                view.TypeForTest("# Quarterly plan ");
                view.TypeForTest("\nWhat we'll do next, in three steps:\n");
                view.TypeForTest("/ta");
                await Settle(400);
                await Capture(window, $"slash-{s}");
                view.CloseSlashMenuForTest();
            }

            if (Wanted(only, "autocorrect"))
            {
                var view = Open(window, Core.Templates.TemplateCatalog.Find("blank")!.Build(false));
                await Settle(500);
                view.TypeForTest("## Notes from the visit\n");
                view.TypeForTest("i think the \"new\" roof works -- the output rose 24% (c) Harbour Light. Several things, e.g. the battery, helped. ");
                await Settle(400);
                await Capture(window, $"autocorrect-{s}");
            }

            if (Wanted(only, "focus"))
            {
                var view = Open(window, DemoDocuments.Essay());
                await Settle(600);
                GoToText(view, "Since the 1990s");
                view.SetFocusMode(true);
                await Settle(700);
                await Capture(window, $"focus-{s}");
                view.SetFocusMode(false);
            }

            if (Wanted(only, "pageless"))
            {
                var view = Open(window, DemoDocuments.Report());
                await Settle(500);
                view.SetViewMode(ViewMode.Pageless);
                GoToText(view, "Harbour Light finished");
                await Settle(600);
                await Capture(window, $"pageless-{s}");
            }

            if (Wanted(only, "picture"))
            {
                var view = Open(window, DemoDocuments.Report());
                await Settle(600);
                var drawing = view.Session.Document.Body.Paragraphs.SelectMany(p => p.Inlines).OfType<DrawingObject>().First(d => d.IsFloating);
                GoToText(view, "The battery changed");
                await Settle(300);
                view.DocumentView.SelectDrawing(drawing);
                view.ShowPanel(PanelMode.Picture);
                await Settle(600);
                await Capture(window, $"picture-wrap-{s}");
                // The alignment guides shown while dragging: the margin edge and the page centre.
                if (view.DocumentView.FindDrawing(drawing) is { } placed)
                {
                    var page = placed.Page;
                    var right = page.Size.Width - page.Margins.Right;
                    view.DocumentView.Guides.Add((page, new Point(right, 40), new Point(right, page.Size.Height - 40)));
                    view.DocumentView.Guides.Add((page, new Point(40, placed.Box.Top), new Point(page.Size.Width - 40, placed.Box.Top)));
                    view.DocumentView.Redraw();
                    await Settle(400);
                    await Capture(window, $"picture-guides-{s}");
                    view.DocumentView.Guides.Clear();
                }
                view.DocumentView.SelectDrawing(null);
            }

            if (Wanted(only, "lists"))
            {
                var b = new DocBuilder(Core.Templates.DocumentDesigns.Default);
                b.Heading(1, "Steps to install");
                b.Numbered(true, "Download the installer", "Run it and choose a folder", "Open Inkwell from the Start menu");
                b.P("After the first list, a paragraph of ordinary text explains what comes next.");
                b.Numbered(false, "Pick a template", "Start writing", "Save: Inkwell keeps saving after that");
                var view = Open(window, b.Doc);
                await Settle(500);
                GoToText(view, "Pick a template");
                view.ShowPanel(PanelMode.Format);
                await Settle(600);
                await Capture(window, $"lists-{s}");
            }

            if (Wanted(only, "paste"))
            {
                var b = new DocBuilder(Core.Templates.DocumentDesigns.Default);
                b.Heading(1, "Research notes");
                b.P("Pasted text takes this document's fonts and styles, but keeps bold, links, lists and headings:");
                var target = b.P("");
                var view = Open(window, b.Doc);
                await Settle(400);
                view.Editor.Selection = new Selection(new DocPos(target, 0));
                var html = Core.Formats.CfHtml.Build("<h2 style=\"font-family:'Comic Sans MS';color:#c00000\">From a web page</h2><p style=\"font-family:'Courier New';font-size:9pt;color:#7030a0\">Copied text with <b>bold words</b>, <i>italics</i> and a <a href=\"https://example.com\">link</a>, in a different font and colour.</p><ul><li>First point</li><li>Second point</li></ul>");
                var data = new DataObject();
                data.SetData(DataFormats.Html, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(html)));
                data.SetData(DataFormats.UnicodeText, "From a web page");
                view.PasteForTest(data, PasteMode.Default);
                await Settle(500);
                await Capture(window, $"paste-{s}");
            }

            if (Wanted(only, "reveal"))
            {
                var view = Open(window, DemoDocuments.Report());
                await Settle(500);
                GoToText(view, "1,284 members");
                view.ShowPanel(PanelMode.Reveal);
                await Settle(700);
                await Capture(window, $"reveal-{s}");
            }

            if (Wanted(only, "breaks"))
            {
                var view = Open(window, DemoDocuments.Report());
                await Settle(500);
                view.DocumentView.ShowFormattingMarks = true;
                view.Layout.View.ShowFormattingMarks = true;
                view.DocumentView.RelayoutAll();
                view.DocumentView.ZoomToWholePage();
                view.GoToPage(0);
                await Settle(700);
                await Capture(window, $"breaks-{s}");
                view.DocumentView.ShowFormattingMarks = false;
                view.Layout.View.ShowFormattingMarks = false;
            }

            if (Wanted(only, "table"))
            {
                var view = Open(window, DemoDocuments.Report());
                await Settle(500);
                GoToText(view, "Used locally");
                view.ShowPanel(PanelMode.Auto);
                await Settle(700);
                await Capture(window, $"table-{s}");
            }

            if (Wanted(only, "review"))
            {
                var view = Open(window, DemoDocuments.Essay());
                await Settle(500);
                view.SetRevisionView(RevisionView.AllMarkup);
                view.ShowPanel(PanelMode.Review);
                GoToText(view, "This essay argues");
                await Settle(800);
                await Capture(window, $"review-{s}");
            }

            if (Wanted(only, "toc"))
            {
                var view = Open(window, DemoDocuments.Report());
                await Settle(500);
                view.DocumentView.ShowMultiplePages(2);
                await Settle(800);
                await Capture(window, $"toc-footnotes-{s}");
                view.DocumentView.MultiplePages = false;
            }

            if (Wanted(only, "rtl"))
            {
                var view = Open(window, DemoDocuments.Bilingual());
                await Settle(500);
                GoToText(view, "اردو پاکستان");
                await Settle(500);
                await Capture(window, $"urdu-{s}");
            }

            if (Wanted(only, "history"))
            {
                var path = Path.Combine(s_scratch, "Annual report.docx");
                var doc = DemoDocuments.Report();
                var now = DateTime.UtcNow;
                var versions = new (SnapshotKind Kind, int Words, string? Label, DateTime When)[]
                {
                    (SnapshotKind.ManualSave, 1012, "First draft", now.AddDays(-2)),
                    (SnapshotKind.Automatic, 1180, null, now.AddDays(-1).AddHours(-3)),
                    (SnapshotKind.ManualSave, 1236, "Sent to the board", now.AddHours(-5)),
                    (SnapshotKind.Automatic, 1291, null, now.AddMinutes(-12)),
                };
                foreach (var v in versions)
                {
                    // Each version differs a little (the history keeps only changed copies).
                    doc.Properties.Revision = v.Words.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    DocumentFiles.SaveDocx(doc, path, embedFonts: false);
                    EditorView.History.Add(path, File.ReadAllBytes(path), v.Kind, v.Words, v.Label, v.When);
                }
                var view = Open(window, DocumentFiles.Open(path).Document, path);
                await Settle(500);
                await CaptureWindow(new Views.Dialogs.VersionHistoryDialog(view) { Owner = window, ShowActivated = false }, $"history-{s}", 1500);
            }

            if (Wanted(only, "citations"))
            {
                var view = Open(window, DemoDocuments.Essay());
                await Settle(500);
                await CaptureWindow(new Views.Dialogs.CitationDialog(view, true) { Owner = window, ShowActivated = false }, $"citations-{s}", 900);
            }

            if (Wanted(only, "export"))
            {
                var view = Open(window, DemoDocuments.Report());
                await Settle(500);
                await CaptureWindow(new Views.Dialogs.ExportDialog(view, null) { Owner = window, ShowActivated = false }, $"export-{s}", 700);
            }

            if (Wanted(only, "settings"))
            {
                await CaptureWindow(new SettingsWindow { Owner = window, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual }, $"settings-{s}", 900);
            }

            if (Wanted(only, "tour"))
            {
                window.ShowStartView();
                await Settle(600);
                window.Start?.Scroller.ScrollToEnd();
                await Settle(500);
                await Capture(window, $"start-tour-{s}");
            }

            if (Wanted(only, "spelling"))
            {
                Environment.SetEnvironmentVariable("INKWELL_TEST_SPELLING", "1");
                var b = new DocBuilder(Core.Templates.DocumentDesigns.Default);
                b.Heading(1, "Proofreading");
                b.P("Thsi sentense has a cuple of mistakes that Windows' spell checker finds as you type.");
                b.P($"Right-click a marked word for suggestions, or press {Shortcuts.CheckSpelling.Text} to go to the next one.");
                var view = Open(window, b.Doc);
                await Settle(2500);
                view.DocumentView.Redraw();
                await Settle(300);
                await Capture(window, $"spelling-{s}");
                Environment.SetEnvironmentVariable("INKWELL_TEST_SPELLING", null);
            }

            if (mode == AppTheme.Light && Wanted(only, "smoke")) await Smoke(window);
            if (mode == AppTheme.Light && Wanted(only, "fit")) await Fit(window);
            if (mode == AppTheme.Light && Wanted(only, "scope"))
            {
                // How this copy was installed, as the updater sees it (for checking a test install).
                File.WriteAllLines(Path.Combine(s_folder, "scope.txt"), new[]
                {
                    "version " + Updater.CurrentVersionText,
                    "edition " + Updater.Edition,
                    "folder " + Updater.AppDirectory,
                    "scope " + Updater.DetectInstallScope(),
                    "installer arguments " + Updater.InstallerArguments(Updater.DetectInstallScope()),
                });
            }

            foreach (var w in Application.Current.Windows.OfType<MainWindow>().ToList())
            {
                if (w.Editor is { } e) e.Session.MarkSaved(e.Editor.History.Version);
                w.ShowStartView();
                w.Close();
            }
            await Settle(300);
        }
    }

    /// <summary>End-to-end checks through the real window code: open, type, save, automatic save, import. Results in smoke.txt.</summary>
    private static async Task Smoke(MainWindow window)
    {
        var results = new List<string>();
        void Check(string name, bool ok, string detail = "") => results.Add((ok ? "PASS " : "FAIL ") + name + (detail.Length > 0 ? " — " + detail : ""));
        try
        {
            var path = Path.Combine(s_scratch, "smoke.docx");
            DocumentFiles.SaveDocx(DemoDocuments.Report(), path, embedFonts: false);
            await window.OpenFileAsync(path);
            var view = window.Editor!;
            Check("open .docx", view.Session.FilePath == path && view.Layout.Pages.Count >= 4, view.Layout.Pages.Count + " pages");
            GoToText(view, "Harbour Light finished");
            view.TypeForTest("Smoke test. ");
            Check("typing", view.Session.Document.Body.Paragraphs.Any(p => p.Text.Contains("Smoke test. Harbour")));
            Check("save", view.Save());
            var reopened = DocumentFiles.Open(path).Document;
            Check("saved text reads back", reopened.Body.Paragraphs.Any(p => p.Text.Contains("Smoke test. Harbour")));
            Check("table of contents kept", DocumentWalker.AllBlocks(reopened.Body.Blocks).OfType<FieldBlock>().Any(f => f.Kind == FieldKind.Toc));

            view.TypeForTest("Autosaved. ");
            var waited = 0;
            while (view.Session.IsDirty && waited < 12000)
            {
                await Task.Delay(500);
                waited += 500;
            }
            var auto = DocumentFiles.Open(path).Document;
            Check("automatic save", auto.Body.Paragraphs.Any(p => p.Text.Contains("Autosaved. ")), $"after {waited} ms");

            view.Undo();
            Check("undo", !view.Session.Document.Body.Paragraphs.Any(p => p.Text.Contains("Autosaved. ")));

            var md = Path.Combine(s_scratch, "notes.md");
            File.WriteAllText(md, "# Notes\n\nSome *emphasis* and a [link](https://example.com).\n\n- one\n- two\n", new System.Text.UTF8Encoding(false));
            view.Session.MarkSaved(view.Editor.History.Version);
            await window.OpenFileAsync(md);
            var mdView = window.Editor!;
            Check("open Markdown", mdView.Session.Format == "md" && mdView.Session.FilePath == null && mdView.Session.Document.Body.Paragraphs.First().Props.StyleId == "Heading1");

            var txt = Path.Combine(s_scratch, "plain.txt");
            File.WriteAllText(txt, "First line\r\nSecond line\r\n");
            mdView.Session.MarkSaved(mdView.Editor.History.Version);
            await window.OpenFileAsync(txt);
            Check("open text", window.Editor!.Session.Document.Body.Paragraphs.Count() >= 2);
            window.Editor!.Session.MarkSaved(window.Editor.Editor.History.Version);

            // Messages belong to the view they were shown in.
            var tv = window.Editor!;
            tv.SetFocusMode(true);
            Check("focus mode shows its message", window.MessageShowing);
            tv.SetFocusMode(false);
            Check("leaving focus mode removes the message", !window.MessageShowing);
            window.ShowMessage("A message about page view.", 10);
            tv.SetViewMode(ViewMode.Pageless);
            Check("pageless view removes a message", !window.MessageShowing);
            tv.SetViewMode(ViewMode.Pages);

            // A correction made by Enter: chip, and one undo puts back what was typed and keeps the new paragraph.
            var ac = Open(window, Core.Templates.TemplateCatalog.Find("blank")!.Build(false));
            await Settle(300);
            ac.TypeForTest("so i\n");
            var paras = ac.Session.Document.Body.Paragraphs.Select(p => p.Text).ToList();
            Check("autocorrect by Enter", paras.Count == 2 && paras[0].EndsWith(" I", StringComparison.Ordinal) && ac.AutoCorrectChipShowing, string.Join(" | ", paras));
            ac.Undo();
            paras = ac.Session.Document.Body.Paragraphs.Select(p => p.Text).ToList();
            Check("one undo after Enter puts back what was typed", paras.Count == 2 && paras[0].EndsWith(" i", StringComparison.Ordinal), string.Join(" | ", paras));

            // "Stop doing this" on a Markdown chip, then the switch in Settings turns it back on.
            var md2 = Open(window, Core.Templates.TemplateCatalog.Find("blank")!.Build(false));
            await Settle(300);
            md2.TypeForTest("# ");
            var first = md2.Session.Document.Body.Paragraphs.First();
            Check("Markdown heading", first.Props.StyleId == "Heading1" && md2.AutoCorrectChipShowing);
            md2.StopAutoCorrectRuleForTest();
            var rule = Core.Editing.AutoCorrectRules.MarkdownBlocks;
            Check("Stop doing this turns the rule off", !Core.Editing.AutoCorrectRules.IsOn(AppSettings.Current.DisabledAutoCorrect, rule) && md2.Session.Document.Body.Paragraphs.First().Text == "# ");
            var settings = new SettingsWindow { Owner = window, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
            MoveOffscreen(settings);
            settings.Show();
            MoveOffscreen(settings);
            await Settle(400);
            var name = Core.Editing.AutoCorrectRules.NameOf(rule);
            var toggle = FindChild<System.Windows.Controls.CheckBox>(settings, c => System.Windows.Automation.AutomationProperties.GetName(c) == name);
            Check("Settings shows the rule, switched off", toggle is { IsChecked: false });
            if (toggle != null)
            {
                toggle.IsChecked = true;
                toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, toggle));
            }
            Check("the switch turns it back on", Core.Editing.AutoCorrectRules.IsOn(AppSettings.Current.DisabledAutoCorrect, rule));
            // And a change made elsewhere shows in the open window.
            AppSettings.Update(s => Core.Editing.AutoCorrectRules.SetOn(s.DisabledAutoCorrect, Core.Editing.AutoCorrectRules.Ellipsis, false));
            await Settle(200);
            var ellipsis = FindChild<System.Windows.Controls.CheckBox>(settings, c => System.Windows.Automation.AutomationProperties.GetName(c) == Core.Editing.AutoCorrectRules.NameOf(Core.Editing.AutoCorrectRules.Ellipsis));
            Check("Settings follows changes made elsewhere", ellipsis is { IsChecked: false });
            AppSettings.Update(s => Core.Editing.AutoCorrectRules.SetOn(s.DisabledAutoCorrect, Core.Editing.AutoCorrectRules.Ellipsis, true));
            settings.Close();
            md2.TypeForTest("\n# ");
            Check("the Markdown shortcut works again", md2.Session.Document.Body.Paragraphs.Last().Props.StyleId == "Heading1");
            md2.Session.MarkSaved(md2.Editor.History.Version);
        }
        catch (Exception ex)
        {
            results.Add("FAIL exception — " + ex);
        }
        File.WriteAllLines(Path.Combine(s_folder, "smoke.txt"), results);
    }

    /// <summary>
    /// The command bar at every common width from below the narrowest window to 4K: nothing overflows and no text is
    /// cut off. The same check runs over the panel, the status bar and the dialogs at the narrowest window. Results
    /// in fit.txt, pictures of the command bar in bar-&lt;width&gt;.png.
    /// </summary>
    private static async Task Fit(MainWindow window)
    {
        var results = new List<string>();
        void Check(string name, bool ok, string detail = "") => results.Add((ok ? "PASS " : "FAIL ") + name + (detail.Length > 0 ? " — " + detail : ""));
        try
        {
            var doc = DemoDocuments.Report();
            var path = Path.Combine(s_scratch, "Harbour Light annual report for the members' meeting.docx");
            DocumentFiles.SaveDocx(doc, path, embedFonts: false);
            await window.OpenFileAsync(path);
            var view = window.Editor!;
            await Settle(500);
            GoToText(view, "Harbour Light finished");
            foreach (var width in new[] { 940, 980, 1024, 1100, 1180, 1280, 1366, 1440, 1536, 1600, 1720, 1920, 2560, 3840 })
            {
                view.Width = width; // laid out at this width even beyond the window (the window stays off screen)
                view.UpdateLayout();
                view.FitCommandBar();
                await Settle(150);
                view.CommandBar.UpdateLayout();
                view.FormatBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var overflow = view.FormatBar.DesiredSize.Width - view.BarScroller.ActualWidth;
                var cut = ClippedText(view.CommandBar).Where(p => p.StartsWith("cut", StringComparison.Ordinal)).ToList();
                Check($"command bar at {width}", overflow <= 0.5 && cut.Count == 0, $"level {view.CommandBarLevel}" + (overflow > 0.5 ? $", {overflow:0} px too wide" : "") + (cut.Count > 0 ? ", " + string.Join("; ", cut) : ""));
                SaveElement(view.CommandBar, $"bar-{width:0000}", 1);
                if (width == 980) SaveElement(view.CommandBar, "bar-0980-150pct", 1.5);
            }
            view.Width = double.NaN;

            // The narrowest window, with each panel.
            window.Width = window.MinWidth;
            await Settle(400);
            void Scan(string what, FrameworkElement root)
            {
                var problems = ClippedText(root);
                Check(what + " at the narrowest window", !problems.Any(p => p.StartsWith("cut", StringComparison.Ordinal)), string.Join("; ", problems));
            }
            foreach (var mode in new[] { PanelMode.Format, PanelMode.Page, PanelMode.Review, PanelMode.Reveal, PanelMode.Design })
            {
                view.ShowPanel(mode);
                await Settle(400);
                Scan("panel: " + mode, view.Panel);
            }
            GoToText(view, "Used locally");
            view.ShowPanel(PanelMode.Auto);
            await Settle(400);
            Scan("panel: table", view.Panel);
            var drawing = view.Session.Document.Body.Paragraphs.SelectMany(p => p.Inlines).OfType<DrawingObject>().First(d => d.IsFloating);
            view.DocumentView.SelectDrawing(drawing);
            view.ShowPanel(PanelMode.Picture);
            await Settle(400);
            Scan("panel: picture", view.Panel);
            view.DocumentView.SelectDrawing(null);
            Scan("status bar", view.StatusBar);
            Scan("outline", view.Outline);
            SaveElement((FrameworkElement)window.Content, "narrowest-window", 1);
            view.Session.MarkSaved(view.Editor.History.Version);

            window.ShowStartView();
            await Settle(500);
            Scan("start screen", window.Start!);

            async Task Dialog(string name, Window dialog, double? width = null)
            {
                dialog.WindowStartupLocation = WindowStartupLocation.Manual;
                MoveOffscreen(dialog);
                if (width != null) dialog.Width = width.Value;
                dialog.Show();
                MoveOffscreen(dialog);
                await Settle(600);
                if (dialog.Content is FrameworkElement content) Scan(name, content);
                if (dialog.Content is FrameworkElement shown) SaveElement(shown, "narrow-" + name, 1);
                dialog.Close();
                await Settle(200);
            }
            var settingsWindow = new SettingsWindow { Owner = window, ShowActivated = false };
            await Dialog("settings", settingsWindow, settingsWindow.MinWidth);
            var essay = Open(window, DemoDocuments.Essay());
            await Settle(400);
            await Dialog("export", new Views.Dialogs.ExportDialog(essay, null) { Owner = window, ShowActivated = false });
            await Dialog("citations", new Views.Dialogs.CitationDialog(essay, true) { Owner = window, ShowActivated = false });
            essay.Session.MarkSaved(essay.Editor.History.Version);
            window.Width = 1440;
        }
        catch (Exception ex)
        {
            results.Add("FAIL exception — " + ex);
        }
        File.WriteAllLines(Path.Combine(s_folder, "fit.txt"), results);
    }

    /// <summary>
    /// Text that doesn't fit: "cut" when it is clipped (by its own width or a parent's), "trimmed" when it ends in an
    /// ellipsis by design (document and font names).
    /// </summary>
    private static List<string> ClippedText(FrameworkElement root)
    {
        var problems = new List<string>();
        root.UpdateLayout();
        foreach (var tb in Descendants<System.Windows.Controls.TextBlock>(root))
        {
            if (!tb.IsVisible || tb.ActualWidth <= 0 || string.IsNullOrEmpty(tb.Text) || tb.TextWrapping != TextWrapping.NoWrap) continue;
            var arranged = tb.ActualWidth;
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var natural = tb.DesiredSize.Width - tb.Margin.Left - tb.Margin.Right; // the desired size includes the margin
            tb.InvalidateMeasure();
            if (natural > arranged + 0.5)
            {
                problems.Add((tb.TextTrimming == TextTrimming.None ? "cut" : "trimmed") + $" \"{tb.Text}\" ({natural:0} px in {arranged:0}, {Where(tb)})");
                continue;
            }
            // Clipped by a parent that got less room than it wanted.
            for (DependencyObject? a = tb; a != null && !ReferenceEquals(a, root); a = VisualTreeHelper.GetParent(a))
            {
                if (a is not FrameworkElement fe || fe.ActualWidth <= 0) continue;
                var clip = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(fe)?.Bounds ?? (fe.ClipToBounds ? new Rect(fe.RenderSize) : (Rect?)null);
                if (clip == null) continue;
                var box = tb.TransformToAncestor(fe).TransformBounds(new Rect(tb.RenderSize));
                if (box.Right > clip.Value.Right + 0.5 || box.Left < clip.Value.Left - 0.5)
                {
                    problems.Add($"cut \"{tb.Text}\" by {fe.GetType().Name} {Where(fe)}");
                    break;
                }
            }
        }
        root.UpdateLayout();
        return problems;
    }

    private static string Where(DependencyObject d)
    {
        for (var a = d; a != null; a = VisualTreeHelper.GetParent(a))
        {
            if (a is FrameworkElement { Name.Length: > 0 } named) return named.Name;
            var automation = System.Windows.Automation.AutomationProperties.GetName(a);
            if (!string.IsNullOrEmpty(automation)) return automation;
        }
        return "?";
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }

    /// <summary>A picture of one of our own elements (at 1.5 the pixels a 150 % display would show).</summary>
    private static void SaveElement(FrameworkElement element, string name, double scale)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0) return;
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * scale), (int)Math.Ceiling(element.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        // Through a brush: rendering the element itself would include its offset in the window.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawRectangle(new VisualBrush(element), null, new Rect(element.RenderSize));
        rtb.Render(visual);
        var bitmap = Flatten(rtb);
        var path = Path.Combine(s_folder, name + ".png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var fs = File.Create(path)) encoder.Save(fs);
        s_written.Add(path);
    }

    private static void GoToText(EditorView view, string text)
    {
        foreach (var story in view.Session.Document.Stories)
            foreach (var p in story.Paragraphs)
            {
                var at = p.Text.IndexOf(text, StringComparison.Ordinal);
                if (at < 0) continue;
                view.GoTo(new DocPos(p, at));
                return;
            }
    }

    private static T? FindChild<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t && match(t)) return t;
            var found = FindChild(child, match);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>Shows a window (without blocking), captures it once it's drawn, then closes it.</summary>
    private static async Task CaptureWindow(Window window, string name, int settle)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        MoveOffscreen(window);
        window.Show();
        MoveOffscreen(window);
        await Settle(settle);
        await Capture(window, name);
        window.Close();
        await Settle(200);
    }

    public static async Task Capture(Window window, string name)
    {
        await Settle(150);
        var hwnd = new WindowInteropHelper(window).Handle;
        BitmapSource? bitmap = null;
        var shot = Native.CaptureOwnWindow(hwnd);
        if (shot is { } s && !MostlyBlack(s.Pixels))
        {
            // PrintWindow gives the window including its frame; Mica regions come back transparent, so put them on the window colour.
            var raw = BitmapSource.Create(s.Width, s.Height, 96, 96, PixelFormats.Bgra32, null, s.Pixels, s.Width * 4);
            bitmap = Flatten(raw);
        }
        if (bitmap == null && window.Content is FrameworkElement content)
        {
            var dpi = VisualTreeHelper.GetDpi(window);
            var rtb = new RenderTargetBitmap((int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            rtb.Render(content);
            bitmap = Flatten(rtb);
        }
        if (bitmap == null) return;
        var path = Path.Combine(s_folder, name + ".png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var fs = File.Create(path)) encoder.Save(fs);
        s_written.Add(path);
    }

    private static bool MostlyBlack(byte[] pixels)
    {
        long sum = 0;
        var samples = 0;
        for (var i = 0; i < pixels.Length; i += 4 * 97)
        {
            sum += pixels[i] + pixels[i + 1] + pixels[i + 2];
            samples++;
        }
        return samples == 0 || sum / (double)samples < 6;
    }

    private static BitmapSource Flatten(BitmapSource source)
    {
        var bg = ThemeManager.IsDark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var rect = new Rect(0, 0, source.PixelWidth, source.PixelHeight);
            dc.DrawRectangle(new SolidColorBrush(bg), null, rect);
            dc.DrawImage(source, rect);
        }
        var target = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }
}
