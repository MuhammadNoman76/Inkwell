using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Inkwell.Controls;

/// <summary>
/// A text box for a number: type a value (Enter or leaving commits), Up / Down or the mouse wheel step it
/// (Shift = ×10). An optional suffix (°, %, s) is shown and ignored when parsing.
/// </summary>
public sealed class NumberBox : TextBox
{
    private double _value;
    private bool _updating;

    public NumberBox()
    {
        // A subclass doesn't pick up the implicit TextBox style by itself.
        SetResourceReference(StyleProperty, typeof(TextBox));
        MinWidth = 60;
        HorizontalContentAlignment = HorizontalAlignment.Left;
        VerticalContentAlignment = VerticalAlignment.Center;
        PreviewKeyDown += OnKey;
        LostKeyboardFocus += (_, _) => Commit();
        PreviewMouseWheel += OnWheel;
        GotKeyboardFocus += (_, _) => SelectAll();
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (IsKeyboardFocusWithin) return;
            Focus();
            e.Handled = true;
        };
    }

    public double Minimum { get; set; } = double.MinValue;
    public double Maximum { get; set; } = double.MaxValue;
    public double Step { get; set; } = 1;
    public int Decimals { get; set; }
    public string Suffix { get; set; } = "";

    /// <summary>Raised when the user commits a new value.</summary>
    public event Action<double>? ValueChanged;

    public double Value
    {
        get => _value;
        set
        {
            _value = value;
            if (!IsKeyboardFocusWithin) Show();
        }
    }

    /// <summary>Shows nothing (several selected elements with different values).</summary>
    public void SetMixed()
    {
        _updating = true;
        Text = "";
        _updating = false;
    }

    private void Show()
    {
        _updating = true;
        Text = Math.Round(_value, Decimals).ToString("0." + new string('#', Math.Max(0, Decimals)), CultureInfo.CurrentCulture) + Suffix;
        _updating = false;
    }

    private void Commit()
    {
        if (_updating) return;
        var text = Text.Replace(Suffix, "").Trim();
        if (text.Length == 0)
        {
            Show();
            return;
        }
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) &&
            !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
        {
            Show();
            return;
        }
        Apply(v);
    }

    private void Apply(double v)
    {
        v = Math.Round(MathEx.Clamp(v, Minimum, Maximum), Math.Max(Decimals, 2));
        var changed = Math.Abs(v - _value) > 1e-9;
        _value = v;
        Show();
        if (IsKeyboardFocusWithin) SelectAll();
        if (changed) ValueChanged?.Invoke(v);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var big = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
        switch (e.Key)
        {
            case Key.Enter:
                Commit();
                e.Handled = true;
                break;
            case Key.Escape:
                Show();
                Keyboard.ClearFocus();
                break;
            case Key.Up:
                Apply(_value + Step * big);
                e.Handled = true;
                break;
            case Key.Down:
                Apply(_value - Step * big);
                e.Handled = true;
                break;
        }
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (!IsKeyboardFocusWithin) return;
        var big = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
        Apply(_value + Math.Sign(e.Delta) * Step * big);
        e.Handled = true;
    }
}
