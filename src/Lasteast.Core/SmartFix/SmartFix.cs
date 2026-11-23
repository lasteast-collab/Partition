using Lasteast.Core.Layout;
using Lasteast.Core.Models;
using Lasteast.Core.Operations;

namespace Lasteast.Core.SmartFix;

public enum SmartSeverity { Critical, High, Recommended, Optional, Healthy }

public enum FixRisk { None, Low, Medium, High }

public enum FixKind
{
    /// <summary>Nothing to do (healthy / informational).</summary>
    None,
    /// <summary>Adds <see cref="SmartFinding.Operations"/> to the pending queue (preview + confirmation follow).</summary>
    QueueOperations,
    /// <summary>Restart elevated.</summary>
    Elevate,
    /// <summary>Instructions only; Lasteast does not change system settings by itself.</summary>
    Guidance,
    /// <summary>The fix belongs to a module scheduled for <see cref="SmartFinding.PlannedPhase"/>.</summary>
    Planned,
    /// <summary>Opens another page (<see cref="SmartFinding.NavigateTo"/>, a nav key) where the user acts.</summary>
    Navigate,
}

/// <summary>
/// One Smart Fix result. Text fields are localization keys with format arguments.
/// Findings are derived only from data that was actually read — never assumed.
/// </summary>
public sealed record SmartFinding
{
    public required string Id { get; init; }
    public required SmartSeverity Severity { get; init; }
    public required string Category { get; init; }
    public required string TitleKey { get; init; }
    public object[] TitleArgs { get; init; } = Array.Empty<object>();
    public string? ExplanationKey { get; init; }
    public object[] ExplanationArgs { get; init; } = Array.Empty<object>();
    public string? RecommendationKey { get; init; }
    public object[] RecommendationArgs { get; init; } = Array.Empty<object>();
    public string? BenefitKey { get; init; }
    public object[] BenefitArgs { get; init; } = Array.Empty<object>();
    public FixRisk Risk { get; init; }
    public bool BackupRecommended { get; init; }
    public FixKind Fix { get; init; }
    public IReadOnlyList<DiskOperation> Operations { get; init; } = Array.Empty<DiskOperation>();
    public int? PlannedPhase { get; init; }
    public string? NavigateTo { get; init; }
    public int? DiskNumber { get; init; }
}

/// <summary>Facts about the running Windows installation gathered by a platform probe (all optional).</summary>
public sealed record SystemSnapshot
{
    public char? WindowsDrive { get; init; }
    public ulong? UserTempBytes { get; init; }
    public ulong? WindowsTempBytes { get; init; }
    public ulong? RecycleBinBytes { get; init; }
    public ulong? HibernationFileBytes { get; init; }
    public ulong? PageFileBytes { get; init; }
    public bool? RecoveryEnvironmentConfigured { get; init; }
}

public interface ISystemProbe
{
    Task<SystemSnapshot> CaptureAsync(CancellationToken cancellationToken = default);
}

public sealed class NullSystemProbe : ISystemProbe
{
    public Task<SystemSnapshot> CaptureAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SystemSnapshot());
}

public enum CDriveBlocker
{
    None,
    NotFound,
    Unreadable,
    NotResizable,
    /// <summary>The disk has no free space at all.</summary>
    NoFreeSpace,
    /// <summary>Free space exists, but a partition sits between C: and it (typically the recovery partition).</summary>
    PartitionInTheWay,
    DiskNotEditable,
}

/// <summary>Result of the C: expansion analysis (spec module 9).</summary>
public sealed record CDrivePlan
{
    public required CDriveBlocker Blocker { get; init; }
    public DiskInfo? Disk { get; init; }
    public PartitionInfo? Partition { get; init; }
    public ulong AdjacentFreeBytes { get; init; }
    public ulong OtherFreeBytesOnDisk { get; init; }
    public PartitionInfo? BlockingPartition { get; init; }
    public ulong MaxSizeBytes { get; init; }
    public ResizePartitionOp? Operation { get; init; }
    public bool CanExpand => Operation is not null;
}

public static class CDriveExpansion
{
    /// <summary>Below this there is nothing worth offering.</summary>
    public const ulong MinimumUsefulBytes = 64 * LayoutSimulator.MiB;

    public static CDrivePlan Analyze(StorageInventory inv)
    {
        var disk = inv.Disks.FirstOrDefault(d => d.IsWindowsDisk);
        var c = disk?.Partitions.FirstOrDefault(p => p.Volume?.IsWindowsVolume == true);
        if (disk is null || c is null) return new CDrivePlan { Blocker = CDriveBlocker.NotFound };
        if (disk.IsDynamic || disk.Style is not (PartitionStyle.Gpt or PartitionStyle.Mbr))
            return new CDrivePlan { Blocker = CDriveBlocker.DiskNotEditable, Disk = disk, Partition = c };
        if (c.Volume?.AccessError is not null) return new CDrivePlan { Blocker = CDriveBlocker.Unreadable, Disk = disk, Partition = c };
        if (!FileSystems.CanResize(c.Volume?.FileSystem)) return new CDrivePlan { Blocker = CDriveBlocker.NotResizable, Disk = disk, Partition = c };

        var segments = DiskMapLayout.BuildSegments(disk, out _);
        var max = LayoutSimulator.MaxExtendSize(disk, c);
        var adjacent = max > c.SizeBytes ? max - c.SizeBytes : 0;
        // free space elsewhere on the disk (the gap starting right at C:'s end is the adjacent space)
        var otherFree = segments.Where(s => s.Kind == SegmentKind.Unallocated && s.OffsetBytes != c.EndBytes)
                                .Aggregate(0UL, (a, s) => a + s.SizeBytes);
        if (adjacent >= MinimumUsefulBytes)
        {
            var op = new ResizePartitionOp(disk.Number, c.OffsetBytes, max);
            var sim = LayoutSimulator.Simulate(disk, new[] { op });
            return new CDrivePlan
            {
                Blocker = sim.IsValid ? CDriveBlocker.None : CDriveBlocker.DiskNotEditable,
                Disk = disk, Partition = c, AdjacentFreeBytes = adjacent, OtherFreeBytesOnDisk = otherFree,
                MaxSizeBytes = max, Operation = sim.IsValid ? op : null,
            };
        }

        var next = disk.Partitions.Where(p => p.OffsetBytes >= c.EndBytes && p.Role != PartitionRole.MbrExtended)
                                  .OrderBy(p => p.OffsetBytes).FirstOrDefault();
        return new CDrivePlan
        {
            Blocker = otherFree >= MinimumUsefulBytes ? CDriveBlocker.PartitionInTheWay : CDriveBlocker.NoFreeSpace,
            Disk = disk, Partition = c, OtherFreeBytesOnDisk = otherFree, BlockingPartition = next, MaxSizeBytes = c.SizeBytes,
        };
    }
}

/// <summary>
/// The "Scan my computer" analysis. Pure: takes what detection and the system probe actually read,
/// returns findings ordered Critical → Healthy. Fixes that change disks are expressed as operations
/// for the pending queue — Smart Fix itself never executes anything.
/// </summary>
public static class SmartFixAnalyzer
{
    public const ulong GiB = 1024UL * 1024 * 1024;
    public const double LowSpaceFraction = 0.10;
    public const ulong LowSpaceAbsolute = 10 * GiB;
    public const ulong TempWorthCleaning = 1 * GiB;

    public static IReadOnlyList<SmartFinding> Analyze(StorageInventory inv, SystemSnapshot? sys = null)
    {
        sys ??= new SystemSnapshot();
        var list = new List<SmartFinding>();
        var lettersInUse = inv.Disks.SelectMany(d => LayoutSimulator.LettersOn(d))
            .Concat(inv.UnmappedVolumes.Select(v => v.DriveLetter).OfType<char>()).ToHashSet();

        // ---------------- privileges
        if (!inv.IsElevated && !inv.IsSampleData)
            list.Add(new SmartFinding
            {
                Id = "elevation", Severity = SmartSeverity.Optional, Category = "System",
                TitleKey = "SF.NotElevated.Title", ExplanationKey = "SF.NotElevated.Explain", RecommendationKey = "SF.NotElevated.Fix",
                Fix = FixKind.Elevate, Risk = FixRisk.None,
            });

        // ---------------- C: drive
        var cplan = CDriveExpansion.Analyze(inv);
        if (cplan.Partition is { Volume: { } cv } c && cv.CapacityBytes is > 0 && cv.FreeBytes is { } cfree)
        {
            var low = cfree < cv.CapacityBytes.Value * LowSpaceFraction || cfree < LowSpaceAbsolute;
            if (low)
            {
                list.Add(new SmartFinding
                {
                    Id = "c-low", Severity = cfree < 2 * GiB ? SmartSeverity.Critical : SmartSeverity.High, Category = "Space",
                    TitleKey = "SF.CLow.Title", TitleArgs = new object[] { cfree },
                    ExplanationKey = "SF.CLow.Explain",
                    RecommendationKey = cplan.CanExpand ? "SF.CLow.FixExtend" : "SF.CLow.FixOther",
                    RecommendationArgs = new object[] { cplan.AdjacentFreeBytes },
                    BenefitKey = cplan.CanExpand ? "SF.Benefit.Space" : null, BenefitArgs = new object[] { cplan.AdjacentFreeBytes },
                    Fix = cplan.CanExpand ? FixKind.QueueOperations : FixKind.Navigate, NavigateTo = cplan.CanExpand ? null : "Nav.Cleanup",
                    Operations = cplan.Operation is { } op ? new DiskOperation[] { op } : Array.Empty<DiskOperation>(),
                    Risk = FixRisk.Medium, BackupRecommended = true, DiskNumber = cplan.Disk?.Number,
                });
            }
            else
            {
                list.Add(Healthy("c-ok", "Space", "SF.COk.Title", cfree));
            }
        }

        // ---------------- per disk / per partition
        bool anyMisaligned = false, anyLowOther = false;
        foreach (var d in inv.Disks)
        {
            var segs = DiskMapLayout.BuildSegments(d, out var problems);
            if (problems.Count > 0)
            {
                list.Add(new SmartFinding
                {
                    Id = $"layout-{d.Number}", Severity = SmartSeverity.Critical, Category = "Layout",
                    TitleKey = "SF.Layout.Title", TitleArgs = new object[] { d.Number },
                    ExplanationKey = "SF.Layout.Explain", RecommendationKey = "SF.Layout.Fix",
                    Fix = FixKind.Guidance, Risk = FixRisk.High, BackupRecommended = true, DiskNumber = d.Number,
                });
                continue;
            }

            foreach (var p in d.Partitions)
            {
                var v = p.Volume;
                if (v is { IsWindowsVolume: false, DriveLetter: { } l, CapacityBytes: > 0, FreeBytes: { } free } &&
                    free < v.CapacityBytes.Value * LowSpaceFraction && free < LowSpaceAbsolute)
                {
                    anyLowOther = true;
                    list.Add(new SmartFinding
                    {
                        Id = $"low-{d.Number}-{p.OffsetBytes}", Severity = SmartSeverity.Recommended, Category = "Space",
                        TitleKey = "SF.Low.Title", TitleArgs = new object[] { $"{l}:", free },
                        ExplanationKey = "SF.Low.Explain", RecommendationKey = "SF.Low.Fix",
                        Fix = FixKind.Navigate, NavigateTo = "Nav.StorageAnalyzer", Risk = FixRisk.Low, DiskNumber = d.Number,
                    });
                }

                if (p.Alignment == AlignmentStatus.Misaligned && p.Role != PartitionRole.MbrExtended)
                {
                    anyMisaligned = true;
                    list.Add(new SmartFinding
                    {
                        Id = $"align-{d.Number}-{p.OffsetBytes}", Severity = d.Media == MediaKind.Ssd ? SmartSeverity.Recommended : SmartSeverity.Optional,
                        Category = "Layout", TitleKey = "SF.Align.Title", TitleArgs = new object[] { d.Number, p.PartitionNumber },
                        ExplanationKey = "SF.Align.Explain", RecommendationKey = "SF.Align.Fix",
                        Fix = FixKind.Planned, PlannedPhase = 5, Risk = FixRisk.Medium, BackupRecommended = true, DiskNumber = d.Number,
                    });
                }

                if (v?.AccessError is not null && v.DriveLetter is { } ul)
                    list.Add(new SmartFinding
                    {
                        Id = $"unreadable-{d.Number}-{p.OffsetBytes}", Severity = SmartSeverity.High, Category = "FileSystem",
                        TitleKey = "SF.Unreadable.Title", TitleArgs = new object[] { $"{ul}:" },
                        ExplanationKey = "SF.Unreadable.Explain", RecommendationKey = "SF.Unreadable.Fix",
                        Fix = FixKind.Guidance, Risk = FixRisk.Low, DiskNumber = d.Number,
                    });
            }

            // Unallocated space: offer to extend the NTFS/ReFS data partition just before it, else create a partition.
            if (!d.IsDynamic)
            {
                // the gap after C: is already offered by the C: finding / card
                foreach (var gap in segs.Where(s => s.Kind == SegmentKind.Unallocated && s.SizeBytes >= GiB
                                                    && !(cplan.CanExpand && d.Number == cplan.Disk?.Number && s.OffsetBytes == cplan.Partition?.EndBytes)))
                {
                    var before = d.Partitions.Where(p => p.EndBytes <= gap.OffsetBytes && p.Role != PartitionRole.MbrExtended)
                                             .OrderByDescending(p => p.EndBytes).FirstOrDefault();
                    DiskOperation? fix = null;
                    string recKey;
                    if (before is { IsCritical: false, Volume: { AccessError: null } bv } && FileSystems.CanResize(bv.FileSystem)
                        && before.EndBytes == gap.OffsetBytes)
                    {
                        fix = new ResizePartitionOp(d.Number, before.OffsetBytes, LayoutSimulator.MaxExtendSize(d, before));
                        recKey = "SF.Unalloc.FixExtend";
                    }
                    else
                    {
                        var start = (gap.OffsetBytes + LayoutSimulator.MiB - 1) / LayoutSimulator.MiB * LayoutSimulator.MiB;
                        var size = (gap.EndBytes - start) / LayoutSimulator.MiB * LayoutSimulator.MiB;
                        char? letter = null;
                        for (char ch = 'D'; ch <= 'Z'; ch++) if (!lettersInUse.Contains(ch)) { letter = ch; break; }
                        fix = new CreatePartitionOp(d.Number, start, size, "NTFS", null, letter);
                        if (letter is { } used) lettersInUse.Add(used);
                        recKey = "SF.Unalloc.FixCreate";
                    }
                    var valid = LayoutSimulator.Simulate(d, new[] { fix }).IsValid;
                    list.Add(new SmartFinding
                    {
                        Id = $"unalloc-{d.Number}-{gap.OffsetBytes}", Severity = SmartSeverity.Optional, Category = "Space",
                        TitleKey = "SF.Unalloc.Title", TitleArgs = new object[] { gap.SizeBytes, d.Number },
                        ExplanationKey = "SF.Unalloc.Explain",
                        RecommendationKey = valid ? recKey : "SF.Unalloc.FixManual",
                        RecommendationArgs = before?.Volume?.DriveLetter is { } bl ? new object[] { $"{bl}:" } : new object[] { "—" },
                        BenefitKey = "SF.Benefit.Space", BenefitArgs = new object[] { gap.SizeBytes },
                        Fix = valid ? FixKind.QueueOperations : FixKind.None,
                        Operations = valid ? new[] { fix } : Array.Empty<DiskOperation>(),
                        Risk = fix is ResizePartitionOp ? FixRisk.Medium : FixRisk.Low, BackupRecommended = fix is ResizePartitionOp,
                        DiskNumber = d.Number,
                    });
                }
            }

            if (d.Style == PartitionStyle.Mbr && d.SizeBytes > LayoutSimulator.Mbr2TiB)
                list.Add(new SmartFinding
                {
                    Id = $"mbr2tb-{d.Number}", Severity = SmartSeverity.Recommended, Category = "Layout",
                    TitleKey = "SF.Mbr2TB.Title", TitleArgs = new object[] { d.Number, d.SizeBytes - LayoutSimulator.Mbr2TiB },
                    ExplanationKey = "SF.Mbr2TB.Explain", RecommendationKey = "SF.Mbr2TB.Fix",
                    Fix = FixKind.Planned, PlannedPhase = 5, Risk = FixRisk.Medium, BackupRecommended = true, DiskNumber = d.Number,
                });

            if (d.Media == MediaKind.Ssd && d.TrimEnabled == false)
                list.Add(new SmartFinding
                {
                    Id = $"trim-{d.Number}", Severity = SmartSeverity.Recommended, Category = "Performance",
                    TitleKey = "SF.Trim.Title", TitleArgs = new object[] { d.Number },
                    ExplanationKey = "SF.Trim.Explain", RecommendationKey = "SF.Trim.Fix",
                    Fix = FixKind.Guidance, Risk = FixRisk.Low, DiskNumber = d.Number,
                });

            if (d.IsDynamic)
                list.Add(new SmartFinding
                {
                    Id = $"dynamic-{d.Number}", Severity = SmartSeverity.Optional, Category = "Layout",
                    TitleKey = "SF.Dynamic.Title", TitleArgs = new object[] { d.Number }, ExplanationKey = "SF.Dynamic.Explain",
                    Fix = FixKind.None,
                });
        }

        // ---------------- boot configuration (facts only)
        var winDisk = inv.Disks.FirstOrDefault(d => d.IsWindowsDisk);
        if (winDisk is not null)
        {
            bool hasEsp = winDisk.Partitions.Any(p => p.Role == PartitionRole.EfiSystem) || inv.Disks.Any(d => d.Partitions.Any(p => p.Role == PartitionRole.EfiSystem && p.Volume?.IsSystemPartitionVolume == true));
            if (inv.Firmware == FirmwareKind.Uefi && !hasEsp)
                list.Add(new SmartFinding
                {
                    Id = "esp-missing", Severity = SmartSeverity.High, Category = "Boot",
                    TitleKey = "SF.EspMissing.Title", ExplanationKey = "SF.EspMissing.Explain", RecommendationKey = "SF.EspMissing.Fix",
                    Fix = FixKind.Planned, PlannedPhase = 8, Risk = FixRisk.High, BackupRecommended = true,
                });
            else if (inv.Firmware == FirmwareKind.Uefi)
                list.Add(Healthy("esp-ok", "Boot", "SF.EspOk.Title"));

            if (!winDisk.Partitions.Any(p => p.Role == PartitionRole.Recovery) && sys.RecoveryEnvironmentConfigured != true)
                list.Add(new SmartFinding
                {
                    Id = "recovery-missing", Severity = SmartSeverity.Optional, Category = "Boot",
                    TitleKey = "SF.NoRecovery.Title", ExplanationKey = "SF.NoRecovery.Explain", RecommendationKey = "SF.NoRecovery.Fix",
                    Fix = FixKind.Planned, PlannedPhase = 8, Risk = FixRisk.None,
                });
            else
                list.Add(Healthy("recovery-ok", "Boot", "SF.RecoveryOk.Title"));
        }

        // ---------------- reclaimable space (read-only measurements)
        var temp = (sys.UserTempBytes ?? 0) + (sys.WindowsTempBytes ?? 0);
        if (sys.UserTempBytes is not null && temp >= TempWorthCleaning)
            list.Add(new SmartFinding
            {
                Id = "temp", Severity = SmartSeverity.Recommended, Category = "Cleanup",
                TitleKey = "SF.Temp.Title", TitleArgs = new object[] { temp },
                ExplanationKey = "SF.Temp.Explain", RecommendationKey = "SF.Temp.Fix",
                BenefitKey = "SF.Benefit.Space", BenefitArgs = new object[] { temp },
                Fix = FixKind.Navigate, NavigateTo = "Nav.Cleanup", Risk = FixRisk.Low,
            });
        if (sys.RecycleBinBytes is >= TempWorthCleaning)
            list.Add(new SmartFinding
            {
                Id = "recycle", Severity = SmartSeverity.Optional, Category = "Cleanup",
                TitleKey = "SF.Recycle.Title", TitleArgs = new object[] { sys.RecycleBinBytes.Value },
                ExplanationKey = "SF.Recycle.Explain", RecommendationKey = "SF.Recycle.Fix",
                BenefitKey = "SF.Benefit.Space", BenefitArgs = new object[] { sys.RecycleBinBytes.Value },
                Fix = FixKind.Guidance, Risk = FixRisk.Low,
            });
        if (sys.HibernationFileBytes is >= 4 * GiB && list.Any(f => f.Id == "c-low"))
            list.Add(new SmartFinding
            {
                Id = "hiberfil", Severity = SmartSeverity.Optional, Category = "Cleanup",
                TitleKey = "SF.Hiber.Title", TitleArgs = new object[] { sys.HibernationFileBytes.Value },
                ExplanationKey = "SF.Hiber.Explain", RecommendationKey = "SF.Hiber.Fix",
                BenefitKey = "SF.Benefit.Space", BenefitArgs = new object[] { sys.HibernationFileBytes.Value },
                Fix = FixKind.Guidance, Risk = FixRisk.Low,
            });

        // ---------------- not measured yet (stated, not guessed)
        list.Add(new SmartFinding
        {
            Id = "health", Severity = SmartSeverity.Optional, Category = "Health",
            TitleKey = "SF.Health.Title", ExplanationKey = "SF.Health.Explain", Fix = FixKind.Planned, PlannedPhase = 9,
        });

        // ---------------- healthy summary
        if (!anyMisaligned && inv.Disks.Count > 0) list.Add(Healthy("align-ok", "Layout", "SF.AlignOk.Title"));
        if (inv.Disks.All(d => { DiskMapLayout.BuildSegments(d, out var pr); return pr.Count == 0; }) && inv.Disks.Count > 0)
            list.Add(Healthy("layout-ok", "Layout", "SF.LayoutOk.Title", inv.Disks.Count));
        if (!anyLowOther && inv.Disks.Count > 0) list.Add(Healthy("space-ok", "Space", "SF.SpaceOk.Title"));

        return list.OrderBy(f => f.Severity).ThenBy(f => f.Category).ToList();
    }

    private static SmartFinding Healthy(string id, string category, string key, params object[] args) => new()
    {
        Id = id, Severity = SmartSeverity.Healthy, Category = category, TitleKey = key, TitleArgs = args, Fix = FixKind.None,
    };
}
