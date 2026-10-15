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
using Inkwell.Core.Rendering;
using Inkwell.Core.Styles;
using Inkwell.Services;
using Inkwell.Views.Panels;

namespace Inkwell.Views;

/// <summary>The command bar's menus and flyouts: app menu, styles, fonts, colours, more formatting, alignment, insert, table grid.</summary>
public partial class EditorView
{
    private ContextMenu NewMenu(FrameworkElement target)
    {
        return new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom, VerticalOffset = 2 };
    }

    private static void Open(ContextMenu menu) => menu.IsOpen = true;

    // ------------------------------------------------------------------ app menu

    private void OnMenu(object sender, RoutedEventArgs e)
    {
        var menu = NewMenu(MenuButton);
        menu.Items.Add(Item("New document", "Icon.DocumentAdd", () => App.Current.ShowStart(null), "Ctrl+N"));
        menu.Items.Add(Item("Open…", "Icon.FolderOpen", OpenDocument, "Ctrl+O"));
        var recent = AppSettings.Current.Recent.Where(r => !string.Equals(r.Path, Session.FilePath, StringComparison.OrdinalIgnoreCase)).Take(5).ToList();
        if (recent.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var r in recent)
            {
                var path = r.Path;
                var item = Item(Path.GetFileName(path), "Icon.Document", () => App.Current.OpenPath(path, _window));
                item.ToolTip = path;
                menu.Items.Add(item);
            }
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Save", "Icon.Save", () => Save(), "Ctrl+S"));
        menu.Items.Add(Item("Save as…", "Icon.SaveCopy", () => SaveAs(), "Ctrl+Shift+S"));
        menu.Items.Add(Item("Export…", "Icon.ArrowExportLtr", () => ShowExport(null), "Ctrl+Shift+E"));
        menu.Items.Add(Item("Print…", "Icon.Print", Print, "Ctrl+P"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Version history…", "Icon.History", ShowVersionHistory, null, Session.FilePath != null));
        menu.Items.Add(Item("Document properties…", "Icon.Info", ShowProperties));
        menu.Items.Add(Item("Start screen", "Icon.Home", GoToStart));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Settings", "Icon.Settings", () => SettingsWindow.ShowSingle(_window), "Ctrl+,"));
        menu.Items.Add(Item("About Inkwell", "Icon.Info", () => AboutDialog.Open(_window)));
        menu.Items.Add(Item("Close", "Icon.Dismiss", () => _window.Close(), "Ctrl+W"));
        Open(menu);
    }

    // ------------------------------------------------------------------ styles

    private static readonly string[] QuickStyles = { "Normal", "Title", "Subtitle", "Heading1", "Heading2", "Heading3", "Quote", "Caption", "Code" };

    private void OnStyleMenu(object sender, RoutedEventArgs e) => ShowStyleMenu(StyleButton);

    public void ShowStyleMenu(FrameworkElement target)
    {
        var menu = NewMenu(target);
        var doc = Session.Document;
        var current = Editor.GetFormatState().ParagraphStyle;
        var resolver = Editor.Styles;
        foreach (var id in QuickStyles.Select(id => id == "Normal" ? doc.Styles.DefaultParagraphStyleId : id).Distinct())
        {
            var style = doc.Styles.Get(id);
            if (style == null) continue;
            var styleId = id;
            var item = new MenuItem { Header = StylePreview(style, resolver), IsChecked = current == id };
            AutomationProperties.SetName(item, DocEditor.DisplayName(style));
            item.Click += (_, _) =>
            {
                Run(() => Editor.ApplyStyle(styleId));
                FocusDocument();
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        if (current != null && doc.Styles.Get(current) is { } cs)
            menu.Items.Add(Item($"Update “{DocEditor.DisplayName(cs)}” to match the selection", "Icon.TextEditStyle", () => Run(Editor.UpdateStyleFromSelection)));
        menu.Items.Add(Item("Clear formatting", "Icon.TextClearFormatting", () => Run(Editor.ClearFormatting)));
        menu.Items.Add(Item("Reveal formatting", "Icon.TextEffects", () => ShowPanel(PanelMode.Reveal), "Shift+F1"));
        menu.Items.Add(Item("Designs…", "Icon.Color", () => ShowPanel(PanelMode.Design)));
        menu.Items.Add(Item("More styles…", "Icon.Search", OpenPalette, "Ctrl+K"));
        Open(menu);
    }

    /// <summary>A style's name shown in its own look (size capped so the menu stays tidy).</summary>
    private static FrameworkElement StylePreview(Core.Model.Style style, StyleResolver resolver)
    {
        var p = new Paragraph { Props = { StyleId = style.Id } };
        var run = resolver.ResolveRun(new RunProps(), p);
        var text = new TextBlock
        {
            Text = DocEditor.DisplayName(style),
            FontFamily = FontCatalog.Get(run.AsciiFont),
            FontSize = MathEx.Clamp(run.Size * 96 / 72 * 0.85, 12, 22),
            FontWeight = run.Bold ? FontWeights.SemiBold : FontWeights.Normal,
            FontStyle = run.Italic ? FontStyles.Italic : FontStyles.Normal,
            Margin = new Thickness(0, 3, 0, 3),
            MaxWidth = 260,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (run.Caps) text.Text = text.Text.ToUpper(CultureInfo.CurrentCulture);
        return text;
    }

    // ------------------------------------------------------------------ fonts

    private void OnFontMenu(object sender, RoutedEventArgs e) => ShowFontMenu(FontButton);

    public void ShowFontMenu(FrameworkElement target)
    {
        var current = Editor.GetFormatState().Font;
        var theme = Session.Document.Theme;
        var docFonts = new List<string>();
        foreach (var f in new[] { theme.Font(true, "latin"), theme.Font(false, "latin") })
            if (!string.IsNullOrEmpty(f) && !docFonts.Contains(f!)) docFonts.Add(f!);
        var all = FontCatalog.Bundled.Concat(FontCatalog.AvailableSystemChoices()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var installed = Fonts.SystemFontFamilies.Select(f => f.Source).Where(n => !n.Contains('#') && !n.StartsWith("@", StringComparison.Ordinal))
            .Except(all, StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        FontPicker.Show(target, current, docFonts, all, installed, name =>
        {
            Run(() => Editor.SetFont(name));
            FocusDocument();
        });
    }

    // ------------------------------------------------------------------ colours

    private static readonly (string Name, string Hex)[] StandardColors =
    {
        ("Black", "000000"), ("Dark grey", "595959"), ("Grey", "A6A6A6"), ("Red", "C00000"), ("Orange", "E36C0A"), ("Gold", "BF8F00"),
        ("Green", "2E7D32"), ("Teal", "00796B"), ("Blue", "1F5FBF"), ("Indigo", "3949AB"), ("Purple", "7B1FA2"), ("Pink", "C2185B"),
    };

    private static readonly (string Name, string Value)[] Highlights =
    {
        ("Yellow", "yellow"), ("Green", "green"), ("Cyan", "cyan"), ("Pink", "magenta"), ("Light grey", "lightGray"), ("Red", "red"),
    };

    private void OnColorMenu(object sender, RoutedEventArgs e) => ShowColorMenu(ColorButton);

    public void ShowColorMenu(FrameworkElement target)
    {
        var stack = new StackPanel { Margin = new Thickness(12, 10, 12, 10), Width = 236 };
        void Header(string text)
        {
            var t = new TextBlock { Text = text, Margin = new Thickness(0, 4, 0, 6) };
            t.SetResourceReference(StyleProperty, "PanelHeader");
            stack.Children.Add(t);
        }
        Popup? popup = null;
        WrapPanel Swatches(IEnumerable<(string Name, Color Color, Action Apply)> items)
        {
            var wrap = new WrapPanel();
            foreach (var (name, color, apply) in items)
            {
                var b = new Button { Background = new SolidColorBrush(color), ToolTip = name };
                b.SetResourceReference(StyleProperty, "Swatch");
                AutomationProperties.SetName(b, name);
                b.Click += (_, _) =>
                {
                    popup!.IsOpen = false;
                    Run(apply);
                    FocusDocument();
                };
                wrap.Children.Add(b);
            }
            stack.Children.Add(wrap);
            return wrap;
        }

        Header("Text colour");
        var auto = new Button { Content = "Automatic", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(8, 3, 8, 3) };
        auto.SetResourceReference(StyleProperty, "SubtleButton");
        auto.Click += (_, _) =>
        {
            popup!.IsOpen = false;
            Run(() => Editor.SetColor(null));
            FocusDocument();
        };
        stack.Children.Add(auto);
        var theme = Session.Document.Theme;
        var themeColors = new[] { ("Document text", "tx1", "dk1"), ("Accent 1", "accent1", "accent1"), ("Accent 2", "accent2", "accent2"), ("Accent 3", "accent3", "accent3"), ("Accent 4", "accent4", "accent4"), ("Accent 5", "accent5", "accent5"), ("Accent 6", "accent6", "accent6") }
            .Select(t => (t.Item1, t.Item2, Hex: theme.Color(t.Item3)))
            .Where(t => t.Hex != null)
            .ToList();
        if (themeColors.Count > 0)
        {
            Swatches(themeColors.Select(t => (t.Item1 + " (design)", OoxColor.Parse(t.Hex) ?? Colors.Black, (Action)(() => Editor.SetColor(t.Hex, t.Item2 == "tx1" ? "text1" : t.Item2)))));
        }
        Swatches(StandardColors.Select(c => (c.Name, OoxColor.Parse(c.Hex) ?? Colors.Black, (Action)(() => Editor.SetColor(c.Hex)))));
        var custom = new Button { Content = "Custom colour…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(8, 3, 8, 3) };
        custom.SetResourceReference(StyleProperty, "SubtleButton");
        custom.Click += (_, _) =>
        {
            popup!.IsOpen = false;
            var initial = OoxColor.Parse(Editor.GetFormatState().Color) ?? Colors.Black;
            ColorPicker.Show(target, initial, c =>
            {
                Run(() => Editor.SetColor(OoxColor.ToHex(c)));
                FocusDocument();
            });
        };
        stack.Children.Add(custom);

        Header("Highlight");
        var none = new Button { Content = "No highlight", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(8, 3, 8, 3) };
        none.SetResourceReference(StyleProperty, "SubtleButton");
        none.Click += (_, _) =>
        {
            popup!.IsOpen = false;
            Run(() => Editor.SetHighlight(null));
            FocusDocument();
        };
        stack.Children.Add(none);
        Swatches(Highlights.Select(h => (h.Name, OoxColor.Highlight(h.Value) ?? Colors.Yellow, (Action)(() => Editor.SetHighlight(h.Value)))));

        popup = Flyouts.Show(target, stack);
    }

    // ------------------------------------------------------------------ more formatting

    private void OnMoreFormatMenu(object sender, RoutedEventArgs e)
    {
        var f = Editor.GetFormatState();
        var menu = NewMenu((FrameworkElement)sender);
        menu.Items.Add(Check("Strikethrough", "Icon.TextStrikethrough", f.Strike == true, Editor.ToggleStrike, "Ctrl+Shift+5"));
        menu.Items.Add(Check("Superscript", "Icon.TextSuperscript", f.Superscript == true, Editor.ToggleSuperscript, "Ctrl+Shift+="));
        menu.Items.Add(Check("Subscript", "Icon.TextSubscript", f.Subscript == true, Editor.ToggleSubscript, "Ctrl+="));
        menu.Items.Add(Item("Small capitals", "Icon.TextCaseTitle", Editor.ToggleSmallCaps));
        menu.Items.Add(Item("Change case", "Icon.TextChangeCase", CycleCase, "Shift+F3"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Check("Single line spacing", "Icon.TextLineSpacing", f.Line == 240 && f.LineRule == "auto", () => Editor.SetLineSpacing(240), "Ctrl+1"));
        menu.Items.Add(Check("1.15 line spacing", "Icon.TextLineSpacing", f.Line == 276 && f.LineRule == "auto", () => Editor.SetLineSpacing(276)));
        menu.Items.Add(Check("1.5 line spacing", "Icon.TextLineSpacing", f.Line == 360 && f.LineRule == "auto", () => Editor.SetLineSpacing(360), "Ctrl+5"));
        menu.Items.Add(Check("Double line spacing", "Icon.TextLineSpacing", f.Line == 480 && f.LineRule == "auto", () => Editor.SetLineSpacing(480), "Ctrl+2"));
        menu.Items.Add(Item(f.SpaceBefore > 0 ? "Remove space before paragraph" : "Add space before paragraph", "Icon.TextParagraph", () => Editor.SetSpacing(f.SpaceBefore > 0 ? 0 : 240, null)));
        menu.Items.Add(Item(f.SpaceAfter > 0 ? "Remove space after paragraph" : "Add space after paragraph", "Icon.TextParagraph", () => Editor.SetSpacing(null, f.SpaceAfter > 0 ? 0 : 160)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Copy formatting (format painter)", "Icon.PaintBrush", StartFormatPainter, "Ctrl+Shift+C"));
        menu.Items.Add(Item("Clear character formatting", "Icon.TextClearFormatting", Editor.ClearCharacterFormatting, "Ctrl+Space"));
        menu.Items.Add(Item("Paragraph and page options…", "Icon.TextParagraph", () => ShowPanel(PanelMode.Format)));
        Open(menu);
    }

    private MenuItem Check(string header, string icon, bool on, Action action, string? gesture = null)
    {
        var item = Item(header, icon, action, gesture);
        item.IsCheckable = false;
        if (on) item.FontWeight = FontWeights.SemiBold;
        if (on) item.Header = header + "  ✓";
        return item;
    }

    private void OnAlignMenu(object sender, RoutedEventArgs e)
    {
        var f = Editor.GetFormatState();
        var menu = NewMenu(AlignButton);
        menu.Items.Add(Check("Align left", "Icon.TextAlignLeft", f.Alignment is null or "left" or "start", () => Editor.SetAlignment("left"), "Ctrl+L"));
        menu.Items.Add(Check("Centre", "Icon.TextAlignCenter", f.Alignment == "center", () => Editor.SetAlignment("center"), "Ctrl+E"));
        menu.Items.Add(Check("Align right", "Icon.TextAlignRight", f.Alignment is "right" or "end", () => Editor.SetAlignment("right"), "Ctrl+R"));
        menu.Items.Add(Check("Justify", "Icon.TextAlignJustify", f.Alignment is "both" or "distribute", () => Editor.SetAlignment("both"), "Ctrl+J"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Check("Right-to-left paragraph", "Icon.TextParagraphDirectionLeft", f.Rtl == true, () => Editor.SetDirection(true), "Ctrl+Alt+."));
        menu.Items.Add(Check("Left-to-right paragraph", "Icon.TextParagraphDirectionRight", f.Rtl == false, () => Editor.SetDirection(false), "Ctrl+Alt+,"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Increase indent", "Icon.TextIndentIncreaseLtr", () => Editor.Indent(true), "Ctrl+M"));
        menu.Items.Add(Item("Decrease indent", "Icon.TextIndentDecreaseLtr", () => Editor.Indent(false), "Ctrl+Shift+M"));
        Open(menu);
    }

    // ------------------------------------------------------------------ insert

    private void OnInsertPicture(object sender, RoutedEventArgs e) => InsertPicture();

    private void OnInsertLink(object sender, RoutedEventArgs e) => InsertLink();

    private void OnInsertComment(object sender, RoutedEventArgs e) => AddComment();

    private void OnExport(object sender, RoutedEventArgs e) => ShowExport(null);

    /// <summary>Table: a grid to pick the size by pointing (or the keyboard), plus a 3 × 3 default.</summary>
    private void OnInsertTable(object sender, RoutedEventArgs e)
    {
        const int maxRows = 8, maxColumns = 10;
        var stack = new StackPanel { Margin = new Thickness(12, 10, 12, 12) };
        var label = new TextBlock { Text = "Insert a table", Margin = new Thickness(0, 0, 0, 8) };
        label.SetResourceReference(StyleProperty, "PanelHeader");
        stack.Children.Add(label);
        var grid = new UniformGrid { Rows = maxRows, Columns = maxColumns, Background = Brushes.Transparent };
        var cells = new Border[maxRows, maxColumns];
        Popup? popup = null;
        var chosen = (Rows: 3, Columns: 3);
        void Highlight(int rows, int columns)
        {
            chosen = (rows, columns);
            for (var r = 0; r < maxRows; r++)
                for (var c = 0; c < maxColumns; c++)
                    cells[r, c].SetResourceReference(Border.BackgroundProperty, r < rows && c < columns ? "Accent.FillSecondary" : "Control.Fill");
            label.Text = $"{columns} × {rows} table";
        }
        for (var r = 0; r < maxRows; r++)
            for (var c = 0; c < maxColumns; c++)
            {
                var cell = new Border { Width = 18, Height = 18, Margin = new Thickness(1.5), CornerRadius = new CornerRadius(2), BorderThickness = new Thickness(1) };
                cell.SetResourceReference(Border.BorderBrushProperty, "Control.StrokeSecondary");
                var (row, column) = (r + 1, c + 1);
                cell.MouseEnter += (_, _) => Highlight(row, column);
                cell.MouseLeftButtonUp += (_, _) =>
                {
                    popup!.IsOpen = false;
                    InsertTable(row, column);
                };
                cells[r, c] = cell;
                grid.Children.Add(cell);
            }
        stack.Children.Add(grid);
        var hint = new TextBlock { Text = "Arrow keys and Enter work too. Tab in the last cell adds a row.", FontSize = 11.5, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 220 };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
        stack.Children.Add(hint);
        stack.Focusable = true;
        stack.KeyDown += (_, k) =>
        {
            switch (k.Key)
            {
                case Key.Right: Highlight(chosen.Rows, Math.Min(maxColumns, chosen.Columns + 1)); break;
                case Key.Left: Highlight(chosen.Rows, Math.Max(1, chosen.Columns - 1)); break;
                case Key.Down: Highlight(Math.Min(maxRows, chosen.Rows + 1), chosen.Columns); break;
                case Key.Up: Highlight(Math.Max(1, chosen.Rows - 1), chosen.Columns); break;
                case Key.Enter:
                    popup!.IsOpen = false;
                    InsertTable(chosen.Rows, chosen.Columns);
                    break;
                default: return;
            }
            k.Handled = true;
        };
        AutomationProperties.SetName(stack, "Table size");
        Highlight(3, 3);
        popup = Flyouts.Show((FrameworkElement)sender, stack);
        stack.Dispatcher.BeginInvoke(new Action(() => Keyboard.Focus(stack)), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnInsertMenu(object sender, RoutedEventArgs e)
    {
        var menu = NewMenu((FrameworkElement)sender);
        menu.Items.Add(Item("Table of contents", "Icon.TableOfContents", () => Editor.InsertTableOfContents()));
        menu.Items.Add(Item("Footnote", "Icon.TextFootnote", () => InsertNote(NoteKind.Footnote), "Ctrl+Alt+F"));
        menu.Items.Add(Item("Endnote", "Icon.TextFootnote", () => InsertNote(NoteKind.Endnote), "Ctrl+Alt+D"));
        menu.Items.Add(Item("Caption…", "Icon.TextCaption", InsertCaption));
        menu.Items.Add(Item("Cross-reference…", "Icon.Link", InsertCrossReference));
        menu.Items.Add(Item("Citation…", "Icon.BookQuestionMark", () => ShowCitations(true)));
        menu.Items.Add(Item("Bibliography", "Icon.Library", () => Editor.InsertBibliography()));
        menu.Items.Add(Item("Bookmark…", "Icon.Bookmark", InsertBookmark, "Ctrl+Shift+F5"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Page break", "Icon.DocumentPageBreak", () => Editor.InsertBreak(TextRun.PageBreak), "Ctrl+Enter"));
        menu.Items.Add(Item("Section break (new page)", "Icon.DocumentPageBreak", () => Editor.InsertSectionBreak("nextPage")));
        menu.Items.Add(Item("Section break (same page)", "Icon.DocumentPageBreak", () => Editor.InsertSectionBreak("continuous")));
        menu.Items.Add(Item("Page numbers", "Icon.DocumentPageNumber", () => Editor.AddPageNumbers("footer", "center")));
        menu.Items.Add(Item("Header", "Icon.DocumentHeader", () => EditHeaderFooterOfCurrentPage(true)));
        menu.Items.Add(Item("Footer", "Icon.DocumentFooter", () => EditHeaderFooterOfCurrentPage(false)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Date", "Icon.Calendar", () => Editor.InsertText(DateTime.Now.ToString("d MMMM yyyy", CultureInfo.CurrentCulture)), "Alt+Shift+D"));
        menu.Items.Add(Item("Divider", "Icon.LineHorizontal1", Editor.InsertDivider));
        menu.Items.Add(Item("Symbol…", "Icon.Symbols", InsertSymbol));
        menu.Items.Add(Item("Comment", "Icon.CommentAdd", AddComment, "Ctrl+Alt+M"));
        Open(menu);
    }
}

/// <summary>Small light-dismiss flyouts under a button.</summary>
internal static class Flyouts
{
    public static Popup Show(FrameworkElement target, FrameworkElement content, PlacementMode placement = PlacementMode.Bottom)
    {
        var surface = new Border { Child = content, Margin = new Thickness(8, 4, 8, 14) };
        surface.SetResourceReference(FrameworkElement.StyleProperty, "FlyoutSurface");
        var popup = new Popup
        {
            PlacementTarget = target,
            Placement = placement,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = surface,
            VerticalOffset = -2,
            HorizontalOffset = -8,
        };
        surface.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            popup.IsOpen = false;
            e.Handled = true;
        };
        popup.IsOpen = true;
        return popup;
    }
}
