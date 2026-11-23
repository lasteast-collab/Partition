using Lasteast.Core.Migration;
using Lasteast.Core.Models;
using Lasteast.Core.Operations;
using Lasteast.Core.RawDisk;

namespace Lasteast.Tests;

public static class MigrationTests
{
    private const ulong MiB = 1024 * 1024, GiB = 1024 * MiB;

    /// <summary>Sample inventory with 130 GiB used on C: and an empty disk of <paramref name="destGiB"/> as disk 5.</summary>
    private static StorageInventory With(ulong destGiB)
    {
        var inv = Fixtures.Real();
        var disks = inv.Disks.Select(d => d with
        {
            Partitions = d.Partitions.Select(p => p.Volume?.IsWindowsVolume == true
                ? p with { Volume = p.Volume with { FreeBytes = p.SizeBytes - 130 * GiB } } : p).ToList(),
        }).ToList();
        disks.Add(new DiskInfo { Number = 5, DevicePath = "x", Model = "New SSD", SizeBytes = destGiB * GiB, Style = PartitionStyle.Raw, Bus = BusKind.FileBackedVirtual, Partitions = new List<PartitionInfo>() });
        return inv with { Disks = disks };
    }

    [Test]
    public static void System_partitions_are_esp_msr_windows_and_recovery()
    {
        var sys = OsMigrationPlanner.SystemPartitionsOf(Fixtures.Disk(Fixtures.Real(), 0));
        Assert.Equal(4, sys.Count);
        Assert.True(sys.Select(p => p.Role).SequenceEqual(new[] { PartitionRole.EfiSystem, PartitionRole.MicrosoftReserved, PartitionRole.BasicData, PartitionRole.Recovery }));
    }

    [Test]
    public static void Large_destination_fits_and_replace_mode_keeps_ids_without_boot_step()
    {
        var plan = OsMigrationPlanner.Plan(With(2000), 5, MigrationTarget.ReplaceOldDisk);
        Assert.Equal(MigrationFit.Fits, plan.Fit);
        var clone = (CloneDiskOp)plan.Migration.Single();
        Assert.True(!clone.NewIdentifiers && clone.Verify && clone.Mode == CloneMode.UsedSpace);
        Assert.Equal(4, clone.OnlyPartitions!.Count);
    }

    [Test]
    public static void Keep_both_mode_adds_boot_preparation_and_validates_as_a_queue()
    {
        var inv = With(2000);
        var plan = OsMigrationPlanner.Plan(inv, 5, MigrationTarget.KeepBoth);
        Assert.Equal(MigrationFit.Fits, plan.Fit);
        Assert.Equal(2, plan.Migration.Count);
        Assert.True(plan.Migration[0] is CloneDiskOp { NewIdentifiers: true } && plan.Migration[1] is PrepareBootOp);
        var q = new OperationQueue();
        q.Rebase(inv);
        foreach (var op in plan.Migration) Assert.True(q.TryAdd(op).All(i => !i.IsError), op.ToString());
        Assert.True(q.IsValid);
    }

    [Test]
    public static void Smaller_destination_needs_shrink_and_recovery_move_first()
    {
        var inv = With(500);
        var plan = OsMigrationPlanner.Plan(inv, 5, MigrationTarget.ReplaceOldDisk);
        Assert.Equal(MigrationFit.NeedsPreparation, plan.Fit);
        Assert.Equal(0, plan.Migration.Count);
        Assert.True(plan.Preparation[0] is ResizePartitionOp && plan.Preparation[1] is MovePartitionOp);
        var sim = LayoutSimulator.Simulate(Fixtures.Disk(inv, 0), plan.Preparation);
        Assert.True(sim.IsValid, string.Join(",", sim.AllIssues.Select(i => i.Key)));

        // after applying the preparation, the plan fits
        var prepared = inv with { Disks = inv.Disks.Select(d => d.Number == 0 ? sim.After : d).ToList() };
        var again = OsMigrationPlanner.Plan(prepared, 5, MigrationTarget.ReplaceOldDisk);
        Assert.Equal(MigrationFit.Fits, again.Fit);
        Assert.True(again.RequiredBytes <= 500 * GiB);
    }

    [Test]
    public static void Too_small_destination_is_refused()
    {
        var plan = OsMigrationPlanner.Plan(With(100), 5, MigrationTarget.ReplaceOldDisk);
        Assert.Equal(MigrationFit.TooSmall, plan.Fit);
        Assert.True(plan.Issues.Any(i => i.IsError && i.Key == "Mig.Error.TooSmall"));
    }

    [Test]
    public static void Prepare_boot_refuses_running_windows_and_disks_without_esp()
    {
        var inv = Fixtures.Real();
        Assert.True(LayoutSimulator.Simulate(Fixtures.Disk(inv, 0), new[] { new PrepareBootOp(0) }).AllIssues.Any(i => i.Key == "Boot.Error.RunningSystem"));
        var gptNoEsp = Fixtures.Disk(inv, 0) with { IsWindowsDisk = false, IsBootDisk = false, Partitions = Fixtures.Disk(inv, 0).Partitions.Where(p => p.Role != PartitionRole.EfiSystem).ToList() };
        Assert.True(LayoutSimulator.Simulate(gptNoEsp, new[] { new PrepareBootOp(0) }).AllIssues.Any(i => i.Key == "Boot.Error.NoEsp"));
    }
}
