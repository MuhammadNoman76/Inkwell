using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Inkwell.Controls;
using Inkwell.Core.Editing;
using Inkwell.Core.IO;
using Inkwell.Services;

namespace Inkwell.Views.Dialogs;

/// <summary>
/// Version history: the copies Inkwell kept of this document (each save, and automatically while editing), with
/// a preview. Open one as a separate copy, or restore it (the current version is kept in the history first).
/// </summary>
public sealed class VersionHistoryDialog : FluentWindow
{
    private readonly EditorView _view;
    private readonly string _path;
    private readonly ListBox _list = new();
    private readonly Image _preview = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), FontSize = 12.5 };
    private readonly Button _open;
    private readonly Button _restore;
    private readonly Button _name;
    private readonly Button _delete;
    private int _previewToken;

    public VersionHistoryDialog(EditorView view)
    {
        _view = view;
        _path = view.Session.FilePath!;
        Title = "Version history";
        Width = 900;
        Height = 640;
        MinWidth = 640;
        MinHeight = 420;
        IsDialog = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new DockPanel();
        var footer = new Border();
        footer.SetResourceReference(StyleProperty, "DialogFooter");
        var buttons = new DockPanel();
        var where = new TextBlock { Text = "Kept on this PC in " + EditorView.History.Root, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 360 };
        where.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
        buttons.Children.Add(where);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _name = new Button { Content = "Name it…", Margin = new Thickness(8, 0, 0, 0) };
        _name.Click += (_, _) => NameSelected();
        _delete = new Button { Content = "Delete", Margin = new Thickness(8, 0, 0, 0) };
        _delete.Click += (_, _) => DeleteSelected();
        _open = new Button { Content = "Open as a copy", Margin = new Thickness(8, 0, 0, 0) };
        _open.Click += (_, _) => OpenSelected();
        _restore = new Button { Content = "Restore this version", Margin = new Thickness(8, 0, 0, 0) };
        _restore.SetResourceReference(StyleProperty, "AccentButton");
        _restore.Click += (_, _) => RestoreSelected();
        var close = new Button { Content = "Close", Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        close.Click += (_, _) => Close();
        foreach (var b in new[] { _name, _delete, _open, _restore, close }) right.Children.Add(b);
        buttons.Children.Add(right);
        footer.Child = buttons;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var grid = new Grid { Margin = new Thickness(24, 20, 24, 16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new DockPanel();
        var heading = new TextBlock { Text = Path.GetFileName(_path) };
        heading.SetResourceReference(StyleProperty, "Text.Subtitle");
        DockPanel.SetDock(heading, Dock.Top);
        left.Children.Add(heading);
        _list.Margin = new Thickness(0, 10, 0, 0);
        AutomationProperties.SetName(_list, "Versions");
        _list.SelectionChanged += (_, _) => ShowSelected();
        left.Children.Add(_list);
        grid.Children.Add(left);

        var preview = new DockPanel();
        DockPanel.SetDock(_details, Dock.Bottom);
        preview.Children.Add(_details);
        var frame = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(16) };
        frame.SetResourceReference(Border.BackgroundProperty, "Canvas.Background");
        frame.SetResourceReference(Border.BorderBrushProperty, "Card.Stroke");
        _preview.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Direction = 270, Opacity = 0.2 };
        AutomationProperties.SetName(_preview, "Preview of the first page");
        frame.Child = _preview;
        preview.Children.Add(frame);
        Grid.SetColumn(preview, 2);
        grid.Children.Add(preview);
        root.Children.Add(grid);
        Content = root;
        Fill(null);
    }

    public static void Show(EditorView view)
    {
        var dialog = new VersionHistoryDialog(view) { Owner = view.Window };
        dialog.ShowDialog();
    }

    private static string KindName(SnapshotKind kind) => kind switch
    {
        SnapshotKind.ManualSave => "Saved",
        SnapshotKind.Automatic => "Saved automatically",
        SnapshotKind.BeforeRestore => "Before a restore",
        _ => kind.ToString(),
    };

    private void Fill(string? select)
    {
        _list.Items.Clear();
        List<SnapshotInfo> snapshots;
        try
        {
            snapshots = EditorView.History.List(_path).OrderByDescending(s => s.TimeUtc).ToList();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Version history list");
            snapshots = new List<SnapshotInfo>();
        }
        var day = DateTime.MinValue;
        foreach (var s in snapshots)
        {
            var local = s.TimeUtc.ToLocalTime();
            if (local.Date != day)
            {
                day = local.Date;
                var header = new TextBlock { Text = day == DateTime.Today ? "Today" : day == DateTime.Today.AddDays(-1) ? "Yesterday" : day.ToString("D", CultureInfo.CurrentCulture), FontSize = 11.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 10, 0, 4) };
                header.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
                _list.Items.Add(new ListBoxItem { Content = header, IsEnabled = false, Focusable = false });
            }
            var stack = new StackPanel { Margin = new Thickness(4, 4, 4, 4) };
            var top = new TextBlock { Text = local.ToString("t", CultureInfo.CurrentCulture) + (s.Label != null ? " · " + s.Label : ""), FontWeight = FontWeights.SemiBold };
            stack.Children.Add(top);
            var sub = new TextBlock { Text = $"{KindName(s.Kind)} · {s.WordCount.ToString("N0", CultureInfo.CurrentCulture)} words · {(s.Size / 1024.0).ToString("N0", CultureInfo.CurrentCulture)} KB", FontSize = 12 };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
            stack.Children.Add(sub);
            var item = new ListBoxItem { Content = stack, Tag = s };
            AutomationProperties.SetName(item, $"{local:g}, {KindName(s.Kind)}, {s.WordCount} words" + (s.Label != null ? ", " + s.Label : ""));
            _list.Items.Add(item);
            if (s.Id == select) _list.SelectedItem = item;
        }
        if (snapshots.Count == 0)
        {
            _list.Items.Add(new ListBoxItem { Content = new TextBlock { Text = "No versions yet. A version is kept each time the document is saved.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 8, 4, 8) }, IsEnabled = false });
        }
        if (_list.SelectedItem == null) _list.SelectedItem = _list.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is SnapshotInfo);
        ShowSelected();
    }

    private SnapshotInfo? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as SnapshotInfo;

    private void ShowSelected()
    {
        var s = Selected;
        foreach (var b in new[] { _open, _restore, _name, _delete }) b.IsEnabled = s != null;
        _preview.Source = null;
        if (s == null)
        {
            _details.Text = "";
            return;
        }
        _details.Text = $"{s.TimeUtc.ToLocalTime().ToString("F", CultureInfo.CurrentCulture)}\n{KindName(s.Kind)}, {s.WordCount.ToString("N0", CultureInfo.CurrentCulture)} words.";
        var token = ++_previewToken;
        var path = _path;
        var id = s.Id;
        // Read and render the version's first page off the UI thread (an STA thread: WPF drawing).
        var thread = new Thread(() =>
        {
            BitmapSource? image = null;
            try
            {
                var bytes = EditorView.History.Read(path, id);
                if (bytes != null)
                {
                    using var ms = new MemoryStream(bytes);
                    var doc = DocxReader.Load(ms, Path.GetFileName(path));
                    image = Thumbnails.RenderFirstPage(doc, 900);
                    image.Freeze();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Version preview");
            }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (token == _previewToken) _preview.Source = image;
            }));
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private byte[]? ReadSelected()
    {
        var s = Selected;
        if (s == null) return null;
        var bytes = EditorView.History.Read(_path, s.Id);
        if (bytes == null) MessageDialog.Show(this, "This version can't be read", "Its file in the history folder is missing or damaged.", "OK", icon: DialogIcon.Warning);
        return bytes;
    }

    private void OpenSelected()
    {
        var bytes = ReadSelected();
        if (bytes == null) return;
        try
        {
            using var ms = new MemoryStream(bytes);
            var doc = DocxReader.Load(ms, Path.GetFileName(_path));
            var when = Selected!.TimeUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            doc.Properties.Title ??= Path.GetFileNameWithoutExtension(_path);
            var session = new DocumentSession(doc, null) { };
            session.MarkDirty();
            Close();
            App.Current.OpenSession(session, null, $"A copy of the version from {when}. Save it under a new name to keep it.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Open version");
            MessageDialog.Show(this, "This version can't be opened", ex.Message, "OK", icon: DialogIcon.Warning);
        }
    }

    private void RestoreSelected()
    {
        var bytes = ReadSelected();
        if (bytes == null) return;
        var when = Selected!.TimeUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        var choice = MessageDialog.Show(this, "Restore this version?", $"The document goes back to how it was on {when}. The current version is kept in the history, so you can come back to it.",
            "Restore", null, "Cancel", DialogIcon.Question);
        if (choice != DialogChoice.Primary) return;
        try
        {
            var current = DocumentFiles.DocxBytes(_view.Session.Document);
            var words = _view.Stats.Count(_view.Session.Document).Words;
            EditorView.History.Add(_path, current, SnapshotKind.BeforeRestore, words, "Before restoring " + when);
            DocumentFiles.WriteAtomically(_path, bytes);
            Recovery.Discard(_view.Session.RecoveryKey);
            _view.Session.MarkSaved(_view.Editor.History.Version, _path);
            Close();
            _ = _view.Window.OpenFileAsync(_path);
            _view.Window.ShowMessage($"Restored the version from {when}. The version before it is in the history.", 5);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Restore version");
            MessageDialog.Show(this, "The version couldn't be restored", ex.Message, "OK", icon: DialogIcon.Error);
        }
    }

    private void NameSelected()
    {
        var s = Selected;
        if (s == null) return;
        var name = InputDialog.Ask(this, "Name this version", "A short name, like “Sent to the editor”. Named versions are easier to find.", s.Label ?? "", "Save");
        if (name == null) return;
        EditorView.History.SetLabel(_path, s.Id, string.IsNullOrWhiteSpace(name) ? null : name.Trim());
        Fill(s.Id);
    }

    private void DeleteSelected()
    {
        var s = Selected;
        if (s == null) return;
        if (MessageDialog.Show(this, "Delete this version?", "It's removed from the history for good.", "Delete", null, "Cancel", DialogIcon.Warning) != DialogChoice.Primary) return;
        EditorView.History.Delete(_path, s.Id);
        Fill(null);
    }
}
