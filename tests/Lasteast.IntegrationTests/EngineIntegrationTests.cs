using System.Runtime.Versioning;
using Lasteast.Core.Logging;
using Lasteast.Core.Models;
using Lasteast.Core.Operations;
using Lasteast.Storage.Windows.Detection;
using Lasteast.Storage.Windows.Operations;

namespace Lasteast.IntegrationTests;

/// <summary>Phase 2 engine against real virtual disks: real WMI executor, real backup, real verification.</summary>
[SupportedOSPlatform("windows")]
public static class EngineIntegrationTests
{
    private const ulong MiB = 1024 * 1024;

    private static (GuardedExecutionEngine Engine, JsonlOperationHistory History, string BackupRoot) MakeEngine()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lasteast-itest", "engine-" + Guid.NewGuid().ToString("N"));
        var history = new JsonlOperationHistory(Path.Combine(dir, "history.jsonl"));
        var log = new FileLog(Path.Combine(dir, "logs"));
        var priv = new WindowsPrivilegeService();
        var engine = new GuardedExecutionEngine(new WindowsStorageInventoryProvider(log, priv), new StorageManagementExecutor(),
            new PartitionTableBackupService(Path.Combine(dir, "backups")), priv, history, log);
        return (engine, history, Path.Combine(dir, "backups"));
    }

    private static async Task<StorageInventory> Inventory() =>
        await new WindowsStorageInventoryProvider(null, new WindowsPrivilegeService()).GetInventoryAsync();

    private static char FreeLetter(StorageInventory inv, char start = 'P')
    {
        var used = inv.Disks.SelectMany(d => d.Partitions).Select(p => p.Volume?.DriveLetter).OfType<char>()
            .Concat(DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0]))).ToHashSet();
        for (char c = start; c <= 'Z'; c++) if (!used.Contains(c)) return c;
        throw new InvalidOperationException("no free drive letter");
    }

    [Test]
    public static async Task Inventory_detects_virtual_gpt_disk_and_partitions()
    {
        using var vd = await VirtualDisk.CreateAsync(1101, "convert gpt", "create partition primary size=300", "format fs=ntfs label=LE-ONE quick");
        var d = await vd.ReadAsync();
        Assert.Equal(PartitionStyle.Gpt, d.Style);
        Assert.Equal(BusKind.FileBackedVirtual, d.Bus);
        var data = d.Partitions.Single(p => p.Role == PartitionRole.BasicData);
        Assert.Equal(300 * MiB, data.SizeBytes);
        Assert.Equal("NTFS", data.Volume?.FileSystem);
        Assert.Equal("LE-ONE", data.Volume?.Label);
        Assert.Equal(AlignmentStatus.Aligned1MiB, data.Alignment);
    }

    [Test]
    public static async Task Engine_runs_full_operation_set_on_gpt_vhdx()
    {
        using var vd = await VirtualDisk.CreateAsync(1203, "convert gpt");
        var (engine, history, backups) = MakeEngine();
        var inv = await Inventory();
        var disk = inv.Disks.First(x => x.Number == vd.Number);
        var free = Lasteast.Core.Layout.DiskMapLayout.BuildSegments(disk, out _).Last(s => s.Kind == Lasteast.Core.Layout.SegmentKind.Unallocated);
        var start = (free.OffsetBytes + MiB - 1) / MiB * MiB;
        var letter = FreeLetter(inv);

        // 1. create + format + letter in one step
        var r1 = await engine.ExecuteAsync(new DiskOperation[] { new CreatePartitionOp(vd.Number, start, 400 * MiB, "NTFS", "LE-TEST", letter) },
            inv, new ExecutionPolicy(false), "APPLY");
        Assert.True(r1.Succeeded, $"create: {r1.FailureKey} {r1.FailureDetail}");
        Assert.True(r1.Backups.Count == 1 && File.Exists(Path.Combine(r1.Backups[0].Folder, "manifest.json")), "backup written");

        var d1 = await vd.ReadAsync();
        var p = d1.Partitions.Single(x => x.OffsetBytes == start);
        Assert.Equal("NTFS", p.Volume?.FileSystem);
        Assert.Equal(letter, p.Volume?.DriveLetter);
        File.WriteAllText($"{letter}:\\hello.txt", "Lasteast integration test");

        // 2. label, shrink, extend, letter change in one queue
        inv = await Inventory();
        var newLetter = FreeLetter(inv, (char)(letter + 1));
        var ops = new DiskOperation[]
        {
            new SetLabelOp(vd.Number, start, "LE-RENAMED"),
            new ResizePartitionOp(vd.Number, start, 300 * MiB),
            new ResizePartitionOp(vd.Number, start, 600 * MiB),
            new SetDriveLetterOp(vd.Number, start, newLetter),
        };
        var r2 = await engine.ExecuteAsync(ops, inv, new ExecutionPolicy(false), "APPLY");
        Assert.True(r2.Succeeded, $"queue: {r2.FailureKey} {r2.FailureDetail} steps={string.Join(",", r2.Steps.Select(s => s.State))}");
        var d2 = await vd.ReadAsync();
        var p2 = d2.Partitions.Single(x => x.OffsetBytes == start);
        Assert.Equal(600 * MiB, p2.SizeBytes);
        Assert.Equal("LE-RENAMED", p2.Volume?.Label);
        Assert.Equal(newLetter, p2.Volume?.DriveLetter);
        Assert.Equal("Lasteast integration test", File.ReadAllText($"{newLetter}:\\hello.txt"));

        // 3. format (data loss) then delete
        inv = await Inventory();
        var r3 = await engine.ExecuteAsync(new DiskOperation[] { new FormatPartitionOp(vd.Number, start, "exFAT", "LE-EXFAT") }, inv, new ExecutionPolicy(false), "APPLY");
        Assert.True(r3.Succeeded, $"format: {r3.FailureKey} {r3.FailureDetail}");
        Assert.Equal("exFAT", (await vd.ReadAsync()).Partitions.Single(x => x.OffsetBytes == start).Volume?.FileSystem);
        inv = await Inventory();
        var r4 = await engine.ExecuteAsync(new DiskOperation[] { new DeletePartitionOp(vd.Number, start) }, inv, new ExecutionPolicy(false), "APPLY");
        Assert.True(r4.Succeeded, $"delete: {r4.FailureKey} {r4.FailureDetail}");
        Assert.True((await vd.ReadAsync()).Partitions.All(x => x.OffsetBytes != start));

        Assert.Equal(4, history.ReadAll().Count);
    }

    [Test]
    public static async Task Engine_creates_partitions_on_mbr_vhdx()
    {
        using var vd = await VirtualDisk.CreateAsync(1307, "convert mbr");
        var (engine, _, _) = MakeEngine();
        var inv = await Inventory();
        var ops = new DiskOperation[]
        {
            new CreatePartitionOp(vd.Number, MiB, 200 * MiB, "FAT32", "LE-FAT", null),
            new CreatePartitionOp(vd.Number, 201 * MiB, 300 * MiB, "NTFS", "LE-NTFS", null),
        };
        var r = await engine.ExecuteAsync(ops, inv, new ExecutionPolicy(false), "APPLY");
        Assert.True(r.Succeeded, $"{r.FailureKey} {r.FailureDetail}");
        var d = await vd.ReadAsync();
        Assert.Equal(PartitionStyle.Mbr, d.Style);
        Assert.Equal("FAT32", d.Partitions.Single(x => x.OffsetBytes == MiB).Volume?.FileSystem);
        Assert.Equal((byte)0x0C, d.Partitions.Single(x => x.OffsetBytes == MiB).MbrType);
        Assert.Equal("NTFS", d.Partitions.Single(x => x.OffsetBytes == 201 * MiB).Volume?.FileSystem);
    }

    [Test]
    public static async Task Engine_refuses_physical_disk_without_opt_in_and_writes_nothing()
    {
        var (engine, history, _) = MakeEngine();
        var inv = await Inventory();
        var physical = inv.Disks.First(d => !d.IsVirtual && d.IsWindowsDisk);
        var c = physical.Partitions.First(p => p.Volume?.IsWindowsVolume == true);
        var r = await engine.ExecuteAsync(new DiskOperation[] { new SetLabelOp(physical.Number, c.OffsetBytes, "X") }, inv, new ExecutionPolicy(false), "APPLY");
        Assert.Equal("Exec.Error.PhysicalPolicy", r.FailureKey);
        Assert.True(r.NothingWritten);
        Assert.Equal(1, history.ReadAll().Count);
    }

    [Test]
    public static async Task Windows_refuses_shrink_below_supported_size_and_engine_reports_it()
    {
        using var vd = await VirtualDisk.CreateAsync(1409, "convert gpt", "create partition primary size=500", "format fs=ntfs label=LE-FULL quick", "assign");
        var d = await vd.ReadAsync();
        var p = d.Partitions.Single(x => x.Role == PartitionRole.BasicData);
        // fill the volume so Windows' own minimum is high
        var letter = p.Volume!.DriveLetter!.Value;
        using (var fs = new FileStream($"{letter}:\\fill.bin", FileMode.Create)) fs.SetLength(400 * (long)MiB);
        var (engine, _, _) = MakeEngine();
        var inv = await Inventory();
        // the simulator uses cached used-space; force a shrink that Windows itself must refuse
        var disk = inv.Disks.First(x => x.Number == vd.Number);
        var fresh = disk.Partitions.Single(x => x.OffsetBytes == p.OffsetBytes);
        var r = await engine.ExecuteAsync(new DiskOperation[] { new ResizePartitionOp(vd.Number, p.OffsetBytes, 100 * MiB) }, inv, new ExecutionPolicy(false), "APPLY");
        Assert.True(!r.Succeeded, "shrinking below the data must fail");
        Assert.True(r.FailureKey is "Exec.Error.Invalid" or "Exec.Error.SupportedRange" or "Exec.Error.Wmi", r.FailureKey);
        Assert.Equal(500 * MiB, (await vd.ReadAsync()).Partitions.Single(x => x.OffsetBytes == p.OffsetBytes).SizeBytes);
    }
}
