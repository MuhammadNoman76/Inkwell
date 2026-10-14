using System.Text;

namespace Inkwell.Core.Model;

/// <summary>Parses field codes such as <c>TOC \o "1-3" \h \z \u</c> or <c>PAGEREF _Toc123 \h</c>.</summary>
public sealed class FieldCode
{
    private FieldCode(string type, List<string> arguments, List<(string Name, string? Value)> switches)
    {
        Type = type;
        Arguments = arguments;
        Switches = switches;
    }

    /// <summary>Upper-case field type (PAGE, TOC, REF…).</summary>
    public string Type { get; }

    /// <summary>Plain arguments after the type (unquoted).</summary>
    public List<string> Arguments { get; }

    /// <summary>Switches with their value: ("\o", "1-3"), ("\h", null).</summary>
    public List<(string Name, string? Value)> Switches { get; }

    public string? FirstArgument => Arguments.Count > 0 ? Arguments[0] : null;

    public bool Has(string name) => Switches.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    public string? SwitchValue(string name) => Switches.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>The \* format switch (ARABIC, ROMAN, roman, ALPHABETIC, alphabetic, MERGEFORMAT…), excluding MERGEFORMAT.</summary>
    public string? NumberFormat => Switches.Where(s => s.Name == "\\*" && s.Value != null && !s.Value.Equals("MERGEFORMAT", StringComparison.OrdinalIgnoreCase))
        .Select(s => s.Value).FirstOrDefault();

    public static FieldCode Parse(string code)
    {
        var tokens = Tokenize(code);
        var type = tokens.Count > 0 ? tokens[0].Text.ToUpperInvariant() : "";
        var args = new List<string>();
        var switches = new List<(string, string?)>();
        for (var i = 1; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (!t.Quoted && t.Text.StartsWith("\\", StringComparison.Ordinal) && t.Text.Length >= 2)
            {
                var name = t.Text;
                string? value = null;
                if (i + 1 < tokens.Count)
                {
                    var next = tokens[i + 1];
                    var nextIsSwitch = !next.Quoted && next.Text.StartsWith("\\", StringComparison.Ordinal);
                    if (next.Quoted || (!nextIsSwitch && TakesValue(type, name)))
                    {
                        value = next.Text;
                        i++;
                    }
                }
                switches.Add((name, value));
            }
            else
            {
                args.Add(t.Text);
            }
        }
        return new FieldCode(type, args, switches);
    }

    /// <summary>Switches whose (unquoted) value follows them: \* ARABIC, \l 1033, \r 1…</summary>
    private static bool TakesValue(string type, string name)
    {
        if (name is "\\*" or "\\#" or "\\@") return true;
        var s = name.ToLowerInvariant();
        return type switch
        {
            "CITATION" => s is "\\l" or "\\p" or "\\s" or "\\f" or "\\v" or "\\m",
            "SEQ" => s is "\\r" or "\\s",
            "TOC" => s is "\\o" or "\\t" or "\\b" or "\\f" or "\\l" or "\\p" or "\\s" or "\\d" or "\\c" or "\\a",
            "BIBLIOGRAPHY" => s is "\\l" or "\\f" or "\\m",
            "STYLEREF" => false,
            "DOCPROPERTY" => false,
            _ => false,
        };
    }

    private static List<(string Text, bool Quoted)> Tokenize(string code)
    {
        var result = new List<(string, bool)>();
        var i = 0;
        while (i < code.Length)
        {
            while (i < code.Length && char.IsWhiteSpace(code[i])) i++;
            if (i >= code.Length) break;
            if (code[i] == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < code.Length && code[i] != '"')
                {
                    if (code[i] == '\\' && i + 1 < code.Length && code[i + 1] == '"')
                    {
                        sb.Append('"');
                        i += 2;
                        continue;
                    }
                    sb.Append(code[i]);
                    i++;
                }
                i++;
                result.Add((sb.ToString(), true));
            }
            else
            {
                var start = i;
                // A switch name is one or two characters after the backslash: "\o", "\*", "\#"; "\h\z" splits.
                if (code[i] == '\\' && i + 1 < code.Length)
                {
                    i += 2;
                    result.Add((code.Substring(start, i - start), false));
                    continue;
                }
                while (i < code.Length && !char.IsWhiteSpace(code[i]) && code[i] != '"' && code[i] != '\\') i++;
                result.Add((code.Substring(start, i - start), false));
            }
        }
        return result;
    }

    /// <summary>The kind of a field from its code.</summary>
    public static FieldKind KindOf(string code)
    {
        var trimmed = code.TrimStart();
        var end = 0;
        while (end < trimmed.Length && !char.IsWhiteSpace(trimmed[end]) && trimmed[end] != '\\') end++;
        var type = trimmed.Substring(0, end).ToUpperInvariant();
        return type switch
        {
            "PAGE" => FieldKind.Page,
            "NUMPAGES" => FieldKind.NumPages,
            "SECTIONPAGES" => FieldKind.SectionPages,
            "DATE" => FieldKind.Date,
            "TIME" => FieldKind.Time,
            "CREATEDATE" => FieldKind.CreateDate,
            "SAVEDATE" => FieldKind.SaveDate,
            "PRINTDATE" => FieldKind.PrintDate,
            "REF" => FieldKind.Ref,
            "PAGEREF" => FieldKind.PageRef,
            "NOTEREF" => FieldKind.NoteRef,
            "SEQ" => FieldKind.Seq,
            "STYLEREF" => FieldKind.StyleRef,
            "HYPERLINK" => FieldKind.Hyperlink,
            "CITATION" => FieldKind.Citation,
            "TOC" => FieldKind.Toc,
            "BIBLIOGRAPHY" => FieldKind.Bibliography,
            "TITLE" => FieldKind.Title,
            "AUTHOR" => FieldKind.Author,
            "SUBJECT" => FieldKind.Subject,
            "FILENAME" => FieldKind.FileName,
            "DOCPROPERTY" => FieldKind.DocProperty,
            "NUMWORDS" => FieldKind.NumWords,
            "NUMCHARS" => FieldKind.NumChars,
            "SYMBOL" => FieldKind.Symbol,
            "INDEX" => FieldKind.Index,
            "MERGEFIELD" => FieldKind.MergeField,
            "IF" => FieldKind.If,
            "FORMTEXT" => FieldKind.FormText,
            _ when type.StartsWith("REF", StringComparison.Ordinal) => FieldKind.Other,
            _ => FieldKind.Other,
        };
    }

    /// <summary>Fields Inkwell computes itself (others keep their saved result).</summary>
    public static bool IsComputed(FieldKind kind) => kind is FieldKind.Page or FieldKind.NumPages or FieldKind.SectionPages or FieldKind.Ref or FieldKind.PageRef
        or FieldKind.Seq or FieldKind.NoteRef or FieldKind.StyleRef or FieldKind.Citation or FieldKind.Title or FieldKind.Author or FieldKind.Subject
        or FieldKind.NumWords or FieldKind.NumChars or FieldKind.Date or FieldKind.Time;

    /// <summary>Formats a number the way a \* switch asks (ARABIC, ROMAN, roman, ALPHABETIC, alphabetic, Ordinal…).</summary>
    public static string FormatNumber(int n, string? format) => format switch
    {
        "ROMAN" => NumberFormats.Roman(n).ToUpperInvariant(),
        "roman" => NumberFormats.Roman(n),
        "ALPHABETIC" => NumberFormats.Letters(n).ToUpperInvariant(),
        "alphabetic" => NumberFormats.Letters(n),
        "Ordinal" => NumberFormats.Ordinal(n),
        "ArabicDash" => "- " + n + " -",
        _ => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}

/// <summary>Number formats for lists, page numbers and fields.</summary>
public static class NumberFormats
{
    public static string Roman(int n)
    {
        if (n <= 0 || n >= 4000) return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var values = new[] { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };
        var symbols = new[] { "m", "cm", "d", "cd", "c", "xc", "l", "xl", "x", "ix", "v", "iv", "i" };
        var sb = new StringBuilder();
        for (var i = 0; i < values.Length; i++)
        {
            while (n >= values[i])
            {
                sb.Append(symbols[i]);
                n -= values[i];
            }
        }
        return sb.ToString();
    }

    /// <summary>a, b, … z, aa, bb, … (Word's letter numbering repeats the letter).</summary>
    public static string Letters(int n)
    {
        if (n <= 0) return "";
        var letter = (char)('a' + (n - 1) % 26);
        return new string(letter, (n - 1) / 26 + 1);
    }

    public static string Ordinal(int n)
    {
        var s = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var mod100 = n % 100;
        if (mod100 is >= 11 and <= 13) return s + "th";
        return (n % 10) switch { 1 => s + "st", 2 => s + "nd", 3 => s + "rd", _ => s + "th" };
    }

    private static readonly string[] Ones = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen" };
    private static readonly string[] Tens = { "", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety" };

    public static string Cardinal(int n)
    {
        if (n < 0) return "minus " + Cardinal(-n);
        if (n < 20) return Ones[n];
        if (n < 100) return Tens[n / 10] + (n % 10 > 0 ? "-" + Ones[n % 10] : "");
        if (n < 1000) return Ones[n / 100] + " hundred" + (n % 100 > 0 ? " " + Cardinal(n % 100) : "");
        if (n < 1000000) return Cardinal(n / 1000) + " thousand" + (n % 1000 > 0 ? " " + Cardinal(n % 1000) : "");
        return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string OrdinalText(int n)
    {
        var c = Cardinal(n);
        if (c.EndsWith("one", StringComparison.Ordinal)) return c.Substring(0, c.Length - 3) + "first";
        if (c.EndsWith("two", StringComparison.Ordinal)) return c.Substring(0, c.Length - 3) + "second";
        if (c.EndsWith("three", StringComparison.Ordinal)) return c.Substring(0, c.Length - 5) + "third";
        if (c.EndsWith("five", StringComparison.Ordinal)) return c.Substring(0, c.Length - 4) + "fifth";
        if (c.EndsWith("eight", StringComparison.Ordinal)) return c + "h";
        if (c.EndsWith("nine", StringComparison.Ordinal)) return c.Substring(0, c.Length - 1) + "th";
        if (c.EndsWith("twelve", StringComparison.Ordinal)) return c.Substring(0, c.Length - 2) + "fth";
        if (c.EndsWith("y", StringComparison.Ordinal)) return c.Substring(0, c.Length - 1) + "ieth";
        return c + "th";
    }

    /// <summary>A number in a WordprocessingML number format (w:numFmt / pgNumType fmt).</summary>
    public static string Format(int n, string? numFmt)
    {
        switch (numFmt)
        {
            case null:
            case "decimal":
                return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            case "decimalZero":
                return n < 10 ? "0" + n : n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            case "upperRoman":
                return Roman(n).ToUpperInvariant();
            case "lowerRoman":
                return Roman(n);
            case "upperLetter":
                return Letters(n).ToUpperInvariant();
            case "lowerLetter":
                return Letters(n);
            case "ordinal":
                return Ordinal(n);
            case "cardinalText":
                return Capitalize(Cardinal(n));
            case "ordinalText":
                return Capitalize(OrdinalText(n));
            case "none":
                return "";
            case "bullet":
                return "•";
            case "arabicAbjad":
            case "arabicAlpha":
                return ArabicLetter(n, numFmt == "arabicAbjad");
            case "hindiNumbers":
            case "hindiVowels":
            case "hindiConsonants":
            case "hindiCounting":
                return Digits(n, '٠');
            case "decimalFullWidth":
            case "decimalFullWidth2":
                return Digits(n, '０');
            case "decimalEnclosedCircle":
                return n is >= 1 and <= 20 ? ((char)(0x2460 + n - 1)).ToString() : n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            default:
                return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

    private static string Digits(int n, char zero)
    {
        var s = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        foreach (var c in s) sb.Append(c is >= '0' and <= '9' ? (char)(zero + (c - '0')) : c);
        return sb.ToString();
    }

    private static string ArabicLetter(int n, bool abjad)
    {
        const string alpha = "ابتثجحخدذرزسشصضطظعغفقكلمنهوي";
        const string abjadOrder = "ابجدهوزحطيكلمنسعفصقرشتثخذضظغ";
        var letters = abjad ? abjadOrder : alpha;
        if (n <= 0) return "";
        return letters[(n - 1) % letters.Length].ToString();
    }
}
