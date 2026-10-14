using System.Globalization;
using System.Windows.Media;
using System.Xml.Linq;
using Inkwell.Core.Model;

namespace Inkwell.Core.Styles;

/// <summary>Effective character formatting of a run after the whole style hierarchy is applied.</summary>
public sealed class ResolvedRun
{
    public string AsciiFont { get; internal set; } = "Calibri";
    public string HAnsiFont { get; internal set; } = "Calibri";
    public string EastAsiaFont { get; internal set; } = "Calibri";
    public string CsFont { get; internal set; } = "Times New Roman";
    public string? Hint { get; internal set; }

    public bool Bold { get; internal set; }
    public bool Italic { get; internal set; }
    public bool BoldCs { get; internal set; }
    public bool ItalicCs { get; internal set; }
    public bool Caps { get; internal set; }
    public bool SmallCaps { get; internal set; }
    public bool Strike { get; internal set; }
    public bool DoubleStrike { get; internal set; }
    public bool Vanish { get; internal set; }
    public bool SpecVanish { get; internal set; }
    public bool WebHidden { get; internal set; }
    public bool NoProof { get; internal set; }
    public bool Rtl { get; internal set; }
    public bool ComplexScript { get; internal set; }
    public bool Emboss { get; internal set; }
    public bool Imprint { get; internal set; }
    public bool Outline { get; internal set; }
    public bool Shadow { get; internal set; }

    /// <summary>Size in points (Latin / East Asian) and for complex scripts.</summary>
    public double Size { get; internal set; } = 10;
    public double SizeCs { get; internal set; } = 10;

    /// <summary>Text colour, or null for automatic (black on light paper).</summary>
    public Color? Color { get; internal set; }

    /// <summary>none, single, double, thick, dotted, dash, wave, words…</summary>
    public string Underline { get; internal set; } = "none";
    public Color? UnderlineColor { get; internal set; }

    /// <summary>Highlight (marker) colour, or null.</summary>
    public Color? Highlight { get; internal set; }

    /// <summary>Run shading fill, or null.</summary>
    public Color? Shading { get; internal set; }

    /// <summary>0 = baseline, 1 = superscript, 2 = subscript.</summary>
    public int VertAlign { get; internal set; }

    /// <summary>Raised (+) or lowered (−) position in points.</summary>
    public double Position { get; internal set; }

    /// <summary>Extra character spacing in points.</summary>
    public double Spacing { get; internal set; }

    /// <summary>Horizontal scale in percent (w:w).</summary>
    public double WidthScale { get; internal set; } = 100;

    /// <summary>Kerning applies from this size (points); 0 = off.</summary>
    public double KernFrom { get; internal set; }

    public string Lang { get; internal set; } = "en-US";
    public string LangBidi { get; internal set; } = "ar-SA";
    public string LangEastAsia { get; internal set; } = "zh-CN";

    /// <summary>OpenType ligatures (w14:ligatures): none, standard, all…</summary>
    public string Ligatures { get; internal set; } = "none";

    public string? CharStyleId { get; internal set; }

    public bool IsHidden => Vanish || SpecVanish;

    public bool Underlined => Underline != "none";

    public ResolvedRun Copy() => (ResolvedRun)MemberwiseClone();
}

/// <summary>Effective paragraph formatting after the style hierarchy (in twips unless noted).</summary>
public sealed class ResolvedPara
{
    public string StyleId { get; internal set; } = "Normal";

    /// <summary>left, center, right, both, distribute, start, end, lowKashida, mediumKashida, highKashida, thaiDistribute.</summary>
    public string Justification { get; internal set; } = "left";

    public int IndentStart { get; internal set; }
    public int IndentEnd { get; internal set; }

    /// <summary>First-line indent; negative = hanging.</summary>
    public int FirstLine { get; internal set; }

    public int SpacingBefore { get; internal set; }
    public int SpacingAfter { get; internal set; }
    public bool BeforeAutospacing { get; internal set; }
    public bool AfterAutospacing { get; internal set; }

    /// <summary>Line spacing value: 240ths of a line (auto) or twips (exact, atLeast).</summary>
    public int Line { get; internal set; } = 240;
    public string LineRule { get; internal set; } = "auto";

    public bool ContextualSpacing { get; internal set; }
    public bool KeepNext { get; internal set; }
    public bool KeepLines { get; internal set; }
    public bool PageBreakBefore { get; internal set; }
    public bool WidowControl { get; internal set; } = true;
    public bool Bidi { get; internal set; }
    public bool SuppressAutoHyphens { get; internal set; }
    public bool MirrorIndents { get; internal set; }

    /// <summary>0–8 for headings, 9 for body text.</summary>
    public int OutlineLevel { get; internal set; } = 9;

    public int NumId { get; internal set; }
    public int NumLevel { get; internal set; }

    public BorderDef? BorderTop { get; internal set; }
    public BorderDef? BorderBottom { get; internal set; }
    public BorderDef? BorderLeft { get; internal set; }
    public BorderDef? BorderRight { get; internal set; }
    public BorderDef? BorderBetween { get; internal set; }

    public Color? Shading { get; internal set; }

    /// <summary>Tab stops (after clears), sorted by position.</summary>
    public IReadOnlyList<TabStopDef> Tabs { get; internal set; } = Array.Empty<TabStopDef>();

    /// <summary>The paragraph mark's character formatting (also used for empty paragraphs and list labels).</summary>
    public ResolvedRun Mark { get; internal set; } = new();

    public bool IsHeading => OutlineLevel is >= 0 and <= 8;

    public ResolvedPara Copy() => (ResolvedPara)MemberwiseClone();
}

/// <summary>Colour helpers for WordprocessingML values.</summary>
public static class OoxColor
{
    public static Color? Parse(string? hex)
    {
        if (string.IsNullOrEmpty(hex) || hex == "auto" || hex!.Length != 6) return null;
        if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return null;
        return System.Windows.Media.Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public static string ToHex(Color c) => $"{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Applies themeTint (lighter) / themeShade (darker), given as hex bytes 00–FF.</summary>
    public static Color ApplyTintShade(Color c, string? tint, string? shade)
    {
        if (tint != null && int.TryParse(tint, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var t))
        {
            var k = t / 255.0;
            c = System.Windows.Media.Color.FromRgb(Mix(c.R, k), Mix(c.G, k), Mix(c.B, k));
        }
        if (shade != null && int.TryParse(shade, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var s))
        {
            var k = s / 255.0;
            c = System.Windows.Media.Color.FromRgb((byte)Math.Round(c.R * k), (byte)Math.Round(c.G * k), (byte)Math.Round(c.B * k));
        }
        return c;
    }

    private static byte Mix(byte channel, double tint) => (byte)Math.Round(channel * tint + 255 * (1 - tint));

    /// <summary>The colour of a w:color / w:shd-like element (val or theme colour with tint/shade).</summary>
    public static Color? Resolve(XElement? e, string valAttr, string themeAttr, string tintAttr, string shadeAttr, DocTheme theme, DocSettings settings)
    {
        if (e == null) return null;
        var themeName = e.Attr(themeAttr);
        if (themeName != null)
        {
            var hex = theme.Color(settings.MapThemeColor(themeName));
            if (Parse(hex) is { } tc) return ApplyTintShade(tc, e.Attr(tintAttr), e.Attr(shadeAttr));
        }
        return Parse(e.Attr(valAttr));
    }

    /// <summary>Word's highlight colour names.</summary>
    public static Color? Highlight(string? name) => name switch
    {
        "yellow" => System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0x00),
        "green" => System.Windows.Media.Color.FromRgb(0x00, 0xFF, 0x00),
        "cyan" => System.Windows.Media.Color.FromRgb(0x00, 0xFF, 0xFF),
        "magenta" => System.Windows.Media.Color.FromRgb(0xFF, 0x00, 0xFF),
        "blue" => System.Windows.Media.Color.FromRgb(0x00, 0x00, 0xFF),
        "red" => System.Windows.Media.Color.FromRgb(0xFF, 0x00, 0x00),
        "darkBlue" => System.Windows.Media.Color.FromRgb(0x00, 0x00, 0x80),
        "darkCyan" => System.Windows.Media.Color.FromRgb(0x00, 0x80, 0x80),
        "darkGreen" => System.Windows.Media.Color.FromRgb(0x00, 0x80, 0x00),
        "darkMagenta" => System.Windows.Media.Color.FromRgb(0x80, 0x00, 0x80),
        "darkRed" => System.Windows.Media.Color.FromRgb(0x80, 0x00, 0x00),
        "darkYellow" => System.Windows.Media.Color.FromRgb(0x80, 0x80, 0x00),
        "darkGray" => System.Windows.Media.Color.FromRgb(0x80, 0x80, 0x80),
        "lightGray" => System.Windows.Media.Color.FromRgb(0xC0, 0xC0, 0xC0),
        "black" => System.Windows.Media.Color.FromRgb(0, 0, 0),
        "white" => System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF),
        _ => null,
    };

    public static readonly string[] HighlightNames =
        { "yellow", "green", "cyan", "magenta", "blue", "red", "darkBlue", "darkCyan", "darkGreen", "darkMagenta", "darkRed", "darkYellow", "darkGray", "lightGray", "black" };

    /// <summary>Relative luminance (0–1).</summary>
    public static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
}
