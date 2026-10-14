using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using static Inkwell.Core.Proofing.SpellCheckInterop;

namespace Inkwell.Core.Proofing;

/// <summary>What the spell checker suggests doing about an error (CORRECTIVE_ACTION).</summary>
public enum SpellingAction
{
    None = 0,

    /// <summary>A misspelling: offer <see cref="WindowsSpellChecker.Suggest"/>.</summary>
    GetSuggestions = 1,

    /// <summary>Replace with <see cref="SpellingError.Replacement"/> (an autocorrect pair).</summary>
    Replace = 2,

    /// <summary>Delete the text (a repeated word: "the the"). Windows marks only the word; the editor should also
    /// remove the space before it.</summary>
    Delete = 3,
}

/// <summary>One spelling error in the checked text (offsets in UTF-16 code units, as in .NET strings).</summary>
public sealed class SpellingError
{
    public SpellingError(int start, int length, SpellingAction action, string? replacement, string word)
    {
        Start = start;
        Length = length;
        Action = action;
        Replacement = replacement;
        Word = word;
    }

    public int Start { get; }
    public int Length { get; }
    public int End => Start + Length;
    public SpellingAction Action { get; }

    /// <summary>The replacement for <see cref="SpellingAction.Replace"/>; null otherwise.</summary>
    public string? Replacement { get; }

    /// <summary>The flagged text.</summary>
    public string Word { get; }

    public override string ToString() => $"{Word} [{Start},{End}) {Action}" + (Replacement != null ? " → " + Replacement : "");
}

/// <summary>
/// The Windows spell checker (Windows 8 and later; the same dictionaries as the rest of Windows, including words
/// the user added). On Windows 7, or if the API can't be created, <see cref="IsAvailable"/> is false and every
/// method returns an empty result: nothing here throws.
/// <para>
/// Threading: all COM calls run on one private MTA thread (the factory is free-threaded), so every public method
/// can be called from any thread, including the UI thread and background workers. Calls are serialised and the
/// synchronous ones block until done (a check of a paragraph takes well under a millisecond); the *Async variants
/// don't block. Create one instance for the application and dispose it on exit; spell checkers are cached per
/// language.
/// </para>
/// </summary>
public sealed class WindowsSpellChecker : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private readonly Dictionary<string, ISpellChecker?> _checkers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _ignored = new();
    private ISpellCheckerFactory? _factory;
    private bool _initialised;
    private bool _available;
    private List<string> _languages = new();
    private volatile bool _disposed;

    public WindowsSpellChecker()
    {
        _thread = new Thread(Worker) { IsBackground = true, Name = "Inkwell spell checker" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>Called with errors from the API (they are never thrown).</summary>
    public Action<Exception, string>? OnError { get; set; }

    /// <summary>The Spell Checking API exists on this version of Windows (8 or later).</summary>
    public static bool IsSupportedOs
    {
        get
        {
            var v = Environment.OSVersion;
            return v.Platform == PlatformID.Win32NT && v.Version >= new Version(6, 2);
        }
    }

    /// <summary>True when the spell checker could be created.</summary>
    public bool IsAvailable => Run(() => _available, false, "Initialise");

    /// <summary>The language tags Windows can check ("en-US", "fr-FR"…), depending on the language packs installed.</summary>
    public IReadOnlyList<string> SupportedLanguages => Run(() => (IReadOnlyList<string>)_languages.ToList(), Array.Empty<string>(), "Supported languages");

    public bool IsSupported(string languageTag)
    {
        if (string.IsNullOrWhiteSpace(languageTag)) return false;
        return Run(() =>
        {
            if (_factory == null) return false;
            var hr = _factory.IsSupported(languageTag, out var supported);
            return hr == S_OK && supported != 0;
        }, false, "IsSupported " + languageTag);
    }

    /// <summary>The spelling errors in <paramref name="text"/> (empty when the language isn't supported).</summary>
    public IReadOnlyList<SpellingError> Check(string text, string languageTag) =>
        Run(() => CheckCore(text, languageTag), Array.Empty<SpellingError>(), "Check");

    public Task<IReadOnlyList<SpellingError>> CheckAsync(string text, string languageTag) =>
        RunAsync(() => CheckCore(text, languageTag), Array.Empty<SpellingError>(), "Check");

    /// <summary>Suggestions for a word, best first (at most <paramref name="max"/>).</summary>
    public IReadOnlyList<string> Suggest(string word, string languageTag, int max = 8) =>
        Run(() => SuggestCore(word, languageTag, max), Array.Empty<string>(), "Suggest");

    public Task<IReadOnlyList<string>> SuggestAsync(string word, string languageTag, int max = 8) =>
        RunAsync(() => SuggestCore(word, languageTag, max), Array.Empty<string>(), "Suggest");

    /// <summary>Ignores a word in every language until this instance is disposed (nothing is saved).</summary>
    public void Ignore(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return;
        Run(() =>
        {
            if (!_ignored.Contains(word)) _ignored.Add(word);
            foreach (var checker in _checkers.Values)
                if (checker != null) checker.Ignore(word);
            return true;
        }, false, "Ignore");
    }

    /// <summary>
    /// Adds a word to the user's dictionary for a language. Windows saves it (other apps see it too), so call this
    /// only when the user asks ("Add to dictionary").
    /// </summary>
    public bool Add(string word, string languageTag)
    {
        if (string.IsNullOrWhiteSpace(word)) return false;
        return Run(() =>
        {
            var checker = Checker(languageTag);
            return checker != null && Succeeded(checker.Add(word), "Add");
        }, false, "Add");
    }

    /// <summary>Removes a word the user added (Windows 10 and later; false where it isn't possible).</summary>
    public bool Remove(string word, string languageTag)
    {
        if (string.IsNullOrWhiteSpace(word)) return false;
        return Run(() =>
        {
            if (Checker(languageTag) is not ISpellChecker2 checker2) return false;
            return Succeeded(checker2.Remove(word), "Remove");
        }, false, "Remove");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _queue.CompleteAdding();
        }
        catch (ObjectDisposedException)
        {
        }
        if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(5));
    }

    // ---- worker thread ----

    private void Worker()
    {
        try
        {
            foreach (var work in _queue.GetConsumingEnumerable()) work();
        }
        finally
        {
            foreach (var checker in _checkers.Values) Release(checker);
            _checkers.Clear();
            Release(_factory);
            _factory = null;
            _queue.Dispose();
        }
    }

    private void EnsureFactory()
    {
        if (_initialised) return;
        _initialised = true;
        if (!IsSupportedOs) return;
        try
        {
            var type = Type.GetTypeFromCLSID(FactoryClsid, throwOnError: false);
            if (type == null) return;
            _factory = Activator.CreateInstance(type) as ISpellCheckerFactory;
            if (_factory == null) return;
            if (_factory.get_SupportedLanguages(out var languages) == S_OK)
            {
                _languages = ReadAll(languages);
                Release(languages);
            }
            _available = true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or TypeLoadException or UnauthorizedAccessException or
                                   NotSupportedException or System.Reflection.TargetInvocationException or MissingMethodException)
        {
            Report(ex, "Create the spell checker");
            Release(_factory);
            _factory = null;
            _available = false;
        }
    }

    private ISpellChecker? Checker(string languageTag)
    {
        if (_factory == null || string.IsNullOrWhiteSpace(languageTag)) return null;
        if (_checkers.TryGetValue(languageTag, out var cached)) return cached;
        ISpellChecker? checker = null;
        if (_factory.IsSupported(languageTag, out var supported) == S_OK && supported != 0 &&
            _factory.CreateSpellChecker(languageTag, out checker) == S_OK && checker != null)
        {
            foreach (var word in _ignored) checker.Ignore(word);
        }
        else
        {
            Release(checker);
            checker = null;
        }
        _checkers[languageTag] = checker;
        return checker;
    }

    private IReadOnlyList<SpellingError> CheckCore(string text, string languageTag)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<SpellingError>();
        var checker = Checker(languageTag);
        if (checker == null) return Array.Empty<SpellingError>();
        // The API reads a NUL-terminated string: keep the offsets but never cut the text short.
        var safe = text.IndexOf('\0') >= 0 ? text.Replace('\0', ' ') : text;
        if (!Succeeded(checker.Check(safe, out var errors), "Check") || errors == null) return Array.Empty<SpellingError>();
        var list = new List<SpellingError>();
        try
        {
            while (errors.Next(out var error) == S_OK && error != null)
            {
                try
                {
                    error.get_StartIndex(out var start);
                    error.get_Length(out var length);
                    error.get_CorrectiveAction(out var action);
                    error.get_Replacement(out var replacementPtr);
                    var replacement = TakeString(replacementPtr);
                    var s = (int)Math.Min(start, (uint)text.Length);
                    var l = (int)Math.Min(length, (uint)(text.Length - s));
                    var kind = action is >= 0 and <= 3 ? (SpellingAction)action : SpellingAction.None;
                    list.Add(new SpellingError(s, l, kind, kind == SpellingAction.Replace && !string.IsNullOrEmpty(replacement) ? replacement : null, text.Substring(s, l)));
                }
                finally
                {
                    Release(error);
                }
            }
        }
        finally
        {
            Release(errors);
        }
        return list;
    }

    private IReadOnlyList<string> SuggestCore(string word, string languageTag, int max)
    {
        if (string.IsNullOrWhiteSpace(word)) return Array.Empty<string>();
        var checker = Checker(languageTag);
        if (checker == null) return Array.Empty<string>();
        var hr = checker.Suggest(word.Replace('\0', ' '), out var suggestions);
        if (hr < 0 || suggestions == null) return Array.Empty<string>();
        try
        {
            return ReadAll(suggestions, Math.Max(0, max));
        }
        finally
        {
            Release(suggestions);
        }
    }

    private bool Succeeded(int hr, string what)
    {
        if (hr >= 0) return true;
        Report(Marshal.GetExceptionForHR(hr) ?? new COMException(what, hr), what);
        return false;
    }

    private void Report(Exception ex, string what)
    {
        try
        {
            OnError?.Invoke(ex, "Spell checker: " + what);
        }
        catch
        {
            // the error callback must not break the worker
        }
    }

    // ---- calls onto the worker ----

    private T Run<T>(Func<T> func, T fallback, string what) => RunAsync(func, fallback, what).GetAwaiter().GetResult();

    private Task<T> RunAsync<T>(Func<T> func, T fallback, string what)
    {
        if (_disposed) return Task.FromResult(fallback);
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Work()
        {
            try
            {
                EnsureFactory();
                done.SetResult(_available ? func() : fallback);
            }
            catch (Exception ex)
            {
                Report(ex, what);
                done.SetResult(fallback);
            }
        }
        if (Thread.CurrentThread == _thread)
        {
            Work();
            return done.Task;
        }
        try
        {
            _queue.Add(Work);
        }
        catch (InvalidOperationException)
        {
            return Task.FromResult(fallback); // disposed meanwhile
        }
        return done.Task;
    }
}
