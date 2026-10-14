using Inkwell.Core.Model;

namespace Inkwell.Core.Citations;

/// <summary>Builds document model content (runs, paragraphs, fields) from formatted citations.</summary>
public static class CitationModel
{
    /// <summary>The paragraph style of bibliography entries (its hanging indent comes from the style).</summary>
    public const string BibliographyStyleId = "Bibliography";

    /// <summary>Text runs for formatted text: italic spans get <see cref="RunProps.Italic"/>, bold spans <see cref="RunProps.Bold"/>.</summary>
    public static List<Inline> ToRuns(StyledText text, RunProps? baseProps = null)
    {
        var runs = new List<Inline>();
        foreach (var span in text)
        {
            if (span.Text.Length == 0) continue;
            var props = baseProps?.Clone() ?? new RunProps();
            if (span.Italic) props.Italic = true;
            if (span.Bold) props.Bold = true;
            runs.Add(new TextRun(span.Text) { Props = props });
        }
        return runs;
    }

    /// <summary>A paragraph holding formatted text.</summary>
    public static Paragraph ToParagraph(StyledText text, string? styleId = BibliographyStyleId, RunProps? baseProps = null)
    {
        var p = new Paragraph();
        if (styleId != null) p.Props.StyleId = styleId;
        p.Inlines.AddRange(ToRuns(text, baseProps));
        p.Normalize();
        return p;
    }

    /// <summary>A bibliography paragraph; IEEE entries start with their "[n]" label and a tab (to the hanging indent).</summary>
    public static Paragraph EntryParagraph(BibliographyEntry entry, string? styleId = BibliographyStyleId, RunProps? baseProps = null)
    {
        var p = new Paragraph();
        if (styleId != null) p.Props.StyleId = styleId;
        if (entry.Label != null) p.Inlines.Add(new TextRun(entry.Label + "\t") { Props = baseProps?.Clone() ?? new RunProps() });
        p.Inlines.AddRange(ToRuns(entry.Text, baseProps));
        p.Normalize();
        return p;
    }

    /// <summary>The bibliography's paragraphs (Word's "no sources" sentence when there are none).</summary>
    public static List<Paragraph> BibliographyParagraphs(IEnumerable<BibliographyEntry> entries, string? styleId = BibliographyStyleId, RunProps? baseProps = null)
    {
        var list = entries.Select(e => EntryParagraph(e, styleId, baseProps)).ToList();
        if (list.Count == 0) list.Add(ToParagraph(new StyledText(CitationEngine.NoSourcesText), styleId, baseProps));
        return list;
    }

    /// <summary>The bibliography of a document in its style.</summary>
    public static List<Paragraph> BibliographyParagraphs(Document document, string? styleId = BibliographyStyleId) =>
        BibliographyParagraphs(CitationEngine.ForDocument(document).Bibliography(), styleId);

    /// <summary>A new CITATION field with its formatted result.</summary>
    public static FieldInline CitationInline(CitationEngine engine, CitationField field, RunProps? props = null)
    {
        var f = new FieldInline { Code = field.ToCode(), Props = props?.Clone() ?? new RunProps() };
        f.Result = ToRuns(engine.Cite(field), props);
        return f;
    }

    /// <summary>Re-formats a CITATION field's result (after a style change or source edit). False if it isn't a citation or is locked.</summary>
    public static bool UpdateResult(FieldInline field, CitationEngine engine)
    {
        if (field.Locked || field.PreservedXml != null) return false;
        var parsed = CitationField.Parse(field.Code);
        if (parsed == null) return false;
        var baseProps = field.Result.FirstOrDefault()?.Props.Clone() ?? field.Props.Clone();
        baseProps.Italic = null;
        field.Result = ToRuns(engine.Cite(parsed), baseProps);
        field.Dirty = false;
        return true;
    }
}
