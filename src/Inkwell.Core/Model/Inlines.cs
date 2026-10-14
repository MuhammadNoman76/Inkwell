using System.Xml.Linq;

namespace Inkwell.Core.Model;

/// <summary>A hyperlink around runs (w:hyperlink). Consecutive inlines sharing one instance form one link.</summary>
public sealed class Hyperlink
{
    /// <summary>External address (http, https, mailto, file…), or null for a link inside the document.</summary>
    public string? Url { get; set; }

    /// <summary>Bookmark name for a link inside the document.</summary>
    public string? Anchor { get; set; }

    public string? Tooltip { get; set; }

    /// <summary>The original w:hyperlink element without its content (other attributes such as w:history, w:tgtFrame).</summary>
    public XElement? Shell { get; set; }

    public Hyperlink Clone() => new() { Url = Url, Anchor = Anchor, Tooltip = Tooltip, Shell = Shell.Copy() };

    public bool SameTarget(Hyperlink? other) =>
        other != null && string.Equals(Url, other.Url, StringComparison.Ordinal) && string.Equals(Anchor, other.Anchor, StringComparison.Ordinal);
}

/// <summary>
/// Something inside a paragraph. Text is stored in <see cref="TextRun"/>s using Word's own control characters:
/// tab \t, line break \v (U+000B), page break \f (U+000C), column break U+000E, soft hyphen U+00AD and
/// non-breaking hyphen U+2011. Everything else is an object (length 1) or a marker (length 0).
/// </summary>
public abstract class Inline
{
    /// <summary>The run properties (w:rPr) of the run that holds this item.</summary>
    public RunProps Props { get; set; } = new();

    /// <summary>Tracked insertion (w:ins / w:moveTo around the run).</summary>
    public RevisionInfo? Inserted { get; set; }

    /// <summary>Tracked deletion (w:del / w:moveFrom around the run).</summary>
    public RevisionInfo? Deleted { get; set; }

    /// <summary>The insertion / deletion was a move (w:moveTo / w:moveFrom).</summary>
    public bool IsMove { get; set; }

    public Hyperlink? Link { get; set; }

    /// <summary>Length in document positions: text length, 1 for objects, 0 for markers.</summary>
    public abstract int Length { get; }

    public abstract Inline CloneCore();

    /// <summary>A deep copy (the hyperlink instance is shared so the copy stays in the same link; callers re-link if needed).</summary>
    public Inline Clone()
    {
        var c = CloneCore();
        c.Props = Props.Clone();
        c.Inserted = Inserted?.Clone();
        c.Deleted = Deleted?.Clone();
        c.IsMove = IsMove;
        c.Link = Link;
        return c;
    }

    /// <summary>Same formatting, revision marks and link: two such text runs can be merged.</summary>
    public bool CanMergeWith(Inline other) =>
        ReferenceEquals(Link, other.Link) && SameRevision(Inserted, other.Inserted) && SameRevision(Deleted, other.Deleted) &&
        IsMove == other.IsMove && Props.SameFormatting(other.Props);

    private static bool SameRevision(RevisionInfo? a, RevisionInfo? b) =>
        a == null ? b == null : b != null && a.Author == b.Author && a.Date == b.Date;
}

/// <summary>Plain text (w:t), possibly with tabs and breaks as control characters (see <see cref="Inline"/>).</summary>
public sealed class TextRun : Inline
{
    public TextRun(string text = "")
    {
        Text = text;
    }

    public string Text { get; set; }

    public override int Length => Text.Length;

    public override Inline CloneCore() => new TextRun(Text);

    public override string ToString() => Text;

    public const char Tab = '\t';
    public const char LineBreak = '\v';
    public const char PageBreak = '\f';
    public const char ColumnBreak = '\u000E';
    public const char SoftHyphen = '­';
    public const char NoBreakHyphen = '‑';

    public static bool IsBreak(char c) => c is LineBreak or PageBreak or ColumnBreak;
}

/// <summary>A line break that clears floating objects (w:br w:clear="left|right|all").</summary>
public sealed class ClearBreak : Inline
{
    public string Clear { get; set; } = "all";

    public override int Length => 1;

    public override Inline CloneCore() => new ClearBreak { Clear = Clear };
}

/// <summary>A symbol character from a particular font (w:sym).</summary>
public sealed class SymbolChar : Inline
{
    public string Font { get; set; } = "Symbol";

    /// <summary>The character code (often in the U+F000 private range for symbol fonts).</summary>
    public char Char { get; set; }

    public override int Length => 1;

    public override Inline CloneCore() => new SymbolChar { Font = Font, Char = Char };
}

/// <summary>How a picture sits in the text.</summary>
public enum WrapMode
{
    /// <summary>In line with text (wp:inline).</summary>
    Inline,
    /// <summary>Text wraps around the picture's box (wrapSquare).</summary>
    Square,
    /// <summary>Wraps tightly (wrapTight / wrapThrough); laid out like Square.</summary>
    Tight,
    /// <summary>Text above and below only (wrapTopAndBottom).</summary>
    TopAndBottom,
    /// <summary>Behind the text (wrapNone + behindDoc).</summary>
    Behind,
    /// <summary>In front of the text (wrapNone).</summary>
    InFront,
}

/// <summary>Where a floating object is positioned from (wp:positionH/V relativeFrom) and its offset or alignment.</summary>
public sealed class FloatPosition
{
    /// <summary>page, margin, column, character, leftMargin, rightMargin, insideMargin, outsideMargin (horizontal);
    /// page, margin, paragraph, line, topMargin, bottomMargin, insideMargin, outsideMargin (vertical).</summary>
    public string RelativeFrom { get; set; } = "column";

    /// <summary>Offset in EMU, when <see cref="Align"/> is null.</summary>
    public long Offset { get; set; }

    /// <summary>left, center, right, inside, outside / top, center, bottom, inside, outside.</summary>
    public string? Align { get; set; }

    public FloatPosition Clone() => new() { RelativeFrom = RelativeFrom, Offset = Offset, Align = Align };
}

/// <summary>A floating object's anchor settings (wp:anchor).</summary>
public sealed class FloatInfo
{
    public FloatPosition Horizontal { get; set; } = new();
    public FloatPosition Vertical { get; set; } = new() { RelativeFrom = "paragraph" };

    /// <summary>Distances from the text in EMU (distT, distB, distL, distR).</summary>
    public long DistTop { get; set; }
    public long DistBottom { get; set; }
    public long DistLeft { get; set; } = 114300;
    public long DistRight { get; set; } = 114300;

    /// <summary>bothSides, left, right, largest (square and tight wrapping).</summary>
    public string WrapSide { get; set; } = "bothSides";

    /// <summary>Stacking order (relativeHeight).</summary>
    public long ZOrder { get; set; } = 251659264;

    /// <summary>Lock anchor (wp:anchor locked="1"): Inkwell also locks the position against dragging.</summary>
    public bool Locked { get; set; }

    public bool AllowOverlap { get; set; } = true;
    public bool LayoutInCell { get; set; } = true;

    public FloatInfo Clone() => new()
    {
        Horizontal = Horizontal.Clone(),
        Vertical = Vertical.Clone(),
        DistTop = DistTop,
        DistBottom = DistBottom,
        DistLeft = DistLeft,
        DistRight = DistRight,
        WrapSide = WrapSide,
        ZOrder = ZOrder,
        Locked = Locked,
        AllowOverlap = AllowOverlap,
        LayoutInCell = LayoutInCell,
    };
}

/// <summary>
/// A drawing (w:drawing): a picture Inkwell can edit (size, wrap, position, alt text), or another kind of
/// drawing (chart, SmartArt, shape, text box, canvas) that is kept exactly as it was and shown read-only.
/// </summary>
public sealed class DrawingObject : Inline
{
    /// <summary>The picture's bytes (null for drawings that aren't pictures).</summary>
    public string? MediaId { get; set; }

    /// <summary>Size in EMU (914400 per inch).</summary>
    public long Width { get; set; }
    public long Height { get; set; }

    public WrapMode Wrap { get; set; } = WrapMode.Inline;

    /// <summary>Anchor settings when floating; null for in-line pictures.</summary>
    public FloatInfo? Float { get; set; }

    /// <summary>Alternative text (wp:docPr descr) and title.</summary>
    public string? AltText { get; set; }
    public string? Title { get; set; }
    public string Name { get; set; } = "Picture";

    /// <summary>What it is: picture, chart, smartart, shape, textbox, group, canvas, ink, other.</summary>
    public string Kind { get; set; } = "picture";

    /// <summary>
    /// The original XML (w:drawing, or mc:AlternateContent / w:pict / w:object). Written back unchanged for
    /// drawings Inkwell doesn't edit; for pictures, the writer patches the size, wrap and position into it so
    /// effects, crops and other settings survive.
    /// </summary>
    public XElement? Original { get; set; }

    /// <summary>The part the original XML came from (its relationship ids belong there).</summary>
    public string? SourcePart { get; set; }

    /// <summary>Paragraphs inside a text box, for display only (read-only).</summary>
    public List<Block>? TextBoxContent { get; set; }

    public bool IsPicture => Kind == "picture" && MediaId != null;

    public bool IsFloating => Wrap != WrapMode.Inline;

    /// <summary>Floating drawings are anchors (length 0); in-line ones take one position.</summary>
    public override int Length => IsFloating ? 0 : 1;

    public double WidthPoints => Width / 12700.0;
    public double HeightPoints => Height / 12700.0;

    public override Inline CloneCore() => new DrawingObject
    {
        MediaId = MediaId,
        Width = Width,
        Height = Height,
        Wrap = Wrap,
        Float = Float?.Clone(),
        AltText = AltText,
        Title = Title,
        Name = Name,
        Kind = Kind,
        Original = Original.Copy(),
        SourcePart = SourcePart,
        TextBoxContent = TextBoxContent?.Select(b => b.CloneBlock()).ToList(),
    };
}

/// <summary>Field kinds Inkwell computes or understands.</summary>
public enum FieldKind
{
    Other, Page, NumPages, SectionPages, Date, Time, CreateDate, SaveDate, PrintDate, Ref, PageRef, NoteRef, Seq, StyleRef, Hyperlink,
    Citation, Toc, Bibliography, Title, Author, Subject, FileName, DocProperty, NumWords, NumChars, Symbol, Index, MergeField, If, FormText,
}

/// <summary>
/// A field kept as one object (w:fldSimple or w:fldChar begin…end inside one paragraph): its code and its last
/// result. Computed fields (PAGE, NUMPAGES, REF, SEQ…) are updated by Inkwell; the result is what's shown.
/// </summary>
public sealed class FieldInline : Inline
{
    /// <summary>The field code without braces, e.g. " PAGE \* MERGEFORMAT ".</summary>
    public string Code { get; set; } = "";

    /// <summary>The result as runs.</summary>
    public List<Inline> Result { get; set; } = new();

    /// <summary>w:dirty: update before showing.</summary>
    public bool Dirty { get; set; }

    /// <summary>w:fldLock: never update.</summary>
    public bool Locked { get; set; }

    /// <summary>The field had nested fields in its code: it is shown and saved as it was (read-only).</summary>
    public XElement? PreservedXml { get; set; }

    /// <summary>Properties of the run with the begin character (w:fldChar begin), if any.</summary>
    public RunProps? BeginProps { get; set; }

    /// <summary>Content control (w:sdt) properties when the field sits in one (citations).</summary>
    public XElement? SdtProperties { get; set; }

    /// <summary>Saved as w:fldSimple (rather than w:fldChar runs).</summary>
    public bool Simple { get; set; }

    public override int Length => 1;

    public FieldKind Kind => FieldCode.KindOf(Code);

    public string ResultText => string.Concat(Result.OfType<TextRun>().Select(r => r.Text));

    public override Inline CloneCore() => new FieldInline
    {
        Code = Code,
        Result = Result.Select(r => r.Clone()).ToList(),
        Dirty = Dirty,
        Locked = Locked,
        PreservedXml = PreservedXml.Copy(),
        BeginProps = BeginProps?.Clone(),
        SdtProperties = SdtProperties.Copy(),
        Simple = Simple,
    };
}

/// <summary>Footnote or endnote.</summary>
public enum NoteKind { Footnote, Endnote }

/// <summary>A footnote / endnote reference in the text (w:footnoteReference / w:endnoteReference).</summary>
public sealed class NoteReference : Inline
{
    public NoteKind Kind { get; set; }
    public int Id { get; set; }

    /// <summary>w:customMarkFollows: the mark is the text that follows instead of a number.</summary>
    public bool CustomMark { get; set; }

    public override int Length => 1;

    public override Inline CloneCore() => new NoteReference { Kind = Kind, Id = Id, CustomMark = CustomMark };
}

/// <summary>The number at the start of a note's own text (w:footnoteRef / w:endnoteRef).</summary>
public sealed class NoteMark : Inline
{
    public NoteKind Kind { get; set; }

    public override int Length => 1;

    public override Inline CloneCore() => new NoteMark { Kind = Kind };
}

/// <summary>The separator line in the special separator notes (w:separator / w:continuationSeparator).</summary>
public sealed class NoteSeparator : Inline
{
    public bool Continuation { get; set; }

    public override int Length => 1;

    public override Inline CloneCore() => new NoteSeparator { Continuation = Continuation };
}

/// <summary>Start or end of a bookmark (w:bookmarkStart / w:bookmarkEnd).</summary>
public sealed class BookmarkMarker : Inline
{
    public int Id { get; set; }

    /// <summary>Name, on the start marker.</summary>
    public string? Name { get; set; }

    public bool IsStart { get; set; }

    /// <summary>Other attributes (w:colFirst, w:colLast…).</summary>
    public XElement? Original { get; set; }

    public override int Length => 0;

    public override Inline CloneCore() => new BookmarkMarker { Id = Id, Name = Name, IsStart = IsStart, Original = Original.Copy() };
}

/// <summary>Start or end of a commented range (w:commentRangeStart / End), or the reference run (w:commentReference).</summary>
public sealed class CommentMarker : Inline
{
    public int Id { get; set; }

    /// <summary>RangeStart, RangeEnd or Reference.</summary>
    public CommentMarkerKind Kind { get; set; }

    public override int Length => 0;

    public override Inline CloneCore() => new CommentMarker { Id = Id, Kind = Kind };
}

public enum CommentMarkerKind { RangeStart, RangeEnd, Reference }

/// <summary>The mark at the start of a comment's own text (w:annotationRef).</summary>
public sealed class CommentMark : Inline
{
    public override int Length => 0;

    public override Inline CloneCore() => new CommentMark();
}

/// <summary>
/// Something Inkwell keeps exactly as it was but doesn't edit: an equation (m:oMath), a content control
/// (w:sdt), ruby text, a VML picture or embedded object, a permission range… Shown as a read-only piece
/// with its text, or a placeholder.
/// </summary>
public sealed class OpaqueInline : Inline
{
    public OpaqueInline(XElement xml, string kind, string display)
    {
        Xml = xml;
        Kind = kind;
        Display = display;
    }

    public XElement Xml { get; set; }

    /// <summary>equation, contentControl, ruby, vml, object, permission, moveRange, other…</summary>
    public string Kind { get; set; }

    /// <summary>Text shown for it (the equation's text, the control's content); empty for markers.</summary>
    public string Display { get; set; }

    /// <summary>The part its relationship ids belong to.</summary>
    public string? SourcePart { get; set; }

    /// <summary>Markers (permission ranges, move ranges) take no position.</summary>
    public bool IsMarker { get; set; }

    /// <summary>Placeholder size in points for things with a size (VML pictures, objects); 0 = text only.</summary>
    public double WidthPoints { get; set; }
    public double HeightPoints { get; set; }

    public override int Length => IsMarker ? 0 : 1;

    public override Inline CloneCore() => new OpaqueInline(new XElement(Xml), Kind, Display)
    {
        SourcePart = SourcePart,
        IsMarker = IsMarker,
        WidthPoints = WidthPoints,
        HeightPoints = HeightPoints,
    };
}
