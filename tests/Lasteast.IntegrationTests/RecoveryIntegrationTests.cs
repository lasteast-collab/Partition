using System.Runtime.Versioning;
using System.Security.Cryptography;
using Lasteast.Core.Logging;
using Lasteast.Core.Models;
using Lasteast.Core.Operations;
using Lasteast.Core.Recovery;
using Lasteast.Storage.Windows.Detection;
using Lasteast.Storage.Windows.Operations;

namespace Lasteast.IntegrationTests;

/// <summary>Phase 7 with files written and deleted by Windows' own NTFS driver, and a partition deleted with diskpart.</summary>
[SupportedOSPlatform("windows")]
public static class RecoveryIntegrationTests
{
    private static char FreeLetter()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        for (char c = 'Z'; c >= 'G'; c--) if (!used.Contains(c)) return c;
        throw new InvalidOperationException("no free drive letter");
    }

    [Test]
    public static async Task Files_deleted_by_windows_are_recovered_byte_exact_to_another_disk()
    {
        var letter = FreeLetter();
        using var vd = await VirtualDisk.CreateAsync(1908, "convert gpt", "create partition primary size=200", "format fs=ntfs quick label=LE-UNDEL", $"assign letter={letter}");
        var root = $"{letter}:\\";
        var data = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 123);
        Directory.CreateDirectory(root + @"Projects\Alpha");
        // files that existed for a while: their data is on disk (deleting a never-flushed file discards its cached data)
        using (var fs = new FileStream(root + @"Projects\Alpha\report.bin", FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            fs.Write(data);
            fs.Flush(flushToDisk: true);
        }
        File.WriteAllText(root + "note.txt", "small note");
        VolumeLocks.Acquire(new[] { (await vd.ReadAsync()).Partitions.Single(p => p.Volume?.Label == "LE-UNDEL").Volume!.VolumeGuidPath }, dismount: true).Dispose();
        File.WriteAllText(root + "keep.txt", "keep");
        File.Delete(root + @"Projects\Alpha\report.bin");
        File.Delete(root + "note.txt");

        var disk = await vd.ReadAsync();
        var part = disk.Partitions.Single(p => p.Volume?.Label == "LE-UNDEL");
        // flush NTFS metadata to disk: lock + dismount, then let Windows remount
        VolumeLocks.Acquire(new[] { part.Volume!.VolumeGuidPath }, dismount: true).Dispose();

        using var dev = new PhysicalDiskDevice(disk, writable: false);
        var reader = RecoveryReaders.Open(new OffsetDevice(dev, (long)part.OffsetBytes, (long)part.SizeBytes))!;
        var deleted = reader.Scan(false, null, CancellationToken.None);
        var report = deleted.Single(f => f.Name == "report.bin");
        Assert.Equal(@"Projects\Alpha", report.Folder);
        Assert.Equal(Recoverability.Good, report.Recoverability);
        Assert.True(deleted.Any(f => f.Name == "note.txt"));

        var dest = Path.Combine(Path.GetTempPath(), "lasteast-itest", "undelete-" + Guid.NewGuid().ToString("N"));
        var inv = await new WindowsStorageInventoryProvider().GetInventoryAsync();
        var destDisk = VolumeLocator.DiskOfPath(inv, Path.GetTempPath());
        Assert.True(destDisk != vd.Number, "temp folder is on another disk");
        var rep = RecoveryWriter.Recover(reader, deleted, dest, vd.Number, destDisk, null, CancellationToken.None);
        Assert.True(rep.Recovered >= 2, $"recovered {rep.Recovered}, errors: {string.Join("; ", rep.Errors)}");
        Assert.True(File.ReadAllBytes(Path.Combine(dest, "Projects", "Alpha", "report.bin")).AsSpan().SequenceEqual(data), "content identical");
        Assert.Equal("small note", File.ReadAllText(Path.Combine(dest, "note.txt")));

        // the same disk as destination is refused
        bool refused = false;
        try { RecoveryWriter.Recover(reader, deleted, root + "out", vd.Number, VolumeLocator.DiskOfPath(inv, root), null, CancellationToken.None); }
        catch (Lasteast.Core.RawDisk.RawOperationException ex) { refused = ex.Key == "Rec.Error.SameDisk"; }
        Assert.True(refused, "same-disk destination refused");
    }

    [Test]
    public static async Task Exfat_files_deleted_by_windows_are_recovered_byte_exact()
    {
        var letter = FreeLetter();
        using var vd = await VirtualDisk.CreateAsync(1908, "convert gpt", "create partition primary size=120", "format fs=exfat quick label=LE-EXFAT", $"assign letter={letter}");
        var root = $"{letter}:\\";
        var big = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 77);
        var small = RandomNumberGenerator.GetBytes(5000);
        Directory.CreateDirectory(root + @"Camera\2026");
        foreach (var (name, data) in new[] { (@"Camera\2026\clip-with-a-rather-long-name.mp4", big), ("small.bin", small) })
            using (var fs = new FileStream(root + name, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { fs.Write(data); fs.Flush(flushToDisk: true); }
        var part = (await vd.ReadAsync()).Partitions.Single(p => p.Volume?.Label == "LE-EXFAT");
        VolumeLocks.Acquire(new[] { part.Volume!.VolumeGuidPath }, dismount: true).Dispose();
        File.Delete(root + @"Camera\2026\clip-with-a-rather-long-name.mp4");
        File.Delete(root + "small.bin");
        VolumeLocks.Acquire(new[] { part.Volume!.VolumeGuidPath }, dismount: true).Dispose();

        var disk = await vd.ReadAsync();
        using var dev = new PhysicalDiskDevice(disk, writable: false);
        var reader = RecoveryReaders.Open(new OffsetDevice(dev, (long)part.OffsetBytes, (long)part.SizeBytes));
        Assert.True(reader is ExfatRecoveryReader, "exFAT reader");
        var deleted = reader!.Scan(false, null, CancellationToken.None);
        foreach (var (folder, name, data) in new[] { (@"Camera\2026", "clip-with-a-rather-long-name.mp4", big), ("", "small.bin", small) })
        {
            var f = deleted.SingleOrDefault(x => x.Folder == folder && x.Name == name);
            Assert.True(f is not null, $"{name} listed; got [{string.Join(", ", deleted.Select(x => x.FullPath))}]");
            Assert.Equal(Recoverability.Good, f!.Recoverability);
            using var ms = new MemoryStream();
            reader.Extract(f, ms, CancellationToken.None);
            Assert.True(ms.ToArray().AsSpan().SequenceEqual(data), $"{name} content identical");
        }
    }

    [Test]
    public static async Task Compressed_and_heavily_fragmented_ntfs_files_deleted_by_windows_are_recovered()
    {
        var letter = FreeLetter();
        using var vd = await VirtualDisk.CreateAsync(1818, "convert gpt", "create partition primary size=96", "format fs=ntfs quick unit=4096 label=LE-ADV", $"assign letter={letter}");
        var root = $"{letter}:\\";
        // compressed file (Windows' own LZNT1)
        var text = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 40000).Select(i => $"{i:D7} lasteast compressed line {i % 97}\r\n")));
        File.WriteAllBytes(root + "packed.txt", text);
        var compact = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("compact.exe", $"/c \"{root}packed.txt\"") { UseShellExecute = false, RedirectStandardOutput = true })!;
        compact.WaitForExit();
        Assert.True((File.GetAttributes(root + "packed.txt") & FileAttributes.Compressed) != 0, "file is NTFS-compressed");
        // fragmented file: small files, fill the rest, delete every other small file, write into the holes
        Directory.CreateDirectory(root + "holes");
        for (int i = 0; i < 3000; i++) File.WriteAllBytes(root + $"holes\\h{i:D5}", new byte[4096]);
        try { using var fill = new FileStream(root + "rest.bin", FileMode.CreateNew); var b = new byte[1 << 20]; while (true) fill.Write(b); }
        catch (IOException) { }
        for (int i = 0; i < 3000; i += 2) File.Delete(root + $"holes\\h{i:D5}");
        var frag = RandomNumberGenerator.GetBytes(1400 * 4096);
        using (var f = new FileStream(root + "frag.bin", FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            for (int i = 0; i < frag.Length; i += 4096) { f.Write(frag, i, 4096); f.Flush(true); }
        var part = (await vd.ReadAsync()).Partitions.Single(p => p.Volume?.Label == "LE-ADV");
        VolumeLocks.Acquire(new[] { part.Volume!.VolumeGuidPath }, dismount: true).Dispose();

        string Diff(byte[] got, byte[] want)
        {
            int bad = 0, first = -1, zero = 0;
            for (int i = 0; i < Math.Min(got.Length, want.Length); i += 4096)
            {
                int n = Math.Min(4096, want.Length - i);
                if (!got.AsSpan(i, Math.Min(n, got.Length - i)).SequenceEqual(want.AsSpan(i, n)))
                {
                    bad++; if (first < 0) first = i;
                    if (got.AsSpan(i, Math.Min(n, got.Length - i)).IndexOfAnyExcept((byte)0) < 0) zero++;
                }
            }
            return $"len {got.Length}/{want.Length}, {bad} bad 4K blocks ({zero} zero), first at {first}";
        }
        void Check(string phase, bool existing)
        {
            var d0 = (vd.ReadAsync().GetAwaiter().GetResult());
            using var dv = new PhysicalDiskDevice(d0, writable: false);
            var rd = RecoveryReaders.Open(new OffsetDevice(dv, (long)part.OffsetBytes, (long)part.SizeBytes))!;
            var list = rd.Scan(existing, null, CancellationToken.None);
            foreach (var (name, data) in new[] { ("packed.txt", text), ("frag.bin", frag) })
            {
                var f = list.SingleOrDefault(x => x.Name == name && x.IsDeleted == !existing);
                Assert.True(f is not null, $"{phase}: {name} listed");
                Assert.True(f!.Recoverability != Recoverability.Unrecoverable, $"{phase}: {name}: {f.Recoverability} {f.Note}");
                using var ms = new MemoryStream();
                rd.Extract(f, ms, CancellationToken.None);
                Assert.True(ms.ToArray().AsSpan().SequenceEqual(data), $"{phase}: {name} ({f.Recoverability}, size {f.Size}): {Diff(ms.ToArray(), data)} {(rd as NtfsRecoveryReader)?.Describe(f)}");
            }
        }
        Check("before delete", existing: true);
        File.Delete(root + "packed.txt");
        File.Delete(root + "frag.bin");
        VolumeLocks.Acquire(new[] { part.Volume!.VolumeGuidPath }, dismount: true).Dispose();
        Check("after delete", existing: false);
    }

    [Test]
    public static async Task Vhdx_image_is_attached_read_only_for_recovery()
    {
        using var vd = await VirtualDisk.CreateAsync(1908, "convert gpt", "create partition primary size=60", "format fs=exfat quick label=LE-IMG");
        var path = vd.File;
        VirtualDisk.DiskPart($"select vdisk file=\"{path}\"", "detach vdisk");
        Assert.Equal(DiskImageKind.NeedsAttach, DiskImages.Classify(path));
        var before = SHA256.HashData(File.ReadAllBytes(path));
        var mgr = new Lasteast.Storage.Windows.Info.WindowsVirtualDiskManager();
        var device = mgr.AttachReadOnly(path);
        try
        {
            int n = int.Parse(new string(device.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray()));
            var inv = await new WindowsStorageInventoryProvider().GetInventoryAsync();
            var disk = inv.Disks.Single(d => d.Number == n);
            var part = disk.Partitions.Single(p => p.SizeBytes == 60UL * 1024 * 1024);
            using var dev = new PhysicalDiskDevice(disk, writable: false);
            Assert.True(RecoveryReaders.Open(new OffsetDevice(dev, (long)part.OffsetBytes, (long)part.SizeBytes)) is ExfatRecoveryReader, "volume readable");
        }
        finally { mgr.Detach(path); }
        Assert.True(before.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))), "image unchanged");
    }

    [Test]
    public static async Task Partition_deleted_with_diskpart_is_found_and_restored_through_the_engine()
    {
        var letter = FreeLetter();
        using var vd = await VirtualDisk.CreateAsync(2009, "convert mbr", "create partition primary size=300", "format fs=ntfs quick label=LE-LOST", $"assign letter={letter}");
        File.WriteAllText($"{letter}:\\proof.txt", "I was lost and now I am found");
        var before = await vd.ReadAsync();
        var p = before.Partitions.Single(x => x.Volume?.Label == "LE-LOST");
        VolumeLocks.Acquire(new[] { p.Volume!.VolumeGuidPath }, dismount: true).Dispose(); // the file existed on disk before the accident
        VirtualDisk.DiskPart($"select disk {vd.Number}", "select partition 1", "delete partition override");
        await Task.Delay(1500);

        var disk = await vd.ReadAsync();
        Assert.Equal(0, disk.Partitions.Count);
        IReadOnlyList<FoundPartition> found;
        string diag;
        using (var dev = new PhysicalDiskDevice(disk, writable: false))
        {
            found = LostPartitionScanner.Scan(dev, Array.Empty<(ulong, ulong)>(), LostScanMode.Quick, null, CancellationToken.None);
            var bs = new byte[512];
            dev.Read((long)p.OffsetBytes, bs);
            diag = $"ss={dev.SectorSize} len={dev.Length} off={p.OffsetBytes} size={p.SizeBytes} bs={Convert.ToHexString(bs.AsSpan(0, 0x50))}..{bs[510]:X2}{bs[511]:X2} found=[{string.Join(";", found.Select(x => $"{x.FileSystem}@{x.OffsetBytes}+{x.SizeBytes}"))}]";
        }
        Assert.True(found.Count(x => x.FileSystem == "NTFS") == 1, diag);
        var f = found.Single(x => x.FileSystem == "NTFS");
        Assert.Equal(p.OffsetBytes, f.OffsetBytes);
        Assert.Equal(p.SizeBytes, f.SizeBytes);
        Assert.Equal(FoundConfidence.High, f.Confidence);

        var dir = Path.Combine(Path.GetTempPath(), "lasteast-itest", "lost-" + Guid.NewGuid().ToString("N"));
        var log = new FileLog(Path.Combine(dir, "logs"));
        var priv = new WindowsPrivilegeService();
        var provider = new WindowsStorageInventoryProvider(log, priv);
        var engine = new GuardedExecutionEngine(provider, new WindowsOperationExecutor(provider, log, Path.Combine(dir, "journal")),
            new PartitionTableBackupService(Path.Combine(dir, "backups")), priv, new JsonlOperationHistory(Path.Combine(dir, "h.jsonl")), log);
        var r = await engine.ExecuteAsync(new DiskOperation[] { new RecoverPartitionOp(vd.Number, f.OffsetBytes, f.SizeBytes, f.FileSystem) },
            await provider.GetInventoryAsync(), new ExecutionPolicy(false), "APPLY");
        Assert.True(r.Succeeded, $"{r.FailureKey} {r.FailureDetail}");

        for (int i = 0; i < 20; i++)
        {
            var d = await vd.ReadAsync();
            var v = d.Partitions.SingleOrDefault()?.Volume;
            if (v?.VolumeGuidPath is { } g && g.StartsWith(@"\\?\Volume{") && v.Label == "LE-LOST")
            {
                Assert.Equal("I was lost and now I am found", File.ReadAllText((g.EndsWith('\\') ? g : g + "\\") + "proof.txt"));
                return;
            }
            await Task.Delay(1000);
        }
        throw new AssertionException("restored volume did not mount");
    }
}
