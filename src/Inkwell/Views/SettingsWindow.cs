using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Inkwell.Controls;
using Inkwell.Core.Editing;
using Inkwell.Core.Templates;
using Inkwell.Services;
using Inkwell.Themes;

namespace Inkwell.Views;

/// <summary>Settings: appearance, writing, autocorrect, saving and history, new documents, updates, about.</summary>
public sealed class SettingsWindow : FluentWindow
{
    private static SettingsWindow? s_open;
    private readonly TextBlock _updateStatus = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };

    public SettingsWindow()
    {
        Title = "Settings";
        Width = 760;
        Height = 820;
        MinWidth = 540;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var s = AppSettings.Current;

        var stack = new StackPanel { Margin = new Thickness(32, 20, 32, 32), MaxWidth = 860 };
        var title = new TextBlock { Text = "Settings", Margin = new Thickness(0, 0, 0, 20) };
        title.SetResourceReference(StyleProperty, "Text.Title");
        stack.Children.Add(title);

        Header(stack, "Appearance");
        stack.Children.Add(Row("Icon.DarkTheme", "App theme", "Light or dark; the accent colour follows Windows.",
            Combo(new[] { "Use my Windows setting", "Light", "Dark" }, (int)s.Theme, i =>
            {
                var theme = (AppTheme)i;
                AppSettings.Update(x => x.Theme = theme);
                ThemeManager.SetMode(theme);
            }, "App theme")));
        stack.Children.Add(Row("Icon.WeatherMoon", "Dark pages in dark mode",
            "Off: white pages on a dark background, as they will print. On: dark pages with light text, easier on the eyes at night (printing and PDF are never affected).",
            Switch(s.DarkPaper, on => AppSettings.Update(x => x.DarkPaper = on), "Dark pages in dark mode")));

        Header(stack, "Writing");
        var author = new TextBox { Width = 240, Text = s.AuthorName ?? "" };
        Ui.SetPlaceholder(author, Environment.UserName);
        author.LostFocus += (_, _) => AppSettings.Update(x => x.AuthorName = string.IsNullOrWhiteSpace(author.Text) ? null : author.Text.Trim());
        AutomationProperties.SetName(author, "Your name");
        stack.Children.Add(Row("Icon.Person", "Your name", "Shown on your comments and tracked changes.", author));
        stack.Children.Add(Row("Icon.TextProofingTools", "Check spelling as I type",
            Core.Proofing.WindowsSpellChecker.IsSupportedOs
                ? "Red squiggles under misspelt words, using Windows' own dictionaries for the languages you've added. Right-click a word for suggestions."
                : "Spell checking needs Windows 8 or later; it isn't available on this version of Windows.",
            Switch(s.SpellCheck, on => AppSettings.Update(x => x.SpellCheck = on), "Check spelling as I type")));
        stack.Children.Add(Row("Icon.Code", "Markdown shortcuts", "Type # for a heading, - for a bullet, 1. for a numbered list, > for a quote, **bold**, _italic_ and `code`. Each one is undone with a single Ctrl+Z.",
            Switch(s.MarkdownShortcuts, on => AppSettings.Update(x => x.MarkdownShortcuts = on), "Markdown shortcuts")));
        stack.Children.Add(Row("Icon.SlashForward", "Slash commands", "Type / at the start of a line to insert a table, heading, picture, contents, footnote and more.",
            Switch(s.SlashCommands, on => AppSettings.Update(x => x.SlashCommands = on), "Slash commands")));
        stack.Children.Add(Row("Icon.ClipboardPaste", "Paste matches my document",
            "Pasted text takes your document's fonts and styles (bold, italic, links and lists are kept). A small chip after pasting lets you keep the source formatting instead. Ctrl+Shift+V always pastes plain text.",
            Switch(s.PasteMatchesDestination, on => AppSettings.Update(x => x.PasteMatchesDestination = on), "Paste matches my document")));

        Header(stack, "Autocorrect");
        var note = new TextBlock
        {
            Text = "Every automatic change shows a small chip and is undone by one Ctrl+Z straight after it. Capitals are never added after abbreviations such as i.e., e.g. or etc.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 0, 0, 10),
        };
        note.SetResourceReference(StyleProperty, "Text.Caption");
        stack.Children.Add(note);
        foreach (var rule in AutoCorrectRules.All.Where(r => r.Id is not (AutoCorrectRules.MarkdownBlocks or AutoCorrectRules.MarkdownInline)))
        {
            var id = rule.Id;
            var enabled = rule.EnabledByDefault ? !s.DisabledAutoCorrect.Contains(id) : s.DisabledAutoCorrect.Contains("+" + id);
            stack.Children.Add(Row("Icon.Wand", rule.Name, rule.Description, Switch(enabled, on => AppSettings.Update(x =>
            {
                x.DisabledAutoCorrect.Remove(id);
                x.DisabledAutoCorrect.Remove("+" + id);
                if (rule.EnabledByDefault && !on) x.DisabledAutoCorrect.Add(id);
                if (!rule.EnabledByDefault && on) x.DisabledAutoCorrect.Add("+" + id);
            }), rule.Name)));
        }

        Header(stack, "Saving");
        stack.Children.Add(Row("Icon.Save", "Save automatically",
            "Saves a document that has a file a few seconds after you stop typing. New documents are never saved automatically until you choose where.",
            Switch(s.AutoSaveFile, on => AppSettings.Update(x => x.AutoSaveFile = on), "Save automatically")));
        var recoveryValues = new[] { 0, 1, 2, 5, 10 };
        stack.Children.Add(Row("Icon.ArrowSync", "Keep a recovery copy", "If Inkwell closes unexpectedly, unsaved work can be recovered from the start screen.",
            Combo(new[] { "Off", "Every minute", "Every 2 minutes", "Every 5 minutes", "Every 10 minutes" }, Math.Max(0, Array.IndexOf(recoveryValues, s.RecoveryMinutes)), i =>
            {
                var minutes = recoveryValues[i];
                AppSettings.Update(x => x.RecoveryMinutes = minutes);
            }, "Recovery copy interval")));
        stack.Children.Add(Row("Icon.History", "Version history",
            "Keeps earlier versions of your saved documents on this PC (in your local app data), so you can look back and restore one. Nothing leaves your PC.",
            Switch(s.VersionHistory, on => AppSettings.Update(x => x.VersionHistory = on), "Version history")));
        var capValues = new[] { 200, 500, 1000, 2000 };
        stack.Children.Add(Row("Icon.Delete", "Space for version history", "The oldest versions are removed when history reaches this size (the newest version of each document is always kept).",
            Combo(new[] { "200 MB", "500 MB", "1 GB", "2 GB" }, Math.Max(0, Array.IndexOf(capValues, s.HistoryTotalMegabytes)), i =>
            {
                var mb = capValues[i];
                AppSettings.Update(x => x.HistoryTotalMegabytes = mb);
            }, "Space for version history")));
        stack.Children.Add(Row("Icon.TextFont", "Embed Inkwell's fonts in documents",
            "Includes the fonts Inkwell bundles (Literata, Inter and the others, all under the SIL Open Font License) in saved .docx files, so they look the same on PCs that don't have them.",
            Switch(s.EmbedFonts, on => AppSettings.Update(x => x.EmbedFonts = on), "Embed fonts")));

        Header(stack, "New documents");
        var designs = DocumentDesigns.All.ToList();
        stack.Children.Add(Row("Icon.Color", "Design for blank documents", "Fonts, sizes and colours of the styles. You can change the design of any document later.",
            Combo(designs.Select(d => d.Name + " · " + d.Description), Math.Max(0, designs.FindIndex(d => d.Id == s.DefaultDesign)), i =>
            {
                var id = designs[i].Id;
                AppSettings.Update(x => x.DefaultDesign = id);
            }, "Design for blank documents")));
        var papers = new[] { "auto", "a4", "letter" };
        stack.Children.Add(Row("Icon.DocumentOnePage", "Paper size", "Automatic picks US Letter in the United States and Canada and A4 elsewhere.",
            Combo(new[] { "Automatic (by region)", "A4", "US Letter" }, Math.Max(0, Array.IndexOf(papers, s.DefaultPaper)), i =>
            {
                var paper = papers[i];
                AppSettings.Update(x => x.DefaultPaper = paper);
            }, "Paper size")));

        Header(stack, "Updates");
        stack.Children.Add(Row("Icon.ArrowSync", "Check for updates automatically",
            "Once a day Inkwell downloads a small file from nomanshafiq.com to see if there's a new version. Nothing about you or your documents is sent.",
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
        if (MainWindow.IsTestOffscreen)
        {
            s_open.WindowStartupLocation = WindowStartupLocation.Manual;
            s_open.Left = SystemParameters.VirtualScreenLeft - 3200;
            s_open.Top = SystemParameters.VirtualScreenTop + 20;
            s_open.ShowActivated = false;
        }
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
        var iconView = new IconView { Width = 20, Height = 20, Margin = new Thickness(2, 0, 16, 0), Data = TryFindResource(icon) as Geometry ?? (Geometry)FindResource("Icon.Settings") };
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
            var d = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap };
            d.SetResourceReference(StyleProperty, "Text.Caption");
            text.Children.Add(d);
        }
        dock.Children.Add(text);
        row.Child = dock;
        return row;
    }

    private static ComboBox Combo(IEnumerable<string> items, int selected, Action<int> changed, string name)
    {
        var combo = new ComboBox { Width = 260 };
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
