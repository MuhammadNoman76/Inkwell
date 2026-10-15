using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace Inkwell.Core.IO;

/// <summary>
/// Checks what a file is before it is opened as a .docx (so other formats get a clear message), and converts
/// Strict Open XML documents to the usual (transitional) form.
/// </summary>
/// <remarks>
/// Strict Open XML (ISO/IEC 29500 Strict, namespaces under http://purl.oclc.org/ooxml/) is converted on open:
/// every XML part of the package has its namespaces, relationship types and URI attribute values mapped to the
/// transitional ones, and w:conformance is removed. The document then opens like any other, keeps everything
/// (charts, custom XML… are converted too) and is saved as a normal .docx; a warning says so.
/// </remarks>
internal static class DocxPackage
{
    public const string StrictPrefix = "http://purl.oclc.org/ooxml/";
    private const string OfficeDocumentRelType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";
    private const string StrictOfficeDocumentRelType = StrictPrefix + "officeDocument/relationships/officeDocument";

    public const string StrictWarning =
        "This document was saved in the Strict Open XML format. Inkwell opened it as a standard Word document; saving writes the standard .docx format.";

    /// <summary>Checks the bytes are a Word package and returns the bytes to open (converted when Strict).</summary>
    public static byte[] Prepare(byte[] bytes, string? fileName, List<string> warnings)
    {
        if (bytes.Length == 0)
            throw new DocxException(DocxErrorKind.Empty, "The file is empty, so there is nothing to open.");
        if (IsCompoundFile(bytes)) throw ClassifyCompoundFile(bytes, fileName);
        if (!IsZip(bytes)) throw ClassifyNonZip(bytes, fileName);

        try
        {
            using var archive = new ZipArchive(new MemoryStream(bytes, false), ZipArchiveMode.Read);
            var entries = archive.Entries.ToList();
            ZipArchiveEntry? Find(string name) => entries.FirstOrDefault(e => string.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase));

            var mimetype = Find("mimetype");
            if (mimetype != null)
            {
                var text = ReadText(mimetype, 200);
                if (text.Contains("opendocument"))
                {
                    throw new DocxException(DocxErrorKind.OtherFormat, text.Contains("opendocument.text")
                        ? "This is an OpenDocument text file (.odt), not a Word document. Open it in LibreOffice or Word and save it as a Word Document (.docx), then open that file."
                        : "This is an OpenDocument file, not a Word document.");
                }
            }

            if (Find("[Content_Types].xml") == null)
                throw NotWord("This file is a ZIP archive, not a Word document.");

            var rels = Find("_rels/.rels");
            string? mainType = null;
            string? mainTarget = null;
            if (rels != null)
            {
                XDocument x;
                using (var s = rels.Open()) x = DocxXml.Load(s);
                foreach (var r in x.Root?.Elements() ?? Enumerable.Empty<XElement>())
                {
                    var type = r.Attribute("Type")?.Value;
                    if (type == OfficeDocumentRelType || type == StrictOfficeDocumentRelType)
                    {
                        mainType = type;
                        mainTarget = r.Attribute("Target")?.Value;
                        break;
                    }
                }
            }

            if (mainType == null)
            {
                if (entries.Any(e => e.FullName.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)))
                    throw new DocxException(DocxErrorKind.OtherFormat, "This is an Excel workbook, not a Word document.");
                if (entries.Any(e => e.FullName.StartsWith("ppt/", StringComparison.OrdinalIgnoreCase)))
                    throw new DocxException(DocxErrorKind.OtherFormat, "This is a PowerPoint presentation, not a Word document.");
                throw new DocxException(DocxErrorKind.Damaged, "This document can't be opened because it is damaged: its main part is missing.");
            }

            var target = (mainTarget ?? "").TrimStart('/');
            if (target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
                throw new DocxException(DocxErrorKind.OtherFormat, "This is an Excel workbook, not a Word document.");
            if (target.StartsWith("ppt/", StringComparison.OrdinalIgnoreCase))
                throw new DocxException(DocxErrorKind.OtherFormat, "This is a PowerPoint presentation, not a Word document.");

            if (mainType == StrictOfficeDocumentRelType)
            {
                warnings.Add(StrictWarning);
                return ConvertStrict(entries);
            }
            return bytes;
        }
        catch (InvalidDataException ex)
        {
            throw Damaged(ex);
        }
        catch (System.Xml.XmlException ex)
        {
            throw Damaged(ex);
        }
    }

    public static DocxException Damaged(Exception? inner) =>
        new(DocxErrorKind.Damaged, "This document can't be opened because it is damaged. It may be incomplete (for example an interrupted download or copy).", inner);

    private static DocxException NotWord(string message) => new(DocxErrorKind.NotAWordDocument, message);

    public static bool IsZip(byte[] b) =>
        b.Length >= 4 && b[0] == 0x50 && b[1] == 0x4B && (b[2] == 3 && b[3] == 4 || b[2] == 5 && b[3] == 6 || b[2] == 7 && b[3] == 8);

    public static bool IsCompoundFile(byte[] b) =>
        b.Length >= 8 && b[0] == 0xD0 && b[1] == 0xCF && b[2] == 0x11 && b[3] == 0xE0 && b[4] == 0xA1 && b[5] == 0xB1 && b[6] == 0x1A && b[7] == 0xE1;

    private static DocxException ClassifyCompoundFile(byte[] bytes, string? fileName)
    {
        // Stream names in the compound file's directory are UTF-16LE.
        if (ContainsUtf16(bytes, "EncryptedPackage") || ContainsUtf16(bytes, "EncryptionInfo"))
        {
            return new DocxException(DocxErrorKind.PasswordProtected,
                "This document is protected with a password, and Inkwell can't open password-protected documents yet. " +
                "Open it in Word, remove the password (File › Info › Protect Document › Encrypt with Password), save it, and open it again.");
        }
        if (ContainsUtf16(bytes, "WordDocument"))
        {
            return new DocxException(DocxErrorKind.LegacyWordFormat,
                "This is a Word 97-2003 document (.doc). Inkwell opens Word documents in the current .docx format: " +
                "open it in Word, choose File › Save As › Word Document (*.docx), and open the new file.");
        }
        if (ContainsUtf16(bytes, "Workbook") || ContainsUtf16(bytes, "PowerPoint Document"))
            return new DocxException(DocxErrorKind.OtherFormat, "This is an older Excel or PowerPoint file, not a Word document.");
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        return new DocxException(ext is ".doc" or ".dot" ? DocxErrorKind.LegacyWordFormat : DocxErrorKind.OtherFormat,
            "This file uses an older Office format that Inkwell can't open. Open it in Word and save it as a Word Document (.docx).");
    }

    private static DocxException ClassifyNonZip(byte[] bytes, string? fileName)
    {
        var head = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 4096)).TrimStart('﻿', ' ', '\r', '\n', '\t');
        if (head.StartsWith("{\\rtf", StringComparison.Ordinal))
        {
            return new DocxException(DocxErrorKind.OtherFormat,
                "This is a Rich Text Format (RTF) file, not a .docx document. Open it in Word or WordPad and save it as a Word Document (.docx).");
        }
        if (head.StartsWith("%PDF", StringComparison.Ordinal))
            return new DocxException(DocxErrorKind.OtherFormat, "This is a PDF file, not a Word document.");
        if (head.StartsWith("<", StringComparison.Ordinal))
        {
            if (head.Contains("pkg:package") || head.Contains("w:wordDocument") || head.Contains("schemas.microsoft.com/office/word/2003"))
            {
                return new DocxException(DocxErrorKind.OtherFormat,
                    "This is a Word XML document, not a .docx file. Open it in Word and save it as a Word Document (.docx).");
            }
            if (head.IndexOf("<html", StringComparison.OrdinalIgnoreCase) >= 0)
                return new DocxException(DocxErrorKind.OtherFormat, "This is a web page (HTML), not a Word document.");
        }
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        if (ext is ".docx" or ".docm" or ".dotx" or ".dotm")
            return Damaged(null);
        return NotWord("This file isn't a Word document (.docx).");
    }

    private static bool ContainsUtf16(byte[] bytes, string text)
    {
        var pattern = Encoding.Unicode.GetBytes(text);
        var limit = bytes.Length - pattern.Length;
        for (var i = 0; i <= limit; i++)
        {
            if (bytes[i] != pattern[0]) continue;
            var match = true;
            for (var j = 1; j < pattern.Length; j++)
            {
                if (bytes[i + j] != pattern[j])
                {
                    match = false;
                    break;
                }
            }
            if (match) return true;
        }
        return false;
    }

    private static string ReadText(ZipArchiveEntry entry, int max)
    {
        using var s = entry.Open();
        var buffer = new byte[max];
        var n = 0;
        int read;
        while (n < max && (read = s.Read(buffer, n, max - n)) > 0) n += read;
        return Encoding.ASCII.GetString(buffer, 0, n);
    }

    // ------------------------------------------------------------------ Strict → transitional

    private static byte[] ConvertStrict(List<ZipArchiveEntry> entries)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            foreach (var entry in entries)
            {
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
                var target = zip.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                using var input = entry.Open();
                using var outStream = target.Open();
                if (IsXmlEntry(entry.FullName))
                {
                    XDocument x;
                    using (var ms = new MemoryStream())
                    {
                        input.CopyTo(ms);
                        ms.Position = 0;
                        x = DocxXml.Load(ms);
                    }
                    ConvertStrictXml(x);
                    if (x.Root != null) DocxFixups.Sanitize(x.Root);
                    DocxXml.Save(x, outStream);
                }
                else
                {
                    input.CopyTo(outStream);
                }
            }
        }
        return output.ToArray();
    }

    private static bool IsXmlEntry(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".xml" or ".rels" or ".vml";
    }

    private static void ConvertStrictXml(XDocument x)
    {
        if (x.Root == null) return;
        var conformance = Model.Ns.W + "conformance";
        foreach (var el in x.Root.DescendantsAndSelf().ToList())
        {
            el.Name = MapName(el.Name);
            var attrs = el.Attributes().ToList();
            if (!attrs.Any(a => NeedsMapping(a))) continue;
            el.RemoveAttributes();
            foreach (var a in attrs)
            {
                if (a.IsNamespaceDeclaration)
                {
                    el.Add(new XAttribute(a.Name, MapUri(a.Value)));
                    continue;
                }
                var name = MapName(a.Name);
                if (name == conformance) continue;
                el.Add(new XAttribute(name, a.Value.StartsWith(StrictPrefix, StringComparison.Ordinal) ? MapUri(a.Value) : a.Value));
            }
        }
    }

    private static bool NeedsMapping(XAttribute a) =>
        a.Name.NamespaceName.StartsWith(StrictPrefix, StringComparison.Ordinal) || a.Value.StartsWith(StrictPrefix, StringComparison.Ordinal);

    private static XName MapName(XName name)
    {
        var ns = name.NamespaceName;
        return ns.StartsWith(StrictPrefix, StringComparison.Ordinal) ? XName.Get(name.LocalName, MapUri(ns)) : name;
    }

    /// <summary>A Strict namespace or relationship type → its transitional equivalent.</summary>
    public static string MapUri(string uri)
    {
        if (!uri.StartsWith(StrictPrefix, StringComparison.Ordinal)) return uri;
        var rest = uri.Substring(StrictPrefix.Length);
        const string relPrefix = "officeDocument/relationships/";
        static string Fix(string s) => s switch
        {
            "extendedProperties" => "extended-properties",
            "customProperties" => "custom-properties",
            _ => s,
        };
        if (rest.StartsWith(relPrefix, StringComparison.Ordinal))
            return "http://schemas.openxmlformats.org/officeDocument/2006/relationships/" + Fix(rest.Substring(relPrefix.Length));
        var slash = rest.IndexOf('/');
        if (slash < 0) return "http://schemas.openxmlformats.org/" + rest;
        return "http://schemas.openxmlformats.org/" + rest.Substring(0, slash) + "/2006/" + Fix(rest.Substring(slash + 1));
    }
}
