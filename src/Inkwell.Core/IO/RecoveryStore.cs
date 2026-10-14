using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Inkwell.Core.IO;

/// <summary>A document that was being edited when Inkwell closed unexpectedly.</summary>
public sealed class RecoveryEntry
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string? OriginalPath { get; set; }
    public DateTime SavedUtc { get; set; }
    public long Size { get; set; }

    /// <summary>The Inkwell process that wrote the copy.</summary>
    public int ProcessId { get; set; }

    public DateTime ProcessStartUtc { get; set; }

    public string? ProcessName { get; set; }

    /// <summary>Set when the entry is read back: the recovery copy (.docx).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string DocumentPath { get; set; } = "";
}

/// <summary>
/// Crash recovery copies in one folder: <c>&lt;key&gt;.docx</c> (the bytes the caller serialised) plus
/// <c>&lt;key&gt;.json</c> (title, original path, time, and the process that wrote it). Copies whose process is still
/// running belong to another open Inkwell window and aren't offered. Both files are written atomically. Errors are
/// reported through <see cref="OnError"/> and never thrown into the editor.
/// </summary>
public sealed class RecoveryStore
{
    private static readonly Regex KeyPattern = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);
    private static readonly (DateTime Start, string Name) s_process = CurrentProcess();
    private readonly object _gate = new();

    public RecoveryStore(string? folder = null)
    {
        Folder = folder ?? DefaultFolder;
    }

    /// <summary>%LOCALAPPDATA%\Inkwell\Recovery.</summary>
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Inkwell", "Recovery");

    public string Folder { get; }

    /// <summary>Called with errors (recovery copies must never interrupt editing).</summary>
    public Action<Exception, string>? OnError { get; set; }

    /// <summary>A new key for a document window.</summary>
    public static string NewKey() => Guid.NewGuid().ToString("N");

    public string DocumentPathOf(string key) => Path.Combine(Folder, key + ".docx");

    private string EntryPathOf(string key) => Path.Combine(Folder, key + ".json");

    /// <summary>Writes a recovery copy in the background (the caller passes bytes it already serialised on its own thread).</summary>
    public Task<bool> SaveAsync(string key, byte[] bytes, string title, string? originalPath) =>
        Task.Run(() => Save(key, bytes, title, originalPath));

    /// <summary>Writes a recovery copy now. False (and the error reported) if it couldn't be written.</summary>
    public bool Save(string key, byte[] bytes, string title, string? originalPath, int? processId = null, DateTime? processStartUtc = null)
    {
        if (!ValidKey(key, "Recovery save")) return false;
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Folder);
                WriteAtomic(DocumentPathOf(key), bytes);
                var entry = new RecoveryEntry
                {
                    Key = key,
                    Title = title ?? "",
                    OriginalPath = originalPath,
                    SavedUtc = DateTime.UtcNow,
                    Size = bytes.LongLength,
                    ProcessId = processId ?? Compat.ProcessId,
                    ProcessStartUtc = processStartUtc ?? s_process.Start,
                    ProcessName = s_process.Name,
                };
                var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true });
                WriteAtomic(EntryPathOf(key), new UTF8Encoding(false).GetBytes(json));
            }
            return true;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Report(ex, "Recovery save");
            return false;
        }
    }

    /// <summary>The bytes of a recovery copy, or null.</summary>
    public byte[]? Read(string key)
    {
        if (!ValidKey(key, "Recovery read")) return null;
        try
        {
            var path = DocumentPathOf(key);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Report(ex, "Recovery read");
            return null;
        }
    }

    /// <summary>Makes a copy this process's own (after the user reopens it), so another Inkwell doesn't offer it too.</summary>
    public bool Claim(string key)
    {
        if (!ValidKey(key, "Recovery claim")) return false;
        try
        {
            lock (_gate)
            {
                var path = EntryPathOf(key);
                if (!File.Exists(path)) return false;
                var entry = JsonSerializer.Deserialize<RecoveryEntry>(File.ReadAllText(path, Encoding.UTF8));
                if (entry == null) return false;
                entry.ProcessId = Compat.ProcessId;
                entry.ProcessStartUtc = s_process.Start;
                entry.ProcessName = s_process.Name;
                WriteAtomic(path, new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true })));
                return true;
            }
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Report(ex, "Recovery claim");
            return false;
        }
    }

    /// <summary>Deletes a recovery copy (after a normal save or close, or when the user declines it).</summary>
    public void Discard(string key)
    {
        if (!ValidKey(key, "Recovery discard")) return;
        lock (_gate)
        {
            var files = new List<string> { EntryPathOf(key), DocumentPathOf(key) };
            try
            {
                if (Directory.Exists(Folder)) files.AddRange(Directory.GetFiles(Folder, key + ".*.tmp"));
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                Report(ex, "Recovery discard");
            }
            foreach (var file in files)
            {
                try
                {
                    if (File.Exists(file)) File.Delete(file);
                }
                catch (Exception ex) when (IsExpected(ex))
                {
                    Report(ex, "Recovery discard");
                }
            }
        }
    }

    /// <summary>Recovery copies left behind by Inkwell processes that are no longer running, newest first.</summary>
    public List<RecoveryEntry> FindOrphans()
    {
        var result = new List<RecoveryEntry>();
        try
        {
            if (!Directory.Exists(Folder)) return result;
            foreach (var file in Directory.GetFiles(Folder, "*.json"))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<RecoveryEntry>(File.ReadAllText(file, Encoding.UTF8));
                    if (entry?.Key is not { } key || !KeyPattern.IsMatch(key)) continue;
                    if (!string.Equals(Path.GetFileNameWithoutExtension(file), key, StringComparison.OrdinalIgnoreCase)) continue;
                    entry.DocumentPath = DocumentPathOf(key);
                    if (!File.Exists(entry.DocumentPath) || IsAlive(entry)) continue;
                    result.Add(entry);
                }
                catch (Exception ex) when (IsExpected(ex) || ex is JsonException)
                {
                    Report(ex, "Read recovery " + Path.GetFileName(file));
                }
            }
            // Temporary files of interrupted writes (older than a day, so a write in progress is never touched).
            foreach (var tmp in Directory.GetFiles(Folder, "*.tmp"))
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(tmp) > TimeSpan.FromDays(1)) File.Delete(tmp);
                }
                catch (Exception ex) when (IsExpected(ex))
                {
                    // in use
                }
            }
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Report(ex, "Find recovery copies");
        }
        return result.OrderByDescending(e => e.SavedUtc).ToList();
    }

    /// <summary>The process that wrote the entry still runs (same id and start time; or, when its start time can't be
    /// read, a process of the same name: better not to offer a copy that may be open).</summary>
    private static bool IsAlive(RecoveryEntry entry)
    {
        if (entry.ProcessId <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(entry.ProcessId);
            try
            {
                var start = p.StartTime.ToUniversalTime();
                return Math.Abs((start - entry.ProcessStartUtc.ToUniversalTime()).TotalSeconds) < 2;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            {
                return entry.ProcessName != null && string.Equals(p.ProcessName, entry.ProcessName, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false; // not running
        }
    }

    private static (DateTime, string) CurrentProcess()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            return (p.StartTime.ToUniversalTime(), p.ProcessName);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return (DateTime.UtcNow, "");
        }
    }

    private bool ValidKey(string key, string what)
    {
        if (key != null && KeyPattern.IsMatch(key)) return true;
        Report(new ArgumentException("Invalid recovery key: " + key), what);
        return false;
    }

    private static bool IsExpected(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.Security.SecurityException or JsonException;

    private void Report(Exception ex, string what)
    {
        try
        {
            OnError?.Invoke(ex, what);
        }
        catch
        {
            // a failing error handler must not break editing
        }
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            Compat.MoveOverwrite(tmp, path);
        }
        finally
        {
            try
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
