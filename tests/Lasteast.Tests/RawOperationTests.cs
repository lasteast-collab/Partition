using Lasteast.Core.Models;
using Lasteast.Core.Operations;
using Lasteast.Core.RawDisk;

namespace Lasteast.Tests;

/// <summary>
/// The simulator's prediction for raw operations must match what the raw engine actually writes:
/// each test simulates, runs the real engine on a disk image, re-reads the image and asks the engine's
/// own <see cref="GuardedExecutionEngine.Verify"/> whether they agree.
/// </summary>
public static class RawOperationTests
{
    private const ulong MiB = 1024 * 1024;
    private static bool HasError(SimulationResult r, string key) => r.AllIssues.Any(i => i.IsError && i.Key == key);
    private static string Keys(SimulationResult r) => string.Join(",", r.AllIssues.Select(i => i.Key));

    [Test]
    public static void Convert_mbr_to_gpt_prediction_matches_engine_result()
    {
        using var img = DiskImage.MbrWithVolumes();
        DiskInfo before;
        using (var dev = img.Open()) before = DiskImage.Describe(dev);
        var op = new ConvertStyleOp(7, PartitionStyle.Gpt);
        var sim = LayoutSimulator.Simulate(before, new[] { op });
        Assert.True(sim.IsValid, Keys(sim));
        Assert.Equal(PartitionStyle.Gpt, sim.After.Style);
        Assert.True(sim.After.Partitions.All(p => p.GptType == PartitionTypes.GptBasicData));
        Assert.True(sim.AllIssues.Any(i => i.Key == "Conv.Warn.BootFlag"), "active flag warning expected");

        using (var dev = img.Open()) StyleConverter.ConvertMbrToGpt(dev);
        DiskInfo after;
        using (var dev = img.Open()) after = DiskImage.Describe(dev);
        Assert.Equal(null, GuardedExecutionEngine.Verify(op, sim.After, after));
    }

    [Test]
    public static void Convert_gpt_to_mbr_prediction_matches_engine_result()
    {
        using var img = DiskImage.GptWithVolumes();
        DiskInfo before;
        using (var dev = img.Open()) before = DiskImage.Describe(dev);
        var op = new ConvertStyleOp(7, PartitionStyle.Mbr);
        var sim = LayoutSimulator.Simulate(before, new[] { op });
        Assert.True(sim.IsValid, Keys(sim));

        using (var dev = img.Open()) StyleConverter.ConvertGptToMbr(dev, new Dictionary<ulong, string?>());
        DiskInfo after;
        using (var dev = img.Open()) after = DiskImage.Describe(dev);
        Assert.Equal(null, GuardedExecutionEngine.Verify(op, sim.After, after));
    }

    [Test]
    public static void Convert_refuses_windows_disk_full_disks_and_no_change()
    {
        var inv = Fixtures.Real();
        var win = inv.Disks.First(d => d.IsWindowsDisk);
        Assert.True(HasError(LayoutSimulator.Simulate(win, new[] { new ConvertStyleOp(win.Number, PartitionStyle.Mbr) }), "Conv.Error.SystemDisk"));

        using var img = DiskImage.MbrWithVolumes();
        DiskInfo d;
        using (var dev = img.Open()) d = DiskImage.Describe(dev);
        Assert.True(HasError(LayoutSimulator.Simulate(d, new[] { new ConvertStyleOp(7, PartitionStyle.Mbr) }), "Op.Error.NoChange"));
        // a partition reaching the last sector leaves no room for the backup GPT
        var full = d with { Partitions = d.Partitions.Select((p, i) => i == 1 ? p with { SizeBytes = d.SizeBytes - p.OffsetBytes } : p).ToList() };
        Assert.True(HasError(LayoutSimulator.Simulate(full, new[] { new ConvertStyleOp(7, PartitionStyle.Gpt) }), "Conv.Error.NoRoomEnd"));
        // five partitions cannot become MBR
        var gpt = d with
        {
            Style = PartitionStyle.Gpt, IsWindowsDisk = false,
            Partitions = Enumerable.Range(0, 5).Select(i => new PartitionInfo
            {
                DiskNumber = 7, PartitionNumber = i + 1, OffsetBytes = (ulong)(1 + i * 10) * MiB, SizeBytes = 8 * MiB,
                Style = PartitionStyle.Gpt, Role = PartitionRole.BasicData, GptType = PartitionTypes.GptBasicData,
            }).ToList(),
        };
        Assert.True(HasError(LayoutSimulator.Simulate(gpt, new[] { new ConvertStyleOp(7, PartitionStyle.Mbr) }), "Conv.Error.TooMany"));
    }

    [Test]
    public static void Move_prediction_matches_engine_result_and_rejects_overlap()
    {
        using var img = DiskImage.MbrWithVolumes();
        DiskInfo before;
        using (var dev = img.Open()) before = DiskImage.Describe(dev);
        var fat = before.Partitions.Single(p => p.OffsetBytes == 32 * MiB);

        Assert.True(HasError(LayoutSimulator.Simulate(before, new[] { new MovePartitionOp(7, fat.OffsetBytes, 20 * MiB) }), "Move.Error.NoSpace"));
        Assert.True(HasError(LayoutSimulator.Simulate(before, new[] { new MovePartitionOp(7, fat.OffsetBytes, 60 * MiB) }), "Move.Error.NoSpace"));
        Assert.True(HasError(LayoutSimulator.Simulate(before, new[] { new MovePartitionOp(7, fat.OffsetBytes, 50 * MiB + 512) }), "Op.Error.Granularity"));

        var op = new MovePartitionOp(7, fat.OffsetBytes, 50 * MiB, fat.SizeBytes);
        var sim = LayoutSimulator.Simulate(before, new[] { op });
        Assert.True(sim.IsValid, Keys(sim));
        Assert.True(sim.After.Partitions.Any(p => p.OffsetBytes == 50 * MiB && p.SizeBytes == fat.SizeBytes));

        var journal = Path.Combine(Path.GetTempPath(), $"lasteast-move-{Guid.NewGuid():N}.json");
        using (var dev = img.Open())
            PartitionMover.Run(dev, PartitionMover.NewJournal("img", (long)op.OffsetBytes, (long)op.NewOffsetBytes, (long)fat.SizeBytes, 512), journal, null, CancellationToken.None);
        File.Delete(journal);
        DiskInfo after;
        using (var dev = img.Open()) after = DiskImage.Describe(dev);
        Assert.Equal(null, GuardedExecutionEngine.Verify(op, sim.After, after));
    }

    [Test]
    public static void Move_refuses_running_windows_volume()
    {
        var inv = Fixtures.Real();
        var c = Fixtures.Letter(inv, 'C');
        var win = Fixtures.Disk(inv, c.DiskNumber);
        Assert.True(HasError(LayoutSimulator.Simulate(win, new[] { new MovePartitionOp(win.Number, c.OffsetBytes, c.OffsetBytes + 100 * MiB) }), "Move.Error.InUseSystem"));
    }

    [Test]
    public static void Clone_prediction_matches_engine_result()
    {
        using var src = DiskImage.GptWithVolumes(96);
        using var dst = new DiskImage(128);
        DiskInfo s, d;
        using (var dev = src.Open()) s = DiskImage.Describe(dev, 7);
        using (var dev = dst.Open()) d = DiskImage.Describe(dev, 8);
        var op = new CloneDiskOp(8, 7, CloneMode.SectorBySector, NewIdentifiers: true, Verify: true);
        var sim = LayoutSimulator.Simulate(d, new[] { op }, null, n => n == 7 ? s : null);
        Assert.True(sim.IsValid, Keys(sim));
        Assert.True(sim.AllIssues.Any(i => i.Key == "Clone.Warn.ExtraSpace"));
        Assert.True(sim.DestroysData);

        using (var sd = src.Open())
        using (var dd = dst.Open())
        {
            var plan = ClonePlanner.Plan(s, dd.Length, 512, op.Mode, op.NewIdentifiers);
            Cloner.Run(sd, dd, plan, op.Verify, null, CancellationToken.None);
        }
        DiskInfo after;
        using (var dev = dst.Open()) after = DiskImage.Describe(dev, 8);
        Assert.Equal(null, GuardedExecutionEngine.Verify(op, sim.After, after));
    }

    [Test]
    public static void Clone_validation_errors()
    {
        using var src = DiskImage.GptWithVolumes(96);
        using var small = new DiskImage(48);
        DiskInfo s, d;
        using (var dev = src.Open()) s = DiskImage.Describe(dev, 7);
        using (var dev = small.Open()) d = DiskImage.Describe(dev, 8);
        DiskInfo? Look(int n) => n == 7 ? s : null;

        Assert.True(HasError(LayoutSimulator.Simulate(d, new[] { new CloneDiskOp(8, 7, CloneMode.SectorBySector, true, true) }, null, Look), "Clone.Error.TooSmall"));
        Assert.True(HasError(LayoutSimulator.Simulate(d, new[] { new CloneDiskOp(8, 8, CloneMode.SectorBySector, true, true) }, null, Look), "Clone.Error.SameDisk"));
        Assert.True(HasError(LayoutSimulator.Simulate(d, new[] { new CloneDiskOp(8, 9, CloneMode.SectorBySector, true, true) }, null, Look), "Clone.Error.SourceMissing"));
        // copying only the first partition (ends at 25 MiB) fits on the small disk
        var only = new CloneDiskOp(8, 7, CloneMode.SectorBySector, true, true, new[] { MiB });
        var ok = LayoutSimulator.Simulate(d, new[] { only }, null, Look);
        Assert.True(ok.IsValid, Keys(ok));
        Assert.Equal(1, ok.After.Partitions.Count);
        var windowsDest = d with { IsWindowsDisk = true };
        Assert.True(HasError(LayoutSimulator.Simulate(windowsDest, new[] { only }, null, Look), "Clone.Error.DestSystem"));
    }

    [Test]
    public static void Queue_rejects_clone_whose_source_has_pending_changes()
    {
        using var src = DiskImage.MbrWithVolumes(96);
        using var dst = new DiskImage(128);
        DiskInfo s, d;
        using (var dev = src.Open()) s = DiskImage.Describe(dev, 7) with { Bus = BusKind.FileBackedVirtual };
        using (var dev = dst.Open()) d = DiskImage.Describe(dev, 8) with { Bus = BusKind.FileBackedVirtual };
        var q = new OperationQueue();
        q.Rebase(new StorageInventory { CapturedAt = DateTimeOffset.Now, Disks = new[] { s, d }, IsElevated = true });

        var clone = new CloneDiskOp(8, 7, CloneMode.SectorBySector, true, true);
        Assert.True(q.Preview(clone).All(i => !i.IsError), string.Join(",", q.Preview(clone).Select(i => i.Key)));
        Assert.True(q.TryAdd(new SetLabelOp(7, MiB, "X")).Any(i => i.Key == "Op.Error.NoVolume")); // no volume info on images
        Assert.True(q.TryAdd(new ConvertStyleOp(7, PartitionStyle.Gpt)).All(i => !i.IsError));
        Assert.True(q.Preview(clone).Any(i => i.IsError && i.Key == "Clone.Error.SourcePending"));
    }
}
