using System.Windows;
using System.Windows.Controls;

namespace Inkwell.Controls;

/// <summary>Gives its child the largest size with a fixed aspect ratio that fits, centred (no letterbox bars).</summary>
public sealed class AspectPanel : Panel
{
    public static readonly DependencyProperty RatioProperty = DependencyProperty.Register(
        nameof(Ratio), typeof(double), typeof(AspectPanel), new FrameworkPropertyMetadata(16.0 / 9, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Width / height.</summary>
    public double Ratio
    {
        get => (double)GetValue(RatioProperty);
        set => SetValue(RatioProperty, value);
    }

    /// <summary>Align to the top instead of centring vertically.</summary>
    public bool AlignTop { get; set; }

    private Size Fit(Size available)
    {
        var ratio = Ratio <= 0 ? 16.0 / 9 : Ratio;
        var w = double.IsInfinity(available.Width) ? (double.IsInfinity(available.Height) ? 320 : available.Height * ratio) : available.Width;
        var h = w / ratio;
        if (!double.IsInfinity(available.Height) && h > available.Height)
        {
            h = available.Height;
            w = h * ratio;
        }
        return new Size(Math.Max(0, w), Math.Max(0, h));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = Fit(availableSize);
        foreach (UIElement child in InternalChildren) child.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = Fit(finalSize);
        var x = (finalSize.Width - size.Width) / 2;
        var y = AlignTop ? 0 : (finalSize.Height - size.Height) / 2;
        foreach (UIElement child in InternalChildren) child.Arrange(new Rect(new Point(x, y), size));
        return finalSize;
    }
}
