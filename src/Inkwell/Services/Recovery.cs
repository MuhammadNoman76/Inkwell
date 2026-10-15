using Inkwell.Core.IO;

namespace Inkwell.Services;

/// <summary>
/// Crash recovery: every few minutes (Settings › Keep a recovery copy) a copy of each document with unsaved
/// changes is written to %LOCALAPPDATA%\Inkwell\Recovery. Saving or closing normally deletes it; if Inkwell stops
/// unexpectedly, the start screen offers it the next time. Test and capture runs never write copies.
/// </summary>
internal static class Recovery
{
    private static readonly RecoveryStore Store = new(Path.Combine(AppSettings.LocalDirectory, "Recovery")) { OnError = Log.Error };

    public static string Folder => Store.Folder;

    public static Task<bool> SaveAsync(string key, byte[] bytes, string title, string? originalPath) =>
        AppSettings.InMemoryOnly ? Task.FromResult(false) : Store.SaveAsync(key, bytes, title, originalPath);

    public static void Discard(string key)
    {
        if (!AppSettings.InMemoryOnly) Store.Discard(key);
    }

    public static List<RecoveryEntry> FindOrphans() => AppSettings.InMemoryOnly ? new List<RecoveryEntry>() : Store.FindOrphans();

    public static byte[]? Read(string key) => Store.Read(key);

    public static bool Claim(string key) => Store.Claim(key);
}
