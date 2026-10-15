using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Inkwell.Controls;


namespace Inkwell.Views.Panels;

/// <summary>Ctrl+K: type to find any command, style, design, template or heading, then Enter.</summary>
public sealed class CommandPalette : Grid
{
    private readonly EditorView _editor;
    private readonly TextBox _search = new();
    private readonly ListBox _list = new();
    private readonly Border _surface = new();
    private readonly TextBlock _empty = new() { Text = "Nothing matches. Try other words.", Margin = new Thickness(20, 14, 20, 18), Visibility = Visibility.Collapsed };
    private static readonly List<string> s_recent = new();

    private sealed class Entry
    {
        public string Title = "";
        public string Category = "";
        public string? Icon;
        public string? Shortcut;
        public string Keywords = "";
        public Action Run = () => { };
        public int Score;
        public string Key => Category + "|" + Title;
    }

    public CommandPalette(EditorView editor)
    {
        _editor = editor;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // catches clicks outside
        MouseDown += (_, e) =>
        {
            if (!_surface.IsMouseOver) Close();
        };

        _surface.SetResourceReference(StyleProperty, "FlyoutSurface");
        _surface.Width = 620;
        _surface.MaxHeight = 520;
        _surface.HorizontalAlignment = HorizontalAlignment.Center;
        _surface.VerticalAlignment = VerticalAlignment.Top;
        _surface.Margin = new Thickness(0, 72, 0, 0);
        AutomationProperties.SetName(_surface, "Command palette");

        var dock = new DockPanel();
        var searchRow = new Border { Padding = new Thickness(10, 10, 10, 8), BorderThickness = new Thickness(0, 0, 0, 1) };
        searchRow.SetResourceReference(Border.BorderBrushProperty, "Divider");
        _search.SetResourceReference(StyleProperty, "SearchBox");
        _search.FontSize = 15;
        _search.MinHeight = 38;
        Ui.SetPlaceholder(_search, "Search commands, styles, templates and headings…");
        AutomationProperties.SetName(_search, "Search commands");
        _search.TextChanged += (_, _) => Filter();
        _search.PreviewKeyDown += OnKey;
        searchRow.Child = _search;
        DockPanel.SetDock(searchRow, Dock.Top);
        dock.Children.Add(searchRow);

        var footer = new TextBlock { Text = "↑↓ to choose · Enter to run · Esc to close", FontSize = 11, Margin = new Thickness(16, 6, 16, 9) };
        footer.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(footer);
        DockPanel.SetDock(_empty, Dock.Top);
        dock.Children.Add(_empty);

        _list.Background = Brushes.Transparent;
        _list.BorderThickness = new Thickness(0);
        _list.Padding = new Thickness(0, 6, 0, 4);
        _list.MaxHeight = 400;
        _list.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "PaletteItem");
        _list.SetResourceReference(Control.ForegroundProperty, "Text.Primary");
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        AutomationProperties.SetName(_list, "Results");
        _list.MouseLeftButtonUp += (_, e) =>
        {
            if (_list.SelectedItem is ListBoxItem { Tag: Entry entry }) RunEntry(entry);
        };
        dock.Children.Add(_list);
        _surface.Child = dock;
        Children.Add(_surface);
    }

    public event Action? Closed;

    public void Open()
    {
        Filter();
        _surface.Opacity = 0;
        _surface.RenderTransform = new TranslateTransform(0, -8);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _surface.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease });
        _surface.RenderTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _search.Focus();
            Keyboard.Focus(_search);
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void Close()
    {
        Closed?.Invoke();
    }

    private IEnumerable<Entry> AllEntries()
    {
        foreach (var c in _editor.Commands)
        {
            if (!c.InPalette) continue;
            bool can;
            try { can = c.CanExecute(); } catch { can = false; }
            if (!can) continue;
            yield return new Entry { Title = c.Title, Category = c.Category, Icon = c.Icon, Shortcut = c.Gesture?.Text, Keywords = c.Keywords, Run = c.Execute };
        }
        // Styles: apply any paragraph or character style by name.
        var doc = _editor.Session.Document;
        foreach (var style in doc.Styles.All.Where(s => (s.Type is "paragraph" or "character") && (!s.Hidden || s.QuickStyle)).OrderBy(s => s.UiPriority))
        {
            var id = style.Id;
            var name = Core.Editing.DocEditor.DisplayName(style);
            yield return new Entry { Title = "Style: " + name, Category = "Style", Icon = style.Type == "character" ? "Icon.TextT" : "Icon.TextEditStyle", Keywords = "apply format " + style.Name, Run = () => _editor.Run(() => _editor.Editor.ApplyStyle(id)) };
        }
        // Headings: jump to any of them.
        foreach (var p in doc.Body.Paragraphs)
        {
            var rp = _editor.Editor.Styles.ResolvePara(p);
            if (rp.OutlineLevel is < 0 or > 5) continue;
            var text = p.PlainText().Trim();
            if (text.Length == 0) continue;
            var target = p;
            yield return new Entry
            {
                Title = new string(' ', rp.OutlineLevel * 2) + text,
                Category = "Go to",
                Icon = rp.OutlineLevel switch { 0 => "Icon.TextHeader1", 1 => "Icon.TextHeader2", _ => "Icon.TextHeader3" },
                Keywords = "heading section",
                Run = () => _editor.GoTo(new Core.Editing.DocPos(target, 0)),
            };
        }
    }

    private static readonly string[] Starters =
    {
        "Insert|Table", "Insert|Picture…", "Insert|Footnote", "Insert|Table of contents", "Format|Reveal formatting", "Page|Make this page landscape",
        "View|Focus mode", "View|Pageless view", "File|Export as PDF…", "File|Version history…",
    };

    private void Filter()
    {
        var query = _search.Text.Trim().ToLowerInvariant();
        var words = query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var entries = AllEntries().ToList();
        List<Entry> results;
        if (words.Length == 0)
        {
            var ordered = new List<Entry>();
            foreach (var key in s_recent.Concat(Starters))
            {
                var e = entries.FirstOrDefault(x => x.Key == key);
                if (e != null && !ordered.Contains(e)) ordered.Add(e);
            }
            results = ordered.Take(12).ToList();
        }
        else
        {
            foreach (var e in entries) e.Score = Score(e, words);
            // Equal scores keep the commands' own order (layouts in gallery order, File before Edit, …).
            results = entries.Where(e => e.Score > 0).OrderByDescending(e => e.Score).Take(60).ToList();
        }

        _list.Items.Clear();
        foreach (var e in results) _list.Items.Add(Row(e));
        _empty.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
    }

    private static int Score(Entry e, string[] words)
    {
        var title = e.Title.Trim().ToLowerInvariant();
        var category = e.Category.ToLowerInvariant();
        var keywords = e.Keywords.ToLowerInvariant();
        var titleWords = title.Split(new[] { ' ', ':', '–', '-', '(', ')', '…' }, StringSplitOptions.RemoveEmptyEntries);
        var score = 0;
        foreach (var w in words)
        {
            if (titleWords.Any(t => t == w)) score += 14;
            else if (titleWords.Any(t => t.StartsWith(w, StringComparison.Ordinal))) score += 10;
            else if (title.Contains(w)) score += 6;
            else if (category.StartsWith(w, StringComparison.Ordinal)) score += 5;
            else if (keywords.Contains(w)) score += 3;
            else if (Subsequence(title, w)) score += 1;
            else return 0;
        }
        if (title.StartsWith(string.Join(" ", words), StringComparison.Ordinal)) score += 8;
        if (e.Category == "Go to") score -= 2;
        return Math.Max(1, score);
    }

    private static bool Subsequence(string text, string word)
    {
        var i = 0;
        foreach (var ch in text)
            if (i < word.Length && ch == word[i]) i++;
        return i == word.Length && word.Length >= 3;
    }

    private ListBoxItem Row(Entry e)
    {
        var dock = new DockPanel { Height = 38 };
        if (e.Shortcut != null)
        {
            var key = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 2), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            key.SetResourceReference(Border.BackgroundProperty, "Subtle.FillSecondary");
            var keyText = new TextBlock { Text = e.Shortcut, FontSize = 11 };
            keyText.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
            key.Child = keyText;
            DockPanel.SetDock(key, Dock.Right);
            dock.Children.Add(key);
        }
        var category = new TextBlock { Text = e.Category, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        category.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
        DockPanel.SetDock(category, Dock.Right);
        dock.Children.Add(category);
        var icon = new IconView { Width = 16, Height = 16, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        if (e.Icon != null && TryFindResource(e.Icon) is Geometry g) icon.Data = g;
        icon.SetResourceReference(IconView.ForegroundProperty, "Text.Secondary");
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);
        dock.Children.Add(new TextBlock { Text = e.Title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var item = new ListBoxItem { Content = dock, Tag = e };
        AutomationProperties.SetName(item, e.Title + (e.Shortcut != null ? ", " + e.Shortcut : ""));
        return item;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.Down:
                if (_list.Items.Count > 0) _list.SelectedIndex = (_list.SelectedIndex + 1) % _list.Items.Count;
                _list.ScrollIntoView(_list.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up:
                if (_list.Items.Count > 0) _list.SelectedIndex = (_list.SelectedIndex - 1 + _list.Items.Count) % _list.Items.Count;
                _list.ScrollIntoView(_list.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter:
                if (_list.SelectedItem is ListBoxItem { Tag: Entry entry }) RunEntry(entry);
                e.Handled = true;
                break;
        }
    }

    private void RunEntry(Entry entry)
    {
        s_recent.Remove(entry.Key);
        if (entry.Category != "Go to") s_recent.Insert(0, entry.Key);
        if (s_recent.Count > 6) s_recent.RemoveAt(s_recent.Count - 1);
        Close();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                entry.Run();
            }
            catch (Exception ex)
            {
                Services.Log.Error(ex, "Palette: " + entry.Title);
            }
        }), System.Windows.Threading.DispatcherPriority.Input);
    }
}
