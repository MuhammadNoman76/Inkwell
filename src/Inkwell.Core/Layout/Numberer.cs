using System.Text;
using Inkwell.Core.Model;
using Inkwell.Core.Styles;

namespace Inkwell.Core.Layout;

/// <summary>
/// Computes list labels in reading order, with Word's rules made predictable: counters belong to the list
/// definition (w:abstractNum) so separate list instances of one definition continue each other, a list instance
/// with a start override (Restart at…) restarts where it first appears, and a level restarts after any higher
/// level unless the definition says otherwise.
/// </summary>
public static class Numberer
{
    private sealed class Counters
    {
        public readonly int[] Values = new int[9];
        public readonly bool[] Started = new bool[9];
    }

    /// <summary>Labels for every numbered paragraph of the given stories.</summary>
    public static Dictionary<Paragraph, string> Labels(Document doc, StyleResolver styles, IEnumerable<Story> stories)
    {
        var result = new Dictionary<Paragraph, string>(ReferenceEqualityComparer<Paragraph>.Instance);
        foreach (var story in stories) Label(doc, styles, story.Paragraphs, result);
        return result;
    }

    /// <summary>Labels for a sequence of paragraphs (one numbering run).</summary>
    public static void Label(Document doc, StyleResolver styles, IEnumerable<Paragraph> paragraphs, Dictionary<Paragraph, string> result)
    {
        var numbering = doc.Numbering;
        if (numbering.IsEmpty) return;
        var counters = new Dictionary<int, Counters>();
        var seenNums = new HashSet<int>();
        foreach (var p in paragraphs)
        {
            var rp = styles.ResolvePara(p, null);
            if (rp.NumId <= 0) continue;
            var level = numbering.Level(rp.NumId, rp.NumLevel);
            if (level == null) continue;
            var label = Next(numbering, level, counters, seenNums);
            if (label != null) result[p] = label;
        }
    }

    private static string? Next(Numbering numbering, NumberingLevel level, Dictionary<int, Counters> all, HashSet<int> seenNums)
    {
        if (!all.TryGetValue(level.AbstractNumId, out var c))
        {
            c = new Counters();
            all[level.AbstractNumId] = c;
        }
        var l = level.Level;

        // A list instance with start overrides restarts those levels where it first appears.
        if (seenNums.Add(level.NumId))
        {
            for (var i = 0; i < 9; i++)
            {
                var other = numbering.Level(level.NumId, i);
                if (other?.StartOverride is { } start)
                {
                    c.Values[i] = start - 1;
                    c.Started[i] = true;
                    for (var d = i + 1; d < 9; d++) c.Started[d] = false;
                }
            }
        }

        c.Values[l] = c.Started[l] ? c.Values[l] + 1 : level.Start;
        c.Started[l] = true;
        // Deeper levels restart after this one (w:lvlRestart 0 = never).
        for (var d = l + 1; d < 9; d++)
        {
            var deeper = numbering.Level(level.NumId, d);
            var restart = deeper?.Restart;
            if (restart == 0) continue;
            if (restart == null || l < restart) c.Started[d] = false;
        }

        if (level.Format == "bullet") return BulletText(level.Text);
        if (level.Format == "none" && !level.Text.Contains('%')) return level.Text;
        var sb = new StringBuilder();
        var text = level.Text;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '%' && i + 1 < text.Length && text[i + 1] is >= '1' and <= '9')
            {
                var idx = text[i + 1] - '1';
                var lvl = numbering.Level(level.NumId, idx);
                var value = idx <= l && c.Started[idx] ? c.Values[idx] : lvl?.Start ?? 1;
                var format = level.IsLegal && idx < l ? "decimal" : lvl?.Format ?? "decimal";
                if (format != "none") sb.Append(NumberFormats.Format(value, format == "bullet" ? "decimal" : format));
                i++;
            }
            else
            {
                sb.Append(text[i]);
            }
        }
        return sb.ToString();
    }

    /// <summary>Symbol-font bullets (U+F0B7 in Symbol, U+F0A7 in Wingdings…) shown as their Unicode look-alikes.</summary>
    public static string BulletText(string text)
    {
        if (text.Length == 0) return "";
        var c = text[0];
        return c switch
        {
            '' => "•",
            '' => "▪",
            '' => "➢",
            '' => "❖",
            '' => "✓",
            '' => "◆",
            '' => "■",
            '' => "❑",
            '' => "➔",
            'o' when text.Length == 1 => "◦",
            _ when c >= '' && c <= '' => "•",
            _ => text,
        };
    }

    /// <summary>Displayed numbers of footnotes and endnotes in reading order of their references.</summary>
    public static Dictionary<(NoteKind, int), string> NoteNumbers(Document doc)
    {
        var result = new Dictionary<(NoteKind, int), string>();
        var foot = doc.Settings.FootnoteStart;
        var end = doc.Settings.EndnoteStart;
        var footFormat = doc.Settings.FootnoteFormat;
        var endFormat = doc.Settings.EndnoteFormat;
        foreach (var p in doc.Body.Paragraphs)
        {
            foreach (var inline in p.Inlines)
            {
                if (inline is not NoteReference r || r.CustomMark) continue;
                var key = (r.Kind, r.Id);
                if (result.ContainsKey(key)) continue;
                result[key] = r.Kind == NoteKind.Footnote ? NumberFormats.Format(foot++, footFormat) : NumberFormats.Format(end++, endFormat);
            }
        }
        return result;
    }
}
