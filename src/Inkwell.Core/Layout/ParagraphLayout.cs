using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using Inkwell.Core.Model;
using Inkwell.Core.Styles;

namespace Inkwell.Core.Layout;

/// <summary>One formatted line of a paragraph.</summary>
public sealed class LineInfo
{
    public int SrcStart;
    public int SrcLength;

    /// <summary>Document positions covered: [DocStart, DocEnd).</summary>
    public int DocStart;
    public int DocEnd;

    /// <summary>Left of the formatting box relative to the paragraph's container (column or cell content box).</summary>
    public double X;

    /// <summary>Width handed to TextFormatter.</summary>
    public double FormatWidth;

    /// <summary>Line box height after the line-spacing rule, and the baseline inside it.</summary>
    public double Height;
    public double Baseline;

    /// <summary>Offset from the line box top to where the TextLine is drawn.</summary>
    public double TextOffset;

    /// <summary>The TextLine's own (natural) height.</summary>
    public double NaturalHeight;

    /// <summary>Width of the text (TextLine.Width) and its start within the formatting box (alignment).</summary>
    public double ContentWidth;
    public double ContentStart;

    /// <summary>'\0' soft wrap, '\v' line break, '\f' page break, U+000E column break, 'c' clearing break, '\r' paragraph end.</summary>
    public char EndBreak;

    public bool IsFirst;
    public InkParaProps ParaProps = null!;
    public TextLineBreak? PreviousBreak;

    /// <summary>Top relative to the page (set when placed); used by hit testing and drawing.</summary>
    public double Top;

    public bool EndsParagraph => EndBreak == '\r';

    public LineInfo Clone() => (LineInfo)MemberwiseClone();
}

/// <summary>A paragraph broken into lines for one width, plus its spacing and borders.</summary>
public sealed class ParagraphLayout
{
    public ParagraphLayout(Paragraph paragraph, ParaSource source, double width)
    {
        Paragraph = paragraph;
        Source = source;
        Width = width;
    }

    public Paragraph Paragraph { get; }
    public ParaSource Source { get; }
    public ResolvedPara Props => Source.Props;
    public double Width { get; }
    public List<LineInfo> Lines { get; } = new();

    /// <summary>Space before / after in DIPs (before contextual spacing and top-of-page rules).</summary>
    public double SpaceBefore { get; set; }
    public double SpaceAfter { get; set; }

    /// <summary>Room taken by a top / bottom border (line + its spacing), in DIPs.</summary>
    public double BorderTop { get; set; }
    public double BorderBottom { get; set; }

    public double LinesHeight
    {
        get
        {
            var h = 0.0;
            foreach (var l in Lines) h += l.Height;
            return h;
        }
    }

    /// <summary>Stamp of everything the layout depends on.</summary>
    public string Key { get; set; } = "";

    public int Version { get; set; }
}

/// <summary>Breaks paragraphs into lines with WPF's TextFormatter, applying indents, tabs, alignment and line spacing.</summary>
public sealed class LineFormatter : IDisposable
{
    private readonly TextFormatter _formatter = TextFormatter.Create(TextFormattingMode.Ideal);

    public TextFormatter Formatter => _formatter;

    public void Dispose() => _formatter.Dispose();

    /// <summary>Lays out a whole paragraph at a width (no floats).</summary>
    public ParagraphLayout Layout(ParaSource source, double width, double defaultTab)
    {
        var layout = new ParagraphLayout(source.Paragraph, source, width);
        var props = source.Props;
        layout.SpaceBefore = props.BeforeAutospacing ? Units.Points(14) : Units.Twips(props.SpacingBefore);
        layout.SpaceAfter = props.AfterAutospacing ? Units.Points(14) : Units.Twips(props.SpacingAfter);
        if (props.BorderTop is { } top) layout.BorderTop = BorderSpace(top);
        if (props.BorderBottom is { } bottom) layout.BorderBottom = BorderSpace(bottom);
        var src = 0;
        TextLineBreak? previous = null;
        var first = true;
        var guard = 0;
        while (src < source.Length && guard++ < 100000)
        {
            var line = FormatLine(source, src, first, 0, width, width, previous, defaultTab, out var next);
            layout.Lines.Add(line);
            src += Math.Max(1, line.SrcLength);
            previous = next;
            first = false;
            if (line.EndsParagraph) break;
        }
        return layout;
    }

    public static double BorderSpace(BorderDef b) => Units.Points(b.Size / 8.0) + Units.Points(b.Space);

    /// <summary>
    /// Formats one line starting at <paramref name="srcStart"/>. The paragraph's container is
    /// <paramref name="containerWidth"/> wide; the line may only use [spanLeft, spanLeft + spanWidth) of it
    /// (narrower than the container when text wraps around a floating picture).
    /// </summary>
    public LineInfo FormatLine(ParaSource source, int srcStart, bool first, double spanLeft, double spanWidth, double containerWidth,
        TextLineBreak? previous, double defaultTab, out TextLineBreak? nextBreak)
    {
        var p = source.Props;
        var rtl = p.Bidi;
        var indStart = Units.Twips(p.IndentStart);
        var indEnd = Units.Twips(p.IndentEnd);
        var firstLine = first ? Units.Twips(p.FirstLine) : 0;
        double left, right;
        if (!rtl)
        {
            left = indStart + firstLine;
            right = containerWidth - indEnd;
        }
        else
        {
            left = indEnd;
            right = containerWidth - indStart - firstLine;
        }
        left = Math.Max(left, spanLeft);
        right = Math.Min(right, spanLeft + spanWidth);
        if (right - left < 12) right = left + 12;
        var width = right - left;

        // Tab stops are measured from the paragraph's start edge (the column edge, not the indent).
        var startOffset = rtl ? containerWidth - right : left;
        var tabs = new List<TextTabProperties>();
        var lastCustom = 0.0;
        foreach (var t in p.Tabs)
        {
            if (t.Kind is "clear" or "bar") continue;
            var at = Units.Twips(t.Position) - startOffset;
            if (at <= 0.5) continue;
            tabs.Add(new TextTabProperties(t.Kind switch
            {
                "center" => TextTabAlignment.Center,
                "right" or "end" => TextTabAlignment.Right,
                "decimal" => TextTabAlignment.Character,
                _ => TextTabAlignment.Left,
            }, at, Leader(t.Leader), t.Kind == "decimal" ? '.' : 0));
            lastCustom = Math.Max(lastCustom, at);
        }
        // A hanging indent works as a tab stop on the first line (Word puts list text there).
        if (first && p.FirstLine < 0)
        {
            var hang = indStart - startOffset;
            if (hang > 0.5 && !tabs.Any(t => Math.Abs(t.Location - hang) < 0.5))
            {
                tabs.Add(new TextTabProperties(TextTabAlignment.Left, hang, 0, 0));
                lastCustom = Math.Max(lastCustom, hang);
            }
        }
        tabs.Sort((a, b) => a.Location.CompareTo(b.Location));
        // Default stops at multiples of the interval from the start edge, after the last custom stop.
        var dt = Math.Max(4, defaultTab);
        var k = Math.Floor((startOffset + lastCustom) / dt) + 1;
        for (var n = 0; n < 64; n++, k++)
        {
            var at = k * dt - startOffset;
            if (at > width + dt) break;
            if (at <= lastCustom + 0.5) continue;
            tabs.Add(new TextTabProperties(TextTabAlignment.Left, at, 0, 0));
        }

        var alignment = Alignment(p.Justification, rtl);
        var flow = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        var paraProps = new InkParaProps(flow, alignment, source.MarkProps, tabs, dt, first);
        var line = _formatter.FormatLine(source, srcStart, width, paraProps, previous);
        try
        {
            var endBreak = EndBreakOf(source, srcStart + line.Length - 1);
            // The last line of a justified paragraph (or one ending in a line break) isn't stretched.
            if (alignment == TextAlignment.Justify && endBreak is '\r' or '\v' or '\f' or '\u000E' or 'c' && p.Justification != "distribute")
            {
                line.Dispose();
                alignment = rtl ? TextAlignment.Right : TextAlignment.Left;
                paraProps = new InkParaProps(flow, alignment, source.MarkProps, tabs, dt, first);
                line = _formatter.FormatLine(source, srcStart, width, paraProps, previous);
                endBreak = EndBreakOf(source, srcStart + line.Length - 1);
            }
            var info = new LineInfo
            {
                SrcStart = srcStart,
                SrcLength = line.Length,
                DocStart = source.SrcToDoc(srcStart),
                X = left,
                FormatWidth = width,
                NaturalHeight = line.Height,
                ContentWidth = line.Width,
                ContentStart = line.Start,
                EndBreak = endBreak,
                IsFirst = first,
                ParaProps = paraProps,
                PreviousBreak = previous,
            };
            info.DocEnd = endBreak == '\r' ? source.DocLength : Math.Max(info.DocStart, source.SrcToDoc(srcStart + line.Length));
            if (srcStart < source.LabelLength) info.DocStart = 0;
            ApplyLineSpacing(info, line.Height, line.Baseline, p);
            nextBreak = line.GetTextLineBreak()?.Clone();
            return info;
        }
        finally
        {
            line.Dispose();
        }
    }

    /// <summary>Recreates a line for drawing or hit testing (the caller disposes it).</summary>
    public TextLine Recreate(ParaSource source, LineInfo info) =>
        _formatter.FormatLine(source, info.SrcStart, info.FormatWidth, info.ParaProps, info.PreviousBreak);

    private static char EndBreakOf(ParaSource source, int lastSrc)
    {
        foreach (var s in source.Segments)
        {
            if (lastSrc < s.SrcStart || lastSrc >= s.SrcEnd) continue;
            return s.Kind switch
            {
                SegKind.ParaEnd => '\r',
                SegKind.LineEnd => s.Break,
                _ => '\0',
            };
        }
        return '\r';
    }

    private static void ApplyLineSpacing(LineInfo info, double natural, double baseline, ResolvedPara p)
    {
        var descent = natural - baseline;
        switch (p.LineRule)
        {
            case "exact":
            {
                var h = Math.Max(1, Units.Twips(p.Line));
                info.Height = h;
                // Exact spacing keeps the descent at the bottom; tall text is cut off at the top as in Word.
                info.Baseline = h - descent * Math.Min(1, h / Math.Max(1, natural));
                info.TextOffset = info.Baseline - baseline;
                break;
            }
            case "atLeast":
            {
                var h = Math.Max(natural, Units.Twips(p.Line));
                info.Height = h;
                info.TextOffset = h - natural;
                info.Baseline = info.TextOffset + baseline;
                break;
            }
            default:
            {
                var factor = p.Line <= 0 ? 1.0 : p.Line / 240.0;
                var h = natural * factor;
                info.Height = h;
                info.TextOffset = h - natural;
                info.Baseline = info.TextOffset + baseline;
                break;
            }
        }
    }

    public static TextAlignment Alignment(string jc, bool rtl) => jc switch
    {
        "center" => TextAlignment.Center,
        "right" or "end" => rtl ? TextAlignment.Left : TextAlignment.Right,
        "both" or "distribute" or "lowKashida" or "mediumKashida" or "highKashida" or "thaiDistribute" => TextAlignment.Justify,
        _ => rtl ? TextAlignment.Right : TextAlignment.Left,
    };

    private static int Leader(string? leader) => leader switch
    {
        "dot" => '.',
        "hyphen" => '-',
        "underscore" or "heavy" => '_',
        "middleDot" => '·',
        _ => 0,
    };
}
