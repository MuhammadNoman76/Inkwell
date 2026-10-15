using System.Text.RegularExpressions;
using Inkwell.Core.Model;

namespace Inkwell.Core.Editing;

public sealed class FindOptions
{
    public bool MatchCase { get; set; }
    public bool WholeWords { get; set; }
    public bool Regex { get; set; }
    public bool IncludeNotes { get; set; } = true;
    public bool IncludeComments { get; set; } = true;
    public bool IncludeHeadersFooters { get; set; } = true;
}

/// <summary>A match: a range in one paragraph.</summary>
public sealed class FindMatch
{
    public FindMatch(Paragraph para, int start, int end)
    {
        Para = para;
        Start = start;
        End = end;
    }

    public Paragraph Para { get; }
    public int Start { get; }
    public int End { get; }

    public Selection Selection => new(new DocPos(Para, Start), new DocPos(Para, End));

    public string Where => Para.Story switch
    {
        Note n => n.NoteKind == NoteKind.Footnote ? "Footnote" : "Endnote",
        Comment => "Comment",
        HeaderFooter h => h.IsHeader ? "Header" : "Footer",
        _ => "",
    };
}

/// <summary>Find and replace across the body, notes, comments, headers and footers (match case, whole words, regular expressions).</summary>
public static class FindReplace
{
    /// <summary>The pattern for a query, or null if a regular expression is invalid (the error is returned).</summary>
    public static Regex? Build(string query, FindOptions options, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(query)) return null;
        var pattern = options.Regex ? query : Regex.Escape(query);
        if (options.WholeWords) pattern = @"(?<![\p{L}\p{Nd}_])(?:" + pattern + @")(?![\p{L}\p{Nd}_])";
        try
        {
            return new Regex(pattern, (options.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) | RegexOptions.Multiline,
                TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public static IEnumerable<Story> Stories(Document doc, FindOptions options)
    {
        yield return doc.Body;
        if (options.IncludeHeadersFooters)
            foreach (var h in doc.HeadersFooters.Values.OrderBy(h => h.IsHeader ? 0 : 1)) yield return h;
        if (options.IncludeNotes)
        {
            foreach (var n in doc.Footnotes.Values.Where(n => !n.IsSpecial)) yield return n;
            foreach (var n in doc.Endnotes.Values.Where(n => !n.IsSpecial)) yield return n;
        }
        if (options.IncludeComments)
            foreach (var c in doc.Comments) yield return c;
    }

    /// <summary>All matches in reading order (body first, then headers / footers, notes and comments).</summary>
    public static List<FindMatch> FindAll(Document doc, string query, FindOptions options, int limit = 10000)
    {
        var result = new List<FindMatch>();
        var regex = Build(query, options, out _);
        if (regex == null) return result;
        foreach (var story in Stories(doc, options))
        {
            foreach (var p in story.Paragraphs)
            {
                var text = p.Text;
                if (text.Length == 0) continue;
                try
                {
                    foreach (Match m in regex.Matches(text))
                    {
                        if (m.Length == 0) continue;
                        result.Add(new FindMatch(p, m.Index, m.Index + m.Length));
                        if (result.Count >= limit) return result;
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    return result;
                }
            }
        }
        return result;
    }

    /// <summary>The replacement text for a match (regular expression groups like $1 expand).</summary>
    public static string ReplacementFor(FindMatch match, string query, string replacement, FindOptions options)
    {
        if (!options.Regex) return replacement;
        var regex = Build(query, options, out _);
        if (regex == null) return replacement;
        var text = match.Para.Text.Substring(match.Start, match.End - match.Start);
        var m = regex.Match(text);
        return m.Success ? m.Result(replacement) : replacement;
    }

    /// <summary>Replaces one match (keeping the formatting of its first character).</summary>
    public static void ReplaceOne(DocEditor editor, FindMatch match, string query, string replacement, FindOptions options)
    {
        var text = ReplacementFor(match, query, replacement, options);
        editor.Edit("Replace", t =>
        {
            DocEditor.EnsureEditable(match.Para);
            Replace(editor, t, match, text);
            return new Selection(new DocPos(match.Para, match.Start + text.Length));
        });
    }

    /// <summary>Replaces every match as one undo step; returns how many were replaced.</summary>
    public static int ReplaceAll(DocEditor editor, string query, string replacement, FindOptions options)
    {
        var matches = FindAll(editor.Doc, query, options).Where(m => !Nav.IsReadOnly(m.Para)).ToList();
        if (matches.Count == 0) return 0;
        var texts = matches.Select(m => ReplacementFor(m, query, replacement, options)).ToList();
        editor.Edit("Replace all", t =>
        {
            // Back to front, so earlier offsets in a paragraph stay valid.
            for (var i = matches.Count - 1; i >= 0; i--) Replace(editor, t, matches[i], texts[i]);
            return null;
        });
        return matches.Count;
    }

    private static void Replace(DocEditor editor, Transaction t, FindMatch match, string text)
    {
        var p = match.Para;
        t.Touch(p);
        var props = p.InlineAt(match.Start)?.Props.Clone() ?? p.PropsForTypingAt(match.Start);
        var link = p.InlineAt(match.Start)?.Link;
        editor.DeleteInParagraph(t, p, match.Start, match.End);
        if (text.Length > 0) p.InsertText(match.Start, text, props, link, editor.TrackChanges ? editor.NewRevision() : null);
    }
}
