using System.Diagnostics;
using Inkwell.Core.Editing;
using Inkwell.Core.IO;
using Inkwell.Core.Layout;
using Inkwell.Core.Model;
using Inkwell.Core.Templates;
using Xunit;

namespace Inkwell.Tests;

/// <summary>
/// A 300-page document: open, lay out, type in the middle, save. The numbers are reported (INKWELL_PERF_OUT=file
/// appends them); the limits are generous so slow test machines don't fail, but they catch anything quadratic.
/// </summary>
public class PerformanceTests
{
    private readonly ITestOutputHelper _out;

    public PerformanceTests(ITestOutputHelper output) => _out = output;

    private static Document BigDocument()
    {
        var b = new DocBuilder(DocumentDesigns.Default);
        var words = "the quick brown fox jumps over a lazy dog while writers draft clear sentences about gardens rivers mountains cities journeys letters reports budgets plans".Split(' ');
        var r = new Random(7);
        string Sentence(int n) => string.Join(" ", Enumerable.Range(0, n).Select(_ => words[r.Next(words.Length)])) + ".";
        for (var chapter = 1; chapter <= 39; chapter++)
        {
            b.Heading(1, "Chapter " + chapter);
            for (var section = 1; section <= 3; section++)
            {
                b.Heading(2, $"Section {chapter}.{section}");
                for (var p = 0; p < 9; p++)
                {
                    var para = b.P(Sentence(40) + " " + Sentence(35) + " " + Sentence(30));
                    if (p == 4 && section == 2) b.Note(para, Sentence(15));
                }
                if (section == 3)
                    b.Table(new List<string[]> { new[] { "Item", "Value", "Note" }, new[] { "A", "1", Sentence(6) }, new[] { "B", "2", Sentence(8) }, new[] { "C", "3", Sentence(5) } });
            }
        }
        return b.Doc;
    }

    [Fact]
    public void ThreeHundredPages_LoadLayoutTypeSave() => Sta.Run(() =>
    {
        var built = BigDocument();
        var bytes = Docx.Save(built);
        var sw = Stopwatch.StartNew();
        var doc = Docx.Load(bytes);
        var load = sw.Elapsed;

        sw.Restart();
        var layout = new DocumentLayout(doc, new ViewOptions());
        layout.LayoutAll();
        var fullLayout = sw.Elapsed;
        var pages = layout.Pages.Count;

        // Typing in the middle: the edit plus the incremental relayout, per keystroke.
        var editor = new DocEditor(doc);
        var middle = doc.Body.Paragraphs.ElementAt(doc.Body.Paragraphs.Count() / 2);
        editor.Selection = new Selection(new DocPos(middle, 10));
        var keystrokes = new List<double>();
        foreach (var c in "Typing in the middle of a long document.")
        {
            sw.Restart();
            editor.InsertText(c.ToString());
            layout.Update();
            keystrokes.Add(sw.Elapsed.TotalMilliseconds);
        }
        keystrokes.Sort();
        var median = keystrokes[keystrokes.Count / 2];
        var worst = keystrokes[keystrokes.Count - 1];

        // Enter (a new paragraph shifts everything after it).
        sw.Restart();
        editor.Enter();
        layout.Update();
        var enter = sw.Elapsed;

        sw.Restart();
        var saved = DocumentFiles.DocxBytes(doc);
        var save = sw.Elapsed;

        var stats = new DocumentStats().Count(doc);
        var report = $"300-page test ({System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}): {pages} pages, {stats.Words:N0} words, {saved.Length / 1024:N0} KB; " +
                     $"load {load.TotalMilliseconds:0} ms, full layout {fullLayout.TotalMilliseconds:0} ms, keystroke median {median:0.0} ms (worst {worst:0.0} ms), " +
                     $"Enter {enter.TotalMilliseconds:0} ms, save {save.TotalMilliseconds:0} ms";
        _out.WriteLine(report);
        var file = Environment.GetEnvironmentVariable("INKWELL_PERF_OUT");
        if (!string.IsNullOrEmpty(file)) File.AppendAllText(file!, report + Environment.NewLine);
        layout.Dispose();

        Assert.True(pages >= 250, "about 300 pages: " + pages);
        Assert.True(load.TotalSeconds < 20, "load " + load);
        Assert.True(fullLayout.TotalSeconds < 60, "layout " + fullLayout);
        Assert.True(median < 250, "keystroke median " + median);
        Assert.True(save.TotalSeconds < 20, "save " + save);
    });
}
