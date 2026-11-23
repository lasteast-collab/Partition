using Lasteast.Core.Layout;
using Lasteast.Core.Models;

namespace Lasteast.Core.Operations;

public sealed record StepResult(DiskOperation Operation, DiskInfo After, IReadOnlyList<ValidationIssue> Issues)
{
    public bool HasErrors => Issues.Any(i => i.IsError);
}

public sealed record SimulationResult(DiskInfo Before, DiskInfo After, IReadOnlyList<StepResult> Steps)
{
    public bool IsValid => Steps.All(s => !s.HasErrors);
    public IEnumerable<ValidationIssue> AllIssues => Steps.SelectMany(s => s.Issues);
    public bool DestroysData => Steps.Any(s => s.Operation.DestroysData);
    public int EstimatedSeconds => Steps.Sum(s => s.Operation.EstimatedSeconds);
}

/// <summary>
/// Applies planned operations to an in-memory copy of a disk layout, validating each step
/// against the layout produced by the previous steps. Pure and deterministic — this is the
/// before/after preview, and the engine re-runs it against a fresh snapshot right before writing.
///
/// A step with errors leaves the layout unchanged, so later steps are judged against what would
/// really be on disk.
/// </summary>
public static partial class LayoutSimulator
{
    public const ulong MiB = 1024 * 1024;
    public const ulong MinimumPartitionBytes = 16 * MiB;
    public const ulong Mbr2TiB = 2UL * 1024 * 1024 * 1024 * 1024;
    /// <summary>Free space kept above used data when shrinking (Windows needs working room).</summary>
    public const ulong ShrinkHeadroomBytes = 256 * MiB;

    /// <param name="lettersInUse">Drive letters used anywhere on the system, excluding this disk's own volumes.</param>
    /// <param name="otherDisk">Looks up other disks by number (clone sources). Null: clone operations fail validation.</param>
    public static SimulationResult Simulate(DiskInfo disk, IReadOnlyList<DiskOperation> operations, IReadOnlySet<char>? lettersInUse = null,
        Func<int, DiskInfo?>? otherDisk = null)
    {
        lettersInUse ??= new HashSet<char>();
        var current = disk;
        var steps = new List<StepResult>();
        foreach (var op in operations)
        {
            var issues = new List<ValidationIssue>();
            var next = op.DiskNumber != disk.Number
                ? Fail(issues, "Op.Error.WrongDisk", current)
                : Apply(current, op, lettersInUse, issues, otherDisk);
            if (issues.Any(i => i.IsError)) next = current;
            steps.Add(new StepResult(op, next, issues));
            current = next;
        }
        return new SimulationResult(disk, current, steps);
    }

    private static DiskInfo Fail(List<ValidationIssue> issues, string key, DiskInfo d, params object[] args)
    {
        issues.Add(new ValidationIssue(IssueLevel.Error, key, args));
        return d;
    }

    private static void Warn(List<ValidationIssue> issues, string key, params object[] args)
        => issues.Add(new ValidationIssue(IssueLevel.Warning, key, args));

    private static DiskInfo Apply(DiskInfo d, DiskOperation op, IReadOnlySet<char> lettersElsewhere, List<ValidationIssue> issues,
        Func<int, DiskInfo?>? otherDisk)
    {
        // A clone replaces the whole destination, which may be blank (no partition table yet).
        if (op is CloneDiskOp clone) return ApplyClone(d, clone, otherDisk, issues);

        // ---- disk-level gates
        if (d.Style is not (PartitionStyle.Gpt or PartitionStyle.Mbr)) return Fail(issues, "Op.Error.DiskStyle", d, d.Number);
        if (d.IsDynamic) return Fail(issues, "Op.Error.Dynamic", d, d.Number);
        DiskMapLayout.BuildSegments(d, out var problems);
        if (problems.Count > 0) return Fail(issues, "Op.Error.LayoutProblem", d, d.Number);

        if (op is CreatePartitionOp create) return ApplyCreate(d, create, lettersElsewhere, issues);
        if (op is ConvertStyleOp convert) return ApplyConvert(d, convert, issues);
        if (op is PrepareBootOp boot) return ApplyPrepareBoot(d, boot, issues);

        var offset = op switch
        {
            DeletePartitionOp x => x.OffsetBytes,
            FormatPartitionOp x => x.OffsetBytes,
            ResizePartitionOp x => x.OffsetBytes,
            SetLabelOp x => x.OffsetBytes,
            SetDriveLetterOp x => x.OffsetBytes,
            MovePartitionOp x => x.OffsetBytes,
            _ => throw new NotSupportedException(op.GetType().Name),
        };
        var target = d.Partitions.FirstOrDefault(p => p.OffsetBytes == offset && p.Role != PartitionRole.MbrExtended);
        if (target is null) return Fail(issues, "Op.Error.NotFound", d);
        if (target.IsPending) return Fail(issues, "Op.Error.PendingTarget", d);

        var v = target.Volume;
        bool isWindows = v?.IsWindowsVolume == true;
        bool isFirmwareSystem = v?.IsSystemPartitionVolume == true || target.MbrActive && d.IsBootDisk;
        bool isSpecialRole = target.Role is PartitionRole.EfiSystem or PartitionRole.MicrosoftReserved or PartitionRole.Recovery;

        return op switch
        {
            DeletePartitionOp => ApplyDelete(),
            FormatPartitionOp f => ApplyFormat(f),
            ResizePartitionOp r => ApplyResize(r),
            SetLabelOp l => ApplyLabel(l),
            SetDriveLetterOp s => ApplyLetter(s),
            MovePartitionOp m => ApplyMove(d, target, m, isWindows, isFirmwareSystem, issues),
            _ => d,
        };

        DiskInfo Replace(PartitionInfo? replacement) => d with
        {
            Partitions = d.Partitions.Select(p => ReferenceEquals(p, target) ? replacement : p).OfType<PartitionInfo>().ToList(),
        };

        DiskInfo ApplyDelete()
        {
            if (isWindows || isFirmwareSystem || isSpecialRole) return Fail(issues, "Op.Error.Protected", d);
            if (v?.AccessError is not null) Warn(issues, "Op.Warn.Unreadable");
            Warn(issues, "Op.Warn.DataLoss");
            return Replace(null);
        }

        DiskInfo ApplyFormat(FormatPartitionOp f)
        {
            if (isWindows || isFirmwareSystem || isSpecialRole) return Fail(issues, "Op.Error.Protected", d);
            if (v is null) return Fail(issues, "Op.Error.NoVolume", d);
            if (!FileSystems.IsSupported(f.FileSystem)) return Fail(issues, "Op.Error.FileSystem", d, f.FileSystem);
            if (f.FileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase) && target.SizeBytes > FileSystems.Fat32MaxBytes)
                return Fail(issues, "Op.Error.Fat32Limit", d);
            if (f.Label is { } lbl && lbl.Length > FileSystems.MaxLabelLength(f.FileSystem))
                return Fail(issues, "Op.Error.LabelTooLong", d, FileSystems.MaxLabelLength(f.FileSystem));
            if (f.FileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase)) Warn(issues, "Op.Warn.ReFS");
            if (v.AccessError is not null) Warn(issues, "Op.Warn.Unreadable");
            Warn(issues, "Op.Warn.DataLoss");
            return Replace(target with
            {
                Volume = v with { FileSystem = f.FileSystem, Label = f.Label, FreeBytes = v.CapacityBytes ?? target.SizeBytes, AccessError = null, ClusterSizeBytes = f.ClusterSizeBytes },
                MbrType = target.Style == PartitionStyle.Mbr ? FileSystems.MbrTypeFor(f.FileSystem) : target.MbrType,
            });
        }

        DiskInfo ApplyResize(ResizePartitionOp r)
        {
            if (isFirmwareSystem || isSpecialRole) return Fail(issues, "Op.Error.Protected", d);
            if (v is null || v.AccessError is not null) return Fail(issues, "Op.Error.Unreadable", d);
            if (!FileSystems.CanResize(v.FileSystem)) return Fail(issues, "Op.Error.ResizeFileSystem", d, v.FileSystem ?? "?");
            if (r.NewSizeBytes == target.SizeBytes) return Fail(issues, "Op.Error.NoChange", d);
            if (r.NewSizeBytes % MiB != 0) return Fail(issues, "Op.Error.Granularity", d);
            if (r.NewSizeBytes < MinimumPartitionBytes) return Fail(issues, "Op.Error.TooSmall", d);

            if (r.NewSizeBytes < target.SizeBytes)
            {
                var used = v.UsedBytes ?? 0;
                if (r.NewSizeBytes < used + ShrinkHeadroomBytes) return Fail(issues, "Op.Error.ShrinkBelowUsed", d, used + ShrinkHeadroomBytes);
                Warn(issues, "Op.Warn.ShrinkLimit");
            }
            else
            {
                var newEnd = target.OffsetBytes + r.NewSizeBytes;
                var limit = NextBoundary(d, target);
                if (newEnd > limit) return Fail(issues, "Op.Error.NoAdjacentSpace", d, limit > target.EndBytes ? limit - target.EndBytes : 0UL);
                if (d.Style == PartitionStyle.Mbr && newEnd > Mbr2TiB) return Fail(issues, "Op.Error.Mbr2TB", d);
            }
            if (isWindows) Warn(issues, "Op.Warn.WindowsVolume");
            Warn(issues, "Op.Warn.Backup");

            long delta = (long)r.NewSizeBytes - (long)target.SizeBytes;
            return Replace(target with
            {
                SizeBytes = r.NewSizeBytes,
                Volume = v with
                {
                    CapacityBytes = v.CapacityBytes is { } c ? (ulong)Math.Max(0, (long)c + delta) : null,
                    FreeBytes = v.FreeBytes is { } f ? (ulong)Math.Max(0, (long)f + delta) : null,
                },
            });
        }

        DiskInfo ApplyLabel(SetLabelOp l)
        {
            if (v is null || v.AccessError is not null) return Fail(issues, "Op.Error.NoVolume", d);
            if (l.Label.Length > FileSystems.MaxLabelLength(v.FileSystem)) return Fail(issues, "Op.Error.LabelTooLong", d, FileSystems.MaxLabelLength(v.FileSystem));
            if (l.Label.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0) return Fail(issues, "Op.Error.LabelChars", d);
            return Replace(target with { Volume = v with { Label = l.Label } });
        }

        DiskInfo ApplyLetter(SetDriveLetterOp s)
        {
            if (v is null) return Fail(issues, "Op.Error.NoVolume", d);
            if (isWindows) return Fail(issues, "Op.Error.Protected", d);
            if (s.Letter is { } letter)
            {
                var upper = char.ToUpperInvariant(letter);
                if (upper is < 'C' or > 'Z') return Fail(issues, "Op.Error.LetterInvalid", d);
                if (v.DriveLetter == upper) return Fail(issues, "Op.Error.NoChange", d);
                if (LettersOn(d).Contains(upper) || lettersElsewhere.Contains(upper)) return Fail(issues, "Op.Error.LetterInUse", d, upper);
                if (v.DriveLetter is not null) Warn(issues, "Op.Warn.LetterApps");
                return Replace(target with { Volume = v with { DriveLetter = upper, MountPoints = new[] { $"{upper}:\\" } } });
            }
            if (v.DriveLetter is null) return Fail(issues, "Op.Error.NoChange", d);
            Warn(issues, "Op.Warn.LetterApps");
            return Replace(target with { Volume = v with { DriveLetter = null, MountPoints = Array.Empty<string>() } });
        }
    }

    private static DiskInfo ApplyCreate(DiskInfo d, CreatePartitionOp c, IReadOnlySet<char> lettersElsewhere, List<ValidationIssue> issues)
    {
        if (c.OffsetBytes % MiB != 0 || c.SizeBytes % MiB != 0) return Fail(issues, "Op.Error.Granularity", d);
        if (c.SizeBytes < MinimumPartitionBytes) return Fail(issues, "Op.Error.TooSmall", d);
        if (!FileSystems.IsSupported(c.FileSystem)) return Fail(issues, "Op.Error.FileSystem", d, c.FileSystem);
        if (c.FileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase) && c.SizeBytes > FileSystems.Fat32MaxBytes)
            return Fail(issues, "Op.Error.Fat32Limit", d);
        if (c.Label is { } lbl && lbl.Length > FileSystems.MaxLabelLength(c.FileSystem))
            return Fail(issues, "Op.Error.LabelTooLong", d, FileSystems.MaxLabelLength(c.FileSystem));

        var free = DiskMapLayout.BuildSegments(d, out _)
            .FirstOrDefault(s => s.Kind == SegmentKind.Unallocated && c.OffsetBytes >= s.OffsetBytes && c.OffsetBytes + c.SizeBytes <= s.EndBytes);
        if (free is null) return Fail(issues, "Op.Error.NoSpace", d);

        byte? mbrType = null;
        bool logical = false;
        if (d.Style == PartitionStyle.Mbr)
        {
            if (c.OffsetBytes + c.SizeBytes > Mbr2TiB) return Fail(issues, "Op.Error.Mbr2TB", d);
            var ext = d.Partitions.FirstOrDefault(p => p.Role == PartitionRole.MbrExtended);
            logical = ext is not null && c.OffsetBytes >= ext.OffsetBytes && c.OffsetBytes + c.SizeBytes <= ext.EndBytes;
            var primaries = d.Partitions.Count(p => p.Role != PartitionRole.MbrLogical);
            if (!logical && primaries >= 4) return Fail(issues, "Op.Error.MbrPrimaryLimit", d);
            mbrType = FileSystems.MbrTypeFor(c.FileSystem);
        }
        else if (d.Partitions.Count >= 128)
        {
            return Fail(issues, "Op.Error.GptLimit", d);
        }

        char? letter = c.DriveLetter is { } l ? char.ToUpperInvariant(l) : null;
        if (letter is { } up)
        {
            if (up is < 'C' or > 'Z') return Fail(issues, "Op.Error.LetterInvalid", d);
            if (LettersOn(d).Contains(up) || lettersElsewhere.Contains(up)) return Fail(issues, "Op.Error.LetterInUse", d, up);
        }
        if (c.FileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase)) Warn(issues, "Op.Warn.ReFS");

        var created = new PartitionInfo
        {
            DiskNumber = d.Number,
            PartitionNumber = 0,
            OffsetBytes = c.OffsetBytes,
            SizeBytes = c.SizeBytes,
            Style = d.Style,
            Role = logical ? PartitionRole.MbrLogical : PartitionRole.BasicData,
            GptType = d.Style == PartitionStyle.Gpt ? PartitionTypes.GptBasicData : null,
            MbrType = mbrType,
            MbrRecognized = d.Style == PartitionStyle.Mbr,
            IsPending = true,
            Alignment = PartitionTypes.GetAlignment(c.OffsetBytes, d.BytesPerPhysicalSector),
            Volume = new VolumeInfo
            {
                VolumeGuidPath = $"pending:{d.Number}:{c.OffsetBytes}",
                DriveLetter = letter,
                MountPoints = letter is { } m ? new[] { $"{m}:\\" } : Array.Empty<string>(),
                Label = c.Label,
                FileSystem = c.FileSystem,
                CapacityBytes = c.SizeBytes,
                FreeBytes = c.SizeBytes,
            },
        };
        return d with { Partitions = d.Partitions.Append(created).OrderBy(p => p.OffsetBytes).ToList() };
    }

    /// <summary>First byte after <paramref name="p"/> that is not free: the next partition's start or the usable end.</summary>
    public static ulong NextBoundary(DiskInfo d, PartitionInfo p)
    {
        var usableEnd = d.Style == PartitionStyle.Gpt && d.UsableEndBytes > 0 ? d.UsableEndBytes : d.SizeBytes;
        var next = d.Partitions
            .Where(x => x.OffsetBytes >= p.EndBytes && !ReferenceEquals(x, p) && x.Role != PartitionRole.MbrExtended)
            .Select(x => x.OffsetBytes).DefaultIfEmpty(usableEnd).Min();
        // Logical partitions cannot grow past their extended container.
        var ext = d.Partitions.FirstOrDefault(x => x.Role == PartitionRole.MbrExtended && p.OffsetBytes >= x.OffsetBytes && p.OffsetBytes < x.EndBytes);
        if (ext is not null) next = Math.Min(next, ext.EndBytes);
        return Math.Min(next, usableEnd);
    }

    /// <summary>Largest size the partition could grow to into adjacent free space (1 MiB granularity).</summary>
    public static ulong MaxExtendSize(DiskInfo d, PartitionInfo p) => (NextBoundary(d, p) - p.OffsetBytes) / MiB * MiB;

    /// <summary>Smallest safe shrink size based on known used space (the engine re-checks with Windows' own limit).</summary>
    public static ulong MinShrinkSize(PartitionInfo p)
    {
        var used = p.Volume?.UsedBytes ?? p.SizeBytes;
        var min = Math.Max(MinimumPartitionBytes, used + ShrinkHeadroomBytes);
        return Math.Min(p.SizeBytes, (min + MiB - 1) / MiB * MiB);
    }

    public static IReadOnlySet<char> LettersOn(DiskInfo d) =>
        d.Partitions.Select(p => p.Volume?.DriveLetter).OfType<char>().Select(char.ToUpperInvariant).ToHashSet();
}
