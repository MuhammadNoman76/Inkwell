using Inkwell.Core.Layout;
using Inkwell.Core.Model;
using Inkwell.Core.Templates;

namespace Inkwell.Tests;

public class LayoutTests
{
    private readonly ITestOutputHelper _out;

    public LayoutTests(ITestOutputHelper output)
    {
        _out = output;
    }

    private static string Lorem(int words, int seed = 1)
    {
        var vocabulary = ("the quick brown fox jumps over a lazy dog while writers draft clear sentences about gardens rivers " +
                          "mountains cities journeys letters reports budgets plans ideas people places history science music").Split(' ');
        var r = new Random(seed);
        var list = new List<string>();
        for (var i = 0; i < words; i++) list.Add(vocabulary[r.Next(vocabulary.Length)]);
        var text = string.Join(" ", list);
        return char.ToUpperInvariant(text[0]) + text.Substring(1) + ".";
    }

    [Fact]
    public void Sample_RendersPages() => Sta.Run(() =>
    {
        var b = new DocBuilder();
        b.Title("A sample report");
        b.Subtitle("Testing the Inkwell layout engine");
        b.Heading(1, "Introduction");
        var p = b.P("Inkwell lays out text with **WPF's TextFormatter**, so shaping, *bidi* and line breaking come from the platform. " + Lorem(60));
        b.Note(p, "A footnote that explains the claim in a little more detail.");
        b.Bullets("First point", "Second point with more words to wrap " + Lorem(20, 3), "  Nested point", "Third point");
        b.Heading(2, "Numbers");
        b.Numbered(true, "One", "Two", "Three");
        b.Table(new List<string[]> { new[] { "Name", "Value", "Notes" }, new[] { "Alpha", "1", Lorem(12, 4) }, new[] { "Beta", "2", "Short" } });
        b.Quote("Write beautifully. Without fighting your document.");
        b.P("اردو ایک خوبصورت زبان ہے۔ This line mixes English and Urdu text.");
        for (var i = 0; i < 12; i++) b.P(Lorem(80, 10 + i));
        using var layout = new DocumentLayout(b.Doc);
        layout.LayoutAll();
        _out.WriteLine($"pages {layout.Pages.Count} in {layout.LastUpdateTime.TotalMilliseconds:0} ms");
        Assert.True(layout.Pages.Count >= 2);
        RenderHelper.SavePages(layout, "sample", 3);
    });
}
