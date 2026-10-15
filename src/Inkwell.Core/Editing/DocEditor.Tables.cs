using Inkwell.Core.Layout;
using Inkwell.Core.Model;

namespace Inkwell.Core.Editing;

public sealed partial class DocEditor
{
    /// <summary>The text width (twips) where a paragraph sits: its cell's content width, or the section's text width.</summary>
    public int AvailableWidthTwips(Paragraph p)
    {
        if (Nav.CellOf(p) is { } cell && cell.Props.Width.Type == "dxa") return Math.Max(720, cell.Props.Width.Value - 216);
        var s = Doc.SectionOf(p);
        var width = s.PageWidth - s.MarginLeft - s.MarginRight - s.Gutter;
        if (s.ColumnCount > 1) width = (width - s.ColumnSpace * (s.ColumnCount - 1)) / s.ColumnCount;
        return Math.Max(720, width);
    }

    /// <summary>Inserts a table at the caret (splitting the paragraph if needed) and puts the caret in its first cell.</summary>
    public Table InsertTable(int rows, int columns, bool headerRow = true)
    {
        rows = MathEx.Clamp(rows, 1, 200);
        columns = MathEx.Clamp(columns, 1, 63);
        Table? created = null;
        Edit("Insert table", t =>
        {
            var pos = Selection.IsEmpty ? Selection.Active : DeleteRange(t, Selection.Start, Selection.End);
            var p = pos.Para;
            EnsureEditable(p);
            var width = AvailableWidthTwips(p);
            var table = new Table();
            table.Props.StyleId = Doc.Styles.Get("InkwellTable") != null ? "InkwellTable" : Doc.Styles.Get("TableGrid") != null ? "TableGrid" : null;
            table.Props.Width = new TableWidth("dxa", width);
            table.Props.Layout = "fixed";
            table.Props.Look = (headerRow, false, false, false, true, false);
            var colWidth = width / columns;
            table.Grid = Enumerable.Repeat(colWidth, columns).ToList();
            for (var r = 0; r < rows; r++)
            {
                var row = new TableRow();
                if (headerRow && r == 0) row.Props.IsHeader = true;
                for (var c = 0; c < columns; c++) row.AddCell(TableCell.Create(colWidth));
                table.AddRow(row);
            }
            var list = p.Parent!;
            t.Touch(list);
            int index;
            if (pos.Offset == 0)
            {
                index = list.IndexOf(p);
            }
            else if (pos.Offset >= p.Length)
            {
                index = list.IndexOf(p) + 1;
                // Keep a paragraph after the table (Word requires one before the end of a cell or the document).
                if (index >= list.Count || list[index] is not Paragraph) list.Insert(index, new Paragraph { Props = { StyleId = p.Props.StyleId } });
            }
            else
            {
                var second = SplitParagraph(t, pos, keepStyle: true);
                index = list.IndexOf(second.Para);
            }
            list.Insert(index, table);
            created = table;
            return new Selection(new DocPos((Paragraph)table.Rows[0].Cells[0].Blocks[0], 0));
        });
        return created!;
    }

    /// <summary>The cells the selection covers in its table (a rectangle of rows and grid columns).</summary>
    public (Table Table, int Row0, int Row1, int Col0, int Col1)? SelectedCellRange()
    {
        var s = Selection;
        var a = Nav.CellOf(s.Start.Para);
        var b = Nav.CellOf(s.End.Para);
        if (a == null) return null;
        if (b == null || !ReferenceEquals(b.Row?.Table, a.Row?.Table)) b = a;
        var table = a.Row!.Table!;
        var r0 = table.Rows.IndexOf(a.Row);
        var r1 = table.Rows.IndexOf(b.Row!);
        var c0 = table.GridColumnOf(a);
        var c1 = table.GridColumnOf(b) + b.Props.GridSpan - 1;
        if (r1 < r0) (r0, r1) = (r1, r0);
        if (c1 < c0)
        {
            var c = c0;
            c0 = table.GridColumnOf(b);
            c1 = c + a.Props.GridSpan - 1;
        }
        return (table, r0, r1, c0, c1);
    }

    private static void SyncCellWidths(Table table)
    {
        foreach (var row in table.Rows)
        {
            var col = row.GridBefore;
            foreach (var cell in row.Cells)
            {
                var span = cell.Props.GridSpan;
                var w = 0;
                for (var k = col; k < col + span && k < table.Grid.Count; k++) w += table.Grid[k];
                if (w > 0) cell.Props.Width = new TableWidth("dxa", w);
                col += span;
            }
        }
        var total = table.Grid.Sum();
        if (table.Props.Width.Type == "dxa" || table.Props.Width.Type == "auto") table.Props.Width = new TableWidth("dxa", total);
    }

    private void TouchTableDeep(Transaction t, Table table)
    {
        t.Touch(table);
        foreach (var row in table.Rows)
            foreach (var cell in row.Cells)
                t.Touch(cell.Blocks);
    }

    /// <summary>Inserts a row above or below the selected rows (copying their formatting).</summary>
    public void InsertRows(bool below)
    {
        var range = SelectedCellRange();
        if (range == null) return;
        var (table, r0, r1, _, _) = range.Value;
        var count = r1 - r0 + 1;
        Edit(below ? "Insert row below" : "Insert row above", t =>
        {
            t.Touch(table);
            var source = table.Rows[below ? r1 : r0];
            var at = below ? r1 + 1 : r0;
            TableRow? first = null;
            for (var i = 0; i < count; i++)
            {
                var row = new TableRow { Props = source.Props.Clone() };
                row.Props.IsHeader = false;
                foreach (var c in source.Cells)
                {
                    var cell = new TableCell { Props = c.Props.Clone() };
                    // A new row continues a vertical merge that runs through it, otherwise starts clean.
                    if (cell.Props.VerticalMerge == "restart") cell.Props.VerticalMerge = below ? "continue" : null;
                    var para = new Paragraph();
                    if (c.Blocks.OfType<Paragraph>().FirstOrDefault() is { } model) para.Props = model.Props.FormattingOnly();
                    cell.Blocks.Add(para);
                    row.AddCell(cell);
                }
                table.InsertRow(at + i, row);
                first ??= row;
            }
            return new Selection(new DocPos((Paragraph)first!.Cells[0].Blocks.OfType<Paragraph>().First(), 0));
        });
    }

    /// <summary>Inserts a column left or right of the selection's column; the column's width is shared.</summary>
    public void InsertColumn(bool right)
    {
        var range = SelectedCellRange();
        if (range == null) return;
        var (table, _, _, c0, c1) = range.Value;
        var col = right ? c1 : c0;
        Edit(right ? "Insert column right" : "Insert column left", t =>
        {
            t.Touch(table);
            if (table.Grid.Count == 0) table.Grid = Enumerable.Repeat(AvailableWidthTwips(Selection.Active.Para) / Math.Max(1, table.ColumnCount), table.ColumnCount).ToList();
            var half = Math.Max(360, table.Grid[col] / 2);
            table.Grid[col] = Math.Max(360, table.Grid[col] - half);
            table.Grid.Insert(right ? col + 1 : col, half);
            var newIndex = right ? col + 1 : col;
            foreach (var row in table.Rows)
            {
                var cell = Table.CellAtColumn(row, col);
                var gridStart = cell != null ? table.GridColumnOf(cell) : -1;
                if (cell != null && cell.Props.GridSpan > 1 && newIndex > gridStart && newIndex < gridStart + cell.Props.GridSpan)
                {
                    // Inside a merged cell: widen it instead.
                    cell.Props.GridSpan += 1;
                    continue;
                }
                var newCell = new TableCell { Props = cell?.Props.Clone() ?? new CellProps() };
                newCell.Props.GridSpan = 1;
                newCell.Props.VerticalMerge = null;
                newCell.Blocks.Add(new Paragraph());
                var index = cell == null ? row.Cells.Count : row.Cells.IndexOf(cell) + (right ? 1 : 0);
                row.InsertCell(index, newCell);
            }
            SyncCellWidths(table);
            return null;
        });
    }

    public void DeleteRows()
    {
        var range = SelectedCellRange();
        if (range == null) return;
        var (table, r0, r1, _, _) = range.Value;
        if (r1 - r0 + 1 >= table.Rows.Count)
        {
            DeleteTable();
            return;
        }
        Edit("Delete row", t =>
        {
            TouchTableDeep(t, table);
            for (var r = r1; r >= r0; r--)
            {
                // A vertical merge that started here continues from the next row.
                var row = table.Rows[r];
                if (r + 1 < table.Rows.Count)
                    foreach (var cell in row.Cells.Where(c => c.Props.VerticalMerge == "restart"))
                    {
                        var below = Table.CellAtColumn(table.Rows[r + 1], table.GridColumnOf(cell));
                        if (below?.Props.VerticalMerge == "continue")
                        {
                            below.Props.VerticalMerge = "restart";
                            if (below.Blocks.All(b => b is Paragraph { IsEmpty: true }))
                            {
                                below.Blocks.Clear();
                                foreach (var b in cell.Blocks.ToList()) below.Blocks.Add(b);
                            }
                        }
                    }
                table.Rows.RemoveAt(r);
            }
            var target = table.Rows[Math.Min(r0, table.Rows.Count - 1)];
            return new Selection(new DocPos(Nav.FirstIn(target.Cells[0].Blocks)!, 0));
        });
    }

    public void DeleteColumn()
    {
        var range = SelectedCellRange();
        if (range == null) return;
        var (table, _, _, c0, c1) = range.Value;
        if (c1 - c0 + 1 >= table.ColumnCount)
        {
            DeleteTable();
            return;
        }
        Edit("Delete column", t =>
        {
            t.Touch(table);
            for (var col = c1; col >= c0; col--)
            {
                foreach (var row in table.Rows)
                {
                    var cell = Table.CellAtColumn(row, col);
                    if (cell == null) continue;
                    if (cell.Props.GridSpan > 1) cell.Props.GridSpan -= 1;
                    else row.Cells.Remove(cell);
                }
                if (col < table.Grid.Count) table.Grid.RemoveAt(col);
            }
            foreach (var row in table.Rows.Where(r => r.Cells.Count == 0).ToList()) table.Rows.Remove(row);
            SyncCellWidths(table);
            var cellAt = Table.CellAtColumn(table.Rows[0], Math.Min(c0, table.ColumnCount - 1)) ?? table.Rows[0].Cells[0];
            return new Selection(new DocPos(Nav.FirstIn(cellAt.Blocks)!, 0));
        });
    }

    public void DeleteTable()
    {
        var cell = Nav.CellOf(Selection.Active.Para);
        var table = cell?.Row?.Table;
        if (table?.Parent == null) return;
        Edit("Delete table", t =>
        {
            var list = table.Parent!;
            t.Touch(list);
            var index = list.IndexOf(table);
            list.RemoveAt(index);
            if (list.Count == 0 || (index >= list.Count && list.Owner is TableCell)) list.Add(new Paragraph());
            var next = index < list.Count ? Nav.FirstIn(list[index]) : null;
            next ??= index > 0 ? Nav.LastIn(list[index - 1]) : null;
            if (next == null)
            {
                next = new Paragraph();
                list.Insert(Math.Min(index, list.Count), next);
            }
            return new Selection(new DocPos(next, 0));
        });
    }

    /// <summary>Merges the selected cells (content joins into the first one).</summary>
    public void MergeCells()
    {
        var range = SelectedCellRange();
        if (range == null) return;
        var (table, r0, r1, c0, c1) = range.Value;
        if (r0 == r1 && c0 == c1) return;
        Edit("Merge cells", t =>
        {
            TouchTableDeep(t, table);
            TableCell? first = null;
            for (var r = r0; r <= r1; r++)
            {
                var row = table.Rows[r];
                var cells = row.Cells.Where(c =>
                {
                    var start = table.GridColumnOf(c);
                    return start + c.Props.GridSpan - 1 >= c0 && start <= c1;
                }).ToList();
                if (cells.Count == 0) continue;
                var keep = cells[0];
                var span = cells.Sum(c => c.Props.GridSpan);
                foreach (var extra in cells.Skip(1))
                {
                    MoveContent(extra, first ?? keep);
                    row.Cells.Remove(extra);
                }
                keep.Props.GridSpan = span;
                if (r1 > r0)
                {
                    if (r == r0)
                    {
                        keep.Props.VerticalMerge = "restart";
                        first = keep;
                    }
                    else
                    {
                        MoveContent(keep, first!);
                        keep.Props.VerticalMerge = "continue";
                    }
                }
                else first ??= keep;
            }
            SyncCellWidths(table);
            return new Selection(new DocPos(Nav.FirstIn(first!.Blocks)!, 0));
        });
    }

    private static void MoveContent(TableCell from, TableCell to)
    {
        if (ReferenceEquals(from, to)) return;
        var blocks = from.Blocks.ToList();
        var meaningful = blocks.Any(b => b is not Paragraph { IsEmpty: true });
        from.Blocks.Clear();
        from.Blocks.Add(new Paragraph());
        if (!meaningful) return;
        if (to.Blocks.Count == 1 && to.Blocks[0] is Paragraph { IsEmpty: true }) to.Blocks.Clear();
        foreach (var b in blocks) to.Blocks.Add(b);
    }

    /// <summary>Splits a merged cell back into its columns (and rows).</summary>
    public void SplitCell()
    {
        var cell = Nav.CellOf(Selection.Active.Para);
        var table = cell?.Row?.Table;
        if (cell == null || table == null) return;
        var span = cell.Props.GridSpan;
        var merged = cell.Props.VerticalMerge == "restart";
        if (span <= 1 && !merged) throw new EditRefusedException("This cell isn't merged. Use Insert column or Insert row to add cells.");
        Edit("Split cell", t =>
        {
            TouchTableDeep(t, table);
            var col = table.GridColumnOf(cell);
            var rowIndex = table.Rows.IndexOf(cell.Row!);
            if (merged)
            {
                cell.Props.VerticalMerge = null;
                for (var r = rowIndex + 1; r < table.Rows.Count; r++)
                {
                    var below = Table.CellAtColumn(table.Rows[r], col);
                    if (below?.Props.VerticalMerge != "continue") break;
                    below.Props.VerticalMerge = null;
                }
            }
            if (span > 1)
            {
                cell.Props.GridSpan = 1;
                var index = cell.Row!.Cells.IndexOf(cell);
                for (var k = 1; k < span; k++)
                {
                    var extra = new TableCell { Props = cell.Props.Clone() };
                    extra.Blocks.Add(new Paragraph());
                    cell.Row.InsertCell(index + k, extra);
                }
            }
            SyncCellWidths(table);
            return null;
        });
    }

    /// <summary>Repeat the first row(s) at the top of each page: on for rows up to the current one, or off.</summary>
    public void ToggleHeaderRow()
    {
        var range = SelectedCellRange();
        if (range == null) return;
        var (table, _, r1, _, _) = range.Value;
        var on = !table.Rows[0].Props.IsHeader;
        Edit(on ? "Repeat header row" : "Don't repeat header row", t =>
        {
            t.Touch(table);
            for (var r = 0; r < table.Rows.Count; r++) table.Rows[r].Props.IsHeader = on && r <= r1;
            var look = table.Props.Look;
            table.Props.Look = (on || look.FirstRow, look.LastRow, look.FirstColumn, look.LastColumn, look.BandedRows, look.BandedColumns);
            return null;
        });
    }

    public void ToggleCantSplit()
    {
        var range = SelectedCellRange();
        if (range == null) return;
        var (table, r0, r1, _, _) = range.Value;
        var on = !table.Rows[r0].Props.CantSplit;
        Edit(on ? "Don't split rows" : "Allow rows to split", t =>
        {
            t.Touch(table);
            // Applies to the whole table when one row is selected (the common wish), else to the selected rows.
            var all = r0 == r1;
            for (var r = 0; r < table.Rows.Count; r++)
                if (all || (r >= r0 && r <= r1)) table.Rows[r].Props.CantSplit = on;
            return null;
        });
    }

    public void DistributeColumns()
    {
        var range = SelectedCellRange();
        if (range == null) return;
        var table = range.Value.Table;
        Edit("Distribute columns", t =>
        {
            t.Touch(table);
            var total = table.Grid.Sum();
            var n = Math.Max(1, table.Grid.Count);
            table.Grid = Enumerable.Repeat(total / n, n).ToList();
            SyncCellWidths(table);
            return null;
        });
    }

    public void FitTableToWidth()
    {
        var range = SelectedCellRange();
        if (range == null) return;
        var table = range.Value.Table;
        var anchor = Nav.FirstIn(table)!;
        var target = table.Parent?.Owner is TableCell outer && outer.Props.Width.Type == "dxa" ? outer.Props.Width.Value - 216 : AvailableWidthTwips(TopLevelParagraphNear(table) ?? anchor);
        Edit("Fit table to width", t =>
        {
            t.Touch(table);
            var total = Math.Max(1, table.Grid.Sum());
            table.Grid = table.Grid.Select(g => (int)Math.Round(g * (double)target / total)).ToList();
            table.Props.Width = new TableWidth("dxa", table.Grid.Sum());
            table.Props.Indent = new TableWidth("dxa", 0);
            SyncCellWidths(table);
            return null;
        });
    }

    private Paragraph? TopLevelParagraphNear(Table table)
    {
        var list = table.Parent;
        if (list == null) return null;
        var i = list.IndexOf(table);
        for (var k = i + 1; k < list.Count; k++)
            if (list[k] is Paragraph p) return p;
        for (var k = i - 1; k >= 0; k--)
            if (list[k] is Paragraph p) return p;
        return null;
    }

    /// <summary>Column widths from the widest content of each column (within the page width).</summary>
    public void AutoFitContents(DocumentLayout layout)
    {
        var range = SelectedCellRange();
        if (range == null) return;
        var table = range.Value.Table;
        var anchor = TopLevelParagraphNear(table) ?? Selection.Active.Para;
        var available = AvailableWidthTwips(anchor);
        var widths = new double[Math.Max(1, table.ColumnCount)];
        foreach (var row in table.Rows)
        {
            foreach (var cell in row.Cells)
            {
                if (cell.Props.GridSpan != 1) continue;
                var col = table.GridColumnOf(cell);
                if (col < 0 || col >= widths.Length) continue;
                foreach (var p in cell.Blocks.OfType<Paragraph>())
                {
                    var pl = layout.Blocks.Paragraph(p, 4000);
                    var w = pl.Lines.Count == 0 ? 0 : pl.Lines.Max(l => l.ContentWidth + l.X);
                    widths[col] = Math.Max(widths[col], Units.ToTwips(w) + 216 + 40);
                }
            }
        }
        for (var i = 0; i < widths.Length; i++) widths[i] = Math.Max(widths[i], 540);
        var sum = widths.Sum();
        if (sum > available) for (var i = 0; i < widths.Length; i++) widths[i] = widths[i] * available / sum;
        Edit("Fit columns to contents", t =>
        {
            t.Touch(table);
            table.Grid = widths.Select(w => (int)Math.Round(w)).ToList();
            SyncCellWidths(table);
            return null;
        });
    }

    /// <summary>Drags a column border by <paramref name="deltaDip"/>: neighbouring columns trade width (the last border widens the table).</summary>
    public void ResizeColumn(Table table, int edge, double deltaDip)
    {
        if (table.Grid.Count == 0) return;
        var delta = Units.ToTwips(deltaDip);
        Edit("Resize column", t =>
        {
            t.Touch(table);
            var left = edge - 1;
            if (left < 0 || left >= table.Grid.Count) return null;
            const int min = 360;
            if (edge < table.Grid.Count)
            {
                var d = MathEx.Clamp(delta, min - table.Grid[left], table.Grid[edge] - min);
                table.Grid[left] += d;
                table.Grid[edge] -= d;
            }
            else
            {
                table.Grid[left] = Math.Max(min, table.Grid[left] + delta);
            }
            table.Props.Layout = "fixed";
            SyncCellWidths(table);
            return null;
        });
    }

    /// <summary>Tab in a table: the next cell (a new row after the last cell); Shift+Tab: the previous cell.</summary>
    public bool TabInTable(bool backwards)
    {
        var cell = Nav.CellOf(Selection.Active.Para);
        var table = cell?.Row?.Table;
        if (cell == null || table == null) return false;
        var cells = table.Rows.SelectMany(r => r.Cells).Where(c => c.Props.VerticalMerge != "continue").ToList();
        var index = cells.IndexOf(cell);
        if (backwards)
        {
            if (index <= 0) return true;
            var prev = cells[index - 1];
            var p = Nav.LastIn(prev.Blocks)!;
            Selection = new Selection(new DocPos(Nav.FirstIn(prev.Blocks)!, 0), new DocPos(p, p.Length));
            return true;
        }
        if (index >= cells.Count - 1)
        {
            // Tab in the last cell adds a row, like Word.
            Selection = new Selection(new DocPos(Nav.FirstIn(cell.Blocks)!, 0));
            InsertRows(true);
            return true;
        }
        var next = cells[index + 1];
        var last = Nav.LastIn(next.Blocks)!;
        Selection = new Selection(new DocPos(Nav.FirstIn(next.Blocks)!, 0), new DocPos(last, last.Length));
        return true;
    }

    /// <summary>Shading for the selected cells.</summary>
    public void SetCellShading(string? hex)
    {
        var range = SelectedCellRange();
        if (range == null) return;
        var (table, r0, r1, c0, c1) = range.Value;
        Edit("Cell shading", t =>
        {
            t.Touch(table);
            for (var r = r0; r <= r1; r++)
                foreach (var cell in table.Rows[r].Cells)
                {
                    var start = table.GridColumnOf(cell);
                    if (start + cell.Props.GridSpan - 1 < c0 || start > c1) continue;
                    cell.Props.ShadingFill = hex;
                }
            return null;
        });
    }

    /// <summary>Applies a table style (and its banding options).</summary>
    public void SetTableStyle(string styleId)
    {
        var range = SelectedCellRange();
        if (range == null) return;
        var table = range.Value.Table;
        Edit("Table style", t =>
        {
            t.Touch(table);
            table.Props.StyleId = styleId;
            return null;
        });
    }

    public void SetTableLook(bool headerRow, bool bandedRows, bool firstColumn)
    {
        var range = SelectedCellRange();
        if (range == null) return;
        var table = range.Value.Table;
        Edit("Table options", t =>
        {
            t.Touch(table);
            var look = table.Props.Look;
            table.Props.Look = (headerRow, look.LastRow, firstColumn, look.LastColumn, bandedRows, look.BandedColumns);
            return null;
        });
    }

    /// <summary>Vertical alignment of the text in the selected cells.</summary>
    public void SetCellVerticalAlignment(string align)
    {
        var range = SelectedCellRange();
        if (range == null) return;
        var (table, r0, r1, c0, c1) = range.Value;
        Edit("Cell alignment", t =>
        {
            t.Touch(table);
            for (var r = r0; r <= r1; r++)
                foreach (var cell in table.Rows[r].Cells)
                {
                    var start = table.GridColumnOf(cell);
                    if (start + cell.Props.GridSpan - 1 < c0 || start > c1) continue;
                    cell.Props.VerticalAlignment = align == "top" ? null : align;
                }
            return null;
        });
    }
}
