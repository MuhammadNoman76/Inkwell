using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;

namespace Inkwell.Core.Layout;

/// <summary>Unit conversions: WordprocessingML units to WPF device-independent pixels (1/96 inch).</summary>
public static class Units
{
    public const double DipPerTwip = 1.0 / 15.0;
    public const double DipPerPoint = 96.0 / 72.0;
    public const double DipPerEmu = 1.0 / 9525.0;

    public static double Twips(int twips) => twips / 15.0;
    public static double Twips(double twips) => twips / 15.0;
    public static double Points(double pt) => pt * DipPerPoint;
    public static double HalfPoints(int hp) => hp * DipPerPoint / 2.0;
    public static double Emu(long emu) => emu / 9525.0;
    public static int ToTwips(double dip) => (int)Math.Round(dip * 15.0);
    public static long ToEmu(double dip) => (long)Math.Round(dip * 9525.0);
}

/// <summary>Frozen brushes and pens, shared.</summary>
public static class Paint
{
    private static readonly ConcurrentDictionary<uint, SolidColorBrush> Brushes = new();

    public static SolidColorBrush Brush(Color c)
    {
        var key = ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
        return Brushes.GetOrAdd(key, _ =>
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        });
    }

    public static Pen Pen(Color c, double thickness, DashStyle? dash = null)
    {
        var p = new Pen(Brush(c), thickness) { DashStyle = dash ?? DashStyles.Solid };
        p.Freeze();
        return p;
    }

    public static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
}

/// <summary>OpenType features for a run: ligatures, kerning, small capitals.</summary>
public sealed class InkTypography : TextRunTypographyProperties
{
    private readonly bool _ligatures;
    private readonly bool _kerning;
    private readonly bool _discretionary;

    public InkTypography(bool ligatures, bool kerning, bool discretionary = false)
    {
        _ligatures = ligatures;
        _kerning = kerning;
        _discretionary = discretionary;
    }

    public override bool StandardLigatures => _ligatures;
    public override bool ContextualLigatures => _ligatures;
    public override bool DiscretionaryLigatures => _discretionary;
    public override bool HistoricalLigatures => false;
    public override int AnnotationAlternates => 0;
    public override bool ContextualAlternates => true;
    public override int ContextualSwashes => 0;
    public override bool CaseSensitiveForms => false;
    public override bool CapitalSpacing => false;
    public override FontCapitals Capitals => FontCapitals.Normal;
    public override bool EastAsianExpertForms => false;
    public override FontEastAsianLanguage EastAsianLanguage => FontEastAsianLanguage.Normal;
    public override FontEastAsianWidths EastAsianWidths => FontEastAsianWidths.Normal;
    public override FontFraction Fraction => FontFraction.Normal;
    public override bool HistoricalForms => false;
    public override bool Kerning => _kerning;
    public override bool MathematicalGreek => false;
    public override FontNumeralAlignment NumeralAlignment => FontNumeralAlignment.Normal;
    public override FontNumeralStyle NumeralStyle => FontNumeralStyle.Normal;
    public override bool SlashedZero => false;
    public override int StandardSwashes => 0;
    public override int StylisticAlternates => 0;
    public override bool StylisticSet1 => false;
    public override bool StylisticSet2 => false;
    public override bool StylisticSet3 => false;
    public override bool StylisticSet4 => false;
    public override bool StylisticSet5 => false;
    public override bool StylisticSet6 => false;
    public override bool StylisticSet7 => false;
    public override bool StylisticSet8 => false;
    public override bool StylisticSet9 => false;
    public override bool StylisticSet10 => false;
    public override bool StylisticSet11 => false;
    public override bool StylisticSet12 => false;
    public override bool StylisticSet13 => false;
    public override bool StylisticSet14 => false;
    public override bool StylisticSet15 => false;
    public override bool StylisticSet16 => false;
    public override bool StylisticSet17 => false;
    public override bool StylisticSet18 => false;
    public override bool StylisticSet19 => false;
    public override bool StylisticSet20 => false;
    public override FontVariants Variants => FontVariants.Normal;

    public static readonly InkTypography Plain = new(false, false);
    public static readonly InkTypography Ligatures = new(true, false);
    public static readonly InkTypography Kerned = new(false, true);
    public static readonly InkTypography Both = new(true, true);

    public static InkTypography For(bool ligatures, bool kerning) => ligatures ? kerning ? Both : Ligatures : kerning ? Kerned : Plain;
}

/// <summary>Character formatting handed to WPF's TextFormatter.</summary>
public sealed class InkRunProps : TextRunProperties
{
    public InkRunProps(Typeface typeface, double size, Brush foreground, Brush? background, TextDecorationCollection? decorations,
        CultureInfo culture, BaselineAlignment baseline, TextRunTypographyProperties? typography)
    {
        Typeface = typeface;
        FontRenderingEmSize = size;
        ForegroundBrush = foreground;
        BackgroundBrush = background;
        TextDecorations = decorations;
        CultureInfo = culture;
        BaselineAlignment = baseline;
        TypographyProperties = typography;
    }

    public override Typeface Typeface { get; }
    public override double FontRenderingEmSize { get; }
    public override double FontHintingEmSize => FontRenderingEmSize;
    public override TextDecorationCollection? TextDecorations { get; }
    public override Brush ForegroundBrush { get; }
    public override Brush? BackgroundBrush { get; }
    public override CultureInfo CultureInfo { get; }
    public override TextEffectCollection? TextEffects => null;
    public override BaselineAlignment BaselineAlignment { get; }
    public override TextRunTypographyProperties? TypographyProperties { get; }

    /// <summary>Height of a line of this font in DIPs (ascent + descent + gap), for empty lines and estimates.</summary>
    public double LineHeight => Typeface.FontFamily.LineSpacing * FontRenderingEmSize;
}

/// <summary>Paragraph-level settings for one call to TextFormatter.FormatLine (Inkwell positions lines itself).</summary>
public sealed class InkParaProps : TextParagraphProperties
{
    public InkParaProps(FlowDirection flow, TextAlignment alignment, TextRunProperties defaults, IList<TextTabProperties> tabs, double defaultTab, bool firstLine)
    {
        FlowDirection = flow;
        TextAlignment = alignment;
        DefaultTextRunProperties = defaults;
        Tabs = tabs;
        DefaultIncrementalTab = defaultTab;
        FirstLineInParagraph = firstLine;
    }

    public override FlowDirection FlowDirection { get; }
    public override TextAlignment TextAlignment { get; }
    public override double LineHeight => 0;
    public override bool FirstLineInParagraph { get; }
    public override TextRunProperties DefaultTextRunProperties { get; }
    public override TextWrapping TextWrapping => TextWrapping.Wrap;
    public override TextMarkerProperties? TextMarkerProperties => null;
    public override double Indent => 0;
    public override double ParagraphIndent => 0;
    public override IList<TextTabProperties> Tabs { get; }
    public override double DefaultIncrementalTab { get; }
}
