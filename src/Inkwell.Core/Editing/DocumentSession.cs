using Inkwell.Core.Model;

namespace Inkwell.Core.Editing;

/// <summary>A document open in a window: the model, its editor (selection, undo), its file and saved state.</summary>
public sealed class DocumentSession
{
    public DocumentSession(Document document, string? filePath = null, string format = "docx")
    {
        Document = document;
        Editor = new DocEditor(document);
        FilePath = filePath;
        Format = format;
        SavedVersion = Editor.History.Version;
    }

    public Document Document { get; }

    public DocEditor Editor { get; }

    /// <summary>The file the document is saved to (null until saved; imported files other than .docx have no save path).</summary>
    public string? FilePath { get; set; }

    /// <summary>docx (also docm / dotx), md, txt, rtf, odt: what the file was opened as.</summary>
    public string Format { get; set; }

    /// <summary>The undo version that matches the file on disk.</summary>
    public int SavedVersion { get; private set; }

    /// <summary>Changed outside the undo history (imports, recovered documents).</summary>
    public bool ForcedDirty { get; set; }

    public bool IsDirty => ForcedDirty || Editor.History.Version != SavedVersion;

    public string DisplayName =>
        FilePath != null ? Path.GetFileNameWithoutExtension(FilePath) :
        !string.IsNullOrWhiteSpace(Document.Properties.Title) ? Document.Properties.Title! : "Untitled document";

    public void MarkSaved(int version, string? path = null)
    {
        SavedVersion = version;
        ForcedDirty = false;
        if (path != null) FilePath = path;
    }

    public void MarkDirty() => ForcedDirty = true;

    /// <summary>A stable key for this session's recovery copy.</summary>
    public string RecoveryKey { get; set; } = Guid.NewGuid().ToString("N");
}
