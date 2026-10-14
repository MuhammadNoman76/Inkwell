using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Inkwell.Core.Model;

namespace Inkwell.Core.IO;

// Blocks, paragraphs, runs and fields.
//
// A paragraph is first turned into a list of tokens (one per run child or paragraph-level element), each knowing
// its run properties and the wrappers around it (hyperlink, ins, del, moveTo, moveFrom). Field characters stay as
// tokens. Then, for each block container (body, cell, content control, note…), field begin/separate/end tokens are
// matched across its paragraphs:
//  - fields inside one paragraph become FieldInline (nested fields in the result nest; fields with nested fields
//    in their code, form fields and fields with field data are kept as exact XML in PreservedXml);
//  - fields spanning paragraphs become a FieldBlock holding those paragraphs (BeginAt / EndAt = inline indexes),
//    or, when their code itself spans paragraphs or holds nested fields, an OpaqueBlock with the exact XML;
//  - fields that don't close in their container (for example begin in a table cell, end after the table) lose
//    their field characters and keep their result as ordinary content, with a warning.
internal sealed partial class DocxReadSession
{
    private const string CustomXmlWarning = "Custom XML markup (smart tags and custom XML elements) was removed from the text; its content was kept. Word removes this markup too.";
    private const string BidiWarning = "Bidirectional embedding markup (w:dir / w:bdo) was removed from the text; its content was kept.";
    private const string BrokenFieldWarning = "A field in the document had a damaged or unusual structure; its result was kept as ordinary text.";

    private class RawBlock
    {
        public RawBlock(Block? block, XElement? source)
        {
            Block = block;
            if (source != null) Sources.Add(source);
        }

        public Block? Block { get; }

        /// <summary>The XML it was read from (several elements for merged markers or unwrapped custom XML).</summary>
        public List<XElement> Sources { get; } = new();
    }

    private sealed class ParaRaw : RawBlock
    {
        public ParaRaw(XElement? source, ParaProps props, XElement? shell)
            : base(null, source)
        {
            Props = props;
            Shell = shell;
        }

        public ParaProps Props { get; }
        public XElement? Shell { get; }
        public List<Tok> Toks { get; } = new();

        /// <summary>Multi-paragraph fields that begin here, in order.</summary>
        public List<FieldSpan>? MultiBegins { get; set; }
    }

    /// <summary>A wrapper around runs: w:hyperlink, w:ins, w:del, w:moveTo, w:moveFrom (with the effective values).</summary>
    private sealed class Wrap
    {
        public Wrap(XElement source, Wrap? parent, Hyperlink? link, RevisionInfo? ins, RevisionInfo? del, bool move)
        {
            Source = source;
            Parent = parent;
            Depth = parent == null ? 1 : parent.Depth + 1;
            Link = link ?? parent?.Link;
            Ins = ins ?? parent?.Ins;
            Del = del ?? parent?.Del;
            IsMove = move || (parent?.IsMove ?? false);
        }

        public XElement Source { get; }
        public Wrap? Parent { get; }
        public int Depth { get; }
        public Hyperlink? Link { get; }
        public RevisionInfo? Ins { get; }
        public RevisionInfo? Del { get; }
        public bool IsMove { get; }
    }

    /// <summary>The run a token came from (null Run for paragraph-level elements) and its wrappers.</summary>
    private sealed class RunCtx
    {
        public RunCtx(Wrap? wrap, XElement? run, XElement? rPr)
        {
            Wrap = wrap;
            Run = run;
            RPr = rPr;
        }

        public Wrap? Wrap { get; }
        public XElement? Run { get; }
        public XElement? RPr { get; }

        public RunProps NewProps() => RPr != null ? new RunProps(new XElement(RPr)) : new RunProps();

        public void Apply(Inline inline)
        {
            if (Wrap == null) return;
            inline.Link = Wrap.Link;
            inline.Inserted = Wrap.Ins?.Clone();
            inline.Deleted = Wrap.Del?.Clone();
            inline.IsMove = Wrap.IsMove;
        }
    }

    private enum TokKind : byte { Text, Inline, Begin, Instr, Sep, End }

    private sealed class Tok
    {
        public Tok(TokKind kind, RunCtx ctx, XElement child)
        {
            Kind = kind;
            Ctx = ctx;
            Child = child;
        }

        public TokKind Kind { get; }
        public RunCtx Ctx { get; }

        /// <summary>The element it came from (run child, or paragraph-level element).</summary>
        public XElement Child { get; }

        public string? Text { get; set; }
        public Inline? Inline { get; set; }
        public FieldSpan? Field { get; set; }

        /// <summary>A field character with no matching field (dropped).</summary>
        public bool Stray { get; set; }

        public bool IsContent => Kind == TokKind.Text || Kind == TokKind.Inline && Inline is { } i && (i.Length > 0 || i is DrawingObject);
    }

    private sealed class FieldSpan
    {
        public FieldSpan(Tok begin, int beginPara, FieldSpan? parent)
        {
            Begin = begin;
            BeginPara = beginPara;
            Parent = parent;
        }

        public Tok Begin { get; }
        public int BeginPara { get; }
        public FieldSpan? Parent { get; }
        public Tok? Sep { get; set; }
        public int SepPara { get; set; } = -1;
        public Tok? End { get; set; }
        public int EndPara { get; set; } = -1;
        public bool NestedInCode { get; set; }
        public bool ContentInCode { get; set; }
        public bool Broken { get; set; }
        public bool Opaque { get; set; }
        public bool InCode => Sep == null;
        public bool IsMulti => !Broken && End != null && EndPara != BeginPara;
        public int BeginAt { get; set; }
        public int EndAt { get; set; }
        public StringBuilder Code { get; } = new();
    }

    // ------------------------------------------------------------------ blocks

    private List<Block> ParseBlocks(IEnumerable<XElement> elements)
    {
        var raws = new List<RawBlock>();
        CollectBlocks(elements, raws);
        return AssembleContainer(raws);
    }

    private void CollectBlocks(IEnumerable<XElement> elements, List<RawBlock> raws)
    {
        foreach (var e in elements)
        {
            var name = e.Name;
            if (name == Wn.P)
            {
                raws.Add(ReadParagraph(e));
            }
            else if (name == Wn.Tbl)
            {
                var table = ReadTable(e);
                if (table != null) raws.Add(new RawBlock(table, e));
                else Warn("An empty table (with no rows), which Word can't open, was removed.");
            }
            else if (name == Wn.Sdt)
            {
                raws.Add(new RawBlock(ReadBlockSdt(e), e));
            }
            else if (name == Wn.CustomXml)
            {
                Warn(CustomXmlWarning);
                CollectBlocks(e.Elements().Where(c => c.Name != Wn.CustomXmlPr), raws);
            }
            else if (name == Wn.ProofErr || name == Wn.SectPr)
            {
                // spelling marks are recomputed; a stray sectPr outside the end of the body is meaningless
            }
            else if (IsRangeMarker(name))
            {
                AddMarker(raws, e);
            }
            else if (name == Wn.AltChunk)
            {
                Tally("altChunk");
                raws.Add(new RawBlock(new OpaqueBlock(new XElement(e), "altChunk") { SourcePart = _part.Uri, Description = "Imported content" }, e));
            }
            else if (name == Wn.OMathPara || name == Wn.OMath)
            {
                Tally("equation");
                var display = new Paragraph();
                display.Inlines.Add(new OpaqueInline(new XElement(e), "equation", DocxXml.VisibleText(e)) { SourcePart = _part.Uri });
                raws.Add(new RawBlock(new OpaqueBlock(new XElement(e), "equation") { SourcePart = _part.Uri, Display = new List<Block> { display }, Description = "Equation" }, e));
            }
            else
            {
                raws.Add(new RawBlock(new OpaqueBlock(new XElement(e), "other") { SourcePart = _part.Uri, Description = e.Name.LocalName }, e));
            }
        }
    }

    private static bool IsRangeMarker(XName name)
    {
        if (name.Namespace != Ns.W) return false;
        switch (name.LocalName)
        {
            case "bookmarkStart":
            case "bookmarkEnd":
            case "commentRangeStart":
            case "commentRangeEnd":
            case "permStart":
            case "permEnd":
            case "moveFromRangeStart":
            case "moveFromRangeEnd":
            case "moveToRangeStart":
            case "moveToRangeEnd":
            case "customXmlInsRangeStart":
            case "customXmlInsRangeEnd":
            case "customXmlDelRangeStart":
            case "customXmlDelRangeEnd":
            case "customXmlMoveFromRangeStart":
            case "customXmlMoveFromRangeEnd":
            case "customXmlMoveToRangeStart":
            case "customXmlMoveToRangeEnd":
                return true;
            default:
                return false;
        }
    }

    /// <summary>A bookmark or comment range marker as an inline (null for other range markers).</summary>
    private static Inline? MarkerInline(XElement e)
    {
        var name = e.Name;
        if (name == Wn.BookmarkStart)
            return new BookmarkMarker { Id = e.IntAttr("id") ?? 0, Name = e.Attr("name"), IsStart = true, Original = new XElement(e) };
        if (name == Wn.BookmarkEnd)
            return new BookmarkMarker { Id = e.IntAttr("id") ?? 0, IsStart = false, Original = new XElement(e) };
        if (name == Wn.CommentRangeStart)
            return new CommentMarker { Id = e.IntAttr("id") ?? 0, Kind = CommentMarkerKind.RangeStart };
        if (name == Wn.CommentRangeEnd)
            return new CommentMarker { Id = e.IntAttr("id") ?? 0, Kind = CommentMarkerKind.RangeEnd };
        return null;
    }

    /// <summary>Any range marker as an inline (other kinds are kept as XML in an OpaqueInline marker).</summary>
    private Inline MarkerOrOpaque(XElement e)
    {
        var inline = MarkerInline(e);
        if (inline != null) return inline;
        var kind = e.Name.LocalName.StartsWith("perm", StringComparison.Ordinal) ? "permission"
            : e.Name.LocalName.StartsWith("move", StringComparison.Ordinal) ? "moveRange" : "marker";
        return new OpaqueInline(new XElement(e), kind, "") { IsMarker = true, SourcePart = _part.Uri };
    }

    private void AddMarker(List<RawBlock> raws, XElement e)
    {
        MarkerBlock block;
        if (raws.Count > 0 && raws[raws.Count - 1].Block is MarkerBlock last)
        {
            block = last;
            raws[raws.Count - 1].Sources.Add(e);
        }
        else
        {
            block = new MarkerBlock();
            raws.Add(new RawBlock(block, e));
        }
        var inline = MarkerInline(e);
        if (inline != null) block.Markers.Add(inline);
        else block.Other.Add(new XElement(e));
    }

    private ParaRaw ReadParagraph(XElement e)
    {
        var pPr = e.Element(Wn.PPr);
        var raw = new ParaRaw(e, pPr != null ? new ParaProps(new XElement(pPr)) : new ParaProps(), DocxXml.Shell(e));
        Tokenize(e, null, raw.Toks);
        return raw;
    }

    // ------------------------------------------------------------------ tables

    private Block? ReadTable(XElement e)
    {
        var structured = HasRowOrCellControls(e);
        if (structured) _displayDepth++;
        Table table;
        try
        {
            table = ReadTableModel(e, structured);
        }
        finally
        {
            if (structured) _displayDepth--;
        }
        if (structured)
        {
            Tally("contentControl");
            return new OpaqueBlock(new XElement(e), "table")
            {
                SourcePart = _part.Uri,
                Display = table.Rows.Count > 0 ? new List<Block> { table } : null,
                Description = "Table with content controls",
            };
        }
        return table.Rows.Count == 0 || table.Rows.All(r => r.Cells.Count == 0) ? null : table;
    }

    private static bool HasRowOrCellControls(XElement tbl)
    {
        foreach (var row in RowLevel(tbl, false))
        {
            if (row.Name == Wn.Sdt) return true;
            if (row.Name != Wn.Tr) continue;
            foreach (var cell in CellLevel(row, false))
                if (cell.Name == Wn.Sdt) return true;
        }
        return false;
    }

    private static IEnumerable<XElement> RowLevel(XElement tbl, bool unwrapSdt)
    {
        foreach (var c in tbl.Elements())
        {
            if (c.Name == Wn.CustomXml)
            {
                foreach (var inner in RowLevel(c, unwrapSdt)) yield return inner;
            }
            else if (unwrapSdt && c.Name == Wn.Sdt)
            {
                var content = c.Element(Wn.SdtContent);
                if (content != null)
                    foreach (var inner in RowLevel(content, unwrapSdt)) yield return inner;
            }
            else if (c.Name != Wn.CustomXmlPr)
            {
                yield return c;
            }
        }
    }

    private static IEnumerable<XElement> CellLevel(XElement tr, bool unwrapSdt) => RowLevel(tr, unwrapSdt);

    private Table ReadTableModel(XElement e, bool unwrapSdt)
    {
        if (e.Descendants(Wn.CustomXml).Any(c => c.Parent?.Name == Wn.Tbl || c.Parent?.Name == Wn.Tr)) Warn(CustomXmlWarning);
        var tblPr = e.Element(Wn.TblPr);
        var table = new Table { Props = tblPr != null ? new TableProps(new XElement(tblPr)) : new TableProps(), Shell = DocxXml.Shell(e) };
        var grid = e.Element(Wn.TblGrid);
        if (grid != null)
        {
            table.Grid = grid.Elements(Wn.GridCol).Select(g => g.IntAttr("w") ?? 0).ToList();
            if (grid.Element(Ns.W + "tblGridChange") != null) Warn("A tracked change to table column widths was accepted.");
        }
        var leading = new List<XElement>();
        foreach (var child in RowLevel(e, unwrapSdt))
        {
            if (child.Name == Wn.Tr)
            {
                var row = ReadRow(child, unwrapSdt);
                row.Leading.InsertRange(0, leading);
                leading.Clear();
                table.AddRow(row);
            }
            else if (child.Name == Wn.TblPr || child.Name == Wn.TblGrid || child.Name == Wn.ProofErr)
            {
                // read above / dropped
            }
            else
            {
                leading.Add(new XElement(child));
            }
        }
        if (leading.Count > 0)
        {
            var lastCell = table.Rows.Count > 0 ? table.Rows[table.Rows.Count - 1].Cells.LastOrDefault() : null;
            if (lastCell != null) AppendMarkers(lastCell, leading);
        }
        return table;
    }

    private TableRow ReadRow(XElement tr, bool unwrapSdt)
    {
        var trPr = tr.Element(Wn.TrPr);
        var row = new TableRow
        {
            Props = trPr != null ? new RowProps(new XElement(trPr)) : new RowProps(),
            PropertyExceptions = tr.Element(Wn.TblPrEx).Copy(),
            Shell = DocxXml.Shell(tr),
        };
        var pending = new List<XElement>();
        foreach (var child in CellLevel(tr, unwrapSdt))
        {
            if (child.Name == Wn.Tc)
            {
                if (pending.Count > 0)
                {
                    if (row.Cells.Count == 0) row.Leading.AddRange(pending);
                    else AppendMarkers(row.Cells[row.Cells.Count - 1], pending);
                    pending.Clear();
                }
                row.AddCell(ReadCell(child));
            }
            else if (child.Name == Wn.TrPr || child.Name == Wn.TblPrEx || child.Name == Wn.ProofErr)
            {
                // read above / dropped
            }
            else
            {
                pending.Add(new XElement(child));
            }
        }
        if (pending.Count > 0)
        {
            if (row.Cells.Count == 0) row.Leading.AddRange(pending);
            else AppendMarkers(row.Cells[row.Cells.Count - 1], pending);
        }
        return row;
    }

    private TableCell ReadCell(XElement tc)
    {
        var tcPr = tc.Element(Wn.TcPr);
        var cell = new TableCell { Props = tcPr != null ? new CellProps(new XElement(tcPr)) : new CellProps(), Shell = DocxXml.Shell(tc) };
        cell.Blocks.AddRange(ParseBlocks(tc.Elements().Where(c => c.Name != Wn.TcPr)));
        return cell;
    }

    /// <summary>Markers that sat between cells or after the last row go to the end of the cell before them.</summary>
    private void AppendMarkers(TableCell cell, List<XElement> elements)
    {
        var paragraph = DocumentWalker.Paragraphs(cell.Blocks).LastOrDefault();
        if (paragraph == null)
        {
            paragraph = new Paragraph();
            cell.Blocks.Add(paragraph);
        }
        foreach (var e in elements)
        {
            if (IsRangeMarker(e.Name)) paragraph.Inlines.Add(MarkerOrOpaque(e));
            else paragraph.Inlines.Add(new OpaqueInline(new XElement(e), "other", "") { IsMarker = true, SourcePart = _part.Uri });
        }
    }

    // ------------------------------------------------------------------ content controls

    private SdtBlock ReadBlockSdt(XElement e)
    {
        var sdtPr = e.Element(Wn.SdtPr);
        var block = new SdtBlock
        {
            Properties = sdtPr != null ? new XElement(sdtPr) : new XElement(Wn.SdtPr),
            EndProperties = e.Element(Wn.SdtEndPr).Copy(),
            SourcePart = _part.Uri,
        };
        var managed = block.IsManaged;
        var content = e.Element(Wn.SdtContent);
        if (!managed) _displayDepth++;
        try
        {
            if (content != null) block.Blocks.AddRange(ParseBlocks(content.Elements()));
        }
        finally
        {
            if (!managed) _displayDepth--;
        }
        if (!managed)
        {
            block.Original = new XElement(e);
            Tally("contentControl");
        }
        return block;
    }

    private Inline ReadInlineSdt(XElement e, Wrap? wrap)
    {
        var sdtPr = e.Element(Wn.SdtPr);
        var content = e.Element(Wn.SdtContent);
        List<Inline> inlines;
        _displayDepth++;
        try
        {
            var toks = new List<Tok>();
            if (content != null) Tokenize(content, wrap, toks);
            inlines = AssembleInlines(toks);
        }
        finally
        {
            _displayDepth--;
        }
        if (sdtPr != null && inlines.Count == 1 && inlines[0] is FieldInline { PreservedXml: null } field && field.Kind == FieldKind.Citation)
        {
            field.SdtProperties = new XElement(sdtPr);
            return field;
        }
        Tally("contentControl");
        var display = new Paragraph { Inlines = inlines }.PlainText();
        return new OpaqueInline(new XElement(e), "contentControl", display) { SourcePart = _part.Uri };
    }

    // ------------------------------------------------------------------ tokens

    private void Tokenize(XElement parent, Wrap? wrap, List<Tok> toks)
    {
        foreach (var e in parent.Elements())
        {
            var n = e.Name;
            if (n == Wn.R)
            {
                TokenizeRun(e, wrap, toks);
                continue;
            }
            if (n == Wn.PPr || n == Wn.ProofErr || n == Wn.SdtPr || n == Wn.SdtEndPr || n == Wn.SmartTagPr || n == Wn.CustomXmlPr || n == Ns.W + "fldData")
                continue;
            if (n == Wn.Hyperlink)
            {
                Tokenize(e, new Wrap(e, wrap, ReadHyperlink(e), null, null, false), toks);
                continue;
            }
            if (n == Wn.Ins || n == Wn.MoveTo)
            {
                Tokenize(e, new Wrap(e, wrap, null, RevisionInfo.Parse(e), null, n == Wn.MoveTo), toks);
                continue;
            }
            if (n == Wn.Del || n == Wn.MoveFrom)
            {
                Tokenize(e, new Wrap(e, wrap, null, null, RevisionInfo.Parse(e), n == Wn.MoveFrom), toks);
                continue;
            }
            if (n == Wn.SmartTag || n == Wn.CustomXml)
            {
                Warn(CustomXmlWarning);
                Tokenize(e, wrap, toks);
                continue;
            }
            if (n == Wn.Dir || n == Wn.Bdo)
            {
                Warn(BidiWarning);
                Tokenize(e, wrap, toks);
                continue;
            }

            var ctx = new RunCtx(wrap, null, null);
            Inline inline;
            if (n == Wn.FldSimple) inline = ReadSimpleField(e, wrap);
            else if (n == Wn.Sdt) inline = ReadInlineSdt(e, wrap);
            else if (IsRangeMarker(n)) inline = MarkerOrOpaque(e);
            else if (n == Wn.OMath || n == Wn.OMathPara)
            {
                Tally("equation");
                inline = new OpaqueInline(new XElement(e), "equation", DocxXml.VisibleText(e)) { SourcePart = _part.Uri };
            }
            else if (n == Wn.AlternateContent)
            {
                inline = new OpaqueInline(CopyAlternateContent(e), "other", AlternateText(e)) { SourcePart = _part.Uri };
            }
            else
            {
                // w:subDoc and elements from newer versions: kept as they were.
                var text = DocxXml.VisibleText(e);
                inline = new OpaqueInline(new XElement(e), n == Ns.W + "subDoc" ? "subDocument" : "other", text) { SourcePart = _part.Uri, IsMarker = text.Length == 0 };
            }
            toks.Add(new Tok(TokKind.Inline, ctx, e) { Inline = inline });
        }
    }

    private void TokenizeRun(XElement run, Wrap? wrap, List<Tok> toks)
    {
        var ctx = new RunCtx(wrap, run, run.Element(Wn.RPr));
        foreach (var c in run.Elements())
        {
            var n = c.Name;
            if (n.Namespace == Ns.W)
            {
                switch (n.LocalName)
                {
                    case "rPr":
                    case "lastRenderedPageBreak":
                        break;
                    case "t":
                    case "delText":
                        Text(c.Value);
                        break;
                    case "tab":
                        Text("\t");
                        break;
                    case "br":
                    {
                        var type = c.Attr("type");
                        var clear = c.Attr("clear");
                        if (type == "page") Text("\f");
                        else if (type == "column") Text("\u000E");
                        else if (clear != null && clear != "none") Object(new ClearBreak { Clear = clear });
                        else Text("\v");
                        break;
                    }
                    case "cr":
                        Text("\v");
                        break;
                    case "softHyphen":
                        Text("­");
                        break;
                    case "noBreakHyphen":
                        Text("‑");
                        break;
                    case "sym":
                        Object(new SymbolChar { Font = c.Attr("font") ?? "Symbol", Char = ParseSymbolChar(c.Attr("char")) });
                        break;
                    case "footnoteReference":
                    case "endnoteReference":
                        Object(new NoteReference
                        {
                            Kind = n.LocalName == "footnoteReference" ? NoteKind.Footnote : NoteKind.Endnote,
                            Id = c.IntAttr("id") ?? 0,
                            CustomMark = DocxXml.OnOffAttr(c, "customMarkFollows"),
                        });
                        break;
                    case "footnoteRef":
                        Object(new NoteMark { Kind = NoteKind.Footnote });
                        break;
                    case "endnoteRef":
                        Object(new NoteMark { Kind = NoteKind.Endnote });
                        break;
                    case "separator":
                        Object(new NoteSeparator());
                        break;
                    case "continuationSeparator":
                        Object(new NoteSeparator { Continuation = true });
                        break;
                    case "annotationRef":
                        Object(new CommentMark());
                        break;
                    case "commentReference":
                        Object(new CommentMarker { Id = c.IntAttr("id") ?? 0, Kind = CommentMarkerKind.Reference });
                        break;
                    case "fldChar":
                    {
                        var type = c.Attr("fldCharType");
                        if (type == "begin") toks.Add(new Tok(TokKind.Begin, ctx, c));
                        else if (type == "separate") toks.Add(new Tok(TokKind.Sep, ctx, c));
                        else if (type == "end") toks.Add(new Tok(TokKind.End, ctx, c));
                        break;
                    }
                    case "instrText":
                    case "delInstrText":
                        toks.Add(new Tok(TokKind.Instr, ctx, c) { Text = c.Value });
                        break;
                    case "drawing":
                        if (c.Element(Wn.WpInline) != null || c.Element(Wn.WpAnchor) != null) Object(ReadDrawing(c, c));
                        else Warn("An empty drawing, which Word can't open, was removed.");
                        break;
                    case "pict":
                        Object(ReadPict(c));
                        break;
                    case "object":
                        Object(ReadEmbeddedObject(c));
                        break;
                    case "ruby":
                        Tally("ruby");
                        Object(new OpaqueInline(new XElement(c), "ruby", DocxXml.VisibleText(c.Element(Ns.W + "rubyBase") ?? c)) { SourcePart = _part.Uri });
                        break;
                    case "ptab":
                        Object(new OpaqueInline(new XElement(c), "ptab", "\t") { SourcePart = _part.Uri });
                        break;
                    case "pgNum":
                        Object(new OpaqueInline(new XElement(c), "pageNumber", "#") { SourcePart = _part.Uri });
                        break;
                    case "contentPart":
                        Tally("ink");
                        Object(new OpaqueInline(new XElement(c), "ink", "") { SourcePart = _part.Uri });
                        break;
                    default:
                        // dayShort, monthLong… and anything newer: kept as it was.
                        Object(new OpaqueInline(new XElement(c), "other", DocxXml.VisibleText(c)) { SourcePart = _part.Uri });
                        break;
                }
            }
            else if (n == Wn.AlternateContent)
            {
                Object(ReadRunAlternateContent(c));
            }
            else
            {
                Object(new OpaqueInline(new XElement(c), "other", "") { SourcePart = _part.Uri });
            }

            void Text(string s) => toks.Add(new Tok(TokKind.Text, ctx, c) { Text = s });

            void Object(Inline inline)
            {
                inline.Props = ctx.NewProps();
                toks.Add(new Tok(TokKind.Inline, ctx, c) { Inline = inline });
            }
        }
    }

    private static char ParseSymbolChar(string? hex)
    {
        if (hex != null && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code) && code > 0 && code <= 0xFFFF)
            return (char)code;
        return '?';
    }

    private Hyperlink ReadHyperlink(XElement e)
    {
        var link = new Hyperlink { Anchor = e.Attr("anchor"), Tooltip = e.Attr("tooltip"), Shell = DocxXml.Shell(e) };
        var id = e.Attribute(Ns.R + "id")?.Value;
        if (id != null) link.Url = _part.ExternalTarget(id);
        return link;
    }

    private FieldInline ReadSimpleField(XElement e, Wrap? wrap)
    {
        var toks = new List<Tok>();
        Tokenize(e, wrap, toks);
        var result = AssembleInlines(toks);
        var field = new FieldInline
        {
            Simple = true,
            Code = e.Attr("instr") ?? "",
            Dirty = DocxXml.OnOffAttr(e, "dirty"),
            Locked = DocxXml.OnOffAttr(e, "fldLock"),
            Result = result,
            Props = result.OfType<TextRun>().FirstOrDefault()?.Props.Clone() ?? new RunProps(),
        };
        var form = IsFormField(field.Code);
        if (form || e.Element(Ns.W + "fldData") != null)
        {
            field.PreservedXml = new XElement(Wn.IwRuns, new XAttribute(XNamespace.Xmlns + "iw", Ns.IW.NamespaceName), new XElement(e));
            Tally(form ? "formField" : "field");
        }
        return field;
    }

    private static bool IsFormField(string code)
    {
        var trimmed = code.TrimStart();
        var end = 0;
        while (end < trimmed.Length && !char.IsWhiteSpace(trimmed[end]) && trimmed[end] != '\\') end++;
        var word = trimmed.Substring(0, end).ToUpperInvariant();
        return word is "FORMTEXT" or "FORMCHECKBOX" or "FORMDROPDOWN";
    }

    /// <summary>A copy of mc:AlternateContent that declares the prefixes its mc:Choice Requires uses, so it stands on its own.</summary>
    private static XElement CopyAlternateContent(XElement ac)
    {
        var copy = new XElement(ac);
        foreach (var choice in ac.Elements(Wn.Choice))
        {
            var requires = choice.Attribute("Requires")?.Value;
            if (requires == null) continue;
            foreach (var prefix in requires.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var ns = choice.GetNamespaceOfPrefix(prefix);
                if (ns != null && copy.Attribute(XNamespace.Xmlns + prefix) == null)
                    copy.SetAttributeValue(XNamespace.Xmlns + prefix, ns.NamespaceName);
            }
        }
        return copy;
    }

    private static string AlternateText(XElement ac)
    {
        var branch = ac.Element(Wn.Choice) ?? ac.Element(Wn.Fallback);
        return branch == null ? "" : DocxXml.VisibleText(branch);
    }

    // ------------------------------------------------------------------ field matching

    private List<Block> AssembleContainer(List<RawBlock> raws)
    {
        var fields = MatchFields(raws);
        if (fields.Count > 0) PlanMultiParagraphFields(raws, fields);
        return Build(raws, 0, raws.Count, null);
    }

    /// <summary>Inlines of one run container (a simple field's result, an inline content control).</summary>
    private List<Inline> AssembleInlines(List<Tok> toks)
    {
        var raw = new ParaRaw(null, new ParaProps(), null);
        raw.Toks.AddRange(toks);
        MatchFields(new List<RawBlock> { raw });
        return AssembleParagraph(raw).Inlines;
    }

    private static List<FieldSpan> MatchFields(List<RawBlock> raws)
    {
        var all = new List<FieldSpan>();
        var stack = new List<FieldSpan>();
        for (var ri = 0; ri < raws.Count; ri++)
        {
            if (raws[ri] is not ParaRaw pr)
            {
                if (stack.Count > 0 && stack[stack.Count - 1].InCode && raws[ri].Block is not MarkerBlock)
                    stack[stack.Count - 1].ContentInCode = true;
                continue;
            }
            foreach (var tok in pr.Toks)
            {
                var top = stack.Count > 0 ? stack[stack.Count - 1] : null;
                switch (tok.Kind)
                {
                    case TokKind.Begin:
                    {
                        var f = new FieldSpan(tok, ri, top);
                        if (top != null && top.InCode) top.NestedInCode = true;
                        tok.Field = f;
                        stack.Add(f);
                        all.Add(f);
                        break;
                    }
                    case TokKind.Instr:
                        if (top == null || !top.InCode) tok.Stray = true;
                        else
                        {
                            tok.Field = top;
                            top.Code.Append(tok.Text);
                        }
                        break;
                    case TokKind.Sep:
                        if (top == null || !top.InCode) tok.Stray = true;
                        else
                        {
                            top.Sep = tok;
                            top.SepPara = ri;
                            tok.Field = top;
                        }
                        break;
                    case TokKind.End:
                        if (top == null) tok.Stray = true;
                        else
                        {
                            top.End = tok;
                            top.EndPara = ri;
                            tok.Field = top;
                            stack.RemoveAt(stack.Count - 1);
                        }
                        break;
                    default:
                        if (top != null && top.InCode && tok.IsContent) top.ContentInCode = true;
                        break;
                }
            }
        }
        foreach (var f in stack) f.Broken = true;
        return all;
    }

    private void PlanMultiParagraphFields(List<RawBlock> raws, List<FieldSpan> fields)
    {
        var endsIn = new HashSet<int>();
        foreach (var f in fields)
        {
            if (!f.IsMulti) continue;
            if (endsIn.Contains(f.BeginPara))
            {
                // Another field spanning paragraphs ends in the paragraph where this one begins: a paragraph can't
                // belong to two field blocks, so this one keeps its result as ordinary content.
                f.Broken = true;
                Warn(BrokenFieldWarning);
                continue;
            }
            endsIn.Add(f.EndPara);
            f.Opaque = f.NestedInCode || f.ContentInCode || f.Sep == null || f.SepPara != f.BeginPara;
            var raw = (ParaRaw)raws[f.BeginPara];
            (raw.MultiBegins ??= new List<FieldSpan>()).Add(f);
        }
    }

    private static FieldSpan? MultiParent(FieldSpan f)
    {
        for (var a = f.Parent; a != null; a = a.Parent)
            if (a.IsMulti) return a;
        return null;
    }

    private List<Block> Build(List<RawBlock> raws, int from, int to, FieldSpan? container)
    {
        var result = new List<Block>();
        var i = from;
        while (i < to)
        {
            var raw = raws[i];
            if (raw is ParaRaw pr)
            {
                FieldSpan? field = null;
                if (pr.MultiBegins != null)
                {
                    foreach (var m in pr.MultiBegins)
                    {
                        if (m.IsMulti && !ReferenceEquals(m, container) && ReferenceEquals(MultiParent(m), container))
                        {
                            field = m;
                            break;
                        }
                    }
                }
                if (field != null)
                {
                    var end = Math.Min(field.EndPara, to - 1);
                    var inner = Build(raws, i, end + 1, field);
                    if (field.Opaque)
                    {
                        var xml = new XElement(Wn.IwBlocks, new XAttribute(XNamespace.Xmlns + "iw", Ns.IW.NamespaceName));
                        for (var k = i; k <= end; k++)
                            foreach (var source in raws[k].Sources)
                                xml.Add(new XElement(source));
                        Tally("field");
                        result.Add(new OpaqueBlock(xml, "field") { SourcePart = _part.Uri, Display = inner, Description = "Field " + field.Code.ToString().Trim() });
                    }
                    else
                    {
                        var block = new FieldBlock
                        {
                            Code = field.Code.ToString(),
                            Dirty = DocxXml.OnOffAttr(field.Begin.Child, "dirty"),
                            Locked = DocxXml.OnOffAttr(field.Begin.Child, "fldLock"),
                            BeginProps = field.Begin.Ctx.RPr != null ? new RunProps(new XElement(field.Begin.Ctx.RPr)) : null,
                            EndProps = field.End?.Ctx.RPr != null ? new RunProps(new XElement(field.End.Ctx.RPr)) : null,
                        };
                        block.Blocks.AddRange(inner);
                        block.BeginAt = field.BeginAt;
                        block.EndAt = field.EndAt;
                        result.Add(block);
                    }
                    i = end + 1;
                    continue;
                }
                result.Add(AssembleParagraph(pr));
            }
            else if (raw.Block != null)
            {
                result.Add(raw.Block);
            }
            i++;
        }
        return result;
    }

    // ------------------------------------------------------------------ paragraph assembly

    private Paragraph AssembleParagraph(ParaRaw raw)
    {
        var p = new Paragraph { Props = raw.Props, Shell = raw.Shell };
        new InlineAssembler(this, p.Inlines).Run(raw.Toks);
        return p;
    }

    private sealed class Builder
    {
        public Builder(FieldSpan field, Tok begin, int start)
        {
            Field = field;
            Begin = begin;
            StartIndex = start;
        }

        public FieldSpan Field { get; }
        public Tok Begin { get; }
        public int StartIndex { get; }
        public bool Separated { get; set; }
        public bool NeedsPreserve { get; set; }
        public Tok? FirstInstr { get; set; }
        public StringBuilder Code { get; } = new();
        public List<Inline> Result { get; } = new();
        public List<Inline> CodeItems { get; } = new();
    }

    /// <summary>Turns a paragraph's tokens into inlines: merges text, builds one-paragraph fields.</summary>
    private sealed class InlineAssembler
    {
        private readonly DocxReadSession _session;
        private readonly List<Inline> _output;
        private readonly List<Builder> _stack = new();
        private readonly StringBuilder _text = new();
        private TextRun? _open;
        private RunCtx? _openCtx;
        private List<Inline>? _openTarget;

        public InlineAssembler(DocxReadSession session, List<Inline> output)
        {
            _session = session;
            _output = output;
        }

        private Builder? Top => _stack.Count > 0 ? _stack[_stack.Count - 1] : null;

        private List<Inline> Target
        {
            get
            {
                var top = Top;
                if (top == null) return _output;
                return top.Separated ? top.Result : top.CodeItems;
            }
        }

        public void Run(List<Tok> toks)
        {
            for (var i = 0; i < toks.Count; i++) Feed(toks, i);
            Flush();
            // Defensive: a field still open here (shouldn't happen) gives its content back as ordinary inlines.
            while (_stack.Count > 0)
            {
                var b = _stack[_stack.Count - 1];
                _stack.RemoveAt(_stack.Count - 1);
                var target = Target;
                target.AddRange(b.CodeItems);
                target.AddRange(b.Result);
                _session.Warn(BrokenFieldWarning);
            }
        }

        private void Feed(List<Tok> toks, int index)
        {
            var tok = toks[index];
            switch (tok.Kind)
            {
                case TokKind.Text:
                    AddText(tok);
                    return;
                case TokKind.Inline:
                    Flush();
                    tok.Ctx.Apply(tok.Inline!);
                    Add(tok.Inline!);
                    return;
            }

            Flush();
            var field = tok.Field;
            if (tok.Stray || field == null || field.Broken)
            {
                _session.Warn(BrokenFieldWarning);
                return;
            }
            if (field.IsMulti)
            {
                if (tok.Kind == TokKind.Begin) field.BeginAt = _output.Count;
                else if (tok.Kind == TokKind.End) field.EndAt = _output.Count;
                return;
            }

            switch (tok.Kind)
            {
                case TokKind.Begin:
                    _stack.Add(new Builder(field, tok, index));
                    break;
                case TokKind.Instr:
                {
                    var top = Top;
                    if (top == null || !ReferenceEquals(top.Field, field)) break;
                    top.Code.Append(tok.Text);
                    top.FirstInstr ??= tok;
                    break;
                }
                case TokKind.Sep:
                {
                    var top = Top;
                    if (top != null && ReferenceEquals(top.Field, field)) top.Separated = true;
                    break;
                }
                case TokKind.End:
                {
                    var top = Top;
                    if (top == null || !ReferenceEquals(top.Field, field)) break;
                    _stack.RemoveAt(_stack.Count - 1);
                    var inline = _session.FinishField(top, toks, index);
                    var parent = Top;
                    if (parent != null && !parent.Separated)
                    {
                        parent.NeedsPreserve = true;
                        parent.Code.Append("{ ").Append(inline.Code.Trim()).Append(" }");
                    }
                    Add(inline);
                    break;
                }
            }
        }

        private void Add(Inline inline)
        {
            var top = Top;
            if (top != null && !top.Separated && (inline.Length > 0 || inline is DrawingObject)) top.NeedsPreserve = true;
            Target.Add(inline);
        }

        private void AddText(Tok tok)
        {
            var target = Target;
            if (_open != null && ReferenceEquals(_openTarget, target) && Equivalent(_openCtx!, tok.Ctx))
            {
                _text.Append(tok.Text);
                return;
            }
            Flush();
            var run = new TextRun { Props = tok.Ctx.NewProps() };
            tok.Ctx.Apply(run);
            Add(run);
            _open = run;
            _openCtx = tok.Ctx;
            _openTarget = target;
            _text.Append(tok.Text);
        }

        private void Flush()
        {
            if (_open == null) return;
            _open.Text = _text.ToString();
            _text.Clear();
            _open = null;
            _openCtx = null;
            _openTarget = null;
        }

        private static bool Equivalent(RunCtx a, RunCtx b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (!ReferenceEquals(a.Wrap?.Link, b.Wrap?.Link)) return false;
            if ((a.Wrap?.IsMove ?? false) != (b.Wrap?.IsMove ?? false)) return false;
            if (!SameRevision(a.Wrap?.Ins, b.Wrap?.Ins) || !SameRevision(a.Wrap?.Del, b.Wrap?.Del)) return false;
            return SameRunProps(a.RPr, b.RPr);
        }
    }

    private static bool SameRevision(RevisionInfo? a, RevisionInfo? b) =>
        a == null ? b == null : b != null && a.Author == b.Author && a.Date == b.Date;

    private static bool SameRunProps(XElement? a, XElement? b)
    {
        static bool Empty(XElement? e) => e == null || !e.HasElements && !e.Attributes().Any(x => !x.IsNamespaceDeclaration);
        if (a == null || b == null) return Empty(a) && Empty(b);
        return DocxXml.SameXml(a, b);
    }

    /// <summary>Builds a one-paragraph field from its collected parts.</summary>
    private FieldInline FinishField(Builder b, List<Tok> toks, int endIndex)
    {
        var beginCtx = b.Begin.Ctx;
        var fldChar = b.Begin.Child;
        var field = new FieldInline
        {
            Code = b.Code.ToString(),
            Result = b.Result,
            Dirty = DocxXml.OnOffAttr(fldChar, "dirty"),
            Locked = DocxXml.OnOffAttr(fldChar, "fldLock"),
            BeginProps = beginCtx.RPr != null ? new RunProps(new XElement(beginCtx.RPr)) : null,
            Props = (b.FirstInstr?.Ctx ?? beginCtx).NewProps(),
        };
        var wrap = beginCtx.Wrap;
        var form = IsFormField(field.Code);
        if (b.NeedsPreserve || form || fldChar.HasElements)
        {
            var (xml, common) = BuildRunsXml(toks, b.StartIndex, endIndex);
            field.PreservedXml = xml;
            wrap = common;
            Tally(form ? "formField" : "field");
        }
        if (wrap != null)
        {
            field.Link = wrap.Link;
            field.Inserted = wrap.Ins?.Clone();
            field.Deleted = wrap.Del?.Clone();
            field.IsMove = wrap.IsMove;
        }
        return field;
    }

    /// <summary>
    /// The exact runs from toks[from] to toks[to] as &lt;iw:runs&gt;: wrappers common to all of them are left out (they
    /// become the field's Link / revision marks); inner hyperlinks and revisions are kept.
    /// </summary>
    private static (XElement Xml, Wrap? Common) BuildRunsXml(List<Tok> toks, int from, int to)
    {
        var common = toks[from].Ctx.Wrap;
        for (var k = from + 1; k <= to && common != null; k++) common = CommonWrap(common, toks[k].Ctx.Wrap);

        var root = new XElement(Wn.IwRuns, new XAttribute(XNamespace.Xmlns + "iw", Ns.IW.NamespaceName));
        var open = new List<(Wrap Wrap, XElement Element)>();
        XElement? run = null;
        XElement? runSource = null;
        XElement Container() => open.Count > 0 ? open[open.Count - 1].Element : root;

        for (var k = from; k <= to; k++)
        {
            var tok = toks[k];
            var path = new List<Wrap>();
            for (var w = tok.Ctx.Wrap; w != null && !ReferenceEquals(w, common); w = w.Parent) path.Add(w);
            path.Reverse();
            var same = 0;
            while (same < open.Count && same < path.Count && ReferenceEquals(open[same].Wrap, path[same])) same++;
            if (same < open.Count)
            {
                open.RemoveRange(same, open.Count - same);
                run = null;
            }
            for (var j = same; j < path.Count; j++)
            {
                var shell = DocxXml.Shell(path[j].Source);
                Container().Add(shell);
                open.Add((path[j], shell));
                run = null;
            }
            if (tok.Ctx.Run != null)
            {
                if (run != null && ReferenceEquals(runSource, tok.Ctx.Run))
                {
                    run.Add(new XElement(tok.Child));
                }
                else
                {
                    run = new XElement(Wn.R, tok.Ctx.Run.Attributes());
                    if (tok.Ctx.RPr != null) run.Add(new XElement(tok.Ctx.RPr));
                    run.Add(new XElement(tok.Child));
                    Container().Add(run);
                    runSource = tok.Ctx.Run;
                }
            }
            else
            {
                Container().Add(new XElement(tok.Child));
                run = null;
            }
        }
        return (root, common);
    }

    private static Wrap? CommonWrap(Wrap? a, Wrap? b)
    {
        while (a != null && b != null && !ReferenceEquals(a, b))
        {
            if (a.Depth > b.Depth) a = a.Parent;
            else if (b.Depth > a.Depth) b = b.Parent;
            else
            {
                a = a.Parent;
                b = b.Parent;
            }
        }
        return ReferenceEquals(a, b) ? a : null;
    }
}
