using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Inkwell.Core.Model;
using Inkwell.Core.Styles;

namespace Inkwell.Core.Layout;

/// <summary>How tracked changes are shown.</summary>
public enum RevisionView
{
    /// <summary>Insertions underlined and deletions struck through in the author's colour, with change bars.</summary>
    AllMarkup,
    /// <summary>The final text with change bars only.</summary>
    Simple,
    /// <summary>The final text as if every change were accepted.</summary>
    NoMarkup,
    /// <summary>The original text as if every change were rejected.</summary>
    Original,
}

/// <summary>Page view (paginated, print layout) or pageless (continuous, for drafting).</summary>
public enum ViewMode { Pages, Pageless }

/// <summary>Display settings that change how the document is laid out or drawn.</summary>
public sealed class ViewOptions
{
    public ViewMode Mode { get; set; } = ViewMode.Pages;

    /// <summary>Text width in pageless view (DIPs).</summary>
    public double PagelessWidth { get; set; } = 680;

    public bool ShowFormattingMarks { get; set; }

    public bool ShowHiddenText { get; set; }

    public RevisionView Revisions { get; set; } = RevisionView.AllMarkup;

    /// <summary>Dark paper: light text on dark pages (dark mode that keeps pages readable).</summary>
    public bool DarkPaper { get; set; }

    /// <summary>Grey background behind fields.</summary>
    public bool FieldShading { get; set; } = true;

    /// <summary>Comment ranges highlighted.</summary>
    public bool ShowComments { get; set; } = true;

    /// <summary>A value that changes whenever layout-affecting options change.</summary>
    public int LayoutStamp => (int)Mode * 1000003 + (ShowFormattingMarks ? 17 : 0) + (ShowHiddenText ? 31 : 0) + (int)Revisions * 101 + (DarkPaper ? 7 : 0) +
                              (int)Math.Round(PagelessWidth);

    public ViewOptions Clone() => (ViewOptions)MemberwiseClone();
}

/// <summary>Values computed fields show on a particular page (headers, footers).</summary>
public sealed class FieldContext
{
    public int PageNumber { get; set; } = 1;
    public string PageFormat { get; set; } = "decimal";
    public int PageCount { get; set; } = 1;
    public int SectionPageCount { get; set; } = 1;
    public string? Title { get; set; }
    public string? Author { get; set; }
    public string? Subject { get; set; }
    public string? FileName { get; set; }

    /// <summary>STYLEREF: the text of the first/last paragraph with a style on the page.</summary>
    public Func<string, string?>? StyleRef { get; set; }

    public string Key => PageNumber + "|" + PageFormat + "|" + PageCount + "|" + SectionPageCount;
}

/// <summary>Everything paragraph layout needs besides the paragraph.</summary>
public sealed class LayoutEnv
{
    public LayoutEnv(Document document, StyleResolver styles, ViewOptions view)
    {
        Document = document;
        Styles = styles;
        View = view;
    }

    public Document Document { get; }
    public StyleResolver Styles { get; }
    public ViewOptions View { get; }

    /// <summary>List labels ("1.", "a)", "•") by paragraph.</summary>
    public IReadOnlyDictionary<Paragraph, string> Labels { get; set; } = new Dictionary<Paragraph, string>();

    /// <summary>Displayed numbers of footnotes / endnotes.</summary>
    public IReadOnlyDictionary<(NoteKind, int), string> NoteNumbers { get; set; } = new Dictionary<(NoteKind, int), string>();

    /// <summary>Field values for the page being laid out (headers / footers), or null in the body.</summary>
    public FieldContext? Fields { get; set; }

    /// <summary>Pictures for drawing (media id, size in DIPs) → image.</summary>
    public Func<string, double, double, ImageSource?>? Images { get; set; }

    /// <summary>Draws a text box's content into a rectangle (set by the layout engine).</summary>
    public Action<DrawingContext, Rect, List<Block>>? DrawTextBox { get; set; }

    /// <summary>Colour for a revision author.</summary>
    public Func<string, Color> AuthorColor { get; set; } = RevisionColors.For;

    public LayoutEnv With(FieldContext? fields) => new(Document, Styles, View)
    {
        Labels = Labels,
        NoteNumbers = NoteNumbers,
        Fields = fields,
        Images = Images,
        DrawTextBox = DrawTextBox,
        AuthorColor = AuthorColor,
    };
}

/// <summary>Distinct, readable colours for revision authors.</summary>
public static class RevisionColors
{
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0xB8, 0x3B, 0x5E), Color.FromRgb(0x2E, 0x6F, 0xB5), Color.FromRgb(0x2F, 0x8F, 0x5B), Color.FromRgb(0x8E, 0x4F, 0xC0),
        Color.FromRgb(0xC2, 0x6A, 0x12), Color.FromRgb(0x14, 0x8A, 0x8A), Color.FromRgb(0x9A, 0x6B, 0x1F), Color.FromRgb(0x5B, 0x5F, 0xC7),
    };

    private static readonly Dictionary<string, Color> Assigned = new(StringComparer.Ordinal);

    public static Color For(string author)
    {
        lock (Assigned)
        {
            if (Assigned.TryGetValue(author, out var c)) return c;
            c = Palette[Assigned.Count % Palette.Length];
            Assigned[author] = c;
            return c;
        }
    }
}

/// <summary>Turns resolved run formatting into TextFormatter run properties (fonts per script, colours, decorations).</summary>
public static class RunPropsFactory
{
    private static readonly Dictionary<string, InkRunProps> Cache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, CultureInfo> Cultures = new(StringComparer.OrdinalIgnoreCase);

    public static CultureInfo Culture(string? tag)
    {
        tag ??= "en-US";
        lock (Cultures)
        {
            if (Cultures.TryGetValue(tag, out var c)) return c;
            try
            {
                c = CultureInfo.GetCultureInfo(tag);
            }
            catch (CultureNotFoundException)
            {
                c = CultureInfo.InvariantCulture;
            }
            Cultures[tag] = c;
            return c;
        }
    }

    /// <summary>Which font slot a character uses (Word's rules for w:rFonts).</summary>
    public enum Slot : byte { Ascii, HAnsi, EastAsia, Complex }

    public static Slot SlotOf(char c, ResolvedRun run)
    {
        if (run.Rtl || run.ComplexScript) return Slot.Complex;
        if (c < 0x80) return Slot.Ascii;
        if (IsComplex(c)) return Slot.Complex;
        if (IsEastAsian(c) || (run.Hint == "eastAsia" && c is >= ' ' and <= '㏿')) return Slot.EastAsia;
        return Slot.HAnsi;
    }

    public static bool IsComplex(char c) =>
        c is >= '֐' and <= 'ࣿ' // Hebrew, Arabic, Syriac, Thaana, NKo, Samaritan, Arabic Extended
            or >= 'ऀ' and <= '෿' // Indic scripts
            or >= '฀' and <= '໿' // Thai, Lao
            or >= 'ༀ' and <= '࿿' // Tibetan
            or >= 'ក' and <= '៿' // Khmer
            or >= 'יִ' and <= '﷿' // Hebrew and Arabic presentation forms A
            or >= 'ﹰ' and <= '﻿'; // Arabic presentation forms B

    public static bool IsEastAsian(char c) =>
        c is >= 'ᄀ' and <= 'ᇿ' or >= '⺀' and <= '⿟' or >= '　' and <= '鿿' or >= 'ꀀ' and <= '꓏'
            or >= '가' and <= '힯' or >= '豈' and <= '﫿' or >= '︰' and <= '﹏' or >= '＀' and <= '￯'
            or >= '\uD840' and <= '\uD87F'; // high surrogates of CJK extension planes

    public static bool IsRtlChar(char c) => c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻿';

    /// <summary>Text colour on screen: automatic is black (white on dark shading / dark paper); explicit colours are lightened on dark paper.</summary>
    public static Color TextColor(ResolvedRun run, Color? background, bool darkPaper)
    {
        if (run.Color is { } c) return darkPaper ? ForDarkPaper(c) : c;
        var bg = background ?? (darkPaper ? Color.FromRgb(0x1E, 0x1F, 0x22) : Colors.White);
        return OoxColor.Luminance(bg) < 0.45 ? Color.FromRgb(0xEC, 0xEC, 0xEC) : Color.FromRgb(0x14, 0x14, 0x14);
    }

    /// <summary>Keeps colours readable on dark paper: dark ones become light with the same hue.</summary>
    public static Color ForDarkPaper(Color c)
    {
        var l = OoxColor.Luminance(c);
        if (l >= 0.55) return c;
        // Invert lightness in HSL, keeping hue and most of the saturation.
        RgbToHsl(c, out var h, out var s, out var light);
        var target = MathEx.Clamp(1.0 - light, 0.72, 0.93);
        return HslToRgb(h, s * 0.85, target);
    }

    /// <summary>Shading / highlight colours on dark paper: kept dark enough for light text.</summary>
    public static Color BackgroundForDarkPaper(Color c)
    {
        RgbToHsl(c, out var h, out var s, out var l);
        return HslToRgb(h, s * 0.7, MathEx.Clamp(0.32 - (l - 0.5) * 0.2, 0.18, 0.35));
    }

    private static void RgbToHsl(Color c, out double h, out double s, out double l)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        l = (max + min) / 2;
        if (Math.Abs(max - min) < 1e-9)
        {
            h = s = 0;
            return;
        }
        var d = max - min;
        s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        h /= 6;
    }

    private static Color HslToRgb(double h, double s, double l)
    {
        double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
        double r, g, b;
        if (s <= 0)
        {
            r = g = b = l;
        }
        else
        {
            var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            var p = 2 * l - q;
            r = Hue(p, q, h + 1.0 / 3);
            g = Hue(p, q, h);
            b = Hue(p, q, h - 1.0 / 3);
        }
        return Color.FromRgb((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    /// <summary>Extra decoration for revisions (insertions underlined, deletions struck through, in the author's colour).</summary>
    public enum RevisionMark : byte { None, Inserted, Deleted, Formatted }

    /// <summary>TextFormatter properties for a run of text in one font slot.</summary>
    public static InkRunProps Create(ResolvedRun run, Slot slot, ViewOptions view, Color? paragraphShading, double sizeScale = 1.0,
        RevisionMark revision = RevisionMark.None, Color? revisionColor = null, bool smallCapsPart = false)
    {
        var complex = slot == Slot.Complex;
        var family = slot switch
        {
            Slot.Ascii => run.AsciiFont,
            Slot.HAnsi => run.HAnsiFont,
            Slot.EastAsia => run.EastAsiaFont,
            _ => run.CsFont,
        };
        var bold = complex ? run.BoldCs : run.Bold;
        var italic = complex ? run.ItalicCs : run.Italic;
        var size = (complex ? run.SizeCs : run.Size) * sizeScale;
        if (smallCapsPart) size *= 0.8;
        var baseline = BaselineAlignment.Baseline;
        if (run.VertAlign != 0)
        {
            size *= 0.65;
            baseline = run.VertAlign == 1 ? BaselineAlignment.Superscript : BaselineAlignment.Subscript;
        }
        size = Math.Max(1, size);
        var background = run.Highlight ?? run.Shading ?? paragraphShading;
        var fore = TextColor(run, background, view.DarkPaper);
        var underline = run.Underline;
        var strike = run.Strike;
        var dstrike = run.DoubleStrike;
        Color? decoColor = null;
        if (revision == RevisionMark.Inserted && view.Revisions == RevisionView.AllMarkup)
        {
            fore = revisionColor ?? fore;
            if (underline == "none") underline = "single";
            decoColor = revisionColor;
        }
        else if (revision == RevisionMark.Deleted && view.Revisions is RevisionView.AllMarkup)
        {
            fore = revisionColor ?? fore;
            strike = true;
            decoColor = revisionColor;
        }
        var bgBrush = run.Highlight is { } hl ? Paint.Brush(view.DarkPaper ? BackgroundForDarkPaper(hl) : hl)
            : run.Shading is { } sh ? Paint.Brush(view.DarkPaper ? BackgroundForDarkPaper(sh) : sh) : null;
        var lang = complex ? run.LangBidi : slot == Slot.EastAsia ? run.LangEastAsia : run.Lang;
        var ligatures = run.Ligatures != "none";
        var kerning = run.KernFrom > 0 && size * 0.75 >= run.KernFrom;
        var key = string.Join("|", family, bold ? "b" : "", italic ? "i" : "", size.ToString("0.###", CultureInfo.InvariantCulture), fore.ToString(),
            bgBrush?.Color.ToString() ?? "", underline, strike ? "s" : "", dstrike ? "d" : "", decoColor?.ToString() ?? run.UnderlineColor?.ToString() ?? "",
            lang, ((int)baseline).ToString(CultureInfo.InvariantCulture), ligatures ? "l" : "", kerning ? "k" : "");
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
            var typeface = new Typeface(Rendering.FontCatalog.Get(family), italic ? FontStyles.Italic : FontStyles.Normal,
                bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
            var props = new InkRunProps(typeface, size * Units.DipPerPoint, Paint.Brush(fore), bgBrush,
                Decorations(underline, strike, dstrike, decoColor ?? run.UnderlineColor ?? fore, size), Culture(lang), baseline,
                InkTypography.For(ligatures, kerning));
            if (Cache.Count > 20000) Cache.Clear();
            Cache[key] = props;
            return props;
        }
    }

    private static TextDecorationCollection? Decorations(string underline, bool strike, bool dstrike, Color color, double sizePt)
    {
        if (underline == "none" && !strike && !dstrike) return null;
        var list = new TextDecorationCollection();
        var thickness = Math.Max(0.75, sizePt * Units.DipPerPoint / 16.0);
        TextDecoration Make(TextDecorationLocation location, double offset, double thick, DashStyle? dash = null)
        {
            var pen = new Pen(Paint.Brush(color), thick) { DashStyle = dash ?? DashStyles.Solid };
            pen.Freeze();
            return new TextDecoration(location, pen, offset, TextDecorationUnit.FontRecommended, TextDecorationUnit.Pixel);
        }
        switch (underline)
        {
            case "none":
                break;
            case "double":
                list.Add(Make(TextDecorationLocation.Underline, 0, thickness));
                list.Add(Make(TextDecorationLocation.Underline, thickness * 2.2, thickness));
                break;
            case "thick":
                list.Add(Make(TextDecorationLocation.Underline, 0, thickness * 2));
                break;
            case "dotted":
            case "dottedHeavy":
                list.Add(Make(TextDecorationLocation.Underline, 0, thickness, DashStyles.Dot));
                break;
            case "dash":
            case "dashedHeavy":
            case "dashLong":
            case "dashLongHeavy":
            case "dotDash":
            case "dotDotDash":
                list.Add(Make(TextDecorationLocation.Underline, 0, thickness, DashStyles.Dash));
                break;
            case "wave":
            case "wavyHeavy":
            case "wavyDouble":
                list.Add(Make(TextDecorationLocation.Underline, 0, thickness, DashStyles.Dot));
                break;
            default:
                list.Add(Make(TextDecorationLocation.Underline, 0, thickness));
                break;
        }
        if (strike) list.Add(Make(TextDecorationLocation.Strikethrough, 0, thickness));
        if (dstrike)
        {
            list.Add(Make(TextDecorationLocation.Strikethrough, -thickness, thickness));
            list.Add(Make(TextDecorationLocation.Strikethrough, thickness, thickness));
        }
        list.Freeze();
        return list;
    }
}
