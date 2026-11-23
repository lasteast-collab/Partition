using Lasteast.Core.Models;
using Lasteast.Core.Operations;
using Lasteast.Core.RawDisk;

namespace Lasteast.Core.Migration;

public enum MigrationFit
{
    /// <summary>The system partitions fit at their current positions.</summary>
    Fits,
    /// <summary>They fit after shrinking C: (and moving the partitions behind it) on the source first.</summary>
    NeedsPreparation,
    /// <summary>Even the used data of C: does not fit.</summary>
    TooSmall,
    NoWindowsDisk,
    Invalid,
}

/// <summary>How the copy will be used afterwards.</summary>
public enum MigrationTarget
{
    /// <summary>The new disk replaces the old one (old disk removed or no longer booted). Identifiers are kept, so Windows boots unchanged.</summary>
    ReplaceOldDisk,
    /// <summary>Both disks stay connected. The copy gets new identifiers, its own boot files and its own C: mapping.</summary>
    KeepBoth,
}

public sealed record MigrationPlan
{
    public required MigrationFit Fit { get; init; }
    public DiskInfo? Source { get; init; }
    public DiskInfo? Destination { get; init; }
    public IReadOnlyList<PartitionInfo> SystemPartitions { get; init; } = Array.Empty<PartitionInfo>();
    /// <summary>Partitions on the Windows disk that are not part of the migration (data partitions).</summary>
    public IReadOnlyList<PartitionInfo> LeftBehind { get; init; } = Array.Empty<PartitionInfo>();
    public ulong RequiredBytes { get; init; }
    public ulong DataBytes { get; init; }
    /// <summary>Run these first (on the source disk), refresh, then plan again.</summary>
    public IReadOnlyList<DiskOperation> Preparation { get; init; } = Array.Empty<DiskOperation>();
    public IReadOnlyList<DiskOperation> Migration { get; init; } = Array.Empty<DiskOperation>();
    public ulong? ShrinkCTo { get; init; }
    public IReadOnlyList<ValidationIssue> Issues { get; init; } = Array.Empty<ValidationIssue>();
}

/// <summary>
/// Spec module "OS migration": copies the partitions Windows needs to start (EFI system, MSR, Windows, Recovery —
/// or on BIOS/MBR the active partition and Windows) to another disk at the same positions. When the target is smaller,
/// the plan first shrinks C: on the source with Windows' own shrink and moves the Recovery partition after it; nothing
/// is ever squeezed into a smaller partition by rewriting file-system structures.
/// </summary>
public static class OsMigrationPlanner
{
    private const ulong MiB = LayoutSimulator.MiB;
    /// <summary>Head-room left on C: after the shrink so Windows keeps working.</summary>
    public const ulong MinimumFreeAfterShrink = 4UL * 1024 * MiB;

    public static IReadOnlyList<PartitionInfo> SystemPartitionsOf(DiskInfo disk)
    {
        var parts = disk.Partitions.Where(p => p.Role != PartitionRole.MbrExtended && !p.IsPending).ToList();
        var c = parts.FirstOrDefault(p => p.Volume?.IsWindowsVolume == true);
        if (c is null) return Array.Empty<PartitionInfo>();
        return parts.Where(p => ReferenceEquals(p, c)
                                || p.Role is PartitionRole.EfiSystem or PartitionRole.MicrosoftReserved or PartitionRole.Recovery
                                || p.Volume?.IsSystemPartitionVolume == true
                                || (disk.Style == PartitionStyle.Mbr && p.MbrActive))
                    .OrderBy(p => p.OffsetBytes).ToList();
    }

    public static MigrationPlan Plan(StorageInventory inv, int destinationDisk, MigrationTarget target)
    {
        var src = inv.Disks.FirstOrDefault(d => d.IsWindowsDisk);
        if (src is null) return new MigrationPlan { Fit = MigrationFit.NoWindowsDisk };
        var dst = inv.Disks.FirstOrDefault(d => d.Number == destinationDisk);
        if (dst is null) return new MigrationPlan { Fit = MigrationFit.Invalid, Source = src, Issues = new[] { new ValidationIssue(IssueLevel.Error, "Clone.Error.SourceMissing", destinationDisk) } };

        var sys = SystemPartitionsOf(src);
        var c = sys.FirstOrDefault(p => p.Volume?.IsWindowsVolume == true);
        if (c is null) return new MigrationPlan { Fit = MigrationFit.NoWindowsDisk, Source = src, Destination = dst };
        var leftBehind = src.Partitions.Where(p => p.Role != PartitionRole.MbrExtended && !sys.Contains(p)).ToList();
        ulong data = sys.Aggregate(0UL, (a, p) => a + (p.Volume?.UsedBytes ?? p.SizeBytes));

        var usableEnd = src.Style == PartitionStyle.Gpt ? dst.SizeBytes - LayoutSimulator.GptTailBytes(dst) : Math.Min(dst.SizeBytes, LayoutSimulator.Mbr2TiB);
        var required = sys.Max(p => p.EndBytes);
        var baseIssues = new List<ValidationIssue>();
        if (leftBehind.Count > 0) baseIssues.Add(new ValidationIssue(IssueLevel.Warning, "Mig.Warn.LeftBehind", leftBehind.Count));

        if (required <= usableEnd)
        {
            var only = sys.Select(p => p.OffsetBytes).ToList();
            bool newIds = target == MigrationTarget.KeepBoth;
            var ops = new List<DiskOperation> { new CloneDiskOp(dst.Number, src.Number, CloneMode.UsedSpace, newIds, Verify: true, only, data) };
            if (newIds) ops.Add(new PrepareBootOp(dst.Number));
            var sim = LayoutSimulator.Simulate(dst, ops, null, n => inv.Disks.FirstOrDefault(d => d.Number == n));
            var issues = baseIssues.Concat(sim.AllIssues).ToList();
            return new MigrationPlan
            {
                Fit = sim.IsValid ? MigrationFit.Fits : MigrationFit.Invalid, Source = src, Destination = dst, SystemPartitions = sys, LeftBehind = leftBehind,
                RequiredBytes = required, DataBytes = data, Migration = sim.IsValid ? ops : Array.Empty<DiskOperation>(), Issues = issues,
            };
        }

        // Does not fit as is: everything after C: must move left behind a smaller C:.
        var after = sys.Where(p => p.OffsetBytes > c.OffsetBytes).ToList();
        if (sys.Any(p => p.OffsetBytes > c.EndBytes && !after.Contains(p)) || leftBehind.Any(p => p.OffsetBytes > c.OffsetBytes && p.OffsetBytes < (after.Count > 0 ? after.Max(a => a.EndBytes) : c.EndBytes)))
            return new MigrationPlan { Fit = MigrationFit.Invalid, Source = src, Destination = dst, SystemPartitions = sys, Issues = baseIssues.Append(new ValidationIssue(IssueLevel.Error, "Mig.Error.Layout")).ToList() };

        ulong tailBytes = after.Aggregate(0UL, (a, p) => a + (p.SizeBytes + MiB - 1) / MiB * MiB);
        if (usableEnd < c.OffsetBytes + tailBytes) return TooSmall();
        var newC = (usableEnd - tailBytes - c.OffsetBytes) / MiB * MiB;
        var minC = (c.Volume?.UsedBytes ?? c.SizeBytes) + MinimumFreeAfterShrink;
        if (newC < minC || newC < LayoutSimulator.MinShrinkSize(c)) return TooSmall();

        var prep = new List<DiskOperation> { new ResizePartitionOp(src.Number, c.OffsetBytes, newC) };
        ulong cursor = c.OffsetBytes + newC;
        foreach (var p in after)
        {
            if (p.OffsetBytes != cursor) prep.Add(new MovePartitionOp(src.Number, p.OffsetBytes, cursor, p.SizeBytes));
            cursor += (p.SizeBytes + MiB - 1) / MiB * MiB;
        }
        var prepSim = LayoutSimulator.Simulate(src, prep);
        return new MigrationPlan
        {
            Fit = prepSim.IsValid ? MigrationFit.NeedsPreparation : MigrationFit.Invalid, Source = src, Destination = dst, SystemPartitions = sys,
            LeftBehind = leftBehind, RequiredBytes = required, DataBytes = data, ShrinkCTo = newC,
            Preparation = prepSim.IsValid ? prep : Array.Empty<DiskOperation>(), Issues = baseIssues.Concat(prepSim.AllIssues).ToList(),
        };

        MigrationPlan TooSmall() => new()
        {
            Fit = MigrationFit.TooSmall, Source = src, Destination = dst, SystemPartitions = sys, LeftBehind = leftBehind, RequiredBytes = required, DataBytes = data,
            Issues = baseIssues.Append(new ValidationIssue(IssueLevel.Error, "Mig.Error.TooSmall", data + MinimumFreeAfterShrink, dst.SizeBytes)).ToList(),
        };
    }
}
