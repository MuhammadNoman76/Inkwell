using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using Inkwell.Core.Model;
using Inkwell.Core.Styles;

namespace Inkwell.Core.Layout;

/// <summary>A block laid out for stacking (in a cell, header, footer, note or text box).</summary>
public sealed class BlockLayout
{
    public Block Block = null!;
    public ParagraphLayout? Paragraph;
    public TableLayout? Table;

    /// <summary>Height of a placeholder block (preserved content without display blocks).</summary>
    public double BoxHeight;
    public string? BoxLabel;
}

/// <summary>A table laid out at a width: columns, rows and cells with their content.</summary>
public sealed class TableLayout
{
    public Table Table = null!;

    /// <summary>Left edge relative to the container, and total width.</summary>
    public double X;
    public double Width;

    /// <summary>Grid column boundaries relative to the table's left edge (Count = columns + 1).</summary>
    public double[] ColumnEdges = Array.Empty<double>();

    public List<RowLayout> Rows { get; } = new();

    /// <summary>Rows repeated at the top of each page (consecutive w:tblHeader rows at the start).</summary>
    public int HeaderRows;

    public bool Rtl;

    public string Key = "";
    public int Version;

    public double Height => Rows.Sum(r => r.Height);
}

public sealed class RowLayout
{
    public TableRow Row = null!;
    public int Index;
    public double Height;
    public bool CantSplit;
    public bool IsHeader;

    /// <summary>Exact height (w:trHeight hRule="exact"), or 0.</summary>
    public double ExactHeight;
    public List<CellLayout> Cells { get; } = new();
}

public sealed class CellLayout
{
    public TableCell Cell = null!;
    public int GridColumn;
    public int GridSpan = 1;

    /// <summary>Rows covered by a vertical merge starting here (1 = not merged).</summary>
    public int RowSpan = 1;

    /// <summary>A continuation cell of a vertical merge (drawn as part of the cell above).</summary>
    public bool IsMergedContinuation;

    /// <summary>Box relative to the table's left edge.</summary>
    public double X;
    public double Width;

    public double MarginTop, MarginBottom, MarginLeft, MarginRight;

    public List<BlockLayout> Content { get; } = new();
    public double ContentHeight;

    public BorderDef? Top, Bottom, Left, Right;
    public bool TopExplicit, BottomExplicit, LeftExplicit, RightExplicit;
    public Color? Fill;
    public string VerticalAlign = "top";
    public TableContext Context = null!;

    public double ContentWidth => Math.Max(1, Width - MarginLeft - MarginRight);
}

/// <summary>
/// Lays out blocks that don't paginate on their own (cells, headers, footers, notes, text boxes) and tables;
/// caches paragraph and table layouts by version.
/// </summary>
public sealed class BlockLayouter
{
    private readonly LineFormatter _lines;
    private readonly Dictionary<Paragraph, ParagraphLayout> _paragraphs = new(ReferenceEqualityComparer<Paragraph>.Instance);
    private readonly Dictionary<Table, TableLayout> _tables = new(ReferenceEqualityComparer<Table>.Instance);

    public BlockLayouter(LineFormatter lines, LayoutEnv env)
    {
        _lines = lines;
        Env = env;
    }

    public LayoutEnv Env { get; set; }

    public LineFormatter Lines => _lines;

    public double DefaultTab => Units.Twips(Env.Document.Settings.DefaultTabStop);

    public void Clear()
    {
        _paragraphs.Clear();
        _tables.Clear();
    }

    /// <summary>Drops cached layouts of blocks that are no longer in use (called after a full pass).</summary>
    public void Trim(HashSet<Paragraph> keepParagraphs, HashSet<Table> keepTables)
    {
        foreach (var p in _paragraphs.Keys.Where(k => !keepParagraphs.Contains(k)).ToList()) _paragraphs.Remove(p);
        foreach (var t in _tables.Keys.Where(k => !keepTables.Contains(k)).ToList()) _tables.Remove(t);
    }

    public int CachedParagraphs => _paragraphs.Count;

    // ------------------------------------------------------------------ paragraphs

    /// <summary>The paragraph's layout at a width (cached by version, width, table context, list label and field values).</summary>
    public ParagraphLayout Paragraph(Paragraph p, double width, TableContext? table = null, LayoutEnv? env = null)
    {
        env ??= Env;
        var label = env.Labels.TryGetValue(p, out var l) ? l : "";
        var fields = env.Fields?.Key ?? "";
        var notes = NoteKey(p, env);
        var key = string.Concat(width.ToString("0.##", CultureInfo.InvariantCulture), "|", table?.Key ?? "", "|", label, "|", fields, "|", notes);
        if (_paragraphs.TryGetValue(p, out var cached) && cached.Version == p.Version && cached.Key == key) return cached;
        var source = ParaSource.Build(p, env, table);
        var layout = _lines.Layout(source, width, DefaultTab);
        layout.Key = key;
        layout.Version = p.Version;
        // Header / footer paragraphs vary per page: keep only body ones in the cache.
        if (env.Fields == null || !HasPageFields(p)) _paragraphs[p] = layout;
        return layout;
    }

    private static string NoteKey(Paragraph p, LayoutEnv env)
    {
        string? key = null;
        foreach (var i in p.Inlines)
        {
            if (i is NoteReference r) key += env.NoteNumbers.TryGetValue((r.Kind, r.Id), out var n) ? n + "," : "?,";
            else if (i is NoteMark m && p.Story is Note note) key += env.NoteNumbers.TryGetValue((m.Kind, note.Id), out var n2) ? n2 + "," : "?,";
        }
        return key ?? "";
    }

    private static bool HasPageFields(Paragraph p) =>
        p.Inlines.Any(i => i is FieldInline f && f.Kind is FieldKind.Page or FieldKind.NumPages or FieldKind.SectionPages or FieldKind.StyleRef or FieldKind.Date or FieldKind.Time or FieldKind.FileName);

    // ------------------------------------------------------------------ stacks (cells, headers, notes)

    /// <summary>Lays out blocks one under another at a width; returns their layouts and the total height.</summary>
    public (List<BlockLayout> Blocks, double Height) Stack(IEnumerable<Block> blocks, double width, TableContext? table = null, LayoutEnv? env = null)
    {
        var list = new List<BlockLayout>();
        foreach (var b in Flatten(blocks))
        {
            switch (b)
            {
                case Paragraph p:
                    list.Add(new BlockLayout { Block = p, Paragraph = Paragraph(p, width, table, env) });
                    break;
                case Table t:
                    list.Add(new BlockLayout { Block = t, Table = Table(t, width, env) });
                    break;
                case OpaqueBlock o:
                    list.Add(new BlockLayout { Block = o, BoxHeight = 40, BoxLabel = string.IsNullOrEmpty(o.Description) ? "Content kept as is" : o.Description });
                    break;
            }
        }
        return (list, StackHeight(list));
    }

    /// <summary>Blocks as they flow: content controls and multi-paragraph fields are opened up; markers dropped.</summary>
    public static IEnumerable<Block> Flatten(IEnumerable<Block> blocks)
    {
        foreach (var b in blocks)
        {
            switch (b)
            {
                case SdtBlock s:
                    foreach (var inner in Flatten(s.Blocks)) yield return inner;
                    break;
                case FieldBlock f:
                    foreach (var inner in Flatten(f.Blocks)) yield return inner;
                    break;
                case OpaqueBlock { Display: { Count: > 0 } display }:
                    foreach (var inner in Flatten(display)) yield return inner;
                    break;
                case MarkerBlock:
                    break;
                default:
                    yield return b;
                    break;
            }
        }
    }

    /// <summary>Height of stacked blocks with Word's spacing rules (space after + space before, contextual spacing).</summary>
    public static double StackHeight(List<BlockLayout> list)
    {
        var y = 0.0;
        for (var i = 0; i < list.Count; i++)
        {
            var b = list[i];
            if (b.Paragraph is { } p)
            {
                var prev = i > 0 ? list[i - 1].Paragraph : null;
                var next = i + 1 < list.Count ? list[i + 1].Paragraph : null;
                y += SpaceBefore(p, prev) + p.BorderTop + p.LinesHeight + p.BorderBottom + SpaceAfter(p, next);
            }
            else if (b.Table is { } t)
            {
                y += t.Height;
            }
            else
            {
                y += b.BoxHeight;
            }
        }
        return y;
    }

    /// <summary>Space before a paragraph, given the one above it (contextual spacing drops it between paragraphs of the same style).</summary>
    public static double SpaceBefore(ParagraphLayout p, ParagraphLayout? previous)
    {
        if (previous != null && p.Props.ContextualSpacing && previous.Props.StyleId == p.Props.StyleId) return 0;
        return p.SpaceBefore;
    }

    public static double SpaceAfter(ParagraphLayout p, ParagraphLayout? next)
    {
        if (next != null && p.Props.ContextualSpacing && next.Props.StyleId == p.Props.StyleId) return 0;
        return p.SpaceAfter;
    }

    /// <summary>
    /// Places stacked block layouts at (x, y) as fragments (page coordinates). Used for cells, headers, footers,
    /// notes and text boxes.
    /// </summary>
    public static double PlaceStack(List<BlockLayout> list, double x, double y, List<Fragment> into, TableContext? table = null)
    {
        for (var i = 0; i < list.Count; i++)
        {
            var b = list[i];
            if (b.Paragraph is { } p)
            {
                var prev = i > 0 ? list[i - 1].Paragraph : null;
                var next = i + 1 < list.Count ? list[i + 1].Paragraph : null;
                y += SpaceBefore(p, prev);
                var f = new ParaFragment(p) { X = x, Y = y, Width = p.Width, IsStart = true, IsEnd = true, Table = table };
                f.BoxTop = y;
                f.DrawTopBorder = p.Props.BorderTop != null && !SameBorders(prev, p);
                f.DrawBottomBorder = p.Props.BorderBottom != null && !SameBorders(p, next);
                if (f.DrawTopBorder) y += p.BorderTop;
                foreach (var line in p.Lines)
                {
                    var copy = line.Clone();
                    copy.Top = y;
                    f.Lines.Add(copy);
                    y += line.Height;
                }
                if (f.DrawBottomBorder) y += p.BorderBottom;
                f.BoxBottom = y;
                f.Height = y - f.Y;
                into.Add(f);
                y += SpaceAfter(p, next);
            }
            else if (b.Table is { } t)
            {
                var tf = PlaceTableWhole(t, x, y);
                into.Add(tf);
                y += t.Height;
            }
            else
            {
                into.Add(new BoxFragment(b.Block, b.BoxLabel ?? "") { X = x, Y = y, Width = 200, Height = b.BoxHeight });
                y += b.BoxHeight;
            }
        }
        return y;
    }

    /// <summary>Paragraphs with identical borders are framed together (Word's border grouping).</summary>
    public static bool SameBorders(ParagraphLayout? a, ParagraphLayout? b)
    {
        if (a == null || b == null) return false;
        var x = a.Props;
        var y = b.Props;
        return Nullable.Equals(x.BorderTop, y.BorderTop) && Nullable.Equals(x.BorderBottom, y.BorderBottom) &&
               Nullable.Equals(x.BorderLeft, y.BorderLeft) && Nullable.Equals(x.BorderRight, y.BorderRight) &&
               x.IndentStart == y.IndentStart && x.IndentEnd == y.IndentEnd;
    }

    /// <summary>All rows of a table as one fragment at (x, y).</summary>
    public static TableFragment PlaceTableWhole(TableLayout t, double x, double y)
    {
        var tf = new TableFragment(t) { X = x + t.X, Y = y, Width = t.Width, Height = t.Height };
        var rowTop = y;
        var rowFragments = new List<RowFragment>();
        foreach (var row in t.Rows)
        {
            var rf = new RowFragment { Row = row, Y = rowTop, Height = row.Height };
            rowFragments.Add(rf);
            tf.Rows.Add(rf);
            rowTop += row.Height;
        }
        for (var r = 0; r < t.Rows.Count; r++)
        {
            foreach (var cell in t.Rows[r].Cells)
            {
                if (cell.IsMergedContinuation) continue;
                var span = Math.Min(cell.RowSpan, t.Rows.Count - r);
                var height = 0.0;
                for (var k = 0; k < span; k++) height += t.Rows[r + k].Height;
                var cf = new CellFragment { Cell = cell, Box = new Rect(tf.X + cell.X, rowFragments[r].Y, cell.Width, height), RowSpan = span };
                var contentTop = rowFragments[r].Y + cell.MarginTop + VerticalOffset(cell, height);
                PlaceStack(cell.Content, tf.X + cell.X + cell.MarginLeft, contentTop, cf.Content, cell.Context);
                rowFragments[r].Cells.Add(cf);
            }
        }
        return tf;
    }

    public static double VerticalOffset(CellLayout cell, double boxHeight)
    {
        var free = boxHeight - cell.MarginTop - cell.MarginBottom - cell.ContentHeight;
        if (free <= 0) return 0;
        return cell.VerticalAlign switch
        {
            "center" => free / 2,
            "bottom" => free,
            _ => 0,
        };
    }

    // ------------------------------------------------------------------ tables

    /// <summary>The table's layout in a container of <paramref name="available"/> width (cached by version and width).</summary>
    public TableLayout Table(Table t, double available, LayoutEnv? env = null)
    {
        env ??= Env;
        var key = available.ToString("0.##", CultureInfo.InvariantCulture) + "|" + (env.Fields?.Key ?? "");
        if (_tables.TryGetValue(t, out var cached) && cached.Version == t.Version && cached.Key == key) return cached;
        var layout = BuildTable(t, available, env);
        layout.Key = key;
        layout.Version = t.Version;
        if (env.Fields == null) _tables[t] = layout;
        return layout;
    }

    private TableLayout BuildTable(Table t, double available, LayoutEnv env)
    {
        t.FixParents();
        var doc = env.Document;
        var props = t.Props;
        var style = doc.Styles.Get(props.StyleId);
        var styleTablePr = StyleTableProps(doc, props.StyleId);
        var layout = new TableLayout { Table = t, Rtl = props.BidiVisual };

        // ---- columns
        var columns = Math.Max(1, t.ColumnCount);
        var grid = t.Grid.Select(g => Units.Twips(Math.Max(0, g))).ToList();
        if (grid.Count < columns || grid.Sum() < 1)
        {
            // No usable grid: take the first row's cell widths, or share the width equally.
            var widths = new List<double>();
            var firstRow = t.Rows.FirstOrDefault();
            if (firstRow != null)
                foreach (var c in firstRow.Cells)
                {
                    var w = c.Props.Width.Resolve(available * 15) is { } tw ? Units.Twips(tw) : 0;
                    var span = c.Props.GridSpan;
                    for (var k = 0; k < span; k++) widths.Add(w / span);
                }
            if (widths.Count != columns || widths.Any(w => w <= 0)) widths = Enumerable.Repeat(available / columns, columns).ToList();
            grid = widths;
        }
        var tableWidth = props.Width;
        if (tableWidth.Type == "pct" && tableWidth.Resolve(available) is { } pctWidth && grid.Sum() > 0)
        {
            var scale = pctWidth / grid.Sum();
            grid = grid.Select(g => g * scale).ToList();
        }
        layout.ColumnEdges = new double[grid.Count + 1];
        for (var i = 0; i < grid.Count; i++) layout.ColumnEdges[i + 1] = layout.ColumnEdges[i] + grid[i];
        layout.Width = layout.ColumnEdges[grid.Count];

        var indent = (props.Indent ?? (styleTablePr?.Indent)) is { } ind && ind.Type == "dxa" ? Units.Twips(ind.Value) : 0;
        var jc = props.Alignment ?? styleTablePr?.Alignment ?? "left";
        layout.X = jc switch
        {
            "center" => (available - layout.Width) / 2,
            "right" or "end" => available - layout.Width,
            _ => indent,
        };

        // ---- default cell margins
        double Margin(string side, double fallback) =>
            props.CellMargin(side) is { } m ? Units.Twips(m) : styleTablePr?.CellMargin(side) is { } sm ? Units.Twips(sm) : fallback;
        var marginLeft = Margin("left", Units.Twips(108));
        var marginRight = Margin("right", Units.Twips(108));
        var marginTop = Margin("top", 0);
        var marginBottom = Margin("bottom", 0);

        // ---- table-level borders (direct over style)
        BorderDef? TableBorder(string side) => props.Border(side) ?? styleTablePr?.Border(side);
        var tTop = TableBorder("top");
        var tBottom = TableBorder("bottom");
        var tLeft = TableBorder("left");
        var tRight = TableBorder("right");
        var tInsideH = TableBorder("insideH");
        var tInsideV = TableBorder("insideV");

        var look = props.El("tblLook") != null ? props.Look : styleTablePr?.El("tblLook") != null ? styleTablePr.Look : props.Look;
        var rowBand = Math.Max(1, (style?.TableProps()?.IntVal("tblStyleRowBandSize")) ?? 1);
        var colBand = Math.Max(1, (style?.TableProps()?.IntVal("tblStyleColBandSize")) ?? 1);

        // Header rows: consecutive rows marked tblHeader from the top.
        layout.HeaderRows = 0;
        foreach (var row in t.Rows)
        {
            if (!row.Props.IsHeader) break;
            layout.HeaderRows++;
        }
        if (layout.HeaderRows == t.Rows.Count) layout.HeaderRows = 0; // a table of only header rows doesn't repeat

        var rowCount = t.Rows.Count;
        for (var r = 0; r < rowCount; r++)
        {
            var row = t.Rows[r];
            var rl = new RowLayout { Row = row, Index = r, CantSplit = row.Props.CantSplit, IsHeader = r < layout.HeaderRows };
            var col = row.GridBefore;
            for (var ci = 0; ci < row.Cells.Count; ci++)
            {
                var cell = row.Cells[ci];
                var span = Math.Max(1, cell.Props.GridSpan);
                if (col + span > grid.Count) span = Math.Max(1, grid.Count - col);
                var cl = new CellLayout { Cell = cell, GridColumn = col, GridSpan = span };
                var x0 = layout.ColumnEdges[Math.Min(col, grid.Count)];
                var x1 = layout.ColumnEdges[Math.Min(col + span, grid.Count)];
                cl.X = layout.Rtl ? layout.Width - x1 : x0;
                cl.Width = Math.Max(1, x1 - x0);

                // Conditional formatting that applies to this cell.
                var conditions = new List<string>();
                var firstRow = look.FirstRow && r == 0;
                var lastRow = look.LastRow && r == rowCount - 1;
                var firstCol = look.FirstColumn && col == 0;
                var lastCol = look.LastColumn && col + span >= grid.Count;
                if (look.BandedColumns && !firstCol && !lastCol)
                {
                    var c = (col - (look.FirstColumn ? 1 : 0)) / colBand;
                    conditions.Add(c % 2 == 0 ? "band1Vert" : "band2Vert");
                }
                if (look.BandedRows && !firstRow && !lastRow)
                {
                    var b = (r - (look.FirstRow ? 1 : 0)) / rowBand;
                    conditions.Add(b % 2 == 0 ? "band1Horz" : "band2Horz");
                }
                if (firstCol) conditions.Add("firstCol");
                if (lastCol) conditions.Add("lastCol");
                if (firstRow) conditions.Add("firstRow");
                if (lastRow) conditions.Add("lastRow");
                if (firstRow && firstCol) conditions.Add("nwCell");
                if (firstRow && lastCol) conditions.Add("neCell");
                if (lastRow && firstCol) conditions.Add("swCell");
                if (lastRow && lastCol) conditions.Add("seCell");
                cl.Context = new TableContext(style?.Type == "table" ? props.StyleId : null, conditions);

                // Margins: the cell's own, else the table's.
                var cp = cell.Props;
                cl.MarginLeft = cp.Margin("left") is { } ml ? Units.Twips(ml) : marginLeft;
                cl.MarginRight = cp.Margin("right") is { } mr ? Units.Twips(mr) : marginRight;
                cl.MarginTop = cp.Margin("top") is { } mt ? Units.Twips(mt) : marginTop;
                cl.MarginBottom = cp.Margin("bottom") is { } mb ? Units.Twips(mb) : marginBottom;
                cl.VerticalAlign = cp.VerticalAlignment ?? StyleCellValue(style, conditions, "vAlign") ?? "top";

                // Borders: the cell's own, then the table style's conditional ones, then the table's.
                var styleBorders = StyleCellBorders(style, conditions);
                BorderDef? Pick(string side, BorderDef? outer, BorderDef? inner, bool isOuter, out bool explicitBorder)
                {
                    var own = cp.Border(side);
                    if (own != null)
                    {
                        explicitBorder = true;
                        return own;
                    }
                    explicitBorder = false;
                    if (styleBorders.TryGetValue(side, out var sb))
                    {
                        explicitBorder = true;
                        return sb;
                    }
                    if (!isOuter && styleBorders.TryGetValue(side is "top" or "bottom" ? "insideH" : "insideV", out var si)) return si;
                    return isOuter ? outer : inner;
                }
                var leftSide = layout.Rtl ? "right" : "left";
                var rightSide = layout.Rtl ? "left" : "right";
                cl.Top = Pick("top", tTop, tInsideH, r == 0, out cl.TopExplicit);
                cl.Bottom = Pick("bottom", tBottom, tInsideH, r == rowCount - 1, out cl.BottomExplicit);
                cl.Left = Pick(leftSide, tLeft, tInsideV, col == 0, out cl.LeftExplicit);
                cl.Right = Pick(rightSide, tRight, tInsideV, col + span >= grid.Count, out cl.RightExplicit);

                // Shading: the cell's own, else the style's conditional, else the table's.
                cl.Fill = OoxColor.Resolve(cp.El("shd"), "fill", "themeFill", "themeFillTint", "themeFillShade", doc.Theme, doc.Settings)
                          ?? StyleCellFill(doc, style, conditions)
                          ?? OoxColor.Resolve(props.El("shd"), "fill", "themeFill", "themeFillTint", "themeFillShade", doc.Theme, doc.Settings);

                var vmerge = cp.VerticalMerge;
                cl.IsMergedContinuation = vmerge == "continue";
                if (!cl.IsMergedContinuation)
                {
                    var (blocks, height) = Stack(cell.Blocks, cl.ContentWidth, cl.Context, env);
                    cl.Content.AddRange(blocks);
                    cl.ContentHeight = height;
                }
                rl.Cells.Add(cl);
                col += span;
            }
            layout.Rows.Add(rl);
        }

        // ---- vertical merges: how many rows each restart cell spans
        for (var r = 0; r < layout.Rows.Count; r++)
        {
            foreach (var cell in layout.Rows[r].Cells)
            {
                if (cell.Cell.Props.VerticalMerge != "restart") continue;
                var span = 1;
                for (var k = r + 1; k < layout.Rows.Count; k++)
                {
                    var below = layout.Rows[k].Cells.FirstOrDefault(c => c.GridColumn == cell.GridColumn);
                    if (below == null || !below.IsMergedContinuation) break;
                    span++;
                }
                cell.RowSpan = span;
            }
        }

        // ---- row heights
        foreach (var rl in layout.Rows)
        {
            var h = 0.0;
            foreach (var c in rl.Cells)
                if (!c.IsMergedContinuation && c.RowSpan == 1) h = Math.Max(h, c.ContentHeight + c.MarginTop + c.MarginBottom);
            if (rl.Cells.All(c => c.IsMergedContinuation || c.RowSpan > 1))
            {
                // Only merged cells: at least one empty line's height.
                h = Math.Max(h, rl.Cells.Where(c => !c.IsMergedContinuation).Select(c => c.MarginTop + c.MarginBottom + 16).DefaultIfEmpty(16).Max());
            }
            if (rl.Row.Props.Height is { } spec)
            {
                var specified = Units.Twips(spec.Height);
                if (spec.Rule == "exact")
                {
                    h = specified;
                    rl.ExactHeight = specified;
                }
                else h = Math.Max(h, specified);
            }
            rl.Height = Math.Max(1, h);
        }
        for (var r = 0; r < layout.Rows.Count; r++)
        {
            foreach (var c in layout.Rows[r].Cells)
            {
                if (c.RowSpan <= 1) continue;
                var last = Math.Min(layout.Rows.Count, r + c.RowSpan) - 1;
                var sum = 0.0;
                for (var k = r; k <= last; k++) sum += layout.Rows[k].Height;
                var need = c.ContentHeight + c.MarginTop + c.MarginBottom;
                if (need > sum && layout.Rows[last].ExactHeight <= 0) layout.Rows[last].Height += need - sum;
            }
        }
        return layout;
    }

    private static TableProps? StyleTableProps(Document doc, string? styleId)
    {
        // The nearest tblPr up the table style chain (merged roughly: first one found per property is enough for borders / margins).
        foreach (var s in doc.Styles.Chain(styleId))
            if (s.Type == "table" && s.TableProps() is { } tp) return MergeChain(doc, styleId) ?? tp;
        return null;
    }

    private static TableProps? MergeChain(Document doc, string? styleId)
    {
        var chain = doc.Styles.Chain(styleId).Where(s => s.Type == "table").Reverse().ToList();
        if (chain.Count == 0) return null;
        var merged = new TableProps();
        foreach (var s in chain)
        {
            var tp = s.TableProps();
            if (tp == null) continue;
            foreach (var e in tp.X.Elements())
            {
                var local = e.Name.LocalName;
                if (local is "tblBorders" or "tblCellMar")
                {
                    var box = merged.Ensure(local);
                    foreach (var side in e.Elements())
                    {
                        box.Elements(side.Name).Remove();
                        box.Add(new XElement(side));
                    }
                }
                else
                {
                    merged.SetElement(local, new XElement(e));
                }
            }
        }
        return merged;
    }

    private static string? StyleCellValue(Model.Style? style, List<string> conditions, string name)
    {
        if (style == null) return null;
        string? value = style.CellProps()?.Val(name);
        foreach (var c in conditions)
            foreach (var (type, x) in style.Conditional)
                if (type == c && x.Element(Ns.W + "tcPr")?.Element(Ns.W + name).Attr("val") is { } v) value = v;
        return value;
    }

    private static Dictionary<string, BorderDef> StyleCellBorders(Model.Style? style, List<string> conditions)
    {
        var result = new Dictionary<string, BorderDef>(StringComparer.Ordinal);
        if (style == null) return result;
        void Take(XElement? borders)
        {
            if (borders == null) return;
            foreach (var e in borders.Elements())
            {
                var side = e.Name.LocalName switch { "start" => "left", "end" => "right", var n => n };
                if (BorderDef.Parse(e) is { } b) result[side] = b;
            }
        }
        Take(style.CellProps()?.El("tcBorders"));
        foreach (var c in conditions)
            foreach (var (type, x) in style.Conditional)
                if (type == c) Take(x.Element(Ns.W + "tcPr")?.Element(Ns.W + "tcBorders"));
        return result;
    }

    private static Color? StyleCellFill(Document doc, Model.Style? style, List<string> conditions)
    {
        if (style == null) return null;
        Color? fill = OoxColor.Resolve(style.CellProps()?.El("shd"), "fill", "themeFill", "themeFillTint", "themeFillShade", doc.Theme, doc.Settings);
        foreach (var c in conditions)
            foreach (var (type, x) in style.Conditional)
                if (type == c && OoxColor.Resolve(x.Element(Ns.W + "tcPr")?.Element(Ns.W + "shd"), "fill", "themeFill", "themeFillTint", "themeFillShade", doc.Theme, doc.Settings) is { } f)
                    fill = f;
        return fill;
    }
}
