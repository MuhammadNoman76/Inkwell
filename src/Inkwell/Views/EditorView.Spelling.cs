using System.Windows;
using System.Windows.Threading;
using Inkwell.Core.Editing;
using Inkwell.Core.Layout;
using Inkwell.Core.Model;
using Inkwell.Core.Proofing;
using Inkwell.Services;

namespace Inkwell.Views;

/// <summary>Spell checking with Windows' own spell checker (Windows 8 and later), in the background, visible pages first.</summary>
public partial class EditorView
{
    private sealed class SpellState
    {
        public int Version;
        public string Language = "";
        public List<SpellingError> Errors = new();
    }

    /// <summary>A misspelling the right-click menu offers suggestions for.</summary>
    private sealed class Misspelling
    {
        public Misspelling(Paragraph para, SpellingError error, string language)
        {
            Para = para;
            Error = error;
            Language = language;
        }

        public Paragraph Para { get; }
        public SpellingError Error { get; }
        public string Word => Error.Word;
        public string Language { get; }
    }

    private static WindowsSpellChecker? s_speller;
    private static bool s_spellerFailed;
    private readonly Dictionary<Paragraph, SpellState> _spellErrors = new(ReferenceEqualityComparer<Paragraph>.Instance);
    private DispatcherTimer? _spellTimer;
    private bool _spellBusy;
    private readonly Dictionary<string, string?> _spellLanguages = new(StringComparer.OrdinalIgnoreCase);

    private static WindowsSpellChecker? Speller
    {
        get
        {
            if (s_speller != null || s_spellerFailed) return s_speller;
            if (!WindowsSpellChecker.IsSupportedOs)
            {
                s_spellerFailed = true;
                return null;
            }
            var speller = new WindowsSpellChecker { OnError = (ex, what) => Log.Error(ex, "Spelling: " + what) };
            if (!speller.IsAvailable)
            {
                speller.Dispose();
                s_spellerFailed = true;
                return null;
            }
            s_speller = speller;
            return s_speller;
        }
    }

    private void StartSpelling()
    {
        if (!AppSettings.Current.SpellCheck || MainWindow.IsTestOffscreen && Environment.GetEnvironmentVariable("INKWELL_TEST_SPELLING") != "1") return;
        _spellTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(450) };
        _spellTimer.Tick -= OnSpellTick;
        _spellTimer.Tick += OnSpellTick;
        _spellTimer.Start();
    }

    private void StopSpelling()
    {
        _spellTimer?.Stop();
    }

    private void InvalidateSpelling()
    {
        if (_spellTimer != null && !_spellTimer.IsEnabled && AppSettings.Current.SpellCheck) _spellTimer.Start();
    }

    /// <summary>The squiggles of a paragraph (its last results while it's being rechecked; none while the word at the caret is being typed).</summary>
    private IReadOnlyList<(int Start, int Length)> SpellingErrors(Paragraph p)
    {
        if (!AppSettings.Current.SpellCheck || !_spellErrors.TryGetValue(p, out var state) || state.Errors.Count == 0) return Array.Empty<(int, int)>();
        var caret = Editor.Selection.Active;
        var typing = ReferenceEquals(caret.Para, p);
        var list = new List<(int, int)>(state.Errors.Count);
        foreach (var e in state.Errors)
        {
            if (e.End > p.Length) continue;
            // The word being typed isn't marked until it's finished.
            if (typing && caret.Offset >= e.Start && caret.Offset <= e.End) continue;
            list.Add((e.Start, e.Length));
        }
        return list;
    }

    private async void OnSpellTick(object? sender, EventArgs e)
    {
        if (_spellBusy || _detached) return;
        var speller = Speller;
        if (speller == null || !AppSettings.Current.SpellCheck)
        {
            _spellTimer?.Stop();
            return;
        }
        var batch = ParagraphsToCheck(24);
        if (batch.Count == 0)
        {
            _spellTimer?.Stop();
            return;
        }
        _spellBusy = true;
        var changed = false;
        try
        {
            foreach (var (p, text, language) in batch)
            {
                var version = p.Version;
                var errors = await speller.CheckAsync(text, language);
                if (_detached) return;
                if (p.Version != version) continue;
                var filtered = errors.Where(err => err.Action != SpellingAction.None && !IsExcluded(p, err)).ToList();
                var had = _spellErrors.TryGetValue(p, out var old) ? old.Errors.Count : 0;
                _spellErrors[p] = new SpellState { Version = version, Language = language, Errors = filtered };
                if (filtered.Count > 0 || had > 0) changed = true;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Spelling");
        }
        finally
        {
            _spellBusy = false;
        }
        if (changed) Doc.Redraw();
        if (_spellErrors.Count > 4 * Math.Max(1000, Session.Document.Body.Blocks.Count)) PruneSpelling();
    }

    /// <summary>Paragraphs whose text changed since they were checked, the visible ones first.</summary>
    private List<(Paragraph Para, string Text, string Language)> ParagraphsToCheck(int max)
    {
        var result = new List<(Paragraph, string, string)>();
        var seen = new HashSet<Paragraph>(ReferenceEqualityComparer<Paragraph>.Instance);
        void Consider(Paragraph p)
        {
            if (result.Count >= max || !seen.Add(p)) return;
            if (_spellErrors.TryGetValue(p, out var state) && state.Version == p.Version) return;
            var language = SpellLanguage(p);
            if (language == null || p.Length == 0 || Nav.IsReadOnly(p))
            {
                _spellErrors[p] = new SpellState { Version = p.Version };
                return;
            }
            result.Add((p, p.Text, language));
        }

        // Visible pages first.
        var current = Doc.CurrentPage;
        if (current != null)
        {
            for (var i = Math.Max(0, current.Index - 1); i < Math.Min(Layout.Pages.Count, current.Index + 3) && result.Count < max; i++)
                foreach (var p in ParagraphsOn(Layout.Pages[i]))
                    Consider(p);
        }
        foreach (var story in Session.Document.Stories)
        {
            if (result.Count >= max) break;
            foreach (var p in story.Paragraphs)
            {
                if (result.Count >= max) break;
                Consider(p);
            }
        }
        return result;
    }

    private static IEnumerable<Paragraph> ParagraphsOn(PageLayout page)
    {
        IEnumerable<Paragraph> From(Fragment f) => f switch
        {
            ParaFragment pf => new[] { pf.Paragraph },
            TableFragment tf => tf.Rows.SelectMany(r => r.Cells).SelectMany(c => c.Content).SelectMany(From),
            _ => Array.Empty<Paragraph>(),
        };
        return page.Columns.SelectMany(c => c.Content.Concat(c.Notes)).Concat(page.Header).Concat(page.Footer).SelectMany(From);
    }

    /// <summary>The language a paragraph is checked in, or null (unsupported language, code, no proofing).</summary>
    private string? SpellLanguage(Paragraph p)
    {
        try
        {
            var rp = Editor.Styles.ResolvePara(p);
            if (rp.StyleId is "Code" or "HTMLPreformatted" or "TOC1" or "TOC2" or "TOC3" or "Bibliography") return null;
            var first = p.Inlines.OfType<TextRun>().FirstOrDefault(r => r.Text.Any(char.IsLetter));
            if (first == null) return null;
            var run = Editor.Styles.ResolveRun(first.Props, p);
            if (run.NoProof) return null;
            var tag = rp.Bidi || first.Text.Any(RunPropsFactory.IsRtlChar) ? run.LangBidi : run.Lang;
            return SupportedSpellLanguage(tag);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Spelling language");
            return null;
        }
    }

    /// <summary>The tag itself when Windows can check it, else another variant of the same language that it can.</summary>
    private string? SupportedSpellLanguage(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return null;
        if (_spellLanguages.TryGetValue(tag, out var known)) return known;
        string? result = null;
        var speller = Speller;
        if (speller != null)
        {
            if (speller.IsSupported(tag)) result = tag;
            else
            {
                var prefix = tag.Split('-')[0];
                result = speller.SupportedLanguages.FirstOrDefault(l => l.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase) || l.Equals(prefix, StringComparison.OrdinalIgnoreCase));
            }
        }
        _spellLanguages[tag] = result;
        return result;
    }

    /// <summary>Not marked: code, text marked "don't check", hidden text, field results, web and e-mail addresses.</summary>
    private bool IsExcluded(Paragraph p, SpellingError error)
    {
        var inline = p.InlineAt(error.Start);
        if (inline == null) return false;
        if (inline is not TextRun || inline.Link?.Url != null) return true;
        var run = Editor.Styles.ResolveRun(inline.Props, p);
        if (run.NoProof || run.IsHidden || run.CharStyleId == "InlineCode" || run.AsciiFont is "Consolas" or "Courier New" or "Cascadia Code" or "Cascadia Mono") return true;
        var text = p.Text;
        var s = error.Start;
        var e = error.End;
        while (s > 0 && !char.IsWhiteSpace(text[s - 1])) s--;
        while (e < text.Length && !char.IsWhiteSpace(text[e])) e++;
        var token = text.Substring(s, e - s);
        return token.Contains('@') || token.Contains("://") || token.StartsWith("www.", StringComparison.OrdinalIgnoreCase) || token.Contains('\\');
    }

    private void PruneSpelling()
    {
        var live = new HashSet<Paragraph>(Session.Document.Stories.SelectMany(s => s.Paragraphs), ReferenceEqualityComparer<Paragraph>.Instance);
        foreach (var p in _spellErrors.Keys.Where(p => !live.Contains(p)).ToList()) _spellErrors.Remove(p);
    }

    private Misspelling? SpellingErrorAt(DocPos pos)
    {
        if (pos.Para == null || !_spellErrors.TryGetValue(pos.Para, out var state) || state.Version != pos.Para.Version) return null;
        var error = state.Errors.FirstOrDefault(e => pos.Offset >= e.Start && pos.Offset <= e.End);
        return error == null ? null : new Misspelling(pos.Para, error, state.Language);
    }

    private IReadOnlyList<string> Suggestions(string word, string language)
    {
        try
        {
            return Speller?.Suggest(word, language, 6) ?? Array.Empty<string>();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Suggestions");
            return Array.Empty<string>();
        }
    }

    private void ReplaceMisspelling(Misspelling m, string replacement)
    {
        var p = m.Para;
        var e = m.Error;
        if (e.End > p.Length || p.Text.Substring(e.Start, e.Length) != e.Word) return;
        Run(() => Editor.Edit("Spelling", t =>
        {
            DocEditor.EnsureEditable(p);
            t.Touch(p);
            var props = p.InlineAt(e.Start)?.Props.Clone() ?? p.PropsForTypingAt(e.Start);
            var link = p.InlineAt(e.Start)?.Link;
            Editor.DeleteInParagraph(t, p, e.Start, e.End);
            p.InsertText(e.Start, replacement, props, link, Editor.TrackChanges ? Editor.NewRevision() : null);
            return new Selection(new DocPos(p, e.Start + replacement.Length));
        }));
    }

    private void IgnoreWord(string word)
    {
        Speller?.Ignore(word);
        RecheckAll();
    }

    private void AddToDictionary(string word, string language)
    {
        if (Speller?.Add(word, language) != true) _window.ShowMessage("The word couldn't be added to the dictionary.", 4, warning: true);
        RecheckAll();
    }

    private void RecheckAll()
    {
        _spellErrors.Clear();
        Doc.Redraw();
        StartSpelling();
    }

    /// <summary>F7: the next misspelling after the caret, selected, with its suggestions.</summary>
    private void NextSpellingError()
    {
        if (!WindowsSpellChecker.IsSupportedOs)
        {
            _window.ShowMessage("Spell checking needs Windows 8 or later. This version of Windows doesn't have the spell checker Inkwell uses.", 6);
            return;
        }
        var speller = Speller;
        if (speller == null)
        {
            _window.ShowMessage("Windows' spell checker isn't available on this PC.", 5, warning: true);
            return;
        }
        if (!AppSettings.Current.SpellCheck)
        {
            _window.ShowMessage("Spell checking is off. Turn it on in Settings › Writing.", 5);
            return;
        }
        var caret = Editor.Selection.End;
        var paragraphs = Editor.Story.Paragraphs.ToList();
        var start = Math.Max(0, paragraphs.IndexOf(caret.Para));
        for (var n = 0; n < paragraphs.Count; n++)
        {
            var p = paragraphs[(start + n) % paragraphs.Count];
            var language = SpellLanguage(p);
            if (language == null || p.Length == 0) continue;
            if (!_spellErrors.TryGetValue(p, out var state) || state.Version != p.Version)
            {
                var errors = speller.Check(p.Text, language).Where(err => err.Action != SpellingAction.None && !IsExcluded(p, err)).ToList();
                state = new SpellState { Version = p.Version, Language = language, Errors = errors };
                _spellErrors[p] = state;
            }
            var found = state.Errors.FirstOrDefault(err => n > 0 || err.Start >= caret.Offset);
            if (found == null) continue;
            GoTo(new Selection(new DocPos(p, found.Start), new DocPos(p, found.End)));
            Doc.Redraw();
            var rect = Doc.ViewRectOf(new DocPos(p, found.Start));
            if (rect != null)
                ShowContextMenu(new PageHit { Position = new DocPos(p, found.Start) }, new Point(rect.Value.Left, rect.Value.Bottom + 4));
            return;
        }
        _window.ShowMessage("No spelling mistakes found.", 3);
    }
}
