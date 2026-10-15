using System.Globalization;
using System.Xml.Linq;
using Inkwell.Core.Citations;
using Inkwell.Core.Layout;
using Inkwell.Core.Model;
using Inkwell.Core.Templates;

namespace Inkwell.Core.Editing;

public sealed partial class DocEditor
{
    // ------------------------------------------------------------------ sections and page setup

    /// <summary>The body's top-level block containing a paragraph.</summary>
    private Block? TopBlock(Paragraph p) => DocumentWalker.TopLevel(p) is { } top && top.Story is MainStory ? top : null;

    /// <summary>Inserts a section break at the caret (the new section starts after it).</summary>
    public void InsertSectionBreak(string type)
    {
        Edit("Section break", t =>
        {
            var pos = Selection.IsEmpty ? Selection.Active : DeleteRange(t, Selection.Start, Selection.End);
            var p = pos.Para;
            EnsureEditable(p);
            if (p.Story is not MainStory || Nav.CellOf(p) != null) throw new EditRefusedException("A section break goes between paragraphs of the main text, not in a table, header or note.");
            var current = Doc.SectionOf(p);
            // The paragraph before the break ends the old section.
            Paragraph ending;
            DocPos caret;
            if (pos.Offset == 0 && Nav.Prev(p) is { } prev && TopBlock(prev) is Paragraph prevTop && ReferenceEquals(prevTop, prev) && prev.Props.Section == null)
            {
                ending = prev;
                caret = pos;
            }
            else
            {
                caret = SplitParagraph(t, pos, keepStyle: true);
                ending = p;
            }
            t.Touch(ending);
            var copy = current.Clone();
            ending.Props.Section = copy;
            // The section that now starts after the break begins the way the user asked.
            TouchSection(t, current);
            current.BreakType = type;
            return new Selection(caret);
        });
    }

    /// <summary>Touches whatever holds a section's properties (a paragraph's w:sectPr or the body's last one).</summary>
    private void TouchSection(Transaction t, SectProps section)
    {
        if (ReferenceEquals(section, Doc.Body.FinalSection) || ReferenceEquals(section.X, Doc.Body.FinalSection.X))
        {
            t.TouchFinalSection();
            return;
        }
        foreach (var p in Doc.Body.Blocks.OfType<Paragraph>())
            if (p.Props.Section is { } s && ReferenceEquals(s.X, section.X))
            {
                t.Touch(p);
                return;
            }
    }

    /// <summary>The section props object currently stored for the section that contains a paragraph (live, editable).</summary>
    private SectProps LiveSection(Paragraph p) => Doc.SectionOf(p);

    /// <summary>
    /// "Make this page landscape": puts the current page's content (or the selected paragraphs) in a section of its
    /// own with the other orientation, adding the section breaks before and after for you.
    /// </summary>
    public void MakePageLandscape(bool landscape, DocumentLayout layout)
    {
        var blocks = Doc.Body.Blocks;
        int i0, i1;
        if (!Selection.IsEmpty && Selection.Start.Para.Story is MainStory)
        {
            i0 = blocks.IndexOf(TopBlock(Selection.Start.Para)!);
            i1 = blocks.IndexOf(TopBlock(Selection.End.Para)!);
        }
        else
        {
            var caret = layout.Caret(Selection.Active);
            if (caret == null) return;
            var page = caret.Value.Page;
            var tops = page.Columns.SelectMany(c => c.Content).Select(f => f.Block).Select(b => b is Paragraph para ? TopBlock(para) : DocumentWalker.TopLevel(b))
                .Where(b => b != null).Select(b => blocks.IndexOf(b!)).Where(i => i >= 0).ToList();
            if (tops.Count == 0) return;
            i0 = tops.Min();
            i1 = tops.Max();
        }
        if (i0 < 0 || i1 < 0) return;
        Edit(landscape ? "Make page landscape" : "Make page portrait", t =>
        {
            t.Touch(blocks);
            var original = Doc.SectionOf(blocks[i1] is Paragraph lp ? lp : Nav.LastIn(blocks[i1]) ?? (Paragraph)blocks.First(b => b is Paragraph));
            var sections = Doc.Sections();
            var sec = sections.First(s => i1 >= s.Start && i1 < Math.Max(s.End, s.Start + 1));
            // Section boundaries need paragraphs: after a table, add a small empty one.
            Paragraph EndParagraphAt(int index)
            {
                if (blocks[index] is Paragraph p0) return p0;
                var filler = new Paragraph();
                filler.Props.SpacingAfter = 0;
                filler.Props.SpacingLine = 20;
                filler.Props.SpacingLineRule = "exact";
                blocks.Insert(index + 1, filler);
                return filler;
            }
            var startsSection = i0 == sec.Start;
            var endsSection = sec.BreakParagraph != null && i1 == sec.End - 1 || sec.BreakParagraph == null && i1 == blocks.Count - 1;
            SectProps oriented;
            if (!endsSection)
            {
                var end = EndParagraphAt(i1);
                t.Touch(end);
                oriented = original.Clone();
                end.Props.Section = oriented;
                TouchSection(t, original);
                original.BreakType = "nextPage";
            }
            else
            {
                TouchSection(t, original);
                oriented = original;
            }
            if (!startsSection && i0 > 0)
            {
                var before = EndParagraphAt(i0 - 1);
                t.Touch(before);
                var copy = original.Clone();
                copy.BreakType = sec.Props.BreakType;
                before.Props.Section = copy;
                oriented.BreakType = "nextPage";
            }
            if (oriented.Landscape != landscape)
            {
                var w = oriented.PageWidth;
                oriented.PageWidth = oriented.PageHeight;
                oriented.PageHeight = w;
                oriented.Landscape = landscape;
                // Margins turn with the page.
                var (top, right, bottom, left) = (oriented.MarginTop, oriented.MarginRight, oriented.MarginBottom, oriented.MarginLeft);
                oriented.SetMargins(left, top, right, bottom, oriented.HeaderDistance, oriented.FooterDistance, oriented.Gutter);
            }
            return null;
        });
    }

    /// <summary>Changes the page setup of the caret's section (or every section).</summary>
    public void SetPageSetup(Action<SectProps> change, bool wholeDocument)
    {
        Edit("Page setup", t =>
        {
            if (wholeDocument)
            {
                foreach (var sec in Doc.Sections())
                {
                    TouchSection(t, sec.Props);
                    change(sec.Props);
                }
            }
            else
            {
                var s = LiveSection(Selection.Active.Para.Story is MainStory ? Selection.Active.Para : Nav.FirstIn(Doc.Body.Blocks)!);
                TouchSection(t, s);
                change(s);
            }
            return null;
        });
    }

    public void SetOrientation(bool landscape, bool wholeDocument) => SetPageSetup(s =>
    {
        if (s.Landscape == landscape) return;
        var w = s.PageWidth;
        s.PageWidth = s.PageHeight;
        s.PageHeight = w;
        s.Landscape = landscape;
    }, wholeDocument);

    public void SetPaperSize(int widthTwips, int heightTwips, bool wholeDocument) => SetPageSetup(s =>
    {
        var landscape = s.Landscape;
        s.PageWidth = landscape ? Math.Max(widthTwips, heightTwips) : Math.Min(widthTwips, heightTwips);
        s.PageHeight = landscape ? Math.Min(widthTwips, heightTwips) : Math.Max(widthTwips, heightTwips);
    }, wholeDocument);

    public void SetMargins(int top, int right, int bottom, int left, bool wholeDocument) => SetPageSetup(s =>
        s.SetMargins(top, right, bottom, left, s.HeaderDistance, s.FooterDistance, s.Gutter), wholeDocument);

    public void SetColumns(int count) => SetPageSetup(s =>
    {
        s.ColumnCount = count;
        if (count > 1 && s.Attr("cols", "space") == null) s.ColumnSpace = 567;
    }, false);

    public void SetTitlePage(bool on) => SetPageSetup(s => s.TitlePage = on, false);

    public void SetEvenOddHeaders(bool on) => Edit(on ? "Different odd and even pages" : "Same headers on all pages", t =>
    {
        t.TouchSettings();
        Doc.Settings.EvenAndOddHeaders = on;
        return null;
    });

    // ------------------------------------------------------------------ headers and footers

    /// <summary>The header or footer a page of a section shows (following inheritance), creating one if there's none.</summary>
    public HeaderFooter EnsureHeaderFooter(int sectionIndex, bool header, string kind)
    {
        var sections = Doc.Sections();
        sectionIndex = MathEx.Clamp(sectionIndex, 0, sections.Count - 1);
        for (var s = sectionIndex; s >= 0; s--)
        {
            var id = sections[s].Props.Reference(header, kind);
            if (id != null && Doc.HeadersFooters.TryGetValue(id, out var existing)) return existing;
        }
        HeaderFooter? created = null;
        Edit(header ? "Add header" : "Add footer", t =>
        {
            t.TouchHeadersFooters();
            var story = new HeaderFooter(header) { RelId = Doc.NewRelId() };
            story.Blocks.Add(new Paragraph { Props = { StyleId = header ? "Header" : "Footer" } });
            Doc.HeadersFooters[story.RelId] = story;
            // A document's first header applies from the first section on (later sections inherit it).
            var target = Doc.HeadersFooters.Values.Any(h => h.IsHeader == header && !ReferenceEquals(h, story)) ? sections[sectionIndex].Props : sections[0].Props;
            TouchSection(t, target);
            target.SetReference(header, kind, story.RelId);
            created = story;
            return null;
        });
        return created!;
    }

    /// <summary>Page numbers in one click: a PAGE field in the header or footer, aligned.</summary>
    public void AddPageNumbers(string where, string alignment, bool withTotal = false, int sectionIndex = 0)
    {
        var story = EnsureHeaderFooter(sectionIndex, where == "header", "default");
        var existing = story.Paragraphs.FirstOrDefault(p => p.Inlines.Any(i => i is FieldInline { Kind: FieldKind.Page }));
        Edit("Page numbers", t =>
        {
            if (existing != null)
            {
                t.Touch(existing);
                existing.Props.Justification = alignment == "left" ? null : alignment;
                return null;
            }
            var p = story.Paragraphs.FirstOrDefault(x => x.IsEmpty) ?? new Paragraph { Props = { StyleId = where == "header" ? "Header" : "Footer" } };
            if (!story.Blocks.Contains(p))
            {
                t.Touch(story);
                story.Blocks.Add(p);
            }
            t.Touch(p);
            p.Props.Justification = alignment == "left" ? null : alignment;
            p.Inlines.Clear();
            p.Inlines.Add(new FieldInline { Code = " PAGE ", Result = { new TextRun("1") } });
            if (withTotal)
            {
                p.Inlines.Add(new TextRun(" of "));
                p.Inlines.Add(new FieldInline { Code = " NUMPAGES ", Result = { new TextRun("1") } });
            }
            return null;
        });
    }

    // ------------------------------------------------------------------ simple inserts

    /// <summary>A divider: an empty paragraph with a soft rule under it.</summary>
    public void InsertDivider()
    {
        Edit("Divider", t =>
        {
            var pos = Selection.IsEmpty ? Selection.Active : DeleteRange(t, Selection.Start, Selection.End);
            EnsureEditable(pos.Para);
            var p = pos.Para;
            Paragraph divider;
            if (p.IsEmpty)
            {
                divider = p;
                t.Touch(p);
            }
            else
            {
                var next = SplitParagraph(t, new DocPos(p, p.Length), keepStyle: false);
                divider = next.Para;
            }
            divider.Props.StyleId = null;
            divider.Props.Remove("numPr");
            divider.Props.SetBorder("bottom", new BorderDef("single", 6, 1, "BFBCB4"));
            divider.Props.SpacingAfter = 240;
            var after = SplitParagraph(t, new DocPos(divider, divider.Length), keepStyle: true);
            t.Touch(after.Para);
            after.Para.Props.SetBorder("bottom", null);
            after.Para.Props.SpacingAfter = null;
            return new Selection(after);
        });
    }

    /// <summary>Shift+F3: lower case → UPPER CASE → Title Case → lower case.</summary>
    public void CycleCase()
    {
        if (Selection.IsEmpty)
        {
            var p0 = Selection.Active.Para;
            var (ws, we) = WordAt(p0, Selection.Active.Offset);
            while (we > ws && p0.Text[we - 1] == ' ') we--;
            if (we <= ws) return;
            Selection = new Selection(new DocPos(p0, ws), new DocPos(p0, we));
        }
        var text = string.Concat(SelectedRanges().Select(r => r.Para.Text.Substring(r.Start, r.End - r.Start)));
        var letters = text.Where(char.IsLetter).ToList();
        if (letters.Count == 0) return;
        Func<string, string> transform;
        if (letters.All(char.IsLower)) transform = s => s.ToUpper(CultureInfo.CurrentCulture);
        else if (letters.All(char.IsUpper)) transform = s => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(s.ToLower(CultureInfo.CurrentCulture));
        else transform = s => s.ToLower(CultureInfo.CurrentCulture);
        var sel = Selection;
        Edit("Change case", t =>
        {
            foreach (var (p, start, end) in SelectedRanges())
            {
                if (Nav.IsReadOnly(p)) continue;
                t.Touch(p);
                var first = p.SplitAt(start);
                var last = p.SplitAt(end);
                for (var i = first; i < last; i++)
                    if (p.Inlines[i] is TextRun run) run.Text = transform(run.Text);
                p.Normalize();
            }
            return sel;
        });
    }

    // ------------------------------------------------------------------ designs

    /// <summary>Applies a design: the theme's fonts and colours and the style set (direct formatting stays).</summary>
    public void ApplyDesign(DocumentDesign design)
    {
        Edit("Design: " + design.Name, t =>
        {
            t.TouchStyles();
            t.TouchTheme();
            t.TouchSettings();
            var fresh = design.BuildStyleSheet();
            // Replace the styles the design defines; keep any others the document has.
            var root = Doc.Styles.Root;
            foreach (var style in fresh.All.ToList())
            {
                var existing = root.Elements(Ns.W + "style").FirstOrDefault(e => e.Attr("styleId") == style.Id);
                if (existing != null) existing.ReplaceWith(new XElement(style.X));
                else root.Add(new XElement(style.X));
            }
            var defaults = root.Element(Ns.W + "docDefaults");
            var freshDefaults = fresh.Root.Element(Ns.W + "docDefaults");
            if (freshDefaults != null)
            {
                if (defaults != null) defaults.ReplaceWith(new XElement(freshDefaults));
                else root.AddFirst(new XElement(freshDefaults));
            }
            Doc.Styles.Changed();
            Doc.Theme.Reset(design.BuildTheme().Doc);
            Doc.Settings.SetVariable("InkwellDesign", design.Id);
            return null;
        });
    }

    // ------------------------------------------------------------------ track changes

    public void SetTrackChanges(bool on) => Edit(on ? "Track changes on" : "Track changes off", t =>
    {
        t.TouchSettings();
        Doc.Settings.TrackRevisions = on;
        return null;
    });

    // ------------------------------------------------------------------ table of contents

    private static readonly Random TocIds = new();

    public bool HasTableOfContents() => DocumentWalker.AllBlocks(Doc.Body.Blocks).OfType<FieldBlock>().Any(f => f.Kind == FieldKind.Toc);

    /// <summary>The headings a TOC lists (outline levels within the \o range).</summary>
    private List<(Paragraph Para, int Level)> TocHeadings(int maxLevel)
    {
        var list = new List<(Paragraph, int)>();
        foreach (var p in Doc.Body.Paragraphs)
        {
            if (Nav.IsReadOnly(p)) continue;
            var rp = Styles.ResolvePara(p);
            if (rp.OutlineLevel < 0 || rp.OutlineLevel >= maxLevel) continue;
            if (p.PlainText().Trim().Length == 0) continue;
            list.Add((p, rp.OutlineLevel));
        }
        return list;
    }

    /// <summary>A hidden bookmark around a paragraph's text (for TOC links and cross-references).</summary>
    private string EnsureParagraphBookmark(Transaction t, Paragraph p, string prefix)
    {
        foreach (var b in p.Inlines.OfType<BookmarkMarker>())
            if (b.IsStart && b.Name != null && b.Name.StartsWith(prefix, StringComparison.Ordinal)) return b.Name;
        string name;
        lock (TocIds) name = prefix + TocIds.Next(10000000, 99999999).ToString(CultureInfo.InvariantCulture);
        var id = Doc.NextBookmarkId();
        t.Touch(p);
        p.Inlines.Insert(0, new BookmarkMarker { Id = id, Name = name, IsStart = true });
        p.Inlines.Add(new BookmarkMarker { Id = id, IsStart = false });
        return name;
    }

    /// <summary>Inserts a table of contents at the caret (Word-compatible: a TOC field in a content control).</summary>
    public void InsertTableOfContents(int levels = 3)
    {
        Edit("Table of contents", t =>
        {
            var pos = Selection.Active;
            var p = pos.Para;
            EnsureEditable(p);
            if (p.Story is not MainStory || Nav.CellOf(p) != null) throw new EditRefusedException("A table of contents goes in the main text.");
            var list = p.Parent!;
            t.Touch(list);
            var heading = new Paragraph("Contents", "TOCHeading");
            var field = new FieldBlock { Code = $" TOC \\o \"1-{levels}\" \\h \\z \\u " };
            var sdt = DocBuilder.ContentControl("Table of Contents", new Block[] { heading, field });
            var index = list.IndexOf(p);
            if (!p.IsEmpty) index++;
            list.Insert(index, sdt);
            FillToc(t, field, levels, null);
            return new Selection(pos);
        });
    }

    /// <summary>Regenerates every table of contents (entries, links and page numbers from the layout).</summary>
    public void UpdateTablesOfContents(DocumentLayout layout)
    {
        var fields = DocumentWalker.AllBlocks(Doc.Body.Blocks).OfType<FieldBlock>().Where(f => f.Kind == FieldKind.Toc).ToList();
        if (fields.Count == 0) return;
        Edit("Update table of contents", t =>
        {
            foreach (var f in fields) FillToc(t, f, LevelsOf(f), layout);
            // The table itself may have moved headings to other pages: once more with the new layout.
            layout.Update();
            foreach (var f in fields) FillToc(t, f, LevelsOf(f), layout);
            return null;
        });
    }

    private static int LevelsOf(FieldBlock f)
    {
        var range = FieldCode.Parse(f.Code).SwitchValue("\\o");
        if (range != null && range.Contains('-') && int.TryParse(range.Split('-')[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var max)) return MathEx.Clamp(max, 1, 9);
        return 3;
    }

    private void FillToc(Transaction t, FieldBlock field, int levels, DocumentLayout? layout)
    {
        t.Touch(field.Blocks);
        var headings = TocHeadings(levels);
        var section = Doc.SectionOf(field.Parent?.Owner is Block owner ? owner : field);
        var tabPos = section.PageWidth - section.MarginLeft - section.MarginRight - section.Gutter;
        var blocks = new List<Block>();
        foreach (var (p, level) in headings)
        {
            var bookmark = EnsureParagraphBookmark(t, p, "_Toc");
            var page = layout != null ? PageNumberOf(layout, p) : "";
            var entry = new Paragraph { Props = { StyleId = "TOC" + (level + 1) } };
            if (Doc.Styles.Get(entry.Props.StyleId) == null) entry.Props.StyleId = null;
            entry.Props.Tabs = new List<TabStopDef> { new("right", tabPos, "dot") };
            var link = new Hyperlink { Anchor = bookmark };
            var text = p.PlainText().Replace('\t', ' ').Replace('\n', ' ').Trim();
            entry.Inlines.Add(new TextRun(text) { Link = link });
            entry.Inlines.Add(new TextRun("\t") { Link = link });
            var pageRef = new FieldInline { Code = $" PAGEREF {bookmark} \\h ", Result = { new TextRun(page) }, Link = link };
            entry.Inlines.Add(pageRef);
            blocks.Add(entry);
        }
        if (blocks.Count == 0) blocks.Add(new Paragraph("No headings yet. Use the Heading styles and update the table of contents."));
        field.Blocks.Clear();
        field.Blocks.AddRange(blocks);
        field.BeginAt = 0;
        field.EndAt = ((Paragraph)blocks[blocks.Count - 1]).Inlines.Count;
        field.Dirty = false;
    }

    /// <summary>The page number shown on the page where a paragraph starts.</summary>
    public static string PageNumberOf(DocumentLayout layout, Paragraph p)
    {
        var loc = layout.Locate(p).FirstOrDefault(l => l.Region == PageRegion.Body);
        return loc?.Page.NumberText ?? "";
    }

    // ------------------------------------------------------------------ captions and cross-references

    /// <summary>Inserts a caption ("Figure 1: text") below or above the selection, numbered by a SEQ field.</summary>
    public Paragraph InsertCaption(string label, string text, bool above = false)
    {
        Paragraph? created = null;
        Edit("Caption", t =>
        {
            var p = Selection.Active.Para;
            EnsureEditable(p);
            var list = p.Parent!;
            t.Touch(list);
            // Under a table: after it; under a picture paragraph: after that paragraph.
            Block anchor = Nav.CellOf(p)?.Row?.Table is { } table && ReferenceEquals(table.Parent, list) ? table : (Block?)TopIn(list, p) ?? p;
            var caption = new Paragraph { Props = { StyleId = "Caption" } };
            var id = Doc.NextBookmarkId();
            string name;
            lock (TocIds) name = "_Ref" + TocIds.Next(100000000, 999999999).ToString(CultureInfo.InvariantCulture);
            caption.Inlines.Add(new BookmarkMarker { Id = id, Name = name, IsStart = true });
            caption.Inlines.Add(new TextRun(label + " "));
            caption.Inlines.Add(new FieldInline { Code = $" SEQ {label} \\* ARABIC ", Result = { new TextRun("1") } });
            caption.Inlines.Add(new BookmarkMarker { Id = id, IsStart = false });
            if (text.Length > 0) caption.Inlines.Add(new TextRun(": " + text));
            var index = list.IndexOf(anchor);
            list.Insert(above ? index : index + 1, caption);
            if (above) caption.Props.KeepNext = true;
            created = caption;
            UpdateSequenceFields(t);
            return new Selection(new DocPos(caption, caption.Length));
        });
        return created!;
    }

    /// <summary>Renumbers SEQ fields (Figure 1, 2…) in reading order.</summary>
    private void UpdateSequenceFields(Transaction t)
    {
        var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Doc.Body.Paragraphs)
        {
            foreach (var f in p.Inlines.OfType<FieldInline>().Where(f => f.Kind == FieldKind.Seq && !f.Locked))
            {
                var code = FieldCode.Parse(f.Code);
                var id = code.FirstArgument ?? "";
                counters.TryGetValue(id, out var n);
                if (code.SwitchValue("\\r") is { } reset && int.TryParse(reset, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r)) n = r;
                else if (!code.Has("\\c")) n++;
                counters[id] = n;
                var text = FieldCode.FormatNumber(n, code.NumberFormat);
                if (f.ResultText == text) continue;
                t.Touch(p);
                SetResult(f, text);
            }
        }
    }

    private static void SetResult(FieldInline f, string text)
    {
        var props = f.Result.FirstOrDefault()?.Props ?? f.Props.Clone();
        f.Result.Clear();
        if (text.Length > 0) f.Result.Add(new TextRun(text) { Props = props.Clone() });
    }

    /// <summary>Things a cross-reference can point to: headings and captions.</summary>
    public List<(string Kind, string Text, Paragraph Para)> CrossReferenceTargets()
    {
        var list = new List<(string, string, Paragraph)>();
        foreach (var p in Doc.Body.Paragraphs)
        {
            if (Nav.IsReadOnly(p)) continue;
            var rp = Styles.ResolvePara(p);
            var text = p.PlainText().Trim();
            if (text.Length == 0) continue;
            if (rp.OutlineLevel is >= 0 and <= 8) list.Add(("Heading", text, p));
            else if (p.Inlines.OfType<FieldInline>().FirstOrDefault(f => f.Kind == FieldKind.Seq) is { } seq)
                list.Add((FieldCode.Parse(seq.Code).FirstArgument ?? "Caption", text, p));
        }
        return list;
    }

    /// <summary>
    /// Inserts a cross-reference: to the heading text, a caption's label and number, the page number, or
    /// "above / below". It's a REF / PAGEREF field to a hidden bookmark, so Word updates it too.
    /// </summary>
    public void InsertCrossReference(Paragraph target, string show, DocumentLayout? layout)
    {
        Edit("Cross-reference", t =>
        {
            EnsureEditable(Selection.Active.Para);
            string bookmark;
            string result;
            var seq = target.Inlines.OfType<FieldInline>().FirstOrDefault(f => f.Kind == FieldKind.Seq);
            if (seq != null && show != "text")
            {
                // The caption's label and number are already bookmarked by InsertCaption, else bookmark them now.
                bookmark = target.Inlines.OfType<BookmarkMarker>().FirstOrDefault(b => b.IsStart && b.Name?.StartsWith("_Ref", StringComparison.Ordinal) == true)?.Name
                           ?? EnsureParagraphBookmark(t, target, "_Ref");
                result = (FieldCode.Parse(seq.Code).FirstArgument ?? "") + " " + seq.ResultText;
            }
            else
            {
                bookmark = EnsureParagraphBookmark(t, target, "_Ref");
                result = target.PlainText().Trim();
            }
            var pos = Selection.IsEmpty ? Selection.Active : DeleteRange(t, Selection.Start, Selection.End);
            FieldInline field;
            switch (show)
            {
                case "page":
                    field = new FieldInline { Code = $" PAGEREF {bookmark} \\h ", Result = { new TextRun(layout != null ? PageNumberOf(layout, target) : "1") } };
                    break;
                case "position":
                    var below = Nav.Compare(new DocPos(target, 0), pos) < 0 ? "above" : "below";
                    field = new FieldInline { Code = $" REF {bookmark} \\p \\h ", Result = { new TextRun(below) } };
                    break;
                default:
                    field = new FieldInline { Code = $" REF {bookmark} \\h ", Result = { new TextRun(result) } };
                    break;
            }
            field.Props = pos.Para.PropsForTypingAt(pos.Offset);
            t.Touch(pos.Para);
            pos.Para.InsertInline(pos.Offset, field);
            return new Selection(new DocPos(pos.Para, pos.Offset + 1));
        });
    }

    // ------------------------------------------------------------------ fields

    /// <summary>F9: updates captions, cross-references, page references, citations, bibliographies and tables of contents.</summary>
    public void UpdateFields(DocumentLayout layout)
    {
        Edit("Update fields", t =>
        {
            UpdateSequenceFields(t);
            var bookmarks = new Dictionary<string, Paragraph>(StringComparer.OrdinalIgnoreCase);
            foreach (var story in Doc.Stories)
                foreach (var p in story.Paragraphs)
                    foreach (var b in p.Inlines.OfType<BookmarkMarker>().Where(b => b.IsStart && b.Name != null))
                        bookmarks[b.Name!] = p;
            var engine = CitationEngine.ForDocument(Doc, CitationStyles.FromName(Doc.Sources.StyleName));
            foreach (var story in Doc.Stories)
            {
                foreach (var p in story.Paragraphs.ToList())
                {
                    foreach (var f in DocumentWalker.DeepInlines(p).OfType<FieldInline>().ToList())
                    {
                        if (f.Locked || f.PreservedXml != null) continue;
                        var code = FieldCode.Parse(f.Code);
                        string? text = null;
                        switch (f.Kind)
                        {
                            case FieldKind.Ref when code.FirstArgument is { } name && bookmarks.TryGetValue(name, out var target):
                                if (code.Has("\\p")) text = Nav.Compare(new DocPos(target, 0), new DocPos(p, 0)) < 0 ? "above" : "below";
                                else
                                {
                                    var seq = target.Inlines.OfType<FieldInline>().FirstOrDefault(x => x.Kind == FieldKind.Seq);
                                    text = seq != null && name.StartsWith("_Ref", StringComparison.Ordinal) && target.Inlines.OfType<BookmarkMarker>().First(b => b.Name == name) is { } bm && target.Inlines.IndexOf(bm) < target.Inlines.IndexOf(seq)
                                        ? (FieldCode.Parse(seq.Code).FirstArgument ?? "") + " " + seq.ResultText
                                        : target.PlainText().Trim();
                                }
                                break;
                            case FieldKind.PageRef when code.FirstArgument is { } name2 && bookmarks.TryGetValue(name2, out var target2):
                                text = PageNumberOf(layout, target2);
                                break;
                            case FieldKind.Citation:
                                t.Touch(p);
                                CitationModel.UpdateResult(f, engine);
                                continue;
                            case FieldKind.NumPages:
                                text = layout.Pages.Count.ToString(CultureInfo.InvariantCulture);
                                break;
                            case FieldKind.Date:
                                text = FieldDisplay.FormatDate(DateTime.Now, code.SwitchValue("\\@"));
                                break;
                        }
                        if (text == null || f.ResultText == text) continue;
                        t.Touch(p);
                        SetResult(f, text);
                    }
                }
            }
            foreach (var f in DocumentWalker.AllBlocks(Doc.Body.Blocks).OfType<FieldBlock>().ToList())
            {
                if (f.Kind == FieldKind.Toc) FillToc(t, f, LevelsOf(f), layout);
                else if (f.Kind == FieldKind.Bibliography) FillBibliography(t, f);
            }
            return null;
        });
    }

    // ------------------------------------------------------------------ citations

    /// <summary>Inserts a citation of one or more sources at the caret (Word-compatible CITATION field in a content control).</summary>
    public void InsertCitation(IReadOnlyList<CitationRef> refs)
    {
        if (refs.Count == 0) return;
        Edit("Citation", t =>
        {
            var pos = Selection.IsEmpty ? Selection.Active : DeleteRange(t, Selection.Start, Selection.End);
            EnsureEditable(pos.Para);
            var engine = CitationEngine.ForDocument(Doc, CitationStyles.FromName(Doc.Sources.StyleName));
            var field = new CitationField(refs.ToArray()) { LanguageId = 1033 };
            var inline = CitationModel.CitationInline(engine, field, pos.Para.PropsForTypingAt(pos.Offset));
            inline.SdtProperties = new XElement(Ns.W + "sdtPr",
                new XElement(Ns.W + "id", new XAttribute(Ox.Val, Ox.Str(Compat.NextRandom(1, int.MaxValue)))),
                new XElement(Ns.W + "citation"));
            t.Touch(pos.Para);
            pos.Para.InsertInline(pos.Offset, inline);
            // Numbers (IEEE) and "2020a" letters may change elsewhere: refresh the other citations.
            var refreshed = CitationEngine.ForDocument(Doc, CitationStyles.FromName(Doc.Sources.StyleName));
            foreach (var p in Doc.Body.Paragraphs)
                foreach (var f in p.Inlines.OfType<FieldInline>().Where(f => f.Kind == FieldKind.Citation).ToList())
                {
                    var before = f.ResultText;
                    var copy = (FieldInline)f.Clone();
                    CitationModel.UpdateResult(copy, refreshed);
                    if (copy.ResultText == before) continue;
                    t.Touch(p);
                    CitationModel.UpdateResult(f, refreshed);
                }
            foreach (var f in DocumentWalker.AllBlocks(Doc.Body.Blocks).OfType<FieldBlock>().Where(f => f.Kind == FieldKind.Bibliography).ToList()) FillBibliography(t, f);
            return new Selection(new DocPos(pos.Para, pos.Offset + 1));
        });
    }

    /// <summary>Adds or replaces a source (by tag).</summary>
    public void SaveSource(BibSource source, string? oldTag = null)
    {
        Edit("Source", t =>
        {
            t.TouchSources();
            Doc.Sources.Items.RemoveAll(s => string.Equals(s.Tag, oldTag ?? source.Tag, StringComparison.OrdinalIgnoreCase));
            Doc.Sources.Items.Add(source);
            return null;
        });
    }

    public void DeleteSource(string tag) => Edit("Delete source", t =>
    {
        t.TouchSources();
        Doc.Sources.Items.RemoveAll(s => string.Equals(s.Tag, tag, StringComparison.OrdinalIgnoreCase));
        return null;
    });

    /// <summary>Changes the citation style (APA, MLA, Chicago, IEEE) and reformats citations and bibliographies.</summary>
    public void SetCitationStyle(string style)
    {
        Edit("Citation style " + style, t =>
        {
            t.TouchSources();
            Doc.Sources.StyleName = style;
            var engine = CitationEngine.ForDocument(Doc, CitationStyles.FromName(style));
            foreach (var p in Doc.Body.Paragraphs)
                foreach (var f in p.Inlines.OfType<FieldInline>().Where(f => f.Kind == FieldKind.Citation).ToList())
                {
                    t.Touch(p);
                    CitationModel.UpdateResult(f, engine);
                }
            foreach (var f in DocumentWalker.AllBlocks(Doc.Body.Blocks).OfType<FieldBlock>().Where(f => f.Kind == FieldKind.Bibliography).ToList()) FillBibliography(t, f);
            return null;
        });
    }

    /// <summary>Inserts the bibliography (references) at the caret.</summary>
    public void InsertBibliography()
    {
        Edit("Bibliography", t =>
        {
            var p = Selection.Active.Para;
            EnsureEditable(p);
            if (p.Story is not MainStory || Nav.CellOf(p) != null) throw new EditRefusedException("A bibliography goes in the main text.");
            var list = p.Parent!;
            t.Touch(list);
            var style = CitationStyles.FromName(Doc.Sources.StyleName);
            var heading = new Paragraph(CitationStyles.BibliographyTitle(style), "Heading1");
            var field = new FieldBlock { Code = BibliographyField.ToCode(1033) };
            var sdt = DocBuilder.ContentControl("Bibliographies", new Block[] { heading, field });
            var index = list.IndexOf(p);
            if (!p.IsEmpty) index++;
            list.Insert(index, sdt);
            FillBibliography(t, field);
            return null;
        });
    }

    private void FillBibliography(Transaction t, FieldBlock field)
    {
        t.Touch(field.Blocks);
        var paragraphs = CitationModel.BibliographyParagraphs(Doc);
        if (paragraphs.Count == 0) paragraphs.Add(new Paragraph(CitationEngine.NoSourcesText));
        field.Blocks.Clear();
        field.Blocks.AddRange(paragraphs);
        field.BeginAt = 0;
        field.EndAt = paragraphs[paragraphs.Count - 1].Inlines.Count;
    }
}
