using System.Runtime.Versioning;
using Lasteast.Core.Health;
using Lasteast.Core.Performance;
using Lasteast.Storage.Windows.Detection;
using Lasteast.Storage.Windows.Health;
using Lasteast.Storage.Windows.Operations;

namespace Lasteast.IntegrationTests;

/// <summary>Phase 9 on the runner: health reading never invents data, unbuffered benchmark, read-only surface test.</summary>
[SupportedOSPlatform("windows")]
public static class HealthIntegrationTests
{
    [Test]
    public static async Task Health_of_every_runner_disk_is_read_and_unknown_when_nothing_is_reported()
    {
        var inv = await new WindowsStorageInventoryProvider().GetInventoryAsync();
        var provider = new WindowsHealthProvider();
        foreach (var d in inv.Disks)
        {
            var r = await provider.ReadAsync(d);
            Console.WriteLine($"    disk {d.Number} {d.Model} {d.Bus}: {r.Verdict} sources=[{string.Join(", ", r.Sources)}] temp={r.Temperature} hours={r.PowerOnHours} wear={r.WearPercent}");
            if (r.Sources.Count == 0) Assert.Equal(HealthVerdict.Unknown, r.Verdict);
            else Assert.True(r.Verdict != HealthVerdict.Unknown, "data present but no verdict");
        }
    }

    [Test]
    public static void Unbuffered_benchmark_runs_and_deletes_its_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lasteast-itest", "bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var r = Benchmark.Run(dir, 64L * 1024 * 1024, TimeSpan.FromSeconds(1), bypassCache: true, null, CancellationToken.None);
        Console.WriteLine($"    seq R {r.SeqReadMBps:0} W {r.SeqWriteMBps:0} MB/s · 4K R {r.Rnd4kReadIops:0} W {r.Rnd4kWriteIops:0} IOPS");
        Assert.True(r.CacheBypassed && r.SeqReadMBps > 1 && r.Rnd4kReadIops > 1);
        Assert.Equal(0, Directory.GetFiles(dir).Length);
    }

    [Test]
    public static async Task Surface_scan_of_a_virtual_disk_reads_everything_and_finds_no_errors()
    {
        using var vd = await VirtualDisk.CreateAsync(1311, "convert gpt", "create partition primary size=100", "format fs=ntfs quick");
        var disk = await vd.ReadAsync();
        using var dev = new PhysicalDiskDevice(disk, writable: false);
        var r = SurfaceScanner.Scan(dev, TimeSpan.FromSeconds(2), null, CancellationToken.None);
        Assert.True(r.Completed && r.BytesScanned == (long)disk.SizeBytes, $"{r.BytesScanned} of {disk.SizeBytes}");
        Assert.Equal(0, r.ErrorBlocks);
    }
}
