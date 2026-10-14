using System.Globalization;
using System.Text.RegularExpressions;

namespace Inkwell.Core.Editing;

/// <summary>Where the caret is when a character is typed.</summary>
public sealed class AutoCorrectContext
{
    /// <summary>The caret is in code (a code style, a code block or inline code): nothing is changed.</summary>
    public bool InCode { get; set; }

    /// <summary>The caret is inside a field: nothing is changed.</summary>
    public bool InField { get; set; }

    /// <summary>The paragraph is already a list item: the list shortcuts ("- ", "1. ") don't fire.</summary>
    public bool IsListItem { get; set; }

    /// <summary>The language of the text at the caret (BCP 47: "en-US", "de-DE", "fr-FR"…). It chooses the quote
    /// characters and the casing rules; "i" → "I" only applies to English.</summary>
    public string Language { get; set; } = "en-US";
}

/// <summary>An autocorrect rule the user can turn off.</summary>
public sealed class AutoCorrectRule
{
    public AutoCorrectRule(string id, string name, string description, bool enabledByDefault = true)
    {
        Id = id;
        Name = name;
        Description = description;
        EnabledByDefault = enabledByDefault;
    }

    /// <summary>Stable id, stored in the settings.</summary>
    public string Id { get; }

    /// <summary>The name shown in settings and on the "undo / stop doing this" chip.</summary>
    public string Name { get; }

    public string Description { get; }

    public bool EnabledByDefault { get; }
}

/// <summary>The autocorrect rules and their ids.</summary>
public static class AutoCorrectRules
{
    public const string SmartQuotes = "smart-quotes";
    public const string CapitalizeSentences = "capitalize-sentences";
    public const string CapitalI = "capital-i";
    public const string Symbols = "symbols";
    public const string Ellipsis = "ellipsis";
    public const string Dashes = "dashes";
    public const string Arrows = "arrows";
    public const string Fractions = "fractions";
    public const string Ordinals = "ordinals";
    public const string TwoInitialCapitals = "two-initial-capitals";
    public const string MarkdownBlocks = "markdown-blocks";
    public const string MarkdownInline = "markdown-inline";

    public static IReadOnlyList<AutoCorrectRule> All { get; } = new[]
    {
        new AutoCorrectRule(SmartQuotes, "Smart quotes", "Straight quotes become curly quotes and apostrophes (“ ” ‘ ’)."),
        new AutoCorrectRule(CapitalizeSentences, "Capitalize sentences", "The first letter of a sentence becomes a capital (not after abbreviations such as “e.g.”)."),
        new AutoCorrectRule(CapitalI, "Capital I", "The English pronoun “i” becomes “I”."),
        new AutoCorrectRule(Symbols, "Symbols", "(c), (r) and (tm) become ©, ® and ™."),
        new AutoCorrectRule(Ellipsis, "Ellipsis", "Three periods become an ellipsis (…)."),
        new AutoCorrectRule(Dashes, "Dashes", "“word -- word” gets an en dash (–), “word--word” an em dash (—)."),
        new AutoCorrectRule(Arrows, "Arrows", "->, <-, => and <=> become →, ←, ⇒ and ⇔."),
        new AutoCorrectRule(Fractions, "Fractions", "1/2, 1/4 and 3/4 become ½, ¼ and ¾."),
        new AutoCorrectRule(Ordinals, "Superscript ordinals", "The suffix of 1st, 2nd, 3rd… is raised.", enabledByDefault: false),
        new AutoCorrectRule(TwoInitialCapitals, "Fix TWo INitial CApitals", "“THe” becomes “The” (not plurals of acronyms such as “CDs”)."),
        new AutoCorrectRule(MarkdownBlocks, "Markdown paragraphs", "#, -, 1., >, ``` and --- at the start of a paragraph make headings, lists, quotes, code and dividers."),
        new AutoCorrectRule(MarkdownInline, "Markdown formatting", "**bold**, _italic_, `code` and ~~strikethrough~~ are formatted when the closing mark is typed."),
    };

    public static AutoCorrectRule? Find(string id) => All.FirstOrDefault(r => r.Id == id);

    public static string NameOf(string id) => Find(id)?.Name ?? id;
}

/// <summary>Which rules are on (defaults from <see cref="AutoCorrectRule.EnabledByDefault"/>), plus the user's own abbreviations.</summary>
public sealed class AutoCorrectSettings
{
    private readonly Dictionary<string, bool> _overrides = new(StringComparer.Ordinal);

    public bool IsEnabled(string ruleId) =>
        _overrides.TryGetValue(ruleId, out var on) ? on : AutoCorrectRules.Find(ruleId)?.EnabledByDefault ?? false;

    public void SetEnabled(string ruleId, bool enabled)
    {
        if (AutoCorrectRules.Find(ruleId)?.EnabledByDefault == enabled) _overrides.Remove(ruleId);
        else _overrides[ruleId] = enabled;
    }

    /// <summary>The rules whose state differs from the default (for saving the settings).</summary>
    public IReadOnlyDictionary<string, bool> Overrides => _overrides;

    /// <summary>Extra abbreviations after which a sentence isn't capitalised ("approx.", "Ch."), compared ignoring case.</summary>
    public HashSet<string> Abbreviations { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public enum AutoCorrectEditKind
{
    /// <summary>Replace [Start, Start + Length) with <see cref="AutoCorrectEdit.Replacement"/>.</summary>
    Replace,

    /// <summary>Format [Start, Start + Length) as superscript (the text stays).</summary>
    Superscript,
}

/// <summary>One change autocorrect wants to make, in the coordinates of the text it was given.</summary>
public sealed class AutoCorrectEdit
{
    public AutoCorrectEdit(int start, int length, string replacement, string original, string ruleId, string description, AutoCorrectEditKind kind = AutoCorrectEditKind.Replace)
    {
        Start = start;
        Length = length;
        Replacement = replacement;
        Original = original;
        RuleId = ruleId;
        Description = description;
        Kind = kind;
    }

    public int Start { get; }
    public int Length { get; }
    public int End => Start + Length;

    /// <summary>The new text (for <see cref="AutoCorrectEditKind.Superscript"/>, the same as <see cref="Original"/>).</summary>
    public string Replacement { get; }

    /// <summary>The text that was there (to undo the change).</summary>
    public string Original { get; }

    public string RuleId { get; }

    public string RuleName => AutoCorrectRules.NameOf(RuleId);

    /// <summary>A short sentence for the "undo / stop doing this" chip.</summary>
    public string Description { get; }

    public AutoCorrectEditKind Kind { get; }

    public override string ToString() => $"[{Start},{End}) \"{Original}\" → \"{Replacement}\" ({RuleId})";
}

/// <summary>What autocorrect wants after a keystroke: text edits, a paragraph shortcut or an inline shortcut.</summary>
public sealed class AutoCorrectResult
{
    public static readonly AutoCorrectResult None = new(Array.Empty<AutoCorrectEdit>(), null, null);

    public AutoCorrectResult(IReadOnlyList<AutoCorrectEdit> edits, BlockShortcut? block, InlineShortcut? inline)
    {
        Edits = edits;
        Block = block;
        Inline = inline;
    }

    /// <summary>Text edits, sorted by position and never overlapping. Apply them from the last to the first.</summary>
    public IReadOnlyList<AutoCorrectEdit> Edits { get; }

    /// <summary>A Markdown paragraph shortcut ("# " → heading…), or null.</summary>
    public BlockShortcut? Block { get; }

    /// <summary>A Markdown inline shortcut ("**bold**" → bold…), or null.</summary>
    public InlineShortcut? Inline { get; }

    public bool IsEmpty => Edits.Count == 0 && Block == null && Inline == null;

    /// <summary>Applies the text edits (not the shortcuts) to <paramref name="text"/> and moves <paramref name="caret"/> with them.</summary>
    public string Apply(string text, ref int caret)
    {
        foreach (var e in Edits.OrderByDescending(e => e.Start))
        {
            if (e.Kind != AutoCorrectEditKind.Replace) continue;
            text = text.Substring(0, e.Start) + e.Replacement + text.Substring(e.End);
            if (e.End <= caret) caret += e.Replacement.Length - e.Length;
            else if (e.Start < caret) caret = e.Start + e.Replacement.Length;
        }
        return text;
    }

    public string Apply(string text)
    {
        var caret = text.Length;
        return Apply(text, ref caret);
    }
}

/// <summary>
/// The autocorrect engine: pure text logic the editor calls after every typed character. It never changes
/// anything itself; it returns the edits, and the editor applies them as one undo step after the keystroke (so
/// one Ctrl+Z restores what was typed) and shows the rule's name with "undo" and "stop doing this".
/// <para>
/// <see cref="Process"/> takes the paragraph's text after the character was inserted and the caret just after it.
/// For Enter, pass '\r' (or '\n'): the text doesn't contain it and the caret is where Enter was pressed.
/// </para>
/// <para>
/// Most word rules (capitals, "i", fractions, ordinals, TWo INitial CApitals, "word -- word") run when a word is
/// finished, i.e. when whitespace or Enter is typed. That is how "iPhone" and "eBay" are left alone (the whole word
/// is known) and why URLs, e-mail addresses and file names are never touched (they contain . @ / or \).
/// </para>
/// </summary>
public sealed class AutoCorrect
{
    public AutoCorrect(AutoCorrectSettings? settings = null)
    {
        Settings = settings ?? new AutoCorrectSettings();
    }

    public AutoCorrectSettings Settings { get; }

    /// <summary>Abbreviations after which "." doesn't end a sentence (compared ignoring case).</summary>
    public static readonly IReadOnlyCollection<string> DefaultAbbreviations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "i.e.", "e.g.", "etc.", "vs.", "cf.", "al.", "approx.", "mr.", "mrs.", "ms.", "dr.", "prof.", "st.", "jr.", "sr.", "no.", "nos.",
        "fig.", "figs.", "eq.", "eqs.", "vol.", "vols.", "pp.", "p.", "a.m.", "p.m.", "inc.", "ltd.", "co.", "corp.", "u.s.", "u.k.",
        "ch.", "chap.", "sec.", "ed.", "eds.", "esp.", "dept.", "est.", "ca.", "viz.", "ibid.", "op.", "cit.", "rev.", "gen.", "col.",
        "capt.", "lt.", "sgt.", "mt.", "ft.", "min.", "max.", "avg.", "ref.", "refs.", "tel.",
    };

    private static readonly HashSet<string> TwoCapsExceptions = new(StringComparer.Ordinal)
    {
        "GHz", "MHz", "KHz", "THz", "MSc", "BSc", "MBps", "GBps", "KBps", "TBps", "OSes", "MBit", "GBit", "KBit",
    };

    /// <summary>Elisions that start with an apostrophe: ’em, ’tis, ’twas, ’til, ’cause, ’n’, ’round.</summary>
    private static readonly HashSet<string> Elisions = new(StringComparer.OrdinalIgnoreCase)
    {
        "em", "tis", "twas", "til", "cause", "n", "n’", "round", "bout", "nuff",
    };

    public AutoCorrectResult Process(string text, int caret, char typed, AutoCorrectContext? context = null)
    {
        context ??= new AutoCorrectContext();
        if (text == null || caret < 0 || caret > text.Length) return AutoCorrectResult.None;
        if (context.InCode || context.InField) return AutoCorrectResult.None;
        var enter = typed is '\r' or '\n';
        if (!enter && (caret == 0 || text[caret - 1] != typed)) return AutoCorrectResult.None;
        // The end of what was there before the typed character.
        var end = enter ? caret : caret - 1;

        // Markdown shortcuts take the keystroke on their own.
        if (On(AutoCorrectRules.MarkdownInline) && !enter && MarkdownShortcuts.DetectInline(text, caret, typed) is { } inline)
            return new AutoCorrectResult(Array.Empty<AutoCorrectEdit>(), null, inline);
        if (On(AutoCorrectRules.MarkdownBlocks) && MarkdownShortcuts.DetectBlock(text, caret, typed, context) is { } block)
            return new AutoCorrectResult(Array.Empty<AutoCorrectEdit>(), block, null);

        var edits = new List<AutoCorrectEdit>();
        var culture = CultureFor(context.Language);
        var english = IsEnglish(context.Language);

        if ((typed == '"' || typed == '\'') && On(AutoCorrectRules.SmartQuotes)) SmartQuote(text, caret, typed, context.Language, edits);
        if (typed == ')' && On(AutoCorrectRules.Symbols)) Symbol(text, caret, edits);
        if (typed == '.' && On(AutoCorrectRules.Ellipsis)) EllipsisRule(text, caret, edits);
        if (typed == '>' && On(AutoCorrectRules.Arrows)) Arrow(text, caret, edits);
        if (char.IsLetterOrDigit(typed) && On(AutoCorrectRules.Dashes)) EmDash(text, caret, edits);

        if (enter || IsSpace(typed))
        {
            if (On(AutoCorrectRules.Dashes)) EnDash(text, end, edits);
            if (On(AutoCorrectRules.Arrows)) LeftArrow(text, end, edits);
            WordFinished(text, end, context, culture, english, edits);
        }

        if (edits.Count == 0) return AutoCorrectResult.None;
        // Sorted, without overlaps (the first rule to claim a range wins).
        var result = new List<AutoCorrectEdit>();
        foreach (var e in edits)
            if (!result.Any(r => e.Start < r.End && r.Start < e.End || (e.Length == 0 && r.Start == e.Start))) result.Add(e);
        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return new AutoCorrectResult(result, null, null);
    }

    private bool On(string ruleId) => Settings.IsEnabled(ruleId);

    internal static bool IsSpace(char c) => c is ' ' or '\t' or '\u00A0' or '\u3000' or '\v' or '\u2009' or '\u202F';

    private static bool IsEnglish(string? language) =>
        string.IsNullOrEmpty(language) || language!.StartsWith("en", StringComparison.OrdinalIgnoreCase);

    private static CultureInfo CultureFor(string? language)
    {
        try
        {
            return string.IsNullOrEmpty(language) ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(language);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    // ---- smart quotes ----

    private static (string Open, string Close, string SingleOpen, string SingleClose) QuotesFor(string? language)
    {
        var lang = (language ?? "en").ToLowerInvariant();
        if (lang.StartsWith("de", StringComparison.Ordinal) || lang.StartsWith("cs", StringComparison.Ordinal) ||
            lang.StartsWith("sk", StringComparison.Ordinal) || lang.StartsWith("sl", StringComparison.Ordinal))
            return ("„", "“", "‚", "‘");
        if (lang.StartsWith("fr", StringComparison.Ordinal)) return ("«\u00A0", "\u00A0»", "‘", "’");
        return ("“", "”", "‘", "’");
    }

    private static bool OpensQuote(char prev) =>
        char.IsWhiteSpace(prev) || prev is '(' or '[' or '{' or '<' or '“' or '‘' or '„' or '‚' or '«' or '‹' or '—' or '–' or '-' or '/' or '\u00A0';

    private static void SmartQuote(string text, int caret, char typed, string language, List<AutoCorrectEdit> edits)
    {
        var at = caret - 1;
        var prev = at > 0 ? text[at - 1] : '\n';
        var (open, close, singleOpen, singleClose) = QuotesFor(language);
        var opening = at == 0 || OpensQuote(prev);
        if (typed == '\'' && char.IsLetterOrDigit(prev)) opening = false; // don’t, Lovelace’s
        // French puts a space before the closing guillemet: after a space, a quote closes an open «.
        if (typed == '"' && opening && open.StartsWith("«", StringComparison.Ordinal) && char.IsWhiteSpace(prev))
        {
            var before = text.Substring(0, at);
            if (before.Count(c => c == '«') > before.Count(c => c == '»')) opening = false;
        }
        string replacement;
        var start = at;
        if (typed == '"')
        {
            replacement = opening ? open : close;
            // French: the closing guillemet takes a no-break space; swallow a space typed before it.
            if (!opening && close.StartsWith("\u00A0", StringComparison.Ordinal) && at > 0 && text[at - 1] == ' ') start = at - 1;
        }
        else
        {
            replacement = opening ? singleOpen : singleClose;
        }
        var original = text.Substring(start, at + 1 - start);
        edits.Add(new AutoCorrectEdit(start, at + 1 - start, replacement, original, AutoCorrectRules.SmartQuotes,
            typed == '\'' && !opening ? "Changed the straight apostrophe to a curly one" : "Changed the straight quote to a curly quote"));
    }

    // ---- symbols, ellipsis, arrows, dashes ----

    private static void Symbol(string text, int caret, List<AutoCorrectEdit> edits)
    {
        foreach (var (code, symbol) in new[] { ("(tm)", "™"), ("(c)", "©"), ("(r)", "®") })
        {
            var start = caret - code.Length;
            if (start < 0 || !string.Equals(text.Substring(start, code.Length), code, StringComparison.OrdinalIgnoreCase)) continue;
            if (code == "(tm)" && text.Substring(start, code.Length) is not ("(tm)" or "(TM)")) continue;
            // "Inkwell(tm)" is fine; (c) and (r) aren't a function call "f(c)", nor a list label "(c)" at the start of
            // a paragraph or after "(b)".
            if (code != "(tm)")
            {
                if (start > 0 && char.IsLetterOrDigit(text[start - 1])) continue;
                if (text.Substring(0, start).Trim().Length == 0) continue;
                var before = code == "(c)" ? "(b)" : "(q)";
                if (text.Substring(0, start).IndexOf(before, StringComparison.OrdinalIgnoreCase) >= 0) continue;
            }
            edits.Add(new AutoCorrectEdit(start, code.Length, symbol, text.Substring(start, code.Length), AutoCorrectRules.Symbols, "Changed " + code + " to " + symbol));
            return;
        }
    }

    private static void EllipsisRule(string text, int caret, List<AutoCorrectEdit> edits)
    {
        var start = caret - 3;
        if (start < 0 || text.Substring(start, 3) != "...") return;
        if (start > 0 && text[start - 1] == '.') return;
        edits.Add(new AutoCorrectEdit(start, 3, "…", "...", AutoCorrectRules.Ellipsis, "Changed three periods to an ellipsis"));
    }

    private static void Arrow(string text, int caret, List<AutoCorrectEdit> edits)
    {
        foreach (var (code, arrow) in new[] { ("<->", "↔"), ("<=>", "⇔"), ("->", "→"), ("=>", "⇒") })
        {
            var start = caret - code.Length;
            if (start < 0 || text.Substring(start, code.Length) != code) continue;
            // "-->", "==>" and "<<->" are left alone.
            if (start > 0 && (text[start - 1] == code[0] || text[start - 1] == '<' || text[start - 1] == '-' || text[start - 1] == '=')) return;
            edits.Add(new AutoCorrectEdit(start, code.Length, arrow, code, AutoCorrectRules.Arrows, "Changed " + code + " to " + arrow));
            return;
        }
    }

    /// <summary>"&lt;-" followed by whitespace, standing alone, becomes ←.</summary>
    private static void LeftArrow(string text, int end, List<AutoCorrectEdit> edits)
    {
        var start = end - 2;
        if (start < 0 || text.Substring(start, 2) != "<-") return;
        if (start > 0 && !char.IsWhiteSpace(text[start - 1])) return;
        edits.Add(new AutoCorrectEdit(start, 2, "←", "<-", AutoCorrectRules.Arrows, "Changed <- to ←"));
    }

    /// <summary>"word--w": the two hyphens between letters become an em dash as soon as the next word starts.</summary>
    private static void EmDash(string text, int caret, List<AutoCorrectEdit> edits)
    {
        var start = caret - 3;
        if (start < 1 || text[start] != '-' || text[start + 1] != '-') return;
        if (!char.IsLetterOrDigit(text[start - 1])) return;
        if (start >= 2 && text[start - 2] == '-') return;
        edits.Add(new AutoCorrectEdit(start, 2, "—", "--", AutoCorrectRules.Dashes, "Changed -- to an em dash"));
    }

    /// <summary>"word -- " (whitespace typed after the hyphens): an en dash.</summary>
    private static void EnDash(string text, int end, List<AutoCorrectEdit> edits)
    {
        var start = end - 2;
        if (start < 2 || text[start] != '-' || text[start + 1] != '-') return;
        if (!IsSpace(text[start - 1]) || char.IsWhiteSpace(text[start - 2]) || text[start - 2] == '-') return;
        edits.Add(new AutoCorrectEdit(start, 2, "–", "--", AutoCorrectRules.Dashes, "Changed -- to an en dash"));
    }

    // ---- rules that run when a word is finished ----

    private const string OpenPunct = "([{“‘\"'«‹¿¡„‚";
    private const string ClosePunct = ".,;:!?)]}”’\"'»›…";

    private void WordFinished(string text, int end, AutoCorrectContext context, CultureInfo culture, bool english, List<AutoCorrectEdit> edits)
    {
        var tokenStart = end;
        while (tokenStart > 0 && !char.IsWhiteSpace(text[tokenStart - 1])) tokenStart--;
        if (tokenStart >= end) return;
        var token = text.Substring(tokenStart, end - tokenStart);

        var coreStart = tokenStart;
        while (coreStart < end && OpenPunct.IndexOf(text[coreStart]) >= 0) coreStart++;
        var coreEnd = end;
        while (coreEnd > coreStart && ClosePunct.IndexOf(text[coreEnd - 1]) >= 0) coreEnd--;
        var core = text.Substring(coreStart, coreEnd - coreStart);

        // ’90s, ’em, ’tis: an opening quote typed before them was really an apostrophe.
        if (english && On(AutoCorrectRules.SmartQuotes) && token.Length > 1 && text[tokenStart] == '‘')
        {
            var rest = text.Substring(tokenStart + 1, coreEnd - tokenStart - 1);
            var restWithClose = text.Substring(tokenStart + 1, end - tokenStart - 1);
            if (Regex.IsMatch(rest, @"^\d\d(s|’s)?$") || Elisions.Contains(rest) || Elisions.Contains(restWithClose.TrimEnd('.', ',', ';', ':', '!', '?')))
                edits.Add(new AutoCorrectEdit(tokenStart, 1, "’", "‘", AutoCorrectRules.SmartQuotes, "Changed the opening quote to an apostrophe"));
        }

        if (core.Length == 0) return;
        if (LooksLikeAddress(token)) return;

        if (On(AutoCorrectRules.Fractions) && coreStart == tokenStart + LeadingOpen(token))
        {
            var fraction = core switch { "1/2" => "½", "1/4" => "¼", "3/4" => "¾", _ => null };
            if (fraction != null)
                edits.Add(new AutoCorrectEdit(coreStart, core.Length, fraction, core, AutoCorrectRules.Fractions, "Changed " + core + " to " + fraction));
        }

        if (On(AutoCorrectRules.Ordinals))
        {
            var m = Regex.Match(core, @"^(\d+)(st|nd|rd|th)$");
            if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) &&
                Inkwell.Core.Model.NumberFormats.Ordinal(n) == core)
            {
                var suffix = m.Groups[2];
                edits.Add(new AutoCorrectEdit(coreStart + suffix.Index, suffix.Length, suffix.Value, suffix.Value, AutoCorrectRules.Ordinals,
                    "Raised the ordinal suffix", AutoCorrectEditKind.Superscript));
            }
        }

        if (english && On(AutoCorrectRules.CapitalI) && IsPronounI(text, tokenStart, coreStart, coreEnd, end))
            edits.Add(new AutoCorrectEdit(coreStart, 1, "I", "i", AutoCorrectRules.CapitalI, "Changed “i” to “I”"));

        if (On(AutoCorrectRules.TwoInitialCapitals) && IsTwoInitialCapitals(core))
        {
            var lower = char.ToLower(core[1], culture).ToString();
            edits.Add(new AutoCorrectEdit(coreStart + 1, 1, lower, core[1].ToString(), AutoCorrectRules.TwoInitialCapitals,
                "Changed “" + core + "” to “" + core[0] + lower + core.Substring(2) + "”"));
        }

        // A single letter followed by "." or ")" is a list label or an initial ("a. first", "i) one").
        var label = core.Length == 1 && coreEnd < end && text[coreEnd] is '.' or ')';
        if (On(AutoCorrectRules.CapitalizeSentences) && !label && IsPlainWord(core) && char.IsLower(core[0]) && !HasCapitalAfterFirst(core) &&
            IsSentenceStart(text, tokenStart))
        {
            var upper = char.ToUpper(core[0], culture).ToString();
            edits.Add(new AutoCorrectEdit(coreStart, 1, upper, core[0].ToString(), AutoCorrectRules.CapitalizeSentences,
                "Capitalized the first letter of the sentence"));
        }
    }

    private static int LeadingOpen(string token)
    {
        var n = 0;
        while (n < token.Length && OpenPunct.IndexOf(token[n]) >= 0) n++;
        return n;
    }

    /// <summary>URLs, e-mail addresses, paths and file names ("example.com", "notes.txt", "C:\x", "a@b").</summary>
    private static bool LooksLikeAddress(string token)
    {
        if (token.IndexOf('@') >= 0 || token.IndexOf('\\') >= 0 || token.IndexOf("://", StringComparison.Ordinal) >= 0) return true;
        if (token.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) return true;
        if (token.IndexOf('/') >= 0 && !Regex.IsMatch(token, @"^[(\[]?\d+/\d+[)\].,;:!?]*$")) return true;
        // A dot between two letters or digits: "example.com", "notes.txt", "v1.2" (but not "e.g." or "U.S.").
        return Regex.IsMatch(token, @"[\p{L}\p{N}]{2,}\.[\p{L}\p{N}]") || Regex.IsMatch(token, @"[\p{L}\p{N}]\.[\p{L}\p{N}]{2,}");
    }

    /// <summary>Letters with apostrophes or hyphens inside ("don’t", "e-mail"), nothing else.</summary>
    private static bool IsPlainWord(string core)
    {
        foreach (var c in core)
            if (!char.IsLetter(c) && c is not ('\'' or '’' or '-' or '‐' or '\u00AD') && !char.IsSurrogate(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                return false;
        return true;
    }

    /// <summary>"iPhone", "eBay", "macOS": deliberately lower-case first letters stay.</summary>
    private static bool HasCapitalAfterFirst(string core)
    {
        for (var i = 1; i < core.Length; i++)
            if (char.IsUpper(core[i])) return true;
        return false;
    }

    private static bool IsTwoInitialCapitals(string core)
    {
        if (core.Length < 3 || !char.IsUpper(core[0]) || !char.IsUpper(core[1])) return false;
        for (var i = 2; i < core.Length; i++)
            if (!char.IsLower(core[i])) return false;
        if (core.Substring(2) == "s") return false; // CDs, IDs, PCs
        return !TwoCapsExceptions.Contains(core);
    }

    private static bool IsPronounI(string text, int tokenStart, int coreStart, int coreEnd, int end)
    {
        var core = text.Substring(coreStart, coreEnd - coreStart);
        if (!(core == "i" || Regex.IsMatch(core, "^i['’](m|d|ll|ve)$"))) return false;
        // Roman numerals and list labels: "(i)", "i)", and "i." at the start of a paragraph.
        if (coreStart > tokenStart && text[coreStart - 1] == '(') return false;
        if (coreEnd < end && text[coreEnd] == ')') return false;
        if (core == "i" && coreEnd < end && text[coreEnd] == '.' && text.Substring(0, tokenStart).Trim().Length == 0) return false;
        return true;
    }

    /// <summary>True if a word starting at <paramref name="tokenStart"/> begins a sentence.</summary>
    private bool IsSentenceStart(string text, int tokenStart)
    {
        var j = tokenStart - 1;
        while (j >= 0 && IsSpace(text[j]) && text[j] != '\v') j--;
        if (j < 0) return true; // start of the paragraph
        if (j == tokenStart - 1) return false; // no space before the word
        if (text[j] == '\v') return false;
        var prevEnd = j + 1;
        var prevStart = j;
        while (prevStart > 0 && !char.IsWhiteSpace(text[prevStart - 1])) prevStart--;
        var prev = text.Substring(prevStart, prevEnd - prevStart);

        // Closing quotes and brackets after the full stop: “… end.” Next
        var k = prev.Length - 1;
        while (k >= 0 && prev[k] is '”' or '’' or '"' or '\'' or ')' or ']' or '»') k--;
        if (k < 0) return false;
        var mark = prev[k];
        if (mark is '!' or '?') return true;
        if (mark != '.') return false;

        var word = prev.Substring(0, k + 1);
        var lead = 0;
        while (lead < word.Length && OpenPunct.IndexOf(word[lead]) >= 0) lead++;
        word = word.Substring(lead);
        if (word.EndsWith("..", StringComparison.Ordinal)) return false; // an ellipsis rarely ends a sentence
        if (DefaultAbbreviations.Contains(word) || Settings.Abbreviations.Contains(word)) return false;
        // Initials and dotted abbreviations: "J.", "U.S.", "i.e.", "J.R.R."
        if (Regex.IsMatch(word, @"^(\p{L}\.)+$")) return false;
        // Numbers with decimals or section numbers: "3.14.", "2.1."
        if (Regex.IsMatch(word, @"^\d+([.,]\d+)+\.$")) return false;
        return true;
    }
}
