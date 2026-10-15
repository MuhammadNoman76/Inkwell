using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Inkwell.Controls;
using Inkwell.Core.Editing;
using Inkwell.Core.Layout;
using Inkwell.Core.Model;
using Inkwell.Services;

namespace Inkwell.Views;

/// <summary>Typing: autocorrect with its "undo / stop doing this" chip, Markdown shortcuts, slash commands, right-to-left text, Tab.</summary>
public partial class EditorView
{
    private AutoCorrect? _autoCorrect;
    private string? _autoCorrectKey;

    private AutoCorrect AutoCorrector
    {
        get
        {
            var settings = AppSettings.Current;
            var key = string.Join(",", settings.DisabledAutoCorrect) + "|" + settings.MarkdownShortcuts;
            if (_autoCorrect != null && _autoCorrectKey == key) return _autoCorrect;
            var s = new AutoCorrectSettings();
            foreach (var entry in settings.DisabledAutoCorrect)
            {
                // "+id" turns on a rule that is off by default; "id" turns one off.
                if (entry.StartsWith("+", StringComparison.Ordinal)) s.SetEnabled(entry.Substring(1), true);
                else s.SetEnabled(entry, false);
            }
            if (!settings.MarkdownShortcuts)
            {
                s.SetEnabled(AutoCorrectRules.MarkdownBlocks, false);
                s.SetEnabled(AutoCorrectRules.MarkdownInline, false);
            }
            _autoCorrect = new AutoCorrect(s);
            _autoCorrectKey = key;
            return _autoCorrect;
        }
    }

    private AutoCorrectContext ContextAt(DocPos pos)
    {
        var p = pos.Para;
        var context = new AutoCorrectContext();
        try
        {
            var rp = Editor.Styles.ResolvePara(p);
            context.IsListItem = rp.NumId > 0;
            var style = rp.StyleId;
            var run = Editor.Styles.ResolveRun(Editor.PendingProps ?? p.PropsForTypingAt(pos.Offset), p);
            context.InCode = style is "Code" or "HTMLPreformatted" || run.CharStyleId is "InlineCode" or "HTMLCode"
                || run.AsciiFont is "Consolas" or "Courier New" or "Cascadia Code" or "Cascadia Mono";
            context.InField = p.InlineAt(Math.Max(0, pos.Offset - 1)) is FieldInline;
            context.Language = rp.Bidi ? run.LangBidi : run.Lang;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Autocorrect context");
        }
        return context;
    }

    // ------------------------------------------------------------------ before and after a keystroke

    /// <summary>Called before typed text goes in. Returns true when the text was handled here.</summary>
    private bool BeforeTyping(string text)
    {
        if (_formatPainter) StopFormatPainter();
        if (text == "\n") return BeforeEnter();
        if (SlashMenuOpen && text == " " && !SlashHasMatches()) CloseSlashMenu();

        // Right-to-left letters (Urdu, Arabic, Hebrew…) make an empty paragraph right-to-left and get the language and a fitting font.
        var rtl = text.Any(RunPropsFactory.IsRtlChar);
        var p = Editor.Selection.Active.Para;
        if (rtl && !Nav.IsReadOnly(p))
        {
            var props = Editor.PrepareRtlTyping(RtlLanguageFor(text));
            if (props != null)
            {
                Editor.InsertText(text, props);
                AfterTyping(text);
                return true;
            }
        }
        else if (!rtl && text.Any(char.IsLetter))
        {
            // Latin letters typed after right-to-left text don't carry the right-to-left run property.
            var caret = Editor.Selection.Start;
            var typing = Editor.PendingProps ?? caret.Para.PropsForTypingAt(caret.Offset);
            if (typing.Rtl == true)
            {
                var props = typing.Clone();
                props.Rtl = null;
                Editor.InsertText(text, props);
                AfterTyping(text);
                return true;
            }
        }
        return false;
    }

    private static string RtlLanguageFor(string text)
    {
        try
        {
            var input = InputLanguageManager.Current?.CurrentInputLanguage;
            if (input != null && input.TextInfo.IsRightToLeft) return input.Name;
        }
        catch
        {
            // no input language information
        }
        if (text.Any(c => c is >= '֐' and <= '׿')) return "he-IL";
        // Letters only Urdu uses (ٹ ڈ ڑ ں ے ھ ۓ ۂ).
        if (text.Any(c => c is 'ٹ' or 'ڈ' or 'ڑ' or 'ں' or 'ے' or 'ھ' or 'ۓ' or 'ۂ')) return "ur-PK";
        if (text.Any(c => c is 'پ' or 'چ' or 'ژ' or 'گ' or 'ک' or 'ی')) return "fa-IR";
        return "ar-SA";
    }

    /// <summary>Enter: Markdown "```" / "---" lines, autocorrect of the word before it, the slash menu.</summary>
    private bool BeforeEnter()
    {
        if (SlashMenuOpen)
        {
            RunSlashSelection();
            return true;
        }
        if (!Editor.Selection.IsEmpty) return false;
        var caret = Editor.Selection.Active;
        var p = caret.Para;
        if (Nav.IsReadOnly(p)) return false;
        var result = AutoCorrector.Process(p.Text, caret.Offset, '\r', ContextAt(caret));
        if (result.Block is { ByEnter: true } block)
        {
            Run(() => Editor.ApplyBlockShortcut(block));
            ShowAutoCorrectChip(null, block.Description, block.RuleId);
            return true;
        }
        if (result.Edits.Count > 0) ApplyAutoCorrect(result.Edits, showChip: false);
        return false;
    }

    /// <summary>After typed text went in: slash menu, Markdown shortcuts and autocorrect (each its own undo step).</summary>
    private void AfterTyping(string text)
    {
        if (text.Length != 1) return;
        var typed = text[0];
        var caret = Editor.Selection.Active;
        var p = caret.Para;
        if (!Editor.Selection.IsEmpty) return;

        if (SlashMenuOpen)
        {
            UpdateSlashMenu();
            return;
        }
        if (typed == '/' && AppSettings.Current.SlashCommands && (caret.Offset == 1 || (caret.Offset >= 2 && char.IsWhiteSpace(p.Text[caret.Offset - 2]))) && p.Story is not Comment)
        {
            OpenSlashMenu(p, caret.Offset - 1);
            return;
        }

        var result = AutoCorrector.Process(p.Text, caret.Offset, typed, ContextAt(caret));
        if (result.IsEmpty) return;
        if (result.Block != null)
        {
            var block = result.Block;
            Run(() => Editor.ApplyBlockShortcut(block));
            ShowAutoCorrectChip(null, block.Description, block.RuleId);
            return;
        }
        if (result.Inline != null)
        {
            var inline = result.Inline;
            Run(() => Editor.ApplyInlineShortcut(inline));
            ShowAutoCorrectChip(null, inline.Description, inline.RuleId);
            return;
        }
        ApplyAutoCorrect(result.Edits, showChip: true);
    }

    private void ApplyAutoCorrect(IReadOnlyList<AutoCorrectEdit> edits, bool showChip)
    {
        var p = Editor.Selection.Active.Para;
        var applied = false;
        Run(() => applied = Editor.ApplyAutoCorrect(edits));
        if (!applied || !showChip) return;
        // Where each change ended up (earlier edits shift later ones).
        var changes = new List<AppliedCorrection>();
        var delta = 0;
        foreach (var e in edits.OrderBy(e => e.Start))
        {
            changes.Add(new AppliedCorrection(p, e.Start + delta, e.Replacement, e.Original, e.Kind));
            if (e.Kind == AutoCorrectEditKind.Replace) delta += e.Replacement.Length - e.Length;
        }
        var first = edits[0];
        ShowAutoCorrectChip(changes, first.Description, first.RuleId);
    }

    private sealed class AppliedCorrection
    {
        public AppliedCorrection(Paragraph para, int start, string replacement, string original, AutoCorrectEditKind kind)
        {
            Para = para;
            Start = start;
            Replacement = replacement;
            Original = original;
            Kind = kind;
        }

        public Paragraph Para { get; }
        public int Start { get; }
        public string Replacement { get; }
        public string Original { get; }
        public AutoCorrectEditKind Kind { get; }
    }

    // ------------------------------------------------------------------ Tab

    /// <summary>Tab: next cell in a table, list level at the start of a list item. Returns true when handled.</summary>
    private bool OnTab(bool shift)
    {
        if (SlashMenuOpen)
        {
            RunSlashSelection();
            return true;
        }
        try
        {
            if (Editor.TabInTable(shift)) return true;
            var caret = Editor.Selection.Active;
            var listItem = Editor.Styles.ResolvePara(caret.Para).NumId > 0;
            if (listItem && (caret.Offset == 0 || !Editor.Selection.InOneParagraph || shift))
            {
                Editor.Indent(!shift);
                return true;
            }
            if (!Editor.Selection.IsEmpty && !Editor.Selection.InOneParagraph)
            {
                Editor.Indent(!shift);
                return true;
            }
        }
        catch (EditRefusedException ex)
        {
            _window.ShowMessage(ex.Message, 5, warning: true);
            return true;
        }
        return shift;
    }

    // ------------------------------------------------------------------ chips

    private Border? _autoCorrectChip;
    private DispatcherTimer? _chipTimer;
    private List<AppliedCorrection>? _chipCorrections;
    private string? _chipRule;

    /// <summary>"Smart quotes · Undo · Stop doing this" next to the change.</summary>
    private void ShowAutoCorrectChip(List<AppliedCorrection>? corrections, string description, string ruleId)
    {
        _chipCorrections = corrections;
        _chipRule = ruleId;
        _chipShown = DateTime.UtcNow;
        _chipAnchor = corrections is { Count: > 0 } ? new DocPos(corrections[0].Para, corrections[0].Start) : Editor.Selection.Active;
        var chip = MakeChip(out var stack);
        AutomationProperties.SetName(chip, "AutoCorrect: " + description);
        var label = new TextBlock { Text = description, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0), FontSize = 12.5, MaxWidth = 300, TextTrimming = TextTrimming.CharacterEllipsis };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        stack.Children.Add(label);
        stack.Children.Add(ChipButton("Undo", "Put back what was typed (Ctrl+Z)", UndoAutoCorrect));
        stack.Children.Add(ChipButton("Stop doing this", "Turn this off (Settings › Autocorrect turns it back on)", StopAutoCorrectRule));
        _autoCorrectChip = chip;
        ChipLayer.Children.Add(chip);
        PositionChips();
        _chipTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _chipTimer.Stop();
        _chipTimer.Tick -= OnChipTimer;
        _chipTimer.Tick += OnChipTimer;
        _chipTimer.Start();
    }

    private void OnChipTimer(object? sender, EventArgs e)
    {
        _chipTimer?.Stop();
        if (_autoCorrectChip != null && !_autoCorrectChip.IsMouseOver) HideAutoCorrectChip();
        if (_pasteChip != null && !_pasteChip.IsMouseOver && (DateTime.UtcNow - _pasteChipShown).TotalSeconds > 12) HidePasteChip();
    }

    private void HideAutoCorrectChip()
    {
        if (_autoCorrectChip == null) return;
        ChipLayer.Children.Remove(_autoCorrectChip);
        _autoCorrectChip = null;
    }

    private void UndoAutoCorrect()
    {
        var corrections = _chipCorrections;
        // Straight after the change: a plain undo. Later (more was typed): put the original text back where it was.
        if (corrections == null || Editor.History.Top is { IsAutomatic: true })
        {
            Undo();
            return;
        }
        Run(() => Editor.Edit("Undo AutoCorrect", t =>
        {
            foreach (var c in corrections.OrderByDescending(c => c.Start))
            {
                var p = c.Para;
                if (p.Parent == null) continue;
                var text = p.Text;
                if (c.Start < 0 || c.Start + c.Replacement.Length > text.Length || text.Substring(c.Start, c.Replacement.Length) != c.Replacement) continue;
                t.Touch(p);
                if (c.Kind == AutoCorrectEditKind.Superscript)
                {
                    p.FormatRange(c.Start, c.Start + c.Replacement.Length, r => r.VertAlign = null);
                    continue;
                }
                var props = p.InlineAt(c.Start)?.Props.Clone() ?? p.PropsForTypingAt(c.Start);
                var link = p.InlineAt(c.Start)?.Link;
                p.RemoveRange(c.Start, c.Start + c.Replacement.Length);
                p.InsertText(c.Start, c.Original, props, link, Editor.TrackChanges ? Editor.NewRevision() : null);
            }
            return null;
        }));
        FocusDocument();
    }

    private void StopAutoCorrectRule()
    {
        var rule = _chipRule;
        UndoAutoCorrect();
        if (rule == null) return;
        var name = AutoCorrectRules.NameOf(rule);
        AppSettings.Update(s =>
        {
            s.DisabledAutoCorrect.Remove("+" + rule);
            if (AutoCorrectRules.Find(rule)?.EnabledByDefault != false && !s.DisabledAutoCorrect.Contains(rule)) s.DisabledAutoCorrect.Add(rule);
        });
        _window.ShowMessage($"“{name}” is off. Turn it back on in Settings › Autocorrect.", 5);
        FocusDocument();
    }

    /// <summary>A small floating bar next to the caret.</summary>
    private Border MakeChip(out StackPanel content)
    {
        var chip = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2),
            Height = 34,
        };
        chip.SetResourceReference(Border.BackgroundProperty, "Flyout.Background");
        chip.SetResourceReference(Border.BorderBrushProperty, "Flyout.Stroke");
        chip.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Direction = 270, Opacity = 0.18 };
        content = new StackPanel { Orientation = Orientation.Horizontal };
        chip.Child = content;
        return chip;
    }

    private Button ChipButton(string text, string tooltip, Action action)
    {
        var b = new Button { Content = text, ToolTip = tooltip, Height = 28, Padding = new Thickness(10, 0, 10, 0), FontSize = 12.5, Focusable = false };
        b.SetResourceReference(StyleProperty, "SubtleButton");
        AutomationProperties.SetName(b, text);
        b.Click += (_, _) => action();
        return b;
    }

    /// <summary>
    /// The document changed: the paste chip goes; the autocorrect chip stays a few seconds while typing goes on in
    /// the same paragraph (its Undo puts the original text back where it was).
    /// </summary>
    private void HideChips()
    {
        if (_suppressChipHide) return;
        HidePasteChip();
        if (_autoCorrectChip != null)
        {
            var samePara = ReferenceEquals(_chipAnchor.Para, Editor.Selection.Active.Para);
            if (!samePara || _chipCorrections == null || (DateTime.UtcNow - _chipShown).TotalSeconds > 6) HideAutoCorrectChip();
            else Dispatcher.BeginInvoke(new Action(PositionChips), DispatcherPriority.Background);
        }
    }

    private bool _suppressChipHide;
    private DateTime _chipShown;
    private DocPos _chipAnchor;

    /// <summary>Puts the chips just below what they're about (after scrolling or zooming too).</summary>
    private void PositionChips()
    {
        foreach (var chip in new[] { _autoCorrectChip, _pasteChip })
        {
            if (chip == null) continue;
            var caret = ReferenceEquals(chip, _autoCorrectChip) && _chipAnchor.Para?.Parent != null
                ? Doc.ViewRectOf(new DocPos(_chipAnchor.Para, Math.Min(_chipAnchor.Offset, _chipAnchor.Para.Length)))
                : Doc.CaretViewRect;
            if (caret == null)
            {
                chip.Visibility = Visibility.Collapsed;
                continue;
            }
            chip.Visibility = Visibility.Visible;
            var rect = caret.Value;
            var origin = Doc.TranslatePoint(new Point(rect.Left, rect.Bottom + 6), ChipLayer);
            chip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var w = chip.DesiredSize.Width;
            var x = MathEx.Clamp(origin.X - 12, 8, Math.Max(8, ChipLayer.ActualWidth - w - 8));
            var y = origin.Y;
            if (y + 40 > ChipLayer.ActualHeight - 36) y = Doc.TranslatePoint(new Point(rect.Left, rect.Top - 40), ChipLayer).Y;
            if (ReferenceEquals(chip, _pasteChip) && _autoCorrectChip != null) y += 40;
            Canvas.SetLeft(chip, x);
            Canvas.SetTop(chip, y);
        }
        PositionSlashMenu();
    }
}
