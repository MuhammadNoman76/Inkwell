namespace Inkwell.Core.IO;

/// <summary>The kind of WordprocessingML package to write (decides the main part's content type).</summary>
public enum DocxFormat
{
    /// <summary>From the file extension when saving to a path, otherwise the format the document was loaded in (.docx for new documents).</summary>
    Auto,

    /// <summary>.docx</summary>
    Document,

    /// <summary>.docm (keeps macros).</summary>
    MacroEnabledDocument,

    /// <summary>.dotx</summary>
    Template,

    /// <summary>.dotm (keeps macros).</summary>
    MacroEnabledTemplate,
}

/// <summary>Options for <see cref="DocxWriter"/>.</summary>
public sealed class DocxSaveOptions
{
    /// <summary>The package kind to write. A macro-free kind always drops the macros (see <see cref="DropMacros"/>).</summary>
    public DocxFormat Format { get; set; } = DocxFormat.Auto;

    /// <summary>
    /// Remove the VBA project (used when a .docm is saved as .docx). Saving to a macro-free format
    /// (.docx, .dotx) removes the macros in any case, since Word refuses macros in those formats.
    /// </summary>
    public bool DropMacros { get; set; }

    /// <summary>Embed the bundled fonts the document uses (regular, bold, italic, bold italic), obfuscated as Word does.</summary>
    public bool EmbedFonts { get; set; }

    /// <summary>Written to the document properties (cp:lastModifiedBy) when set.</summary>
    public string? LastModifiedBy { get; set; }

    /// <summary>Page count for the application properties (docProps/app.xml), when known.</summary>
    public int? Pages { get; set; }

    /// <summary>Word count for the application properties, when known.</summary>
    public int? Words { get; set; }

    /// <summary>Character count for the application properties, when known.</summary>
    public int? Characters { get; set; }

    /// <summary>The time written as the modification date (now when null).</summary>
    public DateTime? Now { get; set; }

    /// <summary>Check that the written package opens again before handing it out (default true).</summary>
    public bool Verify { get; set; } = true;

    /// <summary>Filled by the writer: things it had to change or leave out while saving (for a notice).</summary>
    public List<string> Warnings { get; } = new();
}
