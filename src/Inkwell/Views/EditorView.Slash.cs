using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Inkwell.Controls;
using Inkwell.Core.Editing;
using Inkwell.Core.Model;

namespace Inkwell.Views;

/// <summary>Slash commands: "/" at the start of a word opens a menu of things to insert; typing filters it.</summary>
public partial class EditorView
{
    private sealed class SlashItem
    {
        public SlashItem(string title, string keywords, string icon, string description, Action run)
        {
            Title = title;
            Keywords = keywords;
            Icon = icon;
            Description = description;
            Run = run;
        }

        public string Title { get; }
        public string Keywords { get; }
        public string Icon { get; }
        public string Description { get; }
        public Action Run { get; }
    }

    private Border? _slashMenu;
    private ListBox? _slashList;
    private Paragraph? _slashPara;
    private int _slashStart;
    private List<SlashItem>? _slashItems;

    public bool SlashMenuOpen => _slashMenu != null;

    private List<SlashItem> SlashItems() => _slashItems ??= new List<SlashItem>
    {
        new("Heading 1", "h1 title heading", "Icon.TextHeader1", "Big section heading", () => Editor.ApplyStyle("Heading1")),
        new("Heading 2", "h2 heading subheading", "Icon.TextHeader2", "Medium heading", () => Editor.ApplyStyle("Heading2")),
        new("Heading 3", "h3 heading", "Icon.TextHeader3", "Small heading", () => Editor.ApplyStyle("Heading3")),
        new("Table", "table grid rows columns", "Icon.Table", "3 × 3 table with a header row", () => InsertTableDefault()),
        new("Picture", "image photo picture graphic img", "Icon.ImageAdd", "Insert a picture from a file", () => InsertPicture()),
        new("Bulleted list", "bullets list ul", "Icon.TextBulletListLtr", "A simple bulleted list", () => Editor.ToggleList(true)),
        new("Numbered list", "numbers list ol", "Icon.TextNumberListLtr", "A list with numbers", () => Editor.ToggleList(false)),
        new("Table of contents", "toc contents", "Icon.TableOfContents", "Headings with page numbers", () => Editor.InsertTableOfContents()),
        new("Footnote", "footnote note reference", "Icon.TextFootnote", "A note at the bottom of the page", () => InsertNote(NoteKind.Footnote)),
        new("Endnote", "endnote note", "Icon.TextFootnote", "A note at the end of the document", () => InsertNote(NoteKind.Endnote)),
        new("Page break", "page break new", "Icon.DocumentPageBreak", "Start a new page", () => Editor.InsertBreak(TextRun.PageBreak)),
        new("Quote", "quote blockquote citation", "Icon.TextQuote", "A quotation", () => Editor.ApplyStyle("Quote")),
        new("Code", "code block monospace", "Icon.Code", "A block of code", () => Editor.ApplyStyle("Code")),
        new("Divider", "divider rule line hr separator", "Icon.LineHorizontal1", "A horizontal line", () => Editor.InsertDivider()),
        new("Date", "date today time", "Icon.Calendar", DateTime.Now.ToString("d MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture),
            () => Editor.InsertText(DateTime.Now.ToString("d MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture))),
        new("Link", "link url hyperlink web", "Icon.Link", "Link to a web page or a heading", () => InsertLink()),
        new("Comment", "comment note review", "Icon.CommentAdd", "Add a comment here", () => AddComment()),
        new("Caption", "caption figure label", "Icon.TextCaption", "Figure 1, Table 1…", () => InsertCaption()),
        new("Citation", "cite citation reference source", "Icon.BookQuestionMark", "Cite a source", () => ShowCitations(true)),
        new("Bibliography", "bibliography references works cited", "Icon.Library", "List the sources you cited", () => Editor.InsertBibliography()),
        new("Section break", "section break", "Icon.DocumentPageBreak", "A new section on a new page", () => Editor.InsertSectionBreak("nextPage")),
        new("Symbol", "symbol character emoji special", "Icon.Symbols", "Insert a special character", () => InsertSymbol()),
    };

    private void OpenSlashMenu(Paragraph p, int slashOffset)
    {
        CloseSlashMenu();
        _slashPara = p;
        _slashStart = slashOffset;
        var menu = new Border { Width = 300, MaxHeight = 330 };
        menu.SetResourceReference(StyleProperty, "FlyoutSurface");
        AutomationProperties.SetName(menu, "Insert menu");
        var dock = new DockPanel();
        var hint = new TextBlock { Text = "Type to filter · ↑↓ · Enter", FontSize = 11, Margin = new Thickness(12, 4, 12, 7) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
        DockPanel.SetDock(hint, Dock.Bottom);
        dock.Children.Add(hint);
        var list = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0, 4, 0, 2), Focusable = false };
        list.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "PaletteItem");
        list.SetResourceReference(Control.ForegroundProperty, "Text.Primary");
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        AutomationProperties.SetName(list, "Things to insert");
        list.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(list, d) is ListBoxItem item)
            {
                list.SelectedItem = item;
                RunSlashSelection();
                e.Handled = true;
            }
        };
        dock.Children.Add(list);
        menu.Child = dock;
        _slashMenu = menu;
        _slashList = list;
        ChipLayer.Children.Add(menu);
        UpdateSlashMenu();
    }

    private string SlashQuery()
    {
        var p = _slashPara;
        if (p == null || p.Parent == null) return "";
        var caret = Editor.Selection.Active;
        if (!ReferenceEquals(caret.Para, p) || caret.Offset <= _slashStart || _slashStart >= p.Length) return "\0";
        var text = p.Text;
        if (text[_slashStart] != '/') return "\0";
        return text.Substring(_slashStart + 1, caret.Offset - _slashStart - 1);
    }

    private List<SlashItem> SlashMatches(string query)
    {
        var words = query.Trim().ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return SlashItems();
        return SlashItems()
            .Select(i => (Item: i, Score: words.All(w => i.Title.ToLowerInvariant().Contains(w) || i.Keywords.Contains(w)) ? (i.Title.StartsWith(words[0], StringComparison.OrdinalIgnoreCase) ? 2 : 1) : 0))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Item)
            .ToList();
    }

    private bool SlashHasMatches() => SlashMatches(SlashQuery()).Count > 0;

    private void UpdateSlashMenu()
    {
        if (_slashMenu == null || _slashList == null) return;
        var query = SlashQuery();
        // The "/" was deleted, the caret left, or the query got long without matches: close.
        if (query.Contains('\0') || query.Length > 24)
        {
            CloseSlashMenu();
            return;
        }
        var matches = SlashMatches(query);
        if (matches.Count == 0 && query.EndsWith(" ", StringComparison.Ordinal))
        {
            CloseSlashMenu();
            return;
        }
        _slashList.Items.Clear();
        foreach (var item in matches) _slashList.Items.Add(SlashRow(item));
        if (_slashList.Items.Count > 0) _slashList.SelectedIndex = 0;
        _slashMenu.Visibility = matches.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        PositionSlashMenu();
    }

    private ListBoxItem SlashRow(SlashItem item)
    {
        var dock = new DockPanel { Height = 42 };
        var icon = new IconView { Width = 16, Height = 16, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        if (TryFindResource(item.Icon) is Geometry g) icon.Data = g;
        icon.SetResourceReference(IconView.ForegroundProperty, "Text.Secondary");
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = item.Title, FontSize = 13.5 });
        var desc = new TextBlock { Text = item.Description, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
        desc.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
        text.Children.Add(desc);
        dock.Children.Add(text);
        var row = new ListBoxItem { Content = dock, Tag = item };
        AutomationProperties.SetName(row, item.Title + ", " + item.Description);
        return row;
    }

    private void PositionSlashMenu()
    {
        if (_slashMenu == null || _slashPara == null) return;
        var rect = Doc.ViewRectOf(new DocPos(_slashPara, Math.Min(_slashStart, _slashPara.Length)));
        if (rect == null)
        {
            _slashMenu.Visibility = Visibility.Collapsed;
            return;
        }
        var r = rect.Value;
        var origin = Doc.TranslatePoint(new Point(r.Left, r.Bottom + 4), ChipLayer);
        _slashMenu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var h = Math.Min(_slashMenu.DesiredSize.Height, 330);
        var y = origin.Y;
        if (y + h > ChipLayer.ActualHeight - 36) y = Math.Max(4, Doc.TranslatePoint(new Point(r.Left, r.Top - 4), ChipLayer).Y - h);
        Canvas.SetLeft(_slashMenu, MathEx.Clamp(origin.X - 8, 8, Math.Max(8, ChipLayer.ActualWidth - 310)));
        Canvas.SetTop(_slashMenu, y);
    }

    private void CloseSlashMenu()
    {
        if (_slashMenu == null) return;
        ChipLayer.Children.Remove(_slashMenu);
        _slashMenu = null;
        _slashList = null;
        _slashPara = null;
    }

    /// <summary>Keys while the slash menu is open: arrows choose, Enter / Tab run, Esc closes. Returns true when handled.</summary>
    private bool HandleSlashKey(Key key)
    {
        if (_slashList == null) return false;
        switch (key)
        {
            case Key.Down:
            case Key.Up:
                if (_slashList.Items.Count == 0) return false;
                var n = _slashList.Items.Count;
                _slashList.SelectedIndex = (_slashList.SelectedIndex + (key == Key.Down ? 1 : -1) + n) % n;
                _slashList.ScrollIntoView(_slashList.SelectedItem);
                return true;
            case Key.Enter:
            case Key.Tab:
                if (_slashList.Items.Count == 0)
                {
                    CloseSlashMenu();
                    return false;
                }
                RunSlashSelection();
                return true;
            case Key.Escape:
                CloseSlashMenu();
                return true;
            case Key.Left:
            case Key.Right:
            case Key.Home:
            case Key.End:
            case Key.PageUp:
            case Key.PageDown:
                CloseSlashMenu();
                return false;
            case Key.Back:
            case Key.Delete:
                Dispatcher.BeginInvoke(new Action(UpdateSlashMenu), System.Windows.Threading.DispatcherPriority.Input);
                return false;
        }
        return false;
    }

    private void RunSlashSelection()
    {
        if (_slashList?.SelectedItem is not ListBoxItem { Tag: SlashItem item } || _slashPara == null)
        {
            CloseSlashMenu();
            return;
        }
        var p = _slashPara;
        var start = _slashStart;
        var end = Editor.Selection.Active.Offset;
        CloseSlashMenu();
        Run(() =>
        {
            Editor.RemoveTyped(p, start, end);
            item.Run();
        });
        FocusDocument();
    }
}
