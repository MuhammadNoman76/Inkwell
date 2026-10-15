using System.Text;
using Inkwell.Core.Editing;
using Inkwell.Core.Formats;
using Inkwell.Core.Model;
using Inkwell.Core.Templates;

namespace Inkwell.Core.IO;

/// <summary>Imports (Markdown, text, RTF, ODT, HTML), exports (Markdown, HTML, text) and the clipboard formats.</summary>
public static partial class DocumentFiles
{
    /// <summary>The design imported documents get (the user's default design).</summary>
    public static DocumentDesign? ImportDesign { get; set; }

    private static OpenResult ImportCore(string path, string ext)
    {
        Document doc;
        string format;
        switch (ext)
        {
            case ".md":
            case ".markdown":
                doc = MarkdownImporter.ImportFile(path, ImportDesign);
                format = "md";
                break;
            case ".txt":
            case ".text":
                doc = TextImporter.ImportFile(path, ImportDesign);
                format = "txt";
                break;
            case ".rtf":
                doc = RtfImporter.ImportFile(path, new RtfImportOptions { Design = ImportDesign });
                format = "rtf";
                break;
            case ".odt":
            case ".fodt":
                doc = OdtImporter.ImportFile(path, ImportDesign);
                format = "odt";
                break;
            case ".html":
            case ".htm":
                doc = HtmlImporter.ImportFile(path, ImportDesign);
                format = "html";
                break;
            default:
                throw new DocumentFileException("Inkwell can't open this kind of file", "Inkwell opens Word documents (.docx, .docm, .dotx), Markdown, plain text, RTF, OpenDocument text and HTML.");
        }
        return new OpenResult(doc, format, null);
    }

    private static bool IsImportException(Exception ex) => ex is InvalidDataException or System.Xml.XmlException or DecoderFallbackException;

    private static partial void ExportCore(Document doc, string path, string format)
    {
        var full = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(full)!;
        var name = Path.GetFileNameWithoutExtension(full);
        var hasPictures = doc.Stories.SelectMany(s => s.Paragraphs).SelectMany(p => p.Inlines).OfType<DrawingObject>().Any(d => d.IsPicture);
        try
        {
            switch (format)
            {
                case "md":
                {
                    var options = new MarkdownExportOptions { NewLine = "\n" };
                    if (hasPictures)
                    {
                        options.ImageFolder = Path.Combine(folder, name + "_files");
                        options.ImageFolderReference = name + "_files";
                    }
                    WriteAtomically(full, new UTF8Encoding(false).GetBytes(MarkdownExporter.Export(doc, options)));
                    break;
                }
                case "html":
                {
                    // One self-contained file: pictures are embedded.
                    var html = HtmlExporter.Export(doc, new HtmlExportOptions { Title = doc.Properties.Title });
                    WriteAtomically(full, new UTF8Encoding(false).GetBytes(html));
                    break;
                }
                case "txt":
                    WriteAtomically(full, TextExporter.ExportBytes(doc, new TextExportOptions { IncludeBom = false }));
                    break;
                default:
                    throw new ArgumentException("Unknown export format: " + format, nameof(format));
            }
        }
        catch (IOException ex)
        {
            throw new DocumentFileException("The file couldn't be written", ex.Message, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new DocumentFileException("The file couldn't be written", "Inkwell isn't allowed to write there. Choose another folder.\n\n" + ex.Message, ex);
        }
    }

    /// <summary>Writes a file through a temporary file in the same folder, so a failure never leaves half a file.</summary>
    public static void WriteAtomically(string path, byte[] bytes)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            Compat.MoveOverwrite(temp, path);
        }
        finally
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch (IOException)
            {
                // left for the next clean-up
            }
        }
    }

    // ------------------------------------------------------------------ clipboard

    /// <summary>A scratch document clipboard content is read into (with the default design's styles).</summary>
    private static Document Scratch() => NewDocument.Create(ImportDesign);

    /// <summary>Clipboard HTML (CF_HTML text) as a document keeping its own look (the paste then matches or keeps it), or null.</summary>
    public static Document? ImportClipboardHtml(string cfHtml) => FromPaste(target => HtmlImporter.ImportClipboard(cfHtml, target, new HtmlImportOptions { KeepSourceFormatting = true }));

    /// <summary>Clipboard HTML as UTF-8 bytes (what Windows really stores).</summary>
    public static Document? ImportClipboardHtml(byte[] cfHtml) => FromPaste(target => HtmlImporter.ImportClipboard(cfHtml, target, new HtmlImportOptions { KeepSourceFormatting = true }));

    /// <summary>Clipboard RTF as a document, or null.</summary>
    public static Document? ImportClipboardRtf(string rtf) => FromPaste(target => RtfImporter.ImportFragment(rtf, target, keepSourceFormatting: true));

    /// <summary>Markdown text as a document (for pasting Markdown as formatted text).</summary>
    public static Document? ImportMarkdownText(string markdown) => FromPaste(target => MarkdownImporter.ImportFragment(markdown, target));

    private static Document? FromPaste(Func<Document, PasteResult> import)
    {
        var doc = Scratch();
        var result = import(doc);
        if (result.Blocks.Count == 0) return null;
        foreach (var b in result.Blocks) doc.Body.Blocks.Add(b);
        return doc;
    }

    /// <summary>Copied content as CF_HTML (UTF-8 bytes) for other programs, or null.</summary>
    public static byte[]? FragmentHtml(DocFragment fragment)
    {
        var source = fragment.Source ?? Scratch();
        var html = HtmlExporter.ExportClipboard(source, fragment.Blocks, new HtmlExportOptions { IncludeNotes = false });
        return html.PayloadBytes;
    }
}
