using System.Globalization;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using TextRun = Inkwell.Core.Model.TextRun;
using Inkwell.Core.Model;
using Inkwell.Core.Styles;

namespace Inkwell.Core.Layout;

public enum SegKind : byte
{
    /// <summary>Document text shown 1:1.</summary>
    Text,
    /// <summary>An object (picture, placeholder) for one document position.</summary>
    Object,
    /// <summary>Part of the text shown for one document position (field result, note number, opaque item).</summary>
    Group,
    /// <summary>Document text that isn't shown (hidden text, deleted text in final views).</summary>
    Hidden,
    /// <summary>Text that isn't in the document (the list label and the tab after it).</summary>
    Label,
    /// <summary>A line, page or column break (one document position).</summary>
    LineEnd,
    /// <summary>The paragraph mark.</summary>
    ParaEnd,
}

/// <summary>A piece of a paragraph's text source, mapping document positions to TextFormatter character indices.</summary>
public sealed class Segment
{
    public SegKind Kind;
    public int DocStart;
    public int DocLen;
    public int SrcStart;
    public int SrcLen;
    public string? Chars;
    public int CharsOffset;
    public InkRunProps? Props;
    public TextEmbeddedObject? Object;
    public Inline? Inline;
    public ResolvedRun? Run;

    /// <summary>For LineEnd: '\v' (line), '\f' (page), U+000E (column) or 'c' for a clearing break.</summary>
    public char Break;

    public int SrcEnd => SrcStart + SrcLen;
    public int DocEnd => DocStart + DocLen;

    public override string ToString() => $"{Kind} doc {DocStart}+{DocLen} src {SrcStart}+{SrcLen} '{(Chars == null ? "" : Chars.Substring(CharsOffset, Math.Min(SrcLen, Chars.Length - CharsOffset)))}'";
}

/// <summary>
/// A paragraph's text as TextFormatter sees it: the list label, the text in runs per font slot, objects, hidden
/// parts and breaks, with the mapping between document positions and source indices.
/// </summary>
public sealed class ParaSource : TextSource
{
    private readonly List<Segment> _segments = new();
    private int _cursor;

    private ParaSource(Paragraph paragraph, ResolvedPara props, InkRunProps markProps)
    {
        Paragraph = paragraph;
        Props = props;
        MarkProps = markProps;
    }

    public Paragraph Paragraph { get; }
    public ResolvedPara Props { get; }
    public InkRunProps MarkProps { get; }
    public IReadOnlyList<Segment> Segments => _segments;

    /// <summary>Total source length (including the paragraph mark).</summary>
    public int Length { get; private set; }

    /// <summary>The paragraph's document length.</summary>
    public int DocLength { get; private set; }

    /// <summary>Source length of the list label (+ its tab), 0 when none.</summary>
    public int LabelLength { get; private set; }

    public string? LabelText { get; private set; }

    /// <summary>Floating drawings anchored in this paragraph (document position, drawing).</summary>
    public List<(int Offset, DrawingObject Drawing)> Anchors { get; } = new();

    /// <summary>Footnote and endnote references (document position, reference).</summary>
    public List<(int Offset, NoteReference Note)> NoteRefs { get; } = new();

    /// <summary>The paragraph contains a page or column break character.</summary>
    public bool HasHardBreaks { get; private set; }

    public bool IsRtl => Props.Bidi;

    // ------------------------------------------------------------------ building

    /// <summary>Builds the source for a paragraph.</summary>
    public static ParaSource Build(Paragraph p, LayoutEnv env, TableContext? table = null)
    {
        var styles = env.Styles;
        var view = env.View;
        var rp = styles.ResolvePara(p, table);
        var shading = rp.Shading;
        var markProps = RunPropsFactory.Create(rp.Mark, RunPropsFactory.Slot.HAnsi, view, shading);
        var src = new ParaSource(p, rp, markProps);
        var cache = styles.CreateCache(p, table);

        // List label.
        if (rp.NumId > 0 && env.Labels.TryGetValue(p, out var label) && env.Document.Numbering.Level(rp.NumId, rp.NumLevel) is { } level)
        {
            var labelRun = styles.ResolveLabel(p, level, table);
            if (label.Length > 0)
            {
                src.AddText(SegKind.Label, 0, 0, label, labelRun, view, shading, RunPropsFactory.RevisionMark.None, null, null);
                src.LabelText = label;
            }
            var suffix = level.Suffix switch { "space" => " ", "nothing" => "", _ => "\t" };
            if (suffix.Length > 0) src.AddText(SegKind.Label, 0, 0, suffix, labelRun, view, shading, RunPropsFactory.RevisionMark.None, null, null);
            src.LabelLength = src.Length;
        }

        var pos = 0;
        foreach (var inline in p.Inlines)
        {
            var len = inline.Length;
            var hiddenByRevision = view.Revisions switch
            {
                RevisionView.NoMarkup or RevisionView.Simple => inline.Deleted != null,
                RevisionView.Original => inline.Inserted != null && inline.Deleted == null,
                _ => false,
            };
            var revision = inline.Deleted != null ? RunPropsFactory.RevisionMark.Deleted
                : inline.Inserted != null ? RunPropsFactory.RevisionMark.Inserted : RunPropsFactory.RevisionMark.None;
            if (view.Revisions == RevisionView.Original && inline.Deleted != null) revision = RunPropsFactory.RevisionMark.None;
            Color? revColor = (inline.Deleted ?? inline.Inserted) is { } info ? env.AuthorColor(info.Author) : null;

            switch (inline)
            {
                case TextRun run:
                {
                    var resolved = cache.Get(run.Props);
                    var hidden = hiddenByRevision || (resolved.IsHidden && !view.ShowHiddenText);
                    if (hidden)
                    {
                        src.AddHidden(pos, run.Text.Length, inline);
                        break;
                    }
                    src.AddRunText(pos, run.Text, resolved, view, shading, revision, revColor, inline);
                    break;
                }
                case FieldInline field:
                {
                    if (hiddenByRevision)
                    {
                        src.AddHidden(pos, 1, inline);
                        break;
                    }
                    src.AddField(pos, field, env, cache, shading, revision, revColor);
                    break;
                }
                case NoteReference note:
                {
                    if (note.Kind == NoteKind.Footnote || note.Kind == NoteKind.Endnote) src.NoteRefs.Add((pos, note));
                    if (hiddenByRevision)
                    {
                        src.AddHidden(pos, 1, inline);
                        break;
                    }
                    var number = note.CustomMark ? "" : env.NoteNumbers.TryGetValue((note.Kind, note.Id), out var n) ? n : "?";
                    var resolved = cache.Get(note.Props);
                    if (number.Length == 0) src.AddHidden(pos, 1, inline);
                    else src.AddGroup(pos, number, resolved, view, shading, revision, revColor, inline);
                    break;
                }
                case NoteMark mark:
                {
                    var resolved = cache.Get(mark.Props);
                    var number = env.NoteNumbers.TryGetValue((mark.Kind, NoteIdOf(p)), out var n) ? n : "";
                    if (number.Length == 0) src.AddHidden(pos, 1, inline);
                    else src.AddGroup(pos, number, resolved, view, shading, RunPropsFactory.RevisionMark.None, null, inline);
                    break;
                }
                case NoteSeparator sep:
                {
                    var color = view.DarkPaper ? Color.FromRgb(0x80, 0x80, 0x88) : Color.FromRgb(0x60, 0x60, 0x66);
                    src.AddObject(pos, new SeparatorRun(markProps, sep.Continuation ? 400 : 144, color), inline, null);
                    break;
                }
                case SymbolChar sym:
                {
                    if (hiddenByRevision)
                    {
                        src.AddHidden(pos, 1, inline);
                        break;
                    }
                    var resolved = cache.Get(sym.Props).Copy();
                    resolved.AsciiFont = resolved.HAnsiFont = sym.Font;
                    src.AddGroup(pos, sym.Char.ToString(), resolved, view, shading, revision, revColor, inline, forceSlot: RunPropsFactory.Slot.HAnsi);
                    break;
                }
                case DrawingObject drawing when drawing.IsFloating:
                    if (!hiddenByRevision) src.Anchors.Add((pos, drawing));
                    break;
                case DrawingObject drawing:
                {
                    if (hiddenByRevision)
                    {
                        src.AddHidden(pos, 1, inline);
                        break;
                    }
                    src.AddObject(pos, new DrawingRun(drawing, markProps, env), inline, null);
                    break;
                }
                case ClearBreak:
                    src.AddLineEnd(pos, 'c', inline, markProps);
                    break;
                case OpaqueInline opaque when !opaque.IsMarker:
                {
                    if (hiddenByRevision)
                    {
                        src.AddHidden(pos, 1, inline);
                        break;
                    }
                    if (opaque.WidthPoints > 0 && opaque.HeightPoints > 0)
                    {
                        src.AddObject(pos, new PlaceholderRun(OpaqueLabel(opaque.Kind), markProps, Units.Points(opaque.WidthPoints), Units.Points(opaque.HeightPoints), view.DarkPaper), inline, null);
                    }
                    else
                    {
                        var resolved = cache.Get(opaque.Props).Copy();
                        if (opaque.Kind == "equation")
                        {
                            resolved.AsciiFont = resolved.HAnsiFont = "Cambria Math";
                            resolved.Italic = true;
                        }
                        var text = opaque.Display.Length > 0 ? opaque.Display.Replace('\n', ' ').Replace('\r', ' ') : "[" + OpaqueLabel(opaque.Kind) + "]";
                        src.AddGroup(pos, text, resolved, view, shading, revision, revColor, inline);
                    }
                    break;
                }
            }
            pos += len;
        }
        src.DocLength = pos;
        src.AddParaEnd(pos, markProps);
        return src;
    }

    private static int NoteIdOf(Paragraph p) => p.Story is Note note ? note.Id : 0;

    public static string OpaqueLabel(string kind) => kind switch
    {
        "equation" => "Equation",
        "contentControl" => "Content control",
        "vml" => "Picture",
        "object" => "Embedded object",
        "ruby" => "Ruby text",
        "field" => "Field",
        _ => "Kept as is",
    };

    private void AddSegment(Segment s)
    {
        s.SrcStart = Length;
        Length += s.SrcLen;
        _segments.Add(s);
    }

    private void AddHidden(int docStart, int length, Inline inline)
    {
        if (length <= 0) return;
        AddSegment(new Segment { Kind = SegKind.Hidden, DocStart = docStart, DocLen = length, SrcLen = length, Inline = inline });
    }

    private void AddObject(int docStart, TextEmbeddedObject obj, Inline inline, ResolvedRun? run) =>
        AddSegment(new Segment { Kind = SegKind.Object, DocStart = docStart, DocLen = 1, SrcLen = 1, Object = obj, Inline = inline, Run = run });

    private void AddLineEnd(int docStart, char kind, Inline inline, InkRunProps props)
    {
        if (kind is TextRun.PageBreak or TextRun.ColumnBreak) HasHardBreaks = true;
        AddSegment(new Segment { Kind = SegKind.LineEnd, DocStart = docStart, DocLen = 1, SrcLen = 1, Break = kind, Inline = inline, Props = props });
    }

    private void AddParaEnd(int docStart, InkRunProps props) =>
        AddSegment(new Segment { Kind = SegKind.ParaEnd, DocStart = docStart, DocLen = 0, SrcLen = 1, Props = props });

    /// <summary>Text of a run: breaks become line ends; the rest is split by font slot (and small caps).</summary>
    private void AddRunText(int docStart, string text, ResolvedRun run, ViewOptions view, Color? shading, RunPropsFactory.RevisionMark revision, Color? revColor, Inline inline)
    {
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            var atBreak = i < text.Length && TextRun.IsBreak(text[i]);
            if (i == text.Length || atBreak)
            {
                if (i > start) AddText(SegKind.Text, docStart + start, i - start, text.Substring(start, i - start), run, view, shading, revision, revColor, inline);
                if (atBreak) AddLineEnd(docStart + i, text[i], inline, RunPropsFactory.Create(run, RunPropsFactory.Slot.HAnsi, view, shading));
                start = i + 1;
            }
        }
    }

    /// <summary>Text split by font slot. For Text segments the document length equals the text length.</summary>
    private void AddText(SegKind kind, int docStart, int docLen, string text, ResolvedRun run, ViewOptions view, Color? shading,
        RunPropsFactory.RevisionMark revision, Color? revColor, Inline? inline, RunPropsFactory.Slot? forceSlot = null)
    {
        if (text.Length == 0) return;
        var display = run.Caps || run.SmallCaps ? Upper(text) : text;
        var i = 0;
        while (i < text.Length)
        {
            var slot = forceSlot ?? RunPropsFactory.SlotOf(text[i], run);
            var small = run.SmallCaps && !run.Caps && char.IsLower(text[i]);
            var j = i + 1;
            while (j < text.Length)
            {
                var c = text[j];
                // Spaces and neutral punctuation stay with the run they're in.
                var s = forceSlot ?? (c == ' ' || c == ' ' ? slot : RunPropsFactory.SlotOf(c, run));
                var sm = run.SmallCaps && !run.Caps && char.IsLower(c);
                if (char.IsSurrogate(c)) s = slot;
                if (s != slot || sm != small) break;
                j++;
            }
            var props = RunPropsFactory.Create(run, slot, view, shading, 1.0, revision, revColor, small);
            var seg = new Segment
            {
                Kind = kind,
                DocStart = kind == SegKind.Text ? docStart + i : docStart,
                DocLen = kind == SegKind.Text ? j - i : 0,
                SrcLen = j - i,
                Chars = display,
                CharsOffset = i,
                Props = props,
                Inline = inline,
                Run = run,
            };
            AddSegment(seg);
            i = j;
        }
    }

    private static string Upper(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text) sb.Append(char.ToUpper(c, CultureInfo.CurrentCulture));
        return sb.ToString();
    }

    /// <summary>Text shown for one document position (field results, note numbers, opaque items).</summary>
    private void AddGroup(int docStart, string text, ResolvedRun run, ViewOptions view, Color? shading, RunPropsFactory.RevisionMark revision, Color? revColor,
        Inline inline, RunPropsFactory.Slot? forceSlot = null)
    {
        if (text.Length == 0)
        {
            AddHidden(docStart, 1, inline);
            return;
        }
        var first = _segments.Count;
        AddText(SegKind.Group, docStart, 0, text.Replace('\v', ' ').Replace('\f', ' ').Replace('\u000E', ' '), run, view, shading, revision, revColor, inline, forceSlot);
        for (var k = first; k < _segments.Count; k++)
        {
            _segments[k].DocStart = docStart;
            _segments[k].DocLen = 1;
        }
    }

    private void AddField(int docStart, FieldInline field, LayoutEnv env, ResolveRunCache cache, Color? shading, RunPropsFactory.RevisionMark revision, Color? revColor)
    {
        var computed = FieldDisplay.Evaluate(field, env);
        var first = _segments.Count;
        if (computed != null)
        {
            var props = field.Result.FirstOrDefault(r => r is TextRun)?.Props ?? field.BeginProps ?? field.Props;
            AddText(SegKind.Group, docStart, 0, computed, cache.Get(props), env.View, shading, revision, revColor, field);
        }
        else
        {
            foreach (var r in field.Result)
            {
                switch (r)
                {
                    case TextRun t when t.Text.Length > 0:
                        var resolved = cache.Get(t.Props);
                        if (resolved.IsHidden && !env.View.ShowHiddenText) break;
                        AddText(SegKind.Group, docStart, 0, t.Text.Replace('\v', ' ').Replace('\f', ' ').Replace('\u000E', ' '), resolved, env.View, shading, revision, revColor, field);
                        break;
                    case FieldInline nested:
                        var text = FieldDisplay.Evaluate(nested, env) ?? nested.ResultText;
                        if (text.Length > 0) AddText(SegKind.Group, docStart, 0, text, cache.Get(nested.Result.FirstOrDefault()?.Props ?? nested.Props), env.View, shading, revision, revColor, field);
                        break;
                    case SymbolChar sym:
                        var sr = cache.Get(sym.Props).Copy();
                        sr.AsciiFont = sr.HAnsiFont = sym.Font;
                        AddText(SegKind.Group, docStart, 0, sym.Char.ToString(), sr, env.View, shading, revision, revColor, field, RunPropsFactory.Slot.HAnsi);
                        break;
                }
            }
        }
        if (_segments.Count == first)
        {
            AddHidden(docStart, 1, field);
            return;
        }
        for (var k = first; k < _segments.Count; k++)
        {
            _segments[k].DocStart = docStart;
            _segments[k].DocLen = 1;
            _segments[k].Inline = field;
        }
    }

    // ------------------------------------------------------------------ TextSource

    private Segment? FindBySrc(int index)
    {
        if (_segments.Count == 0) return null;
        // Sequential access is the common case (TextFormatter walks forward).
        if (_cursor < _segments.Count && index >= _segments[_cursor].SrcStart && index < _segments[_cursor].SrcEnd) return _segments[_cursor];
        if (_cursor + 1 < _segments.Count && index >= _segments[_cursor + 1].SrcStart && index < _segments[_cursor + 1].SrcEnd) return _segments[++_cursor];
        int lo = 0, hi = _segments.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var s = _segments[mid];
            if (index < s.SrcStart) hi = mid - 1;
            else if (index >= s.SrcEnd) lo = mid + 1;
            else
            {
                _cursor = mid;
                return s;
            }
        }
        return null;
    }

    public override System.Windows.Media.TextFormatting.TextRun GetTextRun(int textSourceCharacterIndex)
    {
        var seg = FindBySrc(textSourceCharacterIndex);
        if (seg == null) return new TextEndOfParagraph(1, MarkProps);
        var offset = textSourceCharacterIndex - seg.SrcStart;
        switch (seg.Kind)
        {
            case SegKind.Text:
            case SegKind.Group:
            case SegKind.Label:
                return new TextCharacters(seg.Chars!, seg.CharsOffset + offset, seg.SrcLen - offset, seg.Props!);
            case SegKind.Object:
                return seg.Object!;
            case SegKind.Hidden:
                return new TextHidden(seg.SrcLen - offset);
            case SegKind.LineEnd:
                return new TextEndOfLine(1, seg.Props);
            default:
                return new TextEndOfParagraph(1, seg.Props ?? MarkProps);
        }
    }

    public override TextSpan<CultureSpecificCharacterBufferRange> GetPrecedingText(int textSourceCharacterIndexLimit)
    {
        var seg = FindBySrc(textSourceCharacterIndexLimit - 1);
        if (seg != null && seg.Chars != null && seg.Kind is SegKind.Text or SegKind.Group or SegKind.Label)
        {
            var count = textSourceCharacterIndexLimit - seg.SrcStart;
            return new TextSpan<CultureSpecificCharacterBufferRange>(count,
                new CultureSpecificCharacterBufferRange(seg.Props!.CultureInfo, new CharacterBufferRange(seg.Chars, seg.CharsOffset, count)));
        }
        return new TextSpan<CultureSpecificCharacterBufferRange>(0, new CultureSpecificCharacterBufferRange(CultureInfo.InvariantCulture, CharacterBufferRange.Empty));
    }

    public override int GetTextEffectCharacterIndexFromTextSourceCharacterIndex(int textSourceCharacterIndex) => textSourceCharacterIndex;

    // ------------------------------------------------------------------ mapping

    /// <summary>The source index for a document position (the start of the item there).</summary>
    public int DocToSrc(int doc)
    {
        if (doc <= 0) return LabelLength > 0 && _segments.Count > 0 ? FirstContentSrc() : 0;
        foreach (var s in _segments)
        {
            if (s.Kind == SegKind.Label) continue;
            if (s.Kind == SegKind.ParaEnd) return s.SrcStart;
            if (doc >= s.DocStart && doc < s.DocEnd)
                return s.Kind is SegKind.Text or SegKind.Hidden ? s.SrcStart + (doc - s.DocStart) : s.SrcStart;
        }
        return Math.Max(0, Length - 1);
    }

    private int FirstContentSrc()
    {
        foreach (var s in _segments)
            if (s.Kind != SegKind.Label) return s.SrcStart;
        return LabelLength;
    }

    /// <summary>The document position for a source index plus trailing length (a CharacterHit).</summary>
    public int SrcToDoc(int src, int trailing = 0)
    {
        var seg = FindBySrc(src);
        if (seg == null) return DocLength;
        switch (seg.Kind)
        {
            case SegKind.Label:
                return 0;
            case SegKind.Text:
            case SegKind.Hidden:
                return Math.Min(DocLength, seg.DocStart + (src - seg.SrcStart) + (trailing > 0 ? 1 : 0));
            case SegKind.Object:
            case SegKind.LineEnd:
                return Math.Min(DocLength, seg.DocStart + (trailing > 0 ? 1 : 0));
            case SegKind.Group:
            {
                // Inside a field / note number: before or after the whole thing, by which half was hit.
                var groupStart = seg;
                var groupEnd = seg;
                var index = _segments.IndexOf(seg);
                while (index > 0 && _segments[index - 1].Kind == SegKind.Group && _segments[index - 1].DocStart == seg.DocStart) groupStart = _segments[--index];
                index = _segments.IndexOf(seg);
                while (index + 1 < _segments.Count && _segments[index + 1].Kind == SegKind.Group && _segments[index + 1].DocStart == seg.DocStart) groupEnd = _segments[++index];
                var mid = (groupStart.SrcStart + groupEnd.SrcEnd) / 2.0;
                return src + trailing > mid ? seg.DocStart + 1 : seg.DocStart;
            }
            default:
                return DocLength;
        }
    }

    /// <summary>Source range [start, end) covering document positions [docStart, docEnd).</summary>
    public (int Start, int End) DocRangeToSrc(int docStart, int docEnd)
    {
        var start = -1;
        var end = -1;
        foreach (var s in _segments)
        {
            if (s.Kind is SegKind.Label or SegKind.ParaEnd) continue;
            if (s.DocEnd <= docStart || s.DocStart >= docEnd)
            {
                if (s.DocLen == 0 && s.DocStart >= docStart && s.DocStart < docEnd) { }
                else continue;
            }
            int a, b;
            if (s.Kind is SegKind.Text or SegKind.Hidden)
            {
                a = s.SrcStart + Math.Max(0, docStart - s.DocStart);
                b = s.SrcStart + Math.Min(s.DocLen, docEnd - s.DocStart);
            }
            else
            {
                a = s.SrcStart;
                b = s.SrcEnd;
            }
            if (start < 0 || a < start) start = a;
            if (b > end) end = b;
        }
        if (start < 0)
        {
            var at = DocToSrc(docStart);
            return (at, at);
        }
        return (start, end);
    }

    /// <summary>The segment kind at a document position (for caret movement: skipping hidden parts).</summary>
    public bool IsHiddenAt(int doc)
    {
        foreach (var s in _segments)
            if (s.Kind == SegKind.Hidden && doc >= s.DocStart && doc < s.DocEnd) return true;
        return false;
    }
}

/// <summary>What a field shows: computed values for fields Inkwell evaluates live, otherwise the saved result.</summary>
public static class FieldDisplay
{
    /// <summary>The text to show instead of the saved result, or null to show the saved result.</summary>
    public static string? Evaluate(FieldInline field, LayoutEnv env)
    {
        if (field.Locked || field.PreservedXml != null) return null;
        var kind = field.Kind;
        var ctx = env.Fields;
        switch (kind)
        {
            case FieldKind.Page when ctx != null:
            {
                var code = FieldCode.Parse(field.Code);
                return code.NumberFormat != null ? FieldCode.FormatNumber(ctx.PageNumber, code.NumberFormat) : NumberFormats.Format(ctx.PageNumber, ctx.PageFormat);
            }
            case FieldKind.NumPages when ctx != null:
            {
                var code = FieldCode.Parse(field.Code);
                return FieldCode.FormatNumber(ctx.PageCount, code.NumberFormat);
            }
            case FieldKind.SectionPages when ctx != null:
                return FieldCode.FormatNumber(ctx.SectionPageCount, FieldCode.Parse(field.Code).NumberFormat);
            case FieldKind.Title:
                return env.Document.Properties.Title ?? (field.ResultText.Length > 0 ? null : "");
            case FieldKind.Author:
                return env.Document.Properties.Creator ?? (field.ResultText.Length > 0 ? null : "");
            case FieldKind.Subject:
                return env.Document.Properties.Subject ?? (field.ResultText.Length > 0 ? null : "");
            case FieldKind.FileName when ctx?.FileName != null:
                return ctx.FileName;
            case FieldKind.StyleRef when ctx?.StyleRef != null:
            {
                var code = FieldCode.Parse(field.Code);
                var style = code.FirstArgument;
                return style == null ? null : ctx.StyleRef(style) ?? "";
            }
            case FieldKind.Date when ctx != null || field.ResultText.Length == 0:
            {
                var code = FieldCode.Parse(field.Code);
                var format = code.SwitchValue("\\@");
                return FormatDate(DateTime.Now, format);
            }
            default:
                return null;
        }
    }

    /// <summary>Formats a date with a Word date picture (d MMMM yyyy, dd/MM/yy, h:mm am/pm…).</summary>
    public static string FormatDate(DateTime when, string? picture)
    {
        if (string.IsNullOrWhiteSpace(picture)) return when.ToString("d", CultureInfo.CurrentCulture);
        var net = picture!.Replace("am/pm", "tt").Replace("AM/PM", "tt").Replace("'", "");
        try
        {
            return when.ToString(net, CultureInfo.CurrentCulture);
        }
        catch (FormatException)
        {
            return when.ToString("d", CultureInfo.CurrentCulture);
        }
    }
}
