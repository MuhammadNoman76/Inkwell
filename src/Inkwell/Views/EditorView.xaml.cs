using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Inkwell.Core.Editing;
using Inkwell.Core.Layout;
using Inkwell.Core.Model;
using Inkwell.Services;
using Inkwell.Themes;
using Inkwell.Views.Editing;
using Inkwell.Views.Panels;

namespace Inkwell.Views;

/// <summary>
/// One document in the editor: the slim command bar, the outline on the left, the pages in the middle, the
/// contextual panel on the right and the status bar. Commands live in the partial files.
/// </summary>
public partial class EditorView
{
    private readonly MainWindow _window;
    private readonly DispatcherTimer _stateTimer;
    private readonly DispatcherTimer _autoSaveTimer;
    private readonly DispatcherTimer _recoveryTimer;
    private bool _updatingState;
    private bool _detached;

    public EditorView(MainWindow window, DocumentSession session)
    {
        _window = window;
        Session = session;
        InitializeComponent();

        var settings = AppSettings.Current;
        session.Editor.Author = settings.Author;
        var view = new ViewOptions
        {
            DarkPaper = ThemeManager.IsDark && settings.DarkPaper,
            ShowFormattingMarks = settings.ShowFormattingMarks,
        };
        Layout = new DocumentLayout(session.Document, view) { FileName = session.FilePath != null ? Path.GetFileName(session.FilePath) : null };
        Layout.LayoutAll();
        Doc.ShowFormattingMarks = settings.ShowFormattingMarks;
        Doc.TypewriterScrolling = settings.TypewriterScrolling;
        Doc.Attach(session.Editor, Layout);
        ApplyTheme();

        Outline = new OutlinePane(this);
        OutlineHost.Child = Outline;
        Panel = new ContextPanel(this);
        PanelHost.Child = Panel;
        Find = new FindBar(this);
        FindHost.Child = Find;
        SetOutlineVisible(settings.ShowOutline);
        SetPanelVisible(settings.ShowPanel);

        Editor.Changed += OnDocumentChanged;
        Editor.SelectionChanged += OnSelectionChanged;
        Doc.ViewChanged += OnViewChanged;
        Doc.LayoutUpdated2 += () => ScheduleState();
        Doc.Refused += message => _window.ShowMessage(message, 5, warning: true);
        Doc.LinkClicked += (link, _) => FollowLink(link);
        Doc.TocEntryClicked += JumpFromToc;
        Doc.ContextMenuRequested += ShowContextMenu;
        Doc.HeaderFooterRequested += EditHeaderFooter;
        Doc.DrawingMoved += OnDrawingMoved;
        Doc.DrawingResized += (d, size) => Run(() => Editor.ResizeDrawing(d, size));
        Doc.ColumnResized += (t, edge, delta) => Run(() => Editor.ResizeColumn(t, edge, delta));
        Doc.DrawingSelectionChanged += () => ScheduleState();
        Doc.TabPressed = OnTab;
        Doc.BeforeTextInput = BeforeTyping;
        Doc.AfterTextInput = AfterTyping;
        Doc.Squiggles = SpellingErrors;
        ThemeManager.Changed += OnThemeChanged;
        AppSettings.Changed += OnSettingsChanged;
        Scroller.PreviewMouseWheel += OnScrollerWheel;

        SizeBox.ValueChanged += size => Run(() => Editor.SetFontSize(size));
        PreviewKeyDown += OnPreviewKey;
        CommandBar.SizeChanged += (_, _) => Dispatcher.BeginInvoke(new Action(FitCommandBar), DispatcherPriority.Loaded);
        AllowDrop = true;
        Drop += OnDrop;
        DragOver += OnDragOver;

        _stateTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(60) };
        _stateTimer.Tick += (_, _) =>
        {
            _stateTimer.Stop();
            RefreshState();
        };
        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _autoSaveTimer.Tick += (_, _) => AutoSaveTick();
        _autoSaveTimer.Start();
        _recoveryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _recoveryTimer.Tick += (_, _) => RecoveryTick();
        _recoveryTimer.Start();

        Loaded += (_, _) =>
        {
            if (Math.Abs(settings.Zoom - 1) > 0.01) Doc.Zoom = settings.Zoom;
            else Dispatcher.BeginInvoke(new Action(FitWidthIfSmall), DispatcherPriority.Loaded);
            if (!MainWindow.IsTestOffscreen) Keyboard.Focus(Doc);
            RefreshState();
        };
        StartSpelling();
        RefreshState();
    }

    public DocumentSession Session { get; }

    public DocEditor Editor => Session.Editor;

    public DocumentLayout Layout { get; }

    public OutlinePane Outline { get; }

    public ContextPanel Panel { get; }

    public FindBar Find { get; }

    public MainWindow Window => _window;

    public DocumentView DocumentView => Doc;

    /// <summary>Runs an action, turning refused edits into a message instead of an error.</summary>
    public void Run(Action action)
    {
        try
        {
            action();
        }
        catch (EditRefusedException ex)
        {
            _window.ShowMessage(ex.Message, 5, warning: true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Command");
            _window.ShowMessage("That didn't work: " + ex.Message, 6, warning: true);
        }
    }

    public void Detach()
    {
        if (_detached) return;
        _detached = true;
        _stateTimer.Stop();
        _autoSaveTimer.Stop();
        _recoveryTimer.Stop();
        StopSpelling();
        Editor.Changed -= OnDocumentChanged;
        Editor.SelectionChanged -= OnSelectionChanged;
        ThemeManager.Changed -= OnThemeChanged;
        AppSettings.Changed -= OnSettingsChanged;
        Doc.Detach();
        Layout.Dispose();
        if (!Session.IsDirty) Recovery.Discard(Session.RecoveryKey);
    }

    private void FitWidthIfSmall()
    {
        // Small windows start at the page width; large ones at 100 %.
        if (Layout.Pages.Count == 0) return;
        var pageWidth = Layout.Pages[0].Size.Width;
        if (Doc.ActualWidth > 0 && Doc.ActualWidth < pageWidth + 2 * DocumentView.CanvasPadding) Doc.ZoomToPageWidth();
    }

    // ------------------------------------------------------------------ theme

    private void OnThemeChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(OnThemeChanged));
            return;
        }
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        Doc.CanvasBrush = (Brush)FindResource("Canvas.Background");
        Doc.SelectionColor = Color.FromArgb(0x66, ThemeManager.AccentLight2.R, ThemeManager.AccentLight2.G, ThemeManager.AccentLight2.B);
        Doc.AccentColor = ThemeManager.IsDark ? ThemeManager.AccentLight2 : ThemeManager.AccentDark1;
        var dark = ThemeManager.IsDark && AppSettings.Current.DarkPaper;
        if (Layout.View.DarkPaper != dark)
        {
            Layout.View.DarkPaper = dark;
            Doc.RelayoutAll();
        }
        Doc.InvalidateVisual();
        Doc.Redraw();
    }

    private void OnSettingsChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(OnSettingsChanged));
            return;
        }
        Editor.Author = AppSettings.Current.Author;
        ApplyTheme();
        if (!AppSettings.Current.SpellCheck) _spellErrors.Clear();
        Doc.Redraw();
    }

    // ------------------------------------------------------------------ state

    private void OnDocumentChanged()
    {
        ScheduleState();
        _window.UpdateTitle();
        _lastEdit = DateTime.UtcNow;
        HideChips();
        InvalidateSpelling();
        Outline.Invalidate();
    }

    private void OnSelectionChanged()
    {
        ScheduleState();
    }

    private void OnViewChanged()
    {
        ZoomText.Content = Math.Round(Doc.Zoom * 100).ToString(CultureInfo.CurrentCulture) + "%";
        if (Math.Abs(ZoomSlider.Value - Doc.Zoom * 100) > 0.5)
        {
            _updatingState = true;
            ZoomSlider.Value = Doc.Zoom * 100;
            _updatingState = false;
        }
        UpdatePageStatus();
        PositionChips();
    }

    public void ScheduleState()
    {
        if (_detached) return;
        _stateTimer.Stop();
        _stateTimer.Start();
    }

    /// <summary>Brings the command bar, panel and status bar up to date with the selection.</summary>
    public void RefreshState()
    {
        if (_detached) return;
        _updatingState = true;
        try
        {
            var f = Editor.GetFormatState();
            BoldToggle.IsChecked = f.Bold == true;
            ItalicToggle.IsChecked = f.Italic == true;
            UnderlineToggle.IsChecked = f.Underline == true;
            BulletsToggle.IsChecked = f.Bullets == true;
            NumbersToggle.IsChecked = f.Numbers == true;
            var style = f.ParagraphStyle != null ? Session.Document.Styles.Get(f.ParagraphStyle) : null;
            StyleName.Text = style != null ? DocEditor.DisplayName(style) : "Mixed";
            FontName.Text = f.Font ?? "Mixed fonts";
            if (f.Size is { } size) SizeBox.Value = size;
            else SizeBox.SetMixed();
            AlignButton.SetValue(Controls.Ui.IconProperty, TryFindResource(f.Alignment switch
            {
                "center" => "Icon.TextAlignCenter",
                "right" or "end" => "Icon.TextAlignRight",
                "both" or "distribute" => "Icon.TextAlignJustify",
                _ => f.Rtl == true ? "Icon.TextAlignRight" : "Icon.TextAlignLeft",
            }) as Geometry);
            UndoButton.IsEnabled = Editor.History.CanUndo;
            RedoButton.IsEnabled = Editor.History.CanRedo;
            UndoButton.ToolTip = Editor.History.UndoLabel is { } u ? $"Undo {u.ToLowerInvariant()} (Ctrl+Z)" : "Undo (Ctrl+Z)";
            RedoButton.ToolTip = Editor.History.RedoLabel is { } r ? $"Redo {r.ToLowerInvariant()} (Ctrl+Y)" : "Redo (Ctrl+Y)";
            DocName.Text = Session.DisplayName;
            UpdateSaveState();
            ReviewToggle.IsChecked = Panel.Mode == PanelMode.Review;
            TrackStatus.Visibility = Editor.TrackChanges ? Visibility.Visible : Visibility.Collapsed;
            UpdatePageStatus();
            UpdateWordStatus();
            UpdateCommentHighlights();
            UpdateLanguage();
            Panel.Refresh();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Refresh state");
        }
        finally
        {
            _updatingState = false;
        }
    }

    private void UpdatePageStatus()
    {
        var page = Doc.CurrentPage;
        if (Layout.View.Mode == ViewMode.Pageless)
        {
            PageStatus.Text = "Pageless";
            return;
        }
        PageStatus.Text = page == null ? "" : $"Page {page.Index + 1} of {Layout.Pages.Count}";
    }

    private void UpdateLanguage()
    {
        try
        {
            var p = Editor.Selection.Active.Para;
            var run = Editor.Styles.ResolveRun(p.PropsForTypingAt(Editor.Selection.Active.Offset), p);
            var rtl = Editor.Styles.ResolvePara(p).Bidi;
            var tag = rtl ? run.LangBidi : run.Lang;
            var culture = RunPropsFactory.Culture(tag);
            LanguageStatus.Text = (string.IsNullOrEmpty(culture.Name) ? tag : culture.DisplayName) + (rtl ? " · Right to left" : "");
        }
        catch
        {
            LanguageStatus.Text = "";
        }
    }

    // ------------------------------------------------------------------ panels

    public void SetOutlineVisible(bool visible)
    {
        OutlineColumn.Width = visible ? new GridLength(236) : new GridLength(0);
        OutlineHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible) Outline.Invalidate();
    }

    public bool OutlineVisible => OutlineHost.Visibility == Visibility.Visible;

    public void SetPanelVisible(bool visible)
    {
        PanelColumn.Width = visible ? new GridLength(332) : new GridLength(0);
        PanelHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PanelToggle.IsChecked = visible;
    }

    public bool PanelVisible => PanelHost.Visibility == Visibility.Visible;

    public void ShowPanel(PanelMode mode)
    {
        SetPanelVisible(true);
        Panel.Mode = mode;
        RefreshState();
    }

    private void OnPanelToggle(object sender, RoutedEventArgs e)
    {
        var visible = !PanelVisible;
        SetPanelVisible(visible);
        AppSettings.Update(s => s.ShowPanel = visible);
    }

    private void OnReviewToggle(object sender, RoutedEventArgs e)
    {
        if (Panel.Mode == PanelMode.Review && PanelVisible) Panel.Mode = PanelMode.Auto;
        else ShowPanel(PanelMode.Review);
        RefreshState();
    }

    // ------------------------------------------------------------------ view modes and zoom

    private void OnViewModeChanged(object sender, RoutedEventArgs e)
    {
        if (Layout == null || _updatingState) return;
        var mode = ViewPageless.IsChecked == true ? ViewMode.Pageless : ViewMode.Pages;
        SetViewMode(mode);
    }

    public void SetViewMode(ViewMode mode)
    {
        if (Layout.View.Mode == mode) return;
        Layout.View.Mode = mode;
        _updatingState = true;
        ViewPages.IsChecked = mode == ViewMode.Pages;
        ViewPageless.IsChecked = mode == ViewMode.Pageless;
        _updatingState = false;
        if (Doc.EditingHeaderFooter) Doc.ExitHeaderFooter();
        Doc.RelayoutAll();
        Doc.ScrollToCaret(center: true);
        RefreshState();
    }

    private bool _focusMode;

    private void OnFocusToggle(object sender, RoutedEventArgs e) => SetFocusMode(!_focusMode);

    /// <summary>Focus mode: everything but the text hides, the current paragraph stays bright, the line stays centred.</summary>
    public void SetFocusMode(bool on)
    {
        _focusMode = on;
        FocusToggle.IsChecked = on;
        Doc.FocusMode = on;
        Doc.TypewriterScrolling = on || AppSettings.Current.TypewriterScrolling;
        CommandBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        StatusBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        SetOutlineVisible(!on && AppSettings.Current.ShowOutline);
        SetPanelVisible(!on && AppSettings.Current.ShowPanel);
        CenterLayer.CornerRadius = on ? new CornerRadius(0) : new CornerRadius(8, 8, 0, 0);
        CenterLayer.BorderThickness = on ? new Thickness(0) : new Thickness(1, 1, 1, 0);
        if (on) _window.ShowMessage("Focus mode. Press Esc or F11 to come back.", 3);
        Doc.Redraw();
        Doc.ScrollToCaret(center: true);
        if (!MainWindow.IsTestOffscreen) Keyboard.Focus(Doc);
    }

    public bool FocusMode => _focusMode;

    private void OnZoomSlider(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingState || Layout == null) return;
        Doc.Zoom = e.NewValue / 100.0;
        SaveZoom();
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => ZoomBy(1.1);

    private void OnZoomOut(object sender, RoutedEventArgs e) => ZoomBy(1 / 1.1);

    public void ZoomBy(double factor)
    {
        Doc.MultiplePages = false;
        Doc.Zoom = Math.Round(Doc.Zoom * factor, 2);
        SaveZoom();
    }

    private void OnZoom100(object sender, RoutedEventArgs e)
    {
        Doc.MultiplePages = false;
        Doc.Zoom = 1;
        SaveZoom();
    }

    private void OnZoomFit(object sender, RoutedEventArgs e)
    {
        Doc.ZoomToPageWidth();
        SaveZoom();
    }

    private void OnMultiplePages(object sender, RoutedEventArgs e)
    {
        if (Doc.MultiplePages)
        {
            Doc.MultiplePages = false;
            Doc.Zoom = 1;
        }
        else Doc.ShowMultiplePages();
    }

    private void SaveZoom()
    {
        var zoom = Doc.Zoom;
        AppSettings.Update(s => s.Zoom = zoom);
    }

    private void OnScrollerWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        ZoomBy(e.Delta > 0 ? 1.1 : 1 / 1.1);
        e.Handled = true;
    }

    // ------------------------------------------------------------------ simple button handlers

    private void OnUndo(object sender, RoutedEventArgs e) => Undo();

    private void OnRedo(object sender, RoutedEventArgs e) => Redo();

    public void Undo()
    {
        Doc.SelectDrawing(null);
        Editor.Undo();
        FocusDocument();
    }

    public void Redo()
    {
        Doc.SelectDrawing(null);
        Editor.Redo();
        FocusDocument();
    }

    public void FocusDocument()
    {
        if (!MainWindow.IsTestOffscreen) Keyboard.Focus(Doc);
    }

    private void OnBold(object sender, RoutedEventArgs e)
    {
        Run(Editor.ToggleBold);
        FocusDocument();
    }

    private void OnItalic(object sender, RoutedEventArgs e)
    {
        Run(Editor.ToggleItalic);
        FocusDocument();
    }

    private void OnUnderline(object sender, RoutedEventArgs e)
    {
        Run(Editor.ToggleUnderline);
        FocusDocument();
    }

    private void OnBullets(object sender, RoutedEventArgs e)
    {
        Run(() => Editor.ToggleList(true));
        FocusDocument();
    }

    private void OnNumbers(object sender, RoutedEventArgs e)
    {
        Run(() => Editor.ToggleList(false));
        FocusDocument();
    }

    private void OnPalette(object sender, RoutedEventArgs e) => OpenPalette();
}

public partial class EditorView
{
    /// <summary>
    /// Keeps the command bar on one line in narrower windows: first the quick insert buttons go (they're in the
    /// Insert menu too), then the search box shrinks to its icon and shortcut, then the document name gets shorter.
    /// </summary>
    private bool _fitting;

    private void FitCommandBar()
    {
        if (_fitting || CommandBar.ActualWidth <= 0) return;
        _fitting = true;
        try
        {
            QuickInsert.Visibility = Visibility.Visible;
            PaletteButton.Width = 176;
            PaletteButton.Content = "Search commands";
            DocNamePanel.MaxWidth = 190;
            // The middle part scrolls sideways when it doesn't fit: that's the overflow to remove.
            bool Fits()
            {
                CommandBar.UpdateLayout();
                FormatBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                return FormatBar.DesiredSize.Width <= BarScroller.ActualWidth + 0.5;
            }
            if (Fits()) return;
            QuickInsert.Visibility = Visibility.Collapsed;
            if (Fits()) return;
            PaletteButton.Content = null;
            PaletteButton.Width = 96;
            if (Fits()) return;
            DocNamePanel.MaxWidth = 110;
        }
        finally
        {
            _fitting = false;
        }
    }
}
