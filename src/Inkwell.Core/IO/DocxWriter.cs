using System.Globalization;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Inkwell.Core.Model;

namespace Inkwell.Core.IO;

/// <summary>
/// Saves a <see cref="Document"/> as .docx / .docm / .dotx / .dotm. When the document was loaded from a file
/// (<see cref="Document.Source"/>), saving starts from the original package and rewrites only the parts Inkwell owns
/// (document, styles, numbering, settings, theme, headers and footers, notes, comments, properties, bibliography,
/// pictures, hyperlinks, embedded fonts); every other part (macros, charts, SmartArt, embeddings, custom XML,
/// glossary, web settings…) is kept byte for byte.
/// </summary>
public static class DocxWriter
{
    /// <summary>
    /// Saves to a file atomically: the package is written to a temporary file next to the target, flushed, read
    /// back and checked, and only then moved over the target. The target is never left half-written.
    /// </summary>
    public static byte[] Save(Document doc, string path, DocxSaveOptions? options = null)
    {
        options ??= new DocxSaveOptions();
        var full = Path.GetFullPath(path);
        var format = options.Format != DocxFormat.Auto ? options.Format : FormatFromExtension(full) ?? SourceFormat(doc);
        var session = new DocxWriteSession(doc, ApplyDropMacros(format, options), options);
        var bytes = session.Build();
        WriteAtomically(full, bytes, options.Verify);
        session.Commit(bytes);
        return bytes;
    }

    /// <summary>Writes the package to a stream (in one piece, after it has been built and checked).</summary>
    public static byte[] Save(Document doc, Stream output, DocxSaveOptions? options = null)
    {
        options ??= new DocxSaveOptions();
        var format = options.Format != DocxFormat.Auto ? options.Format : SourceFormat(doc);
        var session = new DocxWriteSession(doc, ApplyDropMacros(format, options), options);
        var bytes = session.Build();
        if (options.Verify) Verify(bytes);
        try
        {
            output.Write(bytes, 0, bytes.Length);
            output.Flush();
        }
        catch (IOException ex)
        {
            throw new DocxException(DocxErrorKind.FileAccess, "The document couldn't be written.", ex);
        }
        session.Commit(bytes);
        return bytes;
    }

    internal static DocxFormat? FormatFromExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".docx" => DocxFormat.Document,
        ".docm" => DocxFormat.MacroEnabledDocument,
        ".dotx" => DocxFormat.Template,
        ".dotm" => DocxFormat.MacroEnabledTemplate,
        _ => null,
    };

    internal static DocxFormat SourceFormat(Document doc)
    {
        if (doc.Source == null) return DocxFormat.Document;
        return (doc.Source.IsTemplate, doc.Source.HasMacros) switch
        {
            (true, true) => DocxFormat.MacroEnabledTemplate,
            (true, false) => DocxFormat.Template,
            (false, true) => DocxFormat.MacroEnabledDocument,
            _ => DocxFormat.Document,
        };
    }

    private static DocxFormat ApplyDropMacros(DocxFormat format, DocxSaveOptions options)
    {
        if (!options.DropMacros) return format;
        return format switch
        {
            DocxFormat.MacroEnabledDocument => DocxFormat.Document,
            DocxFormat.MacroEnabledTemplate => DocxFormat.Template,
            _ => format,
        };
    }

    /// <summary>The main document part's content type for a format.</summary>
    public static string ContentType(DocxFormat format) => format switch
    {
        DocxFormat.MacroEnabledDocument => "application/vnd.ms-word.document.macroEnabled.main+xml",
        DocxFormat.Template => "application/vnd.openxmlformats-officedocument.wordprocessingml.template.main+xml",
        DocxFormat.MacroEnabledTemplate => "application/vnd.ms-word.template.macroEnabledTemplate.main+xml",
        _ => "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml",
    };

    /// <summary>Checks a written package opens and has a readable main document part.</summary>
    internal static void Verify(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes, false);
            using var pkg = WordprocessingDocument.Open(ms, false);
            var main = pkg.MainDocumentPart ?? throw new InvalidDataException("The main document part is missing.");
            using var s = main.GetStream(FileMode.Open, FileAccess.Read);
            // Read the whole part as a stream of nodes: well-formed XML with a w:document root, without building a tree.
            using var reader = System.Xml.XmlReader.Create(s, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
            reader.MoveToContent();
            if (reader.LocalName != "document" || reader.NamespaceURI != Ns.W.NamespaceName) throw new InvalidDataException("The main document part isn't a w:document.");
            while (reader.Read())
            {
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not DocxException)
        {
            throw new DocxException(DocxErrorKind.Damaged, "The document couldn't be saved: the written file didn't check out, so the original was left unchanged.", ex);
        }
    }

    private static void WriteAtomically(string path, byte[] bytes, bool verify)
    {
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) dir = Directory.GetCurrentDirectory();
        var temp = Path.Combine(dir!, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".inkwell-tmp");
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            if (verify)
            {
                var back = File.ReadAllBytes(temp);
                if (back.Length != bytes.Length) throw new IOException("The temporary file wasn't written completely.");
                Verify(back);
            }
            Compat.MoveOverwrite(temp, path);
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // best effort: a leftover temporary file is harmless
            }
            if (ex is DocxException) throw;
            if (ex is UnauthorizedAccessException)
                throw new DocxException(DocxErrorKind.FileAccess, "The document couldn't be saved: Inkwell doesn't have permission to write there, or the file is read-only.", ex);
            if (ex is IOException)
                throw new DocxException(DocxErrorKind.FileAccess, "The document couldn't be saved. The file may be open in another program, or the drive may be full or unavailable.", ex);
            throw;
        }
    }
}

/// <summary>One save: builds the package bytes; <see cref="Commit"/> updates the document once the bytes are safely written.</summary>
internal sealed partial class DocxWriteSession
{
    private readonly Document _doc;
    private readonly DocxFormat _format;
    private readonly DocxSaveOptions _options;
    private readonly List<Action> _after = new();
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OpenXmlPart> _partsByUri = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _hfIdMap = new(StringComparer.Ordinal);
    private readonly List<StoryTree> _trees = new();
    private readonly DateTime _now;
    private WordprocessingDocument _pkg = null!;
    private MainDocumentPart _main = null!;
    private bool _isNew;
    private List<int> _footnoteSpecial = new();
    private List<int> _endnoteSpecial = new();
    private HashSet<int> _footnoteIds = new();
    private HashSet<int> _endnoteIds = new();
    private HashSet<int> _commentIds = new();
    private bool _fontsEmbedded;

    /// <summary>A generated part: its XML and the part it goes to.</summary>
    private sealed class StoryTree
    {
        public StoryTree(PartWriter writer, XDocument xml, string kind)
        {
            Writer = writer;
            Xml = xml;
            Kind = kind;
        }

        public PartWriter Writer { get; }
        public XDocument Xml { get; }

        /// <summary>document, header, footer, footnotes, endnotes, comments.</summary>
        public string Kind { get; }

        public bool Skip { get; set; }

        /// <summary>w14:paraId attributes whose value must be kept (the last paragraphs of comments).</summary>
        public HashSet<XAttribute> ReservedParaIds { get; } = new();
    }

    public DocxWriteSession(Document doc, DocxFormat format, DocxSaveOptions options)
    {
        _doc = doc;
        _format = format;
        _options = options;
        _now = (options.Now ?? DateTime.UtcNow).ToUniversalTime();
    }

    private void Warn(string message)
    {
        if (_warned.Add(message)) _options.Warnings.Add(message);
    }

    public byte[] Build()
    {
        var ms = OpenStartingPackage();
        try
        {
            using (var pkg = WordprocessingDocument.Open(ms, true, new OpenSettings { AutoSave = true }))
            {
                _pkg = pkg;
                _main = pkg.MainDocumentPart ?? pkg.AddMainDocumentPart();
                SetDocumentType();
                IndexParts();
                PrepareHeaderFooterParts();
                GenerateStories();
                FixUpTrees();
                RemoveUnusedHeaderFooters();
                foreach (var tree in _trees)
                {
                    if (tree.Skip) continue;
                    ScrubDanglingRelationships(tree);
                    RemoveUnusedRelationships(tree);
                    DocxXml.Save(tree.Xml, tree.Writer.Part);
                }
                WriteCommentExtras();
                WriteStyles();
                WriteNumbering();
                WriteTheme();
                WriteFontTable();
                WriteSettings();
                WriteWebSettings();
                WriteCoreProperties();
                WriteAppProperties();
                WriteBibliography();
            }
            var bytes = ms.ToArray();
            return _retype ? SetMainContentType(bytes, _mainUri, DocxWriter.ContentType(_format)) : bytes;
        }
        catch (DocxException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new DocxException(DocxErrorKind.Damaged, "The document couldn't be saved because of an internal problem; the file on disk was left unchanged.", ex);
        }
    }

    /// <summary>After the bytes are written: the document now comes from them.</summary>
    public void Commit(byte[] bytes)
    {
        foreach (var action in _after) action();
        _doc.Source = new SourcePackage(bytes, DocxWriter.ContentType(_format));
        _doc.Properties.Modified = _now;
        if (_options.LastModifiedBy != null) _doc.Properties.LastModifiedBy = _options.LastModifiedBy;
        if (_doc.Properties.Created == null) _doc.Properties.Created = _now;
    }

    // ------------------------------------------------------------------ package

    private MemoryStream OpenStartingPackage()
    {
        if (_doc.Source != null)
        {
            try
            {
                var copy = new MemoryStream();
                copy.Write(_doc.Source.Bytes, 0, _doc.Source.Bytes.Length);
                copy.Position = 0;
                using (var probe = WordprocessingDocument.Open(new MemoryStream(_doc.Source.Bytes, false), false))
                {
                    if (probe.MainDocumentPart == null) throw new InvalidDataException("no main part");
                }
                return copy;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Warn("The original file couldn't be reused, so parts Inkwell doesn't edit (such as charts or macros) may be missing from the saved copy.");
            }
        }
        _isNew = true;
        var ms = new MemoryStream();
        using (var pkg = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document, true))
        {
            var main = pkg.AddMainDocumentPart();
            DocxXml.Save(new XElement(Wn.Document, new XAttribute(XNamespace.Xmlns + "w", Ns.W.NamespaceName), new XElement(Wn.Body)), main);
        }
        ms.Position = 0;
        return ms;
    }

    private static WordprocessingDocumentType ToSdk(DocxFormat format) => format switch
    {
        DocxFormat.MacroEnabledDocument => WordprocessingDocumentType.MacroEnabledDocument,
        DocxFormat.Template => WordprocessingDocumentType.Template,
        DocxFormat.MacroEnabledTemplate => WordprocessingDocumentType.MacroEnabledTemplate,
        _ => WordprocessingDocumentType.Document,
    };

    private void SetDocumentType()
    {
        var target = ToSdk(_format);
        var macroFree = target is WordprocessingDocumentType.Document or WordprocessingDocumentType.Template;
        if (macroFree && _main.VbaProjectPart is { } vba)
        {
            _main.DeletePart(vba);
            if (!_options.DropMacros) Warn("The macros were removed because the chosen format can't hold them. Save as a macro-enabled document (.docm) to keep them.");
        }
        // The content type is switched in [Content_Types].xml once the package is written: the SDK's
        // ChangeDocumentType would move the main part to a new name (word/document2.xml).
        _mainUri = _main.Uri.OriginalString;
        _retype = !string.Equals(_main.ContentType, DocxWriter.ContentType(_format), StringComparison.Ordinal);
    }

    private string _mainUri = "/word/document.xml";
    private bool _retype;

    /// <summary>Sets the main part's content type in [Content_Types].xml (an Override, so other .xml parts are untouched).</summary>
    private static byte[] SetMainContentType(byte[] bytes, string mainUri, string contentType)
    {
        using var ms = new MemoryStream();
        ms.Write(bytes, 0, bytes.Length);
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Update, true))
        {
            var entry = zip.Entries.First(e => string.Equals(e.FullName, "[Content_Types].xml", StringComparison.OrdinalIgnoreCase));
            XDocument types;
            using (var s = entry.Open()) types = DocxXml.Load(s);
            var ns = XNamespace.Get("http://schemas.openxmlformats.org/package/2006/content-types");
            var root = types.Root!;
            var over = root.Elements(ns + "Override").FirstOrDefault(e => string.Equals(e.Attribute("PartName")?.Value, mainUri, StringComparison.OrdinalIgnoreCase));
            if (over != null) over.SetAttributeValue("ContentType", contentType);
            else root.Add(new XElement(ns + "Override", new XAttribute("PartName", mainUri), new XAttribute("ContentType", contentType)));
            var name = entry.FullName;
            entry.Delete();
            var replacement = zip.CreateEntry(name, System.IO.Compression.CompressionLevel.Optimal);
            using var w = replacement.Open();
            DocxXml.Save(types, w);
        }
        return ms.ToArray();
    }

    private void IndexParts()
    {
        var seen = new HashSet<OpenXmlPart>();
        void Visit(OpenXmlPartContainer container)
        {
            foreach (var pair in container.Parts)
            {
                var part = pair.OpenXmlPart;
                if (!seen.Add(part)) continue;
                _partsByUri[part.Uri.OriginalString] = part;
                Visit(part);
            }
        }
        Visit(_pkg);
    }

    /// <summary>A new relationship id for the main part (never one a header / footer of the model uses).</summary>
    private string NewMainRelId() => DocxXml.NewRelId(_main, _doc.HeadersFooters.Keys);

    private T MainPart<T>(T? existing) where T : OpenXmlPart, IFixedContentTypePart
    {
        if (existing != null) return existing;
        return _main.AddNewPart<T>(NewMainRelId());
    }

    private readonly Dictionary<string, (HeaderFooter Story, OpenXmlPart Part)> _hfParts = new(StringComparer.Ordinal);

    private void PrepareHeaderFooterParts()
    {
        foreach (var pair in _doc.HeadersFooters.ToList())
        {
            var relId = pair.Key;
            var hf = pair.Value;
            OpenXmlPart? part = null;
            var conflict = string.IsNullOrEmpty(relId);
            if (!conflict && _main.TryGetPartById(relId, out var existing))
            {
                if (hf.IsHeader ? existing is HeaderPart : existing is FooterPart) part = existing;
                else conflict = true;
            }
            else if (!conflict && DocxXml.RelationshipIds(_main).Contains(relId))
            {
                conflict = true;
            }
            if (part == null)
            {
                var id = conflict ? NewMainRelId() : relId;
                part = hf.IsHeader ? _main.AddNewPart<HeaderPart>(id) : _main.AddNewPart<FooterPart>(id);
                if (id != relId) _hfIdMap[relId] = id;
                _partsByUri[part.Uri.OriginalString] = part;
            }
            _hfParts[relId] = (hf, part);
        }
    }

    /// <summary>The root element's attributes of a part as it is in the package (for parts the model has no shell for).</summary>
    private static XElement? ExistingRootShell(OpenXmlPart? part)
    {
        if (part == null) return null;
        try
        {
            var x = DocxXml.Load(part);
            return x.Root == null ? null : DocxXml.Shell(x.Root);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or InvalidDataException)
        {
            return null;
        }
    }

    private static XDocument NewXml(XElement root) => new(new XDeclaration("1.0", "UTF-8", "yes"), root);

    // ------------------------------------------------------------------ stories

    private void GenerateStories()
    {
        // Body.
        var pw = new PartWriter(this, _main);
        var root = _doc.DocumentShell != null ? new XElement(Wn.Document, _doc.DocumentShell.Attributes()) : Ns.NewRoot(Wn.Document);
        if (_doc.Background != null && MapRels(new XElement(_doc.Background), _main.Uri.OriginalString, pw, null) is { } background) root.Add(background);
        var body = new XElement(Wn.Body);
        WriteBlocks(body, _doc.Body.Blocks, pw, false);
        if (!body.HasElements) body.Add(new XElement(Wn.P));
        body.Add(new XElement(_doc.Body.FinalSection.X));
        root.Add(body);
        _trees.Add(new StoryTree(pw, NewXml(root), "document"));

        // Headers and footers.
        foreach (var (hf, part) in _hfParts.Values)
        {
            var hpw = new PartWriter(this, part);
            var name = hf.IsHeader ? Ns.W + "hdr" : Ns.W + "ftr";
            var hroot = hf.RootShell != null ? new XElement(name, hf.RootShell.Attributes()) : Ns.NewRoot(name);
            WriteBlocks(hroot, hf.Blocks, hpw, true);
            _trees.Add(new StoryTree(hpw, NewXml(hroot), hf.IsHeader ? "header" : "footer"));
            var uri = part.Uri.OriginalString;
            var story = hf;
            _after.Add(() => story.PartUri = uri);
        }

        GenerateNotes(NoteKind.Footnote);
        GenerateNotes(NoteKind.Endnote);
        GenerateComments();
    }

    private void GenerateNotes(NoteKind kind)
    {
        var notes = kind == NoteKind.Footnote ? _doc.Footnotes : _doc.Endnotes;
        OpenXmlPart? part = kind == NoteKind.Footnote ? _main.FootnotesPart : _main.EndnotesPart;
        var ids = kind == NoteKind.Footnote ? _footnoteIds : _endnoteIds;
        if (notes.Count == 0)
        {
            if (part != null) _main.DeletePart(part);
            return;
        }
        part ??= kind == NoteKind.Footnote ? _main.AddNewPart<FootnotesPart>(NewMainRelId()) : _main.AddNewPart<EndnotesPart>(NewMainRelId());
        _partsByUri[part.Uri.OriginalString] = part;
        var pw = new PartWriter(this, part);
        var rootName = Ns.W + (kind == NoteKind.Footnote ? "footnotes" : "endnotes");
        var shell = ExistingRootShell(part);
        var root = shell != null && shell.Name == rootName ? shell : Ns.NewRoot(rootName);

        var list = notes.Values.ToList();
        var hasNormal = list.Any(n => !n.IsSpecial);
        if (hasNormal && !list.Any(n => n.Type == "separator")) list.Add(DefaultSeparator(kind, FreeNoteId(list, -1), false));
        if (hasNormal && !list.Any(n => n.Type == "continuationSeparator")) list.Add(DefaultSeparator(kind, FreeNoteId(list, 0), true));

        var special = new List<int>();
        var noteName = kind == NoteKind.Footnote ? Wn.Footnote : Wn.Endnote;
        foreach (var note in list.OrderBy(n => n.IsSpecial ? 0 : 1).ThenBy(n => n.Id))
        {
            var el = new XElement(noteName);
            if (note.Shell != null)
                foreach (var a in note.Shell.Attributes())
                    if (a.Name != Wn.WType && a.Name != Wn.WId) el.Add(new XAttribute(a));
            if (note.IsSpecial) el.SetAttributeValue(Wn.WType, note.Type);
            el.SetAttributeValue(Wn.WId, Ox.Str(note.Id));
            WriteBlocks(el, note.Blocks, pw, true);
            root.Add(el);
            if (note.IsSpecial)
            {
                if (note.Type is "separator" or "continuationSeparator" or "continuationNotice") special.Add(note.Id);
            }
            else
            {
                ids.Add(note.Id);
            }
        }
        // Word lists them in this order.
        special = special.OrderBy(id => list.First(n => n.Id == id).Type switch { "separator" => 0, "continuationSeparator" => 1, _ => 2 }).Take(3).ToList();
        if (kind == NoteKind.Footnote) _footnoteSpecial = special;
        else _endnoteSpecial = special;
        _trees.Add(new StoryTree(pw, NewXml(root), kind == NoteKind.Footnote ? "footnotes" : "endnotes"));
    }

    private static int FreeNoteId(List<Note> notes, int preferred)
    {
        var used = new HashSet<int>(notes.Select(n => n.Id));
        if (!used.Contains(preferred)) return preferred;
        var id = Math.Min(-1, used.Min()) - 1;
        while (used.Contains(id)) id--;
        return id;
    }

    private static Note DefaultSeparator(NoteKind kind, int id, bool continuation)
    {
        var note = new Note(kind) { Id = id, Type = continuation ? "continuationSeparator" : "separator" };
        var p = new Paragraph();
        p.Props.SpacingAfter = 0;
        p.Props.SpacingLine = 240;
        p.Props.SpacingLineRule = "auto";
        p.Inlines.Add(new NoteSeparator { Continuation = continuation });
        note.Blocks.Add(p);
        return note;
    }

    private void GenerateComments()
    {
        var part = _main.WordprocessingCommentsPart;
        if (_doc.Comments.Count == 0)
        {
            if (part != null) _main.DeletePart(part);
            if (_main.WordprocessingCommentsExPart is { } ex) _main.DeletePart(ex);
            if (_main.WordprocessingCommentsIdsPart is { } ids) _main.DeletePart(ids);
            if (_main.WordCommentsExtensiblePart is { } cex) _main.DeletePart(cex);
            return;
        }
        part ??= _main.AddNewPart<WordprocessingCommentsPart>(NewMainRelId());
        _partsByUri[part.Uri.OriginalString] = part;
        var pw = new PartWriter(this, part);
        var shell = ExistingRootShell(part);
        var root = shell != null && shell.Name == Ns.W + "comments" ? shell : Ns.NewRoot(Ns.W + "comments");
        var tree = new StoryTree(pw, NewXml(root), "comments");

        // Every comment's last paragraph carries a unique w14:paraId (commentsExtended refers to it).
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedDurable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in _doc.Comments)
        {
            if (!DocxFixups.IsValidParaId(c.ParaId) || !used.Add(c.ParaId!)) c.ParaId = DocxFixups.NewParaId(used);
            if (!DocxFixups.IsValidParaId(c.DurableId) || !usedDurable.Add(c.DurableId!)) c.DurableId = DocxFixups.NewParaId(usedDurable);
        }

        foreach (var c in _doc.Comments)
        {
            if (!_commentIds.Add(c.Id))
            {
                Warn("Two comments had the same number; the second one was left out.");
                continue;
            }
            var el = new XElement(Wn.Comment);
            if (c.Shell != null)
            {
                foreach (var a in c.Shell.Attributes())
                {
                    if (a.Name == Wn.WId || a.Name == Ns.W + "author" || a.Name == Ns.W + "date" || a.Name == Ns.W + "initials") continue;
                    el.Add(new XAttribute(a));
                }
            }
            el.SetAttributeValue(Wn.WId, Ox.Str(c.Id));
            el.SetAttributeValue(Ns.W + "author", c.Author ?? "");
            if (c.Date is { } date) el.SetAttributeValue(Ns.W + "date", RevisionInfo.FormatDate(date));
            if (!string.IsNullOrEmpty(c.Initials)) el.SetAttributeValue(Ns.W + "initials", c.Initials);

            if (c.Blocks.LastOrDefault() is Paragraph lastParagraph)
            {
                lastParagraph.Shell ??= new XElement(Wn.P);
                lastParagraph.Shell.SetAttributeValue(Wn.ParaId, c.ParaId);
            }
            WriteBlocks(el, c.Blocks, pw, true);
            var lastP = el.Elements(Wn.P).LastOrDefault() ?? el.Descendants(Wn.P).LastOrDefault();
            if (lastP == null)
            {
                lastP = new XElement(Wn.P);
                el.Add(lastP);
            }
            lastP.SetAttributeValue(Wn.ParaId, c.ParaId);
            tree.ReservedParaIds.Add(lastP.Attribute(Wn.ParaId)!);
            root.Add(el);
        }
        _trees.Insert(0, tree); // paragraph ids of comments win when the pass resolves duplicates
    }

    private void WriteCommentExtras()
    {
        if (_doc.Comments.Count == 0) return;
        var comments = _doc.Comments.Where(c => c.ParaId != null).GroupBy(c => c.Id).Select(g => g.First()).ToList();
        var byId = comments.ToDictionary(c => c.Id);

        var ex = MainPart(_main.WordprocessingCommentsExPart);
        var exShell = ExistingRootShell(ex);
        var exRoot = exShell != null && exShell.Name == Ns.W15 + "commentsEx" ? exShell : Ns.NewRoot(Ns.W15 + "commentsEx");
        foreach (var c in comments)
        {
            var e = new XElement(Ns.W15 + "commentEx", new XAttribute(Ns.W15 + "paraId", c.ParaId!));
            if (c.ParentId is { } parentId && byId.TryGetValue(parentId, out var parent) && parent.Id != c.Id)
                e.Add(new XAttribute(Ns.W15 + "paraIdParent", parent.ParaId!));
            e.Add(new XAttribute(Ns.W15 + "done", c.Done ? "1" : "0"));
            exRoot.Add(e);
        }
        SaveOwned(exRoot, ex);

        var idsPart = MainPart(_main.WordprocessingCommentsIdsPart);
        var idsShell = ExistingRootShell(idsPart);
        var idsRoot = idsShell != null && idsShell.Name == Ns.W16CID + "commentsIds" ? idsShell : Ns.NewRoot(Ns.W16CID + "commentsIds");
        foreach (var c in comments)
            idsRoot.Add(new XElement(Ns.W16CID + "commentId", new XAttribute(Ns.W16CID + "paraId", c.ParaId!), new XAttribute(Ns.W16CID + "durableId", c.DurableId!)));
        SaveOwned(idsRoot, idsPart);

        var cexPart = MainPart(_main.WordCommentsExtensiblePart);
        var cexShell = ExistingRootShell(cexPart);
        var cexRoot = cexShell != null && cexShell.Name == Ns.W16CEX + "commentsExtensible" ? cexShell : Ns.NewRoot(Ns.W16CEX + "commentsExtensible");
        foreach (var c in comments)
        {
            var e = new XElement(Ns.W16CEX + "commentExtensible", new XAttribute(Ns.W16CEX + "durableId", c.DurableId!));
            var utc = c.DateUtc ?? c.Date;
            if (utc is { } d) e.Add(new XAttribute(Ns.W16CEX + "dateUtc", RevisionInfo.FormatDate(d)));
            cexRoot.Add(e);
        }
        SaveOwned(cexRoot, cexPart);
    }

    /// <summary>Saves a part Inkwell writes from scratch (namespace declarations and characters checked).</summary>
    private static void SaveOwned(XElement root, OpenXmlPart part)
    {
        DocxFixups.FixNamespaces(root);
        DocxFixups.Sanitize(root);
        DocxXml.Save(root, part);
    }

    private static void SaveOwned(XDocument doc, OpenXmlPart part)
    {
        if (doc.Root != null)
        {
            DocxFixups.FixNamespaces(doc.Root);
            DocxFixups.Sanitize(doc.Root);
        }
        DocxXml.Save(doc, part);
    }

    // ------------------------------------------------------------------ other owned parts

    private void WriteStyles()
    {
        var part = MainPart(_main.StyleDefinitionsPart);
        SaveOwned(new XDocument(_doc.Styles.Doc), part);
    }

    private void WriteNumbering()
    {
        var part = _main.NumberingDefinitionsPart;
        if (part == null && _doc.Numbering.IsEmpty) return;
        part ??= MainPart<NumberingDefinitionsPart>(null);
        SaveOwned(new XDocument(_doc.Numbering.Doc), part);
    }

    private void WriteTheme()
    {
        var part = MainPart(_main.ThemePart);
        SaveOwned(new XDocument(_doc.Theme.Doc), part);
    }

    private void WriteSettings()
    {
        var copy = new XDocument(_doc.Settings.Doc);
        var settings = new DocSettings(copy);
        settings.SetSpecialNotes(NoteKind.Footnote, _footnoteSpecial);
        settings.SetSpecialNotes(NoteKind.Endnote, _endnoteSpecial);
        foreach (var name in new[] { "footnotePr", "endnotePr" })
        {
            var pr = copy.Root?.Element(Ns.W + name);
            if (pr == null) continue;
            if (!pr.HasElements && !pr.HasAttributes)
            {
                pr.Remove();
                continue;
            }
            SchemaOrder.Sort(pr, NotePrOrder);
        }
        if (_fontsEmbedded) settings.EmbedTrueTypeFonts = true;
        if (copy.Root != null) SchemaOrder.Sort(copy.Root, SchemaOrder.Settings);
        SaveOwned(copy, MainPart(_main.DocumentSettingsPart));
    }

    private static readonly Dictionary<string, int> NotePrOrder = new(StringComparer.Ordinal)
    {
        ["pos"] = 0, ["numFmt"] = 10, ["numStart"] = 20, ["numRestart"] = 30, ["footnote"] = 40, ["endnote"] = 40, ["*"] = 50,
    };

    private void WriteWebSettings()
    {
        if (!_isNew || _main.WebSettingsPart != null) return;
        var root = Ns.NewRoot(Ns.W + "webSettings");
        root.Add(new XElement(Ns.W + "optimizeForBrowser"), new XElement(Ns.W + "allowPNG"));
        SaveOwned(root, MainPart<WebSettingsPart>(null));
    }

    private void WriteCoreProperties()
    {
        var p = _doc.Properties;
        var part = _pkg.CoreFilePropertiesPart ?? _pkg.AddCoreFilePropertiesPart();
        XElement root;
        if (p.Original?.Root is { } original && original.Name == Ns.CP + "coreProperties")
        {
            root = new XElement(original);
        }
        else
        {
            root = new XElement(Ns.CP + "coreProperties",
                new XAttribute(XNamespace.Xmlns + "cp", Ns.CP.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "dc", Ns.DC.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "dcterms", Ns.DCTerms.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "dcmitype", Ns.DCMIType.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "xsi", Ns.XSI.NamespaceName));
        }
        void Set(XName name, string? value)
        {
            root.Elements(name).Remove();
            if (!string.IsNullOrEmpty(value)) root.Add(new XElement(name, value));
        }
        void SetDate(XName name, DateTime? value)
        {
            root.Elements(name).Remove();
            if (value is { } d)
                root.Add(new XElement(name, new XAttribute(Ns.XSI + "type", "dcterms:W3CDTF"), RevisionInfo.FormatDate(d)));
        }
        Set(Ns.DC + "title", p.Title);
        Set(Ns.DC + "subject", p.Subject);
        Set(Ns.DC + "creator", p.Creator);
        Set(Ns.CP + "keywords", p.Keywords);
        Set(Ns.DC + "description", p.Description);
        Set(Ns.CP + "lastModifiedBy", _options.LastModifiedBy ?? p.LastModifiedBy);
        Set(Ns.CP + "category", p.Category);
        Set(Ns.CP + "contentStatus", p.ContentStatus);
        Set(Ns.CP + "revision", p.Revision);
        SetDate(Ns.DCTerms + "created", p.Created ?? _now);
        SetDate(Ns.DCTerms + "modified", _now);
        foreach (var (prefix, ns) in new[] { ("dcterms", Ns.DCTerms), ("xsi", Ns.XSI), ("dc", Ns.DC), ("cp", Ns.CP) })
            if (root.GetPrefixOfNamespace(ns) == null) root.SetAttributeValue(XNamespace.Xmlns + prefix, ns.NamespaceName);
        DocxFixups.Sanitize(root);
        DocxXml.Save(root, part);
    }

    private void WriteAppProperties()
    {
        var part = _pkg.ExtendedFilePropertiesPart ?? _pkg.AddExtendedFilePropertiesPart();
        XElement? root = null;
        try
        {
            root = DocxXml.Load(part).Root;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or InvalidDataException)
        {
            root = null;
        }
        if (root == null || root.Name != Ns.EP + "Properties")
        {
            root = new XElement(Ns.EP + "Properties",
                new XAttribute("xmlns", Ns.EP.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "vt", Ns.VT.NamespaceName));
        }
        void Set(string name, string value)
        {
            var e = root!.Element(Ns.EP + name);
            if (e == null) root.Add(new XElement(Ns.EP + name, value));
            else e.Value = value;
        }
        Set("Application", "Inkwell");
        if (_options.Pages is { } pages) Set("Pages", Ox.Str(Math.Max(0, pages)));
        if (_options.Words is { } words) Set("Words", Ox.Str(Math.Max(0, words)));
        if (_options.Characters is { } chars) Set("Characters", Ox.Str(Math.Max(0, chars)));
        DocxFixups.Sanitize(root);
        DocxXml.Save(root, part);
    }

    private void WriteBibliography()
    {
        var sources = _doc.Sources;
        CustomXmlPart? part = null;
        if (sources.PartPath != null)
            part = _main.CustomXmlParts.FirstOrDefault(p => string.Equals(p.Uri.OriginalString, sources.PartPath, StringComparison.OrdinalIgnoreCase));
        if (part == null)
        {
            if (sources.Items.Count == 0) return;
            part = _main.AddNewPart<CustomXmlPart>("application/xml", NewMainRelId());
            var props = part.AddNewPart<CustomXmlPropertiesPart>(DocxXml.NewRelId(part));
            var ds = XNamespace.Get("http://schemas.openxmlformats.org/officeDocument/2006/customXml");
            var item = new XElement(ds + "datastoreItem",
                new XAttribute(XNamespace.Xmlns + "ds", ds.NamespaceName),
                new XAttribute(ds + "itemID", Guid.NewGuid().ToString("B").ToUpperInvariant()),
                new XElement(ds + "schemaRefs", new XElement(ds + "schemaRef", new XAttribute(ds + "uri", Ns.B.NamespaceName))));
            DocxXml.Save(item, props);
            var path = part.Uri.OriginalString;
            _after.Add(() => sources.PartPath = path);
        }
        var xml = sources.ToXml();
        if (xml.Root != null) DocxFixups.Sanitize(xml.Root);
        DocxXml.Save(xml, part);
    }

    private static string Str(long v) => v.ToString(CultureInfo.InvariantCulture);
}
