using System.Windows;
using System.Windows.Controls;
using Inkwell.Controls;

namespace Inkwell.Views;

/// <summary>A small dialog that asks for one line of text.</summary>
public sealed class InputDialog : FluentWindow
{
    private readonly TextBox _box = new();
    private bool _ok;

    private InputDialog(string heading, string message, string value, string okText)
    {
        Title = "Inkwell";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        IsDialog = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var body = new StackPanel { Margin = new Thickness(24, 22, 24, 24) };
        var h = new TextBlock { Text = heading };
        h.SetResourceReference(StyleProperty, "Text.Subtitle");
        body.Children.Add(h);
        var m = new TextBlock { Text = message, Margin = new Thickness(0, 8, 0, 14) };
        m.SetResourceReference(StyleProperty, "Text.BodySecondary");
        body.Children.Add(m);
        _box.Text = value;
        System.Windows.Automation.AutomationProperties.SetName(_box, heading);
        body.Children.Add(_box);
        root.Children.Add(body);

        var footer = new Border();
        footer.SetResourceReference(StyleProperty, "DialogFooter");
        Grid.SetRow(footer, 1);
        var buttons = new Grid();
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        var ok = new Button { Content = okText, IsDefault = true };
        ok.SetResourceReference(StyleProperty, "AccentButton");
        ok.Click += (_, _) =>
        {
            _ok = true;
            Close();
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        Grid.SetColumn(cancel, 2);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        footer.Child = buttons;
        root.Children.Add(footer);
        Content = root;
        Loaded += (_, _) =>
        {
            _box.Focus();
            _box.SelectAll();
        };
    }

    /// <summary>Returns the text, or null if cancelled.</summary>
    public static string? Ask(Window owner, string heading, string message, string value, string okText)
    {
        var dialog = new InputDialog(heading, message, value, okText) { Owner = owner };
        dialog.ShowDialog();
        return dialog._ok ? dialog._box.Text : null;
    }
}
