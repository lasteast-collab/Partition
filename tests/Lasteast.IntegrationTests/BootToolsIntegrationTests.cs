using System.Runtime.Versioning;
using Lasteast.Core.Boot;
using Lasteast.Core.Logging;
using Lasteast.Core.Models;
using Lasteast.Core.Operations;
using Lasteast.Storage.Windows.Detection;
using Lasteast.Storage.Windows.Operations;

namespace Lasteast.IntegrationTests;

/// <summary>Phase 8 on the runner: read the real BCD store, and restore a partition table from the engine's own automatic backup.</summary>
[SupportedOSPlatform("windows")]
public static class BootToolsIntegrationTests
{
    [Test]
    public static async Task Bcd_store_of_the_runner_is_parsed_read_only()
    {
        var entries = await new WindowsBootTools().EnumerateBcdAsync();
        Assert.True(entries.Count >= 2, $"{entries.Count} entries");
        Assert.True(entries.Any(e => e.Identifier == "{bootmgr}" || e.Get("path")?.Contains("bootmgfw", StringComparison.OrdinalIgnoreCase) == true || e.Get("path")?.Contains("bootmgr", StringComparison.OrdinalIgnoreCase) == true), "boot manager entry");
        Assert.True(entries.Any(e => e.Get("path")?.Contains("winload", StringComparison.OrdinalIgnoreCase) == true), "Windows loader entry");
    }

    [Test]
    public static async Task Partition_deleted_through_the_engine_comes_back_from_its_automatic_table_backup()
    {
        using var vd = await VirtualDisk.CreateAsync(2110, "convert gpt", "create partition primary size=200", "format fs=ntfs quick label=LE-TB1",
            "create partition primary size=200", "format fs=ntfs quick label=LE-TB2");
        var dir = Path.Combine(Path.GetTempPath(), "lasteast-itest", "tb-" + Guid.NewGuid().ToString("N"));
        var log = new FileLog(Path.Combine(dir, "logs"));
        var priv = new WindowsPrivilegeService();
        var provider = new WindowsStorageInventoryProvider(log, priv);
        var engine = new GuardedExecutionEngine(provider, new WindowsOperationExecutor(provider, log, Path.Combine(dir, "journal")),
            new PartitionTableBackupService(Path.Combine(dir, "backups")), priv, new JsonlOperationHistory(Path.Combine(dir, "h.jsonl")), log);

        var disk = await vd.ReadAsync();
        var second = disk.Partitions.Single(p => p.Volume?.Label == "LE-TB2");
        var del = await engine.ExecuteAsync(new DiskOperation[] { new DeletePartitionOp(vd.Number, second.OffsetBytes) }, await provider.GetInventoryAsync(), new ExecutionPolicy(false), "APPLY");
        Assert.True(del.Succeeded, $"{del.FailureKey} {del.FailureDetail}");
        Assert.True((await vd.ReadAsync()).Partitions.All(p => p.OffsetBytes != second.OffsetBytes), "deleted");

        var b = TableBackups.Load(del.Backups[0].Folder);
        Assert.True(b.Partitions.Contains((second.OffsetBytes, second.SizeBytes)), "backup has the deleted partition");
        var op = new RestoreTableOp(vd.Number, b.Folder, b.DiskSize, b.Serial, b.Model, b.Style, b.Partitions, b.Created);
        var r = await engine.ExecuteAsync(new DiskOperation[] { op }, await provider.GetInventoryAsync(), new ExecutionPolicy(false), "APPLY");
        Assert.True(r.Succeeded, $"{r.FailureKey} {r.FailureDetail}");

        for (int i = 0; i < 20; i++)
        {
            var p = (await vd.ReadAsync()).Partitions.FirstOrDefault(x => x.OffsetBytes == second.OffsetBytes);
            if (p?.Volume?.Label == "LE-TB2") return; // the volume (its data was never touched) mounts again
            await Task.Delay(1000);
        }
        throw new AssertionException("restored partition's volume did not come back");
    }
}
