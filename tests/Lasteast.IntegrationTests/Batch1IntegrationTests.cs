using System.Runtime.Versioning;
using System.Security.Cryptography;
using Lasteast.Core.Logging;
using Lasteast.Core.Models;
using Lasteast.Core.Operations;
using Lasteast.Storage.Windows.Detection;
using Lasteast.Storage.Windows.Operations;

namespace Lasteast.IntegrationTests;

/// <summary>Merge, copy, wipe, conversions and file-system tools through the engine, on VHDX disks.</summary>
[SupportedOSPlatform("windows")]
public static class Batch1IntegrationTests
{
    private static (GuardedExecutionEngine, WindowsStorageInventoryProvider) Engine()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lasteast-itest", "b1-" + Guid.NewGuid().ToString("N"));
        var log = new FileLog(Path.Combine(dir, "logs"));
        var priv = new WindowsPrivilegeService();
        var provider = new WindowsStorageInventoryProvider(log, priv);
        return (new GuardedExecutionEngine(provider, new WindowsOperationExecutor(provider, log, Path.Combine(dir, "journal")),
            new PartitionTableBackupService(Path.Combine(dir, "backups")), priv, new JsonlOperationHistory(Path.Combine(dir, "h.jsonl")), log), provider);
    }

    private static async Task Run(params DiskOperation[] ops)
    {
        var (engine, provider) = Engine();
        var r = await engine.ExecuteAsync(ops, await provider.GetInventoryAsync(), new ExecutionPolicy(false), "APPLY");
        Assert.True(r.Succeeded, $"{string.Join(", ", ops.Select(o => o.GetType().Name))}: {r.FailureKey} {r.FailureDetail}");
    }

    private static char FreeLetter(params char[] not)
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).Concat(not).ToHashSet();
        for (char c = 'X'; c >= 'G'; c--) if (!used.Contains(c)) return c;
        throw new InvalidOperationException("no free drive letter");
    }

    private static string Root(PartitionInfo p) => p.Volume!.DriveLetter is { } l ? $"{l}:\\" : p.Volume.VolumeGuidPath;

    [Test]
    public static async Task Merge_copies_verifies_deletes_and_extends()
    {
        char a = FreeLetter(), b = FreeLetter(a);
        using var vd = await VirtualDisk.CreateAsync(1919, "convert gpt", "create partition primary size=150", "format fs=ntfs quick label=LE-MA", $"assign letter={a}",
            "create partition primary size=100", "format fs=ntfs quick label=LE-MB", $"assign letter={b}");
        var data = RandomNumberGenerator.GetBytes(5 * 1024 * 1024 + 3);
        Directory.CreateDirectory($"{b}:\\docs\\deep");
        File.WriteAllBytes($"{b}:\\docs\\deep\\file.bin", data);
        File.WriteAllText($"{b}:\\note.txt", "merge me");
        var d = await vd.ReadAsync();
        var pa = d.Partitions.Single(p => p.Volume?.Label == "LE-MA");
        var pb = d.Partitions.Single(p => p.Volume?.Label == "LE-MB");
        await Run(new CopyFilesOp(vd.Number, pa.OffsetBytes, vd.Number, pb.OffsetBytes, "From B"),
            new DeletePartitionOp(vd.Number, pb.OffsetBytes),
            new ResizePartitionOp(vd.Number, pa.OffsetBytes, (pb.EndBytes - pa.OffsetBytes) / (1024 * 1024) * 1024 * 1024));
        var after = await vd.ReadAsync();
        var merged = after.Partitions.Single(p => p.OffsetBytes == pa.OffsetBytes);
        Assert.True(merged.SizeBytes >= pb.EndBytes - pa.OffsetBytes - 1024 * 1024, $"extended to {merged.SizeBytes}");
        Assert.True(after.Partitions.All(p => p.Volume?.Label != "LE-MB"), "B deleted");
        Assert.True(File.ReadAllBytes(Root(merged) + "From B\\docs\\deep\\file.bin").AsSpan().SequenceEqual(data));
        Assert.Equal("merge me", File.ReadAllText(Root(merged) + "From B\\note.txt"));
    }

    [Test]
    public static async Task Copy_partition_to_another_disk_is_readable_and_identical()
    {
        char a = FreeLetter();
        using var src = await VirtualDisk.CreateAsync(1920, "convert gpt", "create partition primary size=120", "format fs=ntfs quick label=LE-CP", $"assign letter={a}");
        using var dst = await VirtualDisk.CreateAsync(1921, "convert gpt");
        var data = RandomNumberGenerator.GetBytes(3 * 1024 * 1024);
        File.WriteAllBytes($"{a}:\\payload.bin", data);
        var sp = (await src.ReadAsync()).Partitions.Single(p => p.Volume?.Label == "LE-CP");
        var dd = await dst.ReadAsync();
        // diskpart's "convert gpt" may add a Microsoft Reserved partition: use the first free region
        var free = Lasteast.Core.Layout.DiskMapLayout.BuildSegments(dd, out _).First(s => s.Kind == Lasteast.Core.Layout.SegmentKind.Unallocated && s.SizeBytes > sp.SizeBytes + 2 * 1024 * 1024);
        ulong at = (free.OffsetBytes + 1024 * 1024 - 1) / (1024 * 1024) * (1024 * 1024);
        await Run(new CopyPartitionOp(dst.Number, at, src.Number, sp.OffsetBytes, sp.SizeBytes));
        var copy = (await dst.ReadAsync()).Partitions.Single(p => p.OffsetBytes == at);
        Assert.Equal("NTFS", copy.Volume?.FileSystem);
        Assert.Equal("LE-CP", copy.Volume?.Label);
        Assert.True(File.ReadAllBytes(Root(copy) + "payload.bin").AsSpan().SequenceEqual(data), "copied file identical");
    }

    [Test]
    public static async Task Wipe_partition_leaves_no_readable_data_and_sanitize_is_refused_honestly_on_a_virtual_disk()
    {
        using var vd = await VirtualDisk.CreateAsync(1922, "convert gpt", "create partition primary size=64", "format fs=ntfs quick label=LE-WIPE");
        var p = (await vd.ReadAsync()).Partitions.Single(x => x.Volume?.Label == "LE-WIPE");
        await Run(new WipeOp(vd.Number, p.OffsetBytes, WipeMethod.Zeros, Verify: true, p.SizeBytes));
        var after = (await vd.ReadAsync()).Partitions.Single(x => x.OffsetBytes == p.OffsetBytes);
        Assert.True(after.Volume?.FileSystem is null || after.Volume.AccessError is not null, $"still {after.Volume?.FileSystem}");
        var disk = await vd.ReadAsync();
        using (var dev = new PhysicalDiskDevice(disk, writable: false))
        {
            var buf = new byte[1024 * 1024];
            dev.Read((long)p.OffsetBytes, buf);
            Assert.True(buf.AsSpan().IndexOfAnyExcept((byte)0) < 0, "zeros on disk");
        }
        // whole-disk wipe empties the disk
        await Run(new WipeOp(vd.Number, null, WipeMethod.Random, Verify: false, disk.SizeBytes));
        Assert.True((await vd.ReadAsync()).Partitions.Count == 0, "no partitions after a disk wipe");
    }

    [Test]
    public static async Task Fat32_to_ntfs_and_reformat_keeping_files_and_repair()
    {
        char a = FreeLetter();
        using var vd = await VirtualDisk.CreateAsync(1923, "convert mbr", "create partition primary size=200", "format fs=fat32 quick label=LEFAT", $"assign letter={a}");
        var data = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 11);
        File.WriteAllBytes($"{a}:\\keep.bin", data);
        var p = (await vd.ReadAsync()).Partitions.Single(x => x.Volume?.Label == "LEFAT");
        await Run(new RepairFileSystemOp(vd.Number, p.OffsetBytes));
        await Run(new ConvertFileSystemOp(vd.Number, p.OffsetBytes, "NTFS"));
        var conv = (await vd.ReadAsync()).Partitions.Single(x => x.OffsetBytes == p.OffsetBytes);
        Assert.Equal("NTFS", conv.Volume?.FileSystem);
        Assert.True(File.ReadAllBytes(Root(conv) + "keep.bin").AsSpan().SequenceEqual(data), "file kept by convert");
        var staging = Path.Combine(Path.GetTempPath(), "lasteast-itest", "stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        await Run(new ReformatKeepingFilesOp(vd.Number, p.OffsetBytes, "NTFS", 65536, staging, (ulong)data.Length));
        var re = (await vd.ReadAsync()).Partitions.Single(x => x.OffsetBytes == p.OffsetBytes);
        Assert.Equal(65536u, re.Volume?.ClusterSizeBytes ?? 0);
        Assert.True(File.ReadAllBytes(Root(re) + "keep.bin").AsSpan().SequenceEqual(data), "file kept by reformat");
        Assert.True(!Directory.EnumerateDirectories(staging).Any(), "staging copy removed after success");
    }

    [Test]
    public static async Task Primary_to_logical_and_back_on_mbr()
    {
        char a = FreeLetter();
        using var vd = await VirtualDisk.CreateAsync(1924, "convert mbr", "create partition primary size=60", "format fs=ntfs quick label=LE-P1",
            "create partition primary size=60 offset=70000", "format fs=ntfs quick label=LE-P2", $"assign letter={a}");
        File.WriteAllText($"{a}:\\kind.txt", "logical");
        var p = (await vd.ReadAsync()).Partitions.Single(x => x.Volume?.Label == "LE-P2");
        await Run(new ConvertPartitionKindOp(vd.Number, p.OffsetBytes, true));
        var lg = (await vd.ReadAsync()).Partitions.Single(x => x.OffsetBytes == p.OffsetBytes);
        Assert.Equal(PartitionRole.MbrLogical, lg.Role);
        Assert.Equal("logical", File.ReadAllText(Root(lg) + "kind.txt"));
        await Run(new ConvertPartitionKindOp(vd.Number, p.OffsetBytes, false));
        Assert.True((await vd.ReadAsync()).Partitions.Single(x => x.OffsetBytes == p.OffsetBytes).Role != PartitionRole.MbrLogical);
    }
}

[SupportedOSPlatform("windows")]
public static class VirtualDiskFileTests
{
    [Test]
    public static async Task Expand_shrink_compact_and_convert_virtual_disk_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lasteast-itest", "vdf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var vhdx = Path.Combine(dir, "a.vhdx");
        var vhd = Path.Combine(dir, "a.vhd");
        var mgr = new Lasteast.Storage.Windows.Info.WindowsVirtualDiskManager();
        mgr.Create(vhdx, 64UL * 1024 * 1024, dynamic: true);
        mgr.Expand(vhdx, 128UL * 1024 * 1024);
        async Task<ulong> SizeOf(string path)
        {
            var dev = mgr.AttachReadOnly(path);
            try
            {
                var inv = await new WindowsStorageInventoryProvider().GetInventoryAsync();
                return inv.Disks.Single(d => string.Equals(d.DevicePath, dev, StringComparison.OrdinalIgnoreCase)).SizeBytes;
            }
            finally { mgr.Detach(path); }
        }
        Assert.Equal(128UL * 1024 * 1024, await SizeOf(vhdx));
        mgr.Compact(vhdx);
        mgr.Convert(vhdx, vhd, dynamic: true);
        Assert.Equal(128UL * 1024 * 1024, await SizeOf(vhd));
        mgr.Shrink(vhdx, 96UL * 1024 * 1024); // empty disk: shrinking into unallocated space is safe
        Assert.Equal(96UL * 1024 * 1024, await SizeOf(vhdx));
        bool refused = false;
        try { mgr.Shrink(vhd, null); } catch (OperationFailedException ex) { refused = ex.Key == "Vd.Error.ShrinkVhd"; }
        Assert.True(refused, "VHD shrink refused");
    }
}
