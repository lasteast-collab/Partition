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
        File.WriteAllBytes(root + @"Projects\Alpha\report.bin", data);
        File.WriteAllText(root + "note.txt", "small note");
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
    public static async Task Partition_deleted_with_diskpart_is_found_and_restored_through_the_engine()
    {
        var letter = FreeLetter();
        using var vd = await VirtualDisk.CreateAsync(2009, "convert mbr", "create partition primary size=300", "format fs=ntfs quick label=LE-LOST", $"assign letter={letter}");
        File.WriteAllText($"{letter}:\\proof.txt", "I was lost and now I am found");
        var before = await vd.ReadAsync();
        var p = before.Partitions.Single(x => x.Volume?.Label == "LE-LOST");
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
