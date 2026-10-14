using System.Xml.Linq;
using Inkwell.Core.IO;
using Inkwell.Core.Model;

namespace Inkwell.Tests;

public class DocxStructureTests
{
    private static readonly XNamespace W = Ns.W;

    private static TableCell Cell(string text, int width = 3000)
    {
        var c = TableCell.Create(width, new Paragraph(text));
        return c;
    }

    [Fact]
    public void Tables_MergedCellsBordersShadingHeaderRowsAndNestedTables_RoundTrip()
    {
        var doc = new Document();
        var t = new Table { Grid = new List<int> { 3000, 3000, 3000 } };
        t.Props.StyleId = "TableGrid";
        t.Props.Width = new TableWidth("pct", 5000);
        t.Props.SetBorder("top", BorderDef.Single(8, "1F2A44"));
        t.Props.SetBorder("insideH", BorderDef.Single(4));
        t.Props.Look = (true, false, true, false, true, false);

        var header = new TableRow();
        header.Props.IsHeader = true;
        header.Props.CantSplit = true;
        var merged = Cell("Header across two", 6000);
        merged.Props.GridSpan = 2;
        merged.Props.ShadingFill = "DDE4F0";
        header.AddCell(merged);
        header.AddCell(Cell("H3"));
        t.AddRow(header);

        var row2 = new TableRow();
        var tall = Cell("Tall");
        tall.Props.VerticalMerge = "restart";
        tall.Props.VerticalAlignment = "center";
        tall.Props.SetBorder("left", BorderDef.Single(12, "C00000"));
        row2.AddCell(tall);
        row2.AddCell(Cell("b2"));
        var nestedHolder = Cell("Nested:");
        var nested = new Table { Grid = new List<int> { 1400, 1400 } };
        var nr = new TableRow();
        nr.AddCell(Cell("n1", 1400));
        nr.AddCell(Cell("n2", 1400));
        nested.AddRow(nr);
        nestedHolder.Blocks.Add(nested); // the cell now ends with a table: the writer adds the paragraph Word needs
        row2.AddCell(nestedHolder);
        t.AddRow(row2);

        var row3 = new TableRow();
        row3.Props.Height = (600, "exact");
        var cont = new TableCell();
        cont.Props.VerticalMerge = "continue";
        row3.AddCell(cont); // no paragraph at all: the writer adds one
        row3.AddCell(Cell("c2"));
        row3.AddCell(Cell("c3"));
        t.AddRow(row3);

        doc.Body.Blocks.Add(t);
        doc.Body.Blocks.Add(new Paragraph("After"));

        var back = Docx.RoundTrip(doc, out var bytes);
        var bt = Assert.IsType<Table>(back.Body.Blocks[0]);
        Assert.Equal(new[] { 3000, 3000, 3000 }, bt.Grid);
        Assert.Equal(3, bt.Rows.Count);
        Assert.True(bt.Rows[0].Props.IsHeader);
        Assert.Equal(2, bt.Rows[0].Cells[0].Props.GridSpan);
        Assert.Equal("DDE4F0", bt.Rows[0].Cells[0].Props.ShadingFill);
        Assert.Equal("restart", bt.Rows[1].Cells[0].Props.VerticalMerge);
        Assert.Equal("continue", bt.Rows[2].Cells[0].Props.VerticalMerge);
        Assert.Equal("C00000", bt.Rows[1].Cells[0].Props.Border("left")!.Value.Color);
        Assert.Equal((600, "exact"), bt.Rows[2].Props.Height);
        Assert.True(Ox.SameXml(t.Props.X, bt.Props.X));
        var holder = bt.Rows[1].Cells[2];
        Assert.IsType<Table>(holder.Blocks[1]);
        Assert.IsType<Paragraph>(holder.Blocks[2]);
        Assert.Single(bt.Rows[2].Cells[0].Blocks);

        // Every cell ends with a paragraph in the XML.
        foreach (var tc in Docx.Body(bytes).Descendants(W + "tc"))
            Assert.Equal(W + "p", tc.Elements().Last().Name);
    }

    [Fact]
    public void Sections_SizeOrientationMarginsColumnsAndBreakTypes_RoundTrip()
    {
        var doc = new Document();
        var first = SectProps.CreateDefault(false);
        first.ColumnCount = 2;
        first.ColumnSpace = 567;
        first.ColumnSeparator = true;
        first.PageNumberStart = 3;
        first.PageNumberFormat = "lowerRoman";
        var breakPara = new Paragraph("End of section one");
        breakPara.Props.Section = first;
        doc.Body.Blocks.Add(new Paragraph("Section one"));
        doc.Body.Blocks.Add(breakPara);
        doc.Body.Blocks.Add(new Paragraph("Section two"));
        var last = SectProps.CreateDefault(true);
        last.Landscape = true;
        last.PageWidth = 15840;
        last.PageHeight = 12240;
        last.BreakType = "continuous";
        last.SetMargins(720, 720, 720, 720, 360, 360, 0);
        last.VerticalAlignment = "center";
        doc.Body.FinalSection = last;

        var back = Docx.RoundTrip(doc);
        var sections = back.Sections();
        Assert.Equal(2, sections.Count);
        Assert.Equal(2, sections[0].Props.ColumnCount);
        Assert.True(sections[0].Props.ColumnSeparator);
        Assert.Equal(3, sections[0].Props.PageNumberStart);
        Assert.Equal("lowerRoman", sections[0].Props.PageNumberFormat);
        Assert.True(sections[1].Props.Landscape);
        Assert.Equal(15840, sections[1].Props.PageWidth);
        Assert.Equal("continuous", sections[1].Props.BreakType);
        Assert.Equal(720, sections[1].Props.MarginTop);
        Assert.Equal("center", sections[1].Props.VerticalAlignment);
    }

    [Fact]
    public void HeadersAndFooters_DefaultFirstEven_DifferentPerSection()
    {
        var doc = new Document();
        doc.Settings.EvenAndOddHeaders = true;
        HeaderFooter Make(bool header, string text)
        {
            var hf = new HeaderFooter(header) { RelId = doc.NewRelId() };
            var p = new Paragraph(text, header ? "Header" : "Footer");
            if (!header) p.Inlines.Add(new FieldInline { Code = " PAGE ", Result = { new TextRun("1") } });
            hf.Blocks.Add(p);
            doc.HeadersFooters[hf.RelId] = hf;
            return hf;
        }
        var s1 = SectProps.CreateDefault(false);
        s1.TitlePage = true;
        s1.SetReference(true, "default", Make(true, "Header one").RelId);
        s1.SetReference(true, "first", Make(true, "First page header").RelId);
        s1.SetReference(true, "even", Make(true, "Even header").RelId);
        s1.SetReference(false, "default", Make(false, "Footer one ").RelId);
        var bp = new Paragraph("Section one");
        bp.Props.Section = s1;
        doc.Body.Blocks.Add(bp);
        doc.Body.Blocks.Add(new Paragraph("Section two"));
        var s2 = SectProps.CreateDefault(false);
        s2.SetReference(true, "default", Make(true, "Header two").RelId);
        doc.Body.FinalSection = s2;

        var back = Docx.RoundTrip(doc, out var bytes);
        Assert.Equal(5, back.HeadersFooters.Count);
        Assert.Equal(4, back.HeadersFooters.Values.Count(h => h.IsHeader));
        var sections = back.Sections();
        string TextOf(SectProps s, bool header, string kind) => back.HeadersFooters[s.Reference(header, kind)!].Paragraphs.First().PlainText();
        Assert.Equal("Header one", TextOf(sections[0].Props, true, "default"));
        Assert.Equal("First page header", TextOf(sections[0].Props, true, "first"));
        Assert.Equal("Even header", TextOf(sections[0].Props, true, "even"));
        Assert.Equal("Footer one 1", TextOf(sections[0].Props, false, "default"));
        Assert.Equal("Header two", TextOf(sections[1].Props, true, "default"));
        Assert.True(sections[0].Props.TitlePage);
        Assert.True(back.Settings.EvenAndOddHeaders);
        var footer = back.HeadersFooters[sections[0].Props.Reference(false, "default")!];
        Assert.Equal(FieldKind.Page, Assert.IsType<FieldInline>(footer.Paragraphs.First().Inlines[1]).Kind);
        Assert.All(back.HeadersFooters.Values, h => Assert.NotNull(h.PartUri));
        // Each part keeps the id the section refers to.
        foreach (var pair in back.HeadersFooters) Assert.Equal(pair.Key, pair.Value.RelId);
    }

    [Fact]
    public void HeaderNoLongerReferenced_IsDeleted_AndMissingOnesAreDropped()
    {
        var doc = new Document();
        var hf = new HeaderFooter(true) { RelId = doc.NewRelId() };
        hf.Blocks.Add(new Paragraph("Header"));
        doc.HeadersFooters[hf.RelId] = hf;
        doc.Body.FinalSection.SetReference(true, "default", hf.RelId);
        doc.Body.Blocks.Add(new Paragraph("Body"));
        var bytes = Docx.Save(doc);
        Assert.Contains(Docx.PartNames(bytes), n => n.StartsWith("word/header", StringComparison.Ordinal));

        var loaded = Docx.Load(bytes);
        loaded.Body.FinalSection.SetReference(true, "default", null);
        loaded.Body.FinalSection.SetReference(false, "default", "rIdNotThere");
        var again = Docx.Save(loaded);
        Docx.AssertValid(again);
        Assert.DoesNotContain(Docx.PartNames(again), n => n.StartsWith("word/header", StringComparison.Ordinal));
        Assert.Empty(Docx.Body(again).Descendants(W + "footerReference"));
    }

    [Fact]
    public void Hyperlinks_ExternalAndInternal_RoundTrip()
    {
        var doc = new Document();
        var p = new Paragraph();
        var external = new Hyperlink { Url = "https://example.com/search?q=ink&lang=en#results", Tooltip = "Search" };
        p.Inlines.Add(new TextRun("Visit "));
        p.Inlines.Add(new TextRun("example") { Link = external });
        var bold = new TextRun(".com") { Link = external };
        bold.Props.Bold = true;
        p.Inlines.Add(bold);
        p.Inlines.Add(new TextRun(" or jump to "));
        p.Inlines.Add(new TextRun("the end") { Link = new Hyperlink { Anchor = "TheEnd" } });
        doc.Body.Blocks.Add(p);
        var target = new Paragraph("The end");
        target.Inlines.Insert(0, new BookmarkMarker { Id = 0, Name = "TheEnd", IsStart = true });
        target.Inlines.Add(new BookmarkMarker { Id = 0, IsStart = false });
        doc.Body.Blocks.Add(target);

        var back = Docx.RoundTrip(doc, out var bytes);
        var hyperlinks = Docx.Body(bytes).Descendants(W + "hyperlink").ToList();
        Assert.Equal(2, hyperlinks.Count); // the two runs of the first link share one w:hyperlink
        var bp = (Paragraph)back.Body.Blocks[0];
        var l1 = bp.Inlines[1].Link!;
        Assert.Same(l1, bp.Inlines[2].Link);
        Assert.Equal(external.Url, l1.Url);
        Assert.Equal("Search", l1.Tooltip);
        Assert.Equal("TheEnd", bp.Inlines[4].Link!.Anchor);
        Assert.Null(bp.Inlines[4].Link!.Url);
        Assert.Contains(back.Bookmarks(), b => b.Name == "TheEnd");

        // Saving again reuses the relationship instead of adding another one.
        var again = Docx.Save(back);
        var rels = Docx.PartXml(again, "word/_rels/document.xml.rels").Root!.Elements().Count(e => (string?)e.Attribute("TargetMode") == "External");
        Assert.Equal(1, rels);
    }

    [Fact]
    public void Bookmarks_GetUniquePairedIds()
    {
        var doc = new Document();
        for (var i = 0; i < 3; i++)
        {
            var p = new Paragraph("Para " + i);
            // Every bookmark uses id 7: the writer makes them unique and keeps starts and ends paired.
            p.Inlines.Insert(0, new BookmarkMarker { Id = 7, Name = "B" + i, IsStart = true });
            p.Inlines.Add(new BookmarkMarker { Id = 7, IsStart = false });
            doc.Body.Blocks.Add(p);
        }
        var back = Docx.RoundTrip(doc, out var bytes);
        var starts = Docx.Body(bytes).Descendants(W + "bookmarkStart").Select(e => (string?)e.Attribute(W + "id")).ToList();
        Assert.Equal(3, starts.Distinct().Count());
        foreach (var p in back.Body.Blocks.OfType<Paragraph>())
        {
            var markers = p.Inlines.OfType<BookmarkMarker>().ToList();
            Assert.Equal(markers[0].Id, markers[1].Id);
        }
        Assert.Equal(new[] { "B0", "B1", "B2" }, back.Bookmarks().Select(b => b.Name).ToArray());
    }

    [Fact]
    public void MarkersBetweenBlocks_BecomeMarkerBlocks()
    {
        var bytes = TestDocx.Create("<w:bookmarkStart w:id=\"3\" w:name=\"AroundTable\"/>" +
                                    "<w:tbl><w:tblPr/><w:tblGrid><w:gridCol w:w=\"2000\"/></w:tblGrid><w:tr><w:tc><w:p><w:r><w:t>cell</w:t></w:r></w:p></w:tc></w:tr></w:tbl>" +
                                    "<w:bookmarkEnd w:id=\"3\"/><w:permStart w:id=\"1\" w:edGrp=\"everyone\"/>" + TestDocx.P("after") + "<w:permEnd w:id=\"1\"/>");
        var doc = Docx.Load(bytes);
        var m1 = Assert.IsType<MarkerBlock>(doc.Body.Blocks[0]);
        Assert.Equal("AroundTable", Assert.IsType<BookmarkMarker>(m1.Markers[0]).Name);
        Assert.IsType<Table>(doc.Body.Blocks[1]);
        var m2 = Assert.IsType<MarkerBlock>(doc.Body.Blocks[2]);
        Assert.Single(m2.Markers);
        Assert.Single(m2.Other);
        var again = Docx.Save(doc);
        Docx.AssertValid(again);
        var body = Docx.Body(again);
        Assert.Equal(W + "bookmarkStart", body.Elements().First().Name);
        Assert.Single(body.Elements(W + "permStart"));
    }

    [Fact]
    public void FootnotesAndEndnotes_RoundTrip_WithSeparatorsAdded()
    {
        var doc = new Document();
        Note MakeNote(NoteKind kind, int id, string text)
        {
            var note = new Note(kind) { Id = id };
            var p = new Paragraph("", kind == NoteKind.Footnote ? "FootnoteText" : "EndnoteText");
            var mark = new NoteMark { Kind = kind };
            mark.Props.StyleId = kind == NoteKind.Footnote ? "FootnoteReference" : "EndnoteReference";
            p.Inlines.Add(mark);
            p.Inlines.Add(new TextRun(" " + text));
            note.Blocks.Add(p);
            return note;
        }
        doc.Footnotes[1] = MakeNote(NoteKind.Footnote, 1, "First footnote.");
        doc.Footnotes[2] = MakeNote(NoteKind.Footnote, 2, "Second footnote.");
        doc.Endnotes[1] = MakeNote(NoteKind.Endnote, 1, "An endnote.");
        var p = new Paragraph("Text");
        foreach (var (kind, id) in new[] { (NoteKind.Footnote, 1), (NoteKind.Footnote, 2), (NoteKind.Endnote, 1) })
        {
            var r = new NoteReference { Kind = kind, Id = id };
            r.Props.StyleId = kind == NoteKind.Footnote ? "FootnoteReference" : "EndnoteReference";
            p.Inlines.Add(r);
        }
        doc.Body.Blocks.Add(p);

        var back = Docx.RoundTrip(doc, out var bytes);
        Assert.Equal(4, back.Footnotes.Count); // two notes + separator + continuation separator
        Assert.Equal("separator", back.Footnotes[-1].Type);
        Assert.Equal("continuationSeparator", back.Footnotes[0].Type);
        Assert.IsType<NoteSeparator>(back.Footnotes[-1].Paragraphs.First().Inlines[0]);
        Assert.Equal(" First footnote.", back.Footnotes[1].Paragraphs.First().PlainText());
        Assert.Equal(3, back.Endnotes.Count);
        Assert.Equal(new[] { -1, 0 }, back.Settings.SpecialNoteIds(NoteKind.Footnote).ToArray());
        Assert.Equal(new[] { -1, 0 }, back.Settings.SpecialNoteIds(NoteKind.Endnote).ToArray());
        var refs = ((Paragraph)back.Body.Blocks[0]).Inlines.OfType<NoteReference>().ToList();
        Assert.Equal(3, refs.Count);
        Assert.Equal(NoteKind.Endnote, refs[2].Kind);

        // A reference to a note that was deleted is removed (Word would reject it).
        back.Footnotes.Remove(2);
        var again = Docx.Save(back);
        Docx.AssertValid(again);
        Assert.Single(Docx.Body(again).Descendants(W + "footnoteReference"));
    }

    [Fact]
    public void FootnotePropertiesInSettings_StayInSchemaOrder()
    {
        var doc = new Document();
        var settingsRoot = doc.Settings.Root;
        settingsRoot.Add(new XElement(W + "footnotePr", new XElement(W + "pos", new XAttribute(W + "val", "beneathText")), new XElement(W + "numFmt", new XAttribute(W + "val", "lowerRoman"))));
        SchemaOrder.Sort(settingsRoot, SchemaOrder.Settings);
        var note = new Note(NoteKind.Footnote) { Id = 1 };
        note.Blocks.Add(new Paragraph("Note"));
        doc.Footnotes[1] = note;
        doc.Body.Blocks.Add(new Paragraph("x") { Inlines = { new NoteReference { Kind = NoteKind.Footnote, Id = 1 } } });
        var bytes = Docx.Save(doc);
        Docx.AssertValid(bytes);
        var pr = Docx.PartXml(bytes, "word/settings.xml").Root!.Element(W + "footnotePr")!;
        Assert.Equal(new[] { "pos", "numFmt", "footnote", "footnote" }, pr.Elements().Select(e => e.Name.LocalName).ToArray());
    }

    [Fact]
    public void Comments_WithRepliesAndResolvedState_RoundTrip()
    {
        var doc = new Document();
        Comment MakeComment(int id, string author, string text, int? parent = null, bool done = false)
        {
            var c = new Comment { Id = id, Author = author, Initials = author.Substring(0, 1), Date = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), ParentId = parent, Done = done };
            var p = new Paragraph("", "CommentText");
            p.Inlines.Add(new CommentMark());
            p.Inlines.Add(new TextRun(text));
            c.Blocks.Add(p);
            doc.Comments.Add(c);
            return c;
        }
        MakeComment(0, "Ada", "Is this right?", done: true);
        MakeComment(1, "Grace", "Yes, checked.", parent: 0);
        MakeComment(2, "Alan", "Two\nparagraphs");
        doc.Comments[2].Blocks.Add(new Paragraph("Second paragraph of the comment."));

        var p = new Paragraph();
        p.Inlines.Add(new CommentMarker { Id = 0, Kind = CommentMarkerKind.RangeStart });
        p.Inlines.Add(new CommentMarker { Id = 1, Kind = CommentMarkerKind.RangeStart });
        p.Inlines.Add(new TextRun("commented text"));
        p.Inlines.Add(new CommentMarker { Id = 0, Kind = CommentMarkerKind.RangeEnd });
        p.Inlines.Add(new CommentMarker { Id = 1, Kind = CommentMarkerKind.RangeEnd });
        foreach (var id in new[] { 0, 1, 2 })
        {
            var reference = new CommentMarker { Id = id, Kind = CommentMarkerKind.Reference };
            reference.Props.StyleId = "CommentReference";
            p.Inlines.Add(reference);
        }
        // A marker for a comment that doesn't exist is dropped by the writer.
        p.Inlines.Add(new CommentMarker { Id = 99, Kind = CommentMarkerKind.Reference });
        doc.Body.Blocks.Add(p);

        var back = Docx.RoundTrip(doc, out var bytes);
        Assert.Equal(3, back.Comments.Count);
        var c0 = back.FindComment(0)!;
        var c1 = back.FindComment(1)!;
        Assert.True(c0.Done);
        Assert.False(c1.Done);
        Assert.Equal(0, c1.ParentId);
        Assert.Null(c0.ParentId);
        Assert.Equal("Grace", c1.Author);
        Assert.Equal("G", c1.Initials);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), c1.Date);
        Assert.NotNull(c0.ParaId);
        Assert.NotNull(c0.DurableId);
        Assert.Equal(2, back.FindComment(2)!.Paragraphs.Count());
        Assert.Equal("Second paragraph of the comment.", back.FindComment(2)!.Paragraphs.Last().PlainText());
        // The last paragraph of each comment carries the paraId commentsExtended uses.
        var commentsXml = Docx.PartXml(bytes, "word/comments.xml").Root!;
        foreach (var c in commentsXml.Elements(W + "comment"))
            Assert.NotNull(c.Elements(W + "p").Last().Attribute(Ns.W14 + "paraId"));
        Assert.Contains("word/commentsExtended.xml", Docx.PartNames(bytes));
        Assert.Contains("word/commentsIds.xml", Docx.PartNames(bytes));
        Assert.Equal(3, Docx.Body(bytes).Descendants(W + "commentReference").Count());

        // Deleting every comment removes the comment parts.
        back.Comments.Clear();
        var none = Docx.Save(back);
        Docx.AssertValid(none);
        Assert.DoesNotContain("word/comments.xml", Docx.PartNames(none));
        Assert.Empty(Docx.Body(none).Descendants(W + "commentRangeStart"));
    }

    [Fact]
    public void TrackedChanges_InsertionsDeletionsMovesAndFormatting_RoundTrip()
    {
        var doc = new Document();
        doc.Settings.TrackRevisions = true;
        var when = new DateTime(2026, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        RevisionInfo Rev(string author) => new() { Author = author, Date = when };
        var p = new Paragraph();
        p.Inlines.Add(new TextRun("kept "));
        p.Inlines.Add(new TextRun("inserted ") { Inserted = Rev("Ada") });
        p.Inlines.Add(new TextRun("deleted ") { Deleted = Rev("Grace") });
        p.Inlines.Add(new TextRun("inserted then deleted ") { Inserted = Rev("Ada"), Deleted = Rev("Grace") });
        var formatted = new TextRun("reformatted");
        formatted.Props.Bold = true;
        formatted.Props.SetChange(Rev("Ada"), new RunProps());
        p.Inlines.Add(formatted);
        p.Props.Justification = "center";
        p.Props.SetChange(Rev("Ada"), new ParaProps());
        p.Props.MarkProps(true)!.SetMarkRevision("ins", Rev("Ada"));
        doc.Body.Blocks.Add(p);

        var moves = new Paragraph();
        moves.Inlines.Add(new TextRun("moved away") { Deleted = Rev("Alan"), IsMove = true });
        moves.Inlines.Add(new TextRun(" | "));
        moves.Inlines.Add(new TextRun("moved here") { Inserted = Rev("Alan"), IsMove = true });
        doc.Body.Blocks.Add(moves);

        var back = Docx.RoundTrip(doc, out var bytes);
        var body = Docx.Body(bytes);
        var ids = body.Descendants().Where(e => e.Name.LocalName is "ins" or "del" or "moveFrom" or "moveTo" or "rPrChange" or "pPrChange")
            .Select(e => (string?)e.Attribute(W + "id")).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Single(body.Descendants(W + "moveFrom"));
        Assert.Single(body.Descendants(W + "moveTo"));
        Assert.Single(body.Descendants(W + "delText"), t => t.Value == "deleted ");
        // ins wraps del for text inserted then deleted
        Assert.Contains(body.Descendants(W + "ins"), e => e.Element(W + "del") != null);

        var bp = (Paragraph)back.Body.Blocks[0];
        Assert.Equal("Ada", bp.Inlines[1].Inserted!.Author);
        Assert.Equal(when, bp.Inlines[1].Inserted!.Date);
        Assert.Equal("Grace", bp.Inlines[2].Deleted!.Author);
        Assert.NotNull(bp.Inlines[3].Inserted);
        Assert.NotNull(bp.Inlines[3].Deleted);
        Assert.NotNull(bp.Inlines[4].Props.Change);
        Assert.NotNull(bp.Props.Change);
        Assert.Equal("Ada", bp.Props.MarkProps()!.MarkInserted!.Author);
        Assert.Equal("kept inserted reformatted", bp.PlainText());
        var bm = (Paragraph)back.Body.Blocks[1];
        Assert.True(bm.Inlines[0].IsMove);
        Assert.NotNull(bm.Inlines[0].Deleted);
        Assert.True(bm.Inlines[2].IsMove);
        Assert.NotNull(bm.Inlines[2].Inserted);
        Assert.True(back.Settings.TrackRevisions);
    }

    [Fact]
    public void RevisionIdsInPreservedXml_AreRenumberedToo()
    {
        var bytes = TestDocx.Create(
            "<w:p><w:ins w:id=\"5\" w:author=\"A\" w:date=\"2026-01-01T00:00:00Z\"><w:r><w:t>one</w:t></w:r></w:ins>" +
            "<w:sdt><w:sdtPr><w:id w:val=\"100\"/></w:sdtPr><w:sdtContent><w:ins w:id=\"5\" w:author=\"B\" w:date=\"2026-01-01T00:00:00Z\"><w:r><w:t>two</w:t></w:r></w:ins></w:sdtContent></w:sdt></w:p>");
        var doc = Docx.Load(bytes);
        var again = Docx.Save(doc);
        Docx.AssertValid(again);
        var ids = Docx.Body(again).Descendants(W + "ins").Select(e => (string?)e.Attribute(W + "id")).ToList();
        Assert.Equal(2, ids.Count);
        Assert.NotEqual(ids[0], ids[1]);
    }

    [Fact]
    public void BibliographySources_RoundTrip_InOneCustomXmlPart()
    {
        var doc = new Document();
        doc.Body.Blocks.Add(new Paragraph("Text"));
        doc.Sources.StyleName = "MLA";
        doc.Sources.Items.Add(new BibSource { Tag = "Lov43", Type = "Book", Title = "Notes", Year = "1843", Authors = { SourcePerson.Parse("Ada Lovelace") }, Publisher = "Taylor" });
        doc.Sources.Items.Add(new BibSource { Tag = "Tur50", Type = "JournalArticle", Title = "Computing Machinery and Intelligence", Year = "1950", JournalName = "Mind", Authors = { SourcePerson.Parse("Turing, Alan M.") } });

        var back = Docx.RoundTrip(doc, out var bytes);
        Assert.Equal(2, back.Sources.Items.Count);
        Assert.Equal("MLA", back.Sources.StyleName);
        Assert.Equal("Lovelace", back.Sources.Find("Lov43")!.Authors[0].Last);
        Assert.Equal("Mind", back.Sources.Find("Tur50")!.JournalName);
        Assert.NotNull(back.Sources.PartPath);
        var customParts = Docx.PartNames(bytes).Where(n => n.StartsWith("customXml/item", StringComparison.Ordinal) && !n.Contains("Props")).ToList();
        Assert.Single(customParts);
        var props = Docx.PartNames(bytes).Single(n => n.StartsWith("customXml/itemProps", StringComparison.Ordinal));
        Assert.Contains("bibliography", Docx.PartXml(bytes, props).ToString());

        back.Sources.Items.RemoveAt(0);
        var again = Docx.Save(back);
        Docx.AssertValid(again);
        Assert.Single(Docx.PartNames(again), n => n.StartsWith("customXml/item", StringComparison.Ordinal) && !n.Contains("Props"));
        Assert.Single(Docx.Load(again).Sources.Items);
    }
}
