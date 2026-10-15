using Inkwell.Core.Editing;
using Inkwell.Services;
using Inkwell.Views;

namespace Inkwell.Tests;

/// <summary>
/// Autocorrect rules in the settings: whatever "Stop doing this" turns off, Settings › Autocorrect shows with a switch
/// that turns it back on (the Markdown rules included), and 1.0.0's single Markdown switch carries over.
/// </summary>
public class SettingsTests
{
    [Fact]
    public void SettingsListsEveryRule_TheChipsCanTurnOff()
    {
        var shown = SettingsWindow.AutoCorrectRulesShown.Select(r => r.Id).ToList();
        Assert.Equal(AutoCorrectRules.All.Select(r => r.Id), shown);
        Assert.Contains(AutoCorrectRules.MarkdownBlocks, shown);
        Assert.Contains(AutoCorrectRules.MarkdownInline, shown);

        // Every rule a chip can name, found by typing things that trigger them.
        var engine = new AutoCorrect(AutoCorrectSettings.FromStored(new[] { "+" + AutoCorrectRules.Ordinals }));
        var named = new HashSet<string>();
        foreach (var sample in new[]
        {
            "\"hi\" ", "it's ", "the end. next ", "i ", "(c) ", "wait... ", "a -- b ", "a->b ", "1/2 ", "1st ", "THe ",
            "# ", "- ", "1. ", "> ", "**bold**", "_it_", "`code`", "~~gone~~",
        })
        {
            var text = "";
            foreach (var c in sample)
            {
                text += c;
                var result = engine.Process(text, text.Length, c);
                foreach (var e in result.Edits) named.Add(e.RuleId);
                if (result.Block != null) named.Add(result.Block.RuleId);
                if (result.Inline != null) named.Add(result.Inline.RuleId);
                text = result.Apply(text);
            }
        }
        Assert.True(named.Count >= 10, "Only these rules fired: " + string.Join(", ", named));
        foreach (var id in named) Assert.Contains(id, shown);
    }

    [Fact]
    public void EveryRule_TurnedOffFromTheChip_CanBeTurnedBackOn()
    {
        foreach (var rule in AutoCorrectRules.All)
        {
            var stored = new List<string>();
            if (!rule.EnabledByDefault) AutoCorrectRules.SetOn(stored, rule.Id, true); // on first, as a chip only shows for rules that are on
            Assert.True(AutoCorrectRules.IsOn(stored, rule.Id), rule.Id);
            Assert.True(AutoCorrectSettings.FromStored(stored).IsEnabled(rule.Id), rule.Id);

            AutoCorrectRules.SetOn(stored, rule.Id, false); // "Stop doing this"
            Assert.False(AutoCorrectRules.IsOn(stored, rule.Id), rule.Id);
            Assert.False(AutoCorrectSettings.FromStored(stored).IsEnabled(rule.Id), rule.Id);
            AutoCorrectRules.SetOn(stored, rule.Id, false); // twice changes nothing
            Assert.Equal(rule.EnabledByDefault ? 1 : 0, stored.Count(s => s.TrimStart('+') == rule.Id));

            AutoCorrectRules.SetOn(stored, rule.Id, true); // the switch in Settings
            Assert.True(AutoCorrectRules.IsOn(stored, rule.Id), rule.Id);
            Assert.True(AutoCorrectSettings.FromStored(stored).IsEnabled(rule.Id), rule.Id);
        }
    }

    [Fact]
    public void MarkdownShortcut_StoppedFromTheChip_WorksAgainAfterTheSettingsSwitch()
    {
        var stored = new List<string>();
        Assert.NotNull(new AutoCorrect(AutoCorrectSettings.FromStored(stored)).Process("# ", 2, ' ').Block);
        AutoCorrectRules.SetOn(stored, AutoCorrectRules.MarkdownBlocks, false);
        Assert.Null(new AutoCorrect(AutoCorrectSettings.FromStored(stored)).Process("# ", 2, ' ').Block);
        AutoCorrectRules.SetOn(stored, AutoCorrectRules.MarkdownBlocks, true);
        Assert.NotNull(new AutoCorrect(AutoCorrectSettings.FromStored(stored)).Process("# ", 2, ' ').Block);
        Assert.Empty(stored);
    }

    [Fact]
    public void OldMarkdownSwitch_BecomesTheTwoRules()
    {
        var settings = AppSettings.Parse("{ \"MarkdownShortcuts\": false, \"DisabledAutoCorrect\": [ \"smart-quotes\" ] }");
        Assert.False(AutoCorrectRules.IsOn(settings.DisabledAutoCorrect, AutoCorrectRules.MarkdownBlocks));
        Assert.False(AutoCorrectRules.IsOn(settings.DisabledAutoCorrect, AutoCorrectRules.MarkdownInline));
        Assert.False(AutoCorrectRules.IsOn(settings.DisabledAutoCorrect, AutoCorrectRules.SmartQuotes));
        Assert.True(AutoCorrectRules.IsOn(settings.DisabledAutoCorrect, AutoCorrectRules.Ellipsis));
        // The old switch no longer overrides the rules: their own switches turn them back on.
        Assert.True(settings.MarkdownShortcuts);
        AutoCorrectRules.SetOn(settings.DisabledAutoCorrect, AutoCorrectRules.MarkdownBlocks, true);
        AutoCorrectRules.SetOn(settings.DisabledAutoCorrect, AutoCorrectRules.MarkdownInline, true);
        Assert.True(AutoCorrectSettings.FromStored(settings.DisabledAutoCorrect).IsEnabled(AutoCorrectRules.MarkdownBlocks));
        Assert.True(AutoCorrectSettings.FromStored(settings.DisabledAutoCorrect).IsEnabled(AutoCorrectRules.MarkdownInline));

        var fresh = AppSettings.Parse("{}");
        Assert.Empty(fresh.DisabledAutoCorrect);
        Assert.All(AutoCorrectRules.All, r => Assert.Equal(r.EnabledByDefault, AutoCorrectRules.IsOn(fresh.DisabledAutoCorrect, r.Id)));
    }
}
