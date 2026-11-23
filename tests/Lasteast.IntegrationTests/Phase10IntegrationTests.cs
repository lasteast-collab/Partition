using System.Runtime.Versioning;
using Lasteast.Core.Logging;
using Lasteast.Core.Models;
using Lasteast.Core.Operations;
using Lasteast.Core.Reports;
using Lasteast.Storage.Windows.Detection;
using Lasteast.Storage.Windows.Info;
using Lasteast.Storage.Windows.Operations;

namespace Lasteast.IntegrationTests;

/// <summary>Phase 10 on the runner: virtdisk.dll create/attach/detach + initialize through the engine; read-only system info; reports.</summary>
[SupportedOSPlatform("windows")]
public static class Phase10IntegrationTests
{
    [Test]
    public static async Task Virtual_disk_created_with_virtdisk_is_initialized_and_partitioned_through_the_engine()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lasteast-itest", "vd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "test.vhdx");
        VirtualDiskService.Create(file, 1412UL * 1024 * 1024, dynamic: true);
        var device = VirtualDiskService.Attach(file);
        try
        {
            Assert.True(device.StartsWith(@"\\.\PhysicalDrive", StringComparison.OrdinalIgnoreCase), device);
            var log = new FileLog(Path.Combine(dir, "logs"));
            var priv = new WindowsPrivilegeService();
            var provider = new WindowsStorageInventoryProvider(log, priv);
            DiskInfo? disk = null;
            for (int i = 0; i < 20 && disk is null; i++)
            {
                disk = (await provider.GetInventoryAsync()).Disks.FirstOrDefault(d => string.Equals(d.DevicePath, device, StringComparison.OrdinalIgnoreCase));
                if (disk is null) await Task.Delay(500);
            }
            Assert.True(disk is { IsVirtual: true }, "attached disk is detected as virtual");
            var backing = VirtualDiskService.BackingFile(device);
            Assert.True(backing is not null && backing.EndsWith("test.vhdx", StringComparison.OrdinalIgnoreCase), $"backing file {backing}");

            var engine = new GuardedExecutionEngine(provider, new WindowsOperationExecutor(provider, log, Path.Combine(dir, "journal")),
                new PartitionTableBackupService(Path.Combine(dir, "backups")), priv, new JsonlOperationHistory(Path.Combine(dir, "h.jsonl")), log);
            var r = await engine.ExecuteAsync(new DiskOperation[] { new InitializeDiskOp(disk!.Number, PartitionStyle.Gpt) }, await provider.GetInventoryAsync(), new ExecutionPolicy(false), "APPLY");
            Assert.True(r.Succeeded, $"initialize: {r.FailureKey} {r.FailureDetail}");
            var inv = await provider.GetInventoryAsync();
            var d = inv.Disks.First(x => x.Number == disk.Number);
            Assert.Equal(PartitionStyle.Gpt, d.Style);
            var free = Lasteast.Core.Layout.DiskMapLayout.BuildSegments(d, out _).Last(s => s.Kind == Lasteast.Core.Layout.SegmentKind.Unallocated);
            var start = (free.OffsetBytes + (1UL << 20) - 1) / (1UL << 20) * (1UL << 20);
            var c = await engine.ExecuteAsync(new DiskOperation[] { new CreatePartitionOp(d.Number, start, 300UL << 20, "NTFS", "LE-VD", null) }, inv, new ExecutionPolicy(false), "APPLY");
            Assert.True(c.Succeeded, $"create: {c.FailureKey} {c.FailureDetail}");
        }
        finally
        {
            VirtualDiskService.Detach(file);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Test]
    public static async Task System_info_and_reports_work_read_only_on_the_runner()
    {
        var info = new WindowsSystemInfo();
        var bl = await info.BitLockerAsync();
        var spaces = await info.StorageSpacesAsync();
        Console.WriteLine($"    BitLocker volumes: {bl.Count} · pools: {spaces.Pools.Count} · spaces: {spaces.Spaces.Count}");
        var inv = await new WindowsStorageInventoryProvider().GetInventoryAsync();
        var win = inv.Disks.First(d => d.IsWindowsDisk);
        var c = win.Partitions.First(p => p.Volume?.IsWindowsVolume == true);
        var shrink = await info.ShrinkAsync(win, c);
        Assert.True(shrink is not null && shrink.WindowsMinimumSize is > 0 && shrink.WindowsMinimumSize <= c.SizeBytes, $"min {shrink?.WindowsMinimumSize}");
        Console.WriteLine($"    C: min {shrink!.WindowsMinimumSize} blockers: {string.Join(", ", shrink.Blockers.Select(b => $"{b.Kind}={b.Bytes}"))}");
        var html = TechnicianReport.Html(new ReportInput { Inventory = inv, BitLocker = bl, Spaces = spaces });
        Assert.True(html.Contains("</html>") && html.Contains(win.Model ?? ""));
    }
}
