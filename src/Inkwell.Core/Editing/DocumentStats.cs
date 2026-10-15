using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Inkwell.Core.Model;

namespace Inkwell.Core.Editing;

/// <summary>Counts for a piece of text.</summary>
public readonly struct TextCounts
{
    public TextCounts(int words, int characters, int charactersWithSpaces, int eastAsian)
    {
        Words = words;
        Characters = characters;
        CharactersWithSpaces = charactersWithSpaces;
        EastAsianCharacters = eastAsian;
    }

    public int Words { get; }

    /// <summary>Characters without spaces (code points; zero-width format characters aren't counted).</summary>
    public int Characters { get; }

    /// <summary>Characters including spaces, tabs and line breaks (not paragraph marks).</summary>
    public int CharactersWithSpaces { get; }

    /// <summary>Chinese, Japanese and Korean characters (each one is also a word).</summary>
    public int EastAsianCharacters { get; }

    public static TextCounts operator +(TextCounts a, TextCounts b) =>
        new(a.Words + b.Words, a.Characters + b.Characters, a.CharactersWithSpaces + b.CharactersWithSpaces, a.EastAsianCharacters + b.EastAsianCharacters);

    public override string ToString() => $"{Words} words, {Characters} characters ({CharactersWithSpaces} with spaces)";
}

/// <summary>What to count.</summary>
public sealed class StatsOptions
{
    /// <summary>Count footnotes and endnotes (Word's "Include textboxes, footnotes and endnotes", on by default).</summary>
    public bool IncludeNotes { get; set; } = true;

    /// <summary>Count the text in text boxes.</summary>
    public bool IncludeTextBoxes { get; set; } = true;

    /// <summary>The number of laid-out lines of a paragraph (from the layout engine); null: lines aren't counted.</summary>
    public Func<Paragraph, int>? LineCounter { get; set; }

    /// <summary>The page count, from the layout (passed through to the result).</summary>
    public int? Pages { get; set; }
}

/// <summary>Document statistics, as in Word's Word Count dialog, plus reading and speaking time.</summary>
public sealed class DocumentStatistics
{
    public int Words { get; set; }
    public int Characters { get; set; }
    public int CharactersWithSpaces { get; set; }
    public int EastAsianCharacters { get; set; }

    /// <summary>Paragraphs with at least one word (as Word counts them: empty paragraphs don't count).</summary>
    public int Paragraphs { get; set; }

    public int? Lines { get; set; }
    public int? Pages { get; set; }

    /// <summary>At <see cref="DocumentStats.ReadingWordsPerMinute"/> words a minute.</summary>
    public TimeSpan ReadingTime => DocumentStats.ReadingTime(Words);

    /// <summary>At <see cref="DocumentStats.SpeakingWordsPerMinute"/> words a minute.</summary>
    public TimeSpan SpeakingTime => DocumentStats.SpeakingTime(Words);

    public override string ToString() =>
        $"{Words} words, {Characters} characters ({CharactersWithSpaces} with spaces), {Paragraphs} paragraphs";
}

/// <summary>A part of a paragraph, in paragraph positions [Start, End) (as for the selection).</summary>
public readonly struct StatsRange
{
    public StatsRange(Paragraph paragraph, int start, int end)
    {
        Paragraph = paragraph;
        Start = start;
        End = end;
    }

    public Paragraph Paragraph { get; }
    public int Start { get; }
    public int End { get; }
}

/// <summary>
/// Word, character and paragraph counts that match Microsoft Word's (checked against Word's own statistics):
/// <list type="bullet">
/// <item>A word is a run of characters between spaces. Spaces are the space, tab, line / page / column breaks, the
/// no-break space and the ideographic space; other Unicode spaces (em space, thin space…) are ordinary characters,
/// as in Word.</item>
/// <item>Em dashes, en dashes and bullets separate words and aren't words themselves ("well—known" is two words,
/// "a – b" is two); a hyphen standing alone is a word ("a - b" is three), as are "&amp;", "..." and quotes.</item>
/// <item>Chinese, Japanese and Korean characters (including kana, Hangul and full-width forms) count one word each;
/// Arabic, Urdu, Thai and other scripts are counted by spaces.</item>
/// <item>Format characters (zero-width space and joiners, direction marks) count for nothing and don't split words;
/// the soft hyphen counts as a character (as in Word) but doesn't split words. Characters are counted as code points.</item>
/// <item>Deleted tracked text isn't counted; field results, symbols and the text of read-only content are. Pictures,
/// note reference marks, headers, footers and comments aren't counted.</item>
/// <item>A paragraph counts only if it has a word.</item>
/// </list>
/// Whole paragraphs are cached by <see cref="Block.Version"/> (so the editor must call <see cref="Block.MarkChanged"/>
/// after changing a paragraph, as it does for layout). Safe to use from several threads.
/// </summary>
public sealed class DocumentStats
{
    public const int ReadingWordsPerMinute = 238;
    public const int SpeakingWordsPerMinute = 130;

    private sealed class Cached
    {
        public Cached(int version, TextCounts counts)
        {
            Version = version;
            Counts = counts;
        }

        public int Version { get; }
        public TextCounts Counts { get; }
    }

    private sealed class Holder
    {
        public volatile Cached? Value;
    }

    private ConditionalWeakTable<Paragraph, Holder> _cache = new();

    public static TimeSpan ReadingTime(int words) => TimeSpan.FromMinutes(words / (double)ReadingWordsPerMinute);

    public static TimeSpan SpeakingTime(int words) => TimeSpan.FromMinutes(words / (double)SpeakingWordsPerMinute);

    /// <summary>Forgets the cached paragraph counts.</summary>
    public void ClearCache() => _cache = new ConditionalWeakTable<Paragraph, Holder>();

    /// <summary>The statistics of the whole document: the body (tables and content controls included), and depending
    /// on the options footnotes, endnotes and text boxes.</summary>
    public DocumentStatistics Count(Document document, StatsOptions? options = null)
    {
        options ??= new StatsOptions();
        var acc = new Accumulator(this, options);
        acc.AddBlocks(document.Body.Blocks);
        if (options.IncludeNotes)
        {
            foreach (var note in document.Footnotes.Values.Where(n => !n.IsSpecial)) acc.AddBlocks(note.Blocks);
            foreach (var note in document.Endnotes.Values.Where(n => !n.IsSpecial)) acc.AddBlocks(note.Blocks);
        }
        AddListLabels(acc, document, options);
        return acc.Result(options);
    }

    /// <summary>
    /// Word counts list numbers ("1.", "a)") as words, and bullets too unless they are plain bullet dots (•, ·, or
    /// the Symbol font's private-use bullets); measured against Word: "◦" and "▪" count, "•" doesn't.
    /// </summary>
    private void AddListLabels(Accumulator acc, Document document, StatsOptions options)
    {
        var stories = new List<Story> { document.Body };
        if (options.IncludeNotes)
        {
            stories.AddRange(document.Footnotes.Values.Where(n => !n.IsSpecial));
            stories.AddRange(document.Endnotes.Values.Where(n => !n.IsSpecial));
        }
        Dictionary<Paragraph, string> labels;
        try
        {
            labels = Layout.Numberer.Labels(document, new Styles.StyleResolver(document), stories);
        }
        catch (Exception)
        {
            return;
        }
        foreach (var (p, label) in labels)
        {
            if (p.Props.MarkProps()?.MarkDeleted != null) continue;
            var text = label.Trim();
            if (text.Length == 0 || text.All(c => c is '•' or '·' or '‣' or '⁃' || (c >= '' && c <= ''))) continue;
            acc.AddLabel(CountText(text));
        }
    }

    /// <summary>The statistics of some paragraphs (a story, a section).</summary>
    public DocumentStatistics Count(IEnumerable<Block> blocks, StatsOptions? options = null)
    {
        options ??= new StatsOptions();
        var acc = new Accumulator(this, options);
        acc.AddBlocks(blocks);
        return acc.Result(options);
    }

    /// <summary>The statistics of a selection: parts of paragraphs. A word cut by the range still counts as one word.</summary>
    public DocumentStatistics Count(IEnumerable<StatsRange> ranges, StatsOptions? options = null)
    {
        options ??= new StatsOptions();
        var total = new TextCounts();
        var paragraphs = new HashSet<Paragraph>(ReferenceEqualityComparer<Paragraph>.Instance);
        var touched = new HashSet<Paragraph>(ReferenceEqualityComparer<Paragraph>.Instance);
        var lines = 0;
        foreach (var r in ranges)
        {
            var p = r.Paragraph;
            var start = Math.Max(0, Math.Min(r.Start, r.End));
            var end = Math.Max(r.Start, r.End);
            var counts = start == 0 && end >= p.Length ? CountParagraph(p) : CountText(TextOf(p, start, end));
            total += counts;
            if (counts.Words > 0) paragraphs.Add(p);
            if (touched.Add(p) && options.LineCounter != null) lines += options.LineCounter(p);
        }
        return new DocumentStatistics
        {
            Words = total.Words,
            Characters = total.Characters,
            CharactersWithSpaces = total.CharactersWithSpaces,
            EastAsianCharacters = total.EastAsianCharacters,
            Paragraphs = paragraphs.Count,
            Lines = options.LineCounter != null ? lines : null,
            Pages = options.Pages,
        };
    }

    /// <summary>The counts of one paragraph (cached by its version).</summary>
    public TextCounts CountParagraph(Paragraph paragraph)
    {
        var holder = _cache.GetValue(paragraph, _ => new Holder());
        var cached = holder.Value;
        if (cached != null && cached.Version == paragraph.Version) return cached.Counts;
        var counts = CountText(TextOf(paragraph, 0, int.MaxValue));
        holder.Value = new Cached(paragraph.Version, counts);
        return counts;
    }

    private sealed class Accumulator
    {
        private readonly DocumentStats _stats;
        private readonly StatsOptions _options;
        private TextCounts _total;
        private int _paragraphs;
        private int _lines;

        public Accumulator(DocumentStats stats, StatsOptions options)
        {
            _stats = stats;
            _options = options;
        }

        public void AddBlocks(IEnumerable<Block> blocks)
        {
            foreach (var b in blocks)
            {
                switch (b)
                {
                    case Paragraph p:
                        AddParagraph(p);
                        break;
                    case Table t:
                        foreach (var row in t.Rows)
                            foreach (var cell in row.Cells)
                                AddBlocks(cell.Blocks);
                        break;
                    case SdtBlock s:
                        AddBlocks(s.Blocks);
                        break;
                    case FieldBlock f:
                        AddBlocks(f.Blocks);
                        break;
                    case OpaqueBlock { Display: { } display }:
                        AddBlocks(display);
                        break;
                }
            }
        }

        private void AddParagraph(Paragraph p)
        {
            var counts = _stats.CountParagraph(p);
            _total += counts;
            if (counts.Words > 0) _paragraphs++;
            if (_options.LineCounter != null) _lines += _options.LineCounter(p);
            if (!_options.IncludeTextBoxes) return;
            foreach (var inline in p.Inlines)
                if (inline is DrawingObject { TextBoxContent: { } content } && inline.Deleted == null)
                    AddBlocks(content);
        }

        /// <summary>A list label: counts toward words and characters, not paragraphs.</summary>
        public void AddLabel(TextCounts counts) => _total += counts;

        public DocumentStatistics Result(StatsOptions options) => new()
        {
            Words = _total.Words,
            Characters = _total.Characters,
            CharactersWithSpaces = _total.CharactersWithSpaces,
            EastAsianCharacters = _total.EastAsianCharacters,
            Paragraphs = _paragraphs,
            Lines = options.LineCounter != null ? _lines : null,
            Pages = options.Pages,
        };
    }

    /// <summary>The countable text of positions [start, end) of a paragraph: text runs (not deleted ones), field
    /// results, symbols and the text of read-only pieces. Objects that aren't text become nothing.</summary>
    internal static string TextOf(Paragraph p, int start, int end)
    {
        var sb = new StringBuilder();
        var pos = 0;
        foreach (var inline in p.Inlines)
        {
            var len = inline.Length;
            var s = Math.Max(start, pos);
            var e = Math.Min(end, pos + len);
            if (len > 0 && s < e && inline.Deleted == null)
            {
                switch (inline)
                {
                    case TextRun t:
                        sb.Append(t.Text, s - pos, e - s);
                        break;
                    case FieldInline f:
                        foreach (var r in f.Result)
                            if (r is TextRun rt && r.Deleted == null) sb.Append(rt.Text);
                        break;
                    case SymbolChar sym:
                        sb.Append(sym.Char);
                        break;
                    case OpaqueInline { IsMarker: false } o:
                        sb.Append(o.Display);
                        break;
                }
            }
            pos += len;
            if (pos >= end) break;
        }
        return sb.ToString();
    }

    private enum Kind { Space, Ignored, Breaker, EastAsian, Silent, Other }

    /// <summary>Counts words and characters in plain text the way Word does (see the class remarks).</summary>
    public static TextCounts CountText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return new TextCounts();
        int words = 0, chars = 0, withSpaces = 0, eastAsian = 0;
        var inWord = false;
        for (var i = 0; i < text!.Length; i++)
        {
            int cp = text[i];
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                cp = char.ConvertToUtf32(text[i], text[i + 1]);
                i++;
            }
            switch (Classify(cp))
            {
                case Kind.Space:
                    withSpaces++;
                    inWord = false;
                    break;
                case Kind.Ignored:
                    break;
                case Kind.Silent:
                    chars++;
                    withSpaces++;
                    break;
                case Kind.Breaker:
                    chars++;
                    withSpaces++;
                    inWord = false;
                    break;
                case Kind.EastAsian:
                    chars++;
                    withSpaces++;
                    eastAsian++;
                    words++;
                    inWord = false;
                    break;
                default:
                    chars++;
                    withSpaces++;
                    if (!inWord)
                    {
                        words++;
                        inWord = true;
                    }
                    break;
            }
        }
        return new TextCounts(words, chars, withSpaces, eastAsian);
    }

    private static Kind Classify(int cp)
    {
        switch (cp)
        {
            case ' ':
            case '\t':
            case '\n':
            case '\r':
            case TextRun.LineBreak:
            case TextRun.PageBreak:
            case TextRun.ColumnBreak:
            case '\u00A0': // no-break space
            case '\u3000': // ideographic space
                return Kind.Space;
            case '–': // en dash
            case '—': // em dash
            case '•': // bullet
                return Kind.Breaker;
            case '\u00AD': // soft hyphen: Word counts it as a character, inside the word
                return Kind.Silent;
            case '\uFFFC': // object replacement character
                return Kind.Ignored;
        }
        if (cp < 0x80) return cp < 0x20 ? Kind.Ignored : Kind.Other;
        if (IsEastAsian(cp)) return Kind.EastAsian;
        if (cp <= 0xFFFF && CharUnicodeInfo.GetUnicodeCategory((char)cp) == UnicodeCategory.Format) return Kind.Ignored;
        if (cp <= 0xFFFF && char.IsControl((char)cp)) return Kind.Ignored;
        return Kind.Other;
    }

    /// <summary>Chinese, Japanese and Korean characters: ideographs, kana, Hangul, CJK punctuation and full-width forms.</summary>
    internal static bool IsEastAsian(int cp) =>
        (cp >= 0x1100 && cp <= 0x11FF) ||   // Hangul Jamo
        (cp >= 0x2E80 && cp <= 0x2FFF) ||   // CJK radicals, Kangxi, ideographic description
        (cp >= 0x3001 && cp <= 0x303F) ||   // CJK symbols and punctuation (not the ideographic space)
        (cp >= 0x3040 && cp <= 0x33FF) ||   // kana, Bopomofo, Hangul compatibility jamo, enclosed CJK, CJK compatibility
        (cp >= 0x3400 && cp <= 0x4DBF) ||   // CJK extension A
        (cp >= 0x4E00 && cp <= 0x9FFF) ||   // CJK unified ideographs
        (cp >= 0xA000 && cp <= 0xA4CF) ||   // Yi
        (cp >= 0xA960 && cp <= 0xA97F) ||   // Hangul Jamo extended A
        (cp >= 0xAC00 && cp <= 0xD7FF) ||   // Hangul syllables, Jamo extended B
        (cp >= 0xF900 && cp <= 0xFAFF) ||   // CJK compatibility ideographs
        (cp >= 0xFE30 && cp <= 0xFE4F) ||   // CJK compatibility forms
        (cp >= 0xFF01 && cp <= 0xFFEF) ||   // half-width and full-width forms
        (cp >= 0x20000 && cp <= 0x3FFFF);   // CJK extensions B and later
}
