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
        Doc.AnchorMoved += (d, p) => Run(() => Editor.MoveAnchor(d, p));
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
        ApplyShortcutTips();
        BuildPaletteButton();
        CommandBar.SizeChanged += (_, _) => ScheduleFit();
        BarLeft.SizeChanged += (_, e) => { if (e.WidthChanged) ScheduleFit(); };
        FormatBar.SizeChanged += (_, e) => { if (e.WidthChanged) ScheduleFit(); };
        StatusBar.SizeChanged += (_, _) => ScheduleStatusFit();
        foreach (var part in new FrameworkElement[] { PageStatus, WordStatus, GoalText, TrackStatus, LanguageStatus })
            part.SizeChanged += (_, e) => { if (e.WidthChanged) ScheduleStatusFit(); };
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
            UndoButton.ToolTip = Shortcuts.Tip(Editor.History.UndoLabel is { } u ? "Undo " + u.ToLowerInvariant() : "Undo", Shortcuts.Undo);
            RedoButton.ToolTip = Shortcuts.Tip(Editor.History.RedoLabel is { } r ? "Redo " + r.ToLowerInvariant() : "Redo", Shortcuts.Redo);
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
            PageStatus.ToolTip = null;
            return;
        }
        var full = page == null ? "" : $"Page {page.Index + 1} of {Layout.Pages.Count}";
        PageStatus.Text = page != null && _statusLevel >= 3 ? $"{page.Index + 1} / {Layout.Pages.Count}" : full;
        PageStatus.ToolTip = full.Length > 0 ? full : null;
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
        _window.HideMessage(); // a message about the previous view doesn't belong to this one
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
        if (on) _window.ShowMessage($"Focus mode. Press Esc or {Shortcuts.FocusMode.Text} to come back.", 3);
        else _window.HideMessage();
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
    /// <summary>The tooltips that name a shortcut take it from <see cref="Shortcuts"/>.</summary>
    private void ApplyShortcutTips()
    {
        MenuButton.ToolTip = Shortcuts.Tip("Menu", Shortcuts.Menu);
        SizeBox.ToolTip = Shortcuts.Tip("Font size", Shortcuts.BiggerText, Shortcuts.SmallerText);
        BoldToggle.ToolTip = Shortcuts.Tip("Bold", Shortcuts.Bold);
        ItalicToggle.ToolTip = Shortcuts.Tip("Italic", Shortcuts.Italic);
        UnderlineToggle.ToolTip = Shortcuts.Tip("Underline", Shortcuts.Underline);
        BulletsToggle.ToolTip = Shortcuts.Tip("Bullets", Shortcuts.Bullets);
        LinkButton.ToolTip = Shortcuts.Tip("Insert a link", Shortcuts.Link);
        CommentButton.ToolTip = Shortcuts.Tip("Add a comment", Shortcuts.Comment);
        PaletteButton.ToolTip = Shortcuts.Tip("Find any command, style, template or heading", Shortcuts.CommandPalette);
        PanelToggle.ToolTip = Shortcuts.Tip("Show or hide the panel", Shortcuts.Panel);
        ShareButton.ToolTip = Shortcuts.Tip("Export as PDF, Markdown, HTML or text, or print", Shortcuts.Export);
        FocusToggle.ToolTip = Shortcuts.Tip("Focus mode", Shortcuts.FocusMode);
        FitWidthButton.ToolTip = Shortcuts.Tip("Fit the page width", Shortcuts.FitPageWidth);
        ZoomOutButton.ToolTip = Shortcuts.Tip("Zoom out", Shortcuts.ZoomOut);
        ZoomInButton.ToolTip = Shortcuts.Tip("Zoom in", Shortcuts.ZoomIn);
        ZoomSlider.ToolTip = "Zoom (Ctrl + mouse wheel zooms too)";
    }

    private TextBlock _paletteLabel = null!;
    private Border _paletteBadge = null!;

    /// <summary>The search box: magnifier, "Search commands" and the shortcut badge (shortened by <see cref="FitCommandBar"/>).</summary>
    private void BuildPaletteButton()
    {
        var icon = new Controls.IconView { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(Controls.IconView.DataProperty, "Icon.Search");
        _paletteLabel = new TextBlock { Text = "Search commands", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var badgeText = new TextBlock { Text = Shortcuts.CommandPalette.Text, FontSize = 11 };
        badgeText.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
        _paletteBadge = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(7, 1, 7, 2), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = badgeText };
        _paletteBadge.SetResourceReference(Border.BackgroundProperty, "Subtle.FillSecondary");
        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        stack.Children.Add(icon);
        stack.Children.Add(_paletteLabel);
        stack.Children.Add(_paletteBadge);
        PaletteButton.Content = stack;
    }

    // ------------------------------------------------------------------ fitting the command bar and the status bar

    private StepFitter? _barFitter;
    private StepFitter? _statusFitter;

    private StepFitter BarFitter => _barFitter ??= new StepFitter(CommandBar, MaxCommandBarLevel, SetBarLevel, () =>
    {
        // The middle part scrolls sideways when it doesn't fit: that's the overflow to remove.
        FormatBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return FormatBar.DesiredSize.Width <= BarScroller.ActualWidth + 0.5;
    });

    private StepFitter StatusFitter => _statusFitter ??= new StepFitter(StatusBar, MaxStatusBarLevel, SetStatusLevel, () =>
    {
        StatusLeft.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // A panel arranged narrower than it wants keeps its own width and is clipped: compare with the room it got.
        return StatusLeft.DesiredSize.Width <= System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(StatusLeft).Width + 0.5;
    });

    /// <summary>How far the command bar is shortened (0 = everything shows; see <see cref="SetBarLevel"/>).</summary>
    public int CommandBarLevel => BarFitter.Level;

    public const int MaxCommandBarLevel = 8;

    public const int MaxStatusBarLevel = 4;

    private void ScheduleFit() => BarFitter.Schedule();

    private void ScheduleStatusFit() => StatusFitter.Schedule();

    /// <summary>
    /// Keeps the command bar on one line at every window width, from the narrowest window to 4K: whole steps from
    /// <see cref="SetBarLevel"/> until everything fits, so no label is ever cut off part-way.
    /// </summary>
    public void FitCommandBar() => BarFitter.Fit();

    /// <summary>The same for the status bar, which gets narrow when the outline and the panel are both open.</summary>
    public void FitStatusBar() => StatusFitter.Fit();

    /// <summary>
    /// The steps, from the full command bar to the most compact: 1 "Search" instead of "Search commands"; 2 the quick
    /// insert buttons go (they're in the Insert menu too); 3 the search box shows its magnifier and shortcut only;
    /// 4 a shorter document name; 5 Insert and Export become icons (their tooltips say what they are); 6 the search
    /// box is just the magnifier (its tooltip has the shortcut); 7 the style and font boxes are as wide as their
    /// names; 8 a shorter document name still.
    /// </summary>
    private void SetBarLevel(int level)
    {
        _paletteLabel.Text = level >= 1 ? "Search" : "Search commands";
        _paletteLabel.Visibility = level >= 3 ? Visibility.Collapsed : Visibility.Visible;
        _paletteBadge.Visibility = level >= 6 ? Visibility.Collapsed : Visibility.Visible;
        _paletteBadge.Margin = new Thickness(level >= 3 ? 8 : 10, 0, 0, 0);
        PaletteButton.Padding = level >= 6 ? new Thickness(9, 0, 9, 0) : new Thickness(12, 0, 6, 0);
        QuickInsert.Visibility = level >= 2 ? Visibility.Collapsed : Visibility.Visible;
        DocNamePanel.MaxWidth = level >= 8 ? 76 : level >= 4 ? 110 : 190;
        var iconsOnly = level >= 5;
        InsertButton.Content = iconsOnly ? "" : "Insert";
        InsertButton.Padding = iconsOnly ? new Thickness(10, 0, 10, 0) : new Thickness(10, 0, 12, 0);
        ShareButton.Content = iconsOnly ? "" : "Export";
        ShareButton.Padding = iconsOnly ? new Thickness(10, 0, 10, 0) : new Thickness(10, 0, 12, 0);
        var narrow = level >= 7;
        StyleButton.MinWidth = narrow ? 0 : 100;
        StyleName.MaxWidth = narrow ? 84 : 110;
        FontButton.MinWidth = narrow ? 0 : 100;
        FontName.MaxWidth = narrow ? 84 : 120;
    }

    private int _statusLevel;

    /// <summary>
    /// The status bar's steps: 1 the language goes; 2 the zoom slider
    /// goes (the zoom buttons and the percentage stay); 3 "3 / 12" instead of "Page 3 of 12"; 4 the "several pages"
    /// and "fit the page width" buttons go (the View commands in the palette have them).
    /// </summary>
    private void SetStatusLevel(int level)
    {
        _statusLevel = level;
        LanguageStatus.Visibility = level >= 1 ? Visibility.Collapsed : Visibility.Visible;
        ZoomSlider.Visibility = level >= 2 ? Visibility.Collapsed : Visibility.Visible;
        UpdatePageStatus();
        MultiPageButton.Visibility = level >= 4 ? Visibility.Collapsed : Visibility.Visible;
        FitWidthButton.Visibility = level >= 4 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Shortens a strip of controls in whole steps until it fits: each fit starts from the full strip, applies step 1,
    /// 2… and stops at the first that fits. Size changes during a fit are its own and don't start another.
    /// </summary>
    private sealed class StepFitter
    {
        private readonly FrameworkElement _strip;
        private readonly int _maxLevel;
        private readonly Action<int> _apply;
        private readonly Func<bool> _fits;
        private bool _fitting;
        private bool _queued;

        public StepFitter(FrameworkElement strip, int maxLevel, Action<int> apply, Func<bool> fits)
        {
            _strip = strip;
            _maxLevel = maxLevel;
            _apply = apply;
            _fits = fits;
        }

        public int Level { get; private set; }

        public void Schedule()
        {
            if (_fitting || _queued) return;
            _queued = true;
            _strip.Dispatcher.BeginInvoke(new Action(() =>
            {
                _queued = false;
                Fit();
            }), DispatcherPriority.Loaded);
        }

        public void Fit()
        {
            if (_fitting || _strip.ActualWidth <= 0 || !_strip.IsVisible) return;
            _fitting = true;
            try
            {
                bool Fits()
                {
                    _strip.UpdateLayout();
                    return _fits();
                }
                var level = 0;
                _apply(level);
                while (!Fits() && level < _maxLevel) _apply(++level);
                Level = level;
                // Lay out the final state now, while the size changes it causes are known to be ours.
                _strip.UpdateLayout();
            }
            finally
            {
                _fitting = false;
            }
        }
    }
}
