using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Inkwell.Controls;
using Inkwell.Views.Editing;
using Inkwell.Core.Editing;

namespace Inkwell.Views.Panels;

/// <summary>Find and replace: match case, whole words, regular expressions, in notes, comments, headers and footers too.</summary>
public sealed class FindBar : Border
{
    private readonly EditorView _editor;
    private readonly TextBox _find = new() { Width = 220 };
    private readonly TextBox _replace = new() { Width = 220 };
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0), FontSize = 12, MinWidth = 70 };
    private readonly ToggleButton _case = Toggle("Aa", "Match case");
    private readonly ToggleButton _words = Toggle("ab", "Whole words");
    private readonly ToggleButton _regex = Toggle(".*", "Regular expression");
    private readonly CheckBox _everywhere = new() { Content = "Notes, comments, headers", IsChecked = true, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 };
    private readonly StackPanel _replaceRow;
    private readonly DispatcherTimer _timer;
    private List<FindMatch> _matches = new();
    private int _current = -1;

    public FindBar(EditorView editor)
    {
        _editor = editor;
        Visibility = Visibility.Collapsed;
        Padding = new Thickness(10, 8, 8, 8);
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        SetResourceReference(BackgroundProperty, "Flyout.Background");
        SetResourceReference(BorderBrushProperty, "Flyout.Stroke");
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = 0.18 };
        AutomationProperties.SetName(this, "Find and replace");

        var stack = new StackPanel();
        var findRow = new StackPanel { Orientation = Orientation.Horizontal };
        _find.SetResourceReference(StyleProperty, "SearchBox");
        Ui.SetPlaceholder(_find, "Find");
        AutomationProperties.SetName(_find, "Find");
        findRow.Children.Add(_find);
        findRow.Children.Add(_count);
        findRow.Children.Add(IconButton("Icon.ArrowUp", Shortcuts.Tip("Previous", Shortcuts.Local.FindPrevious), () => Step(-1)));
        findRow.Children.Add(IconButton("Icon.ArrowDown", "Next (Enter)", () => Step(1)));
        findRow.Children.Add(IconButton("Icon.ArrowSwap", Shortcuts.Tip("Replace", Shortcuts.Replace), () => ShowReplace(_replaceRow!.Visibility != Visibility.Visible)));
        findRow.Children.Add(IconButton("Icon.Dismiss", "Close (Esc)", Close));
        stack.Children.Add(findRow);

        var options = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        options.Children.Add(_case);
        options.Children.Add(_words);
        options.Children.Add(_regex);
        options.Children.Add(_everywhere);
        stack.Children.Add(options);

        _replaceRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        Ui.SetPlaceholder(_replace, "Replace with");
        AutomationProperties.SetName(_replace, "Replace with");
        _replaceRow.Children.Add(_replace);
        var one = new Button { Content = "Replace", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3) };
        one.Click += (_, _) => ReplaceOne();
        var all = new Button { Content = "Replace all", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3), ToolTip = $"Replace every match ({Shortcuts.Local.ReplaceAll.Text} in the Replace with box)" };
        all.Click += (_, _) => ReplaceAll();
        _replaceRow.Children.Add(one);
        _replaceRow.Children.Add(all);
        stack.Children.Add(_replaceRow);
        Child = stack;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Search(keepPosition: false);
        };
        _find.TextChanged += (_, _) => Restart();
        foreach (var t in new[] { _case, _words, _regex }) t.Click += (_, _) => Restart();
        _everywhere.Click += (_, _) => Restart();
        _find.PreviewKeyDown += OnKey;
        _replace.PreviewKeyDown += OnKey;
        editor.Editor.Changed += () =>
        {
            if (Visibility == Visibility.Visible) Restart();
        };
    }

    private static ToggleButton Toggle(string text, string tooltip)
    {
        var t = new ToggleButton { Content = text, ToolTip = tooltip, Width = 34, Height = 28, Margin = new Thickness(0, 0, 4, 0), FontSize = 12.5 };
        t.SetResourceReference(StyleProperty, "SubtleToggle");
        AutomationProperties.SetName(t, tooltip);
        return t;
    }

    private Button IconButton(string icon, string tooltip, Action action)
    {
        var b = new Button { ToolTip = tooltip, Width = 30, Height = 30 };
        b.SetResourceReference(StyleProperty, "IconButton");
        if (TryFindResource(icon) is Geometry g) Ui.SetIcon(b, g);
        AutomationProperties.SetName(b, tooltip);
        b.Click += (_, _) => action();
        return b;
    }

    private FindOptions Options => new()
    {
        MatchCase = _case.IsChecked == true,
        WholeWords = _words.IsChecked == true,
        Regex = _regex.IsChecked == true,
        IncludeNotes = _everywhere.IsChecked == true,
        IncludeComments = _everywhere.IsChecked == true,
        IncludeHeadersFooters = _everywhere.IsChecked == true,
    };

    public void Open(bool replace)
    {
        Visibility = Visibility.Visible;
        ShowReplace(replace);
        var sel = _editor.Editor.Selection;
        if (!sel.IsEmpty && sel.InOneParagraph && sel.End.Offset - sel.Start.Offset < 100)
            _find.Text = sel.Start.Para.Text.Substring(sel.Start.Offset, sel.End.Offset - sel.Start.Offset);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _find.Focus();
            _find.SelectAll();
        }), DispatcherPriority.Input);
        Search(keepPosition: false);
    }

    private void ShowReplace(bool on)
    {
        _replaceRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on) Dispatcher.BeginInvoke(new Action(() => (_find.Text.Length > 0 ? _replace : _find).Focus()), DispatcherPriority.Input);
    }

    public void Close()
    {
        Visibility = Visibility.Collapsed;
        _editor.DocumentView.Highlights.Clear();
        _editor.DocumentView.Redraw();
        _editor.FocusDocument();
    }

    private void Restart()
    {
        _timer.Stop();
        _timer.Start();
    }

    private void Search(bool keepPosition)
    {
        var view = _editor.DocumentView;
        view.Highlights.Clear();
        var query = _find.Text;
        if (string.IsNullOrEmpty(query))
        {
            _matches.Clear();
            _current = -1;
            _count.Text = "";
            view.Redraw();
            return;
        }
        FindReplace.Build(query, Options, out var error);
        if (error != null)
        {
            _matches.Clear();
            _count.Text = "Invalid pattern";
            _count.SetResourceReference(TextBlock.ForegroundProperty, "Status.Critical");
            view.Redraw();
            return;
        }
        _count.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        _matches = FindReplace.FindAll(_editor.Session.Document, query, Options);
        var color = Color.FromArgb(0x70, 0xFF, 0xC4, 0x3D);
        foreach (var m in _matches.Take(3000)) view.Highlights.Add(new HighlightRange(new DocPos(m.Para, m.Start), new DocPos(m.Para, m.End), color));
        if (!keepPosition || _current >= _matches.Count)
        {
            // The first match after the caret.
            var caret = _editor.Editor.Selection.Start;
            _current = _matches.FindIndex(m => m.Para.Story == caret.Para.Story && Nav.Compare(new DocPos(m.Para, m.Start), caret) >= 0);
            if (_current < 0 && _matches.Count > 0) _current = 0;
        }
        UpdateCount();
        view.Redraw();
    }

    private void UpdateCount()
    {
        _count.Text = _matches.Count == 0 ? "No results"
            : _current >= 0 ? $"{(_current + 1).ToString(CultureInfo.CurrentCulture)} of {_matches.Count.ToString("N0", CultureInfo.CurrentCulture)}"
            : _matches.Count.ToString("N0", CultureInfo.CurrentCulture) + " found";
    }

    private void Step(int direction)
    {
        if (_timer.IsEnabled)
        {
            _timer.Stop();
            Search(keepPosition: false);
            if (_current >= 0 && direction > 0)
            {
                Select();
                return;
            }
        }
        if (_matches.Count == 0) return;
        _current = (_current + direction + _matches.Count) % _matches.Count;
        Select();
    }

    private void Select()
    {
        if (_current < 0 || _current >= _matches.Count) return;
        var m = _matches[_current];
        if (m.Para.Parent == null)
        {
            Search(keepPosition: true);
            return;
        }
        _editor.GoTo(m.Selection);
        UpdateCount();
        // Keep typing in the find box.
        _find.Focus();
    }

    private void ReplaceOne()
    {
        if (_matches.Count == 0) return;
        if (_current < 0) _current = 0;
        var m = _matches[_current];
        var sel = _editor.Editor.Selection;
        // The first press selects the match; the next one replaces it.
        if (!(sel.Start == new DocPos(m.Para, m.Start) && sel.End == new DocPos(m.Para, m.End)))
        {
            Select();
            return;
        }
        _editor.Run(() => FindReplace.ReplaceOne(_editor.Editor, m, _find.Text, _replace.Text, Options));
        Search(keepPosition: true);
        if (_matches.Count > 0)
        {
            _current = Math.Min(_current, _matches.Count - 1);
            Select();
        }
    }

    private void ReplaceAll()
    {
        var n = 0;
        _editor.Run(() => n = FindReplace.ReplaceAll(_editor.Editor, _find.Text, _replace.Text, Options));
        _editor.Window.ShowMessage(n == 0 ? "Nothing to replace." : $"Replaced {n.ToString("N0", CultureInfo.CurrentCulture)} {(n == 1 ? "time" : "times")}. {Shortcuts.Undo.Text} undoes them all.", 4);
        Search(keepPosition: false);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.Enter when ReferenceEquals(sender, _find):
                Step((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
                e.Handled = true;
                break;
            case Key.Enter when ReferenceEquals(sender, _replace):
                if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) ReplaceAll();
                else ReplaceOne();
                e.Handled = true;
                break;
        }
    }
}
