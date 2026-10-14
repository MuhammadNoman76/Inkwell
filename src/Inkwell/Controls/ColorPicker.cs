using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Inkwell.Controls;

/// <summary>A small HSV colour picker (saturation / value square, hue strip, hex box) shown in a popup.</summary>
public sealed class ColorPicker : Border
{
    private const double SquareSize = 200;
    private readonly Grid _square = new() { Width = SquareSize, Height = 150, ClipToBounds = true, Cursor = Cursors.Cross };
    private readonly Border _hueLayer = new();
    private readonly Ellipse _knob = new() { Width = 14, Height = 14, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
    private readonly Grid _hueStrip = new() { Width = SquareSize, Height = 14, Cursor = Cursors.Hand, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Border _hueKnob = new() { Width = 6, Height = 18, CornerRadius = new CornerRadius(3), BorderBrush = Brushes.White, BorderThickness = new Thickness(2), IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _hex = new() { Width = 110 };
    private readonly Border _preview = new() { Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Margin = new Thickness(10, 0, 0, 0) };
    private double _h, _s = 1, _v = 1;

    public ColorPicker(Color initial)
    {
        SetResourceReference(StyleProperty, "FlyoutSurface");
        Padding = new Thickness(14);
        var root = new StackPanel();

        _hueLayer.Child = null;
        var white = new Border { Background = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0) };
        var black = new Border { Background = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90) };
        var squareFrame = new Border { CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = _square };
        _square.Children.Add(_hueLayer);
        _square.Children.Add(white);
        _square.Children.Add(black);
        var knobCanvas = new Canvas { IsHitTestVisible = false };
        knobCanvas.Children.Add(_knob);
        _square.Children.Add(knobCanvas);
        _knob.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 0, Opacity = 0.6 };
        _square.MouseLeftButtonDown += (_, e) =>
        {
            _square.CaptureMouse();
            PickSquare(e.GetPosition(_square));
        };
        _square.MouseMove += (_, e) =>
        {
            if (_square.IsMouseCaptured) PickSquare(e.GetPosition(_square));
        };
        _square.MouseLeftButtonUp += (_, _) =>
        {
            _square.ReleaseMouseCapture();
            Commit();
        };
        root.Children.Add(squareFrame);

        var hue = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        for (var i = 0; i <= 6; i++) hue.GradientStops.Add(new GradientStop(FromHsv(i * 60 % 360, 1, 1), i / 6.0));
        _hueStrip.Children.Add(new Border { Background = hue, CornerRadius = new CornerRadius(7) });
        var hueCanvas = new Canvas { IsHitTestVisible = false };
        hueCanvas.Children.Add(_hueKnob);
        _hueStrip.Children.Add(hueCanvas);
        _hueStrip.MouseLeftButtonDown += (_, e) =>
        {
            _hueStrip.CaptureMouse();
            PickHue(e.GetPosition(_hueStrip).X);
        };
        _hueStrip.MouseMove += (_, e) =>
        {
            if (_hueStrip.IsMouseCaptured) PickHue(e.GetPosition(_hueStrip).X);
        };
        _hueStrip.MouseLeftButtonUp += (_, _) =>
        {
            _hueStrip.ReleaseMouseCapture();
            Commit();
        };
        root.Children.Add(_hueStrip);

        var row = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(_preview, Dock.Right);
        row.Children.Add(_preview);
        _hex.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) ApplyHex();
        };
        _hex.LostKeyboardFocus += (_, _) => ApplyHex();
        System.Windows.Automation.AutomationProperties.SetName(_hex, "Colour as hex");
        row.Children.Add(_hex);
        root.Children.Add(row);
        Child = root;
        SetColor(initial);
    }

    public Color Color => FromHsv(_h, _s, _v);

    /// <summary>Raised when a colour is picked (on release, or Enter in the hex box).</summary>
    public event Action<Color>? ColorPicked;

    private void SetColor(Color c)
    {
        ToHsv(c, out _h, out _s, out _v);
        UpdateVisuals();
    }

    private void PickSquare(Point p)
    {
        _s = MathEx.Clamp(p.X / _square.Width, 0, 1);
        _v = 1 - MathEx.Clamp(p.Y / _square.Height, 0, 1);
        UpdateVisuals();
    }

    private void PickHue(double x)
    {
        _h = MathEx.Clamp(x / _hueStrip.Width, 0, 0.9999) * 360;
        UpdateVisuals();
    }

    private void ApplyHex()
    {
        var c = Core.Styles.OoxColor.Parse(_hex.Text.Trim().TrimStart('#'));
        if (c == null) return;
        SetColor(c.Value);
        Commit();
    }

    private void Commit() => ColorPicked?.Invoke(Color);

    private void UpdateVisuals()
    {
        _hueLayer.Background = new SolidColorBrush(FromHsv(_h, 1, 1));
        Canvas.SetLeft(_knob, _s * _square.Width - 7);
        Canvas.SetTop(_knob, (1 - _v) * _square.Height - 7);
        Canvas.SetLeft(_hueKnob, _h / 360 * _hueStrip.Width - 3);
        Canvas.SetTop(_hueKnob, -2);
        var c = Color;
        _preview.Background = new SolidColorBrush(c);
        if (!_hex.IsKeyboardFocusWithin) _hex.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    public static Color FromHsv(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        double r, g, b;
        if (h < 60) (r, g, b) = (c, x, 0);
        else if (h < 120) (r, g, b) = (x, c, 0);
        else if (h < 180) (r, g, b) = (0, c, x);
        else if (h < 240) (r, g, b) = (0, x, c);
        else if (h < 300) (r, g, b) = (x, 0, c);
        else (r, g, b) = (c, 0, x);
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    public static void ToHsv(Color c, out double h, out double s, out double v)
    {
        var r = c.R / 255.0;
        var g = c.G / 255.0;
        var b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        v = max;
        s = max <= 0 ? 0 : d / max;
        if (d <= 0) h = 0;
        else if (max == r) h = 60 * (((g - b) / d) % 6);
        else if (max == g) h = 60 * ((b - r) / d + 2);
        else h = 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
    }

    /// <summary>Opens a picker in a popup under <paramref name="target"/>.</summary>
    public static void Show(FrameworkElement target, Color initial, Action<Color> picked)
    {
        var picker = new ColorPicker(initial) { Margin = new Thickness(8, 4, 8, 14) };
        var popup = new Popup
        {
            Child = picker,
            PlacementTarget = target,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
        };
        picker.ColorPicked += c => picked(c);
        popup.IsOpen = true;
        picker.Focus();
    }

    public static string Hex(Color c) => string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
}
