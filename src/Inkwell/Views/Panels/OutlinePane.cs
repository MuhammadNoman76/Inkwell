using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Inkwell.Core.Editing;
using Inkwell.Core.Model;

namespace Inkwell.Views.Panels;

/// <summary>The outline on the left: the document's headings; click one to go there. The current one is marked.</summary>
public sealed class OutlinePane : Border
{
    private readonly EditorView _editor;
    private readonly ListBox _list = new();
    private readonly TextBlock _empty;
    private readonly DispatcherTimer _timer;
    private List<Paragraph> _headings = new();

    public OutlinePane(EditorView editor)
    {
        _editor = editor;
        Padding = new Thickness(10, 12, 4, 10);
        var dock = new DockPanel();
        var header = new TextBlock { Text = "Outline", Margin = new Thickness(8, 0, 0, 8) };
        header.SetResourceReference(StyleProperty, "PanelHeader");
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        _empty = new TextBlock
        {
            Text = "Headings show up here.\n\nUse Heading 1–3 from the style menu (Ctrl+Alt+1–3), or type # and a space at the start of a line.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8, 4, 12, 0),
            FontSize = 12.5,
        };
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
        DockPanel.SetDock(_empty, Dock.Top);
        dock.Children.Add(_empty);
        _list.Background = Brushes.Transparent;
        _list.BorderThickness = new Thickness(0);
        _list.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "PaletteItem");
        _list.SetResourceReference(Control.ForegroundProperty, "Text.Primary");
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        AutomationProperties.SetName(_list, "Headings");
        _list.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(_list, d) is ListBoxItem { Tag: Paragraph p })
                _editor.GoTo(new DocPos(p, 0));
        };
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _list.SelectedItem is ListBoxItem { Tag: Paragraph p })
            {
                _editor.GoTo(new DocPos(p, 0));
                e.Handled = true;
            }
        };
        dock.Children.Add(_list);
        Child = dock;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(350) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Rebuild();
        };
        editor.Editor.SelectionChanged += MarkCurrent;
        Rebuild();
    }

    /// <summary>The document changed: rebuild soon (debounced).</summary>
    public void Invalidate()
    {
        if (Visibility != Visibility.Visible) return;
        _timer.Stop();
        _timer.Start();
    }

    private void Rebuild()
    {
        var doc = _editor.Session.Document;
        var styles = _editor.Editor.Styles;
        var headings = new List<(Paragraph Para, int Level, string Text)>();
        foreach (var p in doc.Body.Paragraphs)
        {
            if (Nav.CellOf(p) != null) continue;
            var rp = styles.ResolvePara(p);
            var isTitle = rp.StyleId == "Title";
            if (rp.OutlineLevel > 5 && !isTitle) continue;
            var text = p.PlainText().Replace('\t', ' ').Trim();
            if (text.Length == 0) continue;
            headings.Add((p, isTitle ? 0 : rp.OutlineLevel, text));
            if (headings.Count > 2000) break;
        }
        var same = headings.Count == _headings.Count && headings.Select(h => h.Para).SequenceEqual(_headings)
            && _list.Items.OfType<ListBoxItem>().Select(i => (string?)((TextBlock)i.Content).Text).SequenceEqual(headings.Select(h => (string?)h.Text));
        if (!same)
        {
            _list.Items.Clear();
            foreach (var (para, level, text) in headings)
            {
                var t = new TextBlock
                {
                    Text = text,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(6 + Math.Min(level, 4) * 14, 6, 6, 6),
                    FontSize = level == 0 ? 13.5 : 13,
                    FontWeight = level == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                };
                if (level >= 2) t.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
                var item = new ListBoxItem { Content = t, Tag = para, ToolTip = text.Length > 40 ? text : null };
                AutomationProperties.SetName(item, text);
                _list.Items.Add(item);
            }
            _headings = headings.Select(h => h.Para).ToList();
        }
        _empty.Visibility = headings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MarkCurrent();
    }

    /// <summary>Selects the heading the caret is under.</summary>
    private void MarkCurrent()
    {
        if (_headings.Count == 0 || Visibility != Visibility.Visible) return;
        var caret = _editor.Editor.Selection.Active;
        if (caret.Para?.Story is not MainStory) return;
        var index = -1;
        var caretPos = new DocPos(caret.Para, 0);
        for (var i = 0; i < _headings.Count; i++)
        {
            if (_headings[i].Parent == null) continue;
            if (Nav.Compare(new DocPos(_headings[i], 0), caretPos) <= 0) index = i;
            else break;
        }
        if (index >= 0 && index < _list.Items.Count)
        {
            _list.SelectedIndex = index;
            _list.ScrollIntoView(_list.SelectedItem);
        }
        else _list.SelectedIndex = -1;
    }
}
