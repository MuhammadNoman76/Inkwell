using System.Text;
using Inkwell.Core.Formats;
using Inkwell.Core.Model;

namespace Inkwell.Tests;

public class FormatsTestsRtf
{
    private static List<Paragraph> Paragraphs(Document doc) => doc.Body.Paragraphs.ToList();

    private static TextRun Run(IEnumerable<Paragraph> ps, string text) =>
        ps.SelectMany(p => p.Inlines).OfType<TextRun>().First(t => t.Text.Contains(text));

    internal const string WordPad =
        "{\\rtf1\\ansi\\ansicpg1252\\deff0\\nouicompat\\deflang1033{\\fonttbl{\\f0\\fnil\\fcharset0 Calibri;}{\\f1\\fnil\\fcharset2 Symbol;}}\r\n" +
        "{\\colortbl ;\\red255\\green0\\blue0;\\red0\\green77\\blue187;}\r\n" +
        "{\\*\\generator Riched20 10.0.22621}\\viewkind4\\uc1 \r\n" +
        "\\pard\\sa200\\sl276\\slmult1\\f0\\fs22\\lang9 Caf\\'e9 na\\'efve \\b bold\\b0  \\i italic\\i0  \\ul under\\ulnone  \\strike gone\\strike0  x\\super 2\\nosupersub  \\cf1 red\\cf0\\par\r\n" +
        "{\\pntext\\f1\\'B7\\tab}{\\*\\pn\\pnlvlblt\\pnf1\\pnindent0{\\pntxtb\\'B7}}\\fi-360\\li720\\sa200\\sl276\\slmult1 First bullet\\par\r\n" +
        "{\\pntext\\f1\\'B7\\tab}Second bullet\\par\r\n" +
        "\\pard{\\pntext\\f0 1.\\tab}{\\*\\pn\\pnlvlbody\\pnf0\\pnindent0\\pnstart1\\pndec{\\pntxta.}}\r\n" +
        "\\fi-360\\li720\\sa200\\sl276\\slmult1 One\\par\r\n" +
        "{\\pntext\\f0 2.\\tab}Two\\par\r\n" +
        "\\pard\\sa200\\sl276\\slmult1\\qc Centered\\par\r\n" +
        "}\r\n";

    [Fact]
    public void WordPadTextFormattingAndLists()
    {
        var doc = RtfImporter.Import(Encoding.ASCII.GetBytes(WordPad));
        var ps = Paragraphs(doc);
        Assert.Equal(6, ps.Count);
        Assert.Equal("Café naïve bold italic under gone x2 red", ps[0].PlainText());
        Assert.True(Run(ps, "bold").Props.Bold);
        Assert.True(Run(ps, "italic").Props.Italic);
        Assert.Equal("single", Run(ps, "under").Props.Underline);
        Assert.True(Run(ps, "gone").Props.Strike);
        Assert.Equal("superscript", ps[0].Inlines.OfType<TextRun>().Single(t => t.Text == "2").Props.VertAlign);
        Assert.Equal("FF0000", Run(ps, "red").Props.Color);
        Assert.Equal("Calibri", Run(ps, "Caf").Props.FontAscii);
        Assert.Equal(22, Run(ps, "Caf").Props.Size);
        Assert.Equal(200, ps[0].Props.SpacingAfter);
        Assert.Equal(276, ps[0].Props.SpacingLine);
        Assert.Equal("auto", ps[0].Props.SpacingLineRule);

        Assert.Equal("First bullet", ps[1].PlainText());
        Assert.Equal(ps[1].Props.NumId, ps[2].Props.NumId);
        var bullet = doc.Numbering.Level(ps[1].Props.NumId!.Value, 0)!;
        Assert.True(bullet.IsBullet);
        Assert.Equal("•", bullet.Text);
        Assert.Equal("ListParagraph", ps[1].StyleId);

        Assert.Equal("One", ps[3].PlainText());
        Assert.Equal(ps[3].Props.NumId, ps[4].Props.NumId);
        Assert.NotEqual(ps[1].Props.NumId, ps[3].Props.NumId);
        var number = doc.Numbering.Level(ps[3].Props.NumId!.Value, 0)!;
        Assert.Equal("decimal", number.Format);
        Assert.Equal("%1.", number.Text);
        Assert.Equal(1, number.Start);

        Assert.Equal("center", ps[5].Props.Justification);
        Assert.Null(ps[5].Props.NumId);
    }

    internal static string WordSample(byte[] png)
    {
        var hex = BitConverter.ToString(png).Replace("-", "").ToLowerInvariant();
        return
            "{\\rtf1\\adeflang1025\\ansi\\ansicpg1252\\uc1\\adeff0\\deff0\\deflang1033\\deflangfe1033\r\n" +
            "{\\fonttbl{\\f0\\fbidi \\froman\\fcharset0\\fprq2{\\*\\panose 02020603050405020304}Times New Roman;}{\\f1\\fbidi \\fswiss\\fcharset0\\fprq2 Calibri;}" +
            "{\\f2\\fbidi \\fnil\\fcharset178\\fprq2 Noto Nastaliq Urdu;}{\\f3\\fbidi \\froman\\fcharset2\\fprq2 Symbol;}}\r\n" +
            "{\\colortbl;\\red0\\green0\\blue0;\\red5\\green99\\blue193;\\red255\\green255\\blue0;\\red217\\green217\\blue217;}\r\n" +
            "{\\stylesheet{\\ql \\li0\\ri0\\widctlpar\\wrapdefault\\aspalpha\\aspnum\\faauto\\adjustright\\rin0\\lin0\\itap0 \\rtlch\\fcs1 \\af0\\afs24\\alang1025 \\ltrch\\fcs0 \\fs22\\lang1033\\langfe1033\\cgrid\\langnp1033\\langfenp1033 \\snext0 \\sqformat \\spriority0 Normal;}\r\n" +
            "{\\s1\\ql \\li0\\ri0\\sb240\\keepn\\widctlpar\\outlinelevel0\\adjustright\\rin0\\lin0\\itap0 \\rtlch\\fcs1 \\ab\\af0\\afs32 \\ltrch\\fcs0 \\b\\fs32\\cf2 \\sbasedon0 \\snext0 \\slink15 \\sqformat heading 1;}\r\n" +
            "{\\s2\\ql \\li720\\ri720\\sb120\\sa120\\widctlpar\\adjustright\\rin720\\lin720\\itap0 \\rtlch\\fcs1 \\ai\\af0 \\ltrch\\fcs0 \\i \\sbasedon0 \\snext0 \\sqformat Quote;}\r\n" +
            "{\\s3\\ql \\outlinelevel1 \\b\\fs28 \\sbasedon0 \\snext0 \\sqformat \\'dcberschrift 2;}\r\n" +
            "{\\*\\cs10 \\additive \\ssemihidden \\sunhideused \\spriority1 Default Paragraph Font;}\r\n" +
            "{\\*\\cs15 \\additive \\rtlch\\fcs1 \\af0 \\ltrch\\fcs0 \\ul\\cf2 \\sbasedon10 \\sunhideused Hyperlink;}\r\n" +
            "{\\*\\cs16 \\additive \\rtlch\\fcs1 \\af0 \\ltrch\\fcs0 \\super \\sbasedon10 \\ssemihidden footnote reference;}\r\n" +
            "{\\s17\\ql \\li0\\ri0\\widctlpar\\itap0 \\rtlch\\fcs1 \\af0\\afs20 \\ltrch\\fcs0 \\fs20\\lang1033 \\sbasedon0 \\snext17 \\ssemihidden footnote text;}}\r\n" +
            "{\\*\\listtable{\\list\\listtemplateid1234\\listhybrid{\\listlevel\\levelnfc0\\levelnfcn0\\leveljc0\\leveljcn0\\levelfollow0\\levelstartat3\\levelspace0\\levelindent0{\\leveltext\\leveltemplateid67698703\\'02\\'00.;}{\\levelnumbers\\'01;}\\rtlch\\fcs1 \\af0 \\ltrch\\fcs0 \\fi-360\\li720\\lin720 }\r\n" +
            "{\\listlevel\\levelnfc4\\levelnfcn4\\leveljc0\\leveljcn0\\levelfollow0\\levelstartat1\\levelspace0\\levelindent0{\\leveltext\\leveltemplateid67698713\\'02\\'01.;}{\\levelnumbers\\'01;}\\rtlch\\fcs1 \\af0 \\ltrch\\fcs0 \\fi-360\\li1440\\lin1440 }{\\listname ;}\\listid111}\r\n" +
            "{\\list\\listtemplateid5678\\listhybrid{\\listlevel\\levelnfc23\\levelnfcn23\\leveljc0\\leveljcn0\\levelfollow0\\levelstartat1\\levelspace0\\levelindent0{\\leveltext\\leveltemplateid67698689\\'01\\u-3913 ?;}{\\levelnumbers;}\\f3\\fbias0 \\fi-360\\li720\\lin720 }{\\listname ;}\\listid222}}\r\n" +
            "{\\*\\listoverridetable{\\listoverride\\listid111\\listoverridecount0\\ls1}{\\listoverride\\listid222\\listoverridecount0\\ls2}}\r\n" +
            "{\\info{\\title Sample Title}{\\author Muhammad Noman}{\\operator Someone Else}{\\creatim\\yr2026\\mo10\\dy15\\hr9\\min30}{\\*\\company Ignored Co}}\r\n" +
            "\\paperw11906\\paperh16838\\margl1134\\margr1134\\margt1440\\margb1440\r\n" +
            "{\\header \\pard\\plain Header text that must not appear\\par}\r\n" +
            "\\sectd \\pgwsxn11906\\pghsxn16838\\marglsxn1134\\margrsxn1134\r\n" +
            "\\pard\\plain \\s1\\ql \\sb240\\keepn\\outlinelevel0 \\rtlch\\fcs1 \\ab\\af0\\afs32 \\ltrch\\fcs0 \\b\\fs32\\cf2 {\\*\\bkmkstart intro}Introduction{\\*\\bkmkend intro}\\par\r\n" +
            "\\pard\\plain \\ql \\rtlch\\fcs1 \\af0\\afs24 \\ltrch\\fcs0 \\fs22 Visit {\\field{\\*\\fldinst {\\rtlch\\fcs1 \\af0 \\ltrch\\fcs0 HYPERLINK \"https://example.com/\" }}{\\fldrslt {\\rtlch\\fcs1 \\af0 \\ltrch\\fcs0 \\cs15\\ul\\cf2 our site}}}" +
            " or {\\field{\\*\\fldinst HYPERLINK \\\\l \"intro\"}{\\fldrslt jump}} on page {\\field{\\*\\fldinst PAGE}{\\fldrslt 7}}." +
            "{\\rtlch\\fcs1 \\af0 \\ltrch\\fcs0 \\cs16\\super \\chftn {\\footnote \\ltrpar \\pard\\plain \\s17\\ql \\rtlch\\fcs1 \\af0\\afs20 \\ltrch\\fcs0 \\fs20 {\\rtlch\\fcs1 \\af0 \\ltrch\\fcs0 \\cs16\\super \\chftn } A \\i footnote\\i0 .\\par}}\\par\r\n" +
            "\\pard\\plain \\rtlpar\\qr \\rtlch\\fcs1 \\af2\\afs28\\alang1056 \\ltrch\\fcs0 \\f2\\fs28 {\\rtlch\\fcs1 \\af2 \\ltrch\\fcs0 \\u1575\\'c7\\u1585\\'d1\\u1583\\'cf\\u1608\\'e6 \\u1605\\'e3\\u1578\\'ca\\u1606\\'e4}\\par\r\n" +
            "\\pard\\plain \\s2\\ql \\li720\\ri720\\sb120\\sa120 \\rtlch\\fcs1 \\ai\\af0 \\ltrch\\fcs0 \\i A quotation.\\par\r\n" +
            "\\pard\\plain \\s3 \\b\\fs28 German heading\\par\r\n" +
            "\\pard\\plain \\ls1\\ilvl0 \\fi-360\\li720 Third\\par\r\n" +
            "\\pard\\plain \\ls1\\ilvl1 \\fi-360\\li1440 Sub a\\par\r\n" +
            "\\pard\\plain \\ls2\\ilvl0 \\fi-360\\li720 Bullet\\par\r\n" +
            "\\trowd \\irow0\\irowband0\\ltrrow\\ts11\\trgaph108\\trleft-108\\trhdr \\clbrdrt\\brdrs\\brdrw10 \\clbrdrl\\brdrs\\brdrw10 \\clbrdrb\\brdrs\\brdrw10 \\clbrdrr\\brdrs\\brdrw10 \\clcbpat4\\cellx3000\\clbrdrt\\brdrs\\brdrw10 \\cellx6000\\cellx9000" +
            "\\pard\\plain \\intbl Head A\\cell Head B\\cell Head C\\cell\\row\r\n" +
            "\\trowd \\trgaph108\\trleft-108 \\clmgf\\cellx3000\\clmrg\\cellx6000\\clvmgf\\clvertalc\\cellx9000\\pard\\plain \\intbl Merged\\cell \\cell Tall\\cell\\row\r\n" +
            "\\trowd \\trgaph108\\trleft-108 \\cellx3000\\cellx6000\\clvmrg\\cellx9000\\pard\\plain \\intbl a\\cell b\\cell \\cell\\row\r\n" +
            "\\pard\\plain {\\*\\shppict{\\pict{\\*\\picprop\\shplid1025{\\sp{\\sn shapeType}{\\sv 75}}}\\picw64\\pich48\\picwgoal960\\pichgoal720\\pngblip\r\n" + hex + "}}{\\nonshppict{\\pict\\picw64\\pich48\\wmetafile8 0100090000}}\\par\r\n" +
            "\\pard\\plain \\qj Page two\\sect\r\n" +
            "\\sectd\\sbknone\\lndscpsxn\\pgwsxn16838\\pghsxn11906 \\pard\\plain Landscape section\\par\r\n" +
            "{\\*\\themedata 504b030414}}\r\n";
    }

    [Fact]
    public void WordStyleDocumentKeepingItsFormatting()
    {
        var png = Sta.Run(() => TestImages.Png(64, 48));
        var doc = RtfImporter.Import(Encoding.ASCII.GetBytes(WordSample(png)));
        var ps = Paragraphs(doc);
        Assert.DoesNotContain(ps, p => p.PlainText().Contains("Header text"));

        var heading = ps.First();
        Assert.Equal("Heading1", heading.StyleId);
        Assert.Equal("Introduction", heading.PlainText());
        var marks = heading.Inlines.OfType<BookmarkMarker>().ToList();
        Assert.Equal(2, marks.Count);
        Assert.Equal("intro", marks[0].Name);
        Assert.Equal(marks[0].Id, marks[1].Id);
        var headingRun = Run(ps, "Introduction");
        Assert.Equal(32, headingRun.Props.Size);
        Assert.Equal("0563C1", headingRun.Props.Color);
        Assert.Null(headingRun.Props.Bold);
        Assert.Equal("Times New Roman", headingRun.Props.FontAscii);

        var links = ps[1];
        Assert.Equal("Visit our site or jump on page 7.", links.PlainText());
        var site = Run(ps, "our site");
        Assert.Equal("https://example.com/", site.Link!.Url);
        Assert.Equal("Hyperlink", site.Props.StyleId);
        Assert.Equal("intro", Run(ps, "jump").Link!.Anchor);
        Assert.Null(Run(ps, "7").Link);
        var reference = links.Inlines.OfType<NoteReference>().Single();
        var note = doc.Footnotes[reference.Id];
        Assert.Equal("FootnoteReference", reference.Props.StyleId);
        var noteText = note.Paragraphs.Single();
        Assert.Equal("FootnoteText", noteText.StyleId);
        Assert.IsType<NoteMark>(noteText.Inlines[0]);
        Assert.Equal("A footnote.", noteText.PlainText().Trim());
        Assert.True(noteText.Inlines.OfType<TextRun>().Single(t => t.Text == "footnote").Props.Italic);

        var urdu = ps[2];
        Assert.Equal("اردو متن", urdu.PlainText());
        Assert.True(urdu.Props.Bidi);
        Assert.Equal("start", urdu.Props.Justification);
        var urduRun = urdu.Inlines.OfType<TextRun>().Single();
        Assert.True(urduRun.Props.Rtl);
        Assert.Equal("Noto Nastaliq Urdu", urduRun.Props.FontCs);
        Assert.Equal("ur-PK", urduRun.Props.LangBidi);

        var quote = ps[3];
        Assert.Equal("Quote", quote.StyleId);
        Assert.Equal(720, quote.Props.IndentStart);
        Assert.Null(Run(ps, "quotation").Props.Italic);
        Assert.Equal("Heading2", ps[4].StyleId);

        var third = ps.Single(p => p.PlainText() == "Third");
        var subA = ps.Single(p => p.PlainText() == "Sub a");
        Assert.Equal(third.Props.NumId, subA.Props.NumId);
        Assert.Equal(1, subA.Props.NumLevel);
        var l0 = doc.Numbering.Level(third.Props.NumId!.Value, 0)!;
        Assert.Equal(("decimal", "%1.", 3), (l0.Format, l0.Text, l0.Start));
        var l1 = doc.Numbering.Level(third.Props.NumId.Value, 1)!;
        Assert.Equal(("lowerLetter", "%2."), (l1.Format, l1.Text));
        var bullet = ps.Single(p => p.PlainText() == "Bullet");
        Assert.Equal("•", doc.Numbering.Level(bullet.Props.NumId!.Value, 0)!.Text);

        var table = doc.Body.Blocks.OfType<Table>().Single();
        Assert.Equal(new[] { 3108, 3000, 3000 }, table.Grid);
        Assert.Equal(3, table.Rows.Count);
        Assert.True(table.Rows[0].Props.IsHeader);
        Assert.Equal(new[] { "Head A", "Head B", "Head C" }, table.Rows[0].Cells.Select(c => ((Paragraph)c.Blocks[0]).PlainText()));
        Assert.Equal("D9D9D9", table.Rows[0].Cells[0].Props.ShadingFill);
        Assert.Equal(new BorderDef("single", 4, 0, "auto"), table.Rows[0].Cells[0].Props.Border("top"));
        Assert.Equal(2, table.Rows[1].Cells.Count);
        Assert.Equal(2, table.Rows[1].Cells[0].Props.GridSpan);
        Assert.Equal("Merged", ((Paragraph)table.Rows[1].Cells[0].Blocks[0]).PlainText());
        Assert.Equal("restart", table.Rows[1].Cells[1].Props.VerticalMerge);
        Assert.Equal("center", table.Rows[1].Cells[1].Props.VerticalAlignment);
        Assert.Equal("continue", table.Rows[2].Cells[2].Props.VerticalMerge);

        var picture = ps.SelectMany(p => p.Inlines).OfType<DrawingObject>().Single();
        Assert.Equal(960L * 635, picture.Width);
        Assert.Equal(720L * 635, picture.Height);
        Assert.Equal(png, doc.Media.Get(picture.MediaId)!.Data);

        var pageTwo = ps.Single(p => p.PlainText() == "Page two");
        Assert.Equal("both", pageTwo.Props.Justification);
        var first = pageTwo.Props.Section!;
        Assert.Equal(11906, first.PageWidth);
        Assert.Equal(1134, first.MarginLeft);
        Assert.False(first.Landscape);
        var last = doc.Body.FinalSection;
        Assert.True(last.Landscape);
        Assert.Equal(16838, last.PageWidth);
        Assert.Equal("continuous", last.BreakType);
        Assert.Equal("Landscape section", ps.Last().PlainText());

        Assert.Equal("Sample Title", doc.Properties.Title);
        Assert.Equal("Muhammad Noman", doc.Properties.Creator);
        Assert.Equal("Someone Else", doc.Properties.LastModifiedBy);
        Assert.Equal(new DateTime(2026, 10, 15, 9, 30, 0, DateTimeKind.Local).ToUniversalTime(), doc.Properties.Created);
    }

    [Fact]
    public void PastingMatchesTheDestination()
    {
        var png = Sta.Run(() => TestImages.Png(8, 8));
        var target = new Document();
        var r = RtfImporter.ImportFragment(WordSample(png), target);
        var ps = DocumentWalker.Paragraphs(r.Blocks).ToList();
        var heading = ps[0];
        Assert.Equal("Heading1", heading.StyleId);
        var run = Run(ps, "Introduction");
        Assert.Null(run.Props.Bold);
        Assert.Null(run.Props.Size);
        Assert.Null(run.Props.Color);
        Assert.Null(run.Props.FontAscii);
        Assert.Null(heading.Props.SpacingBefore);
        var site = Run(ps, "our site");
        Assert.Null(site.Props.Underline);
        Assert.Equal("Hyperlink", site.Props.StyleId);
        Assert.True(Run(ps, "اردو").Props.Rtl);
        Assert.Single(r.AddedNotes);
        Assert.Same(target.Footnotes[r.AddedNotes[0].Id], r.AddedNotes[0]);
        Assert.Equal("InkwellTable", r.Blocks.OfType<Table>().Single().Props.StyleId);
        Assert.Null(r.Blocks.OfType<Table>().Single().Rows[0].Cells[0].Props.ShadingFill);
        Assert.Equal(1, r.ImagesImported);
        Assert.Null(ps.Single(p => p.PlainText() == "Page two").Props.Section);
    }

    [Fact]
    public void CodePagesAndUnicodeEscapes()
    {
        var rtf = "{\\rtf1\\ansi\\ansicpg1251\\deff0{\\fonttbl{\\f0 Arial;}{\\f1\\fcharset161 Arial Greek;}{\\f2\\fcharset0 Arial;}}" +
                  "\\'cf\\'f0\\'e8\\'e2\\'e5\\'f2 {\\f1 \\'e1\\'e5\\'f4\\'e1}\\par" +
                  "\\uc2\\u1575\\'c7\\'c7 x \\uc0\\u1576 y {\\uc1\\u8364? euro} \\uc1\\u-3913? z\\par" +
                  "\\emdash\\endash\\bullet\\lquote\\rquote\\ldblquote\\rdblquote\\~\\_\\-\\tab\\line\\page|\\par}";
        var doc = RtfImporter.Import(Encoding.ASCII.GetBytes(rtf));
        var ps = Paragraphs(doc);
        Assert.Equal("Привет αετα", ps[0].PlainText());
        Assert.Equal("ا x بy € euro \uF0B7 z", ps[1].Text);
        Assert.Equal("—–•‘’“”\u00A0\u2011\u00AD\t\v\f|", ps[2].Text);
    }

    [Fact]
    public void Windows1252AccentsAndStringInput()
    {
        var doc = RtfImporter.Import("{\\rtf1\\ansi{\\fonttbl{\\f0 Arial;}}\\f0 r\\'e9sum\\'e9 \\'80 \\'93ok\\'94 اردو\\par}");
        Assert.Equal("résumé € “ok” اردو", Paragraphs(doc).Single().PlainText());
    }

    [Fact]
    public void NestedTables()
    {
        var rtf = "{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0 Arial;}}" +
                  "\\pard\\intbl\\itap1 Outer A\\cell\r\n" +
                  "\\pard\\intbl\\itap2 Inner 1\\nestcell Inner 2\\nestcell{\\*\\nesttableprops\\trowd\\cellx1000\\cellx2000\\nestrow}{\\nonesttables\\par}\r\n" +
                  "\\pard\\intbl\\itap1 \\cell\r\n\\trowd\\cellx3000\\cellx6000\\row\r\n\\pard After\\par}";
        var doc = RtfImporter.Import(Encoding.ASCII.GetBytes(rtf));
        var outer = doc.Body.Blocks.OfType<Table>().Single();
        Assert.Single(outer.Rows);
        Assert.Equal(2, outer.Rows[0].Cells.Count);
        var cell = outer.Rows[0].Cells[1];
        var inner = Assert.IsType<Table>(cell.Blocks[0]);
        Assert.IsType<Paragraph>(cell.Blocks[1]);
        Assert.Equal(new[] { "Inner 1", "Inner 2" }, inner.Rows[0].Cells.Select(c => ((Paragraph)c.Blocks[0]).PlainText()));
        Assert.Equal("After", doc.Body.Blocks.OfType<Paragraph>().First().PlainText());
    }

    [Fact]
    public void HiddenTextHighlightsAndSkippedDestinations()
    {
        var rtf = "{\\rtf1\\ansi{\\fonttbl{\\f0 Arial;}}{\\colortbl;\\red255\\green255\\blue0;\\red204\\green238\\blue255;}" +
                  "{\\*\\unknowndestination should not appear}{\\footer footer text}" +
                  "visible {\\v hidden} {\\highlight1 marked} {\\cb2 shaded} {\\pict\\macpict 0011}\\par}";
        var target = new Document();
        var r = RtfImporter.ImportFragment(rtf, target, keepSourceFormatting: true);
        var p = (Paragraph)r.Blocks.Single();
        Assert.Equal("visible hidden marked shaded ", p.PlainText());
        Assert.True(p.Inlines.OfType<TextRun>().Single(t => t.Text == "hidden").Props.Vanish);
        Assert.Equal("yellow", p.Inlines.OfType<TextRun>().Single(t => t.Text == "marked").Props.Highlight);
        Assert.Equal("CCEEFF", p.Inlines.OfType<TextRun>().Single(t => t.Text == "shaded").Props.ShadingFill);
        Assert.Equal(1, r.ImagesSkipped);
    }

    [Fact]
    public void WindowsMetafilesGetAPlaceableHeader()
    {
        // A minimal metafile: header, SetWindowOrg(0,0), SetWindowExt(1000 x 500), end of file.
        var wmf = new List<byte>();
        void Word(int v)
        {
            wmf.Add((byte)v);
            wmf.Add((byte)(v >> 8));
        }
        Word(1);
        Word(9);
        Word(0x300);
        Word(15);
        Word(0);
        Word(1);
        Word(5);
        Word(0);
        Word(0);
        Word(5); Word(0); Word(0x020B); Word(0); Word(0);
        Word(5); Word(0); Word(0x020C); Word(500); Word(1000);
        Word(3); Word(0); Word(0);
        var hex = BitConverter.ToString(wmf.ToArray()).Replace("-", "");
        var doc = RtfImporter.Import("{\\rtf1\\ansi{\\pict\\wmetafile8\\picw2540\\pich1270\\picwgoal1440\\pichgoal720 " + hex + "}\\par}");
        var picture = Paragraphs(doc).SelectMany(p => p.Inlines).OfType<DrawingObject>().Single();
        var data = doc.Media.Get(picture.MediaId)!;
        Assert.Equal("wmf", data.Extension);
        Assert.Equal(new byte[] { 0xD7, 0xCD, 0xC6, 0x9A }, data.Data.Take(4));
        Assert.Equal(914400, picture.Width);
        Assert.Equal(457200, picture.Height);
    }

    [Fact]
    public void FieldsSymbolsAndCharacterStyles()
    {
        var rtf = "{\\rtf1\\ansi{\\fonttbl{\\f0 Arial;}{\\f1\\fcharset2 Symbol;}}{\\stylesheet{Normal;}{\\*\\cs20 \\b Strong;}}" +
                  "{\\field{\\*\\fldinst SYMBOL 183 \\\\f \"Symbol\" \\\\s 10}{\\fldrslt }} {\\cs20 strong} {\\f1 \\'b7}\\par}";
        var doc = RtfImporter.Import(rtf, new RtfImportOptions { KeepSourceFormatting = false });
        var p = Paragraphs(doc).Single();
        var symbols = p.Inlines.OfType<SymbolChar>().ToList();
        Assert.Equal(2, symbols.Count);
        Assert.All(symbols, s => Assert.Equal('\uF0B7', s.Char));
        Assert.Equal("Symbol", symbols[0].Font);
        Assert.True(p.Inlines.OfType<TextRun>().Single(t => t.Text == "strong").Props.Bold);
    }
}
