using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Inkwell.Core.Model;
using Inkwell.Core.Templates;

namespace Inkwell.Core.Formats;

/// <summary>Options for <see cref="RtfImporter"/>.</summary>
public sealed class RtfImportOptions
{
    /// <summary>
    /// True (opening a file): keep the RTF's fonts, sizes, colours, highlights, spacing, indents and alignment as
    /// direct formatting. False (pasting, "match destination"): keep the structure (styles by name, lists, tables,
    /// notes, links, pictures) and bold, italic, underline, strikethrough, superscript and subscript only.
    /// </summary>
    public bool KeepSourceFormatting { get; set; } = true;

    /// <summary>The design of the new document (the default design when null).</summary>
    public DocumentDesign? Design { get; set; }
}

/// <summary>
/// RTF import: a complete RTF reader (groups, control words, ignorable \* destinations, \ansicpg and per-font
/// \fcharset code pages for \'hh bytes, \uN with \ucN fallback skipping, font / colour tables, the style sheet
/// (heading N, Title, Quote… mapped by name), character and paragraph formatting, special characters, fields
/// (HYPERLINK becomes a link; other fields keep their result), pictures (PNG, JPEG, EMF, WMF, DIB; others are
/// counted and skipped), tables (\trowd…\row, nested tables, horizontal and vertical merges, cell shading and
/// borders), lists (\listtable / \listoverridetable, and WordPad's \pn paragraphs), footnotes and endnotes,
/// bookmarks, document information and page setup.
/// </summary>
public static class RtfImporter
{
    /// <summary>Opens an RTF file as a new document (keeping its formatting).</summary>
    public static Document ImportFile(string path, RtfImportOptions? options = null) => Import(File.ReadAllBytes(path), options);

    /// <summary>A new document from RTF bytes.</summary>
    public static Document Import(byte[] rtf, RtfImportOptions? options = null)
    {
        options ??= new RtfImportOptions();
        var doc = NewDocument.Create(options.Design);
        var result = new PasteResult();
        var converter = new RtfConverter(doc, options.KeepSourceFormatting, wholeDocument: true, result);
        doc.Body.Blocks.AddRange(converter.Convert(rtf));
        doc.Warnings.AddRange(result.Warnings);
        NewDocument.Finish(doc);
        return doc;
    }

    /// <summary>A new document from RTF text (as the clipboard gives it).</summary>
    public static Document Import(string rtf, RtfImportOptions? options = null) => Import(ToBytes(rtf), options);

    /// <summary>Blocks for pasting RTF into <paramref name="target"/> (its styles; lists, pictures and notes are added to it).</summary>
    public static PasteResult ImportFragment(byte[] rtf, Document target, bool keepSourceFormatting = false)
    {
        var result = new PasteResult();
        var converter = new RtfConverter(target, keepSourceFormatting, wholeDocument: false, result);
        result.Blocks.AddRange(converter.Convert(rtf));
        return result;
    }

    /// <summary>Blocks for pasting RTF text into <paramref name="target"/>.</summary>
    public static PasteResult ImportFragment(string rtf, Document target, bool keepSourceFormatting = false) =>
        ImportFragment(ToBytes(rtf), target, keepSourceFormatting);

    /// <summary>RTF text as bytes: characters up to U+00FF are bytes; others (which RTF can't contain raw) become \uN escapes.</summary>
    private static byte[] ToBytes(string rtf)
    {
        var bytes = new List<byte>(rtf.Length);
        foreach (var c in rtf)
        {
            if (c <= 'ÿ')
            {
                bytes.Add((byte)c);
                continue;
            }
            foreach (var b in Encoding.ASCII.GetBytes("\\uc0\\u" + ((short)c).ToString(CultureInfo.InvariantCulture) + " ")) bytes.Add(b);
        }
        return bytes.ToArray();
    }
}

internal sealed class RtfConverter
{
    private readonly Document _doc;
    private readonly bool _keep;
    private readonly bool _whole;
    private readonly PasteResult _result;
    private readonly StyleMap _styles;
    private readonly ListFactory _lists;

    private byte[] _data = Array.Empty<byte>();
    private int _pos;

    public RtfConverter(Document doc, bool keep, bool wholeDocument, PasteResult result)
    {
        _doc = doc;
        _keep = keep;
        _whole = wholeDocument;
        _result = result;
        _styles = new StyleMap(doc);
        _lists = new ListFactory(doc);
        _story = new StoryContext();
    }

    // ------------------------------------------------------------------ state

    private enum Dest
    {
        Text, Skip, Ignore, FontTable, FontEntry, ColorTable, StyleSheet, StyleEntry, Info, InfoField, InfoTime, Pict, Field, FieldInst, FieldResult,
        BookmarkStart, BookmarkEnd, ListTable, List, ListLevel, LevelText, LevelNumbers, ListOverrideTable, ListOverride, Footnote, Pn, PnTextBefore,
        PnTextAfter, ShapeName, ShapeValue, Upr, UprPending, NestTableProps,
    }

    private sealed class CharFmt
    {
        public bool Bold, Italic, Strike, DoubleStrike, Caps, SmallCaps, Hidden, Rtl;
        public string? Underline;
        public string? VertAlign;
        public int Size = 24;
        public int? SizeCs;
        public int Font = -1;
        public int FontCs = -1;
        public int Color, Highlight, Background;
        public int Lang, LangBidi;
        public int CharStyle = -1;

        public CharFmt Clone() => (CharFmt)MemberwiseClone();

        public string Key() => string.Join("|", Bold, Italic, Strike, DoubleStrike, Caps, SmallCaps, Hidden, Rtl, Underline, VertAlign, Size, SizeCs, Font, FontCs, Color, Highlight, Background, Lang, LangBidi, CharStyle);
    }

    private sealed class PnInfo
    {
        public bool Bullet;
        public string Format = "decimal";
        public int Start = 1;
        public int Level;
        public string Before = "";
        public string After = "";
        public int Font = -1;

        public string Key() => string.Join("|", Bullet, Format, Start, Level, Before, After);
    }

    private sealed class ParaFmt
    {
        public string? Align;
        public int Left, Right, FirstLine;
        public int? LeftLogical, RightLogical;
        public int SpaceBefore, SpaceAfter, Line;
        public bool LineMultiple;
        public bool AutoBefore, AutoAfter;
        public bool KeepNext, KeepLines, PageBreakBefore, Rtl, InTable;
        public int Nest = 1;
        public int Style;
        public int Ls, Ilvl;
        public int? Outline;
        public PnInfo? Pn;
        public Dictionary<string, BorderDef>? Borders;

        public ParaFmt Clone()
        {
            var c = (ParaFmt)MemberwiseClone();
            if (Borders != null) c.Borders = new Dictionary<string, BorderDef>(Borders);
            return c;
        }

        public int Depth => InTable ? Math.Max(1, Nest) : 0;
    }

    private sealed class GroupState
    {
        public Dest Dest = Dest.Text;
        public bool Owner;
        public CharFmt Char = new();
        public ParaFmt Para = new();
        public int Uc = 1;
        public string? InfoKey;
        public Dest UprParent;

        public GroupState Child() => new()
        {
            Dest = Dest,
            Owner = false,
            Char = Char.Clone(),
            Para = Para.Clone(),
            Uc = Uc,
            InfoKey = InfoKey,
            UprParent = UprParent,
        };
    }

    private GroupState _state = new();
    private readonly Stack<GroupState> _stack = new();
    private bool _groupStart;
    private bool _star;
    private int _ucSkip;
    private readonly List<byte> _bytes = new();
    private readonly StringBuilder _run = new();

    // Document-level settings.
    private int _ansiCodePage = 1252;
    private int _defaultFont = -1;
    private int _defaultLang = 1033;

    private sealed class FontInfo
    {
        public string Name = "";
        public int Charset = -1;
        public bool Symbol;
    }

    private readonly Dictionary<int, FontInfo> _fonts = new();
    private FontInfo? _fontEntry;
    private int _fontEntryIndex = -1;
    private readonly StringBuilder _fontName = new();

    private readonly List<string?> _colors = new();
    private int _red, _green, _blue;
    private bool _colorSet;

    private sealed class StyleInfo
    {
        public int Index;
        public string Type = "paragraph";
        public string Name = "";
        public CharFmt Char = new();
        public int? Outline;
    }

    private readonly Dictionary<int, StyleInfo> _paraStyles = new();
    private readonly Dictionary<int, StyleInfo> _charStyles = new();
    private StyleInfo? _styleEntry;
    private readonly StringBuilder _styleName = new();

    // ------------------------------------------------------------------ stories, paragraphs, tables

    private sealed class CellDef
    {
        public int Right;
        public bool HMergeFirst, HMerge, VMergeFirst, VMerge;
        public string? VAlign;
        public int Shading = -1;
        public int ShadingPercent;
        public Dictionary<string, BorderDef> Borders = new(StringComparer.Ordinal);
    }

    private sealed class RowDef
    {
        public int Left;
        public List<CellDef> Cells = new();
        public bool Header, CantSplit;
        public string? Align;
        public Dictionary<string, BorderDef> Borders = new(StringComparer.Ordinal);
    }

    private sealed class TableBuilder
    {
        public List<(List<List<Block>> Cells, RowDef Def)> Rows = new();
        public List<List<Block>> Cells = new();
        public List<Block> CellBlocks = new();
    }

    private sealed class StoryContext
    {
        public List<Block> Blocks = new();
        public List<Inline> Inlines = new();
        public List<TableBuilder> Tables = new();
        public NoteKind Kind = NoteKind.Footnote;
        public NoteReference? PendingReference;
    }

    private StoryContext _story;
    private readonly Stack<StoryContext> _stories = new();
    private Paragraph? _lastBodyParagraph;
    private readonly Dictionary<int, RowDef> _rowDefs = new();
    private int _rowDefDepth = 1;
    private CellDef _cellDef = new();
    private (Dictionary<string, BorderDef> Owner, string Side)? _border;

    // Fields, bookmarks, pictures, lists, sections, information.
    private sealed class FieldState
    {
        public StringBuilder Instruction = new();
        public Hyperlink? Link;
        public bool InResult;
        public bool ResultEmitted;
        public int? SymbolCode;
        public string? SymbolFont;
    }

    private readonly List<FieldState> _fields = new();
    private readonly StringBuilder _bookmarkName = new();
    private readonly Dictionary<string, int> _bookmarks = new(StringComparer.Ordinal);
    private int _nextBookmarkId;
    private readonly HashSet<string> _existingBookmarks = new(StringComparer.Ordinal);

    private sealed class PictState
    {
        public string Type = "";
        public int W, H, GoalW, GoalH, ScaleX = 100, ScaleY = 100;
        public List<byte> Data = new();
        public int Nibble = -1;
    }

    private PictState? _pict;
    private string? _shapeName;
    private readonly StringBuilder _shapeText = new();

    private sealed class RtfLevel
    {
        public int Nfc;
        public int Start = 1;
        public string Text = "";
        public string Numbers = "";
        public int Follow;
        public int Jc;
        public int? Left;
        public int? FirstLine;
        public int Font = -1;
        public bool Legal;
    }

    private sealed class RtfList
    {
        public int Id;
        public List<RtfLevel> Levels = new();
    }

    private readonly Dictionary<int, RtfList> _listDefs = new();
    private RtfList? _listEntry;
    private RtfLevel? _levelEntry;
    private readonly StringBuilder _levelText = new();
    private readonly Dictionary<int, (int ListId, Dictionary<int, int> Starts)> _overrides = new();
    private int _overrideLs, _overrideListId, _overrideLevel = -1;
    private Dictionary<int, int> _overrideStarts = new();
    private readonly Dictionary<int, int?> _numIds = new();
    private string? _lastPnKey;
    private int _lastPnNum;
    private bool _lastWasPn;

    private sealed class SectFmt
    {
        public int? Width, Height, Left, Right, Top, Bottom, Gutter, HeaderY, FooterY, Columns, ColumnSpace, PageStart;
        public bool Landscape, TitlePage;
        public string Break = "nextPage";

        public SectFmt Clone() => (SectFmt)MemberwiseClone();
    }

    private readonly SectFmt _docSect = new();
    private SectFmt _sect = new();
    private bool _sectSeen;
    private readonly StringBuilder _info = new();
    private int[] _infoDate = new int[6];

    // ------------------------------------------------------------------ entry

    public List<Block> Convert(byte[] data)
    {
        _data = data;
        _pos = 0;
        _colors.Clear();
        foreach (var name in _doc.Bookmarks().Select(b => b.Name)) _existingBookmarks.Add(name);
        _nextBookmarkId = _doc.NextBookmarkId();
        var len = data.Length;
        while (_pos < len)
        {
            var b = data[_pos++];
            switch (b)
            {
                case (byte)'{':
                    FlushText();
                    _stack.Push(_state);
                    _state = _state.Child();
                    _groupStart = true;
                    _ucSkip = 0;
                    break;
                case (byte)'}':
                    EndGroup();
                    break;
                case (byte)'\\':
                    ReadControl();
                    break;
                case (byte)'\r':
                case (byte)'\n':
                case 0:
                    break;
                case (byte)'\t':
                    ControlWord("tab", false, 0);
                    break;
                default:
                    _groupStart = false;
                    TextByte(b);
                    break;
            }
        }
        FlushText();
        while (_stack.Count > 0) EndGroup();
        while (_stories.Count > 0) EndFootnote();
        if (_story.Inlines.Count > 0) FinishParagraph();
        CloseTables(0);
        if (_whole) _doc.Body.FinalSection = BuildSection(_sectSeen ? _sect : _docSect);
        // A footnote mark (\chftn) that no footnote followed.
        foreach (var p in DocumentWalker.Paragraphs(_story.Blocks)) p.Inlines.RemoveAll(i => i is NoteReference { Id: -1 });
        return _story.Blocks;
    }

    private void EndGroup()
    {
        FlushText();
        if (_stack.Count == 0) return;
        var ended = _state;
        _state = _stack.Pop();
        _ucSkip = 0;
        _groupStart = false;
        if (ended.Owner) EndDestination(ended);
    }

    private void SetDest(Dest dest)
    {
        _state.Dest = dest;
        _state.Owner = true;
    }

    /// <summary>Skips the rest of the current group (its closing brace included) and returns to the parent state.</summary>
    private void SkipGroup()
    {
        FlushText();
        var depth = 1;
        var len = _data.Length;
        while (_pos < len && depth > 0)
        {
            var b = _data[_pos++];
            if (b == '\\')
            {
                if (_pos >= len) break;
                var c = _data[_pos];
                if (c is (byte)'\\' or (byte)'{' or (byte)'}')
                {
                    _pos++;
                    continue;
                }
                // \binN: N raw bytes follow.
                if (c == 'b' && _pos + 2 < len && _data[_pos + 1] == 'i' && _data[_pos + 2] == 'n')
                {
                    var p = _pos + 3;
                    var n = 0;
                    while (p < len && _data[p] is >= (byte)'0' and <= (byte)'9') n = n * 10 + (_data[p++] - '0');
                    if (p < len && _data[p] == ' ') p++;
                    _pos = Math.Min(len, p + n);
                }
                continue;
            }
            if (b == '{') depth++;
            else if (b == '}') depth--;
        }
        if (_stack.Count > 0) _state = _stack.Pop();
        _ucSkip = 0;
        _groupStart = false;
    }

    private void ReadControl()
    {
        var len = _data.Length;
        if (_pos >= len) return;
        var c = _data[_pos];
        if (c is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z')
        {
            var start = _pos;
            while (_pos < len && _pos - start < 32 && _data[_pos] is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z') _pos++;
            var word = Encoding.ASCII.GetString(_data, start, _pos - start);
            var hasParam = false;
            var negative = false;
            long param = 0;
            if (_pos < len && _data[_pos] == '-' && _pos + 1 < len && _data[_pos + 1] is >= (byte)'0' and <= (byte)'9')
            {
                negative = true;
                _pos++;
            }
            while (_pos < len && _data[_pos] is >= (byte)'0' and <= (byte)'9')
            {
                hasParam = true;
                if (param < int.MaxValue) param = param * 10 + (_data[_pos] - '0');
                _pos++;
            }
            if (_pos < len && _data[_pos] == ' ') _pos++;
            var value = (int)Math.Min(param, int.MaxValue);
            if (negative) value = -value;
            ControlWord(word, hasParam, value);
            return;
        }
        _pos++;
        switch (c)
        {
            case (byte)'\\':
            case (byte)'{':
            case (byte)'}':
                _groupStart = false;
                if (SkipFallback()) return;
                FlushBytes();
                EmitText(((char)c).ToString());
                return;
            case (byte)'\'':
                _groupStart = false;
                if (_pos + 1 < len && Hex(_data[_pos]) >= 0 && Hex(_data[_pos + 1]) >= 0)
                {
                    var value = Hex(_data[_pos]) * 16 + Hex(_data[_pos + 1]);
                    _pos += 2;
                    if (SkipFallback()) return;
                    if (_state.Dest is Dest.LevelText or Dest.LevelNumbers)
                    {
                        _levelText.Append((char)value);
                        return;
                    }
                    _bytes.Add((byte)value);
                }
                return;
            case (byte)'*':
                _star = true;
                return;
            case (byte)'~':
                ControlWord("~", false, 0);
                return;
            case (byte)'_':
                ControlWord("_", false, 0);
                return;
            case (byte)'-':
                ControlWord("-", false, 0);
                return;
            case (byte)'\r':
            case (byte)'\n':
                ControlWord("par", false, 0);
                return;
            case (byte)'\t':
                ControlWord("tab", false, 0);
                return;
        }
    }

    private static int Hex(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };

    private bool SkipFallback()
    {
        if (_ucSkip <= 0) return false;
        _ucSkip--;
        return true;
    }

    private void TextByte(byte b)
    {
        if (_state.Dest == Dest.Pict)
        {
            var h = Hex(b);
            if (h < 0 || _pict == null) return;
            if (_pict.Nibble < 0) _pict.Nibble = h;
            else
            {
                _pict.Data.Add((byte)(_pict.Nibble * 16 + h));
                _pict.Nibble = -1;
            }
            return;
        }
        if (SkipFallback()) return;
        if (b == ';' && _state.Dest is Dest.FontEntry or Dest.FontTable or Dest.StyleEntry or Dest.ColorTable or Dest.LevelText or Dest.LevelNumbers)
        {
            FlushBytes();
            Semicolon();
            return;
        }
        if (_state.Dest is Dest.LevelText or Dest.LevelNumbers)
        {
            _levelText.Append((char)b);
            return;
        }
        _bytes.Add(b);
    }

    private void Semicolon()
    {
        switch (_state.Dest)
        {
            case Dest.FontEntry:
            case Dest.FontTable:
                EndFontEntry();
                break;
            case Dest.StyleEntry:
                if (_styleEntry != null)
                {
                    _styleEntry.Char = _state.Char.Clone();
                    _styleEntry.Outline = _state.Para.Outline;
                }
                EndStyleEntry();
                break;
            case Dest.ColorTable:
                _colors.Add(_colorSet ? _red.ToString("X2", CultureInfo.InvariantCulture) + _green.ToString("X2", CultureInfo.InvariantCulture) + _blue.ToString("X2", CultureInfo.InvariantCulture) : null);
                _red = _green = _blue = 0;
                _colorSet = false;
                break;
        }
    }

    private bool IsTextDest => _state.Dest is Dest.Text or Dest.FieldResult or Dest.Footnote;

    private void FlushBytes()
    {
        if (_bytes.Count == 0) return;
        var bytes = _bytes.ToArray();
        _bytes.Clear();
        var font = CurrentFont();
        if (font is { Symbol: true } && IsTextDest)
        {
            FlushRun();
            foreach (var b in bytes)
            {
                var symbol = new SymbolChar { Font = font.Name, Char = (char)(0xF000 + b) };
                symbol.Props = RunPropsFor(_state.Char, _state.Para, CurrentLink()).Clone();
                _story.Inlines.Add(symbol);
                MarkResult();
            }
            return;
        }
        EmitText(TextEncodings.Get(CodePage(font)).GetString(bytes));
    }

    private FontInfo? CurrentFont()
    {
        var index = _state.Char.Font >= 0 ? _state.Char.Font : _defaultFont;
        if (_state.Dest == Dest.FontEntry && _fontEntry != null) return _fontEntry;
        return _fonts.TryGetValue(index, out var f) ? f : null;
    }

    private int CodePage(FontInfo? font)
    {
        var cp = font?.Charset switch
        {
            null or -1 or 0 or 1 => _ansiCodePage,
            2 => 1252,
            77 => 10000,
            128 => 932,
            129 => 949,
            130 => 1361,
            134 => 936,
            136 => 950,
            161 => 1253,
            162 => 1254,
            163 => 1258,
            177 => 1255,
            178 => 1256,
            186 => 1257,
            204 => 1251,
            222 => 874,
            238 => 1250,
            254 => 437,
            255 => 850,
            _ => _ansiCodePage,
        };
        return cp;
    }

    private void FlushText()
    {
        FlushBytes();
        FlushRun();
    }

    /// <summary>Text for the current destination.</summary>
    private void EmitText(string text)
    {
        if (text.Length == 0) return;
        switch (_state.Dest)
        {
            case Dest.Text:
            case Dest.FieldResult:
            case Dest.Footnote:
                if (CurrentFont() is { Symbol: true } symbolFont && text.Any(c => c >= '\uF000' && c <= '\uF0FF'))
                {
                    // Symbol-font characters (Word writes them as \u-3913 …): keep them as symbols of that font.
                    FlushRun();
                    foreach (var c in text)
                    {
                        if (c >= '\uF000' && c <= '\uF0FF')
                        {
                            var symbol = new SymbolChar { Font = symbolFont.Name, Char = c };
                            symbol.Props = RunPropsFor(_state.Char, _state.Para, CurrentLink()).Clone();
                            _story.Inlines.Add(symbol);
                        }
                        else
                        {
                            _run.Append(c);
                        }
                    }
                    MarkResult();
                    break;
                }
                _run.Append(text);
                MarkResult();
                break;
            case Dest.FontEntry:
            case Dest.FontTable:
                _fontName.Append(text);
                break;
            case Dest.StyleEntry:
                _styleName.Append(text);
                break;
            case Dest.InfoField:
                _info.Append(text);
                break;
            case Dest.FieldInst:
                if (_fields.Count > 0) _fields[_fields.Count - 1].Instruction.Append(text);
                break;
            case Dest.BookmarkStart:
            case Dest.BookmarkEnd:
                _bookmarkName.Append(text);
                break;
            case Dest.LevelText:
            case Dest.LevelNumbers:
                _levelText.Append(text);
                break;
            case Dest.ShapeName:
            case Dest.ShapeValue:
                _shapeText.Append(text);
                break;
            case Dest.PnTextBefore:
                if (_state.Para.Pn != null) _state.Para.Pn.Before += text;
                break;
            case Dest.PnTextAfter:
                if (_state.Para.Pn != null) _state.Para.Pn.After += text;
                break;
        }
    }

    private void MarkResult()
    {
        if (_state.Dest == Dest.FieldResult && _fields.Count > 0) _fields[_fields.Count - 1].ResultEmitted = true;
    }

    private Hyperlink? CurrentLink()
    {
        for (var i = _fields.Count - 1; i >= 0; i--)
            if (_fields[i].InResult && _fields[i].Link != null) return _fields[i].Link;
        return null;
    }

    private void FlushRun()
    {
        if (_run.Length == 0) return;
        var text = Content.CleanText(_run.ToString());
        _run.Clear();
        if (text.Length == 0 || !IsTextDest) return;
        var link = CurrentLink();
        Content.AppendText(_story.Inlines, text, RunPropsFor(_state.Char, _state.Para, link, HasRtlText(text)), link);
    }

    private void AddInline(Inline inline)
    {
        FlushText();
        _story.Inlines.Add(inline);
    }

    // ------------------------------------------------------------------ control words

    private void ControlWord(string word, bool hasParam, int param)
    {
        var first = _groupStart;
        _groupStart = false;
        var starred = _star;
        _star = false;
        if (word != "u" && word != "bin" && SkipFallback()) return;
        if (word != "u" && word != "bin" && word != "'") FlushText();

        if (first)
        {
            var handled = TryDestination(word, starred);
            if (handled == 1) return;
            if (handled < 0 && starred)
            {
                SkipGroup();
                return;
            }
        }
        var d = _state.Dest;
        switch (word)
        {
            // ---- document
            case "ansi":
                _ansiCodePage = 1252;
                return;
            case "mac":
                _ansiCodePage = 10000;
                return;
            case "pc":
                _ansiCodePage = 437;
                return;
            case "pca":
                _ansiCodePage = 850;
                return;
            case "ansicpg":
                if (hasParam && TextEncodings.Exists(param)) _ansiCodePage = param;
                return;
            case "deff":
                _defaultFont = param;
                return;
            case "deflang":
                _defaultLang = param;
                return;
            case "uc":
                _state.Uc = Math.Max(0, param);
                return;
            case "u":
            {
                var ch = (char)(param < 0 ? param + 65536 : param);
                FlushBytes();
                if (d is Dest.LevelText or Dest.LevelNumbers) _levelText.Append(ch);
                else if (d != Dest.Pict) EmitText(ch.ToString());
                _ucSkip = _state.Uc;
                return;
            }
            case "bin":
            {
                var n = Math.Max(0, Math.Min(param, _data.Length - _pos));
                if (d == Dest.Pict && _pict != null)
                    for (var k = 0; k < n; k++) _pict.Data.Add(_data[_pos + k]);
                _pos += n;
                return;
            }

            // ---- font, colour and style tables
            case "f":
                if (d is Dest.FontTable or Dest.FontEntry)
                {
                    if (d == Dest.FontTable && _fontEntry != null) EndFontEntry();
                    _fontEntry = new FontInfo();
                    _fontEntryIndex = param;
                    _fontName.Clear();
                    return;
                }
                _state.Char.Font = param;
                return;
            case "fcharset":
                if (_fontEntry != null && d is Dest.FontTable or Dest.FontEntry)
                {
                    _fontEntry.Charset = param;
                    if (param == 2) _fontEntry.Symbol = true;
                }
                return;
            case "red":
                _red = MathEx.Clamp(param, 0, 255);
                _colorSet = true;
                return;
            case "green":
                _green = MathEx.Clamp(param, 0, 255);
                _colorSet = true;
                return;
            case "blue":
                _blue = MathEx.Clamp(param, 0, 255);
                _colorSet = true;
                return;
            case "s":
                if (d == Dest.StyleEntry && _styleEntry != null)
                {
                    _styleEntry.Index = param;
                    _styleEntry.Type = "paragraph";
                }
                else _state.Para.Style = param;
                return;
            case "cs":
                if (d == Dest.StyleEntry && _styleEntry != null)
                {
                    _styleEntry.Index = param;
                    _styleEntry.Type = "character";
                }
                else _state.Char.CharStyle = param;
                return;
            case "ds":
            case "ts":
                if (d == Dest.StyleEntry && _styleEntry != null)
                {
                    _styleEntry.Index = param;
                    _styleEntry.Type = word == "ds" ? "section" : "table";
                }
                return;

            // ---- character formatting
            case "plain":
                _state.Char = new CharFmt { Font = _defaultFont };
                return;
            case "b":
                _state.Char.Bold = !hasParam || param != 0;
                return;
            case "i":
                _state.Char.Italic = !hasParam || param != 0;
                return;
            case "strike":
                _state.Char.Strike = !hasParam || param != 0;
                return;
            case "striked":
                _state.Char.DoubleStrike = !hasParam || param != 0;
                return;
            case "caps":
                _state.Char.Caps = !hasParam || param != 0;
                return;
            case "scaps":
                _state.Char.SmallCaps = !hasParam || param != 0;
                return;
            case "v":
                _state.Char.Hidden = !hasParam || param != 0;
                return;
            case "ul":
                _state.Char.Underline = !hasParam || param != 0 ? "single" : null;
                return;
            case "ulnone":
                _state.Char.Underline = null;
                return;
            case "ulw":
                _state.Char.Underline = "words";
                return;
            case "uld":
                _state.Char.Underline = "dotted";
                return;
            case "uldash":
                _state.Char.Underline = "dash";
                return;
            case "uldb":
                _state.Char.Underline = "double";
                return;
            case "ulth":
                _state.Char.Underline = "thick";
                return;
            case "ulwave":
                _state.Char.Underline = "wave";
                return;
            case "super":
                _state.Char.VertAlign = "superscript";
                return;
            case "sub":
                _state.Char.VertAlign = "subscript";
                return;
            case "nosupersub":
                _state.Char.VertAlign = null;
                return;
            case "fs":
                if (hasParam && param > 0) _state.Char.Size = param;
                return;
            case "afs":
                if (hasParam && param > 0) _state.Char.SizeCs = param;
                return;
            case "af":
                _state.Char.FontCs = param;
                return;
            case "cf":
                _state.Char.Color = param;
                return;
            case "highlight":
                _state.Char.Highlight = param;
                return;
            case "cb":
            case "chcbpat":
                _state.Char.Background = param;
                return;
            case "lang":
                _state.Char.Lang = param;
                return;
            case "alang":
                _state.Char.LangBidi = param;
                return;
            case "rtlch":
                _state.Char.Rtl = true;
                return;
            case "ltrch":
                _state.Char.Rtl = false;
                return;

            // ---- paragraph formatting
            case "pard":
                _state.Para = new ParaFmt();
                return;
            case "ql":
                _state.Para.Align = "left";
                return;
            case "qc":
                _state.Para.Align = "center";
                return;
            case "qr":
                _state.Para.Align = "right";
                return;
            case "qj":
                _state.Para.Align = "both";
                return;
            case "qd":
                _state.Para.Align = "distribute";
                return;
            case "li":
                if (d == Dest.ListLevel && _levelEntry != null) _levelEntry.Left = param;
                else _state.Para.Left = param;
                return;
            case "lin":
                _state.Para.LeftLogical = param;
                return;
            case "ri":
                _state.Para.Right = param;
                return;
            case "rin":
                _state.Para.RightLogical = param;
                return;
            case "fi":
                if (d == Dest.ListLevel && _levelEntry != null) _levelEntry.FirstLine = param;
                else _state.Para.FirstLine = param;
                return;
            case "sb":
                _state.Para.SpaceBefore = param;
                return;
            case "sa":
                _state.Para.SpaceAfter = param;
                return;
            case "sbauto":
                _state.Para.AutoBefore = param != 0;
                return;
            case "saauto":
                _state.Para.AutoAfter = param != 0;
                return;
            case "sl":
                _state.Para.Line = param;
                return;
            case "slmult":
                _state.Para.LineMultiple = param != 0;
                return;
            case "keepn":
                _state.Para.KeepNext = true;
                return;
            case "keep":
                _state.Para.KeepLines = true;
                return;
            case "pagebb":
                _state.Para.PageBreakBefore = true;
                return;
            case "rtlpar":
                _state.Para.Rtl = true;
                return;
            case "ltrpar":
                _state.Para.Rtl = false;
                return;
            case "outlinelevel":
                _state.Para.Outline = param is >= 0 and <= 8 ? param : null;
                return;
            case "intbl":
                _state.Para.InTable = true;
                return;
            case "itap":
                _state.Para.Nest = param;
                _state.Para.InTable = param > 0;
                return;
            case "ls":
                if (d == Dest.ListOverride) _overrideLs = param;
                else _state.Para.Ls = param;
                return;
            case "ilvl":
                _state.Para.Ilvl = MathEx.Clamp(param, 0, 8);
                return;

            // ---- borders
            case "brdrt":
            case "brdrb":
            case "brdrl":
            case "brdrr":
                _state.Para.Borders ??= new Dictionary<string, BorderDef>(StringComparer.Ordinal);
                _border = (_state.Para.Borders, word switch { "brdrt" => "top", "brdrb" => "bottom", "brdrl" => "left", _ => "right" });
                _state.Para.Borders[_border.Value.Side] = new BorderDef("single", 4, 0, "auto");
                return;
            case "box":
                _state.Para.Borders ??= new Dictionary<string, BorderDef>(StringComparer.Ordinal);
                foreach (var side in new[] { "top", "bottom", "left", "right" }) _state.Para.Borders[side] = new BorderDef("single", 4, 0, "auto");
                _border = (_state.Para.Borders, "box");
                return;
            case "clbrdrt":
            case "clbrdrb":
            case "clbrdrl":
            case "clbrdrr":
                _border = (_cellDef.Borders, word switch { "clbrdrt" => "top", "clbrdrb" => "bottom", "clbrdrl" => "left", _ => "right" });
                _cellDef.Borders[_border.Value.Side] = new BorderDef("single", 4, 0, "auto");
                return;
            case "trbrdrt":
            case "trbrdrb":
            case "trbrdrl":
            case "trbrdrr":
            case "trbrdrh":
            case "trbrdrv":
            {
                var row = CurrentRowDef();
                var side = word switch { "trbrdrt" => "top", "trbrdrb" => "bottom", "trbrdrl" => "left", "trbrdrr" => "right", "trbrdrh" => "insideH", _ => "insideV" };
                _border = (row.Borders, side);
                row.Borders[side] = new BorderDef("single", 4, 0, "auto");
                return;
            }
            case "brdrs":
            case "brdrth":
            case "brdrdb":
            case "brdrdot":
            case "brdrdash":
            case "brdrhair":
            case "brdrtriple":
            case "brdrwavy":
            case "brdrinset":
            case "brdroutset":
            case "brdrdashsm":
            case "brdrnone":
            case "brdrnil":
                SetBorder(b => b with
                {
                    Style = word switch
                    {
                        "brdrdb" => "double",
                        "brdrdot" => "dotted",
                        "brdrdash" or "brdrdashsm" => "dashed",
                        "brdrth" => "thick",
                        "brdrtriple" => "triple",
                        "brdrwavy" => "wave",
                        "brdrinset" => "inset",
                        "brdroutset" => "outset",
                        "brdrnone" or "brdrnil" => "nil",
                        _ => "single",
                    },
                });
                return;
            case "brdrw":
                SetBorder(b => b with { Size = MathEx.Clamp((int)Math.Round(param * 8 / 20.0), 2, 96) });
                return;
            case "brdrcf":
                SetBorder(b => b with { Color = ColorAt(param) ?? "auto" });
                return;
            case "brsp":
                SetBorder(b => b with { Space = MathEx.Clamp(param / 20, 0, 31) });
                return;

            // ---- tables
            case "trowd":
                _rowDefDepth = _state.Dest == Dest.NestTableProps ? Math.Max(2, _state.Para.Nest) : 1;
                _rowDefs[_rowDefDepth] = new RowDef();
                _cellDef = new CellDef();
                return;
            case "trleft":
                CurrentRowDef().Left = param;
                return;
            case "trhdr":
                CurrentRowDef().Header = true;
                return;
            case "trkeep":
                CurrentRowDef().CantSplit = true;
                return;
            case "trqc":
                CurrentRowDef().Align = "center";
                return;
            case "trqr":
                CurrentRowDef().Align = "right";
                return;
            case "cellx":
                _cellDef.Right = param;
                CurrentRowDef().Cells.Add(_cellDef);
                _cellDef = new CellDef();
                return;
            case "clmgf":
                _cellDef.HMergeFirst = true;
                return;
            case "clmrg":
                _cellDef.HMerge = true;
                return;
            case "clvmgf":
                _cellDef.VMergeFirst = true;
                return;
            case "clvmrg":
                _cellDef.VMerge = true;
                return;
            case "clvertalt":
                _cellDef.VAlign = "top";
                return;
            case "clvertalc":
                _cellDef.VAlign = "center";
                return;
            case "clvertalb":
                _cellDef.VAlign = "bottom";
                return;
            case "clcbpat":
                _cellDef.Shading = param;
                return;
            case "clshdng":
                _cellDef.ShadingPercent = param;
                return;
            case "cell":
                EndCell(1);
                return;
            case "nestcell":
                EndCell(Math.Max(2, _state.Para.Nest));
                return;
            case "row":
                EndRow(1);
                return;
            case "nestrow":
                EndRow(Math.Max(2, _state.Para.Nest));
                return;

            // ---- breaks and special characters
            case "par":
                if (IsTextDest) FinishParagraph();
                return;
            case "sect":
                if (IsTextDest) EndSection();
                return;
            case "sectd":
                _sect = _docSect.Clone();
                _sectSeen = true;
                return;
            case "page":
                EmitText("\f");
                return;
            case "line":
                EmitText("\v");
                return;
            case "column":
                EmitText("\u000E");
                return;
            case "tab":
                EmitText("\t");
                return;
            case "emdash":
                EmitText("—");
                return;
            case "endash":
                EmitText("–");
                return;
            case "emspace":
                EmitText(" ");
                return;
            case "enspace":
                EmitText(" ");
                return;
            case "qmspace":
                EmitText(" ");
                return;
            case "bullet":
                EmitText("•");
                return;
            case "lquote":
                EmitText("‘");
                return;
            case "rquote":
                EmitText("’");
                return;
            case "ldblquote":
                EmitText("“");
                return;
            case "rdblquote":
                EmitText("”");
                return;
            case "zwj":
                EmitText("‍");
                return;
            case "zwnj":
                EmitText("‌");
                return;
            case "ltrmark":
                EmitText("‎");
                return;
            case "rtlmark":
                EmitText("‏");
                return;
            case "~":
                EmitText(" ");
                return;
            case "_":
                EmitText("‑");
                return;
            case "-":
                EmitText("­");
                return;
            case "chftn":
                FootnoteMark();
                return;
            case "ftnalt":
                if (_stories.Count > 0) _story.Kind = NoteKind.Endnote;
                return;

            // ---- pictures
            case "pngblip":
            case "jpegblip":
            case "emfblip":
            case "wmetafile":
            case "dibitmap":
            case "wbitmap":
            case "macpict":
            case "pmmetafile":
            case "wbmbitspixel":
                if (_pict != null && word != "wbmbitspixel") _pict.Type = word;
                return;
            case "picw":
                if (_pict != null) _pict.W = param;
                return;
            case "pich":
                if (_pict != null) _pict.H = param;
                return;
            case "picwgoal":
                if (_pict != null) _pict.GoalW = param;
                return;
            case "pichgoal":
                if (_pict != null) _pict.GoalH = param;
                return;
            case "picscalex":
                if (_pict != null && param > 0) _pict.ScaleX = param;
                return;
            case "picscaley":
                if (_pict != null && param > 0) _pict.ScaleY = param;
                return;

            // ---- lists
            case "listid":
                if (d == Dest.List && _listEntry != null) _listEntry.Id = param;
                else if (d == Dest.ListOverride) _overrideListId = param;
                return;
            case "levelnfc":
            case "levelnfcn":
                if (_levelEntry != null) _levelEntry.Nfc = param;
                return;
            case "levelstartat":
                if (_levelEntry != null) _levelEntry.Start = param;
                else if (d == Dest.ListOverride && _overrideLevel >= 0) _overrideStarts[_overrideLevel] = param;
                return;
            case "leveljc":
            case "leveljcn":
                if (_levelEntry != null) _levelEntry.Jc = param;
                return;
            case "levelfollow":
                if (_levelEntry != null) _levelEntry.Follow = param;
                return;
            case "levellegal":
                if (_levelEntry != null) _levelEntry.Legal = param != 0;
                return;
            case "listoverridestartat":
                return;
            case "lfolevel":
                _overrideLevel++;
                return;

            // ---- old-style (Word 6 / WordPad) numbering
            case "pnlvlblt":
                if (_state.Para.Pn != null) _state.Para.Pn.Bullet = true;
                return;
            case "pnlvlbody":
                if (_state.Para.Pn != null) _state.Para.Pn.Bullet = false;
                return;
            case "pnlvl":
                if (_state.Para.Pn != null) _state.Para.Pn.Level = MathEx.Clamp(param - 1, 0, 8);
                return;
            case "pndec":
            case "pnucrm":
            case "pnlcrm":
            case "pnucltr":
            case "pnlcltr":
            case "pnord":
            case "pncard":
                if (_state.Para.Pn != null)
                    _state.Para.Pn.Format = word switch { "pnucrm" => "upperRoman", "pnlcrm" => "lowerRoman", "pnucltr" => "upperLetter", "pnlcltr" => "lowerLetter", "pnord" => "ordinal", "pncard" => "cardinalText", _ => "decimal" };
                return;
            case "pnstart":
                if (_state.Para.Pn != null) _state.Para.Pn.Start = param;
                return;
            case "pnf":
                if (_state.Para.Pn != null) _state.Para.Pn.Font = param;
                return;

            // ---- info dates
            case "yr":
                _infoDate[0] = param;
                return;
            case "mo":
                _infoDate[1] = param;
                return;
            case "dy":
                _infoDate[2] = param;
                return;
            case "hr":
                _infoDate[3] = param;
                return;
            case "min":
                _infoDate[4] = param;
                return;
            case "sec":
                _infoDate[5] = param;
                return;

            // ---- page setup
            case "paperw":
                _docSect.Width = param;
                return;
            case "paperh":
                _docSect.Height = param;
                return;
            case "margl":
                _docSect.Left = param;
                return;
            case "margr":
                _docSect.Right = param;
                return;
            case "margt":
                _docSect.Top = param;
                return;
            case "margb":
                _docSect.Bottom = param;
                return;
            case "gutter":
                _docSect.Gutter = param;
                return;
            case "landscape":
                _docSect.Landscape = true;
                return;
            case "pgwsxn":
                _sect.Width = param;
                return;
            case "pghsxn":
                _sect.Height = param;
                return;
            case "marglsxn":
                _sect.Left = param;
                return;
            case "margrsxn":
                _sect.Right = param;
                return;
            case "margtsxn":
                _sect.Top = param;
                return;
            case "margbsxn":
                _sect.Bottom = param;
                return;
            case "guttersxn":
                _sect.Gutter = param;
                return;
            case "lndscpsxn":
                _sect.Landscape = true;
                return;
            case "headery":
                _sect.HeaderY = param;
                return;
            case "footery":
                _sect.FooterY = param;
                return;
            case "cols":
                _sect.Columns = param;
                return;
            case "colsx":
                _sect.ColumnSpace = param;
                return;
            case "titlepg":
                _sect.TitlePage = true;
                return;
            case "pgnstarts":
                _sect.PageStart = param;
                return;
            case "sbknone":
                _sect.Break = "continuous";
                return;
            case "sbkcol":
                _sect.Break = "nextColumn";
                return;
            case "sbkpage":
                _sect.Break = "nextPage";
                return;
            case "sbkeven":
                _sect.Break = "evenPage";
                return;
            case "sbkodd":
                _sect.Break = "oddPage";
                return;
        }
    }

    private RowDef CurrentRowDef()
    {
        if (!_rowDefs.TryGetValue(_rowDefDepth, out var def))
        {
            def = new RowDef();
            _rowDefs[_rowDefDepth] = def;
        }
        return def;
    }

    private void SetBorder(Func<BorderDef, BorderDef> change)
    {
        if (_border is not { } target) return;
        if (target.Side == "box")
        {
            foreach (var side in new[] { "top", "bottom", "left", "right" })
                if (target.Owner.TryGetValue(side, out var b)) target.Owner[side] = change(b);
            return;
        }
        if (target.Owner.TryGetValue(target.Side, out var current)) target.Owner[target.Side] = change(current);
    }

    private string? ColorAt(int index) => index > 0 && index < _colors.Count ? _colors[index] : null;

    // ------------------------------------------------------------------ destinations

    private static readonly HashSet<string> SkippedDestinations = new(StringComparer.Ordinal)
    {
        "header", "headerl", "headerr", "headerf", "footer", "footerl", "footerr", "footerf", "xe", "tc", "tcn", "txe", "ftnsep", "ftnsepc", "ftncn", "aftnsep",
        "aftnsepc", "aftncn", "nonshppict", "shprslt", "shptxt", "pntext", "listtext", "revtbl", "rsidtbl", "comment", "objdata", "objclass", "objname", "private",
        "template", "userprops", "docvar", "background", "pgdsctbl", "xmlnstbl", "nonesttables", "generator", "mmathPr", "themedata", "colorschememapping",
        "latentstyles", "datastore", "filetbl", "protusertbl", "atnid", "atnauthor", "annotation", "atndate", "atnref", "atnicn", "atnparent", "atrfstart",
        "atrfend", "fldtype", "datafield", "formfield", "listpicture", "pnseclvl", "panose", "falt", "fname", "blipuid", "picprop", "leveltemplateid", "wgrffmtfilter",
        "defchp", "defpap", "pgp", "factoidname", "bkmkcolf", "bkmkcoll", "passwordhash", "ud", "company", "category", "manager",
        "hlinkbase", "nofpages", "nofwords", "nofchars", "nofcharsws", "vern", "edmins", "id", "version", "printim", "buptim",
    };

    /// <summary>The first control word of a group: 1 = it started a destination (done), 0 = it started one and is processed as usual, -1 = no destination.</summary>
    private int TryDestination(string word, bool starred)
    {
        var parent = _state.Dest;
        if (parent == Dest.UprPending)
        {
            if (word == "ud")
            {
                _state.Dest = _state.UprParent;
                return 1;
            }
            SkipGroup();
            return 1;
        }
        switch (word)
        {
            case "fonttbl":
                SetDest(Dest.FontTable);
                return 1;
            case "colortbl":
                SetDest(Dest.ColorTable);
                _colors.Clear();
                return 1;
            case "stylesheet":
                SetDest(Dest.StyleSheet);
                return 1;
            case "info":
                SetDest(Dest.Info);
                return 1;
            case "title":
            case "subject":
            case "author":
            case "keywords":
            case "operator":
            case "doccomm":
                if (parent != Dest.Info) return -1;
                SetDest(Dest.InfoField);
                _state.InfoKey = word;
                _info.Clear();
                return 1;
            case "creatim":
            case "revtim":
                if (parent != Dest.Info) return -1;
                SetDest(Dest.InfoTime);
                _state.InfoKey = word;
                _infoDate = new int[6];
                return 1;
            case "pict":
                SetDest(Dest.Pict);
                _pict = new PictState();
                return 1;
            case "shppict":
                SetDest(parent is Dest.Skip ? Dest.Skip : Dest.Ignore);
                return 1;
            case "shp":
            case "shpinst":
            case "sp":
            case "object":
            case "do":
                SetDest(Dest.Ignore);
                return 1;
            case "sn":
                SetDest(Dest.ShapeName);
                _shapeText.Clear();
                return 1;
            case "sv":
                SetDest(_shapeName == "pib" ? Dest.Ignore : Dest.ShapeValue);
                _shapeText.Clear();
                return 1;
            case "result":
                SetDest(Dest.Text);
                return 1;
            case "field":
                SetDest(Dest.Field);
                _fields.Add(new FieldState());
                return 1;
            case "fldinst":
                SetDest(Dest.FieldInst);
                return 1;
            case "fldrslt":
                SetDest(Dest.FieldResult);
                if (_fields.Count > 0) _fields[_fields.Count - 1].InResult = true;
                return 1;
            case "bkmkstart":
                SetDest(Dest.BookmarkStart);
                _bookmarkName.Clear();
                return 1;
            case "bkmkend":
                SetDest(Dest.BookmarkEnd);
                _bookmarkName.Clear();
                return 1;
            case "listtable":
                SetDest(Dest.ListTable);
                return 1;
            case "list":
                if (parent != Dest.ListTable) return -1;
                SetDest(Dest.List);
                _listEntry = new RtfList();
                return 1;
            case "listlevel":
                if (parent != Dest.List) return -1;
                SetDest(Dest.ListLevel);
                _levelEntry = new RtfLevel();
                return 1;
            case "leveltext":
                if (parent != Dest.ListLevel) return -1;
                SetDest(Dest.LevelText);
                _levelText.Clear();
                return 1;
            case "levelnumbers":
                if (parent != Dest.ListLevel) return -1;
                SetDest(Dest.LevelNumbers);
                _levelText.Clear();
                return 1;
            case "listname":
            case "liststyleid":
                SetDest(Dest.Skip);
                SkipGroup();
                return 1;
            case "listoverridetable":
                SetDest(Dest.ListOverrideTable);
                return 1;
            case "listoverride":
                if (parent != Dest.ListOverrideTable) return -1;
                SetDest(Dest.ListOverride);
                _overrideLs = 0;
                _overrideListId = 0;
                _overrideLevel = -1;
                _overrideStarts = new Dictionary<int, int>();
                return 1;
            case "footnote":
                SetDest(Dest.Footnote);
                StartFootnote();
                return 1;
            case "pn":
                SetDest(Dest.Pn);
                // The numbering belongs to the paragraph around this group.
                var pn = new PnInfo();
                _state.Para.Pn = pn;
                if (_stack.Count > 0) _stack.Peek().Para.Pn = pn;
                return 1;
            case "pntxtb":
                SetDest(Dest.PnTextBefore);
                return 1;
            case "pntxta":
                SetDest(Dest.PnTextAfter);
                return 1;
            case "nesttableprops":
                SetDest(Dest.NestTableProps);
                return 1;
            case "upr":
                _state.UprParent = parent;
                SetDest(Dest.UprPending);
                return 1;
        }
        if (SkippedDestinations.Contains(word))
        {
            SkipGroup();
            return 1;
        }
        if (parent == Dest.FontTable)
        {
            // An entry in braces: "{\f1\fswiss\fcharset0 Arial;}".
            SetDest(Dest.FontEntry);
            return 0;
        }
        if (parent == Dest.StyleSheet)
        {
            // An entry: "{\s1\ql … heading 1;}", "{\*\cs10 \additive Default Paragraph Font;}".
            SetDest(Dest.StyleEntry);
            _styleEntry = new StyleInfo();
            _styleName.Clear();
            return 0;
        }
        return -1;
    }

    private void EndDestination(GroupState ended)
    {
        switch (ended.Dest)
        {
            case Dest.FontEntry:
                EndFontEntry();
                break;
            case Dest.FontTable:
                if (_fontEntry != null) EndFontEntry();
                break;
            case Dest.StyleEntry:
                if (_styleEntry != null)
                {
                    _styleEntry.Char = ended.Char.Clone();
                    _styleEntry.Outline = ended.Para.Outline;
                    EndStyleEntry();
                }
                break;
            case Dest.InfoField:
                if (_whole)
                {
                    var text = _info.ToString().Trim();
                    switch (ended.InfoKey)
                    {
                        case "title":
                            _doc.Properties.Title = text;
                            break;
                        case "subject":
                            _doc.Properties.Subject = text;
                            break;
                        case "author":
                            _doc.Properties.Creator = text;
                            break;
                        case "keywords":
                            _doc.Properties.Keywords = text;
                            break;
                        case "operator":
                            _doc.Properties.LastModifiedBy = text;
                            break;
                        case "doccomm":
                            _doc.Properties.Description = text;
                            break;
                    }
                }
                break;
            case Dest.InfoTime:
                if (_whole && _infoDate[0] > 0)
                {
                    try
                    {
                        var date = new DateTime(_infoDate[0], MathEx.Clamp(_infoDate[1], 1, 12), MathEx.Clamp(_infoDate[2], 1, 31), MathEx.Clamp(_infoDate[3], 0, 23),
                            MathEx.Clamp(_infoDate[4], 0, 59), MathEx.Clamp(_infoDate[5], 0, 59), DateTimeKind.Local);
                        if (ended.InfoKey == "creatim") _doc.Properties.Created = date.ToUniversalTime();
                        else _doc.Properties.Modified = date.ToUniversalTime();
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        // An impossible date: ignored.
                    }
                }
                break;
            case Dest.Pict:
                EndPicture();
                break;
            case Dest.FieldInst:
                EndFieldInstruction();
                break;
            case Dest.Field:
                EndField();
                break;
            case Dest.BookmarkStart:
            case Dest.BookmarkEnd:
                Bookmark(ended.Dest == Dest.BookmarkStart, _bookmarkName.ToString().Trim());
                break;
            case Dest.ListLevel:
                if (_listEntry != null && _levelEntry != null) _listEntry.Levels.Add(_levelEntry);
                _levelEntry = null;
                break;
            case Dest.LevelText:
                if (_levelEntry != null) _levelEntry.Text = _levelText.ToString();
                break;
            case Dest.LevelNumbers:
                if (_levelEntry != null) _levelEntry.Numbers = _levelText.ToString();
                break;
            case Dest.List:
                if (_listEntry != null) _listDefs[_listEntry.Id] = _listEntry;
                _listEntry = null;
                break;
            case Dest.ListOverride:
                if (_overrideLs > 0) _overrides[_overrideLs] = (_overrideListId, _overrideStarts);
                break;
            case Dest.Footnote:
                EndFootnote();
                break;
            case Dest.ShapeName:
                _shapeName = _shapeText.ToString().Trim();
                break;
        }
    }

    private void EndFontEntry()
    {
        if (_fontEntry == null) return;
        _fontEntry.Name = _fontName.ToString().Trim();
        var lower = _fontEntry.Name.ToLowerInvariant();
        if (lower is "symbol" or "wingdings" or "wingdings 2" or "wingdings 3" or "webdings" or "zapf dingbats") _fontEntry.Symbol = true;
        if (_fontEntryIndex >= 0) _fonts[_fontEntryIndex] = _fontEntry;
        _fontEntry = null;
        _fontEntryIndex = -1;
        _fontName.Clear();
    }

    private void EndStyleEntry()
    {
        if (_styleEntry == null) return;
        _styleEntry.Name = _styleName.ToString().Trim();
        if (_styleEntry.Type == "character") _charStyles[_styleEntry.Index] = _styleEntry;
        else if (_styleEntry.Type == "paragraph") _paraStyles[_styleEntry.Index] = _styleEntry;
        _styleName.Clear();
        _styleEntry = null;
    }

    // ------------------------------------------------------------------ styles and formatting

    /// <summary>Inkwell's canonical style id for an RTF paragraph style (by its name).</summary>
    private string ParagraphStyle(int index)
    {
        if (!_paraStyles.TryGetValue(index, out var s)) return "Normal";
        var name = s.Name.ToLowerInvariant();
        if (name.StartsWith("heading ", StringComparison.Ordinal) && int.TryParse(name.Substring(8), NumberStyles.Integer, CultureInfo.InvariantCulture, out var level) && level is >= 1 and <= 9)
            return "Heading" + level.ToString(CultureInfo.InvariantCulture);
        return name switch
        {
            "title" => "Title",
            "subtitle" => "Subtitle",
            "quote" or "intense quote" or "block text" => "Quote",
            "list paragraph" => "ListParagraph",
            "caption" => "Caption",
            "footnote text" => "FootnoteText",
            "endnote text" => "EndnoteText",
            "html preformatted" or "plain text" or "code" => "Code",
            _ when s.Outline is >= 0 and <= 8 => "Heading" + (s.Outline.Value + 1).ToString(CultureInfo.InvariantCulture),
            _ => "Normal",
        };
    }

    private readonly Dictionary<string, RunProps> _runCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (bool? Bold, bool? Italic, string? Color)> _targetStyle = new(StringComparer.Ordinal);

    /// <summary>Text in a right-to-left script (Word marks such runs w:rtl, whatever \rtlch / \ltrch say).</summary>
    private static bool HasRtlText(string text) => text.Any(ch => ch is >= '\u0590' and <= '\u08FF' or >= '\uFB1D' and <= '\uFDFF' or >= '\uFE70' and <= '\uFEFF');

    private RunProps RunPropsFor(CharFmt c, ParaFmt para, Hyperlink? link, bool rtlText = false)
    {
        var key = c.Key() + "#" + para.Style.ToString(CultureInfo.InvariantCulture) + "#" + (link != null) + "#" + rtlText;
        if (_runCache.TryGetValue(key, out var cached)) return cached;
        var r = new RunProps();
        var charStyle = _charStyles.TryGetValue(c.CharStyle, out var cs) ? cs : null;
        var charName = charStyle?.Name.ToLowerInvariant();
        var bold = c.Bold || charName == "strong";
        var italic = c.Italic || charName == "emphasis";
        if (link != null || charName == "hyperlink") r.StyleId = _styles.Get("Hyperlink");
        if (_keep)
        {
            var canonical = ParagraphStyle(para.Style);
            var target = TargetStyle(canonical);
            if (bold != (target.Bold ?? false)) r.Bold = bold;
            if (italic != (target.Italic ?? false)) r.Italic = italic;
            if (c.Underline != null) r.Underline = c.Underline;
            if (c.Strike) r.Strike = true;
            if (c.DoubleStrike) r.DoubleStrike = true;
            if (c.Caps) r.Caps = true;
            if (c.SmallCaps) r.SmallCaps = true;
            var font = _fonts.TryGetValue(c.Font >= 0 ? c.Font : _defaultFont, out var f) ? f : null;
            if (font != null && font.Name.Length > 0 && !font.Symbol)
            {
                r.SetLatinFont(font.Name);
                var csFont = _fonts.TryGetValue(c.FontCs, out var fc) && fc.Name.Length > 0 && !fc.Symbol ? fc.Name : font.Name;
                r.SetComplexFont(csFont);
            }
            r.Size = c.Size;
            r.SizeCs = c.SizeCs ?? c.Size;
            if (ColorAt(c.Color) is { } color) r.Color = color;
            else if (link == null && canonical != "Normal" && target.Color != null && target.Color != "auto") r.Color = "auto";
            var highlight = ColorAt(c.Highlight);
            var background = ColorAt(c.Background);
            if (highlight != null)
            {
                var name = HighlightName(highlight);
                if (name != null) r.Highlight = name;
                else r.ShadingFill = highlight;
            }
            else if (background != null) r.ShadingFill = background;
            if (c.Lang > 0 && c.Lang != _defaultLang && c.Lang != 1024 && CultureName(c.Lang) is { } lang) r.Lang = lang;
            if (rtlText && c.LangBidi > 0 && c.LangBidi != 1024 && CultureName(c.LangBidi) is { } bidiLang) r.LangBidi = bidiLang;
        }
        else
        {
            // Matching the destination: formatting the RTF style gives (headings are bold…) is the style's, not direct.
            var style = _paraStyles.TryGetValue(para.Style, out var ps) ? ps.Char : null;
            if (bold && style?.Bold != true) r.Bold = true;
            if (italic && style?.Italic != true) r.Italic = true;
            if (c.Underline != null && link == null && charName != "hyperlink" && style?.Underline == null) r.Underline = c.Underline;
            if (c.Strike) r.Strike = true;
            if (c.DoubleStrike) r.DoubleStrike = true;
        }
        if (c.VertAlign != null) r.VertAlign = c.VertAlign;
        if (c.Hidden) r.Vanish = true;
        if (rtlText) r.Rtl = true;
        _runCache[key] = r;
        return r;
    }

    private (bool? Bold, bool? Italic, string? Color) TargetStyle(string canonical)
    {
        if (_targetStyle.TryGetValue(canonical, out var t)) return t;
        var id = _styles.Get(canonical);
        t = (StyleChain.RunValue(_doc.Styles, id, x => x.Bold), StyleChain.RunValue(_doc.Styles, id, x => x.Italic), StyleChain.RunString(_doc.Styles, id, x => x.Color));
        _targetStyle[canonical] = t;
        return t;
    }

    private static string? CultureName(int lcid)
    {
        try
        {
            var name = CultureInfo.GetCultureInfo(lcid).Name;
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string? HighlightName(string hex) => hex switch
    {
        "FFFF00" => "yellow",
        "00FF00" => "green",
        "00FFFF" => "cyan",
        "FF00FF" => "magenta",
        "0000FF" => "blue",
        "FF0000" => "red",
        "000080" => "darkBlue",
        "008080" => "darkCyan",
        "008000" => "darkGreen",
        "800080" => "darkMagenta",
        "800000" => "darkRed",
        "808000" => "darkYellow",
        "808080" => "darkGray",
        "C0C0C0" => "lightGray",
        "000000" => "black",
        _ => null,
    };

    // ------------------------------------------------------------------ paragraphs

    private void FinishParagraph()
    {
        FlushText();
        var p = new Paragraph { Inlines = _story.Inlines };
        _story.Inlines = new List<Inline>();
        ApplyParagraph(p, _state.Para);
        p.Normalize();
        AddParagraph(p, _state.Para.Depth);
    }

    private void ApplyParagraph(Paragraph p, ParaFmt f)
    {
        var canonical = ParagraphStyle(f.Style);
        int? numId = null;
        var level = f.Ilvl;
        if (f.Ls > 0) numId = ListNum(f.Ls);
        else if (f.Pn != null)
        {
            var key = f.Pn.Key();
            if (_lastWasPn && key == _lastPnKey) numId = _lastPnNum;
            else
            {
                numId = PnList(f.Pn);
                _lastPnKey = key;
                _lastPnNum = numId.Value;
            }
            level = f.Pn.Level;
        }
        _lastWasPn = f.Pn != null && f.Ls == 0;
        if (canonical == "Normal" && numId != null) canonical = "ListParagraph";
        if (_stories.Count > 0 && canonical == "Normal") canonical = _story.Kind == NoteKind.Footnote ? "FootnoteText" : "EndnoteText";
        p.Props.StyleId = _styles.Get(canonical);
        if (numId != null) p.Props.SetNumbering(numId, level);
        if (f.KeepNext) p.Props.KeepNext = true;
        if (f.KeepLines) p.Props.KeepLines = true;
        if (f.PageBreakBefore) p.Props.PageBreakBefore = true;
        if (f.Rtl) p.Props.Bidi = true;
        if (f.Outline != null) p.Props.OutlineLevel = f.Outline;
        if (f.Borders != null)
        {
            foreach (var (side, border) in f.Borders)
                if (_keep || side == "bottom") p.Props.SetBorder(side, border);
        }
        if (!_keep) return;
        if (f.Align != null)
        {
            // RTF alignment is physical; in a right-to-left paragraph left / right are its end / start.
            p.Props.Justification = f.Rtl ? f.Align switch { "left" => "end", "right" => "start", _ => f.Align } : f.Align;
        }
        else
        {
            p.Props.Justification = f.Rtl ? "start" : "left";
        }
        var start = f.LeftLogical ?? (f.Rtl ? f.Right : f.Left);
        var end = f.RightLogical ?? (f.Rtl ? f.Left : f.Right);
        if (numId == null || start != 0) p.Props.IndentStart = start;
        if (end != 0 || numId == null) p.Props.IndentEnd = end;
        if (numId == null || f.FirstLine != 0) p.Props.FirstLine = f.FirstLine;
        p.Props.SpacingBefore = f.SpaceBefore;
        p.Props.SpacingAfter = f.SpaceAfter;
        if (f.AutoBefore) p.Props.BeforeAutospacing = true;
        if (f.AutoAfter) p.Props.AfterAutospacing = true;
        if (f.Line == 0)
        {
            p.Props.SpacingLine = 240;
            p.Props.SpacingLineRule = "auto";
        }
        else if (f.LineMultiple)
        {
            p.Props.SpacingLine = Math.Abs(f.Line);
            p.Props.SpacingLineRule = "auto";
        }
        else
        {
            p.Props.SpacingLine = Math.Abs(f.Line);
            p.Props.SpacingLineRule = f.Line < 0 ? "exact" : "atLeast";
        }
    }

    private void AddParagraph(Paragraph p, int depth)
    {
        if (depth <= 0)
        {
            CloseTables(0);
            _story.Blocks.Add(p);
            if (_stories.Count == 0) _lastBodyParagraph = p;
            return;
        }
        CloseTables(depth);
        while (_story.Tables.Count < depth) _story.Tables.Add(new TableBuilder());
        _story.Tables[depth - 1].CellBlocks.Add(p);
    }

    private void EndCell(int depth)
    {
        FlushText();
        var p = new Paragraph { Inlines = _story.Inlines };
        _story.Inlines = new List<Inline>();
        ApplyParagraph(p, _state.Para);
        p.Normalize();
        AddParagraph(p, depth);
        var tb = _story.Tables[depth - 1];
        tb.Cells.Add(tb.CellBlocks);
        tb.CellBlocks = new List<Block>();
    }

    private void EndRow(int depth)
    {
        FlushText();
        if (_story.Inlines.Count > 0) EndCell(depth);
        CloseTables(depth);
        while (_story.Tables.Count < depth) _story.Tables.Add(new TableBuilder());
        var tb = _story.Tables[depth - 1];
        if (tb.CellBlocks.Count > 0)
        {
            tb.Cells.Add(tb.CellBlocks);
            tb.CellBlocks = new List<Block>();
        }
        var def = _rowDefs.TryGetValue(depth, out var d) ? d : new RowDef();
        if (tb.Cells.Count > 0) tb.Rows.Add((tb.Cells, def));
        tb.Cells = new List<List<Block>>();
    }

    private void CloseTables(int depth)
    {
        while (_story.Tables.Count > depth)
        {
            var tb = _story.Tables[_story.Tables.Count - 1];
            _story.Tables.RemoveAt(_story.Tables.Count - 1);
            var table = BuildTable(tb);
            if (table == null) continue;
            if (_story.Tables.Count == 0) _story.Blocks.Add(table);
            else _story.Tables[_story.Tables.Count - 1].CellBlocks.Add(table);
        }
    }

    private Model.Table? BuildTable(TableBuilder tb)
    {
        if (tb.CellBlocks.Count > 0 || tb.Cells.Count > 0)
        {
            if (tb.CellBlocks.Count > 0) tb.Cells.Add(tb.CellBlocks);
            tb.Rows.Add((tb.Cells, _rowDefs.TryGetValue(1, out var d) ? d : new RowDef()));
            tb.Cells = new List<List<Block>>();
            tb.CellBlocks = new List<Block>();
        }
        if (tb.Rows.Count == 0) return null;
        // Every row's cell boundaries make the grid.
        var rows = new List<(List<List<Block>> Cells, RowDef Def, List<CellDef> Defs)>();
        var bounds = new SortedSet<int>();
        foreach (var (cells, def) in tb.Rows)
        {
            var defs = def.Cells.ToList();
            var left = def.Left;
            var lastRight = defs.Count > 0 ? defs[defs.Count - 1].Right : left;
            var avg = defs.Count > 0 ? Math.Max(360, (lastRight - left) / defs.Count) : 1440;
            while (defs.Count < cells.Count)
            {
                lastRight += avg;
                defs.Add(new CellDef { Right = lastRight });
            }
            // Boundaries must increase.
            var prev = left;
            foreach (var cd in defs)
            {
                if (cd.Right <= prev) cd.Right = prev + 60;
                prev = cd.Right;
            }
            bounds.Add(left);
            foreach (var cd in defs) bounds.Add(cd.Right);
            rows.Add((cells, def, defs));
        }
        var list = bounds.ToList();
        var index = new Dictionary<int, int>();
        for (var k = 0; k < list.Count; k++) index[list[k]] = k;
        var table = new Model.Table();
        for (var k = 0; k + 1 < list.Count; k++) table.Grid.Add(list[k + 1] - list[k]);
        if (table.Grid.Count == 0) table.Grid.Add(Content.TextWidth(_doc));
        var anyHeader = false;
        foreach (var (cells, def, defs) in rows)
        {
            var tr = new Model.TableRow();
            if (def.Header)
            {
                tr.Props.IsHeader = true;
                anyHeader = true;
            }
            if (def.CantSplit) tr.Props.CantSplit = true;
            tr.Props.GridBefore = index[def.Left];
            var prev = def.Left;
            Model.TableCell? lastCell = null;
            for (var k = 0; k < defs.Count; k++)
            {
                var cd = defs[k];
                var span = index[cd.Right] - index[prev];
                if (cd.HMerge && !cd.HMergeFirst && lastCell != null)
                {
                    lastCell.Props.GridSpan += span;
                    lastCell.Props.Width = new TableWidth("dxa", lastCell.Props.Width.Value + cd.Right - prev);
                    prev = cd.Right;
                    continue;
                }
                var cell = new Model.TableCell();
                cell.Props.Width = new TableWidth("dxa", cd.Right - prev);
                if (span > 1) cell.Props.GridSpan = span;
                if (cd.VMergeFirst) cell.Props.VerticalMerge = "restart";
                else if (cd.VMerge) cell.Props.VerticalMerge = "continue";
                if (_keep)
                {
                    if (cd.VAlign != null) cell.Props.VerticalAlignment = cd.VAlign;
                    var fill = ColorAt(cd.Shading);
                    if (fill == null && cd.ShadingPercent > 0)
                    {
                        var gray = (int)Math.Round(255 - MathEx.Clamp(cd.ShadingPercent, 0, 10000) / 10000.0 * 255);
                        fill = gray.ToString("X2", CultureInfo.InvariantCulture) + gray.ToString("X2", CultureInfo.InvariantCulture) + gray.ToString("X2", CultureInfo.InvariantCulture);
                    }
                    if (fill != null) cell.Props.ShadingFill = fill;
                    foreach (var (side, border) in cd.Borders) cell.Props.SetBorder(side, border);
                }
                var blocks = k < cells.Count ? cells[k] : new List<Block>();
                if (blocks.Count == 0 || blocks[blocks.Count - 1] is not Paragraph) blocks.Add(new Paragraph());
                cell.Blocks.AddRange(blocks);
                tr.AddCell(cell);
                lastCell = cell;
                prev = cd.Right;
            }
            tr.Props.GridAfter = list.Count - 1 - index[prev];
            table.AddRow(tr);
        }
        if (_keep)
        {
            var first = rows[0].Def;
            table.Props.Width = new TableWidth("dxa", table.Grid.Sum());
            if (first.Align != null) table.Props.Alignment = first.Align;
            else if (first.Left != 0) table.Props.Indent = new TableWidth("dxa", first.Left);
            foreach (var (side, border) in first.Borders) table.Props.SetBorder(side, border);
        }
        else
        {
            table.Props.StyleId = _styles.Get("InkwellTable");
            table.Props.Width = new TableWidth("pct", 5000);
        }
        table.Props.Look = (anyHeader, false, false, false, true, false);
        return table;
    }

    private void EndSection()
    {
        FlushText();
        if (!_whole || _stories.Count > 0)
        {
            FinishParagraph();
            return;
        }
        var needParagraph = _story.Inlines.Count > 0 || _lastBodyParagraph == null || _lastBodyParagraph.Props.Section != null || _story.Tables.Count > 0 ||
                            _state.Para.Depth > 0 || (_story.Blocks.Count > 0 && !ReferenceEquals(_story.Blocks[_story.Blocks.Count - 1], _lastBodyParagraph));
        if (needParagraph)
        {
            var inTable = _state.Para.InTable;
            _state.Para.InTable = false;
            FinishParagraph();
            _state.Para.InTable = inTable;
        }
        if (_lastBodyParagraph != null) _lastBodyParagraph.Props.Section = BuildSection(_sectSeen ? _sect : _docSect);
    }

    private static SectProps BuildSection(SectFmt f)
    {
        var s = SectProps.CreateDefault(false);
        if (f.Width is > 0) s.PageWidth = f.Width.Value;
        if (f.Height is > 0) s.PageHeight = f.Height.Value;
        s.SetMargins(f.Top ?? s.MarginTop, f.Right ?? s.MarginRight, f.Bottom ?? s.MarginBottom, f.Left ?? s.MarginLeft, f.HeaderY ?? s.HeaderDistance, f.FooterY ?? s.FooterDistance, f.Gutter ?? 0);
        if (f.Landscape)
        {
            s.Landscape = true;
            if (s.PageWidth < s.PageHeight)
            {
                var w = s.PageWidth;
                s.PageWidth = s.PageHeight;
                s.PageHeight = w;
            }
        }
        if (f.Columns is > 1) s.ColumnCount = f.Columns.Value;
        if (f.ColumnSpace is > 0) s.ColumnSpace = f.ColumnSpace.Value;
        if (f.TitlePage) s.TitlePage = true;
        if (f.PageStart != null) s.PageNumberStart = f.PageStart;
        s.BreakType = f.Break;
        return s;
    }

    // ------------------------------------------------------------------ lists

    private int? ListNum(int ls)
    {
        if (_numIds.TryGetValue(ls, out var cached)) return cached;
        int? numId = null;
        if (_overrides.TryGetValue(ls, out var ov) && _listDefs.TryGetValue(ov.ListId, out var list))
        {
            var levels = new Dictionary<int, XElement>();
            var anyBullet = false;
            for (var l = 0; l < list.Levels.Count && l < 9; l++)
            {
                var lvl = list.Levels[l];
                var format = lvl.Nfc switch
                {
                    0 => "decimal",
                    1 => "upperRoman",
                    2 => "lowerRoman",
                    3 => "upperLetter",
                    4 => "lowerLetter",
                    5 => "ordinal",
                    6 => "cardinalText",
                    7 => "ordinalText",
                    22 => "decimalZero",
                    23 => "bullet",
                    255 => "none",
                    _ => "decimal",
                };
                var text = LevelText(lvl);
                if (format == "bullet")
                {
                    text = ListFactory.NormalizeBullet(text.Length > 0 ? text : "•");
                    if (l == 0) anyBullet = true;
                }
                var start = ov.Starts.TryGetValue(l, out var s) ? s : lvl.Start;
                var hanging = lvl.FirstLine is < 0 ? -lvl.FirstLine.Value : 360;
                var element = ListFactory.Level(l, format, text, start, lvl.Left ?? ListFactory.LevelIndent(l), hanging,
                    suffix: lvl.Follow switch { 1 => "space", 2 => "nothing", _ => "tab" }, jc: lvl.Jc switch { 1 => "center", 2 => "right", _ => "left" });
                if (lvl.Legal) element.Element(Ns.W + "numFmt")!.AddAfterSelf(new XElement(Ns.W + "isLgl"));
                levels[l] = element;
            }
            numId = _lists.AddDefinition(ListFactory.Definition(levels, anyBullet));
        }
        _numIds[ls] = numId;
        return numId;
    }

    /// <summary>A level's label pattern from RTF's \leveltext (length, then text with level placeholders) and \levelnumbers (their positions).</summary>
    private static string LevelText(RtfLevel level)
    {
        var raw = level.Text;
        if (raw.Length == 0) return "";
        var length = Math.Min(raw[0], raw.Length - 1);
        var body = raw.Substring(1, Math.Max(0, length));
        var positions = new HashSet<int>(level.Numbers.TrimEnd(';').Select(c => (int)c));
        var sb = new StringBuilder();
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (positions.Contains(i + 1) && c < 9) sb.Append('%').Append((char)('1' + c));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private int PnList(PnInfo pn)
    {
        if (pn.Bullet)
        {
            var bulletText = pn.Before.Trim();
            var font = _fonts.TryGetValue(pn.Font, out var f) ? f : null;
            if (font is { Symbol: true } && bulletText.Length == 1 && bulletText[0] < 0x100) bulletText = ((char)(0xF000 + bulletText[0])).ToString();
            return _lists.NewList(ListKind.Bullet, pn.Level, bullet: ListFactory.NormalizeBullet(bulletText.Length > 0 ? bulletText : "•"));
        }
        var levels = new Dictionary<int, XElement>
        {
            [pn.Level] = ListFactory.Level(pn.Level, pn.Format, pn.Before + "%" + (pn.Level + 1).ToString(CultureInfo.InvariantCulture) + pn.After, pn.Start),
        };
        return _lists.AddDefinition(ListFactory.Definition(levels, false), pn.Start, pn.Level);
    }

    // ------------------------------------------------------------------ fields

    private void EndFieldInstruction()
    {
        if (_fields.Count == 0) return;
        var field = _fields[_fields.Count - 1];
        var instruction = field.Instruction.ToString().Trim();
        var code = FieldCode.Parse(instruction);
        switch (code.Type)
        {
            case "HYPERLINK":
            {
                var url = code.FirstArgument;
                var anchor = code.SwitchValue("\\l");
                if (string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(anchor)) break;
                field.Link = new Hyperlink
                {
                    Url = string.IsNullOrWhiteSpace(url) ? null : url,
                    Anchor = string.IsNullOrWhiteSpace(anchor) ? null : anchor,
                    Tooltip = code.SwitchValue("\\o"),
                };
                break;
            }
            case "SYMBOL":
                if (int.TryParse(code.FirstArgument, NumberStyles.Integer, CultureInfo.InvariantCulture, out var symbol))
                {
                    field.SymbolCode = symbol;
                    field.SymbolFont = code.SwitchValue("\\f");
                }
                break;
        }
    }

    private void EndField()
    {
        if (_fields.Count == 0) return;
        var field = _fields[_fields.Count - 1];
        _fields.RemoveAt(_fields.Count - 1);
        if (!field.ResultEmitted && field.SymbolCode is { } code && code is > 0 and < 0x10000)
        {
            var font = field.SymbolFont ?? "Symbol";
            var symbol = font.Equals("Symbol", StringComparison.OrdinalIgnoreCase) || font.StartsWith("Wingdings", StringComparison.OrdinalIgnoreCase) || font.Equals("Webdings", StringComparison.OrdinalIgnoreCase);
            var inline = new SymbolChar { Font = font, Char = (char)(symbol && code < 0x100 ? 0xF000 + code : code) };
            inline.Props = RunPropsFor(_state.Char, _state.Para, CurrentLink()).Clone();
            AddInline(inline);
        }
    }

    // ------------------------------------------------------------------ bookmarks

    private void Bookmark(bool start, string name)
    {
        if (name.Length == 0) return;
        if (!IsTextDest) return;
        if (!_whole && _existingBookmarks.Contains(name)) return;
        if (start)
        {
            if (_bookmarks.ContainsKey(name)) return;
            var id = _nextBookmarkId++;
            _bookmarks[name] = id;
            AddInline(new BookmarkMarker { Id = id, Name = name, IsStart = true });
        }
        else if (_bookmarks.TryGetValue(name, out var id))
        {
            AddInline(new BookmarkMarker { Id = id, IsStart = false });
        }
    }

    // ------------------------------------------------------------------ footnotes

    private void FootnoteMark()
    {
        if (_stories.Count > 0)
        {
            var mark = new NoteMark { Kind = _story.Kind };
            mark.Props.StyleId = _styles.Get(_story.Kind == NoteKind.Footnote ? "FootnoteReference" : "EndnoteReference");
            AddInline(mark);
            return;
        }
        if (!IsTextDest) return;
        var reference = NoteFactory.Reference(_styles, NoteKind.Footnote, -1);
        AddInline(reference);
        _story.PendingReference = reference;
    }

    private void StartFootnote()
    {
        FlushText();
        _stories.Push(_story);
        _story = new StoryContext();
    }

    private void EndFootnote()
    {
        FlushText();
        if (_story.Inlines.Count > 0) FinishParagraph();
        CloseTables(0);
        var note = _story;
        _story = _stories.Pop();
        var kind = note.Kind;
        foreach (var p in note.Blocks.OfType<Paragraph>())
        {
            var inlines = p.Inlines;
            var markIndex = inlines.FindIndex(x => x is NoteMark);
            if (markIndex > 0) inlines.RemoveRange(0, markIndex);
            foreach (var m in inlines.OfType<NoteMark>()) m.Kind = kind;
        }
        var created = NoteFactory.Create(_doc, _styles, kind, note.Blocks);
        _result.AddedNotes.Add(created);
        var reference = _story.PendingReference;
        _story.PendingReference = null;
        if (reference != null && reference.Id == -1)
        {
            reference.Id = created.Id;
            reference.Kind = kind;
            reference.Props.StyleId = _styles.Get(kind == NoteKind.Footnote ? "FootnoteReference" : "EndnoteReference");
        }
        else
        {
            AddInline(NoteFactory.Reference(_styles, kind, created.Id));
        }
    }

    // ------------------------------------------------------------------ pictures

    private void EndPicture()
    {
        var pict = _pict;
        _pict = null;
        if (pict == null || pict.Data.Count == 0) return;
        var data = pict.Data.ToArray();
        byte[]? bytes = pict.Type switch
        {
            "pngblip" or "jpegblip" or "emfblip" => data,
            "wmetafile" => PlaceableWmf(data, pict),
            "dibitmap" => BitmapFile(data),
            _ => null,
        };
        bool bitmap = pict.Type is "pngblip" or "jpegblip" or "dibitmap" or "wbitmap";
        long Twips(int goal, int size, int scale)
        {
            double t = goal > 0 ? goal : bitmap ? size * 15.0 : size * 1440.0 / 2540.0;
            return (long)Math.Round(t * scale / 100.0);
        }
        var w = Twips(pict.GoalW, pict.W, pict.ScaleX) * Pictures.EmuPerTwip;
        var h = Twips(pict.GoalH, pict.H, pict.ScaleY) * Pictures.EmuPerTwip;
        var drawing = bytes == null ? null : Pictures.Create(_doc, bytes, null, 0, w > 0 ? w : null, h > 0 ? h : null);
        if (drawing == null)
        {
            _result.ImagesSkipped++;
            Warn("Pictures in a format Inkwell can't read were left out.");
            return;
        }
        _result.ImagesImported++;
        drawing.Link = CurrentLink();
        AddInline(drawing);
    }

    private void Warn(string text)
    {
        if (!_result.Warnings.Contains(text)) _result.Warnings.Add(text);
    }

    /// <summary>A Windows metafile from RTF (which has no placeable header) with the header Word's files have, sized from its own extents.</summary>
    private static byte[]? PlaceableWmf(byte[] wmf, PictState pict)
    {
        if (wmf.Length < 18 || !(wmf[0] is 1 or 2) || wmf[1] != 0 || wmf[2] != 9 || wmf[3] != 0) return null;
        short ExtX = 0, ExtY = 0, OrgX = 0, OrgY = 0;
        var pos = 18;
        while (pos + 6 <= wmf.Length)
        {
            var size = wmf[pos] | (wmf[pos + 1] << 8) | (wmf[pos + 2] << 16) | (wmf[pos + 3] << 24);
            var function = wmf[pos + 4] | (wmf[pos + 5] << 8);
            if (size < 3 || pos + size * 2 > wmf.Length) break;
            if (function == 0x020C && pos + 10 <= wmf.Length)
            {
                ExtY = (short)(wmf[pos + 6] | (wmf[pos + 7] << 8));
                ExtX = (short)(wmf[pos + 8] | (wmf[pos + 9] << 8));
            }
            else if (function == 0x020B && pos + 10 <= wmf.Length)
            {
                OrgY = (short)(wmf[pos + 6] | (wmf[pos + 7] << 8));
                OrgX = (short)(wmf[pos + 8] | (wmf[pos + 9] << 8));
            }
            if (function == 0) break;
            pos += size * 2;
        }
        double widthInches = pict.GoalW > 0 ? pict.GoalW / 1440.0 : pict.W > 0 ? pict.W / 2540.0 : 0;
        if (ExtX == 0 || ExtY == 0 || widthInches <= 0) return null;
        var inch = (int)Math.Round(Math.Abs(ExtX) / widthInches);
        if (inch <= 0 || inch > 65535) return null;
        var header = new byte[22];
        void Put16(int at, int v)
        {
            header[at] = (byte)(v & 0xFF);
            header[at + 1] = (byte)((v >> 8) & 0xFF);
        }
        header[0] = 0xD7;
        header[1] = 0xCD;
        header[2] = 0xC6;
        header[3] = 0x9A;
        Put16(6, OrgX);
        Put16(8, OrgY);
        Put16(10, OrgX + ExtX);
        Put16(12, OrgY + ExtY);
        Put16(14, inch);
        var checksum = 0;
        for (var k = 0; k < 20; k += 2) checksum ^= header[k] | (header[k + 1] << 8);
        Put16(20, checksum);
        var result = new byte[header.Length + wmf.Length];
        Buffer.BlockCopy(header, 0, result, 0, header.Length);
        Buffer.BlockCopy(wmf, 0, result, header.Length, wmf.Length);
        return result;
    }

    /// <summary>A device-independent bitmap (as RTF stores it) with a BMP file header.</summary>
    private static byte[]? BitmapFile(byte[] dib)
    {
        if (dib.Length < 40) return null;
        var headerSize = dib[0] | (dib[1] << 8) | (dib[2] << 16) | (dib[3] << 24);
        if (headerSize < 12 || headerSize > dib.Length) return null;
        var bitCount = headerSize >= 16 ? dib[14] | (dib[15] << 8) : 24;
        var compression = headerSize >= 20 ? dib[16] | (dib[17] << 8) | (dib[18] << 16) | (dib[19] << 24) : 0;
        var used = headerSize >= 36 ? dib[32] | (dib[33] << 8) | (dib[34] << 16) | (dib[35] << 24) : 0;
        var palette = used != 0 ? used : bitCount <= 8 ? 1 << bitCount : 0;
        var masks = compression == 3 && headerSize == 40 ? 12 : 0;
        var offset = 14 + headerSize + palette * 4 + masks;
        var size = 14 + dib.Length;
        var file = new byte[size];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        file[2] = (byte)size;
        file[3] = (byte)(size >> 8);
        file[4] = (byte)(size >> 16);
        file[5] = (byte)(size >> 24);
        file[10] = (byte)offset;
        file[11] = (byte)(offset >> 8);
        file[12] = (byte)(offset >> 16);
        file[13] = (byte)(offset >> 24);
        Buffer.BlockCopy(dib, 0, file, 14, dib.Length);
        return file;
    }
}
