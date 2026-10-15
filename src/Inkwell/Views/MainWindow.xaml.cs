using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Inkwell.Core.Editing;
using Inkwell.Core.IO;
using Inkwell.Services;

namespace Inkwell.Views;

/// <summary>An Inkwell window: the start screen, or one document in the editor.</summary>
public partial class MainWindow
{
    private StartView? _start;
    private EditorView? _editor;
    private DispatcherTimer? _toastTimer;

    public MainWindow()
    {
        InitializeComponent();
        RestorePlacement();
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            UpdateService.Changed -= OnUpdateChanged;
            _editor?.Detach();
        };
        UpdateService.Changed += OnUpdateChanged;
        Loaded += (_, _) => OnUpdateChanged();
        Drop += OnDropFiles;
        DragOver += (_, e) =>
        {
            if (_editor == null && e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effects = DragDropEffects.Copy;
        };
    }

    /// <summary>INKWELL_TEST_OFFSCREEN=1: windows open off screen and never take the focus (automated tests).</summary>
    public static bool IsTestOffscreen => s_offscreen || Services.CaptureRunner.Running;

    private static readonly bool s_offscreen = Environment.GetEnvironmentVariable("INKWELL_TEST_OFFSCREEN") == "1";

    public DocumentSession? Session => _editor?.Session;
    public EditorView? Editor => _editor;
    public StartView? Start => _start;

    // ------------------------------------------------------------------ views

    public void ShowStartView()
    {
        HideMessage(); // messages are about the view they were shown in
        if (_editor != null)
        {
            _editor.Detach();
            _editor = null;
        }
        _start ??= new StartView(this);
        _start.Refresh();
        Host.Content = _start;
        Title = "Inkwell";
        Dispatcher.BeginInvoke(new Action(() => _start.FocusFirst()), DispatcherPriority.Loaded);
    }

    public void ShowEditor(DocumentSession session)
    {
        HideMessage();
        _editor?.Detach();
        _editor = new EditorView(this, session);
        Host.Content = _editor;
        UpdateTitle();
    }

    public void UpdateTitle()
    {
        if (_editor == null)
        {
            Title = "Inkwell";
            return;
        }
        var s = _editor.Session;
        Title = (s.IsDirty ? "• " : "") + s.DisplayName + " – Inkwell";
    }

    public void BringToFront()
    {
        if (IsTestOffscreen) return; // test hook: never take the focus
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Focus();
    }

    /// <summary>Opens a document (or imports Markdown, text, RTF or ODT) into this window.</summary>
    public async Task OpenFileAsync(string path)
    {
        if (!File.Exists(path))
        {
            AppSettings.RemoveRecent(path);
            MessageDialog.Show(this, "The file isn't there any more", path, "OK", icon: DialogIcon.Warning);
            _start?.Refresh();
            return;
        }
        var result = await RunBusyAsync($"Opening {Path.GetFileName(path)}…", "", () => DocumentFiles.Open(path));
        if (result == null) return;
        var session = new DocumentSession(result.Document, result.SavePath, result.Format);
        if (result.SavePath == null) session.MarkDirty();
        ShowEditor(session);
        Log.Info($"Opened {path} ({result.Format})");
        if (result.SavePath != null) AppSettings.AddRecent(path);
        else AppSettings.AddRecent(path);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        _editor?.ShowOpenNotice(result);
    }

    private void OnDropFiles(object sender, DragEventArgs e)
    {
        if (_editor != null) return; // the editor handles its own drops (pictures, documents)
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var docs = files.Where(DocumentFiles.CanOpen).ToList();
        if (docs.Count == 0) return;
        App.Current.OpenPath(docs[0], this);
        foreach (var other in docs.Skip(1).Take(7)) App.Current.OpenPath(other, null);
        e.Handled = true;
    }

    // ------------------------------------------------------------------ busy overlay, messages

    /// <summary>Runs work on a background thread with a busy overlay. Shows the error and returns null on failure.</summary>
    public async Task<T?> RunBusyAsync<T>(string title, string detail, Func<T> work) where T : class
    {
        BusyTitle.Text = title;
        BusyDetail.Text = detail;
        BusyBar.IsIndeterminate = true;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Busy.Visibility = Visibility.Visible;
            Busy.Opacity = 0;
            Busy.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
        };
        timer.Start();
        try
        {
            return await Task.Run(work);
        }
        catch (Exception ex)
        {
            if (ex is not DocumentFileException) Log.Error(ex, title);
            ShowError(ex);
            return null;
        }
        finally
        {
            timer.Stop();
            Busy.BeginAnimation(OpacityProperty, null);
            Busy.Visibility = Visibility.Collapsed;
        }
    }

    public void SetBusy(string? title, string detail = "", double fraction = -1)
    {
        if (title == null)
        {
            Busy.Visibility = Visibility.Collapsed;
            return;
        }
        BusyTitle.Text = title;
        BusyDetail.Text = detail;
        BusyBar.IsIndeterminate = fraction < 0;
        if (fraction >= 0) BusyBar.Value = fraction;
        Busy.Visibility = Visibility.Visible;
        Busy.Opacity = 1;
    }

    public void ShowError(Exception ex)
    {
        var (heading, message) = ex switch
        {
            DocumentFileException d => (d.Heading, d.Message),
            UnauthorizedAccessException => ("Access denied", "Windows didn't allow Inkwell to use this file. It may be read-only or in a protected folder."),
            IOException io => ("The file couldn't be read or written", io.Message),
            _ => ("Something went wrong", ex.Message),
        };
        MessageDialog.Show(this, heading, message, "OK", icon: DialogIcon.Error);
    }

    public void ShowMessage(string text, double seconds = 4, bool warning = false)
    {
        var toast = ++_toastNumber;
        ToastText.Text = text;
        ToastIcon.Data = (System.Windows.Media.Geometry)FindResource(warning ? "Icon.Warning" : "Icon.Info");
        Toast.Visibility = Visibility.Visible;
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
            // A newer message that came in while this one faded stays.
            fade.Completed += (_, _) =>
            {
                if (toast == _toastNumber) Toast.Visibility = Visibility.Collapsed;
            };
            Toast.BeginAnimation(OpacityProperty, fade);
        };
        _toastTimer.Start();
    }

    /// <summary>Takes the message away at once: the view changed (focus mode, pageless, another document), so it no longer applies.</summary>
    public void HideMessage()
    {
        _toastNumber++;
        _toastTimer?.Stop();
        Toast.BeginAnimation(OpacityProperty, null);
        Toast.Visibility = Visibility.Collapsed;
    }

    /// <summary>A message is showing (checked by the capture mode).</summary>
    public bool MessageShowing => Toast.Visibility == Visibility.Visible;

    private int _toastNumber;

    // ------------------------------------------------------------------ closing, placement

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_editor != null && !_editor.ConfirmClose())
        {
            e.Cancel = true;
            return;
        }
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (bounds.Width > 0 && bounds.Height > 0 && !IsTestOffscreen)
        {
            AppSettings.Update(s => s.Window = new WindowPlacement
            {
                Left = bounds.Left,
                Top = bounds.Top,
                Width = bounds.Width,
                Height = bounds.Height,
                Maximized = WindowState == WindowState.Maximized,
            });
        }
    }

    private void RestorePlacement()
    {
        // Hidden test hook (installer and association tests): open off screen, without taking the focus.
        if (IsTestOffscreen)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = SystemParameters.VirtualScreenLeft - 3200;
            Top = SystemParameters.VirtualScreenTop + 20;
            ShowActivated = false;
            ShowInTaskbar = false;
            return;
        }
        var placement = AppSettings.Current.Window;
        if (placement == null || placement.Width < MinWidth || placement.Height < MinHeight)
        {
            FitFirstStart();
            return;
        }
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var rect = new Rect(placement.Left, placement.Top, placement.Width, placement.Height);
        if (!screen.IntersectsWith(rect) || rect.Top < screen.Top - 8)
        {
            FitFirstStart();
            return;
        }
        // Cascade windows opened after the first so they don't sit exactly on top of each other.
        var offset = App.Current.DocumentWindows.Count(w => w != this && w.IsVisible) * 28;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = placement.Left + offset;
        Top = placement.Top + offset;
        Width = placement.Width;
        Height = placement.Height;
        if (placement.Maximized) WindowState = WindowState.Maximized;
    }

    /// <summary>
    /// First start: the preferred size shrinks to fit the main display's work area, centred on it, so the title
    /// bar is never above the top of the screen. <see cref="Controls.FluentWindow.KeepOnScreen"/> then checks the real display.
    /// </summary>
    private void FitFirstStart()
    {
        var work = SystemParameters.WorkArea;
        if (work.Width <= 0 || work.Height <= 0) return;
        var (bounds, maximize) = Core.Editing.ScreenFit.FirstStart(new Size(Width, Height), new Size(MinWidth, MinHeight), work);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
        if (maximize) WindowState = WindowState.Maximized;
    }

    // ------------------------------------------------------------------ updates

    private void OnUpdateChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(OnUpdateChanged));
            return;
        }
        var manifest = UpdateService.Available;
        if (manifest == null)
        {
            if (!UpdateService.IsDownloading) UpdateBar.Visibility = Visibility.Collapsed;
            return;
        }
        UpdateHeading.Text = $"Inkwell {manifest.Version} is available";
        var subtitle = $"You have {Updater.CurrentVersionText}";
        if (DateTime.TryParseExact(manifest.Released, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var released))
            subtitle += "  ·  released " + released.ToString("d MMM yyyy", System.Globalization.CultureInfo.CurrentCulture);
        UpdateSubtitle.Text = subtitle;
        UpdateNotes.Items.Clear();
        foreach (var note in manifest.Notes.Take(6))
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
            var bullet = new TextBlock { Text = "•", Margin = new Thickness(2, 0, 9, 0), Style = (Style)FindResource("Text.BodySecondary") };
            DockPanel.SetDock(bullet, Dock.Left);
            row.Children.Add(bullet);
            row.Children.Add(new TextBlock { Text = note, Style = (Style)FindResource("Text.Body") });
            UpdateNotes.Items.Add(row);
        }
        UpdateNotes.Visibility = UpdateNotes.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateNowButton.Content = UpdateService.CanInstall(manifest) ? "Update now" : "Download page";
        UpdateError.Visibility = Visibility.Collapsed;
        SetUpdateBusy(false);
        if (UpdateBar.Visibility != Visibility.Visible)
        {
            UpdateBar.Visibility = Visibility.Visible;
            UpdateBar.Opacity = 0;
            UpdateBar.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
        }
    }

    private void SetUpdateBusy(bool busy)
    {
        UpdateButtons.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        UpdateProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnUpdateNow(object sender, RoutedEventArgs e)
    {
        var manifest = UpdateService.Available;
        if (manifest == null) return;
        if (!UpdateService.CanInstall(manifest))
        {
            UpdateService.OpenInBrowser(string.IsNullOrWhiteSpace(manifest.Page) ? UpdateService.DownloadPageUrl : manifest.Page!);
            UpdateService.Later();
            return;
        }
        // Everything must be saved first: the installer closes Inkwell.
        foreach (var w in App.Current.DocumentWindows.ToList())
        {
            if (w.Editor == null || !w.Session!.IsDirty) continue;
            if (!w.Editor.ConfirmClose()) return;
        }

        UpdateError.Visibility = Visibility.Collapsed;
        SetUpdateBusy(true);
        var sizeMb = (manifest.ForThisEdition?.Size ?? 0) / (1024.0 * 1024.0);
        UpdateProgressText.Text = $"Downloading Inkwell {manifest.Version}…  0%  ({sizeMb:0.0} MB)";
        try
        {
            var progress = new Progress<double>(f =>
            {
                UpdateProgress.Value = f;
                UpdateProgressText.Text = f >= 1
                    ? "Verified. Installing… Inkwell restarts by itself."
                    : $"Downloading Inkwell {manifest.Version}…  {f * 100:0}%  ({sizeMb:0.0} MB)";
            });
            await UpdateService.UpdateNowAsync(progress);
        }
        catch (OperationCanceledException)
        {
            SetUpdateBusy(false);
            UpdateError.Text = "Download cancelled.";
            UpdateError.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Update download / install");
            SetUpdateBusy(false);
            UpdateNowButton.Content = "Try again";
            UpdateError.Text = ex is UpdateVerificationException ? ex.Message + " The file was deleted." : "Couldn't download the update: " + ex.Message;
            UpdateError.Visibility = Visibility.Visible;
        }
    }

    private void OnUpdateLater(object sender, RoutedEventArgs e) => UpdateService.Later();

    private void OnUpdateSkip(object sender, RoutedEventArgs e) => UpdateService.Skip();

    private void OnUpdateCancel(object sender, RoutedEventArgs e) => UpdateService.CancelDownload();
}
