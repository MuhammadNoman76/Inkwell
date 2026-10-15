using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Inkwell.Core.Editing;

namespace Inkwell.Services;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AppTheme { System, Light, Dark }

public sealed class RecentDocument
{
    public string Path { get; set; } = "";
    public DateTime LastOpened { get; set; }
    public bool Pinned { get; set; }
}

public sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>
/// Preferences and recent documents, stored as JSON in %APPDATA%\Inkwell\settings.json.
/// Every change is applied to the file's current contents under a lock and written atomically.
/// </summary>
public sealed class AppSettings
{
    public const int MaxRecent = 30;

    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>In dark mode, pages are dark too (light text); otherwise pages stay white on a dark canvas.</summary>
    public bool DarkPaper { get; set; }

    /// <summary>Saves documents that have a file a few seconds after editing pauses (new documents are never saved automatically).</summary>
    public bool AutoSaveFile { get; set; } = true;

    /// <summary>Minutes between recovery copies of unsaved work (0 = off).</summary>
    public int RecoveryMinutes { get; set; } = 2;

    /// <summary>Keep local version history snapshots of saved documents.</summary>
    public bool VersionHistory { get; set; } = true;

    /// <summary>Snapshots kept per document, and the total size cap for all history (MB).</summary>
    public int HistoryPerDocument { get; set; } = 50;
    public int HistoryTotalMegabytes { get; set; } = 500;

    // Updates (see Updater): check the feed now and then, remember when, and a version the user chose to skip.
    public bool CheckForUpdates { get; set; } = true;
    public DateTime? LastUpdateCheck { get; set; }
    public string? SkippedVersion { get; set; }

    // Writing
    /// <summary>Your name on comments and tracked changes.</summary>
    public string? AuthorName { get; set; }
    public bool SpellCheck { get; set; } = true;

    /// <summary>
    /// Inkwell 1.0.0's one switch for both Markdown rules. Off in an old settings file turns the two rules off
    /// (<see cref="Normalize"/>); since 1.0.1 each rule has its own switch in Settings › Autocorrect.
    /// </summary>
    public bool MarkdownShortcuts { get; set; } = true;

    public bool SlashCommands { get; set; } = true;

    /// <summary>Autocorrect rules that differ from their defaults: "id" is off, "+id" is on (see AutoCorrectRules.SetOn).</summary>
    public List<string> DisabledAutoCorrect { get; set; } = new();

    /// <summary>Paste matches the destination's formatting (Keep source formatting stays one click away).</summary>
    public bool PasteMatchesDestination { get; set; } = true;

    /// <summary>Embed Inkwell's bundled fonts when saving .docx, so documents look the same on other PCs.</summary>
    public bool EmbedFonts { get; set; } = true;

    /// <summary>Design for new blank documents.</summary>
    public string DefaultDesign { get; set; } = "inkwell";

    /// <summary>Paper for new documents: auto (by region), a4, letter.</summary>
    public string DefaultPaper { get; set; } = "auto";

    // View
    public bool ShowOutline { get; set; } = true;
    public bool ShowPanel { get; set; } = true;
    public double Zoom { get; set; } = 1.0;
    public bool TypewriterScrolling { get; set; }
    public bool ShowFormattingMarks { get; set; }

    public string? LastFolder { get; set; }
    public string? LastExportFolder { get; set; }
    public WindowPlacement? Window { get; set; }
    public List<RecentDocument> Recent { get; set; } = new();

    /// <summary>The start screen's tour of what Inkwell fixes was dismissed.</summary>
    public bool TourDismissed { get; set; }

    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Inkwell");

    /// <summary>%LOCALAPPDATA%\Inkwell: version history, recovery copies, thumbnails.</summary>
    public static string LocalDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Inkwell");

    private static string FilePath => Path.Combine(DataDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static AppSettings? s_current;

    public static AppSettings Current => s_current ??= Load();

    public static event Action? Changed;

    /// <summary>Automated test runs keep settings in memory only (INKWELL_TEST_OFFSCREEN, --capture).</summary>
    public static bool InMemoryOnly => Views.MainWindow.IsTestOffscreen || CaptureRunner.Running;

    public string Author => string.IsNullOrWhiteSpace(AuthorName) ? Environment.UserName : AuthorName!.Trim();

    public static AppSettings Load()
    {
        if (InMemoryOnly)
        {
            s_current ??= new AppSettings();
            return s_current;
        }
        var settings = WithFileLock(ReadFile) ?? new AppSettings();
        settings.Normalize();
        s_current = settings;
        return settings;
    }

    /// <summary>Applies <paramref name="change"/> to the latest settings on disk and saves them.</summary>
    public static void Update(Action<AppSettings> change)
    {
        if (InMemoryOnly)
        {
            var current = Current;
            change(current);
            current.Normalize();
            Changed?.Invoke();
            return;
        }
        try
        {
            WithFileLock<object>(() =>
            {
                var settings = ReadFile() ?? s_current ?? new AppSettings();
                settings.Normalize();
                change(settings);
                settings.Normalize();
                Directory.CreateDirectory(DataDirectory);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions), new UTF8Encoding(false));
                Compat.MoveOverwrite(tmp, FilePath);
                s_current = settings;
                return null;
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Save settings");
            if (s_current != null) change(s_current);
        }
        Changed?.Invoke();
    }

    public static DateTime? ReadLastUpdateCheck() => InMemoryOnly ? Current.LastUpdateCheck : WithFileLock(ReadFile)?.LastUpdateCheck;

    public static void AddRecent(string path) => Update(s =>
    {
        var existing = s.Recent.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        s.Recent.RemoveAll(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        s.Recent.Insert(0, new RecentDocument { Path = path, LastOpened = DateTime.UtcNow, Pinned = existing?.Pinned ?? false });
    });

    public static void RemoveRecent(string path) => Update(s => s.Recent.RemoveAll(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Settings from the JSON of settings.json, tidied up (old settings brought up to date). Throws on invalid JSON.</summary>
    public static AppSettings Parse(string json)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        settings.Normalize();
        return settings;
    }

    private static AppSettings? ReadFile()
    {
        try
        {
            return File.Exists(FilePath) ? Parse(File.ReadAllText(FilePath, Encoding.UTF8)) : null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Read settings");
            return null;
        }
    }

    private static T? WithFileLock<T>(Func<T?> action)
    {
        using var mutex = new Mutex(false, @"Local\Inkwell-settings-" + Environment.UserName);
        var owned = false;
        try
        {
            try { owned = mutex.WaitOne(TimeSpan.FromSeconds(3)); }
            catch (AbandonedMutexException) { owned = true; }
            return action();
        }
        finally
        {
            if (owned) mutex.ReleaseMutex();
        }
    }

    private void Normalize()
    {
        Recent ??= new List<RecentDocument>();
        Recent = Recent.Where(r => r != null && !string.IsNullOrWhiteSpace(r.Path))
            .GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .ToList();
        var unpinned = Recent.Where(r => !r.Pinned).OrderByDescending(r => r.LastOpened).Skip(MaxRecent).ToList();
        foreach (var old in unpinned) Recent.Remove(old);
        DisabledAutoCorrect ??= new List<string>();
        DisabledAutoCorrect = DisabledAutoCorrect.Where(e => !string.IsNullOrWhiteSpace(e)).Distinct(StringComparer.Ordinal).ToList();
        if (!MarkdownShortcuts)
        {
            // 1.0.0 turned both Markdown rules off with one switch; they are rules with their own switches now.
            AutoCorrectRules.SetOn(DisabledAutoCorrect, AutoCorrectRules.MarkdownBlocks, false);
            AutoCorrectRules.SetOn(DisabledAutoCorrect, AutoCorrectRules.MarkdownInline, false);
            MarkdownShortcuts = true;
        }
        if (RecoveryMinutes < 0 || RecoveryMinutes > 60) RecoveryMinutes = 2;
        if (double.IsNaN(Zoom) || Zoom < 0.25 || Zoom > 5) Zoom = 1.0;
        if (HistoryPerDocument < 5 || HistoryPerDocument > 500) HistoryPerDocument = 50;
        if (HistoryTotalMegabytes < 50 || HistoryTotalMegabytes > 20000) HistoryTotalMegabytes = 500;
        DefaultDesign ??= "inkwell";
        DefaultPaper ??= "auto";
    }
}
