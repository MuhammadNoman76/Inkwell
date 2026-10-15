using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using Inkwell.Core.Editing;
using Inkwell.Core.Layout;
using Inkwell.Core.Model;
using Inkwell.Core.Styles;
using Inkwell.Controls;

namespace Inkwell.Views.Panels;

/// <summary>The picture, table and page sections of the panel.</summary>
public sealed partial class ContextPanel
{
    // ------------------------------------------------------------------ pictures

    private bool _previewing;
    private (Paragraph Para, int Index)? _previewAt;

    private void BuildPicture()
    {
        var drawing = _editor.DocumentView.SelectedDrawing;
        if (drawing == null)
        {
            BuildFormat();
            return;
        }
        var wrap = Section("Wrap text", "Point at a choice to preview it; click to keep it.");
        var grid = new UniformGrid { Columns = 3 };
        foreach (var mode in new[] { WrapMode.Inline, WrapMode.Square, WrapMode.Tight, WrapMode.TopAndBottom, WrapMode.Behind, WrapMode.InFront })
            grid.Children.Add(WrapCard(drawing, mode));
        wrap.Children.Add(grid);

        var position = Section("Position");
        if (drawing.IsFloating)
        {
            position.Children.Add(Segments(new (string?, string?, string, bool, Action)[]
            {
                (null, "Icon.AlignLeft", "Left of the margins", drawing.Float?.Horizontal.Align == "left", () => Editor.AlignDrawing(drawing, "left")),
                (null, "Icon.AlignCenterHorizontal", "Centred between the margins", drawing.Float?.Horizontal.Align == "center", () => Editor.AlignDrawing(drawing, "center")),
                (null, "Icon.AlignRight", "Right of the margins", drawing.Float?.Horizontal.Align == "right", () => Editor.AlignDrawing(drawing, "right")),
            }));
            position.Children.Add(Switch("Lock position", drawing.Float?.Locked == true, on => Editor.SetDrawingLocked(drawing, on),
                "A locked picture can't be dragged by accident; editing text never moves it on its page."));
            var anchor = Editor.ParagraphOf(drawing);
            if (anchor != null)
            {
                var text = anchor.PlainText().Trim();
                position.Children.Add(Note("Anchored to: " + (text.Length == 0 ? "an empty paragraph" : "“" + (text.Length > 48 ? text.Substring(0, 45) + "…" : text) + "”")
                    + ". The picture stays on that paragraph's page, at the same place. Drag the anchor mark ⚓ to move it."));
                position.Children.Add(Command("Go to the anchor paragraph", "Icon.Attach", () =>
                {
                    _editor.DocumentView.SelectDrawing(null);
                    _editor.GoTo(new DocPos(anchor, 0));
                }));
            }
        }
        else
        {
            position.Children.Add(Note("In line with text: the picture moves with the words around it, like a large letter. Choose another wrap to place it freely."));
        }

        var size = Section("Size");
        var width = Emu(drawing.Width);
        var height = Emu(drawing.Height);
        var ratio = height > 0 ? width / height : 1;
        var keep = new CheckBox { Content = "Keep proportions", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
        NumberBox? hBox = null;
        var wBox = Number(width, 0.1, 55, Metric ? 0.5 : 0.25, 2, UnitSuffix, v =>
        {
            var w = FromDisplay(v);
            var h = keep.IsChecked == true ? w / ratio : Units.Emu(drawing.Height);
            Editor.ResizeDrawing(drawing, new Size(w, h));
        });
        hBox = Number(height, 0.1, 55, Metric ? 0.5 : 0.25, 2, UnitSuffix, v =>
        {
            var h = FromDisplay(v);
            var w = keep.IsChecked == true ? h * ratio : Units.Emu(drawing.Width);
            Editor.ResizeDrawing(drawing, new Size(w, h));
        });
        size.Children.Add(Row("Width", wBox, 64));
        size.Children.Add(Row("Height", hBox, 64));
        size.Children.Add(keep);
        if (drawing.IsPicture && Doc.Media.Get(drawing.MediaId) is { } media)
        {
            size.Children.Add(Command("Original size (fits the page)", "Icon.ArrowReset", () =>
            {
                var m = Core.Rendering.MediaImages.Measure(media.Data);
                if (m.Width <= 0) return;
                var w = m.Width * 96.0 / m.DpiX;
                var h = m.Height * 96.0 / m.DpiY;
                var section = Doc.SectionOf(Editor.ParagraphOf(drawing) ?? Editor.Selection.Active.Para);
                var max = Units.Twips(section.PageWidth - section.MarginLeft - section.MarginRight);
                if (w > max)
                {
                    h *= max / w;
                    w = max;
                }
                Editor.ResizeDrawing(drawing, new Size(w, h));
            }));
        }

        var alt = Section("Alt text", "Describes the picture for people using screen readers, and in exported PDFs and web pages.");
        var box = new TextBox { Text = drawing.AltText ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60 };
        Ui.SetPlaceholder(box, "e.g. A bar chart of sales by month");
        AutomationProperties.SetName(box, "Alt text");
        box.LostKeyboardFocus += (_, _) =>
        {
            if ((drawing.AltText ?? "") != box.Text.Trim()) _editor.Run(() => Editor.SetAltText(drawing, box.Text));
        };
        alt.Children.Add(box);

        var actions = Section("Picture");
        actions.Children.Add(Command("Delete picture", "Icon.Delete", () =>
        {
            Editor.DeleteDrawing(drawing);
            _editor.DocumentView.SelectDrawing(null);
        }));
    }

    private static double Emu(long emu) => Metric ? emu / 360000.0 : emu / 914400.0;

    private static double FromDisplay(double value) => Metric ? value / 2.54 * 96 : value * 96;

    /// <summary>A wrap choice with a small drawing of how text flows around the picture; hovering previews it.</summary>
    private Button WrapCard(DrawingObject drawing, WrapMode mode)
    {
        var current = drawing.Wrap == mode;
        var b = new Button { Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(6, 8, 6, 6), ToolTip = DocEditor.WrapName(mode) };
        b.SetResourceReference(StyleProperty, "CardButton");
        b.SetResourceReference(BackgroundProperty, "Card.Background");
        b.SetResourceReference(BorderBrushProperty, "Card.Stroke");
        b.BorderThickness = new Thickness(1);
        if (current)
        {
            b.BorderThickness = new Thickness(2);
            b.SetResourceReference(BorderBrushProperty, "Accent.Fill");
        }
        var stack = new StackPanel();
        stack.Children.Add(WrapIcon(mode));
        var label = new TextBlock { Text = DocEditor.WrapName(mode), FontSize = 11, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        stack.Children.Add(label);
        b.Content = stack;
        AutomationProperties.SetName(b, "Wrap: " + DocEditor.WrapName(mode) + (current ? " (current)" : ""));
        b.MouseEnter += (_, _) =>
        {
            if (current || _previewing) return;
            StartPreview(drawing, mode);
        };
        b.MouseLeave += (_, _) => EndPreview(keep: false);
        b.Click += (_, _) =>
        {
            if (_previewing) EndPreview(keep: true);
            else if (!current) ApplyWrap(drawing, mode);
            _shown = (PanelMode)(-1);
            Refresh();
        };
        return b;
    }

    private void ApplyWrap(DrawingObject drawing, WrapMode mode)
    {
        var placement = _editor.DocumentView.FindDrawing(drawing);
        _editor.Run(() => Editor.SetWrap(drawing, mode, placement?.Page, placement?.Box));
    }

    private void StartPreview(DrawingObject drawing, WrapMode mode)
    {
        var p = Editor.ParagraphOf(drawing);
        if (p == null) return;
        _previewAt = (p, p.Inlines.IndexOf(drawing));
        _suspend = true;
        _previewing = true;
        ApplyWrap(drawing, mode);
    }

    /// <summary>Ends a preview: keeps the change, or undoes it and selects the picture again.</summary>
    private void EndPreview(bool keep)
    {
        if (!_previewing) return;
        _previewing = false;
        _suspend = false;
        if (keep) return;
        Editor.Undo();
        if (_previewAt is { } at && at.Index >= 0 && at.Index < at.Para.Inlines.Count && at.Para.Inlines[at.Index] is DrawingObject restored)
            _editor.DocumentView.SelectDrawing(restored);
        _previewAt = null;
    }

    private static FrameworkElement WrapIcon(WrapMode mode)
    {
        var canvas = new Canvas { Width = 54, Height = 40, HorizontalAlignment = HorizontalAlignment.Center };
        Brush lineBrush = new SolidColorBrush(Color.FromArgb(0x99, 0x80, 0x80, 0x80));
        void Line(double x1, double x2, double y)
        {
            if (x2 - x1 < 2) return;
            canvas.Children.Add(new Rectangle { Width = x2 - x1, Height = 2.4, Fill = lineBrush, RadiusX = 1, RadiusY = 1, Margin = new Thickness(x1, y, 0, 0) });
        }
        var picture = new Rectangle { Width = 20, Height = 16, RadiusX = 2, RadiusY = 2 };
        picture.SetResourceReference(Shape.FillProperty, "Accent.Fill");
        double px = 17, py = 12;
        switch (mode)
        {
            case WrapMode.Inline:
                for (var y = 2.0; y < 40; y += 6) Line(0, 54, y);
                px = 6;
                py = 16;
                picture.Height = 14;
                break;
            case WrapMode.Square:
            case WrapMode.Tight:
                for (var y = 2.0; y < 40; y += 6)
                {
                    if (y + 2 >= py - 2 && y <= py + 18) Line(0, mode == WrapMode.Tight ? px - 2 : px - 4, y);
                    if (y + 2 >= py - 2 && y <= py + 18) Line(px + (mode == WrapMode.Tight ? 22 : 24), 54, y);
                    else Line(0, 54, y);
                }
                break;
            case WrapMode.TopAndBottom:
                for (var y = 2.0; y < 40; y += 6)
                    if (y + 2 < py - 2 || y > py + 18) Line(0, 54, y);
                break;
            case WrapMode.Behind:
                canvas.Children.Add(picture);
                picture.Opacity = 0.45;
                for (var y = 2.0; y < 40; y += 6) Line(0, 54, y);
                Canvas.SetLeft(picture, px);
                Canvas.SetTop(picture, py);
                return canvas;
            case WrapMode.InFront:
                for (var y = 2.0; y < 40; y += 6) Line(0, 54, y);
                break;
        }
        Canvas.SetLeft(picture, px);
        Canvas.SetTop(picture, py);
        canvas.Children.Add(picture);
        return canvas;
    }

    // ------------------------------------------------------------------ tables

    private void BuildTable()
    {
        var cell = Nav.CellOf(Editor.Selection.Active.Para);
        var table = cell?.Row?.Table;
        if (cell == null || table == null)
        {
            BuildFormat();
            return;
        }
        var rows = Section("Rows and columns", $"{table.Rows.Count} rows × {table.ColumnCount} columns. Tab in the last cell adds a row.");
        rows.Children.Add(Commands(2,
            Command("Row above", "Icon.TableInsertRow", () => Editor.InsertRows(false)),
            Command("Row below", "Icon.TableInsertRow", () => Editor.InsertRows(true)),
            Command("Column left", "Icon.TableInsertColumn", () => Editor.InsertColumn(false)),
            Command("Column right", "Icon.TableInsertColumn", () => Editor.InsertColumn(true)),
            Command("Delete row", "Icon.TableDeleteRow", Editor.DeleteRows),
            Command("Delete column", "Icon.TableDeleteColumn", Editor.DeleteColumn),
            Command("Merge cells", "Icon.TableCellsMerge", Editor.MergeCells),
            Command("Split cell", "Icon.TableCellsSplit", Editor.SplitCell)));

        var pages = Section("Across pages");
        pages.Children.Add(Switch("Repeat header row on each page", table.Rows[0].Props.IsHeader, _ => Editor.ToggleHeaderRow()));
        pages.Children.Add(Switch("Don't split rows across pages", cell.Row!.Props.CantSplit, _ => Editor.ToggleCantSplit()));

        var size = Section("Size", "Drag column borders in the page to resize them.");
        size.Children.Add(Commands(1,
            Command("Fit columns to their contents", "Icon.TableResizeColumn", () => Editor.AutoFitContents(_editor.Layout)),
            Command("Fit the table to the page width", "Icon.TableResizeColumn", Editor.FitTableToWidth),
            Command("Make columns the same width", "Icon.TableResizeColumn", Editor.DistributeColumns)));

        var look = Section("Look");
        var styles = new List<(string Id, string Name)>();
        foreach (var (id, name) in new[] { ("InkwellTable", "Inkwell"), ("TableGrid", "Grid"), ("TableNormal", "Plain") })
            if (Doc.Styles.Get(id) != null) styles.Add((id, name));
        if (table.Props.StyleId is { } current && styles.All(s => s.Id != current) && Doc.Styles.Get(current) is { } cs) styles.Insert(0, (current, Core.Editing.DocEditor.DisplayName(cs)));
        if (styles.Count > 0)
        {
            look.Children.Add(Row("Style", Segments(styles.Select(s => ((string?)s.Name, (string?)null, "Table style " + s.Name, table.Props.StyleId == s.Id || (table.Props.StyleId == null && s.Id == "TableNormal"),
                (Action)(() => Editor.SetTableStyle(s.Id)))).ToArray()), 64));
        }
        var tl = table.Props.Look;
        look.Children.Add(Switch("Header row", tl.FirstRow, on => Editor.SetTableLook(on, table.Props.Look.BandedRows, table.Props.Look.FirstColumn)));
        look.Children.Add(Switch("Banded rows", tl.BandedRows, on => Editor.SetTableLook(table.Props.Look.FirstRow, on, table.Props.Look.FirstColumn)));
        look.Children.Add(Switch("First column", tl.FirstColumn, on => Editor.SetTableLook(table.Props.Look.FirstRow, table.Props.Look.BandedRows, on)));

        var cells = Section("Cells");
        var va = cell.Props.VerticalAlignment ?? "top";
        cells.Children.Add(Row("Align text", Segments(new (string?, string?, string, bool, Action)[]
        {
            ("Top", null, "Top", va == "top", () => Editor.SetCellVerticalAlignment("top")),
            ("Middle", null, "Middle", va == "center", () => Editor.SetCellVerticalAlignment("center")),
            ("Bottom", null, "Bottom", va == "bottom", () => Editor.SetCellVerticalAlignment("bottom")),
        }), 84));
        var shades = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var (name, hex) in new (string, string?)[] { ("No shading", null), ("Light grey", "F2F2F2"), ("Grey", "D9D9D9"), ("Blue", "DEEAF6"), ("Green", "E2EFD9"), ("Yellow", "FFF2CC"), ("Rose", "FBE4D5") })
        {
            var swatch = new Button { ToolTip = name, Background = hex == null ? Brushes.Transparent : new SolidColorBrush(OoxColor.Parse(hex) ?? Colors.White) };
            swatch.SetResourceReference(StyleProperty, "Swatch");
            if (hex == null) swatch.Content = "∅";
            AutomationProperties.SetName(swatch, "Cell shading: " + name);
            swatch.Click += (_, _) => _editor.Run(() => Editor.SetCellShading(hex));
            shades.Children.Add(swatch);
        }
        cells.Children.Add(Row("Shading", shades, 84));
        var del = Section("Table");
        del.Children.Add(Command("Delete table", "Icon.Delete", Editor.DeleteTable));
    }

    // ------------------------------------------------------------------ page

    private static readonly (string Name, int W, int H)[] Papers =
    {
        ("A4", 11906, 16838), ("Letter", 12240, 15840), ("Legal", 12240, 20160), ("A5", 8391, 11906), ("A3", 16838, 23811), ("Executive", 10440, 15120),
    };

    private void BuildPage()
    {
        var p = Editor.Selection.Active.Para.Story is MainStory ? Editor.Selection.Active.Para : Nav.FirstIn(Doc.Body.Blocks)!;
        var section = Doc.SectionOf(p);
        var sections = Doc.Sections();
        var sectionIndex = Math.Max(0, sections.FindIndex(s => ReferenceEquals(s.Props.X, section.X)));
        var many = sections.Count > 1;

        var paper = Section("Paper", many ? $"This document has {sections.Count} sections; paper size and margins apply to all of them." : null);
        var combo = new ComboBox { Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
        var w = Math.Min(section.PageWidth, section.PageHeight);
        var h = Math.Max(section.PageWidth, section.PageHeight);
        var index = Array.FindIndex(Papers, x => Math.Abs(x.W - w) < 40 && Math.Abs(x.H - h) < 40);
        foreach (var x in Papers) combo.Items.Add($"{x.Name} ({ToUnit(x.W).ToString("0.#", CultureInfo.CurrentCulture)} × {ToUnit(x.H).ToString("0.#", CultureInfo.CurrentCulture)}{UnitSuffix.Trim()})");
        if (index < 0) combo.Items.Add($"Custom ({ToUnit(w).ToString("0.#", CultureInfo.CurrentCulture)} × {ToUnit(h).ToString("0.#", CultureInfo.CurrentCulture)}{UnitSuffix.Trim()})");
        combo.SelectedIndex = index >= 0 ? index : combo.Items.Count - 1;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0 && combo.SelectedIndex < Papers.Length)
                _editor.Run(() => Editor.SetPaperSize(Papers[combo.SelectedIndex].W, Papers[combo.SelectedIndex].H, true));
        };
        paper.Children.Add(Row("Size", combo, 84));
        paper.Children.Add(Row("Orientation", Segments(new (string?, string?, string, bool, Action)[]
        {
            ("Portrait", null, "Portrait", !section.Landscape, () => Editor.SetOrientation(false, true)),
            ("Landscape", null, "Landscape", section.Landscape, () => Editor.SetOrientation(true, true)),
        }), 84));
        paper.Children.Add(Command(section.Landscape ? "Make only this page portrait" : "Make only this page landscape", "Icon.DocumentLandscape",
            () => Editor.MakePageLandscape(!section.Landscape, _editor.Layout), "Adds section breaks around this page; the pages around it stay as they are"));

        var margins = Section("Margins");
        var presets = new[] { ("Normal", 1440, 1440), ("Narrow", 720, 720), ("Moderate", 1440, 1080), ("Wide", 1440, 2880) };
        var currentPreset = Array.FindIndex(presets, m => section.MarginTop == m.Item2 && section.MarginBottom == m.Item2 && section.MarginLeft == m.Item3 && section.MarginRight == m.Item3);
        margins.Children.Add(Segments(presets.Select((m, i) => ((string?)m.Item1, (string?)null, m.Item1 + " margins", i == currentPreset,
            (Action)(() => Editor.SetMargins(m.Item2, m.Item3, m.Item2, m.Item3, true)))).ToArray()));
        var step = Metric ? 0.25 : 0.1;
        margins.Children.Add(Row("Top", Number(ToUnit(section.MarginTop), 0, 20, step, 2, UnitSuffix, v => Editor.SetMargins(FromUnit(v), section.MarginRight, section.MarginBottom, section.MarginLeft, true)), 84));
        margins.Children.Add(Row("Bottom", Number(ToUnit(section.MarginBottom), 0, 20, step, 2, UnitSuffix, v => Editor.SetMargins(section.MarginTop, section.MarginRight, FromUnit(v), section.MarginLeft, true)), 84));
        margins.Children.Add(Row("Left", Number(ToUnit(section.MarginLeft), 0, 20, step, 2, UnitSuffix, v => Editor.SetMargins(section.MarginTop, section.MarginRight, section.MarginBottom, FromUnit(v), true)), 84));
        margins.Children.Add(Row("Right", Number(ToUnit(section.MarginRight), 0, 20, step, 2, UnitSuffix, v => Editor.SetMargins(section.MarginTop, FromUnit(v), section.MarginBottom, section.MarginLeft, true)), 84));

        var columns = Section("Columns", many ? "For this section." : null);
        columns.Children.Add(Segments(new[] { 1, 2, 3 }.Select(n => ((string?)n.ToString(CultureInfo.CurrentCulture), (string?)null, n + (n == 1 ? " column" : " columns"), section.ColumnCount == n,
            (Action)(() => Editor.SetColumns(n)))).ToArray()));

        var hf = Section("Header and footer", "Click the top or bottom of a page to edit them.");
        hf.Children.Add(Commands(2,
            Command("Edit header", "Icon.DocumentHeader", () => _editor.EditHeaderFooterOfCurrentPage(true)),
            Command("Edit footer", "Icon.DocumentFooter", () => _editor.EditHeaderFooterOfCurrentPage(false))));
        hf.Children.Add(Switch("Different first page", section.TitlePage, on => Editor.SetTitlePage(on), "A title page without the header and footer (or with its own)"));
        hf.Children.Add(Switch("Different odd and even pages", Doc.Settings.EvenAndOddHeaders, on => Editor.SetEvenOddHeaders(on), "For printed books: page numbers on the outside edge"));
        var numbers = Section("Page numbers");
        var where = new ComboBox { Width = 170, HorizontalAlignment = HorizontalAlignment.Left };
        var places = new[] { ("Bottom, centre", "footer", "center"), ("Bottom, right", "footer", "right"), ("Bottom, left", "footer", "left"), ("Top, right", "header", "right"), ("Top, centre", "header", "center") };
        foreach (var x in places) where.Items.Add(x.Item1);
        where.SelectedIndex = 0;
        numbers.Children.Add(Row("Where", where, 64));
        var total = new CheckBox { Content = "Show “Page 3 of 10”", Margin = new Thickness(0, 6, 0, 4) };
        numbers.Children.Add(total);
        numbers.Children.Add(Command("Add page numbers", "Icon.DocumentPageNumber", () =>
        {
            var x = places[Math.Max(0, where.SelectedIndex)];
            Editor.AddPageNumbers(x.Item2, x.Item3, total.IsChecked == true, sectionIndex);
        }));

        var breaks = Section("Breaks");
        breaks.Children.Add(Commands(1,
            Command("Page break", "Icon.DocumentPageBreak", () => Editor.InsertBreak(TextRun.PageBreak), "Ctrl+Enter"),
            Command("Section break (new page)", "Icon.DocumentPageBreak", () => Editor.InsertSectionBreak("nextPage")),
            Command("Section break (same page)", "Icon.DocumentPageBreak", () => Editor.InsertSectionBreak("continuous"), "For changing the number of columns part-way down a page")));
        breaks.Children.Add(Note("Show formatting marks (Ctrl+Shift+8) to see the breaks; click one and press Delete to remove it."));
    }
}
