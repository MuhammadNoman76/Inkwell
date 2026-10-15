using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Inkwell.Controls;
using Inkwell.Core.Rendering;

namespace Inkwell.Views.Panels;

/// <summary>The font flyout: this document's fonts, Inkwell's bundled fonts, common Windows fonts, then every installed font; type to search.</summary>
public static class FontPicker
{
    public static Popup Show(FrameworkElement target, string? current, IReadOnlyList<string> documentFonts, IReadOnlyList<string> featured, IReadOnlyList<string> installed, Action<string> picked)
    {
        var root = new DockPanel { Width = 300, Height = 420, Margin = new Thickness(8) };
        var search = new TextBox { Margin = new Thickness(2, 2, 2, 8) };
        search.SetResourceReference(FrameworkElement.StyleProperty, "SearchBox");
        Ui.SetPlaceholder(search, "Search fonts");
        AutomationProperties.SetName(search, "Search fonts");
        DockPanel.SetDock(search, Dock.Top);
        root.Children.Add(search);
        var list = new ListBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        list.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "PaletteItem");
        list.SetResourceReference(Control.ForegroundProperty, "Text.Primary");
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(list, true);
        AutomationProperties.SetName(list, "Fonts");
        root.Children.Add(list);
        Popup? popup = null;

        void Fill()
        {
            list.Items.Clear();
            var q = search.Text.Trim();
            bool Match(string name) => q.Length == 0 || name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
            void Section(string title, IEnumerable<string> names)
            {
                var items = names.Where(Match).ToList();
                if (items.Count == 0) return;
                var header = new TextBlock { Text = title, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 8, 8, 4) };
                header.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
                list.Items.Add(new ListBoxItem { Content = header, IsEnabled = false, Focusable = false });
                foreach (var name in items)
                {
                    var row = new DockPanel { Height = 32 };
                    if (string.Equals(name, current, StringComparison.OrdinalIgnoreCase))
                    {
                        var check = new TextBlock { Text = "✓", Margin = new Thickness(8, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
                        DockPanel.SetDock(check, Dock.Right);
                        row.Children.Add(check);
                    }
                    if (FontCatalog.IsBundled(name))
                    {
                        var tag = new TextBlock { Text = "Inkwell", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
                        tag.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
                        DockPanel.SetDock(tag, Dock.Right);
                        row.Children.Add(tag);
                    }
                    row.Children.Add(new TextBlock { Text = name, FontFamily = FontCatalog.Get(name), FontSize = 15, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 0, 0) });
                    var item = new ListBoxItem { Content = row, Tag = name };
                    AutomationProperties.SetName(item, name);
                    list.Items.Add(item);
                }
            }
            Section("This document", documentFonts);
            Section("Recommended", featured.Except(documentFonts, StringComparer.OrdinalIgnoreCase));
            Section("All fonts", installed);
            var first = list.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is string);
            list.SelectedItem = first;
        }

        void Pick()
        {
            if (list.SelectedItem is not ListBoxItem { Tag: string name })
            {
                if (search.Text.Trim().Length > 0) name = search.Text.Trim();
                else return;
            }
            popup!.IsOpen = false;
            picked(name);
        }

        search.TextChanged += (_, _) => Fill();
        search.PreviewKeyDown += (_, e) =>
        {
            var items = list.Items.OfType<ListBoxItem>().Where(i => i.Tag is string).ToList();
            var index = list.SelectedItem is ListBoxItem selected ? items.IndexOf(selected) : -1;
            switch (e.Key)
            {
                case Key.Down when items.Count > 0:
                    list.SelectedItem = items[Math.Min(items.Count - 1, index + 1)];
                    list.ScrollIntoView(list.SelectedItem);
                    e.Handled = true;
                    break;
                case Key.Up when items.Count > 0:
                    list.SelectedItem = items[Math.Max(0, index - 1)];
                    list.ScrollIntoView(list.SelectedItem);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    Pick();
                    e.Handled = true;
                    break;
            }
        };
        list.MouseLeftButtonUp += (_, _) => Pick();
        Fill();
        popup = Views.Flyouts.Show(target, root);
        search.Dispatcher.BeginInvoke(new Action(() => Keyboard.Focus(search)), System.Windows.Threading.DispatcherPriority.Input);
        return popup;
    }
}

/// <summary>Symbols and special characters in a grid; click to insert.</summary>
public static class SymbolPicker
{
    private static readonly (string Group, string Chars)[] Groups =
    {
        ("Punctuation", "–—‘’“”«»‹›…·•†‡§¶©®™°′″‰¡¿"),
        ("Maths", "±×÷≠≈≤≥∞√∑∏∫∂∆∇∈∉∩∪⊂⊃∧∨¬∀∃∅πµΩαβγδεθλσφω½¼¾⅓⅔"),
        ("Arrows and shapes", "←→↑↓↔⇐⇒⇔↗↘✓✗★☆●○■□▲△◆◇♠♣♥♦"),
        ("Currency", "€£¥¢₹₨₩₽₺₿"),
        ("Letters", "ÀÁÂÄÇÈÉÊËÍÎÏÑÓÔÖØÙÚÛÜßàáâäçèéêëíîïñóôöøùúûüÿæœ"),
    };

    public static void Show(EditorView view)
    {
        var stack = new StackPanel { Margin = new Thickness(12, 8, 12, 12), Width = 340 };
        Popup? popup = null;
        foreach (var (group, chars) in Groups)
        {
            var header = new TextBlock { Text = group, Margin = new Thickness(0, 6, 0, 4) };
            header.SetResourceReference(FrameworkElement.StyleProperty, "PanelHeader");
            stack.Children.Add(header);
            var wrap = new WrapPanel();
            foreach (var c in chars)
            {
                var ch = c.ToString();
                var b = new Button { Content = ch, Width = 32, Height = 32, FontSize = 16, Padding = new Thickness(0), Margin = new Thickness(0, 0, 2, 2), ToolTip = $"U+{(int)c:X4}" };
                b.SetResourceReference(FrameworkElement.StyleProperty, "SubtleButton");
                AutomationProperties.SetName(b, ch);
                b.Click += (_, _) =>
                {
                    view.Run(() => view.Editor.InsertText(ch));
                    view.FocusDocument();
                };
                wrap.Children.Add(b);
            }
            stack.Children.Add(wrap);
        }
        var note = new TextBlock { Text = "Tip: Windows key + . opens emoji and more symbols.", FontSize = 11.5, Margin = new Thickness(0, 8, 0, 0) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
        stack.Children.Add(note);
        var target = view.DocumentView;
        popup = Views.Flyouts.Show(target, stack, PlacementMode.Center);
        _ = popup;
    }
}
