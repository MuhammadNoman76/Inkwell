using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Inkwell.Core.Editing;
using Inkwell.Core.IO;
using Inkwell.Core.Model;
using Inkwell.Services;

namespace Inkwell.Views;

public partial class EditorView
{
    private DateTime _lastEdit = DateTime.MinValue;
    private DateTime _lastRecovery = DateTime.MinValue;
    private Task? _saving;
    private bool _autoSaveFailed;
    private Border? _notice;

    // ------------------------------------------------------------------ save

    private void UpdateSaveState()
    {
        if (_saving != null && !_saving.IsCompleted) SaveState.Text = "Saving…";
        else if (Session.FilePath == null) SaveState.Text = Session.Format == "docx" ? "Not saved yet" : "Imported " + Session.Format.ToUpperInvariant() + " · save as .docx";
        else if (!Session.IsDirty) SaveState.Text = "Saved";
        else if (AppSettings.Current.AutoSaveFile && !_autoSaveFailed) SaveState.Text = "Editing…";
        else SaveState.Text = "Unsaved changes";
    }

    /// <summary>Saves to the document's file (asks for a name the first time). Returns false if not saved.</summary>
    public bool Save()
    {
        if (Session.FilePath == null) return SaveAs();
        return SaveTo(Session.FilePath, automatic: false);
    }

    public bool SaveAs()
    {
        var macro = Session.Document.Source?.HasMacros == true;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save as",
            Filter = macro ? "Word document with macros (*.docm)|*.docm|Word document (*.docx)|*.docx" : "Word document (*.docx)|*.docx",
            DefaultExt = macro ? ".docm" : ".docx",
            FileName = SafeName(Session.DisplayName) + (macro ? ".docm" : ".docx"),
            AddExtension = true,
        };
        var folder = Session.FilePath != null ? Path.GetDirectoryName(Session.FilePath) : AppSettings.Current.LastFolder;
        if (folder != null && Directory.Exists(folder)) dialog.InitialDirectory = folder;
        if (dialog.ShowDialog(_window) != true) return false;
        var path = dialog.FileName;
        if (macro && Path.GetExtension(path).Equals(".docx", StringComparison.OrdinalIgnoreCase))
        {
            var choice = MessageDialog.Show(_window, "Save without macros?", "A .docx file can't contain macros, so the macros in this document will be left out of the copy. Save as .docm to keep them.",
                "Save without macros", null, "Cancel", DialogIcon.Warning);
            if (choice != DialogChoice.Primary) return false;
        }
        AppSettings.Update(s => s.LastFolder = Path.GetDirectoryName(path));
        var oldPath = Session.FilePath;
        if (!SaveTo(path, automatic: false)) return false;
        if (oldPath != null && !string.Equals(oldPath, path, StringComparison.OrdinalIgnoreCase)) { }
        Session.Format = "docx";
        Layout.FileName = Path.GetFileName(path);
        return true;
    }

    private static string SafeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim().Length == 0 ? "Document" : name.Trim();
    }

    /// <summary>Writes the document to a file now (waiting for an automatic save that's running).</summary>
    private bool SaveTo(string path, bool automatic)
    {
        try
        {
            _saving?.Wait(TimeSpan.FromSeconds(30));
        }
        catch
        {
            // The automatic save reported its own error.
        }
        var version = Editor.History.Version;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            PrepareForSave();
            var dropMacros = Session.Document.Source?.HasMacros == true && Path.GetExtension(path).Equals(".docx", StringComparison.OrdinalIgnoreCase);
            var bytes = DocumentFiles.SaveDocx(Session.Document, path, AppSettings.Current.EmbedFonts, dropMacros);
            AfterSave(path, version, bytes, automatic ? SnapshotKindAutomatic : SnapshotKindManual);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Save " + path);
            _window.ShowError(ex is DocumentFileException ? ex : new DocumentFileException("The document couldn't be saved", ex.Message + "\n\nYour work is still open here. Try Save as to save it somewhere else.", ex));
            return false;
        }
        finally
        {
            Mouse.OverrideCursor = null;
            UpdateSaveState();
        }
    }

    private const int SnapshotKindManual = 0;
    private const int SnapshotKindAutomatic = 1;

    /// <summary>Small invisible fixes before saving (the trailing empty paragraph after a final table, properties).</summary>
    private void PrepareForSave()
    {
        var doc = Session.Document;
        doc.Properties.Modified = DateTime.UtcNow;
        doc.Properties.LastModifiedBy = AppSettings.Current.Author;
        doc.Properties.Created ??= DateTime.UtcNow;
        doc.Properties.Creator ??= AppSettings.Current.Author;
    }

    private void AfterSave(string path, int version, byte[] bytes, int kind)
    {
        Session.MarkSaved(version, path);
        _autoSaveFailed = false;
        _window.UpdateTitle();
        Recovery.Discard(Session.RecoveryKey);
        if (!AppSettings.InMemoryOnly) AppSettings.AddRecent(path);
        Snapshot(path, bytes, kind == SnapshotKindManual ? Core.IO.SnapshotKind.ManualSave : Core.IO.SnapshotKind.Automatic);
        // A fresh thumbnail for the start screen.
        try
        {
            var thumb = Thumbnails.RenderFirstPage(Session.Document, 360);
            Thumbnails.Store(path, thumb);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Thumbnail after save");
        }
        RefreshState();
    }

    // ------------------------------------------------------------------ version history

    public static Core.IO.VersionHistory History => s_history ??= new Core.IO.VersionHistory(Path.Combine(AppSettings.LocalDirectory, "History"),
        new Core.IO.VersionHistoryOptions
        {
            MaxSnapshotsPerDocument = AppSettings.Current.HistoryPerDocument,
            MaxTotalBytes = (long)AppSettings.Current.HistoryTotalMegabytes * 1024 * 1024,
        })
    { OnError = Log.Error };

    private static Core.IO.VersionHistory? s_history;

    private void Snapshot(string path, byte[] bytes, Core.IO.SnapshotKind kind)
    {
        if (!AppSettings.Current.VersionHistory || AppSettings.InMemoryOnly || bytes.Length == 0) return;
        var words = Stats.Count(Session.Document).Words;
        Task.Run(() =>
        {
            try
            {
                History.Add(path, bytes, kind, words);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Version history snapshot");
            }
        });
    }

    public void ShowVersionHistory()
    {
        if (Session.FilePath == null)
        {
            _window.ShowMessage("Version history starts once the document is saved.", 4);
            return;
        }
        VersionHistoryDialog.Show(this);
    }

    // ------------------------------------------------------------------ automatic saving and recovery

    /// <summary>Saves a document that has a file a few seconds after editing pauses (on a background thread).</summary>
    private void AutoSaveTick()
    {
        UpdateSaveState();
        if (!AppSettings.Current.AutoSaveFile || Session.FilePath == null || Session.Format != "docx" || !Session.IsDirty) return;
        if (_saving != null && !_saving.IsCompleted) return;
        if ((DateTime.UtcNow - _lastEdit).TotalSeconds < 3) return;
        if (Mouse.LeftButton == MouseButtonState.Pressed || _window.OwnedWindows.Count > 0 || !_window.IsLoaded) return;
        if (Session.Document.Source?.HasMacros == true && !Session.FilePath.EndsWith(".docm", StringComparison.OrdinalIgnoreCase)) return;
        var path = Session.FilePath;
        var version = Editor.History.Version;
        Document copy;
        try
        {
            PrepareForSave();
            copy = DocumentCloner.Clone(Session.Document);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Copy for automatic save");
            return;
        }
        var embed = AppSettings.Current.EmbedFonts;
        UpdateSaveState();
        _saving = Task.Run(() => DocumentFiles.SaveDocx(copy, path, embed)).ContinueWith(t =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (t.IsFaulted)
                {
                    _autoSaveFailed = true;
                    Log.Error(t.Exception, "Automatic save " + path);
                    _window.ShowMessage("Couldn't save automatically. Press Ctrl+S to save, or Save as to save elsewhere.", 6, warning: true);
                    UpdateSaveState();
                    return;
                }
                // The live document keeps its own package bytes: the copy's save only changed the copy.
                if (Session.FilePath == path) AfterSave(path, version, t.Result, SnapshotKindAutomatic);
            }));
        }, TaskScheduler.Default);
    }

    /// <summary>Keeps a recovery copy of unsaved work every few minutes (new documents too).</summary>
    private void RecoveryTick()
    {
        var minutes = AppSettings.Current.RecoveryMinutes;
        if (minutes <= 0 || !Session.IsDirty || AppSettings.InMemoryOnly) return;
        if ((DateTime.UtcNow - _lastRecovery).TotalMinutes < minutes) return;
        if ((DateTime.UtcNow - _lastEdit).TotalSeconds < 2) return;
        _lastRecovery = DateTime.UtcNow;
        Document copy;
        try
        {
            copy = DocumentCloner.Clone(Session.Document);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Copy for recovery");
            return;
        }
        var key = Session.RecoveryKey;
        var title = Session.DisplayName;
        var original = Session.FilePath;
        Task.Run(async () =>
        {
            try
            {
                var bytes = DocumentFiles.DocxBytes(copy);
                if (bytes.Length > 0) await Recovery.SaveAsync(key, bytes, title, original);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Recovery copy");
            }
        });
    }

    /// <summary>Asks to save unsaved changes. Returns false when the user cancels.</summary>
    public bool ConfirmClose()
    {
        try
        {
            _saving?.Wait(TimeSpan.FromSeconds(30));
        }
        catch
        {
            // reported already
        }
        if (!Session.IsDirty)
        {
            Recovery.Discard(Session.RecoveryKey);
            return true;
        }
        var choice = MessageDialog.Show(_window, $"Save changes to “{Session.DisplayName}”?",
            Session.FilePath == null ? "This document hasn't been saved yet." : "Your recent changes haven't been saved.",
            "Save", "Don't save", "Cancel", DialogIcon.Question);
        switch (choice)
        {
            case DialogChoice.Primary:
                if (!Save()) return false;
                return true;
            case DialogChoice.Secondary:
                Recovery.Discard(Session.RecoveryKey);
                return true;
            default:
                return false;
        }
    }

    private void GoToStart()
    {
        if (!ConfirmClose()) return;
        _window.ShowStartView();
    }

    private void OpenDocument()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open a document",
            Filter = DocumentFiles.OpenFilter,
            CheckFileExists = true,
        };
        if (AppSettings.Current.LastFolder is { } folder && Directory.Exists(folder)) dialog.InitialDirectory = folder;
        if (dialog.ShowDialog(_window) != true) return;
        AppSettings.Update(s => s.LastFolder = Path.GetDirectoryName(dialog.FileName));
        App.Current.OpenPath(dialog.FileName, null);
    }

    // ------------------------------------------------------------------ export and print

    public void ShowExport(string? kind) => ExportDialog.Show(this, kind);

    public void Print() => Printing.Print(this);

    private void ShowProperties() => PropertiesDialog.Show(this);

    // ------------------------------------------------------------------ the notice after opening

    /// <summary>Tells the user plainly what the file contains that's kept but not editable, and what was converted.</summary>
    public void ShowOpenNotice(OpenResult result)
    {
        var doc = result.Document;
        var lines = new List<string>();
        foreach (var p in doc.Preserved) lines.Add(p.Description);
        lines.AddRange(doc.Warnings);
        lines.AddRange(result.Notes);
        var missingFonts = MissingFonts(doc);
        if (missingFonts.Count > 0)
            lines.Add("Fonts this PC doesn't have are shown with similar ones: " + string.Join(", ", missingFonts.Take(6)) + (missingFonts.Count > 6 ? "…" : "") + ". The file keeps the original font names.");
        if (lines.Count == 0 && result.SavePath != null) return;
        string heading;
        if (result.SavePath == null) heading = "Imported from " + result.Format.ToUpperInvariant() + ". Save to keep it as a Word document (.docx).";
        else if (doc.Preserved.Count > 0) heading = "Some parts of this document are kept exactly as they are, but can't be edited in Inkwell";
        else heading = "Opened with a few adjustments";
        ShowNotice(heading, lines);
    }

    private static List<string> MissingFonts(Document doc)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Take(System.Xml.Linq.XElement? rFonts)
        {
            if (rFonts == null) return;
            foreach (var a in new[] { "ascii", "hAnsi", "cs" })
                if (rFonts.Attribute(Ns.W + a)?.Value is { } n && n.Length > 0) names.Add(n);
        }
        foreach (var e in doc.Styles.Root.Descendants(Ns.W + "rFonts")) Take(e);
        foreach (var p in doc.Body.Paragraphs.Take(3000))
            foreach (var i in p.Inlines)
                Take(i.Props.El("rFonts"));
        foreach (var slot in new[] { true, false })
            if (doc.Theme.Font(slot, "latin") is { } f) names.Add(f);
        return names.Where(n => !Core.Rendering.FontCatalog.IsAvailable(n) && !n.StartsWith("+", StringComparison.Ordinal)).OrderBy(n => n).ToList();
    }

    private void ShowNotice(string heading, List<string> lines)
    {
        HideNotice();
        var bar = new Border
        {
            Margin = new Thickness(18, 12, 18, 0),
            Padding = new Thickness(16, 12, 10, 12),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Top,
            MaxWidth = 860,
        };
        bar.SetResourceReference(Border.BackgroundProperty, "Flyout.Background");
        bar.SetResourceReference(Border.BorderBrushProperty, "Card.Stroke");
        bar.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = 0.16 };
        var dock = new DockPanel();
        var icon = new Controls.IconView { Width = 18, Height = 18, Margin = new Thickness(0, 1, 12, 0), VerticalAlignment = VerticalAlignment.Top, Data = (Geometry)FindResource("Icon.InfoFilled") };
        icon.SetResourceReference(Controls.IconView.ForegroundProperty, "Accent.Fill");
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);
        var close = new Button { VerticalAlignment = VerticalAlignment.Top, ToolTip = "Close" };
        close.SetResourceReference(StyleProperty, "IconButton");
        Controls.Ui.SetIcon(close, (Geometry)FindResource("Icon.Dismiss"));
        System.Windows.Automation.AutomationProperties.SetName(close, "Close the notice");
        close.Click += (_, _) => HideNotice();
        DockPanel.SetDock(close, Dock.Right);
        dock.Children.Add(close);
        var stack = new StackPanel();
        var h = new TextBlock { Text = heading, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        h.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        stack.Children.Add(h);
        foreach (var line in lines.Take(8))
        {
            var t = new TextBlock { Text = "•  " + line, FontSize = 12.5, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
            stack.Children.Add(t);
        }
        if (lines.Count > 8)
        {
            var more = new TextBlock { Text = $"…and {lines.Count - 8} more.", FontSize = 12.5, Margin = new Thickness(0, 4, 0, 0) };
            more.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
            stack.Children.Add(more);
        }
        dock.Children.Add(stack);
        bar.Child = dock;
        _notice = bar;
        var grid = (Grid)CenterLayer.Child;
        grid.Children.Add(bar);
    }

    private void HideNotice()
    {
        if (_notice == null) return;
        ((Grid)CenterLayer.Child).Children.Remove(_notice);
        _notice = null;
    }
}
