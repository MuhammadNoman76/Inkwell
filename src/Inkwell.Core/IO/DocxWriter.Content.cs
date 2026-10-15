using System.Text;
using System.Xml.Linq;
using Inkwell.Core.Model;

namespace Inkwell.Core.IO;

// Blocks, paragraphs and runs → WordprocessingML.
//
// A paragraph's inlines become a flat list of items (a w:r, or a paragraph-level element such as a bookmark,
// w:fldSimple, w:sdt, m:oMath), each with its hyperlink and revision marks. Consecutive items are then grouped:
// w:hyperlink outermost (w:ins can't contain a hyperlink), then w:ins / w:moveTo, then w:del / w:moveFrom.
// Zero-length markers (bookmarks, comment ranges) join the group around them instead of splitting it.
internal sealed partial class DocxWriteSession
{
    /// <summary>One paragraph-level element with the wrappers it belongs in.</summary>
    private sealed class Item
    {
        public Item(XElement element)
        {
            Element = element;
        }

        public XElement Element { get; }
        public Hyperlink? Link { get; set; }
        public RevisionInfo? Ins { get; set; }
        public RevisionInfo? Del { get; set; }
        public bool IsMove { get; set; }

        /// <summary>A zero-length marker: it takes the hyperlink / revision of the items around it.</summary>
        public bool Transparent { get; set; }
    }

    /// <summary>A block to write, with field characters to insert into it (for FieldBlocks).</summary>
    private sealed class FlatBlock
    {
        public FlatBlock(Block block)
        {
            Block = block;
        }

        public Block Block { get; }

        public List<(int At, List<Item> Items)>? Injections { get; set; }

        public void Inject(int at, List<Item> items, bool front)
        {
            Injections ??= new List<(int, List<Item>)>();
            if (front)
            {
                var index = Injections.FindIndex(x => x.At >= at);
                Injections.Insert(index < 0 ? Injections.Count : index, (at, items));
            }
            else
            {
                var index = Injections.FindIndex(x => x.At > at);
                Injections.Insert(index < 0 ? Injections.Count : index, (at, items));
            }
        }
    }

    // ------------------------------------------------------------------ blocks

    /// <summary>
    /// Writes blocks into a container. <paramref name="story"/>: the container is a cell, header, footer, note or
    /// comment, which must hold at least one paragraph and end with one (Word rejects a cell ending with a table).
    /// </summary>
    private void WriteBlocks(XElement container, IEnumerable<Block> blocks, PartWriter pw, bool story)
    {
        foreach (var flat in Flatten(blocks))
        {
            switch (flat.Block)
            {
                case Paragraph p:
                    container.Add(ParagraphElement(p, pw, flat.Injections));
                    break;
                case Table t:
                    if (TableElement(t, pw) is { } table) container.Add(table);
                    break;
                case SdtBlock s:
                    if (SdtElement(s, pw) is { } sdt) container.Add(sdt);
                    break;
                case OpaqueBlock o:
                {
                    var x = MapRels(new XElement(o.Xml), o.SourcePart, pw, o);
                    if (x == null) break;
                    if (x.Name == Wn.IwBlocks) container.Add(x.Elements().ToList());
                    else container.Add(x);
                    break;
                }
                case MarkerBlock m:
                    foreach (var marker in m.Markers)
                    {
                        var items = new List<Item>();
                        AddInlineItems(items, marker, pw);
                        foreach (var item in items)
                            if (IsBlockLevelMarker(item.Element)) container.Add(item.Element);
                    }
                    foreach (var other in m.Other) container.Add(new XElement(other));
                    break;
                default:
                    Warn("Part of the document Inkwell couldn't save was left out.");
                    break;
            }
        }
        if (story)
        {
            var last = container.Elements().LastOrDefault(e => e.Name == Wn.P || e.Name == Wn.Tbl || e.Name == Wn.Sdt || e.Name == Ns.W + "altChunk");
            if (last == null || last.Name != Wn.P && last.Name != Wn.Sdt) container.Add(new XElement(Wn.P));
        }
    }

    private static bool IsBlockLevelMarker(XElement e) =>
        e.Name == Wn.BookmarkStart || e.Name == Wn.BookmarkEnd || e.Name == Wn.CommentRangeStart || e.Name == Wn.CommentRangeEnd ||
        e.Name.Namespace == Ns.W && (e.Name.LocalName.EndsWith("RangeStart", StringComparison.Ordinal) || e.Name.LocalName.EndsWith("RangeEnd", StringComparison.Ordinal) ||
                                     e.Name.LocalName is "permStart" or "permEnd");

    /// <summary>Expands FieldBlocks into their blocks, with the field characters to insert into the first and last paragraph.</summary>
    private List<FlatBlock> Flatten(IEnumerable<Block> blocks)
    {
        var list = new List<FlatBlock>();
        foreach (var b in blocks)
        {
            if (b is FieldBlock fb) list.AddRange(FlattenField(fb));
            else list.Add(new FlatBlock(b));
        }
        return list;
    }

    private List<FlatBlock> FlattenField(FieldBlock fb)
    {
        var inner = Flatten(fb.Blocks);
        var beginAt = fb.BeginAt;
        var endAt = fb.EndAt;
        if (inner.Count == 0 || inner[0].Block is not Paragraph)
        {
            inner.Insert(0, new FlatBlock(new Paragraph()));
            beginAt = 0;
        }
        if (inner[inner.Count - 1].Block is not Paragraph)
        {
            inner.Add(new FlatBlock(new Paragraph()));
            endAt = 0;
        }
        var first = inner[0];
        var last = inner[inner.Count - 1];
        beginAt = MathEx.Clamp(beginAt, 0, ((Paragraph)first.Block).Inlines.Count);
        endAt = MathEx.Clamp(endAt, 0, ((Paragraph)last.Block).Inlines.Count);
        if (ReferenceEquals(first, last) && endAt < beginAt) endAt = beginAt;

        var props = fb.BeginProps;
        var begin = new XElement(Wn.FldChar, new XAttribute(Ns.W + "fldCharType", "begin"));
        if (fb.Dirty) begin.Add(new XAttribute(Ns.W + "dirty", "true"));
        if (fb.Locked) begin.Add(new XAttribute(Ns.W + "fldLock", "true"));
        first.Inject(beginAt, new List<Item>
        {
            new(Run(props, begin)),
            new(Run(props, new XElement(Wn.InstrText, new XAttribute(Wn.XmlSpace, "preserve"), fb.Code))),
            new(Run(props, new XElement(Wn.FldChar, new XAttribute(Ns.W + "fldCharType", "separate")))),
        }, true);
        last.Inject(endAt, new List<Item> { new(Run(fb.EndProps ?? props, new XElement(Wn.FldChar, new XAttribute(Ns.W + "fldCharType", "end")))) }, false);
        return inner;
    }

    private XElement ParagraphElement(Paragraph p, PartWriter pw, List<(int At, List<Item> Items)>? injections)
    {
        var el = p.Shell != null ? new XElement(Wn.P, p.Shell.Attributes()) : new XElement(Wn.P);
        if (!p.Props.IsEmpty) el.Add(ParaPropsCopy(p.Props));
        var items = new List<Item>();
        var next = 0;
        for (var i = 0; i < p.Inlines.Count; i++)
        {
            while (injections != null && next < injections.Count && injections[next].At <= i) items.AddRange(injections[next++].Items);
            AddInlineItems(items, p.Inlines[i], pw);
        }
        while (injections != null && next < injections.Count) items.AddRange(injections[next++].Items);
        EmitItems(el, items, pw);
        return el;
    }

    private static XElement ParaPropsCopy(ParaProps props)
    {
        var copy = new XElement(props.X);
        SortIfNeeded(copy, SchemaOrder.ParaProps);
        var rPr = copy.Element(Wn.RPr);
        if (rPr != null) SortIfNeeded(rPr, SchemaOrder.RunProps);
        var sectPr = copy.Element(Wn.SectPr);
        if (sectPr != null) SortIfNeeded(sectPr, SectionOrder);
        return copy;
    }

    /// <summary>
    /// CT_SectPr's order. Header and footer references share the first place (they may come in any order);
    /// SchemaOrder.SectProps lacks footerReference, so new elements could land before it.
    /// </summary>
    internal static readonly Dictionary<string, int> SectionOrder = MakeOrder(
        new[] { "headerReference", "footerReference" }, new[] { "footnotePr" }, new[] { "endnotePr" }, new[] { "type" }, new[] { "pgSz" }, new[] { "pgMar" },
        new[] { "paperSrc" }, new[] { "pgBorders" }, new[] { "lnNumType" }, new[] { "pgNumType" }, new[] { "cols" }, new[] { "formProt" }, new[] { "vAlign" },
        new[] { "noEndnote" }, new[] { "titlePg" }, new[] { "textDirection" }, new[] { "bidi" }, new[] { "rtlGutter" }, new[] { "docGrid" },
        new[] { "printerSettings" }, new[] { "*" }, new[] { "sectPrChange" });

    private static Dictionary<string, int> MakeOrder(params string[][] ranks)
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < ranks.Length; i++)
            foreach (var name in ranks[i]) d[name] = i * 10;
        return d;
    }

    private static XElement SectionCopy(SectProps props)
    {
        var copy = new XElement(props.X);
        SortIfNeeded(copy, SectionOrder);
        return copy;
    }

    /// <summary>Puts children in schema order, only when they aren't already (the common case costs no allocation).</summary>
    internal static void SortIfNeeded(XElement e, Dictionary<string, int> order)
    {
        var last = int.MinValue;
        for (var node = e.FirstNode; node != null; node = node.NextNode)
        {
            if (node is not XElement child) continue;
            var index = SchemaOrder.IndexOf(order, child.Name);
            if (index < last)
            {
                SchemaOrder.Sort(e, order);
                return;
            }
            last = index;
        }
    }

    /// <summary>A run's properties to write (null when empty); paragraph-mark-only revision elements are dropped.</summary>
    private static XElement? RunPropsCopy(RunProps? props)
    {
        if (props == null || props.IsEmpty) return null;
        var copy = new XElement(props.X);
        if (copy.Element(Wn.Ins) != null || copy.Element(Wn.Del) != null || copy.Element(Wn.MoveFrom) != null || copy.Element(Wn.MoveTo) != null)
        {
            copy.Elements().Where(c => c.Name == Wn.Ins || c.Name == Wn.Del || c.Name == Wn.MoveFrom || c.Name == Wn.MoveTo).Remove();
            if (!copy.HasElements && !copy.HasAttributes) return null;
        }
        SortIfNeeded(copy, SchemaOrder.RunProps);
        return copy;
    }

    private static XElement Run(RunProps? props, params object[] content)
    {
        var r = new XElement(Wn.R);
        if (RunPropsCopy(props) is { } rPr) r.Add(rPr);
        r.Add(content);
        return r;
    }

    private XElement? TableElement(Table t, PartWriter pw)
    {
        if (t.Rows.Count == 0)
        {
            Warn("An empty table (with no rows) was left out.");
            return null;
        }
        var el = t.Shell != null ? new XElement(Wn.Tbl, t.Shell.Attributes()) : new XElement(Wn.Tbl);
        var tblPr = new XElement(t.Props.X);
        SortIfNeeded(tblPr, SchemaOrder.TableProps);
        el.Add(tblPr);
        var grid = t.Grid.Count > 0 ? t.Grid : DeriveGrid(t);
        el.Add(new XElement(Wn.TblGrid, grid.Select(w => new XElement(Wn.GridCol, new XAttribute(Ns.W + "w", Ox.Str(Math.Max(0, w)))))));
        var rows = 0;
        foreach (var row in t.Rows)
        {
            foreach (var lead in row.Leading) el.Add(new XElement(lead));
            if (row.Cells.Count == 0)
            {
                Warn("An empty table row (with no cells) was left out.");
                continue;
            }
            var tr = row.Shell != null ? new XElement(Wn.Tr, row.Shell.Attributes()) : new XElement(Wn.Tr);
            if (row.PropertyExceptions != null) tr.Add(new XElement(row.PropertyExceptions));
            if (!row.Props.IsEmpty)
            {
                var trPr = new XElement(row.Props.X);
                SortIfNeeded(trPr, SchemaOrder.RowProps);
                tr.Add(trPr);
            }
            foreach (var cell in row.Cells)
            {
                var tc = cell.Shell != null ? new XElement(Wn.Tc, cell.Shell.Attributes()) : new XElement(Wn.Tc);
                if (!cell.Props.IsEmpty)
                {
                    var tcPr = new XElement(cell.Props.X);
                    SortIfNeeded(tcPr, SchemaOrder.CellProps);
                    tc.Add(tcPr);
                }
                WriteBlocks(tc, cell.Blocks, pw, true);
                tr.Add(tc);
            }
            el.Add(tr);
            rows++;
        }
        return rows > 0 ? el : null;
    }

    private static List<int> DeriveGrid(Table t)
    {
        var count = Math.Max(1, t.Rows.Max(r => r.GridBefore + r.Cells.Sum(c => c.Props.GridSpan) + r.GridAfter));
        var widths = new int[count];
        var row = t.Rows.OrderByDescending(r => r.Cells.Count).First();
        var col = row.GridBefore;
        foreach (var cell in row.Cells)
        {
            var span = cell.Props.GridSpan;
            var w = cell.Props.Width;
            var each = w.Type == "dxa" && w.Value > 0 ? w.Value / span : 0;
            for (var k = 0; k < span && col + k < count; k++) widths[col + k] = each;
            col += span;
        }
        var fallback = 9000 / count;
        return widths.Select(w => w > 0 ? w : fallback).ToList();
    }

    private XElement? SdtElement(SdtBlock s, PartWriter pw)
    {
        if (!s.IsManaged && s.Original != null) return MapRels(new XElement(s.Original), s.SourcePart, pw, s);
        var el = new XElement(Wn.Sdt, new XElement(s.Properties));
        if (s.EndProperties != null) el.Add(new XElement(s.EndProperties));
        var content = new XElement(Wn.SdtContent);
        WriteBlocks(content, s.Blocks, pw, false);
        if (!content.HasElements) content.Add(new XElement(Wn.P));
        el.Add(content);
        return el;
    }

    // ------------------------------------------------------------------ inlines

    private static Item Keyed(Item item, Inline inline)
    {
        item.Link = inline.Link;
        item.Ins = inline.Inserted;
        item.Del = inline.Deleted;
        item.IsMove = inline.IsMove;
        return item;
    }

    private void AddInlineItems(List<Item> items, Inline inline, PartWriter pw)
    {
        switch (inline)
        {
            case TextRun t:
            {
                if (t.Text.Length == 0) return;
                var r = Run(t.Props);
                AddText(r, t.Text, t.Deleted != null);
                if (r.Elements().Any(e => e.Name != Wn.RPr)) items.Add(Keyed(new Item(r), t));
                return;
            }
            case ClearBreak cb:
            {
                var clear = cb.Clear is "left" or "right" or "all" ? cb.Clear : "all";
                items.Add(Keyed(new Item(Run(cb.Props, new XElement(Wn.Br, new XAttribute(Wn.WType, "textWrapping"), new XAttribute(Ns.W + "clear", clear)))), cb));
                return;
            }
            case SymbolChar s:
                items.Add(Keyed(new Item(Run(s.Props, new XElement(Wn.Sym,
                    new XAttribute(Ns.W + "font", string.IsNullOrEmpty(s.Font) ? "Symbol" : s.Font),
                    new XAttribute(Ns.W + "char", ((int)s.Char).ToString("X4", System.Globalization.CultureInfo.InvariantCulture))))), s));
                return;
            case NoteReference n:
            {
                var e = new XElement(n.Kind == NoteKind.Footnote ? Wn.FootnoteReference : Wn.EndnoteReference);
                if (n.CustomMark) e.Add(new XAttribute(Ns.W + "customMarkFollows", "1"));
                e.Add(new XAttribute(Wn.WId, Ox.Str(n.Id)));
                items.Add(Keyed(new Item(Run(n.Props, e)), n));
                return;
            }
            case NoteMark m:
                items.Add(Keyed(new Item(Run(m.Props, new XElement(Ns.W + (m.Kind == NoteKind.Footnote ? "footnoteRef" : "endnoteRef")))), m));
                return;
            case NoteSeparator sep:
                items.Add(Keyed(new Item(Run(sep.Props, new XElement(Ns.W + (sep.Continuation ? "continuationSeparator" : "separator")))), sep));
                return;
            case CommentMark cm:
                items.Add(Keyed(new Item(Run(cm.Props, new XElement(Ns.W + "annotationRef"))), cm));
                return;
            case CommentMarker c:
                switch (c.Kind)
                {
                    case CommentMarkerKind.Reference:
                        items.Add(Keyed(new Item(Run(c.Props, new XElement(Wn.CommentReference, new XAttribute(Wn.WId, Ox.Str(c.Id))))), c));
                        break;
                    case CommentMarkerKind.RangeStart:
                        items.Add(Keyed(new Item(new XElement(Wn.CommentRangeStart, new XAttribute(Wn.WId, Ox.Str(c.Id)))) { Transparent = true }, c));
                        break;
                    default:
                        items.Add(Keyed(new Item(new XElement(Wn.CommentRangeEnd, new XAttribute(Wn.WId, Ox.Str(c.Id)))) { Transparent = true }, c));
                        break;
                }
                return;
            case BookmarkMarker b:
            {
                var e = new XElement(b.IsStart ? Wn.BookmarkStart : Wn.BookmarkEnd);
                if (b.Original != null)
                    foreach (var a in b.Original.Attributes())
                        if (a.Name != Wn.WId && a.Name != Wn.WName && !a.IsNamespaceDeclaration) e.Add(new XAttribute(a));
                e.SetAttributeValue(Wn.WId, Ox.Str(b.Id));
                if (b.IsStart) e.SetAttributeValue(Wn.WName, b.Name ?? "_Bookmark" + Ox.Str(b.Id));
                items.Add(Keyed(new Item(e) { Transparent = true }, b));
                return;
            }
            case DrawingObject d:
            {
                var x = DrawingXml(d, pw);
                if (x != null) items.Add(Keyed(new Item(Run(d.Props, x)), d));
                return;
            }
            case FieldInline f:
                items.AddRange(FieldItems(f, pw, false));
                return;
            case OpaqueInline o:
            {
                var x = MapRels(new XElement(o.Xml), o.SourcePart, pw, o);
                if (x == null) return;
                if (IsParagraphLevel(x)) items.Add(Keyed(new Item(x) { Transparent = o.IsMarker }, o));
                else items.Add(Keyed(new Item(Run(o.Props, x)), o));
                return;
            }
            default:
                Warn("Part of the text Inkwell couldn't save was left out.");
                return;
        }
    }

    private static readonly HashSet<string> ParagraphLevelNames = new(StringComparer.Ordinal)
    {
        "r", "hyperlink", "fldSimple", "sdt", "customXml", "smartTag", "dir", "bdo", "ins", "del", "moveFrom", "moveTo",
        "bookmarkStart", "bookmarkEnd", "commentRangeStart", "commentRangeEnd", "permStart", "permEnd", "proofErr",
        "moveFromRangeStart", "moveFromRangeEnd", "moveToRangeStart", "moveToRangeEnd",
        "customXmlInsRangeStart", "customXmlInsRangeEnd", "customXmlDelRangeStart", "customXmlDelRangeEnd",
        "customXmlMoveFromRangeStart", "customXmlMoveFromRangeEnd", "customXmlMoveToRangeStart", "customXmlMoveToRangeEnd", "subDoc",
    };

    /// <summary>True for elements that sit directly in a paragraph (not inside a w:r).</summary>
    internal static bool IsParagraphLevel(XElement x)
    {
        if (x.Name.Namespace == Ns.W) return ParagraphLevelNames.Contains(x.Name.LocalName);
        if (x.Name == Wn.OMath || x.Name == Wn.OMathPara) return true;
        if (x.Name == Wn.AlternateContent)
        {
            var first = (x.Element(Wn.Choice) ?? x.Element(Wn.Fallback))?.Elements().FirstOrDefault();
            return first != null && IsParagraphLevel(first);
        }
        return false;
    }

    /// <summary>Text with Word's control characters → w:t / w:tab / w:br / w:softHyphen / w:noBreakHyphen.</summary>
    private static void AddText(XElement run, string text, bool deleted)
    {
        var sb = new StringBuilder();
        void Flush()
        {
            if (sb.Length == 0) return;
            var s = sb.ToString();
            var t = new XElement(deleted ? Wn.DelText : Wn.T);
            if (char.IsWhiteSpace(s[0]) || char.IsWhiteSpace(s[s.Length - 1])) t.Add(new XAttribute(Wn.XmlSpace, "preserve"));
            t.Add(s);
            run.Add(t);
            sb.Clear();
        }
        void Add(XElement e)
        {
            Flush();
            run.Add(e);
        }
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case TextRun.Tab:
                    Add(new XElement(Wn.Tab));
                    break;
                case TextRun.LineBreak:
                case '\n':
                    Add(new XElement(Wn.Br));
                    break;
                case '\r':
                    Add(new XElement(Wn.Br));
                    if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                    break;
                case TextRun.PageBreak:
                    Add(new XElement(Wn.Br, new XAttribute(Wn.WType, "page")));
                    break;
                case TextRun.ColumnBreak:
                    Add(new XElement(Wn.Br, new XAttribute(Wn.WType, "column")));
                    break;
                case TextRun.SoftHyphen:
                case '\u001F':
                    Add(new XElement(Wn.SoftHyphen));
                    break;
                case TextRun.NoBreakHyphen:
                case '\u001E':
                    Add(new XElement(Wn.NoBreakHyphen));
                    break;
                default:
                    if (c < 0x20) break; // other control characters can't be stored in XML
                    if (char.IsHighSurrogate(c))
                    {
                        if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                        {
                            sb.Append(c).Append(text[i + 1]);
                            i++;
                        }
                        else
                        {
                            sb.Append('�');
                        }
                        break;
                    }
                    if (char.IsLowSurrogate(c))
                    {
                        sb.Append('�');
                        break;
                    }
                    if (c == '￾' || c == '￿') break;
                    sb.Append(c);
                    break;
            }
        }
        Flush();
    }

    // ------------------------------------------------------------------ fields

    private List<Item> FieldItems(FieldInline f, PartWriter pw, bool insideSdt)
    {
        if (f.PreservedXml != null)
        {
            var list = new List<Item>();
            var children = f.PreservedXml.Name == Wn.IwRuns ? f.PreservedXml.Elements() : new[] { f.PreservedXml };
            foreach (var c in children) list.Add(Keyed(new Item(new XElement(c)), f));
            return list;
        }

        if (f.SdtProperties != null && !insideSdt)
        {
            var sdt = new XElement(Wn.Sdt, new XElement(f.SdtProperties));
            var content = new XElement(Wn.SdtContent);
            var inner = FieldItems(f, pw, true);
            StripKey(inner, f);
            EmitItems(content, inner, pw);
            sdt.Add(content);
            return new List<Item> { Keyed(new Item(sdt), f) };
        }

        if (f.Simple && f.Inserted == null && f.Deleted == null)
        {
            var simple = new XElement(Wn.FldSimple, new XAttribute(Ns.W + "instr", f.Code));
            if (f.Dirty) simple.Add(new XAttribute(Ns.W + "dirty", "true"));
            if (f.Locked) simple.Add(new XAttribute(Ns.W + "fldLock", "true"));
            var inner = new List<Item>();
            foreach (var r in f.Result) AddInlineItems(inner, r, pw);
            StripKey(inner, f);
            EmitItems(simple, inner, pw);
            return new List<Item> { Keyed(new Item(simple), f) };
        }

        var items = new List<Item>();
        var begin = new XElement(Wn.FldChar, new XAttribute(Ns.W + "fldCharType", "begin"));
        if (f.Dirty) begin.Add(new XAttribute(Ns.W + "dirty", "true"));
        if (f.Locked) begin.Add(new XAttribute(Ns.W + "fldLock", "true"));
        items.Add(Keyed(new Item(Run(f.BeginProps ?? f.Props, begin)), f));
        items.Add(Keyed(new Item(Run(f.Props, new XElement(f.Deleted != null ? Wn.DelInstrText : Wn.InstrText, new XAttribute(Wn.XmlSpace, "preserve"), f.Code))), f));
        if (f.Result.Count > 0)
        {
            items.Add(Keyed(new Item(Run(f.Props, new XElement(Wn.FldChar, new XAttribute(Ns.W + "fldCharType", "separate")))), f));
            // The result belongs to the field: it shares the field's hyperlink and tracked change, so the field
            // characters and the result end up inside one w:hyperlink / w:ins (Word refuses a field split across them).
            var first = items.Count;
            foreach (var r in f.Result) AddInlineItems(items, r, pw);
            for (var i = first; i < items.Count; i++)
            {
                items[i].Link ??= f.Link;
                items[i].Ins ??= f.Inserted;
                items[i].Del ??= f.Deleted;
            }
        }
        items.Add(Keyed(new Item(Run(f.Props, new XElement(Wn.FldChar, new XAttribute(Ns.W + "fldCharType", "end")))), f));
        return items;
    }

    /// <summary>Items nested in an element that already carries the field's wrappers don't repeat them.</summary>
    private static void StripKey(List<Item> items, FieldInline f)
    {
        foreach (var item in items)
        {
            if (f.Link != null && ReferenceEquals(item.Link, f.Link)) item.Link = null;
            if (f.Inserted != null && SameRevision(item.Ins, f.Inserted)) item.Ins = null;
            if (f.Deleted != null && SameRevision(item.Del, f.Deleted)) item.Del = null;
        }
    }

    private static bool SameRevision(RevisionInfo? a, RevisionInfo? b) =>
        a == null ? b == null : b != null && a.Author == b.Author && a.Date == b.Date;

    // ------------------------------------------------------------------ grouping into wrappers

    private void EmitItems(XElement parent, List<Item> items, PartWriter pw)
    {
        Group(items, i => i.Link, (a, b) => ReferenceEquals(a, b), (link, group) =>
        {
            XElement target = parent;
            if (link != null && HyperlinkElement(link, pw) is { } h)
            {
                parent.Add(h);
                target = h;
            }
            EmitInsertions(target, group);
        });
    }

    private void EmitInsertions(XElement parent, List<Item> items)
    {
        Group(items, i => i.Ins == null ? null : RevisionKey(i.Ins, i.IsMove), (a, b) => a == b, (key, group) =>
        {
            XElement target = parent;
            if (key != null)
            {
                var first = group.First(i => i.Ins != null);
                var el = new XElement(first.IsMove ? Wn.MoveTo : Wn.Ins);
                first.Ins!.WriteTo(el);
                parent.Add(el);
                target = el;
            }
            EmitDeletions(target, group);
        });
    }

    private static void EmitDeletions(XElement parent, List<Item> items)
    {
        Group(items, i => i.Del == null ? null : RevisionKey(i.Del, i.IsMove && i.Ins == null), (a, b) => a == b, (key, group) =>
        {
            XElement target = parent;
            if (key != null)
            {
                var first = group.First(i => i.Del != null);
                var el = new XElement(first.IsMove && first.Ins == null ? Wn.MoveFrom : Wn.Del);
                first.Del!.WriteTo(el);
                parent.Add(el);
                target = el;
            }
            foreach (var item in group) target.Add(item.Element);
        });
    }

    private static string RevisionKey(RevisionInfo r, bool move) =>
        (move ? "m|" : "r|") + r.Author + "|" + (r.Date?.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "");

    /// <summary>
    /// Splits items into runs of equal keys. A transparent item without a key of its own takes the key of the
    /// group it sits in when the items before and after it share that key.
    /// </summary>
    private static void Group<T>(List<Item> items, Func<Item, T?> keyOf, Func<T?, T?, bool> same, Action<T?, List<Item>> emit) where T : class
    {
        var n = items.Count;
        if (n == 0) return;
        var keys = new T?[n];
        var own = new T?[n];
        for (var i = 0; i < n; i++)
        {
            own[i] = keyOf(items[i]);
            keys[i] = own[i];
        }
        for (var i = 0; i < n; i++)
        {
            if (!items[i].Transparent || own[i] != null) continue;
            T? prev = null;
            T? next = null;
            for (var j = i - 1; j >= 0; j--)
            {
                if (items[j].Transparent && own[j] == null) continue;
                prev = keys[j];
                break;
            }
            for (var j = i + 1; j < n; j++)
            {
                if (items[j].Transparent && own[j] == null) continue;
                next = own[j];
                break;
            }
            keys[i] = prev != null && next != null && same(prev, next) ? prev : null;
        }
        var start = 0;
        for (var i = 1; i <= n; i++)
        {
            if (i < n && same(keys[i], keys[start])) continue;
            emit(keys[start], items.GetRange(start, i - start));
            start = i;
        }
    }

    private XElement? HyperlinkElement(Hyperlink link, PartWriter pw)
    {
        var el = new XElement(Wn.Hyperlink);
        if (link.Shell != null)
        {
            foreach (var a in link.Shell.Attributes())
            {
                if (a.IsNamespaceDeclaration || a.Name == Ns.R + "id" || a.Name == Ns.W + "anchor" || a.Name == Ns.W + "tooltip") continue;
                el.Add(new XAttribute(a));
            }
        }
        else
        {
            el.Add(new XAttribute(Ns.W + "history", "1"));
        }
        if (!string.IsNullOrEmpty(link.Url))
        {
            var id = pw.LinkRel(link.Url!);
            if (id != null) el.SetAttributeValue(Ns.R + "id", id);
            else Warn("A hyperlink with an address that isn't valid was saved as plain text.");
        }
        if (!string.IsNullOrEmpty(link.Anchor)) el.SetAttributeValue(Ns.W + "anchor", link.Anchor);
        if (!string.IsNullOrEmpty(link.Tooltip)) el.SetAttributeValue(Ns.W + "tooltip", link.Tooltip);
        return el;
    }
}
