using Inkwell.Core.Model;

namespace Inkwell.Core.IO;

public static partial class DocumentFiles
{
    private static partial OpenResult OpenCore(string path, string ext)
    {
        if (!WordExtensions.Contains(ext)) return ImportCore(path, ext);
        Document doc;
        try
        {
            doc = DocxReader.Load(path);
        }
        catch (DocxException ex) when (ex.Kind == DocxErrorKind.OtherFormat && LooksLikeRtf(path))
        {
            // An RTF file with a Word extension (some programs save them like that).
            var imported = ImportCore(path, ".rtf");
            imported.Notes.Insert(0, "This file is really a Rich Text Format document; it was converted.");
            return imported;
        }
        var format = ext.TrimStart('.');
        // Templates open as new documents (saving asks for a name), like Word.
        var savePath = ext is ".dotx" or ".dotm" ? null : path;
        return new OpenResult(doc, ext is ".dotx" or ".dotm" ? "docx" : format, savePath);
    }

    private static bool LooksLikeRtf(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var head = new byte[5];
            return fs.Read(head, 0, 5) == 5 && System.Text.Encoding.ASCII.GetString(head) == "{\rtf";
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static partial bool IsReaderException(Exception ex) => ex is DocxException or InvalidDataException or FormatException or System.Xml.XmlException || IsImportException(ex);

    private static partial byte[] SaveDocxCore(Document doc, string path, bool embedFonts, bool dropMacros)
    {
        try
        {
            return DocxWriter.Save(doc, path, new DocxSaveOptions { EmbedFonts = embedFonts, DropMacros = dropMacros, LastModifiedBy = doc.Properties.LastModifiedBy });
        }
        catch (DocxException ex)
        {
            throw new DocumentFileException(ex.Kind == DocxErrorKind.FileAccess ? "The file couldn't be written" : "The document couldn't be saved",
                ex.Message + "\n\nYour work is still open here. Try Save as to save it somewhere else.", ex);
        }
    }

    private static partial byte[] DocxBytesCore(Document doc, bool embedFonts)
    {
        using var ms = new MemoryStream();
        return DocxWriter.Save(doc, ms, new DocxSaveOptions { EmbedFonts = embedFonts, Verify = false });
    }
}
