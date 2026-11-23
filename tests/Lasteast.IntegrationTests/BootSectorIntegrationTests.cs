using System.Runtime.Versioning;
using System.Security.Cryptography;
using Lasteast.Core.Logging;
using Lasteast.Core.Models;
using Lasteast.Core.Operations;
using Lasteast.Core.Recovery;
using Lasteast.Storage.Windows.Detection;
using Lasteast.Storage.Windows.Operations;

namespace Lasteast.IntegrationTests;

/// <summary>A volume made RAW by a zeroed boot sector, seen by Windows as RAW, repaired through the engine, read back by Windows.</summary>
[SupportedOSPlatform("windows")]
public static class BootSectorIntegrationTests
{
    private static (GuardedExecutionEngine, WindowsStorageInventoryProvider) Engine()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lasteast-itest", "bootfix-" + Guid.NewGuid().ToString("N"));
        var log = new FileLog(Path.Combine(dir, "logs"));
        var priv = new WindowsPrivilegeService();
        var provider = new WindowsStorageInventoryProvider(log, priv);
        return (new GuardedExecutionEngine(provider, new WindowsOperationExecutor(provider, log, Path.Combine(dir, "journal")),
            new PartitionTableBackupService(Path.Combine(dir, "backups")), priv, new JsonlOperationHistory(Path.Combine(dir, "h.jsonl")), log), provider);
    }

    private static char FreeLetter()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        for (char c = 'Y'; c >= 'G'; c--) if (!used.Contains(c)) return c;
        throw new InvalidOperationException("no free drive letter");
    }

    [Test] public static Task Ntfs_volume_made_raw_is_repaired_from_its_backup() => Run("ntfs", "NTFS", 1);
    [Test] public static Task Exfat_volume_made_raw_is_repaired_from_its_backup_region() => Run("exfat", "exFAT", 12);
    [Test] public static Task Fat32_volume_made_raw_is_repaired_from_sector_6() => Run("fat32", "FAT32", 3);

    private static async Task Run(string fs, string expectedFs, int sectorsToDamage)
    {
        var letter = FreeLetter();
        using var vd = await VirtualDisk.CreateAsync(1717, "convert gpt", "create partition primary size=300", $"format fs={fs} quick label=LE-BOOT", $"assign letter={letter}");
        var data = RandomNumberGenerator.GetBytes(1024 * 1024 + 17);
        using (var f = new FileStream($"{letter}:\\keep.bin", FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            f.Write(data);
        var disk = await vd.ReadAsync();
        var part = disk.Partitions.Single(p => p.Volume?.Label == "LE-BOOT");

        // the damage: zero the start of the volume while it is locked
        using (VolumeLocks.Acquire(new[] { part.Volume!.VolumeGuidPath }, dismount: true))
        using (var dev = new PhysicalDiskDevice(disk, writable: true))
        {
            dev.Write((long)part.OffsetBytes, new byte[sectorsToDamage * 512]);
            dev.Flush();
        }
        PartitionInfo? raw = null;
        for (int i = 0; i < 20 && raw?.Volume?.AccessError is null; i++)
        {
            await Task.Delay(500);
            raw = (await vd.ReadAsync()).Partitions.Single(p => p.OffsetBytes == part.OffsetBytes);
        }
        Assert.True(raw?.Volume?.AccessError is not null, "Windows sees the volume as unreadable");
        Assert.Equal(VolumeProblem.NotRecognized, VolumeProblems.Classify(raw!.Volume!.AccessErrorCode));

        var (engine, provider) = Engine();
        var r = await engine.ExecuteAsync(new[] { new RepairBootSectorOp(vd.Number, part.OffsetBytes, expectedFs) }, await provider.GetInventoryAsync(), new ExecutionPolicy(false), "APPLY");
        Assert.True(r.Succeeded, $"{r.FailureKey} {r.FailureDetail}");

        var after = (await vd.ReadAsync()).Partitions.Single(p => p.OffsetBytes == part.OffsetBytes);
        Assert.Equal(expectedFs, after.Volume?.FileSystem);
        var path = (after.Volume!.MountPoints.FirstOrDefault() ?? after.Volume.VolumeGuidPath) + "keep.bin";
        Assert.True(File.ReadAllBytes(path).AsSpan().SequenceEqual(data), "file intact after the repair");
    }
}
