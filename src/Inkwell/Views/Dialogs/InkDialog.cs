using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Inkwell.Controls;

namespace Inkwell.Views.Dialogs;

/// <summary>
/// A standard Inkwell dialog: heading, optional description, a body and a footer with buttons. Shown modally by
/// <see cref="Run"/>; the capture mode shows them without blocking.
/// </summary>
public class InkDialog : FluentWindow
{
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly ScrollViewer _scroller;

    public InkDialog(string heading, string? description = null, double width = 480)
    {
        Title = heading;
        Width = width;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        IsDialog = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MaxHeight = Math.Max(420, SystemParameters.WorkArea.Height - 40);

        var root = new DockPanel();
        var footer = new Border();
        footer.SetResourceReference(StyleProperty, "DialogFooter");
        footer.Child = _buttons;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var top = new StackPanel { Margin = new Thickness(24, 22, 24, 0) };
        var h = new TextBlock { Text = heading, TextWrapping = TextWrapping.Wrap };
        h.SetResourceReference(StyleProperty, "Text.Subtitle");
        top.Children.Add(h);
        if (!string.IsNullOrEmpty(description))
        {
            var d = new TextBlock { Text = description, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
            d.SetResourceReference(StyleProperty, "Text.BodySecondary");
            top.Children.Add(d);
        }
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        Body = new StackPanel { Margin = new Thickness(24, 16, 24, 22) };
        _scroller = new ScrollViewer { Content = Body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        root.Children.Add(_scroller);
        Content = root;
    }

    /// <summary>Where the dialog's content goes.</summary>
    public StackPanel Body { get; }

    /// <summary>The button that closed the dialog (its text), or null when closed otherwise.</summary>
    public string? Choice { get; private set; }

    public Button AddButton(string text, bool primary = false, Func<bool>? onClick = null, bool isCancel = false)
    {
        var b = new Button { Content = text, MinWidth = 96, Margin = new Thickness(8, 0, 0, 0), IsDefault = primary, IsCancel = isCancel };
        if (primary) b.SetResourceReference(StyleProperty, "AccentButton");
        AutomationProperties.SetName(b, text);
        b.Click += (_, _) =>
        {
            // The handler returns false to keep the dialog open (a value needs fixing).
            if (onClick != null && !onClick()) return;
            Choice = text;
            Close();
        };
        _buttons.Children.Add(b);
        return b;
    }

    /// <summary>A small grey label above a field.</summary>
    public TextBlock Label(string text, double top = 12)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(0, top, 0, 6), TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(StyleProperty, "Text.BodySecondary");
        Body.Children.Add(t);
        return t;
    }

    public TextBox Field(string label, string value, string? placeholder = null, double top = 12)
    {
        Label(label, top);
        var box = new TextBox { Text = value };
        if (placeholder != null) Ui.SetPlaceholder(box, placeholder);
        AutomationProperties.SetName(box, label);
        Body.Children.Add(box);
        return box;
    }

    public ComboBox Choices(string label, IEnumerable<string> items, int selected = 0, double top = 12)
    {
        Label(label, top);
        var combo = new ComboBox();
        foreach (var i in items) combo.Items.Add(i);
        combo.SelectedIndex = Math.Min(selected, combo.Items.Count - 1);
        AutomationProperties.SetName(combo, label);
        Body.Children.Add(combo);
        return combo;
    }

    public CheckBox Check(string text, bool on, double top = 10)
    {
        var c = new CheckBox { Content = text, IsChecked = on, Margin = new Thickness(0, top, 0, 0) };
        Body.Children.Add(c);
        return c;
    }

    /// <summary>Shows the dialog modally over <paramref name="owner"/>; returns the button chosen.</summary>
    public string? Run(Window owner)
    {
        Owner = owner;
        ShowDialog();
        return Choice;
    }
}
