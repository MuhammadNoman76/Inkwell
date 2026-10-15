using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using Inkwell.Controls;
using Inkwell.Core.Citations;
using Inkwell.Core.Model;

namespace Inkwell.Views.Dialogs;

/// <summary>
/// Sources and citations: the document's sources (kept in the .docx sources part, so Word sees them too), a form
/// to add or edit one with a live preview in the chosen style, citing at the caret and the bibliography.
/// </summary>
public sealed class CitationDialog : FluentWindow
{
    private static readonly (string Type, string Name)[] Types =
    {
        ("Book", "Book"), ("JournalArticle", "Journal article"), ("InternetSite", "Web page"), ("Report", "Report"),
        ("BookSection", "Book chapter"), ("ConferenceProceedings", "Conference paper"), ("ArticleInAPeriodical", "Magazine or newspaper article"), ("Misc", "Other"),
    };

    private readonly EditorView _view;
    private readonly bool _insert;
    private readonly ListBox _list = new() { MinHeight = 300 };
    private readonly ComboBox _style = new() { Width = 230 };
    private readonly ComboBox _type = new();
    private readonly TextBox _authors = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 52 };
    private readonly TextBox _title = new();
    private readonly TextBox _year = new() { Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _container = new();
    private readonly TextBox _publisher = new();
    private readonly TextBox _volume = new() { Width = 70 };
    private readonly TextBox _issue = new() { Width = 70 };
    private readonly TextBox _pages = new() { Width = 100 };
    private readonly TextBox _url = new();
    private readonly TextBox _doi = new();
    private readonly TextBox _citePages = new() { Width = 90 };
    private readonly TextBlock _preview = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
    private readonly TextBlock _containerLabel = new();
    private BibSource? _editing;
    private bool _loading;

    public CitationDialog(EditorView view, bool insert)
    {
        _view = view;
        _insert = insert;
        Title = "Sources and citations";
        Width = 860;
        Height = 640;
        MinWidth = 640;
        MinHeight = 480;
        IsDialog = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new DockPanel();
        var footer = new Border();
        footer.SetResourceReference(StyleProperty, "DialogFooter");
        var buttons = new DockPanel();
        var styleRow = new StackPanel { Orientation = Orientation.Horizontal };
        var styleLabel = new TextBlock { Text = "Style", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        styleLabel.SetResourceReference(StyleProperty, "Text.BodySecondary");
        styleRow.Children.Add(styleLabel);
        foreach (var s in new[] { CitationStyle.Apa, CitationStyle.Mla, CitationStyle.Chicago, CitationStyle.Ieee }) _style.Items.Add(CitationStyles.DisplayName(s));
        _style.SelectedIndex = (int)CitationStyles.FromName(Doc.Sources.StyleName);
        AutomationProperties.SetName(_style, "Citation style");
        _style.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            var style = CitationStyles.ToName((CitationStyle)_style.SelectedIndex);
            _view.Run(() => _view.Editor.SetCitationStyle(style));
            UpdatePreview();
        };
        styleRow.Children.Add(_style);
        buttons.Children.Add(styleRow);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var bibliography = new Button { Content = "Insert bibliography", Margin = new Thickness(8, 0, 0, 0) };
        bibliography.Click += (_, _) =>
        {
            _view.Run(() => _view.Editor.InsertBibliography());
            Close();
        };
        right.Children.Add(bibliography);
        var cite = new Button { Content = "Cite at the caret", Margin = new Thickness(8, 0, 0, 0), IsDefault = insert };
        cite.SetResourceReference(StyleProperty, "AccentButton");
        cite.Click += (_, _) => Cite();
        right.Children.Add(cite);
        var close = new Button { Content = "Close", Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        close.Click += (_, _) => Close();
        right.Children.Add(close);
        buttons.Children.Add(right);
        footer.Child = buttons;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var grid = new Grid { Margin = new Thickness(24, 20, 24, 16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var left = new DockPanel();
        var heading = new TextBlock { Text = "Sources in this document" };
        heading.SetResourceReference(StyleProperty, "Text.BodyStrong");
        DockPanel.SetDock(heading, Dock.Top);
        left.Children.Add(heading);
        var leftButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var add = new Button { Content = "New source" };
        add.Click += (_, _) => Edit(null);
        var delete = new Button { Content = "Delete", Margin = new Thickness(6, 0, 0, 0) };
        delete.Click += (_, _) => DeleteSelected();
        leftButtons.Children.Add(add);
        leftButtons.Children.Add(delete);
        DockPanel.SetDock(leftButtons, Dock.Bottom);
        left.Children.Add(leftButtons);
        _list.Margin = new Thickness(0, 8, 0, 0);
        _list.SelectionMode = SelectionMode.Extended;
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        AutomationProperties.SetName(_list, "Sources");
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItems.Count == 1 && _list.SelectedItem is ListBoxItem { Tag: BibSource s }) Edit(s);
        };
        left.Children.Add(_list);
        grid.Children.Add(left);

        var form = new StackPanel();
        void Field(string label, FrameworkElement control)
        {
            var l = new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 4) };
            l.SetResourceReference(StyleProperty, "Text.BodySecondary");
            form.Children.Add(l);
            AutomationProperties.SetName(control, label);
            form.Children.Add(control);
            if (control is TextBox tb) tb.TextChanged += (_, _) => UpdatePreview();
        }
        foreach (var t in Types) _type.Items.Add(t.Name);
        _type.SelectedIndex = 0;
        _type.SelectionChanged += (_, _) =>
        {
            UpdateLabels();
            UpdatePreview();
        };
        Field("Type", _type);
        Field("Authors (one per line: Last, First)", _authors);
        Ui.SetPlaceholder(_authors, "Lovelace, Ada\nBabbage, Charles");
        Field("Title", _title);
        Field("Year", _year);
        var containerRow = new StackPanel();
        _containerLabel.Margin = new Thickness(0, 8, 0, 4);
        _containerLabel.SetResourceReference(StyleProperty, "Text.BodySecondary");
        form.Children.Add(_containerLabel);
        form.Children.Add(_container);
        _container.TextChanged += (_, _) => UpdatePreview();
        Field("Publisher", _publisher);
        var numbers = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, box) in new[] { ("Volume", _volume), ("Issue", _issue), ("Pages", _pages) })
        {
            var s = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
            var l = new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 4) };
            l.SetResourceReference(StyleProperty, "Text.BodySecondary");
            s.Children.Add(l);
            AutomationProperties.SetName(box, label);
            box.TextChanged += (_, _) => UpdatePreview();
            s.Children.Add(box);
            numbers.Children.Add(s);
        }
        form.Children.Add(numbers);
        Field("Web address", _url);
        Field("DOI", _doi);
        var preview = new Border { Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 14, 0, 0), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
        preview.SetResourceReference(Border.BackgroundProperty, "Card.BackgroundSecondary");
        preview.SetResourceReference(Border.BorderBrushProperty, "Card.Stroke");
        var pstack = new StackPanel();
        var ptitle = new TextBlock { Text = "In the bibliography", FontSize = 11.5, Margin = new Thickness(0, 0, 0, 4) };
        ptitle.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
        pstack.Children.Add(ptitle);
        pstack.Children.Add(_preview);
        preview.Child = pstack;
        form.Children.Add(preview);
        var saveRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var save = new Button { Content = "Save source" };
        save.Click += (_, _) => SaveForm();
        saveRow.Children.Add(save);
        var pagesLabel = new TextBlock { Text = "Cite page(s)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 8, 0) };
        pagesLabel.SetResourceReference(StyleProperty, "Text.BodySecondary");
        saveRow.Children.Add(pagesLabel);
        AutomationProperties.SetName(_citePages, "Pages to cite");
        saveRow.Children.Add(_citePages);
        form.Children.Add(saveRow);
        var scroller = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) };
        Grid.SetColumn(scroller, 2);
        grid.Children.Add(scroller);
        root.Children.Add(grid);
        Content = root;

        FillList(null);
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        UpdateLabels();
        if (Doc.Sources.Items.Count == 0) Edit(null);
        Loaded += (_, _) => (Doc.Sources.Items.Count == 0 ? (Control)_authors : _list).Focus();
    }

    private Document Doc => _view.Session.Document;

    public static void Show(EditorView view, bool insert)
    {
        var dialog = new CitationDialog(view, insert) { Owner = view.Window };
        dialog.ShowDialog();
    }

    private void FillList(string? select)
    {
        _list.Items.Clear();
        foreach (var s in Doc.Sources.Items.OrderBy(s => s.Authors.FirstOrDefault()?.Last ?? s.CorporateAuthor ?? s.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            var stack = new StackPanel { Margin = new Thickness(2, 4, 2, 4) };
            var who = s.Authors.Count > 0 ? string.Join(", ", s.Authors.Take(2).Select(a => a.Last)) + (s.Authors.Count > 2 ? " et al." : "") : s.CorporateAuthor ?? "No author";
            stack.Children.Add(new TextBlock { Text = who + (s.Year != null ? " (" + s.Year + ")" : ""), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            var title = new TextBlock { Text = s.Title ?? "Untitled", FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
            stack.Children.Add(title);
            var item = new ListBoxItem { Content = stack, Tag = s };
            AutomationProperties.SetName(item, who + ", " + s.Title);
            _list.Items.Add(item);
            if (s.Tag == select) _list.SelectedItem = item;
        }
    }

    private void UpdateLabels()
    {
        var type = Types[Math.Max(0, _type.SelectedIndex)].Type;
        _containerLabel.Text = type switch
        {
            "JournalArticle" => "Journal",
            "InternetSite" => "Website name",
            "BookSection" => "Book title",
            "ConferenceProceedings" => "Conference",
            "ArticleInAPeriodical" => "Magazine or newspaper",
            "Report" => "Institution",
            _ => "Series or collection (optional)",
        };
        AutomationProperties.SetName(_container, _containerLabel.Text);
    }

    private void Edit(BibSource? source)
    {
        _loading = true;
        _editing = source;
        _type.SelectedIndex = Math.Max(0, Array.FindIndex(Types, t => t.Type == (source?.Type ?? "Book")));
        _authors.Text = source == null ? "" : string.Join("\n", source.Authors.Select(a => a.Last + (a.First.Length > 0 ? ", " + a.First + (a.Middle.Length > 0 ? " " + a.Middle : "") : "")));
        if (source?.CorporateAuthor is { } corp && source.Authors.Count == 0) _authors.Text = corp;
        _title.Text = source?.Title ?? "";
        _year.Text = source?.Year ?? "";
        _container.Text = source == null ? "" : source.JournalName ?? source.InternetSiteTitle ?? source.BookTitle ?? source.ConferenceName ?? source.PeriodicalTitle ?? source.Institution ?? "";
        _publisher.Text = source?.Publisher ?? "";
        _volume.Text = source?.Volume ?? "";
        _issue.Text = source?.Issue ?? "";
        _pages.Text = source?.Pages ?? "";
        _url.Text = source?.Url ?? "";
        _doi.Text = source?.Doi ?? "";
        _loading = false;
        UpdateLabels();
        UpdatePreview();
    }

    private BibSource FromForm()
    {
        var type = Types[Math.Max(0, _type.SelectedIndex)].Type;
        var s = _editing?.Clone() ?? new BibSource { Guid = "{" + System.Guid.NewGuid().ToString().ToUpperInvariant() + "}" };
        s.Type = type;
        s.Authors = new List<SourcePerson>();
        s.CorporateAuthor = null;
        var lines = _authors.Text.Replace("\r", "").Split(new[] { '\n', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 1 && !lines[0].Contains(',') && lines[0].Split(' ').Length > 3) s.CorporateAuthor = lines[0];
        else foreach (var line in lines) s.Authors.Add(SourcePerson.Parse(line));
        static string? N(string t) => string.IsNullOrWhiteSpace(t) ? null : t.Trim();
        s.Title = N(_title.Text);
        s.Year = N(_year.Text);
        s.JournalName = s.InternetSiteTitle = s.BookTitle = s.ConferenceName = s.PeriodicalTitle = s.Institution = null;
        var container = N(_container.Text);
        switch (type)
        {
            case "JournalArticle": s.JournalName = container; break;
            case "InternetSite": s.InternetSiteTitle = container; break;
            case "BookSection": s.BookTitle = container; break;
            case "ConferenceProceedings": s.ConferenceName = container; break;
            case "ArticleInAPeriodical": s.PeriodicalTitle = container; break;
            case "Report": s.Institution = container; break;
        }
        s.Publisher = N(_publisher.Text);
        s.Volume = N(_volume.Text);
        s.Issue = N(_issue.Text);
        s.Pages = N(_pages.Text);
        s.Url = N(_url.Text);
        s.Doi = N(_doi.Text);
        if (string.IsNullOrEmpty(s.Tag)) s.Tag = NewTag(s);
        return s;
    }

    /// <summary>A tag like Word's: first author's surname (first three letters) and the year's last two digits, unique.</summary>
    private string NewTag(BibSource s)
    {
        var name = s.Authors.FirstOrDefault()?.Last ?? s.CorporateAuthor ?? s.Title ?? "Src";
        var letters = new string(name.Where(char.IsLetter).Take(3).ToArray());
        if (letters.Length == 0) letters = "Src";
        var year = s.Year is { Length: >= 2 } y ? y.Substring(y.Length - 2) : "";
        var tag = letters + year;
        var n = 1;
        var candidate = tag;
        while (Doc.Sources.Find(candidate) != null) candidate = tag + (++n).ToString(CultureInfo.InvariantCulture);
        return candidate;
    }

    private void UpdatePreview()
    {
        if (_loading) return;
        try
        {
            var source = FromForm();
            var style = (CitationStyle)Math.Max(0, _style.SelectedIndex);
            var engine = new CitationEngine(style, new[] { source }, new[] { source.Tag });
            var entry = engine.Bibliography().FirstOrDefault();
            _preview.Inlines.Clear();
            if (entry == null)
            {
                _preview.Text = "Fill in the author, title and year.";
                return;
            }
            if (entry.Label != null) _preview.Inlines.Add(new Run(entry.Label + " "));
            foreach (var span in entry.Text)
            {
                var run = new Run(span.Text);
                if (span.Italic) run.FontStyle = FontStyles.Italic;
                if (span.Bold) run.FontWeight = FontWeights.SemiBold;
                _preview.Inlines.Add(run);
            }
            var cite = engine.Cite(source.Tag, string.IsNullOrWhiteSpace(_citePages.Text) ? null : _citePages.Text.Trim()).PlainText;
            _preview.Inlines.Add(new LineBreak());
            _preview.Inlines.Add(new Run("In the text: " + cite) { FontSize = 12 });
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex, "Citation preview");
        }
    }

    private bool SaveForm()
    {
        if (_authors.Text.Trim().Length == 0 && _title.Text.Trim().Length == 0)
        {
            _view.Window.ShowMessage("Give the source at least an author or a title.", 4, warning: true);
            _authors.Focus();
            return false;
        }
        var source = FromForm();
        var oldTag = _editing?.Tag;
        _view.Run(() => _view.Editor.SaveSource(source, oldTag));
        _editing = Doc.Sources.Find(source.Tag);
        FillList(source.Tag);
        return true;
    }

    private void DeleteSelected()
    {
        var tags = _list.SelectedItems.OfType<ListBoxItem>().Select(i => ((BibSource)i.Tag).Tag).ToList();
        foreach (var tag in tags) _view.Run(() => _view.Editor.DeleteSource(tag));
        FillList(null);
        Edit(null);
    }

    private void Cite()
    {
        // Unsaved changes in the form are saved first.
        if (_editing == null || _list.SelectedItems.Count <= 1)
        {
            if (!SaveForm()) return;
        }
        var selected = _list.SelectedItems.OfType<ListBoxItem>().Select(i => (BibSource)i.Tag).ToList();
        if (selected.Count == 0 && _editing != null) selected.Add(_editing);
        if (selected.Count == 0) return;
        var pages = string.IsNullOrWhiteSpace(_citePages.Text) ? null : _citePages.Text.Trim();
        var refs = selected.Select(s => new CitationRef(s.Tag, selected.Count == 1 ? pages : null)).ToList();
        _view.Run(() => _view.Editor.InsertCitation(refs));
        Close();
    }
}
