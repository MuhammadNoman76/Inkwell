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
                b.P("Right-click a marked word for suggestions, or press F7 to go to the next one.");
                var view = Open(window, b.Doc);
                await Settle(2500);
                view.DocumentView.Redraw();
                await Settle(300);
                await Capture(window, $"spelling-{s}");
                Environment.SetEnvironmentVariable("INKWELL_TEST_SPELLING", null);
            }

            if (mode == AppTheme.Light && Wanted(only, "smoke")) await Smoke(window);

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
        }
        catch (Exception ex)
        {
            results.Add("FAIL exception — " + ex);
        }
        File.WriteAllLines(Path.Combine(s_folder, "smoke.txt"), results);
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
