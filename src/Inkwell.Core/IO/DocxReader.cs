using System.Globalization;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using Inkwell.Core.Model;

namespace Inkwell.Core.IO;

/// <summary>
/// Opens .docx / .docm / .dotx / .dotm files into a <see cref="Document"/>. The original bytes are kept in
/// <see cref="Document.Source"/> so everything Inkwell doesn't edit is saved back unchanged; what can't be edited is
/// listed in <see cref="Document.Preserved"/>, and anything changed on open in <see cref="Document.Warnings"/>.
/// </summary>
/// <remarks>
/// Malformed or unusual input never crashes: files that aren't Word documents, are damaged, password-protected
/// or in the old .doc format raise a <see cref="DocxException"/> with a message for the user. Strict Open XML
/// documents are converted to the standard form on open (see <see cref="DocxPackage"/>).
/// </remarks>
public static class DocxReader
{
    public static Document Load(string path)
    {
        byte[] bytes;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bytes = ReadAll(fs);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new DocxException(DocxErrorKind.FileAccess, "The file couldn't be found. It may have been moved, renamed or deleted.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new DocxException(DocxErrorKind.FileAccess, "Inkwell doesn't have permission to read this file.", ex);
        }
        catch (IOException ex)
        {
            throw new DocxException(DocxErrorKind.FileAccess, "The file couldn't be read. It may be in use by another program, or the drive may be unavailable.", ex);
        }
        return LoadBytes(bytes, Path.GetFileName(path));
    }

    public static Document Load(Stream stream, string? fileName = null)
    {
        byte[] bytes;
        try
        {
            bytes = ReadAll(stream);
        }
        catch (IOException ex)
        {
            throw new DocxException(DocxErrorKind.FileAccess, "The document couldn't be read.", ex);
        }
        return LoadBytes(bytes, fileName);
    }

    internal static Document LoadBytes(byte[] bytes, string? fileName)
    {
        var warnings = new List<string>();
        try
        {
            var prepared = DocxPackage.Prepare(bytes, fileName, warnings);
            var doc = new DocxReadSession(prepared).Run();
            doc.Warnings.InsertRange(0, warnings);
            return doc;
        }
        catch (DocxException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw DocxPackage.Damaged(ex);
        }
    }

    private static byte[] ReadAll(Stream stream)
    {
        if (stream is MemoryStream m && m.Position == 0) return m.ToArray();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}

/// <summary>One load: reads the package parts into a <see cref="Document"/>.</summary>
internal sealed partial class DocxReadSession
{
    private readonly byte[] _bytes;
    private readonly Document _doc = new();
    private readonly Dictionary<string, int> _tally = new(StringComparer.Ordinal);
    private readonly List<string> _tallyOrder = new();
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _mediaByPart = new(StringComparer.OrdinalIgnoreCase);
    private PartContext _part = null!;

    /// <summary>Above 0 while reading content that is only displayed (text boxes, read-only content controls): no notices.</summary>
    private int _displayDepth;

    public DocxReadSession(byte[] bytes)
    {
        _bytes = bytes;
    }

    /// <summary>The part being read: its relationships resolve r:id values.</summary>
    private sealed class PartContext
    {
        private Dictionary<string, string>? _external;

        public PartContext(OpenXmlPart part)
        {
            Part = part;
            Uri = part.Uri.OriginalString;
        }

        public OpenXmlPart Part { get; }

        public string Uri { get; }

        public string? ExternalTarget(string id)
        {
            if (_external == null)
            {
                _external = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var h in Part.HyperlinkRelationships) _external[h.Id] = h.Uri.OriginalString;
                foreach (var e in Part.ExternalRelationships)
                    if (!_external.ContainsKey(e.Id)) _external[e.Id] = e.Uri.OriginalString;
            }
            return _external.TryGetValue(id, out var target) ? target : null;
        }
    }

    public Document Run()
    {
        using var ms = new MemoryStream(_bytes, false);
        WordprocessingDocument pkg;
        try
        {
            pkg = WordprocessingDocument.Open(ms, false, new OpenSettings { AutoSave = false });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not DocxException)
        {
            if (ex.Message.IndexOf("content type", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new DocxException(DocxErrorKind.NotAWordDocument, "This file isn't a Word document (.docx).", ex);
            throw DocxPackage.Damaged(ex);
        }

        using (pkg)
        {
            var main = pkg.MainDocumentPart
                ?? throw new DocxException(DocxErrorKind.NotAWordDocument, "This file isn't a Word document: it has no main document part.");
            _doc.Source = new SourcePackage(_bytes, main.ContentType);
            foreach (var id in DocxXml.RelationshipIds(main)) _doc.ReservedRelIds.Add(id);

            ReadStylesAndSettings(main);
            ReadMainDocument(main);
            foreach (var pair in main.Parts)
            {
                if (pair.OpenXmlPart is HeaderPart hp) ReadHeaderFooter(pair.RelationshipId, hp, true);
                else if (pair.OpenXmlPart is FooterPart fp) ReadHeaderFooter(pair.RelationshipId, fp, false);
            }
            if (main.FootnotesPart is { } fn) ReadNotes(fn, NoteKind.Footnote);
            if (main.EndnotesPart is { } en) ReadNotes(en, NoteKind.Endnote);
            ReadComments(main);
            ReadCoreProperties(pkg);
            ReadCustomXml(main);
            NotePackageFeatures(main);
            if (pkg.DigitalSignatureOriginPart != null)
                Warn("This document is digitally signed. Saving it from Inkwell removes the signatures, as any change makes them invalid.");
        }

        FinishNotices();
        return _doc;
    }

    // ------------------------------------------------------------------ parts

    private void ReadStylesAndSettings(MainDocumentPart main)
    {
        _doc.Styles = main.StyleDefinitionsPart is { } sp
            ? new StyleSheet(DocxXml.Load(sp))
            : new StyleSheet(new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), Ns.NewRoot(Ns.W + "styles")));
        _doc.Numbering = main.NumberingDefinitionsPart is { } np
            ? new Numbering(DocxXml.Load(np)) { Styles = _doc.Styles }
            : new Numbering { Styles = _doc.Styles };
        _doc.Settings = main.DocumentSettingsPart is { } setp
            ? new DocSettings(DocxXml.Load(setp))
            : new DocSettings(new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), Ns.NewRoot(Ns.W + "settings")));
        if (main.ThemePart is { } tp)
        {
            _doc.Theme = new DocTheme(DocxXml.Load(tp));
        }
        else
        {
            // Word falls back to its built-in Office theme when the part is missing.
            _doc.Theme = DocTheme.CreateDefault("Calibri Light", "Calibri", "Office Theme");
        }
        if (main.FontTablePart is { } ft) _doc.FontTable = DocxXml.Load(ft);
    }

    private void ReadMainDocument(MainDocumentPart main)
    {
        var x = DocxXml.Load(main);
        var root = x.Root;
        if (root == null || root.Name != Wn.Document)
            throw new DocxException(DocxErrorKind.Damaged, "This document can't be opened because its main part isn't WordprocessingML.");
        _doc.DocumentShell = DocxXml.Shell(root);
        _doc.Background = root.Element(Wn.Background).Copy();
        _part = new PartContext(main);
        var body = root.Element(Wn.Body);
        if (body == null)
        {
            _doc.Body.FinalSection = new SectProps();
            return;
        }
        var children = body.Elements().ToList();
        var sectPr = children.LastOrDefault(e => e.Name == Wn.SectPr);
        _doc.Body.Blocks.AddRange(ParseBlocks(children.Where(e => e.Name != Wn.SectPr)));
        _doc.Body.FinalSection = sectPr != null ? new SectProps(new XElement(sectPr)) : new SectProps();
    }

    private void ReadHeaderFooter(string relId, OpenXmlPart part, bool header)
    {
        var x = DocxXml.Load(part);
        var root = x.Root;
        if (root == null) return;
        var hf = new HeaderFooter(header) { RelId = relId, PartUri = part.Uri.OriginalString, RootShell = DocxXml.Shell(root) };
        _part = new PartContext(part);
        hf.Blocks.AddRange(ParseBlocks(root.Elements()));
        _doc.HeadersFooters[relId] = hf;
    }

    private void ReadNotes(OpenXmlPart part, NoteKind kind)
    {
        var x = DocxXml.Load(part);
        if (x.Root == null) return;
        _part = new PartContext(part);
        var name = kind == NoteKind.Footnote ? Wn.Footnote : Wn.Endnote;
        var notes = kind == NoteKind.Footnote ? _doc.Footnotes : _doc.Endnotes;
        foreach (var e in x.Root.Elements(name))
        {
            var id = e.IntAttr("id");
            if (id == null) continue;
            if (notes.ContainsKey(id.Value))
            {
                Warn("Two notes in the document had the same number; the second one was left out.");
                continue;
            }
            var note = new Note(kind) { Id = id.Value, Type = e.Attr("type"), Shell = DocxXml.Shell(e) };
            note.Blocks.AddRange(ParseBlocks(e.Elements()));
            notes[id.Value] = note;
        }
    }

    private void ReadComments(MainDocumentPart main)
    {
        if (main.WordprocessingCommentsPart is not { } cp) return;
        var x = DocxXml.Load(cp);
        if (x.Root == null) return;
        _part = new PartContext(cp);
        var seen = new HashSet<int>();
        foreach (var e in x.Root.Elements(Wn.Comment))
        {
            var id = e.IntAttr("id");
            if (id == null || !seen.Add(id.Value)) continue;
            var c = new Comment
            {
                Id = id.Value,
                Author = e.Attr("author") ?? "",
                Initials = e.Attr("initials") ?? "",
                Date = RevisionInfo.ParseDate(e.Attr("date")),
                Shell = DocxXml.Shell(e),
            };
            c.Blocks.AddRange(ParseBlocks(e.Elements()));
            c.ParaId = c.Paragraphs.LastOrDefault()?.Shell?.Attribute(Wn.ParaId)?.Value;
            _doc.Comments.Add(c);
        }

        var byPara = new Dictionary<string, Comment>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in _doc.Comments)
            if (c.ParaId != null && !byPara.ContainsKey(c.ParaId)) byPara[c.ParaId] = c;

        if (main.WordprocessingCommentsExPart is { } ex && LoadOptional(ex)?.Root is { } exRoot)
        {
            foreach (var e in exRoot.Elements(Ns.W15 + "commentEx"))
            {
                var paraId = e.Attribute(Ns.W15 + "paraId")?.Value;
                if (paraId == null || !byPara.TryGetValue(paraId, out var c)) continue;
                c.Done = DocxXml.OnOffAttr(e, Ns.W15 + "done");
                var parent = e.Attribute(Ns.W15 + "paraIdParent")?.Value;
                if (parent != null && byPara.TryGetValue(parent, out var p) && !ReferenceEquals(p, c)) c.ParentId = p.Id;
            }
        }

        var byDurable = new Dictionary<string, Comment>(StringComparer.OrdinalIgnoreCase);
        if (main.WordprocessingCommentsIdsPart is { } idp && LoadOptional(idp)?.Root is { } idRoot)
        {
            foreach (var e in idRoot.Elements(Ns.W16CID + "commentId"))
            {
                var paraId = e.Attribute(Ns.W16CID + "paraId")?.Value;
                var durable = e.Attribute(Ns.W16CID + "durableId")?.Value;
                if (paraId == null || durable == null || !byPara.TryGetValue(paraId, out var c)) continue;
                c.DurableId = durable;
                byDurable[durable] = c;
            }
        }

        if (main.WordCommentsExtensiblePart is { } cex && LoadOptional(cex)?.Root is { } cexRoot)
        {
            foreach (var e in cexRoot.Elements(Ns.W16CEX + "commentExtensible"))
            {
                var durable = e.Attribute(Ns.W16CEX + "durableId")?.Value;
                if (durable == null || !byDurable.TryGetValue(durable, out var c)) continue;
                c.DateUtc = RevisionInfo.ParseDate(e.Attribute(Ns.W16CEX + "dateUtc")?.Value);
            }
        }
    }

    /// <summary>Optional parts: a damaged one is skipped (with a notice) instead of failing the whole document.</summary>
    private XDocument? LoadOptional(OpenXmlPart part)
    {
        try
        {
            return DocxXml.Load(part);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or InvalidDataException)
        {
            Warn("Part of the document's extra information (" + part.Uri.OriginalString + ") was damaged and has been ignored.");
            return null;
        }
    }

    private void ReadCoreProperties(WordprocessingDocument pkg)
    {
        if (pkg.CoreFilePropertiesPart is not { } part) return;
        var x = LoadOptional(part);
        var root = x?.Root;
        if (root == null) return;
        string? Text(XName name) => root.Element(name)?.Value;
        var p = new CoreProps
        {
            Title = Text(Ns.DC + "title"),
            Subject = Text(Ns.DC + "subject"),
            Creator = Text(Ns.DC + "creator"),
            Keywords = Text(Ns.CP + "keywords"),
            Description = Text(Ns.DC + "description"),
            LastModifiedBy = Text(Ns.CP + "lastModifiedBy"),
            Category = Text(Ns.CP + "category"),
            ContentStatus = Text(Ns.CP + "contentStatus"),
            Revision = Text(Ns.CP + "revision"),
            Created = RevisionInfo.ParseDate(Text(Ns.DCTerms + "created")),
            Modified = RevisionInfo.ParseDate(Text(Ns.DCTerms + "modified")),
            Original = x,
        };
        _doc.Properties = p;
    }

    private void ReadCustomXml(MainDocumentPart main)
    {
        foreach (var part in main.CustomXmlParts)
        {
            XDocument? x;
            try
            {
                x = DocxXml.Load(part);
            }
            catch (Exception ex) when (ex is System.Xml.XmlException or IOException or InvalidDataException)
            {
                Tally("customXml");
                continue;
            }
            var root = x.Root;
            if (root == null) continue;
            if (root.Name == Ns.B + "Sources")
            {
                if (_doc.Sources.PartPath == null) _doc.Sources = BibliographySources.FromXml(x, part.Uri.OriginalString);
                continue;
            }
            // Word's own cover page properties aren't worth a notice.
            if (root.Name.NamespaceName == "http://schemas.microsoft.com/office/2006/coverPageProps") continue;
            Tally("customXml");
        }
    }

    private void NotePackageFeatures(MainDocumentPart main)
    {
        if (main.VbaProjectPart != null) Tally("macros");
    }

    // ------------------------------------------------------------------ notices

    private void Tally(string category)
    {
        if (_displayDepth > 0) return;
        if (_tally.TryGetValue(category, out var n)) _tally[category] = n + 1;
        else
        {
            _tally[category] = 1;
            _tallyOrder.Add(category);
        }
    }

    private void Warn(string message)
    {
        if (_displayDepth > 0) return;
        if (_warned.Add(message)) _doc.Warnings.Add(message);
    }

    private void FinishNotices()
    {
        foreach (var category in _tallyOrder)
        {
            var n = _tally[category];
            var description = Describe(category, n);
            _doc.NotePreserved(category, description);
            _doc.Preserved[_doc.Preserved.Count - 1].Count = n;
        }
    }

    internal static string Describe(string category, int n)
    {
        string Count(string one, string many) => n == 1 ? "1 " + one : n.ToString(CultureInfo.InvariantCulture) + " " + many;
        return category switch
        {
            "macros" => "Macros (a VBA project)",
            "chart" => Count("chart", "charts"),
            "smartart" => Count("SmartArt graphic", "SmartArt graphics"),
            "equation" => Count("equation", "equations"),
            "contentControl" => Count("content control", "content controls"),
            "field" => Count("complex field (with fields nested in its code)", "complex fields (with fields nested in their code)"),
            "formField" => Count("form field", "form fields"),
            "customXml" => Count("custom XML part", "custom XML parts"),
            "textbox" => Count("text box", "text boxes"),
            "shape" => Count("shape or drawing", "shapes and drawings"),
            "object" => Count("embedded object", "embedded objects"),
            "vml" => Count("picture in an older format", "pictures in an older format"),
            "ink" => Count("ink drawing", "ink drawings"),
            "activeX" => Count("ActiveX control", "ActiveX controls"),
            "altChunk" => Count("piece of imported content", "pieces of imported content"),
            "ruby" => Count("phonetic guide (ruby text)", "phonetic guides (ruby text)"),
            "linkedPicture" => Count("linked picture", "linked pictures"),
            "other" => Count("other item", "other items"),
            _ => Count(category, category),
        };
    }
}
