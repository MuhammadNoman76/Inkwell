using System.Security.Cryptography;
using Inkwell.Core.Proofing;

namespace Inkwell.Tests;

/// <summary>
/// The Windows spell checker. Skipped (passing) where the API isn't available. These tests never call
/// <see cref="WindowsSpellChecker.Add"/>: that would change the user's dictionary. They check that the dictionary
/// files under %APPDATA%\Microsoft\Spelling are byte-for-byte unchanged afterwards.
/// </summary>
public class SpellCheckerTests
{
    private readonly ITestOutputHelper _output;

    public SpellCheckerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static Dictionary<string, string> DictionarySnapshot()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Spelling");
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(folder)) return result;
        using var sha = SHA256.Create();
        foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
        {
            try
            {
                result[file] = Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(file)));
            }
            catch (IOException)
            {
                result[file] = "locked";
            }
        }
        return result;
    }

    [Fact]
    public void Checks_English_AndLeavesTheUserDictionaryAlone()
    {
        var before = DictionarySnapshot();
        var errors = new List<string>();
        using (var checker = new WindowsSpellChecker { OnError = (ex, what) => errors.Add(what + ": " + ex.Message) })
        {
            _output.WriteLine("Windows 8 or later: " + WindowsSpellChecker.IsSupportedOs);
            _output.WriteLine("Available: " + checker.IsAvailable);
            if (!checker.IsAvailable) return;
            _output.WriteLine("Supported languages: " + string.Join(", ", checker.SupportedLanguages));
            foreach (var tag in new[] { "en-US", "en-GB", "ur-PK", "ur", "ar-SA", "fr-FR", "de-DE", "es-ES", "hi-IN" })
                _output.WriteLine($"  IsSupported({tag}) = {checker.IsSupported(tag)}");
            if (!checker.IsSupported("en-US")) return;

            const string text = "This is a speling mistake.";
            var found = checker.Check(text, "en-US");
            var error = Assert.Single(found);
            Assert.Equal(10, error.Start);
            Assert.Equal(7, error.Length);
            Assert.Equal("speling", error.Word);
            Assert.Equal(SpellingAction.GetSuggestions, error.Action);
            var suggestions = checker.Suggest("speling", "en-US");
            _output.WriteLine("Suggestions for 'speling': " + string.Join(", ", suggestions));
            Assert.Contains("spelling", suggestions);

            Assert.Empty(checker.Check("This sentence is spelled correctly.", "en-US"));
            Assert.Empty(checker.Check("", "en-US"));

            // A repeated word asks for a deletion.
            var repeated = checker.Check("It was the the end.", "en-US");
            _output.WriteLine("Repeated word: " + string.Join("; ", repeated));
            Assert.Contains(repeated, e => e.Action == SpellingAction.Delete);

            // Offsets stay right with text the API would otherwise cut short.
            var withNul = checker.Check("Good\0 speling", "en-US");
            Assert.Equal(6, Assert.Single(withNul).Start);

            // Unknown languages give nothing, without throwing.
            Assert.False(checker.IsSupported("xx-XX"));
            Assert.False(checker.IsSupported(""));
            Assert.Empty(checker.Check("speling", "xx-XX"));
            Assert.Empty(checker.Suggest("speling", "xx-XX"));
        }
        Assert.Empty(errors);
        Assert.Equal(before, DictionarySnapshot());
    }

    [Fact]
    public void Ignore_LastsForTheSessionOnly()
    {
        const string word = "inkwellzqx";
        using (var checker = new WindowsSpellChecker())
        {
            if (!checker.IsAvailable || !checker.IsSupported("en-US")) return;
            Assert.Single(checker.Check("An " + word + " here.", "en-US"));
            checker.Ignore(word);
            Assert.Empty(checker.Check("An " + word + " here.", "en-US"));
            // Also for a language whose checker is created after the Ignore call.
            if (checker.IsSupported("en-GB")) Assert.Empty(checker.Check(word, "en-GB"));
        }
        using (var fresh = new WindowsSpellChecker())
        {
            Assert.Single(fresh.Check("An " + word + " here.", "en-US"));
        }
    }

    [Fact]
    public async Task SafeFromBackgroundThreads()
    {
        using var checker = new WindowsSpellChecker();
        if (!checker.IsAvailable || !checker.IsSupported("en-US")) return;
        var tasks = Enumerable.Range(0, 16).Select(i => Task.Run(() => checker.Check("Thread " + i + " has a speling error.", "en-US").Count));
        var counts = await Task.WhenAll(tasks);
        Assert.All(counts, n => Assert.Equal(1, n));
        Assert.Single(await checker.CheckAsync("Anothr one", "en-US"));
        // From an STA thread (the UI thread's apartment).
        var fromSta = Sta.Run(() => checker.Suggest("speling", "en-US"));
        Assert.Contains("spelling", fromSta);
    }

    [Fact]
    public void Urdu_WhenInstalled()
    {
        using var checker = new WindowsSpellChecker();
        if (!checker.IsAvailable) return;
        var tag = checker.IsSupported("ur-PK") ? "ur-PK" : checker.IsSupported("ur") ? "ur" : null;
        _output.WriteLine("Urdu: " + (tag ?? "not installed"));
        if (tag == null) return;
        var errors = checker.Check("یہ ایک جملہ ہے", tag);
        _output.WriteLine("Urdu errors: " + string.Join("; ", errors));
        Assert.Empty(errors);
    }

    [Fact]
    public void AfterDispose_NothingThrows()
    {
        var checker = new WindowsSpellChecker();
        _ = checker.IsAvailable;
        checker.Dispose();
        checker.Dispose();
        Assert.False(checker.IsAvailable);
        Assert.Empty(checker.Check("speling", "en-US"));
        Assert.Empty(checker.Suggest("speling", "en-US"));
        Assert.Empty(checker.SupportedLanguages);
        Assert.False(checker.IsSupported("en-US"));
        checker.Ignore("word");
        Assert.False(checker.Add("word", "en-US"));
        Assert.False(checker.Remove("word", "en-US"));
    }
}
