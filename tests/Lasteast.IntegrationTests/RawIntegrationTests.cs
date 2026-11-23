using System.Runtime.Versioning;
using System.Security.Cryptography;
using Lasteast.Core.Logging;
using Lasteast.Core.Models;
using Lasteast.Core.Operations;
using Lasteast.Core.RawDisk;
using Lasteast.Storage.Windows.Detection;
using Lasteast.Storage.Windows.Operations;

namespace Lasteast.IntegrationTests;

/// <summary>
/// Phase 5 raw operations through the guarded engine on real virtual disks: the data written before the
/// operation must read back byte-identical through Windows' own file system drivers afterwards.
/// </summary>
[SupportedOSPlatform("windows")]
public static class RawIntegrationTests
{
    private const ulong MiB = 1024 * 1024;

    private static GuardedExecutionEngine MakeEngine()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lasteast-itest", "raw-" + Guid.NewGuid().ToString("N"));
        var log = new FileLog(Path.Combine(dir, "logs"));
        var priv = new WindowsPrivilegeService();
        var provider = new WindowsStorageInventoryProvider(log, priv);
        return new GuardedExecutionEngine(provider, new WindowsOperationExecutor(provider, log, Path.Combine(dir, "journal")),
            new PartitionTableBackupService(Path.Combine(dir, "backups")), priv, new JsonlOperationHistory(Path.Combine(dir, "history.jsonl")), log);
    }

    private static Task<StorageInventory> Inventory() => new WindowsStorageInventoryProvider(null, new WindowsPrivilegeService()).GetInventoryAsync();

    private static char FreeLetter()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        for (char c = 'Z'; c >= 'G'; c--) if (!used.Contains(c)) return c;
        throw new InvalidOperationException("no free drive letter");
    }

    /// <summary>Writes a random 3 MiB file and returns its hash.</summary>
    private static string WritePayload(string root)
    {
        var data = RandomNumberGenerator.GetBytes(3 * 1024 * 1024);
        File.WriteAllBytes(Path.Combine(root, "payload.bin"), data);
        File.WriteAllText(Path.Combine(root, "hello.txt"), "Lasteast raw integration");
        return Convert.ToHexString(SHA256.HashData(data));
    }

    /// <summary>Waits until a mounted volume appears at <paramref name="offset"/> on the disk, then hashes the payload through it.</summary>
    private static async Task<string> ReadPayload(int disk, ulong offset)
    {
        string? last = null;
        for (int i = 0; i < 30; i++)
        {
            var inv = await Inventory();
            var p = inv.Disks.First(d => d.Number == disk).Partitions.FirstOrDefault(x => x.OffsetBytes == offset);
            var v = p?.Volume?.VolumeGuidPath;
            if (v is not null && v.StartsWith(@"\\?\Volume{"))
            {
                try
                {
                    var root = v.EndsWith('\\') ? v : v + "\\";
                    Assert.Equal("Lasteast raw integration", File.ReadAllText(root + "hello.txt"));
                    return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(root + "payload.bin")));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { last = ex.Message; }
            }
            await Task.Delay(1000);
        }
        throw new AssertionException($"volume at {offset} on disk {disk} not readable: {last}");
    }

    private static string Describe(ExecutionReport r) =>
        $"{r.FailureKey} {r.FailureDetail} steps={string.Join(",", r.Steps.Select(s => $"{s.State}:{s.ErrorKey}:{s.ErrorDetail}"))}";

    [Test]
    public static async Task Convert_mbr_to_gpt_and_back_keeps_data()
    {
        var letter = FreeLetter();
        using var vd = await VirtualDisk.CreateAsync(1301, "convert mbr", "create partition primary size=200",
            "format fs=ntfs label=LE-CONV quick", $"assign letter={letter}");
        var hash = WritePayload($"{letter}:\\");
        var before = await vd.ReadAsync();
        var offset = before.Partitions.Single(p => p.Role != PartitionRole.MbrExtended).OffsetBytes;
        var engine = MakeEngine();

        var r1 = await engine.ExecuteAsync(new DiskOperation[] { new ConvertStyleOp(vd.Number, PartitionStyle.Gpt) }, await Inventory(), new ExecutionPolicy(false), "APPLY");
        Assert.True(r1.Succeeded, "to GPT: " + Describe(r1));
        var mid = await vd.ReadAsync();
        Assert.Equal(PartitionStyle.Gpt, mid.Style);
        Assert.Equal(hash, await ReadPayload(vd.Number, offset));

        var r2 = await engine.ExecuteAsync(new DiskOperation[] { new ConvertStyleOp(vd.Number, PartitionStyle.Mbr) }, await Inventory(), new ExecutionPolicy(false), "APPLY");
        Assert.True(r2.Succeeded, "to MBR: " + Describe(r2));
        Assert.Equal(PartitionStyle.Mbr, (await vd.ReadAsync()).Style);
        Assert.Equal(hash, await ReadPayload(vd.Number, offset));
    }

    [Test]
    public static async Task Move_partition_right_and_back_keeps_data()
    {
        var letter = FreeLetter();
        using var vd = await VirtualDisk.CreateAsync(1402, "convert gpt", "create partition primary size=160",
            "format fs=ntfs label=LE-MOVE quick", $"assign letter={letter}");
        var hash = WritePayload($"{letter}:\\");
        var before = await vd.ReadAsync();
        var part = before.Partitions.Single(p => p.Role == PartitionRole.BasicData);
        var engine = MakeEngine();

        // right by 400 MiB (non-overlapping), then left by 100 MiB (overlapping copy)
        var target = part.OffsetBytes + 400 * MiB;
        var r1 = await engine.ExecuteAsync(new DiskOperation[] { new MovePartitionOp(vd.Number, part.OffsetBytes, target, part.SizeBytes) },
            await Inventory(), new ExecutionPolicy(false), "APPLY");
        Assert.True(r1.Succeeded, "move right: " + Describe(r1));
        Assert.Equal(hash, await ReadPayload(vd.Number, target));

        var back = target - 100 * MiB;
        var r2 = await engine.ExecuteAsync(new DiskOperation[] { new MovePartitionOp(vd.Number, target, back, part.SizeBytes) },
            await Inventory(), new ExecutionPolicy(false), "APPLY");
        Assert.True(r2.Succeeded, "move left: " + Describe(r2));
        var after = await vd.ReadAsync();
        Assert.Equal(part.SizeBytes, after.Partitions.Single(p => p.OffsetBytes == back).SizeBytes);
        Assert.Equal(hash, await ReadPayload(vd.Number, back));
    }

    [Test]
    public static async Task Clone_vhdx_to_larger_vhdx_with_new_ids_and_verification()
    {
        var letter = FreeLetter();
        using var src = await VirtualDisk.CreateAsync(1503, "convert gpt", "create partition primary size=200",
            "format fs=ntfs label=LE-SRC quick", $"assign letter={letter}");
        var hash = WritePayload($"{letter}:\\");
        using var dst = await VirtualDisk.CreateAsync(1704);
        var s = await src.ReadAsync();
        var offset = s.Partitions.Single(p => p.Role == PartitionRole.BasicData).OffsetBytes;

        var engine = MakeEngine();
        var op = new CloneDiskOp(dst.Number, src.Number, CloneMode.UsedSpace, NewIdentifiers: true, Verify: true);
        var r = await engine.ExecuteAsync(new DiskOperation[] { op }, await Inventory(), new ExecutionPolicy(false), "APPLY");
        Assert.True(r.Succeeded, "clone: " + Describe(r));

        var d = await dst.ReadAsync();
        Assert.Equal(PartitionStyle.Gpt, d.Style);
        Assert.True(d.Partitions.Count == s.Partitions.Count, "same partition count");
        Assert.True(d.Partitions.Single(p => p.OffsetBytes == offset).GptId != s.Partitions.Single(p => p.OffsetBytes == offset).GptId, "new partition GUID");
        Assert.Equal(hash, await ReadPayload(dst.Number, offset));
        // the source is untouched
        Assert.Equal(hash, await ReadPayload(src.Number, offset));
    }

    [Test]
    public static async Task Clone_refuses_when_destination_too_small_and_writes_nothing()
    {
        using var src = await VirtualDisk.CreateAsync(1605, "convert gpt", "create partition primary size=1400", "format fs=ntfs quick");
        using var dst = await VirtualDisk.CreateAsync(1206, "convert mbr");
        var engine = MakeEngine();
        var r = await engine.ExecuteAsync(new DiskOperation[] { new CloneDiskOp(dst.Number, src.Number, CloneMode.SectorBySector, true, false) },
            await Inventory(), new ExecutionPolicy(false), "APPLY");
        Assert.True(!r.Succeeded && r.NothingWritten, Describe(r));
        Assert.Equal("Exec.Error.Invalid", r.FailureKey);
        Assert.Equal(0, (await dst.ReadAsync()).Partitions.Count);
    }
}
