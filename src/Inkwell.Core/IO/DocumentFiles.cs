using Inkwell.Core.Model;

namespace Inkwell.Core.IO;

/// <summary>A file that couldn't be opened or saved, with a heading and message for the user.</summary>
public sealed class DocumentFileException : Exception
{
    public DocumentFileException(string heading, string message, Exception? inner = null) : base(message, inner)
    {
        Heading = heading;
    }

    public string Heading { get; }
}

/// <summary>A document read from a file.</summary>
public sealed class OpenResult
{
    public OpenResult(Document document, string format, string? savePath)
    {
        Document = document;
        Format = format;
        SavePath = savePath;
    }

    public Document Document { get; }

    /// <summary>docx, docm, dotx, md, txt, rtf, odt.</summary>
    public string Format { get; }

    /// <summary>The path Save writes to: the file itself for Word documents, null for imports (Save asks for a .docx name).</summary>
    public string? SavePath { get; }

    /// <summary>Notes for the user (what was converted, what's kept but not editable).</summary>
    public List<string> Notes { get; } = new();

    public string SourcePath { get; set; } = "";
}

/// <summary>Opening and saving documents in every format Inkwell reads and writes.</summary>
public static partial class DocumentFiles
{
    public static readonly string[] WordExtensions = { ".docx", ".docm", ".dotx", ".dotm" };
    public static readonly string[] ImportExtensions = { ".md", ".markdown", ".txt", ".text", ".rtf", ".odt", ".fodt", ".html", ".htm" };

    public static bool CanOpen(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return WordExtensions.Contains(ext) || ImportExtensions.Contains(ext);
    }

    /// <summary>The Open dialog filter.</summary>
    public const string OpenFilter =
        "Documents|*.docx;*.docm;*.dotx;*.dotm;*.md;*.markdown;*.txt;*.rtf;*.odt;*.fodt;*.html;*.htm|Word documents|*.docx;*.docm|Word templates|*.dotx;*.dotm|Markdown|*.md;*.markdown|Text|*.txt|Rich Text Format|*.rtf|OpenDocument Text|*.odt;*.fodt|Web page|*.html;*.htm|All files|*.*";

    /// <summary>Reads a document. Throws <see cref="DocumentFileException"/> with a friendly message when it can't.</summary>
    public static OpenResult Open(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            var result = OpenCore(path, ext);
            result.SourcePath = path;
            return result;
        }
        catch (DocumentFileException)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            throw;
        }
        catch (IOException ex) when (ex is not FileNotFoundException)
        {
            throw new DocumentFileException("The file is in use", "Another program has this file open, or it can't be read right now. Close it there and try again.\n\n" + ex.Message, ex);
        }
        catch (Exception ex) when (IsReaderException(ex))
        {
            throw new DocumentFileException("Inkwell can't open this file", ex.Message, ex);
        }
    }

    private static partial OpenResult OpenCore(string path, string ext);

    private static partial bool IsReaderException(Exception ex);
}

public static partial class DocumentFiles
{
    /// <summary>Saves a Word document atomically (embedding Inkwell's fonts when asked); returns the bytes written.</summary>
    public static byte[] SaveDocx(Document doc, string path, bool embedFonts, bool dropMacros = false) => SaveDocxCore(doc, path, embedFonts, dropMacros);

    /// <summary>The .docx bytes of a document without touching any file (recovery copies, version history of clones).</summary>
    public static byte[] DocxBytes(Document doc, bool embedFonts = false) => DocxBytesCore(doc, embedFonts);

    /// <summary>Exports Markdown (md), HTML (html) or plain text (txt).</summary>
    public static void Export(Document doc, string path, string format) => ExportCore(doc, path, format);

    private static partial byte[] SaveDocxCore(Document doc, string path, bool embedFonts, bool dropMacros);

    private static partial byte[] DocxBytesCore(Document doc, bool embedFonts);

    private static partial void ExportCore(Document doc, string path, string format);
}
