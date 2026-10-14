using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Inkwell.Controls;
using Inkwell.Interop;
using Inkwell.Services;
using Inkwell.Themes;

namespace Inkwell.Views;

/// <summary>Settings: appearance, autosave, smart guides, slide show displays, updates, about.</summary>
public sealed class SettingsWindow : FluentWindow
{
    private static SettingsWindow? s_open;
    private readonly TextBlock _updateStatus = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };

    public SettingsWindow()
    {
        Title = "Settings";
        Width = 720;
        Height = 780;
        MinWidth = 540;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var s = AppSettings.Current;

        var stack = new StackPanel { Margin = new Thickness(32, 20, 32, 32), MaxWidth = 820 };
        var title = new TextBlock { Text = "Settings", Margin = new Thickness(0, 0, 0, 20) };
        title.SetResourceReference(StyleProperty, "Text.Title");
        stack.Children.Add(title);

        Header(stack, "Appearance");
        stack.Children.Add(Row("Icon.DarkTheme", "App theme", "Light or dark; the accent colour follows Windows. Slides always use their own theme.",
            Combo(new[] { "Use my Windows setting", "Light", "Dark" }, (int)s.Theme, i =>
            {
                var theme = (AppTheme)i;
                AppSettings.Update(x => x.Theme = theme);
                ThemeManager.SetMode(theme);
            }, "App theme")));

        Header(stack, "Editing");
        stack.Children.Add(Row("Icon.Save", "Save automatically", "Saves your presentation a few seconds after you stop editing. New presentations are saved automatically once you've saved them the first time.",
            Switch(s.AutoSaveFile, on => AppSettings.Update(x => x.AutoSaveFile = on), "Save automatically")));
        var autosaveValues = new[] { 0, 1, 2, 5, 10 };
        stack.Children.Add(Row("Icon.History", "Keep a recovery copy", "If Inkwell closes unexpectedly, unsaved work can be recovered from the start screen.",
            Combo(new[] { "Off", "Every minute", "Every 2 minutes", "Every 5 minutes", "Every 10 minutes" }, Math.Max(0, Array.IndexOf(autosaveValues, s.AutosaveMinutes)), i =>
            {
                var minutes = autosaveValues[i];
                AppSettings.Update(x => x.AutosaveMinutes = minutes);
            }, "Recovery copy interval")));
        stack.Children.Add(Row("Icon.Ruler", "Smart guides", "Snap to edges, centres and equal spacing while moving things. Hold Alt to skip them once.",
            Switch(s.SnapToGuides, on => AppSettings.Update(x => x.SnapToGuides = on), "Smart guides")));
        stack.Children.Add(Row("Icon.TextProofingTools", "Check spelling as I type", "Underlines misspelt words in red while you edit slide text and notes. Right-click a word for suggestions.",
            Switch(s.SpellCheck, on => AppSettings.Update(x => x.SpellCheck = on), "Check spelling as I type")));

        Header(stack, "Slide show");
        stack.Children.Add(Row("Icon.Presenter", "Presenter view", "With two displays, show your notes, the next slide and a timer on yours while the audience sees the slides.",
            Switch(s.UsePresenterView, on => AppSettings.Update(x => x.UsePresenterView = on), "Presenter view")));
        var monitors = Native.Monitors();
        var names = new List<string> { "Automatic (the second display)" };
        names.AddRange(monitors.Select(m => m.Describe));
        var selected = monitors.FindIndex(m => m.Device == s.AudienceDisplay) + 1;
        stack.Children.Add(Row("Icon.Desktop", "Show slides on", monitors.Count > 1 ? $"{monitors.Count} displays connected." : "One display connected: the slides fill it.",
            Combo(names, Math.Max(0, selected), i =>
            {
                var device = i == 0 ? "" : monitors[i - 1].Device;
                AppSettings.Update(x => x.AudienceDisplay = device);
            }, "Display for the audience")));

        Header(stack, "Updates");
        stack.Children.Add(Row("Icon.ArrowSync", "Check for updates automatically",
            "Once a day Inkwell downloads a small file from nomanshafiq.com to see if there's a new version. Nothing about you or your files is sent.",
            Switch(s.CheckForUpdates, on => AppSettings.Update(x => x.CheckForUpdates = on), "Check for updates automatically")));
        var check = new Button { Content = "Check now", VerticalAlignment = VerticalAlignment.Center };
        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            _updateStatus.Text = "Checking…";
            _updateStatus.Text = await UpdateService.CheckAsync(manual: true);
            check.IsEnabled = true;
        };
        var checkRow = Row("Icon.ArrowDownload", "Check for updates now", null, check);
        ((StackPanel)((DockPanel)checkRow.Child).Children[2]).Children.Add(_updateStatus);
        _updateStatus.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        _updateStatus.Text = $"You have Inkwell {Updater.CurrentVersionText} ({UpdateService.EditionName(Updater.Edition)}).";
        stack.Children.Add(checkRow);

        Header(stack, "About");
        var about = new Border { Padding = new Thickness(16) };
        about.SetResourceReference(StyleProperty, "Card");
        var dock = new DockPanel();
        var logo = new Image { Width = 56, Height = 56, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top };
        logo.SetResourceReference(Image.SourceProperty, "Logo.Mark");
        DockPanel.SetDock(logo, Dock.Left);
        dock.Children.Add(logo);
        var info = new StackPanel();
        var name = new TextBlock { Text = "Inkwell" };
        name.SetResourceReference(StyleProperty, "Text.Subtitle");
        info.Children.Add(name);
        var version = new TextBlock { Text = $"Version {Updater.CurrentVersionText} · {UpdateService.EditionName(Updater.Edition)}", Margin = new Thickness(0, 2, 0, 0) };
        version.SetResourceReference(StyleProperty, "Text.Caption");
        info.Children.Add(version);
        var links = new WrapPanel { Margin = new Thickness(-2, 10, 0, 0) };
        links.Children.Add(Link("About and licences", () => AboutDialog.Open(this)));
        links.Children.Add(Link("Website", () => UpdateService.OpenInBrowser(UpdateService.DownloadPageUrl)));
        links.Children.Add(Link("Open the settings folder", () =>
        {
            Directory.CreateDirectory(AppSettings.DataDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + AppSettings.DataDirectory + "\"") { UseShellExecute = true })?.Dispose();
        }));
        info.Children.Add(links);
        dock.Children.Add(info);
        about.Child = dock;
        stack.Children.Add(about);

        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        Closed += (_, _) =>
        {
            if (s_open == this) s_open = null;
        };
    }

    /// <summary>One settings window at a time.</summary>
    public static void ShowSingle(Window? owner)
    {
        if (s_open != null)
        {
            s_open.Activate();
            return;
        }
        s_open = new SettingsWindow();
        if (owner != null && owner.IsVisible) s_open.Owner = owner;
        else s_open.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        s_open.Show();
    }

    private static void Header(Panel stack, string text)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(2, stack.Children.Count > 1 ? 20 : 0, 0, 8) };
        t.SetResourceReference(StyleProperty, "Text.BodyStrong");
        stack.Children.Add(t);
    }

    private Border Row(string icon, string title, string? description, FrameworkElement control)
    {
        var row = new Border();
        row.SetResourceReference(StyleProperty, "SettingRow");
        var dock = new DockPanel();
        var iconView = new IconView { Width = 20, Height = 20, Margin = new Thickness(2, 0, 16, 0), Data = (Geometry)FindResource(icon) };
        DockPanel.SetDock(iconView, Dock.Left);
        dock.Children.Add(iconView);
        control.VerticalAlignment = VerticalAlignment.Center;
        control.Margin = new Thickness(16, 0, 0, 0);
        DockPanel.SetDock(control, Dock.Right);
        dock.Children.Add(control);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var t = new TextBlock { Text = title };
        t.SetResourceReference(StyleProperty, "Text.Body");
        text.Children.Add(t);
        if (description != null)
        {
            var d = new TextBlock { Text = description };
            d.SetResourceReference(StyleProperty, "Text.Caption");
            text.Children.Add(d);
        }
        dock.Children.Add(text);
        row.Child = dock;
        return row;
    }

    private static ComboBox Combo(IEnumerable<string> items, int selected, Action<int> changed, string name)
    {
        var combo = new ComboBox { Width = 240 };
        foreach (var i in items) combo.Items.Add(new ComboBoxItem { Content = i });
        combo.SelectedIndex = selected;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0) changed(combo.SelectedIndex);
        };
        AutomationProperties.SetName(combo, name);
        return combo;
    }

    private static CheckBox Switch(bool value, Action<bool> changed, string name)
    {
        var c = new CheckBox { IsChecked = value };
        c.SetResourceReference(StyleProperty, "ToggleSwitch");
        c.Click += (_, _) => changed(c.IsChecked == true);
        AutomationProperties.SetName(c, name);
        return c;
    }

    private static Button Link(string text, Action action)
    {
        var b = new Button { Content = text, Padding = new Thickness(2), Margin = new Thickness(0, 0, 16, 0) };
        b.SetResourceReference(StyleProperty, "LinkButton");
        b.Click += (_, _) => action();
        return b;
    }
}
