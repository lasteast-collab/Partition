using Lasteast.Core.Layout;
using Lasteast.Core.Models;
using Lasteast.Core.PartitionTables;
using Lasteast.Core.RawDisk;

namespace Lasteast.Core.Operations;

/// <summary>Validation and preview of the raw-disk operations (conversion, move, clone).</summary>
public static partial class LayoutSimulator
{
    /// <summary>Sectors a GPT needs at the start (MBR + header + 128×128-byte entries).</summary>
    public static ulong GptHeadBytes(DiskInfo d) => (2UL + (ulong)PartitionTableCodec.EntryArraySectors((int)d.BytesPerLogicalSector)) * d.BytesPerLogicalSector;
    public static ulong GptTailBytes(DiskInfo d) => (1UL + (ulong)PartitionTableCodec.EntryArraySectors((int)d.BytesPerLogicalSector)) * d.BytesPerLogicalSector;

    private static IEnumerable<PartitionInfo> Real(DiskInfo d) => d.Partitions.Where(p => p.Role != PartitionRole.MbrExtended && !p.IsPending);

    private static DiskInfo ApplyConvert(DiskInfo d, ConvertStyleOp c, List<ValidationIssue> issues)
    {
        if (d.Style == c.Target) return Fail(issues, "Op.Error.NoChange", d);
        // The running Windows disk needs firmware changes too (BIOS→UEFI); Microsoft's MBR2GPT handles that case.
        if (d.IsWindowsDisk || d.IsBootDisk) return Fail(issues, "Conv.Error.SystemDisk", d);
        if (d.Partitions.Any(p => p.IsPending)) return Fail(issues, "Op.Error.PendingTarget", d);
        var parts = Real(d).ToList();

        if (c.Target == PartitionStyle.Gpt)
        {
            if (d.Partitions.Any(p => p.Role is PartitionRole.MbrExtended or PartitionRole.MbrLogical)) return Fail(issues, "Conv.Error.Extended", d);
            if (parts.Any(p => p.OffsetBytes < GptHeadBytes(d))) return Fail(issues, "Conv.Error.NoRoomStart", d);
            if (parts.Any(p => p.EndBytes > d.SizeBytes - GptTailBytes(d))) return Fail(issues, "Conv.Error.NoRoomEnd", d);
            var mapped = new List<PartitionInfo>();
            foreach (var p in parts)
            {
                var type = p.MbrType is { } t ? TypeMap.ToGpt(t) : null;
                if (type is null) return Fail(issues, "Conv.Error.Type", d, $"0x{p.MbrType:X2}");
                mapped.Add(p with { Style = PartitionStyle.Gpt, GptType = type, MbrType = null, MbrActive = false, Role = PartitionTypes.Classify(type.Value) });
            }
            if (parts.Any(p => p.MbrActive)) Warn(issues, "Conv.Warn.BootFlag");
            Warn(issues, "Op.Warn.Backup");
            return d with
            {
                Style = PartitionStyle.Gpt, Partitions = mapped,
                UsableStartBytes = GptHeadBytes(d), UsableEndBytes = d.SizeBytes - GptTailBytes(d),
            };
        }

        if (c.Target == PartitionStyle.Mbr)
        {
            var kept = parts.Where(p => p.Role != PartitionRole.MicrosoftReserved).ToList();
            if (kept.Count > 4) return Fail(issues, "Conv.Error.TooMany", d);
            if (kept.Any(p => p.EndBytes > Mbr2TiB)) return Fail(issues, "Conv.Error.Beyond2TB", d);
            var mapped = new List<PartitionInfo>();
            foreach (var p in kept)
            {
                var type = p.GptType is { } g ? TypeMap.ToMbr(g, p.Volume?.FileSystem) : null;
                if (type is null) return Fail(issues, "Conv.Error.Type", d, p.GptType?.ToString() ?? "?");
                mapped.Add(p with { Style = PartitionStyle.Mbr, MbrType = type, GptType = null, MbrRecognized = true, Role = PartitionTypes.Classify(type.Value, false) });
            }
            if (kept.Count != parts.Count) Warn(issues, "Conv.Warn.MsrDropped");
            if (d.SizeBytes > Mbr2TiB) Warn(issues, "Conv.Warn.SpaceBeyond2TB");
            Warn(issues, "Op.Warn.Backup");
            return d with { Style = PartitionStyle.Mbr, Partitions = mapped, UsableStartBytes = 0, UsableEndBytes = Math.Min(d.SizeBytes, Mbr2TiB) };
        }
        return Fail(issues, "Op.Error.DiskStyle", d, d.Number);
    }

    private static DiskInfo ApplyMove(DiskInfo d, PartitionInfo target, MovePartitionOp m, bool isWindows, bool isFirmwareSystem, List<ValidationIssue> issues)
    {
        // Moving the running Windows volume or the active firmware boot partition cannot be done from inside Windows.
        if (isWindows || isFirmwareSystem) return Fail(issues, "Move.Error.InUseSystem", d);
        if (target.Role is PartitionRole.MbrLogical) return Fail(issues, "Move.Error.Logical", d);
        if (target.Role is PartitionRole.MicrosoftReserved or PartitionRole.LdmData or PartitionRole.LdmMetadata or PartitionRole.StorageSpaces)
            return Fail(issues, "Op.Error.Protected", d);
        if (m.NewOffsetBytes == target.OffsetBytes) return Fail(issues, "Op.Error.NoChange", d);
        if (m.NewOffsetBytes % MiB != 0) return Fail(issues, "Op.Error.Granularity", d);
        var fs = target.Volume?.FileSystem;
        bool movableFs = fs is null || fs.Equals("NTFS", StringComparison.OrdinalIgnoreCase) || fs.Equals("FAT32", StringComparison.OrdinalIgnoreCase)
                         || fs.Equals("FAT", StringComparison.OrdinalIgnoreCase) || fs.Equals("RAW", StringComparison.OrdinalIgnoreCase);
        // exFAT and ReFS record their own position in ways we do not patch; refuse instead of guessing.
        if (!movableFs) return Fail(issues, "Move.Error.FileSystem", d, fs ?? "?");

        // The new range must lie in free space plus the partition's own current range.
        ulong newEnd = m.NewOffsetBytes + target.SizeBytes;
        ulong low = Math.Max(d.Style == PartitionStyle.Gpt ? Math.Max(d.UsableStartBytes, GptHeadBytes(d)) : MiB, MiB);
        ulong prevEnd = Real(d).Where(p => !ReferenceEquals(p, target) && p.EndBytes <= target.OffsetBytes).Select(p => p.EndBytes).DefaultIfEmpty(low).Max();
        ulong next = NextBoundary(d, target);
        if (m.NewOffsetBytes < Math.Max(prevEnd, low) || newEnd > next) return Fail(issues, "Move.Error.NoSpace", d);
        if (d.Style == PartitionStyle.Mbr && newEnd > Mbr2TiB) return Fail(issues, "Op.Error.Mbr2TB", d);

        if (target.Role == PartitionRole.Recovery) Warn(issues, "Move.Warn.Recovery");
        Warn(issues, "Move.Warn.Duration");
        Warn(issues, "Op.Warn.Backup");
        var moved = target with { OffsetBytes = m.NewOffsetBytes, Alignment = PartitionTypes.GetAlignment(m.NewOffsetBytes, d.BytesPerPhysicalSector) };
        return d with { Partitions = d.Partitions.Select(p => ReferenceEquals(p, target) ? moved : p).OrderBy(p => p.OffsetBytes).ToList() };
    }

    private static DiskInfo ApplyClone(DiskInfo dest, CloneDiskOp c, Func<int, DiskInfo?>? otherDisk, List<ValidationIssue> issues)
    {
        if (c.SourceDiskNumber == dest.Number) return Fail(issues, "Clone.Error.SameDisk", dest);
        var src = otherDisk?.Invoke(c.SourceDiskNumber);
        if (src is null) return Fail(issues, "Clone.Error.SourceMissing", dest, c.SourceDiskNumber);
        if (dest.IsWindowsDisk || dest.IsBootDisk) return Fail(issues, "Clone.Error.DestSystem", dest);
        if (dest.IsDynamic) return Fail(issues, "Op.Error.Dynamic", dest, dest.Number);
        if (dest.Partitions.Any(p => p.IsPending)) return Fail(issues, "Op.Error.PendingTarget", dest);
        try
        {
            ClonePlanner.Plan(src, (long)dest.SizeBytes, (int)dest.BytesPerLogicalSector, CloneMode.SectorBySector, c.NewIdentifiers, null, c.OnlyPartitions);
        }
        catch (RawOperationException ex)
        {
            return Fail(issues, ex.Key, dest, ex.Message);
        }

        var parts = Real(src).Where(p => c.OnlyPartitions is null || c.OnlyPartitions.Contains(p.OffsetBytes)).ToList();
        if (parts.Count == 0) return Fail(issues, "Clone.Error.Nothing", dest);
        if (dest.Partitions.Count > 0) Warn(issues, "Clone.Warn.DestErased", dest.Number);
        if (Real(src).Any(p => p.Volume?.IsWindowsVolume == true)) Warn(issues, "Clone.Warn.Snapshot");
        if (!c.NewIdentifiers) Warn(issues, "Clone.Warn.SameIds");
        if (c.Mode == CloneMode.UsedSpace) Warn(issues, "Clone.Warn.UsedSpace");
        if (dest.SizeBytes > src.SizeBytes) Warn(issues, "Clone.Warn.ExtraSpace", dest.SizeBytes - src.SizeBytes);

        var cloned = parts.Select(p => p with
        {
            DiskNumber = dest.Number,
            Alignment = PartitionTypes.GetAlignment(p.OffsetBytes, dest.BytesPerPhysicalSector),
            Volume = p.Volume is null ? null : p.Volume with
            {
                VolumeGuidPath = $"pending:{dest.Number}:{p.OffsetBytes}",
                DriveLetter = null,
                MountPoints = Array.Empty<string>(),
                IsWindowsVolume = false,
                IsSystemPartitionVolume = false,
            },
        }).ToList();
        var usableEnd = src.Style == PartitionStyle.Gpt ? dest.SizeBytes - GptTailBytes(dest) : Math.Min(dest.SizeBytes, Mbr2TiB);
        return dest with
        {
            Style = src.Style,
            Partitions = cloned,
            UsableStartBytes = src.Style == PartitionStyle.Gpt ? GptHeadBytes(dest) : 0,
            UsableEndBytes = usableEnd,
        };
    }
}
