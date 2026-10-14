using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Inkwell.Core.IO;

/// <summary>Why a snapshot was taken.</summary>
public enum SnapshotKind
{
    /// <summary>The user saved the document.</summary>
    ManualSave,

    /// <summary>Taken automatically while editing (throttled).</summary>
    Automatic,

    /// <summary>The document as it was just before an older version was restored.</summary>
    BeforeRestore,
}

/// <summary>One saved version of a document.</summary>
public sealed class SnapshotInfo
{
    public string Id { get; set; } = "";
    public DateTime TimeUtc { get; set; }

    /// <summary>Size of the saved .docx in bytes.</summary>
    public long Size { get; set; }

    public int WordCount { get; set; }

    /// <summary>A name the user gave this version, or null.</summary>
    public string? Label { get; set; }

    public SnapshotKind Kind { get; set; }

    /// <summary>The document's file name when the snapshot was taken.</summary>
    public string FileName { get; set; } = "";

    /// <summary>SHA-256 of the bytes (hex), to skip identical snapshots.</summary>
    public string Sha256 { get; set; } = "";
}

public sealed class VersionHistoryOptions
{
    /// <summary>At most this many snapshots per document (the oldest go first).</summary>
    public int MaxSnapshotsPerDocument { get; set; } = 50;

    /// <summary>At most this many bytes per document.</summary>
    public long MaxBytesPerDocument { get; set; } = 100L * 1024 * 1024;

    /// <summary>At most this many bytes for all documents together.</summary>
    public long MaxTotalBytes { get; set; } = 500L * 1024 * 1024;

    /// <summary>Automatic snapshots are skipped when the newest snapshot is younger than this.</summary>
    public TimeSpan MinAutomaticInterval { get; set; } = TimeSpan.FromMinutes(10);
}

public enum SnapshotStatus
{
    Added,

    /// <summary>The bytes are identical to the newest snapshot.</summary>
    SkippedIdentical,

    /// <summary>An automatic snapshot within <see cref="VersionHistoryOptions.MinAutomaticInterval"/> of the newest one.</summary>
    SkippedTooSoon,

    /// <summary>It couldn't be written (reported through <see cref="VersionHistory.OnError"/>).</summary>
    Failed,
}

public sealed class SnapshotResult
{
    public SnapshotResult(SnapshotStatus status, SnapshotInfo? snapshot = null)
    {
        Status = status;
        Snapshot = snapshot;
    }

    public SnapshotStatus Status { get; }

    /// <summary>The new snapshot, or the newest existing one when skipped.</summary>
    public SnapshotInfo? Snapshot { get; }
}

/// <summary>A document with history, for a "manage history" view.</summary>
public sealed class HistoryDocument
{
    public string Key { get; set; } = "";
    public string? Path { get; set; }
    public int Count { get; set; }
    public long Size { get; set; }
    public DateTime? NewestUtc { get; set; }
}

/// <summary>
/// Local snapshots of saved documents ("version history"). Each document has a folder named by
/// <see cref="KeyFor"/> (the first 16 hex characters of the SHA-256 of its normalised full path) holding the saved
/// .docx files and an index.json. Writes are atomic (temporary file, then rename); a damaged or missing index is
/// rebuilt from the files. Every operation holds a named mutex, so several threads and several Inkwell processes can
/// use the same folder. Caps keep the history bounded: per document (count and bytes) and overall; pruning removes
/// the oldest snapshots first, unnamed ones before named ones, and never the newest snapshot of a document.
/// Nothing here throws: errors go to <see cref="OnError"/>.
/// </summary>
public sealed class VersionHistory
{
    private const string IndexFile = "index.json";
    private const string Extension = ".docx";
    private static readonly Regex IdPattern = new(@"^[0-9]{8}T[0-9]{9}Z-[0-9a-f]{4}$", RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _mutexName;

    public VersionHistory(string? root = null, VersionHistoryOptions? options = null)
    {
        Root = Path.GetFullPath(root ?? DefaultRoot);
        Options = options ?? new VersionHistoryOptions();
        _mutexName = "Local\\Inkwell.History." + Hex(Sha(Encoding.UTF8.GetBytes(Root.ToLowerInvariant()))).Substring(0, 16);
    }

    /// <summary>%LOCALAPPDATA%\Inkwell\History.</summary>
    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Inkwell", "History");

    public string Root { get; }

    public VersionHistoryOptions Options { get; }

    /// <summary>Called with errors (history must never interrupt saving).</summary>
    public Action<Exception, string>? OnError { get; set; }

    /// <summary>The folder name for a document: SHA-256 of the full path in lower case, first 16 hex characters.</summary>
    public static string KeyFor(string documentPath)
    {
        string normal;
        try
        {
            normal = Path.GetFullPath(documentPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            normal = documentPath;
        }
        normal = normal.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
        return Hex(Sha(Encoding.UTF8.GetBytes(normal))).Substring(0, 16);
    }

    public string FolderFor(string documentPath) => Path.Combine(Root, KeyFor(documentPath));

    // ---- public operations ----

    /// <summary>Takes a snapshot of the saved bytes, unless they equal the newest snapshot or an automatic one comes too soon.</summary>
    public SnapshotResult Add(string documentPath, byte[] bytes, SnapshotKind kind, int wordCount = 0, string? label = null, DateTime? nowUtc = null) =>
        Locked(() =>
        {
            var now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
            var folder = FolderFor(documentPath);
            Directory.CreateDirectory(folder);
            var index = Load(folder, documentPath);
            var newest = index.Snapshots.Count > 0 ? index.Snapshots[index.Snapshots.Count - 1] : null;
            var hash = Hex(Sha(bytes));
            if (newest != null && newest.Size == bytes.LongLength && string.Equals(newest.Sha256, hash, StringComparison.OrdinalIgnoreCase))
                return new SnapshotResult(SnapshotStatus.SkippedIdentical, newest);
            if (kind == SnapshotKind.Automatic && newest != null && now >= newest.TimeUtc && now - newest.TimeUtc < Options.MinAutomaticInterval)
                return new SnapshotResult(SnapshotStatus.SkippedTooSoon, newest);

            var id = NewId(folder, now);
            WriteAtomic(Path.Combine(folder, id + Extension), bytes);
            var info = new SnapshotInfo
            {
                Id = id,
                TimeUtc = now,
                Size = bytes.LongLength,
                WordCount = wordCount,
                Label = CleanLabel(label),
                Kind = kind,
                FileName = Path.GetFileName(documentPath),
                Sha256 = hash,
            };
            index.Path = SafeFullPath(documentPath);
            index.Snapshots.Add(info);
            PruneDocument(folder, index);
            Save(folder, index);
            PruneAll();
            return new SnapshotResult(SnapshotStatus.Added, info);
        }, new SnapshotResult(SnapshotStatus.Failed), "Add a version");

    /// <summary>The snapshots of a document, newest first.</summary>
    public List<SnapshotInfo> List(string documentPath) =>
        Locked(() =>
        {
            var folder = FolderFor(documentPath);
            if (!Directory.Exists(folder)) return new List<SnapshotInfo>();
            var list = Load(folder, documentPath).Snapshots.ToList();
            list.Reverse();
            return list;
        }, new List<SnapshotInfo>(), "List versions");

    /// <summary>The bytes of a snapshot, or null.</summary>
    public byte[]? Read(string documentPath, string snapshotId) =>
        Locked<byte[]?>(() =>
        {
            if (!IdPattern.IsMatch(snapshotId ?? "")) return null;
            var file = Path.Combine(FolderFor(documentPath), snapshotId + Extension);
            return File.Exists(file) ? File.ReadAllBytes(file) : null;
        }, null, "Read a version");

    public bool Delete(string documentPath, string snapshotId) =>
        Locked(() =>
        {
            var folder = FolderFor(documentPath);
            if (!Directory.Exists(folder)) return false;
            var index = Load(folder, documentPath);
            var entry = index.Snapshots.FirstOrDefault(s => s.Id == snapshotId);
            if (entry == null) return false;
            DeleteFile(Path.Combine(folder, entry.Id + Extension));
            index.Snapshots.Remove(entry);
            if (index.Snapshots.Count == 0) Directory.Delete(folder, true);
            else Save(folder, index);
            return true;
        }, false, "Delete a version");

    /// <summary>Deletes every snapshot of a document.</summary>
    public bool DeleteAll(string documentPath) =>
        Locked(() =>
        {
            var folder = FolderFor(documentPath);
            if (!Directory.Exists(folder)) return false;
            Directory.Delete(folder, true);
            return true;
        }, false, "Delete the history");

    /// <summary>Names a snapshot (null or blank removes the name).</summary>
    public bool SetLabel(string documentPath, string snapshotId, string? label) =>
        Locked(() =>
        {
            var folder = FolderFor(documentPath);
            if (!Directory.Exists(folder)) return false;
            var index = Load(folder, documentPath);
            var entry = index.Snapshots.FirstOrDefault(s => s.Id == snapshotId);
            if (entry == null) return false;
            entry.Label = CleanLabel(label);
            Save(folder, index);
            return true;
        }, false, "Name a version");

    /// <summary>Moves a document's history after it was renamed or saved under a new name (merging with history already there).</summary>
    public bool MoveHistory(string oldPath, string newPath) =>
        Locked(() =>
        {
            var from = FolderFor(oldPath);
            var to = FolderFor(newPath);
            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return true;
            if (!Directory.Exists(from)) return false;
            var moving = Load(from, oldPath);
            if (!Directory.Exists(to))
            {
                Directory.Move(from, to);
                moving.Path = SafeFullPath(newPath);
                Save(to, moving);
                return true;
            }
            var target = Load(to, newPath);
            foreach (var s in moving.Snapshots)
            {
                if (target.Snapshots.Any(t => t.Id == s.Id)) continue;
                Compat.MoveOverwrite(Path.Combine(from, s.Id + Extension), Path.Combine(to, s.Id + Extension));
                target.Snapshots.Add(s);
            }
            target.Snapshots.Sort((a, b) => a.TimeUtc != b.TimeUtc ? a.TimeUtc.CompareTo(b.TimeUtc) : string.CompareOrdinal(a.Id, b.Id));
            target.Path = SafeFullPath(newPath);
            PruneDocument(to, target);
            Save(to, target);
            Directory.Delete(from, true);
            return true;
        }, false, "Move the history");

    /// <summary>Every document with history.</summary>
    public List<HistoryDocument> Documents() =>
        Locked(() =>
        {
            var result = new List<HistoryDocument>();
            if (!Directory.Exists(Root)) return result;
            foreach (var folder in Directory.GetDirectories(Root))
            {
                var index = Load(folder, null);
                if (index.Snapshots.Count == 0) continue;
                result.Add(new HistoryDocument
                {
                    Key = Path.GetFileName(folder),
                    Path = index.Path,
                    Count = index.Snapshots.Count,
                    Size = index.Snapshots.Sum(s => s.Size),
                    NewestUtc = index.Snapshots[index.Snapshots.Count - 1].TimeUtc,
                });
            }
            return result.OrderByDescending(d => d.NewestUtc).ToList();
        }, new List<HistoryDocument>(), "List documents");

    /// <summary>The bytes used by all snapshots.</summary>
    public long TotalBytes() => Locked(SnapshotBytes, 0L, "Measure the history");

    // ---- index ----

    private sealed class HistoryIndex
    {
        public int Version { get; set; } = 1;
        public string? Path { get; set; }
        public List<SnapshotInfo> Snapshots { get; set; } = new();
    }

    /// <summary>Reads a folder's index, rebuilding it from the files if it is missing or damaged, and reconciling it
    /// with the files (entries without a file are dropped, files without an entry are added).</summary>
    private HistoryIndex Load(string folder, string? documentPath)
    {
        HistoryIndex? index = null;
        var indexPath = Path.Combine(folder, IndexFile);
        if (File.Exists(indexPath))
        {
            try
            {
                index = JsonSerializer.Deserialize<HistoryIndex>(File.ReadAllText(indexPath, Encoding.UTF8), Json);
                if (index?.Snapshots == null) throw new JsonException("The index has no snapshot list.");
                index.Snapshots.RemoveAll(s => s == null || !IdPattern.IsMatch(s.Id ?? ""));
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
            {
                Report(ex, "The version history index of " + (documentPath ?? folder) + " was damaged and has been rebuilt");
                index = null;
            }
        }
        var changed = index == null;
        index ??= new HistoryIndex { Path = documentPath == null ? null : SafeFullPath(documentPath) };

        var files = Directory.GetFiles(folder, "*" + Extension)
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .Where(id => IdPattern.IsMatch(id))
            .ToList();
        var known = new HashSet<string>(index.Snapshots.Select(s => s.Id), StringComparer.Ordinal);
        if (index.Snapshots.RemoveAll(s => !files.Contains(s.Id)) > 0) changed = true;
        foreach (var id in files.Where(id => !known.Contains(id)))
        {
            var file = Path.Combine(folder, id + Extension);
            var bytes = File.ReadAllBytes(file);
            index.Snapshots.Add(new SnapshotInfo
            {
                Id = id,
                TimeUtc = TimeOfId(id) ?? File.GetLastWriteTimeUtc(file),
                Size = bytes.LongLength,
                Kind = SnapshotKind.Automatic,
                FileName = documentPath != null ? Path.GetFileName(documentPath) : index.Path != null ? Path.GetFileName(index.Path) : "",
                Sha256 = Hex(Sha(bytes)),
            });
            changed = true;
        }
        if (changed)
        {
            index.Snapshots.Sort((a, b) => a.TimeUtc != b.TimeUtc ? a.TimeUtc.CompareTo(b.TimeUtc) : string.CompareOrdinal(a.Id, b.Id));
            Save(folder, index);
        }
        // Leftovers of interrupted writes.
        foreach (var tmp in Directory.GetFiles(folder, "*.tmp")) DeleteFile(tmp);
        return index;
    }

    private static void Save(string folder, HistoryIndex index)
    {
        var json = JsonSerializer.Serialize(index, Json);
        WriteAtomic(Path.Combine(folder, IndexFile), new UTF8Encoding(false).GetBytes(json));
    }

    // ---- caps ----

    /// <summary>Removes the oldest snapshots (unnamed first) until the document fits its caps; the newest always stays.</summary>
    private void PruneDocument(string folder, HistoryIndex index)
    {
        var maxCount = Math.Max(1, Options.MaxSnapshotsPerDocument);
        while (index.Snapshots.Count > 1 &&
               (index.Snapshots.Count > maxCount || index.Snapshots.Sum(s => s.Size) > Options.MaxBytesPerDocument))
        {
            var candidates = index.Snapshots.Take(index.Snapshots.Count - 1).ToList();
            var victim = candidates.FirstOrDefault(s => s.Label == null) ?? candidates[0];
            DeleteFile(Path.Combine(folder, victim.Id + Extension));
            index.Snapshots.Remove(victim);
        }
    }

    /// <summary>Removes the oldest snapshots of all documents (unnamed first, never a document's newest) until the total fits.</summary>
    private void PruneAll()
    {
        var total = SnapshotBytes();
        if (total <= Options.MaxTotalBytes) return;
        var indexes = new List<(string Folder, HistoryIndex Index)>();
        foreach (var folder in Directory.GetDirectories(Root)) indexes.Add((folder, Load(folder, null)));
        var candidates = indexes
            .SelectMany(x => x.Index.Snapshots.Take(Math.Max(0, x.Index.Snapshots.Count - 1)).Select(s => (x.Folder, x.Index, Snapshot: s)))
            .OrderBy(c => c.Snapshot.Label == null ? 0 : 1)
            .ThenBy(c => c.Snapshot.TimeUtc)
            .ToList();
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates)
        {
            if (total <= Options.MaxTotalBytes) break;
            DeleteFile(Path.Combine(c.Folder, c.Snapshot.Id + Extension));
            c.Index.Snapshots.Remove(c.Snapshot);
            total -= c.Snapshot.Size;
            changed.Add(c.Folder);
        }
        foreach (var (folder, index) in indexes)
            if (changed.Contains(folder)) Save(folder, index);
    }

    private long SnapshotBytes()
    {
        if (!Directory.Exists(Root)) return 0;
        long total = 0;
        foreach (var folder in Directory.GetDirectories(Root))
            foreach (var file in Directory.GetFiles(folder, "*" + Extension))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (IOException)
                {
                    // deleted meanwhile
                }
            }
        return total;
    }

    // ---- helpers ----

    private T Locked<T>(Func<T> action, T fallback, string what)
    {
        Mutex? mutex = null;
        var owned = false;
        try
        {
            mutex = new Mutex(false, _mutexName);
            try
            {
                owned = mutex.WaitOne(TimeSpan.FromSeconds(15));
            }
            catch (AbandonedMutexException)
            {
                owned = true; // another process died while holding it; the files are still consistent (atomic writes)
            }
            if (!owned)
            {
                Report(new TimeoutException("The version history is busy."), what);
                return fallback;
            }
            return action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException or
                                   System.Security.SecurityException or WaitHandleCannotBeOpenedException or InvalidOperationException)
        {
            Report(ex, what);
            return fallback;
        }
        finally
        {
            if (owned) mutex!.ReleaseMutex();
            mutex?.Dispose();
        }
    }

    private void Report(Exception ex, string what)
    {
        try
        {
            OnError?.Invoke(ex, what);
        }
        catch
        {
            // a failing error handler must not break saving
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
            DeleteFile(tmp);
        }
    }

    private static void DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // in use: removed next time
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string NewId(string folder, DateTime now)
    {
        while (true)
        {
            var id = now.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture) + "-" + Compat.NextRandom(0, 0x10000).ToString("x4", CultureInfo.InvariantCulture);
            if (!File.Exists(Path.Combine(folder, id + Extension))) return id;
        }
    }

    private static DateTime? TimeOfId(string id) =>
        DateTime.TryParseExact(id.Substring(0, 19), "yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    private static string? CleanLabel(string? label) => string.IsNullOrWhiteSpace(label) ? null : label!.Trim();

    private static string SafeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return path;
        }
    }

    private static byte[] Sha(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(bytes);
    }

    private static string Hex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
