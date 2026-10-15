using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using Inkwell.Core.Model;

namespace Inkwell.Core.Layout;

/// <summary>An object in a line of text (picture, placeholder, separator) with a fixed size.</summary>
public abstract class InkObject : TextEmbeddedObject
{
    protected InkObject(TextRunProperties props, double width, double height, double baseline)
    {
        Properties = props;
        Width = width;
        Height = height;
        BaselineOffset = baseline;
    }

    public double Width { get; }
    public double Height { get; }

    /// <summary>Distance from the top of the object to the text baseline.</summary>
    public double BaselineOffset { get; }

    public override int Length => 1;
    public override TextRunProperties Properties { get; }
    public override LineBreakCondition BreakBefore => LineBreakCondition.BreakPossible;
    public override LineBreakCondition BreakAfter => LineBreakCondition.BreakPossible;
    public override bool HasFixedSize => true;
    public override CharacterBufferReference CharacterBufferReference => new("￼", 0);

    public override TextEmbeddedObjectMetrics Format(double remainingParagraphWidth) => new(Width, Height, BaselineOffset);

    // WPF passes the baseline point as the origin: the object extends BaselineOffset above it.
    public override Rect ComputeBoundingBox(bool rightToLeft, bool sideways) => new(0, -BaselineOffset, Width, Height);

    public override void Draw(DrawingContext drawingContext, Point origin, bool rightToLeft, bool sideways) =>
        DrawIn(drawingContext, new Rect(origin.X, origin.Y - BaselineOffset, Width, Height));

    /// <summary>Draws the object filling <paramref name="box"/>.</summary>
    public abstract void DrawIn(DrawingContext dc, Rect box);
}

/// <summary>A picture in line with text; drawings Inkwell keeps but doesn't edit show as labelled placeholders.</summary>
public sealed class DrawingRun : InkObject
{
    private readonly LayoutEnv _env;

    public DrawingRun(DrawingObject drawing, TextRunProperties props, LayoutEnv env)
        : base(props, Math.Max(1, Units.Emu(drawing.Width)), Math.Max(1, Units.Emu(drawing.Height)), Math.Max(1, Units.Emu(drawing.Height)))
    {
        Drawing = drawing;
        _env = env;
    }

    public DrawingObject Drawing { get; }

    public override void DrawIn(DrawingContext dc, Rect box) => DrawDrawing(dc, box, Drawing, _env);

    /// <summary>Draws a drawing object (picture or placeholder) into a box. Shared by in-line and floating drawings.</summary>
    public static void DrawDrawing(DrawingContext dc, Rect box, DrawingObject drawing, LayoutEnv env)
    {
        if (drawing.IsPicture && env.Images?.Invoke(drawing.MediaId!, box.Width, box.Height) is { } image)
        {
            dc.DrawImage(image, box);
            return;
        }
        if (drawing.Kind == "textbox" && drawing.TextBoxContent is { Count: > 0 } content && env.DrawTextBox != null)
        {
            var frame = Paint.Pen(env.View.DarkPaper ? Color.FromRgb(0x70, 0x70, 0x78) : Color.FromRgb(0xA0, 0xA4, 0xAC), 0.75);
            dc.DrawRectangle(null, frame, box);
            env.DrawTextBox(dc, new Rect(box.X + 7, box.Y + 4, Math.Max(1, box.Width - 14), Math.Max(1, box.Height - 8)), content);
            return;
        }
        Placeholder(dc, box, LabelFor(drawing), env.View.DarkPaper);
    }

    public static string LabelFor(DrawingObject d) => d.Kind switch
    {
        "picture" => "Picture",
        "linkedpicture" => "Linked picture",
        "chart" => "Chart",
        "smartart" => "SmartArt diagram",
        "shape" => "Shape",
        "textbox" => "Text box",
        "group" => "Group of shapes",
        "canvas" => "Drawing canvas",
        "ink" => "Ink",
        _ => "Drawing",
    };

    /// <summary>A soft box with a label: something kept exactly as it was but not shown in Inkwell.</summary>
    public static void Placeholder(DrawingContext dc, Rect box, string label, bool dark)
    {
        var fill = dark ? Color.FromRgb(0x2A, 0x2D, 0x33) : Color.FromRgb(0xF1, 0xF2, 0xF5);
        var stroke = dark ? Color.FromRgb(0x55, 0x5A, 0x64) : Color.FromRgb(0xC5, 0xC9, 0xD2);
        var pen = Paint.Pen(stroke, 1, DashStyles.Dash);
        dc.DrawRoundedRectangle(Paint.Brush(fill), pen, box, 4, 4);
        if (box.Width < 30 || box.Height < 14) return;
        var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            Math.Min(12, Math.Max(8, box.Height / 4)), Paint.Brush(dark ? Color.FromRgb(0xB0, 0xB4, 0xBC) : Color.FromRgb(0x5A, 0x60, 0x6B)), 1.0)
        {
            MaxTextWidth = Math.Max(10, box.Width - 12),
            MaxLineCount = 2,
            TextAlignment = TextAlignment.Center,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        dc.DrawText(text, new Point(box.X + 6, box.Y + (box.Height - text.Height) / 2));
    }
}

/// <summary>A read-only piece kept as it was (VML picture, embedded object) shown as a placeholder of its size.</summary>
public sealed class PlaceholderRun : InkObject
{
    private readonly string _label;
    private readonly bool _dark;

    public PlaceholderRun(string label, TextRunProperties props, double width, double height, bool dark)
        : base(props, width, height, height)
    {
        _label = label;
        _dark = dark;
    }

    public override void DrawIn(DrawingContext dc, Rect box) => DrawingRun.Placeholder(dc, box, _label, _dark);
}

/// <summary>The footnote separator line (a short rule, or the full width for continuations).</summary>
public sealed class SeparatorRun : InkObject
{
    private readonly Color _color;

    public SeparatorRun(TextRunProperties props, double width, Color color)
        : base(props, width, 8, 6)
    {
        _color = color;
    }

    public override void DrawIn(DrawingContext dc, Rect box) =>
        dc.DrawLine(Paint.Pen(_color, 0.75), new Point(box.Left, box.Top + 4), new Point(box.Right, box.Top + 4));
}
