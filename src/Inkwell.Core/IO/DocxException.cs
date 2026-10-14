namespace Inkwell.Core.IO;

/// <summary>Why a .docx file couldn't be opened or saved.</summary>
public enum DocxErrorKind
{
    /// <summary>The file is damaged (broken ZIP, malformed XML, missing main part…).</summary>
    Damaged,

    /// <summary>The file isn't a Word document (another kind of ZIP package, plain text, a picture…).</summary>
    NotAWordDocument,

    /// <summary>The document is encrypted with a password.</summary>
    PasswordProtected,

    /// <summary>A Word 97-2003 (.doc) binary document.</summary>
    LegacyWordFormat,

    /// <summary>Another document format Inkwell doesn't open directly (RTF, OpenDocument, Word 2003 XML…).</summary>
    OtherFormat,

    /// <summary>The file is empty.</summary>
    Empty,

    /// <summary>The file couldn't be read or written (missing, locked by another program, no permission, disk full…).</summary>
    FileAccess,
}

/// <summary>
/// A .docx file couldn't be opened or saved. <see cref="Exception.Message"/> is written for the person using
/// Inkwell; <see cref="Exception.InnerException"/> keeps the technical cause.
/// </summary>
public sealed class DocxException : Exception
{
    public DocxException(DocxErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    public DocxErrorKind Kind { get; }
}
