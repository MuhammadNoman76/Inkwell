using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Inkwell.Core.Editing;
using Inkwell.Core.Layout;
using Inkwell.Core.Model;

namespace Inkwell.Views.Editing;

public sealed partial class DocumentView
{
    private enum DragMode { None, Select, SelectWords, SelectParagraphs, MoveDrawing, ResizeDrawing, ColumnBorder, MoveAnchor }

    private DragMode _drag;
    private DocPos _dragAnchorStart;
    private DocPos _dragAnchorEnd;
    private Point _dragStartView;
    private Rect _dragStartBox;
    private int _resizeHandle;
    private PageLayout? _dragPage;
    private DispatcherTimer? _autoScroll;
    private Point _lastMouse;
    private (TableFragment Table, int Edge, double X)? _columnDrag;
    private double _columnDragDelta;

    /// <summary>Typing hook: return true when the text was handled (slash menu, Markdown shortcuts).</summary>
    public Func<string, bool>? BeforeTextInput { get; set; }

    /// <summary>Called after text was typed (autocorrect).</summary>
    public Action<string>? AfterTextInput { get; set; }

    /// <summary>Ctrl+click on a link (or a click on a table-of-contents entry).</summary>
    public event Action<Hyperlink, Paragraph>? LinkClicked;

    /// <summary>Right-click: the hit under the mouse (or null) and the point in view coordinates.</summary>
    public event Action<PageHit?, Point>? ContextMenuRequested;

    /// <summary>A click in the header / footer area: (page, header?) — the editor creates or opens it.</summary>
    public event Action<PageLayout, bool>? HeaderFooterRequested;

    /// <summary>A floating picture was dragged to a new box on a page (page coordinates).</summary>
    public event Action<DrawingObject, PageLayout, Rect>? DrawingMoved;

    /// <summary>A picture was resized (new size in DIPs).</summary>
    public event Action<DrawingObject, Size>? DrawingResized;

    /// <summary>A table column border was dragged: table, grid edge index, change in DIPs.</summary>
    public event Action<Table, int, double>? ColumnResized;

    /// <summary>Tab in a table / list is handled by the editor view; return true if handled.</summary>
    public Func<bool, bool>? TabPressed { get; set; }

    /// <summary>A table of contents entry was clicked (jump to its heading).</summary>
    public event Action<Paragraph>? TocEntryClicked;

    private void InitInput()
    {
        AllowDrop = true;
    }

    // ------------------------------------------------------------------ keyboard

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || Editor == null || Layout == null) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        if (alt && key is not (Key.Up or Key.Down)) return;
        try
        {
            switch (key)
            {
                case Key.Left:
                case Key.Right:
                    MoveHorizontal(key == Key.Right, ctrl, shift);
                    e.Handled = true;
                    break;
                case Key.Up:
                case Key.Down:
                    if (ctrl) MoveParagraph(key == Key.Down, shift);
                    else MoveVertical(key == Key.Down, shift);
                    e.Handled = true;
                    break;
                case Key.Home:
                    MoveHome(ctrl, shift);
                    e.Handled = true;
                    break;
                case Key.End:
                    MoveEnd(ctrl, shift);
                    e.Handled = true;
                    break;
                case Key.PageUp:
                case Key.PageDown:
                    MovePage(key == Key.PageDown, shift);
                    e.Handled = true;
                    break;
                case Key.Back:
                    if (SelectedDrawing != null) DeleteSelectedDrawing();
                    else Editor.Backspace(ctrl);
                    e.Handled = true;
                    break;
                case Key.Delete:
                    if (SelectedDrawing != null) DeleteSelectedDrawing();
                    else Editor.DeleteForward(ctrl);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    SelectDrawing(null);
                    if (ctrl && shift) Editor.InsertBreak(TextRun.ColumnBreak);
                    else if (ctrl) Editor.InsertBreak(TextRun.PageBreak);
                    else if (shift) Editor.InsertBreak(TextRun.LineBreak);
                    else if (BeforeTextInput?.Invoke("\n") != true) Editor.Enter();
                    e.Handled = true;
                    break;
                case Key.Tab:
                    if (ctrl) break;
                    if (TabPressed?.Invoke(shift) == true)
                    {
                        e.Handled = true;
                        break;
                    }
                    if (!shift) Editor.InsertText("\t");
                    e.Handled = true;
                    break;
                case Key.Escape:
                    if (SelectedDrawing != null)
                    {
                        SelectDrawing(null);
                        e.Handled = true;
                    }
                    else if (EditingHeaderFooter)
                    {
                        ExitHeaderFooter();
                        e.Handled = true;
                    }
                    break;
            }
        }
        catch (EditRefusedException ex)
        {
            Refused?.Invoke(ex.Message);
            e.Handled = true;
        }
    }

    /// <summary>An edit was refused (read-only content): the message for the user.</summary>
    public event Action<string>? Refused;

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled || Editor == null) return;
        var text = e.Text;
        if (string.IsNullOrEmpty(text)) return;
        // Control characters from Ctrl+key combinations aren't text.
        if (text.All(c => c < ' ' && c != '\t') || text == "\u007F") return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && (Keyboard.Modifiers & ModifierKeys.Alt) == 0) return;
        e.Handled = true;
        try
        {
            if (SelectedDrawing != null)
            {
                SelectDrawing(null);
            }
            if (BeforeTextInput?.Invoke(text) == true) return;
            Editor.InsertText(text);
            AfterTextInput?.Invoke(text);
        }
        catch (EditRefusedException ex)
        {
            Refused?.Invoke(ex.Message);
        }
    }

    private void SetCaret(DocPos pos, bool extend, bool atLineEnd = false, bool keepDesiredX = false)
    {
        if (!keepDesiredX) _desiredX = null;
        _caretAtLineEnd = atLineEnd;
        var editor = Editor!;
        editor.Selection = extend ? new Selection(editor.Selection.Anchor, pos) : new Selection(pos);
        _caretPlace = null;
        DrawCaret();
    }

    private bool IsRtlParagraph(Paragraph p) => Layout!.Styles.ResolvePara(p).Bidi;

    private void MoveHorizontal(bool right, bool word, bool extend)
    {
        var editor = Editor!;
        var sel = editor.Selection;
        var pos = sel.Active;
        var forward = IsRtlParagraph(pos.Para) ? !right : right;
        if (!sel.IsEmpty && !extend)
        {
            SetCaret(forward ? sel.End : sel.Start, false);
            return;
        }
        DocPos target;
        if (forward)
        {
            if (pos.Offset >= pos.Para.Length)
            {
                var next = Nav.Next(pos.Para);
                if (next == null) return;
                target = new DocPos(next, 0);
            }
            else
            {
                var o = word ? DocEditor.WordEndAfter(pos.Para, pos.Offset) : DocEditor.NextPosition(pos.Para, pos.Offset);
                target = new DocPos(pos.Para, SkipHidden(pos.Para, o, true));
            }
        }
        else
        {
            if (pos.Offset <= 0)
            {
                var prev = Nav.Prev(pos.Para);
                if (prev == null) return;
                target = new DocPos(prev, prev.Length);
            }
            else
            {
                var o = word ? DocEditor.WordStartBefore(pos.Para, pos.Offset) : DocEditor.PreviousPosition(pos.Para, pos.Offset);
                target = new DocPos(pos.Para, SkipHidden(pos.Para, o, false));
            }
        }
        SetCaret(target, extend);
    }

    /// <summary>Steps over text that isn't shown (hidden text, deleted text in final views).</summary>
    private int SkipHidden(Paragraph p, int offset, bool forward)
    {
        var loc = Layout!.Locate(p).FirstOrDefault();
        if (loc == null) return offset;
        var source = loc.Fragment.Layout.Source;
        var guard = 0;
        while (offset > 0 && offset < p.Length && source.IsHiddenAt(offset) && guard++ < 100000) offset += forward ? 1 : -1;
        return offset;
    }

    private void MoveVertical(bool down, bool extend)
    {
        var caret = CaretPlace;
        if (caret == null) return;
        var pageRect = PageRect(caret.Value.Page);
        _desiredX ??= caret.Value.Rect.X;
        var target = Layout!.LineUpDown(caret.Value, _desiredX.Value, down ? 1 : -1, out var atEnd);
        if (target == null)
        {
            // Top / bottom of the story: go to its start / end.
            var story = Editor!.Story;
            target = down ? Nav.EndOf(story) : Nav.StartOf(story);
        }
        _ = pageRect;
        SetCaret(target.Value, extend, atEnd, keepDesiredX: true);
    }

    private void MoveParagraph(bool down, bool extend)
    {
        var pos = Editor!.Selection.Active;
        DocPos target;
        if (down)
        {
            var next = Nav.Next(pos.Para);
            target = next != null ? new DocPos(next, 0) : new DocPos(pos.Para, pos.Para.Length);
        }
        else if (pos.Offset > 0) target = new DocPos(pos.Para, 0);
        else
        {
            var prev = Nav.Prev(pos.Para);
            target = prev != null ? new DocPos(prev, 0) : pos;
        }
        SetCaret(target, extend);
    }

    private void MoveHome(bool document, bool extend)
    {
        if (document)
        {
            SetCaret(Nav.StartOf(Editor!.Story), extend);
            return;
        }
        var caret = CaretPlace;
        if (caret == null) return;
        var (start, _) = HitTesting.LineBounds(caret.Value);
        SetCaret(new DocPos(Editor!.Selection.Active.Para, start), extend);
    }

    private void MoveEnd(bool document, bool extend)
    {
        if (document)
        {
            SetCaret(Nav.EndOf(Editor!.Story), extend);
            return;
        }
        var caret = CaretPlace;
        if (caret == null) return;
        var (_, end) = HitTesting.LineBounds(caret.Value);
        var line = caret.Value.Line;
        SetCaret(new DocPos(Editor!.Selection.Active.Para, end), extend, atLineEnd: !line.EndsParagraph);
    }

    private void MovePage(bool down, bool extend)
    {
        var caret = CaretPlace;
        if (caret == null) return;
        var view = PageToView(caret.Value.Page, caret.Value.Rect.TopLeft);
        var delta = _viewport.Height * 0.9 * (down ? 1 : -1);
        SetVerticalOffset(_offset.Y + delta);
        var hitAt = PageAt(new Point(view.X, view.Y + caret.Value.Rect.Height / 2 * _zoom));
        if (hitAt == null) return;
        var hit = Layout!.HitTest(hitAt.Value.Page, hitAt.Value.Point);
        if (hit?.Position.Para != null) SetCaret(hit.Position, extend, hit.AtLineEnd);
    }

    // ------------------------------------------------------------------ mouse

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Editor == null || Layout == null) return;
        Focus();
        Keyboard.Focus(this);
        var view = e.GetPosition(this);
        _lastMouse = view;
        var at = PageAt(view);
        if (at == null) return;
        var (page, point) = at.Value;

        // The anchor mark of the selected floating picture: drag it to another paragraph.
        if (SelectedDrawing is { IsFloating: true } anchored && anchored.Float?.Locked != true && AnchorGlyphPoint(page, anchored) is { } glyph
            && (new Point(glyph.X + 7 / _zoom, glyph.Y + 7 / _zoom) - point).Length <= 11 / _zoom)
        {
            _drag = DragMode.MoveAnchor;
            _anchorTarget = null;
            _dragPage = page;
            CaptureMouse();
            e.Handled = true;
            return;
        }

        // Handles of the selected picture.
        if (SelectedDrawing != null && DrawingBox(page, SelectedDrawing) is { } box && SelectedDrawing.Float?.Locked != true)
        {
            var handles = HandlePoints(box).ToList();
            for (var i = 0; i < handles.Count; i++)
            {
                if ((handles[i] - point).Length <= 7 / _zoom)
                {
                    _drag = DragMode.ResizeDrawing;
                    _resizeHandle = i;
                    _dragStartBox = box;
                    _dragStartView = view;
                    _dragPage = page;
                    CaptureMouse();
                    e.Handled = true;
                    return;
                }
            }
        }

        // Table column borders.
        if (FindColumnBorder(page, point) is { } border)
        {
            _drag = DragMode.ColumnBorder;
            _columnDrag = border;
            _columnDragDelta = 0;
            _dragStartView = view;
            _dragPage = page;
            CaptureMouse();
            e.Handled = true;
            return;
        }

        var inHeaderArea = !IsPageless && (point.Y < page.BodyRect.Top - 2 || point.Y > page.BodyRect.Bottom + 2) && point.Y >= 0 && point.Y <= page.Size.Height;
        if (!EditingHeaderFooter && inHeaderArea && e.ClickCount >= 1)
        {
            HeaderFooterRequested?.Invoke(page, point.Y < page.BodyRect.Top);
            e.Handled = true;
            return;
        }
        if (EditingHeaderFooter && !inHeaderArea)
        {
            ExitHeaderFooter();
        }

        var hit = Layout.HitTest(page, point, EditingHeaderFooter);
        if (hit == null) return;
        if (EditingHeaderFooter && hit.Region is PageRegion.Header or PageRegion.Footer) HeaderFooterPage = page;

        // Pictures: select (and start moving floating ones).
        var drawing = hit.Float?.Drawing ?? hit.InlineDrawing;
        if (drawing != null)
        {
            SelectDrawing(drawing);
            Editor.Selection = new Selection(hit.Position);
            if (hit.Float != null && drawing.Float?.Locked != true)
            {
                _drag = DragMode.MoveDrawing;
                _dragStartBox = hit.Float.Box;
                _dragStartView = view;
                _dragPage = page;
                CaptureMouse();
            }
            e.Handled = true;
            return;
        }
        SelectDrawing(null);

        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var pos = hit.Position;
        if (pos.Para == null) return;

        // Links: Ctrl+click follows them. A click in a table of contents jumps to the heading.
        if (ctrl && e.ClickCount == 1 && !shift)
        {
            var inline = pos.Para.InlineAt(pos.Offset) ?? (pos.Offset > 0 ? pos.Para.InlineAt(pos.Offset - 1) : null);
            if (inline?.Link != null)
            {
                LinkClicked?.Invoke(inline.Link, pos.Para);
                e.Handled = true;
                return;
            }
        }
        if (!ctrl && !shift && e.ClickCount == 1 && IsTocEntry(pos.Para))
        {
            TocEntryClicked?.Invoke(pos.Para);
            e.Handled = true;
            return;
        }

        if (hit.InSelectionMargin && !EditingHeaderFooter)
        {
            // The selection margin: a line, or the paragraph on double-click.
            if (e.ClickCount >= 2) SelectParagraph(pos.Para);
            else if (hit.Line != null) Editor.Selection = new Selection(new DocPos(pos.Para, hit.Line.DocStart), new DocPos(pos.Para, hit.Line.EndsParagraph ? pos.Para.Length : hit.Line.DocEnd));
            _drag = DragMode.SelectParagraphs;
            _dragAnchorStart = Editor.Selection.Start;
            _dragAnchorEnd = Editor.Selection.End;
            CaptureMouse();
            e.Handled = true;
            return;
        }

        if (e.ClickCount == 2)
        {
            var (ws, we) = DocEditor.WordAt(pos.Para, pos.Offset);
            Editor.Selection = new Selection(new DocPos(pos.Para, ws), new DocPos(pos.Para, we));
            _drag = DragMode.SelectWords;
            _dragAnchorStart = Editor.Selection.Start;
            _dragAnchorEnd = Editor.Selection.End;
        }
        else if (e.ClickCount >= 3)
        {
            SelectParagraph(pos.Para);
            _drag = DragMode.SelectParagraphs;
            _dragAnchorStart = Editor.Selection.Start;
            _dragAnchorEnd = Editor.Selection.End;
        }
        else if (ctrl)
        {
            var (ss, se) = DocEditor.SentenceAt(pos.Para, pos.Offset);
            Editor.Selection = new Selection(new DocPos(pos.Para, ss), new DocPos(pos.Para, se));
            _drag = DragMode.SelectWords;
            _dragAnchorStart = Editor.Selection.Start;
            _dragAnchorEnd = Editor.Selection.End;
        }
        else
        {
            if (shift && Editor.Selection.Anchor.Para?.Story == pos.Para.Story) SetCaret(pos, true, hit.AtLineEnd);
            else SetCaret(pos, false, hit.AtLineEnd);
            _drag = DragMode.Select;
        }
        CaptureMouse();
        e.Handled = true;
    }

    private bool IsTocEntry(Paragraph p)
    {
        var owner = p.Parent?.Owner;
        while (owner is Block b)
        {
            if (b is FieldBlock { Kind: FieldKind.Toc }) return true;
            owner = b.Parent?.Owner;
        }
        return false;
    }

    public void SelectParagraph(Paragraph p) => Editor!.Selection = new Selection(new DocPos(p, 0), new DocPos(p, p.Length));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Editor == null || Layout == null) return;
        var view = e.GetPosition(this);
        _lastMouse = view;
        if (_drag == DragMode.None)
        {
            UpdateCursor(view);
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag();
            return;
        }
        switch (_drag)
        {
            case DragMode.Select:
            case DragMode.SelectWords:
            case DragMode.SelectParagraphs:
                ExtendDragSelection(view);
                EnsureAutoScroll(view);
                break;
            case DragMode.MoveDrawing:
                DragDrawing(view);
                break;
            case DragMode.MoveAnchor:
                DragAnchor(view);
                break;
            case DragMode.ResizeDrawing:
                ResizeDrawing(view);
                break;
            case DragMode.ColumnBorder:
                _columnDragDelta = (view.X - _dragStartView.X) / _zoom;
                DrawColumnGuide();
                break;
        }
    }

    private void UpdateCursor(Point view)
    {
        var at = PageAt(view, nearest: false);
        if (at == null)
        {
            Cursor = Cursors.Arrow;
            return;
        }
        var (page, point) = at.Value;
        if (SelectedDrawing != null && DrawingBox(page, SelectedDrawing) is { } box)
        {
            var handles = HandlePoints(box).ToList();
            for (var i = 0; i < handles.Count; i++)
            {
                if ((handles[i] - point).Length <= 7 / _zoom)
                {
                    Cursor = i is 0 or 4 ? Cursors.SizeNWSE : i is 2 or 6 ? Cursors.SizeNESW : i is 1 or 5 ? Cursors.SizeNS : Cursors.SizeWE;
                    return;
                }
            }
            if (box.Contains(point) && SelectedDrawing.IsFloating && SelectedDrawing.Float?.Locked != true)
            {
                Cursor = Cursors.SizeAll;
                return;
            }
        }
        if (FindColumnBorder(page, point) != null)
        {
            Cursor = Cursors.SizeWE;
            return;
        }
        if (page.Floats.Any(f => !f.Behind && !f.InHeaderFooter && f.Box.Contains(point)))
        {
            Cursor = Cursors.Arrow;
            return;
        }
        if (point.X < page.Margins.Left - 6 && point.Y > page.BodyRect.Top && point.Y < page.BodyRect.Bottom)
        {
            Cursor = Cursors.Arrow;
            return;
        }
        Cursor = Cursors.IBeam;
    }

    private void ExtendDragSelection(Point view)
    {
        var at = PageAt(view);
        if (at == null) return;
        var hit = Layout!.HitTest(at.Value.Page, at.Value.Point, EditingHeaderFooter);
        if (hit?.Position.Para == null || hit.Position.Para.Story != Editor!.Selection.Anchor.Para?.Story) return;
        var pos = hit.Position;
        switch (_drag)
        {
            case DragMode.Select:
                _caretAtLineEnd = hit.AtLineEnd;
                Editor.Selection = new Selection(Editor.Selection.Anchor, pos);
                break;
            case DragMode.SelectWords:
            {
                var (ws, we) = DocEditor.WordAt(pos.Para, pos.Offset);
                var forward = Nav.Compare(pos, _dragAnchorStart) >= 0;
                Editor.Selection = forward
                    ? new Selection(_dragAnchorStart, new DocPos(pos.Para, Math.Max(we, pos.Offset)))
                    : new Selection(_dragAnchorEnd, new DocPos(pos.Para, Math.Min(ws, pos.Offset)));
                break;
            }
            case DragMode.SelectParagraphs:
            {
                var forward = Nav.Compare(pos, _dragAnchorStart) >= 0;
                Editor.Selection = forward
                    ? new Selection(_dragAnchorStart, new DocPos(pos.Para, pos.Para.Length))
                    : new Selection(_dragAnchorEnd, new DocPos(pos.Para, 0));
                break;
            }
        }
        _caretPlace = null;
    }

    private void EnsureAutoScroll(Point view)
    {
        var outside = view.Y < 0 || view.Y > _viewport.Height;
        if (!outside)
        {
            _autoScroll?.Stop();
            return;
        }
        if (_autoScroll == null)
        {
            _autoScroll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _autoScroll.Tick += (_, _) =>
            {
                if (_drag is not (DragMode.Select or DragMode.SelectWords or DragMode.SelectParagraphs) || Mouse.LeftButton != MouseButtonState.Pressed)
                {
                    _autoScroll!.Stop();
                    return;
                }
                var dy = _lastMouse.Y < 0 ? _lastMouse.Y : _lastMouse.Y - _viewport.Height;
                SetVerticalOffset(_offset.Y + MathEx.Clamp(dy, -60, 60));
                ExtendDragSelection(_lastMouse);
            };
        }
        _autoScroll.Start();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        EndDrag(commit: true);
    }

    private void EndDrag(bool commit = false)
    {
        var mode = _drag;
        _drag = DragMode.None;
        _autoScroll?.Stop();
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (!commit) return;
        switch (mode)
        {
            case DragMode.MoveDrawing when SelectedDrawing != null && _dragPage != null && _moveBox is { } box:
                Guides.Clear();
                if ((box.TopLeft - _dragStartBox.TopLeft).Length > 0.5) DrawingMoved?.Invoke(SelectedDrawing, _dragPage, box);
                _moveBox = null;
                DrawOverlay();
                break;
            case DragMode.ResizeDrawing when SelectedDrawing != null && _resizeBox is { } rbox:
                if (Math.Abs(rbox.Width - _dragStartBox.Width) > 0.5 || Math.Abs(rbox.Height - _dragStartBox.Height) > 0.5)
                    DrawingResized?.Invoke(SelectedDrawing, rbox.Size);
                _resizeBox = null;
                DrawOverlay();
                break;
            case DragMode.MoveAnchor when SelectedDrawing != null:
                Guides.Clear();
                if (_anchorTarget != null) AnchorMoved?.Invoke(SelectedDrawing, _anchorTarget);
                _anchorTarget = null;
                DrawOverlay();
                break;
            case DragMode.ColumnBorder when _columnDrag is { } c:
                _columnDrag = null;
                if (Math.Abs(_columnDragDelta) > 0.5) ColumnResized?.Invoke(c.Table.Layout.Table, c.Edge, _columnDragDelta);
                DrawOverlay();
                break;
        }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (Editor == null || Layout == null) return;
        Focus();
        var view = e.GetPosition(this);
        var at = PageAt(view);
        PageHit? hit = null;
        if (at != null)
        {
            hit = Layout.HitTest(at.Value.Page, at.Value.Point, EditingHeaderFooter);
            // Right-click outside the selection moves the caret there first.
            if (hit != null && hit.Float == null && hit.Position.Para != null)
            {
                var sel = Editor.Selection;
                var inside = !sel.IsEmpty && Nav.Compare(hit.Position, sel.Start) >= 0 && Nav.Compare(hit.Position, sel.End) <= 0;
                if (!inside && hit.InlineDrawing == null) SetCaret(hit.Position, false, hit.AtLineEnd);
                if (hit.InlineDrawing != null) SelectDrawing(hit.InlineDrawing);
            }
            else if (hit?.Float != null) SelectDrawing(hit.Float.Drawing);
        }
        ContextMenuRequested?.Invoke(hit, view);
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            Zoom = Math.Round(Zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1), 2);
            e.Handled = true;
            return;
        }
        base.OnMouseWheel(e);
    }

    // ------------------------------------------------------------------ pictures

    private Rect? _moveBox;
    private Rect? _resizeBox;

    private void DragDrawing(Point view)
    {
        if (_dragPage == null) return;
        var delta = new Vector((view.X - _dragStartView.X) / _zoom, (view.Y - _dragStartView.Y) / _zoom);
        var box = _dragStartBox;
        box.Offset(delta);
        // Smart guides: snap to the margins, the column and page centres, and the top of the text.
        Guides.Clear();
        if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0)
        {
            var page = _dragPage;
            var xs = new[] { page.Margins.Left, page.Size.Width - page.Margins.Right, page.Size.Width / 2, page.BodyRect.Left + page.BodyRect.Width / 2 };
            var ys = new[] { page.BodyRect.Top, page.BodyRect.Bottom, page.Size.Height / 2 };
            var threshold = 6 / _zoom;
            double? snapX = null, snapY = null;
            foreach (var x in xs)
            {
                foreach (var (edge, offset) in new[] { (box.Left, 0.0), (box.Left + box.Width / 2, box.Width / 2), (box.Right, box.Width) })
                {
                    if (Math.Abs(edge - x) < threshold && snapX == null)
                    {
                        snapX = x - offset;
                        Guides.Add((page, new Point(x, 0), new Point(x, page.Size.Height)));
                    }
                }
            }
            foreach (var y in ys)
            {
                foreach (var (edge, offset) in new[] { (box.Top, 0.0), (box.Top + box.Height / 2, box.Height / 2), (box.Bottom, box.Height) })
                {
                    if (Math.Abs(edge - y) < threshold && snapY == null)
                    {
                        snapY = y - offset;
                        Guides.Add((page, new Point(0, y), new Point(page.Size.Width, y)));
                    }
                }
            }
            box = new Rect(snapX ?? box.X, snapY ?? box.Y, box.Width, box.Height);
        }
        _moveBox = box;
        DrawOverlay();
        using var dc = _caretVisual.RenderOpen();
        var r = PageRect(_dragPage);
        var outline = new Pen(new SolidColorBrush(AccentColor), 1.5 / _zoom) { DashStyle = DashStyles.Dash };
        var b = box;
        b.Offset(r.X, r.Y);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x22, AccentColor.R, AccentColor.G, AccentColor.B)), outline, b);
        _caretVisual.Opacity = 1;
    }

    private void ResizeDrawing(Point view)
    {
        if (_dragPage == null || SelectedDrawing == null) return;
        var d = new Vector((view.X - _dragStartView.X) / _zoom, (view.Y - _dragStartView.Y) / _zoom);
        var b = _dragStartBox;
        double left = b.Left, top = b.Top, right = b.Right, bottom = b.Bottom;
        switch (_resizeHandle)
        {
            case 0: left += d.X; top += d.Y; break;
            case 1: top += d.Y; break;
            case 2: right += d.X; top += d.Y; break;
            case 3: right += d.X; break;
            case 4: right += d.X; bottom += d.Y; break;
            case 5: bottom += d.Y; break;
            case 6: left += d.X; bottom += d.Y; break;
            case 7: left += d.X; break;
        }
        var w = Math.Max(12, right - left);
        var h = Math.Max(12, bottom - top);
        // Corners keep the picture's proportions (Shift frees them).
        if (_resizeHandle is 0 or 2 or 4 or 6 && (Keyboard.Modifiers & ModifierKeys.Shift) == 0 && b.Width > 0 && b.Height > 0)
        {
            var aspect = b.Width / b.Height;
            if (w / h > aspect) w = h * aspect;
            else h = w / aspect;
            if (_resizeHandle is 0 or 6) left = right - w;
            if (_resizeHandle is 0 or 2) top = bottom - h;
        }
        _resizeBox = new Rect(left, top, w, h);
        using var dc = _caretVisual.RenderOpen();
        var r = PageRect(_dragPage);
        var box = _resizeBox.Value;
        box.Offset(r.X, r.Y);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x22, AccentColor.R, AccentColor.G, AccentColor.B)), new Pen(new SolidColorBrush(AccentColor), 1.5 / _zoom), box);
        _caretVisual.Opacity = 1;
    }

    private void DeleteSelectedDrawing()
    {
        var drawing = SelectedDrawing;
        if (drawing == null || Editor == null) return;
        SelectDrawing(null);
        Editor.DeleteDrawing(drawing);
    }

    // ------------------------------------------------------------------ table columns

    private (TableFragment Table, int Edge, double X)? FindColumnBorder(PageLayout page, Point point)
    {
        foreach (var col in page.Columns)
        {
            foreach (var tf in col.Content.OfType<TableFragment>())
            {
                if (point.Y < tf.Y - 2 || point.Y > tf.Y + tf.Height + 2) continue;
                var edges = tf.Layout.ColumnEdges;
                for (var i = 1; i < edges.Length; i++)
                {
                    var x = tf.X + (tf.Layout.Rtl ? tf.Layout.Width - edges[i] : edges[i]);
                    if (Math.Abs(point.X - x) <= 3.5 / _zoom) return (tf, i, x);
                }
            }
        }
        return null;
    }

    private void DrawColumnGuide()
    {
        if (_columnDrag is not { } c || _dragPage == null) return;
        using var dc = _caretVisual.RenderOpen();
        var r = PageRect(_dragPage);
        var x = r.X + c.X + _columnDragDelta;
        dc.DrawLine(new Pen(new SolidColorBrush(AccentColor), 1.25 / _zoom) { DashStyle = DashStyles.Dash }, new Point(x, r.Y + c.Table.Y), new Point(x, r.Y + c.Table.Y + c.Table.Height));
        _caretVisual.Opacity = 1;
    }
}

public sealed partial class DocumentView
{
    private Paragraph? _anchorTarget;

    /// <summary>A floating picture's anchor mark was dragged onto another paragraph.</summary>
    public event Action<DrawingObject, Paragraph>? AnchorMoved;

    /// <summary>Where the anchor mark of a floating picture is drawn on a page (page coordinates), or null.</summary>
    internal Point? AnchorGlyphPoint(PageLayout page, DrawingObject drawing)
    {
        var placement = page.Floats.FirstOrDefault(x => ReferenceEquals(x.Drawing, drawing));
        if (placement == null || Layout == null) return null;
        var caret = Layout.Caret(new DocPos(placement.Anchor, placement.AnchorOffset), false, page);
        if (caret == null || caret.Value.Page != page) return null;
        return new Point(Math.Max(4, page.Margins.Left - 22), caret.Value.Rect.Top + 2);
    }

    /// <summary>Shows where the anchor would go: a line at the start of the paragraph under the mouse.</summary>
    private void DragAnchor(Point view)
    {
        var at = PageAt(view);
        Guides.Clear();
        _anchorTarget = null;
        if (at != null && Layout != null)
        {
            var (page, point) = at.Value;
            var hit = Layout.HitTest(page, new Point(Math.Max(point.X, page.Margins.Left + 2), point.Y));
            var p = hit?.Position.Para;
            if (p != null && p.Story is MainStory && !Nav.IsReadOnly(p) && Nav.CellOf(p) == null)
            {
                var caret = Layout.Caret(new DocPos(p, 0), false, page);
                if (caret != null && caret.Value.Page == page)
                {
                    _anchorTarget = p;
                    var y = caret.Value.Rect.Top;
                    Guides.Add((page, new Point(page.Margins.Left - 24, y), new Point(page.Size.Width - page.Margins.Right, y)));
                }
            }
        }
        DrawOverlay();
    }
}
