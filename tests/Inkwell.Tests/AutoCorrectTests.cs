using Inkwell.Core.Editing;

namespace Inkwell.Tests;

/// <summary>The autocorrect engine: every rule, with the cases where it must stay quiet.</summary>
public class AutoCorrectTests
{
    /// <summary>Types <paramref name="input"/> one character at a time ('\r' = Enter), applying the text edits after each keystroke.</summary>
    private static string Type(string input, AutoCorrect? engine = null, AutoCorrectContext? context = null)
    {
        engine ??= new AutoCorrect();
        var text = "";
        var caret = 0;
        foreach (var c in input)
        {
            if (c == '\r')
            {
                var r = engine.Process(text, caret, c, context);
                text = r.Apply(text, ref caret);
                continue;
            }
            text = text.Insert(caret, c.ToString());
            caret++;
            var result = engine.Process(text, caret, c, context);
            text = result.Apply(text, ref caret);
        }
        return text;
    }

    /// <summary>The result of typing the last character of <paramref name="text"/>.</summary>
    private static AutoCorrectResult Last(string text, AutoCorrect? engine = null, AutoCorrectContext? context = null) =>
        (engine ?? new AutoCorrect()).Process(text, text.Length, text[text.Length - 1], context);

    private static AutoCorrect With(string ruleId, bool on)
    {
        var engine = new AutoCorrect();
        engine.Settings.SetEnabled(ruleId, on);
        return engine;
    }

    // ---- smart quotes ----

    [Fact]
    public void SmartQuotes_OpenAndClose()
    {
        Assert.Equal("“Hello,” she said.", Type("\"Hello,\" she said."));
        Assert.Equal("He said “yes” (and “no”).", Type("He said \"yes\" (and \"no\")."));
        Assert.Equal("‘Single’ quotes", Type("'Single' quotes"));
        Assert.Equal("“‘Nested’”", Type("\"'Nested'\""));
        Assert.Equal("Don’t stop, it’s Lovelace’s.", Type("Don't stop, it's Lovelace's."));
        Assert.Equal("Wait—“now”", Type("Wait—\"now\""));
    }

    [Fact]
    public void SmartQuotes_YearsAndElisions()
    {
        Assert.Equal("In the ’90s we sang", Type("In the '90s we sang"));
        Assert.Equal("Class of ’69, ’tis true", Type("Class of '69, 'tis true"));
        Assert.Equal("Rock ’n’ roll ", Type("Rock 'n' roll "));
        Assert.Equal("Let ’em go", Type("Let 'em go"));
        // A real quotation is left open.
        Assert.Equal("He said ‘hello’ ", Type("He said 'hello' "));
    }

    [Fact]
    public void SmartQuotes_Languages()
    {
        Assert.Equal("Er sagte „ja“", Type("Er sagte \"ja\"", context: new AutoCorrectContext { Language = "de-DE" }));
        Assert.Equal("Il a dit « oui »", Type("Il a dit \"oui \"", context: new AutoCorrectContext { Language = "fr-FR" }));
        Assert.Equal("Il a dit « oui »", Type("Il a dit \"oui\"", context: new AutoCorrectContext { Language = "fr-FR" }));
    }

    [Fact]
    public void NothingChangesInCodeOrFields()
    {
        Assert.True(Last("x = \"", context: new AutoCorrectContext { InCode = true }).IsEmpty);
        Assert.Equal("print(\"hi\") -> (c) ... 1/2 i ", Type("print(\"hi\") -> (c) ... 1/2 i ", context: new AutoCorrectContext { InCode = true }));
        Assert.True(Last("a -> b", context: new AutoCorrectContext { InField = true }).IsEmpty);
    }

    // ---- capitals ----

    [Fact]
    public void Capitalize_SentenceStarts()
    {
        Assert.Equal("Hello world. This is it! Really? Yes ", Type("hello world. this is it! really? yes "));
        Assert.Equal("He said “stop.” Then ", Type("he said \"stop.\" then "));
        Assert.Equal("(The end) ", Type("(the end) "));
        Assert.Equal("It costs 3.5 million. The end ", Type("it costs 3.5 million. the end "));
        Assert.Equal("Visit example.com. Then ", Type("visit example.com. then "));
    }

    [Fact]
    public void Capitalize_NotAfterAbbreviations()
    {
        // The two classic false positives.
        Assert.Equal("Some fruit, e.g. apples and pears ", Type("some fruit, e.g. apples and pears "));
        Assert.Equal("The capital, i.e. the city ", Type("the capital, i.e. the city "));
        Assert.Equal("Use (e.g. this) ", Type("use (e.g. this) "));
        Assert.Equal("Apples, pears, etc. and more ", Type("apples, pears, etc. and more "));
        Assert.Equal("Ask Dr. smith and Mrs. jones ", Type("ask Dr. smith and Mrs. jones "));
        Assert.Equal("Babbage et al. found ", Type("babbage et al. found "));
        Assert.Equal("At 9 a.m. today ", Type("at 9 a.m. today "));
        Assert.Equal("See Fig. three and pp. four ", Type("see Fig. three and pp. four "));
        Assert.Equal("Made in the U.S. by them ", Type("made in the U.S. by them "));
        Assert.Equal("Written by J. smith ", Type("written by J. smith "));
        Assert.Equal("See section 2.1. then ", Type("see section 2.1. then "));
        Assert.Equal("Wait... what ", Type("wait... what ", With(AutoCorrectRules.Ellipsis, false)));
        Assert.Equal("Wait… what ", Type("wait... what "));

        var custom = new AutoCorrect();
        custom.Settings.Abbreviations.Add("fn.");
        Assert.Equal("See fn. two ", Type("see fn. two ", custom));
    }

    [Fact]
    public void Capitalize_LeavesAddressesAndBrandNamesAlone()
    {
        Assert.Equal("Go. www.example.com is up ", Type("go. www.example.com is up "));
        Assert.Equal("Mail. user@example.com works ", Type("mail. user@example.com works "));
        Assert.Equal("Open. notes.txt now ", Type("open. notes.txt now "));
        Assert.Equal("New phone. iPhone is ", Type("new phone. iPhone is "));
        Assert.Equal("Shop. eBay sells ", Type("shop. eBay sells "));
        // List labels at the start of a paragraph.
        Assert.Equal("a. first ", Type("a. first "));
        Assert.Equal("i) one ", Type("i) one "));
        // Only lower-case letters change; numbers and other scripts are left alone.
        Assert.Equal("Done. 3rd place ", Type("done. 3rd place "));
        Assert.Equal("یہ اردو ہے۔ اور ", Type("یہ اردو ہے۔ اور "));
    }

    [Fact]
    public void Capitalize_UsesTheLanguage()
    {
        var turkish = new AutoCorrectContext { Language = "tr-TR" };
        Assert.Equal("İstanbul ", Type("istanbul ", context: turkish));
    }

    [Fact]
    public void CapitalI()
    {
        Assert.Equal("Then I went and I’m here, I’ll see ", Type("then i went and i'm here, i'll see "));
        Assert.Equal("So do I.", Type("so do i.\r"));
        Assert.Equal("Points (i) and (ii) ", Type("points (i) and (ii) "));
        Assert.Equal("i. first ", Type("i. first "));
        Assert.Equal("Und i ", Type("und i ", context: new AutoCorrectContext { Language = "de-DE" }));
        Assert.Equal("Then i ", Type("then i ", With(AutoCorrectRules.CapitalI, false)));
    }

    [Fact]
    public void TwoInitialCapitals()
    {
        Assert.Equal("The end ", Type("THe end "));
        Assert.Equal("Our CDs and IDs at 3 GHz, OK ", Type("our CDs and IDs at 3 GHz, OK "));
        Assert.Equal("THe ", Type("THe ", With(AutoCorrectRules.TwoInitialCapitals, false)));
        var r = Last("We THe ");
        var edit = Assert.Single(r.Edits);
        Assert.Equal(AutoCorrectRules.TwoInitialCapitals, edit.RuleId);
        Assert.Equal("Changed “THe” to “The”", edit.Description);
    }

    // ---- replacements ----

    [Fact]
    public void Symbols()
    {
        Assert.Equal("Copyright © 2026 Inkwell™ and Brand ®", Type("Copyright (c) 2026 Inkwell(tm) and Brand (R)"));
        Assert.Equal("Copyright ©", Type("Copyright (C)"));
        Assert.Equal("Trademark ™", Type("Trademark (TM)"));
        // Function calls and list labels stay.
        Assert.Equal("f(c) = 2", Type("f(c) = 2"));
        Assert.Equal("(c) third", Type("(c) third"));
        Assert.Equal("(a) one (b) two (c) three", Type("(a) one (b) two (c) three"));
    }

    [Fact]
    public void EllipsisAndDashes()
    {
        Assert.Equal("Wait… ", Type("Wait... "));
        Assert.Equal("A dash—like this", Type("A dash--like this"));
        Assert.Equal("Pages – here", Type("Pages -- here"));
        Assert.Equal("Use --verbose here", Type("Use --verbose here"));
        Assert.Equal("a---b", Type("a---b"));
        Assert.Equal("A--b", Type("A--b", With(AutoCorrectRules.Dashes, false)));
    }

    [Fact]
    public void Arrows()
    {
        Assert.Equal("A → b ⇒ c ⇔ d ↔ e", Type("A -> b => c <=> d <-> e"));
        Assert.Equal("Back ← here", Type("Back <- here"));
        Assert.Equal("End -->", Type("End -->"));
        Assert.Equal("If x<-5 ", Type("If x<-5 "));
    }

    [Fact]
    public void Fractions()
    {
        Assert.Equal("Add ½ cup and ¼ (¾) ", Type("Add 1/2 cup and 1/4 (3/4) "));
        Assert.Equal("Take 11/2 and 1/2/2026 and 1/3 ", Type("Take 11/2 and 1/2/2026 and 1/3 "));
    }

    [Fact]
    public void Ordinals_OffByDefault()
    {
        Assert.True(Last("the 1st ").IsEmpty);
        var engine = With(AutoCorrectRules.Ordinals, true);
        var edit = Assert.Single(Last("The 21st ", engine).Edits);
        Assert.Equal(AutoCorrectEditKind.Superscript, edit.Kind);
        Assert.Equal(6, edit.Start);
        Assert.Equal(2, edit.Length);
        Assert.Equal("st", edit.Replacement);
        Assert.True(Last("The 11st ", engine).IsEmpty);
        Assert.Single(Last("The 11th ", engine).Edits);
        Assert.Single(Last("The 2nd ", engine).Edits);
    }

    // ---- edits, settings ----

    [Fact]
    public void Edits_CarryRuleAndUndoInformation()
    {
        var r = Last("Copyright (c)");
        var e = Assert.Single(r.Edits);
        Assert.Equal(10, e.Start);
        Assert.Equal(3, e.Length);
        Assert.Equal("©", e.Replacement);
        Assert.Equal("(c)", e.Original);
        Assert.Equal(AutoCorrectRules.Symbols, e.RuleId);
        Assert.Equal("Symbols", e.RuleName);
        Assert.Equal("Changed (c) to ©", e.Description);

        // Two rules at once, sorted and applied together; the caret moves with them.
        var both = Last("hello. so i ");
        Assert.Equal(new[] { AutoCorrectRules.CapitalI }, both.Edits.Select(x => x.RuleId));
        var two = new AutoCorrect().Process("hello 1/2 -- ", 13, ' ');
        Assert.Equal(new[] { AutoCorrectRules.Dashes }, two.Edits.Select(x => x.RuleId));
        var caret = 13;
        Assert.Equal("hello 1/2 – ", two.Apply("hello 1/2 -- ", ref caret));
        Assert.Equal(12, caret);
    }

    [Fact]
    public void Settings_RulesCanBeTurnedOff()
    {
        var settings = new AutoCorrectSettings();
        Assert.True(settings.IsEnabled(AutoCorrectRules.SmartQuotes));
        Assert.False(settings.IsEnabled(AutoCorrectRules.Ordinals));
        settings.SetEnabled(AutoCorrectRules.SmartQuotes, false);
        Assert.False(settings.IsEnabled(AutoCorrectRules.SmartQuotes));
        Assert.False(settings.Overrides[AutoCorrectRules.SmartQuotes]);
        settings.SetEnabled(AutoCorrectRules.SmartQuotes, true);
        Assert.Empty(settings.Overrides);
        Assert.Equal("\"plain\"", Type("\"plain\"", With(AutoCorrectRules.SmartQuotes, false)));

        // Every rule has a unique id and a name.
        Assert.Equal(AutoCorrectRules.All.Count, AutoCorrectRules.All.Select(r => r.Id).Distinct().Count());
        Assert.All(AutoCorrectRules.All, r => Assert.False(string.IsNullOrWhiteSpace(r.Name)));
        Assert.Null(AutoCorrectRules.Find("nope"));
    }

    [Fact]
    public void BadInput_IsIgnored()
    {
        var engine = new AutoCorrect();
        Assert.True(engine.Process("abc", 5, 'c').IsEmpty);
        Assert.True(engine.Process("abc", 2, 'x').IsEmpty);
        Assert.True(engine.Process("", 0, ' ').IsEmpty);
        Assert.True(engine.Process("", 0, '\r').IsEmpty);
    }

    // ---- Markdown ----

    [Fact]
    public void Markdown_BlockShortcuts()
    {
        BlockShortcut? Block(string text, AutoCorrectContext? context = null) => Last(text, null, context).Block;

        var h = Block("# ")!;
        Assert.Equal(BlockShortcutKind.Heading, h.Kind);
        Assert.Equal(1, h.Level);
        Assert.Equal(2, h.RemoveLength);
        Assert.Equal(3, Block("### ")!.Level);
        Assert.Equal(6, Block("###### ")!.Level);
        Assert.Null(Block("####### "));
        Assert.Equal(BlockShortcutKind.BulletList, Block("- ")!.Kind);
        Assert.Equal('*', Block("* ")!.Bullet);
        var n = Block("1. ")!;
        Assert.Equal(BlockShortcutKind.NumberedList, n.Kind);
        Assert.Equal(1, n.StartNumber);
        Assert.Equal(".", n.NumberSuffix);
        var n3 = Block("3) ")!;
        Assert.Equal(3, n3.StartNumber);
        Assert.Equal(")", n3.NumberSuffix);
        Assert.Equal(BlockShortcutKind.Quote, Block("> ")!.Kind);
        Assert.Equal(BlockShortcutKind.Divider, Block("--- ")!.Kind);
        Assert.Equal(BlockShortcutKind.CodeBlock, Block("``` ")!.Kind);
        Assert.Null(Block("a # "));
        Assert.Null(Block("#a "));
        Assert.Null(Block("- ", new AutoCorrectContext { IsListItem = true }));
        Assert.Null(Block("# ", new AutoCorrectContext { InCode = true }));

        // Typed at the start of existing text.
        var engine = new AutoCorrect();
        var mid = engine.Process("## Title", 3, ' ').Block!;
        Assert.Equal(2, mid.Level);
        Assert.Equal(3, mid.RemoveLength);

        var enter = engine.Process("---", 3, '\r').Block!;
        Assert.Equal(BlockShortcutKind.Divider, enter.Kind);
        Assert.True(enter.ByEnter);
        var code = engine.Process("```cs", 5, '\r').Block!;
        Assert.Equal(BlockShortcutKind.CodeBlock, code.Kind);
        Assert.Equal("cs", code.CodeLanguage);
        Assert.Equal(5, code.RemoveLength);
        Assert.Null(engine.Process("--- more", 3, '\r').Block);
        Assert.Null(With(AutoCorrectRules.MarkdownBlocks, false).Process("# ", 2, ' ').Block);
    }

    [Fact]
    public void Markdown_InlineShortcuts()
    {
        InlineShortcut? Inline(string text) => Last(text).Inline;

        Assert.Null(Inline("Make **this** bold")); // the closing marker isn't the last character typed
        var bold = Inline("Make **this**")!;
        Assert.Equal(InlineFormat.Bold, bold.Format);
        Assert.Equal(5, bold.OpenStart);
        Assert.Equal(2, bold.OpenLength);
        Assert.Equal(11, bold.CloseStart);
        Assert.Equal(7, bold.ContentStart);
        Assert.Equal(4, bold.ContentLength);
        Assert.Equal("Make this", bold.Apply("Make **this**"));

        Assert.Equal(InlineFormat.Bold, Inline("__strong__")!.Format);
        Assert.Equal(InlineFormat.Italic, Inline("an *idea*")!.Format);
        Assert.Equal(InlineFormat.Italic, Inline("an _idea_")!.Format);
        Assert.Equal(InlineFormat.Code, Inline("run `make`")!.Format);
        Assert.Equal(InlineFormat.Strikethrough, Inline("~~gone~~")!.Format);
        Assert.Equal("run make", Inline("run `make`")!.Apply("run `make`"));

        // The first star of a closing "**" is not an italic closer.
        Assert.Null(Inline("**bold*"));
        // snake_case, arithmetic, spaces inside markers, empty content.
        Assert.Null(Inline("my_var_"));
        Assert.Null(Inline("2*3*"));
        Assert.Null(Inline("a * b *"));
        Assert.Null(Inline("a ** b **"));
        Assert.Null(Inline("````"));
        Assert.Null(Inline("**"));
        // "_" doesn't close before a letter (typing in the middle of snake_case).
        Assert.Null(new AutoCorrect().Process("_a_b", 3, '_').Inline);
        Assert.Null(Last("x *y*", context: new AutoCorrectContext { InCode = true }).Inline);
    }
}

