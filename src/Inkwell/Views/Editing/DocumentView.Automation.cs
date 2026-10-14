using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;
using Inkwell.Core.Editing;
using Inkwell.Core.Layout;
using Inkwell.Core.Model;

namespace Inkwell.Views.Editing;

/// <summary>Screen reader support: the document exposes the UI Automation Text pattern (read, navigate, select).</summary>
public sealed partial class DocumentView
{
    private DocumentAutomationPeer? _peer;

    protected override AutomationPeer OnCreateAutomationPeer() => _peer = new DocumentAutomationPeer(this);

    private void AutomationChanged()
    {
        if (_peer != null && AutomationPeer.ListenerExists(AutomationEvents.TextPatternOnTextChanged))
            _peer.RaiseAutomationEvent(AutomationEvents.TextPatternOnTextChanged);
    }

    private void AutomationSelectionChanged()
    {
        if (_peer != null && AutomationPeer.ListenerExists(AutomationEvents.TextPatternOnTextSelectionChanged))
            _peer.RaiseAutomationEvent(AutomationEvents.TextPatternOnTextSelectionChanged);
    }

    internal Point ScreenPoint(Point view) => PointToScreen(view);

    internal Point FromScreen(Point screen) => PointFromScreen(screen);
}

internal sealed class DocumentAutomationPeer : FrameworkElementAutomationPeer
{
    private readonly DocTextProvider _text;

    public DocumentAutomationPeer(DocumentView owner) : base(owner)
    {
        _text = new DocTextProvider(owner, this);
    }

    public DocumentView View => (DocumentView)Owner;

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;

    protected override string GetClassNameCore() => "InkwellDocument";

    protected override string GetNameCore() => "Document";

    protected override bool IsKeyboardFocusableCore() => true;

    public override object? GetPattern(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Text ? _text : base.GetPattern(patternInterface);

    public IRawElementProviderSimple Provider => ProviderFromPeer(this);
}

internal sealed class DocTextProvider : ITextProvider
{
    private readonly DocumentView _view;
    private readonly DocumentAutomationPeer _peer;

    public DocTextProvider(DocumentView view, DocumentAutomationPeer peer)
    {
        _view = view;
        _peer = peer;
    }

    internal DocumentView View => _view;
    internal DocumentAutomationPeer Peer => _peer;

    private Story Story => _view.Editor?.Story ?? _view.Editor!.Doc.Body;

    public ITextRangeProvider DocumentRange => new DocTextRange(this, Nav.StartOf(Story), Nav.EndOf(Story));

    public SupportedTextSelection SupportedTextSelection => SupportedTextSelection.Single;

    public ITextRangeProvider[] GetSelection()
    {
        var s = _view.Editor?.Selection;
        if (s == null || s.Value.Active.Para == null) return Array.Empty<ITextRangeProvider>();
        return new ITextRangeProvider[] { new DocTextRange(this, s.Value.Start, s.Value.End) };
    }

    public ITextRangeProvider[] GetVisibleRanges()
    {
        var layout = _view.Layout;
        var page = _view.CurrentPage;
        if (layout == null || page == null) return Array.Empty<ITextRangeProvider>();
        var paragraphs = page.Columns.SelectMany(c => c.Content).OfType<ParaFragment>().ToList();
        if (paragraphs.Count == 0) return Array.Empty<ITextRangeProvider>();
        var first = paragraphs[0];
        var last = paragraphs[paragraphs.Count - 1];
        var start = new DocPos(first.Paragraph, first.Lines.FirstOrDefault()?.DocStart ?? 0);
        var lastLine = last.Lines.LastOrDefault();
        var end = new DocPos(last.Paragraph, lastLine == null ? 0 : lastLine.EndsParagraph ? last.Paragraph.Length : lastLine.DocEnd);
        return new ITextRangeProvider[] { new DocTextRange(this, start, end) };
    }

    public ITextRangeProvider? RangeFromChild(IRawElementProviderSimple childElement) => null;

    public ITextRangeProvider RangeFromPoint(Point screenLocation)
    {
        var view = _view.FromScreen(screenLocation);
        var at = _view.PageAt(view);
        if (at != null && _view.Layout != null && _view.Layout.HitTest(at.Value.Page, at.Value.Point) is { } hit && hit.Position.Para != null)
            return new DocTextRange(this, hit.Position, hit.Position);
        var start = Nav.StartOf(Story);
        return new DocTextRange(this, start, start);
    }
}

internal sealed class DocTextRange : ITextRangeProvider
{
    private readonly DocTextProvider _provider;
    private DocPos _start;
    private DocPos _end;

    public DocTextRange(DocTextProvider provider, DocPos start, DocPos end)
    {
        _provider = provider;
        if (Nav.Compare(start, end) > 0) (start, end) = (end, start);
        _start = start;
        _end = end;
    }

    private DocumentView View => _provider.View;

    public ITextRangeProvider Clone() => new DocTextRange(_provider, _start, _end);

    public bool Compare(ITextRangeProvider range) => range is DocTextRange r && r._start == _start && r._end == _end;

    public int CompareEndpoints(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint)
    {
        if (targetRange is not DocTextRange r) return 0;
        var a = endpoint == TextPatternRangeEndpoint.Start ? _start : _end;
        var b = targetEndpoint == TextPatternRangeEndpoint.Start ? r._start : r._end;
        return Math.Sign(Nav.Compare(a, b));
    }

    public void ExpandToEnclosingUnit(TextUnit unit)
    {
        var p = _start.Para;
        switch (unit)
        {
            case TextUnit.Character:
                _end = _start.Offset < p.Length ? new DocPos(p, DocEditor.NextPosition(p, _start.Offset)) : _start;
                break;
            case TextUnit.Format:
            case TextUnit.Word:
            {
                var (s, e) = DocEditor.WordAt(p, _start.Offset);
                _start = new DocPos(p, s);
                _end = new DocPos(p, e);
                break;
            }
            case TextUnit.Line:
            {
                var caret = View.Layout?.Caret(_start);
                if (caret != null)
                {
                    var (s, e) = HitTesting.LineBounds(caret.Value);
                    _start = new DocPos(p, s);
                    _end = new DocPos(p, e);
                }
                else
                {
                    _start = new DocPos(p, 0);
                    _end = new DocPos(p, p.Length);
                }
                break;
            }
            case TextUnit.Paragraph:
            case TextUnit.Page:
                _start = new DocPos(p, 0);
                _end = new DocPos(p, p.Length);
                break;
            default:
                var story = p.Story!;
                _start = Nav.StartOf(story);
                _end = Nav.EndOf(story);
                break;
        }
    }

    public ITextRangeProvider? FindAttribute(int attribute, object value, bool backward) => null;

    public ITextRangeProvider? FindText(string text, bool backward, bool ignoreCase)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var comparison = ignoreCase ? StringComparison.CurrentCultureIgnoreCase : StringComparison.CurrentCulture;
        var paragraphs = Nav.Range(_start.Para, _end.Para).ToList();
        if (backward) paragraphs.Reverse();
        foreach (var p in paragraphs)
        {
            var t = p.Text;
            var from = ReferenceEquals(p, _start.Para) ? _start.Offset : 0;
            var to = ReferenceEquals(p, _end.Para) ? _end.Offset : t.Length;
            if (to <= from) continue;
            var segment = t.Substring(from, to - from);
            var i = backward ? segment.LastIndexOf(text, comparison) : segment.IndexOf(text, comparison);
            if (i >= 0) return new DocTextRange(_provider, new DocPos(p, from + i), new DocPos(p, from + i + text.Length));
        }
        return null;
    }

    public object GetAttributeValue(int attribute)
    {
        var editor = View.Editor;
        if (editor == null) return AutomationElementIdentifiers.NotSupported;
        var p = _start.Para;
        var run = editor.Styles.ResolveRun(p.PropsForTypingAt(Math.Min(p.Length, _start.Offset + 1)), p);
        if (attribute == TextPattern.FontNameAttribute.Id) return run.HAnsiFont;
        if (attribute == TextPattern.FontSizeAttribute.Id) return run.Size;
        if (attribute == TextPattern.FontWeightAttribute.Id) return run.Bold ? 700 : 400;
        if (attribute == TextPattern.IsItalicAttribute.Id) return run.Italic;
        if (attribute == TextPattern.IsReadOnlyAttribute.Id) return Nav.IsReadOnly(p);
        if (attribute == TextPattern.UnderlineStyleAttribute.Id) return run.Underlined ? TextDecorationLineStyle.Single : TextDecorationLineStyle.None;
        if (attribute == TextPattern.IsHiddenAttribute.Id) return run.IsHidden;
        if (attribute == TextPattern.CultureAttribute.Id) return Core.Layout.RunPropsFactory.Culture(run.Lang);
        return AutomationElementIdentifiers.NotSupported;
    }

    public double[] GetBoundingRectangles()
    {
        var layout = View.Layout;
        if (layout == null) return Array.Empty<double>();
        var result = new List<double>();
        foreach (var page in layout.Pages)
        {
            var rects = _start == _end
                ? (layout.Caret(_start) is { } c && c.Page == page ? new List<Rect> { new(c.Rect.X, c.Rect.Y, 1, c.Rect.Height) } : new List<Rect>())
                : layout.SelectionRects(new Selection(_start, _end), page);
            foreach (var r in rects)
            {
                var tl = View.ScreenPoint(View.PageToView(page, r.TopLeft));
                var br = View.ScreenPoint(View.PageToView(page, r.BottomRight));
                result.AddRange(new[] { tl.X, tl.Y, Math.Max(1, br.X - tl.X), Math.Max(1, br.Y - tl.Y) });
            }
            if (result.Count > 4000) break;
        }
        return result.ToArray();
    }

    public IRawElementProviderSimple GetEnclosingElement() => _provider.Peer.Provider;

    public string GetText(int maxLength)
    {
        var sb = new StringBuilder();
        foreach (var p in Nav.Range(_start.Para, _end.Para))
        {
            var text = p.Text.Replace('￼', ' ');
            var s = ReferenceEquals(p, _start.Para) ? _start.Offset : 0;
            var e = ReferenceEquals(p, _end.Para) ? _end.Offset : text.Length;
            if (e > s) sb.Append(text, s, Math.Min(e, text.Length) - s);
            if (!ReferenceEquals(p, _end.Para)) sb.Append("\r\n");
            if (maxLength >= 0 && sb.Length >= maxLength) break;
        }
        var result = sb.ToString();
        return maxLength >= 0 && result.Length > maxLength ? result.Substring(0, maxLength) : result;
    }

    public int Move(TextUnit unit, int count)
    {
        if (count == 0) return 0;
        var moved = MoveEndpoint(ref _start, unit, count);
        _end = _start;
        ExpandToEnclosingUnit(unit == TextUnit.Character ? TextUnit.Character : unit);
        return moved;
    }

    public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count)
    {
        var pos = endpoint == TextPatternRangeEndpoint.Start ? _start : _end;
        var moved = MoveEndpoint(ref pos, unit, count);
        if (endpoint == TextPatternRangeEndpoint.Start)
        {
            _start = pos;
            if (Nav.Compare(_start, _end) > 0) _end = _start;
        }
        else
        {
            _end = pos;
            if (Nav.Compare(_start, _end) > 0) _start = _end;
        }
        return moved;
    }

    private static int MoveEndpoint(ref DocPos pos, TextUnit unit, int count)
    {
        var moved = 0;
        var step = Math.Sign(count);
        for (var i = 0; i < Math.Abs(count); i++)
        {
            var p = pos.Para;
            DocPos next;
            switch (unit)
            {
                case TextUnit.Character:
                    if (step > 0)
                        next = pos.Offset < p.Length ? new DocPos(p, DocEditor.NextPosition(p, pos.Offset)) : Nav.Next(p) is { } np ? new DocPos(np, 0) : pos;
                    else
                        next = pos.Offset > 0 ? new DocPos(p, DocEditor.PreviousPosition(p, pos.Offset)) : Nav.Prev(p) is { } pp ? new DocPos(pp, pp.Length) : pos;
                    break;
                case TextUnit.Word:
                case TextUnit.Format:
                    if (step > 0)
                        next = pos.Offset < p.Length ? new DocPos(p, DocEditor.WordEndAfter(p, pos.Offset)) : Nav.Next(p) is { } np2 ? new DocPos(np2, 0) : pos;
                    else
                        next = pos.Offset > 0 ? new DocPos(p, DocEditor.WordStartBefore(p, pos.Offset)) : Nav.Prev(p) is { } pp2 ? new DocPos(pp2, DocEditor.WordStartBefore(pp2, pp2.Length)) : pos;
                    break;
                case TextUnit.Document:
                    next = step > 0 ? Nav.EndOf(p.Story!) : Nav.StartOf(p.Story!);
                    break;
                default:
                    if (step > 0) next = Nav.Next(p) is { } np3 ? new DocPos(np3, 0) : new DocPos(p, p.Length);
                    else next = pos.Offset > 0 ? new DocPos(p, 0) : Nav.Prev(p) is { } pp3 ? new DocPos(pp3, 0) : pos;
                    break;
            }
            if (next == pos) break;
            pos = next;
            moved += step;
        }
        return moved;
    }

    public void MoveEndpointByRange(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint)
    {
        if (targetRange is not DocTextRange r) return;
        var target = targetEndpoint == TextPatternRangeEndpoint.Start ? r._start : r._end;
        if (endpoint == TextPatternRangeEndpoint.Start)
        {
            _start = target;
            if (Nav.Compare(_start, _end) > 0) _end = _start;
        }
        else
        {
            _end = target;
            if (Nav.Compare(_start, _end) > 0) _start = _end;
        }
    }

    public void Select()
    {
        if (View.Editor != null) View.Editor.Selection = new Selection(_start, _end);
    }

    public void AddToSelection() => throw new InvalidOperationException("Inkwell supports one selection.");

    public void RemoveFromSelection() => throw new InvalidOperationException("Inkwell supports one selection.");

    public void ScrollIntoView(bool alignToTop)
    {
        if (View.Editor == null) return;
        View.Editor.Selection = new Selection(_start);
        View.ScrollToCaret(center: !alignToTop);
    }

    public IRawElementProviderSimple[] GetChildren() => Array.Empty<IRawElementProviderSimple>();
}
