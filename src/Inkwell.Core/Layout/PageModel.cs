using System.Windows;
using System.Windows.Media;
using Inkwell.Core.Model;
using Inkwell.Core.Styles;

namespace Inkwell.Core.Layout;

/// <summary>A positioned piece of a block on a page (page coordinates, DIPs).</summary>
public abstract class Fragment
{
    public double X;
    public double Y;
    public double Width;
    public double Height;

    public abstract Block Block { get; }

    public Rect Bounds => new(X, Y, Width, Height);

    /// <summary>Moves the fragment (and everything inside it) down by <paramref name="dy"/>.</summary>
    public abstract void Offset(double dx, double dy);
}

/// <summary>Some (or all) lines of a paragraph.</summary>
public sealed class ParaFragment : Fragment
{
    public ParaFragment(ParagraphLayout layout)
    {
        Layout = layout;
    }

    public ParagraphLayout Layout { get; }

    /// <summary>The lines shown here; each line's <see cref="LineInfo.Top"/> is in page coordinates, X relative to <see cref="Fragment.X"/>.</summary>
    public List<LineInfo> Lines { get; } = new();

    /// <summary>Index of the first line here among the paragraph's lines.</summary>
    public int FirstLineIndex;

    /// <summary>The paragraph's first / last line is in this fragment.</summary>
    public bool IsStart;
    public bool IsEnd;

    /// <summary>Box for shading and borders (page coordinates).</summary>
    public double BoxTop;
    public double BoxBottom;
    public bool DrawTopBorder;
    public bool DrawBottomBorder;

    /// <summary>The table context the paragraph was laid out with (cells).</summary>
    public TableContext? Table;

    public override Block Block => Layout.Paragraph;

    public Paragraph Paragraph => Layout.Paragraph;

    public override void Offset(double dx, double dy)
    {
        X += dx;
        Y += dy;
        BoxTop += dy;
        BoxBottom += dy;
        foreach (var l in Lines) l.Top += dy;
    }
}

/// <summary>Some rows of a table.</summary>
public sealed class TableFragment : Fragment
{
    public TableFragment(TableLayout layout)
    {
        Layout = layout;
    }

    public TableLayout Layout { get; }

    public List<RowFragment> Rows { get; } = new();

    public override Block Block => Layout.Table;

    public override void Offset(double dx, double dy)
    {
        X += dx;
        Y += dy;
        foreach (var r in Rows)
        {
            r.Y += dy;
            foreach (var c in r.Cells)
            {
                c.Box = new Rect(c.Box.X + dx, c.Box.Y + dy, c.Box.Width, c.Box.Height);
                foreach (var f in c.Content) f.Offset(dx, dy);
            }
        }
    }
}

/// <summary>A table row on a page (possibly part of a row split across pages, or a repeated header row).</summary>
public sealed class RowFragment
{
    public RowLayout Row = null!;
    public double Y;
    public double Height;
    public bool IsRepeatedHeader;
    public bool ContinuesFromPrevious;
    public bool ContinuesOnNext;
    public List<CellFragment> Cells { get; } = new();
}

/// <summary>A cell on a page: its box and the fragments of its content.</summary>
public sealed class CellFragment
{
    public CellLayout Cell = null!;
    public Rect Box;
    public List<Fragment> Content { get; } = new();

    /// <summary>For vertically merged cells: the box spans this many row fragments.</summary>
    public int RowSpan = 1;
}

/// <summary>A placeholder block (a preserved element shown read-only).</summary>
public sealed class BoxFragment : Fragment
{
    public BoxFragment(Block block, string label)
    {
        BlockRef = block;
        Label = label;
    }

    public Block BlockRef { get; }
    public string Label { get; }

    public override Block Block => BlockRef;

    public override void Offset(double dx, double dy)
    {
        X += dx;
        Y += dy;
    }
}

/// <summary>A floating drawing placed on a page.</summary>
public sealed class FloatPlacement
{
    public DrawingObject Drawing = null!;
    public Paragraph Anchor = null!;
    public int AnchorOffset;

    /// <summary>The drawing's box and its wrap box (box plus distances), page coordinates.</summary>
    public Rect Box;
    public Rect WrapBox;

    public bool Wraps => Drawing.Wrap is WrapMode.Square or WrapMode.Tight or WrapMode.TopAndBottom;
    public bool Behind => Drawing.Wrap == WrapMode.Behind;

    /// <summary>Header / footer floats are drawn with the header.</summary>
    public bool InHeaderFooter;
}

/// <summary>A column on a page: its box, its content, and its footnotes.</summary>
public sealed class ColumnArea
{
    public Rect Box;
    public List<Fragment> Content { get; } = new();
    public List<Fragment> Notes { get; } = new();
    public double NotesTop;
    public bool HasNotes => Notes.Count > 0;
    public bool NotesContinued;
}

/// <summary>Where layout resumes: a block of the flow and a line (paragraphs) or row (tables) inside it.</summary>
public readonly struct FlowCursor : IEquatable<FlowCursor>
{
    public FlowCursor(int index, int pos, int row = 0, CellSplit? split = null)
    {
        Index = index;
        Pos = pos;
        Row = row;
        Split = split;
    }

    /// <summary>Index in the flattened flow of blocks.</summary>
    public int Index { get; }

    /// <summary>Source position (TextFormatter index) of the next line of a paragraph; 0 = not started.</summary>
    public int Pos { get; }

    /// <summary>Next row of a table.</summary>
    public int Row { get; }

    /// <summary>A table row split across pages: how far each cell got.</summary>
    public CellSplit? Split { get; }

    public bool Equals(FlowCursor other) => Index == other.Index && Pos == other.Pos && Row == other.Row && Equals(Split, other.Split);

    public override bool Equals(object? obj) => obj is FlowCursor c && Equals(c);

    public override int GetHashCode() => Index * 397 ^ Pos * 31 ^ Row;

    public override string ToString() => $"{Index}:{Pos}:{Row}{(Split != null ? "*" : "")}";
}

/// <summary>Progress inside a split table row: per cell, the content block and line to resume from.</summary>
public sealed class CellSplit : IEquatable<CellSplit>
{
    public CellSplit((int Block, int Line)[] positions)
    {
        Positions = positions;
    }

    public (int Block, int Line)[] Positions { get; }

    public bool Equals(CellSplit? other) => other != null && Positions.SequenceEqual(other.Positions);

    public override bool Equals(object? obj) => Equals(obj as CellSplit);

    public override int GetHashCode() => Positions.Length;
}

/// <summary>One page: geometry, header and footer, columns with their content and notes, floating drawings.</summary>
public sealed class PageLayout
{
    public int Index;

    /// <summary>The page number shown (sections can restart numbering) and its text.</summary>
    public int Number;
    public string NumberText = "1";

    public Size Size;
    public Thickness Margins;
    public SectProps Section = null!;
    public int SectionIndex;

    /// <summary>The page is the first of its section.</summary>
    public bool FirstOfSection;

    public Rect BodyRect;
    public List<ColumnArea> Columns { get; } = new();

    /// <summary>Lines between columns.</summary>
    public List<(Point From, Point To)> ColumnSeparators { get; } = new();

    public List<FloatPlacement> Floats { get; } = new();

    public HeaderFooter? HeaderStory;
    public HeaderFooter? FooterStory;
    public List<Fragment> Header { get; } = new();
    public List<Fragment> Footer { get; } = new();
    public Rect HeaderRect;
    public Rect FooterRect;

    /// <summary>A blank page inserted so a section starts on an odd / even page.</summary>
    public bool IsBlank;

    /// <summary>Where the page's content starts and ends in the flow.</summary>
    public FlowCursor Start;
    public FlowCursor End;

    /// <summary>The page ends with a manual page break / section break (for markers).</summary>
    public string? EndMarker;

    /// <summary>Endnotes are laid out on this page (after the body).</summary>
    public bool HasEndnotes;

    /// <summary>Everything in the body of the page, column by column (content, then notes).</summary>
    public IEnumerable<Fragment> AllBodyFragments => Columns.SelectMany(c => c.Content.Concat(c.Notes));

    /// <summary>Unique stamp for caching the rendered page.</summary>
    public int Stamp;
}

/// <summary>Visual paper colours for page view.</summary>
public static class PaperColors
{
    public static Color Paper(bool dark) => dark ? Color.FromRgb(0x1E, 0x1F, 0x22) : Colors.White;
}
