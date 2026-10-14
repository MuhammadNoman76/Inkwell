using System.Diagnostics;
using System.Text;
using Inkwell.Core.IO;

namespace Inkwell.Tests;

/// <summary>Crash-recovery copies. Everything under %TEMP%\inkwell-tests-*.</summary>
public class RecoveryStoreTests
{
    /// <summary>A process id no running process has.</summary>
    private const int DeadPid = 2_000_000_000;

    private static (RecoveryStore Store, List<string> Errors) Make(TempDir dir)
    {
        var errors = new List<string>();
        return (new RecoveryStore(dir.File("Recovery")) { OnError = (ex, what) => errors.Add(what + ": " + ex.Message) }, errors);
    }

    private static DateTime CurrentStart()
    {
        using var p = Process.GetCurrentProcess();
        return p.StartTime.ToUniversalTime();
    }

    [Fact]
    public void CopiesOfDeadProcesses_AreOrphans()
    {
        using var dir = new TempDir();
        var (store, errors) = Make(dir);
        var key = RecoveryStore.NewKey();
        Assert.True(store.Save(key, Encoding.UTF8.GetBytes("docx bytes"), "Essay", @"C:\Docs\Essay.docx", DeadPid, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.True(File.Exists(store.DocumentPathOf(key)));
        Assert.True(File.Exists(Path.Combine(store.Folder, key + ".json")));
        Assert.Empty(Directory.GetFiles(store.Folder, "*.tmp"));

        var orphan = Assert.Single(store.FindOrphans());
        Assert.Equal(key, orphan.Key);
        Assert.Equal("Essay", orphan.Title);
        Assert.Equal(@"C:\Docs\Essay.docx", orphan.OriginalPath);
        Assert.Equal(10, orphan.Size);
        Assert.Equal(store.DocumentPathOf(key), orphan.DocumentPath);
        Assert.True((DateTime.UtcNow - orphan.SavedUtc).TotalMinutes < 5);
        Assert.Equal("docx bytes", Encoding.UTF8.GetString(store.Read(key)!));

        store.Discard(key);
        Assert.Empty(store.FindOrphans());
        Assert.Null(store.Read(key));
        Assert.Empty(Directory.GetFiles(store.Folder));
        Assert.Empty(errors);
    }

    [Fact]
    public void CopiesOfRunningProcesses_AreNotOffered()
    {
        using var dir = new TempDir();
        var (store, _) = Make(dir);
        // This process: alive.
        Assert.True(store.Save("mine", new byte[] { 1 }, "Mine", null));
        Assert.True(store.Save("explicit", new byte[] { 1 }, "Mine too", null, Process.GetCurrentProcess().Id, CurrentStart()));
        // Same id, different start time: the id was reused by another process, so the writer is gone.
        Assert.True(store.Save("reused", new byte[] { 2 }, "Reused", null, Process.GetCurrentProcess().Id, CurrentStart().AddHours(-3)));
        var orphans = store.FindOrphans();
        Assert.Equal(new[] { "reused" }, orphans.Select(o => o.Key));

        // Reopened by this process: no longer offered elsewhere.
        Assert.True(store.Claim("reused"));
        Assert.Empty(store.FindOrphans());
        Assert.False(store.Claim("missing"));
    }

    [Fact]
    public async Task SaveAsync_AndNewestFirst()
    {
        using var dir = new TempDir();
        var (store, _) = Make(dir);
        Assert.True(store.Save("older", new byte[] { 1 }, "Older", null, DeadPid, DateTime.UtcNow));
        await Task.Delay(20);
        Assert.True(await store.SaveAsync("newer", new byte[] { 1, 2 }, "Newer", null));
        // Overwriting a copy keeps one pair of files.
        Assert.True(await store.SaveAsync("newer", new byte[] { 1, 2, 3 }, "Newer", null));
        Assert.Equal(new byte[] { 1, 2, 3 }, store.Read("newer"));
        Assert.Equal(4, Directory.GetFiles(store.Folder).Length);
        Assert.Equal(new[] { "older" }, store.FindOrphans().Select(o => o.Key)); // "newer" belongs to this process
    }

    [Fact]
    public void Errors_AreReported_NotThrown()
    {
        using var dir = new TempDir();
        var (store, errors) = Make(dir);

        // Keys can't escape the folder.
        Assert.False(store.Save(@"..\evil", new byte[] { 1 }, "x", null));
        Assert.False(store.Save("", new byte[] { 1 }, "x", null));
        Assert.Null(store.Read("a/b"));
        store.Discard("..");
        Assert.Equal(4, errors.Count);

        // A damaged entry is skipped; a valid one next to it is still found.
        Assert.True(store.Save("good", new byte[] { 1 }, "Good", null, DeadPid, DateTime.UtcNow));
        File.WriteAllText(Path.Combine(store.Folder, "bad.json"), "{ not json", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(store.Folder, "bad.docx"), new byte[] { 1 });
        // An entry whose key doesn't match its file name is ignored.
        File.Copy(Path.Combine(store.Folder, "good.json"), Path.Combine(store.Folder, "copy.json"));
        Assert.Equal(new[] { "good" }, store.FindOrphans().Select(o => o.Key));
        Assert.Contains(errors, e => e.Contains("bad.json"));

        // The folder can't be created: a file is in the way.
        File.WriteAllText(dir.File("blocked"), "x");
        var blocked = new RecoveryStore(Path.Combine(dir.File("blocked"), "Recovery"));
        var blockedErrors = new List<string>();
        blocked.OnError = (ex, what) => blockedErrors.Add(what);
        Assert.False(blocked.Save("k", new byte[] { 1 }, "x", null));
        Assert.Single(blockedErrors);
        Assert.Empty(blocked.FindOrphans());
        Assert.Empty(new RecoveryStore(dir.File("missing")).FindOrphans());
    }
}
