using Lasteast.Core.Analysis;
using Lasteast.Core.Cleanup;

namespace Lasteast.Tests;

internal sealed class TempTree : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "lasteast-fs-" + Guid.NewGuid().ToString("N"));
    public TempTree() => Directory.CreateDirectory(Root);

    public string File(string rel, int bytes, byte fill = 1, DateTime? mtime = null)
    {
        var p = Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        var data = new byte[bytes];
        Array.Fill(data, fill);
        System.IO.File.WriteAllBytes(p, data);
        if (mtime is { } t) System.IO.File.SetLastWriteTimeUtc(p, t);
        return p;
    }

    public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
}

public static class AnalysisTests
{
    private const int MiB = 1024 * 1024;

    [Test]
    public static void Scanner_aggregates_sizes_categories_and_lists()
    {
        using var t = new TempTree();
        t.File("Videos/a.mp4", 3 * MiB);
        t.File("Videos/b.mkv", 2 * MiB, 2);
        t.File("Docs/x.pdf", MiB, 3, DateTime.UtcNow.AddYears(-3));
        t.File("Docs/sub/y.txt", 1000);
        Directory.CreateDirectory(Path.Combine(t.Root, "Empty/Nested"));

        var r = StorageScanner.Scan(t.Root, new ScanOptions { MinFolderBytesToKeep = 1 }, null, CancellationToken.None);
        Assert.Equal(4L, r.Files);
        Assert.Equal((ulong)(6 * MiB + 1000), r.Root.Size);
        Assert.Equal("Videos", r.Root.Children[0].Name); // sorted by size
        Assert.Equal((ulong)(5 * MiB), r.ByCategory[FileCategory.Videos].Bytes);
        Assert.Equal(FileCategory.Documents, FileCategories.Classify("x.PDF"));
        Assert.Equal("a.mp4", r.LargestFiles[0].Name);
        Assert.True(r.OldLargeFiles.Any(f => f.Name == "x.pdf") && r.OldLargeFiles.All(f => f.Name != "a.mp4"));
        Assert.True(r.EmptyFolders.Any(p => p.EndsWith("Nested")));
        Assert.True(r.SizeGroups.Count == 0, "no two files share a size");
        Assert.Equal(4L, r.Root.FileCount);
    }

    [Test]
    public static void Scanner_honours_exclusions_and_cancellation()
    {
        using var t = new TempTree();
        t.File("keep/a.bin", 1000);
        t.File("skip/b.bin", 5000);
        var r = StorageScanner.Scan(t.Root, new ScanOptions { Exclusions = new[] { Path.Combine(t.Root, "skip") } }, null, CancellationToken.None);
        Assert.Equal(1L, r.Files);
        Assert.True(StorageScanner.IsExcluded(Path.Combine(t.Root, "skip", "x"), new[] { Path.Combine(t.Root, "skip") }));
        Assert.True(!StorageScanner.IsExcluded(Path.Combine(t.Root, "skipped", "x"), new[] { Path.Combine(t.Root, "skip") }), "prefix must match whole folder names");

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.True(StorageScanner.Scan(t.Root, new ScanOptions(), null, cts.Token).Cancelled);
    }

    [Test]
    public static void Scanner_does_not_follow_symlinks()
    {
        using var t = new TempTree();
        using var outside = new TempTree();
        outside.File("big.bin", 2 * MiB);
        t.File("real.bin", 10);
        try { Directory.CreateSymbolicLink(Path.Combine(t.Root, "link"), outside.Root); }
        catch (Exception) { return; } // platform without symlink permission: nothing to test
        var r = StorageScanner.Scan(t.Root, new ScanOptions(), null, CancellationToken.None);
        Assert.Equal(1L, r.Files);
    }

    [Test]
    public static void Duplicates_require_identical_content()
    {
        using var t = new TempTree();
        var a = t.File("a/one.bin", 2 * MiB, 7);
        var b = t.File("b/two.bin", 2 * MiB, 7);
        t.File("c/same-size-different.bin", 2 * MiB, 8);
        t.File("d/three.bin", 2 * MiB, 7);
        var scan = StorageScanner.Scan(t.Root, new ScanOptions(), null, CancellationToken.None);
        Assert.Equal(1, scan.SizeGroups.Count);
        var groups = DuplicateFinder.Find(scan.SizeGroups, null, CancellationToken.None);
        Assert.Equal(1, groups.Count);
        Assert.Equal(3, groups[0].Paths.Count);
        Assert.Equal((ulong)(4 * MiB), groups[0].WastedBytes);
        Assert.True(groups[0].Paths.Contains(a) && groups[0].Paths.Contains(b));
    }

    [Test]
    public static void Treemap_fills_the_area_without_overlap()
    {
        var sizes = new double[] { 60, 25, 10, 4, 1, 0 };
        var rects = Treemap.Layout(sizes, 0, 0, 400, 300);
        Assert.Equal(5, rects.Count); // zero-size item skipped
        Assert.Near(400 * 300, rects.Sum(r => r.Width * r.Height), 1e-6);
        foreach (var r in rects)
        {
            Assert.Near(sizes[r.Index] / 100 * 120000, r.Width * r.Height, 1e-6);
            Assert.True(r.X >= -1e-9 && r.Y >= -1e-9 && r.X + r.Width <= 400 + 1e-6 && r.Y + r.Height <= 300 + 1e-6);
        }
        for (int i = 0; i < rects.Count; i++)
            for (int j = i + 1; j < rects.Count; j++)
            {
                var a = rects[i]; var b = rects[j];
                var ox = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
                var oy = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
                Assert.True(ox <= 1e-6 || oy <= 1e-6, $"rects {i},{j} overlap");
            }
        Assert.Equal(0, Treemap.Layout(new double[] { 1 }, 0, 0, 0, 10).Count);
    }
}

public static class CleanupTests
{
    [Test]
    public static void Scan_lists_only_old_enough_files_under_root()
    {
        using var t = new TempTree();
        var old = t.File("a/old.tmp", 100, mtime: DateTime.UtcNow.AddDays(-3));
        t.File("a/new.tmp", 100);
        var target = new CleanupTarget("x", "k", "d", t.Root, TimeSpan.FromDays(1), false, true);
        var r = CleanupService.ScanTarget(target, Array.Empty<string>());
        Assert.Equal(1, r.Files.Count);
        Assert.Equal(old, r.Files[0].Path);
        Assert.Equal(100UL, r.TotalBytes);
    }

    [Test]
    public static void Clean_removes_only_confirmed_unchanged_files_inside_root()
    {
        using var t = new TempTree();
        using var other = new TempTree();
        var target = new CleanupTarget("x", "k", "d", t.Root, TimeSpan.Zero, false, true);
        var a = t.File("a.tmp", 100);
        var b = t.File("b.tmp", 100);
        var outside = other.File("precious.docx", 100);
        var excluded = t.File("keep/me.tmp", 100);
        var scan = CleanupService.ScanTarget(target, Array.Empty<string>());
        var confirmed = scan.Files.Select(f => (target, f)).ToList();
        // a forged entry pointing outside the root must be refused
        confirmed.Add((target, new CleanupFile(outside, 100, File.GetLastWriteTimeUtc(outside))));
        confirmed.Add((target, new CleanupFile(Path.Combine(t.Root, "..", Path.GetFileName(other.Root), "precious.docx"), 100, File.GetLastWriteTimeUtc(outside))));
        // b changes after the scan → skipped
        File.WriteAllBytes(b, new byte[200]);

        var svc = new CleanupService(new PermanentFileRemover());
        var report = svc.Clean(confirmed, RemovalMode.Permanent, new[] { Path.Combine(t.Root, "keep") }, null, CancellationToken.None);

        Assert.True(!File.Exists(a));
        Assert.True(File.Exists(b), "changed file must be skipped");
        Assert.True(File.Exists(outside), "file outside the root must never be touched");
        Assert.True(File.Exists(excluded), "excluded file must be kept");
        Assert.Equal(1, report.Removed);
        Assert.Equal(100UL, report.FreedBytes);
        Assert.Equal(confirmed.Count - 1, report.Skipped);
    }

    [Test]
    public static void Recheck_blocks_linked_folders()
    {
        using var t = new TempTree();
        using var other = new TempTree();
        var target = new CleanupTarget("x", "k", "d", t.Root, TimeSpan.Zero, false, true);
        var victim = other.File("victim.txt", 10);
        try { Directory.CreateSymbolicLink(Path.Combine(t.Root, "link"), other.Root); }
        catch (Exception) { return; }
        var viaLink = Path.Combine(t.Root, "link", "victim.txt");
        var reason = CleanupService.Recheck(target, new CleanupFile(viaLink, 10, File.GetLastWriteTimeUtc(victim)), Array.Empty<string>());
        Assert.True(reason is not null, "a file reached through a link must not be removable");
    }
}
