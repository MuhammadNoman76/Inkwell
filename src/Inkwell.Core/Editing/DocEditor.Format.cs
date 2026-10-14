using System.Xml.Linq;
using Inkwell.Core.Model;
using Inkwell.Core.Styles;

namespace Inkwell.Core.Editing;

/// <summary>The formatting at the selection, for the command bar and panel (null = mixed).</summary>
public sealed class FormatState
{
    public bool? Bold;
    public bool? Italic;
    public bool? Underline;
    public bool? Strike;
    public bool? Superscript;
    public bool? Subscript;
    public string? Font;
    public double? Size;
    public string? ParagraphStyle;
    public string? CharacterStyle;
    public string? Alignment;
    public bool? Bullets;
    public bool? Numbers;
    public bool? Rtl;
    public int? Line;
    public string? LineRule;
    public int? SpaceBefore;
    public int? SpaceAfter;
    public string? Color;
    public string? Highlight;
    public bool InTable;
    public bool HasLink;
}

public sealed partial class DocEditor
{
    private StyleResolver? _styles;

    /// <summary>A style resolver for this document (queries; layout has its own).</summary>
    public StyleResolver Styles => _styles ??= new StyleResolver(Doc);

    /// <summary>Paragraphs touched by the selection (start to end, in order).</summary>
    public List<Paragraph> SelectedParagraphs()
    {
        var s = Selection;
        if (s.InOneParagraph) return new List<Paragraph> { s.Active.Para };
        return Nav.Range(s.Start.Para, s.End.Para).ToList();
    }

    /// <summary>The ranges of each selected paragraph: (paragraph, start, end).</summary>
    public List<(Paragraph Para, int Start, int End)> SelectedRanges()
    {
        var s = Selection;
        var list = new List<(Paragraph, int, int)>();
        foreach (var p in SelectedParagraphs())
        {
            var start = ReferenceEquals(p, s.Start.Para) ? s.Start.Offset : 0;
            var end = ReferenceEquals(p, s.End.Para) ? s.End.Offset : p.Length;
            list.Add((p, start, end));
        }
        return list;
    }

    // ------------------------------------------------------------------ state

    public FormatState GetFormatState()
    {
        var state = new FormatState();
        var s = Selection;
        var resolver = Styles;
        var runs = new List<(ResolvedRun Run, Inline Inline)>();
        if (s.IsEmpty)
        {
            var p = s.Active.Para;
            var props = PendingProps ?? p.PropsForTypingAt(s.Active.Offset);
            runs.Add((resolver.ResolveRun(props, p), new TextRun()));
            state.HasLink = LinkAt(p, s.Active.Offset) != null || (s.Active.Offset > 0 && p.InlineAt(s.Active.Offset - 1)?.Link != null);
        }
        else
        {
            foreach (var (p, start, end) in SelectedRanges())
            {
                var pos = 0;
                foreach (var inline in p.Inlines)
                {
                    var len = inline.Length;
                    if (len > 0 && pos < end && pos + len > start && inline is TextRun or FieldInline or SymbolChar)
                    {
                        runs.Add((resolver.ResolveRun(inline.Props, p), inline));
                        if (inline.Link != null) state.HasLink = true;
                    }
                    pos += len;
                    if (runs.Count > 400) break;
                }
                if (runs.Count > 400) break;
            }
            if (runs.Count == 0)
            {
                var p = s.Start.Para;
                runs.Add((resolver.ResolveRun(p.PropsForTypingAt(s.Start.Offset), p), new TextRun()));
            }
        }
        T? Same<T>(Func<ResolvedRun, T> get)
        {
            var first = get(runs[0].Run);
            return runs.All(r => EqualityComparer<T>.Default.Equals(get(r.Run), first)) ? first : default;
        }
        bool? SameBool(Func<ResolvedRun, bool> get)
        {
            var first = get(runs[0].Run);
            return runs.All(r => get(r.Run) == first) ? first : null;
        }
        state.Bold = SameBool(r => r.Bold);
        state.Italic = SameBool(r => r.Italic);
        state.Underline = SameBool(r => r.Underlined);
        state.Strike = SameBool(r => r.Strike);
        state.Superscript = SameBool(r => r.VertAlign == 1);
        state.Subscript = SameBool(r => r.VertAlign == 2);
        state.Font = Same(r => r.HAnsiFont);
        var size = runs[0].Run.Size;
        state.Size = runs.All(r => Math.Abs(r.Run.Size - size) < 0.01) ? size : null;
        state.CharacterStyle = Same(r => r.CharStyleId);
        state.Color = Same(r => r.Color is { } c ? OoxColor.ToHex(c) : "auto");
        state.Highlight = runs.Select(r => r.Inline.Props.Highlight).Distinct().Count() == 1 ? runs[0].Inline.Props.Highlight : null;

        var paragraphs = SelectedParagraphs().Take(400).ToList();
        var resolvedParas = paragraphs.Select(p => resolver.ResolvePara(p)).ToList();
        string? SameStr(Func<ResolvedPara, string?> get) => resolvedParas.Select(get).Distinct().Count() == 1 ? get(resolvedParas[0]) : null;
        state.ParagraphStyle = SameStr(r => r.StyleId);
        state.Alignment = SameStr(r => r.Justification switch { "start" => "left", "end" => "right", var j => j });
        var listKinds = resolvedParas.Select(r => r.NumId > 0 ? Doc.Numbering.Level(r.NumId, r.NumLevel)?.IsBullet == true ? "b" : "n" : "").Distinct().ToList();
        state.Bullets = listKinds.Count == 1 ? listKinds[0] == "b" : null;
        state.Numbers = listKinds.Count == 1 ? listKinds[0] == "n" : null;
        state.Rtl = resolvedParas.Select(r => r.Bidi).Distinct().Count() == 1 ? resolvedParas[0].Bidi : null;
        state.Line = resolvedParas.Select(r => r.Line).Distinct().Count() == 1 ? resolvedParas[0].Line : null;
        state.LineRule = SameStr(r => r.LineRule);
        state.SpaceBefore = resolvedParas.Select(r => r.SpacingBefore).Distinct().Count() == 1 ? resolvedParas[0].SpacingBefore : null;
        state.SpaceAfter = resolvedParas.Select(r => r.SpacingAfter).Distinct().Count() == 1 ? resolvedParas[0].SpacingAfter : null;
        state.InTable = Nav.CellOf(s.Active.Para) != null;
        return state;
    }

    // ------------------------------------------------------------------ character formatting

    /// <summary>
    /// Applies a character format to the selection. With an empty selection inside a word, Word applies it to
    /// the whole word; at a word boundary it applies to what's typed next.
    /// </summary>
    public void ApplyRunFormat(string label, Action<RunProps> change, bool wholeWordWhenEmpty = true)
    {
        var s = Selection;
        if (s.IsEmpty)
        {
            var p = s.Active.Para;
            var (ws, we) = WordAt(p, s.Active.Offset);
            var text = p.Text;
            var inside = wholeWordWhenEmpty && s.Active.Offset > ws && s.Active.Offset < we && we - ws > 0 && s.Active.Offset < text.Length && IsWordChar(text[s.Active.Offset]);
            if (!inside)
            {
                var pending = (PendingProps ?? p.PropsForTypingAt(s.Active.Offset)).Clone();
                change(pending);
                PendingProps = pending;
                NotifySelection();
                return;
            }
            // Trim the trailing space WordAt includes.
            while (we > ws && text[we - 1] == ' ') we--;
            var caret = s.Active;
            Edit(label, t =>
            {
                FormatRange(t, p, ws, we, change);
                return new Selection(caret);
            });
            return;
        }
        Edit(label, t =>
        {
            foreach (var (p, start, end) in SelectedRanges())
            {
                if (Nav.IsReadOnly(p)) continue;
                FormatRange(t, p, start, end, change);
                // A whole paragraph selected: its mark takes the format too (empty lines keep it).
                if (start == 0 && end >= p.Length && !ReferenceEquals(p, Selection.End.Para))
                    change(p.Props.MarkProps(create: true)!);
            }
            return Selection;
        });
    }

    private void NotifySelection() => SelectionChanged?.Invoke();

    /// <summary>Formats [start, end) of a paragraph, recording tracked formatting changes when on.</summary>
    public void FormatRange(Transaction t, Paragraph p, int start, int end, Action<RunProps> change)
    {
        if (end <= start) return;
        t.Touch(p);
        var first = p.SplitAt(start);
        var last = p.SplitAt(end);
        var rev = TrackChanges ? NewRevision() : null;
        for (var i = first; i < last; i++)
        {
            var inline = p.Inlines[i];
            if (inline.Length == 0 && inline is not DrawingObject) continue;
            var before = rev != null && inline.Props.Change == null ? inline.Props.Clone() : null;
            change(inline.Props);
            if (inline is FieldInline f)
                foreach (var r in f.Result) change(r.Props);
            if (before != null && !before.SameFormatting(inline.Props) && inline.Inserted == null) inline.Props.SetChange(rev, before);
        }
        p.Normalize();
    }

    private bool AllRuns(Func<ResolvedRun, bool> test)
    {
        var state = GetFormatState();
        _ = state;
        var resolver = Styles;
        var s = Selection;
        if (s.IsEmpty)
        {
            var p = s.Active.Para;
            return test(resolver.ResolveRun(PendingProps ?? p.PropsForTypingAt(s.Active.Offset), p));
        }
        foreach (var (p, start, end) in SelectedRanges())
        {
            var pos = 0;
            foreach (var inline in p.Inlines)
            {
                if (inline.Length > 0 && pos < end && pos + inline.Length > start && inline is TextRun or FieldInline or SymbolChar)
                    if (!test(resolver.ResolveRun(inline.Props, p))) return false;
                pos += inline.Length;
            }
        }
        return true;
    }

    public void ToggleBold()
    {
        var on = !AllRuns(r => r.Bold);
        ApplyRunFormat(on ? "Bold" : "Not bold", r =>
        {
            r.Bold = on;
            r.BoldCs = on;
        });
    }

    public void ToggleItalic()
    {
        var on = !AllRuns(r => r.Italic);
        ApplyRunFormat(on ? "Italic" : "Not italic", r =>
        {
            r.Italic = on;
            r.ItalicCs = on;
        });
    }

    public void ToggleUnderline()
    {
        var on = !AllRuns(r => r.Underlined);
        ApplyRunFormat(on ? "Underline" : "No underline", r => r.Underline = on ? "single" : "none");
    }

    public void ToggleStrike()
    {
        var on = !AllRuns(r => r.Strike);
        ApplyRunFormat(on ? "Strikethrough" : "No strikethrough", r => r.Strike = on);
    }

    public void ToggleSuperscript()
    {
        var on = !AllRuns(r => r.VertAlign == 1);
        ApplyRunFormat("Superscript", r => r.VertAlign = on ? "superscript" : null);
    }

    public void ToggleSubscript()
    {
        var on = !AllRuns(r => r.VertAlign == 2);
        ApplyRunFormat("Subscript", r => r.VertAlign = on ? "subscript" : null);
    }

    public void ToggleSmallCaps()
    {
        var on = !AllRuns(r => r.SmallCaps);
        ApplyRunFormat("Small capitals", r => r.SmallCaps = on);
    }

    public void SetFont(string name)
    {
        var complex = SelectedRanges().Any(r => r.Para.Text.Substring(r.Start, Math.Max(0, r.End - r.Start)).Any(Layout.RunPropsFactory.IsComplex));
        ApplyRunFormat("Font", r =>
        {
            r.SetLatinFont(name);
            r.SetAttr("rFonts", "eastAsiaTheme", null);
            if (complex) r.SetComplexFont(name);
        }, wholeWordWhenEmpty: false);
    }

    public void SetFontSize(double points)
    {
        var half = (int)Math.Round(MathEx.Clamp(points, 1, 1638) * 2);
        ApplyRunFormat("Font size", r =>
        {
            r.Size = half;
            r.SizeCs = half;
        }, wholeWordWhenEmpty: false);
    }

    public static readonly double[] StandardSizes = { 8, 9, 10, 10.5, 11, 12, 14, 16, 18, 20, 22, 24, 28, 32, 36, 40, 48, 60, 72, 96 };

    /// <summary>Ctrl+] / Ctrl+[ (or Ctrl+Shift+&gt; / &lt;): the next standard size up or down.</summary>
    public void GrowFont(bool larger)
    {
        var current = GetFormatState().Size ?? 11;
        var next = larger ? StandardSizes.FirstOrDefault(s => s > current + 0.01) : StandardSizes.LastOrDefault(s => s < current - 0.01);
        if (next <= 0) next = larger ? current + 10 : Math.Max(1, current - 1);
        SetFontSize(next);
    }

    /// <summary>Text colour: "RRGGBB", or null for automatic.</summary>
    public void SetColor(string? hex, string? themeColor = null) => ApplyRunFormat("Text colour", r =>
    {
        r.Remove("color");
        if (hex == null) return;
        r.Color = hex;
        if (themeColor != null) r.SetAttr("color", "themeColor", themeColor);
    }, wholeWordWhenEmpty: false);

    /// <summary>Highlight colour name (yellow, green…), or null to remove.</summary>
    public void SetHighlight(string? name) => ApplyRunFormat("Highlight", r => r.Highlight = name, wholeWordWhenEmpty: false);

    public void SetCharacterStyle(string? styleId) => ApplyRunFormat("Character style", r => r.StyleId = styleId, wholeWordWhenEmpty: true);

    /// <summary>Removes direct character formatting (Ctrl+Space); links keep their link style.</summary>
    public void ClearCharacterFormatting()
    {
        ApplyRunFormat("Clear character formatting", ClearRun, wholeWordWhenEmpty: false);
    }

    private static void ClearRun(RunProps r)
    {
        var keep = r.X.Elements().Where(e => e.Name.LocalName is "ins" or "del" or "rPrChange" || (e.Name.LocalName == "rStyle" && e.Attr("val") is "Hyperlink" or "FootnoteReference" or "EndnoteReference" or "CommentReference")).ToList();
        foreach (var e in r.X.Elements().ToList())
            if (!keep.Contains(e)) e.Remove();
    }

    /// <summary>
    /// One-click Clear formatting: removes direct character and paragraph formatting, keeping styles, lists,
    /// section breaks and text direction.
    /// </summary>
    public void ClearFormatting()
    {
        Edit("Clear formatting", t =>
        {
            var ranges = Selection.IsEmpty ? new List<(Paragraph, int, int)> { (Selection.Active.Para, 0, Selection.Active.Para.Length) } : SelectedRanges();
            foreach (var (p, start, end) in ranges)
            {
                if (Nav.IsReadOnly(p)) continue;
                t.Touch(p);
                FormatRange(t, p, start, end, ClearRun);
                ClearParagraph(p.Props);
                var mark = p.Props.MarkProps();
                if (mark != null) ClearRun(mark);
            }
            return Selection;
        });
    }

    private static void ClearParagraph(ParaProps props)
    {
        var keep = new HashSet<string> { "pStyle", "numPr", "sectPr", "bidi", "rPr", "pPrChange" };
        foreach (var e in props.X.Elements().ToList())
            if (!keep.Contains(e.Name.LocalName)) e.Remove();
    }

    // ------------------------------------------------------------------ paragraph formatting

    /// <summary>Applies a change to every selected paragraph's properties.</summary>
    public void ApplyParaFormat(string label, Action<ParaProps> change)
    {
        Edit(label, t =>
        {
            var rev = TrackChanges ? NewRevision() : null;
            foreach (var p in SelectedParagraphs())
            {
                if (Nav.IsReadOnly(p)) continue;
                t.Touch(p);
                var before = rev != null && p.Props.Change == null ? p.Props.FormattingOnly() : null;
                change(p.Props);
                if (before != null && !Ox.SameXml(before.X, p.Props.FormattingOnly().X)) p.Props.SetChange(rev, before);
            }
            return Selection;
        });
    }

    /// <summary>left, center, right, both. In right-to-left paragraphs "left" means the start edge (right).</summary>
    public void SetAlignment(string jc) => ApplyParaFormat("Alignment", p => p.Justification = jc == "left" ? null : jc);

    public void SetLineSpacing(int line, string rule = "auto") => ApplyParaFormat("Line spacing", p =>
    {
        p.SpacingLine = line;
        p.SpacingLineRule = rule;
    });

    public void SetSpacing(int? before, int? after) => ApplyParaFormat("Paragraph spacing", p =>
    {
        if (before != null) p.SpacingBefore = before;
        if (after != null) p.SpacingAfter = after;
    });

    public void SetIndents(int? start, int? end, int? firstLine) => ApplyParaFormat("Indent", p =>
    {
        if (start != null) p.IndentStart = start;
        if (end != null) p.IndentEnd = end;
        if (firstLine != null) p.FirstLine = firstLine == 0 ? null : firstLine;
    });

    /// <summary>Tab / Ctrl+M: list items go a level down; other paragraphs indent by half an inch.</summary>
    public void Indent(bool increase)
    {
        var paragraphs = SelectedParagraphs();
        var allLists = paragraphs.All(p => Styles.ResolvePara(p).NumId > 0);
        if (allLists)
        {
            ApplyParaFormat(increase ? "List level down" : "List level up", p =>
            {
                var rp = Styles.ResolvePara(new Paragraph { Props = p.Clone() });
                var level = MathEx.Clamp(rp.NumLevel + (increase ? 1 : -1), 0, 8);
                p.SetNumbering(rp.NumId, level);
                p.Remove("ind");
            });
            return;
        }
        ApplyParaFormat(increase ? "Increase indent" : "Decrease indent", p =>
        {
            var rp = Styles.ResolvePara(new Paragraph { Props = p.Clone() });
            var value = rp.IndentStart + (increase ? 720 : -720);
            value = Math.Max(0, (int)Math.Round(value / 720.0) * 720);
            p.IndentStart = value == 0 ? null : value;
        });
    }

    /// <summary>Paragraph direction: right-to-left (Urdu, Arabic, Hebrew) or left-to-right.</summary>
    public void SetDirection(bool rtl) => ApplyParaFormat(rtl ? "Right-to-left" : "Left-to-right", p => p.Bidi = rtl ? true : null);

    public void SetKeepWithNext(bool on) => ApplyParaFormat("Keep with next", p => p.KeepNext = on ? true : false);

    public void SetKeepLinesTogether(bool on) => ApplyParaFormat("Keep lines together", p => p.KeepLines = on ? true : false);

    public void SetPageBreakBefore(bool on) => ApplyParaFormat("Page break before", p => p.PageBreakBefore = on ? true : null);

    public void SetWidowControl(bool on) => ApplyParaFormat("Widow and orphan control", p => p.WidowControl = on);

    public void SetParagraphShading(string? hex) => ApplyParaFormat("Shading", p => p.ShadingFill = hex);

    // ------------------------------------------------------------------ styles

    /// <summary>Applies a style: paragraph styles to the selected paragraphs, character styles to the selected text.</summary>
    public void ApplyStyle(string styleId)
    {
        var style = Doc.Styles.Get(styleId);
        if (style == null) return;
        if (style.Type == "character")
        {
            SetCharacterStyle(styleId);
            return;
        }
        if (style.Type != "paragraph") return;
        var name = style.Name;
        Edit("Style: " + DisplayName(style), t =>
        {
            foreach (var p in SelectedParagraphs())
            {
                if (Nav.IsReadOnly(p)) continue;
                t.Touch(p);
                p.Props.StyleId = styleId == Doc.Styles.DefaultParagraphStyleId ? null : styleId;
                // Formatting that covers the whole paragraph goes, so the style shows (Word does the same).
                RemoveUniformRunFormatting(p);
                if (style.ParaProps()?.HasNumbering != true && styleId.StartsWith("Heading", StringComparison.Ordinal) && p.Props.HasNumbering && p.Props.NumId is > 0
                    && Doc.Numbering.Level(p.Props.NumId.Value, 0)?.StyleId == null)
                    p.Props.Remove("numPr");
            }
            return Selection;
        });
        _ = name;
    }

    private static void RemoveUniformRunFormatting(Paragraph p)
    {
        var runs = p.Inlines.Where(i => i is TextRun { Text.Length: > 0 }).ToList();
        if (runs.Count == 0) return;
        foreach (var name in new[] { "rFonts", "sz", "szCs", "color", "b", "bCs", "i", "iCs", "caps", "smallCaps", "u", "kern", "spacing" })
        {
            var first = runs[0].Props.El(name);
            if (first == null) continue;
            if (runs.All(r => r.Props.El(name) is { } e && Ox.SameXml(e, first)))
                foreach (var r in runs) r.Props.Remove(name);
        }
        p.Normalize();
    }

    /// <summary>The name to show for a style (built-in names in Word's display form: "Heading 1", "Caption").</summary>
    public static string DisplayName(Style style)
    {
        var name = style.Name;
        return name switch
        {
            "Normal" => "Body",
            "heading 1" => "Heading 1",
            "heading 2" => "Heading 2",
            "heading 3" => "Heading 3",
            "heading 4" => "Heading 4",
            "heading 5" => "Heading 5",
            "heading 6" => "Heading 6",
            "caption" => "Caption",
            "footnote text" => "Footnote text",
            "header" => "Header",
            "footer" => "Footer",
            "toc 1" => "Contents 1",
            "toc 2" => "Contents 2",
            "toc 3" => "Contents 3",
            _ when name.Length > 0 && char.IsLower(name[0]) => char.ToUpperInvariant(name[0]) + name.Substring(1),
            _ => name,
        };
    }

    /// <summary>"Update style to match selection": the paragraph's direct formatting becomes the style's.</summary>
    public void UpdateStyleFromSelection()
    {
        var p = Selection.Active.Para;
        var styleId = p.Props.StyleId ?? Doc.Styles.DefaultParagraphStyleId;
        var style = Doc.Styles.Get(styleId);
        if (style == null) return;
        Edit("Update style " + DisplayName(style), t =>
        {
            t.TouchStyles();
            var pPr = style.ParaProps(create: true)!;
            foreach (var e in p.Props.FormattingOnly().X.Elements().Where(e => e.Name.LocalName is not ("pStyle" or "numPr")))
                pPr.SetElement(e.Name.LocalName, new XElement(e));
            var firstRun = p.Inlines.OfType<TextRun>().FirstOrDefault(r => r.Text.Length > 0);
            if (firstRun != null)
            {
                var rPr = style.RunProps(create: true)!;
                foreach (var e in firstRun.Props.X.Elements().Where(e => e.Name.LocalName is not ("rStyle" or "ins" or "del" or "rPrChange")))
                    rPr.SetElement(e.Name.LocalName, new XElement(e));
            }
            Doc.Styles.Changed();
            // The paragraphs of that style now get it from the style: drop the same direct formatting there.
            foreach (var other in Doc.Body.Paragraphs.Where(x => (x.Props.StyleId ?? Doc.Styles.DefaultParagraphStyleId) == styleId).ToList())
            {
                if (Nav.IsReadOnly(other)) continue;
                t.Touch(other);
                foreach (var e in other.Props.X.Elements().ToList())
                    if (e.Name.LocalName is not ("pStyle" or "numPr" or "sectPr" or "rPr" or "pPrChange") && pPr.El(e.Name.LocalName) is { } se && Ox.SameXml(se, e)) e.Remove();
                if (style.RunProps() is { } srPr)
                    foreach (var run in other.Inlines.OfType<TextRun>())
                        foreach (var e in run.Props.X.Elements().ToList())
                            if (srPr.El(e.Name.LocalName) is { } se2 && Ox.SameXml(se2, e)) e.Remove();
                other.Normalize();
            }
            return Selection;
        });
    }

    // ------------------------------------------------------------------ lists

    /// <summary>The list kind of a paragraph: null (none), "bullet" or "number".</summary>
    public string? ListKindOf(Paragraph p)
    {
        var rp = Styles.ResolvePara(p);
        if (rp.NumId <= 0) return null;
        return Doc.Numbering.Level(rp.NumId, rp.NumLevel)?.IsBullet == true ? "bullet" : "number";
    }

    /// <summary>
    /// Bullets or numbering on / off. Turning a list on continues the list directly above when it's the same
    /// kind (predictable: a list never continues from something far away); otherwise a new list starts at 1.
    /// </summary>
    public void ToggleList(bool bullets)
    {
        var kind = bullets ? "bullet" : "number";
        var paragraphs = SelectedParagraphs().Where(p => !Nav.IsReadOnly(p)).ToList();
        if (paragraphs.Count == 0) return;
        var allOn = paragraphs.All(p => ListKindOf(p) == kind);
        Edit(allOn ? "Remove list" : bullets ? "Bullets" : "Numbering", t =>
        {
            if (allOn)
            {
                foreach (var p in paragraphs)
                {
                    t.Touch(p);
                    p.Props.Remove("numPr");
                    if (Styles.ResolvePara(p).NumId > 0) p.Props.SetNumbering(0, null);
                    if (p.Props.StyleId == "ListParagraph") p.Props.StyleId = null;
                    p.Props.Remove("ind");
                }
                return Selection;
            }
            var above = Nav.Prev(paragraphs[0]);
            int numId;
            if (above != null && ListKindOf(above) == kind && ReferenceEquals(above.Parent, paragraphs[0].Parent))
                numId = Styles.ResolvePara(above).NumId;
            else
            {
                t.TouchNumbering();
                var abs = Doc.Numbering.AddAbstract(Numbering.StandardDefinition(bullets));
                numId = Doc.Numbering.AddNum(abs);
            }
            foreach (var p in paragraphs)
            {
                t.Touch(p);
                var level = ListKindOf(p) != null ? Styles.ResolvePara(p).NumLevel : 0;
                p.Props.SetNumbering(numId, level);
                if (p.Props.StyleId == null || p.Props.StyleId == Doc.Styles.DefaultParagraphStyleId) p.Props.StyleId = "ListParagraph";
                p.Props.Remove("ind");
            }
            Doc.Numbering.Changed();
            return Selection;
        });
    }

    /// <summary>The paragraphs of the list run starting at <paramref name="p"/>: following items of the same list until it ends.</summary>
    private List<Paragraph> ListRunFrom(Paragraph p, int numId)
    {
        var abstractId = Doc.Numbering.AbstractOf(numId);
        var run = new List<Paragraph>();
        for (var q = (Paragraph?)p; q != null; q = Nav.Next(q))
        {
            var rp = Styles.ResolvePara(q);
            if (rp.NumId <= 0)
            {
                // Indented text under a list item (List Paragraph without a number) doesn't end the list; anything else does.
                if (q.Props.StyleId == "ListParagraph") continue;
                break;
            }
            if (Doc.Numbering.AbstractOf(rp.NumId) != abstractId) break;
            run.Add(q);
        }
        return run;
    }

    /// <summary>"Restart at 1" (or at <paramref name="start"/>): this item and the rest of its list start over.</summary>
    public void RestartNumbering(int start = 1)
    {
        var p = Selection.Active.Para;
        var rp = Styles.ResolvePara(p);
        if (rp.NumId <= 0) return;
        Edit("Restart numbering at " + start, t =>
        {
            t.TouchNumbering();
            var abstractId = Doc.Numbering.AbstractOf(rp.NumId);
            var newNum = Doc.Numbering.AddNum(abstractId, start, rp.NumLevel);
            foreach (var q in ListRunFrom(p, rp.NumId))
            {
                var qp = Styles.ResolvePara(q);
                if (qp.NumId != rp.NumId) continue;
                t.Touch(q);
                q.Props.SetNumbering(newNum, qp.NumLevel);
            }
            return Selection;
        });
    }

    /// <summary>"Continue from previous": joins this list to the nearest earlier list of the same definition.</summary>
    public void ContinueNumbering()
    {
        var p = Selection.Active.Para;
        var rp = Styles.ResolvePara(p);
        if (rp.NumId <= 0) return;
        var abstractId = Doc.Numbering.AbstractOf(rp.NumId);
        var level = Doc.Numbering.Level(rp.NumId, rp.NumLevel);
        // The nearest earlier list item with the same kind of label.
        int? previousNum = null;
        for (var q = Nav.Prev(p); q != null; q = Nav.Prev(q))
        {
            var qp = Styles.ResolvePara(q);
            if (qp.NumId <= 0 || qp.NumId == rp.NumId) continue;
            var ql = Doc.Numbering.Level(qp.NumId, qp.NumLevel);
            if (ql != null && level != null && ql.IsBullet == level.IsBullet && (Doc.Numbering.AbstractOf(qp.NumId) == abstractId || ql.Format == level.Format))
            {
                previousNum = qp.NumId;
                break;
            }
        }
        if (previousNum == null) return;
        Edit("Continue numbering", t =>
        {
            foreach (var q in ListRunFrom(p, rp.NumId))
            {
                var qp = Styles.ResolvePara(q);
                if (qp.NumId != rp.NumId) continue;
                t.Touch(q);
                q.Props.SetNumbering(previousNum, qp.NumLevel);
            }
            return Selection;
        });
    }

    /// <summary>Whether "Continue from previous" has something to continue.</summary>
    public bool CanContinueNumbering()
    {
        var p = Selection.Active.Para;
        var rp = Styles.ResolvePara(p);
        if (rp.NumId <= 0) return false;
        for (var q = Nav.Prev(p); q != null; q = Nav.Prev(q))
        {
            var qp = Styles.ResolvePara(q);
            if (qp.NumId > 0 && qp.NumId != rp.NumId) return true;
        }
        return false;
    }
}
