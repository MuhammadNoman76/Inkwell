using System.Diagnostics;
using System.Printing;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Inkwell.Controls;
using Inkwell.Core.Export;
using Inkwell.Core.IO;
using Inkwell.Core.Layout;
using Inkwell.Core.Model;
using Inkwell.Core.Rendering;
using Inkwell.Services;

namespace Inkwell.Views.Dialogs;

/// <summary>Export: PDF (selectable text, bookmarks, links), Markdown, a web page, plain text, or print.</summary>
public sealed class ExportDialog : InkDialog
{
    private readonly EditorView _view;
    private readonly List<RadioButton> _kinds = new();
    private readonly StackPanel _pdfOptions = new();
    private readonly CheckBox _selectable;
    private readonly CheckBox _bookmarks;
    private readonly CheckBox _links;
    private readonly CheckBox _markup;
    private static readonly (string Kind, string Title, string Description, string Icon)[] Kinds =
    {
        ("pdf", "PDF", "For sharing and printing. Looks the same everywhere; text stays selectable and searchable.", "Icon.DocumentPdf"),
        ("md", "Markdown", "Plain text with simple marks for headings, lists and links. Pictures go in a folder next to it.", "Icon.Code"),
        ("html", "Web page", "One HTML file with the pictures inside, for a browser or a website.", "Icon.Globe"),
        ("txt", "Plain text", "Just the words (UTF-8). Lists keep their numbers.", "Icon.DocumentText"),
        ("print", "Print", "Send it to a printer.", "Icon.Print"),
    };

    public ExportDialog(EditorView view, string? kind) : base("Export", "Your document stays as it is; this makes a copy in another format.", 560)
    {
        _view = view;
        var list = new StackPanel();
        foreach (var k in Kinds)
        {
            var r = new RadioButton { GroupName = "export", IsChecked = (kind ?? "pdf") == k.Kind, Margin = new Thickness(0, 0, 0, 6), Tag = k.Kind, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            var row = new DockPanel { Margin = new Thickness(4, 2, 0, 4) };
            var icon = new IconView { Width = 20, Height = 20, Margin = new Thickness(0, 2, 12, 0), VerticalAlignment = VerticalAlignment.Top };
            if (TryFindResource(k.Icon) is Geometry g) icon.Data = g;
            icon.SetResourceReference(IconView.ForegroundProperty, "Accent.Fill");
            row.Children.Add(icon);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = k.Title, FontWeight = FontWeights.SemiBold });
            var d = new TextBlock { Text = k.Description, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
            d.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
            text.Children.Add(d);
            row.Children.Add(text);
            r.Content = row;
            AutomationProperties.SetName(r, k.Title + ": " + k.Description);
            r.Checked += (_, _) => _pdfOptions.Visibility = (string)r.Tag == "pdf" ? Visibility.Visible : Visibility.Collapsed;
            _kinds.Add(r);
            list.Children.Add(r);
        }
        Body.Children.Add(list);
        _pdfOptions.Margin = new Thickness(32, 4, 0, 0);
        _selectable = new CheckBox { Content = "Selectable, searchable text", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
        _bookmarks = new CheckBox { Content = "Bookmarks from headings", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
        _links = new CheckBox { Content = "Clickable links", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
        _markup = new CheckBox { Content = "Show tracked changes and comments", IsChecked = false, Margin = new Thickness(0, 4, 0, 0), IsEnabled = view.Editor.HasRevisions() || view.Session.Document.Comments.Count > 0 };
        _pdfOptions.Children.Add(_selectable);
        _pdfOptions.Children.Add(_bookmarks);
        _pdfOptions.Children.Add(_links);
        _pdfOptions.Children.Add(_markup);
        _pdfOptions.Visibility = (kind ?? "pdf") == "pdf" ? Visibility.Visible : Visibility.Collapsed;
        Body.Children.Add(_pdfOptions);
        AddButton("Export…", primary: true, onClick: () => true);
        AddButton("Cancel", isCancel: true);
    }

    public string Kind => (string)(_kinds.FirstOrDefault(k => k.IsChecked == true)?.Tag ?? "pdf");

    public static void Show(EditorView view, string? kind)
    {
        var dialog = new ExportDialog(view, kind);
        if (dialog.Run(view.Window) != "Export…") return;
        var chosen = dialog.Kind;
        if (chosen == "print")
        {
            Printing.Print(view);
            return;
        }
        var options = new PdfExportOptions
        {
            SelectableText = dialog._selectable.IsChecked == true,
            Bookmarks = dialog._bookmarks.IsChecked == true,
            Links = dialog._links.IsChecked == true,
            ShowMarkup = dialog._markup.IsChecked == true,
            FileName = view.Session.FilePath != null ? Path.GetFileName(view.Session.FilePath) : null,
        };
        Export(view, chosen, options);
    }

    /// <summary>Asks for a file name and exports a copy of the document there (on a background thread).</summary>
    public static void Export(EditorView view, string kind, PdfExportOptions? pdf = null)
    {
        var (filter, ext) = kind switch
        {
            "md" => ("Markdown (*.md)|*.md", ".md"),
            "html" => ("Web page (*.html)|*.html", ".html"),
            "txt" => ("Plain text (*.txt)|*.txt", ".txt"),
            _ => ("PDF (*.pdf)|*.pdf", ".pdf"),
        };
        var name = view.Session.DisplayName;
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Export", Filter = filter, DefaultExt = ext, AddExtension = true, FileName = Path.GetFileNameWithoutExtension(name) + ext };
        var folder = AppSettings.Current.LastExportFolder ?? (view.Session.FilePath != null ? Path.GetDirectoryName(view.Session.FilePath) : AppSettings.Current.LastFolder);
        if (folder != null && Directory.Exists(folder)) dialog.InitialDirectory = folder;
        if (dialog.ShowDialog(view.Window) != true) return;
        var path = dialog.FileName;
        AppSettings.Update(s => s.LastExportFolder = Path.GetDirectoryName(path));
        Document copy;
        try
        {
            copy = DocumentCloner.Clone(view.Session.Document);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Copy for export");
            view.Window.ShowError(ex);
            return;
        }
        var window = view.Window;
        window.SetBusy("Exporting…", Path.GetFileName(path));
        pdf ??= new PdfExportOptions();
        pdf.Progress = (done, total) => window.Dispatcher.BeginInvoke(new Action(() => window.SetBusy("Exporting…", $"Page {done} of {total}", total > 0 ? done / (double)total : -1)));
        // WPF drawing needs an STA thread; exporting on one keeps the editor responsive.
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (kind == "pdf") PdfExporter.Export(copy, path, pdf);
                else DocumentFiles.Export(copy, path, kind);
            }
            catch (Exception ex)
            {
                error = ex;
            }
            window.Dispatcher.BeginInvoke(new Action(() => Finished(view, path, error)));
        }) { IsBackground = true, Name = "Inkwell export" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static void Finished(EditorView view, string path, Exception? error)
    {
        var window = view.Window;
        window.SetBusy(null);
        if (error != null)
        {
            Log.Error(error, "Export " + path);
            window.ShowError(error is DocumentFileException ? error : new DocumentFileException("The export didn't work", error.Message, error));
            return;
        }
        Log.Info("Exported " + path);
        if (MainWindow.IsTestOffscreen)
        {
            window.ShowMessage("Exported " + Path.GetFileName(path), 3);
            return;
        }
        var choice = MessageDialog.Show(window, "Exported", path, "Open it", "Show in folder", "Close", DialogIcon.Success);
        try
        {
            if (choice == DialogChoice.Primary) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else if (choice == DialogChoice.Secondary) StartView.ShowInFolder(path);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Open export");
        }
    }
}

/// <summary>Printing through Windows' print dialog: the pages as laid out, at full resolution.</summary>
public static class Printing
{
    public static void Print(EditorView view)
    {
        var dialog = new PrintDialog { UserPageRangeEnabled = true, CurrentPageEnabled = true };
        var pages = view.Layout.Pages.Count;
        dialog.PageRangeSelection = PageRangeSelection.AllPages;
        dialog.MinPage = 1;
        dialog.MaxPage = (uint)Math.Max(1, pages);
        if (dialog.ShowDialog() != true) return;
        try
        {
            // A fresh layout of a copy without editing marks (no formatting marks, no markup unless shown).
            var copy = DocumentCloner.Clone(view.Session.Document);
            var options = new ViewOptions { Revisions = view.Layout.View.Revisions == RevisionView.AllMarkup ? RevisionView.AllMarkup : RevisionView.NoMarkup, FieldShading = false };
            var layout = new DocumentLayout(copy, options) { FileName = view.Layout.FileName };
            layout.LayoutAll();
            var first = 0;
            var last = layout.Pages.Count - 1;
            if (dialog.PageRangeSelection == PageRangeSelection.UserPages)
            {
                first = Math.Max(0, dialog.PageRange.PageFrom - 1);
                last = Math.Min(last, dialog.PageRange.PageTo - 1);
            }
            else if (dialog.PageRangeSelection == PageRangeSelection.CurrentPage && view.DocumentView.CurrentPage is { } current)
            {
                first = last = Math.Min(current.Index, last);
            }
            var paginator = new LayoutPaginator(layout, first, last);
            dialog.PrintDocument(paginator, view.Session.DisplayName);
            view.Window.ShowMessage($"Sent {last - first + 1} {(last == first ? "page" : "pages")} to the printer.", 3);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Print");
            view.Window.ShowError(new DocumentFileException("Printing didn't work", ex.Message, ex));
        }
    }

    private sealed class LayoutPaginator : DocumentPaginator
    {
        private readonly DocumentLayout _layout;
        private readonly PageRenderer _renderer;
        private readonly int _first;
        private readonly int _last;

        public LayoutPaginator(DocumentLayout layout, int first, int last)
        {
            _layout = layout;
            _renderer = new PageRenderer(layout);
            _first = first;
            _last = last;
        }

        public override DocumentPage GetPage(int pageNumber)
        {
            var page = _layout.Pages[_first + pageNumber];
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) _renderer.DrawPage(dc, page);
            return new DocumentPage(visual, page.Size, new Rect(page.Size), new Rect(page.Size));
        }

        public override bool IsPageCountValid => true;

        public override int PageCount => Math.Max(0, _last - _first + 1);

        public override Size PageSize
        {
            get => _layout.Pages.Count > 0 ? _layout.Pages[_first].Size : new Size(816, 1056);
            set { }
        }

        public override IDocumentPaginatorSource? Source => null;
    }
}
