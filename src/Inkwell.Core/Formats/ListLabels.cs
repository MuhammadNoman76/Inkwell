using System.Globalization;
using System.Text;
using Inkwell.Core.Model;

namespace Inkwell.Core.Formats;

/// <summary>A list paragraph's label, as computed by <see cref="ListTracker"/>.</summary>
internal sealed class ListLabel
{
    public ListLabel(int numId, int level, NumberingLevel definition, int value, string text)
    {
        NumId = numId;
        Level = level;
        Definition = definition;
        Value = value;
        Text = text;
    }

    public int NumId { get; }
    public int Level { get; }
    public NumberingLevel Definition { get; }

    /// <summary>This level's counter (the item's number).</summary>
    public int Value { get; }

    /// <summary>The label as shown ("1.", "1.2.", "a)", "•"); "" for lists without labels.</summary>
    public string Text { get; }

    public bool IsBullet => Definition.IsBullet;

    /// <summary>A list with no visible label (numFmt none).</summary>
    public bool IsPlain => Definition.Format == "none" || (Text.Length == 0 && !IsBullet);

    public bool IsOrdered => !IsBullet && !IsPlain;
}

/// <summary>
/// Follows the lists of a document in reading order and computes each list paragraph's number and label the way
/// Word does: counters are shared by the list instances of one definition unless an instance restarts
/// (w:startOverride); deeper levels restart after a higher level (w:lvlRestart).
/// </summary>
internal sealed class ListTracker
{
    private readonly Document _doc;
    private readonly Dictionary<string, int[]> _counters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool[]> _started = new(StringComparer.Ordinal);

    public ListTracker(Document doc)
    {
        _doc = doc;
    }

    /// <summary>The paragraph's list (numId, level) from its own properties or its style, or null.</summary>
    public (int NumId, int Level)? ListOf(Paragraph p)
    {
        var numId = p.Props.NumId;
        var level = p.Props.NumLevel;
        if (numId == null)
        {
            var styleId = p.Props.StyleId ?? _doc.Styles.DefaultParagraphStyleId;
            foreach (var s in _doc.Styles.Chain(styleId))
            {
                var pp = s.ParaProps();
                if (pp?.NumId is { } styleNum)
                {
                    numId = styleNum;
                    level ??= pp.NumLevel;
                    if (level == null && _doc.Numbering.ForStyle(styleId) is { } link && link.NumId == styleNum) level = link.Level;
                    break;
                }
            }
        }
        if (numId is not > 0) return null;
        return (numId.Value, MathEx.Clamp(level ?? 0, 0, 8));
    }

    /// <summary>Advances the counters for a list paragraph and returns its label; null for other paragraphs.</summary>
    public ListLabel? Next(Paragraph p)
    {
        if (ListOf(p) is not { } list) return null;
        var def = _doc.Numbering.Level(list.NumId, list.Level);
        if (def == null) return null;
        var key = Key(list.NumId);
        if (!_counters.TryGetValue(key, out var counters))
        {
            counters = new int[9];
            _counters[key] = counters;
            _started[key] = new bool[9];
        }
        var started = _started[key];
        var level = list.Level;
        counters[level] = started[level] ? counters[level] + 1 : StartOf(list.NumId, level);
        started[level] = true;
        for (var deeper = level + 1; deeper < 9; deeper++)
        {
            var d = _doc.Numbering.Level(list.NumId, deeper);
            var restart = d?.Restart;
            // lvlRestart: null = after any higher level; 0 = never; n = after level n (1-based) or higher.
            if (restart == null || (restart.Value > 0 && level < restart.Value)) started[deeper] = false;
        }
        var text = Label(list.NumId, level, def, counters, started);
        return new ListLabel(list.NumId, level, def, counters[level], text);
    }

    private string Key(int numId)
    {
        var num = _doc.Numbering.NumElement(numId);
        var restarts = num?.Elements(Ns.W + "lvlOverride").Any(o => o.Element(Ns.W + "startOverride") != null || o.Element(Ns.W + "lvl") != null) == true;
        return restarts ? "n" + numId.ToString(CultureInfo.InvariantCulture) : "a" + _doc.Numbering.AbstractOf(numId).ToString(CultureInfo.InvariantCulture);
    }

    private int StartOf(int numId, int level) => _doc.Numbering.Level(numId, level)?.Start ?? 1;

    private string Label(int numId, int level, NumberingLevel def, int[] counters, bool[] started)
    {
        if (def.Format == "none") return Pattern(def.Text, numId, level, def, counters, started);
        if (def.IsBullet) return ListFactory.NormalizeBullet(def.Text);
        return Pattern(def.Text, numId, level, def, counters, started);
    }

    private string Pattern(string text, int numId, int level, NumberingLevel def, int[] counters, bool[] started)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '%' && i + 1 < text.Length && text[i + 1] is >= '1' and <= '9')
            {
                var l = text[i + 1] - '1';
                var ld = l == level ? def : _doc.Numbering.Level(numId, l);
                var value = started[l] ? counters[l] : ld?.Start ?? 1;
                var format = ld?.Format ?? "decimal";
                if (def.IsLegal && l < level) format = "decimal";
                sb.Append(NumberFormats.Format(value, format == "bullet" ? "decimal" : format));
                i++;
                continue;
            }
            sb.Append(text[i]);
        }
        return sb.ToString();
    }
}
