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

    private static DiskInfo ApplyPrepareBoot(DiskInfo d, PrepareBootOp b, List<ValidationIssue> issues)
    {
        // The running Windows' boot configuration is repaired with the rescue tools (Phase 8), not from here.
        if (d.IsWindowsDisk || d.IsBootDisk) return Fail(issues, "Boot.Error.RunningSystem", d);
        var parts = Real(d).ToList();
        if (d.Style == PartitionStyle.Gpt)
        {
            var esp = parts.FirstOrDefault(p => p.Role == PartitionRole.EfiSystem);
            if (esp is null) return Fail(issues, "Boot.Error.NoEsp", d);
            if (esp.Volume?.FileSystem is { } fs && !fs.StartsWith("FAT", StringComparison.OrdinalIgnoreCase)) return Fail(issues, "Boot.Error.EspFileSystem", d, fs);
        }
        else if (d.Style == PartitionStyle.Mbr)
        {
            if (!parts.Any(p => p.MbrActive)) Warn(issues, "Boot.Warn.SetActive");
        }
        else return Fail(issues, "Op.Error.DiskStyle", d, d.Number);

        if (b.WindowsDirectoryOverride is null && !parts.Any(p => string.Equals(p.Volume?.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase)))
            return Fail(issues, "Boot.Error.NoWindows", d);
        Warn(issues, "Boot.Warn.Bcd");
        if (b.FixDriveLetter && b.WindowsDirectoryOverride is null) Warn(issues, "Boot.Warn.Registry");
        return d;
    }

    private static DiskInfo ApplyRepairBootSector(DiskInfo d, PartitionInfo target, RepairBootSectorOp b, bool protectedPartition, List<ValidationIssue> issues)
    {
        if (protectedPartition) return Fail(issues, "Op.Error.Protected", d);
        if (d.IsDynamic) return Fail(issues, "Op.Error.Dynamic", d, d.Number);
        if (b.FileSystem is not ("NTFS" or "FAT32" or "exFAT")) return Fail(issues, "BootFix.Error.FileSystem", d, b.FileSystem);
        // a volume Windows reads fine has a working boot sector: never overwrite it
        if (target.Volume is { AccessError: null, FileSystem: not null }) return Fail(issues, "BootFix.Error.Readable", d);
        Warn(issues, "BootFix.Warn.Sectors", b.FileSystem);
        var repaired = target with
        {
            Volume = (target.Volume ?? new VolumeInfo { VolumeGuidPath = $"pending:{d.Number}:{target.OffsetBytes}" })
                with { FileSystem = b.FileSystem, AccessError = null, AccessErrorCode = null },
        };
        return d with { Partitions = d.Partitions.Select(p => ReferenceEquals(p, target) ? repaired : p).ToList() };
    }

    private static DiskInfo ApplyRecover(DiskInfo d, RecoverPartitionOp r, List<ValidationIssue> issues)
    {
        if (d.IsDynamic) return Fail(issues, "Op.Error.Dynamic", d, d.Number);
        ulong ss = d.BytesPerLogicalSector == 0 ? 512 : d.BytesPerLogicalSector;
        if (r.OffsetBytes % ss != 0 || r.SizeBytes % ss != 0 || r.SizeBytes == 0 || r.OffsetBytes < ss) return Fail(issues, "Op.Error.Granularity", d);
        ulong end = r.OffsetBytes + r.SizeBytes;
        if (end > d.SizeBytes) return Fail(issues, "Op.Error.NoSpace", d);
        bool raw = d.Style is not (PartitionStyle.Gpt or PartitionStyle.Mbr);
        if (d.Style == PartitionStyle.Gpt && (r.OffsetBytes < Math.Max(d.UsableStartBytes, GptHeadBytes(d)) || end > (d.UsableEndBytes > 0 ? d.UsableEndBytes : d.SizeBytes - GptTailBytes(d))))
            return Fail(issues, "Rec.Error.GptArea", d);
        if (Real(d).Any(p => r.OffsetBytes < p.EndBytes && p.OffsetBytes < end)) return Fail(issues, "Rec.Error.Overlap", d);
        if (!raw && d.Style == PartitionStyle.Mbr)
        {
            if (d.Partitions.Any(p => p.Role is PartitionRole.MbrExtended or PartitionRole.MbrLogical)) return Fail(issues, "Rec.Error.Extended", d);
            if (Real(d).Count() >= 4) return Fail(issues, "Op.Error.MbrPrimaryLimit", d);
        }
        if ((raw || d.Style == PartitionStyle.Mbr) && end > Mbr2TiB) return Fail(issues, "Op.Error.Mbr2TB", d);
        if (d.Style == PartitionStyle.Gpt && d.Partitions.Count >= 128) return Fail(issues, "Op.Error.GptLimit", d);
        if (raw) Warn(issues, "Rec.Warn.NewMbr");
        Warn(issues, "Rec.Warn.TableOnly");
        if (r.RestoreBootSector) Warn(issues, "Rec.Warn.BootSector");

        var style = raw ? PartitionStyle.Mbr : d.Style;
        var created = new PartitionInfo
        {
            DiskNumber = d.Number, PartitionNumber = 0, OffsetBytes = r.OffsetBytes, SizeBytes = r.SizeBytes, Style = style,
            Role = PartitionRole.BasicData, GptType = style == PartitionStyle.Gpt ? PartitionTypes.GptBasicData : null,
            MbrType = style == PartitionStyle.Mbr ? (r.FileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase) ? (byte)0x0C : (byte)0x07) : null,
            MbrRecognized = style == PartitionStyle.Mbr, IsPending = true,
            Alignment = PartitionTypes.GetAlignment(r.OffsetBytes, d.BytesPerPhysicalSector),
            Volume = new VolumeInfo { VolumeGuidPath = $"pending:{d.Number}:{r.OffsetBytes}", FileSystem = r.FileSystem, CapacityBytes = r.SizeBytes },
        };
        return d with { Style = style, Partitions = d.Partitions.Append(created).OrderBy(p => p.OffsetBytes).ToList() };
    }

    private static DiskInfo ApplyRestore(DiskInfo d, RestoreTableOp r, List<ValidationIssue> issues)
    {
        if (d.SizeBytes != r.BackupDiskSize) return Fail(issues, "Restore.Error.Size", d);
        if (!string.IsNullOrEmpty(r.Serial) && !string.IsNullOrEmpty(d.SerialNumber) && r.Serial != d.SerialNumber) return Fail(issues, "Restore.Error.OtherDisk", d);
        if (!string.IsNullOrEmpty(r.Model) && !string.IsNullOrEmpty(d.Model) && r.Model != d.Model) return Fail(issues, "Restore.Error.OtherDisk", d);
        if (d.IsWindowsDisk && Real(d).Any(p => p.Volume?.IsWindowsVolume == true && !r.Partitions.Any(x => x.Offset == p.OffsetBytes && x.Size == p.SizeBytes)))
            return Fail(issues, "Restore.Error.WindowsChanged", d);
        var current = Real(d).ToList();
        if (current.Count == r.Partitions.Count && current.All(p => r.Partitions.Contains((p.OffsetBytes, p.SizeBytes))) && d.Style == r.Style)
            return Fail(issues, "Op.Error.NoChange", d);
        foreach (var p in current.Where(p => !r.Partitions.Contains((p.OffsetBytes, p.SizeBytes))))
            Warn(issues, "Restore.Warn.Removed", p.OffsetBytes, p.SizeBytes);
        Warn(issues, "Restore.Warn.TableOnly");
        var parts = r.Partitions.Select((x, i) =>
            current.FirstOrDefault(p => p.OffsetBytes == x.Offset && p.SizeBytes == x.Size) ?? new PartitionInfo
            {
                DiskNumber = d.Number, PartitionNumber = i + 1, OffsetBytes = x.Offset, SizeBytes = x.Size, Style = r.Style,
                Role = PartitionRole.BasicData, IsPending = true, Alignment = PartitionTypes.GetAlignment(x.Offset, d.BytesPerPhysicalSector),
            }).ToList();
        return d with { Style = r.Style, Partitions = parts };
    }

    private static DiskInfo ApplyRepairBoot(DiskInfo d, RepairBootOp r, List<ValidationIssue> issues)
    {
        if (!d.IsWindowsDisk) return Fail(issues, "Boot.Error.NotWindowsDisk", d);
        if (d.Style == PartitionStyle.Gpt && !Real(d).Any(p => p.Role == PartitionRole.EfiSystem)) return Fail(issues, "Boot.Error.NoEsp", d);
        Warn(issues, "Boot.Warn.RepairRunning");
        return d;
    }

    /// <summary>
    /// No partition table: RAW, or what Windows reports for a blank disk (MBR style, signature 0, no partitions).
    /// </summary>
    public static bool IsUninitialized(DiskInfo d) =>
        d.Partitions.Count == 0 && (d.Style is not (PartitionStyle.Gpt or PartitionStyle.Mbr) || (d.Style == PartitionStyle.Mbr && (d.MbrSignature ?? 0) == 0));

    private static DiskInfo ApplyInitialize(DiskInfo d, InitializeDiskOp op, List<ValidationIssue> issues)
    {
        if (!IsUninitialized(d)) return Fail(issues, "Init.Error.NotRaw", d);
        if (op.Style is not (PartitionStyle.Gpt or PartitionStyle.Mbr)) return Fail(issues, "Op.Error.DiskStyle", d, d.Number);
        if (op.Style == PartitionStyle.Mbr && d.SizeBytes > Mbr2TiB) Warn(issues, "Conv.Warn.SpaceBeyond2TB");
        Warn(issues, "Init.Warn.Raw");
        return op.Style == PartitionStyle.Gpt
            ? d with { Style = PartitionStyle.Gpt, UsableStartBytes = GptHeadBytes(d), UsableEndBytes = d.SizeBytes - GptTailBytes(d) }
            : d with { Style = PartitionStyle.Mbr, UsableStartBytes = 0, UsableEndBytes = Math.Min(d.SizeBytes, Mbr2TiB) };
    }

    private static DiskInfo ApplySetActive(DiskInfo d, PartitionInfo target, List<ValidationIssue> issues)
    {
        if (d.Style != PartitionStyle.Mbr) return Fail(issues, "Attr.Error.MbrOnly", d);
        if (target.Role == PartitionRole.MbrLogical) return Fail(issues, "Attr.Error.Logical", d);
        if (target.MbrActive) return Fail(issues, "Op.Error.NoChange", d);
        // Changing which partition starts the running computer could leave it unable to boot.
        if (d.IsBootDisk || d.IsWindowsDisk) return Fail(issues, "Attr.Error.BootDisk", d);
        Warn(issues, "Attr.Warn.Active");
        return d with { Partitions = d.Partitions.Select(p => ReferenceEquals(p, target) ? p with { MbrActive = true } : p.MbrActive ? p with { MbrActive = false } : p).ToList() };
    }

    private static readonly (byte Visible, byte Hidden)[] MbrHidePairs = { (0x07, 0x17), (0x0C, 0x1C), (0x0B, 0x1B), (0x0E, 0x1E), (0x06, 0x16), (0x04, 0x14), (0x01, 0x11) };

    public static byte? MbrHiddenCounterpart(byte type, bool hide) =>
        hide ? MbrHidePairs.FirstOrDefault(x => x.Visible == type).Hidden is var h and > 0 ? h : null
             : MbrHidePairs.FirstOrDefault(x => x.Hidden == type).Visible is var v and > 0 ? v : null;

    private static DiskInfo ApplySetHidden(DiskInfo d, PartitionInfo target, SetHiddenOp h, bool protectedPartition, List<ValidationIssue> issues)
    {
        if (protectedPartition) return Fail(issues, "Op.Error.Protected", d);
        if (target.IsHidden == h.Hidden) return Fail(issues, "Op.Error.NoChange", d);
        PartitionInfo changed;
        if (d.Style == PartitionStyle.Gpt)
        {
            if (target.Role != PartitionRole.BasicData) return Fail(issues, "Attr.Error.HideType", d);
            var attrs = h.Hidden ? target.GptAttributes | PartitionTypes.GptAttributeHidden | PartitionTypes.GptAttributeNoDriveLetter
                                 : target.GptAttributes & ~(PartitionTypes.GptAttributeHidden | PartitionTypes.GptAttributeNoDriveLetter);
            changed = target with { GptAttributes = attrs };
        }
        else
        {
            if (target.MbrType is not { } t || MbrHiddenCounterpart(t, h.Hidden) is not { } nt) return Fail(issues, "Attr.Error.HideType", d);
            changed = target with { MbrType = nt };
        }
        if (h.Hidden && target.Volume?.DriveLetter is not null) Warn(issues, "Attr.Warn.HideLetter");
        Warn(issues, h.Hidden ? "Attr.Warn.Hidden" : "Attr.Warn.Shown");
        var v = changed.Volume;
        if (h.Hidden && v is not null) changed = changed with { Volume = v with { DriveLetter = null, MountPoints = Array.Empty<string>() } };
        return d with { Partitions = d.Partitions.Select(p => ReferenceEquals(p, target) ? changed : p).ToList() };
    }

    private static DiskInfo ApplySetType(DiskInfo d, PartitionInfo target, SetTypeOp t, bool protectedPartition, List<ValidationIssue> issues)
    {
        if (protectedPartition) return Fail(issues, "Op.Error.Protected", d);
        if (target.Role is PartitionRole.EfiSystem or PartitionRole.MicrosoftReserved or PartitionRole.LdmData or PartitionRole.LdmMetadata or PartitionRole.StorageSpaces)
            return Fail(issues, "Op.Error.Protected", d);
        // Windows Recovery on the running Windows' disk: changing its type disables the recovery environment.
        if (target.Role == PartitionRole.Recovery && d.IsWindowsDisk) return Fail(issues, "Op.Error.Protected", d);
        PartitionInfo changed;
        if (d.Style == PartitionStyle.Gpt)
        {
            if (t.GptType is not { } g || g == Guid.Empty) return Fail(issues, "Attr.Error.Type", d);
            if (g == target.GptType) return Fail(issues, "Op.Error.NoChange", d);
            if (g == PartitionTypes.GptEfiSystem || g == PartitionTypes.GptMicrosoftReserved || g == PartitionTypes.GptLdmData || g == PartitionTypes.GptLdmMetadata)
                return Fail(issues, "Attr.Error.TypeNotAllowed", d);
            changed = target with { GptType = g, Role = PartitionTypes.Classify(g) };
        }
        else
        {
            if (t.MbrType is not { } b || b == 0) return Fail(issues, "Attr.Error.Type", d);
            if (b == target.MbrType) return Fail(issues, "Op.Error.NoChange", d);
            if (PartitionTypes.IsMbrExtended(b) || target.Role == PartitionRole.MbrExtended || b == 0xEE || b == 0x42) return Fail(issues, "Attr.Error.TypeNotAllowed", d);
            changed = target with { MbrType = b, Role = target.Role == PartitionRole.MbrLogical ? PartitionRole.MbrLogical : PartitionTypes.Classify(b, false) };
        }
        Warn(issues, "Attr.Warn.Type");
        return d with { Partitions = d.Partitions.Select(p => ReferenceEquals(p, target) ? changed : p).ToList() };
    }
}
