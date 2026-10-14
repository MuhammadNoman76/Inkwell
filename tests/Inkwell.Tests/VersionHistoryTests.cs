using System.Text;
using Inkwell.Core.IO;

namespace Inkwell.Tests;

/// <summary>Version history: keys, dedupe, throttling, caps, damaged indexes, concurrency. Everything under %TEMP%\inkwell-tests-*.</summary>
public class VersionHistoryTests
{
    private static readonly DateTime T0 = new(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc);

    private static byte[] Bytes(string text, int padding = 0) => Encoding.UTF8.GetBytes(text + new string('x', padding));

    private static (VersionHistory History, List<string> Errors) Make(TempDir dir, VersionHistoryOptions? options = null)
    {
        var errors = new List<string>();
        var history = new VersionHistory(dir.File("History"), options) { OnError = (ex, what) => errors.Add(what + ": " + ex.Message) };
        return (history, errors);
    }

    [Fact]
    public void Keys_NormaliseThePath()
    {
        var a = VersionHistory.KeyFor(@"C:\Docs\Report.docx");
        Assert.Equal(16, a.Length);
        Assert.Matches("^[0-9a-f]{16}$", a);
        Assert.Equal(a, VersionHistory.KeyFor(@"c:\docs\REPORT.DOCX"));
        Assert.Equal(a, VersionHistory.KeyFor("C:/Docs/Report.docx"));
        Assert.Equal(a, VersionHistory.KeyFor(@"C:\Docs\Sub\..\Report.docx"));
        Assert.NotEqual(a, VersionHistory.KeyFor(@"C:\Docs\Report2.docx"));
        Assert.NotEqual(a, VersionHistory.KeyFor(@"D:\Docs\Report.docx"));
        // SHA-256 of the lower-case full path.
        using var sha = System.Security.Cryptography.SHA256.Create();
        var expected = string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(@"c:\docs\report.docx")).Take(8).Select(b => b.ToString("x2")));
        Assert.Equal(expected, a);
    }

    [Fact]
    public void Add_List_Read()
    {
        using var dir = new TempDir();
        var (history, errors) = Make(dir);
        var doc = dir.File("Essay.docx");
        var first = history.Add(doc, Bytes("one"), SnapshotKind.ManualSave, wordCount: 120, label: "  Draft  ", nowUtc: T0);
        Assert.Equal(SnapshotStatus.Added, first.Status);
        var second = history.Add(doc, Bytes("two"), SnapshotKind.Automatic, wordCount: 150, nowUtc: T0.AddMinutes(30));
        Assert.Equal(SnapshotStatus.Added, second.Status);

        var list = history.List(doc);
        Assert.Equal(2, list.Count);
        Assert.Equal(second.Snapshot!.Id, list[0].Id); // newest first
        Assert.Equal(T0.AddMinutes(30), list[0].TimeUtc);
        Assert.Equal(SnapshotKind.Automatic, list[0].Kind);
        Assert.Equal(150, list[0].WordCount);
        Assert.Equal("Draft", list[1].Label);
        Assert.Equal(3, list[1].Size);
        Assert.Equal("Essay.docx", list[1].FileName);
        Assert.Equal("one", Encoding.UTF8.GetString(history.Read(doc, list[1].Id)!));
        Assert.Null(history.Read(doc, "nope"));
        Assert.Null(history.Read(doc, @"..\..\secret"));
        Assert.Empty(history.List(dir.File("Other.docx")));

        // Files on disk: the key folder with index.json and one .docx per snapshot.
        var folder = history.FolderFor(doc);
        Assert.Equal(Path.Combine(dir.File("History"), VersionHistory.KeyFor(doc)), folder);
        Assert.True(File.Exists(Path.Combine(folder, "index.json")));
        Assert.Equal(2, Directory.GetFiles(folder, "*.docx").Length);
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
        Assert.Empty(errors);
    }

    [Fact]
    public void IdenticalBytes_AreSkipped()
    {
        using var dir = new TempDir();
        var (history, _) = Make(dir);
        var doc = dir.File("a.docx");
        Assert.Equal(SnapshotStatus.Added, history.Add(doc, Bytes("same"), SnapshotKind.ManualSave, nowUtc: T0).Status);
        var again = history.Add(doc, Bytes("same"), SnapshotKind.ManualSave, nowUtc: T0.AddHours(1));
        Assert.Equal(SnapshotStatus.SkippedIdentical, again.Status);
        Assert.NotNull(again.Snapshot);
        Assert.Equal(SnapshotStatus.Added, history.Add(doc, Bytes("different"), SnapshotKind.ManualSave, nowUtc: T0.AddHours(2)).Status);
        // Identical to an older snapshot but not the newest: kept (the user went back to an earlier text).
        Assert.Equal(SnapshotStatus.Added, history.Add(doc, Bytes("same"), SnapshotKind.ManualSave, nowUtc: T0.AddHours(3)).Status);
        Assert.Equal(3, history.List(doc).Count);
    }

    [Fact]
    public void AutomaticSnapshots_AreThrottled()
    {
        using var dir = new TempDir();
        var (history, _) = Make(dir, new VersionHistoryOptions { MinAutomaticInterval = TimeSpan.FromMinutes(10) });
        var doc = dir.File("a.docx");
        Assert.Equal(SnapshotStatus.Added, history.Add(doc, Bytes("1"), SnapshotKind.Automatic, nowUtc: T0).Status);
        Assert.Equal(SnapshotStatus.SkippedTooSoon, history.Add(doc, Bytes("2"), SnapshotKind.Automatic, nowUtc: T0.AddMinutes(9)).Status);
        // A manual save is never throttled, and resets the clock for automatic ones.
        Assert.Equal(SnapshotStatus.Added, history.Add(doc, Bytes("3"), SnapshotKind.ManualSave, nowUtc: T0.AddMinutes(9)).Status);
        Assert.Equal(SnapshotStatus.SkippedTooSoon, history.Add(doc, Bytes("4"), SnapshotKind.Automatic, nowUtc: T0.AddMinutes(15)).Status);
        Assert.Equal(SnapshotStatus.Added, history.Add(doc, Bytes("5"), SnapshotKind.Automatic, nowUtc: T0.AddMinutes(20)).Status);
        Assert.Equal(SnapshotStatus.Added, history.Add(doc, Bytes("6"), SnapshotKind.BeforeRestore, nowUtc: T0.AddMinutes(21)).Status);
        Assert.Equal(4, history.List(doc).Count);
    }

    [Fact]
    public void PerDocumentCaps_PruneTheOldest_UnnamedFirst()
    {
        using var dir = new TempDir();
        var (history, _) = Make(dir, new VersionHistoryOptions { MaxSnapshotsPerDocument = 3 });
        var doc = dir.File("a.docx");
        history.Add(doc, Bytes("v1"), SnapshotKind.ManualSave, label: "First draft", nowUtc: T0);
        for (var i = 2; i <= 5; i++) history.Add(doc, Bytes("v" + i), SnapshotKind.ManualSave, nowUtc: T0.AddHours(i));
        var list = history.List(doc);
        Assert.Equal(3, list.Count);
        Assert.Equal(new[] { "v5", "v4", "First draft" }, list.Select(s => s.Label ?? Encoding.UTF8.GetString(history.Read(doc, s.Id)!)));
        Assert.Equal(3, Directory.GetFiles(history.FolderFor(doc), "*.docx").Length);

        // Bytes: a single snapshot bigger than the cap stays (the newest is never removed).
        using var dir2 = new TempDir();
        var (small, _) = Make(dir2, new VersionHistoryOptions { MaxBytesPerDocument = 100 });
        small.Add(doc, Bytes("a", 60), SnapshotKind.ManualSave, nowUtc: T0);
        small.Add(doc, Bytes("b", 60), SnapshotKind.ManualSave, nowUtc: T0.AddHours(1));
        Assert.Single(small.List(doc));
        small.Add(doc, Bytes("c", 500), SnapshotKind.ManualSave, nowUtc: T0.AddHours(2));
        var only = Assert.Single(small.List(doc));
        Assert.Equal(501, only.Size);
    }

    [Fact]
    public void GlobalCap_KeepsEachDocumentsNewest()
    {
        using var dir = new TempDir();
        var (history, _) = Make(dir, new VersionHistoryOptions { MaxTotalBytes = 1000 });
        var a = dir.File("a.docx");
        var b = dir.File("b.docx");
        history.Add(a, Bytes("a1", 298), SnapshotKind.ManualSave, nowUtc: T0);
        history.Add(b, Bytes("b1", 298), SnapshotKind.ManualSave, nowUtc: T0.AddMinutes(1));
        history.Add(a, Bytes("a2", 298), SnapshotKind.ManualSave, nowUtc: T0.AddMinutes(2));
        Assert.Equal(900, history.TotalBytes());
        history.Add(b, Bytes("b2", 298), SnapshotKind.ManualSave, nowUtc: T0.AddMinutes(3));
        // 1200 bytes > 1000: the oldest snapshot overall (a1) goes.
        Assert.Equal(900, history.TotalBytes());
        Assert.Single(history.List(a));
        Assert.Equal(2, history.List(b).Count);

        history.Add(a, Bytes("a3", 898), SnapshotKind.ManualSave, nowUtc: T0.AddMinutes(4));
        // Only the newest of each document is left, even though that is still over the cap.
        Assert.Single(history.List(a));
        Assert.Single(history.List(b));
        Assert.Equal("b2", Encoding.UTF8.GetString(history.Read(b, history.List(b)[0].Id)!).Substring(0, 2));

        var docs = history.Documents();
        Assert.Equal(2, docs.Count);
        Assert.Equal(Path.GetFullPath(a), docs[0].Path);
        Assert.Equal(900, docs[0].Size);
    }

    [Fact]
    public void DamagedIndex_IsRebuiltFromTheFiles()
    {
        using var dir = new TempDir();
        var (history, errors) = Make(dir);
        var doc = dir.File("a.docx");
        history.Add(doc, Bytes("one"), SnapshotKind.ManualSave, label: "Named", nowUtc: T0);
        history.Add(doc, Bytes("two"), SnapshotKind.ManualSave, nowUtc: T0.AddHours(1));
        var folder = history.FolderFor(doc);
        File.WriteAllText(Path.Combine(folder, "index.json"), "{ this is not json", Encoding.UTF8);
        File.WriteAllText(Path.Combine(folder, "index.json.1234abcd.tmp"), "left over", Encoding.UTF8);

        var list = history.List(doc);
        Assert.Equal(2, list.Count);
        Assert.Equal(T0.AddHours(1), list[0].TimeUtc);
        Assert.Equal(3, list[0].Size);
        Assert.Equal("a.docx", list[0].FileName);
        Assert.Contains(errors, e => e.Contains("damaged"));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
        // The rebuilt index still dedupes against the newest snapshot.
        Assert.Equal(SnapshotStatus.SkippedIdentical, history.Add(doc, Bytes("two"), SnapshotKind.ManualSave, nowUtc: T0.AddHours(2)).Status);

        // A snapshot file that disappeared is dropped; a file the index doesn't know is adopted.
        File.Delete(Path.Combine(folder, list[1].Id + ".docx"));
        File.WriteAllBytes(Path.Combine(folder, "20261016T080000000Z-beef.docx"), Bytes("orphan"));
        var after = history.List(doc);
        Assert.Equal(2, after.Count);
        Assert.Equal("20261016T080000000Z-beef", after[0].Id);
        Assert.Equal(new DateTime(2026, 10, 16, 8, 0, 0, DateTimeKind.Utc), after[0].TimeUtc);

        // An index that is valid JSON but empty is also handled.
        File.WriteAllText(Path.Combine(folder, "index.json"), "null", Encoding.UTF8);
        Assert.Equal(2, history.List(doc).Count);
    }

    [Fact]
    public void Delete_Label_Move()
    {
        using var dir = new TempDir();
        var (history, _) = Make(dir);
        var doc = dir.File("a.docx");
        var s1 = history.Add(doc, Bytes("1"), SnapshotKind.ManualSave, nowUtc: T0).Snapshot!;
        var s2 = history.Add(doc, Bytes("2"), SnapshotKind.ManualSave, nowUtc: T0.AddHours(1)).Snapshot!;

        Assert.True(history.SetLabel(doc, s1.Id, "Sent to editor"));
        Assert.Equal("Sent to editor", history.List(doc)[1].Label);
        Assert.True(history.SetLabel(doc, s1.Id, " "));
        Assert.Null(history.List(doc)[1].Label);
        Assert.False(history.SetLabel(doc, "20200101T000000000Z-0000", "x"));

        // Saved under a new name: the history follows (merging with history already there).
        var renamed = dir.File("b.docx");
        history.Add(renamed, Bytes("b"), SnapshotKind.ManualSave, nowUtc: T0.AddMinutes(30));
        Assert.True(history.MoveHistory(doc, renamed));
        Assert.Empty(history.List(doc));
        Assert.False(Directory.Exists(history.FolderFor(doc)));
        var merged = history.List(renamed);
        Assert.Equal(new[] { s2.Id, null, s1.Id }, merged.Select(s => s.Id == s1.Id || s.Id == s2.Id ? s.Id : null));
        var third = dir.File("c.docx");
        Assert.True(history.MoveHistory(renamed, third));
        Assert.Equal(3, history.List(third).Count);
        Assert.Equal(Path.GetFullPath(third), history.Documents().Single().Path);

        Assert.True(history.Delete(third, s2.Id));
        Assert.Equal(2, history.List(third).Count);
        Assert.False(history.Delete(third, s2.Id));
        Assert.True(history.DeleteAll(third));
        Assert.Empty(history.List(third));
        Assert.False(history.DeleteAll(third));
        Assert.Equal(0, history.TotalBytes());
    }

    [Fact]
    public void ConcurrentAdds_AreSerialised()
    {
        using var dir = new TempDir();
        var (history, errors) = Make(dir);
        var doc = dir.File("a.docx");
        // Two instances on the same folder (as two Inkwell processes would have) and several threads.
        var other = new VersionHistory(dir.File("History"));
        Parallel.For(0, 24, i =>
        {
            var target = i % 2 == 0 ? history : other;
            target.Add(doc, Bytes("version " + i), SnapshotKind.ManualSave, nowUtc: T0.AddSeconds(i));
        });
        Assert.Equal(24, history.List(doc).Count);
        Assert.Equal(24, Directory.GetFiles(history.FolderFor(doc), "*.docx").Length);
        Assert.Empty(errors);
    }

    [Fact]
    public void Errors_AreReported_NotThrown()
    {
        using var dir = new TempDir();
        // The root is a file, so nothing can be created under it.
        File.WriteAllText(dir.File("blocked"), "x");
        var errors = new List<string>();
        var history = new VersionHistory(dir.File("blocked"), null) { OnError = (ex, what) => errors.Add(what) };
        var result = history.Add(dir.File("a.docx"), Bytes("1"), SnapshotKind.ManualSave);
        Assert.Equal(SnapshotStatus.Failed, result.Status);
        Assert.NotEmpty(errors);
        Assert.Empty(history.List(dir.File("a.docx")));
        Assert.Empty(history.Documents());
    }
}
