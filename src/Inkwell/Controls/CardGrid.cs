using System.Windows;
using System.Windows.Controls;

namespace Inkwell.Controls;

/// <summary>
/// Lays cards out in as many equal columns as fit (each at least <see cref="MinItemWidth"/> wide) and stretches
/// them to fill the row, so a grid of cards always lines up with the content above it.
/// </summary>
public sealed class CardGrid : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(CardGrid), new FrameworkPropertyMetadata(240.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(CardGrid), new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private (int Columns, double ItemWidth) Columns(double width)
    {
        if (double.IsInfinity(width) || width <= 0) width = MinItemWidth * 4 + Spacing * 3;
        var columns = Math.Max(1, (int)Math.Floor((width + Spacing) / (MinItemWidth + Spacing)));
        var itemWidth = Math.Max(1, (width - Spacing * (columns - 1)) / columns);
        return (columns, itemWidth);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (columns, itemWidth) = Columns(availableSize.Width);
        double total = 0, rowHeight = 0;
        var visible = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
        for (var i = 0; i < visible.Count; i++)
        {
            visible[i].Measure(new Size(itemWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, visible[i].DesiredSize.Height);
            if (i % columns == columns - 1 || i == visible.Count - 1)
            {
                total += rowHeight + (i == visible.Count - 1 ? 0 : Spacing);
                rowHeight = 0;
            }
        }
        var width = double.IsInfinity(availableSize.Width) ? columns * itemWidth + (columns - 1) * Spacing : availableSize.Width;
        return new Size(width, total);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (columns, itemWidth) = Columns(finalSize.Width);
        var visible = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
        double y = 0;
        for (var start = 0; start < visible.Count; start += columns)
        {
            var row = visible.Skip(start).Take(columns).ToList();
            var rowHeight = row.Max(c => c.DesiredSize.Height);
            for (var i = 0; i < row.Count; i++)
                row[i].Arrange(new Rect(i * (itemWidth + Spacing), y, itemWidth, rowHeight));
            y += rowHeight + Spacing;
        }
        return finalSize;
    }
}
