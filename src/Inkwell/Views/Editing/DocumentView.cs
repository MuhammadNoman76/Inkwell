using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Inkwell.Core.Editing;
using Inkwell.Core.Layout;
using Inkwell.Core.Model;
using Inkwell.Core.Rendering;

namespace Inkwell.Views.Editing;

/// <summary>
/// The document surface: pages laid out by Inkwell's engine, drawn as cached drawings (only the visible pages),
/// with its own caret, selection, hit testing, keyboard and IME input, and a UI Automation text provider.
/// Hosted in a ScrollViewer (IScrollInfo); zoom scales everything.
/// </summary>
public sealed partial class DocumentView : FrameworkElement, IScrollInfo
{
    public const double PageGap = 28;
    public const double CanvasPadding = 36;

    private readonly VisualCollection _visuals;
    private readonly ContainerVisual _pagesLayer = new();
    private readonly DrawingVisual _overlay = new();
    private readonly DrawingVisual _caretVisual = new();
    private readonly Dictionary<PageLayout, PageVisual> _pageVisuals = new(ReferenceEqualityComparer<PageLayout>.Instance);
    private readonly DispatcherTimer _caretTimer;
    private List<Rect> _pageRects = new();
    private Size _extent;
    private Size _viewport;
    private Vector _offset;
    private double _zoom = 1.0;
    private bool _caretOn = true;

    public DocumentView()
    {
        _visuals = new VisualCollection(this) { _pagesLayer, _overlay, _caretVisual };
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        Cursor = System.Windows.Input.Cursors.IBeam;
        SnapsToDevicePixels = false;
        _caretTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(530) };
        _caretTimer.Tick += (_, _) =>
        {
            _caretOn = !_caretOn;
            _caretVisual.Opacity = _caretOn && IsKeyboardFocusWithin ? 1 : 0;
        };
        GotKeyboardFocus += (_, _) =>
        {
            ResetCaretBlink();
            OnFocusIme();
        };
        LostKeyboardFocus += (_, _) =>
        {
            _caretVisual.Opacity = 0;
            OnBlurIme();
            DrawOverlay();
        };
        System.Windows.Input.InputMethod.SetIsInputMethodSuspended(this, true);
        System.Windows.Input.InputMethod.SetIsInputMethodEnabled(this, true);
        Loaded += (_, _) => _caretTimer.Start();
        Unloaded += (_, _) => _caretTimer.Stop();
        InitInput();
    }

    public DocEditor? Editor { get; private set; }
    public DocumentLayout? Layout { get; private set; }

    /// <summary>Raised when zoom, scroll position or the visible page changed.</summary>
    public event Action? ViewChanged;

    /// <summary>Raised after the layout was brought up to date (page count, word count may have changed).</summary>
    public event Action? LayoutUpdated2;

    /// <summary>Canvas colour behind the pages (theme-dependent; set by the editor).</summary>
    public Brush CanvasBrush { get; set; } = Brushes.LightGray;

    /// <summary>The colour of the selection highlight.</summary>
    public Color SelectionColor { get; set; } = Color.FromArgb(0x55, 0x33, 0x88, 0xFF);

    public Color AccentColor { get; set; } = Color.FromRgb(0x00, 0x67, 0xC0);

    public bool ShowFormattingMarks { get; set; }

    /// <summary>Focus mode: everything but the paragraph you're writing is dimmed.</summary>
    public bool FocusMode { get; set; }

    /// <summary>Typewriter scrolling: the line you type on stays in the middle of the window.</summary>
    public bool TypewriterScrolling { get; set; }

    /// <summary>Several pages side by side when they fit.</summary>
    public bool MultiplePages { get; set; }

    public void Attach(DocEditor editor, DocumentLayout layout)
    {
        if (Editor != null)
        {
            Editor.Changed -= OnDocumentChanged;
            Editor.SelectionChanged -= OnSelectionChanged;
        }
        Editor = editor;
        Layout = layout;
        editor.Changed += OnDocumentChanged;
        editor.SelectionChanged += OnSelectionChanged;
        _pageVisuals.Clear();
        _pagesLayer.Children.Clear();
        if (layout.Pages.Count == 0) layout.LayoutAll();
        Arrange();
        Redraw();
        AutomationChanged();
    }

    public void Detach()
    {
        if (Editor != null)
        {
            Editor.Changed -= OnDocumentChanged;
            Editor.SelectionChanged -= OnSelectionChanged;
        }
        _caretTimer.Stop();
        Editor = null;
        Layout = null;
        _pageVisuals.Clear();
        _pagesLayer.Children.Clear();
    }

    // ------------------------------------------------------------------ zoom

    public double Zoom
    {
        get => _zoom;
        set
        {
            var z = MathEx.Clamp(value, 0.25, 5.0);
            if (Math.Abs(z - _zoom) < 0.0001) return;
            // Keep the centre of the viewport where it is.
            var center = new Point((_offset.X + _viewport.Width / 2) / _zoom, (_offset.Y + _viewport.Height / 2) / _zoom);
            _zoom = z;
            Arrange();
            SetOffsets(center.X * _zoom - _viewport.Width / 2, center.Y * _zoom - _viewport.Height / 2);
            Redraw();
            ViewChanged?.Invoke();
        }
    }

    /// <summary>Zoom so one page's width fits the window.</summary>
    public void ZoomToPageWidth()
    {
        if (Layout == null || Layout.Pages.Count == 0 || _viewport.Width <= 0) return;
        var w = Layout.Pages.Max(p => p.Size.Width);
        MultiplePages = false;
        Zoom = Math.Max(0.25, (_viewport.Width - CanvasPadding * 2) / w);
    }

    /// <summary>Zoom so a whole page fits the window.</summary>
    public void ZoomToWholePage()
    {
        if (Layout == null || Layout.Pages.Count == 0 || _viewport.Width <= 0) return;
        var page = CurrentPage ?? Layout.Pages[0];
        MultiplePages = false;
        Zoom = Math.Max(0.25, Math.Min((_viewport.Width - CanvasPadding * 2) / page.Size.Width, (_viewport.Height - PageGap * 2) / page.Size.Height));
    }

    /// <summary>Several pages side by side (as many as fit at a readable size).</summary>
    public void ShowMultiplePages(int count = 0)
    {
        if (Layout == null || Layout.Pages.Count == 0 || _viewport.Width <= 0) return;
        var page = Layout.Pages[0].Size;
        if (count <= 0) count = Math.Max(2, (int)((_viewport.Width - CanvasPadding * 2) / (page.Width * 0.45 + PageGap)));
        MultiplePages = true;
        Zoom = Math.Max(0.25, Math.Min((_viewport.Width - CanvasPadding * 2 - PageGap * (count - 1)) / (page.Width * count), (_viewport.Height - PageGap * 2) / page.Height));
    }

    // ------------------------------------------------------------------ arranging pages

    private bool IsPageless => Layout?.View.Mode == ViewMode.Pageless;

    /// <summary>Positions every page in document space (DIPs at 100 %) and computes the extent.</summary>
    private void Arrange()
    {
        var layout = Layout;
        _pageRects = new List<Rect>();
        if (layout == null)
        {
            _extent = new Size();
            return;
        }
        var viewportDocWidth = Math.Max(100, _viewport.Width / _zoom);
        var pages = layout.Pages;
        if (IsPageless)
        {
            var page = pages.Count > 0 ? pages[0] : null;
            var w = page?.Size.Width ?? 700;
            var h = page?.Size.Height ?? 400;
            var x = Math.Max(CanvasPadding, (viewportDocWidth - w) / 2);
            _pageRects.Add(new Rect(x, 0, w, h));
            _extent = new Size(Math.Max(viewportDocWidth, w + CanvasPadding * 2) * _zoom, (h + PageGap) * _zoom);
        }
        else if (MultiplePages)
        {
            var maxW = pages.Count == 0 ? 600 : pages.Max(p => p.Size.Width);
            var perRow = Math.Max(1, (int)((viewportDocWidth - CanvasPadding * 2 + PageGap) / (maxW + PageGap)));
            var rowWidth = perRow * maxW + (perRow - 1) * PageGap;
            var left = Math.Max(CanvasPadding, (viewportDocWidth - rowWidth) / 2);
            var y = PageGap;
            for (var i = 0; i < pages.Count; i += perRow)
            {
                var rowHeight = 0.0;
                for (var k = 0; k < perRow && i + k < pages.Count; k++)
                {
                    var p = pages[i + k];
                    _pageRects.Add(new Rect(left + k * (maxW + PageGap) + (maxW - p.Size.Width) / 2, y, p.Size.Width, p.Size.Height));
                    rowHeight = Math.Max(rowHeight, p.Size.Height);
                }
                y += rowHeight + PageGap;
            }
            _extent = new Size(Math.Max(viewportDocWidth, rowWidth + CanvasPadding * 2) * _zoom, y * _zoom);
        }
        else
        {
            var maxW = pages.Count == 0 ? 600 : pages.Max(p => p.Size.Width);
            var y = PageGap;
            foreach (var p in pages)
            {
                var x = Math.Max(CanvasPadding, (viewportDocWidth - p.Size.Width) / 2);
                _pageRects.Add(new Rect(x, y, p.Size.Width, p.Size.Height));
                y += p.Size.Height + PageGap;
            }
            _extent = new Size(Math.Max(viewportDocWidth, maxW + CanvasPadding * 2) * _zoom, y * _zoom);
        }
        ScrollOwner?.InvalidateScrollInfo();
    }

    /// <summary>A page's rectangle in document space.</summary>
    public Rect PageRect(int index) => index >= 0 && index < _pageRects.Count ? _pageRects[index] : Rect.Empty;

    public Rect PageRect(PageLayout page) => Layout == null ? Rect.Empty : PageRect(Layout.Pages.IndexOf(page));

    /// <summary>Document-space point → view point.</summary>
    public Point ToView(Point doc) => new(doc.X * _zoom - _offset.X, doc.Y * _zoom - _offset.Y);

    public Point ToDocument(Point view) => new((view.X + _offset.X) / _zoom, (view.Y + _offset.Y) / _zoom);

    /// <summary>A point on a page (page coordinates) → view point.</summary>
    public Point PageToView(PageLayout page, Point p)
    {
        var r = PageRect(page);
        return ToView(new Point(r.X + p.X, r.Y + p.Y));
    }

    /// <summary>The page under a view point (or the nearest) and the point in its coordinates.</summary>
    public (PageLayout Page, Point Point)? PageAt(Point view, bool nearest = true)
    {
        if (Layout == null || Layout.Pages.Count == 0) return null;
        var doc = ToDocument(view);
        var best = -1;
        var bestD = double.MaxValue;
        for (var i = 0; i < _pageRects.Count; i++)
        {
            var r = _pageRects[i];
            var dx = doc.X < r.Left ? r.Left - doc.X : doc.X > r.Right ? doc.X - r.Right : 0;
            var dy = doc.Y < r.Top ? r.Top - doc.Y : doc.Y > r.Bottom ? doc.Y - r.Bottom : 0;
            var d = dx + dy * 2;
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
            if (r.Top > doc.Y + 4000 / _zoom) break;
        }
        if (best < 0 || (!nearest && bestD > 0)) return null;
        var rect = _pageRects[best];
        return (Layout.Pages[best], new Point(doc.X - rect.X, doc.Y - rect.Y));
    }

    /// <summary>The page at the middle of the viewport.</summary>
    public PageLayout? CurrentPage
    {
        get
        {
            if (Layout == null || Layout.Pages.Count == 0) return null;
            var caret = CaretPlace;
            if (caret != null && IsVisibleOnScreen(caret.Value)) return caret.Value.Page;
            var mid = (_offset.Y + _viewport.Height / 3) / _zoom;
            for (var i = 0; i < _pageRects.Count; i++)
                if (_pageRects[i].Bottom + PageGap / 2 >= mid) return Layout.Pages[i];
            return Layout.Pages[Layout.Pages.Count - 1];
        }
    }

    private bool IsVisibleOnScreen(CaretPlace caret)
    {
        var p = PageToView(caret.Page, caret.Rect.TopLeft);
        return p.Y >= -caret.Rect.Height && p.Y <= _viewport.Height;
    }

    // ------------------------------------------------------------------ document changes

    private void OnDocumentChanged()
    {
        if (Layout == null) return;
        try
        {
            Layout.Update();
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex, "Layout update");
            Layout.LayoutAll();
        }
        Arrange();
        Redraw();
        ScrollToCaret();
        LayoutUpdated2?.Invoke();
        AutomationChanged();
    }

    /// <summary>Lays out everything again (view options or styles changed).</summary>
    public void RelayoutAll()
    {
        if (Layout == null) return;
        Layout.LayoutAll();
        _pageVisuals.Clear();
        _pagesLayer.Children.Clear();
        Arrange();
        Redraw();
        LayoutUpdated2?.Invoke();
    }

    private void OnSelectionChanged()
    {
        _caretPlace = null;
        ResetCaretBlink();
        ScrollToCaret();
        DrawOverlay();
        DrawCaret();
        UpdateImeWindow();
        AutomationSelectionChanged();
    }

    private void ResetCaretBlink()
    {
        _caretOn = true;
        _caretVisual.Opacity = IsKeyboardFocusWithin ? 1 : 0;
        _caretTimer.Stop();
        _caretTimer.Start();
    }

    // ------------------------------------------------------------------ drawing

    /// <summary>A page on screen: its shadow and its cached content.</summary>
    private sealed class PageVisual : ContainerVisual
    {
        public readonly DrawingVisual Shadow = new();
        public readonly DrawingVisual Content = new();
        public int Stamp = -1;
        public int OptionsStamp = -1;

        public PageVisual()
        {
            Children.Add(Shadow);
            Children.Add(Content);
        }
    }

    private int OptionsStamp => (ShowFormattingMarks ? 1 : 0) + (Layout?.View.DarkPaper == true ? 2 : 0) + (EditingHeaderFooter ? 4 : 0) + (int)(Layout?.View.Revisions ?? 0) * 8;

    /// <summary>Redraws the visible pages (from cache when unchanged), the overlay and the caret.</summary>
    public void Redraw()
    {
        var layout = Layout;
        if (layout == null) return;
        var transform = new MatrixTransform(_zoom, 0, 0, _zoom, -_offset.X, -_offset.Y);
        transform.Freeze();
        _pagesLayer.Transform = transform;
        _overlay.Transform = transform;
        _caretVisual.Transform = transform;

        var visibleTop = _offset.Y / _zoom - 200;
        var visibleBottom = (_offset.Y + _viewport.Height) / _zoom + 200;
        var visibleLeft = _offset.X / _zoom - 200;
        var visibleRight = (_offset.X + _viewport.Width) / _zoom + 200;
        var keep = new HashSet<PageLayout>(ReferenceEqualityComparer<PageLayout>.Instance);
        var renderer = new PageRenderer(layout);
        var optionsStamp = OptionsStamp;
        for (var i = 0; i < layout.Pages.Count && i < _pageRects.Count; i++)
        {
            var rect = _pageRects[i];
            if (rect.Bottom < visibleTop || rect.Top > visibleBottom || rect.Right < visibleLeft || rect.Left > visibleRight) continue;
            var page = layout.Pages[i];
            keep.Add(page);
            if (!_pageVisuals.TryGetValue(page, out var visual))
            {
                visual = new PageVisual();
                _pageVisuals[page] = visual;
                _pagesLayer.Children.Add(visual);
            }
            visual.Offset = new Vector(rect.X, rect.Y);
            if (visual.Stamp != page.Stamp || visual.OptionsStamp != optionsStamp)
            {
                using (var dc = visual.Shadow.RenderOpen())
                {
                    if (!IsPageless)
                    {
                        // A soft two-layer shadow under the paper.
                        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x14, 0, 0, 0)), null, new Rect(-1, 1, page.Size.Width + 2, page.Size.Height + 2), 2, 2);
                        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x0C, 0, 0, 0)), null, new Rect(-3, 2, page.Size.Width + 6, page.Size.Height + 6), 5, 5);
                    }
                }
                using (var dc = visual.Content.RenderOpen())
                {
                    try
                    {
                        renderer.DrawPage(dc, page, new RenderExtras { FormattingMarks = ShowFormattingMarks });
                        DrawHeaderFooterChrome(dc, page);
                    }
                    catch (Exception ex)
                    {
                        Services.Log.Error(ex, "Draw page " + (i + 1));
                    }
                }
                visual.Stamp = page.Stamp;
                visual.OptionsStamp = optionsStamp;
            }
        }
        foreach (var gone in _pageVisuals.Keys.Where(p => !keep.Contains(p)).ToList())
        {
            _pagesLayer.Children.Remove(_pageVisuals[gone]);
            _pageVisuals.Remove(gone);
        }
        DrawOverlay();
        DrawCaret();
    }

    protected override int VisualChildrenCount => _visuals.Count;

    protected override Visual GetVisualChild(int index) => _visuals[index];

    protected override void OnRender(DrawingContext dc) => dc.DrawRectangle(CanvasBrush, null, new Rect(RenderSize));

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 600 : availableSize.Height);

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (finalSize != _viewport)
        {
            var oldWidth = _viewport.Width;
            _viewport = finalSize;
            if (Math.Abs(oldWidth - finalSize.Width) > 0.5) Arrange();
            ScrollOwner?.InvalidateScrollInfo();
            SetOffsets(_offset.X, _offset.Y);
            Redraw();
        }
        return finalSize;
    }

    // ------------------------------------------------------------------ IScrollInfo

    public bool CanVerticallyScroll { get; set; } = true;
    public bool CanHorizontallyScroll { get; set; } = true;
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;
    public ScrollViewer? ScrollOwner { get; set; }

    private const double LineStep = 48;

    public void LineUp() => SetVerticalOffset(_offset.Y - LineStep);
    public void LineDown() => SetVerticalOffset(_offset.Y + LineStep);
    public void LineLeft() => SetHorizontalOffset(_offset.X - LineStep);
    public void LineRight() => SetHorizontalOffset(_offset.X + LineStep);
    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height * 0.9);
    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height * 0.9);
    public void PageLeft() => SetHorizontalOffset(_offset.X - _viewport.Width * 0.9);
    public void PageRight() => SetHorizontalOffset(_offset.X + _viewport.Width * 0.9);
    public void MouseWheelUp() => SetVerticalOffset(_offset.Y - LineStep * 2);
    public void MouseWheelDown() => SetVerticalOffset(_offset.Y + LineStep * 2);
    public void MouseWheelLeft() => SetHorizontalOffset(_offset.X - LineStep * 2);
    public void MouseWheelRight() => SetHorizontalOffset(_offset.X + LineStep * 2);

    public void SetHorizontalOffset(double offset) => SetOffsets(offset, _offset.Y);

    public void SetVerticalOffset(double offset) => SetOffsets(_offset.X, offset);

    private void SetOffsets(double x, double y)
    {
        var nx = MathEx.Clamp(x, 0, Math.Max(0, _extent.Width - _viewport.Width));
        var ny = MathEx.Clamp(y, 0, Math.Max(0, _extent.Height - _viewport.Height));
        if (Math.Abs(nx - _offset.X) < 0.01 && Math.Abs(ny - _offset.Y) < 0.01) return;
        _offset = new Vector(nx, ny);
        ScrollOwner?.InvalidateScrollInfo();
        Redraw();
        UpdateImeWindow();
        ViewChanged?.Invoke();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;

    /// <summary>Scrolls so a rectangle (document space) is visible, with a margin.</summary>
    public void BringIntoView(Rect docRect, double margin = 40, bool center = false)
    {
        var view = new Rect(docRect.X * _zoom, docRect.Y * _zoom, docRect.Width * _zoom, docRect.Height * _zoom);
        var x = _offset.X;
        var y = _offset.Y;
        if (center)
        {
            y = view.Top + view.Height / 2 - _viewport.Height / 2;
        }
        else
        {
            if (view.Top - margin < y) y = view.Top - margin;
            else if (view.Bottom + margin > y + _viewport.Height) y = view.Bottom + margin - _viewport.Height;
        }
        if (view.Left - margin < x) x = view.Left - margin;
        else if (view.Right + margin > x + _viewport.Width) x = view.Right + margin - _viewport.Width;
        SetOffsets(x, y);
    }

    /// <summary>Scrolls the caret into view (typewriter scrolling keeps it centred).</summary>
    public void ScrollToCaret(bool center = false)
    {
        var caret = CaretPlace;
        if (caret == null) return;
        var r = PageRect(caret.Value.Page);
        var docRect = new Rect(r.X + caret.Value.Rect.X - 2, r.Y + caret.Value.Rect.Y, 4, caret.Value.Rect.Height);
        BringIntoView(docRect, 48, center || TypewriterScrolling);
    }

    public void GoToPage(int index)
    {
        if (Layout == null || index < 0 || index >= _pageRects.Count) return;
        SetOffsets(_offset.X, _pageRects[index].Top * _zoom - PageGap * _zoom);
    }
}
