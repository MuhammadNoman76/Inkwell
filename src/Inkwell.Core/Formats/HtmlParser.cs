using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Inkwell.Core.Formats;

/// <summary>A node of a parsed HTML document: an element, text, a comment, or a downlevel-revealed conditional block ("!if").</summary>
internal sealed class HtmlNode
{
    public HtmlNode(string name, string text = "")
    {
        Name = name;
        Text = text;
    }

    /// <summary>Lower-case tag name, or "#text", "#comment", "#document", "!if" (Word's &lt;![if …]&gt; … &lt;![endif]&gt;).</summary>
    public string Name { get; }

    /// <summary>Text of a text or comment node; the condition of an "!if" node.</summary>
    public string Text { get; set; }

    public List<KeyValuePair<string, string>> Attributes { get; } = new();

    public List<HtmlNode> Children { get; } = new();

    public HtmlNode? Parent { get; private set; }

    public bool IsElement => Name[0] != '#' && Name != "!if";

    public string? Attr(string name)
    {
        foreach (var kv in Attributes)
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

    public void Add(HtmlNode child)
    {
        child.Parent = this;
        Children.Add(child);
    }

    /// <summary>All text below this node.</summary>
    public string InnerText()
    {
        var sb = new StringBuilder();
        void Walk(HtmlNode n)
        {
            if (n.Name == "#text") sb.Append(n.Text);
            foreach (var c in n.Children) Walk(c);
        }
        Walk(this);
        return sb.ToString();
    }

    public IEnumerable<HtmlNode> Descendants()
    {
        foreach (var c in Children)
        {
            yield return c;
            foreach (var d in c.Descendants()) yield return d;
        }
    }

    public override string ToString() => Name;
}

/// <summary>
/// A forgiving HTML parser in the spirit of browsers: unclosed and stray tags, implied end tags (p, li, td, tr,
/// option…), void elements, entities (named, decimal, hexadecimal, Windows-1252 remapping), comments, CDATA,
/// raw-text elements (script, style), and Word's downlevel-revealed conditional comments
/// (&lt;![if !supportLists]&gt; … &lt;![endif]&gt;), which become "!if" nodes.
/// </summary>
internal static class HtmlParser
{
    private static readonly HashSet<string> Void = new(StringComparer.Ordinal)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr", "basefont", "bgsound", "frame", "keygen",
    };

    private static readonly HashSet<string> RawText = new(StringComparer.Ordinal) { "script", "style", "xmp", "iframe", "noembed", "noframes", "textarea", "title" };

    /// <summary>Elements whose start closes an open &lt;p&gt;.</summary>
    private static readonly HashSet<string> ClosesP = new(StringComparer.Ordinal)
    {
        "address", "article", "aside", "blockquote", "center", "details", "dialog", "dir", "div", "dl", "fieldset", "figcaption", "figure", "footer", "form",
        "h1", "h2", "h3", "h4", "h5", "h6", "header", "hgroup", "hr", "main", "menu", "nav", "ol", "p", "pre", "section", "summary", "table", "ul", "li", "dd", "dt",
    };

    /// <summary>Formatting elements: when a mismatched end tag closes them implicitly, browsers reopen them for the text that follows.</summary>
    private static readonly HashSet<string> Formatting = new(StringComparer.Ordinal) { "a", "b", "big", "code", "em", "font", "i", "nobr", "s", "small", "strike", "strong", "tt", "u" };

    /// <summary>Elements that stop that reopening (formatting never leaks out of a cell or table).</summary>
    private static readonly HashSet<string> Markers = new(StringComparer.Ordinal) { "td", "th", "tr", "table", "caption", "body", "template", "object", "#document" };

    /// <summary>Elements before which formatting isn't reopened (it is reopened inside them, when text comes).</summary>
    private static readonly HashSet<string> NoReconstruct = new(StringComparer.Ordinal)
    {
        "tbody", "thead", "tfoot", "tr", "td", "th", "caption", "col", "colgroup", "li", "dd", "dt", "head", "body", "title", "style", "script", "meta", "link", "base",
    };

    /// <summary>The deepest element nesting kept (browsers cap it too).</summary>
    private const int MaxDepth = 400;

    /// <summary>Elements end tags don't look past (a stray &lt;/div&gt; in a cell doesn't close the table).</summary>
    private static readonly HashSet<string> Scope = new(StringComparer.Ordinal) { "td", "th", "table", "caption", "template", "#document", "body" };

    public static HtmlNode Parse(string html)
    {
        var root = new HtmlNode("#document");
        var stack = new List<HtmlNode> { root };
        var reopen = new List<HtmlNode>();
        HtmlNode Current() => stack[stack.Count - 1];

        void Reconstruct()
        {
            if (reopen.Count == 0) return;
            foreach (var f in reopen)
            {
                var clone = new HtmlNode(f.Name);
                clone.Attributes.AddRange(f.Attributes);
                Current().Add(clone);
                stack.Add(clone);
            }
            reopen.Clear();
        }

        bool InStack(string name, out int index, bool stopAtScope = true)
        {
            for (var k = stack.Count - 1; k > 0; k--)
            {
                if (stack[k].Name == name)
                {
                    index = k;
                    return true;
                }
                if (stopAtScope && Scope.Contains(stack[k].Name)) break;
            }
            index = -1;
            return false;
        }

        void PopTo(int index, bool remember = true)
        {
            if (index <= 0) return;
            if (remember)
            {
                if (stack.Skip(index).Any(e => Markers.Contains(e.Name)))
                {
                    reopen.Clear();
                }
                else
                {
                    for (var k = index + 1; k < stack.Count; k++)
                        if (Formatting.Contains(stack[k].Name)) reopen.Add(stack[k]);
                }
            }
            stack.RemoveRange(index, stack.Count - index);
        }

        void AddText(string text)
        {
            if (text.Length == 0) return;
            if (reopen.Count > 0 && text.Any(c => !char.IsWhiteSpace(c))) Reconstruct();
            var parent = Current();
            if (parent.Children.Count > 0 && parent.Children[parent.Children.Count - 1] is { Name: "#text" } last) last.Text += text;
            else parent.Add(new HtmlNode("#text", text));
        }

        var i = 0;
        var n = html.Length;
        var textStart = 0;

        void FlushText(int end)
        {
            if (end > textStart) AddText(HtmlEntities.Decode(html.Substring(textStart, end - textStart), false));
        }

        while (i < n)
        {
            if (html[i] != '<')
            {
                i++;
                continue;
            }
            // A tag, comment or declaration starts here.
            if (i + 1 >= n)
            {
                i++;
                break;
            }
            var next = html[i + 1];
            if (next == '!')
            {
                FlushText(i);
                if (string.CompareOrdinal(html, i, "<!--", 0, 4) == 0)
                {
                    var end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    var text = end < 0 ? html.Substring(i + 4) : html.Substring(i + 4, end - i - 4);
                    Current().Add(new HtmlNode("#comment", text));
                    i = end < 0 ? n : end + 3;
                }
                else if (string.Compare(html, i, "<![CDATA[", 0, 9, StringComparison.Ordinal) == 0)
                {
                    var end = html.IndexOf("]]>", i + 9, StringComparison.Ordinal);
                    AddText(end < 0 ? html.Substring(i + 9) : html.Substring(i + 9, end - i - 9));
                    i = end < 0 ? n : end + 3;
                }
                else if (string.Compare(html, i, "<![if", 0, 5, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    var end = html.IndexOf(']', i + 5);
                    var close = end < 0 ? -1 : html.IndexOf('>', end);
                    var expr = end < 0 ? "" : html.Substring(i + 5, end - i - 5).Trim();
                    var node = new HtmlNode("!if", expr);
                    Current().Add(node);
                    if (stack.Count < MaxDepth) stack.Add(node);
                    i = close < 0 ? n : close + 1;
                }
                else if (string.Compare(html, i, "<![endif", 0, 8, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    var close = html.IndexOf('>', i);
                    if (InStack("!if", out var at, stopAtScope: false)) PopTo(at, remember: false);
                    i = close < 0 ? n : close + 1;
                }
                else
                {
                    var close = html.IndexOf('>', i);
                    i = close < 0 ? n : close + 1;
                }
                textStart = i;
                continue;
            }
            if (next == '?')
            {
                FlushText(i);
                var close = html.IndexOf('>', i);
                i = close < 0 ? n : close + 1;
                textStart = i;
                continue;
            }
            if (next == '/')
            {
                if (i + 2 < n && IsNameStart(html[i + 2]))
                {
                    FlushText(i);
                    var j = i + 2;
                    while (j < n && IsNameChar(html[j])) j++;
                    var name = html.Substring(i + 2, j - i - 2).ToLowerInvariant();
                    var close = html.IndexOf('>', j);
                    i = close < 0 ? n : close + 1;
                    textStart = i;
                    EndTag(name);
                    continue;
                }
                if (i + 2 < n && html[i + 2] == '>')
                {
                    FlushText(i);
                    i += 3;
                    textStart = i;
                    continue;
                }
                // "</" followed by something else: a bogus comment.
                FlushText(i);
                var bogus = html.IndexOf('>', i);
                i = bogus < 0 ? n : bogus + 1;
                textStart = i;
                continue;
            }
            if (!IsNameStart(next))
            {
                i++;
                continue;
            }
            FlushText(i);
            var tag = ReadStartTag(html, ref i, out var selfClosing);
            textStart = i;
            StartTag(tag, selfClosing);
            if (RawText.Contains(tag.Name) && !selfClosing)
            {
                var endTag = "</" + tag.Name;
                var end = html.IndexOf(endTag, i, StringComparison.OrdinalIgnoreCase);
                var raw = end < 0 ? html.Substring(i) : html.Substring(i, end - i);
                if (raw.Length > 0) Current().Add(new HtmlNode("#text", tag.Name is "textarea" or "title" ? HtmlEntities.Decode(raw, false) : raw));
                if (end < 0)
                {
                    i = n;
                }
                else
                {
                    var close = html.IndexOf('>', end);
                    i = close < 0 ? n : close + 1;
                }
                if (InStack(tag.Name, out var at, stopAtScope: false)) PopTo(at, remember: false);
                textStart = i;
            }
        }
        FlushText(n);
        return root;

        void StartTag(HtmlNode element, bool selfClosing)
        {
            var name = element.Name;
            switch (name)
            {
                case "html":
                    return;
                case "body":
                {
                    if (InStack("body", out _, stopAtScope: false)) return;
                    if (InStack("head", out var h, stopAtScope: false)) PopTo(h, remember: false);
                    break;
                }
                case "head":
                    if (InStack("head", out _, stopAtScope: false)) return;
                    break;
            }
            if (ClosesP.Contains(name) && InStack("p", out var p)) PopTo(p);
            if (name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6" && Current().Name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6") stack.RemoveAt(stack.Count - 1);
            if (name == "li")
            {
                for (var k = stack.Count - 1; k > 0; k--)
                {
                    var s = stack[k].Name;
                    if (s == "li")
                    {
                        PopTo(k);
                        break;
                    }
                    if (s is "ul" or "ol" or "menu" or "td" or "th" or "table" or "body") break;
                }
            }
            if (name is "dd" or "dt")
            {
                for (var k = stack.Count - 1; k > 0; k--)
                {
                    var s = stack[k].Name;
                    if (s is "dd" or "dt")
                    {
                        PopTo(k);
                        break;
                    }
                    if (s is "dl" or "td" or "th" or "table" or "body") break;
                }
            }
            if (name is "td" or "th")
            {
                for (var k = stack.Count - 1; k > 0; k--)
                {
                    var s = stack[k].Name;
                    if (s is "td" or "th")
                    {
                        PopTo(k);
                        break;
                    }
                    if (s is "tr" or "table") break;
                }
            }
            if (name == "tr")
            {
                for (var k = stack.Count - 1; k > 0; k--)
                {
                    var s = stack[k].Name;
                    if (s == "tr")
                    {
                        PopTo(k);
                        break;
                    }
                    if (s is "table" or "thead" or "tbody" or "tfoot") break;
                }
            }
            if (name is "thead" or "tbody" or "tfoot")
            {
                for (var k = stack.Count - 1; k > 0; k--)
                {
                    var s = stack[k].Name;
                    if (s is "thead" or "tbody" or "tfoot")
                    {
                        PopTo(k);
                        break;
                    }
                    if (s == "table") break;
                }
            }
            if (name == "a" && InStack("a", out var a)) PopTo(a);
            if (name == "option" && Current().Name == "option") stack.RemoveAt(stack.Count - 1);
            if (reopen.Count > 0 && !ClosesP.Contains(name) && !NoReconstruct.Contains(name))
            {
                // A formatting element opened again by its own tag replaces the copy waiting to be reopened.
                if (Formatting.Contains(name)) reopen.RemoveAll(f => f.Name == name);
                Reconstruct();
            }
            Current().Add(element);
            var isVoid = Void.Contains(name) || (selfClosing && name.Contains(':'));
            // Absurdly deep nesting (hostile or broken pages) stays flat beyond MaxDepth, so walking the tree can't overflow the stack.
            if (!isVoid && stack.Count < MaxDepth) stack.Add(element);
        }

        void EndTag(string name)
        {
            switch (name)
            {
                case "html":
                case "body":
                case "head":
                    if (name == "head" && InStack("head", out var h, stopAtScope: false)) PopTo(h, remember: false);
                    return;
                case "br":
                    Current().Add(new HtmlNode("br"));
                    return;
                case "p":
                    if (InStack("p", out var p)) PopTo(p);
                    return;
            }
            var stopAtScope = !Scope.Contains(name) && name is not ("tr" or "tbody" or "thead" or "tfoot");
            if (name is "tr" or "tbody" or "thead" or "tfoot")
            {
                for (var k = stack.Count - 1; k > 0; k--)
                {
                    if (stack[k].Name == name)
                    {
                        PopTo(k);
                        return;
                    }
                    if (stack[k].Name == "table") return;
                }
                return;
            }
            if (InStack(name, out var at, stopAtScope))
            {
                PopTo(at);
                return;
            }
            // Closing a formatting element that is waiting to be reopened: it stays closed.
            var pending = reopen.FindLastIndex(f => f.Name == name);
            if (pending >= 0) reopen.RemoveAt(pending);
        }
    }

    private static bool IsNameStart(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    private static bool IsNameChar(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or ':' or '-' or '_' or '.';

    private static HtmlNode ReadStartTag(string html, ref int i, out bool selfClosing)
    {
        var n = html.Length;
        var j = i + 1;
        while (j < n && IsNameChar(html[j])) j++;
        var element = new HtmlNode(html.Substring(i + 1, j - i - 1).ToLowerInvariant());
        selfClosing = false;
        while (j < n)
        {
            while (j < n && char.IsWhiteSpace(html[j])) j++;
            if (j >= n) break;
            var c = html[j];
            if (c == '>')
            {
                j++;
                break;
            }
            if (c == '/')
            {
                if (j + 1 < n && html[j + 1] == '>')
                {
                    selfClosing = true;
                    j += 2;
                    break;
                }
                j++;
                continue;
            }
            var nameStart = j;
            while (j < n && !char.IsWhiteSpace(html[j]) && html[j] != '=' && html[j] != '>' && !(html[j] == '/' && j + 1 < n && html[j + 1] == '>')) j++;
            var attrName = html.Substring(nameStart, j - nameStart).ToLowerInvariant();
            while (j < n && char.IsWhiteSpace(html[j])) j++;
            var value = "";
            if (j < n && html[j] == '=')
            {
                j++;
                while (j < n && char.IsWhiteSpace(html[j])) j++;
                if (j < n && (html[j] == '"' || html[j] == '\''))
                {
                    var quote = html[j];
                    var end = html.IndexOf(quote, j + 1);
                    if (end < 0) end = n;
                    value = html.Substring(j + 1, end - j - 1);
                    j = Math.Min(n, end + 1);
                }
                else
                {
                    var start = j;
                    while (j < n && !char.IsWhiteSpace(html[j]) && html[j] != '>') j++;
                    value = html.Substring(start, j - start);
                }
                value = HtmlEntities.Decode(value, true);
            }
            if (attrName.Length > 0 && element.Attr(attrName) == null) element.Attributes.Add(new KeyValuePair<string, string>(attrName, value));
        }
        i = j;
        return element;
    }
}

/// <summary>HTML character references.</summary>
internal static class HtmlEntities
{
    private static readonly Dictionary<string, string?> Cache = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    /// <summary>Entities browsers also accept without the semicolon (the common ones).</summary>
    private static readonly string[] Legacy = { "nbsp", "amp", "lt", "gt", "quot", "copy", "reg", "AMP", "LT", "GT", "QUOT", "COPY", "REG", "shy", "deg", "middot", "laquo", "raquo" };

    private static readonly char[] Windows1252 =
    {
        '€', '\u0081', '‚', 'ƒ', '„', '…', '†', '‡', 'ˆ', '‰', 'Š', '‹', 'Œ', '\u008D', 'Ž', '\u008F',
        '\u0090', '‘', '’', '“', '”', '•', '–', '—', '˜', '™', 'š', '›', 'œ', '\u009D', 'ž', 'Ÿ',
    };

    public static string Decode(string s, bool attribute)
    {
        if (s.IndexOf('&') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c != '&')
            {
                sb.Append(c);
                continue;
            }
            if (i + 1 < s.Length && s[i + 1] == '#')
            {
                var j = i + 2;
                var hex = j < s.Length && (s[j] == 'x' || s[j] == 'X');
                if (hex) j++;
                var digitsStart = j;
                long value = 0;
                while (j < s.Length && (hex ? Uri.IsHexDigit(s[j]) : s[j] is >= '0' and <= '9'))
                {
                    value = value * (hex ? 16 : 10) + (hex ? Convert.ToInt32(s[j].ToString(), 16) : s[j] - '0');
                    if (value > 0x10FFFF) value = 0x110000;
                    j++;
                }
                if (j == digitsStart)
                {
                    sb.Append('&');
                    continue;
                }
                if (j < s.Length && s[j] == ';') j++;
                sb.Append(NumericChar(value));
                i = j - 1;
                continue;
            }
            var k = i + 1;
            while (k < s.Length && k - i <= 32 && char.IsLetterOrDigit(s[k])) k++;
            if (k > i + 1)
            {
                var name = s.Substring(i + 1, k - i - 1);
                if (k < s.Length && s[k] == ';' && Lookup(name) is { } decoded)
                {
                    sb.Append(decoded);
                    i = k;
                    continue;
                }
                var legacy = Legacy.Where(l => name.StartsWith(l, StringComparison.Ordinal)).OrderByDescending(l => l.Length).FirstOrDefault();
                if (legacy != null)
                {
                    var after = i + 1 + legacy.Length;
                    var blocked = attribute && after < s.Length && (s[after] == '=' || char.IsLetterOrDigit(s[after]));
                    if (!blocked && Lookup(legacy) is { } v)
                    {
                        sb.Append(v);
                        i = after - 1;
                        continue;
                    }
                }
            }
            sb.Append('&');
        }
        return sb.ToString();
    }

    private static string NumericChar(long value)
    {
        if (value == 0 || value > 0x10FFFF || value is >= 0xD800 and <= 0xDFFF) return "�";
        if (value is >= 0x80 and <= 0x9F) return Windows1252[value - 0x80].ToString();
        return char.ConvertFromUtf32((int)value);
    }

    private static string? Lookup(string name)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(name, out var cached)) return cached;
        }
        string? result = name switch
        {
            "apos" => "'",
            "nbsp" => "\u00A0",
            "amp" or "AMP" => "&",
            "lt" or "LT" => "<",
            "gt" or "GT" => ">",
            "quot" or "QUOT" => "\"",
            "COPY" => "©",
            "REG" => "®",
            "hellip" => "…",
            "mldr" => "…",
            "check" or "checkmark" => "✓",
            "rarr" => "→",
            "larr" => "←",
            _ => null,
        };
        if (result == null)
        {
            var probe = "&" + name + ";";
            var decoded = System.Net.WebUtility.HtmlDecode(probe);
            if (decoded != probe) result = decoded;
        }
        lock (Gate) Cache[name] = result;
        return result;
    }
}

/// <summary>A CSS rule with a simple selector (tag, .class, tag.class).</summary>
internal sealed class CssRule
{
    public CssRule(string? tag, string[] classes, int order, List<KeyValuePair<string, string>> declarations)
    {
        Tag = tag;
        Classes = classes;
        Order = order;
        Declarations = declarations;
    }

    public string? Tag { get; }
    public string[] Classes { get; }
    public int Order { get; }
    public int Specificity => Classes.Length * 10 + (Tag != null ? 1 : 0);
    public List<KeyValuePair<string, string>> Declarations { get; }
}

/// <summary>The style sheets of a page (only simple selectors, which is what Word and most clipboard HTML use), plus Word's @list rules.</summary>
internal sealed class CssSheet
{
    public List<CssRule> Rules { get; } = new();

    /// <summary>Word's list definitions: "l0:level1" → declarations (mso-level-number-format, mso-level-text…).</summary>
    public Dictionary<string, List<KeyValuePair<string, string>>> ListRules { get; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex Simple = new(@"^(?<tag>[a-zA-Z][a-zA-Z0-9]*|\*)?(?<classes>(\.[A-Za-z_][A-Za-z0-9_-]*)*)$", RegexOptions.CultureInvariant);

    public void Parse(string css)
    {
        css = Regex.Replace(css, @"/\*.*?\*/", " ", RegexOptions.Singleline | RegexOptions.CultureInvariant);
        css = css.Replace("<!--", " ").Replace("-->", " ");
        var i = 0;
        while (i < css.Length)
        {
            var open = css.IndexOf('{', i);
            if (open < 0) break;
            var selector = css.Substring(i, open - i).Trim();
            var close = MatchingBrace(css, open);
            var body = css.Substring(open + 1, Math.Max(0, close - open - 1));
            i = close + 1;
            if (selector.StartsWith("@", StringComparison.Ordinal))
            {
                if (selector.StartsWith("@list", StringComparison.OrdinalIgnoreCase))
                {
                    var key = selector.Substring(5).Trim();
                    if (key.Length > 0) ListRules[key] = CssParser.Declarations(body);
                }
                continue;
            }
            var decls = CssParser.Declarations(body);
            if (decls.Count == 0) continue;
            foreach (var part in selector.Split(','))
            {
                var s = part.Trim();
                var m = Simple.Match(s);
                if (!m.Success || s.Length == 0) continue;
                var tag = m.Groups["tag"].Success && m.Groups["tag"].Value.Length > 0 && m.Groups["tag"].Value != "*" ? m.Groups["tag"].Value.ToLowerInvariant() : null;
                var classes = m.Groups["classes"].Value.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
                Rules.Add(new CssRule(tag, classes, Rules.Count, decls));
            }
        }
    }

    private static int MatchingBrace(string s, int open)
    {
        var depth = 0;
        for (var k = open; k < s.Length; k++)
        {
            if (s[k] == '{') depth++;
            else if (s[k] == '}' && --depth == 0) return k;
        }
        return s.Length;
    }

    /// <summary>The declarations that apply to an element, in cascade order (least specific first).</summary>
    public IEnumerable<KeyValuePair<string, string>> Match(string tag, string? classAttribute)
    {
        if (Rules.Count == 0) yield break;
        var classes = (classAttribute ?? "").Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var rule in Rules.Where(r => (r.Tag == null || r.Tag == tag) && r.Classes.All(c => classes.Contains(c, StringComparer.OrdinalIgnoreCase)) && (r.Tag != null || r.Classes.Length > 0))
                     .OrderBy(r => r.Specificity).ThenBy(r => r.Order))
            foreach (var d in rule.Declarations)
                yield return d;
    }
}

/// <summary>CSS values: declarations, lengths, colours.</summary>
internal static class CssParser
{
    /// <summary>Parses "a: b; c: d" (quotes and parentheses respected, !important dropped, names lower-cased).</summary>
    public static List<KeyValuePair<string, string>> Declarations(string? style)
    {
        var result = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrWhiteSpace(style)) return result;
        var parts = new List<string>();
        var sb = new StringBuilder();
        char quote = '\0';
        var depth = 0;
        foreach (var c in style!)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                sb.Append(c);
                continue;
            }
            if (c is '"' or '\'') quote = c;
            else if (c == '(') depth++;
            else if (c == ')') depth = Math.Max(0, depth - 1);
            else if (c == ';' && depth == 0)
            {
                parts.Add(sb.ToString());
                sb.Clear();
                continue;
            }
            sb.Append(c);
        }
        parts.Add(sb.ToString());
        foreach (var part in parts)
        {
            var colon = part.IndexOf(':');
            if (colon <= 0) continue;
            var name = part.Substring(0, colon).Trim().ToLowerInvariant();
            var value = part.Substring(colon + 1).Trim();
            var bang = value.IndexOf("!important", StringComparison.OrdinalIgnoreCase);
            if (bang >= 0) value = value.Substring(0, bang).Trim();
            if (name.Length > 0) result.Add(new KeyValuePair<string, string>(name, value));
        }
        return result;
    }

    /// <summary>A length in points ("12pt", "16px", "1.2em", "50%", "0.5in", "1cm"); relative units use <paramref name="emPoints"/>.</summary>
    public static double? Points(string value, double emPoints = 12)
    {
        var v = value.Trim().ToLowerInvariant();
        var m = Regex.Match(v, @"^(-?[0-9]*\.?[0-9]+)\s*(pt|px|em|rem|%|in|cm|mm|pc|ex|ch)?$", RegexOptions.CultureInvariant);
        if (!m.Success) return null;
        var n = double.Parse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
        return m.Groups[2].Value switch
        {
            "pt" => n,
            "px" or "" => n * 0.75,
            "em" or "rem" => n * emPoints,
            "%" => n * emPoints / 100,
            "in" => n * 72,
            "cm" => n * 72 / 2.54,
            "mm" => n * 72 / 25.4,
            "pc" => n * 12,
            "ex" or "ch" => n * emPoints / 2,
            _ => null,
        };
    }

    /// <summary>A font size in points: lengths, keywords (small, large…), relative to <paramref name="parentPoints"/>.</summary>
    public static double? FontSize(string value, double parentPoints)
    {
        var v = value.Trim().ToLowerInvariant();
        switch (v)
        {
            case "xx-small": return 7;
            case "x-small": return 7.5;
            case "small": return 10;
            case "medium": return 12;
            case "large": return 13.5;
            case "x-large": return 18;
            case "xx-large": return 24;
            case "xxx-large": return 36;
            case "smaller": return parentPoints / 1.2;
            case "larger": return parentPoints * 1.2;
        }
        return Points(v, parentPoints);
    }

    private static readonly Dictionary<string, string> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = "000000", ["white"] = "FFFFFF", ["red"] = "FF0000", ["lime"] = "00FF00", ["green"] = "008000", ["blue"] = "0000FF", ["yellow"] = "FFFF00",
        ["cyan"] = "00FFFF", ["aqua"] = "00FFFF", ["magenta"] = "FF00FF", ["fuchsia"] = "FF00FF", ["silver"] = "C0C0C0", ["gray"] = "808080", ["grey"] = "808080",
        ["maroon"] = "800000", ["olive"] = "808000", ["purple"] = "800080", ["teal"] = "008080", ["navy"] = "000080", ["orange"] = "FFA500", ["pink"] = "FFC0CB",
        ["brown"] = "A52A2A", ["gold"] = "FFD700", ["indigo"] = "4B0082", ["violet"] = "EE82EE", ["darkred"] = "8B0000", ["darkgreen"] = "006400",
        ["darkblue"] = "00008B", ["darkgray"] = "A9A9A9", ["darkgrey"] = "A9A9A9", ["lightgray"] = "D3D3D3", ["lightgrey"] = "D3D3D3", ["crimson"] = "DC143C",
        ["tomato"] = "FF6347", ["coral"] = "FF7F50", ["salmon"] = "FA8072", ["orangered"] = "FF4500", ["dodgerblue"] = "1E90FF", ["royalblue"] = "4169E1",
        ["steelblue"] = "4682B4", ["slategray"] = "708090", ["darkorange"] = "FF8C00", ["lightyellow"] = "FFFFE0", ["lightblue"] = "ADD8E6", ["lightgreen"] = "90EE90",
        ["windowtext"] = "000000",
    };

    /// <summary>A colour as "RRGGBB"; null for transparent, inherit and unknown values.</summary>
    public static string? Color(string value)
    {
        var v = value.Trim();
        if (v.Length == 0) return null;
        if (v[0] == '#')
        {
            var hex = v.Substring(1);
            if (!hex.All(Uri.IsHexDigit)) return null;
            if (hex.Length is 3 or 4)
            {
                if (hex.Length == 4 && hex[3] == '0') return null;
                return string.Concat(hex.Take(3).Select(c => new string(c, 2))).ToUpperInvariant();
            }
            if (hex.Length == 8 && hex.Substring(6) == "00") return null;
            if (hex.Length is 6 or 8) return hex.Substring(0, 6).ToUpperInvariant();
            return null;
        }
        var m = Regex.Match(v, @"^rgba?\(\s*([0-9.]+%?)\s*[, ]\s*([0-9.]+%?)\s*[, ]\s*([0-9.]+%?)\s*(?:[,/]\s*([0-9.]+%?)\s*)?\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (m.Success)
        {
            if (m.Groups[4].Success)
            {
                var a = m.Groups[4].Value;
                var alpha = a.EndsWith("%", StringComparison.Ordinal) ? double.Parse(a.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture) / 100 : double.Parse(a, NumberStyles.Float, CultureInfo.InvariantCulture);
                if (alpha <= 0.01) return null;
            }
            int Channel(string s) => (int)Math.Round(MathEx.Clamp(s.EndsWith("%", StringComparison.Ordinal)
                ? double.Parse(s.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture) * 2.55
                : double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture), 0, 255));
            return Channel(m.Groups[1].Value).ToString("X2", CultureInfo.InvariantCulture) + Channel(m.Groups[2].Value).ToString("X2", CultureInfo.InvariantCulture) +
                   Channel(m.Groups[3].Value).ToString("X2", CultureInfo.InvariantCulture);
        }
        return Named.TryGetValue(v, out var named) ? named : null;
    }

    /// <summary>The colour in a shorthand value such as "background: url(x) rgb(1, 2, 3) no-repeat"; null if there is none.</summary>
    public static string? ColorIn(string value)
    {
        if (Color(value) is { } whole) return whole;
        foreach (Match m in Regex.Matches(value, @"rgba?\([^)]*\)|#[0-9a-fA-F]{3,8}\b|\b[A-Za-z]+\b", RegexOptions.CultureInvariant))
            if (Color(m.Value) is { } c) return c;
        return null;
    }

    /// <summary>The first real family of a font-family list (generic families and quotes removed).</summary>
    public static string? FontFamily(string value)
    {
        foreach (var part in value.Split(','))
        {
            var name = part.Trim().Trim('"', '\'').Trim();
            if (name.Length == 0) continue;
            switch (name.ToLowerInvariant())
            {
                case "serif":
                case "sans-serif":
                case "monospace":
                case "cursive":
                case "fantasy":
                case "system-ui":
                case "-apple-system":
                case "blinkmacsystemfont":
                case "inherit":
                case "initial":
                case "unset":
                case "ui-sans-serif":
                case "ui-serif":
                case "ui-monospace":
                    continue;
            }
            return name;
        }
        return null;
    }
}
