using System.Text;

namespace Inkwell.Core.Citations;

/// <summary>A piece of a formatted citation or bibliography entry: text plus its emphasis.</summary>
public readonly record struct StyledSpan(string Text, bool Italic = false, bool Bold = false);

/// <summary>
/// Formatted text as a list of spans (neighbouring spans with the same emphasis are merged). The editor turns
/// each span into a run; <see cref="CitationModel"/> builds model runs and paragraphs from it.
/// </summary>
public sealed class StyledText : IReadOnlyList<StyledSpan>
{
    private readonly List<StyledSpan> _spans = new();

    public StyledText()
    {
    }

    public StyledText(string? text, bool italic = false, bool bold = false)
    {
        Append(text, italic, bold);
    }

    public int Count => _spans.Count;

    public StyledSpan this[int index] => _spans[index];

    public bool IsEmpty => _spans.Count == 0;

    /// <summary>The text without emphasis.</summary>
    public string PlainText
    {
        get
        {
            var sb = new StringBuilder();
            foreach (var s in _spans) sb.Append(s.Text);
            return sb.ToString();
        }
    }

    public StyledText Append(string? text, bool italic = false, bool bold = false)
    {
        if (string.IsNullOrEmpty(text)) return this;
        if (_spans.Count > 0)
        {
            var last = _spans[_spans.Count - 1];
            if (last.Italic == italic && last.Bold == bold)
            {
                _spans[_spans.Count - 1] = last with { Text = last.Text + text };
                return this;
            }
        }
        _spans.Add(new StyledSpan(text!, italic, bold));
        return this;
    }

    public StyledText AppendItalic(string? text) => Append(text, italic: true);

    public StyledText Append(StyledText? other)
    {
        if (other == null) return this;
        foreach (var s in other._spans) Append(s.Text, s.Italic, s.Bold);
        return this;
    }

    /// <summary>The last character, or '\0' when empty.</summary>
    public char LastChar
    {
        get
        {
            for (var i = _spans.Count - 1; i >= 0; i--)
            {
                var t = _spans[i].Text;
                if (t.Length > 0) return t[t.Length - 1];
            }
            return '\0';
        }
    }

    /// <summary>True when the text ends with sentence-ending punctuation (. ? !), possibly inside closing quotes.</summary>
    public bool EndsWithTerminal => CiteText.EndsWithTerminal(PlainText);

    /// <summary>Adds <paramref name="mark"/> unless the text already ends with . ? or ! (so "Why?" never becomes "Why?.").</summary>
    public StyledText Terminate(char mark = '.')
    {
        if (IsEmpty || EndsWithTerminal) return this;
        return Append(mark.ToString());
    }

    /// <summary>Adds a space unless the text is empty or already ends with whitespace.</summary>
    public StyledText Space()
    {
        if (IsEmpty || char.IsWhiteSpace(LastChar)) return this;
        return Append(" ");
    }

    /// <summary>The text with *italic* and **bold** marks (for tests, logs and plain-text export).</summary>
    public string ToMarkup()
    {
        var sb = new StringBuilder();
        foreach (var s in _spans)
        {
            var mark = (s.Italic ? "*" : "") + (s.Bold ? "**" : "");
            sb.Append(mark).Append(s.Text).Append(Reverse(mark));
        }
        return sb.ToString();
    }

    private static string Reverse(string s)
    {
        var a = s.ToCharArray();
        Array.Reverse(a);
        return new string(a);
    }

    public StyledText Clone()
    {
        var c = new StyledText();
        c._spans.AddRange(_spans);
        return c;
    }

    public IEnumerator<StyledSpan> GetEnumerator() => _spans.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _spans.GetEnumerator();

    public override string ToString() => PlainText;
}
