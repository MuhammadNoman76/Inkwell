using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Inkwell.Controls;
using Inkwell.Core.Editing;
using Inkwell.Core.Model;
using Inkwell.Core.Templates;
using Inkwell.Core.Styles;

namespace Inkwell.Views.Panels;

/// <summary>What the right-hand panel shows.</summary>
public enum PanelMode
{
    /// <summary>Follows the selection: picture, table or text.</summary>
    Auto,
    Format,
    Picture,
    Table,
    Page,
    Review,
    Reveal,
    Design,
}

/// <summary>
/// The contextual panel on the right: formatting for the selection (text, picture or table), page setup with
/// headers and footers, review (changes and comments), "Reveal formatting" with the style consistency check,
/// and designs.
/// </summary>
public sealed partial class ContextPanel : Border
{
    private readonly EditorView _editor;
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal };
    private readonly ScrollViewer _scroller = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
    private StackPanel _content = new();
    private PanelMode _mode = PanelMode.Auto;
    private PanelMode _shown = (PanelMode)(-1);
    private object? _shownTarget;
    private int _shownVersion = -1;
    private bool _suspend;

    public ContextPanel(EditorView editor)
    {
        _editor = editor;
        Padding = new Thickness(0, 8, 0, 0);
        AutomationProperties.SetName(this, "Panel");
        var dock = new DockPanel();
        var tray = new Border { Margin = new Thickness(14, 2, 14, 10), HorizontalAlignment = HorizontalAlignment.Left };
        tray.SetResourceReference(StyleProperty, "SegmentTray");
        tray.Child = _tabs;
        DockPanel.SetDock(tray, Dock.Top);
        dock.Children.Add(tray);
        dock.Children.Add(_scroller);
        Child = dock;
        IsKeyboardFocusWithinChanged += (_, e) =>
        {
            if (!(bool)e.NewValue) Dispatcher.BeginInvoke(new Action(Refresh), System.Windows.Threading.DispatcherPriority.Background);
        };
    }

    private DocEditor Editor => _editor.Editor;
    private Document Doc => _editor.Session.Document;

    public PanelMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            _shown = (PanelMode)(-1);
            Refresh();
        }
    }

    /// <summary>The mode actually shown (Auto resolved).</summary>
    public PanelMode Effective
    {
        get
        {
            if (_mode != PanelMode.Auto)
            {
                if (_mode == PanelMode.Picture && _editor.DocumentView.SelectedDrawing == null) return PanelMode.Format;
                if (_mode == PanelMode.Table && Nav.CellOf(Editor.Selection.Active.Para) == null) return PanelMode.Format;
                return _mode;
            }
            if (_editor.DocumentView.SelectedDrawing != null) return PanelMode.Picture;
            if (_editor.DocumentView.EditingHeaderFooter) return PanelMode.Page;
            if (Nav.CellOf(Editor.Selection.Active.Para) != null) return PanelMode.Table;
            return PanelMode.Format;
        }
    }

    /// <summary>Rebuilds the panel for the selection (not while the user is typing in it).</summary>
    public void Refresh()
    {
        if (_suspend || Visibility != Visibility.Visible || !IsLoaded && _shown != (PanelMode)(-1)) return;
        var mode = Effective;
        object? target = mode switch
        {
            PanelMode.Picture => _editor.DocumentView.SelectedDrawing,
            PanelMode.Table => Nav.CellOf(Editor.Selection.Active.Para)?.Row?.Table,
            _ => Editor.Selection.Active.Para,
        };
        var version = Editor.History.Version;
        if (IsKeyboardFocusWithin && mode == _shown) return;
        if (mode == _shown && ReferenceEquals(target, _shownTarget) && version == _shownVersion && mode is PanelMode.Design or PanelMode.Review) return;
        _shown = mode;
        _shownTarget = target;
        _shownVersion = version;
        BuildTabs(mode);
        var offset = _scroller.VerticalOffset;
        _content = new StackPanel { Margin = new Thickness(14, 0, 14, 24) };
        try
        {
            switch (mode)
            {
                case PanelMode.Picture:
                    BuildPicture();
                    break;
                case PanelMode.Table:
                    BuildTable();
                    break;
                case PanelMode.Page:
                    BuildPage();
                    break;
                case PanelMode.Review:
                    BuildReview();
                    break;
                case PanelMode.Reveal:
                    BuildReveal();
                    break;
                case PanelMode.Design:
                    BuildDesign();
                    break;
                default:
                    BuildFormat();
                    break;
            }
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex, "Panel " + mode);
        }
        _scroller.Content = _content;
        _scroller.ScrollToVerticalOffset(offset);
    }

    private void BuildTabs(PanelMode current)
    {
        _tabs.Children.Clear();
        void Tab(string text, PanelMode mode)
        {
            var r = new RadioButton { Content = text, GroupName = "PanelTabs" + GetHashCode(), IsChecked = current == mode, FontSize = 12.5, Padding = new Thickness(9, 3, 9, 4) };
            r.SetResourceReference(StyleProperty, "TextSegment");
            AutomationProperties.SetName(r, text + " panel");
            r.Checked += (_, _) =>
            {
                if (_mode == mode || (current == mode && _mode == PanelMode.Auto)) return;
                Mode = mode is PanelMode.Format or PanelMode.Picture or PanelMode.Table ? PanelMode.Auto : mode;
                _editor.ScheduleState();
            };
            _tabs.Children.Add(r);
        }
        var contextual = current switch
        {
            PanelMode.Picture => ("Picture", PanelMode.Picture),
            PanelMode.Table => ("Table", PanelMode.Table),
            _ => ("Format", PanelMode.Format),
        };
        Tab(contextual.Item1, contextual.Item2);
        Tab("Page", PanelMode.Page);
        Tab("Review", PanelMode.Review);
        Tab("Reveal", PanelMode.Reveal);
        Tab("Design", PanelMode.Design);
    }

    // ------------------------------------------------------------------ building blocks

    private StackPanel Section(string title, string? hint = null)
    {
        var header = new TextBlock { Text = title, Margin = new Thickness(0, _content.Children.Count == 0 ? 4 : 20, 0, hint == null ? 8 : 2) };
        header.SetResourceReference(StyleProperty, "PanelHeader");
        _content.Children.Add(header);
        if (hint != null)
        {
            var h = new TextBlock { Text = hint, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            h.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
            _content.Children.Add(h);
        }
        var stack = new StackPanel();
        _content.Children.Add(stack);
        return stack;
    }

    private static FrameworkElement Row(string label, UIElement control, double labelWidth = 104)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        var l = new TextBlock { Text = label, Width = labelWidth };
        l.SetResourceReference(StyleProperty, "PanelLabel");
        dock.Children.Add(l);
        dock.Children.Add(control);
        if (control is FrameworkElement fe) AutomationProperties.SetName(fe, label);
        return dock;
    }

    private Button Command(string text, string? icon, Action action, string? tooltip = null)
    {
        var b = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 1, 0, 1), ToolTip = tooltip };
        b.SetResourceReference(StyleProperty, "CommandButton");
        if (icon != null && TryFindResource(icon) is Geometry g) Ui.SetIcon(b, g);
        AutomationProperties.SetName(b, text);
        b.Click += (_, _) =>
        {
            _editor.Run(action);
            _editor.ScheduleState();
        };
        return b;
    }

    private UniformGrid Commands(int columns, params Button[] buttons)
    {
        var grid = new UniformGrid { Columns = columns };
        foreach (var b in buttons) grid.Children.Add(b);
        return grid;
    }

    /// <summary>A labelled on / off switch (the label on the left, the switch on the right).</summary>
    private FrameworkElement Switch(string text, bool on, Action<bool> changed, string? tooltip = null)
    {
        var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1), ToolTip = tooltip };
        var c = new CheckBox { IsChecked = on };
        c.SetResourceReference(StyleProperty, "ToggleSwitch");
        AutomationProperties.SetName(c, text);
        DockPanel.SetDock(c, Dock.Right);
        row.Children.Add(c);
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 8, 0) };
        row.Children.Add(label);
        c.Click += (_, _) =>
        {
            var value = c.IsChecked == true;
            _editor.Run(() => changed(value));
            _editor.ScheduleState();
        };
        return row;
    }

    private Border Segments(IEnumerable<(string? Text, string? Icon, string Name, bool On, Action Click)> items)
    {
        var tray = new Border { HorizontalAlignment = HorizontalAlignment.Left };
        tray.SetResourceReference(StyleProperty, "SegmentTray");
        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        var group = "seg" + Guid.NewGuid().ToString("N");
        foreach (var (text, icon, name, on, click) in items)
        {
            var r = new RadioButton { GroupName = group, IsChecked = on, ToolTip = name };
            if (icon != null)
            {
                r.SetResourceReference(StyleProperty, "IconSegment");
                if (TryFindResource(icon) is Geometry g) Ui.SetIcon(r, g);
            }
            else
            {
                r.Content = text;
                r.SetResourceReference(StyleProperty, "TextSegment");
            }
            AutomationProperties.SetName(r, name);
            r.Click += (_, _) =>
            {
                _editor.Run(click);
                _editor.ScheduleState();
                _editor.FocusDocument();
            };
            stack.Children.Add(r);
        }
        tray.Child = stack;
        return tray;
    }

    private NumberBox Number(double value, double min, double max, double step, int decimals, string suffix, Action<double> changed)
    {
        var box = new NumberBox { Minimum = min, Maximum = max, Step = step, Decimals = decimals, Suffix = suffix, Width = 96, HorizontalAlignment = HorizontalAlignment.Left };
        box.Value = value;
        box.ValueChanged += v =>
        {
            _editor.Run(() => changed(v));
            _editor.ScheduleState();
        };
        return box;
    }

    private TextBlock Note(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
        return t;
    }

    // ------------------------------------------------------------------ units

    private static bool Metric => RegionInfo.CurrentRegion.IsMetric;
    private static string UnitSuffix => Metric ? " cm" : "\"";
    private static double ToUnit(int twips) => Metric ? twips / 1440.0 * 2.54 : twips / 1440.0;
    private static int FromUnit(double value) => (int)Math.Round(Metric ? value / 2.54 * 1440 : value * 1440);

    // ------------------------------------------------------------------ text and paragraph

    private void BuildFormat()
    {
        var f = Editor.GetFormatState();
        var p = Editor.Selection.Active.Para;
        var rp = Editor.Styles.ResolvePara(p);

        var style = Section("Style");
        var styleName = f.ParagraphStyle != null && Doc.Styles.Get(f.ParagraphStyle) is { } s ? DocEditor.DisplayName(s) : "Mixed";
        var styleButton = new Button { Content = styleName + "  ▾", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 5, 10, 6) };
        styleButton.Click += (_, _) => _editor.ShowStyleMenu(styleButton);
        AutomationProperties.SetName(styleButton, "Paragraph style: " + styleName);
        style.Children.Add(styleButton);
        style.Children.Add(Commands(1,
            Command("Update style", "Icon.TextEditStyle", Editor.UpdateStyleFromSelection, "Make the style look like the selection (every paragraph in this style changes)"),
            Command("Clear formatting", "Icon.TextClearFormatting", Editor.ClearFormatting, "Back to the style's look")));

        // In a list, the list options come first (restart / continue numbering is what people look for).
        var kind = Editor.ListKindOf(p);
        if (kind != null) BuildListSection(kind);

        var text = Section("Text");
        var font = new Button { Content = (f.Font ?? "Mixed fonts") + "  ▾", HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 5, 10, 6) };
        font.Click += (_, _) => _editor.ShowFontMenu(font);
        text.Children.Add(Row("Font", font, 64));
        var size = Number(f.Size ?? 11, 1, 1638, 1, 1, " pt", v => Editor.SetFontSize(v));
        if (f.Size == null) size.SetMixed();
        text.Children.Add(Row("Size", size, 64));
        text.Children.Add(Segments(new (string?, string?, string, bool, Action)[]
        {
            (null, "Icon.TextBold", "Bold", f.Bold == true, Editor.ToggleBold),
            (null, "Icon.TextItalic", "Italic", f.Italic == true, Editor.ToggleItalic),
            (null, "Icon.TextUnderline", "Underline", f.Underline == true, Editor.ToggleUnderline),
            (null, "Icon.TextStrikethrough", "Strikethrough", f.Strike == true, Editor.ToggleStrike),
            (null, "Icon.TextSuperscript", "Superscript", f.Superscript == true, Editor.ToggleSuperscript),
            (null, "Icon.TextSubscript", "Subscript", f.Subscript == true, Editor.ToggleSubscript),
        }));
        // Toggles behave as toggles, not as a radio group: rebuild after each click.
        var color = new Button { Content = "Colour and highlight  ▾", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(10, 4, 10, 5) };
        color.Click += (_, _) => _editor.ShowColorMenu(color);
        text.Children.Add(color);

        var para = Section("Paragraph");
        para.Children.Add(Row("Alignment", Segments(new (string?, string?, string, bool, Action)[]
        {
            (null, "Icon.TextAlignLeft", "Align left", f.Alignment is null or "left" or "start", () => Editor.SetAlignment("left")),
            (null, "Icon.TextAlignCenter", "Centre", f.Alignment == "center", () => Editor.SetAlignment("center")),
            (null, "Icon.TextAlignRight", "Align right", f.Alignment is "right" or "end", () => Editor.SetAlignment("right")),
            (null, "Icon.TextAlignJustify", "Justify", f.Alignment is "both" or "distribute", () => Editor.SetAlignment("both")),
        })));
        para.Children.Add(Row("Direction", Segments(new (string?, string?, string, bool, Action)[]
        {
            (null, "Icon.TextParagraphDirectionRight", "Left to right", !rp.Bidi, () => Editor.SetDirection(false)),
            (null, "Icon.TextParagraphDirectionLeft", "Right to left (Urdu, Arabic, Hebrew)", rp.Bidi, () => Editor.SetDirection(true)),
        })));
        var spacing = new ComboBox { Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        var lines = new[] { (240, "1.0"), (276, "1.15"), (360, "1.5"), (480, "2.0") };
        foreach (var (_, label) in lines) spacing.Items.Add(label);
        var currentLine = Array.FindIndex(lines, l => l.Item1 == rp.Line && rp.LineRule == "auto");
        if (currentLine < 0) spacing.Items.Add(rp.LineRule == "auto" ? (rp.Line / 240.0).ToString("0.##", CultureInfo.CurrentCulture) : (rp.Line / 20.0).ToString("0.#", CultureInfo.CurrentCulture) + " pt");
        spacing.SelectedIndex = currentLine >= 0 ? currentLine : spacing.Items.Count - 1;
        spacing.SelectionChanged += (_, _) =>
        {
            if (spacing.SelectedIndex >= 0 && spacing.SelectedIndex < lines.Length) _editor.Run(() => Editor.SetLineSpacing(lines[spacing.SelectedIndex].Item1));
        };
        para.Children.Add(Row("Line spacing", spacing));
        para.Children.Add(Row("Space before", Number(rp.SpacingBefore / 20.0, 0, 1584, 6, 0, " pt", v => Editor.SetSpacing((int)Math.Round(v * 20), null))));
        para.Children.Add(Row("Space after", Number(rp.SpacingAfter / 20.0, 0, 1584, 6, 0, " pt", v => Editor.SetSpacing(null, (int)Math.Round(v * 20)))));
        para.Children.Add(Row("Indent start", Number(ToUnit(rp.IndentStart), -10, 50, Metric ? 0.25 : 0.1, 2, UnitSuffix, v => Editor.SetIndents(FromUnit(v), null, null))));
        para.Children.Add(Row("Indent end", Number(ToUnit(rp.IndentEnd), -10, 50, Metric ? 0.25 : 0.1, 2, UnitSuffix, v => Editor.SetIndents(null, FromUnit(v), null))));
        para.Children.Add(Row("First line", Number(ToUnit(rp.FirstLine), -20, 20, Metric ? 0.25 : 0.1, 2, UnitSuffix, v => Editor.SetIndents(null, null, FromUnit(v)))));
        para.Children.Add(Note("A negative first line is a hanging indent."));

        if (kind == null) BuildListSection(null);

        var flow = Section("Pages", "How the paragraph breaks across pages.");
        flow.Children.Add(Switch("Keep with next", rp.KeepNext, on => Editor.SetKeepWithNext(on), "Never alone at the bottom of a page"));
        flow.Children.Add(Switch("Keep lines together", rp.KeepLines, on => Editor.SetKeepLinesTogether(on)));
        flow.Children.Add(Switch("Start on a new page", rp.PageBreakBefore, on => Editor.SetPageBreakBefore(on)));
        flow.Children.Add(Switch("No single lines at a page edge", rp.WidowControl, on => Editor.SetWidowControl(on), "Widow and orphan control"));
    }

    private void AskStartNumber()
    {
        var answer = InputDialog.Ask(_editor.Window, "Start numbering at", "The number this item gets (the rest of the list follows on).", "1", "Set");
        if (answer != null && int.TryParse(answer.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var n) && n >= 0 && n < 32768)
            _editor.Run(() => Editor.RestartNumbering(n));
    }

    // ------------------------------------------------------------------ designs

    private void BuildDesign()
    {
        var designs = Section("Design", "A design sets the fonts, colours and heading styles of the whole document at once.");
        var current = Doc.Theme.Name;
        foreach (var d in DocumentDesigns.All)
        {
            var design = d;
            var card = new Button { HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(14, 10, 14, 12) };
            card.SetResourceReference(StyleProperty, "CardButton");
            card.SetResourceReference(BackgroundProperty, "Card.Background");
            card.SetResourceReference(BorderBrushProperty, "Card.Stroke");
            card.BorderThickness = new Thickness(1);
            var stack = new StackPanel();
            var heading = new TextBlock { Text = design.Name, FontFamily = Core.Rendering.FontCatalog.Get(design.HeadingFont), FontSize = 19, FontWeight = FontWeights.SemiBold };
            if (!Themes.ThemeManager.IsDark && OoxColor.Parse(design.HeadingColor) is { } accent) heading.Foreground = new SolidColorBrush(accent);
            stack.Children.Add(heading);
            var body = new TextBlock { Text = design.Description, FontFamily = Core.Rendering.FontCatalog.Get(design.BodyFont), FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            body.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
            stack.Children.Add(body);
            var fonts = new TextBlock { Text = design.HeadingFont + " · " + design.BodyFont, FontSize = 11.5, Margin = new Thickness(0, 6, 0, 0) };
            fonts.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
            stack.Children.Add(fonts);
            card.Content = stack;
            AutomationProperties.SetName(card, "Design " + design.Name);
            if (string.Equals(current, design.Name, StringComparison.OrdinalIgnoreCase))
            {
                card.BorderThickness = new Thickness(2);
                card.SetResourceReference(BorderBrushProperty, "Accent.Fill");
            }
            card.Click += (_, _) =>
            {
                _editor.Run(() => Editor.ApplyDesign(design));
                _editor.ScheduleState();
            };
            designs.Children.Add(card);
        }
        designs.Children.Add(Note("Your own changes to individual paragraphs stay; Ctrl+Z goes back to the previous design."));
    }
}

public sealed partial class ContextPanel
{
    private void BuildListSection(string? kind)
    {
        var list = Section("List", kind == null ? "Not a list. Type “- ” or “1. ” at the start of a line, or use the buttons." : null);
        list.Children.Add(Segments(new (string?, string?, string, bool, Action)[]
        {
            (null, "Icon.TextBulletListLtr", "Bullets", kind == "bullet", () => Editor.ToggleList(true)),
            (null, "Icon.TextNumberListLtr", "Numbering", kind == "number", () => Editor.ToggleList(false)),
        }));
        if (kind == null) return;
        list.Children.Add(Commands(2,
            Command("Level down", "Icon.TextIndentIncreaseLtr", () => Editor.Indent(true), "Tab at the start of the item"),
            Command("Level up", "Icon.TextIndentDecreaseLtr", () => Editor.Indent(false), "Shift+Tab")));
        if (kind != "number") return;
        var buttons = new List<Button> { Command("Restart at 1", "Icon.TextNumberListLtr", () => Editor.RestartNumbering(1), "This item starts a new list at 1") };
        if (Editor.CanContinueNumbering())
            buttons.Add(Command("Continue from the list above", "Icon.TextNumberListLtr", Editor.ContinueNumbering, "Number on from the previous list"));
        buttons.Add(Command("Start at…", "Icon.TextNumberListLtr", AskStartNumber));
        list.Children.Add(Commands(1, buttons.ToArray()));
    }
}
